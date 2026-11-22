using PureDb;
using Xdm.Core.Configuration;
using Xdm.Core.Downloads;
using Xdm.Core.Http;
using Xdm.Core.Scheduling;
using Xdm.Core.Tasks;
using Xdm.Ui.Samples;
using Xdm.Ui.ViewModels;

namespace Xdm.App.Mew;

/// <summary>
/// MewUI 壳的组合根：复刻 Avalonia 版 <c>App.axaml.cs</c> 的装配顺序（设置先于引擎、事件订阅晚于构造），
/// 业务组件（<c>TaskStore</c> / <c>DownloadManager</c> / <c>AppPaths</c>）一律复用 <c>Xdm.Core</c>，不在此重写。
/// </summary>
internal sealed class MewRuntime : IDisposable
{
    private readonly TaskStore? _store;
    private readonly DbSettingsStore? _settingsStore;
    private readonly DownloadManager? _manager;
    private readonly RangeHttpClient? _client;
    private readonly SpeedLimiter? _limiter;

    private MewDownloadBridge? _bridge;
    private AppSettings _settings;
    private bool _disposed;

    private MewRuntime(
        AppPaths paths,
        bool isDemo,
        AppSettings settings,
        MainWindowViewModel viewModel,
        TaskStore? store = null,
        DbSettingsStore? settingsStore = null,
        DownloadManager? manager = null,
        RangeHttpClient? client = null,
        SpeedLimiter? limiter = null)
    {
        Paths = paths;
        IsDemo = isDemo;
        _settings = settings;
        ViewModel = viewModel;
        _store = store;
        _settingsStore = settingsStore;
        _manager = manager;
        _client = client;
        _limiter = limiter;
    }

    public AppPaths Paths { get; }

    /// <summary>是否演示模式（<c>XDM_DEMO</c> 存在即可，含 0 验空态）。</summary>
    public static bool IsDemoMode() => int.TryParse(Environment.GetEnvironmentVariable("XDM_DEMO"), out _);

    /// <summary>演示模式只跑假数据，不创建任务库（避免污染安装目录）。</summary>
    public bool IsDemo { get; }

    public MainWindowViewModel ViewModel { get; }

    /// <summary>引擎事件桥；<see cref="ConnectEngine"/> 之前为 <c>null</c>（演示模式永远为 <c>null</c>）。</summary>
    public MewDownloadBridge? Bridge => _bridge;

    public AppSettings Settings => _settings;

    /// <summary>最近一次落库失败的原因（引擎不会因此中断下载，只在这里暴露）。</summary>
    public string? PersistenceError => _manager?.PersistenceError;

    /// <summary>
    /// 解析路径并装配运行时。参数错误抛 <see cref="ArgumentException"/>，
    /// 任务库不可用抛 <see cref="InvalidOperationException"/>（调用方决定如何提示）。
    /// </summary>
    public static MewRuntime Create(string[] args) => Create(LaunchOptions.Parse(args));

    /// <summary>用已解析的启动参数装配（单实例闸门要先拿到库路径，所以解析被提到组合根外面）。</summary>
    public static MewRuntime Create(LaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var paths = options.Paths;

        // XDM_DEMO 存在即可（含 0 验空态）：未设置才走真实引擎，避免误建库。
        if (IsDemoMode())
        {
            var demoCount = int.TryParse(Environment.GetEnvironmentVariable("XDM_DEMO"), out var count) ? count : 0;
            var viewModel = new MainWindowViewModel(SampleDownloads.Create(Math.Max(demoCount, 0)))
            {
                DbPath = "(演示模式 · 未接任务库)",
            };

            return new MewRuntime(paths, isDemo: true, new AppSettings(), viewModel);
        }

        TaskStore store;
        try
        {
            store = new TaskStore(paths.DbPath);
        }
        catch (Exception ex) when (ex is SqlException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException($"任务库不可用：{paths.DbPath}{Environment.NewLine}{ex.Message}", ex);
        }

        try
        {
            // 配置存在任务库的 settings 表里（与任务同库同备份）；旧版 settings.json 只在库里还没有配置时迁一次。
            var settingsStore = new DbSettingsStore(store);
            settingsStore.ImportLegacyJson(paths.SettingsPath);
            var settings = settingsStore.Load();

            var limiter = new SpeedLimiter(settings.SpeedLimitBytesPerSecond);
            var client = new RangeHttpClient(proxyMode: settings.ProxyMode, proxyUrl: settings.ProxyUrl);
            var manager = new DownloadManager(new DownloadManagerOptions
            {
                StateDir = paths.StateDir,
                Store = store,
                Client = client,
                MaxConcurrent = settings.MaxConcurrent,
                EngineOptions = new DownloadEngineOptions
                {
                    Limiter = limiter,
                    MaxRetries = settings.MaxRetries,
                    RetryBaseDelay = TimeSpan.FromMilliseconds(settings.RetryBaseDelayMs),
                },
            })
            {
                DefaultHeaders = settings.DefaultHeaders,
            };

            var viewModel = new MainWindowViewModel(manager.Snapshot())
            {
                DbPath = paths.DbPath,
                Actions = new MewDownloadActions(manager),
            };

            return new MewRuntime(
                paths,
                isDemo: false,
                settings,
                viewModel,
                store,
                settingsStore,
                manager,
                client,
                limiter);
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 窗口进入消息循环后接引擎：<see cref="MewDownloadBridge"/> 里的 <c>DispatcherTimer</c>
    /// 必须在 UI 线程构造，所以从 <c>Application.Run(window, startup)</c> 的 startup 回调里调用。
    /// </summary>
    public void ConnectEngine()
    {
        if (_manager is not null && _bridge is null)
        {
            _bridge = new MewDownloadBridge(_manager, ViewModel);
        }
    }

    /// <summary>保存设置：先落盘，成功后才应用到运行中的引擎（失败把原因还给窗口）。</summary>
    public string? SaveSettings(AppSettings settings)
    {
        try
        {
            _settingsStore?.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"设置保存失败：{ex.Message}";
        }

        ApplySettings(settings);
        return null;
    }

    /// <summary>套用设置：并发/限速/默认头即时生效；代理与重试参数已固定在引擎构造时，下次启动生效。</summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _limiter?.SetRate(settings.SpeedLimitBytesPerSecond);

        if (_manager is not null)
        {
            _manager.MaxConcurrent = settings.MaxConcurrent;
            _manager.DefaultHeaders = settings.DefaultHeaders;
        }

        ViewModel.DefaultSegments = settings.MaxSegments;
        if (settings.DefaultSaveFolder is { Length: > 0 } folder)
        {
            ViewModel.DefaultSaveFolder = folder;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bridge?.Dispose();
        _manager?.Dispose();
        _client?.Dispose();
        _store?.Dispose();
    }
}
