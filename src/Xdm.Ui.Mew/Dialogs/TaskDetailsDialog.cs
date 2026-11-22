using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xdm.Core.Downloads;
using Xdm.Ui.Formatting;
using Xdm.Ui.Mew.Controls;
using Xdm.Ui.Services;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Mew.Dialogs;

/// <summary>
/// 任务进度详情（<c>UI-DESIGN.md</c> 的「分段时间轴」）：普通任务逐段显示区间与进度，
/// 多文件任务（HF 模型包）逐文件显示进度。
/// </summary>
/// <remarks>
/// 数据是**拉**的：每 250ms 通过 <see cref="IDownloadActions.Segments"/> /
/// <see cref="IDownloadActions.Children"/> 取一次快照，窗口关掉就没有任何开销。
/// 与主列表同一节拍（4Hz），不额外给引擎加事件。
/// </remarks>
public sealed class TaskDetailsDialog : Window
{
    private const double DialogWidth = 660;
    private const double DialogHeight = 520;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly DownloadRowViewModel _row;
    private readonly IDownloadActions? _actions;

    private readonly TextBlock _name = Text(15, MewTheme.TextPrimary, semiBold: true);
    private readonly TextBlock _url = Text(11, MewTheme.TextSecondary);
    private readonly TextBlock _detail = Text(11, MewTheme.TextSecondary);
    private readonly TextBlock _size = Text(11, MewTheme.TextSecondary);
    private readonly TextBlock _speed = Text(11, MewTheme.TextSecondary);
    private readonly TextBlock _remaining = Text(11, MewTheme.TextSecondary);
    private readonly TextBlock _percent = Text(11, MewTheme.TextSecondary);
    private readonly TextBlock _badgeText = Text(10, MewTheme.OnAccent);
    private readonly TextBlock _listTitle = Text(12, MewTheme.TextPrimary, semiBold: true);
    private readonly TextBlock _emptyHint = Text(11, MewTheme.TextSecondary);
    private readonly Border _badge;
    private readonly SegmentBar _bar = new();
    private readonly StackPanel _rowsPanel = new StackPanel().Spacing(2);

    private List<RowView> _rows = [];
    private long[] _keys = [];

    private DispatcherTimer? _timer;
    private Action? _tick;

    public TaskDetailsDialog(DownloadRowViewModel row, IDownloadActions? actions = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        _row = row;
        _actions = actions;

        Borderless = true;
        Title = "任务详情";
        ShowInTaskbar = false;
        StartupLocation = WindowStartupLocation.CenterOwner;
        WindowSize = WindowSize.Resizable(
            DialogWidth, DialogHeight, DialogWidth, DialogHeight, DialogWidth, DialogHeight);

        _badge = new Border()
            .Height(18)
            .Padding(6, 0)
            .Background(MewTheme.BadgeBackground)
            .Child(_badgeText)
            .CenterVertical();

        _name.TextTrimming(TextTrimming.CharacterEllipsis);
        _url.TextTrimming(TextTrimming.CharacterEllipsis);
        _detail.TextTrimming(TextTrimming.CharacterEllipsis);

        var header = new StackPanel()
            .Spacing(6)
            .Padding(16, 12, 16, 10)
            .Children(
                _name,
                _url,
                new StackPanel()
                    .Horizontal()
                    .Spacing(8)
                    .Children(_badge, _size, _speed, _remaining),
                new StackPanel()
                    .Horizontal()
                    .Spacing(8)
                    .Children(_bar.CenterVertical(), _percent.CenterVertical()),
                _detail);

        // Border.Child 只能一个元素，列表头 + 滚动区用两行 Grid（MewUI 的 Dock 附加属性实测不生效）。
        var listArea = new Grid()
            .Rows("Auto,*")
            .Children(
                new Border()
                    .Padding(16, 6, 16, 6)
                    .BorderThickness(new Thickness(0, 1, 0, 1))
                    .BorderBrush(MewTheme.Divider)
                    .Child(_listTitle)
                    .Row(0),
                new Border()
                    .Padding(16, 8, 16, 8)
                    .Child(new ScrollViewer
                    {
                        VerticalScroll = ScrollMode.Auto,
                        HorizontalScroll = ScrollMode.Disabled,
                        Content = new StackPanel().Spacing(6).Children(_rowsPanel, _emptyHint),
                    })
                    .Row(1));

        var footer = new StackPanel()
            .Horizontal()
            .HorizontalAlignment(HorizontalAlignment.Right)
            .Padding(16, 8, 16, 12)
            .Children(new Button()
                .Content("关闭")
                .FontSize(12)
                .Width(84)
                .Height(28)
                .OnClick(Close));

        Content = new Border()
            .Background(MewTheme.WindowBackground)
            .BorderThickness(new Thickness(1))
            .BorderBrush(MewTheme.Divider)
            .Child(new Grid()
                .Rows($"{MewTheme.TitleBarHeight},Auto,*,Auto")
                .Children(
                    TitleBarChrome.Build(this, "任务详情", Close).Row(0),
                    header.Row(1),
                    listArea.Row(2),
                    footer.Row(3)));

        Closed += StopAutoRefresh;
        Refresh();
    }

    /// <summary>模态显示；定时刷新只在真实窗口里起（测试里没有消息循环）。</summary>
    public static void Show(Window owner, DownloadRowViewModel row, IDownloadActions? actions = null)
    {
        var dialog = new TaskDetailsDialog(row, actions);
        dialog.StartAutoRefresh();
        dialog.ShowDialog(owner);
    }

    /// <summary>当前渲染出来的段/文件行数（测试断言用）。</summary>
    internal int RowCount => _rows.Count;

    /// <summary>拉一次快照并重绘（<see cref="Show"/> 之前也会调一次，避免闪一帧空白）。</summary>
    internal void Refresh()
    {
        RefreshHeader();

        if (_row.IsContainer)
        {
            RefreshChildren();
        }
        else
        {
            RefreshSegments();
        }
    }

    private void StartAutoRefresh()
    {
        if (_timer is not null)
        {
            return;
        }

        // DispatcherTimer 必须属于消息循环线程：由 Application.Run 之后再调用（见 MainWindow.ShowDetails）。
        _tick ??= Refresh;
        _timer = new DispatcherTimer(RefreshInterval);
        _timer.Tick += _tick;
        _timer.Start();
    }

    private void StopAutoRefresh()
    {
        if (_timer is { } timer)
        {
            timer.Stop();
            if (_tick is not null)
            {
                timer.Tick -= _tick;
            }

            _timer = null;
        }
    }

    private void RefreshHeader()
    {
        _name.Text = _row.FileName;
        _url.Text = _row.Url;
        _size.Text = _row.SizeText;
        _speed.Text = _row.SpeedText;
        _remaining.Text = _row.RemainingText;
        _detail.Text = _row.SegmentAndFolderText;
        _percent.Text = _row.ProgressText;
        _badgeText.Text = _row.StatusText;
        _badge.Background = _row.IsCompleted
            ? MewTheme.Success
            : _row.IsFailed
                ? MewTheme.Error
                : MewTheme.BadgeBackground;

        _bar.Segments = Math.Max(1, _row.Segments);
        _bar.Value = _row.Progress;
        _bar.Fill = _row.IsFailed ? MewTheme.Error : MewTheme.Progress;
        _bar.IsVisible = _row.ShowProgress;
    }

    private void RefreshSegments()
    {
        var segments = _actions?.Segments(_row.Id) ?? [];
        _listTitle.Text = $"分段（{segments.Count}）";

        if (!ApplyKeys([.. segments.Select(static s => s.Id)]))
        {
            _emptyHint.Text = "还没有分段：任务排队或尚未开始。";
            return;
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var view = _rows[i];
            view.Head.Text = segment.Id.ToString();
            view.Range.Text = DescribeRange(segment);
            view.Detail.Text = $"{TextFormat.Bytes(segment.Synced)} / {TextFormat.Bytes(segment.Length)}";
            view.Status.Text = DescribeSegment(segment.Status);
            view.Bar.Segments = 1;
            view.Bar.Value = segment.Length > 0 ? (double)segment.Synced / segment.Length : 0;
            view.Bar.Fill = segment.Status == SegmentStatus.Failed ? MewTheme.Error : MewTheme.Progress;
        }
    }

    private void RefreshChildren()
    {
        var children = _actions?.Children(_row.Id) ?? [];
        _listTitle.Text = $"文件（{children.Count}）";

        if (!ApplyKeys([.. children.Select(static c => c.Id)]))
        {
            _emptyHint.Text = "没有子任务：父行创建后没有生成文件任务。";
            return;
        }

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var view = _rows[i];
            view.Head.Text = (i + 1).ToString();
            view.Range.Text = child.FileName;
            view.Detail.Text = $"{TextFormat.Bytes(child.SyncedBytes)} / {TextFormat.Bytes(child.SizeBytes)}";
            view.Status.Text = DownloadRowViewModel.DescribeStatus(child);
            view.Bar.Segments = 1;
            view.Bar.Value = child.Progress;
            view.Bar.Fill = child.Status is DownloadStatus.Failed or DownloadStatus.DiskError
                ? MewTheme.Error
                : MewTheme.Progress;
        }
    }

    /// <summary>
    /// 段/文件集合变了才重建控件（切分、删除子任务）；只是进度在走就复用已有控件。
    /// 返回 <see langword="false"/> 表示现在没有可渲染的行。
    /// </summary>
    private bool ApplyKeys(long[] keys)
    {
        var hasRows = keys.Length > 0;
        _emptyHint.IsVisible = !hasRows;
        _rowsPanel.IsVisible = hasRows;
        if (!hasRows)
        {
            if (_rows.Count > 0)
            {
                _rowsPanel.Clear();
                _rows = [];
                _keys = [];
            }

            return false;
        }

        if (!keys.AsSpan().SequenceEqual(_keys))
        {
            _rowsPanel.Clear();
            _rows = new List<RowView>(keys.Length);
            foreach (var key in keys)
            {
                var view = CreateRow();
                _rows.Add(view);
                _rowsPanel.Children(view.Root);
            }

            _keys = keys;
        }

        return true;
    }

    private static RowView CreateRow()
    {
        var head = Text(10, MewTheme.TextSecondary);
        var range = Text(11, MewTheme.TextPrimary);
        var detail = Text(10, MewTheme.TextSecondary);
        var status = Text(10, MewTheme.TextSecondary);
        var bar = new SegmentBar();

        range.TextTrimming(TextTrimming.CharacterEllipsis);
        detail.TextTrimming(TextTrimming.CharacterEllipsis);

        var root = new Grid()
            .Columns("26,180,*,86")
            .Padding(0, 3)
            .Children(
                head.Column(0).CenterVertical(),
                range.Column(1).CenterVertical(),
                new StackPanel()
                    .Horizontal()
                    .Spacing(8)
                    .Children(bar.CenterVertical(), detail.CenterVertical())
                    .Column(2),
                status.Column(3).CenterVertical());

        return new RowView
        {
            Head = head,
            Range = range,
            Detail = detail,
            Status = status,
            Bar = bar,
            Root = root,
        };
    }

    private static string DescribeRange(SegmentSnapshot segment)
    {
        var end = segment.Length >= 0 ? segment.Offset + segment.Length : segment.Offset;
        return segment.Length >= 0
            ? $"{TextFormat.Bytes(segment.Offset)} – {TextFormat.Bytes(end)}"
            : $"从 {TextFormat.Bytes(segment.Offset)} 起（长度未知）";
    }

    private static string DescribeSegment(SegmentStatus status) => status switch
    {
        SegmentStatus.Ready => "等待",
        SegmentStatus.Downloading => "下载中",
        SegmentStatus.Finished => "完成",
        SegmentStatus.Failed => "失败",
        _ => "已取消",
    };

    private static TextBlock Text(double size, Color color, bool semiBold = false)
    {
        var block = new TextBlock().FontSize(size).Foreground(color);
        return semiBold ? block.FontWeight(FontWeight.SemiBold) : block;
    }

    /// <summary>一行（段或文件）的控件组：重建时一起换掉。</summary>
    private sealed class RowView
    {
        public TextBlock Head { get; init; } = null!;

        public TextBlock Range { get; init; } = null!;

        public TextBlock Detail { get; init; } = null!;

        public TextBlock Status { get; init; } = null!;

        public SegmentBar Bar { get; init; } = null!;

        public Grid Root { get; init; } = null!;
    }
}
