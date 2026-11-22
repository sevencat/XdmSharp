using System.Windows.Input;
using Aprillz.MewUI;

namespace Xdm.Ui.Mew;

/// <summary>
/// 共享层命令（<see cref="System.Windows.Input.ICommand"/>）到 MewUI <see cref="Command"/> 的桥。
/// MewUI 没有 <c>ICommand</c>，命令必须先在窗口的 <see cref="CommandScope"/> 里注册处理器，
/// 控件（按钮/菜单项）才能挂上它——处理器只是转调共享层，判断与副作用都留在 ViewModel。
/// </summary>
/// <remarks>
/// MewUI 没有 <c>CanExecuteChanged</c>，可用性在每次命令求值时问一次 <see cref="ICommand.CanExecute"/>；
/// 所以这里把共享层的变更事件转成 <see cref="Window.RequerySuggested"/>（见 PLAN-MEWUI.md 风险 3）。
/// </remarks>
internal sealed class MewCommandBridge : IDisposable
{
    private readonly Window _window;
    private readonly ICommand _source;

    public MewCommandBridge(Window window, ICommand source, string id, string text)
    {
        _window = window;
        _source = source;
        Command = new Command(id, text, null);

        window.Commands.Register(
            Command,
            () => _source.Execute(null),
            () => _source.CanExecute(null));

        _source.CanExecuteChanged += OnCanExecuteChanged;
    }

    /// <summary>挂到控件上的 MewUI 命令。</summary>
    public Command Command { get; }

    /// <summary>一次性接线：包装 + 返回可挂到 <c>Button.Command</c> 上的命令。</summary>
    public static Command Connect(Window window, ICommand source, string id, string text) =>
        new MewCommandBridge(window, source, id, text).Command;

    /// <summary>测试缝：注册进 <see cref="CommandScope"/> 的处理器判定的可用性。</summary>
    internal bool CanExecute() => _source.CanExecute(null);

    /// <summary>测试缝：注册进 <see cref="CommandScope"/> 的处理器转发的执行。</summary>
    internal void Execute() => _source.Execute(null);

    private void OnCanExecuteChanged(object? sender, EventArgs e) => _window.RequerySuggested();

    public void Dispose()
    {
        _source.CanExecuteChanged -= OnCanExecuteChanged;
        _window.Commands.Unregister(Command);
    }
}
