using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using ScreenTask.Core.Audio;
using ScreenTask.Core.Streaming;

namespace ScreenTask.Core.Capture
{
    public class ScreenCaptureEngine : IDisposable
    {
        private readonly StreamBroadcaster _broadcaster;
        private readonly WasapiAudioCaptureService _audioService;
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private Task? _encodeTask;
        private LatestFrameQueue<CapturedFrame>? _frames;
        private readonly object _sourceLock = new();
        private readonly object _stateLock = new();
        private int _captureEpoch;
        private int _sourceVersion;
        private long _discardedFrames;
        private double _lastFrameAgeMilliseconds;
        private double _lastQueueWaitMilliseconds;
        public double LastFrameAgeMilliseconds => Volatile.Read(ref _lastFrameAgeMilliseconds);
        public double LastQueueWaitMilliseconds => Volatile.Read(ref _lastQueueWaitMilliseconds);
        public long DroppedCapturedFrames => Interlocked.Read(ref _discardedFrames) + (_frames?.DroppedCount ?? 0);
        public int PendingFrameCount => _frames?.PendingCount ?? 0;
        private Task? _stopTask;
        private readonly object _lifecycleLock = new();
        private double _lastCaptureMilliseconds;
        public double LastCaptureMilliseconds => Volatile.Read(ref _lastCaptureMilliseconds);
        public string CaptureBackend => CurrentSource is ScreenCaptureSource screen ? screen.CaptureBackend : "PrintWindow";

        public ICaptureSource CurrentSource { get; private set; }
        public bool CaptureMouse { get; set; } = true;
        public int TargetFps { get; set; } = 30;
        public bool AudioEnabled { get; set; } = false;
        public AudioDeviceDescriptor? SelectedAudioDevice { get; set; }
        public bool IsRunning { get; private set; }

        public StreamBroadcaster Broadcaster => _broadcaster;
        public WasapiAudioCaptureService AudioService => _audioService;
        public event Action<Bitmap>? PreviewFrameAvailable;
        /// <summary>Capture-to-encode completion time in milliseconds; excludes network and display latency.</summary>
        public event Action<double>? FrameProcessed;
        private long _lastPreviewTicks;
        private volatile bool _isFrozen;

        public bool IsFrozen
        {
            get => _isFrozen;
            set
            {
                lock (_stateLock)
                {
                    if (_isFrozen == value) return;
                    _isFrozen = value;
                    _captureEpoch++; // Pending/in-flight captures from the old state must not be replayed.
                }
                _frames?.Wake();
            }
        }

        public ScreenCaptureEngine(StreamBroadcaster broadcaster)
        {
            _broadcaster = broadcaster;
            _audioService = new WasapiAudioCaptureService();
            _audioService.AudioDataAvailable += OnAudioDataAvailable;
            CurrentSource = new ScreenCaptureSource(0);
        }

        public void SetSource(ICaptureSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            lock (_sourceLock)
            {
                var old = CurrentSource;
                if (ReferenceEquals(old, source)) return;
                lock (_stateLock)
                {
                    CurrentSource = source;
                    _sourceVersion++;
                    _captureEpoch++;
                }
                old.Dispose();
            }
            _frames?.Wake();
        }

        public void SetAudioEnabled(bool enabled)
        {
            AudioEnabled = enabled;
            if (IsRunning)
            {
                if (enabled && !_audioService.IsRunning)
                {
                    _audioService.Start(SelectedAudioDevice?.Device, SelectedAudioDevice?.IsInput ?? false);
                    if (_audioService.AudioFormat != null)
                    {
                        var fmt = _audioService.AudioFormat;
                        _ = _broadcaster.BroadcastAudioFormatAsync(fmt.SampleRate, (short)fmt.Channels, (short)fmt.BitsPerSample);
                    }
                }
                else if (!enabled && _audioService.IsRunning)
                {
                    _audioService.Stop();
                }
            }
        }

        public void SetAudioDevice(AudioDeviceDescriptor? device)
        {
            SelectedAudioDevice = device;
            if (IsRunning && AudioEnabled)
            {
                _audioService.Stop();
                _audioService.Start(device?.Device, device?.IsInput ?? false);
                if (_audioService.AudioFormat != null)
                {
                    var fmt = _audioService.AudioFormat;
                    _ = _broadcaster.BroadcastAudioFormatAsync(fmt.SampleRate, (short)fmt.Channels, (short)fmt.BitsPerSample);
                }
            }
        }

        public void SetAudioDevice(MMDevice? device)
        {
            SetAudioDevice(device != null ? new AudioDeviceDescriptor(device.ID, device.FriendlyName, false, device) : null);
        }

        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (IsRunning || _stopTask is { IsCompleted: false }) return;
                _stopTask = null;
                if (AudioEnabled)
                {
                    _audioService.Start(SelectedAudioDevice?.Device, SelectedAudioDevice?.IsInput ?? false);
                    if (_audioService.AudioFormat != null)
                    {
                        var fmt = _audioService.AudioFormat;
                        _ = _broadcaster.BroadcastAudioFormatAsync(fmt.SampleRate, (short)fmt.Channels, (short)fmt.BitsPerSample);
                    }
                }

                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                var frames = new LatestFrameQueue<CapturedFrame>(token);
                _frames = frames;
                Interlocked.Exchange(ref _discardedFrames, 0);
                Volatile.Write(ref _lastFrameAgeMilliseconds, 0);
                Volatile.Write(ref _lastQueueWaitMilliseconds, 0);
                _lastPreviewTicks = 0;
                lock (_stateLock) _captureEpoch++;
                IsRunning = true;

                // Independent dedicated workers: capture can overlap JPEG encoding.
                _captureTask = _encodeTask = null;
                try
                {
                    _encodeTask = Task.Factory.StartNew(() => EncodeLoop(frames, token), token,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    _captureTask = Task.Factory.StartNew(() => CaptureLoop(frames, token), token,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                catch
                {
                    IsRunning = false;
                    _cts.Cancel();
                    frames.Complete();
                    _stopTask = StopCoreAsync();
                    throw;
                }
            }
        }

        public Task StopAsync()
        {
            lock (_lifecycleLock)
            {
                if (_stopTask != null) return _stopTask;
                if (!IsRunning) return Task.CompletedTask;
                IsRunning = false;
                lock (_stateLock)
                {
                    _isFrozen = false;
                    _captureEpoch++;
                }
                _cts?.Cancel();
                return _stopTask = StopCoreAsync();
            }
        }

        private async Task StopCoreAsync()
        {
            try { await Task.Run(() => _audioService.Stop()); } catch { }
            try
            {
                await Task.WhenAll(_captureTask ?? Task.CompletedTask, _encodeTask ?? Task.CompletedTask);
            }
            catch { }
            _frames?.Dispose();
            _cts?.Dispose();
            _cts = null;
        }

        private async void OnAudioDataAvailable(byte[] pcmChunk)
        {
            if (!IsRunning || !AudioEnabled) return;
            await _broadcaster.BroadcastAudioChunkAsync(pcmChunk);
        }

        private (bool Frozen, int Epoch, int SourceVersion) ReadCaptureState()
        {
            lock (_stateLock) return (_isFrozen, _captureEpoch, _sourceVersion);
        }

        private void CaptureLoop(LatestFrameQueue<CapturedFrame> frames, CancellationToken token)
        {
            using var pacer = new FramePacer(token);
            long deadline = Stopwatch.GetTimestamp();
            int previousFps = Math.Clamp(TargetFps, 1, 60);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    long iterationStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        CapturedFrame? captured = null;
                        // Source disposal/switching cannot race with its native capture call.
                        lock (_sourceLock)
                        {
                            var state = ReadCaptureState();
                            if (!state.Frozen)
                            {
                                long started = Stopwatch.GetTimestamp();
                                var bitmap = CurrentSource.CaptureFrame(CaptureMouse);
                                long completed = Stopwatch.GetTimestamp();
                                Volatile.Write(ref _lastCaptureMilliseconds, Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds);
                                if (bitmap != null)
                                    captured = new CapturedFrame(bitmap, started, completed, state.Epoch, state.SourceVersion);
                            }
                        }
                        if (captured != null) frames.Publish(captured);
                    }
                    catch
                    {
                        // Desktop changes and unavailable windows are transient capture errors.
                    }
                    int fps = Math.Clamp(TargetFps, 1, 60);
                    if (fps != previousFps) { deadline = iterationStarted; previousFps = fps; }
                    long now = Stopwatch.GetTimestamp();
                    deadline = FramePacer.NextDeadline(deadline, now, fps);
                    if (!pacer.Wait(TimeSpan.FromSeconds((deadline - now) / (double)Stopwatch.Frequency))) break;
                }
            }
            finally
            {
                frames.Complete();
            }
        }

        private void EncodeLoop(LatestFrameQueue<CapturedFrame> frames, CancellationToken token)
        {
            CapturedFrame? retained = null;
            long lastBroadcast = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var state = ReadCaptureState();
                    int wait = Timeout.Infinite;
                    if (state.Frozen && retained != null && retained.SourceVersion == state.SourceVersion)
                        wait = Math.Max(0, 1500 - (int)Math.Min(1500, Stopwatch.GetElapsedTime(lastBroadcast).TotalMilliseconds));
                    var captured = frames.Take(wait);
                    if (captured != null)
                    {
                        try
                        {
                            state = ReadCaptureState();
                            if (state.Frozen || captured.Epoch != state.Epoch)
                            {
                                Interlocked.Increment(ref _discardedFrames);
                                continue;
                            }
                            Volatile.Write(ref _lastQueueWaitMilliseconds, Stopwatch.GetElapsedTime(captured.Completed).TotalMilliseconds);
                            BroadcastFrame(captured, updatePreview: true);
                            lastBroadcast = Stopwatch.GetTimestamp();
                            var previous = retained;
                            retained = captured;
                            captured = null; // Encoding worker owns this frame until replacement or shutdown.
                            previous?.Dispose();
                        }
                        catch { }
                        finally { captured?.Dispose(); }
                    }
                    else if (token.IsCancellationRequested || frames.IsCompleted)
                    {
                        break;
                    }
                    state = ReadCaptureState();
                    if (state.Frozen && retained != null && retained.SourceVersion == state.SourceVersion &&
                        Stopwatch.GetElapsedTime(lastBroadcast).TotalMilliseconds >= 1500)
                    {
                        try { BroadcastFrame(retained, updatePreview: false); } catch { }
                        lastBroadcast = Stopwatch.GetTimestamp();
                    }
                }
            }
            finally
            {
                retained?.Dispose();
            }
        }

        private void BroadcastFrame(CapturedFrame frame, bool updatePreview)
        {
            long now = Stopwatch.GetTimestamp();
            if (updatePreview && PreviewFrameAvailable != null && Stopwatch.GetElapsedTime(_lastPreviewTicks, now).TotalMilliseconds >= 160)
            {
                _lastPreviewTicks = now;
                try { PreviewFrameAvailable(frame.Bitmap); } catch { }
            }
            _broadcaster.BroadcastBitmapAsync(frame.Bitmap).GetAwaiter().GetResult();
            double age = Stopwatch.GetElapsedTime(frame.Started).TotalMilliseconds;
            Volatile.Write(ref _lastFrameAgeMilliseconds, age);
            try { FrameProcessed?.Invoke(age); } catch { }
        }

        private sealed class CapturedFrame(Bitmap bitmap, long started, long completed, int epoch, int sourceVersion) : IDisposable
        {
            public Bitmap Bitmap { get; } = bitmap;
            public long Started { get; } = started;
            public long Completed { get; } = completed;
            public int Epoch { get; } = epoch;
            public int SourceVersion { get; } = sourceVersion;
            public void Dispose() => Bitmap.Dispose();
        }

        public void Dispose()
        {
            var stopTask = StopAsync();
            _ = Task.Run(async () =>
            {
                await stopTask;
                try { _audioService.Dispose(); } catch { }
                CurrentSource?.Dispose();
            });
        }
    }
}
