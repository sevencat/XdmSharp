using Xdm.Core.Http;

namespace Xdm.Core.Configuration;

/// <summary>
/// 用户设置（落盘为库目录下的 <c>settings.json</c>）。所有字段都带安全默认值，
/// 文件缺失或损坏时按默认值启动（<c>ARCHITECTURE.md</c> §7.1）。
/// </summary>
/// <remarks>
/// 限速与并发数可运行时生效；代理/重试参数在下次启动时生效（见 <c>docs/PROGRESS.md</c> M6 台账）。
/// </remarks>
public sealed class AppSettings
{
    /// <summary>当前设置文件结构版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>同时下载的任务数上限（1–32）。</summary>
    public int MaxConcurrent { get; set; } = 3;

    /// <summary>新任务的默认分段数（1–256）。</summary>
    public int MaxSegments { get; set; } = 8;

    /// <summary>全局限速（字节/秒）；0 表示不限速。</summary>
    public long SpeedLimitBytesPerSecond { get; set; }

    /// <summary>代理方式。</summary>
    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;

    /// <summary>自定义代理地址（仅 <see cref="ProxyMode.Custom"/> 时有意义）。</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>所有任务共享的默认请求头（任务级同名头优先）。</summary>
    public Dictionary<string, string> DefaultHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>新建任务的默认保存目录；为空时用用户 Downloads。</summary>
    public string? DefaultSaveFolder { get; set; }

    /// <summary>完成时是否提醒。</summary>
    public bool NotifyOnComplete { get; set; } = true;

    /// <summary>段级瞬时错误重试次数（0–10）。</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>重试退避基数（毫秒，50–5000；第 n 次等待 = base × 2^(n-1)）。</summary>
    public int RetryBaseDelayMs { get; set; } = 300;

    /// <summary>设置文件结构版本（向前兼容用）。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>深拷贝（调用方拿去做编辑，保存前不污染当前生效值）。</summary>
    public AppSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        MaxConcurrent = MaxConcurrent,
        MaxSegments = MaxSegments,
        SpeedLimitBytesPerSecond = SpeedLimitBytesPerSecond,
        ProxyMode = ProxyMode,
        ProxyUrl = ProxyUrl,
        DefaultHeaders = new Dictionary<string, string>(DefaultHeaders, StringComparer.OrdinalIgnoreCase),
        DefaultSaveFolder = DefaultSaveFolder,
        NotifyOnComplete = NotifyOnComplete,
        MaxRetries = MaxRetries,
        RetryBaseDelayMs = RetryBaseDelayMs,
    };

    /// <summary>
    /// 夹紧到合法范围并清洗请求头：手改 JSON、旧版本文件、UI 误输入都走这里，
    /// 保证后续代码拿到的值可用（不抛异常、不把 CR/LF 塞进请求头）。
    /// </summary>
    public AppSettings Normalized()
    {
        var mode = Enum.IsDefined(ProxyMode) ? ProxyMode : Http.ProxyMode.System;
        var proxyUrl = string.IsNullOrWhiteSpace(ProxyUrl) ? null : ProxyUrl.Trim();
        if (mode == Http.ProxyMode.Custom && !IsSupportedProxyUrl(proxyUrl))
        {
            // 地址不合法就退回系统代理，别把请求全指向一个坏代理。
            mode = Http.ProxyMode.System;
            proxyUrl = null;
        }

        return new AppSettings
        {
            SchemaVersion = Math.Max(1, SchemaVersion),
            MaxConcurrent = Math.Clamp(MaxConcurrent, 1, 32),
            MaxSegments = Math.Clamp(MaxSegments, 1, 256),
            SpeedLimitBytesPerSecond = Math.Clamp(SpeedLimitBytesPerSecond, 0, 1024L * 1024 * 1024),
            ProxyMode = mode,
            ProxyUrl = proxyUrl,
            DefaultHeaders = SanitizeHeaders(DefaultHeaders),
            DefaultSaveFolder = string.IsNullOrWhiteSpace(DefaultSaveFolder) ? null : DefaultSaveFolder.Trim(),
            NotifyOnComplete = NotifyOnComplete,
            MaxRetries = Math.Clamp(MaxRetries, 0, 10),
            RetryBaseDelayMs = Math.Clamp(RetryBaseDelayMs, 50, 5000),
        };
    }

    /// <summary>代理地址是否受支持（http/https/socks4/socks4a/socks5）。</summary>
    public static bool IsSupportedProxyUrl(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            return false;
        }

        return Uri.TryCreate(proxyUrl.Trim(), UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https" or "socks4" or "socks4a" or "socks5";
    }

    private static Dictionary<string, string> SanitizeHeaders(Dictionary<string, string>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return result;
        }

        foreach (var (name, value) in headers)
        {
            if (result.Count >= 64)
            {
                break;
            }

            var key = name?.Trim();
            var text = value?.Trim();
            if (string.IsNullOrEmpty(key) || text is null || !IsHeaderName(key) || text.Contains('\r') || text.Contains('\n'))
            {
                continue;
            }

            result[key] = text;
        }

        return result;
    }

    private static bool IsHeaderName(string name)
    {
        // RFC 7230 token：不允许空格、冒号、控制字符与非 ASCII。
        foreach (var c in name)
        {
            if (c <= ' ' || c >= 0x7F || c is ':' or '(' or ')' or '<' or '>' or '@' or ',' or ';' or '\\' or '"' or '/' or '[' or ']' or '?' or '=' or '{' or '}')
            {
                return false;
            }
        }

        return true;
    }
}
