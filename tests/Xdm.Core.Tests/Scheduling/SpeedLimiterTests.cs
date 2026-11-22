using System.Diagnostics;
using Xdm.Core.Scheduling;

namespace Xdm.Core.Tests.Scheduling;

public sealed class SpeedLimiterTests
{
    [Fact]
    public void Disabled_DoesNotBlock()
    {
        var limiter = new SpeedLimiter(0);
        var watch = Stopwatch.StartNew();

        limiter.Acquire(8 * 1024 * 1024, CancellationToken.None);

        watch.Stop();
        Assert.False(limiter.IsEnabled);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(200), $"不限速却等了 {watch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Enabled_ThrottlesTotalThroughput()
    {
        // 100 KB/s；申请 3 × 20 KB，理论至少 600ms（桶初始为空）。
        var limiter = new SpeedLimiter(100 * 1024);
        var watch = Stopwatch.StartNew();

        limiter.Acquire(20 * 1024, CancellationToken.None);
        limiter.Acquire(20 * 1024, CancellationToken.None);
        limiter.Acquire(20 * 1024, CancellationToken.None);

        watch.Stop();
        Assert.True(limiter.IsEnabled);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(300), $"限速未生效：{watch.ElapsedMilliseconds}ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"限速等待异常：{watch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void SetRateZero_ReleasesWaiter()
    {
        var limiter = new SpeedLimiter(1024);
        var started = new ManualResetEventSlim();
        var finished = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            started.Set();
            limiter.Acquire(50 * 1024 * 1024, CancellationToken.None);
            finished.Set();
        })
        { IsBackground = true };
        thread.Start();

        Assert.True(started.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Thread.Sleep(100);
        limiter.SetRate(0);

        Assert.True(
            finished.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken),
            "把速率设为 0 后等待线程没有放行");
        thread.Join(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Acquire_HonorsCancellation()
    {
        var limiter = new SpeedLimiter(1024);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Run(() => limiter.Acquire(100 * 1024 * 1024, cts.Token), cts.Token));
    }

    [Fact]
    public void Rate_CanBeChangedAtRuntime()
    {
        var limiter = new SpeedLimiter(0);
        limiter.Rate = 4096;

        Assert.True(limiter.IsEnabled);
        Assert.Equal(4096, limiter.Rate);

        limiter.Rate = -1;
        Assert.Equal(0, limiter.Rate);
        Assert.False(limiter.IsEnabled);
    }
}
