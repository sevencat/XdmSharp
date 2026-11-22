using System.Security.Cryptography;
using Xdm.Core.Downloads;
using Xdm.Core.Scheduling;
using Xdm.Core.Tests.Fakes;

namespace Xdm.Core.Tests.Downloads;

/// <summary>
/// 引擎真的按全局配额限流：分段再多，同时在途的连接也不超过
/// <see cref="ConnectionSlots.Capacity"/>，下完把名额全部归还（`ARCHITECTURE.md` §12.2 第 6 条）。
/// </summary>
/// <remarks>
/// 断言看的是配额自己的 <see cref="ConnectionSlots.InFlight"/>，不是服务端接受的连接数：
/// 服务端"写完 → 关 socket"比客户端"读完 → 让名额"慢半拍，按连接数统计会把已让出的连接也算进去。
/// </remarks>
public sealed class ConnectionQuotaTests
{
    [Fact]
    public async Task Engine_UsesFullQuota_AndNeverGoesBeyond()
    {
        using var slots = new ConnectionSlots(2);
        using var harness = new EngineHarness(EngineHarness.FastOptionsWith(slots));
        var bytes = EngineHarness.MakeBytes(600 * 1024, seed: 17);
        harness.Server.AddFile(
            "/capped.bin",
            bytes,
            new FileBehavior { ChunkSize = 16 * 1024, PerChunkDelay = TimeSpan.FromMilliseconds(5) });

        using var sampler = new SlotSampler(slots);
        var task = harness.CreateTask("/capped.bin", fileName: "capped.bin", maxSegments: 4);
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);
        var peak = await sampler.StopAsync();

        Assert.True(result.Success, result.Error.ToString());

        // 慢速响应保证 4 段会抢名额：必须爬到上限（说明配额真在管），又一步不能多。
        Assert.Equal(2, peak);
        Assert.True(sampler.SawWaiting, "有段在等名额，但采样没看到 Waiting > 0");
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(result.FinalPath!)));
        Assert.Equal(0, slots.InFlight);
    }

    [Fact]
    public async Task QuotaOfOne_SerializesEveryConnection()
    {
        using var slots = new ConnectionSlots(1);
        using var harness = new EngineHarness(EngineHarness.FastOptionsWith(slots));
        var bytes = EngineHarness.MakeBytes(400 * 1024, seed: 13);
        harness.Server.AddFile(
            "/quota.bin",
            bytes,
            new FileBehavior { ChunkSize = 16 * 1024, PerChunkDelay = TimeSpan.FromMilliseconds(3) });

        using var sampler = new SlotSampler(slots);
        var task = harness.CreateTask("/quota.bin", fileName: "quota.bin", maxSegments: 4);
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);
        var peak = await sampler.StopAsync();

        Assert.True(result.Success, result.Error.ToString());
        Assert.Equal(1, peak);
        Assert.True(sampler.SawWaiting);
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(result.FinalPath!)));
        Assert.Equal(0, slots.InFlight);
    }

    [Fact]
    public async Task SharedQuota_AppliesAcrossTasks()
    {
        // 两个任务共用一份配额：各自都想开 2 段，合起来也只有 1 条在途连接。
        using var slots = new ConnectionSlots(1);
        using var harness = new EngineHarness(EngineHarness.FastOptionsWith(slots));
        var slow = new FileBehavior { ChunkSize = 16 * 1024, PerChunkDelay = TimeSpan.FromMilliseconds(2) };
        harness.Server.AddFile("/one.bin", EngineHarness.MakeBytes(200 * 1024, seed: 19), slow);
        harness.Server.AddFile("/two.bin", EngineHarness.MakeBytes(200 * 1024, seed: 23), slow);

        using var sampler = new SlotSampler(slots);
        var taskA = harness.CreateTask("/one.bin", id: 1, fileName: "one.bin", maxSegments: 2);
        var taskB = harness.CreateTask("/two.bin", id: 2, fileName: "two.bin", maxSegments: 2);
        harness.Engine.Start(taskA);
        harness.Engine.Start(taskB);

        var resultA = harness.WaitForResult(taskA);
        var resultB = harness.WaitForResult(taskB);
        var peak = await sampler.StopAsync();

        Assert.True(resultA.Success);
        Assert.True(resultB.Success);
        Assert.Equal(1, peak);
        Assert.Equal(0, slots.InFlight);
    }

    [Fact]
    public void PausedTask_ReleasesItsSlot()
    {
        using var slots = new ConnectionSlots(1);
        using var harness = new EngineHarness(EngineHarness.FastOptionsWith(slots));
        var bytes = EngineHarness.MakeBytes(800 * 1024, seed: 29);
        harness.Server.AddFile(
            "/pausable.bin",
            bytes,
            new FileBehavior { ChunkSize = 8 * 1024, PerChunkDelay = TimeSpan.FromMilliseconds(20) });

        var task = harness.CreateTask("/pausable.bin", fileName: "pausable.bin", maxSegments: 2);
        harness.Engine.Start(task);

        // 跑到有进度再暂停：暂停必须立刻把名额让出来，否则排队的任务永远起不来。
        Assert.True(harness.WaitFor(() => task.SyncedTotal > 0));
        Assert.True(harness.Engine.Pause(task));

        Assert.True(
            harness.WaitFor(() => slots.InFlight == 0),
            $"暂停后仍占着 {slots.InFlight} 个连接名额");
        Assert.Equal(DownloadStatus.Paused, task.Status);
    }

    /// <summary>后台按 1ms 采样配额的在途/排队数（测试只关心"爬到上限、没有越界"）。</summary>
    private sealed class SlotSampler : IDisposable
    {
        private readonly ConnectionSlots _slots;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _peak;
        private int _sawWaiting;

        public SlotSampler(ConnectionSlots slots)
        {
            _slots = slots;
            _loop = Task.Run(
                async () =>
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        if (_slots.InFlight > Volatile.Read(ref _peak))
                        {
                            Volatile.Write(ref _peak, _slots.InFlight);
                        }

                        if (_slots.Waiting > 0)
                        {
                            Volatile.Write(ref _sawWaiting, 1);
                        }

                        await Task.Delay(1, TestContext.Current.CancellationToken);
                    }
                },
                TestContext.Current.CancellationToken);
        }

        public int Peak => Volatile.Read(ref _peak);

        public bool SawWaiting => Volatile.Read(ref _sawWaiting) == 1;

        public async Task<int> StopAsync()
        {
            await _stop.CancelAsync();
            await _loop;
            return Peak;
        }

        public void Dispose() => _stop.Dispose();
    }
}
