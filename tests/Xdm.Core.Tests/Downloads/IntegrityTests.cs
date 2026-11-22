using System.Security.Cryptography;
using Xdm.Core.Downloads;
using Xdm.Core.Storage;
using Xdm.Core.Tests.Fakes;

namespace Xdm.Core.Tests.Downloads;

public class IntegrityTests
{
    [Fact]
    public void Sha256_KnownVectors()
    {
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            SegmentVerifier.ComputeSha256(ReadOnlySpan<byte>.Empty));

        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            SegmentVerifier.ComputeSha256("abc"u8));
    }

    [Fact]
    public void MultiSegmentDownload_MatchesSourceBytesExactly()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(1024 * 1024, seed: 123);
        harness.Server.AddFile("/sha.bin", bytes);

        var task = harness.CreateTask("/sha.bin", fileName: "sha.bin", maxSegments: 4);
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);

        Assert.True(result.Success);
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(result.FinalPath!)));
        Assert.True(SegmentVerifier.VerifyFile(result.FinalPath!, SegmentVerifier.ComputeSha256(bytes)));
    }

    [Fact]
    public void VerifyFile_ReturnsTrue_WhenNoExpectedHash()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(4096);
        harness.Server.AddFile("/nohash.bin", bytes);

        var task = harness.CreateTask("/nohash.bin", fileName: "nohash.bin");
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);

        Assert.True(result.Success);
        Assert.True(SegmentVerifier.VerifyFile(result.FinalPath!, null));
        Assert.False(SegmentVerifier.VerifyFile(result.FinalPath!, new string('0', 64)));
    }

    [Fact]
    public void Segments_DoNotOverlap_AndCoverWholeFile()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(700 * 1024, seed: 55);
        harness.Server.AddFile("/cover.bin", bytes);

        var task = harness.CreateTask("/cover.bin", fileName: "cover.bin", maxSegments: 4);
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);

        Assert.True(result.Success);
        var segments = task.Snapshot().OrderBy(static s => s.Offset).ToArray();
        var cursor = 0L;
        foreach (var segment in segments)
        {
            Assert.Equal(cursor, segment.Offset);
            Assert.True(segment.Length > 0);
            cursor += segment.Length;
        }

        Assert.Equal(bytes.Length, cursor);
        Assert.Equal(bytes.Length, task.SyncedTotal);
    }
}
