namespace Xdm.Core.Http;

/// <summary>
/// 按代理路线取 HTTP 客户端。同一个路线必须返回<b>同一个实例</b>（连接池复用）；
/// 实现方负责在释放时关掉所有客户端。
/// </summary>
public interface IRangeHttpClientProvider
{
    /// <summary>取该路线对应的客户端（线程安全，可并发调用）。</summary>
    IRangeHttpClient For(ProxyRoute route);
}

/// <summary>
/// 按 <see cref="ProxyRoute"/> 懒建并缓存客户端：一个代理一份连接池，任务之间共享。
/// </summary>
public sealed class ProxyHttpClientProvider : IRangeHttpClientProvider, IDisposable
{
    private readonly Dictionary<ProxyRoute, IRangeHttpClient> _clients = [];
    private readonly object _gate = new();
    private bool _disposed;

    /// <inheritdoc />
    public IRangeHttpClient For(ProxyRoute route)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_clients.TryGetValue(route, out var client))
            {
                client = new RangeHttpClient(proxyMode: route.Mode, proxyUrl: route.Url);
                _clients[route] = client;
            }

            return client;
        }
    }

    /// <summary>已缓存的客户端数量（诊断与测试用）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _clients.Count;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var client in _clients.Values)
            {
                if (client is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _clients.Clear();
        }
    }
}
