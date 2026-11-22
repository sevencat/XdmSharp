using Xdm.Core.Downloads;
using Xdm.Core.Storage;

namespace Xdm.Core.Tests.Storage;

public class AtomicJsonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xdm-json-tests", Guid.NewGuid().ToString("N"));

    public AtomicJsonTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 忽略清理失败。
        }
    }

    [Fact]
    public void WriteThenRead_RoundTripsEnumsAndSegments()
    {
        var path = Path.Combine(_root, "state", "1.json");
        var state = new DownloadState
        {
            Id = 1,
            Url = "http://x/a.bin",
            FileName = "a.bin",
            TotalSize = 4096,
            Validator = "Sun, 22 Nov 2026 08:00:00 GMT",
            Segments =
            [
                new SegmentState { Id = 0, Offset = 0, Length = 2048, Synced = 1024, Status = SegmentStatus.Downloading },
                new SegmentState { Id = 1, Offset = 2048, Length = 2048, Synced = 2048, Status = SegmentStatus.Finished },
            ],
        };

        AtomicJson.Write(path, state);
        var loaded = AtomicJson.Read<DownloadState>(path);

        Assert.NotNull(loaded);
        Assert.Equal(4096, loaded!.TotalSize);
        Assert.Equal(2, loaded.Segments.Count);
        Assert.Equal(SegmentStatus.Finished, loaded.Segments[1].Status);
        Assert.Equal(1024, loaded.Segments[0].Synced);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Write_ReplacesExistingFileWithoutTempLeftovers()
    {
        var path = Path.Combine(_root, "1.json");
        AtomicJson.Write(path, new DownloadState { Id = 1, FileName = "old" });
        AtomicJson.Write(path, new DownloadState { Id = 1, FileName = "new" });

        Assert.Equal("new", AtomicJson.Read<DownloadState>(path)!.FileName);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Read_ReturnsNull_ForMissingOrCorruptFile()
    {
        Assert.Null(AtomicJson.Read<DownloadState>(Path.Combine(_root, "missing.json")));

        var corrupt = Path.Combine(_root, "corrupt.json");
        File.WriteAllText(corrupt, "{ not json");
        Assert.Null(AtomicJson.Read<DownloadState>(corrupt));
    }

    [Fact]
    public void Delete_IsSilentWhenFileMissing()
    {
        AtomicJson.Delete(Path.Combine(_root, "ghost.json"));
    }
}
