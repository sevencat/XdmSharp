using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace Xdm.Ui.Mew.Controls;

/// <summary>
/// 中文右键菜单的输入框，对应 Avalonia 版 <c>Controls/EditTextBox</c>。
/// MewUI 的 <see cref="TextBox"/> 默认没有右键菜单，这里挂 <see cref="StandardCommands"/>
/// （撤销 / 剪切 / 复制 / 粘贴 / 全选），处理器由 <see cref="TextBox"/> 自己注册在命令作用域里。
/// </summary>
internal static class EditBox
{
    /// <summary>建一个带中文右键菜单的输入框；<paramref name="placeholder"/> 为空表示不显示占位符。</summary>
    public static TextBox Create(string placeholder = "", double fontSize = 12)
    {
        var box = new TextBox().FontSize(fontSize);
        if (placeholder.Length > 0)
        {
            box.Placeholder(placeholder);
        }

        AttachMenu(box);
        return box;
    }

    /// <summary>给已有输入框挂中文右键菜单（多行文本框等场景）；<see cref="TextBox"/> 与
    /// <see cref="MultiLineTextBox"/> 都派生自 <see cref="TextBase"/>，共用同一套编辑命令。</summary>
    public static T AttachMenu<T>(T box)
        where T : TextBase
    {
        var menu = new ContextMenu().Items(
            new MenuItem("撤销", StandardCommands.Undo),
            new MenuItem("剪切", StandardCommands.Cut),
            new MenuItem("复制", StandardCommands.Copy),
            new MenuItem("粘贴", StandardCommands.Paste),
            new MenuItem("全选", StandardCommands.SelectAll));

        // 菜单是独立 popup 窗口：从它自己的树里找不到输入框的命令作用域，
        // 必须显式把命令目标指到输入框（与列表右键菜单同一坑，见 PLAN-MEWUI.md 坑 9）。
        menu.SetCommandTarget(CommandTarget.From(box));
        box.ContextMenu(menu);
        return box;
    }
}
