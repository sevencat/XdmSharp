using Xdm.Core.Storage;

namespace Xdm.Core.Tests.Storage;

public class FileNamingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xdm-name-tests", Guid.NewGuid().ToString("N"));

    public FileNamingTests() => Directory.CreateDirectory(_root);

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

    [Theory]
    [InlineData("attachment; filename=\"model.zip\"", "model.zip")]
    [InlineData("attachment; filename=plain.tar.gz", "plain.tar.gz")]
    [InlineData("inline; filename*=UTF-8''%E6%A8%A1%E5%9E%8B.bin", "模型.bin")]
    public void FromDisposition_HandlesQuotedAndRfc5987(string header, string expected)
    {
        Assert.Equal(expected, FileNaming.FromDisposition(header));
    }

    [Fact]
    public void FromDisposition_ReturnsNull_WhenUnparsable()
    {
        Assert.Null(FileNaming.FromDisposition(null));
        Assert.Null(FileNaming.FromDisposition("not a disposition"));
    }

    [Fact]
    public void FromUrl_DecodesPercentEscapes_AndIgnoresQuery()
    {
        var url = new Uri("https://example.com/files/%E6%A8%A1%E5%9E%8B.zip?token=abc");
        Assert.Equal("模型.zip", FileNaming.FromUrl(url));
    }

    [Fact]
    public void FromUrl_ReturnsNull_WhenUrlEndsWithSlash()
    {
        Assert.Null(FileNaming.FromUrl(new Uri("https://example.com/files/")));
    }

    [Fact]
    public void FromResponse_PrefersDisposition_ThenUrl()
    {
        var url = new Uri("https://example.com/a/real.bin");
        Assert.Equal("named.bin", FileNaming.FromResponse(url, "attachment; filename=\"named.bin\""));
        Assert.Equal("real.bin", FileNaming.FromResponse(url, null));
    }

    [Fact]
    public void Sanitize_ReplacesInvalidCharacters_AndReservedNames()
    {
        Assert.Equal("a_b_c_.txt", FileNaming.Sanitize("a:b*c?.txt"));
        Assert.Equal("_CON.txt", FileNaming.Sanitize("CON.txt"));
        Assert.Equal(FileNaming.FallbackName, FileNaming.Sanitize("   "));
    }

    [Fact]
    public void Sanitize_TruncatesLongNames_KeepingExtension()
    {
        var name = new string('x', 300) + ".bin";
        var sanitized = FileNaming.Sanitize(name);

        Assert.True(sanitized.Length <= 200);
        Assert.EndsWith(".bin", sanitized);
    }

    [Fact]
    public void ResolveTarget_FollowsConflictPolicy()
    {
        var existing = Path.Combine(_root, "a.bin");
        File.WriteAllText(existing, "x");

        Assert.Equal(Path.Combine(_root, "a (1).bin"), FileNaming.ResolveTarget(_root, "a.bin", NameConflictPolicy.Rename));
        Assert.Equal(existing, FileNaming.ResolveTarget(_root, "a.bin", NameConflictPolicy.Overwrite));
        Assert.Null(FileNaming.ResolveTarget(_root, "a.bin", NameConflictPolicy.Fail));

        Assert.Equal(Path.Combine(_root, "b.bin"), FileNaming.ResolveTarget(_root, "b.bin", NameConflictPolicy.Fail));
        Assert.Equal(Path.Combine(_root, "b.bin"), FileNaming.ResolveTarget(_root, "b.bin", NameConflictPolicy.Rename));
    }

    [Fact]
    public void ResolveTarget_SanitizesBeforeCheckingConflicts()
    {
        File.WriteAllText(Path.Combine(_root, "a_b_.bin"), "x");
        Assert.Null(FileNaming.ResolveTarget(_root, "a:b?.bin", NameConflictPolicy.Fail));
    }

    [Fact]
    public void MakeUnique_AppendsCounter_WhenFileExists()
    {
        var first = Path.Combine(_root, "a.bin");
        File.WriteAllText(first, "x");

        var second = FileNaming.MakeUnique(_root, "a.bin");
        Assert.Equal(Path.Combine(_root, "a (1).bin"), second);

        File.WriteAllText(second, "x");
        Assert.Equal(Path.Combine(_root, "a (2).bin"), FileNaming.MakeUnique(_root, "a.bin"));
    }
}
