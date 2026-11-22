using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xdm.Core.Configuration;
using Xdm.Ui.Mew.Controls;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Mew.Dialogs;

/// <summary>
/// 设置对话框（对应 Avalonia 版 <c>Views/SettingsWindow.axaml</c>）：四个页签 + 保存/关闭。
/// 控件值在保存时回灌 <see cref="SettingsViewModel"/>，校验仍走 <see cref="SettingsViewModel.TryBuild"/>。
/// </summary>
public sealed class SettingsDialog : Window
{
    private const double DialogWidth = 620;
    private const double DialogHeight = 640;

    /// <summary>默认代理单选里最多列出的代理条数（再多就让用户去新建窗口里选，避免撑破窗口）。</summary>
    private const int MaxDefaultProxyOptions = 8;

    private readonly SettingsViewModel _vm;
    private readonly Func<AppSettings, string?>? _onSave;

    private readonly NumericUpDown _segmentsBox;
    private readonly NumericUpDown _concurrentBox;
    private readonly CheckBox _speedLimitBox;
    private readonly NumericUpDown _speedLimitValue;
    private readonly NumericUpDown _retriesBox;
    private readonly NumericUpDown _retryDelayBox;
    private readonly TextBox _saveFolderBox;
    private readonly StackPanel _proxyRowsPanel;
    private readonly WrapPanel _defaultProxyPanel;
    private readonly List<ProxyRowControls> _proxyRows = [];
    private readonly List<(int Index, RadioButton Button)> _defaultProxyRadios = [];
    private readonly MultiLineTextBox _headersBox;
    private readonly CheckBox _notifyBox;
    private readonly TextBlock _errorText;

    public SettingsDialog(SettingsViewModel viewModel, Func<AppSettings, string?>? onSave = null)
    {
        _vm = viewModel;
        _onSave = onSave;

        Borderless = true;
        Title = "设置";
        ShowInTaskbar = false;
        StartupLocation = WindowStartupLocation.CenterOwner;
        // 最小高度按「网络」页兜底（代理列表 132 + 默认代理单选 + 请求头 110），否则缩下去会被裁掉。
        WindowSize = WindowSize.Resizable(
            DialogWidth, DialogHeight, 540, 580, DialogWidth, DialogHeight);

        _segmentsBox = NumberBox(1, 256, 1, (double)_vm.MaxSegments, 150);
        _concurrentBox = NumberBox(1, 32, 1, (double)_vm.MaxConcurrent, 150);
        _speedLimitBox = new CheckBox().Content("启用全局限速").FontSize(12);
        _speedLimitBox.IsChecked = _vm.SpeedLimitEnabled;
        _speedLimitValue = NumberBox(1, (double)SettingsViewModel.MaxSpeedKbps, 64, (double)_vm.SpeedLimitKbps, 170);
        _speedLimitValue.IsEnabled = _vm.SpeedLimitEnabled;
        _speedLimitBox.OnCheckedChanged(_ =>
        {
            // 勾选状态变了才允许改限速值（与 Avalonia 版的 IsEnabled 绑定一致）。
            _speedLimitValue.IsEnabled = _speedLimitBox.IsChecked == true;
        });
        _retriesBox = NumberBox(0, 10, 1, (double)_vm.MaxRetries, 150);
        _retryDelayBox = NumberBox(50, 5000, 50, (double)_vm.RetryBaseDelayMs, 150);
        _notifyBox = new CheckBox().Content("下载完成后提醒").FontSize(12);
        _notifyBox.IsChecked = _vm.NotifyOnComplete;

        _saveFolderBox = EditBox.Create();
        _saveFolderBox.Text = _vm.DefaultSaveFolder;

        // 代理用「可增删的行 + 单选默认代理」：MewUI 0.22 的 ComboBox 在自绘弹层里不响应点击（PLAN-MEWUI 坑 11）。
        _proxyRowsPanel = new StackPanel().Spacing(6);
        _defaultProxyPanel = new WrapPanel().Spacing(12);

        var proxyScroll = new ScrollViewer
        {
            Height = 132,
            VerticalScroll = ScrollMode.Auto,
            HorizontalScroll = ScrollMode.Disabled,
            Content = _proxyRowsPanel,
        };

        _headersBox = new MultiLineTextBox()
            .FontSize(12)
            .Height(110)
            .Wrap(true)
            .Placeholder("Referer: https://example.com\nUser-Agent: Mozilla/5.0 ...");
        _headersBox.Text = _vm.DefaultHeadersText;
        EditBox.AttachMenu(_headersBox);

        // 先按现有代理建行，再生成「默认代理」单选（顺序与 _vm.DefaultProxyIndex 约定一致：0 系统、1 直连、2+i 第 i 行）。
        foreach (var row in _vm.Proxies)
        {
            AddProxyRow(row, rebuild: false);
        }

        if (_proxyRows.Count == 0)
        {
            AddProxyRow(new ProxyEditRow(), rebuild: false);
        }

        RebuildDefaultProxyOptions();

        // 内容区不滚动：四页最长的一页（网络：代理列表 + 默认代理单选 + 请求头）也放得下 620×640。
        var tabs = new TabControl().TabItems(
            Tab("常规",
                Section("界面语言", HintCombo("简体中文", "English")),
                Section("主题", HintCombo("跟随系统", "浅色", "深色")),
                _notifyBox,
                Section("任务库", Text(_vm.DbPath)),
                Section("设置存放", Text("与任务同库（settings 表），随上面的库文件一起备份/搬家"))),
            Tab("连接",
                Section("新任务默认分段数", _segmentsBox),
                Section("同时下载任务数", _concurrentBox),
                Section("", null, _speedLimitBox,
                    new StackPanel()
                        .Horizontal()
                        .Spacing(8)
                        .Children(_speedLimitValue, Centered(Text("KB/s（所有任务合计）")))),
                Section("失败重试次数", _retriesBox),
                Section("重试退避基数（毫秒）", _retryDelayBox)),
            Tab("目录",
                Section("新建任务的默认保存目录", new Grid()
                    .Columns("*,Auto")
                    .Children(
                        _saveFolderBox.Column(0),
                        new Button()
                            .Content("浏览…")
                            .FontSize(12)
                            .Height(26)
                            .Margin(8, 0, 0, 0)
                            .OnClick(OnBrowse)
                            .Column(1))),
                Text("任务库位置由启动参数 --db 决定，界面只读显示。")),
            Tab("网络",
                Section("代理列表（新建任务时可选用）", new StackPanel()
                    .Spacing(8)
                    .Children(
                        proxyScroll,
                        new Button()
                            .Content("+ 新增代理")
                            .FontSize(12)
                            .Height(26)
                            .HorizontalAlignment(HorizontalAlignment.Left)
                            .OnClick(OnAddProxy))),
                Section("默认代理", _defaultProxyPanel),
                Section("默认请求头（每行一条「名称: 值」）", _headersBox),
                Text("代理与请求头保存后即时生效：新任务按新代理表解析，已建任务仍走建任务时的路线。")));

        _errorText = new TextBlock()
            .FontSize(12)
            .Foreground(MewTheme.Error)
            .TextWrapping(TextWrapping.Wrap)
            .IsVisible(false);

        var footer = new StackPanel()
            .Spacing(8)
            .Padding(16, 4, 16, 16)
            .Children(
                _errorText,
                new Grid()
                    .Columns("*,Auto,Auto")
                    .Children(
                        Centered(Text("限速/并发/代理表立即生效；重试参数下次启动生效。")).Column(0),
                        DialogButton("保存", primary: true, OnSave).Column(1),
                        DialogButton("关闭", primary: false, Close).Column(2).Margin(8, 0, 0, 0)));

        Content = new Border()
            .Background(MewTheme.WindowBackground)
            .BorderThickness(new Thickness(1))
            .BorderBrush(MewTheme.Divider)
            .Child(new Grid()
                .Rows($"{MewTheme.TitleBarHeight},*,Auto")
                .Children(BuildTitleBar().Row(0), tabs.Row(1), footer.Row(2)));

        PreviewKeyDown += e =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
    }

    /// <summary>点「保存」并成功落盘后的设置；取消时为 <c>null</c>。</summary>
    public AppSettings? Result { get; private set; }

    /// <summary>模态显示；返回保存后的设置，用户取消/关闭时为 <c>null</c>。</summary>
    public static AppSettings? Show(Window owner, SettingsViewModel viewModel, Func<AppSettings, string?>? onSave = null)
    {
        var dialog = new SettingsDialog(viewModel, onSave);
        dialog.ShowDialog(owner);
        return dialog.Result;
    }

    // ── 控件工厂 ─────────────────────────────────────────────────────────────

    // TabControl.AddTab 要求 Header 元素非空：只设 HeaderText 会在加页签时抛 ArgumentException。
    private static TabItem Tab(string header, params Element[] content) =>
        new TabItem()
            .Header(new TextBlock().Text(header).FontSize(12).Foreground(MewTheme.TextPrimary))
            .Content(new StackPanel().Spacing(12).Padding(14, 12).Children(content));

    /// <summary>一「段」：可选标题 + 内容；<paramref name="extra"/> 用在同一段里放多行（如限速）。</summary>
    private static StackPanel Section(string title, Element? body, params Element[] extra)
    {
        var panel = new StackPanel().Spacing(4);
        if (title.Length > 0)
        {
            panel.Children(Text(title));
        }

        if (body is not null)
        {
            panel.Children(body);
        }

        foreach (var element in extra)
        {
            panel.Children(element);
        }

        return panel;
    }

    private static TextBlock Text(string text) =>
        new TextBlock()
            .Text(text)
            .FontSize(11)
            .Foreground(MewTheme.TextSecondary)
            .TextWrapping(TextWrapping.Wrap);

    private static StackPanel Field(string label, Element control) => Section(label, control);

    private static RadioButton ProxyOption(string text, string group, bool selected) =>
        new RadioButton()
            .GroupName(group)
            .Content(text)
            .FontSize(12)
            .IsChecked(selected);

    /// <summary>本阶段不落地的下拉框（语言/主题）：只展示当前值并置灰，与 Avalonia 版一致。</summary>
    private static ComboBox HintCombo(params string[] items) =>
        new ComboBox()
            .Items(items)
            .SelectedIndex(0)
            .Width(180)
            .HorizontalAlignment(HorizontalAlignment.Left)
            .IsEnabled(false);

    private static NumericUpDown NumberBox(double min, double max, double step, double value, double width) =>
        new NumericUpDown()
            .Minimum(min)
            .Maximum(max)
            .Step(step)
            .Value(value)
            .Width(width)
            .HorizontalAlignment(HorizontalAlignment.Left);

    private static TextBlock Centered(TextBlock text) => text.CenterVertical();

    private static Button DialogButton(string text, bool primary, Action onClick)
    {
        var button = new Button()
            .Content(text)
            .FontSize(12)
            .Width(84)
            .Height(28)
            .OnClick(onClick);

        if (primary)
        {
            button.Background(MewTheme.Accent).Foreground(MewTheme.OnAccent);
        }

        return button;
    }

    private Border BuildTitleBar() => TitleBarChrome.Build(this, "设置", Close);

    // ── 代理列表 ─────────────────────────────────────────────────────────────

    private sealed class ProxyRowControls
    {
        public required ProxyEditRow Row { get; init; }

        public required Element Container { get; init; }

        public required TextBox NameBox { get; init; }

        public required TextBox UrlBox { get; init; }
    }

    private void OnAddProxy() => AddProxyRow(new ProxyEditRow(), rebuild: true);

    private void AddProxyRow(ProxyEditRow row, bool rebuild)
    {
        var nameBox = EditBox.Create("名称（可空）");
        nameBox.Text = row.Name;
        nameBox.Width = 150;

        var urlBox = EditBox.Create("http://127.0.0.1:7890 或 socks5://127.0.0.1:1080");
        urlBox.Text = row.Url;

        var remove = new Button().Content("删除").FontSize(11).Width(52).Height(24);
        var container = new Grid()
            .Columns("150,*,Auto")
            .Children(
                nameBox.Column(0),
                urlBox.Column(1).Margin(6, 0, 0, 0),
                remove.Column(2).Margin(6, 0, 0, 0));
        remove.OnClick(() => RemoveProxyRow(container));

        _proxyRowsPanel.Add(container);
        _proxyRows.Add(new ProxyRowControls
        {
            Row = row,
            Container = container,
            NameBox = nameBox,
            UrlBox = urlBox,
        });

        if (rebuild)
        {
            RebuildDefaultProxyOptions();
        }
    }

    private void RemoveProxyRow(Element container)
    {
        var index = _proxyRows.FindIndex(r => ReferenceEquals(r.Container, container));
        if (index < 0)
        {
            return;
        }

        _proxyRows.RemoveAt(index);
        _proxyRowsPanel.Remove(container);
        RebuildDefaultProxyOptions();
    }

    /// <summary>
    /// 重建「默认代理」单选组：增删代理会改变行号，单选必须跟着重建（组内固定 0 系统、1 直连）。
    /// </summary>
    private void RebuildDefaultProxyOptions()
    {
        var selected = _vm.DefaultProxyIndex;
        _defaultProxyRadios.Clear();
        _defaultProxyPanel.Clear();
        _defaultProxyPanel.Children(DefaultProxyRadio(0, "系统代理"));
        _defaultProxyPanel.Children(DefaultProxyRadio(1, "直连"));

        for (var i = 0; i < _proxyRows.Count && i < MaxDefaultProxyOptions; i++)
        {
            var row = _proxyRows[i];
            var label = !string.IsNullOrWhiteSpace(row.NameBox.Text)
                ? row.NameBox.Text!
                : !string.IsNullOrWhiteSpace(row.UrlBox.Text)
                    ? row.UrlBox.Text!
                    : $"代理 {i + 1}";
            _defaultProxyPanel.Children(DefaultProxyRadio(i + 2, label));
        }

        var target = _defaultProxyRadios.FirstOrDefault(r => r.Index == selected).Button
                     ?? _defaultProxyRadios[0].Button;
        target.IsChecked = true;
    }

    private RadioButton DefaultProxyRadio(int index, string label)
    {
        var radio = new RadioButton()
            .GroupName("default-proxy")
            .Content(label)
            .FontSize(12);
        radio.OnCheckedChanged(checkedNow =>
        {
            if (checkedNow == true)
            {
                _vm.DefaultProxyIndex = index;
            }
        });

        _defaultProxyRadios.Add((index, radio));
        return radio;
    }

    private int SelectedDefaultProxyIndex()
    {
        foreach (var (index, button) in _defaultProxyRadios)
        {
            if (button.IsChecked == true)
            {
                return index;
            }
        }

        return _vm.DefaultProxyIndex;
    }

    // ── 动作 ─────────────────────────────────────────────────────────────────

    private void OnBrowse()
    {
        var folder = FileDialog.SelectFolder(new FolderDialogOptions
        {
            Owner = this,
            Title = "选择默认保存目录",
            InitialDirectory = Directory.Exists(_vm.DefaultSaveFolder) ? _vm.DefaultSaveFolder : null,
        });

        if (!string.IsNullOrWhiteSpace(folder))
        {
            _saveFolderBox.Text = folder;
        }
    }

    private void OnSave()
    {
        // 控件 → ViewModel（按页签顺序，与 Avalonia 版双向绑定等价）。
        _vm.MaxSegments = (decimal)_segmentsBox.Value;
        _vm.MaxConcurrent = (decimal)_concurrentBox.Value;
        _vm.SpeedLimitEnabled = _speedLimitBox.IsChecked == true;
        _vm.SpeedLimitKbps = (decimal)_speedLimitValue.Value;
        _vm.MaxRetries = (decimal)_retriesBox.Value;
        _vm.RetryBaseDelayMs = (decimal)_retryDelayBox.Value;
        _vm.DefaultSaveFolder = _saveFolderBox.Text;

        // 代理行控件 → VM 的编辑行（Id 沿用，保证已有任务引用的代理不被换掉）。
        _vm.Proxies.Clear();
        foreach (var row in _proxyRows)
        {
            _vm.Proxies.Add(new ProxyEditRow
            {
                Id = row.Row.Id,
                Name = row.NameBox.Text ?? string.Empty,
                Url = row.UrlBox.Text ?? string.Empty,
            });
        }

        _vm.DefaultProxyIndex = SelectedDefaultProxyIndex();
        _vm.DefaultHeadersText = _headersBox.Text;
        _vm.NotifyOnComplete = _notifyBox.IsChecked == true;

        if (!_vm.TryBuild(out var settings))
        {
            ShowError();
            return;
        }

        var error = _onSave?.Invoke(settings);
        if (error is not null)
        {
            _vm.ShowError(error);
            ShowError();
            return;
        }

        Result = settings;
        Close();
    }

    private void ShowError()
    {
        _errorText.Text = _vm.ErrorText;
        _errorText.IsVisible = _vm.HasError;
    }
}
