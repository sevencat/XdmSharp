using Xdm.Core.Downloads;

namespace Xdm.Ui.Services;

/// <summary>
/// 判定「任务刚刚完成」。引擎会重复推送同一状态的记录（轮询 + 事件），
/// 只有「非完成 → 完成」这一跳才该提示一次，否则同一任务会连着弹好几个气泡。
/// </summary>
/// <remarks>
/// 纯状态机、不碰 UI、不持锁：所有调用方都必须在同一个线程上（壳里是 UI 线程的批量 drain）。
/// </remarks>
public sealed class DownloadCompletionWatcher
{
    private readonly Dictionary<long, DownloadStatus> _lastStatus = new();

    /// <summary>已记录状态的任务数（仅用于诊断与测试）。</summary>
    public int Count => _lastStatus.Count;

    /// <summary>记录任务当前状态；返回 true 表示这是「刚刚完成」的那一跳。</summary>
    public bool Observe(long id, DownloadStatus status)
    {
        var wasCompleted = _lastStatus.TryGetValue(id, out var previous) && previous == DownloadStatus.Completed;
        _lastStatus[id] = status;
        return !wasCompleted && status == DownloadStatus.Completed;
    }

    /// <summary>任务被删除/移除时清理，避免 id 复用后把新任务当成「刚完成过」。</summary>
    public void Forget(long id) => _lastStatus.Remove(id);
}
