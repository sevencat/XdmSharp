namespace Xdm.Core.Downloads;

/// <summary>
/// 多文件任务的入参（HF 模型包 / V3 torrent）：<see cref="Files"/> 各建一条子任务，
/// 另外建一条只做汇总的父行（<see cref="DownloadRecord.IsContainer"/>）。
/// </summary>
/// <remarks>
/// 父行不发起任何请求，所以它只需要「显示名 + 目标目录 + 来源元数据」；
/// 传输层的一切（分段、代理、校验）都由子任务按 <see cref="DownloadRequest"/> 各自决定。
/// </remarks>
public sealed record DownloadGroupRequest
{
    /// <summary>父行显示名（HF: <c>owner/name</c>）。</summary>
    public required string Name { get; init; }

    /// <summary>来源地址（仓库页 / 磁力链接），只用于展示与去重，不会被下载。</summary>
    public required string Url { get; init; }

    /// <summary>父行目标目录（HF: <c>{保存目录}/{owner}/{name}</c>）。</summary>
    public required string TargetFolder { get; init; }

    /// <summary>子任务的协议种类。</summary>
    public TaskKind Kind { get; init; } = TaskKind.Http;

    /// <summary>父行的来源元数据 JSON（随任务持久化，HF 里带 <c>container</c> 标记与文件数）。</summary>
    public string? SourceMetaJson { get; init; }

    /// <summary>父行记录用的代理选择（子任务各自带自己的）。</summary>
    public string? ProxyId { get; init; }

    /// <summary>子任务是否立刻开始；<see langword="false"/> 时父子都建成暂停态。</summary>
    public bool StartImmediately { get; init; } = true;

    /// <summary>子任务（路径已按仓库内相对目录展开好）。</summary>
    public IReadOnlyList<DownloadRequest> Files { get; init; } = [];
}
