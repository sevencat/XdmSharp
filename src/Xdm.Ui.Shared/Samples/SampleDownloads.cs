using Xdm.Core.Downloads;

namespace Xdm.Ui.Samples;

/// <summary>
/// M1 用的假数据（M4 换成 TaskStore + 引擎）。固定种子，保证每次启动列表一致、好对比截图。
/// 两个前端（Avalonia / MewUI）共用同一份，便于逐像素对照。
/// </summary>
public static class SampleDownloads
{
    /// <summary>
    /// 演示里的多文件任务父行 id（列表第 4 行）。给 <see cref="Children"/> 提供 4 个 safetensors 子文件，
    /// 用来在 <c>XDM_DEMO</c> 下肉眼验收「行首三角展开」。
    /// </summary>
    public const long GroupId = 4;

    private static readonly string[] Catalog =
    [
        "ubuntu-24.04-desktop-amd64.iso",
        "report-2026Q4.pdf",
        "dotnet-sdk-10.0.101-win-x64.zip",
        "VSCodeUserSetup-x64-1.96.exe",
        "model-00001-of-00004.safetensors",
        "family-photo-2026-11.zip",
        "resume.docx",
        "dataset-train.tar.gz",
        "conference-talk.mp4",
        "music-album.flac",
        "driver-nvidia-566.36.exe",
        "logs-2026-11-22.7z",
    ];

    public static IReadOnlyList<DownloadRecord> Create(int count)
    {
        var random = new Random(20261122);
        var now = DateTimeOffset.Now;
        var records = new List<DownloadRecord>(count);

        for (var i = 0; i < count; i++)
        {
            // 第 4 行留一个 HF 模型包：演示模式下点行首三角能看到子文件行。
            if (i == GroupId - 1 && count > GroupId - 1)
            {
                records.Add(Group(now));
                continue;
            }

            var status = i switch
            {
                0 or 1 or 2 => DownloadStatus.Downloading,
                3 => DownloadStatus.Connecting,
                4 or 5 => DownloadStatus.Paused,
                _ when i % 29 == 0 => DownloadStatus.Failed,
                _ => DownloadStatus.Completed,
            };

            var size = random.NextInt64(200_000, 8_000_000_000);
            var segments = random.Next(2) == 0 ? 8 : 16;
            var progress = status switch
            {
                DownloadStatus.Downloading => 0.05 + (random.NextDouble() * 0.9),
                DownloadStatus.Paused => random.NextDouble() * 0.8,
                DownloadStatus.Completed => 1d,
                _ => 0d,
            };
            var synced = (long)(size * progress);
            var speed = status is DownloadStatus.Downloading ? 1_200_000 + (random.NextDouble() * 11_000_000) : 0d;
            int? remaining = speed > 0 ? (int)Math.Max(1, (size - synced) / speed) : null;

            records.Add(new DownloadRecord
            {
                Id = i + 1,
                FileName = NameOf(i),
                Url = $"https://mirror.example.com/files/{NameOf(i)}",
                TargetFolder = @"D:\Downloads",
                SizeBytes = size,
                SyncedBytes = synced,
                SpeedBps = speed,
                RemainingSeconds = remaining,
                Status = status,
                SegmentCount = segments,
                CompletedSegments = (int)Math.Floor(segments * progress),
                ErrorText = status is DownloadStatus.Failed ? "连接被重置" : null,
                CompletedAt = status is DownloadStatus.Completed ? now.AddDays(-random.Next(1, 60)) : null,
            });
        }

        return records;
    }

    /// <summary>演示模式下的子文件快照（真实引擎走 <c>IDownloadActions.Children</c>）。</summary>
    public static IReadOnlyList<DownloadRecord> Children(long parentId)
        => parentId == GroupId ? GroupChildren() : [];

    private static DownloadRecord Group(DateTimeOffset now) => new()
    {
        Id = GroupId,
        // 与真实 HF 任务对齐：父行显示 owner/模型名，落盘在 {下载目录}/owner/模型名。
        FileName = "Qwen/Qwen2.5-7B-Instruct",
        Url = "https://huggingface.co/Qwen/Qwen2.5-7B-Instruct",
        TargetFolder = @"D:\Downloads\Qwen\Qwen2.5-7B-Instruct",
        SizeBytes = 15_200_000_000,
        SyncedBytes = 8_700_000_000,
        SpeedBps = 6_400_000,
        RemainingSeconds = 1016,
        Status = DownloadStatus.Downloading,
        IsContainer = true,
        ChildCount = 4,
        CompletedChildren = 2,
    };

    private static IReadOnlyList<DownloadRecord> GroupChildren()
    {
        var now = DateTimeOffset.Now;
        return
        [
            File(1, 0.9, DownloadStatus.Completed, 0, now.AddMinutes(-11)),
            File(2, 0.75, DownloadStatus.Completed, 0, now.AddMinutes(-8)),
            File(3, 0.31, DownloadStatus.Downloading, 6_400_000, null),
            File(4, 0, DownloadStatus.Queued, 0, null),
        ];

        static DownloadRecord File(int index, double progress, DownloadStatus status, double speed, DateTimeOffset? done)
        {
            const long size = 3_800_000_000;
            var synced = (long)(size * progress);
            return new DownloadRecord
            {
                Id = GroupId * 100 + index,
                ParentId = GroupId,
                FileName = $"model-0000{index}-of-00004.safetensors",
                Url = $"https://huggingface.co/Qwen/Qwen2.5-7B-Instruct/resolve/main/model-0000{index}-of-00004.safetensors",
                TargetFolder = @"D:\Downloads\Qwen\Qwen2.5-7B-Instruct",
                SizeBytes = size,
                SyncedBytes = synced,
                SpeedBps = speed,
                RemainingSeconds = speed > 0 ? (int)((size - synced) / speed) : null,
                Status = status,
                SegmentCount = 4,
                CompletedSegments = status is DownloadStatus.Completed ? 4 : (int)(progress * 4),
                CompletedAt = done,
            };
        }
    }

    private static string NameOf(int index)
    {
        var name = Catalog[index % Catalog.Length];
        var round = index / Catalog.Length;
        if (round == 0)
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        return $"{stem}-{round}{ext}";
    }
}
