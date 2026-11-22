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

    /// <summary>暂停指定任务。</summary>
    void Pause(long id);

    /// <summary>恢复指定任务。</summary>
    void Resume(long id);

    /// <summary>删除指定任务（含临时文件）。<paramref name="deleteFile"/> 为真时连已下载的文件一起删。</summary>
    void Delete(long id, bool deleteFile);
}
