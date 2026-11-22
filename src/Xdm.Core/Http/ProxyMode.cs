namespace Xdm.Core.Http;

/// <summary>HTTP 代理方式（对应设置页「网络」页签）。</summary>
public enum ProxyMode
{
    /// <summary>跟随系统/环境变量代理（<see cref="System.Net.Http.HttpClient"/> 默认行为）。</summary>
    System = 0,

    /// <summary>直连，忽略系统代理。</summary>
    Direct = 1,

    /// <summary>使用 <see cref="Custom"/> 指定的代理地址。</summary>
    Custom = 2,
}
