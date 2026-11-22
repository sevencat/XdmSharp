using System.Globalization;

namespace Xdm.Ui.Formatting;

/// <summary>界面文案的唯一格式化入口（便于单测断言，XAML 里不再做字符串拼接）。</summary>
public static class TextFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>字节数 → <c>5.7 GB</c> / <c>12 MB</c> / <c>0 B</c>。</summary>
    public static string Bytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // 单位内 <10 保留一位小数（5.7 GB），否则取整（12 MB），与参考实现观感一致
        var text = unit == 0
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value < 10
                ? value.ToString("0.0", CultureInfo.InvariantCulture)
                : value.ToString("0", CultureInfo.InvariantCulture);

        return $"{text} {Units[unit]}";
    }

    /// <summary>速度 → <c>24.3 MB/s</c>；未知或已停 → <c>—</c>。</summary>
    public static string Speed(double bytesPerSecond)
        => bytesPerSecond <= 0 ? "—" : $"{Bytes((long)Math.Round(bytesPerSecond))}/s";

    /// <summary>剩余时间 → <c>约 45 秒</c> / <c>约 1 分 12 秒</c> / <c>约 1 小时 5 分</c>。</summary>
    public static string Remaining(int? seconds)
    {
        if (seconds is not int s || s <= 0)
        {
            return "—";
        }

        if (s < 60)
        {
            return $"约 {s} 秒";
        }

        if (s < 3600)
        {
            return $"约 {s / 60} 分 {s % 60} 秒";
        }

        return $"约 {s / 3600} 小时 {s % 3600 / 60} 分";
    }

    /// <summary>完成度 → <c>76%</c>（截断，不四舍五入，避免 99.6% 显示成 100%）。</summary>
    public static string Percent(double progress)
        => $"{(int)Math.Floor(Math.Clamp(progress, 0, 1) * 100)}%";

    /// <summary>完成时间 → <c>2026-11-22 10:31</c>。</summary>
    public static string CompletedAt(DateTimeOffset? at)
        => at?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;
}
