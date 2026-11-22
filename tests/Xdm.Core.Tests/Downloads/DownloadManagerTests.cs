using System.Collections.Concurrent;
using Xdm.Core.Downloads;
using Xdm.Core.Tests.Fakes;

namespace Xdm.Core.Tests.Downloads;

public class DownloadManagerTests
{
    private static DownloadManager CreateManager(EngineHarness harness, int maxConcurrent = 1)
        => new(new DownloadManagerOptions
        {
            MaxConcurrent = maxConcurrent,
            StateDir = harness.StateDir,
            Client = harness.Client,
            EngineOptions = EngineHarness.FastOptions,
        });

    private static DownloadRequest Request(EngineHarness harness, string path, string? fileName, bool start = true)
        => new()
        {
            Url = harness.Server.BaseUrl + path,
            TargetFolder = harness.TargetFolder,
            FileName = fileName,
            MaxSegments = 4,
            StartImmediately = start,
        };

    private static FileBehavior Slow => new()
    {
        PerChunkDelay = TimeSpan.FromMilliseconds(6),
        ChunkSize = 16 * 1024,
    };

    [Fact]
    public void Enqueue_DownloadsAndPublishesRecord()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);
        var bytes = EngineHarness.MakeBytes(80 * 1024, seed: 2);
        harness.Server.AddFile("/one.bin", bytes);

        var added = new ConcurrentQueue<DownloadRecord>();
        manager.TaskAdded += added.Enqueue;

        var id = manager.Enqueue(Request(harness, "/one.bin", "one.bin"));

        Assert.True(harness.WaitFor(() => manager.Find(id)?.Status == DownloadStatus.Completed), "任务没完成");
        var record = manager.Find(id)!;
        Assert.Equal("one.bin", record.FileName);
        Assert.Equal(bytes.Length, record.SizeBytes);
        Assert.Equal(bytes.Length, record.SyncedBytes);
        Assert.NotNull(record.CompletedAt);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(harness.TargetFolder, "one.bin")));
        Assert.Single(added);
        Assert.Equal(DownloadStatus.Queued, added.First().Status);
    }

    [Fact]
    public void FileName_IsDerivedFromUrl_WhenNotProvided()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);
        harness.Server.AddFile("/dir/derived-name.bin", EngineHarness.MakeBytes(4096));

        var id = manager.Enqueue(Request(harness, "/dir/derived-name.bin", fileName: null));

        Assert.True(harness.WaitFor(() => manager.Find(id)?.Status == DownloadStatus.Completed));
        Assert.Equal("derived-name.bin", manager.Find(id)!.FileName);
    }

    [Fact]
    public void SecondTask_WaitsForSlot_AndRunsAfterFirst()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness, maxConcurrent: 1);
        var bytes = EngineHarness.MakeBytes(256 * 1024, seed: 3);
        harness.Server.AddFile("/first.bin", bytes, Slow);
        harness.Server.AddFile("/second.bin", bytes, Slow);

        var first = manager.Enqueue(Request(harness, "/first.bin", "first.bin"));
        var second = manager.Enqueue(Request(harness, "/second.bin", "second.bin"));

        Assert.True(harness.WaitFor(() => manager.Find(first)!.Status == DownloadStatus.Downloading));
        Assert.Equal(DownloadStatus.Queued, manager.Find(second)!.Status);

        Assert.True(harness.WaitFor(() => manager.Find(first)!.Status == DownloadStatus.Completed));
        Assert.True(harness.WaitFor(() => manager.Find(second)!.Status == DownloadStatus.Completed));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(harness.TargetFolder, "second.bin")));
    }

    [Fact]
    public void PausedQueuedTask_IsNotStarted_UntilResumed()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness, maxConcurrent: 1);
        var bytes = EngineHarness.MakeBytes(256 * 1024, seed: 4);
        harness.Server.AddFile("/a.bin", bytes, Slow);
        harness.Server.AddFile("/b.bin", bytes, Slow);

        var first = manager.Enqueue(Request(harness, "/a.bin", "a.bin"));
        var second = manager.Enqueue(Request(harness, "/b.bin", "b.bin"));
        Assert.True(harness.WaitFor(() => manager.Find(first)!.Status == DownloadStatus.Downloading));

        Assert.True(manager.Pause(second));
        Assert.Equal(DownloadStatus.Paused, manager.Find(second)!.Status);

        harness.Server.ResetRequests();
        Assert.True(harness.WaitFor(() => manager.Find(first)!.Status == DownloadStatus.Completed));
        Thread.Sleep(150);
        Assert.Equal(DownloadStatus.Paused, manager.Find(second)!.Status);
        Assert.DoesNotContain(harness.Server.Requests, static r => r.Path == "/b.bin");

        Assert.True(manager.Resume(second));
        Assert.True(harness.WaitFor(() => manager.Find(second)!.Status == DownloadStatus.Completed));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(harness.TargetFolder, "b.bin")));
    }

    [Fact]
    public void PauseRunningTask_KeepsPartFile_AndResumeFinishes()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);
        var bytes = EngineHarness.MakeBytes(1024 * 1024, seed: 5);
        harness.Server.AddFile("/slow.bin", bytes, Slow);

        var id = manager.Enqueue(Request(harness, "/slow.bin", "slow.bin"));
        Assert.True(harness.WaitFor(() => manager.Find(id)?.SyncedBytes >= 128 * 1024), "没跑起来");

        Assert.True(manager.Pause(id));
        var paused = manager.Find(id)!;
        Assert.Equal(DownloadStatus.Paused, paused.Status);
        Assert.Equal(0, paused.SpeedBps);
        Assert.True(File.Exists(Path.Combine(harness.TargetFolder, ".1.xdm-part")));

        Assert.True(manager.Resume(id));
        Assert.True(harness.WaitFor(() => manager.Find(id)?.Status == DownloadStatus.Completed));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(harness.TargetFolder, "slow.bin")));
    }

    [Fact]
    public void Delete_RemovesEntryAndPartFile()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);
        harness.Server.AddFile("/gone.bin", EngineHarness.MakeBytes(1024 * 1024), Slow);

        var removed = new ConcurrentQueue<long>();
        manager.TaskRemoved += removed.Enqueue;

        var id = manager.Enqueue(Request(harness, "/gone.bin", "gone.bin"));
        Assert.True(harness.WaitFor(() => manager.Find(id)?.SyncedBytes > 0));

        Assert.True(manager.Delete(id));
        Assert.Null(manager.Find(id));
        Assert.False(File.Exists(Path.Combine(harness.TargetFolder, ".1.xdm-part")));
        Assert.Equal([id], removed);
        Assert.Equal(0, manager.ActiveCount);
    }

    [Fact]
    public void Delete_CompletedTask_WithDeleteFile_RemovesPublishedFile()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);
        harness.Server.AddFile("/keep.bin", EngineHarness.MakeBytes(64 * 1024));

        var id = manager.Enqueue(Request(harness, "/keep.bin", "keep.bin"));
        Assert.True(harness.WaitFor(() => manager.Find(id)?.Status == DownloadStatus.Completed));
        var published = Path.Combine(harness.TargetFolder, "keep.bin");
        Assert.True(File.Exists(published));

        Assert.True(manager.Delete(id, deleteFile: true));

        Assert.False(File.Exists(published));
        Assert.Null(manager.Find(id));
    }

    [Fact]
    public void Delete_CompletedTask_WithoutDeleteFile_KeepsPublishedFile()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);
        harness.Server.AddFile("/stay.bin", EngineHarness.MakeBytes(64 * 1024));

        var id = manager.Enqueue(Request(harness, "/stay.bin", "stay.bin"));
        Assert.True(harness.WaitFor(() => manager.Find(id)?.Status == DownloadStatus.Completed));

        Assert.True(manager.Delete(id));

        Assert.True(File.Exists(Path.Combine(harness.TargetFolder, "stay.bin")));
        Assert.Null(manager.Find(id));
    }

    [Fact]
    public void FailedTask_FreesSlotForNextTask()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness, maxConcurrent: 1);
        harness.Server.AddFile("/ok.bin", EngineHarness.MakeBytes(64 * 1024));

        var bad = manager.Enqueue(Request(harness, "/missing.bin", "missing.bin"));
        var good = manager.Enqueue(Request(harness, "/ok.bin", "ok.bin"));

        Assert.True(harness.WaitFor(() => manager.Find(bad)!.Status == DownloadStatus.Failed));
        var failed = manager.Find(bad)!;
        Assert.False(string.IsNullOrWhiteSpace(failed.ErrorText));

        Assert.True(harness.WaitFor(() => manager.Find(good)?.Status == DownloadStatus.Completed));
    }

    [Fact]
    public void StartImmediatelyFalse_LeavesTaskPausedWithoutRequests()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);
        harness.Server.AddFile("/later.bin", EngineHarness.MakeBytes(64 * 1024));

        var id = manager.Enqueue(Request(harness, "/later.bin", "later.bin", start: false));

        Assert.Equal(DownloadStatus.Paused, manager.Find(id)!.Status);
        Thread.Sleep(100);
        Assert.Empty(harness.Server.Requests);
        Assert.Equal(0, manager.ActiveCount);

        Assert.True(manager.Resume(id));
        Assert.True(harness.WaitFor(() => manager.Find(id)?.Status == DownloadStatus.Completed));
    }

    [Fact]
    public void ProgressEvents_ReportSpeedWhileDownloading()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);

        // 必须跑够两个以上的进度周期，否则第一帧（速度为 0）之后就直接完成，没有 SpeedBps>0 的样本。
        harness.Server.AddFile(
            "/progress.bin",
            EngineHarness.MakeBytes(2 * 1024 * 1024, seed: 6),
            new FileBehavior { ChunkSize = 16 * 1024, PerChunkDelay = TimeSpan.FromMilliseconds(20) });

        var updates = new ConcurrentQueue<DownloadRecord>();
        manager.TaskUpdated += updates.Enqueue;

        var id = manager.Enqueue(Request(harness, "/progress.bin", "progress.bin"));
        Assert.True(harness.WaitFor(() => manager.Find(id)?.Status == DownloadStatus.Completed));

        var samples = updates.ToArray();
        Assert.Contains(samples, static r => r.Status == DownloadStatus.Downloading);
        Assert.Contains(samples, static r => r.SpeedBps > 0);
        Assert.Contains(samples, static r => r.Status == DownloadStatus.Completed);
        Assert.Equal(0, manager.Find(id)!.SpeedBps);
    }

    [Fact]
    public void Enqueue_RejectsNonHttpUrl()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);

        var error = Assert.Throws<ArgumentException>(() => manager.Enqueue(new DownloadRequest
        {
            Url = "ftp://example.com/a.bin",
            TargetFolder = harness.TargetFolder,
        }));

        Assert.Contains("http", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PauseWhileConnecting_StopsTaskWithoutFailing()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness);
        var bytes = EngineHarness.MakeBytes(128 * 1024, seed: 31);
        harness.Server.AddFile("/hang.bin", bytes, new FileBehavior
        {
            ResponseDelay = TimeSpan.FromMilliseconds(600),
        });

        var id = manager.Enqueue(Request(harness, "/hang.bin", "hang.bin"));
        Assert.True(harness.WaitFor(() => manager.Find(id)?.Status == DownloadStatus.Connecting), "没卡在连接阶段");

        Assert.True(manager.Pause(id));
        Assert.Equal(DownloadStatus.Paused, manager.Find(id)!.Status);

        // 跨过服务器延迟：探测线程此时应已因取消退出，不能把状态改回 Failed。
        Thread.Sleep(900);
        Assert.Equal(DownloadStatus.Paused, manager.Find(id)!.Status);
        Assert.Equal(0, manager.ActiveCount);

        Assert.True(manager.Resume(id));
        Assert.True(harness.WaitFor(() => manager.Find(id)?.Status == DownloadStatus.Completed));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(harness.TargetFolder, "hang.bin")));
    }

    [Fact]
    public void PauseAllAndResumeAll_ToggleEveryTask()
    {
        using var harness = new EngineHarness();
        using var manager = CreateManager(harness, maxConcurrent: 1);
        harness.Server.AddFile("/p1.bin", EngineHarness.MakeBytes(2 * 1024 * 1024, seed: 21), Slow);
        harness.Server.AddFile("/p2.bin", EngineHarness.MakeBytes(2 * 1024 * 1024, seed: 22), Slow);

        var one = manager.Enqueue(Request(harness, "/p1.bin", "p1.bin"));
        var two = manager.Enqueue(Request(harness, "/p2.bin", "p2.bin"));
        Assert.True(harness.WaitFor(() => manager.Find(one)!.Status == DownloadStatus.Downloading));

        manager.PauseAll();
        Assert.True(harness.WaitFor(() => manager.Snapshot().All(static r => r.Status == DownloadStatus.Paused)));

        manager.ResumeAll();
        Assert.True(harness.WaitFor(() => manager.Snapshot().All(static r => r.Status == DownloadStatus.Completed)));
    }
}
