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
    /// 空格/逗号分隔的多个关键字各查一次再按 id 取交集（HF 的 <c>search</c> 只认单个串，
    /// 「同时包含」只能客户端拼）。
    /// </summary>
    public async Task<IReadOnlyList<HfModelSummary>> SearchModelsAsync(
        string? endpoint,
        string? query,
        string? token,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var keywords = SplitKeywords(query);
        if (keywords.Count == 0)
        {
            return [];
        }

        var merged = await SearchOneAsync(endpoint, keywords[0], token, limit, cancellationToken)
            .ConfigureAwait(false);

        // 交集只可能越查越小：中间空掉就不用再发后面的请求了。
        for (var i = 1; i < keywords.Count && merged.Count > 0; i++)
        {
            var page = await SearchOneAsync(endpoint, keywords[i], token, limit, cancellationToken)
                .ConfigureAwait(false);
            merged = Intersect(merged, page);
        }

        return merged;
    }

    /// <summary>一次搜索最多识别的关键字个数（每个关键字一条请求，别把连接池占满）。</summary>
    public const int MaxKeywords = 5;

    /// <summary>关键字分隔符：空白（含制表/换行）、中英文逗号与分号。</summary>
    private static readonly char[] KeywordSeparators = [' ', '\t', '\r', '\n', ',', '，', ';', '；'];

    /// <summary>
    /// 把输入拆成关键字：按空白 / 逗号 / 分号分隔，去空、忽略大小写去重，最多 <see cref="MaxKeywords"/> 个。
    /// 只有一个关键字时等价于以前的「整串当关键字」。
    /// </summary>
    public static IReadOnlyList<string> SplitKeywords(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        List<string> keywords = [];
        foreach (var part in query.Split(
            KeywordSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!keywords.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                keywords.Add(part);
                if (keywords.Count >= MaxKeywords)
                {
                    break;
                }
            }
        }

        return keywords;
    }

    /// <summary>单个关键字的一次搜索（就是 <c>/api/models?search=…</c> 那一页）。</summary>
    private async Task<IReadOnlyList<HfModelSummary>> SearchOneAsync(
        string? endpoint,
        string keyword,
        string? token,
        int limit,
        CancellationToken cancellationToken)
    {
        var url = SearchUrl(endpoint, keyword, limit);
        using var response = await SendAsync(url, token, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseSearchPage(json);
    }

    /// <summary>按 id 求交集（大小写不敏感），统计量取两边的较大值，结果仍按下载量降序。</summary>
    private static List<HfModelSummary> Intersect(
        IReadOnlyList<HfModelSummary> left,
        IReadOnlyList<HfModelSummary> right)
    {
        var byId = new Dictionary<string, HfModelSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in right)
        {
            byId[model.Id] = model;
        }

        List<HfModelSummary> overlap = [];
        foreach (var model in left)
        {
            if (!byId.TryGetValue(model.Id, out var other))
            {
                continue;
            }

            overlap.Add(new HfModelSummary
            {
                Id = model.Id,
                Downloads = MaxOf(model.Downloads, other.Downloads),
                Likes = MaxOf(model.Likes, other.Likes),
                PipelineTag = model.PipelineTag ?? other.PipelineTag,
            });
        }

        overlap.Sort(static (a, b) => (b.Downloads ?? -1).CompareTo(a.Downloads ?? -1));
        return overlap;
    }

    /// <summary>两边的计数取大值；缺失一侧就用另一侧（原样保持 <see langword="null"/>）。</summary>
    private static long? MaxOf(long? left, long? right) => (left, right) switch
    {
        ({ } l, { } r) => Math.Max(l, r),
        ({ } l, null) => l,
        _ => right,
    };

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

    /// <summary>
    /// 拉仓库 README 原文（<c>GET {endpoint}/{owner}/{name}/raw/{revision}/README.md</c>）。
    /// 仓库没有 README（404）返回 <see langword="null"/>——这是常态，不算失败；
    /// 网络/限流等其它问题照常抛 <see cref="HfException"/>。
    /// </summary>
    public async Task<string?> GetReadmeAsync(HfRepo repo, string? token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        using var request = new HttpRequestMessage(HttpMethod.Get, repo.ReadmeUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        using var response = await SendRawAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw StatusError(response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        var response = await SendRawAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = response.StatusCode;
        response.Dispose();
        throw StatusError(status);
    }

    /// <summary>发请求并把网络层异常翻成能直接展示的 <see cref="HfException"/>；状态码交给调用方判断。</summary>
    private async Task<HttpResponseMessage> SendRawAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _http
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
    }

    private static HfException StatusError(HttpStatusCode status) =>
        new(status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "HuggingFace 拒绝访问：仓库可能是私有的，或需要先在网页上同意模型许可，然后在设置里填 token",
            HttpStatusCode.NotFound => "HuggingFace 仓库或版本不存在，检查模型 id 与 revision",
            HttpStatusCode.TooManyRequests => "HuggingFace 限流了，稍后再试",
            _ => $"HuggingFace 返回 {(int)status} {status}",
        });

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
