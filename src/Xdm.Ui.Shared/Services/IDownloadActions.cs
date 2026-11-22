using Xdm.Core.Downloads;

namespace Xdm.Ui.Services;

/// <summary>
/// 界面命令到下载引擎的出口。`Xdm.App` 用 <c>DownloadManager</c> 实现，
/// 界面测试注入假实现，所以 Ui 层不认识引擎。
/// </summary>
public interface IDownloadActions
{
    /// <summary>把新建窗口收集到的请求交给管理器（管理器负责建任务、排队）。</summary>
    void Start(IReadOnlyList<DownloadRequest> requests);

    /// <summary>把多文件任务（HF 模型包）交给管理器：父行 + 每个文件一条子任务。</summary>
    void StartGroup(DownloadGroupRequest group);

    /// <summary>暂停指定任务。</summary>
    void Pause(long id);

    /// <summary>恢复指定任务。</summary>
    void Resume(long id);

    /// <summary>删除指定任务（含临时文件）。<paramref name="deleteFile"/> 为真时连已下载的文件一起删。</summary>
    void Delete(long id, bool deleteFile);

    /// <summary>
    /// 任务的段快照（进度详情窗口按 250ms 轮询）；未知 id 或还没建段时返回空。
    /// 拉模式而非事件：窗口不开就没有任何额外开销。
    /// </summary>
    IReadOnlyList<SegmentSnapshot> Segments(long id);

    /// <summary>父行（HF 模型包）下的文件子任务快照；非父行返回空。</summary>
    IReadOnlyList<DownloadRecord> Children(long id);
}
