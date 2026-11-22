using Xdm.Core.Downloads;
using Xdm.Ui.Services;

namespace Xdm.Ui.Mew.Tests;

/// <summary>
/// 「刚完成」判定的纯逻辑测试（实现放在 <c>Xdm.Ui.Shared</c>，壳里用它决定要不要弹气泡）。
/// </summary>
public sealed class DownloadCompletionWatcherTests
{
    [Fact]
    public void 首次完成返回真()
    {
        var watcher = new DownloadCompletionWatcher();

        Assert.True(watcher.Observe(1, DownloadStatus.Completed));
    }

    [Fact]
    public void 重复推送完成只报一次()
    {
        var watcher = new DownloadCompletionWatcher();

        Assert.True(watcher.Observe(1, DownloadStatus.Completed));
        Assert.False(watcher.Observe(1, DownloadStatus.Completed));
        Assert.False(watcher.Observe(1, DownloadStatus.Completed));
    }

    [Fact]
    public void 下载中转到完成返回真()
    {
        var watcher = new DownloadCompletionWatcher();

        Assert.False(watcher.Observe(1, DownloadStatus.Downloading));
        Assert.True(watcher.Observe(1, DownloadStatus.Completed));
    }

    [Fact]
    public void 完成后重新下载再完成仍会报()
    {
        var watcher = new DownloadCompletionWatcher();

        Assert.True(watcher.Observe(1, DownloadStatus.Completed));
        Assert.False(watcher.Observe(1, DownloadStatus.Downloading));
        Assert.True(watcher.Observe(1, DownloadStatus.Completed));
    }

    [Fact]
    public void 其他状态不触发()
    {
        var watcher = new DownloadCompletionWatcher();

        Assert.False(watcher.Observe(1, DownloadStatus.Queued));
        Assert.False(watcher.Observe(1, DownloadStatus.Failed));
        Assert.False(watcher.Observe(1, DownloadStatus.Paused));
    }

    [Fact]
    public void 不同任务各算各的()
    {
        var watcher = new DownloadCompletionWatcher();

        Assert.True(watcher.Observe(1, DownloadStatus.Completed));
        Assert.True(watcher.Observe(2, DownloadStatus.Completed));
        Assert.False(watcher.Observe(1, DownloadStatus.Completed));
    }

    [Fact]
    public void 忘记任务后重来算首次()
    {
        var watcher = new DownloadCompletionWatcher();

        Assert.True(watcher.Observe(7, DownloadStatus.Completed));
        watcher.Forget(7);
        Assert.Equal(0, watcher.Count);
        Assert.True(watcher.Observe(7, DownloadStatus.Completed));
    }
}
