using Xdm.Core.Downloads;
using Xdm.Core.Storage;
using Xdm.Core.Tasks;
using Xdm.Core.Tests.Fakes;

namespace Xdm.Core.Tests.Downloads;

public sealed class DownloadPersistenceTests
{
    [Fact]
    public void CompletedDownload_IsPersistedAndRestored()
    {
        using var harness = new EngineHarness();
        using var store = new TaskStore(Path.Combine(harness.Root, "xdm.db"));
        var bytes = EngineHarness.MakeBytes(300_000);
        harness.Server.AddFile("/a.bin", bytes);

        long id;
        using (var manager = CreateManager(harness, store))
        {
            id = manager.Enqueue(NewRequest(harness, "a.bin"));
            Assert.True(WaitStatus(manager, id, DownloadStatus.Completed));
        }

        var stored = Assert.Single(store.LoadAll());
        Assert.Equal(DownloadStatus.Completed, stored.Status);
        Assert.Equal(bytes.Length, stored.Downloaded);
        Assert.Equal(bytes.Length, stored.TotalSize);
        Assert.Equal("a.bin", stored.FileName);
        Assert.Equal(FakeHttpServer.DefaultLastModified, stored.Validator);
        Assert.NotNull(stored.FinishedAt);

        using var restarted = CreateManager(harness, store);
        var record = Assert.Single(restarted.Snapshot());
        Assert.Equal(id, record.Id);
        Assert.Equal(DownloadStatus.Completed, record.Status);
        Assert.Equal(bytes.Length, record.SizeBytes);
        Assert.Equal(bytes.Length, record.SyncedBytes);
        Assert.True(File.Exists(Path.Combine(harness.TargetFolder, "a.bin")));
    }

    [Fact]
    public void PausedDownload_RestoresProgressAndResumesToCompletion()
    {
        using var harness = new EngineHarness();
        using var store = new TaskStore(Path.Combine(harness.Root, "xdm.db"));
        // 放慢到"暂停时必然没下完"：并行跑测试时 CPU 竞争会让 8ms/块的版本提前完成，
        // 触发 InRange(synced, 1, len-1) 失败。
        var bytes = EngineHarness.MakeBytes(600_000);
        harness.Server.AddFile(
            "/slow.bin",
            bytes,
            new FileBehavior { ChunkSize = 8 * 1024, PerChunkDelay = TimeSpan.FromMilliseconds(40) });

        long id;
        long syncedBeforeRestart;
        using (var manager = CreateManager(harness, store))
        {
            id = manager.Enqueue(NewRequest(harness, "slow.bin"));
            Assert.True(WaitStatus(manager, id, DownloadStatus.Downloading));
            Assert.True(harness.WaitFor(() => manager.Find(id)?.SyncedBytes > 0));

            Assert.True(manager.Pause(id));
            Assert.True(WaitStatus(manager, id, DownloadStatus.Paused));
            syncedBeforeRestart = manager.Find(id)!.SyncedBytes;
            Assert.InRange(syncedBeforeRestart, 1, bytes.Length - 1);
            Assert.True(File.Exists(PartPath(harness, id)));

            // 接了任务库就不写 state/*.json：段表只有库里一份，恢复也不靠文件。
            Assert.False(File.Exists(Path.Combine(harness.StateDir, $"{id}.json")));
        }

        Assert.Equal(DownloadStatus.Paused, Assert.Single(store.LoadAll()).Status);

        using (var restarted = CreateManager(harness, store))
        {
            var record = Assert.Single(restarted.Snapshot());
            Assert.Equal(DownloadStatus.Paused, record.Status);
            Assert.Equal(syncedBeforeRestart, record.SyncedBytes);
            Assert.True(record.SegmentCount > 0);

            Assert.True(restarted.Resume(id));
            Assert.True(WaitStatus(restarted, id, DownloadStatus.Completed));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(harness.TargetFolder, "slow.bin")));
        }

        Assert.Equal(DownloadStatus.Completed, Assert.Single(store.LoadAll()).Status);
    }

    [Fact]
    public void ActiveRow_OnRestart_BecomesPaused()
    {
        using var harness = new EngineHarness();
        using var store = new TaskStore(Path.Combine(harness.Root, "xdm.db"));
        var id = store.Insert(new StoredTask
        {
            Url = "https://example.com/a.bin",
            FileName = "a.bin",
            TargetFolder = harness.TargetFolder,
            TempFolder = harness.TargetFolder,
            Status = DownloadStatus.Downloading,
            MaxSegments = 4,
            Downloaded = 128,
            TotalSize = 1024,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        store.ReplaceSegments(
            id,
            [new SegmentState { Id = 0, Offset = 0, Length = 1024, Synced = 128 }],
            downloaded: 128,
            totalSize: 1024);

        using var manager = CreateManager(harness, store);
        var record = Assert.Single(manager.Snapshot());
        Assert.Equal(DownloadStatus.Paused, record.Status);
        Assert.Equal(DownloadStatus.Paused, Assert.Single(store.LoadAll()).Status);
    }

    [Fact]
    public void CompletedRow_WithMissingFile_BecomesFailed()
    {
        using var harness = new EngineHarness();
        using var store = new TaskStore(Path.Combine(harness.Root, "xdm.db"));
        var id = store.Insert(new StoredTask
        {
            Url = "https://example.com/gone.bin",
            FileName = "gone.bin",
            TargetFolder = harness.TargetFolder,
            TempFolder = harness.TargetFolder,
            Status = DownloadStatus.Completed,
            MaxSegments = 1,
            TotalSize = 10,
            Downloaded = 10,
            FinishedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        using var manager = CreateManager(harness, store);
        var record = Assert.Single(manager.Snapshot());
        Assert.Equal(id, record.Id);
        Assert.Equal(DownloadStatus.Failed, record.Status);
        Assert.Contains("不存在", record.ErrorText);
        Assert.Equal(DownloadStatus.Failed, Assert.Single(store.LoadAll()).Status);
    }

    [Fact]
    public void Delete_RemovesRowFromStore()
    {
        using var harness = new EngineHarness();
        using var store = new TaskStore(Path.Combine(harness.Root, "xdm.db"));

        using var manager = CreateManager(harness, store);
        var id = manager.Enqueue(NewRequest(harness, "a.bin", startImmediately: false));
        Assert.Equal(1, store.CountTasks());

        Assert.True(manager.Delete(id));
        Assert.Equal(0, store.CountTasks());
        Assert.Empty(manager.Snapshot());
    }

    [Fact]
    public void RestoredIds_AreNotReusedByNewTasks()
    {
        using var harness = new EngineHarness();
        using var store = new TaskStore(Path.Combine(harness.Root, "xdm.db"));
        for (var i = 0; i < 3; i++)
        {
            store.Insert(new StoredTask
            {
                Url = $"https://example.com/{i}.bin",
                FileName = $"{i}.bin",
                TargetFolder = harness.TargetFolder,
                TempFolder = harness.TargetFolder,
                Status = DownloadStatus.Paused,
                MaxSegments = 1,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        using var manager = CreateManager(harness, store);
        Assert.Equal(3, manager.Snapshot().Count);

        var id = manager.Enqueue(NewRequest(harness, "new.bin", startImmediately: false));
        Assert.Equal(4, id);
        Assert.Equal(4, store.MaxId());
    }

    [Fact]
    public void PeriodicSync_WritesProgressBeforeShutdown()
    {
        using var harness = new EngineHarness();
        using var store = new TaskStore(Path.Combine(harness.Root, "xdm.db"));
        var bytes = EngineHarness.MakeBytes(4_000_000);
        harness.Server.AddFile(
            "/big.bin",
            bytes,
            new FileBehavior { ChunkSize = 16 * 1024, PerChunkDelay = TimeSpan.FromMilliseconds(5) });

        long id;
        using (var manager = CreateManager(harness, store, persistInterval: TimeSpan.FromMilliseconds(80)))
        {
            id = manager.Enqueue(NewRequest(harness, "big.bin"));
            Assert.True(harness.WaitFor(() =>
            {
                var rows = store.LoadAll();
                return rows.Count == 1 && rows[0].Downloaded > 0;
            }));
        }

        var stored = Assert.Single(store.LoadAll());
        Assert.True(stored.Downloaded > 0, $"周期同步没有落库：{stored.Downloaded}");
        Assert.Equal(DownloadStatus.Downloading, stored.Status);

        // 模拟重启：库里还是"下载中"，恢复时必须降成暂停，且进度不丢。
        using var restarted = CreateManager(harness, store);
        var record = Assert.Single(restarted.Snapshot());
        Assert.Equal(id, record.Id);
        Assert.Equal(DownloadStatus.Paused, record.Status);
        Assert.True(record.SyncedBytes > 0);
        Assert.True(WaitStatus(restarted, id, DownloadStatus.Paused));
    }

    [Fact]
    public void Segments_ArePersistedAtSplit()
    {
        using var harness = new EngineHarness();
        using var store = new TaskStore(Path.Combine(harness.Root, "xdm.db"));
        var bytes = EngineHarness.MakeBytes(1_200_000);
        harness.Server.AddFile(
            "/multi.bin",
            bytes,
            new FileBehavior { ChunkSize = 8 * 1024, PerChunkDelay = TimeSpan.FromMilliseconds(8) });

        using var manager = CreateManager(harness, store);
        var id = manager.Enqueue(NewRequest(harness, "multi.bin"));
        Assert.True(harness.WaitFor(() =>
        {
            var rows = store.LoadAll();
            return rows.Count == 1 && rows[0].Segments.Count > 1;
        }));

        Assert.True(manager.Pause(id));
        Assert.True(WaitStatus(manager, id, DownloadStatus.Paused));

        var stored = Assert.Single(store.LoadAll());
        Assert.True(stored.Segments.Count > 1);
        Assert.All(stored.Segments, static s => Assert.True(s.Synced >= 0));
    }

    private static DownloadManager CreateManager(
        EngineHarness harness,
        TaskStore store,
        TimeSpan? persistInterval = null)
        => new(new DownloadManagerOptions
        {
            StateDir = harness.StateDir,
            Client = harness.Client,
            Store = store,
            EngineOptions = EngineHarness.FastOptions,
            PersistInterval = persistInterval ?? TimeSpan.FromSeconds(30),
        });

    private static DownloadRequest NewRequest(
        EngineHarness harness,
        string fileName,
        bool startImmediately = true)
        => new()
        {
            Url = harness.Server.BaseUrl + "/" + fileName,
            TargetFolder = harness.TargetFolder,
            FileName = fileName,
            StartImmediately = startImmediately,
            NameConflict = NameConflictPolicy.Rename,
        };

    private static string PartPath(EngineHarness harness, long id)
        => Path.Combine(harness.TargetFolder, $".{id}.xdm-part");

    private static bool WaitStatus(
        DownloadManager manager,
        long id,
        DownloadStatus status,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
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
