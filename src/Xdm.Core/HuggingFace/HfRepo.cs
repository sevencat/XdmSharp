namespace Xdm.Core.HuggingFace;

/// <summary>
/// HuggingFace 仓库坐标：<c>endpoint + owner/name + revision</c>（<c>ARCHITECTURE.md</c> §12.1）。
/// 清单走 <see cref="FilesApiUrl"/>，文件内容走 <see cref="ResolveUrl"/>。
/// </summary>
public sealed record HfRepo
{
    /// <summary>官方站点；镜像通过设置覆盖（形如 <c>https://hf-mirror.com</c>）。</summary>
    public const string DefaultEndpoint = "https://huggingface.co";

    /// <summary>站点根，无尾斜杠。</summary>
    public required string Endpoint { get; init; }

    /// <summary><c>owner/name</c>（两段，已校验）。</summary>
    public required string Repo { get; init; }

    /// <summary>分支 / tag / commit；默认 <c>main</c>。</summary>
    public string Revision { get; init; } = "main";

    public string Owner => Repo[..Repo.IndexOf('/')];

    public string Name => Repo[(Repo.IndexOf('/') + 1)..];

    /// <summary>递归列文件的 API 地址（分页由响应头 <c>Link</c> 给下一页）。</summary>
    public string FilesApiUrl =>
        $"{Endpoint}/api/models/{Owner}/{Name}/tree/{Uri.EscapeDataString(Revision)}?recursive=true&expand=true";

    /// <summary>README 原文地址（详情页摘要用）；仓库没有 README 时服务端回 404。</summary>
    public string ReadmeUrl => $"{Endpoint}/{Owner}/{Name}/raw/{Uri.EscapeDataString(Revision)}/README.md";

    /// <summary>单文件下载地址；<c>?download=true</c> 让服务端回 <c>Content-Disposition: attachment</c>。</summary>
    public string ResolveUrl(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var escaped = string.Join(
            '/',
            path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

        return $"{Endpoint}/{Owner}/{Name}/resolve/{Uri.EscapeDataString(Revision)}/{escaped}?download=true";
    }

    /// <summary>
    /// 解析用户输入：<c>owner/name</c>、完整站点 URL（可粘贴模型页地址），
    /// 以及带 <c>.git</c> 后缀的克隆地址。<paramref name="endpoint"/> 为设置里的默认站点。
    /// </summary>
    public static bool TryParse(string? text, string? endpoint, string revision, out HfRepo repo, out string error)
    {
        repo = null!;
        error = string.Empty;

        var raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            error = "请填写模型 id，例如 meta-llama/Llama-3.1-8B";
            return false;
        }

        var site = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim().TrimEnd('/');

        // 完整 URL：以用户粘贴的站点为准（可能就是从镜像页复制的），路径前两段是 owner/name。
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            site = $"{uri.Scheme}://{uri.Authority}";
            raw = uri.AbsolutePath;
        }

        var segments = raw
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // 去掉站点前缀残留与 .git。
        if (segments.Count >= 2 && segments[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            segments[^1] = segments[^1][..^4];
        }

        if (segments.Count < 2)
        {
            error = "模型 id 要写成 owner/模型名，例如 meta-llama/Llama-3.1-8B";
            return false;
        }

        var owner = segments[0];
        var name = segments[1];
        if (!IsValidSegment(owner) || !IsValidSegment(name))
        {
            error = $"模型 id 不合法：{owner}/{name}";
            return false;
        }

        // 模型页 URL 里 owner/name 之后可能跟 /tree/main 之类，直接丢掉。
        if (!Uri.TryCreate(site, UriKind.Absolute, out var siteUri)
            || siteUri.Scheme is not ("http" or "https"))
        {
            error = $"HuggingFace 站点地址不合法：{site}";
            return false;
        }

        repo = new HfRepo
        {
            Endpoint = site,
            Repo = $"{owner}/{name}",
            Revision = string.IsNullOrWhiteSpace(revision) ? "main" : revision.Trim(),
        };
        return true;
    }

    /// <summary>URL 里的 revision 可能与参数不同（模型页 URL 带 /tree/v2.0 时用它）。</summary>
    public static string RevisionFromUrl(string? text, string fallback)
    {
        var raw = (text ?? string.Empty).Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return fallback;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (segments[i] is "tree" or "resolve" or "blob")
            {
                return segments[i + 1];
            }
        }

        return fallback;
    }

    private static bool IsValidSegment(string value) =>
        value.Length > 0 &&
        value is not ("." or "..") &&
        value.IndexOfAny(['/', '\\', '?', '#', ' ', '\t']) < 0;
}
