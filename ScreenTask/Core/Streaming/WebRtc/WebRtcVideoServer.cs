using System.Collections.Concurrent;
using System.Drawing;
using System.Diagnostics;

namespace ScreenTask.Core.Streaming.WebRtc;

/// <summary>One shared encoder, independent encrypted media connections for each viewer.</summary>
internal sealed class WebRtcVideoServer : IDisposable
{
    private readonly ConcurrentDictionary<string, NativeRtcPeer> _peers = new();
    private readonly object _encoderLock = new();
    private H264Encoder? _encoder;
    private int _keyFrameRequested;
    private long _lastOutput;
    private volatile bool _enabled;
    private string _address = "127.0.0.1";
    private int _port = 7070;
    internal bool Enabled => _enabled;
    internal string EncoderBackend { get; private set; } = "Not started";
    internal string? UnavailableReason { get; private set; }
    internal event Action<string, string>? Fallback;
    internal event Action<int>? FrameSent;
    internal event Action? FrameDropped;

    internal bool Configure(bool enabled, string address, int port)
    {
        Reset();
        _address = address; _port = port;
        UnavailableReason = null;
        if (!enabled) return false;
        try
        {
            NativeRtcPeer.EnsureAvailable();
            using var probe = new H264Encoder(320, 240, 30);
            EncoderBackend = probe.Backend;
            return _enabled = true;
        }
        catch (Exception ex)
        {
            UnavailableReason = $"WebRTC unavailable ({ex.GetType().Name}); JPEG remains active.";
            return false;
        }
    }

    internal async Task<string> CreateOfferAsync(string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_enabled || _peers.Count >= 16) throw new InvalidOperationException("WebRTC is unavailable or full.");
        var peer = new NativeRtcPeer(_address, _port);
        peer.Ready += () => Interlocked.Exchange(ref _keyFrameRequested, 1);
        peer.KeyFrameRequested += () => Interlocked.Exchange(ref _keyFrameRequested, 1);
        peer.Failed += () => _ = Task.Run(() => UseJpeg(id, "WebRTC connection lost; using JPEG."));
        if (!_peers.TryAdd(id, peer)) { peer.Dispose(); throw new InvalidOperationException("Peer already exists."); }
        try { return await peer.CreateOfferAsync(token); }
        catch { Remove(id); throw; }
    }

    internal void SetAnswer(string id, string answer)
    {
        if (_peers.TryGetValue(id, out var peer)) peer.SetAnswer(answer);
    }

    internal int Broadcast(Bitmap bitmap, int fps)
    {
        if (!_enabled || !_peers.Values.Any(p => p.IsReady)) return 0;
        try
        {
            lock (_encoderLock)
            {
                if (!_enabled) return 0;
                if (_encoder == null || _encoder.Width != ((bitmap.Width + 1) & ~1) ||
                    _encoder.Height != ((bitmap.Height + 1) & ~1) || _encoder.Fps != Math.Clamp(fps, 1, 60))
                {
                    _encoder?.Dispose();
                    _encoder = new H264Encoder(bitmap.Width, bitmap.Height, fps);
                    _lastOutput = Stopwatch.GetTimestamp();
                    EncoderBackend = _encoder.Backend;
                    Interlocked.Exchange(ref _keyFrameRequested, 1);
                }
                if (Interlocked.Exchange(ref _keyFrameRequested, 0) != 0) _encoder.RequestKeyFrame();
                var frames = _encoder.Encode(bitmap);
                if (frames.Count > 0) _lastOutput = Stopwatch.GetTimestamp();
                else if (Stopwatch.GetElapsedTime(_lastOutput).TotalSeconds > 5)
                    throw new TimeoutException("H.264 encoder stopped producing frames.");
                foreach (var frame in frames)
                {
                    foreach (var entry in _peers)
                    {
                        try
                        {
                            if (!entry.Value.IsReady) continue;
                            if (entry.Value.Send(frame.Data)) FrameSent?.Invoke(frame.Data.Length);
                            else FrameDropped?.Invoke();
                        }
                        catch { _ = Task.Run(() => UseJpeg(entry.Key, "WebRTC send failed; using JPEG.")); }
                    }
                }
                return frames.Count;
            }
        }
        catch (Exception ex)
        {
            _enabled = false;
            UnavailableReason = $"H.264 encoding failed ({ex.GetType().Name}); using JPEG.";
            foreach (var id in _peers.Keys) _ = Task.Run(() => UseJpeg(id, "H.264 encoding unavailable; using JPEG."));
            return 0;
        }
    }

    internal void UseJpeg(string id, string reason)
    {
        Remove(id);
        Fallback?.Invoke(id, reason);
    }

    internal void Remove(string id)
    {
        if (_peers.TryRemove(id, out var peer)) peer.Dispose();
    }

    internal void StopSending(string id)
    {
        if (_peers.TryGetValue(id, out var peer)) peer.StopSending();
    }

    internal void Reset()
    {
        _enabled = false;
        foreach (var id in _peers.Keys) Remove(id);
        lock (_encoderLock) { _encoder?.Dispose(); _encoder = null; }
    }

    public void Dispose() => Reset();
}
