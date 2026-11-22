using Avalonia;
using PureDb;
using SimdSkia.Avalonia;
using Xdm.Core.Configuration;
using Xdm.Core.Tasks;

namespace Xdm.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Startup.Paths = AppPaths.Resolve(args);
        }
        catch (ArgumentException ex)
        {
            // 参数错误必须显式失败：静默回退会让用户对着错误的库排查（ARCHITECTURE.md §7.1）
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        // 演示模式只跑假数据，不创建任务库（避免污染安装目录）。
        var demo = int.TryParse(Environment.GetEnvironmentVariable("XDM_DEMO"), out var demoCount) && demoCount > 0;
        if (!demo)
        {
            try
            {
                Startup.Store = new TaskStore(Startup.Paths.DbPath);
            }
            catch (Exception ex) when (ex is SqlException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // 库不可用时宁可显式失败，也不静默丢失任务列表。
                Console.Error.WriteLine($"任务库不可用：{Startup.Paths.DbPath}{Environment.NewLine}{ex.Message}");
                return 3;
            }
        }

        // 启动期唯一一行诊断：WinExe 无控制台，但重定向 stderr 时可用（也是 M0 的验收手段）
        Console.Error.WriteLine($"[startup] db={Startup.Paths.DbPath} state={Startup.Paths.StateDir} fallback={Startup.Paths.IsFallback}");
        Console.Error.Flush();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
