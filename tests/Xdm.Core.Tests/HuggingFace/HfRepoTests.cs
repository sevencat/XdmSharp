using Xdm.Core.HuggingFace;

namespace Xdm.Core.Tests.HuggingFace;

public sealed class HfRepoTests
{
    [Fact]
    public void TryParse_AcceptsOwnerName()
    {
        Assert.True(HfRepo.TryParse("meta-llama/Llama-3.1-8B", null, "main", out var repo, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal("meta-llama", repo.Owner);
        Assert.Equal("Llama-3.1-8B", repo.Name);
        Assert.Equal("main", repo.Revision);
        Assert.Equal(HfRepo.DefaultEndpoint, repo.Endpoint);
    }

    [Fact]
    public void TryParse_AcceptsModelPageUrl_AndKeepsItsSite()
    {
        Assert.True(HfRepo.TryParse(
            "https://hf-mirror.com/Qwen/Qwen3-8B/tree/v2.0", null, "main", out var repo, out _));

        Assert.Equal("Qwen/Qwen3-8B", repo.Repo);
        Assert.Equal("https://hf-mirror.com", repo.Endpoint);
        Assert.Equal("v2.0", HfRepo.RevisionFromUrl("https://hf-mirror.com/Qwen/Qwen3-8B/tree/v2.0", "main"));
    }

    [Fact]
    public void TryParse_StripsGitSuffix()
    {
        Assert.True(HfRepo.TryParse("owner/model.git", null, "main", out var repo, out _));
        Assert.Equal("owner/model", repo.Repo);
    }

    [Fact]
    public void TryParse_UsesConfiguredEndpoint()
    {
        Assert.True(HfRepo.TryParse("owner/model", "https://hf-mirror.com/", "dev", out var repo, out _));
        Assert.Equal("https://hf-mirror.com", repo.Endpoint);
        Assert.Equal("dev", repo.Revision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nodash")]
    [InlineData("owner/")]
    [InlineData("https://huggingface.co")]
    public void TryParse_RejectsInvalidInput(string text)
    {
        Assert.False(HfRepo.TryParse(text, null, "main", out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void FilesApiUrl_UsesRevisionAndExpansion()
    {
        var repo = new HfRepo { Endpoint = "https://huggingface.co", Repo = "owner/name", Revision = "v1.0" };

        Assert.Equal(
            "https://huggingface.co/api/models/owner/name/tree/v1.0?recursive=true&expand=true",
            repo.FilesApiUrl);
    }

    [Fact]
    public void ResolveUrl_EscapesPathSegmentsButKeepsSlashes()
    {
        var repo = new HfRepo { Endpoint = "https://huggingface.co", Repo = "owner/name" };

        Assert.Equal(
            "https://huggingface.co/owner/name/resolve/main/onnx/model%20v2.onnx?download=true",
            repo.ResolveUrl("onnx/model v2.onnx"));
    }
}
