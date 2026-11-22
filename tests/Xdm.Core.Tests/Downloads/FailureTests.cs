using Xdm.Core.Downloads;
using Xdm.Core.Errors;
using Xdm.Core.Tests.Fakes;

namespace Xdm.Core.Tests.Downloads;

public class FailureTests
{
    [Fact]
    public void TruncatedResponse_RetriesFromWrittenBytes()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(300 * 1024, seed: 77);
        harness.Server.AddFile("/truncate.bin", bytes, new FileBehavior { DropAfterBytes = 100 * 1024 });

        var task = harness.CreateTask("/truncate.bin", fileName: "truncate.bin", maxSegments: 1);
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);

        Assert.True(result.Success, $"{result.Error}: {result.Detail}; requests={string.Join(" | ", harness.Server.Requests.Select(r => r.Range))}");
        Assert.Equal(bytes, File.ReadAllBytes(result.FinalPath!));
        var ranged = harness.Server.Requests.Where(static r => r.Range is not null).ToArray();
        Assert.Equal(3, ranged.Length); // 探测 + 首次(被截断) + 续传
        Assert.Contains(ranged, static r => r.Range!.StartsWith("bytes=102400-", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingFile_FailsWithRejected()
    {
        using var harness = new EngineHarness();
        var task = harness.CreateTask("/nope.bin", fileName: "nope.bin");

        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);

        Assert.False(result.Success);
        Assert.Equal(DownloadError.Rejected, result.Error);
        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.False(File.Exists(task.PartPath));
        Assert.Single(harness.Observer.Failed);
        Assert.Empty(harness.Observer.Completed);
    }

    [Fact]
    public void BogusRangeLength_FailsWithLengthMismatch_WithoutWriting()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(200 * 1024);
        harness.Server.AddFile("/liar.bin", bytes, new FileBehavior { ForcedRangeLength = 1000 });

        var task = harness.CreateTask("/liar.bin", fileName: "liar.bin");
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);

        Assert.False(result.Success);
        Assert.Equal(DownloadError.LengthMismatch, result.Error);
        Assert.False(File.Exists(task.PartPath));
    }

    [Fact]
    public void BusyServer_GivesUpAfterConfiguredWaits()
    {
        using var harness = new EngineHarness();
        harness.Server.AddFile("/busy.bin", EngineHarness.MakeBytes(1024), new FileBehavior
        {
            StatusCode = 503,
            RetryAfterSeconds = 0,
        });

        var task = harness.CreateTask("/busy.bin", fileName: "busy.bin");
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);

        Assert.False(result.Success);
        Assert.Equal(DownloadError.Busy, result.Error);
        Assert.Equal(3, harness.Server.Requests.Count); // 初始 + MaxBusyWaits(2)
    }

    [Fact]
    public void Engine_RejectsDuplicateStart_ForSameTaskId()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(1024 * 1024, seed: 4);
        harness.Server.AddFile("/dup.bin", bytes, new FileBehavior
        {
            PerChunkDelay = TimeSpan.FromMilliseconds(5),
            ChunkSize = 16 * 1024,
        });

        var first = harness.CreateTask("/dup.bin", fileName: "dup.bin");
        harness.Engine.Start(first);

        var second = new DownloadTask(first.Id, first.Url, "other.bin", harness.TargetFolder);
        Assert.Throws<InvalidOperationException>(() => harness.Engine.Start(second));

        harness.Engine.Pause(first);
    }

    [Fact]
    public void Pause_OnIdleTask_ReturnsFalse()
    {
        using var harness = new EngineHarness();
        var task = harness.CreateTask("/idle.bin", fileName: "idle.bin");

        Assert.False(harness.Engine.Pause(task));
        Assert.False(harness.Engine.IsRunning(task.Id));
    }
}
