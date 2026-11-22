namespace Xdm.Core.Http;

/// <summary>
/// 一次请求走哪条代理。任务级代理在创建任务时解析成这个值，引擎只认它，不再回头查设置。
/// </summary>
public readonly record struct ProxyRoute
{
    private ProxyRoute(ProxyMode mode, string? url)
    {
        Mode = mode;
        Url = url;
    }

    /// <summary>代理方式。</summary>
    public ProxyMode Mode { get; }

    /// <summary><see cref="ProxyMode.Custom"/> 时的代理地址（可含 <c>user:pass@</c>）。</summary>
    public string? Url { get; }

    /// <summary>跟随系统/环境变量代理。</summary>
    public static ProxyRoute System => new(ProxyMode.System, null);

    /// <summary>直连（忽略系统代理）。</summary>
    public static ProxyRoute Direct => new(ProxyMode.Direct, null);

    /// <summary>指定代理地址。</summary>
    public static ProxyRoute Custom(string url) => new(ProxyMode.Custom, url);

    /// <inheritdoc />
    public override string ToString() => Mode switch
    {
        ProxyMode.System => "system",
        ProxyMode.Direct => "direct",
        _ => Url ?? "custom",
    };
}
