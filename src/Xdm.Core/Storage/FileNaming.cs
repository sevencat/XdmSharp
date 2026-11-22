using System.Net.Http.Headers;

namespace Xdm.Core.Storage;

/// <summary>
/// 文件名推导与清洗：<c>Content-Disposition</c> → URL 路径 → 兜底名，
/// 再处理非法字符、Windows 保留名与重名（<c>UI-DESIGN.md</c> §6）。
/// </summary>
public static class FileNaming
{
    /// <summary>推导不出名字时的兜底。</summary>
    public const string FallbackName = "download";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 从响应推导文件名：优先 <c>Content-Disposition</c>（含 RFC 5987 的 <c>filename*=</c>），
    /// 否则用最终 URL 的最后一段。
    /// </summary>
    public static string FromResponse(Uri finalUrl, string? contentDisposition)
        => Sanitize(FromDisposition(contentDisposition) ?? FromUrl(finalUrl) ?? FallbackName);

    /// <summary>只取 <c>Content-Disposition</c> 里的文件名；解析不出返回 <c>null</c>。</summary>
    public static string? FromDisposition(string? contentDisposition)
    {
        if (string.IsNullOrWhiteSpace(contentDisposition)
            || !ContentDispositionHeaderValue.TryParse(contentDisposition, out var parsed))
        {
            return null;
        }

        // FileNameStar 已经被框架做过去引号与百分号解码（filename*=UTF-8''%E4%B8%AD.zip）。
        var star = parsed.FileNameStar;
        if (!string.IsNullOrWhiteSpace(star))
        {
            var cleaned = CleanQuotes(star);
            if (cleaned.Length > 0)
            {
                return cleaned;
            }
        }

        var plain = parsed.FileName;
        if (!string.IsNullOrWhiteSpace(plain))
        {
            var cleaned = CleanQuotes(plain);
            if (cleaned.Length > 0)
            {
                return cleaned;
            }
        }

        return null;
    }

    /// <summary>取 URL 最后一段（解码 percent-encoding，丢掉查询串）。</summary>
    public static string? FromUrl(Uri url)
    {
        var absolute = url.AbsolutePath;
        if (absolute.Length == 0 || absolute.EndsWith('/'))
        {
            return null;
        }

        var path = absolute.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        var segment = slash >= 0 ? path[(slash + 1)..] : path;
        if (segment.Length == 0)
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(segment);
        }
        catch (UriFormatException)
        {
            decoded = segment;
        }

        return decoded.Length == 0 ? null : decoded;
    }

    /// <summary>清洗成合法文件名：去非法字符、保留名加前缀、限制长度（保留扩展名）。</summary>
    public static string Sanitize(string name)
    {
        var raw = CleanQuotes(name).Trim();
        if (raw.Length == 0)
        {
            return FallbackName;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        var cleaned = new string(chars).Trim().TrimEnd('.');
        if (cleaned.Length == 0)
        {
            return FallbackName;
        }

        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (ReservedNames.Contains(stem))
        {
            cleaned = "_" + cleaned;
        }

        return Truncate(cleaned, 200);
    }

    /// <summary>
    /// 按重名策略算出发布路径；<see cref="NameConflictPolicy.Fail"/> 且目标已存在时返回 <see langword="null"/>。
    /// </summary>
    public static string? ResolveTarget(string folder, string fileName, NameConflictPolicy policy)
    {
        var safe = Sanitize(fileName);
        var candidate = Path.Combine(folder, safe);
        return policy switch
        {
            NameConflictPolicy.Overwrite => candidate,
            NameConflictPolicy.Fail => File.Exists(candidate) ? null : candidate,
            _ => MakeUnique(folder, safe),
        };
    }

    /// <summary>
    /// 在目录里取一个不冲突的名字：<c>a.zip</c> → <c>a (1).zip</c>、<c>a (2).zip</c>…
    /// </summary>
    public static string MakeUnique(string folder, string fileName)
    {
        var safe = Sanitize(fileName);
        var candidate = Path.Combine(folder, safe);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(safe);
        var extension = Path.GetExtension(safe);
        for (var i = 1; i < 10_000; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private static string CleanQuotes(string value) => value.Trim().Trim('"').Trim();

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var extension = Path.GetExtension(value);
        if (extension.Length > 20)
        {
            extension = string.Empty;
        }

        var keep = Math.Max(1, maxLength - extension.Length);
        return value[..keep] + extension;
    }
}
