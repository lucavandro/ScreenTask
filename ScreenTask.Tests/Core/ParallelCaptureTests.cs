using System.Collections.Concurrent;
using System.Drawing;
using System.Net.WebSockets;
using ScreenTask.Core.Capture;
using ScreenTask.Core.Streaming;

namespace ScreenTask.Tests.Core;

public class ParallelCaptureTests
{
    [Fact]
    public async Task SlowConsumer_DoesNotBlockCapture_AndOnlyNewestPendingFrameIsEncoded()
    {
        using var broadcaster = new StreamBroadcaster();
        using var socket = new SinkSocket();
        broadcaster.AddVideoClient("viewer", socket);
        using var source = new SteppedSource();
        using var engine = new ScreenCaptureEngine(broadcaster) { TargetFps = 60 };
        engine.SetSource(source);
        using var releaseEncoder = new ManualResetEventSlim();
        var encoderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PreviewFrameAvailable += _ =>
        {
            encoderEntered.TrySetResult();
            releaseEncoder.Wait(TimeSpan.FromSeconds(5));
        };
        engine.Start();
        try
        {
            await encoderEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            source.AllowFurtherCaptures.Set();
            await source.FifthEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, engine.PendingFrameCount);
            Assert.Equal(2, engine.DroppedCapturedFrames);
            Assert.Empty(socket.Packets);
            releaseEncoder.Set();
            await socket.SecondSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // Frame 1 was in use; frames 2/3 were replaced; frame 4 is the newest ready frame.
            using (var stream = new MemoryStream(broadcaster.GetLatestSnapshot()))
            using (var decoded = new Bitmap(stream)) Assert.True(decoded.GetPixel(0, 0).B > 200);
            var stopping = engine.StopAsync();
            Assert.False(stopping.IsCompleted); // Still waits for the independent capture worker.
            source.ReleaseFifth.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, engine.PendingFrameCount);
            foreach (var bitmap in source.CreatedFrames)
                Assert.Throws<ArgumentException>(() => bitmap.GetPixel(0, 0));
        }
        finally
        {
            releaseEncoder.Set();
            source.AllowFurtherCaptures.Set();
            source.ReleaseFifth.Set();
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task SourceChange_DiscardsPendingFramesFromPreviousSource()
    {
        using var broadcaster = new StreamBroadcaster();
        using var socket = new SinkSocket();
        broadcaster.AddVideoClient("viewer", socket);
        using var oldSource = new SteppedSource();
        using var newSource = new SolidSource();
        using var engine = new ScreenCaptureEngine(broadcaster) { TargetFps = 60 };
        engine.SetSource(oldSource);
        using var releaseEncoder = new ManualResetEventSlim();
        var encoderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int previews = 0;
        engine.PreviewFrameAvailable += _ =>
        {
            if (Interlocked.Increment(ref previews) != 1) return;
            encoderEntered.TrySetResult();
            releaseEncoder.Wait(TimeSpan.FromSeconds(5));
        };
        engine.Start();
        try
        {
            await encoderEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            oldSource.AllowFurtherCaptures.Set();
            await oldSource.FifthEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var change = Task.Run(() => engine.SetSource(newSource));
            oldSource.ReleaseFifth.Set();
            await change.WaitAsync(TimeSpan.FromSeconds(3));
            await newSource.Captured.Task.WaitAsync(TimeSpan.FromSeconds(3));
            releaseEncoder.Set();
            await socket.SecondSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var stream = new MemoryStream(broadcaster.GetLatestSnapshot());
            using var decoded = new Bitmap(stream);
            Assert.True(decoded.GetPixel(0, 0).G > 200); // New source, never an old pending blue frame.
        }
        finally
        {
            releaseEncoder.Set();
            oldSource.AllowFurtherCaptures.Set();
            oldSource.ReleaseFifth.Set();
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Freeze_StopsNewCaptures_ResendsRetainedFrame_AndResumes()
    {
        using var broadcaster = new StreamBroadcaster();
        using var socket = new SinkSocket();
        broadcaster.AddVideoClient("viewer", socket);
        using var source = new SolidSource();
        using var engine = new ScreenCaptureEngine(broadcaster) { TargetFps = 60 };
        engine.SetSource(source);
        Action<Bitmap> freeze = _ => engine.IsFrozen = true;
        engine.PreviewFrameAvailable += freeze;
        engine.Start();
        try
        {
            await source.Captured.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(200); // Allow any capture already in flight to finish.
            int captures = source.Count;
            await socket.SecondSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(captures, source.Count);
            Assert.Equal(socket.Packets.First(), socket.Packets.Last());
            engine.PreviewFrameAvailable -= freeze;
            engine.IsFrozen = false;
            await socket.ThirdSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(source.Count > captures);
        }
        finally { await engine.StopAsync(); }

    }

    [Fact]
    public async Task StopAndRestart_CreatesFreshWorkersAndQueue()
    {
        using var broadcaster = new StreamBroadcaster();
        using var source = new SolidSource();
        using var engine = new ScreenCaptureEngine(broadcaster);
        engine.SetSource(source);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.FrameProcessed += _ => first.TrySetResult();
        engine.Start();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await engine.StopAsync();
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.FrameProcessed += _ => restarted.TrySetResult();
        engine.Start();
        try { await restarted.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { await engine.StopAsync(); }
        Assert.Equal(0, engine.PendingFrameCount);
        Assert.False(engine.IsRunning);
    }

    private sealed class SteppedSource : ICaptureSource
    {
        public readonly ManualResetEventSlim AllowFurtherCaptures = new();
        public readonly ManualResetEventSlim ReleaseFifth = new();
        public readonly TaskCompletionSource FifthEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<Bitmap> CreatedFrames { get; } = new();
        private int _count;
        public string Id => "stepped";
        public string DisplayName => Id;
        public Rectangle Bounds => new(0, 0, 32, 24);
        public bool IsValid => true;
        public Bitmap CaptureFrame(bool cursor)
        {
            int index = Interlocked.Increment(ref _count);
            if (index == 2) AllowFurtherCaptures.Wait(TimeSpan.FromSeconds(5));
            var bitmap = new Bitmap(32, 24);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.Clear(index == 4 ? Color.Blue : Color.Red);
            CreatedFrames.Enqueue(bitmap);
            if (index == 5)
            {
                FifthEntered.TrySetResult();
                ReleaseFifth.Wait(TimeSpan.FromSeconds(5));
            }
            return bitmap;
        }
        public void Dispose() { }
    }

    private sealed class SolidSource : ICaptureSource
    {
        public readonly TaskCompletionSource Captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count;
        public string Id => "solid";
        public string DisplayName => Id;
        public Rectangle Bounds => new(0, 0, 32, 24);
        public bool IsValid => true;
        public Bitmap CaptureFrame(bool cursor)
        {
            var bitmap = new Bitmap(32, 24);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Lime);
            Interlocked.Increment(ref Count);
            Captured.TrySetResult();
            return bitmap;
        }
        public void Dispose() { }
    }

    private sealed class SinkSocket : WebSocket
    {
        public ConcurrentQueue<byte[]> Packets { get; } = new();
        public readonly TaskCompletionSource SecondSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ThirdSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken t) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken t) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken t) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType type, bool end, CancellationToken t)
        {
            Packets.Enqueue(b.ToArray());
            if (Packets.Count >= 2) SecondSent.TrySetResult();
            if (Packets.Count >= 3) ThirdSent.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
