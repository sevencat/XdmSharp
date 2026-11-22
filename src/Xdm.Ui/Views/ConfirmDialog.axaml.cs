using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Xdm.Ui.Views;

/// <summary>确认框结果：是否确认 + 可选勾选项的状态。</summary>
/// <param name="Confirmed">点了「确定」（关窗 / 取消都算 <c>false</c>）。</param>
/// <param name="OptionChecked">勾选项是否被选中；没配勾选项时恒为 <c>false</c>。</param>
public readonly record struct ConfirmResult(bool Confirmed, bool OptionChecked);

/// <summary>通用确认框：「确定 / 取消」，可选带一个勾选项（如「同时删除已下载的文件」）。</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(
        string title,
        string message,
        string okText,
        string cancelText,
        string? optionText = null) : this()
    {
        Bar.Title = title;
        Title = title;
        MessageText.Text = message;

        // 按钮内容是「图标 + 文本」的 StackPanel，改文案要落到 TextBlock 上；
        // AutomationProperties.Name 同步，保证 UIA / 无头测试拿到的仍是纯文字。
        OkText.Text = okText;
        CancelText.Text = cancelText;
        AutomationProperties.SetName(OkButton, okText);
        AutomationProperties.SetName(CancelButton, cancelText);

        if (!string.IsNullOrWhiteSpace(optionText))
        {
            OptionCheck.Content = optionText;
            OptionCheck.IsVisible = true;
            AutomationProperties.SetName(OptionCheck, optionText);
        }
    }

    /// <summary>模态显示；关闭窗口（含 Alt+F4）按取消处理。</summary>
    public static async Task<bool> ShowAsync(
        Window owner,
        string title,
        string message,
        string okText = "确定",
        string cancelText = "取消")
    {
        var result = await ShowWithOptionAsync(owner, title, message, optionText: null, okText, cancelText);
        return result.Confirmed;
    }

    /// <summary>带勾选项的确认框；<paramref name="optionText"/> 为 <c>null</c> 时不显示勾选项。</summary>
    public static async Task<ConfirmResult> ShowWithOptionAsync(
        Window owner,
        string title,
        string message,
        string? optionText,
        string okText = "确定",
        string cancelText = "取消")
    {
        var dialog = new ConfirmDialog(title, message, okText, cancelText, optionText);
        return await dialog.ShowDialog<ConfirmResult>(owner);
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(Result(confirmed: true));

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(Result(confirmed: false));

    private ConfirmResult Result(bool confirmed) => new(confirmed, OptionCheck.IsChecked == true);
}
