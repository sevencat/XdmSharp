using Aprillz.MewUI;
using Xdm.Core.Downloads;
using Xdm.Ui.Mew.Dialogs;
using Xdm.Ui.Services;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Mew.Tests;

/// <summary>
/// 任务详情窗口（分段时间轴）。逻辑走「拉快照 + 复核行集合」，
/// 这里直接调 <c>Refresh()</c> 断言渲染结果，不依赖消息循环。
/// </summary>
public sealed class TaskDetailsDialogTests
{
    [Fact]
    public void Segments_RenderOneRowPerSegment_WithRangeAndSynced()
    {
        var row = Row(new DownloadRecord
        {
            Id = 7,
            FileName = "model.bin",
            Url = "https://hf.test/model.bin",
            TargetFolder = @"D:\Downloads",
            SizeBytes = 800,
            SyncedBytes = 400,
            Status = DownloadStatus.Downloading,
            SegmentCount = 2,
            CompletedSegments = 1,
        });

        var actions = new FakeActions
        {
            Segments_ =
            [
                new SegmentSnapshot(0, 0, 400, 400, SegmentStatus.Finished),
                new SegmentSnapshot(1, 400, 400, 0, SegmentStatus.Downloading),
            ],
        };

        var dialog = new TaskDetailsDialog(row, actions);

        Assert.Equal(2, dialog.RowCount);
        Assert.True(VisualTree.ContainsText(dialog, "model.bin"));
        Assert.True(VisualTree.ContainsText(dialog, "分段（2）"));

        // 每行「已同步 / 段长」：第一段写满、第二段还没动。
        Assert.True(VisualTree.ContainsText(dialog, "400 B / 400 B"));
        Assert.True(VisualTree.ContainsText(dialog, "0 B / 400 B"));
    }

    [Fact]
    public void Segments_AreReused_WhenIdSetUnchanged()
    {
        var row = Row(new DownloadRecord
        {
            Id = 8,
            FileName = "a.bin",
            Url = "https://hf.test/a.bin",
            TargetFolder = @"D:\Downloads",
            SizeBytes = 100,
            Status = DownloadStatus.Downloading,
            SegmentCount = 1,
        });

        var actions = new FakeActions
        {
            Segments_ = [new SegmentSnapshot(0, 0, 100, 0, SegmentStatus.Downloading)],
        };

        var dialog = new TaskDetailsDialog(row, actions);
        Assert.Equal(1, dialog.RowCount);

        // 段没变、只有进度在走：行控件复用，数量不变。
        actions.Segments_ = [new SegmentSnapshot(0, 0, 100, 60, SegmentStatus.Downloading)];
        dialog.Refresh();
        Assert.Equal(1, dialog.RowCount);

        // 切分成两段：重建成两行。
        actions.Segments_ =
        [
            new SegmentSnapshot(0, 0, 50, 50, SegmentStatus.Finished),
            new SegmentSnapshot(1, 50, 50, 10, SegmentStatus.Downloading),
        ];
        dialog.Refresh();
        Assert.Equal(2, dialog.RowCount);
        Assert.True(VisualTree.ContainsText(dialog, "分段（2）"));
    }

    [Fact]
    public void NoSegments_ShowsEmptyHint()
    {
        var row = Row(new DownloadRecord
        {
            Id = 9,
            FileName = "queued.bin",
            Url = "https://hf.test/queued.bin",
            TargetFolder = @"D:\Downloads",
            Status = DownloadStatus.Queued,
        });

        var dialog = new TaskDetailsDialog(row, new FakeActions());

        Assert.Equal(0, dialog.RowCount);
        Assert.True(VisualTree.ContainsText(dialog, "分段（0）"));
    }

    [Fact]
    public void Container_RendersChildren_NotSegments()
    {
        var parent = Row(new DownloadRecord
        {
            Id = 20,
            FileName = "owner/name",
            Url = "https://hf.test/owner/name",
            TargetFolder = @"D:\Downloads\owner\name",
            SizeBytes = 300,
            SyncedBytes = 100,
            Status = DownloadStatus.Downloading,
            IsContainer = true,
            ChildCount = 2,
            CompletedChildren = 1,
        });

        var actions = new FakeActions
        {
            Children_ =
            [
                new DownloadRecord
                {
                    Id = 21,
                    ParentId = 20,
                    FileName = "config.json",
                    TargetFolder = @"D:\Downloads\owner\name",
                    SizeBytes = 100,
                    SyncedBytes = 100,
                    Status = DownloadStatus.Completed,
                },
                new DownloadRecord
                {
                    Id = 22,
                    ParentId = 20,
                    FileName = "model.safetensors",
                    TargetFolder = @"D:\Downloads\owner\name",
                    SizeBytes = 200,
                    SyncedBytes = 0,
                    Status = DownloadStatus.Downloading,
                },
            ],
        };

        var dialog = new TaskDetailsDialog(parent, actions);

        Assert.Equal(2, dialog.RowCount);
        Assert.True(VisualTree.ContainsText(dialog, "文件（2）"));
        Assert.True(VisualTree.ContainsText(dialog, "model.safetensors"));
        Assert.True(VisualTree.ContainsText(dialog, "config.json"));
    }

    [Fact]
    public void FailedTask_ShowsErrorStatusText()
    {
        var row = Row(new DownloadRecord
        {
            Id = 30,
            FileName = "broken.bin",
            Url = "https://hf.test/broken.bin",
            TargetFolder = @"D:\Downloads",
            Status = DownloadStatus.Failed,
            ErrorText = "HTTP 404",
        });

        var dialog = new TaskDetailsDialog(row, new FakeActions());

        Assert.True(VisualTree.ContainsText(dialog, "失败：HTTP 404"));
    }

    private static DownloadRowViewModel Row(DownloadRecord record) => new(record);

    /// <summary>只回答段/子任务查询的假实现，其余动作不该被详情窗口调到。</summary>
    private sealed class FakeActions : IDownloadActions
    {
        public IReadOnlyList<SegmentSnapshot> Segments_ { get; set; } = [];

        public IReadOnlyList<DownloadRecord> Children_ { get; set; } = [];

        public void Start(IReadOnlyList<DownloadRequest> requests) => throw new NotSupportedException();

        public void StartGroup(DownloadGroupRequest group) => throw new NotSupportedException();

        public void Pause(long id) => throw new NotSupportedException();

        public void Resume(long id) => throw new NotSupportedException();

        public void Delete(long id, bool deleteFile) => throw new NotSupportedException();

        public IReadOnlyList<SegmentSnapshot> Segments(long id) => Segments_;

        public IReadOnlyList<DownloadRecord> Children(long id) => Children_;
    }
}
