using Avalonia.Controls;
using Avalonia.Input;

namespace Xdm.Ui.Controls;

/// <summary>
/// 右键菜单中文化的 <see cref="TextBox"/>。主题内置菜单是英文 Cut / Copy / Paste / Select All，
/// 这里整体换成「撤销 / 剪切 / 复制 / 粘贴 / 全选」，可用状态跟随选区与剪贴板。
/// </summary>
public class EditTextBox : TextBox
{
    public EditTextBox()
    {
        ContextFlyout = BuildMenu();
    }

    /// <summary>
    /// 主题是按类型精确匹配的（<c>Selector="TextBox"</c> 不认子类）；不改样式键的话这个子类
    /// 拿不到底板与 PART 部件，界面上会变成一个隐形控件。
    /// </summary>
    protected override Type StyleKeyOverride => typeof(TextBox);

    private MenuFlyout BuildMenu()
    {
        var undo = Item("撤销", "Ctrl+Z");
        undo.Click += (_, _) => Undo();

        var cut = Item("剪切", "Ctrl+X");
        cut.Click += (_, _) => Cut();

        var copy = Item("复制", "Ctrl+C");
        copy.Click += (_, _) => Copy();

        var paste = Item("粘贴", "Ctrl+V");
        paste.Click += (_, _) => Paste();

        var selectAll = Item("全选", "Ctrl+A");
        selectAll.Click += (_, _) => SelectAll();

        var flyout = new MenuFlyout();
        flyout.Items.Add(undo);
        flyout.Items.Add(cut);
        flyout.Items.Add(copy);
        flyout.Items.Add(paste);
        flyout.Items.Add(selectAll);

        // 每次弹出按当前状态刷新可用项，与主题菜单行为一致（没选中内容时「剪切/复制」置灰）。
        flyout.Opening += (_, _) =>
        {
            undo.IsEnabled = CanUndo;
            cut.IsEnabled = CanCut;
            copy.IsEnabled = CanCopy;
            paste.IsEnabled = CanPaste;
        };

        return flyout;
    }

    private static MenuItem Item(string header, string gesture) => new()
    {
        Header = header,
        HotKey = KeyGesture.Parse(gesture),
    };
}
