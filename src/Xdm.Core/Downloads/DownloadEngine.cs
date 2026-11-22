using System.Collections.Concurrent;
using Xdm.Core.Errors;
using Xdm.Core.Http;
using Xdm.Core.Storage;

namespace Xdm.Core.Downloads;

/// <summary>
/// HTTP 分段下载引擎：探测 → 建段 → 开句柄 → 起段线程 → 全部完成则发布。
/// </summary>
/// <remarks>
/// 阻塞 IO + 专职线程（<c>ARCHITECTURE.md</c> §6）：段线程是 <see cref="Thread"/> 而不是线程池，
/// 因为它们长驻且大部分时间阻塞在读上；调度决策只在「段结束」时做一次。
/// 首期范围：等分切段、断点续传、Range 退化；动态二分 / 段接管 / 尾段竞速 / 限速留给 M6。
/// </remarks>
public sealed class DownloadEngine : IDisposable
{
    private readonly IRangeHttpClient _client;
    private readonly bool _ownsClient;
    private readonly string _stateDir;
    private readonly IDownloadObserver? _observer;
    private readonly DownloadEngineOptions _options;
    private readonly ConcurrentDictionary<long, Runtime> _runtimes = new();
    private bool _disposed;

    /// <summary>创建引擎。<paramref name="client"/> 为空时自建 <see cref="RangeHttpClient"/>。</summary>
    public DownloadEngine(
        IRangeHttpClient? client = null,
        string? stateDir = null,
        IDownloadObserver? observer = null,
        DownloadEngineOptions? options = null)
    {
        _client = client ?? new RangeHttpClient();
        _ownsClient = client is null;
        _stateDir = stateDir ?? Path.Combine(AppContext.BaseDirectory, "state");
        _observer = observer;
        _options = options ?? new DownloadEngineOptions();
    }

    /// <summary>新建并启动任务（阻塞到探测完成；段在后台线程跑）。</summary>
    public DownloadTask Start(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_runtimes.ContainsKey(task.Id))
        {
            throw new InvalidOperationException($"任务 {task.Id} 已在运行");
        }

        var cancellation = new CancellationTokenSource();
        var runtime = new Runtime(task, cancellation);
        if (!_runtimes.TryAdd(task.Id, runtime))
        {
            cancellation.Dispose();
            throw new InvalidOperationException($"任务 {task.Id} 已在运行");
        }

        try
        {
            ChangeStatus(task, DownloadStatus.Connecting);

            // runtime 先注册再探测：否则用户在「正在连接」阶段点暂停会落空（探测最长可卡到读超时）。
            var probe = Probe(task, cancellation.Token);
            runtime.ProbeDone = true;
            if (cancellation.IsCancellationRequested)
            {
                // 探测期间被暂停（探测自己也可能因取消而返回失败，这里先判取消，避免误报失败）。
                return task;
            }

            if (!probe.Success)
            {
                cancellation.Dispose();
                Fail(task, probe.Error, probe.Detail);
                return task;
            }

            BuildSegments(task);
            if (!OpenPartFile(task, fresh: true))
            {
                cancellation.Dispose();
                Fail(task, DownloadError.Disk, "无法创建临时文件");
                return task;
            }

            if (cancellation.IsCancellationRequested)
            {
                // 探测期间被暂停：句柄刚开，交回给 Pause 的收尾逻辑前先关掉。
                ClosePartFile(task);
                return task;
            }

            ChangeStatus(task, DownloadStatus.Downloading);
            Launch(runtime);
            return task;
        }
        catch (OperationCanceledException)
        {
            // 暂停路径负责保存快照、关句柄、置 Paused，这里只退出。
            return task;
        }
    }

    /// <summary>从 <c>.xdm-part</c> + 状态快照恢复；没有快照则等同 <see cref="Start"/>。</summary>
    public DownloadTask Resume(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_runtimes.ContainsKey(task.Id))
        {
            throw new InvalidOperationException($"任务 {task.Id} 已在运行");
        }

        var state = AtomicJson.Read<DownloadState>(StatePath(task));
        if (state is null || !File.Exists(task.PartPath))
        {
            return Start(task);
        }

        var cancellation = new CancellationTokenSource();
        var runtime = new Runtime(task, cancellation);
        if (!_runtimes.TryAdd(task.Id, runtime))
        {
            cancellation.Dispose();
            throw new InvalidOperationException($"任务 {task.Id} 已在运行");
        }

        try
        {
            ChangeStatus(task, DownloadStatus.Connecting);

            var oldValidator = state.Validator;
            var oldSize = state.TotalSize;
            task.Validator = state.Validator;

            var probe = Probe(task, cancellation.Token);
            runtime.ProbeDone = true;
            if (cancellation.IsCancellationRequested)
            {
                return task;
            }

            if (!probe.Success)
            {
                cancellation.Dispose();
                Fail(task, probe.Error, probe.Detail);
                return task;
            }

            // 文件变了（Last-Modified 或大小不同）或不支持 Range → 整任务重来，旧数据一律不认。
            var validatorChanged = oldValidator is not null
                && task.Validator is not null
                && !string.Equals(oldValidator, task.Validator, StringComparison.Ordinal);
            var sizeChanged = oldSize is not null && task.TotalSize is not null && oldSize != task.TotalSize;

            if (validatorChanged || sizeChanged || task.NoRangeSupport || !TryRestoreSegments(task, state))
            {
                _runtimes.TryRemove(task.Id, out _);
                cancellation.Dispose();
                ResetProgress(task);
                return Start(task);
            }

            if (!OpenPartFile(task, fresh: false))
            {
                cancellation.Dispose();
                Fail(task, DownloadError.Disk, "无法打开临时文件");
                return task;
            }

            if (cancellation.IsCancellationRequested)
            {
                ClosePartFile(task);
                return task;
            }

            ChangeStatus(task, DownloadStatus.Downloading);
            Launch(runtime);
            return task;
        }
        catch (OperationCanceledException)
        {
            // 暂停路径负责收尾；探测期间取消的 CTS 由这里释放（Pause 见 ProbeDone=false 不会动它）。
            cancellation.Dispose();
            return task;
        }
    }

    /// <summary>暂停：取消 → 等段线程退出 → 刷盘 → 写状态快照 → 关句柄。</summary>
    public bool Pause(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_runtimes.TryRemove(task.Id, out var runtime))
        {
            return false;
        }

        runtime.Cancellation.Cancel();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        foreach (var thread in runtime.Threads)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                break;
            }

            thread.Join(left);
        }

        runtime.Reporter?.Join(TimeSpan.FromMilliseconds(200));
        if (task.Status == DownloadStatus.Completed)
        {
            // 竞态：取消的同一瞬间最后一段正好完成并已发布，不把状态改回暂停。
            DisposeCancellation(runtime);
            return false;
        }

        SaveStateSafe(task);
        ClosePartFile(task);
        ChangeStatus(task, DownloadStatus.Paused);
        DisposeCancellation(runtime);
        return true;
    }

    /// <summary>
    /// 探测进行中不动 CTS：探测线程正拿它的 token 做 Register/Send，
    /// 此时 Dispose 会抛 <see cref="ObjectDisposedException"/>；交给 Start/Resume 的取消分支释放。
    /// </summary>
    private static void DisposeCancellation(Runtime runtime)
    {
        if (runtime.ProbeDone)
        {
            runtime.Cancellation.Dispose();
        }
    }

    /// <summary>暂停并删掉临时文件与快照。</summary>
    public void Delete(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        Pause(task);
        task.Completion.TrySetResult(new DownloadResult(false, null, DownloadError.Cancelled, "已删除"));
        AtomicJson.Delete(StatePath(task));
        try
        {
            File.Delete(task.PartPath);
        }
        catch (IOException)
        {
            // 句柄没放开时留到下次启动清理。
        }
    }

    /// <summary>该任务当前是否在引擎里跑。</summary>
    public bool IsRunning(long taskId) => _runtimes.ContainsKey(taskId);

    /// <summary>分段快照目录（<c>state/&lt;id&gt;.json</c>）。</summary>
    public string StateDir => _stateDir;

    /// <summary>
    /// 周期同步（任务库 30s 落盘时调用）：刷盘 + 写状态快照。不在运行中的任务直接返回。
    /// </summary>
    public void PersistProgress(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_runtimes.ContainsKey(task.Id))
        {
            return;
        }

        try
        {
            task.PartFile?.FlushToDisk();
            SaveStateSafe(task);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // 同步失败不影响下载；下一次 30s 再试。
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var runtime in _runtimes.Values)
        {
            runtime.Cancellation.Cancel();
        }

        foreach (var id in _runtimes.Keys)
        {
            _runtimes.TryRemove(id, out _);
        }

        if (_ownsClient && _client is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    // ---------- 探测 / 建段 ----------

    private ProbeResult Probe(DownloadTask task, CancellationToken cancellationToken)
    {
        var url = new Uri(task.Url);
        var retries = 0;
        var busyWaits = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var response = _client.Send(
                new HttpRangeRequest
                {
                    Url = url,
                    Offset = 0,
                    Length = 1,
                    IfRange = task.Validator,
                    Headers = task.Headers,
                    Cookie = task.Cookie,
                },
                cancellationToken);

            var info = response.Info;
            switch (info.Outcome)
            {
                case SourceOutcome.Ok:
                    AdoptResponse(task, url, info);
                    return ProbeResult.Ok;

                case SourceOutcome.Unsupported:
                    AdoptResponse(task, url, info);
                    task.NoRangeSupport = true;
                    return ProbeResult.Ok;

                case SourceOutcome.Busy:
                    if (++busyWaits > _options.MaxBusyWaits)
                    {
                        return new ProbeResult(false, DownloadError.Busy, "服务器持续繁忙");
                    }

                    Wait(info.RetryAfter ?? TimeSpan.FromSeconds(1), cancellationToken);
                    continue;

                case SourceOutcome.Retry:
                    if (++retries > _options.MaxRetries)
                    {
                        return new ProbeResult(false, info.Error, info.Detail);
                    }

                    Wait(Backoff(retries), cancellationToken);
                    continue;

                default:
                    return new ProbeResult(
                        false,
                        info.Error == DownloadError.None ? DownloadError.Rejected : info.Error,
                        info.Detail);
            }
        }
    }

    private static void AdoptResponse(DownloadTask task, Uri requested, HttpResponseInfo info)
    {
        task.FinalUrl = info.FinalUrl?.ToString();
        task.TotalSize = info.TotalSize ?? info.ContentLength;
        task.Validator = info.Validator;
        if (string.IsNullOrWhiteSpace(task.FileName))
        {
            task.FileName = FileNaming.FromResponse(info.FinalUrl ?? requested, info.ContentDisposition);
        }
    }

    private void BuildSegments(DownloadTask task)
    {
        var size = task.TotalSize;
        if (task.NoRangeSupport || size is null || size <= 0)
        {
            task.ReplaceSegments([new Segment(0, 0, size ?? -1)]);
            return;
        }

        var max = Math.Max(1, Math.Min(task.MaxSegments, _options.MaxSegments));
        var bySize = size.Value / Math.Max(1L, _options.MinSegmentSize);
        var count = (int)Math.Clamp(bySize, 1L, max);

        var segments = new List<Segment>(count);
        var baseLength = size.Value / count;
        var remainder = size.Value % count;
        long offset = 0;
        for (var i = 0; i < count; i++)
        {
            var length = baseLength + (i == count - 1 ? remainder : 0);
            segments.Add(new Segment(i, offset, length));
            offset += length;
        }

        task.ReplaceSegments(segments);
        _observer?.OnSegmentsChanged(task.Id, Snapshots(task));
    }

    private bool OpenPartFile(DownloadTask task, bool fresh)
    {
        try
        {
            if (fresh)
            {
                task.PartFile = PartFile.Create(task.PartPath, task.TotalSize);
            }
            else
            {
                var part = PartFile.Open(task.PartPath);
                if (task.TotalSize is { } size && part.Length != size)
                {
                    part.SetLength(size);
                }

                task.PartFile = part;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            task.PartFile = null;
            return false;
        }
    }

    private static bool TryRestoreSegments(DownloadTask task, DownloadState state)
    {
        if (state.TotalSize is null || state.TotalSize != task.TotalSize || state.Segments.Count == 0)
        {
            return false;
        }

        long expected = 0;
        var segments = new List<Segment>(state.Segments.Count);
        foreach (var saved in state.Segments.OrderBy(static s => s.Id))
        {
            if (saved.Offset != expected || saved.Length <= 0 || saved.Synced < 0 || saved.Synced > saved.Length)
            {
                return false;
            }

            var segment = new Segment(saved.Id, saved.Offset, saved.Length);
            segment.SetDownloaded(saved.Synced);
            segment.SetSynced(saved.Synced);
            segment.SetStatus(saved.Synced >= saved.Length ? SegmentStatus.Finished : SegmentStatus.Ready);
            segments.Add(segment);
            expected += saved.Length;
        }

        if (expected != state.TotalSize)
        {
            return false;
        }

        task.ReplaceSegments(segments);
        return true;
    }

    // ---------- 线程 ----------

    private void Launch(Runtime runtime)
    {
        var task = runtime.Task;
        var segments = task.Snapshot();

        var pending = 0;
        foreach (var segment in segments)
        {
            if (segment.IsDone)
            {
                segment.SetStatus(SegmentStatus.Finished);
                continue;
            }

            pending++;
        }

        runtime.Pending = pending;
        if (pending == 0)
        {
            Complete(runtime);
            return;
        }

        foreach (var segment in segments)
        {
            if (segment.Status == SegmentStatus.Finished)
            {
                continue;
            }

            var thread = new Thread(() => RunSegment(runtime, segment))
            {
                IsBackground = true,
                Name = $"xdm-seg-{task.Id}-{segment.Id}",
            };
            runtime.Threads.Add(thread);
            thread.Start();
        }

        runtime.Reporter = new Thread(() => ReportLoop(runtime))
        {
            IsBackground = true,
            Name = $"xdm-progress-{task.Id}",
        };
        runtime.Reporter.Start();

        _observer?.OnSegmentsChanged(task.Id, Snapshots(task));
    }

    private void RunSegment(Runtime runtime, Segment segment)
    {
        var task = runtime.Task;
        try
        {
            var runner = new SegmentRunner(task, segment, _client, task.PartFile!, _options);
            var result = runner.Run(runtime.Cancellation.Token);
            if (!result.Success && result.Error != DownloadError.Cancelled)
            {
                lock (runtime.Gate)
                {
                    runtime.Failure ??= result;
                }
            }
        }
        catch (OperationCanceledException)
        {
            segment.SetStatus(SegmentStatus.Cancelled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (runtime.Gate)
            {
                runtime.Failure ??= SegmentRunResult.Fail(DownloadError.Internal, ex.Message);
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref runtime.Pending) == 0)
            {
                Complete(runtime);
            }
        }
    }

    private void ReportLoop(Runtime runtime)
    {
        var task = runtime.Task;
        var tracker = new ProgressTracker();
        var token = runtime.Cancellation.Token;
        while (Volatile.Read(ref runtime.Pending) > 0 && !token.IsCancellationRequested)
        {
            var synced = task.SyncedTotal;
            tracker.Add(synced, DateTimeOffset.UtcNow);
            _observer?.OnProgress(task.Id, synced, tracker.BytesPerSecond, tracker.Eta(task.TotalSize));
            if (token.WaitHandle.WaitOne(_options.ProgressInterval))
            {
                break;
            }
        }
    }

    private void Complete(Runtime runtime)
    {
        var task = runtime.Task;
        try
        {
            if (runtime.Cancellation.IsCancellationRequested)
            {
                // 暂停路径负责收尾（保存快照、关句柄、置 Paused）。
                return;
            }

            if (runtime.Failure is { } failure)
            {
                Fail(task, failure.Error, failure.Detail);
                return;
            }

            var unfinished = task.Snapshot().Where(static s => s.Status != SegmentStatus.Finished).ToArray();
            if (unfinished.Length > 0)
            {
                Fail(task, DownloadError.Internal, $"{unfinished.Length} 个段未完成");
                return;
            }

            if (task.TotalSize is { } size && task.SyncedTotal != size)
            {
                Fail(task, DownloadError.Internal, $"落盘字节 {task.SyncedTotal} 与总大小 {size} 不符");
                return;
            }

            task.PartFile?.FlushToDisk();
            ClosePartFile(task);

            var name = string.IsNullOrWhiteSpace(task.FileName) ? FileNaming.FallbackName : task.FileName;
            var finalPath = FileNaming.ResolveTarget(task.TargetFolder, name, task.NamePolicy);
            if (finalPath is null)
            {
                Fail(task, DownloadError.TargetExists, $"目标文件已存在：{Path.Combine(task.TargetFolder, name)}");
                return;
            }

            // 同目录改名是原子操作：断电只会有完整 part 或完整目标文件，不会留半个。
            File.Move(task.PartPath, finalPath, overwrite: task.NamePolicy == NameConflictPolicy.Overwrite);
            AtomicJson.Delete(StatePath(task));

            ChangeStatus(task, DownloadStatus.Completed);
            _observer?.OnCompleted(task.Id, finalPath);
            task.Completion.TrySetResult(new DownloadResult(true, finalPath, DownloadError.None, null));
            _runtimes.TryRemove(task.Id, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(task, DownloadError.Disk, ex.Message);
        }
        finally
        {
            runtime.Cancellation.Dispose();
        }
    }

    private void Fail(DownloadTask task, DownloadError error, string? detail)
    {
        SaveStateSafe(task);
        ClosePartFile(task);
        _runtimes.TryRemove(task.Id, out _);
        ChangeStatus(task, error == DownloadError.Disk ? DownloadStatus.DiskError : DownloadStatus.Failed);
        _observer?.OnFailed(task.Id, error, detail ?? DownloadErrorText.Describe(error));
        task.Completion.TrySetResult(new DownloadResult(false, null, error, detail));
    }

    // ---------- 持久化 ----------

    private string StatePath(DownloadTask task) => Path.Combine(_stateDir, $"{task.Id}.json");

    private void SaveStateSafe(DownloadTask task)
    {
        if (task.TotalSize is null)
        {
            return;
        }

        try
        {
            var state = new DownloadState
            {
                Id = task.Id,
                Url = task.Url,
                FileName = task.FileName,
                TotalSize = task.TotalSize,
                Validator = task.Validator,
                NoRangeSupport = task.NoRangeSupport,
                Segments = task.Snapshot().Select(static s => new SegmentState
                {
                    Id = s.Id,
                    Offset = s.Offset,
                    Length = s.Length,
                    Synced = s.Synced,
                    Status = s.Status,
                }).ToList(),
            };
            AtomicJson.Write(StatePath(task), state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 快照写不下去不影响正在下载的任务；暂停后仍可用 .xdm-part 大小粗判。
        }
    }

    private void ClosePartFile(DownloadTask task)
    {
        task.PartFile?.Dispose();
        task.PartFile = null;
    }

    private void ResetProgress(DownloadTask task)
    {
        AtomicJson.Delete(StatePath(task));
        try
        {
            File.Delete(task.PartPath);
        }
        catch (IOException)
        {
            // 后面 Create 会覆盖。
        }

        task.ClearSegments();
        task.Validator = null;
        task.NoRangeSupport = false;
        task.TotalSize = null;
    }

    // ---------- 小工具 ----------

    private void ChangeStatus(DownloadTask task, DownloadStatus status)
    {
        if (task.SetStatus(status))
        {
            _observer?.OnTaskStatusChanged(task.Id, status);
        }
    }

    private static SegmentSnapshot[] Snapshots(DownloadTask task)
        => task.Snapshot()
            .Select(static s => new SegmentSnapshot(s.Id, s.Offset, s.Length, s.Synced, s.Status))
            .ToArray();

    private TimeSpan Backoff(int attempt)
    {
        var delay = _options.RetryBaseDelay * Math.Pow(2, attempt - 1);
        return delay > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delay;
    }

    private static void Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        if (cancellationToken.WaitHandle.WaitOne(delay))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private readonly record struct ProbeResult(bool Success, DownloadError Error, string? Detail)
    {
        public static ProbeResult Ok => new(true, DownloadError.None, null);
    }

    private sealed class Runtime
    {
        public Runtime(DownloadTask task, CancellationTokenSource cancellation)
        {
            Task = task;
            Cancellation = cancellation;
        }

        public DownloadTask Task { get; }

        public CancellationTokenSource Cancellation { get; }

        /// <summary>探测已结束（成功或失败）；只有为真时 Pause 才能安全释放 CTS。</summary>
        public volatile bool ProbeDone;

        public object Gate { get; } = new();

        public List<Thread> Threads { get; } = [];

        public Thread? Reporter { get; set; }

        public int Pending;

        public SegmentRunResult? Failure;
    }
}
