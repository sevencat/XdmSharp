using Aprillz.MewUI;
using Xdm.Core.Downloads;
using Xdm.Ui.Mew;
using Xdm.Ui.Samples;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Mew.Tests;

/// <summary>
/// 命令桥：共享层 <see cref="System.Windows.Input.ICommand"/> → MewUI <see cref="Command"/>。
/// MewUI 没有 <c>ICommand</c>，命令必须先注册进窗口作用域，按钮才能拿到处理器。
/// </summary>
public class MewCommandBridgeTests
{
    [Fact]
    public void Connect_Registers_Command_In_Window_Scope()
    {
        var (window, viewModel) = CreateContext();

        var command = MewCommandBridge.Connect(window, viewModel.PauseSelectedCommand, "test.pause", "暂停");

        Assert.Equal("test.pause", command.Id);
        Assert.True(window.Commands.Contains(command));
    }

    [Fact]
    public void Dispose_Unregisters_Command()
    {
        var (window, viewModel) = CreateContext();
        var bridge = new MewCommandBridge(window, viewModel.PauseSelectedCommand, "test.pause", "暂停");

        Assert.True(window.Commands.Contains(bridge.Command));

        bridge.Dispose();

        Assert.False(window.Commands.Contains(bridge.Command));
    }

    [Fact]
    public void Execute_Forwards_To_Source_Command()
    {
        var (window, viewModel) = CreateContext();
        var bridge = new MewCommandBridge(window, viewModel.PauseSelectedCommand, "test.pause", "暂停");
        viewModel.SelectedRow = viewModel.Rows[0];

        bridge.Execute();

        // 演示模式没有引擎出口，命令只改本地状态。
        Assert.Equal(DownloadStatus.Paused, viewModel.Rows[0].Record.Status);
    }

    [Fact]
    public void CanExecute_Follows_Selection()
    {
        var (window, viewModel) = CreateContext();
        var bridge = new MewCommandBridge(window, viewModel.PauseSelectedCommand, "test.pause", "暂停");

        Assert.False(bridge.CanExecute());

        viewModel.SelectedRow = viewModel.Rows[0];
        Assert.True(bridge.CanExecute());

        viewModel.SelectedRow = null;
        Assert.False(bridge.CanExecute());
    }

    [Fact]
    public void Source_CanExecuteChanged_Does_Not_Throw_Without_MessageLoop()
    {
        // 桥把 CanExecuteChanged 转成 window.RequerySuggested()；无消息循环时也不能炸。
        var (window, viewModel) = CreateContext();
        using var bridge = new MewCommandBridge(window, viewModel.DeleteSelectedCommand, "test.delete", "删除");

        viewModel.SelectedRow = viewModel.Rows[0];
        viewModel.SelectedRow = null;

        Assert.False(bridge.CanExecute());
    }

    private static (Window Window, MainWindowViewModel ViewModel) CreateContext()
    {
        var viewModel = new MainWindowViewModel(SampleDownloads.Create(2));
        return (new Window(), viewModel);
    }
}
