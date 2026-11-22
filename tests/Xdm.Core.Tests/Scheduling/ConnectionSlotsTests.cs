using Xdm.Core.Scheduling;

namespace Xdm.Core.Tests.Scheduling;

/// <summary>
/// 全局连接配额（`ARCHITECTURE.md` §12.2 第 6 条）：名额用满就阻塞、让出即唤醒、
/// 取消/关闭能踢出等待者，容量可运行中调整。
/// </summary>
public sealed class ConnectionSlotsTests
{
    [Fact]
    public async Task Acquire_BlocksWhenSaturated_AndReleaseWakesWaiter()
    {
        using var slots = new ConnectionSlots(1);
        slots.Acquire(TestContext.Current.CancellationToken);

        Assert.Equal(1, slots.InFlight);
        Assert.True(slots.IsSaturated);

        using var acquired = new ManualResetEventSlim();
        var waiter = Task.Run(
            () =>
            {
                slots.Acquire(TestContext.Current.CancellationToken);
                acquired.Set();
            },
            TestContext.Current.CancellationToken);

        Assert.True(await WaitForAsync(() => slots.Waiting == 1), "第二个 Acquire 没有排队");
        Assert.False(acquired.IsSet, "名额没让出前不能放行");

        slots.Release();
        Assert.True(
            acquired.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
            "Release 之后没唤醒等待者");
        Assert.Equal(1, slots.InFlight);

        slots.Release();
        await waiter;
        Assert.Equal(0, slots.InFlight);
    }

    [Fact]
    public void Acquire_WithoutContention_DoesNotBlock()
    {
        using var slots = new ConnectionSlots(2);
        slots.Acquire(TestContext.Current.CancellationToken);
        slots.Acquire(TestContext.Current.CancellationToken);

        Assert.Equal(2, slots.InFlight);
        Assert.Equal(0, slots.Waiting);
    }

    [Fact]
    public async Task Acquire_ThrowsWhenCancelledWhileWaiting()
    {
        using var slots = new ConnectionSlots(1);
        slots.Acquire(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        var waiter = Task.Run(() =>
        {
            try
            {
                slots.Acquire(cts.Token);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        Assert.True(await WaitForAsync(() => slots.Waiting == 1));
        cts.Cancel();

        var thrown = await waiter;
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Equal(1, slots.InFlight);
        Assert.Equal(0, slots.Waiting);

        slots.Release();
        Assert.Equal(0, slots.InFlight);
    }

    [Fact]
    public async Task RaisingCapacity_ReleasesWaiter()
    {
        using var slots = new ConnectionSlots(1);
        slots.Acquire(TestContext.Current.CancellationToken);

        using var acquired = new ManualResetEventSlim();
        var waiter = Task.Run(
            () =>
            {
                slots.Acquire(TestContext.Current.CancellationToken);
                acquired.Set();
            },
            TestContext.Current.CancellationToken);
        Assert.True(await WaitForAsync(() => slots.Waiting == 1));

        // 用户在设置里把上限调大：等待者应立刻拿到名额，不用等兜底轮询。
        slots.SetCapacity(2);
        Assert.True(
            acquired.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
            "调大容量后等待者没被放行");

        slots.Release();
        slots.Release();
        await waiter;
        Assert.Equal(0, slots.InFlight);
    }

    [Fact]
    public void LoweringCapacity_DoesNotKillInFlight()
    {
        using var slots = new ConnectionSlots(4);
        slots.Acquire(TestContext.Current.CancellationToken);
        slots.Acquire(TestContext.Current.CancellationToken);
        slots.Acquire(TestContext.Current.CancellationToken);

        slots.SetCapacity(1);

        // 在途连接不受影响：还占着 3 个名额，只是新请求要等到降到 1 以下才放行。
        Assert.Equal(1, slots.Capacity);
        Assert.Equal(3, slots.InFlight);
        Assert.True(slots.IsSaturated);

        slots.Release();
        slots.Release();
        slots.Release();
        Assert.Equal(0, slots.InFlight);
    }

    [Fact]
    public async Task Dispose_CancelsWaiters()
    {
        var slots = new ConnectionSlots(1);
        slots.Acquire(TestContext.Current.CancellationToken);

        var waiter = Task.Run(() =>
        {
            try
            {
                slots.Acquire(TestContext.Current.CancellationToken);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });
        Assert.True(await WaitForAsync(() => slots.Waiting == 1));

        slots.Dispose();

        var thrown = await waiter;
        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Throws<OperationCanceledException>(() => slots.Acquire(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExcessRelease_DoesNotPushInFlightBelowZero()
    {
        using var slots = new ConnectionSlots(2);
        slots.Acquire(TestContext.Current.CancellationToken);
        slots.Release();
        slots.Release();
        slots.Release();

        Assert.Equal(0, slots.InFlight);
    }

    [Fact]
    public void Capacity_IsClampedToSupportedRange()
    {
        using var slots = new ConnectionSlots(0);
        Assert.Equal(1, slots.Capacity);

        slots.Capacity = 99999;
        Assert.Equal(4096, slots.Capacity);
    }

    [Fact]
    public void LeaseNullOwner_IsNoOp()
    {
        // 不限连接时引擎传 null，租约必须是空转（不能 NRE）。
        using var lease = ConnectionSlots.AcquireLease(null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Lease_ReturnsSlotOnDispose()
    {
        using var slots = new ConnectionSlots(1);
        using (ConnectionSlots.AcquireLease(slots, TestContext.Current.CancellationToken))
        {
            Assert.Equal(1, slots.InFlight);
        }

        Assert.Equal(0, slots.InFlight);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5);
        }

        return condition();
    }
}
