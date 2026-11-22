using Xdm.Core.Downloads;
using Xdm.Core.Storage;
using Xdm.Core.Tests.Fakes;

namespace Xdm.Core.Tests.Downloads;

public sealed class NameConflictTests
{
    [Fact]
    public void Rename_KeepsExistingFileAndAddsNumberedCopy()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(120_000);
        harness.Server.AddFile("/a.bin", bytes);
        var existing = Path.Combine(harness.TargetFolder, "a.bin");
        File.WriteAllText(existing, "old-content");

        using var manager = CreateManager(harness);
        var id = manager.Enqueue(NewRequest(harness, "a.bin", NameConflictPolicy.Rename));
        Assert.True(WaitStatus(manager, id, DownloadStatus.Completed));

        Assert.Equal("old-content", File.ReadAllText(existing));
        var published = Path.Combine(harness.TargetFolder, "a (1).bin");
        Assert.True(File.Exists(published));
        Assert.Equal(bytes, File.ReadAllBytes(published));
    }

    [Fact]
    public void Overwrite_ReplacesExistingFileOnlyAfterFullDownload()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(200_000);
        harness.Server.AddFile(
            "/b.bin",
            bytes,
            new FileBehavior { ChunkSize = 16 * 1024, PerChunkDelay = TimeSpan.FromMilliseconds(5) });
        var existing = Path.Combine(harness.TargetFolder, "b.bin");
        File.WriteAllText(existing, "old-content");

        using var manager = CreateManager(harness);
        var id = manager.Enqueue(NewRequest(harness, "b.bin", NameConflictPolicy.Overwrite));
        Assert.True(WaitStatus(manager, id, DownloadStatus.Downloading));

        // 下载途中旧文件原样保留，发布时才原子替换。
        Assert.Equal("old-content", File.ReadAllText(existing));

        Assert.True(WaitStatus(manager, id, DownloadStatus.Completed));
        Assert.Equal(bytes, File.ReadAllBytes(existing));
        Assert.False(File.Exists(Path.Combine(harness.TargetFolder, "b (1).bin")));
    }

    [Fact]
    public void Fail_LeavesTargetUntouchedAndKeepsPartFile()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(80_000);
        harness.Server.AddFile("/c.bin", bytes);
        var existing = Path.Combine(harness.TargetFolder, "c.bin");
        File.WriteAllText(existing, "old-content");

        using var manager = CreateManager(harness);
        var id = manager.Enqueue(NewRequest(harness, "c.bin", NameConflictPolicy.Fail));
        Assert.True(WaitStatus(manager, id, DownloadStatus.Failed));

        var record = manager.Find(id)!;
        Assert.Contains("已存在", record.ErrorText);
        Assert.Equal("old-content", File.ReadAllText(existing));
        Assert.True(File.Exists(Path.Combine(harness.TargetFolder, $".{id}.xdm-part")));
    }

    private static DownloadManager CreateManager(EngineHarness harness)
        => new(new DownloadManagerOptions
        {
            StateDir = harness.StateDir,
            Client = harness.Client,
            EngineOptions = EngineHarness.FastOptions,
        });

    private static DownloadRequest NewRequest(
        EngineHarness harness,
        string fileName,
        NameConflictPolicy policy)
        => new()
        {
            Url = harness.Server.BaseUrl + "/" + fileName,
            TargetFolder = harness.TargetFolder,
            FileName = fileName,
            NameConflict = policy,
        };

    private static bool WaitStatus(
        DownloadManager manager,
        long id,
        DownloadStatus status,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            if (manager.Find(id)?.Status == status)
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return manager.Find(id)?.Status == status;
    }
}
