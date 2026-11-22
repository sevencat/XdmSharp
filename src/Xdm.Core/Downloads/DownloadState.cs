namespace Xdm.Core.Downloads;

/// <summary>
/// <c>state/&lt;id&gt;.json</c> 的形状（<c>ARCHITECTURE.md</c> §7.2）。
/// 只记已 fsync 的字节，崩溃后回退量不超过一个刷盘周期。
/// </summary>
public sealed class DownloadState
{
    /// <summary>格式版本，为将来的迁移留口。</summary>
    public int Version { get; set; } = 1;

    /// <summary>任务 id。</summary>
    public long Id { get; set; }

    /// <summary>原始 URL（恢复时若任务被重建，用它认领）。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>文件名（探测后推导出来的值，恢复时沿用）。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>完整大小。</summary>
    public long? TotalSize { get; set; }

    /// <summary><c>If-Range</c> 校验值。</summary>
    public string? Validator { get; set; }

    /// <summary>是否不支持 Range。</summary>
    public bool NoRangeSupport { get; set; }

    /// <summary>段快照。</summary>
    public List<SegmentState> Segments { get; set; } = [];

    /// <summary>写快照时间（UTC，仅供排查）。</summary>
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>单个段的持久化形状。</summary>
public sealed class SegmentState
{
    /// <summary>段号。</summary>
    public long Id { get; set; }

    /// <summary>起点。</summary>
    public long Offset { get; set; }

    /// <summary>长度（<c>-1</c> 表示未知）。</summary>
    public long Length { get; set; }

    /// <summary>已落盘字节。</summary>
    public long Synced { get; set; }

    /// <summary>段状态。</summary>
    public SegmentStatus Status { get; set; }
}
