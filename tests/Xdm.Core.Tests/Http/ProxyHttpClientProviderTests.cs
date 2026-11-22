using Xdm.Core.Http;

namespace Xdm.Core.Tests.Http;

/// <summary>
/// 客户端池：同一路线必须复用同一个实例（否则每个任务一个连接池，句柄会漏），
/// 不同路线必须分开。
/// </summary>
public sealed class ProxyHttpClientProviderTests
{
    [Fact]
    public void SameRoute_ReturnsSameInstance()
    {
        using var provider = new ProxyHttpClientProvider();
        var route = ProxyRoute.Custom("http://127.0.0.1:7890");

        var a = provider.For(route);
        var b = provider.For(ProxyRoute.Custom("http://127.0.0.1:7890"));

        Assert.Same(a, b);
        Assert.Equal(1, provider.Count);
    }

    [Fact]
    public void DifferentRoutes_GetSeparateInstances()
    {
        using var provider = new ProxyHttpClientProvider();

        var system = provider.For(ProxyRoute.System);
        var direct = provider.For(ProxyRoute.Direct);
        var custom = provider.For(ProxyRoute.Custom("socks5://127.0.0.1:1080"));

        Assert.NotSame(system, direct);
        Assert.NotSame(direct, custom);
        Assert.Equal(3, provider.Count);
    }

    [Fact]
    public void Dispose_RejectsFurtherUse()
    {
        var provider = new ProxyHttpClientProvider();
        provider.For(ProxyRoute.System);

        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() => provider.For(ProxyRoute.System));
    }
}
