using Aprillz.MewUI;
using Xdm.Core.Configuration;
using Xdm.Core.Downloads;
using Xdm.Core.Shell;
using Xdm.Ui.Mew;

namespace Xdm.App.Mew;

/// <summary>
/// MewUI 壳：只做平台注册、运行时装配与窗口启动，不写业务（对应 Avalonia 版的 <c>Program</c> + <c>App.axaml.cs</c>）。
/// 后端只有 Direct2D —— 包引用决定了别的后端根本不在产物里。
/// </summary>
internal static class Program
{
    private static readonly object Gate = new();
    private static readonly Queue<string?> PendingActivations = new();
    private static Action<string?>? _activationHandler;
    private static bool _ready;

    [STAThread]
    private static int Main(string[] args)
    {
        Win32Platform.Register();
        Direct2DBackend.Register();

        LaunchOptions options;
        try
        {
            options = LaunchOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            // 参数错误必须显式失败：静默回退会让用户对着错误的库排查（ARCHITECTURE.md §7.1）
            Fail(ex.Message);
            return 2;
        }

        // 单实例闸门要先于「打开任务库」：第二个实例不该去碰主实例正占着的库。
        SingleInstanceHost? primary = null;
        if (!MewRuntime.IsDemoMode())
        {
            primary = SingleInstanceHost.TryAcquire(options.Paths.DbPath, PostActivation);
            if (primary is null)
            {
                // 已有实例在跑：把链接递过去（没链接就是请它把窗口提到前面），自己退出。
                var delivered = SingleInstanceHost.TrySend(options.Paths.DbPath, options.Url);
                Console.Error.WriteLine($"[startup] second-instance delivered={delivered}");
                Console.Error.Flush();
                if (!delivered)
                {
                    // 递不过去就是真问题（主实例卡死/权限），必须告诉用户，否则会以为链接丢了。
                    NativeMessageBox.Show(
                        "XDM Sharp 已在运行，但没能把链接交给它。请到主窗口手动新建下载。",
                        "XDM Sharp",
                        NativeMessageBoxButtons.Ok,
                        NativeMessageBoxIcon.Warning);
                }

                return delivered ? 0 : 4;
            }
        }

        try
        {
            MewRuntime runtime;
            try
            {
                runtime = MewRuntime.Create(options);
            }
            catch (InvalidOperationException ex)
            {
                // 库不可用时宁可显式失败，也不静默丢失任务列表。
                Fail(ex.Message);
                return 3;
            }

            // 启动期唯一一行诊断：WinExe 无控制台，但重定向 stderr 时可用（也是 M0 的验收手段）
            Console.Error.WriteLine(
                $"[startup] db={runtime.Paths.DbPath} state={runtime.Paths.StateDir} fallback={runtime.Paths.IsFallback} demo={runtime.IsDemo} url={options.Url ?? "-"}");
            Console.Error.Flush();

            try
            {
                runtime.ViewModel.OpenFolderRequested += (_, folder) => Shell.OpenFolder(folder);

                var window = new MainWindow(runtime.ViewModel)
                {
                    Settings = runtime.Settings,
                    SaveSettings = runtime.SaveSettings,
                    ConfirmOnExit = true,
                };

                TrayIcon? tray = null;

                // 引擎事件桥在 startup 回调里建：DispatcherTimer 必须在消息循环的 UI 线程上构造。
                Application.Run(window, () =>
                {
                    runtime.ConnectEngine();

                    if (runtime.Bridge is { } bridge)
                    {
                        bridge.TaskCompleted += record => NotifyCompleted(window, tray, runtime, record);
                    }

                    // startup 回调跑在主窗口真正显示之前：此时弹模态框会让主窗口永远等不到显示
                    // （实测只剩「新建下载」一个窗口）。等首帧画完再处理链接与转发请求。
                    void OnFirstFrame()
                    {
                        window.FirstFrameRendered -= OnFirstFrame;

                        var dispatcher = Application.Current?.Dispatcher;
                        dispatcher?.BeginInvoke(() =>
                        {
                            // 托盘图标要消息泵已经在跑（自定义 message-only 窗口的 WndProc 靠它派发）。
                            tray = TryAttachTray(window);

                            if (!string.IsNullOrWhiteSpace(options.Url))
                            {
                                Activate(window, options.Url);
                            }

                            // 首帧之后第二个实例的请求才可以直接切到 UI 线程处理，之前到达的都排在队里。
                            MarkReady(url => Activate(window, url));
                        });
                    }

                    window.FirstFrameRendered += OnFirstFrame;
                });

                tray?.Dispose();
                return 0;
            }
            finally
            {
                runtime.Dispose();
            }
        }
        finally
        {
            primary?.Dispose();
        }
    }

    /// <summary>第二个实例的诉求（首帧之前到达的先排队，别在窗口还没显示时弹模态框）。</summary>
    private static void PostActivation(string? url)
    {
        Action<string?>? handler;
        lock (Gate)
        {
            if (!_ready || _activationHandler is null)
            {
                PendingActivations.Enqueue(url);
                return;
            }

            handler = _activationHandler;
        }

        handler(url);
    }

    /// <summary>主窗口首帧画完：接管后续请求，并补发此前攒下的那些。</summary>
    private static void MarkReady(Action<string?> handler)
    {
        string?[] pending;
        lock (Gate)
        {
            _activationHandler = handler;
            _ready = true;
            pending = PendingActivations.ToArray();
            PendingActivations.Clear();
        }

        foreach (var url in pending)
        {
            handler(url);
        }
    }

    /// <summary>把主窗口带到前台；带链接时顺便弹「新建下载」。</summary>
    private static void Activate(MainWindow window, string? url)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (!dispatcher.IsOnUIThread)
        {
            dispatcher.BeginInvoke(() => Activate(window, url));
            return;
        }

        if (window.WindowState != Aprillz.MewUI.Controls.WindowState.Normal)
        {
            window.Restore();
        }

        window.Activate();

        if (!string.IsNullOrWhiteSpace(url))
        {
            window.BeginNewDownload(url);
        }
    }

    /// <summary>
    /// 托盘入口：左键/双击把窗口提到前面，右键菜单可打开窗口或退出。失败只记日志——托盘是锦上添花，
    /// 不能让「Shell_NotifyIcon 不可用」（比如被组策略禁掉）连累主界面。
    /// </summary>
    private static TrayIcon? TryAttachTray(MainWindow window)
    {
        try
        {
            return TrayIcon.Attach("XDM Sharp", () => Activate(window, null), () => window.Close());
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"[tray] 托盘图标不可用：{ex.Message}");
            Console.Error.Flush();
            return null;
        }
    }

    /// <summary>
    /// 完成提示：设置里关掉就不提示；窗口在前台时也不打扰（用户正看着那一行变成「已完成」），
    /// 其余情况用托盘气泡 + 任务栏闪烁。
    /// </summary>
    private static void NotifyCompleted(MainWindow window, TrayIcon? tray, MewRuntime runtime, DownloadRecord record)
    {
        if (tray is null || !runtime.Settings.NotifyOnComplete || ForegroundWindow.Is(window.Handle))
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(record.FileName) ? record.Url : record.FileName;
        tray.ShowBalloon("下载完成", name);
        TrayIcon.FlashTaskbar(window.Handle);
    }

    private static void Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.Flush();
        NativeMessageBox.Show(message, "XDM Sharp", NativeMessageBoxButtons.Ok, NativeMessageBoxIcon.Error);
    }
}
