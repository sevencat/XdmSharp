using Xdm.Core.Downloads;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Tests;

public class MainWindowViewModelTests
{
    private static DownloadRecord Record(long id, DownloadStatus status, string name, double speed = 0) => new()
    {
        Id = id,
        FileName = name,
        Url = $"https://example.com/{name}",
        TargetFolder = @"D:\Downloads",
        SizeBytes = 1000,
        SyncedBytes = status is DownloadStatus.Completed ? 1000 : 500,
        SpeedBps = speed,
        Status = status,
        SegmentCount = 4,
        CompletedSegments = status is DownloadStatus.Completed ? 4 : 2,
    };

    private static MainWindowViewModel CreateVm() => new(
    [
        Record(1, DownloadStatus.Downloading, "ubuntu.iso", speed: 2_000_000),
        Record(2, DownloadStatus.Downloading, "movie.mp4", speed: 1_000_000),
        Record(3, DownloadStatus.Paused, "book.pdf"),
        Record(4, DownloadStatus.Completed, "photo.zip"),
        Record(5, DownloadStatus.Failed, "setup.exe"),
    ]);

    [Fact]
    public void Categories_CountEverything()
    {
        var vm = CreateVm();

        Assert.Equal(5, vm.Categories[0].Count);
        Assert.Equal(2, vm.Categories[1].Count);
        Assert.Equal(1, vm.Categories[2].Count);
        Assert.Equal(1, vm.Categories[3].Count);
        Assert.Equal(5, vm.Rows.Count);
    }

    [Fact]
    public void SelectingCategory_FiltersRows()
    {
        var vm = CreateVm();

        vm.SelectedCategory = vm.Categories[2];

        Assert.Single(vm.Rows);
        Assert.Equal("photo.zip", vm.Rows[0].FileName);
        Assert.Equal("显示 1 / 共 5 个任务", vm.ListSummary);
    }

    [Fact]
    public void Search_FiltersByFileNameAndUrl()
    {
        var vm = CreateVm();

        vm.SearchText = "ubuntu";
        Assert.Single(vm.Rows);

        vm.SearchText = "setup";
        Assert.Single(vm.Rows);

        vm.SearchText = "no-such-file";
        Assert.Empty(vm.Rows);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public void PauseSelected_StopsSpeedAndUpdatesCounts()
    {
        var vm = CreateVm();
        vm.SelectedRow = vm.Rows[0];

        vm.PauseSelectedCommand.Execute(null);

        Assert.Equal(DownloadStatus.Paused, vm.SelectedRow!.Record.Status);
        Assert.Equal(1, vm.Categories[1].Count);
        Assert.Equal("1 个下载中 · 总速度 977 KB/s", vm.StatusSummary);
    }

    [Fact]
    public void ResumeSelected_RestoresDownloading()
    {
        var vm = CreateVm();
        vm.SelectedRow = vm.Rows.First(r => r.FileName == "book.pdf");

        vm.ResumeSelectedCommand.Execute(null);

        Assert.Equal(DownloadStatus.Downloading, vm.SelectedRow!.Record.Status);
    }

    [Fact]
    public void TogglePause_FlipsPausedAndActive()
    {
        var vm = CreateVm();
        var row = vm.Rows[0];

        vm.TogglePause(row);
        Assert.True(row.IsPaused);

        vm.TogglePause(row);
        Assert.False(row.IsPaused);
    }

    [Fact]
    public void DeleteSelected_RemovesRowAndRefreshes()
    {
        var vm = CreateVm();
        vm.SelectedRow = vm.Rows[4];

        vm.DeleteSelectedCommand.Execute(null);

        Assert.Equal(4, vm.Rows.Count);
        Assert.Equal(4, vm.Categories[0].Count);
        Assert.Null(vm.SelectedRow);
    }

    [Fact]
    public void UpdateRow_AppliesSnapshot()
    {
        var vm = CreateVm();
        var record = Record(1, DownloadStatus.Completed, "ubuntu.iso");

        vm.UpdateRow(record);

        var row = vm.Rows.First(r => r.Id == 1);
        Assert.True(row.IsCompleted);
        Assert.Equal("已完成", row.StatusText);
    }

    [Fact]
    public void Empty_ShowsEmptyState()
    {
        var vm = new MainWindowViewModel([]);

        Assert.True(vm.IsEmpty);
        Assert.Equal("0 个下载中 · 总速度 —", vm.StatusSummary);
    }
}
