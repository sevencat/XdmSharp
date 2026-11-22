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

    /// <summary>
    /// 单个多文件任务（HF 模型包）里同时下载的文件数（1–16，默认 3）。
    /// 只约束「同一父行下的子任务」，不影响独立任务。
    /// </summary>
    public int MaxFilesPerTask { get; init; } = 3;

    /// <summary>快照目录；空则用引擎默认（<c>AppContext.BaseDirectory/state</c>）。</summary>
    public string? StateDir { get; init; }

    /// <summary>HTTP 客户端注入（测试用）；空则引擎自建。</summary>
    public IRangeHttpClient? Client { get; init; }

    /// <summary>
    /// 按代理 id 解析路线的回调（壳里接 <c>ProxyResolver.Resolve</c>）；空则该路线恒为系统代理。
    /// 建任务与重启恢复时各调一次——两次之间用户改了代理表就以当时为准。
    /// </summary>
    public Func<string?, ProxyRoute>? ResolveProxy { get; init; }

    /// <summary>按代理路线分客户端的池（壳里传 <c>ProxyHttpClientProvider</c>）；空则所有任务共用一个客户端。</summary>
    public IRangeHttpClientProvider? ClientProvider { get; init; }

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
    private Func<string?, ProxyRoute>? _resolveProxy;
    private readonly Timer? _persistTimer;
    private int _maxConcurrent;
    private int _maxFilesPerTask = 3;
    private IReadOnlyDictionary<string, string>? _defaultHeaders;

    private long _nextId;
    private int _active;

    /// <summary>父行 id → 当前在跑的子任务数（兄弟闸门用，访问需持 <c>_gate</c>）。</summary>
    private readonly Dictionary<long, int> _runningChildren = [];

    private bool _disposed;

    public DownloadManager()
        : this(null)
    {
    }

    public DownloadManager(DownloadManagerOptions? options)
    {
        options ??= new DownloadManagerOptions();
        _maxConcurrent = Math.Max(1, options.MaxConcurrent);
        _maxFilesPerTask = Math.Clamp(options.MaxFilesPerTask, 1, 16);
        _store = options.Store;
        _resolveProxy = options.ResolveProxy;

        // 接了任务库就别再写 state/*.json：段表只在库里落一份，重启从库恢复，避免两处数据对不上。
        _engine = new DownloadEngine(
            options.Client,
            options.StateDir,
            this,
            options.EngineOptions,
            writeStateFiles: _store is null,
            clients: options.ClientProvider);

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

    /// <summary>
    /// 按代理 id 解析路线的回调。壳要「保存设置后立刻生效」就改这里——
    /// 用 <see cref="DownloadManagerOptions.ResolveProxy"/> 固定住的话，用户换默认代理得重启才生效。
    /// </summary>
    public Func<string?, ProxyRoute>? ResolveProxy
    {
        get => Volatile.Read(ref _resolveProxy);
        set => Volatile.Write(ref _resolveProxy, value);
    }

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
    /// 单个多文件任务（HF 模型包）里同时下载的文件数（1–16）；调大后立即唤醒调度线程放行被挡住的兄弟文件。
    /// 闸门只作用于「同一父行下的子任务」，独立任务仍受 <see cref="MaxConcurrent"/> 管。
    /// </summary>
    public int MaxFilesPerTask
    {
        get
        {
            lock (_gate)
            {
                return _maxFilesPerTask;
            }
        }

        set
        {
            lock (_gate)
            {
                _maxFilesPerTask = Math.Clamp(value, 1, 16);
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
        return AddTask(request, parentId: 0);
    }

    /// <summary>
    /// 新建多文件任务（HF 模型包 / torrent）：先建只做汇总的父行，再按
    /// <see cref="DownloadGroupRequest.Files"/> 各建一条子任务。返回父任务 id。
    /// </summary>
    public long EnqueueGroup(DownloadGroupRequest group)
    {
        ArgumentNullException.ThrowIfNull(group);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(group.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(group.TargetFolder);
        if (group.Files.Count == 0)
        {
            throw new ArgumentException("多文件任务至少要有一个文件", nameof(group));
        }

        if (!Uri.TryCreate(group.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"只支持 http/https 链接：{group.Url}", nameof(group));
        }

        var initialStatus = group.StartImmediately ? DownloadStatus.Queued : DownloadStatus.Paused;
        var proxyId = NormalizeProxyId(group.ProxyId);
        var id = InsertRow(new StoredTask
        {
            Url = uri.AbsoluteUri,
            FileName = group.Name,
            TargetFolder = group.TargetFolder,
            TempFolder = group.TargetFolder,
            MaxSegments = 1,
            Status = initialStatus,
            Kind = group.Kind,
            ProxyId = proxyId,
            SourceMetaJson = group.SourceMetaJson,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var task = new DownloadTask(id, uri.AbsoluteUri, group.Name, group.TargetFolder, 1)
        {
            Kind = group.Kind,
            ProxyId = proxyId,
            Route = _resolveProxy?.Invoke(proxyId) ?? ProxyRoute.System,
            SourceMetaJson = group.SourceMetaJson,
        };
        task.SetStatus(initialStatus);

        var entry = new Entry(task) { Container = true };
        lock (_gate)
        {
            _entries[id] = entry;
        }

        // 父行先入列（列表里在子行前面），子行随后各自 TaskAdded。
        TaskAdded?.Invoke(ToRecord(entry));

        foreach (var file in group.Files)
        {
            AddTask(file, id);
        }

        // 子任务齐了再推一次父行：文件数、总大小、汇总状态这时才准。
        RefreshContainer(id);
        return id;
    }

    /// <summary>
    /// 新建单条任务；<paramref name="parentId"/> 非 0 时挂到父行下（<see cref="EnqueueGroup"/> 用）。
    /// </summary>
    private long AddTask(DownloadRequest request, long parentId)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"只支持 http/https 链接：{request.Url}", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetFolder);

        var maxSegments = Math.Clamp(request.MaxSegments, 1, 256);
        var initialStatus = request.StartImmediately ? DownloadStatus.Queued : DownloadStatus.Paused;
        var headers = MergeHeaders(DefaultHeaders, request.Headers);
        var proxyId = NormalizeProxyId(request.ProxyId);
        var route = _resolveProxy?.Invoke(proxyId) ?? ProxyRoute.System;

        var id = InsertRow(new StoredTask
        {
            Url = uri.AbsoluteUri,
            FileName = request.FileName ?? string.Empty,
            TargetFolder = request.TargetFolder,
            TempFolder = request.TargetFolder,
            MaxSegments = maxSegments,
            Status = initialStatus,
            Kind = request.Kind,
            ParentId = parentId > 0 ? parentId : null,
            NamePolicy = request.NameConflict,
            Headers = headers,
            Cookie = request.Cookie,
            ProxyId = proxyId,
            ContentSha256 = request.ContentSha256,
            SourceMetaJson = request.SourceMetaJson,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var task = new DownloadTask(
            id,
            uri.AbsoluteUri,
            request.FileName ?? string.Empty,
            request.TargetFolder,
            maxSegments)
        {
            Kind = request.Kind,
            ParentId = parentId,
            NamePolicy = request.NameConflict,
            Headers = headers,
            Cookie = request.Cookie,
            ProxyId = proxyId,
            Route = route,
            ContentSha256 = request.ContentSha256,
            SourceMetaJson = request.SourceMetaJson,
        };

        var entry = new Entry(task);
        lock (_gate)
        {
            _entries[id] = entry;
            if (parentId > 0 && _entries.TryGetValue(parentId, out var parent))
            {
                parent.Children.Add(id);
            }
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

    /// <summary>有任务库时 id 由任务库分配（重启后不会复用旧 id）；否则进程内自增。</summary>
    private long InsertRow(StoredTask row)
        => _store is { } store ? store.Insert(row) : Interlocked.Increment(ref _nextId);

    private static string? NormalizeProxyId(string? proxyId)
        => string.IsNullOrWhiteSpace(proxyId) ? null : proxyId.Trim();

    /// <summary>全部任务快照（id 升序）。</summary>
    public IReadOnlyList<DownloadRecord> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Values
                .OrderBy(static e => e.Task.Id)
                .Select(ToRecordLocked)
                .ToArray();
        }
    }

    /// <summary>单任务快照；不在管理器里返回 <see langword="null"/>。</summary>
    public DownloadRecord? Find(long id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry) ? ToRecordLocked(entry) : null;
        }
    }

    /// <summary>
    /// 该任务的段快照（详情窗口 250ms 轮询用；未跑过的任务为空）。
    /// <para>
    /// 段表直接取自任务本身（任务内部有锁），不读 <c>entry.Segments</c> 那份缓存——
    /// 缓存只在建段/暂停时更新，拿去画时间轴会一直停在旧进度。这里也不持 <c>_gate</c>：
    /// 轮询不该和调度线程抢锁。
    /// </para>
    /// </summary>
    public IReadOnlyList<SegmentSnapshot> Segments(long id)
    {
        DownloadTask? task;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                return [];
            }

            task = entry.Task;
        }

        var live = task.Snapshot();
        if (live.Count > 0)
        {
            return Array.ConvertAll([.. live], ToSnapshot);
        }

        lock (_gate)
        {
            return _entries.TryGetValue(id, out var fallback) ? fallback.Segments : [];
        }
    }

    /// <summary>父行下的子任务快照（id 升序，详情窗口用）；非父行返回空。</summary>
    public IReadOnlyList<DownloadRecord> Children(long id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var parent) || parent.Children.Count == 0)
            {
                return [];
            }

            var records = new List<DownloadRecord>(parent.Children.Count);
            foreach (var childId in parent.Children)
            {
                if (_entries.TryGetValue(childId, out var child))
                {
                    records.Add(ToRecordLocked(child));
                }
            }

            return records;
        }
    }

    /// <summary>暂停：排队中的直接置暂停；跑着的交给引擎（保存快照后关句柄）。父行则逐个暂停子任务。</summary>
    public bool Pause(long id)
    {
        if (!TryGetEntry(id, out var entry))
        {
            return false;
        }

        if (entry.Container)
        {
            // 父行自身不在队列里，暂停就是停掉所有子任务；父行状态由汇总重算。
            foreach (var childId in ChildIds(entry))
            {
                Pause(childId);
            }

            return true;
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

    /// <summary>恢复：失败/暂停的任务回到队列，等闸门放行。父行则逐个恢复子任务。</summary>
    public bool Resume(long id)
    {
        if (!TryGetEntry(id, out var entry))
        {
            return false;
        }

        if (entry.Container)
        {
            foreach (var childId in ChildIds(entry))
            {
                Resume(childId);
            }

            return true;
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

    /// <summary>
    /// 删除：暂停、删临时文件、移出列表、删库记录。
    /// <paramref name="deleteFile"/> 为 <c>true</c> 时连已发布的目标文件一起删（界面上是「同时删除已下载的文件」）。
    /// </summary>
    public bool Delete(long id, bool deleteFile = false)
    {
        Entry? entry;
        long[] children;
        lock (_gate)
        {
            if (!_entries.Remove(id, out entry))
            {
                return false;
            }

            children = entry.Container ? [.. entry.Children] : [];
        }

        // 先删子任务（各自删 part / 已发布文件），再把父行收掉，避免留下孤儿行。
        foreach (var childId in children)
        {
            Delete(childId, deleteFile);
        }

        _queue.Remove(id);
        _engine.Delete(entry.Task);
        ReleaseSlot(entry);

        if (deleteFile)
        {
            DeletePublishedFile(entry);
        }

        TryStore(store => store.Delete(id));
        TaskRemoved?.Invoke(id);
        return true;
    }

    /// <summary>
    /// 删已发布的目标文件：优先用本次会话记下的真实路径（改名策略下可能与 <see cref="DownloadTask.FileName"/> 不同），
    /// 重启恢复的任务则按「目标目录 + 文件名」拼。
    /// </summary>
    private static void DeletePublishedFile(Entry entry)
    {
        var path = entry.FinalPath;
        if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(entry.Task.FileName))
        {
            path = Path.Combine(entry.Task.TargetFolder, entry.Task.FileName);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件被占用或没权限：任务照样删，文件由用户自己处理。
            Debug.WriteLine($"[xdm] 删除文件失败 {path}：{ex.Message}");
        }
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

            // 只扫一遍当前队列长度：被兄弟闸门挡住的候选会放回队尾，不设上限就会空转。
            var budget = _queue.Count;
            while (budget-- > 0 && _queue.TryDequeue(out var candidate))
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

                var parentId = entry.Task.ParentId;
                if (parentId > 0)
                {
                    _runningChildren.TryGetValue(parentId, out var running);
                    if (running >= _maxFilesPerTask)
                    {
                        // 同一个包里的文件已经占满名额：放回队尾，等有兄弟结束（ReleaseSlot 会唤醒调度）。
                        _queue.Enqueue(candidate);
                        continue;
                    }

                    _runningChildren[parentId] = running + 1;
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

            // 还掉兄弟名额：父行的下一个文件这才能被放行。
            var parentId = entry.Task.ParentId;
            if (parentId > 0 && _runningChildren.TryGetValue(parentId, out var running))
            {
                if (running <= 1)
                {
                    _runningChildren.Remove(parentId);
                }
                else
                {
                    _runningChildren[parentId] = running - 1;
                }
            }
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
            var rows = store.LoadAll();

            foreach (var row in rows)
            {
                var status = row.Status;
                var errorText = row.Error is { } storedError && storedError != DownloadError.None
                    ? DownloadErrorText.Describe(storedError)
                    : null;

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
                    ContentSha256 = row.ContentSha256,
                    SourceMetaJson = row.SourceMetaJson,
                    ProxyId = row.ProxyId,

                    // 重启后按当时的代理表重新解析：代理被删/改地址都跟着最新设置走。
                    Route = _resolveProxy?.Invoke(row.ProxyId) ?? ProxyRoute.System,
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
            }

            // 父子关系：有子行的才是父行（首版 HF 是每个文件一条、没有子行，按普通任务显示）。
            foreach (var row in rows)
            {
                if (row.ParentId is not { } parentId || !_entries.TryGetValue(parentId, out var parent))
                {
                    continue;
                }

                parent.Container = true;
                parent.Children.Add(row.Id);
            }

            foreach (var row in rows)
            {
                if (!_entries.TryGetValue(row.Id, out var entry))
                {
                    continue;
                }

                if (entry.Container)
                {
                    // 父行没有自己的一致性可查：状态/错误/完成时间全部按子任务重算。
                    var totals = AggregateLocked(entry);
                    entry.Task.SetStatus(totals.Status);
                    entry.ErrorText = totals.Error;
                    entry.CompletedAt = totals.CompletedAt;
                    continue;
                }

                if (row.Status == DownloadStatus.Completed)
                {
                    var published = Path.Combine(row.TargetFolder, row.FileName);
                    if (!File.Exists(published))
                    {
                        entry.Task.SetStatus(DownloadStatus.Failed);
                        entry.ErrorText = "已完成的文件不存在";
                        store.UpdateStatus(row.Id, DownloadStatus.Failed, DownloadError.None, null);
                    }
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
            if (entry.Container)
            {
                // 父行没有段可写，只需把汇总进度落到库里（重启后进度条不倒退）。
                GroupTotals totals;
                lock (_gate)
                {
                    totals = AggregateLocked(entry);
                }

                if (totals.Count == 0)
                {
                    continue;
                }

                var id = entry.Task.Id;
                var synced = totals.Synced;
                var size = totals.Size;
                TryStore(store => store.UpdateProgress(id, synced, size));
                continue;
            }

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
        lock (_gate)
        {
            return ToRecordLocked(entry);
        }
    }

    /// <summary>调用方需持有 <see cref="_gate"/>（父行汇总要读子任务）。</summary>
    private DownloadRecord ToRecordLocked(Entry entry)
    {
        var task = entry.Task;
        var name = task.FileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = DerivedName(task.Url);
        }

        if (entry.Container)
        {
            // 父行自身没有段：大小/进度/速度/状态全按子任务汇总，段数位让给「文件数」。
            var totals = AggregateLocked(entry);
            return new DownloadRecord
            {
                Id = task.Id,
                FileName = name,
                Url = task.Url,
                TargetFolder = task.TargetFolder,
                SizeBytes = totals.Size,
                SyncedBytes = totals.Synced,
                SpeedBps = totals.Speed,
                RemainingSeconds = totals.Speed > 0 && totals.Size > totals.Synced
                    ? (int)Math.Ceiling((totals.Size - totals.Synced) / totals.Speed)
                    : null,
                Status = totals.Status,
                ErrorText = totals.Error,
                IsContainer = true,
                ChildCount = totals.Count,
                CompletedChildren = totals.Done,
                CompletedAt = totals.CompletedAt,
            };
        }

        var segments = task.Snapshot();
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
            ParentId = task.ParentId > 0 ? task.ParentId : null,
            CompletedAt = entry.CompletedAt,
        };
    }

    /// <summary>父行汇总结果（只用于生成 <see cref="DownloadRecord"/>，不回写子任务）。</summary>
    private readonly record struct GroupTotals(
        long Size,
        long Synced,
        double Speed,
        int Count,
        int Done,
        DownloadStatus Status,
        string? Error,
        DateTimeOffset? CompletedAt);

    /// <summary>按子任务汇总父行；调用方需持有 <see cref="_gate"/>。</summary>
    private GroupTotals AggregateLocked(Entry parent)
    {
        long size = 0;
        long synced = 0;
        double speed = 0;
        int done = 0;
        int failed = 0;
        int diskError = 0;
        int active = 0;
        int queued = 0;
        string? error = null;
        DateTimeOffset? completedAt = null;

        foreach (var childId in parent.Children)
        {
            if (!_entries.TryGetValue(childId, out var child))
            {
                continue;
            }

            var task = child.Task;
            size += task.TotalSize ?? 0;
            synced += task.SyncedTotal;
            speed += child.SpeedBps;

            switch (task.Status)
            {
                case DownloadStatus.Completed:
                    done++;
                    if (child.CompletedAt is { } at && (completedAt is null || at > completedAt))
                    {
                        completedAt = at;
                    }

                    break;
                case DownloadStatus.Failed:
                    failed++;
                    error ??= child.ErrorText ?? "下载失败";
                    break;
                case DownloadStatus.DiskError:
                    diskError++;
                    error ??= child.ErrorText ?? "磁盘错误";
                    break;
                case DownloadStatus.Queued:
                    queued++;
                    break;
                case DownloadStatus.Paused:
                    break;
                default:
                    active++;
                    break;
            }
        }

        var count = parent.Children.Count;
        DownloadStatus status;
        if (count == 0)
        {
            status = parent.Task.Status;
        }
        else if (done == count)
        {
            status = DownloadStatus.Completed;
        }
        else if (active > 0)
        {
            // 有文件在跑就显示下载中，别因为个别失败把整包说成失败。
            status = DownloadStatus.Downloading;
        }
        else if (diskError > 0 || failed > 0)
        {
            status = diskError > 0 ? DownloadStatus.DiskError : DownloadStatus.Failed;
            error ??= "部分文件下载失败";
        }
        else if (queued > 0)
        {
            status = DownloadStatus.Queued;
        }
        else
        {
            status = DownloadStatus.Paused;
        }

        return new GroupTotals(
            size,
            synced,
            speed,
            count,
            done,
            status,
            status is DownloadStatus.Failed or DownloadStatus.DiskError ? error : null,
            status == DownloadStatus.Completed ? completedAt ?? parent.CompletedAt : null);
    }

    /// <summary>父行下子任务 id 的快照（避免持锁遍历时被别人改）。</summary>
    private IReadOnlyList<long> ChildIds(Entry parent)
    {
        lock (_gate)
        {
            return [.. parent.Children];
        }
    }

    /// <summary>
    /// 子任务变化后重算父行并推送。状态真的变了才落库（进度由 30s 的 <see cref="PersistTick"/> 统一写），
    /// 否则 12 个文件的进度事件会把任务库刷爆。
    /// </summary>
    private void RefreshContainer(long parentId)
    {
        DownloadRecord record;
        bool statusChanged;
        lock (_gate)
        {
            if (!_entries.TryGetValue(parentId, out var parent) || !parent.Container)
            {
                return;
            }

            var totals = AggregateLocked(parent);
            statusChanged = parent.Task.SetStatus(totals.Status);
            parent.ErrorText = totals.Error;
            parent.CompletedAt = totals.CompletedAt;
            record = ToRecordLocked(parent);
        }

        if (statusChanged)
        {
            TryStore(store => store.UpdateStatus(parentId, record.Status, null, record.CompletedAt));
        }

        TaskUpdated?.Invoke(record);
    }

    private static string DerivedName(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? FileNaming.FromUrl(uri) ?? url
            : url;

    private void Publish(long id)
    {
        DownloadRecord? record;
        long parentId;
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                record = ToRecordLocked(entry);
                parentId = entry.Task.ParentId;
            }
            else
            {
                record = null;
                parentId = 0;
            }
        }

        if (record is not null)
        {
            TaskUpdated?.Invoke(record);
        }

        // 子任务变了父行就得跟着变（状态/总进度/完成时间）。
        if (parentId > 0)
        {
            RefreshContainer(parentId);
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

        /// <summary>父行：自身不下载，状态/进度/速度按 <see cref="Children"/> 汇总。</summary>
        public bool Container { get; set; }

        /// <summary>父行下的子任务 id（按创建顺序）；非父行为空。访问需持 <c>_gate</c>。</summary>
        public List<long> Children { get; } = [];
    }
}
