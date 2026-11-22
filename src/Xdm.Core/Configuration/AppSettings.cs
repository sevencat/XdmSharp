using Xdm.Core.HuggingFace;
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
    /// <summary>
    /// 当前设置文件结构版本（2 起：单代理 → 具名代理列表；3 起：加 HuggingFace 站点/令牌；
    /// 4 起：加全局连接上限 <see cref="MaxConnections"/>；5 起：加单任务文件并发
    /// <see cref="MaxFilesPerTask"/>；6 起：加 HuggingFace 专用代理
    /// <see cref="HuggingFaceProxyId"/>）。
    /// </summary>
    public const int CurrentSchemaVersion = 6;

    /// <summary>同时下载的任务数上限（1–32）。</summary>
    public int MaxConcurrent { get; set; } = 3;

    /// <summary>新任务的默认分段数（1–256）。</summary>
    public int MaxSegments { get; set; } = 8;

    /// <summary>
    /// 全局连接上限（1–256）：所有任务的所有分段同时在途的 HTTP 连接总数，防止
    /// 「任务数 × 段数」打爆路由器（见 <c>ARCHITECTURE.md</c> §12.2 第 6 条）。
    /// 默认 32，刚好覆盖默认的 3 任务 × 8 段，不改变默认行为。
    /// </summary>
    public int MaxConnections { get; set; } = 32;

    /// <summary>
    /// 单个多文件任务（HF 模型包）里同时下载的文件数（1–16，默认 3）。
    /// HF 一个模型动辄几百个文件，若全丢进队列会长时间占满任务槽位、把小任务饿死；
    /// 这里是「同一父行下最多几个子任务在跑」的闸门（见 <c>ARCHITECTURE.md</c> §6.2）。
    /// </summary>
    public int MaxFilesPerTask { get; set; } = 3;

    /// <summary>全局限速（字节/秒）；0 表示不限速。</summary>
    public long SpeedLimitBytesPerSecond { get; set; }

    /// <summary>具名代理列表：可配多条，新建任务时挑一条用。</summary>
    public List<ProxyProfile> Proxies { get; set; } = [];

    /// <summary>
    /// 新任务的默认代理 id：<c>null</c> = 跟随系统，<see cref="ProxyIds.Direct"/> = 直连，
    /// 其余是 <see cref="Proxies"/> 里某条的 <see cref="ProxyProfile.Id"/>。
    /// </summary>
    public string? DefaultProxyId { get; set; }

    /// <summary>旧版单代理方式（≤1 版设置文件）；只在 <see cref="Normalized"/> 里迁进 <see cref="Proxies"/>。</summary>
    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;

    /// <summary>旧版自定义代理地址（≤1 版设置文件）；同上，仅迁移用。</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>所有任务共享的默认请求头（任务级同名头优先）。</summary>
    public Dictionary<string, string> DefaultHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>HuggingFace 站点根；填镜像（如 <c>https://hf-mirror.com</c>）时清单与文件都走它。</summary>
    public string HuggingFaceEndpoint { get; set; } = HfRepo.DefaultEndpoint;

    /// <summary>
    /// HuggingFace 访问令牌（私有 / gated 仓库需要）；留空时再读环境变量 <c>HF_TOKEN</c>。
    /// </summary>
    public string? HuggingFaceToken { get; set; }

    /// <summary>
    /// HuggingFace 请求（搜索 / 文件清单 / 模型包下载）默认走的代理 id：
    /// <c>null</c> = 跟随 <see cref="DefaultProxyId"/>，<see cref="ProxyIds.Direct"/> = 直连，
    /// 其余是 <see cref="Proxies"/> 里某条的 <see cref="ProxyProfile.Id"/>。
    /// </summary>
    /// <remarks>
    /// HF 站点与 CDN 常需要单独穿墙（与普通下载的默认代理不是同一条），所以这里允许另配；
    /// 不配时行为与加这个字段之前完全一致。
    /// </remarks>
    public string? HuggingFaceProxyId { get; set; }

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
        MaxConnections = MaxConnections,
        MaxFilesPerTask = MaxFilesPerTask,
        SpeedLimitBytesPerSecond = SpeedLimitBytesPerSecond,
        Proxies = [.. Proxies],
        DefaultProxyId = DefaultProxyId,
        ProxyMode = ProxyMode,
        ProxyUrl = ProxyUrl,
        DefaultHeaders = new Dictionary<string, string>(DefaultHeaders, StringComparer.OrdinalIgnoreCase),
        HuggingFaceEndpoint = HuggingFaceEndpoint,
        HuggingFaceToken = HuggingFaceToken,
        HuggingFaceProxyId = HuggingFaceProxyId,
        DefaultSaveFolder = DefaultSaveFolder,
        NotifyOnComplete = NotifyOnComplete,
        MaxRetries = MaxRetries,
        RetryBaseDelayMs = RetryBaseDelayMs,
    };

    /// <summary>
    /// 夹紧到合法范围并清洗请求头/代理表：手改 JSON、旧版本文件、UI 误输入都走这里，
    /// 保证后续代码拿到的值可用（不抛异常、不把 CR/LF 塞进请求头）。
    /// </summary>
    public AppSettings Normalized()
    {
        var proxies = SanitizeProxies(Proxies, ProxyMode, ProxyUrl);

        // 旧版「全局自定义代理」迁进来后必须当默认用，否则升级后悄悄变回系统代理。
        var defaultProxyId = NormalizeProxyId(DefaultProxyId, proxies)
            ?? (proxies.Any(p => string.Equals(p.Id, LegacyProxyId, StringComparison.Ordinal)) ? LegacyProxyId : null);
        var huggingFaceProxyId = NormalizeHuggingFaceProxyId(HuggingFaceProxyId, proxies);

        return new AppSettings
        {
            SchemaVersion = Math.Max(CurrentSchemaVersion, SchemaVersion),
            MaxConcurrent = Math.Clamp(MaxConcurrent, 1, 32),
            MaxSegments = Math.Clamp(MaxSegments, 1, 256),
            MaxConnections = Math.Clamp(MaxConnections, 1, 256),
            MaxFilesPerTask = Math.Clamp(MaxFilesPerTask, 1, 16),
            SpeedLimitBytesPerSecond = Math.Clamp(SpeedLimitBytesPerSecond, 0, 1024L * 1024 * 1024),
            Proxies = proxies,
            DefaultProxyId = defaultProxyId,

            // 旧字段已经迁进 Proxies，保存时留空，别让下次启动再迁一遍。
            ProxyMode = ProxyMode.System,
            ProxyUrl = null,

            DefaultHeaders = SanitizeHeaders(DefaultHeaders),
            HuggingFaceEndpoint = SanitizeEndpoint(HuggingFaceEndpoint),
            HuggingFaceToken = SanitizeToken(HuggingFaceToken),
            HuggingFaceProxyId = huggingFaceProxyId,
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

    /// <summary>HuggingFace 站点根清洗：非 http/https 回默认站点，去掉尾斜杠与 query/fragment。</summary>
    public static string SanitizeEndpoint(string? endpoint)
    {
        var text = (endpoint ?? string.Empty).Trim();
        if (text.Length == 0 || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return HfRepo.DefaultEndpoint;
        }

        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}";
    }

    /// <summary>token 清洗：去空白；含控制字符或空格（手改配置粘坏了）整条丢弃。</summary>
    public static string? SanitizeToken(string? token)
    {
        var text = (token ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > 4096)
        {
            return null;
        }

        return text.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c)) ? null : text;
    }

    /// <summary>
    /// 清洗代理表：丢掉地址非法的条目、补 id/名字、按 id 去重（最多 32 条）。
    /// 旧版单代理配置在这里迁进来（<c>legacy</c> 这条 id 固定，重复调用不会翻倍）。
    /// </summary>
    private static List<ProxyProfile> SanitizeProxies(List<ProxyProfile>? proxies, ProxyMode legacyMode, string? legacyUrl)
    {
        var result = new List<ProxyProfile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (proxies is not null)
        {
            foreach (var profile in proxies)
            {
                if (result.Count >= 32 || profile is null || !IsSupportedProxyUrl(profile.Url))
                {
                    continue;
                }

                var url = profile.Url.Trim();
                var id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id.Trim();
                if (!seen.Add(id))
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(profile.Name) ? url : profile.Name.Trim();
                result.Add(new ProxyProfile { Id = id, Name = name, Url = url });
            }
        }

        if (result.Count == 0 && legacyMode == ProxyMode.Custom && IsSupportedProxyUrl(legacyUrl))
        {
            result.Add(new ProxyProfile { Id = LegacyProxyId, Name = "默认代理", Url = legacyUrl!.Trim() });
        }

        return result;
    }

    /// <summary>默认代理 id 清洗：指向不存在/非法的代理时退回系统代理。</summary>
    private static string? NormalizeProxyId(string? proxyId, List<ProxyProfile> proxies)
    {
        if (string.IsNullOrWhiteSpace(proxyId))
        {
            return null;
        }

        var id = proxyId.Trim();
        if (id is ProxyIds.System or ProxyIds.Direct)
        {
            return id == ProxyIds.System ? null : ProxyIds.Direct;
        }

        return proxies.Any(p => string.Equals(p.Id, id, StringComparison.Ordinal)) ? id : null;
    }

    /// <summary>
    /// HF 代理 id 清洗：空 / <see cref="ProxyIds.System"/> = 跟随默认代理（<c>null</c>）；
    /// <see cref="ProxyIds.Direct"/> = 直连；其余必须是现存代理，被删了就退回跟随默认代理。
    /// </summary>
    /// <remarks>
    /// 老设置文件没有这个字段，读出来是 <c>null</c> → 跟随默认代理，与加字段之前的行为一致。
    /// </remarks>
    private static string? NormalizeHuggingFaceProxyId(string? proxyId, List<ProxyProfile> proxies)
    {
        var id = (proxyId ?? string.Empty).Trim();
        if (id.Length == 0 || string.Equals(id, ProxyIds.System, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.Equals(id, ProxyIds.Direct, StringComparison.Ordinal))
        {
            return ProxyIds.Direct;
        }

        return proxies.Any(p => string.Equals(p.Id, id, StringComparison.Ordinal)) ? id : null;
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
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text) || !IsHeaderName(key) || text.Contains('\r') || text.Contains('\n'))
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

    /// <summary>旧版自定义代理迁移后固定用的 id。</summary>
    public const string LegacyProxyId = "legacy";
}
