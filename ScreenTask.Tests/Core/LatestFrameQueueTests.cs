using ScreenTask.Core.Capture;

namespace ScreenTask.Tests.Core;

public class LatestFrameQueueTests
{
    [Fact]
    public void NewestFrame_ReplacesPendingFrame_WithoutDisposingConsumerOwnedFrame()
    {
        using var cancellation = new CancellationTokenSource();
        using var queue = new LatestFrameQueue<TestFrame>(cancellation.Token);
        var first = new TestFrame();
        var obsolete = new TestFrame();
        var newest = new TestFrame();
        queue.Publish(first);
        using var inUse = queue.Take(0);
        queue.Publish(obsolete);
        queue.Publish(newest);
        Assert.Equal(1, queue.PendingCount);
        Assert.Equal(1, queue.DroppedCount);
        Assert.Equal(0, first.Disposals);
        Assert.Equal(1, obsolete.Disposals);
        using var next = queue.Take(0);
        Assert.Same(newest, next);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Complete_WakesBlockedConsumer_AndRejectsLaterFrames()
    {
        using var cancellation = new CancellationTokenSource();
        using var queue = new LatestFrameQueue<TestFrame>(cancellation.Token);
        var take = Task.Run(() => queue.Take(Timeout.Infinite));
        queue.Complete();
        Assert.Null(await take.WaitAsync(TimeSpan.FromSeconds(3)));
        var late = new TestFrame();
        queue.Publish(late);
        Assert.Equal(1, late.Disposals);
        Assert.True(queue.IsCompleted);
    }

    [Fact]
    public async Task Cancellation_WakesConsumer_AndDisposalReleasesPendingFrameOnce()
    {
        using var cancellation = new CancellationTokenSource();
        using var queue = new LatestFrameQueue<TestFrame>(cancellation.Token);
        var take = Task.Run(() => queue.Take(Timeout.Infinite));
        cancellation.Cancel();
        Assert.Null(await take.WaitAsync(TimeSpan.FromSeconds(3)));
        var rejected = new TestFrame();
        queue.Publish(rejected);
        Assert.Equal(1, rejected.Disposals);
        using var secondCancellation = new CancellationTokenSource();
        var second = new LatestFrameQueue<TestFrame>(secondCancellation.Token);
        var pending = new TestFrame();
        second.Publish(pending);
        second.Dispose();
        second.Dispose();
        second.Wake();
        Assert.Equal(1, pending.Disposals);
    }

    private sealed class TestFrame : IDisposable
    {
        public int Disposals;
        public void Dispose() => Disposals++;
    }
}
