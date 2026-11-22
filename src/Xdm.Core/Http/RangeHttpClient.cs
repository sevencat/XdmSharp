using System.Net;
using System.Net.Http.Headers;
using Xdm.Core.Errors;

namespace Xdm.Core.Http;

/// <summary>
/// <see cref="IRangeHttpClient"/> 的 <see cref="SocketsHttpHandler"/> 实现。
/// </summary>
/// <remarks>
/// 三条要点（<c>ARCHITECTURE.md</c> §11）：
/// <list type="number">
/// <item>关闭自动重定向，自己跟，才能记 <c>FinalUrl</c> 与每跳的 <c>Last-Modified</c>；</item>
/// <item>关闭透明解压 —— 下载的是文件本身，被解压就写坏了；</item>
/// <item>不使用 <c>HttpClient.Timeout</c>（那是整个请求的超时），读超时由 <c>SegmentRunner</c> 按次计。</item>
/// </list>
/// </remarks>
public sealed class RangeHttpClient : IRangeHttpClient, IDisposable
{
    /// <summary>重定向跳数上限。</summary>
    public const int MaxRedirects = 10;

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private bool _disposed;

    /// <summary>创建默认客户端（自带 handler）。传入 <paramref name="client"/> 时由调用方负责释放。</summary>
    /// <param name="client">外部 client；为空则按 <paramref name="proxyMode"/> 自建 handler。</param>
    /// <param name="maxConnectionsPerServer">单服务器连接上限（默认 256，远大于任务×段并发）。</param>
    /// <param name="proxyMode">代理方式；仅在自建 handler 时生效。</param>
    /// <param name="proxyUrl"><paramref name="proxyMode"/> 为 <see cref="ProxyMode.Custom"/> 时的代理地址。</param>
    public RangeHttpClient(
        HttpClient? client = null,
        int maxConnectionsPerServer = 256,
        ProxyMode proxyMode = ProxyMode.System,
        string? proxyUrl = null)
    {
        if (client is not null)
        {
            _client = client;
            _ownsClient = false;
            return;
        }

        _client = new HttpClient(CreateHandler(maxConnectionsPerServer, proxyMode, proxyUrl))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _ownsClient = true;
    }

    /// <summary>
    /// 构造下载用的 <see cref="SocketsHttpHandler"/>：关自动重定向与透明解压，按需挂代理。
    /// 单独暴露是为了能对代理配置做单测（handler 属性可读）。
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="proxyMode"/> 为 <see cref="ProxyMode.Custom"/> 但地址为空、不是绝对 URI 或协议不受支持。
    /// </exception>
    public static SocketsHttpHandler CreateHandler(
        int maxConnectionsPerServer = 256,
        ProxyMode proxyMode = ProxyMode.System,
        string? proxyUrl = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = maxConnectionsPerServer,
        };

        switch (proxyMode)
        {
            case ProxyMode.System:
                // 默认即系统/环境变量代理，这里显式写清楚，别让后续改动悄悄改行为。
                handler.UseProxy = true;
                handler.Proxy = null;
                break;

            case ProxyMode.Direct:
                handler.UseProxy = false;
                handler.Proxy = null;
                break;

            case ProxyMode.Custom:
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(ParseProxyUri(proxyUrl));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(proxyMode), proxyMode, "未知的代理方式");
        }

        return handler;
    }

    private static Uri ParseProxyUri(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            throw new ArgumentException("自定义代理缺少地址", nameof(proxyUrl));
        }

        if (!Uri.TryCreate(proxyUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
        {
            throw new ArgumentException($"代理地址无效（只支持 http/https/socks）：{proxyUrl}", nameof(proxyUrl));
        }

        return uri;
    }

    /// <inheritdoc />
    public HttpRangeResponse Send(HttpRangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var url = request.Url;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            HttpResponseMessage response;
            using (var message = BuildMessage(request, url))
            {
                try
                {
                    response = _client.Send(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    return Failure(SourceOutcome.Retry, DownloadError.Timeout, 0, ex.Message);
                }
                catch (HttpRequestException ex)
                {
                    return Failure(SourceOutcome.Retry, DownloadError.Network, 0, ex.Message);
                }
                catch (IOException ex)
                {
                    return Failure(SourceOutcome.Retry, DownloadError.Network, 0, ex.Message);
                }
            }

            var status = (int)response.StatusCode;
            if (IsRedirect(status))
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                {
                    return Failure(SourceOutcome.Rejected, DownloadError.Rejected, status, "重定向缺少 Location");
                }

                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }

            return Map(response, request, url, status);
        }

        return Failure(SourceOutcome.Rejected, DownloadError.Rejected, 0, "重定向次数过多");
    }

    private static HttpRequestMessage BuildMessage(HttpRangeRequest request, Uri url)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        if (request.Offset is { } offset)
        {
            message.Headers.Range = request.Length is { } length && length > 0
                ? new RangeHeaderValue(offset, offset + length - 1)
                : new RangeHeaderValue(offset, null);
        }

        if (!string.IsNullOrEmpty(request.IfRange))
        {
            message.Headers.TryAddWithoutValidation("If-Range", request.IfRange);
        }

        if (!string.IsNullOrEmpty(request.Cookie))
        {
            message.Headers.TryAddWithoutValidation("Cookie", request.Cookie);
        }

        if (request.Headers is { Count: > 0 } extras)
        {
            foreach (var (name, value) in extras)
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return message;
    }

    private static HttpRangeResponse Map(HttpResponseMessage response, HttpRangeRequest request, Uri finalUrl, int status)
    {
        var content = response.Content;
        var validator = FirstHeader(content.Headers, "Last-Modified");
        var disposition = FirstHeader(content.Headers, "Content-Disposition");

        switch (status)
        {
            case 200:
            {
                // 请求了区间却回全量 → 服务器不支持 Range，交给调度层退化单流。
                var ignored = request.Offset is not null;
                var info = new HttpResponseInfo
                {
                    Outcome = ignored ? SourceOutcome.Unsupported : SourceOutcome.Ok,
                    StatusCode = status,
                    ContentLength = content.Headers.ContentLength,
                    TotalSize = content.Headers.ContentLength,
                    Validator = validator,
                    ContentDisposition = disposition,
                    FinalUrl = finalUrl,
                    RangeIgnored = ignored,
                    Detail = ignored ? "服务器忽略 Range，返回 200 全量" : null,
                };
                return new HttpRangeResponse(info, content.ReadAsStream(), response);
            }

            case 206:
            {
                if (!TryParseContentRange(FirstHeader(content.Headers, "Content-Range"), out var start, out var end, out var total))
                {
                    response.Dispose();
                    return Failure(SourceOutcome.Rejected, DownloadError.Rejected, status, "Content-Range 缺失或无法解析");
                }

                if (request.Offset is { } expectedStart && start != expectedStart)
                {
                    response.Dispose();
                    return Failure(SourceOutcome.Rejected, DownloadError.LengthMismatch, status,
                        $"区间起点不符：期望 {expectedStart}，实际 {start}");
                }

                if (request.Length is { } expectedLength && end - start + 1 != expectedLength)
                {
                    response.Dispose();
                    return Failure(SourceOutcome.Rejected, DownloadError.LengthMismatch, status,
                        $"区间长度不符：期望 {expectedLength}，实际 {end - start + 1}");
                }

                var info = new HttpResponseInfo
                {
                    Outcome = SourceOutcome.Ok,
                    StatusCode = status,
                    ContentLength = content.Headers.ContentLength ?? end - start + 1,
                    TotalSize = total,
                    RangeStart = start,
                    RangeEnd = end,
                    Validator = validator,
                    ContentDisposition = disposition,
                    FinalUrl = finalUrl,
                };
                return new HttpRangeResponse(info, content.ReadAsStream(), response);
            }

            case 416:
                response.Dispose();
                return Failure(SourceOutcome.Rejected, DownloadError.Rejected, status, "服务器拒绝该区间（416）");

            case 401 or 407:
                response.Dispose();
                return Failure(SourceOutcome.Rejected, DownloadError.Rejected, status, "需要认证");

            case 403 or 410:
                response.Dispose();
                return Failure(SourceOutcome.Expired, DownloadError.Expired, status, "链接失效或被拒绝（403/410）");

            case 429 or 503:
            {
                var retryAfter = response.Headers.RetryAfter;
                response.Dispose();
                return new HttpRangeResponse(
                    new HttpResponseInfo
                    {
                        Outcome = SourceOutcome.Busy,
                        StatusCode = status,
                        Error = DownloadError.Busy,
                        RetryAfter = retryAfter?.Delta
                            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null),
                        Detail = $"服务器繁忙（{status}）",
                    },
                    null,
                    null);
            }

            default:
                response.Dispose();
                if (status >= 500)
                {
                    return Failure(SourceOutcome.Retry, DownloadError.Network, status, $"服务器错误（{status}）");
                }

                return Failure(SourceOutcome.Rejected, DownloadError.Rejected, status, $"未预期的状态码 {status}");
        }
    }

    private static bool IsRedirect(int status) => status is 301 or 302 or 303 or 307 or 308;

    private static string? FirstHeader(HttpContentHeaders headers, string name)
        => headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static bool TryParseContentRange(string? value, out long start, out long end, out long? total)
    {
        start = 0;
        end = 0;
        total = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // bytes 0-1023/4096   |   bytes 0-1023/*
        var span = value.AsSpan().Trim();
        if (!span.StartsWith("bytes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        span = span[5..].Trim();
        var slash = span.IndexOf('/');
        if (slash < 0)
        {
            return false;
        }

        var range = span[..slash].Trim();
        var size = span[(slash + 1)..].Trim();
        var dash = range.IndexOf('-');
        if (dash <= 0)
        {
            return false;
        }

        if (!long.TryParse(range[..dash], out start) || !long.TryParse(range[(dash + 1)..], out end))
        {
            return false;
        }

        if (!size.SequenceEqual("*"))
        {
            if (!long.TryParse(size, out var parsed))
            {
                return false;
            }

            total = parsed;
        }

        return true;
    }

    private static HttpRangeResponse Failure(SourceOutcome outcome, DownloadError error, int status, string detail)
        => HttpRangeResponse.Failure(new HttpResponseInfo
        {
            Outcome = outcome,
            StatusCode = status,
            Error = error,
            Detail = detail,
        });

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
