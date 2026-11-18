using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using ScreenTask.Core.Streaming;
using Xunit;

namespace ScreenTask.Tests.Core
{
    public class StreamBroadcasterTests : IDisposable
    {
        private readonly StreamBroadcaster _broadcaster;

        public StreamBroadcasterTests()
        {
            _broadcaster = new StreamBroadcaster(75);
        }

        [Fact]
        public void InitialState_HasZeroClientsAndZeroFps()
        {
            Assert.Equal(0, _broadcaster.ConnectedClientsCount);
            Assert.Equal(0, _broadcaster.CurrentFps);
            Assert.Equal(0, _broadcaster.TotalBytesTransferred);
            Assert.Empty(_broadcaster.GetLatestSnapshot());
        }

        [Fact]
        public async Task BroadcastBitmapAsync_UpdatesLatestSnapshot()
        {
            using var bmp = new Bitmap(100, 100);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Blue);

            await _broadcaster.BroadcastBitmapAsync(bmp);

            var snapshot = _broadcaster.GetLatestSnapshot();
            Assert.NotEmpty(snapshot);

            // Valid JPEG header starts with 0xFF, 0xD8
            Assert.Equal(0xFF, snapshot[0]);
            Assert.Equal(0xD8, snapshot[1]);
        }

        [Fact]
        public void SetQuality_ClampsValuesProperly()
        {
            // Should not throw even with extreme values
            _broadcaster.SetQuality(150);
            _broadcaster.SetQuality(-10);
            _broadcaster.SetQuality(50);
        }

        [Fact]
        public async Task BroadcastAudioFormatAsync_DoesNotThrowAndCanBeCalledRepeatedly()
        {
            await _broadcaster.BroadcastAudioFormatAsync(44100, 2, 16);
            await _broadcaster.BroadcastAudioFormatAsync(48000, 1, 32);
        }

        [Fact]
        public async Task BroadcastAudioChunkAsync_WithNoClients_DoesNotThrow()
        {
            var pcmData = new byte[100];
            await _broadcaster.BroadcastAudioChunkAsync(pcmData);
        }

        [Fact]
        public void RemoveClients_NonExistent_DoesNotThrow()
        {
            _broadcaster.RemoveVideoClient("unknown_vid");
            _broadcaster.RemoveAudioClient("unknown_aud");
        }

        [Fact]
        public async Task SlowClient_DoesNotBlockFastViewer_AndRetainsOriginalPacket()
        {
            using var slow = new ControlledSocket(block: true);
            using var fast = new ControlledSocket(block: false);
            _broadcaster.AddVideoClient("slow", slow);
            _broadcaster.AddVideoClient("fast", fast);
            using var bmp = new Bitmap(64, 64);
            using var graphics = Graphics.FromImage(bmp);
            graphics.Clear(Color.Red);

            // This must return while the slow client's send is still pending.
            await _broadcaster.BroadcastBitmapAsync(bmp).WaitAsync(TimeSpan.FromSeconds(1));
            await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var firstJpeg = _broadcaster.GetLatestSnapshot();
            graphics.Clear(Color.Blue);
            await _broadcaster.BroadcastBitmapAsync(bmp).WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(2, fast.Packets.Count);
            Assert.Empty(slow.Packets);
            Assert.Equal(1, _broadcaster.TotalFramesDropped);
            Assert.NotEqual(firstJpeg, _broadcaster.GetLatestSnapshot());

            // Removing a viewer during send must not dispose its owned semaphore.
            _broadcaster.RemoveVideoClient("slow");
            slow.Release.TrySetResult();
            await slow.Completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(slow.Packets.TryPeek(out var packet));
            Assert.Equal(0x01, packet[0]);
            Assert.Equal(firstJpeg, packet.AsSpan(1).ToArray());
        }

        [Fact]
        public async Task StalledClient_IsAbortedAfterSendTimeout()
        {
            using var stalled = new ControlledSocket(block: true);
            _broadcaster.AddVideoClient("stalled", stalled);
            using var bmp = new Bitmap(16, 16);
            await _broadcaster.BroadcastBitmapAsync(bmp);

            await stalled.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Abort happens before removal; wait for the disconnection notification.
            for (int i = 0; i < 100 && _broadcaster.ConnectedClientsCount != 0; i++)
                await Task.Delay(10);
            Assert.Equal(0, _broadcaster.ConnectedClientsCount);
            Assert.Equal(0, _broadcaster.TotalFramesSent);
        }

        [Fact]
        public async Task Snapshot_StaysValidAfterNextFrame_AndAnnouncementsAreNotFrames()
        {
            using var client = new ControlledSocket(block: false);
            _broadcaster.AddVideoClient("viewer", client);
            using var bmp = new Bitmap(32, 32);
            using var graphics = Graphics.FromImage(bmp);
            graphics.Clear(Color.Red);
            await _broadcaster.BroadcastBitmapAsync(bmp);
            var first = _broadcaster.GetLatestSnapshot();
            var copy = first.ToArray();
            graphics.Clear(Color.Blue);
            await _broadcaster.BroadcastBitmapAsync(bmp);
            await _broadcaster.BroadcastAnnouncementAsync("Hello");

            Assert.Equal(copy, first);
            Assert.Equal(2, _broadcaster.TotalFramesSent);
            Assert.Equal(client.Packets.Sum(p => (long)p.Length), _broadcaster.TotalBytesTransferred);
        }

        private sealed class ControlledSocket(bool block) : WebSocket
        {
            public ConcurrentQueue<byte[]> Packets { get; } = new();
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override WebSocketCloseStatus? CloseStatus => null;
            public override string? CloseStatusDescription => null;
            public override WebSocketState State => Aborted.Task.IsCompleted ? WebSocketState.Aborted : WebSocketState.Open;
            public override string? SubProtocol => null;
            public override void Abort() => Aborted.TrySetResult();
            public override void Dispose() => Release.TrySetResult();
            public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            {
                Started.TrySetResult();
                if (block) await Release.Task.WaitAsync(cancellationToken);
                // Copy after the wait to detect reuse of a pooled or mutable packet.
                Packets.Enqueue(buffer.ToArray());
                Completed.TrySetResult();
            }
        }

        public void Dispose()
        {
            _broadcaster.Dispose();
        }
    }
}
