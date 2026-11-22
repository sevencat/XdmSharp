using Xdm.Core.HuggingFace;

namespace Xdm.Core.Tests.HuggingFace;

public sealed class HfModelDetailsTests
{
    [Fact]
    public void Create_SumsFileSizes_AndSkipsUnknownSizes()
    {
        var repo = Repo();
        HfFile[] files =
        [
            new() { Path = "config.json", Size = 100 },
            new() { Path = "onnx/model.onnx", Size = 2_000 },
            new() { Path = "mystery.bin", Size = null },
        ];

        var details = HfModelDetails.Create(repo, null, files, "hello");

        Assert.Equal("owner/name", details.Id);
        Assert.Equal(3, details.FileCount);
        Assert.Equal(2_100, details.TotalBytes);
        Assert.Equal("hello", details.Summary);
        Assert.Null(details.PipelineTag);
        Assert.Null(details.Downloads);
    }

    [Fact]
    public void Create_TakesStatsFromSearchResult()
    {
        var model = new HfModelSummary
        {
            Id = "owner/name",
            Downloads = 12_345,
            Likes = 67,
            PipelineTag = "text-generation",
        };

        var details = HfModelDetails.Create(Repo(), model, [], null);

        Assert.Equal(0, details.FileCount);
        Assert.Equal(0, details.TotalBytes);
        Assert.Equal("text-generation", details.PipelineTag);
        Assert.Equal(12_345, details.Downloads);
        Assert.Equal(67, details.Likes);
        Assert.Null(details.Summary);
    }

    private static HfRepo Repo() => new() { Endpoint = "https://hf.test", Repo = "owner/name" };
}
