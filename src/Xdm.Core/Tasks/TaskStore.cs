using System.Data;
using System.Data.Common;
using System.Text.Json;
using PureDb;
using PureDb.AdoNet;
using Xdm.Core.Downloads;
using Xdm.Core.Errors;
using Xdm.Core.Storage;

namespace Xdm.Core.Tasks;

/// <summary>
/// PureDb 任务库（<c>ARCHITECTURE.md</c> §7.1）：首次打开自动建表，写库只在状态迁移 /
/// 切分 / 30s 同步 / 完成失败时发生，不在每秒进度里写。
/// </summary>
/// <remarks>
/// <para>
/// **全局一条连接**：构造时打开一条 <see cref="PureDbConnection"/> 并一直持有，所有公开方法
/// 在 <c>_gate</c> 锁内串行使用它（PureDb 不做连接池，多连接各自缓存页，容易读到旧数据）。
/// 调用方可在任意线程使用，方法间互相排队，不会并发碰库。
/// </para>
/// <para>
/// PureDb 方言带来的差异：表必须有且只有一个 <c>INTEGER PRIMARY KEY</c>（段表因此多了自增
/// <c>id</c> 列，唯一性由索引保证）、没有 <c>PRAGMA</c>、没有 <c>last_insert_rowid()</c>
/// （插入时在事务内取 <c>MAX(id)+1</c>）、事务为乐观 Serializable，冲突抛
/// <see cref="DBConcurrencyException"/>，所以写路径仍保留 <see cref="Run{T}(Func{T})"/> 重试
/// （只用于另一个进程实例抢同一库的情况）。事务内的命令必须显式挂上 <see cref="DbCommand.Transaction"/>。
/// </para>
/// <para>
/// 表结构**一次建全**（<c>kind</c> / <c>parent_id</c> / <c>content_sha256</c> / <c>source_meta_json</c>
/// 以及重名策略），不做 ALTER 迁移。
/// </para>
/// </remarks>
public sealed class TaskStore : IDisposable
{
    /// <summary>当前 schema 版本（表结构一次建全，仅作语义标记）。</summary>
    public const int SchemaVersion = 1;

    /// <summary>乐观事务冲突的重试次数上限（仅跨进程争用同一库时会用到）。</summary>
    private const int MaxRetries = 6;

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
          parent_id        INTEGER,
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
          id      INTEGER PRIMARY KEY,
          task_id INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
          seg_id  INTEGER NOT NULL,
          offset  INTEGER NOT NULL,
          length  INTEGER NOT NULL,
          synced  INTEGER NOT NULL,
          status  INTEGER NOT NULL
        );
        """;

    private const string CreateSegmentsIndexSql =
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_segments_task_seg ON segments(task_id, seg_id);";

    /// <summary>配置表：整份 <c>AppSettings</c> 一行（<see cref="SettingsRowId"/>），与任务同库同备份。</summary>
    /// <remarks>PureDb 要求每表有且仅有一个 <c>INTEGER PRIMARY KEY</c>，所以用固定 id 而不是 text 主键。</remarks>
    private const string CreateSettingsSql = """
        CREATE TABLE IF NOT EXISTS settings (
          id    INTEGER PRIMARY KEY,
          value TEXT NOT NULL
        );
        """;

    private const string InsertSql = """
        INSERT INTO tasks (
          id, kind, parent_id, url, file_name, target_folder, temp_folder, total_size, downloaded,
          status, max_segments, headers_json, cookie, validator, no_range_support, content_sha256,
          source_meta_json, error_code, name_policy, created_at, finished_at)
        VALUES (
          $id, $kind, $parent, $url, $file, $target, $temp, $total, $downloaded,
          $status, $segments, $headers, $cookie, $validator, $noRange, $sha256,
          $meta, $error, $policy, $created, $finished);
        """;

    /// <summary>串行化同一实例上的所有库访问。</summary>
    private readonly object _gate = new();

    /// <summary>全局唯一连接，生命周期与 <see cref="TaskStore"/> 一致。</summary>
    private readonly PureDbConnection _connection;

    /// <summary><c>:memory:</c> 用临时文件模拟；释放时连同 <c>.lock</c> / <c>.pidx</c> 一起删掉。</summary>
    private readonly string? _tempPath;

    private bool _disposed;

    /// <summary>打开（必要时创建）任务库。<paramref name="dbPath"/> 为 <c>:memory:</c> 时用进程内临时库。</summary>
    public TaskStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        var inMemory = string.Equals(dbPath, ":memory:", StringComparison.Ordinal);
        var actualPath = inMemory
            ? Path.Combine(Path.GetTempPath(), "xdm-memory", $"xdm-{Guid.NewGuid():N}.db")
            : Path.GetFullPath(dbPath);

        var directory = Path.GetDirectoryName(actualPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        RejectLegacySqliteFile(inMemory ? null : actualPath);

        _tempPath = inMemory ? actualPath : null;
        DbPath = inMemory ? ":memory:" : actualPath;

        // Shared：允许多个进程实例打开同一库（各自持锁串行写，冲突由 Run 重试兜底）。
        _connection = new PureDbConnection($"Data Source={actualPath};Access=Shared");
        try
        {
            _connection.Open();
            Initialize();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    /// <summary>库文件路径（内存库为 <c>:memory:</c>）。</summary>
    public string DbPath { get; }

    /// <summary>插入一行并返回任务 id（事务内取 <c>MAX(id)+1</c>，不依赖自增回填）。</summary>
    public long Insert(StoredTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return Locked(connection =>
        {
            using var transaction = connection.BeginTransaction();

            long id;
            using (var query = Command(connection, transaction, "SELECT COALESCE(MAX(id), 0) FROM tasks;"))
            {
                id = (long)(query.ExecuteScalar() ?? 0L) + 1;
            }

            using (var insert = Command(connection, transaction, InsertSql))
            {
                AddParameter(insert, "$id", id);
                AddParameter(insert, "$kind", (int)task.Kind);
                AddParameter(insert, "$parent", task.ParentId);
                AddParameter(insert, "$url", task.Url);
                AddParameter(insert, "$file", task.FileName);
                AddParameter(insert, "$target", task.TargetFolder);
                AddParameter(insert, "$temp", task.TempFolder);
                AddParameter(insert, "$total", task.TotalSize);
                AddParameter(insert, "$downloaded", task.Downloaded);
                AddParameter(insert, "$status", StatusCode(task.Status));
                AddParameter(insert, "$segments", task.MaxSegments);
                AddParameter(insert, "$headers", SerializeHeaders(task.Headers));
                AddParameter(insert, "$cookie", task.Cookie);
                AddParameter(insert, "$validator", task.Validator);
                AddParameter(insert, "$noRange", task.NoRangeSupport ? 1 : 0);
                AddParameter(insert, "$sha256", task.ContentSha256);
                AddParameter(insert, "$meta", task.SourceMetaJson);
                AddParameter(insert, "$error", task.Error is { } error ? (int)error : null);
                AddParameter(insert, "$policy", (int)task.NamePolicy);
                AddParameter(insert, "$created", task.CreatedAt.ToUnixTimeSeconds());
                AddParameter(insert, "$finished", task.FinishedAt?.ToUnixTimeSeconds());
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return id;
        });
    }

    /// <summary>全表读取（id 升序），段表一并带上。</summary>
    public IReadOnlyList<StoredTask> LoadAll() => Locked(connection =>
    {
        var segments = new Dictionary<long, List<SegmentState>>();
        using (var query = Command(connection, null, "SELECT task_id, seg_id, offset, length, synced, status FROM segments ORDER BY task_id, seg_id;"))
        using (var reader = query.ExecuteReader())
        {
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
        using (var query = Command(connection, null, $"SELECT {TaskColumns} FROM tasks ORDER BY id;"))
        using (var reader = query.ExecuteReader())
        {
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
    });

    /// <summary>迁移状态；<paramref name="finishedAt"/> 为空时清掉完成时间。</summary>
    public void UpdateStatus(long id, DownloadStatus status, DownloadError? error, DateTimeOffset? finishedAt)
        => Locked(connection =>
        {
            using var command = Command(connection, null, """
                UPDATE tasks
                   SET status = $status, error_code = $error, finished_at = $finished
                 WHERE id = $id;
                """);
            AddParameter(command, "$status", StatusCode(status));
            AddParameter(command, "$error", error is { } value ? (int)value : null);
            AddParameter(command, "$finished", finishedAt?.ToUnixTimeSeconds());
            AddParameter(command, "$id", id);
            command.ExecuteNonQuery();
        });

    /// <summary>探测结果回写（文件名 / 总大小 / <c>If-Range</c> 校验值 / Range 能力）。</summary>
    public void UpdateProbe(long id, string fileName, long? totalSize, string? validator, bool noRangeSupport)
        => Locked(connection =>
        {
            using var command = Command(connection, null, """
                UPDATE tasks
                   SET file_name = $file, total_size = $total, validator = $validator, no_range_support = $noRange
                 WHERE id = $id;
                """);
            AddParameter(command, "$file", fileName);
            AddParameter(command, "$total", totalSize);
            AddParameter(command, "$validator", validator);
            AddParameter(command, "$noRange", noRangeSupport ? 1 : 0);
            AddParameter(command, "$id", id);
            command.ExecuteNonQuery();
        });

    /// <summary>刷进度（30s 同步用，不参与每秒刷新）。</summary>
    public void UpdateProgress(long id, long downloaded, long? totalSize)
        => Locked(connection =>
        {
            using var command = Command(connection, null, "UPDATE tasks SET downloaded = $downloaded, total_size = $total WHERE id = $id;");
            AddParameter(command, "$downloaded", downloaded);
            AddParameter(command, "$total", totalSize);
            AddParameter(command, "$id", id);
            command.ExecuteNonQuery();
        });

    /// <summary>整表替换段快照，并顺带更新已落盘总数（同一事务）。</summary>
    public void ReplaceSegments(long id, IReadOnlyList<SegmentState> segments, long downloaded, long? totalSize)
    {
        ArgumentNullException.ThrowIfNull(segments);

        Locked(connection =>
        {
            using var transaction = connection.BeginTransaction();

            using (var delete = Command(connection, transaction, "DELETE FROM segments WHERE task_id = $id;"))
            {
                AddParameter(delete, "$id", id);
                delete.ExecuteNonQuery();
            }

            using (var insert = Command(connection, transaction, """
                INSERT INTO segments (task_id, seg_id, offset, length, synced, status)
                VALUES ($task, $seg, $offset, $length, $synced, $status);
                """))
            {
                AddParameter(insert, "$task", id);
                var segParam = AddParameter(insert, "$seg", 0L);
                var offsetParam = AddParameter(insert, "$offset", 0L);
                var lengthParam = AddParameter(insert, "$length", 0L);
                var syncedParam = AddParameter(insert, "$synced", 0L);
                var statusParam = AddParameter(insert, "$status", 0);

                foreach (var segment in segments)
                {
                    segParam.Value = segment.Id;
                    offsetParam.Value = segment.Offset;
                    lengthParam.Value = segment.Length;
                    syncedParam.Value = segment.Synced;
                    statusParam.Value = (int)segment.Status;
                    insert.ExecuteNonQuery();
                }
            }

            using (var update = Command(connection, transaction, "UPDATE tasks SET downloaded = $downloaded, total_size = $total WHERE id = $id;"))
            {
                AddParameter(update, "$downloaded", downloaded);
                AddParameter(update, "$total", totalSize);
                AddParameter(update, "$id", id);
                update.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    /// <summary>发布完成后把 <c>file_name</c> 改成实际落盘的名字（重名时会被加序号）。</summary>
    public void Rename(long id, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        Locked(connection =>
        {
            using var command = Command(connection, null, "UPDATE tasks SET file_name = $file WHERE id = $id;");
            AddParameter(command, "$file", fileName);
            AddParameter(command, "$id", id);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>删除任务及其段（显式删，不依赖外键级联）。</summary>
    public bool Delete(long id) => Locked(connection =>
    {
        using var transaction = connection.BeginTransaction();

        using (var deleteSegments = Command(connection, transaction, "DELETE FROM segments WHERE task_id = $id;"))
        {
            AddParameter(deleteSegments, "$id", id);
            deleteSegments.ExecuteNonQuery();
        }

        int affected;
        using (var deleteTask = Command(connection, transaction, "DELETE FROM tasks WHERE id = $id;"))
        {
            AddParameter(deleteTask, "$id", id);
            affected = deleteTask.ExecuteNonQuery();
        }

        transaction.Commit();
        return affected > 0;
    });

    /// <summary>启动恢复：未完成（排队 / 连接 / 下载中）一律转暂停，返回受影响行数。</summary>
    public int ResetIncomplete() => Locked(connection =>
    {
        using var command = Command(connection, null, "UPDATE tasks SET status = $paused WHERE status IN ($queued, $active);");
        AddParameter(command, "$paused", StatusPaused);
        AddParameter(command, "$queued", StatusQueued);
        AddParameter(command, "$active", StatusActive);
        return command.ExecuteNonQuery();
    });

    /// <summary>当前最大任务 id（新建任务时用来核对 id 不被复用）。</summary>
    public long MaxId() => Locked(connection =>
    {
        using var command = Command(connection, null, "SELECT COALESCE(MAX(id), 0) FROM tasks;");
        return (long)(command.ExecuteScalar() ?? 0L);
    });

    /// <summary>任务数（测试与状态栏用）。</summary>
    public int CountTasks() => Locked(connection =>
    {
        using var command = Command(connection, null, "SELECT COUNT(*) FROM tasks;");
        return (int)(long)(command.ExecuteScalar() ?? 0L);
    });

    /// <summary>
    /// 兼容旧调用：PureDb 由存储引擎按 <c>CheckpointIntervalBytes</c> 自动做检查点，
    /// 没有可手动触发的公开入口，这里只做一次可读性自检。
    /// </summary>
    public void Checkpoint()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = CountTasks();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection.Dispose();

            if (_tempPath is not null)
            {
                // PureDb 会给库文件配 .lock / .pidx，临时库要把三件都清掉。
                TryDelete(_tempPath);
                TryDelete(_tempPath + ".lock");
                TryDelete(_tempPath + ".pidx");
            }
        }
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

    private void Initialize() => Locked(connection =>
    {
        foreach (var sql in new[] { CreateTasksSql, CreateSegmentsSql, CreateSegmentsIndexSql, CreateSettingsSql })
        {
            using var command = Command(connection, null, sql);
            command.ExecuteNonQuery();
        }
    });

    /// <summary>在全局锁内使用唯一连接；冲突重试只在另一个进程实例同时写同一库时会发生。</summary>
    private T Locked<T>(Func<PureDbConnection, T> body) => Run(() =>
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return body(_connection);
        }
    });

    private void Locked(Action<PureDbConnection> body) => Locked(connection =>
    {
        body(connection);
        return true;
    });

    /// <summary>建一条命令；事务内的命令必须显式挂上事务，否则 PureDb 直接拒绝执行。</summary>
    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        return command;
    }

    private static DbParameter AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }

    /// <summary>乐观 Serializable 事务在并发写同一行时会抛 <see cref="DBConcurrencyException"/>，退避后重试。</summary>
    private static T Run<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (attempt < MaxRetries && IsRetryable(ex))
            {
                Thread.Sleep(5 * attempt);
            }
        }
    }

    private static bool IsRetryable(Exception ex) =>
        ex is DBConcurrencyException
        || (ex is SqlException sql && sql.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 临时库清理失败不算错误。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>旧版本用 SQLite 写 <c>xdm.db</c>；PureDb 打不开它，提前给出可操作的报错而不是底层格式错误。</summary>
    private static void RejectLegacySqliteFile(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[16];
            if (stream.Read(header) < header.Length)
            {
                return;
            }

            if (header.SequenceEqual("SQLite format 3\0"u8))
            {
                throw new InvalidOperationException(
                    $"任务库 {path} 是旧版 SQLite 格式，PureDb 无法读取；请备份后删除或改名，应用会用新格式重建。");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>配置行的固定 id（整份 <c>AppSettings</c> 存在这一行里）。</summary>
    private const long SettingsRowId = 1;

    /// <summary>读配置 JSON；没写过时返回 <c>null</c>。</summary>
    public string? LoadSettingsJson() => Locked(connection =>
    {
        using var query = Command(connection, null, "SELECT value FROM settings WHERE id = $id;");
        AddParameter(query, "$id", SettingsRowId);
        return query.ExecuteScalar() as string;
    });

    /// <summary>整份覆盖写配置 JSON（先 UPDATE，缺行时 INSERT；同一事务，断电不会只写一半）。</summary>
    public void SaveSettingsJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        Locked(connection =>
        {
            using var transaction = connection.BeginTransaction();

            int affected;
            using (var update = Command(connection, transaction, "UPDATE settings SET value = $value WHERE id = $id;"))
            {
                AddParameter(update, "$value", json);
                AddParameter(update, "$id", SettingsRowId);
                affected = update.ExecuteNonQuery();
            }

            if (affected == 0)
            {
                using var insert = Command(connection, transaction, "INSERT INTO settings (id, value) VALUES ($id, $value);");
                AddParameter(insert, "$id", SettingsRowId);
                AddParameter(insert, "$value", json);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        });
    }

    private static object SerializeHeaders(IReadOnlyDictionary<string, string>? headers)
        => headers is { Count: > 0 }
            ? JsonSerializer.Serialize(
                new Dictionary<string, string>(headers),
                XdmJsonContext.Default.DictionaryStringString)
            : DBNull.Value;

    private static IReadOnlyDictionary<string, string>? DeserializeHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, XdmJsonContext.Default.DictionaryStringString);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
