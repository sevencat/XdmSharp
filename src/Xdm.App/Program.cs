using Avalonia;
using PureDb;
using SimdSkia.Avalonia;
using Xdm.Core.Configuration;
using Xdm.Core.Shell;
using Xdm.Core.Tasks;

namespace Xdm.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        LaunchOptions options;
        try
        {
            options = LaunchOptions.Parse(args);
            Startup.Paths = options.Paths;
        }
        catch (ArgumentException ex)
        {
            // 参数错误必须显式失败：静默回退会让用户对着错误的库排查（ARCHITECTURE.md §7.1）
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        // 演示模式只跑假数据，不创建任务库（避免污染安装目录）。
        var demo = int.TryParse(Environment.GetEnvironmentVariable("XDM_DEMO"), out var demoCount) && demoCount > 0;
        Startup.InitialUrl = options.Url;

        // 单实例闸门要先于「打开任务库」：第二个实例不该去碰主实例正占着的库。
        SingleInstanceHost? primary = null;
        if (!demo)
        {
            primary = SingleInstanceHost.TryAcquire(options.Paths.DbPath, Startup.PostActivation);
            if (primary is null)
            {
                // 已有实例在跑：把链接递过去（没链接就是请它把窗口提到前面），自己退出。
                var delivered = SingleInstanceHost.TrySend(options.Paths.DbPath, options.Url);
                Console.Error.WriteLine($"[startup] second-instance delivered={delivered}");
                Console.Error.Flush();
                return delivered ? 0 : 4;
            }
        }

        try
        {
            if (!demo)
            {
                try
                {
                    Startup.Store = new TaskStore(options.Paths.DbPath);
                }
                catch (Exception ex) when (ex is SqlException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // 库不可用时宁可显式失败，也不静默丢失任务列表。
                    Console.Error.WriteLine($"任务库不可用：{options.Paths.DbPath}{Environment.NewLine}{ex.Message}");
                    return 3;
                }
            }

            // 启动期唯一一行诊断：WinExe 无控制台，但重定向 stderr 时可用（也是 M0 的验收手段）
            Console.Error.WriteLine(
                $"[startup] db={options.Paths.DbPath} state={options.Paths.StateDir} fallback={options.Paths.IsFallback} url={options.Url ?? "-"}");
            Console.Error.Flush();

            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            primary?.Dispose();
        }
    }

    /// <summary>
    /// 唯一的启动链。SimdSkia 走软件 framebuffer，Win32 渲染模式必须只留 Software；
    /// 渲染优先 Direct2D，失败回退 CPU。
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseStandardRuntimePlatformSubsystem()
            .UseWin32()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .UseSimdSkia(static options => options.UseGpuPreferred())
            .LogToTrace();
}
