using Xdm.Core.Configuration;
using Xdm.Core.Http;

namespace Xdm.Core.Tests.Configuration;

/// <summary>
/// 任务级代理选择 → 引擎路线 的解析规则（任务引用的代理被删、地址被改坏都要有确定行为）。
/// </summary>
public sealed class ProxyResolverTests
{
    private static AppSettings Settings(string? defaultProxyId = null, params ProxyProfile[] profiles) => new()
    {
        Proxies = [.. profiles],
        DefaultProxyId = defaultProxyId,
    };

    [Fact]
    public void NoSelection_UsesDefaultProxy()
    {
        var settings = Settings("corp", new ProxyProfile { Id = "corp", Url = "http://127.0.0.1:7890" });

        var route = ProxyResolver.Resolve(settings, null);

        Assert.Equal(ProxyMode.Custom, route.Mode);
        Assert.Equal("http://127.0.0.1:7890", route.Url);
    }

    [Fact]
    public void NoSelectionNoDefault_FollowsSystemProxy()
    {
        var route = ProxyResolver.Resolve(Settings(), null);

        Assert.Equal(ProxyMode.System, route.Mode);
        Assert.Null(route.Url);
    }

    [Fact]
    public void DirectSelection_IgnoresDefaultProxy()
    {
        var settings = Settings("corp", new ProxyProfile { Id = "corp", Url = "http://127.0.0.1:7890" });

        Assert.Equal(ProxyMode.Direct, ProxyResolver.Resolve(settings, ProxyIds.Direct).Mode);
    }

    [Fact]
    public void SystemSelection_FollowsSystemProxy()
    {
        var settings = Settings("corp", new ProxyProfile { Id = "corp", Url = "http://127.0.0.1:7890" });

        Assert.Equal(ProxyMode.System, ProxyResolver.Resolve(settings, ProxyIds.System).Mode);
    }

    [Fact]
    public void NamedSelection_WinsOverDefault()
    {
        var settings = Settings(
            "corp",
            new ProxyProfile { Id = "corp", Url = "http://127.0.0.1:7890" },
            new ProxyProfile { Id = "socks", Url = "socks5://127.0.0.1:1080" });

        var route = ProxyResolver.Resolve(settings, "socks");

        Assert.Equal(ProxyMode.Custom, route.Mode);
        Assert.Equal("socks5://127.0.0.1:1080", route.Url);
    }

    [Fact]
    public void DeletedProxy_FallsBackToSystemProxy()
    {
        var settings = Settings("corp", new ProxyProfile { Id = "corp", Url = "http://127.0.0.1:7890" });

        // 任务里存的是 "gone"，用户后来把这条代理删了。
        Assert.Equal(ProxyMode.System, ProxyResolver.Resolve(settings, "gone").Mode);
    }

    [Fact]
    public void ProfileWithBrokenUrl_IsIgnored()
    {
        var settings = Settings(null, new ProxyProfile { Id = "bad", Url = "ftp://127.0.0.1" });

        Assert.Equal(ProxyMode.System, ProxyResolver.Resolve(settings, "bad").Mode);
    }

    [Fact]
    public void HuggingFace_UsesItsOwnProxy()
    {
        var settings = new AppSettings
        {
            Proxies =
            [
                new ProxyProfile { Id = "corp", Url = "http://127.0.0.1:7890" },
                new ProxyProfile { Id = "hf", Url = "socks5://127.0.0.1:1080" },
            ],
            DefaultProxyId = "corp",
            HuggingFaceProxyId = "hf",
        };

        var route = ProxyResolver.ResolveHuggingFace(settings);

        Assert.Equal(ProxyMode.Custom, route.Mode);
        Assert.Equal("socks5://127.0.0.1:1080", route.Url);
    }

    [Fact]
    public void HuggingFace_WithoutOwnProxy_FollowsDefaultProxy()
    {
        var settings = Settings("corp", new ProxyProfile { Id = "corp", Url = "http://127.0.0.1:7890" });

        var route = ProxyResolver.ResolveHuggingFace(settings);

        Assert.Equal(ProxyMode.Custom, route.Mode);
        Assert.Equal("http://127.0.0.1:7890", route.Url);
    }

    [Fact]
    public void HuggingFace_DirectIgnoresDefaultProxy()
    {
        var settings = new AppSettings
        {
            Proxies = [new ProxyProfile { Id = "corp", Url = "http://127.0.0.1:7890" }],
            DefaultProxyId = "corp",
            HuggingFaceProxyId = ProxyIds.Direct,
        };

        Assert.Equal(ProxyMode.Direct, ProxyResolver.ResolveHuggingFace(settings).Mode);
    }

    [Fact]
    public void RouteKeepsUrlForSocks()
    {
        var settings = Settings(null, new ProxyProfile { Id = "s", Url = "socks5://user:pass@127.0.0.1:1080" });

        var route = ProxyResolver.Resolve(settings, "s");

        Assert.Equal("socks5://user:pass@127.0.0.1:1080", route.Url);
    }
}
