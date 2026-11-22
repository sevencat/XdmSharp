namespace Xdm.Ui.ViewModels;

/// <summary>
/// 设置页里的一行代理（名称 + 地址）。<see cref="Id"/> 为空表示新加的，保存时才会分配。
/// </summary>
public sealed class ProxyEditRow
{
    /// <summary>已有代理的稳定 id（任务靠它引用）；新增行为空。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>显示名，可空（空则用地址兜底）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>代理地址。</summary>
    public string Url { get; set; } = string.Empty;
}
