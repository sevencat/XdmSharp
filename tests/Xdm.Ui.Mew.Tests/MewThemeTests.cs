using Aprillz.MewUI;
using Xdm.Ui.Mew;

namespace Xdm.Ui.Mew.Tests;

/// <summary>
/// <see cref="MewTheme"/> 是 MewUI 侧的配色/尺寸唯一来源（无 XAML 资源字典），
/// 所以把 <c>UI-DESIGN.md</c> 的关键 token 钉在测试里，防止改样式时悄悄漂移。
/// </summary>
public class MewThemeTests
{
    [Fact]
    public void Window_Colors_Match_Design()
    {
        Assert.Equal(Color.FromRgb(0xF3, 0xF3, 0xF3), MewTheme.WindowBackground);
        Assert.Equal(Color.FromRgb(0xFA, 0xFA, 0xFA), MewTheme.SurfaceBackground);
        Assert.Equal(Color.FromRgb(0xE1, 0xE1, 0xE1), MewTheme.Divider);
    }

    [Fact]
    public void TitleBar_Colors_Match_Design()
    {
        // 深色标题栏 + 系统关闭键的红（UI-DESIGN.md §11）。
        Assert.Equal(Color.FromRgb(0x1F, 0x2B, 0x3B), MewTheme.TitleBarBackground);
        Assert.Equal(Color.FromRgb(0xE7, 0xEE, 0xF8), MewTheme.TitleBarForeground);
        Assert.Equal(Color.FromRgb(0xC4, 0x2B, 0x1C), MewTheme.TitleBarCloseHover);
    }

    [Fact]
    public void Semantic_Colors_Match_Design()
    {
        Assert.Equal(Color.FromRgb(0x0F, 0x6C, 0xBD), MewTheme.Accent);
        Assert.Equal(Color.FromRgb(0x10, 0x7C, 0x10), MewTheme.Success);
        Assert.Equal(Color.FromRgb(0xC4, 0x2B, 0x1C), MewTheme.Error);
        Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), MewTheme.OnAccent);
    }

    [Fact]
    public void Metrics_Match_Design()
    {
        Assert.Equal(30, MewTheme.TitleBarHeight);
        Assert.Equal(40, MewTheme.ToolbarHeight);
        Assert.Equal(24, MewTheme.StatusBarHeight);
        Assert.Equal(180, MewTheme.CategoryPaneWidth);
        Assert.Equal(56, MewTheme.RowHeight);
    }
}
