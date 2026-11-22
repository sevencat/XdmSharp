using Xdm.Core.Downloads;
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
