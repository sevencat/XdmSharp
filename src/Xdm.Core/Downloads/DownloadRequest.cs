namespace Xdm.Core.Downloads;

using Xdm.Core.Storage;

/// <summary>
/// 新建下载的入参：UI 只产这个，任务 id、临时文件名、探测结果都由引擎/管理器决定。
/// </summary>
public sealed record DownloadRequest
{
    /// <summary>原始 URL（http/https）。</summary>
    public required string Url { get; init; }

    /// <summary>保存目录（存在性由调用方或管理器校验）。</summary>
    public required string TargetFolder { get; init; }

    /// <summary>指定文件名；留空则由响应头/URL 推导。</summary>
    public string? FileName { get; init; }

    /// <summary>分段上限 1–256，实际段数还要受大小与引擎闸门约束。</summary>
    public int MaxSegments { get; init; } = 8;

    /// <summary>是否立刻开始；<see langword="false"/> 时建成暂停态。</summary>
    public bool StartImmediately { get; init; } = true;

    /// <summary>重名策略；默认自动改名。</summary>
    public NameConflictPolicy NameConflict { get; init; } = NameConflictPolicy.Rename;

    /// <summary>任务种类（V2/V3 预留）；首期恒为 <see cref="TaskKind.Http"/>。</summary>
    public TaskKind Kind { get; init; } = TaskKind.Http;

    /// <summary>自定义请求头（随任务持久化）。</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>任务级 Cookie（随任务持久化）。</summary>
    public string? Cookie { get; init; }

    /// <summary>
    /// 代理选择：留空 = 用设置的默认代理；<c>system</c> / <c>direct</c> 或设置里某条代理的 id。
    /// 建任务时解析成具体路线，之后改设置不影响这个任务。
    /// </summary>
    public string? ProxyId { get; init; }

    /// <summary>期望内容 sha256（HF 的 LFS oid）；非空时发布前校验，不符则删 part 并报 <see cref="Xdm.Core.Errors.DownloadError.Checksum"/>。</summary>
    public string? ContentSha256 { get; init; }

    /// <summary>来源元数据 JSON（HF: endpoint/repo/revision/path），随任务持久化。</summary>
    public string? SourceMetaJson { get; init; }
}
