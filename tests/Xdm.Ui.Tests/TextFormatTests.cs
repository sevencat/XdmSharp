using Xdm.Ui.Formatting;

namespace Xdm.Ui.Tests;

public class TextFormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(900, "900 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(12_300_000, "12 MB")]
    [InlineData(5_700_000_000, "5.3 GB")]
    public void Bytes_UsesUnitThresholds(long bytes, string expected)
        => Assert.Equal(expected, TextFormat.Bytes(bytes));

    [Fact]
    public void Speed_IsDashWhenStopped()
    {
        Assert.Equal("—", TextFormat.Speed(0));
        Assert.Equal("23 MB/s", TextFormat.Speed(24_300_000));
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0, "—")]
    [InlineData(45, "约 45 秒")]
    [InlineData(72, "约 1 分 12 秒")]
    [InlineData(3900, "约 1 小时 5 分")]
    public void Remaining_FormatsDurations(int? seconds, string expected)
        => Assert.Equal(expected, TextFormat.Remaining(seconds));

    [Theory]
    [InlineData(0d, "0%")]
    [InlineData(0.763, "76%")]
    [InlineData(0.999, "99%")]
    [InlineData(1d, "100%")]
    public void Percent_Truncates(double progress, string expected)
        => Assert.Equal(expected, TextFormat.Percent(progress));

    [Fact]
    public void CompletedAt_UsesMinutePrecision()
    {
        Assert.Equal(string.Empty, TextFormat.CompletedAt(null));

        var text = TextFormat.CompletedAt(new DateTimeOffset(2026, 11, 22, 10, 31, 5, TimeSpan.FromHours(8)));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", text);
    }
}
