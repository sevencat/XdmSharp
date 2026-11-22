using Xdm.Core.Configuration;
using Xdm.Core.Http;

namespace Xdm.Ui.ViewModels;

/// <summary>
/// 设置窗口 ViewModel：把 <see cref="AppSettings"/> 摊平成可绑定控件，
/// 保存前做校验（代理地址、请求头、目录），失败只写 <see cref="ErrorText"/>，不关窗口。
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    /// <summary>限速输入上限（1 GB/s）。</summary>
    public const decimal MaxSpeedKbps = 1024m * 1024m;

    /// <summary>默认代理下拉/单选里的固定项数：系统代理、直连。</summary>
    public const int FixedProxyOptions = 2;

    private readonly AppSettings _source;

    private decimal _maxConcurrent;
    private decimal _maxSegments;
    private bool _speedLimitEnabled;
    private decimal _speedLimitKbps;
    private int _defaultProxyIndex;
    private string _defaultSaveFolder;
    private bool _notifyOnComplete;
    private decimal _maxRetries;
    private decimal _retryBaseDelayMs;
    private string _defaultHeadersText;
    private string _errorText = string.Empty;

    public SettingsViewModel(AppSettings settings, string dbPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _source = settings;

        _maxConcurrent = settings.MaxConcurrent;
        _maxSegments = settings.MaxSegments;
        _speedLimitEnabled = settings.SpeedLimitBytesPerSecond > 0;
        _speedLimitKbps = settings.SpeedLimitBytesPerSecond > 0
            ? Math.Max(1m, Math.Round(settings.SpeedLimitBytesPerSecond / 1024m, 1))
            : 512m;
        Proxies = [.. settings.Proxies.Select(static p => new ProxyEditRow { Id = p.Id, Name = p.Name, Url = p.Url })];
        _defaultProxyIndex = DefaultProxyIndexOf(settings, Proxies);
        _defaultSaveFolder = settings.DefaultSaveFolder ?? DefaultDownloadsFolder();
        _notifyOnComplete = settings.NotifyOnComplete;
        _maxRetries = settings.MaxRetries;
        _retryBaseDelayMs = settings.RetryBaseDelayMs;
        _defaultHeadersText = string.Join(
            Environment.NewLine,
            settings.DefaultHeaders.Select(static h => $"{h.Key}: {h.Value}"));

        DbPath = dbPath;
    }

    /// <summary>任务库位置（只读展示；配置也存在这个库的 settings 表里）。</summary>
    public string DbPath { get; }

    /// <summary>代理列表（可直接增删行；保存时按当前内容落库）。</summary>
    public List<ProxyEditRow> Proxies { get; }

    /// <summary>同时下载任务数（1–32）。</summary>
    public decimal MaxConcurrent
    {
        get => _maxConcurrent;
        set => SetProperty(ref _maxConcurrent, value);
    }

    /// <summary>新任务默认分段数（1–256）。</summary>
    public decimal MaxSegments
    {
        get => _maxSegments;
        set => SetProperty(ref _maxSegments, value);
    }

    /// <summary>是否启用全局限速。</summary>
    public bool SpeedLimitEnabled
    {
        get => _speedLimitEnabled;
        set => SetProperty(ref _speedLimitEnabled, value);
    }

    /// <summary>全局限速（KB/s）；仅在勾选限速时生效。</summary>
    public decimal SpeedLimitKbps
    {
        get => _speedLimitKbps;
        set => SetProperty(ref _speedLimitKbps, value);
    }

    /// <summary>
    /// 默认代理选项下标：0 = 系统代理，1 = 直连，2 + i = <see cref="Proxies"/> 第 i 条
    /// （与设置页单选按钮顺序一致）。
    /// </summary>
    public int DefaultProxyIndex
    {
        get => _defaultProxyIndex;
        set => SetProperty(ref _defaultProxyIndex, value);
    }

    /// <summary>新建任务的默认保存目录。</summary>
    public string DefaultSaveFolder
    {
        get => _defaultSaveFolder;
        set => SetProperty(ref _defaultSaveFolder, value ?? string.Empty);
    }

    /// <summary>完成时是否提醒。</summary>
    public bool NotifyOnComplete
    {
        get => _notifyOnComplete;
        set => SetProperty(ref _notifyOnComplete, value);
    }

    /// <summary>段级重试次数（0–10）。</summary>
    public decimal MaxRetries
    {
        get => _maxRetries;
        set => SetProperty(ref _maxRetries, value);
    }

    /// <summary>重试退避基数（毫秒，50–5000）。</summary>
    public decimal RetryBaseDelayMs
    {
        get => _retryBaseDelayMs;
        set => SetProperty(ref _retryBaseDelayMs, value);
    }

    /// <summary>默认请求头，每行一条 <c>Name: value</c>（也接受 <c>=</c> 分隔）。</summary>
    public string DefaultHeadersText
    {
        get => _defaultHeadersText;
        set => SetProperty(ref _defaultHeadersText, value ?? string.Empty);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _errorText.Length > 0;

    /// <summary>由窗口在保存回调失败时调用，显示外部错误（如磁盘只读）。</summary>
    public void ShowError(string message) => ErrorText = message ?? string.Empty;

    /// <summary>解析并校验当前输入；失败时返回 <see langword="false"/> 并写 <see cref="ErrorText"/>。</summary>
    public bool TryBuild(out AppSettings settings)
    {
        settings = _source.Clone();

        if (!TryBuildProxies(out var proxies, out var defaultProxyId, out var proxyError))
        {
            ErrorText = proxyError;
            return false;
        }

        if (!TryParseHeaders(DefaultHeadersText, out var headers, out var headerError))
        {
            ErrorText = headerError;
            return false;
        }

        var folder = DefaultSaveFolder.Trim();
        if (folder.Length == 0)
        {
            ErrorText = "默认保存目录不能为空";
            return false;
        }

        settings.MaxConcurrent = (int)Math.Clamp(MaxConcurrent, 1, 32);
        settings.MaxSegments = (int)Math.Clamp(MaxSegments, 1, 256);
        settings.SpeedLimitBytesPerSecond = SpeedLimitEnabled
            ? (long)Math.Round(Math.Clamp(SpeedLimitKbps, 1, MaxSpeedKbps) * 1024m)
            : 0;
        settings.Proxies = proxies;
        settings.DefaultProxyId = defaultProxyId;

        // 旧版单代理字段已经由 Proxies 取代，保存时清空避免再次迁移。
        settings.ProxyMode = ProxyMode.System;
        settings.ProxyUrl = null;

        settings.DefaultHeaders = headers;
        settings.DefaultSaveFolder = folder;
        settings.NotifyOnComplete = NotifyOnComplete;
        settings.MaxRetries = (int)Math.Clamp(MaxRetries, 0, 10);
        settings.RetryBaseDelayMs = (int)Math.Clamp(RetryBaseDelayMs, 50, 5000);
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;

        settings = settings.Normalized();
        ErrorText = string.Empty;
        return true;
    }

    /// <summary>把编辑行折成代理表 + 默认代理 id；空行忽略，地址非法直接报错。</summary>
    private bool TryBuildProxies(
        out List<ProxyProfile> proxies,
        out string? defaultProxyId,
        out string error)
    {
        proxies = [];
        defaultProxyId = null;
        error = string.Empty;

        // 记录「原行号 → 落库代理」，默认代理单选按原行号选中的才有意义。
        var kept = new List<(ProxyProfile Profile, int RowIndex)>();

        for (var i = 0; i < Proxies.Count; i++)
        {
            var row = Proxies[i];
            var name = (row.Name ?? string.Empty).Trim();
            var url = (row.Url ?? string.Empty).Trim();

            if (name.Length == 0 && url.Length == 0)
            {
                continue;
            }

            if (!AppSettings.IsSupportedProxyUrl(url))
            {
                error = string.IsNullOrEmpty(url)
                    ? $"第 {i + 1} 条代理缺少地址（示例：http://127.0.0.1:7890）"
                    : $"第 {i + 1} 条代理地址无效（示例：http://127.0.0.1:7890）";
                return false;
            }

            var profile = new ProxyProfile
            {
                Id = string.IsNullOrWhiteSpace(row.Id) ? Guid.NewGuid().ToString("N") : row.Id,
                Name = name.Length == 0 ? url : name,
                Url = url,
            };

            kept.Add((profile, i));
            proxies.Add(profile);
        }

        defaultProxyId = DefaultProxyIndex switch
        {
            <= 0 => null,
            1 => ProxyIds.Direct,
            _ => kept.FirstOrDefault(k => k.RowIndex == DefaultProxyIndex - FixedProxyOptions).Profile?.Id,
        };

        return true;
    }

    private static int DefaultProxyIndexOf(AppSettings settings, List<ProxyEditRow> rows)
    {
        if (string.Equals(settings.DefaultProxyId, ProxyIds.Direct, StringComparison.Ordinal))
        {
            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultProxyId))
        {
            return 0;
        }

        var index = rows.FindIndex(r => string.Equals(r.Id, settings.DefaultProxyId, StringComparison.Ordinal));
        return index < 0 ? 0 : index + FixedProxyOptions;
    }

    private static bool TryParseHeaders(
        string text,
        out Dictionary<string, string> headers,
        out string error)
    {
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                separator = line.IndexOf('=');
            }

            if (separator <= 0)
            {
                error = $"请求头格式应为「名称: 值」：{line}";
                return false;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Length == 0)
            {
                error = $"请求头缺少名称：{line}";
                return false;
            }

            if (name.Any(static c => c <= ' ' || c >= 0x7F || c == ':'))
            {
                error = $"请求头名称含非法字符：{name}";
                return false;
            }

            headers[name] = value;
        }

        return true;
    }

    private static string DefaultDownloadsFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads");
}
