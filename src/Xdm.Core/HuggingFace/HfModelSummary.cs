namespace Xdm.Core.HuggingFace;

/// <summary>
/// 模型搜索结果的一条（来自 <c>/api/models?search=…</c>，<c>ARCHITECTURE.md</c> §12.2）。
/// 只带列表要显示的字段；真正的文件清单仍按 <see cref="HfRepo"/> 拉。
/// </summary>
public sealed record HfModelSummary
{
    /// <summary>模型 id，形如 <c>owner/name</c>（已校验为两段）。</summary>
    public required string Id { get; init; }

    /// <summary>下载次数；服务端没给时为 <see langword="null"/>。</summary>
    public long? Downloads { get; init; }

    /// <summary>点赞数。</summary>
    public long? Likes { get; init; }

    /// <summary>任务类型（如 <c>text-generation</c>），可能为空。</summary>
    public string? PipelineTag { get; init; }
}
