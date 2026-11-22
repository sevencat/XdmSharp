using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Xdm.Core.Configuration;
using Xdm.Core.Downloads;
using Xdm.Core.Http;
using Xdm.Core.Scheduling;
using Xdm.Ui.ViewModels;
using Xdm.Ui.Views;

namespace Xdm.App;

public partial class App : Application
{
    private DownloadManager? _manager;
    private DownloadUiBridge? _bridge;
    private RangeHttpClient? _client;
    private SpeedLimiter? _limiter;
    private ISettingsStore? _settingsStore;
    private AppSettings _settings = new();
    private MainWindowViewModel? _mainViewModel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = CreateViewModel();
            var window = new MainWindow(viewModel)
            {
                ConfirmOnExit = true,
            };
            window.SettingsRequested += (_, _) => ShowSettings(window);
            desktop.MainWindow = window;
            desktop.Exit += OnExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private MainWindowViewModel CreateViewModel()
    {
        // XDM_DEMO=1000 启动假数据，用来复验列表虚拟化；正常启动走真实引擎。
        var demo = Environment.GetEnvironmentVariable("XDM_DEMO");
        if (int.TryParse(demo, out var count) && count > 0)
        {
            _mainViewModel = new MainWindowViewModel(SampleDownloads.Create(count))
            {
                DbPath = Startup.Paths.DbPath,
            };
            return _mainViewModel;
        }

        // 设置先于引擎加载：并发/限速/代理/重试都是建引擎时的输入。
        // 配置存在任务库的 settings 表里（与任务同库同备份）；旧版 settings.json 只在库里还没有配置时迁一次。
        var store = Startup.Store ?? throw new InvalidOperationException("任务库未打开，无法加载设置");
        var settingsStore = new DbSettingsStore(store);
        settingsStore.ImportLegacyJson(Startup.Paths.SettingsPath);
        _settingsStore = settingsStore;
        _settings = settingsStore.Load();
        _limiter = new SpeedLimiter(_settings.SpeedLimitBytesPerSecond);
        _client = new RangeHttpClient(proxyMode: _settings.ProxyMode, proxyUrl: _settings.ProxyUrl);

        _manager = new DownloadManager(new DownloadManagerOptions
        {
            StateDir = Startup.Paths.StateDir,
            Store = Startup.Store,
            Client = _client,
            MaxConcurrent = _settings.MaxConcurrent,
            EngineOptions = new DownloadEngineOptions
            {
                Limiter = _limiter,
                MaxRetries = _settings.MaxRetries,
                RetryBaseDelay = TimeSpan.FromMilliseconds(_settings.RetryBaseDelayMs),
            },
        })
        {
            DefaultHeaders = _settings.DefaultHeaders,
        };

        var viewModel = new MainWindowViewModel(_manager.Snapshot())
        {
            DbPath = Startup.Paths.DbPath,
            Actions = new UiDownloadActions(_manager),
        };
        ApplySettings(viewModel, _settings);

        // 事件订阅晚于构造；启动恢复的行已经由 Snapshot() 带进列表，不会漏。
        _bridge = new DownloadUiBridge(_manager, viewModel);
        _mainViewModel = viewModel;
        return viewModel;
    }

    private void ShowSettings(Window owner)
    {
        var viewModel = new SettingsViewModel(_settings, Startup.Paths.DbPath);
        var dialog = new SettingsWindow(viewModel, SaveSettings);
        _ = dialog.ShowDialog(owner);
    }

    /// <summary>保存设置：先落盘，成功后才应用到运行中的引擎（失败把原因还给窗口）。</summary>
    private string? SaveSettings(AppSettings settings)
    {
        try
        {
            _settingsStore?.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"设置保存失败：{ex.Message}";
        }

        if (_mainViewModel is not null)
        {
            ApplySettings(_mainViewModel, settings);
        }
        else
        {
            _settings = settings;
        }

        return null;
    }

    private void ApplySettings(MainWindowViewModel viewModel, AppSettings settings)
    {
        _settings = settings;
        _limiter?.SetRate(settings.SpeedLimitBytesPerSecond);

        if (_manager is not null)
        {
            // 并发与默认头即时生效；代理/重试参数已在引擎构造时固定，下次启动生效。
            _manager.MaxConcurrent = settings.MaxConcurrent;
            _manager.DefaultHeaders = settings.DefaultHeaders;
        }

        viewModel.DefaultSegments = settings.MaxSegments;
        if (settings.DefaultSaveFolder is { Length: > 0 } folder)
        {
            viewModel.DefaultSaveFolder = folder;
        }
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _bridge?.Dispose();
        _manager?.Dispose();
        _client?.Dispose();
        Startup.Store?.Dispose();
    }
}
