namespace Xdm.Core.Downloads;

/// <summary>任务状态（与 <c>UI-DESIGN.md</c> §4 的状态徽标一一对应）。</summary>
public enum DownloadStatus
{
    /// <summary>排队等待连接。</summary>
    Queued,

    /// <summary>正在连接 / 探测文件大小。</summary>
    Connecting,

    /// <summary>下载中。</summary>
    Downloading,

    /// <summary>已暂停（保留 <c>.xdm-part</c> 与快照）。</summary>
    Paused,

    /// <summary>已完成并发布。</summary>
    Completed,

    /// <summary>失败（可重试）。<see cref="DownloadRecord.ErrorText"/> 给出原因。</summary>
    Failed,

    /// <summary>磁盘错误（空间不足 / 目录不可写），需要人工处理。</summary>
    DiskError,
}
