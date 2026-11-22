using Xdm.Core.Errors;

namespace Xdm.Core.Downloads;

/// <summary>段的不可变快照。UI 永远只看快照，不碰引擎可变字段（<c>ARCHITECTURE.md</c> §8）。</summary>
public readonly record struct SegmentSnapshot(long Id, long Offset, long Length, long Synced, SegmentStatus Status);

/// <summary>
/// 引擎 → 外界的通知口。回调发生在引擎线程上，实现方负责切线程/节流
/// （<c>Xdm.App</c> 的 <c>DownloadManager</c> 会 250ms 合并后 Post 到 UI 线程）。
/// </summary>
public interface IDownloadObserver
{
    /// <summary>任务状态迁移。</summary>
    void OnTaskStatusChanged(long id, DownloadStatus status);

    /// <summary>进度（已落盘总量 / 瞬时速率 / 预计剩余）。</summary>
    void OnProgress(long id, long syncedTotal, double bytesPerSecond, TimeSpan? eta);

    /// <summary>段表变化（切分 / 恢复 / 结束）。首期只在建段与暂停时发，详情窗口订阅。</summary>
    void OnSegmentsChanged(long id, IReadOnlyList<SegmentSnapshot> segments);

    /// <summary>成功并已发布。</summary>
    void OnCompleted(long id, string finalPath);

    /// <summary>失败。</summary>
    void OnFailed(long id, DownloadError error, string detail);
}
