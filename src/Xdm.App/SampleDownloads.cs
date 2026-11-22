using Xdm.Core.Downloads;

namespace Xdm.App;

/// <summary>
/// M1 用的假数据（M4 换成 TaskStore + 引擎）。固定种子，保证每次启动列表一致、好对比截图。
/// </summary>
internal static class SampleDownloads
{
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
