using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Platform;
using Aprillz.MewUI.Rendering;
using Xdm.Core.Configuration;
using Xdm.Core.Downloads;
using Xdm.Ui.Mew.Controls;
using Xdm.Ui.Mew.Dialogs;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Mew;

/// <summary>
/// MewUI 主窗口（对应 Avalonia 版 <c>Views/MainWindow.axaml</c>）。结构一致：自绘标题栏 /
/// 工具栏 / 分类栏 / 任务列表 / 状态栏；数据来自共享的 <see cref="MainWindowViewModel"/>。
/// MewUI 没有 XAML，整棵控件树在这里用链式 API 搭。
/// </summary>
public sealed class MainWindow : Window
{
    private const double DefaultWidth = 1000;
    private const double DefaultHeight = 620;
    private const double MinimumWidth = 820;
    private const double MinimumHeight = 500;

    // 右键菜单项只能挂 Command（没有 OnClick），这几个是行级语义命令，处理器在 ctor 里注册。
    private static readonly Command RowTogglePauseCommand = new("xdm.row.togglePause", "暂停 / 继续");
    private static readonly Command RowDetailsCommand = new("xdm.row.details", "任务详情…");
    private static readonly Command RowOpenFolderCommand = new("xdm.row.openFolder", "打开所在文件夹");
    private static readonly Command RowCopyUrlCommand = new("xdm.row.copyUrl", "复制下载链接");
    private static readonly Command RowDeleteCommand = new("xdm.row.delete", "删除任务");

    private readonly MainWindowViewModel _vm;

    // 可视行 → 模板控件组；行属性变化时按这个映射就地重刷（容器回收/复用时改写）。
    private readonly Dictionary<DownloadRowViewModel, RowBinding> _rowBindings = [];

    private AppSettings _settings = new();

    private ListBox _taskList = null!;
    private ListBox _categoryList = null!;
    private TextBox _searchBox = null!;
    private TextBlock _emptyHint = null!;
    private TextBlock _statusSummary = null!;
    private TextBlock _listSummary = null!;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _vm = viewModel;

        Borderless = true;
        Title = "XDM Sharp";

        // WindowSize 全程是 DIP（S2 踩坑：按 DPI 乘成物理像素会让窗口大出一倍、内容按 1.75 倍
        // 渲染后右侧整块跑到屏幕外）。交给框架自己按 DPI 换算并居中。
        WindowSize = WindowSize.Resizable(DefaultWidth, DefaultHeight, MinimumWidth, MinimumHeight, double.MaxValue, double.MaxValue);
        StartupLocation = WindowStartupLocation.CenterScreen;

        AllowDrop = true;
        DragOver += OnWindowDragOver;
        Drop += OnWindowDrop;

        // 删除前确认（与 Avalonia 版一致：未完成的任务提示临时文件，已完成的可选连带删文件）。
        _vm.ConfirmDelete = ConfirmDeleteAsync;

        Content = BuildRoot();
        RegisterRowCommands();

        PreviewKeyDown += OnPreviewKeyDown;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Closing += OnClosingConfirmed;
    }

    /// <summary>关闭窗口前是否弹确认（组合根设为 true；无头验证可保持 false 以免挂住）。</summary>
    public bool ConfirmOnExit { get; set; }

    private bool _exitConfirmed;
    private bool _exitDialogOpen;

    private void OnClosingConfirmed(ClosingEventArgs e)
    {
        if (!ConfirmOnExit || _exitConfirmed)
        {
            return;
        }

        // 先取消这次关闭，等用户确认后再以「已确认」身份重入。
        e.Cancel = true;
        if (_exitDialogOpen)
        {
            return;
        }

        _exitDialogOpen = true;
        try
        {
            var active = _vm.ActiveCount;
            var message = active > 0
                ? $"还有 {active} 个任务正在下载。\n退出后进度会保存，下次启动可继续。\n\n确定退出？"
                : "确定退出 XDM Sharp？";

            if (!ConfirmDialog.Show(this, "退出", message, "退出", "取消").Confirmed)
            {
                return;
            }

            _exitConfirmed = true;
            Close();
        }
        finally
        {
            _exitDialogOpen = false;
        }
    }

    private void SubscribeViewModel()
    {
        RefreshSummaryText();

        // 分类计数没有绑定引擎可用：条目只在 bind 时取一次值，计数变了得重建 ItemsView 才会刷新。
        foreach (var category in _vm.Categories)
        {
            category.PropertyChanged += OnCategoryPropertyChanged;
        }

        // 行属性（暂停/进度/速度）变化不会触发模板重绑，得自己订阅后重刷可视行。
        foreach (var row in _vm.Rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }

        _vm.Rows.CollectionChanged += OnRowsChanged;
    }

    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (DownloadRowViewModel row in e.OldItems)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
                _rowBindings.Remove(row);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (DownloadRowViewModel row in e.NewItems)
            {
                row.PropertyChanged += OnRowPropertyChanged;
            }
        }
    }

    private void OnCategoryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CategoryViewModel.Count))
        {
            RefreshCategoryItems();
        }
    }

    private void RefreshCategoryItems()
    {
        var selected = _vm.SelectedCategory;
        _categoryList.ItemsSource(ItemsView.Create(_vm.Categories));
        _categoryList.SelectedItem = selected;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsEmpty):
                _emptyHint.IsVisible = _vm.IsEmpty;
                break;
            case nameof(MainWindowViewModel.StatusSummary):
                _statusSummary.Text = _vm.StatusSummary;
                break;
            case nameof(MainWindowViewModel.ListSummary):
                _listSummary.Text = _vm.ListSummary;
                break;
        }
    }

    private void RefreshSummaryText()
    {
        _statusSummary.Text = _vm.StatusSummary;
        _listSummary.Text = _vm.ListSummary;
        _emptyHint.IsVisible = _vm.IsEmpty;
    }

    private Element BuildRoot()
    {
        // 用 Grid 而不是 DockPanel：Dock 附加属性在这里不生效（实测子元素全部铺满），
        // 行列定位可以确定的预测（S2 踩坑，见 PLAN-MEWUI.md）。
        var grid = new Grid()
            .Rows("30,40,*,24")
            .Columns("180,*");

        grid.Children(
            BuildTitleBar().Row(0).Column(0).ColumnSpan(2),
            BuildToolbar().Row(1).Column(0).ColumnSpan(2),
            BuildCategoryPane().Row(2).Column(0),
            BuildTaskArea().Row(2).Column(1),
            BuildStatusBar().Row(3).Column(0).ColumnSpan(2));

        SubscribeViewModel();
        return new Border().Background(MewTheme.WindowBackground).Child(grid);
    }

    // ── 标题栏 ───────────────────────────────────────────────────────────────

    private Border BuildTitleBar()
    {
        // 图标直接复用 Avalonia 版的透明 logo（嵌入本程序集，发布时随 exe 走）。
        var logo = new Image()
            .SourceResource(typeof(MainWindow).Assembly, "Xdm.Ui.Mew.Assets.xdm-logo.png")
            .Width(18)
            .Height(18)
            .CenterVertical();

        var caption = new TextBlock()
            .Text("XDM Sharp")
            .FontSize(12)
            .Foreground(MewTheme.TitleBarForeground)
            .CenterVertical();

        caption.FontWeight = FontWeight.SemiBold;

        var left = new StackPanel()
            .Horizontal()
            .Spacing(8)
            .Children(logo, caption)
            .CenterVertical();

        _maxGlyph = Glyph(MaximizeGeometry);

        var buttons = new StackPanel()
            .Horizontal()
            .Spacing(0)
            .Children(
                CaptionButton(Glyph(MinimizeGeometry), () => WindowState = WindowState.Minimized, isClose: false),
                CaptionButton(_maxGlyph, ToggleMaximize, isClose: false),
                CaptionButton(Glyph(CloseGeometry), Close, isClose: true));

        var bar = new Border()
            .Height(MewTheme.TitleBarHeight)
            .Background(MewTheme.TitleBarBackground)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .BorderBrush(MewTheme.TitleBarBorder)
            .Padding(12, 0)
            .Child(new Grid()
                .Columns("*,Auto")
                .Children(left.Column(0), buttons.Column(1)));

        // 最大化/还原时换字形；SizeChanged 比 WindowState 赋值可靠（双击标题栏也走这里）。
        bar.SizeChanged += _ => UpdateMaxGlyph();

        // 自绘标题栏拖动：MewUI 没有 DragMove 的可靠行为，交给窗口管理器当标题栏处理
        // （双击最大化、贴边吸附随之免费获得，S1 已验证）。
        bar.MouseDown += _ => BeginWindowDrag();
        return bar;
    }

    // 窗钮字形用矢量路径（1px 描边），避免用字体符号导致不同字体下粗细/位置漂移。
    private const string MinimizeGeometry = "M 0,5 L 10,5";

    private const string MaximizeGeometry = "M 0.5,0.5 L 9.5,0.5 L 9.5,9.5 L 0.5,9.5 Z";

    private const string RestoreGeometry = "M 2.5,0.5 L 9.5,0.5 L 9.5,7.5 M 0.5,2.5 L 7.5,2.5 L 7.5,9.5 L 0.5,9.5 Z";

    private const string CloseGeometry = "M 0.5,0.5 L 9.5,9.5 M 9.5,0.5 L 0.5,9.5";

    private PathShape? _maxGlyph;

    private static PathShape Glyph(string geometry)
    {
        var glyph = new PathShape()
            .Width(10)
            .Height(10)
            .CenterHorizontal()
            .CenterVertical();
        glyph.Data = PathGeometry.Parse(geometry);
        glyph.Stroke = MewTheme.TitleBarForeground;
        glyph.StrokeThickness = 1;
        return glyph;
    }

    private static Border CaptionButton(PathShape glyph, Action onClick, bool isClose)
    {
        // 用 Border 而不是 Button：Button 的背景由默认样式模板绘制，Background 属性压不住，
        // 标题栏按钮需要和标题栏同色、只在 hover 时浮出色块。
        var button = new Border()
            .Width(44)
            .Height(MewTheme.TitleBarHeight)
            .Child(glyph);

        button.Background = MewTheme.TitleBarBackground;
        button.MouseEnter += () =>
            button.Background = isClose ? MewTheme.TitleBarCloseHover : MewTheme.TitleBarHover;
        button.MouseLeave += () => button.Background = MewTheme.TitleBarBackground;

        // 用 MouseDown 而不是 MouseUp：Border 没有按钮那样的指针捕获，MouseUp 在拖出后不可靠。
        button.MouseDown += e =>
        {
            if (e.Button == MouseButton.Left)
            {
                onClick();
            }
        };

        return button;
    }

    private void BeginWindowDrag() => WindowDrag.Begin(Handle);

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaxGlyph()
    {
        if (_maxGlyph is not null)
        {
            _maxGlyph.Data = PathGeometry.Parse(
                WindowState == WindowState.Maximized ? RestoreGeometry : MaximizeGeometry);
        }
    }

    // ── 工具栏 ───────────────────────────────────────────────────────────────

    private Border BuildToolbar()
    {
        _searchBox = EditBox.Create("搜索任务").Width(260);

        // 清空按钮只在有输入时出现（对齐 Avalonia 版搜索框的 ✕ 行为）。
        var clearSearch = new Button()
            .Content("✕")
            .FontSize(11)
            .Width(24)
            .Height(24)
            .IsVisible(false)
            .OnClick(() => _searchBox.Text = string.Empty);

        _searchBox.OnTextChanged(text =>
        {
            _vm.SearchText = text;
            clearSearch.IsVisible = !string.IsNullOrEmpty(text);
        });

        var searchArea = new StackPanel()
            .Horizontal()
            .Spacing(4)
            .Children(_searchBox, clearSearch);

        // 「恢复 / 暂停 / 删除」走共享层命令：桥接后按钮可用性跟随 CanExecute（没选中行时置灰）。
        var resume = ToolbarButton("恢复");
        resume.Command = MewCommandBridge.Connect(this, _vm.ResumeSelectedCommand, "xdm.toolbar.resume", "恢复");

        var pause = ToolbarButton("暂停");
        pause.Command = MewCommandBridge.Connect(this, _vm.PauseSelectedCommand, "xdm.toolbar.pause", "暂停");

        var delete = ToolbarButton("删除");
        delete.Command = MewCommandBridge.Connect(this, _vm.DeleteSelectedCommand, "xdm.toolbar.delete", "删除");

        var left = new StackPanel()
            .Horizontal()
            .Spacing(6)
            .Children(ToolbarButton("新建", OnNewDownload), resume, pause, delete);

        var right = new StackPanel()
            .Horizontal()
            .Spacing(6)
            .Children(
                ToolbarButton("设置", OnSettings),
                ToolbarButton("关于", () => AboutDialog.ShowAbout(this)),
                ToolbarButton("退出", Close));

        return new Border()
            .Height(MewTheme.ToolbarHeight)
            .Padding(8, 0)
            .Background(MewTheme.SurfaceBackground)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .BorderBrush(MewTheme.Divider)
            .Child(new Grid()
                .Columns("Auto,*,Auto")
                .Children(
                    left.Column(0).CenterVertical(),
                    searchArea.Column(1).CenterHorizontal().CenterVertical(),
                    right.Column(2).CenterVertical()));
    }

    private static Button ToolbarButton(string text, Action? onClick = null)
    {
        var button = new Button()
            .Content(text)
            .FontSize(12)
            .Height(26);

        if (onClick is not null)
        {
            button.OnClick(onClick);
        }

        return button;
    }

    /// <summary>
    /// 弹「新建下载」并预填链接。工具栏「新建」、命令行 URL、第二个实例转发都走这一条路径。
    /// </summary>
    /// <param name="initialUrl">预填的链接；空则等同普通新建。</param>
    public void BeginNewDownload(string? initialUrl = null)
    {
        var result = NewDownloadDialog.Show(
            this,
            initialUrl,
            _vm.DefaultSaveFolder,
            _vm.DefaultSegments,
            _vm.ProxyChoices,
            _settings.HuggingFaceEndpoint,
            _settings.HuggingFaceToken,
            hfProxy: ProxyResolver.Resolve(_settings, null),
            hfProxyResolver: proxyId => ProxyResolver.Resolve(_settings, proxyId));
        if (result is null)
        {
            return;
        }

        if (result.Group is { } group)
        {
            _vm.StartGroup(group);
            ShowNotice($"已新建 {group.Name}（{group.Files.Count} 个文件）");
            return;
        }

        if (result.Requests is { Count: > 0 } requests)
        {
            _vm.StartDownloads(requests);
            ShowNotice($"已新建 {requests.Count} 个任务");
        }
    }

    private void OnNewDownload() => BeginNewDownload();

    /// <summary>弹出任务详情（分段时间轴 / 多文件子任务列表）。</summary>
    private void ShowDetails(DownloadRowViewModel row) => TaskDetailsDialog.Show(this, row, _vm.Actions);

    private void ShowNotice(string text) => Title = $"XDM Sharp — {text}";

    // ── 设置 ─────────────────────────────────────────────────────────────────

    /// <summary>当前设置；壳层（<c>Xdm.App.Mew</c>）接手任务库后从 <c>ISettingsStore</c> 回填。</summary>
    public AppSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            _vm.DefaultSaveFolder = string.IsNullOrWhiteSpace(value.DefaultSaveFolder)
                ? _vm.DefaultSaveFolder
                : value.DefaultSaveFolder;
            _vm.DefaultSegments = value.MaxSegments;
        }
    }

    /// <summary>落盘回调：返回非空字符串表示失败（对话框保持打开并显示原因）。</summary>
    public Func<AppSettings, string?>? SaveSettings { get; set; }

    /// <summary>测试缝：无头测试直接观测窗口绑定的 ViewModel（窗口不暴露 DataContext）。</summary>
    internal MainWindowViewModel ViewModelForTest => _vm;

    private void OnSettings()
    {
        var viewModel = new SettingsViewModel(_settings, _vm.DbPath);
        if (SettingsDialog.Show(this, viewModel, SaveSettings) is not { } saved)
        {
            return;
        }

        // 保存成功后立即套用到运行中的界面（并发/限速的引擎侧生效留给壳层回调）。
        Settings = saved;
        ShowNotice("设置已保存");
    }

    // ── 分类栏 ───────────────────────────────────────────────────────────────

    private Border BuildCategoryPane()
    {
        _categoryList = new ListBox()
            .ItemHeight(30)
            .ItemsSource(ItemsView.Create(_vm.Categories))
            .ItemTemplate<CategoryViewModel>(
                build: ctx =>
                {
                    var name = new TextBlock().FontSize(13).CenterVertical();
                    var count = new TextBlock().FontSize(12)
                        .Foreground(MewTheme.TextSecondary)
                        .CenterVertical()
                        .Column(1);
                    ctx.Register("name", name);
                    ctx.Register("count", count);
                    // 分类栏宽度固定，条目直接给定宽：星号列在这里会被条目容器的宽度反向撑开
                    // （容器沿用上一轮的期望宽度，越测越宽），定宽才能让计数稳定贴在右侧。
                    var row = new Grid()
                        .Width(MewTheme.CategoryPaneWidth - 12)
                        .Columns("*,Auto")
                        .Padding(12, 0)
                        .Children(name, count);
                    return row;
                },
                bind: (FrameworkElement root, CategoryViewModel item, int index, TemplateContext ctx) =>
                {
                    ctx.Get<TextBlock>("name").Text = item.Name;
                    ctx.Get<TextBlock>("count").Text = item.Count.ToString();
                });

        _categoryList.HorizontalScroll = ScrollMode.Disabled;

        _categoryList.OnSelectionChanged(item =>
        {
            if (item is CategoryViewModel category)
            {
                _vm.SelectedCategory = category;
            }
        });

        _categoryList.SelectedItem = _vm.SelectedCategory;

        return new Border()
            .Width(MewTheme.CategoryPaneWidth)
            .Background(MewTheme.SurfaceBackground)
            .BorderThickness(new Thickness(0, 0, 1, 0))
            .BorderBrush(MewTheme.Divider)
            .Child(new DockPanel().Children(
                new StackPanel()
                    .Padding(12, 10, 12, 4)
                    .Children(new TextBlock()
                        .Text("分类")
                        .FontSize(11)
                        .Foreground(MewTheme.TextSecondary))
                    .DockTop(),
                _categoryList));
    }

    // ── 任务列表 ─────────────────────────────────────────────────────────────

    private Grid BuildTaskArea()
    {
        _taskList = new ListBox()
            .ItemHeight(MewTheme.RowHeight)
            .ItemsSource(ItemsView.Create(_vm.Rows))
            .ItemTemplate<DownloadRowViewModel>(build: BuildRow, bind: BindRow);

        // 行内容按视口宽度排布，本来就不该横向滚动；Auto 下会因浮点取整溢出而常驻一条滚动条。
        _taskList.HorizontalScroll = ScrollMode.Disabled;
        _taskList.AllowDrop = true; // 放置目标按指针下最上层元素命中，列表要自己声明才能收到 drop
        _taskList.OnSelectionChanged(item => _vm.SelectedRow = item as DownloadRowViewModel);
        // 菜单是分层弹出的独立 popup，从它自己的树里找不到窗口的命令作用域，
        // 必须显式把命令目标指到窗口（否则点菜单项什么都不会发生）。
        var menu = new ContextMenu().Items(
            new MenuItem("暂停 / 继续", RowTogglePauseCommand),
            new MenuItem("任务详情…", RowDetailsCommand),
            new MenuItem("打开所在文件夹", RowOpenFolderCommand),
            new MenuItem("复制下载链接", RowCopyUrlCommand),
            new MenuItem("删除任务", RowDeleteCommand));
        menu.SetCommandTarget(CommandTarget.From(Commands));
        _taskList.ContextMenu(menu);

        _emptyHint = new TextBlock()
            .Text("没有任务 · 点「新建」开始下载")
            .FontSize(13)
            .Foreground(MewTheme.TextSecondary)
            .CenterHorizontal()
            .CenterVertical();

        return new Grid().Children(_taskList, _emptyHint);
    }

    private FrameworkElement BuildRow(TemplateContext ctx)
    {
        var ext = new TextBlock()
            .FontSize(9)
            .Foreground(MewTheme.TextSecondary)
            .CenterHorizontal()
            .CenterVertical();

        var icon = new Border()
            .Width(26)
            .Height(26)
            .Background(MewTheme.BadgeBackground)
            .Child(ext)
            .RowSpan(2)
            .CenterVertical();

        var name = new TextBlock()
            .FontSize(13)
            .Foreground(MewTheme.TextPrimary)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .Column(1);

        var size = new TextBlock()
            .FontSize(11)
            .Foreground(MewTheme.TextSecondary)
            .CenterVertical();

        var sizeHost = new Border()
            .Child(size)
            .Column(2);

        var bar = new SegmentBar().CenterVertical();
        var progress = new TextBlock().FontSize(11).Foreground(MewTheme.TextSecondary).CenterVertical();
        var detail = new TextBlock()
            .FontSize(11)
            .Foreground(MewTheme.TextSecondary)
            .TextTrimming(TextTrimming.CharacterEllipsis)
            .CenterVertical();

        var middle = new StackPanel()
            .Horizontal()
            .Spacing(8)
            .Children(bar, progress, detail)
            .Row(1)
            .Column(1);

        var speed = new TextBlock().FontSize(11).Foreground(MewTheme.TextSecondary).CenterVertical();
        var remaining = new TextBlock().FontSize(11).Foreground(MewTheme.TextSecondary).CenterVertical();
        var badgeText = new TextBlock().FontSize(10).Foreground(MewTheme.OnAccent).CenterVertical();
        var badge = new Border()
            .Height(18)
            .Padding(6, 0)
            .Background(MewTheme.BadgeBackground)
            .Child(badgeText)
            .CenterVertical();

        var binding = new RowBinding
        {
            Ext = ext,
            Name = name,
            Size = size,
            Bar = bar,
            Progress = progress,
            Detail = detail,
            Speed = speed,
            Remaining = remaining,
            Badge = badge,
            BadgeText = badgeText,
        };

        // 行内按钮在 build 时挂一次事件，行对象由 bind 写进 RowBinding —— 容器复用后仍指向正确的行。
        var toggle = new Button().Content("暂停").FontSize(10).Height(20).Tag(binding);
        toggle.OnClick(() =>
        {
            if (toggle.Tag is RowBinding b && b.Item is { } row)
            {
                _vm.TogglePause(row);
            }
        });

        var open = new Button().Content("目录").FontSize(10).Height(20).Tag(binding);
        open.OnClick(() =>
        {
            if (open.Tag is RowBinding b && b.Item is { } row)
            {
                row.OpenFolderCommand.Execute(null);
            }
        });

        var delete = new Button().Content("删除").FontSize(10).Height(20).Tag(binding);
        delete.OnClick(() =>
        {
            if (delete.Tag is RowBinding b && b.Item is { } row)
            {
                _vm.Delete(row);
            }
        });

        binding.Toggle = toggle;
        binding.Open = open;
        binding.Delete = delete;

        var actions = new StackPanel()
            .Horizontal()
            .Spacing(4)
            .Children(speed, remaining, badge, toggle, open, delete)
            .Row(1)
            .Column(2);

        var grid = new RowHost()
            .Columns(
            [
                new GridLength(26, GridUnitType.Pixel),
                GridLength.Star,
                new GridLength(360, GridUnitType.Pixel),
            ])
            .Rows("Auto,Auto")
            .Children(icon, name, sizeHost, middle, actions);

        var root = new Border()
            .Height(MewTheme.RowHeight)
            .Padding(12, 4)
            .BorderThickness(new Thickness(0, 0, 0, 1))
            .BorderBrush(MewTheme.Divider)
            .Child(grid)
            .Tag(binding);

        binding.Root = root;

        // 右键先选中该行：菜单在 ListBox 上，命令读的是 SelectedRow。
        root.MouseDown += e =>
        {
            if (e.Button == MouseButton.Right && root.Tag is RowBinding b && b.Item is { } row)
            {
                _vm.SelectedRow = row;
                _taskList.SelectedItem = row;
            }
        };

        // 双击 = 任务详情（分段时间轴）；行本身不是按钮，只能在这里接。
        root.MouseDoubleClick += e =>
        {
            if (e.Button == MouseButton.Left && root.Tag is RowBinding b && b.Item is { } row)
            {
                ShowDetails(row);
            }
        };

        return root;
    }

    private void BindRow(FrameworkElement root, DownloadRowViewModel item, int index, TemplateContext ctx)
    {
        if (root.Tag is not RowBinding binding)
        {
            return;
        }

        // 容器复用：旧的可见映射换成新行，否则行属性变更会刷到别的行上。
        if (binding.Item is { } previous && !ReferenceEquals(previous, item))
        {
            _rowBindings.Remove(previous);
        }

        binding.Item = item;
        _rowBindings[item] = binding;
        ApplyRow(binding, item);
    }

    /// <summary>
    /// 把行 ViewModel 的最新值写进模板控件。MewUI 的 <c>ItemTemplate</c> 没有属性绑定，
    /// <c>bind</c> 只在容器创建/复用时调用一次，所以暂停、进度变化要靠 <see cref="OnRowPropertyChanged"/> 手动重刷。
    /// </summary>
    private static void ApplyRow(RowBinding binding, DownloadRowViewModel item)
    {
        binding.Ext.Text = item.Extension;
        binding.Name.Text = item.FileName;
        binding.Size.Text = item.SizeText;
        binding.Progress.Text = item.ProgressText;
        binding.Detail.Text = item.SegmentAndFolderText;
        binding.Speed.Text = item.SpeedText;
        binding.Remaining.Text = item.RemainingText;
        binding.BadgeText.Text = item.StatusText;
        binding.Badge.Background = StatusColor(item);

        binding.Bar.Segments = item.Segments;
        binding.Bar.Value = item.Progress;
        binding.Bar.IsVisible = item.ShowProgress;
        binding.Bar.Fill = item.IsFailed ? MewTheme.Error : MewTheme.Progress;

        binding.Toggle.Content(item.ToggleText);
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is DownloadRowViewModel row && _rowBindings.TryGetValue(row, out var binding))
        {
            ApplyRow(binding, row);
        }
    }

    /// <summary>可视行的控件组；随模板容器复用而改指向别的行。</summary>
    private sealed class RowBinding
    {
        public DownloadRowViewModel? Item { get; set; }

        public Border Root { get; set; } = null!;

        public TextBlock Ext { get; set; } = null!;

        public TextBlock Name { get; set; } = null!;

        public TextBlock Size { get; set; } = null!;

        public SegmentBar Bar { get; set; } = null!;

        public TextBlock Progress { get; set; } = null!;

        public TextBlock Detail { get; set; } = null!;

        public TextBlock Speed { get; set; } = null!;

        public TextBlock Remaining { get; set; } = null!;

        public Border Badge { get; set; } = null!;

        public TextBlock BadgeText { get; set; } = null!;

        public Button Toggle { get; set; } = null!;

        public Button Open { get; set; } = null!;

        public Button Delete { get; set; } = null!;
    }

    private static Color StatusColor(DownloadRowViewModel row) => row switch
    {
        { IsCompleted: true } => MewTheme.Success,
        { IsFailed: true } => MewTheme.Error,
        { IsPaused: true } => MewTheme.TextSecondary,
        _ => MewTheme.Accent,
    };

    // ── 状态栏 ───────────────────────────────────────────────────────────────

    private Border BuildStatusBar()
    {
        _statusSummary = new TextBlock().FontSize(11).Foreground(MewTheme.TextSecondary).CenterVertical();
        _listSummary = new TextBlock().FontSize(11).Foreground(MewTheme.TextSecondary).CenterVertical();
        var dbPath = new TextBlock()
            .Text(_vm.DbPath)
            .FontSize(11)
            .Foreground(MewTheme.TextSecondary)
            .CenterVertical();

        return new Border()
            .Height(MewTheme.StatusBarHeight)
            .Padding(12, 0)
            .Background(MewTheme.SurfaceBackground)
            .BorderThickness(new Thickness(0, 1, 0, 0))
            .BorderBrush(MewTheme.Divider)
            .Child(new Grid()
                .Columns("Auto,*,Auto")
                .Children(
                    _statusSummary.Column(0).CenterVertical(),
                    _listSummary.Column(1).CenterVertical().Margin(12, 0, 0, 0),
                    dbPath.Column(2).CenterVertical()));
    }

    // ── 命令与拖放 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 删除确认。返回值语义（见 <see cref="MainWindowViewModel.ConfirmDelete"/>）：
    /// <c>null</c> = 用户取消；<c>false</c> = 只删任务记录；<c>true</c> = 连带删除已下载的文件。
    /// </summary>
    private Task<bool?> ConfirmDeleteAsync(DownloadRowViewModel row)
    {
        var name = string.IsNullOrWhiteSpace(row.FileName) ? row.Url : row.FileName;

        if (!row.IsCompleted)
        {
            var message = $"确定删除任务「{name}」？\n未完成的临时文件（.xdm-part）会一起删除。";
            var plain = ConfirmDialog.Show(this, "删除任务", message, "删除", "取消");
            return Task.FromResult<bool?>(plain.Confirmed ? false : null);
        }

        var result = ConfirmDialog.Show(
            this,
            "删除任务",
            $"确定删除任务「{name}」？\n任务记录会从列表中移除。",
            "删除",
            "取消",
            "同时删除已下载的文件");

        return Task.FromResult<bool?>(result.Confirmed ? result.OptionChecked : null);
    }

    /// <summary>
    /// 菜单/快捷键的命令桥：MewUI 的 <see cref="MenuItem"/> 只有 <see cref="Command"/>，没有 OnClick，
    /// 处理器要注册到窗口的命令作用域上。命令统一读 <see cref="MainWindowViewModel.SelectedRow"/>。
    /// </summary>
    private void RegisterRowCommands()
    {
        Commands.Register(RowTogglePauseCommand,
            () =>
            {
                if (_vm.SelectedRow is { } row)
                {
                    _vm.TogglePause(row);
                }
            },
            () => _vm.SelectedRow is not null);

        Commands.Register(RowDetailsCommand,
            () =>
            {
                if (_vm.SelectedRow is { } row)
                {
                    ShowDetails(row);
                }
            },
            () => _vm.SelectedRow is not null);

        Commands.Register(RowOpenFolderCommand,
            () => _vm.SelectedRow?.OpenFolderCommand.Execute(null),
            () => _vm.SelectedRow is not null);

        Commands.Register(RowCopyUrlCommand,
            () =>
            {
                if (_vm.SelectedRow is { } row)
                {
                    var clipboard = Application.Current?.PlatformServices?.Clipboard;
                    ShowNotice(clipboard?.TrySetText(row.Url) == true ? "链接已复制" : "复制失败");
                }
            },
            () => _vm.SelectedRow is not null);

        Commands.Register(RowDeleteCommand,
            () =>
            {
                if (_vm.SelectedRow is { } row)
                {
                    _vm.Delete(row);
                }
            },
            () => _vm.SelectedRow is not null);
    }

    private void OnWindowDragOver(DragEventArgs e) =>
        e.Effect = ExtractUrls(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnWindowDrop(DragEventArgs e)
    {
        var urls = ExtractUrls(e.Data);
        if (urls.Count == 0)
        {
            return;
        }

        _vm.StartDownloads(urls.Select(CreateRequest).ToList());
        ShowNotice($"已从拖入的链接新建 {urls.Count} 个任务");
    }

    /// <summary>拖入的文本按行/空白切成绝对 http(s) 链接（文件拖动不处理，本阶段只接 URL）。</summary>
    private static List<string> ExtractUrls(IDataObject? data)
    {
        if (data is null || !data.TryGetData<string>(StandardDataFormats.Text, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => Uri.TryCreate(token, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .ToList();
    }

    private DownloadRequest CreateRequest(string url)
    {
        var fileName = "download.bin";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var last = uri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrWhiteSpace(last))
            {
                fileName = Uri.UnescapeDataString(last);
            }
        }

        return new DownloadRequest
        {
            Url = url,
            FileName = fileName,
            TargetFolder = _vm.DefaultSaveFolder,
            MaxSegments = _vm.DefaultSegments,
            StartImmediately = true,
        };
    }

    // ── 输入 ─────────────────────────────────────────────────────────────────

    private void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _vm.SelectedRow is { } row)
        {
            _vm.Delete(row);
            e.Handled = true;
            return;
        }

        if (e.ControlKey && e.Key == Key.N)
        {
            OnNewDownload();
            e.Handled = true;
        }
    }
}
