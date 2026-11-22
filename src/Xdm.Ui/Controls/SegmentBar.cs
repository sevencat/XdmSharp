using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Xdm.Ui.Controls;

/// <summary>
/// 行内分段进度条：<see cref="DrawingContext"/> 直绘（不复用 <c>ProgressBar</c>，
/// 减少视觉树与布局开销，见 <c>UI-DESIGN.md</c> §4）。
/// </summary>
public sealed class SegmentBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SegmentBar, double>(nameof(Value));

    public static readonly StyledProperty<int> SegmentsProperty =
        AvaloniaProperty.Register<SegmentBar, int>(nameof(Segments), 1);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<SegmentBar, IBrush?>(nameof(Track));

    static SegmentBar()
    {
        AffectsRender<SegmentBar>(ValueProperty, SegmentsProperty, FillProperty, TrackProperty);
    }

    /// <summary>0..1 的完成度。</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>段数；≤ 1 或 &gt; 64 时不画分段线（避免噪声）。</summary>
    public int Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>已完成部分与分段线。</summary>
    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>未完成轨道。</summary>
    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = height / 2;
        var track = new Rect(0, 0, width, height);
        if (Track is { } trackBrush)
        {
            context.DrawRectangle(trackBrush, null, track, radius, radius);
        }

        var ratio = Math.Clamp(Value, 0, 1);
        if (ratio > 0 && Fill is { } fillBrush)
        {
            context.DrawRectangle(fillBrush, null, new Rect(0, 0, width * ratio, height), radius, radius);
        }

        if (Segments is > 1 and <= 64 && Track is { } separator)
        {
            var step = width / Segments;
            for (var i = 1; i < Segments; i++)
            {
                var x = step * i;
                context.DrawRectangle(separator, null, new Rect(x - 0.5, 0, 1, height));
            }
        }
    }
}
