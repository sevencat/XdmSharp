using System.Text.Json;
using Xdm.Core.Downloads;
using Xdm.Core.HuggingFace;
using Xdm.Core.Storage;

namespace Xdm.Core.Tests.HuggingFace;

public sealed class HfDownloadPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "xdm-hf-" + Guid.NewGuid().ToString("N"));

    private static readonly HfRepo Repo = new()
    {
        Endpoint = "https://huggingface.co",
        Repo = "meta-llama/Llama-3.1-8B",
    };

    public HfDownloadPlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Plan_ExpandsOwnerNameAndSubfolders()
    {
        var files = new HfFile[]
        {
            new() { Path = "config.json", Size = 100 },
            new() { Path = "onnx/model.onnx", Size = 200 },
        };

        var plan = HfDownloadPlanner.Plan(Repo, files, _root);

        Assert.Equal(Path.Combine(_root, "meta-llama", "Llama-3.1-8B"), plan.RootFolder);
        Assert.Equal(2, plan.Files.Count);
        Assert.Equal(300, plan.TotalBytes);
        Assert.Equal(0, plan.SkippedCount);

        var nested = plan.Files[1];
        Assert.Equal(Path.Combine(plan.RootFolder, "onnx"), nested.TargetFolder);
        Assert.Equal("model.onnx", nested.File.FileName);
        Assert.Equal(
            "https://huggingface.co/meta-llama/Llama-3.1-8B/resolve/main/onnx/model.onnx?download=true",
            nested.Url);
    }

    [Fact]
    public void Plan_SkipsExistingFileWithSameSize()
    {
        var folder = Path.Combine(_root, "meta-llama", "Llama-3.1-8B");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "config.json"), new byte[100]);

        var plan = HfDownloadPlanner.Plan(Repo, [new HfFile { Path = "config.json", Size = 100 }], _root);

        Assert.Empty(plan.Files);
        Assert.Equal(1, plan.SkippedCount);
    }

    [Fact]
    public void Plan_ReDownloadsWhenLocalSizeDiffers()
    {
        var folder = Path.Combine(_root, "meta-llama", "Llama-3.1-8B");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "config.json"), new byte[10]);

        var plan = HfDownloadPlanner.Plan(Repo, [new HfFile { Path = "config.json", Size = 100 }], _root);

        Assert.Single(plan.Files);
        Assert.Equal(0, plan.SkippedCount);
    }

    [Theory]
    [InlineData("../evil.bin")]
    [InlineData("/abs.bin")]
    [InlineData("a//b.bin")]
    [InlineData("a\\b.bin")]
    public void Plan_DropsUnsafePaths(string path)
    {
        var plan = HfDownloadPlanner.Plan(Repo, [new HfFile { Path = path }], _root);

        Assert.Empty(plan.Files);
    }

    [Fact]
    public void ToRequests_SetsKindProxyTokenAndSha()
    {
        var plan = HfDownloadPlanner.Plan(
            Repo,
            [new HfFile { Path = "onnx/model.onnx", Size = 200, Sha256 = "deadbeef" }],
            _root);

        var request = Assert.Single(HfDownloadPlanner.ToRequests(plan, 8, "corp", "hf_secret"));

        Assert.Equal(TaskKind.HuggingFace, request.Kind);
        Assert.Equal(NameConflictPolicy.Overwrite, request.NameConflict);
        Assert.Equal("corp", request.ProxyId);
        Assert.Equal("model.onnx", request.FileName);
        Assert.Equal("deadbeef", request.ContentSha256);
        Assert.Equal("Bearer hf_secret", request.Headers!["Authorization"]);

        using var meta = JsonDocument.Parse(request.SourceMetaJson!);
        Assert.Equal("huggingface", meta.RootElement.GetProperty("kind").GetString());
        Assert.Equal("onnx/model.onnx", meta.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void ToRequests_OmitsAuthorizationWhenNoToken()
    {
        var plan = HfDownloadPlanner.Plan(Repo, [new HfFile { Path = "a.bin", Size = 1 }], _root);

        var request = Assert.Single(HfDownloadPlanner.ToRequests(plan, 8, null, null));

        Assert.Null(request.Headers);
        Assert.Null(request.ContentSha256);
    }

    [Fact]
    public void ToRequests_CreatesSubfoldersBeforeDownload()
    {
        var plan = HfDownloadPlanner.Plan(Repo, [new HfFile { Path = "onnx/model.onnx", Size = 1 }], _root);

        HfDownloadPlanner.ToRequests(plan, 8, null, null);

        Assert.True(Directory.Exists(Path.Combine(_root, "meta-llama", "Llama-3.1-8B", "onnx")));
    }
}
