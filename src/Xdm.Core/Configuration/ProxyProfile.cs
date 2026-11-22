namespace Xdm.Core.Configuration;

/// <summary>
/// 一条具名代理（设置页可配多条，新建任务时挑一条）。
/// </summary>
public sealed record ProxyProfile
{
    /// <summary>稳定 id（任务靠它引用代理；删除代理后旧任务回退到默认）。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>显示名（为空时用地址兜底）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>代理地址：<c>http://</c> / <c>https://</c> / <c>socks5://</c>（可含 <c>user:pass@</c>）。</summary>
    public string Url { get; init; } = string.Empty;

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Url : Name;
}

/// <summary>
/// 任务/默认代理里表示「不是某条具名代理」的两个保留 id。
/// </summary>
public static class ProxyIds
{
    /// <summary>跟随系统/环境变量代理。</summary>
    public const string System = "system";

    /// <summary>直连，忽略系统代理。</summary>
    public const string Direct = "direct";
}
