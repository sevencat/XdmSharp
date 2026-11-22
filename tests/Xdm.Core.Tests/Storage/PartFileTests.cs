using Xdm.Core.Storage;

namespace Xdm.Core.Tests.Storage;

public class PartFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xdm-part-tests", Guid.NewGuid().ToString("N"));

    public PartFileTests() => Directory.CreateDirectory(_root);

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
    public void BoundedFile_WritesAtArbitraryOffsets()
    {
        var path = Path.Combine(_root, "a.part");

        using (var part = PartFile.Create(path, 1024))
        {
            Assert.Equal(1024, part.Length);
            part.Write(10, [1, 2, 3]);
            part.Write(1020, [9]);
            part.FlushToDisk();
        }

        var data = File.ReadAllBytes(path);
        Assert.Equal(1024, data.Length);
        Assert.Equal([1, 2, 3], data[10..13]);
        Assert.Equal(9, data[1020]);
    }

    [Fact]
    public void BoundedFile_RejectsWritePastEnd()
    {
        var path = Path.Combine(_root, "b.part");
        using var part = PartFile.Create(path, 16);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => part.Write(15, [1, 2]));
        Assert.Equal("offset", error.ParamName);
    }

    [Fact]
    public void UnboundedFile_GrowsAsBytesArrive()
    {
        var path = Path.Combine(_root, "c.part");
        using (var part = PartFile.Create(path, null))
        {
            Assert.Equal(0, part.Length);
            part.Write(0, new byte[100]);
            Assert.Equal(100, part.Length);
            part.Write(100, new byte[50]);
            Assert.Equal(150, part.Length);
        }

        Assert.Equal(150, new FileInfo(path).Length);
    }

    [Fact]
    public void Dispose_ReleasesTheHandle()
    {
        var path = Path.Combine(_root, "d.part");
        var part = PartFile.Create(path, 32);
        Assert.True(part.IsOpen);

        part.Dispose();

        Assert.False(part.IsOpen);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(32, exclusive.Length);
    }

    [Fact]
    public void Open_ReadsExistingLength_AndAllowsResume()
    {
        var path = Path.Combine(_root, "e.part");
        using (var created = PartFile.Create(path, 64))
        {
            created.Write(0, "hello"u8);
        }

        using (var reopened = PartFile.Open(path))
        {
            Assert.Equal(64, reopened.Length);
            reopened.Write(8, "ok"u8);
            reopened.FlushToDisk();
        }

        var data = File.ReadAllBytes(path);
        Assert.Equal("hello"u8.ToArray(), data[..5]);
        Assert.Equal("ok"u8.ToArray(), data[8..10]);
    }
}
