using Xdm.Core.Downloads;
using Xdm.Ui.Services;

namespace Xdm.App;

/// <summary>把界面命令转给 <see cref="DownloadManager"/>（Ui 层只认接口）。</summary>
internal sealed class UiDownloadActions(DownloadManager manager) : IDownloadActions
{
    public void Start(IReadOnlyList<DownloadRequest> requests)
    {
        foreach (var request in requests)
        {
            manager.Enqueue(request);
        }
    }

    public void Pause(long id) => manager.Pause(id);

    public void Resume(long id) => manager.Resume(id);

    public void Delete(long id, bool deleteFile) => manager.Delete(id, deleteFile);
}
