using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

namespace Xdm.Ui.Mew;

/// <summary>
/// 分段进度条：把总进度按段平均切开，逐段填色（对应 Avalonia 版 <c>Controls/SegmentBar.cs</c>）。
/// MewUI 没有 <c>DrawingContext</c>，自绘走 <see cref="OnRender(IGraphicsContext)"/>。
/// </summary>
public sealed class SegmentBar : Control
{
    private const double Gap = 1;
    private const double BarWidth = 150;
    private const double BarHeight = 4;
    private double _value;
    private int _segments = 8;

    public Color Fill { get; set; } = MewTheme.Progress;

    public Color Track { get; set; } = MewTheme.Divider;

    /// <summary>0..1。</summary>
    public double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(_value - clamped) < 0.0005)
            {
                return;
            }

            _value = clamped;
            InvalidateVisual();
        }
    }

    public int Segments
    {
        get => _segments;
        set
        {
            if (value <= 0 || _segments == value)
            {
                return;
            }

            _segments = value;
            InvalidateVisual();
        }
    }

    // Control 默认不占位（desired size 为 0），不覆盖测量的话自绘区域是空的（S2 踩坑）。
    protected override Size MeasureOverride(Size availableSize) => new(BarWidth, BarHeight);

    protected override void OnRender(IGraphicsContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var left = Bounds.X;
        var top = Bounds.Y;
        context.FillRectangle(new Rect(left, top, width, height), Track);

        var segmentWidth = (width - (Gap * (_segments - 1))) / _segments;
        if (segmentWidth <= 0)
        {
            return;
        }

        // 已完成的段整段填，正在下的段按段内比例填。
        var filled = _value * _segments;
        for (var i = 0; i < _segments; i++)
        {
            var ratio = Math.Clamp(filled - i, 0, 1);
            if (ratio <= 0)
            {
                break;
            }

            var x = left + (i * (segmentWidth + Gap));
            context.FillRectangle(new Rect(x, top, segmentWidth * ratio, height), Fill);
        }
    }
}
