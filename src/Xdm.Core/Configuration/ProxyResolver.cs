using Xdm.Core.Http;

namespace Xdm.Core.Configuration;

/// <summary>
/// 把「任务上的代理 id」解析成引擎能直接用的 <see cref="ProxyRoute"/>。
/// </summary>
/// <remarks>
/// 解析发生在<b>创建任务时</b>：之后用户删代理、改默认代理都不影响已建任务（它的路线已经定了）。
/// </remarks>
public static class ProxyResolver
{
    /// <summary>
    /// <paramref name="proxyId"/> 为 <see langword="null"/>/空表示「用设置的默认代理」；
    /// <see cref="ProxyIds.Direct"/> / <see cref="ProxyIds.System"/> 走对应固定路线；
    /// 其余按 id 查 <see cref="AppSettings.Proxies"/>，查不到（代理被删了）就退回系统代理。
    /// </summary>
    public static ProxyRoute Resolve(AppSettings settings, string? proxyId)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var id = string.IsNullOrWhiteSpace(proxyId) ? settings.DefaultProxyId : proxyId.Trim();
        return Resolve(settings.Proxies, id);
    }

    /// <summary>
    /// HuggingFace 请求（搜索 / 文件清单 / 模型包下载）默认走的路线：先看
    /// <see cref="AppSettings.HuggingFaceProxyId"/>，没单独配（<c>null</c>）就跟随
    /// <see cref="AppSettings.DefaultProxyId"/>。
    /// </summary>
    public static ProxyRoute ResolveHuggingFace(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // 传 null 时 Resolve 会退到 DefaultProxyId，正好就是「跟随默认代理」的语义。
        return Resolve(settings, settings.HuggingFaceProxyId);
    }

    /// <summary>按 id 在给定代理表里查（<c>null</c> 或未知 id 都退回系统代理）。</summary>
    public static ProxyRoute Resolve(IReadOnlyList<ProxyProfile>? proxies, string? proxyId)
    {
        if (string.IsNullOrWhiteSpace(proxyId))
        {
            return ProxyRoute.System;
        }

        var id = proxyId.Trim();
        switch (id)
        {
            case ProxyIds.System:
                return ProxyRoute.System;

            case ProxyIds.Direct:
                return ProxyRoute.Direct;

            default:
                break;
        }

        if (proxies is not null)
        {
            foreach (var profile in proxies)
            {
                if (string.Equals(profile.Id, id, StringComparison.Ordinal) && AppSettings.IsSupportedProxyUrl(profile.Url))
                {
                    return ProxyRoute.Custom(profile.Url);
                }
            }
        }

        // 代理被删/地址被改坏：宁可走系统代理，也别让任务卡在一个不存在的代理上。
        return ProxyRoute.System;
    }
}
