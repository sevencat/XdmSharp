using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xdm.Ui.Mew.Controls;

namespace Xdm.Ui.Mew.Dialogs;

/// <summary>确认框结果：是否确认 + 可选勾选项的状态。</summary>
/// <param name="Confirmed">点了「确定」（关窗 / 取消都算 <c>false</c>）。</param>
/// <param name="OptionChecked">勾选项是否被选中；没配勾选项时恒为 <c>false</c>。</param>
public readonly record struct ConfirmResult(bool Confirmed, bool OptionChecked);

/// <summary>
/// 通用确认框：「确定 / 取消」，可选带一个勾选项（如「同时删除已下载的文件」）。
/// 与 Avalonia 版 <c>Views/ConfirmDialog.axaml</c> 文案、按钮顺序一致。
/// </summary>
public sealed class ConfirmDialog : Window
{
    private const double DialogWidth = 440;
    private const double BodyPadding = 20;

    private readonly CheckBox? _option;

    public ConfirmDialog(
        string title,
        string message,
        string okText,
        string cancelText,
        string? optionText)
    {
        Borderless = true;
        Title = title;
        ShowInTaskbar = false;
        StartupLocation = WindowStartupLocation.CenterOwner;

        var lines = message.Split('\n');
        var hasOption = !string.IsNullOrWhiteSpace(optionText);
        var height = MewTheme.TitleBarHeight + 18 + lines.Length * 20 + 16
            + (hasOption ? 30 : 0) + 34 + 16;

        WindowSize = WindowSize.Resizable(DialogWidth, height, DialogWidth, height, DialogWidth, height);

        var body = new StackPanel()
            .Spacing(hasOption ? 14 : 18)
            .Padding(BodyPadding, 18, BodyPadding, 16)
            .Children(
                new TextBlock()
                    .Text(message)
                    .FontSize(13)
                    .TextWrapping(TextWrapping.Wrap)
                    .LineSpacing(4)
                    .Foreground(MewTheme.TextPrimary));

        if (hasOption)
        {
            _option = new CheckBox()
                .Content(optionText!)
                .FontSize(12);
            body.Children(_option);
        }

        body.Children(new StackPanel()
            .Horizontal()
            .Spacing(8)
            .HorizontalAlignment(HorizontalAlignment.Right)
            .Children(
                DialogButton(cancelText, primary: false, OnCancel),
                DialogButton(okText, primary: true, OnOk)));

        Content = new Border()
            .Background(MewTheme.WindowBackground)
            .BorderThickness(new Thickness(1))
            .BorderBrush(MewTheme.Divider)
            .Child(new Grid()
                .Rows($"{MewTheme.TitleBarHeight},*")
                .Children(BuildTitleBar(title).Row(0), body.Row(1)));
    }

    /// <summary>对话框结果；窗口被直接关掉时保持 <c>(false, false)</c>。</summary>
    public ConfirmResult Result { get; private set; }

    /// <summary>模态显示并返回结果（阻塞到对话框关闭）。</summary>
    public static ConfirmResult Show(
        Window owner,
        string title,
        string message,
        string okText = "确定",
        string cancelText = "取消",
        string? optionText = null)
    {
        var dialog = new ConfirmDialog(title, message, okText, cancelText, optionText);
        dialog.ShowDialog(owner);
        return dialog.Result;
    }

    private Border BuildTitleBar(string title) => TitleBarChrome.Build(this, title, OnCancel);

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

    private void OnOk()
    {
        Result = new ConfirmResult(true, _option?.IsChecked == true);
        Close();
    }

    private void OnCancel() => Close();
}
