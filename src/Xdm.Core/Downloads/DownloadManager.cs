using System.Diagnostics;
using PureDb;
using Xdm.Core.Errors;
using Xdm.Core.Http;
using Xdm.Core.Scheduling;
using Xdm.Core.Storage;
using Xdm.Core.Tasks;

namespace Xdm.Core.Downloads;

/// <summary>管理器调参。</summary>
public sealed class DownloadManagerOptions
{
    /// <summary>同时跑几个任务（每任务内部还有自己的段并发）。默认 3，与 UI 设计稿一致。</summary>
    public int MaxConcurrent { get; init; } = 3;

    /// <summary>快照目录；空则用引擎默认（<c>AppContext.BaseDirectory/state</c>）。</summary>
    public string? StateDir { get; init; }

    /// <summary>HTTP 客户端注入（测试用）；空则引擎自建。</summary>
    public IRangeHttpClient? Client { get; init; }

    /// <summary>引擎调参；空则默认。</summary>
    public DownloadEngineOptions? EngineOptions { get; init; }

    /// <summary>任务库；空则纯内存（测试与演示）。库的生命周期由调用方管理。</summary>
    public TaskStore? Store { get; init; }

    /// <summary>进度落库间隔（默认 30s，<c>ARCHITECTURE.md</c> §7.1）。</summary>
    public TimeSpan PersistInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// 任务队列 + 全局并发闸门 + 视图快照。
/// </summary>
/// <remarks>
/// 线程模型（<c>ARCHITECTURE.md</c> §6）：所有公开方法可在任意线程调用；引擎回调也在这里落地。
/// 「启动」必须离开调用方线程（探测是阻塞 IO），所以每个任务交给一个短命线程跑
/// <see cref="DownloadEngine.Resume"/>，跑完就退出，长驻的是段线程。
/// 事件在调用线程上同步触发，**实现方负责切线程**（`Xdm.App` 的 <c>DownloadUiBridge</c> 做 4Hz 合并）。
/// </remarks>
public sealed class DownloadManager : IDownloadObserver, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly DownloadQueue _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _scheduler;
    private readonly DownloadEngine _engine;
    private readonly TaskStore? _store;
    private readonly string _stateDir;
    private readonly Timer? _persistTimer;
    private int _maxConcurrent;
    private IReadOnlyDictionary<string, string>? _defaultHeaders;

    private long _nextId;
    private int _active;
    private bool _disposed;

    public DownloadManager()
        : this(null)
    {
    }

    public DownloadManager(DownloadManagerOptions? options)
    {
        options ??= new DownloadManagerOptions();
        _maxConcurrent = Math.Max(1, options.MaxConcurrent);
        _store = options.Store;
        _engine = new DownloadEngine(options.Client, options.StateDir, this, options.EngineOptions);
        _stateDir = _engine.StateDir;

        if (_store is not null)
        {
            RestoreFromStore();
            if (options.PersistInterval > TimeSpan.Zero)
            {
                _persistTimer = new Timer(
                    static state => ((DownloadManager)state!).PersistTick(),
                    this,
                    options.PersistInterval,
                    options.PersistInterval);
            }
        }

        _scheduler = new Thread(SchedulerLoop)
        {
            IsBackground = true,
            Name = "xdm-scheduler",
        };
        _scheduler.Start();
    }

    /// <summary>最近一次落库失败的说明（<see langword="null"/> 表示一直成功）；下载本身不受影响。</summary>
    public string? PersistenceError { get; private set; }

    /// <summary>同时跑几个任务（1–32）；调大后立即唤醒调度线程放行排队任务。</summary>
    public int MaxConcurrent
    {
        get
        {
            lock (_gate)
            {
                return _maxConcurrent;
            }
        }

        set
        {
            lock (_gate)
            {
                _maxConcurrent = Math.Clamp(value, 1, 32);
            }

            _wake.Set();
        }
    }

    /// <summary>
    /// 全局默认请求头（如 <c>User-Agent</c>/<c>Referer</c>）。新建任务时与任务级头合并，
    /// 任务级同名头优先。设置变更只影响之后新建的任务。
    /// </summary>
    public IReadOnlyDictionary<string, string>? DefaultHeaders
    {
        get => Volatile.Read(ref _defaultHeaders);
        set => Volatile.Write(ref _defaultHeaders, value);
    }

    /// <summary>任务新增（结构变化，直接刷行）。</summary>
    public event Action<DownloadRecord>? TaskAdded;

    /// <summary>进度/状态变化（高频，接收方自己合并）。</summary>
    public event Action<DownloadRecord>? TaskUpdated;

    /// <summary>任务被删除（结构变化）。</summary>
    public event Action<long>? TaskRemoved;

    /// <summary>当前排队数（测试与状态栏用）。</summary>
    public int QueuedCount => _queue.Count;

    /// <summary>正在跑的任务数。</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>新建任务并排队；返回任务 id。</summary>
    public long Enqueue(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"只支持 http/https 链接：{request.Url}", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetFolder);

        var maxSegments = Math.Clamp(request.MaxSegments, 1, 256);
        var initialStatus = request.StartImmediately ? DownloadStatus.Queued : DownloadStatus.Paused;
        var headers = MergeHeaders(DefaultHeaders, request.Headers);

        // 有任务库时 id 由任务库分配（重启后不会复用旧 id）；否则进程内自增。
        long id;
        if (_store is { } store)
        {
            id = store.Insert(new StoredTask
            {
                Url = uri.AbsoluteUri,
                FileName = request.FileName ?? string.Empty,
                TargetFolder = request.TargetFolder,
                TempFolder = request.TargetFolder,
                MaxSegments = maxSegments,
                Status = initialStatus,
                Kind = request.Kind,
                NamePolicy = request.NameConflict,
                Headers = headers,
                Cookie = request.Cookie,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            id = Interlocked.Increment(ref _nextId);
        }

        var task = new DownloadTask(
            id,
            uri.AbsoluteUri,
            request.FileName ?? string.Empty,
            request.TargetFolder,
            maxSegments)
        {
            Kind = request.Kind,
            NamePolicy = request.NameConflict,
            Headers = headers,
            Cookie = request.Cookie,
        };

        var entry = new Entry(task);
        lock (_gate)
        {
            _entries[id] = entry;
        }

        if (!request.StartImmediately)
        {
            task.SetStatus(DownloadStatus.Paused);
        }

        TaskAdded?.Invoke(ToRecord(entry));

        if (request.StartImmediately)
        {
            _queue.Enqueue(id);
            _wake.Set();
        }

        return id;
    }

    /// <summary>全部任务快照（id 升序）。</summary>
    public IReadOnlyList<DownloadRecord> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Values
                .OrderBy(static e => e.Task.Id)
                .Select(ToRecord)
                .ToArray();
        }
    }

    /// <summary>单任务快照；不在管理器里返回 <see langword="null"/>。</summary>
    public DownloadRecord? Find(long id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry) ? ToRecord(entry) : null;
        }
    }

    /// <summary>该任务的段快照（详情窗口用；未跑过的任务为空）。</summary>
    public IReadOnlyList<SegmentSnapshot> Segments(long id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry) ? entry.Segments : [];
        }
    }

    /// <summary>暂停：排队中的直接置暂停；跑着的交给引擎（保存快照后关句柄）。</summary>
    public bool Pause(long id)
    {
        if (!TryGetEntry(id, out var entry))
        {
            return false;
        }

        if (_queue.Remove(id))
        {
            if (entry.Task.SetStatus(DownloadStatus.Paused))
            {
                PersistStatus(entry);
                Publish(id);
            }

            return true;
        }

        if (entry.Task.Status is not (DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading))
        {
            return false;
        }

        if (!_engine.Pause(entry.Task))
        {
            // 已出队但引擎还没注册 runtime（RunEntry 刚被调度起来）：留标记，由它收口。
            lock (_gate)
            {
                entry.PauseRequested = true;
            }

            return true;
        }

        Publish(id);
        return true;
    }

    /// <summary>恢复：失败/暂停的任务回到队列，等闸门放行。</summary>
    public bool Resume(long id)
    {
        if (!TryGetEntry(id, out var entry))
        {
            return false;
        }

        var status = entry.Task.Status;
        if (status is not (DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.DiskError))
        {
            return false;
        }

        entry.ErrorText = null;
        entry.CompletedAt = null;
        entry.PauseRequested = false;
        entry.Task.SetStatus(DownloadStatus.Queued);
        PersistStatus(entry);
        Publish(id);
        _queue.Enqueue(id);
        _wake.Set();
        return true;
    }

    /// <summary>暂停全部（含排队中的）。</summary>
    public void PauseAll()
    {
        foreach (var id in SnapshotIds())
        {
            Pause(id);
        }
    }

    /// <summary>恢复全部暂停/失败的任务。</summary>
    public void ResumeAll()
    {
        foreach (var id in SnapshotIds())
        {
            Resume(id);
        }
    }

    /// <summary>删除：暂停、删临时文件与快照、移出列表。已发布的目标文件不动。</summary>
    public bool Delete(long id)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.Remove(id, out entry))
            {
                return false;
            }
        }

        _queue.Remove(id);
        _engine.Delete(entry.Task);
        ReleaseSlot(entry);
        TryStore(store => store.Delete(id));
        TaskRemoved?.Invoke(id);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // 退出前做最后一次同步：DB 里的进度与 state 快照尽量接近内存值。
        PersistTick();

        _persistTimer?.Dispose();
        _disposed = true;
        _wake.Set();
        _engine.Dispose();
        _scheduler.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }

    // ---------- 引擎回调（任意线程） ----------

    void IDownloadObserver.OnTaskStatusChanged(long id, DownloadStatus status)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out entry))
            {
                return;
            }

            if (status == DownloadStatus.Completed)
            {
                entry.CompletedAt = DateTimeOffset.Now;
            }
        }

        if (status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.DiskError or DownloadStatus.Paused)
        {
            ReleaseSlot(entry);
        }

        // 完成/失败带最终文件名与错误码，走 OnCompleted / OnFailed 落库；这里只记中间态与暂停。
        if (status is DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading)
        {
            PersistStatus(entry);
        }
        else if (status == DownloadStatus.Paused)
        {
            PersistSnapshot(entry, error: null);
        }

        Publish(id);
    }

    void IDownloadObserver.OnProgress(long id, long syncedTotal, double bytesPerSecond, TimeSpan? eta)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return;
            }

            entry.SpeedBps = bytesPerSecond;
            entry.Eta = eta;
        }

        Publish(id);
    }

    void IDownloadObserver.OnSegmentsChanged(long id, IReadOnlyList<SegmentSnapshot> segments)
    {
        Entry? entry;
        lock (_gate)
        {
            _entries.TryGetValue(id, out entry);
            if (entry is not null)
            {
                entry.Segments = segments;
            }
        }

        if (entry is not null && segments.Count > 0)
        {
            // 切分/恢复是低频事件，正好是"写库时机"之一；文件名/大小也在这时定下来。
            PersistProbe(entry);
            PersistSegments(entry);
        }
    }

    void IDownloadObserver.OnCompleted(long id, string finalPath)
    {
        Entry? entry;
        lock (_gate)
        {
            _entries.TryGetValue(id, out entry);
            if (entry is not null)
            {
                entry.CompletedAt = DateTimeOffset.Now;
                entry.FinalPath = finalPath;
                entry.SpeedBps = 0;
                entry.Eta = null;
            }
        }

        if (entry is not null)
        {
            // 重名策略可能改过文件名：库里记实际落盘名，重启后才能校验文件存在。
            TryStore(store => store.Rename(id, Path.GetFileName(finalPath)));
            PersistCompleted(entry);
        }

        // 不在这里发布：调用方随后会 ChangeStatus(Completed)，由那次发布带上正确状态，
        // 避免 UI 先收到一帧仍是 Downloading 的记录。
    }

    void IDownloadObserver.OnFailed(long id, DownloadError error, string detail)
    {
        Entry? entry;
        lock (_gate)
        {
            _entries.TryGetValue(id, out entry);
            if (entry is not null)
            {
                entry.ErrorText = detail;
                entry.SpeedBps = 0;
                entry.Eta = null;
            }
        }

        if (entry is not null)
        {
            PersistSnapshot(entry, error);
        }

        Publish(id);
    }

    // ---------- 调度 ----------

    private void SchedulerLoop()
    {
        while (true)
        {
            _wake.WaitOne();
            if (_disposed)
            {
                return;
            }

            while (TryTakeNext(out var id))
            {
                var thread = new Thread(() => RunEntry(id))
                {
                    IsBackground = true,
                    Name = $"xdm-start-{id}",
                };
                thread.Start();
            }
        }
    }

    private bool TryTakeNext(out long id)
    {
        lock (_gate)
        {
            id = 0;
            if (_disposed || _active >= _maxConcurrent)
            {
                return false;
            }

            while (_queue.TryDequeue(out var candidate))
            {
                if (!_entries.TryGetValue(candidate, out var entry))
                {
                    continue;
                }

                if (entry.Task.Status != DownloadStatus.Queued)
                {
                    // 排队期间被暂停/删除
                    continue;
                }

                entry.SlotHeld = true;
                _active++;
                id = candidate;
                return true;
            }

            return false;
        }
    }

    private void RunEntry(long id)
    {
        Entry? entry;
        lock (_gate)
        {
            _entries.TryGetValue(id, out entry);
        }

        if (entry is null)
        {
            return;
        }

        // 出队到引擎注册 runtime 之间被暂停：这里直接收口，不启动下载。
        if (TakePauseRequest(entry))
        {
            entry.Task.SetStatus(DownloadStatus.Paused);
            Publish(id);
            ReleaseSlot(entry);
            return;
        }

        try
        {
            // Resume 内部会判断有没有快照：没有就等同 Start，有就从 .xdm-part 续。
            _engine.Resume(entry.Task);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_gate)
            {
                entry.ErrorText = ex.Message;
            }

            entry.Task.SetStatus(DownloadStatus.Failed);
            Publish(id);
            ReleaseSlot(entry);
            return;
        }

        if (TakePauseRequest(entry))
        {
            // 刚启动成功就收到了暂停：补一刀（引擎此时已注册 runtime，能正常暂停）。
            _engine.Pause(entry.Task);
        }

        // 引擎已经在后台跑段线程；槽位等终态/暂停时再还。
        if (entry.Task.Status is not (DownloadStatus.Connecting or DownloadStatus.Downloading))
        {
            ReleaseSlot(entry);
        }
    }

    private bool TakePauseRequest(Entry entry)
    {
        lock (_gate)
        {
            var requested = entry.PauseRequested;
            entry.PauseRequested = false;
            return requested;
        }
    }

    private void ReleaseSlot(Entry entry)
    {
        lock (_gate)
        {
            if (!entry.SlotHeld)
            {
                return;
            }

            entry.SlotHeld = false;
            _active--;
        }

        _wake.Set();
    }

    private static IReadOnlyDictionary<string, string>? MergeHeaders(
        IReadOnlyDictionary<string, string>? defaults,
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (defaults is not { Count: > 0 })
        {
            return overrides;
        }

        var merged = new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase);
        if (overrides is { Count: > 0 })
        {
            foreach (var (name, value) in overrides)
            {
                merged[name] = value;
            }
        }

        return merged;
    }

    // ---------- 持久化 ----------

    /// <summary>启动恢复：未完成转暂停、已完成校验文件、段与进度回填；快照缺失时用库里的段补写。</summary>
    private void RestoreFromStore()
    {
        var store = _store!;
        try
        {
            store.ResetIncomplete();
            foreach (var row in store.LoadAll())
            {
                var status = row.Status;
                string? errorText = row.Error is { } storedError && storedError != DownloadError.None
                    ? DownloadErrorText.Describe(storedError)
                    : null;

                if (status == DownloadStatus.Completed)
                {
                    var published = Path.Combine(row.TargetFolder, row.FileName);
                    if (!File.Exists(published))
                    {
                        status = DownloadStatus.Failed;
                        errorText = "已完成的文件不存在";
                        store.UpdateStatus(row.Id, status, DownloadError.None, null);
                    }
                }

                var task = new DownloadTask(
                    row.Id,
                    row.Url,
                    row.FileName,
                    row.TargetFolder,
                    Math.Max(1, row.MaxSegments))
                {
                    Kind = row.Kind,
                    ParentId = row.ParentId ?? 0,
                    NamePolicy = row.NamePolicy,
                    Headers = row.Headers,
                    Cookie = row.Cookie,
                };
                task.TotalSize = row.TotalSize;
                task.Validator = row.Validator;
                task.NoRangeSupport = row.NoRangeSupport;
                task.SetStatus(status);

                var partExists = File.Exists(task.PartPath);
                if (row.Segments.Count > 0 &&
                    (partExists || status == DownloadStatus.Completed) &&
                    TryBuildSegments(row.Segments, row.TotalSize, out var segments))
                {
                    task.ReplaceSegments(segments);
                }

                _entries[row.Id] = new Entry(task)
                {
                    CompletedAt = row.FinishedAt,
                    ErrorText = errorText,
                    Segments = task.Snapshot().Select(ToSnapshot).ToArray(),
                };

                // 快照文件比库更"重"（引擎只认它）；只有库里才有段时补写一份，恢复后才能真续传。
                if (partExists &&
                    row.Segments.Count > 0 &&
                    status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.DiskError)
                {
                    WriteStateIfMissing(task);
                }
            }
        }
        catch (Exception ex) when (ex is SqlException or IOException or UnauthorizedAccessException)
        {
            PersistenceError = ex.Message;
            Debug.WriteLine($"[xdm] 任务库恢复失败：{ex}");
        }
    }

    /// <summary>30s 同步：刷盘 + 快照 + 库里的进度（只针对运行中的任务）。</summary>
    private void PersistTick()
    {
        if (_store is null)
        {
            return;
        }

        Entry[] entries;
        lock (_gate)
        {
            entries = _entries.Values.ToArray();
        }

        foreach (var entry in entries)
        {
            if (entry.Task.Status is not (DownloadStatus.Connecting or DownloadStatus.Downloading))
            {
                continue;
            }

            _engine.PersistProgress(entry.Task);
            PersistSegments(entry);
        }
    }

    private void PersistStatus(Entry entry)
        => TryStore(store => store.UpdateStatus(entry.Task.Id, entry.Task.Status, null, null));

    private void PersistProbe(Entry entry)
    {
        var task = entry.Task;
        if (string.IsNullOrWhiteSpace(task.FileName))
        {
            return;
        }

        TryStore(store => store.UpdateProbe(
            task.Id,
            task.FileName,
            task.TotalSize,
            task.Validator,
            task.NoRangeSupport));
    }

    private void PersistSegments(Entry entry)
    {
        var task = entry.Task;
        var segments = task.Snapshot().Select(ToState).ToArray();
        TryStore(store => store.ReplaceSegments(task.Id, segments, task.SyncedTotal, task.TotalSize));
    }

    /// <summary>
    /// 完成态落库：显式写 <see cref="DownloadStatus.Completed"/> 与终态文件名。
    /// 不依赖 <c>task.Status</c>（引擎在 <c>OnCompleted</c> 之后才切状态），
    /// 且"已下载 = 总长"由引擎完成前校验保证。
    /// </summary>
    private void PersistCompleted(Entry entry)
    {
        var task = entry.Task;
        var segments = task.Snapshot().Select(ToState).ToArray();
        var downloaded = task.TotalSize ?? task.SyncedTotal;
        TryStore(store =>
        {
            store.UpdateStatus(task.Id, DownloadStatus.Completed, null, entry.CompletedAt ?? DateTimeOffset.Now);
            store.ReplaceSegments(task.Id, segments, downloaded, task.TotalSize);
        });
    }

    private void PersistSnapshot(Entry entry, DownloadError? error)
    {
        var task = entry.Task;
        var segments = task.Snapshot().Select(ToState).ToArray();
        var finishedAt = entry.CompletedAt;
        TryStore(store =>
        {
            store.UpdateStatus(
                task.Id,
                task.Status,
                error,
                task.Status == DownloadStatus.Completed ? finishedAt ?? DateTimeOffset.Now : null);
            store.ReplaceSegments(task.Id, segments, task.SyncedTotal, task.TotalSize);
        });
    }

    private void TryStore(Action<TaskStore> action)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            action(_store);
            PersistenceError = null;
        }
        catch (Exception ex) when (ex is SqlException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 落库失败不能影响下载线程；暴露在 PersistenceError 里供界面/测试排查。
            PersistenceError = ex.Message;
            Debug.WriteLine($"[xdm] 任务库写入失败：{ex}");
        }
    }

    private void WriteStateIfMissing(DownloadTask task)
    {
        var path = Path.Combine(_stateDir, $"{task.Id}.json");
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            AtomicJson.Write(path, new DownloadState
            {
                Id = task.Id,
                Url = task.Url,
                FileName = task.FileName,
                TotalSize = task.TotalSize,
                Validator = task.Validator,
                NoRangeSupport = task.NoRangeSupport,
                Segments = task.Snapshot().Select(ToState).ToList(),
                SavedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[xdm] 快照补写失败：{ex}");
        }
    }

    /// <summary>把库里的段还原成内存段；偏移必须首尾相接，否则整组丢弃（宁可重下也不写坏）。</summary>
    private static bool TryBuildSegments(
        IReadOnlyList<SegmentState> saved,
        long? totalSize,
        out Segment[] result)
    {
        result = [];
        if (saved.Count == 0)
        {
            return false;
        }

        long expectedOffset = 0;
        var segments = new List<Segment>(saved.Count);
        foreach (var state in saved.OrderBy(static s => s.Id))
        {
            if (state.Offset != expectedOffset || state.Synced < 0)
            {
                return false;
            }

            if (state.Length >= 0 && state.Synced > state.Length)
            {
                return false;
            }

            var segment = new Segment(state.Id, state.Offset, state.Length);
            segment.SetDownloaded(state.Synced);
            segment.SetSynced(state.Synced);
            segment.SetStatus(
                state.Length >= 0 && state.Synced >= state.Length
                    ? SegmentStatus.Finished
                    : SegmentStatus.Ready);
            segments.Add(segment);

            if (state.Length >= 0)
            {
                expectedOffset += state.Length;
            }
        }

        if (totalSize is { } total && expectedOffset != total)
        {
            return false;
        }

        result = segments.ToArray();
        return true;
    }

    private static SegmentState ToState(Segment segment) => new()
    {
        Id = segment.Id,
        Offset = segment.Offset,
        Length = segment.Length,
        Synced = segment.Synced,
        Status = segment.Status,
    };

    private static SegmentSnapshot ToSnapshot(Segment segment) => new()
    {
        Id = segment.Id,
        Offset = segment.Offset,
        Length = segment.Length,
        Synced = segment.Synced,
        Status = segment.Status,
    };

    // ---------- 快照 ----------

    private DownloadRecord ToRecord(Entry entry)
    {
        var task = entry.Task;
        var segments = task.Snapshot();
        var name = task.FileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = DerivedName(task.Url);
        }

        return new DownloadRecord
        {
            Id = task.Id,
            FileName = name,
            Url = task.Url,
            TargetFolder = task.TargetFolder,
            SizeBytes = task.TotalSize ?? 0,
            SyncedBytes = task.SyncedTotal,
            SpeedBps = entry.SpeedBps,
            RemainingSeconds = entry.Eta is { } eta ? (int)Math.Ceiling(eta.TotalSeconds) : null,
            Status = task.Status,
            ErrorText = entry.ErrorText,
            SegmentCount = segments.Count,
            CompletedSegments = segments.Count(static s => s.Status == SegmentStatus.Finished),
            CompletedAt = entry.CompletedAt,
        };
    }

    private static string DerivedName(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? FileNaming.FromUrl(uri) ?? url
            : url;

    private void Publish(long id)
    {
        DownloadRecord? record;
        lock (_gate)
        {
            record = _entries.TryGetValue(id, out var entry) ? ToRecord(entry) : null;
        }

        if (record is not null)
        {
            TaskUpdated?.Invoke(record);
        }
    }

    private bool TryGetEntry(long id, out Entry entry)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out entry!);
        }
    }

    private long[] SnapshotIds()
    {
        lock (_gate)
        {
            return _entries.Keys.ToArray();
        }
    }

    private sealed class Entry(DownloadTask task)
    {
        public DownloadTask Task { get; } = task;

        public string? ErrorText { get; set; }

        public string? FinalPath { get; set; }

        public DateTimeOffset? CompletedAt { get; set; }

        public double SpeedBps { get; set; }

        public TimeSpan? Eta { get; set; }

        public bool SlotHeld { get; set; }

        /// <summary>暂停落在「已出队、引擎未注册 runtime」的窗口里，由 RunEntry 收口。</summary>
        public bool PauseRequested { get; set; }

        public IReadOnlyList<SegmentSnapshot> Segments { get; set; } = [];
    }
}
