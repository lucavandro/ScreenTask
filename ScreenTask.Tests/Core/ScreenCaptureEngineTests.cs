using System.Drawing;
using ScreenTask.Core.Capture;
using ScreenTask.Core.Streaming;

namespace ScreenTask.Tests.Core;

public class ScreenCaptureEngineTests
{
    [Fact]
    public async Task StopAsync_WaitsForCapture_AndConcurrentStopsShareCompletion()
    {
        using var broadcaster = new StreamBroadcaster();
        using var source = new BlockingSource();
        using var engine = new ScreenCaptureEngine(broadcaster);
        engine.SetSource(source);
        engine.Start();
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var firstStop = engine.StopAsync();
            var secondStop = engine.StopAsync();
            Assert.Same(firstStop, secondStop);
            Assert.False(firstStop.IsCompleted);
            source.Release.Set();
            await firstStop.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(engine.IsRunning);
            Assert.Empty(broadcaster.GetLatestSnapshot()); // A capture completed after stop must not enter the encoder.
            Assert.NotNull(source.LastFrame);
            Assert.Throws<ArgumentException>(() => source.LastFrame.GetPixel(0, 0));
        }
        finally
        {
            source.Release.Set();
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Freeze_RetainsCapturedFrame_AndUnfreezeDoesNotDisposeItDuringEncoding()
    {
        using var broadcaster = new StreamBroadcaster();
        using var source = new BlockingSource();
        source.Release.Set();
        using var engine = new ScreenCaptureEngine(broadcaster);
        engine.SetSource(source);
        var preview = new TaskCompletionSource<(Bitmap Frame, Size Size, Color Pixel)>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PreviewFrameAvailable += frame =>
        {
            engine.IsFrozen = true;
            engine.IsFrozen = false;
            engine.IsFrozen = true;
            // Inspect within the callback, before this worker starts JPEG encoding.
            preview.TrySetResult((frame, frame.Size, frame.GetPixel(0, 0)));
        };
        engine.Start();
        try
        {
            var retained = await preview.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(new Size(32, 32), retained.Size);
            Assert.Equal(Color.FromArgb(255, 0, 0, 255), retained.Pixel);
            await engine.StopAsync();
            Assert.Throws<ArgumentException>(() => retained.Frame.GetPixel(0, 0));
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    private sealed class BlockingSource : ICaptureSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public Bitmap? LastFrame { get; private set; }
        public string Id => "synthetic";
        public string DisplayName => "Synthetic test source";
        public Rectangle Bounds => new(0, 0, 32, 32);
        public bool IsValid => true;
        public Bitmap CaptureFrame(bool captureCursor)
        {
            Entered.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(5));
            var bitmap = new Bitmap(32, 32);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Blue);
            LastFrame = bitmap;
            return bitmap;
        }
        public void Dispose() { }
    }
}
