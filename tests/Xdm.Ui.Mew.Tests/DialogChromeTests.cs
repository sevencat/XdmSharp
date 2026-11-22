using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using Xdm.Core.Configuration;
using Xdm.Ui.Mew.Dialogs;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Mew.Tests;

/// <summary>
/// 四个对话框共用 <c>TitleBarChrome</c>：深色自绘标题栏 + 一个矢量关闭键。
/// 这里同时守住「标题文案」与「没有退回系统 chrome」。
/// </summary>
public class DialogChromeTests
{
    [Fact]
    public void NewDownloadDialog_Uses_Shared_TitleBar()
    {
        AssertChrome(new NewDownloadDialog(), "新建下载");
    }

    [Fact]
    public void SettingsDialog_Uses_Shared_TitleBar()
    {
        AssertChrome(new SettingsDialog(new SettingsViewModel(new AppSettings(), @"D:\XdmData\xdm.db")), "设置");
    }

    [Fact]
    public void AboutDialog_Uses_Shared_TitleBar()
    {
        // 标题栏写「关于」，窗口标题（任务栏/无障碍）是完整名。
        AssertChrome(new AboutDialog(), "关于", "关于 XDM Sharp");
    }

    [Fact]
    public void ConfirmDialog_Uses_Shared_TitleBar()
    {
        var dialog = new ConfirmDialog("退出", "确定退出 XDM Sharp？", "退出", "取消", null);

        AssertChrome(dialog, "退出");
    }

    [Fact]
    public void ConfirmDialog_Exposes_Message_And_Option()
    {
        var withOption = new ConfirmDialog("删除任务", "确定删除？", "删除", "取消", "同时删除已下载的文件");

        Assert.True(VisualTree.ContainsText(withOption.Content, "确定删除？"));
        Assert.True(VisualTree.ContainsText(withOption.Content, "同时删除已下载的文件"));
        Assert.True(VisualTree.ContainsText(withOption.Content, "删除"));
        Assert.True(VisualTree.ContainsText(withOption.Content, "取消"));
    }

    [Fact]
    public void ConfirmDialog_Without_Option_Has_No_CheckBox()
    {
        var dialog = new ConfirmDialog("退出", "确定退出？", "退出", "取消", null);

        Assert.Empty(VisualTree.Descendants(dialog.Content).OfType<CheckBox>());

        var withOption = new ConfirmDialog("删除任务", "确定删除？", "删除", "取消", "同时删除已下载的文件");
        Assert.Single(VisualTree.Descendants(withOption.Content).OfType<CheckBox>());
    }

    [Fact]
    public void Dialog_Keeps_Borderless_Chrome()
    {
        // 系统标题栏与自绘标题栏不能同时出现（否则两个关闭键）。
        Assert.True(new ConfirmDialog("退出", "确定退出？", "退出", "取消", null).Borderless);
        Assert.False(new SettingsDialog(new SettingsViewModel(new AppSettings(), "x.db")).ShowInTaskbar);
    }

    private static void AssertChrome(Window dialog, string? chromeTitle = null, string? windowTitle = null)
    {
        var expectedChrome = chromeTitle ?? windowTitle;
        Assert.Equal(windowTitle ?? expectedChrome, dialog.Title);
        Assert.True(VisualTree.ContainsText(dialog.Content, expectedChrome!));

        // 标题栏里恰好一个关闭字形（PathShape），且不是系统 chrome。
        Assert.Single(VisualTree.Descendants(dialog.Content).OfType<PathShape>());
    }
}
