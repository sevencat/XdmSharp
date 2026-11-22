namespace Xdm.Core.Downloads;

/// <summary>
/// 不可变的任务视图数据：ViewModel 只持有它，不持有引擎内部的可变对象
/// （<c>ARCHITECTURE.md</c> §8）。
/// </summary>
/// <remarks>
/// 多协议预留字段（<c>Kind</c> / <c>ParentId</c> / <c>Children</c> 语义）首期即定义，
/// 首期 <c>ParentId</c> 恒为 <see langword="null"/>，见 <c>ARCHITECTURE.md</c> §12.1。
/// </remarks>
public sealed record DownloadRecord
{
    public long Id { get; init; }

    public string FileName { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    /// <summary>目标文件夹（不含文件名）。</summary>
    public string TargetFolder { get; init; } = string.Empty;

    /// <summary>文件总字节数；未知（未拿到 <c>Content-Length</c>）时为 0。</summary>
    public long SizeBytes { get; init; }

    /// <summary>已落盘字节数（只算 fsync 过的）。</summary>
    public long SyncedBytes { get; init; }

    /// <summary>瞬时速度，字节/秒。</summary>
    public double SpeedBps { get; init; }

    /// <summary>预计剩余秒数；未知为 <see langword="null"/>。</summary>
    public int? RemainingSeconds { get; init; }

    public DownloadStatus Status { get; init; }

    /// <summary>失败原因（仅 <see cref="DownloadStatus.Failed"/> / <see cref="DownloadStatus.DiskError"/>）。</summary>
    public string? ErrorText { get; init; }

    public int SegmentCount { get; init; }

    public int CompletedSegments { get; init; }

    /// <summary>父任务 id（V2 HF / V3 BT 的多文件任务用）；首期恒为 <see langword="null"/>。</summary>
    public long? ParentId { get; init; }

    /// <summary>完成时间（用于行内"已完成 2026-11-22 10:31"）。</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>0..1 的完成度；大小未知时返回 0。</summary>
    public double Progress => SizeBytes > 0
        ? Math.Clamp((double)SyncedBytes / SizeBytes, 0, 1)
        : 0;

    public bool IsFinished => Status is DownloadStatus.Completed;

    public bool IsActive => Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Queued;
}
