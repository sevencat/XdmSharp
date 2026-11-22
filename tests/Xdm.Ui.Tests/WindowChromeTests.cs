using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xdm.Ui.Controls;
using Xdm.Ui.Views;

namespace Xdm.Ui.Tests;

/// <summary>窗口外观（<c>UI-DESIGN.md</c> §11）：系统 chrome 关闭 + 自绘标题栏。</summary>
public class WindowChromeTests
{
    [AvaloniaFact]
    public void NewDownloadWindow_Uses_SelfDrawn_TitleBar()
    {
        AssertChrome(new NewDownloadWindow(), "新建下载", showMaximize: false);
    }

    [AvaloniaFact]
    public void SettingsWindow_Uses_SelfDrawn_TitleBar()
    {
        AssertChrome(new SettingsWindow(), "设置", showMaximize: false);
    }

    [AvaloniaFact]
    public void AboutWindow_Loads()
    {
        var window = new AboutWindow();
        AssertChrome(window, "关于", showMaximize: false);
    }

    [AvaloniaFact]
    public void ConfirmDialog_Loads_And_Exposes_Text()
    {
        var dialog = new ConfirmDialog("退出", "确定退出 XDM Sharp？", "退出", "取消");
        AssertChrome(dialog, "退出", showMaximize: false);

        Assert.Equal("确定退出 XDM Sharp？", dialog.FindControl<TextBlock>("MessageText")!.Text);

        // 按钮内容是「图标 + 文本」，文案在 TextBlock 上；UIA 名字同步成纯文字
        var ok = dialog.FindControl<Button>("OkButton")!;
        var cancel = dialog.FindControl<Button>("CancelButton")!;
        Assert.Equal("退出", dialog.FindControl<TextBlock>("OkText")!.Text);
        Assert.Equal("取消", dialog.FindControl<TextBlock>("CancelText")!.Text);
        Assert.Equal("退出", AutomationProperties.GetName(ok));
        Assert.Equal("取消", AutomationProperties.GetName(cancel));
        Assert.IsType<StackPanel>(ok.Content);
        Assert.IsType<StackPanel>(cancel.Content);
    }

    [AvaloniaFact]
    public void ConfirmDialog_WithOption_ShowsCheckBox()
    {
        var dialog = new ConfirmDialog("删除任务", "确定删除？", "删除", "取消", "同时删除已下载的文件");

        var check = dialog.FindControl<CheckBox>("OptionCheck")!;
        Assert.True(check.IsVisible);
        Assert.Equal("同时删除已下载的文件", check.Content);
        Assert.Equal("同时删除已下载的文件", AutomationProperties.GetName(check));
    }

    [AvaloniaFact]
    public void ConfirmDialog_WithoutOption_HidesCheckBox()
    {
        var dialog = new ConfirmDialog("退出", "确定退出？", "退出", "取消");

        Assert.False(dialog.FindControl<CheckBox>("OptionCheck")!.IsVisible);
    }

    private static void AssertChrome(Window window, string expectedTitle, bool showMaximize)
    {
        window.Show();

        Assert.Equal(WindowDecorations.BorderOnly, window.WindowDecorations);

        var bars = window.GetVisualDescendants().OfType<TitleBar>().ToList();
        Assert.Single(bars);
        Assert.Equal(expectedTitle, bars[0].Title);
        Assert.Equal(showMaximize, bars[0].ShowMaximize);
    }
}
