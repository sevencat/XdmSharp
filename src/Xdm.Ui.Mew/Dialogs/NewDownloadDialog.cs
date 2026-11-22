using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xdm.Core.Downloads;
using Xdm.Ui.Mew.Controls;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Mew.Dialogs;

/// <summary>
/// 「新建下载」对话框，字段与 Avalonia 版 <c>Views/NewDownloadWindow.axaml</c> 的「链接」页一致
/// （MewUI 没有多行文本框，这里一次填一个链接；校验仍走 <see cref="NewDownloadViewModel.TryCreate"/>）。
/// </summary>
public sealed class NewDownloadDialog : Window
{
    private const double DialogWidth = 580;
    private const double DialogHeight = 402;

    private readonly NewDownloadViewModel _vm = new();

    private readonly TextBox _urlBox;
    private readonly TextBox _folderBox;
    private readonly TextBox _fileNameBox;
    private readonly NumericUpDown _segmentsBox;
    private readonly CheckBox _startNowBox;
    private readonly TextBlock _errorText;

    public NewDownloadDialog(string? initialUrl = null, string? saveFolder = null, int? segments = null)
    {
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

        _errorText = new TextBlock()
            .FontSize(12)
            .Foreground(MewTheme.Error)
            .CenterVertical()
            .IsVisible(false);

        var form = new Grid()
            .Columns("90,*")
            .Rows("Auto,12,Auto,12,Auto,12,Auto,12,Auto,12,Auto")
            .Padding(16, 14)
            .Children(
                Label("链接").Row(0).Column(0),
                _urlBox.Row(0).Column(1),
                Label("保存到").Row(2).Column(0),
                new Grid()
                    .Columns("*,Auto")
                    .Row(2)
                    .Column(1)
                    .Children(
                        _folderBox.Column(0),
                        new Button()
                            .Content("浏览…")
                            .FontSize(12)
                            .OnClick(OnBrowse)
                            .Margin(8, 0, 0, 0)
                            .Column(1)),
                Label("文件名").Row(4).Column(0),
                _fileNameBox.Row(4).Column(1),
                Label("分段数").Row(6).Column(0),
                _segmentsBox.Row(6).Column(1),
                _startNowBox.Row(8).Column(1),
                new TextBlock()
                    .Text("一期只支持 HTTP / HTTPS 直链；HuggingFace 与 BT 后续版本再加。")
                    .FontSize(11)
                    .Foreground(MewTheme.TextSecondary)
                    .TextWrapping(TextWrapping.Wrap)
                    .Row(10)
                    .Column(1));

        var buttons = new Grid()
            .Columns("*,Auto,Auto,Auto")
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
                .Rows($"{MewTheme.TitleBarHeight},*,Auto")
                .Children(BuildTitleBar().Row(0), form.Row(1), buttons.Row(2)));

        // 控件初值来自 ViewModel，这里把 ViewModel 的当前值回灌到控件（避免只靠 OnTextChanged）。
        _urlBox.Text = _vm.Urls;
        _folderBox.Text = _vm.SaveFolder;
        _fileNameBox.Text = _vm.FileName;
    }

    /// <summary>点「添加」后构造出的请求；取消时为 <c>null</c>。</summary>
    public IReadOnlyList<DownloadRequest>? Requests { get; private set; }

    /// <summary>模态显示；返回 <c>null</c> 表示用户取消。</summary>
    /// <summary>
    /// 弹出对话框并返回待添加的请求（取消返回 <see langword="null"/>）。
    /// <paramref name="saveFolder"/> / <paramref name="segments"/> 来自设置页的默认值。
    /// </summary>
    public static IReadOnlyList<DownloadRequest>? Show(
        Window owner,
        string? initialUrl = null,
        string? saveFolder = null,
        int? segments = null)
    {
        var dialog = new NewDownloadDialog(initialUrl, saveFolder, segments);
        dialog.ShowDialog(owner);
        return dialog.Requests;
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

    private Border BuildTitleBar() => TitleBarChrome.Build(this, "新建下载", Close);

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

        if (!_vm.TryCreate(out var requests))
        {
            _errorText.Text = _vm.ErrorText;
            _errorText.IsVisible = true;
            return false;
        }

        Requests = requests;
        return true;
    }

    /// <summary>测试缝：断言预设值是否真的进了 ViewModel。</summary>
    internal NewDownloadViewModel ViewModel => _vm;
}
