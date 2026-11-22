namespace Xdm.Core.Downloads;

/// <summary>段状态。</summary>
public enum SegmentStatus
{
    /// <summary>尚未开始（等待线程 / 等待接管）。</summary>
    Ready,

    /// <summary>正在下载。</summary>
    Downloading,

    /// <summary>已完成（<see cref="Segment.Synced"/> == <see cref="Segment.Length"/>）。</summary>
    Finished,

    /// <summary>失败。</summary>
    Failed,

    /// <summary>被取消（暂停 / 删除）。</summary>
    Cancelled,
}
