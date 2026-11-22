using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xdm.Core.Downloads;
using Xdm.Ui.Mew;
using Xdm.Ui.Samples;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Mew.Tests;

/// <summary>
/// 主窗口结构断言。MewUI 0.22 没有 Avalonia.Headless 那样的无渲染宿主，
/// 但控件在 <c>Show()</c> 之前就是普通对象，所以「不显示地构造 + 走逻辑树」覆盖结构类断言，
/// 行内刷新、虚拟化这类必须走布局/绘制的部分交给端到端脚本。
/// </summary>
public class MainWindowTests
{
    private static MainWindow CreateWindow(int demoRows = 2, string dbPath = @"D:\XdmData\xdm.db")
    {
        var viewModel = new MainWindowViewModel(SampleDownloads.Create(demoRows))
        {
            DbPath = dbPath,
        };

        return new MainWindow(viewModel);
    }

    [Fact]
    public void Window_Shows_Title()
    {
        Assert.Equal("XDM Sharp", CreateWindow().Title);
    }

    [Fact]
    public void Window_Uses_SelfDrawn_Chrome()
    {
        // UI-DESIGN.md §11：系统 chrome 关闭，标题栏与窗钮全部自绘。
        Assert.True(CreateWindow().Borderless);
    }

    [Fact]
    public void TitleBar_Shows_Logo_And_Title()
    {
        var window = CreateWindow();

        Assert.Single(VisualTree.Descendants(window.Content).OfType<Image>());
        Assert.True(VisualTree.ContainsText(window.Content, "XDM Sharp"));
    }

    [Fact]
    public void StatusBar_Shows_DbPath()
    {
        // 状态栏右侧回显真实库路径，用来核对 --db（M0 验收点）。
        Assert.True(VisualTree.ContainsText(CreateWindow().Content, @"D:\XdmData\xdm.db"));
    }

    [Fact]
    public void Toolbar_Commands_Are_Registered_In_Window_Scope()
    {
        var window = CreateWindow();

        // MewUI 没有 ICommand：命令必须注册进窗口的 CommandScope，按钮才拿得到处理器。
        var commands = VisualTree.Descendants(window.Content)
            .OfType<Button>()
            .Select(button => button.Command)
            .Where(command => command is not null)
            .Select(command => command!)
            .ToList();

        Assert.Equal(
            ["xdm.toolbar.resume", "xdm.toolbar.pause", "xdm.toolbar.delete"],
            commands.Select(command => command.Id).ToArray());

        Assert.All(commands, command => Assert.True(window.Commands.Contains(command), command.Id));
    }

    [Fact]
    public void Empty_State_Hint_Is_Shown_Without_Tasks()
    {
        var window = CreateWindow(demoRows: 0);

        Assert.Empty(((MainWindowViewModel)window.ViewModelForTest).Rows);
        Assert.True(VisualTree.ContainsText(window.Content, "没有任务"));
    }

    [Fact]
    public void Delete_Is_Wired_To_Confirmation()
    {
        // 删除必须经过确认框：窗口负责注入回调，ViewModel 在无头 / 演示下不弹框。
        var window = CreateWindow();

        Assert.NotNull(window.ViewModelForTest.ConfirmDelete);
    }

    [Fact]
    public void ConfirmOnExit_Is_Off_Until_Composition_Root_Sets_It()
    {
        // 无头测试里保持关闭，否则 Closing 会卡在对话框上；组合根（Xdm.App.Mew）才打开它。
        var window = CreateWindow();

        Assert.False(window.ConfirmOnExit);
        window.ConfirmOnExit = true;
        Assert.True(window.ConfirmOnExit);
    }
}
