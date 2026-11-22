using Xdm.Core.Configuration;
using Xdm.Core.Tasks;

namespace Xdm.App;

/// <summary>启动期解析结果，供 <see cref="App"/> 消费。</summary>
internal static class Startup
{
    public static AppPaths Paths { get; set; } = null!;

    /// <summary>任务库；演示模式（<c>XDM_DEMO</c>）下为 <see langword="null"/>。</summary>
    public static TaskStore? Store { get; set; }
}
