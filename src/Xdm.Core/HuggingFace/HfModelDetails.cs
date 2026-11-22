namespace Xdm.Core.HuggingFace;

/// <summary>
/// 「模型详情」：下载前想知道的几件事——多少文件、总共多大、什么任务、README 里怎么介绍自己。
/// 文件统计来自清单（<see cref="HfFile"/>），简介来自 README 摘要（<see cref="HfReadme"/>）。
/// </summary>
public sealed record HfModelDetails
{
    /// <summary>模型 id（<c>owner/name</c>）。</summary>
    public required string Id { get; init; }

    /// <summary>清单里的文件数（不含目录/符号链接，与真正会入队的文件一致）。</summary>
    public required int FileCount { get; init; }

    /// <summary>清单里所有文件大小之和；个别文件没给大小时按 0 计。</summary>
    public required long TotalBytes { get; init; }

    /// <summary>任务类型（如 <c>text-generation</c>）；搜索结果没给就是空。</summary>
    public string? PipelineTag { get; init; }

    /// <summary>下载次数（来自搜索结果，可能为空）。</summary>
    public long? Downloads { get; init; }

    /// <summary>点赞数（来自搜索结果，可能为空）。</summary>
    public long? Likes { get; init; }

    /// <summary>README 开头一段；仓库没 README 或正文为空时为空。</summary>
    public string? Summary { get; init; }

    /// <summary>
    /// 用清单 + README 原文拼出详情。<paramref name="model"/> 可以为空（手填 id 直接拉详情的情况），
    /// 此时任务类型/下载/点赞都没有。
    /// </summary>
    public static HfModelDetails Create(
        HfRepo repo,
        HfModelSummary? model,
        IReadOnlyList<HfFile> files,
        string? readme)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(files);

        return new HfModelDetails
        {
            Id = repo.Repo,
            FileCount = files.Count,
            TotalBytes = files.Sum(static f => f.Size ?? 0),
            PipelineTag = model?.PipelineTag,
            Downloads = model?.Downloads,
            Likes = model?.Likes,
            Summary = HfReadme.Summarize(readme),
        };
    }
}
