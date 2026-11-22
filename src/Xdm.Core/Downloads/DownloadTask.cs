using Xdm.Core.Storage;

namespace Xdm.Core.Downloads;

/// <summary>任务种类。首期只实现 <see cref="Http"/>；V2/V3 只加枚举值与新引擎（<c>ARCHITECTURE.md</c> §12.1）。</summary>
public enum TaskKind
{
    /// <summary>普通 HTTP/HTTPS 单文件。</summary>
    Http,

    /// <summary>HuggingFace 模型包（V2）。</summary>
    HuggingFace,

    /// <summary>BT / 磁力（V3）。</summary>
    Torrent,
}

/// <summary>
/// 一个下载任务：段表、状态、临时文件句柄。段表用一把锁保护，锁内只做状态迁移，IO 一律在锁外。
/// </summary>
public sealed class DownloadTask
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Segment> _segments = new();
    private DownloadStatus _status = DownloadStatus.Queued;
    private long? _totalSize;
    private string? _validator;
    private bool _noRangeSupport;

    /// <summary>创建任务。<paramref name="fileName"/> 可留空，探测后由响应头推导。</summary>
    public DownloadTask(long id, string url, string fileName, string targetFolder, int maxSegments = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegments);

        Id = id;
        Url = url;
        FileName = fileName;
        TargetFolder = targetFolder;
        MaxSegments = maxSegments;
    }

    /// <summary>任务 id（同时决定临时文件名 <c>.&lt;id&gt;.xdm-part</c>）。</summary>
    public long Id { get; }

    /// <summary>任务种类。</summary>
    public TaskKind Kind { get; init; } = TaskKind.Http;

    /// <summary>父任务 id（HF 模型包 / torrent）；首期恒为 0。</summary>
    public long ParentId { get; init; }

    /// <summary>原始 URL。</summary>
    public string Url { get; set; }

    /// <summary>跟完重定向后的 URL（内存态，不持久化）。</summary>
    public string? FinalUrl { get; set; }

    /// <summary>最终文件名；空串表示探测后推导。</summary>
    public string FileName { get; set; }

    /// <summary>目标目录（临时文件与最终文件都放这里）。</summary>
    public string TargetFolder { get; set; }

    /// <summary>段数上限。</summary>
    public int MaxSegments { get; }

    /// <summary>发布时目标目录已有同名文件的处理策略。</summary>
    public NameConflictPolicy NamePolicy { get; init; } = NameConflictPolicy.Rename;

    /// <summary>完整大小；<c>null</c> 表示未知（EOF 模式）。</summary>
    public long? TotalSize
    {
        get
        {
            lock (_gate)
            {
                return _totalSize;
            }
        }
        set
        {
            lock (_gate)
            {
                _totalSize = value;
            }
        }
    }

    /// <summary><c>If-Range</c> 校验值（首次响应的 <c>Last-Modified</c>）。</summary>
    public string? Validator
    {
        get
        {
            lock (_gate)
            {
                return _validator;
            }
        }
        set
        {
            lock (_gate)
            {
                _validator = value;
            }
        }
    }

    /// <summary>服务器不支持 Range（整任务只能单流）。</summary>
    public bool NoRangeSupport
    {
        get
        {
            lock (_gate)
            {
                return _noRangeSupport;
            }
        }
        set
        {
            lock (_gate)
            {
                _noRangeSupport = value;
            }
        }
    }

    /// <summary>当前状态。</summary>
    public DownloadStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>自定义请求头。</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>任务级 Cookie。</summary>
    public string? Cookie { get; init; }

    /// <summary>
    /// 任务选定的代理 id（<see langword="null"/> = 用建任务时的默认代理，
    /// <c>system</c>/<c>direct</c> 或具名代理 id）。只用于展示与重启后恢复，不发请求。
    /// </summary>
    public string? ProxyId { get; init; }

    /// <summary>
    /// 创建任务时解析好的代理路线；引擎按它取客户端。代理被删/改都不影响已建任务。
    /// </summary>
    public Http.ProxyRoute Route { get; init; } = Http.ProxyRoute.System;

    /// <summary>未完成文件（<c>.&lt;id&gt;.xdm-part</c>，点前缀避免被误认为完整文件）。</summary>
    public string PartPath => Path.Combine(TargetFolder, $".{Id}.xdm-part");

    /// <summary>当前写句柄；由 <see cref="DownloadEngine"/> 管理。</summary>
    public PartFile? PartFile { get; internal set; }

    /// <summary>
    /// 终态（<see cref="DownloadStatus.Completed"/> / <see cref="DownloadStatus.Failed"/> /
    /// <see cref="DownloadStatus.DiskError"/>）时给出结果；**暂停不完成**，同一个任务恢复后继续等它。
    /// </summary>
    public TaskCompletionSource<DownloadResult> Completion { get; }
        = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>已落盘总字节（= Σ <see cref="Segment.Synced"/>）。</summary>
    public long SyncedTotal
    {
        get
        {
            lock (_gate)
            {
                long total = 0;
                foreach (var segment in _segments.Values)
                {
                    total += segment.Synced;
                }

                return total;
            }
        }
    }

    /// <summary>段快照（按 id 排序，稳定顺序）。</summary>
    public IReadOnlyList<Segment> Snapshot()
    {
        lock (_gate)
        {
            return _segments.Values.OrderBy(static s => s.Id).ToArray();
        }
    }

    /// <summary>整表替换（探测后建段、恢复、重来都走这里）。</summary>
    public void ReplaceSegments(IEnumerable<Segment> segments)
    {
        lock (_gate)
        {
            _segments.Clear();
            foreach (var segment in segments)
            {
                _segments[segment.Id] = segment;
            }
        }
    }

    /// <summary>清空段表。</summary>
    public void ClearSegments()
    {
        lock (_gate)
        {
            _segments.Clear();
        }
    }

    /// <summary>迁移状态并返回是否真的变了。</summary>
    public bool SetStatus(DownloadStatus status)
    {
        lock (_gate)
        {
            if (_status == status)
            {
                return false;
            }

            _status = status;
            return true;
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"Task#{Id} {FileName} {Status} {SyncedTotal}/{TotalSize?.ToString() ?? "?"}";
}

/// <summary>任务结束时的结果。</summary>
/// <param name="Success">是否成功并已发布。</param>
/// <param name="FinalPath">发布后的完整路径。</param>
/// <param name="Error">失败原因。</param>
/// <param name="Detail">给日志看的细节。</param>
public sealed record DownloadResult(bool Success, string? FinalPath, Errors.DownloadError Error, string? Detail);
