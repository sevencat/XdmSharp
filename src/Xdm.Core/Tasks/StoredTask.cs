using Xdm.Core.Downloads;
using Xdm.Core.Errors;
using Xdm.Core.Storage;

namespace Xdm.Core.Tasks;

/// <summary>
/// 任务表的一行（<c>ARCHITECTURE.md</c> §7.1）。插入时 <see cref="Id"/> 忽略，由任务库分配。
/// </summary>
public sealed record StoredTask
{
    public long Id { get; init; }

    /// <summary>任务种类（HTTP / HF / BT），首期恒为 <see cref="TaskKind.Http"/>。</summary>
    public TaskKind Kind { get; init; } = TaskKind.Http;

    /// <summary>父任务 id（HF 模型包 → 文件）；首期为 <see langword="null"/>。</summary>
    public long? ParentId { get; init; }

    public string Url { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string TargetFolder { get; init; } = string.Empty;

    /// <summary>临时文件目录（当前实现与目标目录相同：<c>.&lt;id&gt;.xdm-part</c> 就在目标目录）。</summary>
    public string TempFolder { get; init; } = string.Empty;

    public long? TotalSize { get; init; }

    /// <summary>已落盘字节（只算 fsync 过的）。</summary>
    public long Downloaded { get; init; }

    public DownloadStatus Status { get; init; } = DownloadStatus.Queued;

    public int MaxSegments { get; init; } = 8;

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public string? Cookie { get; init; }

    /// <summary>
    /// 任务选定的代理 id（<c>null</c> = 用建任务时的默认代理；<c>system</c>/<c>direct</c> 或具名代理 id）。
    /// 重启恢复时按当时的代理表重新解析。
    /// </summary>
    public string? ProxyId { get; init; }

    public string? Validator { get; init; }

    public bool NoRangeSupport { get; init; }

    /// <summary>V2 的 LFS sha256 / V3 的整体校验；首期为 <see langword="null"/>。</summary>
    public string? ContentSha256 { get; init; }

    /// <summary>V2/V3 的来源元数据；首期为 <see langword="null"/>。</summary>
    public string? SourceMetaJson { get; init; }

    /// <summary>失败原因；非失败态为 <see langword="null"/>。</summary>
    public DownloadError? Error { get; init; }

    public NameConflictPolicy NamePolicy { get; init; } = NameConflictPolicy.Rename;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedAt { get; init; }

    public IReadOnlyList<SegmentState> Segments { get; init; } = [];
}
