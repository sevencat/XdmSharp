using System.Reflection;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xdm.Ui.Mew.Controls;

namespace Xdm.Ui.Mew.Dialogs;

/// <summary>「关于」对话框，文案对齐 Avalonia 版 <c>Views/AboutWindow.axaml</c>。</summary>
public sealed class AboutDialog : Window
{
    private const double DialogWidth = 460;
    private const double DialogHeight = 300;

    public AboutDialog()
    {
        Borderless = true;
        Title = "关于 XDM Sharp";
        ShowInTaskbar = false;
        StartupLocation = WindowStartupLocation.CenterOwner;
        WindowSize = WindowSize.Resizable(
            DialogWidth, DialogHeight, DialogWidth, DialogHeight, DialogWidth, DialogHeight);

        var body = new StackPanel()
            .Spacing(10)
            .Padding(24, 20, 24, 18)
            .Children(
                new StackPanel()
                    .Horizontal()
                    .Spacing(14)
                    .Children(
                        new Border()
                            .Width(52)
                            .Height(52)
                            .Background(MewTheme.Accent)
                            .Child(new TextBlock()
                                .Text("XDM")
                                .FontSize(14)
                                .Foreground(MewTheme.OnAccent)
                                .CenterHorizontal()
                                .CenterVertical()),
                        new StackPanel()
                            .Spacing(3)
                            .CenterVertical()
                            .Children(
                                new TextBlock()
                                    .Text("XDM Sharp")
                                    .FontSize(18)
                                    .FontWeight(FontWeight.SemiBold)
                                    .Foreground(MewTheme.TextPrimary),
                                new TextBlock()
                                    .Text($"版本 {FormatVersion()}")
                                    .FontSize(12)
                                    .Foreground(MewTheme.TextSecondary))),
                new TextBlock()
                    .Text("Windows 桌面多线程下载工具：HTTP / HTTPS 分段下载、断点续传、限速与任务队列。")
                    .FontSize(12)
                    .TextWrapping(TextWrapping.Wrap)
                    .LineSpacing(4)
                    .Foreground(MewTheme.TextPrimary),
                new TextBlock()
                    .Text("参考 XDM（Kotlin / Swing，GPL-3.0-or-later）重写为 C#，本程序同样以 GPL-3.0 发布。")
                    .FontSize(12)
                    .TextWrapping(TextWrapping.Wrap)
                    .LineSpacing(4)
                    .Foreground(MewTheme.TextSecondary),
                new TextBlock()
                    .Text(".NET 11 · MewUI · SimdAIO · PureDb")
                    .FontSize(11)
                    .Foreground(MewTheme.TextSecondary),
                new StackPanel()
                    .Horizontal()
                    .HorizontalAlignment(HorizontalAlignment.Right)
                    .Children(new Button()
                        .Content("关闭")
                        .FontSize(12)
                        .Width(84)
                        .Height(28)
                        .OnClick(Close)));

        Content = new Border()
            .Background(MewTheme.WindowBackground)
            .BorderThickness(new Thickness(1))
            .BorderBrush(MewTheme.Divider)
            .Child(new Grid()
                .Rows($"{MewTheme.TitleBarHeight},*")
                .Children(BuildTitleBar().Row(0), body.Row(1)));
    }

    /// <summary>模态显示「关于」。</summary>
    public static void ShowAbout(Window owner) => new AboutDialog().ShowDialog(owner);

    private Border BuildTitleBar() => TitleBarChrome.Build(this, "关于", Close);

    private static string FormatVersion()
    {
        var asm = typeof(AboutDialog).Assembly;

        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // 去掉 SourceLink 追加的 "+<commit>" 后缀
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        var v = asm.GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
