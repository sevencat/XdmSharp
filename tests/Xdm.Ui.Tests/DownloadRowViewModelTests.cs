using Xdm.Core.Downloads;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Tests;

public class DownloadRowViewModelTests
{
    private static DownloadRecord Sample(
        DownloadStatus status = DownloadStatus.Downloading,
        long size = 100,
        long synced = 76,
        string fileName = "ubuntu-24.04.iso") => new()
    {
        Id = 1,
        FileName = fileName,
        Url = "https://example.com/ubuntu-24.04.iso",
        TargetFolder = @"D:\Downloads",
        SizeBytes = size,
        SyncedBytes = synced,
        Status = status,
        SegmentCount = 12,
        CompletedSegments = 3,
    };

    [Fact]
    public void DescribeStatus_CoversAllBranches()
    {
        Assert.Equal("下载中", DownloadRowViewModel.DescribeStatus(Sample()));
        Assert.Equal("正在连接…", DownloadRowViewModel.DescribeStatus(Sample(DownloadStatus.Connecting)));
        Assert.Equal("等待中", DownloadRowViewModel.DescribeStatus(Sample(DownloadStatus.Queued)));
        Assert.Equal("已暂停", DownloadRowViewModel.DescribeStatus(Sample(DownloadStatus.Paused)));
        Assert.Equal("已完成", DownloadRowViewModel.DescribeStatus(Sample(DownloadStatus.Completed, synced: 100)));
        Assert.Equal("失败：连接被重置", DownloadRowViewModel.DescribeStatus(
            Sample(DownloadStatus.Failed) with { ErrorText = "连接被重置" }));
        Assert.Equal("磁盘错误", DownloadRowViewModel.DescribeStatus(Sample(DownloadStatus.DiskError)));
    }

    [Fact]
    public void Row_ExposesDerivedTexts()
    {
        var row = new DownloadRowViewModel(Sample());

        Assert.Equal("76%", row.ProgressText);
        Assert.Equal("100 B", row.SizeText);
        Assert.Equal("3/12 段 · D:\\Downloads", row.SegmentAndFolderText);
        Assert.Equal("ISO", row.Extension);
        Assert.True(row.ShowProgress);
        Assert.Equal(12, row.Segments);
    }

    [Fact]
    public void Progress_OnlyNotifiesOnOnePercentSteps()
    {
        var row = new DownloadRowViewModel(Sample(synced: 0));
        Assert.Equal(0, row.Progress);

        row.Update(Sample(synced: 0) with { SyncedBytes = 0 });
        Assert.Equal(0, row.Progress);

        row.Update(Sample() with { SyncedBytes = 2 });
        Assert.Equal(0.02, row.Progress, 3);
    }

    [Fact]
    public void Update_RefreshesStatusAndHidesProgressWhenCompleted()
    {
        var row = new DownloadRowViewModel(Sample());
        row.Update(Sample(DownloadStatus.Completed, synced: 100) with { CompletedAt = DateTimeOffset.Now });

        Assert.True(row.IsCompleted);
        Assert.False(row.ShowProgress);
        Assert.Equal("已完成", row.StatusText);
    }

    [Fact]
    public void ToggleText_FollowsPausedState()
    {
        Assert.Equal("暂停", new DownloadRowViewModel(Sample()).ToggleText);
        Assert.Equal("恢复", new DownloadRowViewModel(Sample(DownloadStatus.Paused)).ToggleText);
    }

    [Fact]
    public void CompletedRow_ShowsFinishTimeInsteadOfSegments()
    {
        var record = Sample(DownloadStatus.Completed, synced: 100) with
        {
            CompletedAt = new DateTimeOffset(2026, 11, 22, 10, 31, 0, TimeSpan.FromHours(8)),
        };

        var row = new DownloadRowViewModel(record);

        Assert.StartsWith(@"D:\Downloads · 完成于 ", row.SegmentAndFolderText, StringComparison.Ordinal);
        Assert.False(row.ShowProgress);
        Assert.Equal(string.Empty, row.SpeedText);
    }

    [Fact]
    public void NoExtension_FallsBackToPlaceholder()
        => Assert.Equal("文件", new DownloadRowViewModel(Sample(fileName: "README")).Extension);
}
