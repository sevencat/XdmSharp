using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

namespace Xdm.Ui.Mew.Controls;

/// <summary>
/// 对话框共用的自绘标题栏：深色底 + 拖动 + 矢量关闭键。
/// 四个对话框原先各有一份同样的代码，且关闭键用的是 <see cref="Button"/>，会被默认样式画成浅色方块。
/// </summary>
public static class TitleBarChrome
{
    private const string CloseGeometry = "M 0.5,0.5 L 7.5,7.5 M 7.5,0.5 L 0.5,7.5";

    public static Border Build(Window window, string title, Action onClose)
    {
        var glyph = new PathShape()
            .Width(8)
            .Height(8)
            .CenterHorizontal()
            .CenterVertical();
        glyph.Data = PathGeometry.Parse(CloseGeometry);
        glyph.Stroke = MewTheme.TitleBarForeground;
        glyph.StrokeThickness = 1;

        // 同主窗口窗钮：Button 的背景受模板控制，压不住，所以用 Border 自绘 hover。
        var close = new Border()
            .Width(36)
            .Height(22)
            .Child(glyph);
        close.Background = MewTheme.TitleBarBackground;
        close.MouseEnter += () => close.Background = MewTheme.TitleBarCloseHover;
        close.MouseLeave += () => close.Background = MewTheme.TitleBarBackground;
        close.MouseDown += e =>
        {
            if (e.Button == MouseButton.Left)
            {
                onClose();
            }
        };

        var bar = new Border()
            .Height(MewTheme.TitleBarHeight)
            .Background(MewTheme.TitleBarBackground)
            .Padding(12, 0)
            .Child(new Grid()
                .Columns("*,Auto")
                .Children(
                    new TextBlock()
                        .Text(title)
                        .FontSize(12)
                        .Foreground(MewTheme.TitleBarForeground)
                        .CenterVertical()
                        .Column(0),
                    close.Column(1).CenterVertical()));

        bar.MouseDown += _ => WindowDrag.Begin(window.Handle);
        return bar;
    }
}
