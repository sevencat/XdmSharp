using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xdm.Core.Http;

namespace Xdm.Core.HuggingFace;

/// <summary>清单拉取失败（消息直接展示给用户）。</summary>
public sealed class HfException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// HuggingFace Hub 只读客户端：拉仓库文件清单（<c>ARCHITECTURE.md</c> §12.2）。
/// 只做清单，文件内容仍走 <see cref="Xdm.Core.Http.IRangeHttpClient"/> 的分段下载。
/// </summary>
public sealed class HfHubClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <param name="http">注入用（测试指向本地假服务器）；不传则自建并随本对象释放。</param>
    /// <param name="proxy">清单请求走的代理路线；为空跟随系统代理。</param>
    public HfHubClient(HttpClient? http = null, ProxyRoute? proxy = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(CreateHandler(proxy ?? ProxyRoute.System))
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
    }

    /// <summary>
    /// 清单用的 handler：与下载不同，这里要自动跟重定向（HF 常用 307 跳到 CDN）并且要解压
    /// （<c>Accept-Encoding</c> 由运行时自动加，gzip 的 JSON 不解压就解析不了）。
    /// </summary>
    public static SocketsHttpHandler CreateHandler(ProxyRoute route)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        switch (route.Mode)
        {
            case ProxyMode.System:
                handler.UseProxy = true;
                handler.Proxy = null;
                break;

            case ProxyMode.Direct:
                handler.UseProxy = false;
                handler.Proxy = null;
                break;

            case ProxyMode.Custom:
                // 地址在设置层已校验过；这里再失败就退回系统代理，别让清单功能整体不可用。
                if (Uri.TryCreate(route.Url?.Trim(), UriKind.Absolute, out var uri))
                {
                    handler.UseProxy = true;
                    handler.Proxy = new WebProxy(uri);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(route), route.Mode, "未知的代理方式");
        }

        return handler;
    }

    /// <summary>
    /// 按关键字搜模型：<c>GET {endpoint}/api/models?search=…&amp;sort=downloads&amp;direction=-1</c>。
    /// 关键字就是用户输入（可以是模型 id 片段）；按下载量降序，便于挑官方/热门版本。
    /// </summary>
    public async Task<IReadOnlyList<HfModelSummary>> SearchModelsAsync(
        string? endpoint,
        string? query,
        string? token,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var keyword = (query ?? string.Empty).Trim();
        if (keyword.Length == 0)
        {
            return [];
        }

        var url = SearchUrl(endpoint, keyword, limit);
        using var response = await SendAsync(url, token, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseSearchPage(json);
    }

    /// <summary>搜索地址（独立出来便于断言查询串）。<paramref name="limit"/> 夹到 [1, 100]。</summary>
    public static string SearchUrl(string? endpoint, string query, int limit = 20)
    {
        var site = string.IsNullOrWhiteSpace(endpoint) ? HfRepo.DefaultEndpoint : endpoint.Trim().TrimEnd('/');
        var size = Math.Clamp(limit, 1, 100);
        return $"{site}/api/models?search={Uri.EscapeDataString(query.Trim())}&limit={size}&sort=downloads&direction=-1";
    }

    /// <summary>递归列出仓库文件（目录与符号链接跳过），按路径排序。</summary>
    public async Task<IReadOnlyList<HfFile>> GetFilesAsync(
        HfRepo repo,
        string? token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var files = new List<HfFile>();
        var url = repo.FilesApiUrl;

        // 大仓库分页（每页 1000），最多 50 页兜底，避免服务端 Link 异常时死循环。
        for (var page = 0; page < 50 && url is not null; page++)
        {
            using var response = await SendAsync(url, token, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            files.AddRange(ParsePage(json, repo));
            url = NextPageUrl(response, repo);
        }

        files.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return files;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HfException($"连接 HuggingFace 失败：{ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HfException("拉取 HuggingFace 清单超时，检查网络或代理", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = response.StatusCode;
        response.Dispose();
        throw new HfException(status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "HuggingFace 拒绝访问：仓库可能是私有的，或需要先在网页上同意模型许可，然后在设置里填 token",
            HttpStatusCode.NotFound => "HuggingFace 仓库或版本不存在，检查模型 id 与 revision",
            HttpStatusCode.TooManyRequests => "HuggingFace 限流了，稍后再试",
            _ => $"HuggingFace 返回 {(int)status} {status}",
        });
    }

    private static IEnumerable<HfFile> ParsePage(string json, HfRepo repo)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new HfException("HuggingFace 清单不是合法 JSON（站点可能是镜像或需要登录）", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new HfException("HuggingFace 清单格式意外（期望 JSON 数组）");
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("type", out var type) || type.GetString() != "file")
                {
                    continue;
                }

                if (!item.TryGetProperty("path", out var pathElement) || pathElement.GetString() is not { } path)
                {
                    continue;
                }

                long? size = item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value)
                    ? value
                    : null;

                string? sha256 = null;
                if (item.TryGetProperty("lfs", out var lfs)
                    && lfs.ValueKind == JsonValueKind.Object
                    && lfs.TryGetProperty("oid", out var oid))
                {
                    sha256 = oid.GetString();
                }

                var file = new HfFile { Path = path, Size = size, Sha256 = sha256 };

                // 清单里的路径也当不可信输入：穿越路径直接跳过（记录在异常里更好，但一版先静默跳过）。
                if (!file.IsSafePath)
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static IReadOnlyList<HfModelSummary> ParseSearchPage(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new HfException("HuggingFace 搜索结果不是合法 JSON（站点可能是镜像或需要登录）", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new HfException("HuggingFace 搜索结果格式意外（期望 JSON 数组）");
            }

            var results = new List<HfModelSummary>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("id", out var idElement) || idElement.GetString() is not { } id)
                {
                    continue;
                }

                // 搜索结果里可能混进组织名、数据集形状的 id：只要标准的两段 owner/name。
                if (!IsRepoId(id))
                {
                    continue;
                }

                var pipelineTag = item.TryGetProperty("pipeline_tag", out var tagElement)
                    && tagElement.ValueKind == JsonValueKind.String
                        ? tagElement.GetString()
                        : null;

                results.Add(new HfModelSummary
                {
                    Id = id,
                    Downloads = ReadInt64(item, "downloads"),
                    Likes = ReadInt64(item, "likes"),
                    PipelineTag = pipelineTag,
                });
            }

            return results;
        }
    }

    private static long? ReadInt64(JsonElement item, string name) =>
        item.TryGetProperty(name, out var element) && element.TryGetInt64(out var value) ? value : null;

    private static bool IsRepoId(string id) =>
        id.Split('/') is { Length: 2 } segments
        && segments.All(static segment =>
            segment.Length > 0
            && segment.IndexOfAny(['\\', '?', '#', ' ', '\t']) < 0);

    private static string? NextPageUrl(HttpResponseMessage response, HfRepo repo)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var header in values)
        {
            foreach (var part in header.Split(','))
            {
                if (!part.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)
                    && !part.Contains("rel=next", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var start = part.IndexOf('<');
                var end = part.IndexOf('>');
                if (start >= 0 && end > start)
                {
                    var next = part[(start + 1)..end];
                    // 只跟随本站地址，防止清单把请求带到别处。
                    if (next.StartsWith(repo.Endpoint, StringComparison.OrdinalIgnoreCase))
                    {
                        return next;
                    }
                }
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
