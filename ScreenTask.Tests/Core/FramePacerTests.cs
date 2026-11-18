using System.Diagnostics;
using ScreenTask.Core.Capture;

namespace ScreenTask.Tests.Core;

public class FramePacerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingWait_CanBeCancelled_AndWorkerCanExit(bool nativeTimer)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = Task.Factory.StartNew(() =>
        {
            using var pacer = new FramePacer(cancellation.Token, nativeTimer);
            started.SetResult();
            return pacer.Wait(TimeSpan.FromSeconds(30));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(30);
        Assert.False(worker.IsCompleted);
        cancellation.Cancel();
        Assert.False(await worker.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Wait_HandlesExpiredDeadlineAndNormalCompletion(bool nativeTimer)
    {
        using var cancellation = new CancellationTokenSource();
        using var pacer = new FramePacer(cancellation.Token, nativeTimer);
        Assert.True(pacer.Wait(TimeSpan.Zero));
        Assert.True(pacer.Wait(TimeSpan.FromMilliseconds(1)));
        cancellation.Cancel();
        Assert.False(pacer.Wait(TimeSpan.Zero));
    }

    [Fact]
    public void MissedDeadline_DoesNotAccumulateCatchUpWork()
    {
        long now = Stopwatch.GetTimestamp();
        long oldDeadline = now - Stopwatch.Frequency;
        Assert.Equal(now, FramePacer.NextDeadline(oldDeadline, now, 30));
        Assert.True(FramePacer.NextDeadline(now, now, 30) > now);
    }

    [Fact]
    public void FrameTargets_AreClampedToSupportedLimits()
    {
        long now = Stopwatch.GetTimestamp();
        Assert.Equal(FramePacer.NextDeadline(now, now, 1), FramePacer.NextDeadline(now, now, 0));
        Assert.Equal(FramePacer.NextDeadline(now, now, 60), FramePacer.NextDeadline(now, now, 120));
    }
}
