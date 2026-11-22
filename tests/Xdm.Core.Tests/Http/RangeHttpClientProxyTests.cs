using System.Net;
using Xdm.Core.Http;

namespace Xdm.Core.Tests.Http;

public sealed class RangeHttpClientProxyTests
{
    [Fact]
    public void SystemMode_UsesDefaultProxyResolution()
    {
        using var handler = RangeHttpClient.CreateHandler(proxyMode: ProxyMode.System);

        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void DirectMode_DisablesProxy()
    {
        using var handler = RangeHttpClient.CreateHandler(proxyMode: ProxyMode.Direct);

        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void CustomMode_SetsWebProxy()
    {
        using var handler = RangeHttpClient.CreateHandler(
            proxyMode: ProxyMode.Custom,
            proxyUrl: "http://127.0.0.1:7890");

        Assert.True(handler.UseProxy);
        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal(new Uri("http://127.0.0.1:7890"), proxy.Address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("127.0.0.1:7890")]
    [InlineData("ftp://127.0.0.1:21")]
    public void CustomMode_RejectsBadUrl(string? proxyUrl)
    {
        Assert.Throws<ArgumentException>(() => RangeHttpClient.CreateHandler(
            proxyMode: ProxyMode.Custom,
            proxyUrl: proxyUrl));
    }

    [Fact]
    public void DefaultClient_ConstructsWithSystemProxy()
    {
        using var client = new RangeHttpClient();

        Assert.NotNull(client);
    }
}
