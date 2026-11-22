using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xdm.Core.HuggingFace;
using Xdm.Ui.Formatting;
using Xdm.Ui.Mew.Controls;

namespace Xdm.Ui.Mew.Dialogs;

/// <summary>
/// 「模型详情」：下载前先看一眼——文件数、总大小、任务类型、README 摘要（<c>ARCHITECTURE.md</c> §12.1）。
/// 数据由 <see cref="HfDownloadViewModel.LoadDetails"/> 拉好再传进来，本窗口只负责摆。
/// </summary>
public sealed class HfModelDetailsWindow : Window
{
    private const double DialogWidth = 520;
    private const double DialogHeight = 360;

    private readonly HfModelDetails _details;

    public HfModelDetailsWindow(HfModelDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        _details = details;

        Borderless = true;
        Title = "模型详情";
        ShowInTaskbar = false;
        StartupLocation = WindowStartupLocation.CenterOwner;
        WindowSize = WindowSize.Resizable(
            DialogWidth, DialogHeight, DialogWidth, DialogHeight, DialogWidth, DialogHeight);

        var body = new StackPanel()
            .Spacing(8)
            .Padding(20, 14, 20, 16)
            .Children(
                new TextBlock()
                    .Text(details.Id)
                    .FontSize(15)
                    .FontWeight(FontWeight.SemiBold)
                    .Foreground(MewTheme.TextPrimary),
                MetaLine(details),
                new Border()
                    .Height(1)
                    .Background(MewTheme.Divider),
                new ScrollViewer
                {
                    Height = 168,
                    VerticalScroll = ScrollMode.Auto,
                    HorizontalScroll = ScrollMode.Disabled,
                    Content = new TextBlock()
                        .Text(string.IsNullOrWhiteSpace(details.Summary)
                            ? "这个模型没有写 README（或正文为空），只能从文件清单判断内容。"
                            : details.Summary)
                        .FontSize(12)
                        .LineSpacing(4)
                        .TextWrapping(TextWrapping.Wrap)
                        .Foreground(string.IsNullOrWhiteSpace(details.Summary)
                            ? MewTheme.TextSecondary
                            : MewTheme.TextPrimary),
                },
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
                .Children(TitleBarChrome.Build(this, "模型详情", Close).Row(0), body.Row(1)));
    }

    /// <summary>测试缝：本窗口显示的数据。</summary>
    internal HfModelDetails Details => _details;

    /// <summary>模态显示模型详情。</summary>
    public static void ShowDetails(Window owner, HfModelDetails details) =>
        new HfModelDetailsWindow(details).ShowDialog(owner);

    /// <summary>「32 个文件 · 共 12.4 GB · text-generation · 1,234 次下载 · 56 赞」（缺的项直接不写）。</summary>
    private static TextBlock MetaLine(HfModelDetails details)
    {
        var parts = new List<string>
        {
            details.FileCount == 0 ? "没有文件" : $"{details.FileCount} 个文件",
        };

        if (details.FileCount > 0)
        {
            parts.Add($"共 {TextFormat.Bytes(details.TotalBytes)}");
        }

        if (!string.IsNullOrWhiteSpace(details.PipelineTag))
        {
            parts.Add(details.PipelineTag!);
        }

        if (details.Downloads is { } downloads)
        {
            parts.Add($"{downloads:N0} 次下载");
        }

        if (details.Likes is { } likes)
        {
            parts.Add($"{likes:N0} 赞");
        }

        return new TextBlock()
            .Text(string.Join(" · ", parts))
            .FontSize(12)
            .Foreground(MewTheme.TextSecondary);
    }
}
