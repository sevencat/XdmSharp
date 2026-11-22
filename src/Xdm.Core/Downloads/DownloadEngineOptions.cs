using Xdm.Core.Scheduling;

namespace Xdm.Core.Downloads;

/// <summary>引擎调参（默认值面向普通家用宽带，测试里按需改小）。</summary>
public sealed class DownloadEngineOptions
{
    /// <summary>全局限速器；为空表示不限速。多个任务应共享同一个实例。</summary>
    public SpeedLimiter? Limiter { get; init; }

    /// <summary>
    /// 全局连接配额（同时在途的 HTTP 连接上限）；为空表示不限。
    /// 与 <see cref="Limiter"/> 一样，多个任务应共享同一个实例，否则"全局"名不副实。
    /// </summary>
    public ConnectionSlots? ConnectionSlots { get; init; }

    /// <summary>段数上限的引擎侧闸门（任务自身的 <see cref="DownloadTask.MaxSegments"/> 再与之取小）。</summary>
    public int MaxSegments { get; init; } = 8;

    /// <summary>低于这个长度就不再切分（256 KB，与参考实现一致）。</summary>
    public long MinSegmentSize { get; init; } = 256 * 1024;

    /// <summary>每次读的缓冲区大小。</summary>
    public int BufferSize { get; init; } = 64 * 1024;

    /// <summary>单次读超时（每次读之间重新计时，对应 Kotlin 的 readTimeoutSeconds）。</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>刷盘间隔：到点 fsync 一次，并把 <see cref="Segment.Synced"/> 推到 <see cref="Segment.Downloaded"/>。</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>瞬时错误的重试预算（429/503 的等待不计入）。</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>退避基数（第 n 次等待 = base * 2^(n-1)）。</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>进度上报间隔。</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>对端繁忙（429/503）的最多等待轮数，防止被无限拖着。</summary>
    public int MaxBusyWaits { get; init; } = 8;
}
