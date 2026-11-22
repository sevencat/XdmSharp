using System.Text.Json;
using Xdm.Core.Downloads;
using Xdm.Core.Storage;

namespace Xdm.Core.HuggingFace;

/// <summary>选中文件的落盘计划（目标目录、URL 与是否有本地同大小文件）。</summary>
public sealed record HfPlannedFile(HfFile File, string Url, string TargetFolder);

/// <summary>
/// 一次拉清单的结果：<see cref="RootFolder"/> 是 <c>{保存目录}/{owner}/{name}</c>，
/// <see cref="TotalBytes"/> 只算要下载的部分（不含跳过的）。
/// </summary>
public sealed record HfPlan(
    string RootFolder,
    IReadOnlyList<HfPlannedFile> Files,
    int SkippedCount,
    long TotalBytes);

/// <summary>
/// 把选中的仓库文件变成下载任务（<c>ARCHITECTURE.md</c> §12.1）：一条父行（<c>owner/name</c>，只做汇总）
/// + 每个文件一条 <see cref="TaskKind.HuggingFace"/> 子任务，目标目录按
/// <c>{保存目录}/{owner}/{name}/&lt;仓库内相对目录&gt;</c> 展开；
/// 同名文件走 <see cref="NameConflictPolicy.Overwrite"/>（模型目录是镜像，不能改成 <c>x (1).onnx</c>）。
/// </summary>
public static class HfDownloadPlanner
{
    /// <summary>仓库根目录：保留 owner 一级，避免不同 owner 下的同名模型互相覆盖。</summary>
    public static string RootFolder(string saveRoot, HfRepo repo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveRoot);
        ArgumentNullException.ThrowIfNull(repo);

        return Path.Combine(saveRoot, repo.Owner, repo.Name);
    }

    /// <summary>
    /// 规划落盘位置。<paramref name="skipExisting"/> 时，本地已有同大小文件算跳过（断点重下的第二遍就能秒过）。
    /// </summary>
    public static HfPlan Plan(HfRepo repo, IEnumerable<HfFile> files, string saveRoot, bool skipExisting = true)
    {
        var root = RootFolder(saveRoot, repo);
        var planned = new List<HfPlannedFile>();
        var skipped = 0;
        long total = 0;

        foreach (var file in files)
        {
            if (!file.IsSafePath)
            {
                continue;
            }

            var folder = Path.Combine(root, file.RelativeFolder ?? string.Empty);
            var target = Path.Combine(folder, file.FileName);

            if (skipExisting && File.Exists(target))
            {
                // 大小已知就按大小判；大小未知只在文件非空时算跳过。
                var length = new FileInfo(target).Length;
                if (file.Size is not { } size || length == size)
                {
                    skipped++;
                    continue;
                }
            }

            planned.Add(new HfPlannedFile(file, repo.ResolveUrl(file.Path), folder));
            total += file.Size ?? 0;
        }

        return new HfPlan(root, planned, skipped, total);
    }

    /// <summary>生成下载请求；<paramref name="plan"/> 里用到的子目录会先建好（引擎直接往目标目录写 <c>.part</c>）。</summary>
    public static IReadOnlyList<DownloadRequest> ToRequests(
        HfPlan plan,
        int maxSegments,
        string? proxyId,
        string? token,
        bool startImmediately = true)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var requests = new List<DownloadRequest>(plan.Files.Count);
        var headers = string.IsNullOrWhiteSpace(token)
            ? null
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {token.Trim()}",
            };

        foreach (var item in plan.Files)
        {
            TryCreateDirectory(item.TargetFolder);

            requests.Add(new DownloadRequest
            {
                Url = item.Url,
                TargetFolder = item.TargetFolder,
                FileName = item.File.FileName,
                MaxSegments = maxSegments,
                StartImmediately = startImmediately,
                // 模型目录是仓库镜像：大小变了就是文件更新，要覆盖，不能变成 model (1).onnx
                // （否则推理框架按原名加载会读不到）。大小一致的文件已被 Plan 跳过。
                NameConflict = NameConflictPolicy.Overwrite,
                Kind = TaskKind.HuggingFace,
                ProxyId = proxyId,
                Headers = headers,
                ContentSha256 = item.File.Sha256,
                SourceMetaJson = JsonSerializer.Serialize(new
                {
                    kind = "huggingface",
                    path = item.File.Path,
                    size = item.File.Size,
                    sha256 = item.File.Sha256,
                }),
            });
        }

        return requests;
    }

    /// <summary>
    /// 生成多文件任务：父行显示 <c>owner/name</c>、落在仓库根目录，汇总信息写在
    /// <see cref="DownloadGroupRequest.SourceMetaJson"/> 里（含 <c>container</c> 标记，重启也能认出父行）。
    /// </summary>
    public static DownloadGroupRequest ToGroup(
        HfRepo repo,
        HfPlan plan,
        int maxSegments,
        string? proxyId,
        string? token,
        bool startImmediately = true)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(plan);

        return new DownloadGroupRequest
        {
            Name = $"{repo.Owner}/{repo.Name}",
            Url = $"{repo.Endpoint}/{repo.Owner}/{repo.Name}",
            TargetFolder = plan.RootFolder,
            Kind = TaskKind.HuggingFace,
            ProxyId = proxyId,
            StartImmediately = startImmediately,
            SourceMetaJson = JsonSerializer.Serialize(new
            {
                kind = "huggingface",
                repo = repo.Repo,
                revision = repo.Revision,
                endpoint = repo.Endpoint,
                container = true,
                files = plan.Files.Count,
                skipped = plan.SkippedCount,
                bytes = plan.TotalBytes,
            }),
            Files = ToRequests(plan, maxSegments, proxyId, token, startImmediately),
        };
    }

    private static void TryCreateDirectory(string folder)
    {
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }
    }
}
