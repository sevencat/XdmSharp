using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using Xdm.Ui.Mew.Controls;

namespace Xdm.Ui.Mew.Tests;

/// <summary>
/// 对话框标题栏抽成 <see cref="TitleBarChrome"/>（PLAN-MEWUI.md 坑 21）：
/// 原来是 <c>Button</c>，会被默认样式画成浅色方块压在深色底上。
/// </summary>
public class TitleBarChromeTests
{
    [Fact]
    public void Builds_Title_And_Single_Close_Glyph()
    {
        var bar = TitleBarChrome.Build(new Window(), "测试标题", () => { });

        Assert.True(VisualTree.ContainsText(bar, "测试标题"));
        Assert.Single(VisualTree.Descendants(bar).OfType<PathShape>());
    }

    [Fact]
    public void Uses_Dark_TitleBar_Background()
    {
        var bar = TitleBarChrome.Build(new Window(), "测试标题", () => { });

        Assert.Equal(MewTheme.TitleBarBackground, bar.Background);
        Assert.Equal(MewTheme.TitleBarHeight, bar.Height);
    }

    [Fact]
    public void Close_Glyph_Is_Hit_Testable()
    {
        // 关闭键是 Border + 矢量字形（Button 压不住默认背景），因此必须仍然可点。
        var bar = TitleBarChrome.Build(new Window(), "测试标题", () => { });
        var close = VisualTree.Descendants(bar).OfType<Border>().Last(border => border.Child is PathShape);

        Assert.True(close.Width is > 0);
        Assert.True(close.Height is > 0);
        Assert.True(close.IsHitTestVisible);
    }
}
