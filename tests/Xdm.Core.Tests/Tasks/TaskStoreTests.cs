using Microsoft.Data.Sqlite;
using Xdm.Core.Downloads;
using Xdm.Core.Errors;
using Xdm.Core.Storage;
using Xdm.Core.Tasks;

namespace Xdm.Core.Tests.Tasks;

public sealed class TaskStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "xdm-store-tests", Guid.NewGuid().ToString("N"));

    private readonly TaskStore _store;

    public TaskStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new TaskStore(Path.Combine(_root, "xdm.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不算测试失败。
        }
    }

    [Fact]
    public void Open_CreatesSchemaInWalMode()
    {
        using var connection = new SqliteConnection($"Data Source={_store.DbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)command.ExecuteScalar());

        command.CommandText = "PRAGMA user_version;";
        Assert.Equal((long)TaskStore.SchemaVersion, command.ExecuteScalar());

        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        var tables = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.Contains("tasks", tables);
        Assert.Contains("segments", tables);
    }

    [Fact]
    public void Open_CreatesMissingDirectory()
    {
        var nested = Path.Combine(_root, "a", "b", "xdm.db");
        using var store = new TaskStore(nested);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Insert_RoundTripsEveryColumn()
    {
        var created = new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
        var id = _store.Insert(new StoredTask
        {
            Url = "https://example.com/a.bin",
            FileName = "a.bin",
            TargetFolder = _root,
            TempFolder = _root,
            TotalSize = 4096,
            Status = DownloadStatus.Queued,
            MaxSegments = 6,
            Kind = TaskKind.HuggingFace,
            Headers = new Dictionary<string, string> { ["X-Test"] = "1" },
            Cookie = "k=v",
            NamePolicy = NameConflictPolicy.Overwrite,
            CreatedAt = created,
        });

        var row = Assert.Single(_store.LoadAll());
        Assert.Equal(id, row.Id);
        Assert.Equal(TaskKind.HuggingFace, row.Kind);
        Assert.Null(row.ParentId);
        Assert.Equal("https://example.com/a.bin", row.Url);
        Assert.Equal(4096, row.TotalSize);
        Assert.Equal(0, row.Downloaded);
        Assert.Equal(DownloadStatus.Queued, row.Status);
        Assert.Equal(6, row.MaxSegments);
        Assert.Equal("1", row.Headers!["X-Test"]);
        Assert.Equal("k=v", row.Cookie);
        Assert.Equal(NameConflictPolicy.Overwrite, row.NamePolicy);
        Assert.Equal(created, row.CreatedAt);
        Assert.Null(row.FinishedAt);
        Assert.Null(row.ContentSha256);
        Assert.Null(row.SourceMetaJson);
    }

    [Fact]
    public void InMemoryStore_KeepsSchemaAcrossConnections()
    {
        using var store = new TaskStore(":memory:");
        var id = store.Insert(NewTask());

        Assert.Equal(id, Assert.Single(store.LoadAll()).Id);
        Assert.Equal(":memory:", store.DbPath);
    }

    [Fact]
    public void Insert_AssignsIncreasingIds()
    {
        var first = _store.Insert(NewTask());
        var second = _store.Insert(NewTask());

        Assert.Equal(first + 1, second);
        Assert.Equal(second, _store.MaxId());
        Assert.Equal(2, _store.CountTasks());
    }

    [Fact]
    public void UpdateProbe_PersistsProbeResult()
    {
        var id = _store.Insert(NewTask());

        _store.UpdateProbe(id, "real.bin", 999, "Sun, 22 Nov 2026 08:00:00 GMT", noRangeSupport: true);

        var row = Assert.Single(_store.LoadAll());
        Assert.Equal("real.bin", row.FileName);
        Assert.Equal(999, row.TotalSize);
        Assert.Equal("Sun, 22 Nov 2026 08:00:00 GMT", row.Validator);
        Assert.True(row.NoRangeSupport);
    }

    [Fact]
    public void ReplaceSegments_RoundTripsAndUpdatesTotals()
    {
        var id = _store.Insert(NewTask());

        _store.ReplaceSegments(
            id,
            [
                new SegmentState { Id = 0, Offset = 0, Length = 128, Synced = 64, Status = SegmentStatus.Downloading },
                new SegmentState { Id = 1, Offset = 128, Length = 128, Synced = 128, Status = SegmentStatus.Finished },
            ],
            downloaded: 192,
            totalSize: 256);

        var row = Assert.Single(_store.LoadAll());
        Assert.Equal(192, row.Downloaded);
        Assert.Equal(256, row.TotalSize);
        Assert.Equal(2, row.Segments.Count);
        Assert.Equal(SegmentStatus.Downloading, row.Segments[0].Status);
        Assert.Equal(128, row.Segments[1].Offset);
        Assert.Equal(SegmentStatus.Finished, row.Segments[1].Status);

        // 再替换一次必须先清掉旧段，不能累加。
        _store.ReplaceSegments(id, [new SegmentState { Id = 0, Offset = 0, Length = 256, Synced = 256 }], 256, 256);
        Assert.Single(Assert.Single(_store.LoadAll()).Segments);
    }

    [Fact]
    public void UpdateStatus_TogglesFinishedAndError()
    {
        var id = _store.Insert(NewTask());
        var finished = DateTimeOffset.UtcNow;

        _store.UpdateStatus(id, DownloadStatus.Completed, null, finished);
        var completed = Assert.Single(_store.LoadAll());
        Assert.Equal(DownloadStatus.Completed, completed.Status);
        Assert.NotNull(completed.FinishedAt);

        _store.UpdateStatus(id, DownloadStatus.Failed, DownloadError.Network, null);
        var failed = Assert.Single(_store.LoadAll());
        Assert.Equal(DownloadStatus.Failed, failed.Status);
        Assert.Equal(DownloadError.Network, failed.Error);
        Assert.Null(failed.FinishedAt);
    }

    [Fact]
    public void DiskError_RoundTripsAsDiskError()
    {
        var id = _store.Insert(NewTask());
        _store.UpdateStatus(id, DownloadStatus.DiskError, DownloadError.Disk, null);

        var row = Assert.Single(_store.LoadAll());
        Assert.Equal(DownloadStatus.DiskError, row.Status);
        Assert.Equal(DownloadError.Disk, row.Error);
    }

    [Fact]
    public void ResetIncomplete_TurnsQueuedAndActiveIntoPaused()
    {
        var queued = _store.Insert(NewTask(DownloadStatus.Queued));
        var connecting = _store.Insert(NewTask(DownloadStatus.Connecting));
        var downloading = _store.Insert(NewTask(DownloadStatus.Downloading));
        var completed = _store.Insert(NewTask(DownloadStatus.Completed));

        Assert.Equal(3, _store.ResetIncomplete());

        var rows = _store.LoadAll().ToDictionary(static r => r.Id);
        Assert.Equal(DownloadStatus.Paused, rows[queued].Status);
        Assert.Equal(DownloadStatus.Paused, rows[connecting].Status);
        Assert.Equal(DownloadStatus.Paused, rows[downloading].Status);
        Assert.Equal(DownloadStatus.Completed, rows[completed].Status);
    }

    [Fact]
    public void Delete_RemovesTaskAndSegments()
    {
        var id = _store.Insert(NewTask());
        _store.ReplaceSegments(id, [new SegmentState { Id = 0, Offset = 0, Length = 10, Synced = 5 }], 5, 10);

        Assert.True(_store.Delete(id));
        Assert.Empty(_store.LoadAll());
        Assert.Equal(0, _store.CountTasks());
        Assert.False(_store.Delete(id));
    }

    [Fact]
    public void Rename_UpdatesPublishedFileName()
    {
        var id = _store.Insert(NewTask());
        _store.Rename(id, "a (1).bin");

        Assert.Equal("a (1).bin", Assert.Single(_store.LoadAll()).FileName);
    }

    [Fact]
    public void Checkpoint_KeepsDataReadable()
    {
        var id = _store.Insert(NewTask());
        _store.Checkpoint();

        Assert.Equal(id, Assert.Single(_store.LoadAll()).Id);
    }

    [Theory]
    [InlineData(DownloadStatus.Queued, 0)]
    [InlineData(DownloadStatus.Connecting, 1)]
    [InlineData(DownloadStatus.Downloading, 1)]
    [InlineData(DownloadStatus.Paused, 2)]
    [InlineData(DownloadStatus.Completed, 4)]
    [InlineData(DownloadStatus.Failed, 5)]
    [InlineData(DownloadStatus.DiskError, 5)]
    public void StatusMapping_MatchesArchitecture(DownloadStatus status, int expectedCode)
    {
        Assert.Equal(expectedCode, TaskStore.StatusCode(status));
    }

    [Fact]
    public void StatusFrom_CodeOneIsDownloading_AndDiskErrorNeedsErrorCode()
    {
        Assert.Equal(DownloadStatus.Downloading, TaskStore.StatusFrom(1, null));
        Assert.Equal(DownloadStatus.Failed, TaskStore.StatusFrom(5, DownloadError.Network));
        Assert.Equal(DownloadStatus.DiskError, TaskStore.StatusFrom(5, DownloadError.Disk));
        Assert.Equal(DownloadStatus.Completed, TaskStore.StatusFrom(4, null));
        Assert.Equal(DownloadStatus.Paused, TaskStore.StatusFrom(2, null));
        Assert.Equal(DownloadStatus.Queued, TaskStore.StatusFrom(0, null));
    }

    private StoredTask NewTask(DownloadStatus status = DownloadStatus.Queued) => new()
    {
        Url = "https://example.com/a.bin",
        FileName = "a.bin",
        TargetFolder = _root,
        TempFolder = _root,
        MaxSegments = 4,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
