using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xdm.Core.Downloads;
using Xdm.Core.Errors;
using Xdm.Core.Storage;

namespace Xdm.Core.Tasks;

/// <summary>
/// SQLite 任务库（<c>ARCHITECTURE.md</c> §7.1）：首次打开自动建表，WAL 模式，写库只在状态迁移 /
/// 切分 / 30s 同步 / 完成失败时发生，不在每秒进度里写。
/// </summary>
/// <remarks>
/// 每个方法开一条（池化）连接，调用方可在任意线程使用；SQLite 的 WAL + busy_timeout 负责并发写排队。
/// 表结构**一次建全**（<c>kind</c> / <c>parent_id</c> / <c>content_sha256</c> / <c>source_meta_json</c>
/// 以及重名策略），不做 ALTER 迁移。
/// </remarks>
public sealed class TaskStore : IDisposable
{
    /// <summary>当前 schema 版本（写入 <c>PRAGMA user_version</c>）。</summary>
    public const int SchemaVersion = 1;

    private const int StatusQueued = 0;
    private const int StatusActive = 1;
    private const int StatusPaused = 2;
    private const int StatusFinished = 4;
    private const int StatusFailed = 5;

    private const string TaskColumns = """
        id, kind, parent_id, url, file_name, target_folder, temp_folder, total_size, downloaded,
        status, max_segments, headers_json, cookie, validator, no_range_support, content_sha256,
        source_meta_json, error_code, name_policy, created_at, finished_at
        """;

    private const string CreateTasksSql = """
        CREATE TABLE IF NOT EXISTS tasks (
          id               INTEGER PRIMARY KEY,
          kind             INTEGER NOT NULL DEFAULT 0,
          parent_id        INTEGER REFERENCES tasks(id) ON DELETE CASCADE,
          url              TEXT    NOT NULL,
          file_name        TEXT    NOT NULL,
          target_folder    TEXT    NOT NULL,
          temp_folder      TEXT    NOT NULL,
          total_size       INTEGER,
          downloaded       INTEGER NOT NULL DEFAULT 0,
          status           INTEGER NOT NULL,
          max_segments     INTEGER NOT NULL,
          headers_json     TEXT,
          cookie           TEXT,
          validator        TEXT,
          no_range_support INTEGER NOT NULL DEFAULT 0,
          content_sha256   TEXT,
          source_meta_json TEXT,
          error_code       INTEGER,
          name_policy      INTEGER NOT NULL DEFAULT 0,
          created_at       INTEGER NOT NULL,
          finished_at      INTEGER
        );
        """;

    private const string CreateSegmentsSql = """
        CREATE TABLE IF NOT EXISTS segments (
          task_id INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
          seg_id  INTEGER NOT NULL,
          offset  INTEGER NOT NULL,
          length  INTEGER NOT NULL,
          synced  INTEGER NOT NULL,
          status  INTEGER NOT NULL,
          PRIMARY KEY (task_id, seg_id)
        );
        """;

    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAlive;
    private bool _disposed;

    /// <summary>打开（必要时创建）任务库。<paramref name="dbPath"/> 为 <c>:memory:</c> 时用进程内内存库。</summary>
    public TaskStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        var inMemory = string.Equals(dbPath, ":memory:", StringComparison.Ordinal);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = inMemory ? $"xdm_mem_{Guid.NewGuid():N}" : Path.GetFullPath(dbPath),
            Mode = inMemory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = inMemory ? SqliteCacheMode.Shared : SqliteCacheMode.Default,
            Pooling = true,
            DefaultTimeout = 10,
            ForeignKeys = true,
        };

        _connectionString = builder.ConnectionString;
        if (!inMemory)
        {
            var directory = Path.GetDirectoryName(builder.DataSource);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        else
        {
            // Mode=Memory 的库只在至少一条连接存活时存在；留一条空连接当锚。
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
        }

        DbPath = inMemory ? ":memory:" : builder.DataSource;
        Initialize();
    }

    /// <summary>库文件路径（内存库为 <c>:memory:</c>）。</summary>
    public string DbPath { get; }

    /// <summary>插入一行并返回 SQLite 分配的任务 id。</summary>
    public long Insert(StoredTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO tasks (
              kind, parent_id, url, file_name, target_folder, temp_folder, total_size, downloaded,
              status, max_segments, headers_json, cookie, validator, no_range_support, content_sha256,
              source_meta_json, error_code, name_policy, created_at, finished_at)
            VALUES (
              $kind, $parent, $url, $file, $target, $temp, $total, $downloaded,
              $status, $segments, $headers, $cookie, $validator, $noRange, $sha256,
              $meta, $error, $policy, $created, $finished);
            """;
        insert.Parameters.AddWithValue("$kind", (int)task.Kind);
        insert.Parameters.AddWithValue("$parent", (object?)task.ParentId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$url", task.Url);
        insert.Parameters.AddWithValue("$file", task.FileName);
        insert.Parameters.AddWithValue("$target", task.TargetFolder);
        insert.Parameters.AddWithValue("$temp", task.TempFolder);
        insert.Parameters.AddWithValue("$total", (object?)task.TotalSize ?? DBNull.Value);
        insert.Parameters.AddWithValue("$downloaded", task.Downloaded);
        insert.Parameters.AddWithValue("$status", StatusCode(task.Status));
        insert.Parameters.AddWithValue("$segments", task.MaxSegments);
        insert.Parameters.AddWithValue("$headers", SerializeHeaders(task.Headers));
        insert.Parameters.AddWithValue("$cookie", (object?)task.Cookie ?? DBNull.Value);
        insert.Parameters.AddWithValue("$validator", (object?)task.Validator ?? DBNull.Value);
        insert.Parameters.AddWithValue("$noRange", task.NoRangeSupport ? 1 : 0);
        insert.Parameters.AddWithValue("$sha256", (object?)task.ContentSha256 ?? DBNull.Value);
        insert.Parameters.AddWithValue("$meta", (object?)task.SourceMetaJson ?? DBNull.Value);
        insert.Parameters.AddWithValue("$error", task.Error is { } error ? (int)error : DBNull.Value);
        insert.Parameters.AddWithValue("$policy", (int)task.NamePolicy);
        insert.Parameters.AddWithValue("$created", task.CreatedAt.ToUnixTimeSeconds());
        insert.Parameters.AddWithValue(
            "$finished",
            task.FinishedAt is { } finished ? finished.ToUnixTimeSeconds() : DBNull.Value);
        insert.ExecuteNonQuery();

        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT last_insert_rowid();";
        var id = (long)(query.ExecuteScalar() ?? 0L);

        transaction.Commit();
        return id;
    }

    /// <summary>全表读取（id 升序），段表一并带上。</summary>
    public IReadOnlyList<StoredTask> LoadAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var connection = OpenConnection();

        var segments = new Dictionary<long, List<SegmentState>>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT task_id, seg_id, offset, length, synced, status FROM segments ORDER BY task_id, seg_id;";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var taskId = reader.GetInt64(0);
                if (!segments.TryGetValue(taskId, out var list))
                {
                    list = [];
                    segments[taskId] = list;
                }

                list.Add(new SegmentState
                {
                    Id = reader.GetInt64(1),
                    Offset = reader.GetInt64(2),
                    Length = reader.GetInt64(3),
                    Synced = reader.GetInt64(4),
                    Status = (SegmentStatus)reader.GetInt32(5),
                });
            }
        }

        var tasks = new List<StoredTask>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = $"SELECT {TaskColumns} FROM tasks ORDER BY id;";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var error = reader.IsDBNull(17) ? (DownloadError?)null : (DownloadError)reader.GetInt32(17);
                tasks.Add(new StoredTask
                {
                    Id = id,
                    Kind = (TaskKind)reader.GetInt32(1),
                    ParentId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    Url = reader.GetString(3),
                    FileName = reader.GetString(4),
                    TargetFolder = reader.GetString(5),
                    TempFolder = reader.GetString(6),
                    TotalSize = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    Downloaded = reader.GetInt64(8),
                    Status = StatusFrom(reader.GetInt32(9), error),
                    MaxSegments = reader.GetInt32(10),
                    Headers = DeserializeHeaders(reader.IsDBNull(11) ? null : reader.GetString(11)),
                    Cookie = reader.IsDBNull(12) ? null : reader.GetString(12),
                    Validator = reader.IsDBNull(13) ? null : reader.GetString(13),
                    NoRangeSupport = reader.GetInt32(14) != 0,
                    ContentSha256 = reader.IsDBNull(15) ? null : reader.GetString(15),
                    SourceMetaJson = reader.IsDBNull(16) ? null : reader.GetString(16),
                    Error = error,
                    NamePolicy = (NameConflictPolicy)reader.GetInt32(18),
                    CreatedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(19)),
                    FinishedAt = reader.IsDBNull(20) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(20)),
                    Segments = segments.TryGetValue(id, out var list) ? list : [],
                });
            }
        }

        return tasks;
    }

    /// <summary>迁移状态；<paramref name="finishedAt"/> 为空时清掉完成时间。</summary>
    public void UpdateStatus(long id, DownloadStatus status, DownloadError? error, DateTimeOffset? finishedAt)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks
               SET status = $status, error_code = $error, finished_at = $finished
             WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", StatusCode(status));
        command.Parameters.AddWithValue("$error", error is { } value ? (int)value : DBNull.Value);
        command.Parameters.AddWithValue(
            "$finished",
            finishedAt is { } time ? time.ToUnixTimeSeconds() : DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>探测结果回写（文件名 / 总大小 / <c>If-Range</c> 校验值 / Range 能力）。</summary>
    public void UpdateProbe(long id, string fileName, long? totalSize, string? validator, bool noRangeSupport)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tasks
               SET file_name = $file, total_size = $total, validator = $validator, no_range_support = $noRange
             WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$file", fileName);
        command.Parameters.AddWithValue("$total", (object?)totalSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$validator", (object?)validator ?? DBNull.Value);
        command.Parameters.AddWithValue("$noRange", noRangeSupport ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>刷进度（30s 同步用，不参与每秒刷新）。</summary>
    public void UpdateProgress(long id, long downloaded, long? totalSize)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tasks SET downloaded = $downloaded, total_size = $total WHERE id = $id;";
        command.Parameters.AddWithValue("$downloaded", downloaded);
        command.Parameters.AddWithValue("$total", (object?)totalSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>整表替换段快照，并顺带更新已落盘总数（同一事务）。</summary>
    public void ReplaceSegments(long id, IReadOnlyList<SegmentState> segments, long downloaded, long? totalSize)
    {
        ArgumentNullException.ThrowIfNull(segments);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM segments WHERE task_id = $id;";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO segments (task_id, seg_id, offset, length, synced, status)
                VALUES ($task, $seg, $offset, $length, $synced, $status);
                """;
            var taskParam = insert.Parameters.Add("$task", SqliteType.Integer);
            var segParam = insert.Parameters.Add("$seg", SqliteType.Integer);
            var offsetParam = insert.Parameters.Add("$offset", SqliteType.Integer);
            var lengthParam = insert.Parameters.Add("$length", SqliteType.Integer);
            var syncedParam = insert.Parameters.Add("$synced", SqliteType.Integer);
            var statusParam = insert.Parameters.Add("$status", SqliteType.Integer);

            foreach (var segment in segments)
            {
                taskParam.Value = id;
                segParam.Value = segment.Id;
                offsetParam.Value = segment.Offset;
                lengthParam.Value = segment.Length;
                syncedParam.Value = segment.Synced;
                statusParam.Value = (int)segment.Status;
                insert.ExecuteNonQuery();
            }
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE tasks SET downloaded = $downloaded, total_size = $total WHERE id = $id;";
            update.Parameters.AddWithValue("$downloaded", downloaded);
            update.Parameters.AddWithValue("$total", (object?)totalSize ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>发布完成后把 <c>file_name</c> 改成实际落盘的名字（重名时会被加序号）。</summary>
    public void Rename(long id, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tasks SET file_name = $file WHERE id = $id;";
        command.Parameters.AddWithValue("$file", fileName);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>删除任务及其段（显式删，不依赖外键开关）。</summary>
    public bool Delete(long id)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var deleteSegments = connection.CreateCommand())
        {
            deleteSegments.Transaction = transaction;
            deleteSegments.CommandText = "DELETE FROM segments WHERE task_id = $id;";
            deleteSegments.Parameters.AddWithValue("$id", id);
            deleteSegments.ExecuteNonQuery();
        }

        int affected;
        using (var deleteTask = connection.CreateCommand())
        {
            deleteTask.Transaction = transaction;
            deleteTask.CommandText = "DELETE FROM tasks WHERE id = $id;";
            deleteTask.Parameters.AddWithValue("$id", id);
            affected = deleteTask.ExecuteNonQuery();
        }

        transaction.Commit();
        return affected > 0;
    }

    /// <summary>启动恢复：未完成（排队 / 连接 / 下载中）一律转暂停，返回受影响行数。</summary>
    public int ResetIncomplete()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tasks SET status = $paused WHERE status IN ($queued, $active);";
        command.Parameters.AddWithValue("$paused", StatusPaused);
        command.Parameters.AddWithValue("$queued", StatusQueued);
        command.Parameters.AddWithValue("$active", StatusActive);
        return command.ExecuteNonQuery();
    }

    /// <summary>当前最大任务 id（新建任务时用来核对 id 不被复用）。</summary>
    public long MaxId()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(id), 0) FROM tasks;";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>任务数（测试与状态栏用）。</summary>
    public int CountTasks()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tasks;";
        return (int)(long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>强制把 WAL 合并回主库（备份/迁移前调用）。</summary>
    public void Checkpoint()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _keepAlive?.Dispose();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>状态枚举 → 库里的整数（0 Ready / 1 Downloading / 2 Paused / 4 Finished / 5 Failed）。</summary>
    public static int StatusCode(DownloadStatus status) => status switch
    {
        DownloadStatus.Queued => StatusQueued,
        DownloadStatus.Connecting or DownloadStatus.Downloading => StatusActive,
        DownloadStatus.Paused => StatusPaused,
        DownloadStatus.Completed => StatusFinished,
        _ => StatusFailed,
    };

    /// <summary>库里的整数 → 状态枚举；<c>5</c> 再按 <paramref name="error"/> 区分磁盘错误。</summary>
    public static DownloadStatus StatusFrom(int code, DownloadError? error) => code switch
    {
        StatusQueued => DownloadStatus.Queued,
        StatusActive => DownloadStatus.Downloading,
        StatusPaused => DownloadStatus.Paused,
        StatusFinished => DownloadStatus.Completed,
        _ => error == DownloadError.Disk ? DownloadStatus.DiskError : DownloadStatus.Failed,
    };

    private void Initialize()
    {
        using var connection = OpenConnection();
        using (var wal = connection.CreateCommand())
        {
            // 返回一行检查结果；内存库会得到 "memory"，不是错误。
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteScalar();
        }

        using (var sync = connection.CreateCommand())
        {
            sync.CommandText = "PRAGMA synchronous=NORMAL;";
            sync.ExecuteNonQuery();
        }

        using (var tasks = connection.CreateCommand())
        {
            tasks.CommandText = CreateTasksSql;
            tasks.ExecuteNonQuery();
        }

        using (var segments = connection.CreateCommand())
        {
            segments.CommandText = CreateSegmentsSql;
            segments.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.CommandText = $"PRAGMA user_version={SchemaVersion};";
            version.ExecuteNonQuery();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static object SerializeHeaders(IReadOnlyDictionary<string, string>? headers)
        => headers is { Count: > 0 }
            ? JsonSerializer.Serialize(headers)
            : DBNull.Value;

    private static IReadOnlyDictionary<string, string>? DeserializeHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
