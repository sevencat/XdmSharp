using Xdm.Core.Configuration;
using Xdm.Core.Tasks;

namespace Xdm.App;

/// <summary>启动期解析结果，供 <see cref="App"/> 消费。</summary>
internal static class Startup
{
    private static readonly object Gate = new();
    private static readonly Queue<string?> PendingActivations = new();
    private static Action<string?>? _activationHandler;

    public static AppPaths Paths { get; set; } = null!;

    /// <summary>命令行带上来的链接（由本进程自己处理）。</summary>
    public static string? InitialUrl { get; set; }

    /// <summary>任务库；演示模式（<c>XDM_DEMO</c>）下为 <see langword="null"/>。</summary>
    public static TaskStore? Store { get; set; }

    /// <summary>第二个实例的诉求（单实例管道回调，可能在主窗口建好之前就到达）。</summary>
    public static void PostActivation(string? url)
    {
        Action<string?>? handler;
        lock (Gate)
        {
            if (_activationHandler is null)
            {
                PendingActivations.Enqueue(url);
                return;
            }

            handler = _activationHandler;
        }

        handler(url);
    }

    /// <summary>主窗口建好后接管后续请求，并补发启动期攒下的那些。</summary>
    public static void SetActivationHandler(Action<string?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        string?[] pending;
        lock (Gate)
        {
            _activationHandler = handler;
            pending = PendingActivations.ToArray();
            PendingActivations.Clear();
        }

        foreach (var url in pending)
        {
            handler(url);
        }
    }
}
