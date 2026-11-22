using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xdm.Ui.Mew.Controls;

namespace Xdm.Ui.Mew.Tests;

/// <summary>右键菜单中文化：MewUI 主题自带的是英文 Cut / Copy / Paste。</summary>
public class EditBoxTests
{
    [Fact]
    public void ContextMenu_Uses_Chinese_Items()
    {
        var box = new TextBox();
        EditBox.AttachMenu(box);

        var items = MenuItems(box);

        Assert.Equal(["撤销", "剪切", "复制", "粘贴", "全选"], items.Select(item => item.Text).ToArray());
    }

    [Fact]
    public void ContextMenu_Maps_To_StandardCommands()
    {
        // 菜单项必须挂标准命令，才能吃到快捷键与文本框自身的 Undo/Cut/Copy/Paste/SelectAll 实现。
        var box = new TextBox();
        EditBox.AttachMenu(box);

        var commands = MenuItems(box).Select(item => item.Command).ToArray();

        Assert.Same(StandardCommands.Undo, commands[0]);
        Assert.Same(StandardCommands.Cut, commands[1]);
        Assert.Same(StandardCommands.Copy, commands[2]);
        Assert.Same(StandardCommands.Paste, commands[3]);
        Assert.Same(StandardCommands.SelectAll, commands[4]);
    }

    [Fact]
    public void Every_MenuItem_Can_Resolve_Its_Command_Target()
    {
        // ContextMenu 是独立的分层窗口，不设 CommandTarget 时菜单项找不到文本框，点了没反应（PLAN-MEWUI.md 坑 17）。
        var box = new TextBox();
        EditBox.AttachMenu(box);

        Assert.NotNull(box.ContextMenu);
        Assert.All(MenuItems(box), item => Assert.NotNull(item.Command));
    }

    [Fact]
    public void Create_Returns_TextBase_With_Menu()
    {
        var box = EditBox.Create();

        Assert.NotNull(box.ContextMenu);
        Assert.Equal(5, MenuItems(box).Count);
    }

    private static IReadOnlyList<MenuItem> MenuItems(TextBox box) =>
        (box.ContextMenu?.Items ?? []).OfType<MenuItem>().ToList();
}
