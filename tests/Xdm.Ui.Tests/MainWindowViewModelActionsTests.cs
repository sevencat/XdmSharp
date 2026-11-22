using Xdm.Core.Downloads;
using Xdm.Ui.Services;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Tests;

/// <summary>接引擎后：界面命令必须转发给 <see cref="IDownloadActions"/>，不能自己改状态骗人。</summary>
public class MainWindowViewModelActionsTests
{
    private sealed class FakeActions : IDownloadActions
    {
        public List<IReadOnlyList<DownloadRequest>> Started { get; } = [];

        public List<long> Paused { get; } = [];

        public List<long> Resumed { get; } = [];

        public List<long> Deleted { get; } = [];

        public void Start(IReadOnlyList<DownloadRequest> requests) => Started.Add(requests);

        public void Pause(long id) => Paused.Add(id);

        public void Resume(long id) => Resumed.Add(id);

        public void Delete(long id) => Deleted.Add(id);
    }

    private static DownloadRecord Record(long id, DownloadStatus status, string name) => new()
    {
        Id = id,
        FileName = name,
        Url = $"https://example.com/{name}",
        TargetFolder = @"D:\Downloads",
        SizeBytes = 1000,
        SyncedBytes = 500,
        Status = status,
        SegmentCount = 4,
        CompletedSegments = 2,
    };

    private static (MainWindowViewModel Vm, FakeActions Actions) Create()
    {
        var actions = new FakeActions();
        var vm = new MainWindowViewModel(
        [
            Record(1, DownloadStatus.Downloading, "ubuntu.iso"),
            Record(2, DownloadStatus.Paused, "book.pdf"),
        ])
        {
            Actions = actions,
        };

        return (vm, actions);
    }

    [Fact]
    public void TogglePause_OnActiveRow_AsksEngineWithoutFakingStatus()
    {
        var (vm, actions) = Create();
        var row = vm.Rows.First(r => r.Id == 1);

        vm.TogglePause(row);

        Assert.Equal([1], actions.Paused);
        Assert.Empty(actions.Resumed);
        // 状态由引擎回报，界面不抢答
        Assert.Equal(DownloadStatus.Downloading, row.Record.Status);
    }

    [Fact]
    public void TogglePause_OnPausedRow_AsksEngineToResume()
    {
        var (vm, actions) = Create();

        vm.TogglePause(vm.Rows.First(r => r.Id == 2));

        Assert.Equal([2], actions.Resumed);
        Assert.Empty(actions.Paused);
    }

    [Fact]
    public void PauseSelectedCommand_ForwardsSelection()
    {
        var (vm, actions) = Create();
        vm.SelectedRow = vm.Rows.First(r => r.Id == 1);

        vm.PauseSelectedCommand.Execute(null);

        Assert.Equal([1], actions.Paused);
    }

    [Fact]
    public void Delete_ForwardsThenRemovesRow()
    {
        var (vm, actions) = Create();
        var row = vm.Rows.First(r => r.Id == 1);

        vm.Delete(row);

        Assert.Equal([1], actions.Deleted);
        Assert.DoesNotContain(vm.Rows, r => r.Id == 1);
    }

    [Fact]
    public void RemoveById_IsIdempotent_ForEngineEvents()
    {
        var (vm, _) = Create();

        vm.RemoveById(1);
        vm.RemoveById(1);

        Assert.Single(vm.Rows);
        Assert.Equal(1, vm.Categories[0].Count);
    }

    [Fact]
    public void StartDownloads_ForwardsRequests_WithoutCreatingRows()
    {
        var (vm, actions) = Create();
        var requests = new List<DownloadRequest>
        {
            new() { Url = "https://example.com/a.bin", TargetFolder = @"D:\Downloads", FileName = "a.bin" },
        };

        vm.StartDownloads(requests);

        var started = Assert.Single(actions.Started);
        Assert.Same(requests, started);
        Assert.Equal(2, vm.Rows.Count); // 新行等管理器 TaskAdded 事件，不在这里造
    }

    [Fact]
    public void StartDownloads_WithoutActions_CreatesPlaceholderRows()
    {
        var vm = new MainWindowViewModel([]);

        vm.StartDownloads(
        [
            new DownloadRequest
            {
                Url = "https://example.com/a.bin",
                TargetFolder = @"D:\Downloads",
                FileName = "a.bin",
                MaxSegments = 8,
                StartImmediately = false,
            },
        ]);

        var row = Assert.Single(vm.Rows);
        Assert.Equal("a.bin", row.FileName);
        Assert.True(row.IsPaused);
        Assert.Equal(1, vm.Categories[0].Count);
        Assert.Equal(0, vm.Categories[1].Count);
    }

    [Fact]
    public void AddAndUpdateRow_DriveListFromManagerEvents()
    {
        var (vm, _) = Create();

        vm.Add(Record(9, DownloadStatus.Queued, "new.bin"));
        Assert.Contains(vm.Rows, r => r.Id == 9);

        vm.UpdateRow(Record(9, DownloadStatus.Downloading, "new.bin") with { SpeedBps = 4096 });
        var row = vm.Rows.First(r => r.Id == 9);
        Assert.Equal(DownloadStatus.Downloading, row.Record.Status);
        Assert.Equal("4.0 KB/s", row.SpeedText);
    }
}
