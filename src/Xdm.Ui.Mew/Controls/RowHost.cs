using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace Xdm.Ui.Mew.Controls;

/// <summary>
/// 横向撑满可用宽度的 <see cref="Grid"/>，用作列表行的模板根。
/// </summary>
/// <remarks>
/// MewUI 的条目容器铺满整行，但按模板根的 DesiredSize 摆放内容：带星号列的 <see cref="Grid"/>
/// 测量时星号列只算内容宽度，整行于是只有内容那么宽，右侧定宽列被摆到行外（S2 踩坑）。
/// 把测量宽度报到可用宽度后，星号列拿到剩余空间，行内三列都在可见区域内。
/// </remarks>
public sealed class RowHost : Grid
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = base.MeasureOverride(availableSize);
        return double.IsInfinity(availableSize.Width)
            ? desired
            : new Size(availableSize.Width, desired.Height);
    }
}
