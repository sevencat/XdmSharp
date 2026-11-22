using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xdm.Ui.Controls;

namespace Xdm.Ui.Tests;

/// <summary>右键菜单中文化：主题自带的是英文 Cut / Copy / Paste。</summary>
public class EditTextBoxTests
{
    [AvaloniaFact]
    public void ContextMenu_UsesChineseItems()
    {
        var box = new EditTextBox();

        var flyout = Assert.IsType<MenuFlyout>(box.ContextFlyout);
        var headers = flyout.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray();

        Assert.Equal(["撤销", "剪切", "复制", "粘贴", "全选"], headers);
    }

    [AvaloniaFact]
    public void KeepsThemeTemplate()
    {
        // 主题按 TextBox 类型匹配样式；子类必须照样拿到底板与 PART 部件，否则搜索框会变成隐形控件。
        var box = new EditTextBox { Text = "https://example.com", Width = 240 };
        var window = new Window { Content = box, Width = 400, Height = 200 };

        window.Show();
        try
        {
            Assert.NotNull(box.Template);
            Assert.True(box.Bounds.Width > 0);
            Assert.NotNull(box.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().FirstOrDefault());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ContextMenu_ShowsShortcutHints()
    {
        var flyout = Assert.IsType<MenuFlyout>(new EditTextBox().ContextFlyout);
        var gestures = flyout.Items
            .OfType<MenuItem>()
            .Select(item => item.HotKey?.ToString() ?? string.Empty)
            .ToArray();

        Assert.Equal(["Ctrl+Z", "Ctrl+X", "Ctrl+C", "Ctrl+V", "Ctrl+A"], gestures);
    }
}
