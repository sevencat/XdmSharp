using Xdm.Core.Downloads;
using Xdm.Core.Tests.Fakes;

namespace Xdm.Core.Tests.Downloads;

public class ResumeTests
{
    private const int FileSize = 2 * 1024 * 1024;

    private static FileBehavior Slow => new()
    {
        PerChunkDelay = TimeSpan.FromMilliseconds(8),
        ChunkSize = 16 * 1024,
    };

    [Fact]
    public void PauseThenResume_ContinuesFromSyncedBytes()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(FileSize, seed: 13);
        harness.Server.AddFile("/slow.bin", bytes, Slow);

        var task = harness.CreateTask("/slow.bin", fileName: "slow.bin", maxSegments: 4);
        harness.Engine.Start(task);

        Assert.True(harness.WaitFor(() => task.SyncedTotal >= 128 * 1024), "下载没有推进，无法测续传");
        Assert.True(harness.Engine.Pause(task));
        Assert.Equal(DownloadStatus.Paused, task.Status);
        Assert.True(task.SyncedTotal > 0);
        Assert.True(File.Exists(task.PartPath));

        var statePath = Path.Combine(harness.StateDir, "1.json");
        Assert.True(File.Exists(statePath));

        harness.Server.ResetRequests();
        harness.Engine.Resume(task);
        var result = harness.WaitForResult(task, TimeSpan.FromSeconds(90));

        Assert.True(result.Success);
        Assert.Equal(bytes, File.ReadAllBytes(result.FinalPath!));
        Assert.False(File.Exists(statePath));
        Assert.Contains(harness.Server.Requests, r =>
            r.Range is not null && StartOf(r.Range) > 0);
    }

    [Fact]
    public void Resume_AfterServerFileChanged_RedownloadsWholeFile()
    {
        using var harness = new EngineHarness();
        var oldBytes = EngineHarness.MakeBytes(FileSize, seed: 1);
        harness.Server.AddFile("/changed.bin", oldBytes, Slow);

        var task = harness.CreateTask("/changed.bin", fileName: "changed.bin", maxSegments: 4);
        harness.Engine.Start(task);
        Assert.True(harness.WaitFor(() => task.SyncedTotal >= 128 * 1024));
        Assert.True(harness.Engine.Pause(task));

        var newBytes = EngineHarness.MakeBytes(FileSize + 4096, seed: 200);
        harness.Server.UpdateFile("/changed.bin", newBytes, new FileBehavior
        {
            LastModified = "Mon, 23 Nov 2026 09:00:00 GMT",
            PerChunkDelay = TimeSpan.FromMilliseconds(1),
        });

        harness.Engine.Resume(task);
        var result = harness.WaitForResult(task, TimeSpan.FromSeconds(90));

        Assert.True(result.Success);
        Assert.Equal(newBytes, File.ReadAllBytes(result.FinalPath!));
    }

    [Fact]
    public void ResumedTask_StartsAtZero_WhenServerIgnoresRange()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(400 * 1024, seed: 21);
        harness.Server.AddFile("/norange.bin", bytes, new FileBehavior { SupportsRange = false });

        var task = harness.CreateTask("/norange.bin", fileName: "norange.bin");
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);

        Assert.True(result.Success);
        Assert.Equal(bytes, File.ReadAllBytes(result.FinalPath!));
    }

    [Fact]
    public void Resume_WithoutStateFile_FallsBackToFreshStart()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(80 * 1024, seed: 33);
        harness.Server.AddFile("/fresh.bin", bytes);

        var task = harness.CreateTask("/fresh.bin", fileName: "fresh.bin");
        harness.Engine.Resume(task);
        var result = harness.WaitForResult(task);

        Assert.True(result.Success);
        Assert.Equal(bytes, File.ReadAllBytes(result.FinalPath!));
    }

    [Fact]
    public void Delete_RemovesPartAndState()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(FileSize, seed: 44);
        harness.Server.AddFile("/delete.bin", bytes, Slow);

        var task = harness.CreateTask("/delete.bin", fileName: "delete.bin");
        harness.Engine.Start(task);
        Assert.True(harness.WaitFor(() => task.SyncedTotal > 0));
        Assert.True(harness.Engine.Pause(task));

        var statePath = Path.Combine(harness.StateDir, "1.json");
        Assert.True(File.Exists(task.PartPath));

        harness.Engine.Delete(task);

        Assert.False(File.Exists(task.PartPath));
        Assert.False(File.Exists(statePath));
        Assert.False(harness.Engine.IsRunning(task.Id));
    }

    private static long StartOf(string range)
    {
        var spec = range["bytes=".Length..];
        var dash = spec.IndexOf('-');
        return long.Parse(spec[..dash]);
    }
}
