using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenTask.Core.Streaming
{
    public class StreamBroadcaster : IDisposable
    {
        private readonly ConcurrentDictionary<string, (WebSocket Socket, SemaphoreSlim Lock)> _videoClients = new();
        private readonly ConcurrentDictionary<string, (WebSocket Socket, SemaphoreSlim Lock)> _audioClients = new();
        private readonly ConcurrentDictionary<string, ViewerSession> _sessions = new();
        private readonly ImageCodecInfo _jpegEncoder;
        private double _lastEncodeMilliseconds;
        public double LastEncodeMilliseconds => Volatile.Read(ref _lastEncodeMilliseconds);

        private EncoderParameters _encoderParams;
        private long _frameCounter;
        private long _totalBytesTransferred;
        private long _totalFramesSent;
        private long _totalFramesDropped;
        private int _currentFps;
        private int _fpsCounter;
        private long _lastFpsTimestamp;
        private byte[] _latestVideoPacket = Array.Empty<byte>();
        private byte[]? _latestJpegSnapshot;
        private readonly object _encoderLock = new();
        private readonly object _snapshotLock = new();
        private byte[]? _latestAudioFormatPacket;
        private readonly object _audioFormatLock = new();

        public event Action<ViewerSession>? SessionConnected;
        public event Action<string>? SessionDisconnected;
        public event Action<ViewerSession>? SessionUpdated;

        public int ConnectedClientsCount => Math.Max(_videoClients.Count, _audioClients.Count);
        public int CurrentFps => _currentFps;
        public long TotalBytesTransferred => Interlocked.Read(ref _totalBytesTransferred);
        public long TotalFramesSent => Interlocked.Read(ref _totalFramesSent);
        public long TotalFramesDropped => Interlocked.Read(ref _totalFramesDropped);

        public double FrameDropPercentage
        {
            get
            {
                long sent = TotalFramesSent;
                long dropped = TotalFramesDropped;
                long total = sent + dropped;
                return total == 0 ? 0.0 : (double)dropped / total * 100.0;
            }
        }

        public IReadOnlyCollection<ViewerSession> ConnectedSessions => _sessions.Values.ToArray();

        public double AverageFleetLatencyMs
        {
            get
            {
                var sessions = _sessions.Values.ToArray();
                if (sessions.Length == 0) return 0.0;
                double sum = 0.0;
                int count = 0;
                foreach (var s in sessions)
                {
                    if (s.RttMs > 0)
                    {
                        sum += s.RttMs;
                        count++;
                    }
                }
                return count > 0 ? sum / count : 0.0;
            }
        }

        public StreamBroadcaster(int initialQuality = 75)
        {
            _jpegEncoder = GetEncoder(ImageFormat.Jpeg) ?? throw new InvalidOperationException("JPEG encoder not found");
            _encoderParams = CreateEncoderParams(Math.Clamp(initialQuality, 10, 100));
            _lastFpsTimestamp = Environment.TickCount64;
        }

        public void SetQuality(int quality)
        {
            lock (_encoderLock)
            {
                var previous = _encoderParams;
                _encoderParams = CreateEncoderParams(Math.Clamp(quality, 10, 100));
                previous.Dispose();
            }
        }

        private static EncoderParameters CreateEncoderParams(int quality)
        {
            var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            return p;
        }

        public void AddVideoClient(string id, WebSocket socket, string remoteIp = "127.0.0.1", string? userAgent = null)
        {
            var clientLock = new SemaphoreSlim(1, 1);
            _videoClients[id] = (socket, clientLock);

            var (device, icon, browser) = UserAgentParser.Parse(userAgent);
            var session = new ViewerSession
            {
                Id = id,
                RemoteIp = remoteIp,
                UserAgent = userAgent ?? "",
                DeviceName = device,
                DeviceIcon = icon,
                BrowserName = browser,
                ConnectedAt = DateTime.UtcNow
            };

            _sessions[id] = session;
            SessionConnected?.Invoke(session);
        }

        public void RemoveVideoClient(string id)
        {
            if (_videoClients.TryRemove(id, out var entry))
            {
                // SemaphoreSlim has no wait handle here; let in-flight operations release it before GC.
            }
            if (_sessions.TryRemove(id, out _))
            {
                SessionDisconnected?.Invoke(id);
            }
        }

        public void UpdateTelemetry(string id, double rttMs, string? viewMode)
        {
            if (_sessions.TryGetValue(id, out var session))
            {
                if (rttMs > 0) session.RttMs = rttMs;
                if (!string.IsNullOrWhiteSpace(viewMode)) session.ViewMode = viewMode;
                session.LastTelemetryTime = DateTime.UtcNow;
                SessionUpdated?.Invoke(session);
            }
        }

        public void SetClientAudioMuted(string id, bool isMuted)
        {
            if (_sessions.TryGetValue(id, out var session))
            {
                session.IsAudioMuted = isMuted;
                SessionUpdated?.Invoke(session);
            }
        }

        // Backward compatibility
        public void AddClient(string id, WebSocket socket) => AddVideoClient(id, socket);
        public void RemoveClient(string id) => RemoveVideoClient(id);

        public void AddAudioClient(string id, WebSocket socket)
        {
            var clientLock = new SemaphoreSlim(1, 1);
            _audioClients[id] = (socket, clientLock);

            byte[]? formatPacket;
            lock (_audioFormatLock)
            {
                formatPacket = _latestAudioFormatPacket;
            }

            if (formatPacket != null)
            {
                _ = SendToClientAsync(id, socket, clientLock, formatPacket, isAudio: true);
            }
        }

        public void RemoveAudioClient(string id)
        {
            if (_audioClients.TryRemove(id, out var entry))
            {
                // SemaphoreSlim has no wait handle here; let in-flight operations release it before GC.
            }
        }

        public async Task KickAllClientsAsync(WebSocketCloseStatus closeStatus, string reason)
        {
            var clients = _videoClients.ToArray();
            var audioClients = _audioClients.ToArray();
            foreach (var client in clients) RemoveVideoClient(client.Key);
            foreach (var client in audioClients) RemoveAudioClient(client.Key);
            await Task.WhenAll(clients.Select(c => CloseClientAsync(c.Value, closeStatus, reason))
                .Concat(audioClients.Select(c => CloseClientAsync(c.Value, closeStatus, reason))));
        }

        public async Task KickClientAsync(string id, WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure, string reason = "Kicked by host")
        {
            _videoClients.TryGetValue(id, out var videoEntry);
            _audioClients.TryGetValue(id, out var audioEntry);
            RemoveVideoClient(id);
            RemoveAudioClient(id);
            var tasks = new List<Task>();
            if (videoEntry.Socket != null) tasks.Add(CloseClientAsync(videoEntry, closeStatus, reason));
            if (audioEntry.Socket != null) tasks.Add(CloseClientAsync(audioEntry, closeStatus, reason));
            await Task.WhenAll(tasks);
        }

        private static async Task CloseClientAsync((WebSocket Socket, SemaphoreSlim Lock) client, WebSocketCloseStatus closeStatus, string reason)
        {
            bool acquired = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                // Close frames share the socket's send path with video, audio and pong.
                await client.Lock.WaitAsync(timeout.Token);
                acquired = true;
                if (client.Socket.State == WebSocketState.Open)
                    await client.Socket.CloseAsync(closeStatus, reason, timeout.Token);
            }
            catch
            {
                try { client.Socket.Abort(); } catch { }
            }
            finally
            {
                if (acquired) client.Lock.Release();
            }
        }

        public async Task<int> BroadcastAnnouncementAsync(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || _videoClients.IsEmpty) return 0;

            var msgBytes = System.Text.Encoding.UTF8.GetBytes(message);
            var packet = new byte[msgBytes.Length + 1];
            packet[0] = 0x04; // Broadcast announcement packet type
            Buffer.BlockCopy(msgBytes, 0, packet, 1, msgBytes.Length);


            var sendTasks = new List<Task>();
            foreach (var kvp in _videoClients)
            {
                sendTasks.Add(SendToClientAsync(kvp.Key, kvp.Value.Socket, kvp.Value.Lock, packet, isAudio: false));
            }

            await Task.WhenAll(sendTasks);
            return sendTasks.Count;
        }

        public async Task SendPongAsync(string clientId, double timestamp)
        {
            if (_videoClients.TryGetValue(clientId, out var entry))
            {
                if (entry.Socket.State != WebSocketState.Open) return;
                var pongBytes = System.Text.Encoding.UTF8.GetBytes($"{{\"type\":\"pong\",\"t\":{timestamp}}}");
                if (await entry.Lock.WaitAsync(50))
                {
                    try
                    {
                        if (entry.Socket.State == WebSocketState.Open)
                        {
                            await entry.Socket.SendAsync(new ArraySegment<byte>(pongBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    }
                    catch { }
                    finally
                    {
                        entry.Lock.Release();
                    }
                }
            }
        }

        public byte[] GetLatestSnapshot()
        {
            lock (_snapshotLock)
            {
                if (_latestVideoPacket.Length == 0) return Array.Empty<byte>();
                return _latestJpegSnapshot ??= _latestVideoPacket.AsSpan(1).ToArray();
            }
        }

        public Task BroadcastBitmapAsync(Bitmap bitmap)
        {
            if (bitmap == null) return Task.CompletedTask;
            long encodeStarted = Stopwatch.GetTimestamp();

            // Snapshot the client collection so connecting/disconnecting viewers
            // cannot change the audience while this immutable JPEG is encoded.
            var clients = _videoClients.ToArray();
            byte[] packet;
            lock (_encoderLock)
            {
                packet = EncodeJpegPacket(bitmap);
            }
            Volatile.Write(ref _lastEncodeMilliseconds, Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds);
            lock (_snapshotLock)
            {
                _latestVideoPacket = packet;
                _latestJpegSnapshot = null; // Materialize the header-free JPEG only on request.
            }

            Interlocked.Increment(ref _frameCounter);

            // Update FPS metrics
            Interlocked.Increment(ref _fpsCounter);
            var now = Environment.TickCount64;
            if (now - _lastFpsTimestamp >= 1000)
            {
                _currentFps = _fpsCounter;
                _fpsCounter = 0;
                _lastFpsTimestamp = now;
            }

            // Each client has at most one send in flight. Busy clients skip this frame;
            // capture and fast viewers never wait for a slow network connection.
            foreach (var kvp in clients)
            {
                _ = SendToClientAsync(kvp.Key, kvp.Value.Socket, kvp.Value.Lock, packet, isAudio: false);
            }
            return Task.CompletedTask;
        }

        // Caller holds _encoderLock.
        private byte[] EncodeJpegPacket(Bitmap bitmap)
        {
            using var stream = new PooledBufferWriterStream(64 * 1024);
            stream.Position = 1;
            bitmap.Save(stream, _jpegEncoder, _encoderParams);
            stream.Buffer[0] = 0x01;
            return stream.WrittenSpan.ToArray();
        }

        public async Task BroadcastAudioChunkAsync(byte[] audioPcm)
        {
            if (audioPcm == null || audioPcm.Length == 0) return;

            // Send to dedicated audio clients; if none connected yet, fallback to video clients
            var targetClients = !_audioClients.IsEmpty ? _audioClients : _videoClients;
            if (targetClients.IsEmpty) return;

            // Construct binary packet: [0x02 (Audio), ...pcm bytes]
            var packet = new byte[audioPcm.Length + 1];
            packet[0] = 0x02; // Audio chunk type
            Buffer.BlockCopy(audioPcm, 0, packet, 1, audioPcm.Length);


            var sendTasks = new List<Task>(targetClients.Count);
            foreach (var kvp in targetClients)
            {
                if (_sessions.TryGetValue(kvp.Key, out var session) && session.IsAudioMuted)
                {
                    continue;
                }
                sendTasks.Add(SendToClientAsync(kvp.Key, kvp.Value.Socket, kvp.Value.Lock, packet, isAudio: true));
            }

            await Task.WhenAll(sendTasks);
        }

        public async Task BroadcastAudioFormatAsync(int sampleRate, short channels, short bitsPerSample)
        {
            // Construct metadata packet: [0x03, 4 bytes sampleRate, 2 bytes channels, 2 bytes bits]
            var packet = new byte[9];
            packet[0] = 0x03; // Audio format info
            BitConverter.GetBytes(sampleRate).CopyTo(packet, 1);
            BitConverter.GetBytes(channels).CopyTo(packet, 5);
            BitConverter.GetBytes(bitsPerSample).CopyTo(packet, 7);

            lock (_audioFormatLock)
            {
                _latestAudioFormatPacket = packet;
            }

            var count = _audioClients.Count + _videoClients.Count;
            if (count == 0) return;

            var sendTasks = new List<Task>(count);
            foreach (var kvp in _audioClients)
            {
                sendTasks.Add(SendToClientAsync(kvp.Key, kvp.Value.Socket, kvp.Value.Lock, packet, isAudio: true));
            }
            foreach (var kvp in _videoClients)
            {
                sendTasks.Add(SendToClientAsync(kvp.Key, kvp.Value.Socket, kvp.Value.Lock, packet, isAudio: false));
            }

            await Task.WhenAll(sendTasks);
        }

        private async Task SendToClientAsync(string clientId, WebSocket socket, SemaphoreSlim clientLock, ArraySegment<byte> data, bool isAudio)
        {
            if (socket.State != WebSocketState.Open)
            {
                if (isAudio) RemoveAudioClient(clientId);
                else RemoveVideoClient(clientId);
                return;
            }

            // Drop-frame protection:
            // For video: if previous video frame is still sending, drop the new video frame to maintain real-time sync.
            // For audio: audio packets are small (1KB). A small 50ms wait ensures audio packets are never dropped due to transient lock contention.
            bool isVideoFrame = !isAudio && data.Count > 0 && data.Array?[data.Offset] == 0x01;
            if (isAudio)
            {
                if (!await clientLock.WaitAsync(50)) return;
            }
            else
            {
                if (!clientLock.Wait(0))
                {
                    if (isVideoFrame) Interlocked.Increment(ref _totalFramesDropped);
                    return;
                }
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.SendAsync(data, WebSocketMessageType.Binary, true, timeout.Token);
                Interlocked.Add(ref _totalBytesTransferred, data.Count);
                if (isVideoFrame) Interlocked.Increment(ref _totalFramesSent);
            }
            catch (Exception)
            {
                try { socket.Abort(); } catch { }
                if (isAudio) RemoveAudioClient(clientId);
                else RemoveVideoClient(clientId);
            }
            finally
            {
                clientLock.Release();
            }
        }

        private Task SendToClientAsync(string clientId, WebSocket socket, SemaphoreSlim clientLock, byte[] data, bool isAudio)
            => SendToClientAsync(clientId, socket, clientLock, new ArraySegment<byte>(data), isAudio);

        private static ImageCodecInfo? GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid) return codec;
            }
            return null;
        }

        public void Dispose()
        {
            foreach (var kvp in _videoClients)
            {
                try
                {
                    if (kvp.Value.Socket.State == WebSocketState.Open) kvp.Value.Socket.Abort();
                    // In-flight operations retain the managed semaphore until completion.
                }
                catch { }
            }
            _videoClients.Clear();

            foreach (var kvp in _audioClients)
            {
                try
                {
                    if (kvp.Value.Socket.State == WebSocketState.Open) kvp.Value.Socket.Abort();
                    // In-flight operations retain the managed semaphore until completion.
                }
                catch { }
            }
            _audioClients.Clear();
            _sessions.Clear();
        }
    }
}
