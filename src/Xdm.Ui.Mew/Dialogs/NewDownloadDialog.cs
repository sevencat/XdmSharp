using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xdm.Core.Configuration;
using Xdm.Core.Downloads;
using Xdm.Core.HuggingFace;
using Xdm.Core.Http;
using Xdm.Ui.Formatting;
using Xdm.Ui.Mew.Controls;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Mew.Dialogs;

/// <summary>
/// 「新建下载」对话框，两个页签：<c>链接</c>（原来的 HTTP 直链表单）与 <c>HuggingFace</c>
/// （模型 id → 拉清单 → 勾文件）。保存目录 / 分段数 / 代理 / 立即开始 是两页共用的，
/// 放在页签下面，避免同一套控件做两份。
/// </summary>
public sealed class NewDownloadDialog : Window
{
    private const double DialogWidth = 620;
    private const double DialogHeight = 600;
    private const double TabAreaHeight = 320;

    private readonly NewDownloadViewModel _vm = new();
    private readonly HfDownloadViewModel _hf;
    private readonly Func<string?, ProxyRoute>? _hfProxyResolver;

    private readonly TextBox _urlBox;
    private readonly TextBox _folderBox;
    private readonly TextBox _fileNameBox;
    private readonly NumericUpDown _segmentsBox;
    private readonly CheckBox _startNowBox;
    private readonly WrapPanel _proxyPanel;
    private readonly TextBlock _errorText;

    private readonly TabControl _tabs;
    private readonly TextBox _hfRepoBox;
    private readonly TextBox _hfRevisionBox;
    private readonly ListBox _hfList;
    private readonly TextBlock _hfStatus;
    private readonly Button _fetchButton;

    public NewDownloadDialog(
        string? initialUrl = null,
        string? saveFolder = null,
        int? segments = null,
        IReadOnlyList<ProxyChoice>? proxies = null,
        string? hfEndpoint = null,
        string? hfToken = null,
        HfHubClient? hfClient = null,
        ProxyRoute? hfProxy = null,
        Func<string?, ProxyRoute>? hfProxyResolver = null)
    {
        _hfProxyResolver = hfProxyResolver;
        Borderless = true;
        Title = "新建下载";
        ShowInTaskbar = false;
        StartupLocation = WindowStartupLocation.CenterOwner;
        WindowSize = WindowSize.Resizable(
            DialogWidth, DialogHeight, DialogWidth, DialogHeight, DialogWidth, DialogHeight);

        // 主窗口把设置里的默认目录/分段数塞进来（对应 Avalonia 版 NewDownloadWindow 的两个预设属性）。
        _vm.Urls = initialUrl ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(saveFolder))
        {
            _vm.SaveFolder = saveFolder;
        }

        if (segments is { } segmentCount)
        {
            _vm.Segments = Math.Clamp(segmentCount, 1, 256);
        }

        // 测试可以注入指向假服务器的客户端；正常路径自建，并走设置里的默认代理（拉清单也要能穿墙）。
        _hf = new HfDownloadViewModel(hfEndpoint, hfToken, hfClient, hfProxy);

        _urlBox = EditBox.Create().OnTextChanged(text => _vm.Urls = text);
        _folderBox = EditBox.Create().OnTextChanged(text => _vm.SaveFolder = text);
        _fileNameBox = EditBox.Create().OnTextChanged(text => _vm.FileName = text);
        _segmentsBox = new NumericUpDown()
            .Minimum(1)
            .Maximum(256)
            .Step(1)
            .Value((double)_vm.Segments)
            .Width(140)
            .HorizontalAlignment(HorizontalAlignment.Left);
        _startNowBox = new CheckBox().Content("立即开始").FontSize(12);
        _startNowBox.IsChecked = _vm.StartImmediately;

        // 代理用单选组：MewUI 0.22 的 ComboBox 在弹层里点不动（PLAN-MEWUI 坑 11）。
        // 传进来的选项由主窗口按设置生成；没有就退化成「默认 + 直连」两项。
        _vm.ProxyChoices = proxies is { Count: > 0 } ? proxies : ProxyChoice.Build(new AppSettings());
        _vm.SelectedProxyIndex = 0;
        _proxyPanel = new WrapPanel().Spacing(12);
        for (var i = 0; i < _vm.ProxyChoices.Count; i++)
        {
            var index = i;
            var radio = new RadioButton()
                .GroupName("new-download-proxy")
                .Content(_vm.ProxyChoices[i].Label)
                .FontSize(12)
                .IsChecked(index == 0);
            radio.OnCheckedChanged(checkedNow =>
            {
                if (checkedNow == true)
                {
                    _vm.SelectedProxyIndex = index;
                }
            });

            _proxyPanel.Children(radio);
        }

        _errorText = new TextBlock()
            .FontSize(12)
            .Foreground(MewTheme.Error)
            .CenterVertical()
            .IsVisible(false);

        // HF 页的控件要在 BuildHfPage 之前建好（它只负责摆位置）。
        _hfRepoBox = EditBox.Create().OnTextChanged(text =>
        {
            _hf.Repo = text;
        });
        _hfRevisionBox = EditBox.Create().OnTextChanged(text => _hf.Revision = text);
        _hfRevisionBox.Text = _hf.Revision;
        _hfRevisionBox.Width(200);
        _hfRevisionBox.HorizontalAlignment(HorizontalAlignment.Left);

        _fetchButton = new Button()
            .Content("拉取文件")
            .FontSize(12)
            .Width(92)
            .Height(26)
            .OnClick(OnFetchFiles);

        _hfStatus = new TextBlock()
            .FontSize(11)
            .Foreground(MewTheme.TextSecondary)
            .TextWrapping(TextWrapping.Wrap)
            .Text(_hf.StatusText);

        _hfList = new ListBox()
            .ItemHeight(24)
            .ItemsSource(ItemsView.Create(new List<HfFile>()))
            .ItemTemplate<HfFile>(build: BuildHfRow, bind: BindHfRow);
        _hfList.SelectionMode = ItemsSelectionMode.Multiple;
        _hfList.HorizontalScroll = ScrollMode.Disabled;

        _tabs = new TabControl()
            .Height(TabAreaHeight)
            .TabItems(
                Tab("链接", BuildLinkPage()),
                Tab("HuggingFace", BuildHfPage()));

        var buttons = new Grid()
            .Columns("*,Auto,Auto")
            .Padding(16, 0, 16, 16)
            .Children(
                _errorText.Column(0),
                DialogButton("取消", primary: false, Close).Column(1),
                DialogButton("添加", primary: true, OnAdd).Column(2).Margin(8, 0, 0, 0));

        Content = new Border()
            .Background(MewTheme.WindowBackground)
            .BorderThickness(new Thickness(1))
            .BorderBrush(MewTheme.Divider)
            .Child(new Grid()
                .Rows($"{MewTheme.TitleBarHeight},Auto,*,Auto")
                .Children(
                    BuildTitleBar().Row(0),
                    _tabs.Row(1),
                    BuildSharedFields().Row(2),
                    buttons.Row(3)));

        // 控件初值来自 ViewModel，这里把 ViewModel 的当前值回灌到控件（避免只靠 OnTextChanged）。
        _urlBox.Text = _vm.Urls;
        _folderBox.Text = _vm.SaveFolder;
        _fileNameBox.Text = _vm.FileName;

        // 拉清单用的是 HttpClient：窗口关掉就释放（X 按钮关闭也要走到这里）。
        Closed += () => _hf.Dispose();
    }

    /// <summary>链接页点「添加」后构造出的请求；HF 页为空（那是 <see cref="Group"/>）。</summary>
    public IReadOnlyList<DownloadRequest>? Requests { get; private set; }

    /// <summary>HuggingFace 页点「添加」后构造出的多文件任务；链接页为 <c>null</c>。</summary>
    public DownloadGroupRequest? Group { get; private set; }

    /// <summary>
    /// 弹出对话框并返回结果（取消返回 <see langword="null"/>）。
    /// <paramref name="saveFolder"/> / <paramref name="segments"/> / <paramref name="proxies"/> 来自设置，
    /// <paramref name="hfEndpoint"/> / <paramref name="hfToken"/> 给 HuggingFace 页拉清单用。
    /// </summary>
    public static NewDownloadResult? Show(
        Window owner,
        string? initialUrl = null,
        string? saveFolder = null,
        int? segments = null,
        IReadOnlyList<ProxyChoice>? proxies = null,
        string? hfEndpoint = null,
        string? hfToken = null,
        HfHubClient? hfClient = null,
        ProxyRoute? hfProxy = null,
        Func<string?, ProxyRoute>? hfProxyResolver = null)
    {
        var dialog = new NewDownloadDialog(
            initialUrl, saveFolder, segments, proxies, hfEndpoint, hfToken, hfClient, hfProxy, hfProxyResolver);
        dialog.ShowDialog(owner);
        return dialog.Requests is null && dialog.Group is null
            ? null
            : new NewDownloadResult(dialog.Requests ?? [], dialog.Group);
    }

    private static TextBlock Label(string text) =>
        new TextBlock()
            .Text(text)
            .FontSize(12)
            .Foreground(MewTheme.TextPrimary)
            .CenterVertical();

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

    private static Button SmallButton(string text, Action onClick) =>
        new Button().Content(text).FontSize(11).Height(24).OnClick(onClick);

    // TabControl 要求 Header 元素非空（只给文本会在加页签时抛 ArgumentException）。
    private static TabItem Tab(string header, FrameworkElement content) =>
        new TabItem()
            .Header(new TextBlock().Text(header).FontSize(12).Foreground(MewTheme.TextPrimary))
            .Content(content);

    private Border BuildTitleBar() => TitleBarChrome.Build(this, "新建下载", Close);

    // ── 链接页（原来的一期表单，去掉与 HF 页共用的字段）────────────────────────

    private Grid BuildLinkPage() =>
        new Grid()
            .Columns("90,*")
            .Rows("Auto,12,Auto")
            .Padding(16, 14)
            .Children(
                Label("链接").Row(0).Column(0),
                _urlBox.Row(0).Column(1),
                Label("文件名").Row(2).Column(0),
                _fileNameBox.Row(2).Column(1));

    // ── HuggingFace 页 ──────────────────────────────────────────────────────

    private Grid BuildHfPage()
    {
        // 行高固定：TabControl 有确定高度，列表用 * 吃掉剩下的空间。
        return new Grid()
            .Rows("Auto,10,Auto,10,*,8,Auto,6,Auto")
            .Padding(16, 12)
            .Children(
                new Grid()
                    .Columns("90,*,Auto")
                    .Children(
                        Label("模型 id").Column(0),
                        _hfRepoBox.Column(1),
                        _fetchButton.Column(2).Margin(8, 0, 0, 0))
                    .Row(0),
                new Grid()
                    .Columns("90,*")
                    .Children(Label("版本").Column(0), _hfRevisionBox.Column(1))
                    .Row(2),
                _hfList.Row(4),
                new Grid()
                    .Columns("Auto,Auto,*")
                    .Children(
                        SmallButton("全选", _hfList.SelectAll).Column(0),
                        SmallButton("全不选", _hfList.ClearSelection).Column(1).Margin(8, 0, 0, 0))
                    .Row(6),
                _hfStatus.Row(8));
    }

    private static FrameworkElement BuildHfRow(TemplateContext ctx)
    {
        var path = new TextBlock().FontSize(12).CenterVertical();
        var size = new TextBlock()
            .FontSize(11)
            .Foreground(MewTheme.TextSecondary)
            .CenterVertical()
            .Column(1);

        ctx.Register("path", path);
        ctx.Register("size", size);

        // 列表宽度固定（对话框定宽），条目用定宽避免星号列被反向撑开（同分类栏的坑）。
        return new Grid()
            .Width(548)
            .Columns("*,76")
            .Padding(8, 0)
            .Children(path, size);
    }

    private static void BindHfRow(FrameworkElement root, HfFile item, int index, TemplateContext ctx)
    {
        ctx.Get<TextBlock>("path").Text = item.Path;
        ctx.Get<TextBlock>("size").Text = item.Size is { } size ? TextFormat.Bytes(size) : "—";
    }

    // ── 两页共用的字段 ──────────────────────────────────────────────────────

    private Grid BuildSharedFields()
    {
        var browse = new Button()
            .Content("浏览…")
            .FontSize(12)
            .OnClick(OnBrowse);

        return new Grid()
            .Rows("Auto,10,Auto,10,Auto,12,Auto")
            .Padding(16, 4, 16, 0)
            .Children(
                new Grid()
                    .Columns("90,*,Auto")
                    .Children(Label("保存到").Column(0), _folderBox.Column(1), browse.Column(2).Margin(8, 0, 0, 0))
                    .Row(0),
                new Grid()
                    .Columns("90,*")
                    .Children(Label("分段数").Column(0), _segmentsBox.Column(1))
                    .Row(2),
                new Grid()
                    .Columns("90,*")
                    .Children(_startNowBox.Column(1))
                    .Row(4),
                new Grid()
                    .Columns("90,*")
                    .Children(
                        Label("代理").Column(0),
                        // 代理可能很多：限高 + 滚动，避免把窗口撑破（这样窗口高度才能固定）。
                        // 64 ≈ 两行单选；再多就在框内滚。
                        new ScrollViewer
                        {
                            Height = 64,
                            VerticalScroll = ScrollMode.Auto,
                            HorizontalScroll = ScrollMode.Disabled,
                            Content = _proxyPanel,
                        }.Column(1))
                    .Row(6));
    }

    private void OnBrowse()
    {
        var folder = FileDialog.SelectFolder(new FolderDialogOptions
        {
            Owner = this,
            Title = "选择保存目录",
            InitialDirectory = Directory.Exists(_vm.SaveFolder) ? _vm.SaveFolder : null,
        });

        if (!string.IsNullOrWhiteSpace(folder))
        {
            _vm.SaveFolder = folder;
            _folderBox.Text = folder;
        }
    }

    /// <summary>拉清单走后台线程，结果经 Dispatcher 回 UI 线程（不在 UI 层引入 SynchronizationContext）。</summary>
    private void OnFetchFiles()
    {
        // 用户在代理单选组里换过项：拉清单跟着当前选择走，别用窗口打开时的默认代理。
        if (_hfProxyResolver is not null)
        {
            _hf.TryUseProxy(_hfProxyResolver(_vm.SelectedProxyId));
        }

        _fetchButton.IsEnabled = false;
        _hfStatus.Text = "正在拉取文件清单…";

        var dispatcher = Application.Current?.Dispatcher;
        Task.Run(() =>
        {
            var ok = _hf.LoadFiles();
            if (dispatcher is null)
            {
                ApplyHuggingFaceFiles(ok);
            }
            else
            {
                dispatcher.BeginInvoke(() => ApplyHuggingFaceFiles(ok));
            }
        });
    }

    /// <summary>把 ViewModel 的清单刷到列表控件；拉成功时默认全选（HF 场景通常整仓下载）。</summary>
    private void ApplyHuggingFaceFiles(bool selectAll)
    {
        _hfList.ItemsSource(ItemsView.Create(_hf.Files.ToList()));
        if (selectAll && _hf.HasFiles)
        {
            _hfList.SelectAll();
        }

        _hfStatus.Text = _hf.StatusText;
        _fetchButton.IsEnabled = true;
        _errorText.IsVisible = false;
    }

    private void OnAdd()
    {
        if (Submit())
        {
            Close();
        }
    }

    /// <summary>
    /// 点「添加」走的同一段逻辑（校验 → 构造请求），只是不关窗口。
    /// 测试直接调它，避免为了点按钮去模拟鼠标。
    /// </summary>
    internal bool Submit()
    {
        _vm.Segments = (decimal)Math.Clamp(_segmentsBox.Value, 1, 256);
        _vm.StartImmediately = _startNowBox.IsChecked == true;

        return _tabs.SelectedIndex == 1 ? SubmitHuggingFace() : SubmitLink();
    }

    private bool SubmitLink()
    {
        if (!_vm.TryCreate(out var requests))
        {
            ShowError(_vm.ErrorText);
            return false;
        }

        Requests = requests;
        return true;
    }

    private bool SubmitHuggingFace()
    {
        // 保存目录是 HF 页要用的（目标 = 保存目录/owner/name），先兜住再谈勾选。
        if (!Directory.Exists(_vm.SaveFolder))
        {
            ShowError($"保存目录不存在：{_vm.SaveFolder}");
            return false;
        }

        var selected = _hfList.SelectedIndices;
        if (!_hf.TryCreateGroup(
                _vm.SaveFolder,
                (int)_vm.Segments,
                _vm.SelectedProxyId,
                selected,
                _vm.StartImmediately,
                out var group))
        {
            // 失败原因既放页内状态行（用户正看着文件列表），也放底部错误行（和链接页一致）。
            _hfStatus.Text = _hf.StatusText;
            ShowError(_hf.StatusText);
            return false;
        }

        // 一个模型包 = 一条父行 + 每个文件一条子任务，列表里只显示父行。
        Group = group;
        return true;
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        _errorText.IsVisible = true;
    }

    /// <summary>测试缝：断言预设值是否真的进了 ViewModel。</summary>
    internal NewDownloadViewModel ViewModel => _vm;

    /// <summary>测试缝：HuggingFace 页的 ViewModel（拉清单要网络，测试里注入假客户端）。</summary>
    internal HfDownloadViewModel HuggingFace => _hf;

    /// <summary>测试缝：切到 HuggingFace 页。</summary>
    internal void SelectHuggingFaceTab() => _tabs.SelectedIndex = 1;

    /// <summary>测试缝：按 ViewModel 当前清单刷新列表（等同后台拉取成功后那一步）。</summary>
    internal void RefreshHuggingFaceFiles() => ApplyHuggingFaceFiles(selectAll: true);

    /// <summary>测试缝：把当前清单里的文件全部勾上（等同点「全选」）。</summary>
    internal void SelectAllHuggingFaceFiles() => _hfList.SelectAll();

    /// <summary>测试缝：清空勾选（等同点「全不选」）。</summary>
    internal void ClearHuggingFaceSelection() => _hfList.ClearSelection();
}

/// <summary>
/// 新建窗口的结果：链接页给一组单文件请求，HuggingFace 页给一个「父行 + 子任务」的模型包任务。
/// 两者互斥（<see cref="Group"/> 非空时 <see cref="Requests"/> 为空）。
/// </summary>
public sealed record NewDownloadResult(IReadOnlyList<DownloadRequest> Requests, DownloadGroupRequest? Group)
{
    /// <summary>这次实际会新建多少个文件任务。</summary>
    public int FileCount => Group?.Files.Count ?? Requests.Count;
}
