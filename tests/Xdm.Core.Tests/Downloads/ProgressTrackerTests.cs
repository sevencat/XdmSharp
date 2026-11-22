using Xdm.Core.Downloads;

namespace Xdm.Core.Tests.Downloads;

public class ProgressTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SingleSample_ReportsNoSpeedAndNoEta()
    {
        var tracker = new ProgressTracker(TimeSpan.FromSeconds(3));
        tracker.Add(1000, Start);

        Assert.Equal(0, tracker.BytesPerSecond);
        Assert.Null(tracker.Eta(10_000));
    }

    [Fact]
    public void TwoSamples_ComputeSpeedAndEta()
    {
        var tracker = new ProgressTracker(TimeSpan.FromSeconds(3));
        tracker.Add(0, Start);
        tracker.Add(2_000_000, Start.AddSeconds(2));

        Assert.Equal(1_000_000, tracker.BytesPerSecond, 0);

        var eta = tracker.Eta(3_000_000);
        Assert.NotNull(eta);
        Assert.InRange(eta!.Value.TotalSeconds, 0.99, 1.01);
    }

    [Fact]
    public void Eta_IsNull_WithoutTotalSize()
    {
        var tracker = new ProgressTracker();
        tracker.Add(0, Start);
        tracker.Add(500, Start.AddSeconds(1));

        Assert.Null(tracker.Eta(null));
    }

    [Fact]
    public void OldSamples_AreTrimmedFromTheWindow()
    {
        var tracker = new ProgressTracker(TimeSpan.FromSeconds(3));
        tracker.Add(0, Start);
        tracker.Add(1_000, Start.AddSeconds(1));
        tracker.Add(1_000, Start.AddSeconds(60));
        tracker.Add(2_000, Start.AddSeconds(61));

        // 60 秒前的点被丢掉，只剩最近两秒的 1000 字节。
        Assert.Equal(1_000, tracker.BytesPerSecond, 0);
    }

    [Fact]
    public void Reset_ClearsSamples()
    {
        var tracker = new ProgressTracker();
        tracker.Add(0, Start);
        tracker.Add(1000, Start.AddSeconds(1));
        tracker.Reset();

        Assert.Equal(0, tracker.BytesPerSecond);
    }

    [Fact]
    public void NonPositiveWindow_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProgressTracker(TimeSpan.Zero));
    }
}
