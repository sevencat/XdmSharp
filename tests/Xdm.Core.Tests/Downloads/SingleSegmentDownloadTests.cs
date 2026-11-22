using Xdm.Core.Downloads;
using Xdm.Core.Tests.Fakes;

namespace Xdm.Core.Tests.Downloads;

public class SingleSegmentDownloadTests
{
    [Fact]
    public void SmallFile_DownloadsSingleSegment_AndPublishesWithoutPartFile()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(100 * 1024);
        harness.Server.AddFile("/small.bin", bytes, new FileBehavior
        {
            ContentDisposition = "attachment; filename=\"small.bin\"",
        });

        var task = harness.CreateTask("/small.bin", fileName: "small.bin");
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);

        Assert.True(result.Success);
        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.NotNull(result.FinalPath);
        Assert.Equal(bytes, File.ReadAllBytes(result.FinalPath!));
        Assert.False(File.Exists(task.PartPath));
        Assert.Single(task.Snapshot());
        Assert.Single(harness.Observer.Completed);
        Assert.Empty(harness.Observer.Failed);
    }

    [Fact]
    public void FileName_ComesFromContentDisposition_WhenTaskHasNone()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(2048);
        harness.Server.AddFile("/download", bytes, new FileBehavior
        {
            ContentDisposition = "attachment; filename*=UTF-8''%E6%A8%A1%E5%9E%8B.bin",
        });

        var task = harness.CreateTask("/download");
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);

        Assert.True(result.Success);
        Assert.Equal("模型.bin", Path.GetFileName(result.FinalPath));
    }

    [Fact]
    public void EmptyFile_PublishesZeroByteFile()
    {
        using var harness = new EngineHarness();
        harness.Server.AddFile("/empty.txt", [], new FileBehavior { ContentDisposition = "attachment; filename=\"empty.txt\"" });

        var task = harness.CreateTask("/empty.txt", fileName: "empty.txt");
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);

        Assert.True(result.Success);
        Assert.Equal(0, new FileInfo(result.FinalPath!).Length);
    }

    [Fact]
    public void ServerWithoutRange_FallsBackToSingleStream()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(300 * 1024, seed: 11);
        harness.Server.AddFile("/norange.bin", bytes, new FileBehavior { SupportsRange = false });

        var task = harness.CreateTask("/norange.bin", fileName: "norange.bin", maxSegments: 4);
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);

        Assert.True(result.Success);
        Assert.True(task.NoRangeSupport);
        Assert.Single(task.Snapshot());
        Assert.Equal(bytes, File.ReadAllBytes(result.FinalPath!));
    }

    [Fact]
    public void UnknownLength_StreamsUntilEof()
    {
        using var harness = new EngineHarness();
        var bytes = EngineHarness.MakeBytes(120 * 1024, seed: 3);
        harness.Server.AddFile("/eof.bin", bytes, new FileBehavior
        {
            SupportsRange = false,
            OmitContentLength = true,
        });

        var task = harness.CreateTask("/eof.bin", fileName: "eof.bin");
        harness.Engine.Start(task);
        var result = harness.WaitForResult(task);

        Assert.True(result.Success);
        Assert.Equal(bytes, File.ReadAllBytes(result.FinalPath!));
        Assert.Equal(-1, Assert.Single(task.Snapshot()).Length);
    }
}
