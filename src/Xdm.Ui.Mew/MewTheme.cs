using Aprillz.MewUI;

namespace Xdm.Ui.Mew;

/// <summary>
/// MewUI 侧的色值与尺寸。取值对齐 <c>docs/UI-DESIGN.md</c> 的语义（Fluent 浅色主题的近似值），
/// 不追求与 Avalonia 版逐像素一致（见 PLAN-MEWUI.md 风险 6）。
/// </summary>
internal static class MewTheme
{
    public static readonly Color WindowBackground = Color.FromRgb(0xF3, 0xF3, 0xF3);
    public static readonly Color SurfaceBackground = Color.FromRgb(0xFA, 0xFA, 0xFA);

    // 标题栏按 UI-DESIGN.md §11.1：深蓝底 + 更深的 1px 分隔线，三个窗钮 hover 用白色 15% 叠层。
    public static readonly Color TitleBarBackground = Color.FromRgb(0x1F, 0x2B, 0x3B);
    public static readonly Color TitleBarBorder = Color.FromRgb(0x2C, 0x3A, 0x4E);
    public static readonly Color TitleBarForeground = Color.FromRgb(0xE7, 0xEE, 0xF8);
    public static readonly Color TitleBarHover = Color.FromRgb(0x33, 0x3E, 0x4D);
    public static readonly Color TitleBarCloseHover = Color.FromRgb(0xC4, 0x2B, 0x1C);

    public static readonly Color Divider = Color.FromRgb(0xE1, 0xE1, 0xE1);
    public static readonly Color TextPrimary = Color.FromRgb(0x1F, 0x1F, 0x1F);
    public static readonly Color TextSecondary = Color.FromRgb(0x6E, 0x6E, 0x6E);
    public static readonly Color Accent = Color.FromRgb(0x0F, 0x6C, 0xBD);
    public static readonly Color Success = Color.FromRgb(0x10, 0x7C, 0x10);
    public static readonly Color Error = Color.FromRgb(0xC4, 0x2B, 0x1C);
    public static readonly Color Progress = Color.FromRgb(0x0F, 0x6C, 0xBD);
    public static readonly Color BadgeBackground = Color.FromRgb(0xE8, 0xE8, 0xE8);
    public static readonly Color OnAccent = Color.FromRgb(0xFF, 0xFF, 0xFF);

    public const double TitleBarHeight = 30;
    public const double ToolbarHeight = 40;
    public const double StatusBarHeight = 24;
    public const double CategoryPaneWidth = 180;
    public const double RowHeight = 56;
}
