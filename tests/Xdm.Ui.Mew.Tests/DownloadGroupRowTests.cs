using Xdm.Core.Downloads;
using Xdm.Ui.Samples;
using Xdm.Ui.Services;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Mew.Tests;

/// <summary>
/// 多文件任务在列表里的呈现（<c>UI-DESIGN.md</c> §4）：只出父行，第二行显示「N 个文件」，
/// 子行不单独占一行（引擎把它们汇总进父行的 <see cref="DownloadRecord"/>）。
/// </summary>
public sealed class DownloadGroupRowTests
{
    private static DownloadRecord Parent(DownloadStatus status = DownloadStatus.Downloading)
        => new()
        {
            Id = 10,
            FileName = "owner/name",
            Url = "https://hf.test/owner/name",
            TargetFolder = @"D:\Downloads\owner\name",
            SizeBytes = 4_000,
            SyncedBytes = 1_000,
            Status = status,
            IsContainer = true,
            ChildCount = 4,
            CompletedChildren = 1,
        };

    private static DownloadRecord Child(long parentId = 10)
        => new()
        {
            Id = 11,
            FileName = "model.onnx",
            Url = "https://hf.test/owner/name/resolve/main/model.onnx",
            TargetFolder = @"D:\Downloads\owner\name",
            SizeBytes = 1_000,
            SyncedBytes = 1_000,
            Status = DownloadStatus.Completed,
            SegmentCount = 1,
            CompletedSegments = 1,
            ParentId = parentId,
        };

    [Fact]
    public void ChildRows_DoNotGetTheirOwnListRow()
    {
        var viewModel = new MainWindowViewModel([Parent(), Child()]);

        var row = Assert.Single(viewModel.Rows);
        Assert.True(row.IsContainer);
        Assert.True(row.IsParent);
    }

    [Fact]
    public void ContainerRow_ShowsFileCountInsteadOfSegments()
    {
        var viewModel = new MainWindowViewModel([Parent()]);

        var row = Assert.Single(viewModel.Rows);
        Assert.Equal("1/4 个文件 · D:\\Downloads\\owner\\name", row.SegmentAndFolderText);
        Assert.Equal("包", row.Extension);
        Assert.Equal(0.25, row.Progress, 3);
    }

    [Fact]
    public void ContainerRow_Completed_ShowsPhaseAndCompletion()
    {
        var completed = Parent(DownloadStatus.Completed) with
        {
            SyncedBytes = 4_000,
            CompletedChildren = 4,
            CompletedAt = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(8)),
        };
        var viewModel = new MainWindowViewModel([completed]);

        var row = Assert.Single(viewModel.Rows);
        Assert.True(row.IsCompleted);
        Assert.Contains("完成于", row.SegmentAndFolderText);
        Assert.Equal("已完成", row.StatusText);
    }

    [Fact]
    public void ToggleExpand_InsertsChildRows_AfterParent()
    {
        var viewModel = Expanded(ChildAt(11, "model.onnx"), ChildAt(12, "tokenizer.json"));

        var parent = viewModel.Rows[0];
        viewModel.ToggleExpand(parent);

        Assert.True(parent.IsExpanded);
        Assert.Equal(new long[] { 10, 11, 12 }, viewModel.Rows.Select(r => r.Id).ToArray());
        Assert.True(viewModel.Rows[1].IsChild);
        Assert.True(viewModel.Rows[2].IsChild);
        Assert.Equal("tokenizer.json", viewModel.Rows[2].FileName);

        // 「显示 N」只数父行：展开出来的子文件不是独立任务。
        Assert.Equal("显示 1 / 共 1 个任务", viewModel.ListSummary);
    }

    [Fact]
    public void ToggleExpand_Again_CollapsesChildRows()
    {
        var viewModel = Expanded(ChildAt(11, "model.onnx"));
        viewModel.ToggleExpand(viewModel.Rows[0]);
        viewModel.ToggleExpand(viewModel.Rows[0]);

        var row = Assert.Single(viewModel.Rows);
        Assert.Equal(10, row.Id);
        Assert.False(row.IsExpanded);
    }

    [Fact]
    public void ToggleExpand_IgnoresPlainRow()
    {
        var plain = Parent() with { Id = 20, FileName = "ubuntu.iso", IsContainer = false, ChildCount = 0 };
        var viewModel = new MainWindowViewModel([plain]) { Actions = new FakeActions([]) };
        var row = Assert.Single(viewModel.Rows);

        viewModel.ToggleExpand(row);

        Assert.False(row.IsExpanded);
        Assert.Single(viewModel.Rows);
    }

    [Fact]
    public void ExpandedChildren_SurviveCategorySwitchAndSearch()
    {
        var viewModel = Expanded(ChildAt(11, "model.onnx"));
        viewModel.ToggleExpand(viewModel.Rows[0]);

        // 父行正在下载 → 落在「正在下载」分类；换分类会重建 Rows。
        viewModel.SelectedCategory = viewModel.Categories[1];
        Assert.Equal(2, viewModel.Rows.Count);
        Assert.True(viewModel.Rows[1].IsChild);

        // 搜索词命中父行（owner/name）时子行跟着出来；命中不了就整段消失。
        viewModel.SearchText = "owner";
        Assert.Equal(2, viewModel.Rows.Count);
        viewModel.SearchText = "model.onnx";
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public void ExpandedChild_FollowsEngineUpdates()
    {
        var viewModel = Expanded(ChildAt(11, "model.onnx"));
        viewModel.ToggleExpand(viewModel.Rows[0]);

        viewModel.UpdateRow(ChildAt(11, "model.onnx") with
        {
            SizeBytes = 1_000,
            SyncedBytes = 500,
            Status = DownloadStatus.Downloading,
            SpeedBps = 2_000,
        });

        var child = viewModel.Rows[1];
        Assert.Equal(0.5, child.Progress, 3);
        Assert.Equal("下载中", child.StatusText);
        Assert.Equal("2.0 KB/s", child.SpeedText);
    }

    [Fact]
    public void RemoveById_RemovesExpandedChildRow()
    {
        var viewModel = Expanded(ChildAt(11, "a.bin"), ChildAt(12, "b.bin"));
        viewModel.ToggleExpand(viewModel.Rows[0]);

        viewModel.RemoveById(11);

        Assert.Equal(new long[] { 10, 12 }, viewModel.Rows.Select(r => r.Id).ToArray());
        Assert.True(viewModel.Rows[0].IsExpanded);
    }

    [Fact]
    public void DeleteParent_DropsExpandedChildren()
    {
        var viewModel = Expanded(ChildAt(11, "a.bin"));
        viewModel.ToggleExpand(viewModel.Rows[0]);

        viewModel.Delete(viewModel.Rows[0]);

        Assert.Empty(viewModel.Rows);
        Assert.Null(viewModel.SelectedRow);
    }

    [Fact]
    public void DemoData_HasExpandableGroup()
    {
        // XDM_DEMO 的演示数据里留了一个可展开的模型包，用于肉眼验收行首三角。
        var viewModel = new MainWindowViewModel(SampleDownloads.Create(6))
        {
            DemoChildren = SampleDownloads.Children,
        };

        var group = Assert.Single(viewModel.Rows, r => r.CanExpand);
        Assert.Equal(SampleDownloads.GroupId, group.Id);

        viewModel.ToggleExpand(group);

        Assert.Equal(4, viewModel.Rows.Count(r => r.IsChild));
        Assert.Equal(4, group.Record.ChildCount);
        Assert.All(viewModel.Rows.Where(r => r.IsChild), r => Assert.Equal(SampleDownloads.GroupId, r.Record.ParentId));
    }

    private static MainWindowViewModel Expanded(params DownloadRecord[] children)
        => new([Parent()]) { Actions = new FakeActions(children) };

    private static DownloadRecord ChildAt(long id, string name)
        => Child() with { Id = id, FileName = name };

    /// <summary>只回答「父行有哪些子文件」的假实现；别的动作记下来供断言。</summary>
    private sealed class FakeActions(IReadOnlyList<DownloadRecord> children) : IDownloadActions
    {
        public List<long> Deleted { get; } = [];

        public IReadOnlyList<DownloadRecord> Children(long id) => id == 10 ? children : [];

        public IReadOnlyList<SegmentSnapshot> Segments(long id) => [];

        public void Start(IReadOnlyList<DownloadRequest> requests) => throw new NotSupportedException();

        public void StartGroup(DownloadGroupRequest group) => throw new NotSupportedException();

        public void Pause(long id) => throw new NotSupportedException();

        public void Resume(long id) => throw new NotSupportedException();

        public void Delete(long id, bool deleteFile) => Deleted.Add(id);
    }

    [Fact]
    public void StartGroup_WithoutEngine_AddsOneAggregatedRow()
    {
        var viewModel = new MainWindowViewModel([]);
        var folder = Path.Combine(Path.GetTempPath(), "xdm-hf-row");
        var group = new DownloadGroupRequest
        {
            Name = "owner/name",
            Url = "https://hf.test/owner/name",
            TargetFolder = folder,
            Kind = TaskKind.HuggingFace,
            Files =
            [
                new DownloadRequest { Url = "https://hf.test/a", TargetFolder = folder, FileName = "a.bin" },
                new DownloadRequest { Url = "https://hf.test/b", TargetFolder = folder, FileName = "b.bin" },
            ],
        };

        viewModel.StartGroup(group);

        var row = Assert.Single(viewModel.Rows);
        Assert.Equal("owner/name", row.FileName);
        Assert.True(row.IsContainer);
        Assert.Contains("0/2 个文件", row.SegmentAndFolderText);
    }
}
