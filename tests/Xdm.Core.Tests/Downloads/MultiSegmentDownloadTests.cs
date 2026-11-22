using Xdm.Core.Downloads;
using Xdm.Core.Tests.Fakes;

namespace Xdm.Core.Tests.Downloads;

public class MultiSegmentDownloadTests
{
    private const int FileSize = 1024 * 1024;

    [Fact]
    public void OneMegabyte_SplitsIntoFourContiguousSegments()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(FileSize);
        harness.Server.AddFile("/big.bin", bytes);

        var task = harness.CreateTask("/big.bin", fileName: "big.bin", maxSegments: 4);
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);

        Assert.True(result.Success);
        Assert.Equal(bytes, File.ReadAllBytes(result.FinalPath!));

        var segments = task.Snapshot();
        Assert.Equal(4, segments.Count);
        Assert.Equal(0, segments[0].Offset);
        for (var i = 1; i < segments.Count; i++)
        {
            Assert.Equal(segments[i - 1].EndExclusive, segments[i].Offset);
        }

        Assert.Equal(FileSize, segments[^1].EndExclusive);
        Assert.All(segments, s => Assert.Equal(SegmentStatus.Finished, s.Status));
    }

    [Fact]
    public void Observer_ReceivesSegmentLayout_WithTotalCoverage()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(FileSize, seed: 5);
        harness.Server.AddFile("/layout.bin", bytes);

        var task = harness.CreateTask("/layout.bin", fileName: "layout.bin", maxSegments: 4);
        harness.Engine.Start(task);
        harness.WaitForResult(task);

        Assert.NotEmpty(harness.Observer.SegmentUpdates);
        var layout = harness.Observer.SegmentUpdates[0];
        Assert.Equal(4, layout.Count);
        Assert.Equal(FileSize, layout.Sum(s => s.Length));
    }

    [Fact]
    public void EachSegmentRequestsItsOwnRange()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(FileSize, seed: 9);
        harness.Server.AddFile("/ranges.bin", bytes);

        var task = harness.CreateTask("/ranges.bin", fileName: "ranges.bin", maxSegments: 4);
        harness.Engine.Start(task);
        harness.WaitForResult(task);

        var starts = harness.Server.Requests
            .Where(r => r.Range is not null)
            .Select(r => ParseStart(r.Range!))
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        // 探测发 0-0，4 个段起点 0、256K、512K、768K。
        Assert.Contains(0L, starts);
        Assert.Contains(256L * 1024, starts);
        Assert.Contains(512L * 1024, starts);
        Assert.Contains(768L * 1024, starts);
    }

    private static long ParseStart(string range)
    {
        var spec = range["bytes=".Length..];
        var dash = spec.IndexOf('-');
        return long.Parse(spec[..dash]);
    }
}
