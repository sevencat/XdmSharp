using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace Xdm.Ui.Mew.Tests;

/// <summary>
/// MewUI 没有 <c>GetVisualDescendants</c> 这类可视化树 API，也没有 Avalonia 那样的 Headless 宿主；
/// 这里按「逻辑树 + 模板内容」自己走一遍，供断言标题栏、状态栏、对话框文案。
/// </summary>
/// <remarks>
/// <see cref="ListBox"/> 的项是绘制出来的、不生成子元素（这本身也是它的虚拟化方式），
/// 所以这里不会看到行容器 —— 行级刷新只能靠端到端脚本看截图。
/// </remarks>
internal static class VisualTree
{
    /// <summary>先序遍历，包含 <paramref name="root"/> 自身。</summary>
    public static IEnumerable<Element> Descendants(Element? root)
    {
        if (root is null)
        {
            yield break;
        }

        yield return root;

        foreach (var child in Children(root))
        {
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    public static IEnumerable<TextBlock> TextBlocks(Element? root) =>
        Descendants(root).OfType<TextBlock>();

    /// <summary>
    /// 文本有三种存放处：<see cref="TextBlock.Text"/>、按钮/复选框内容用的 <c>AccessText.RawText</c>
    /// （MewUI 内部类型，只能反射读）、以及 <see cref="TextBox.Text"/>。
    /// 少收一种，断言就会莫名「找不到文案」。
    /// </summary>
    public static IEnumerable<string> Texts(Element? root) =>
        Descendants(root)
            .Select(element => element switch
            {
                TextBlock block => block.Text,
                TextBox box => box.Text,
                _ => AccessTextOf(element),
            })
            .Where(text => !string.IsNullOrEmpty(text))
            .Select(text => text!);

    private static readonly System.Reflection.PropertyInfo? AccessTextProperty =
        typeof(Element).Assembly.GetType("Aprillz.MewUI.Controls.AccessText")
            ?.GetProperty("RawText", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

    private static string? AccessTextOf(Element element) =>
        AccessTextProperty is not null
        && AccessTextProperty.DeclaringType!.IsInstanceOfType(element)
            ? AccessTextProperty.GetValue(element) as string
            : null;

    public static bool ContainsText(Element? root, string expected) =>
        Texts(root).Any(text => text.Contains(expected, StringComparison.Ordinal));

    /// <summary>主列表是窗口里第二个 <see cref="ListBox"/>（第一个是左侧分类）。</summary>
    public static ListBox TaskList(Window window) =>
        Descendants(window.Content).OfType<ListBox>().Last();

    private static IEnumerable<Element> Children(Element element) => element switch
    {
        Panel panel => panel.Children,
        Border border => border.Child is null ? [] : [border.Child],
        ContentControl content => content.Content as Element is { } child ? [child] : [],
        _ => [],
    };
}
