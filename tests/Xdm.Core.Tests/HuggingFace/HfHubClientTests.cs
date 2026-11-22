using Xdm.Core.HuggingFace;
using Xdm.Core.Tests.Fakes;

namespace Xdm.Core.Tests.HuggingFace;

public sealed class HfHubClientTests
{
    private const string TreePath = "/api/models/owner/name/tree/main";

    private static HfRepo Repo(FakeHttpServer server) =>
        new() { Endpoint = server.BaseUrl, Repo = "owner/name" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetFiles_ParsesFilesAndLfs_AndSkipsDirectories()
    {
        using var server = new FakeHttpServer();
        server.AddText(TreePath, """
            [
              { "type": "directory", "path": "onnx", "size": 0 },
              { "type": "file", "path": "config.json", "size": 812 },
              { "type": "file", "path": "onnx/model.onnx", "size": 4096,
                "lfs": { "oid": "abc123", "size": 4096, "pointerSize": 134 } }
            ]
            """);
        using var client = new HfHubClient();

        var files = await client.GetFilesAsync(Repo(server), null, Ct);

        Assert.Collection(
            files,
            file =>
            {
                Assert.Equal("config.json", file.Path);
                Assert.Equal(812, file.Size);
                Assert.False(file.IsLfs);
            },
            file =>
            {
                Assert.Equal("onnx/model.onnx", file.Path);
                Assert.Equal("abc123", file.Sha256);
                Assert.Equal("onnx", file.RelativeFolder);
                Assert.Equal("model.onnx", file.FileName);
            });
    }

    [Fact]
    public async Task GetFiles_SendsBearerToken()
    {
        using var server = new FakeHttpServer();
        server.AddText(TreePath, "[]");
        using var client = new HfHubClient();

        await client.GetFilesAsync(Repo(server), "hf_secret", Ct);

        var request = Assert.Single(server.Requests);
        Assert.Equal("Bearer hf_secret", request.Headers["Authorization"]);
    }

    [Fact]
    public async Task GetFiles_Forbidden_AsksForToken()
    {
        using var server = new FakeHttpServer();
        server.AddText(TreePath, "{}", statusCode: 403);
        using var client = new HfHubClient();

        var error = await Assert.ThrowsAsync<HfException>(() => client.GetFilesAsync(Repo(server), null, Ct));

        Assert.Contains("token", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetFiles_NotFound_MentionsRepo()
    {
        using var server = new FakeHttpServer();
        using var client = new HfHubClient();

        var error = await Assert.ThrowsAsync<HfException>(() => client.GetFilesAsync(Repo(server), null, Ct));

        Assert.Contains("不存在", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFiles_FollowsLinkPagination()
    {
        using var server = new FakeHttpServer();
        server.AddText(
            TreePath,
            """[{ "type": "file", "path": "a.bin", "size": 1 }]""",
            headers: new Dictionary<string, string>
            {
                ["Link"] = $"<{server.BaseUrl}{TreePath}?cursor=page2>; rel=\"next\"",
            });
        server.AddText(
            TreePath + "?cursor=page2",
            """[{ "type": "file", "path": "b.bin", "size": 2 }]""");
        using var client = new HfHubClient();

        var files = await client.GetFilesAsync(Repo(server), null, Ct);

        Assert.Equal(2, files.Count);
        Assert.Equal("b.bin", files[1].Path);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task GetFiles_RejectsNonJson()
    {
        using var server = new FakeHttpServer();
        server.AddText(TreePath, "<html>login</html>");
        using var client = new HfHubClient();

        await Assert.ThrowsAsync<HfException>(() => client.GetFilesAsync(Repo(server), null, Ct));
    }

    // ── 模型搜索 ──────────────────────────────────────────────────────────

    [Fact]
    public void SearchUrl_CarriesKeywordAndSortsByDownloads()
    {
        var url = HfHubClient.SearchUrl("https://hf.test/", "llama 3.1", 25);

        Assert.Equal(
            "https://hf.test/api/models?search=llama%203.1&limit=25&sort=downloads&direction=-1",
            url);
    }

    [Fact]
    public void SearchUrl_EmptyEndpointFallsBackToOfficialSite()
    {
        Assert.StartsWith(
            HfRepo.DefaultEndpoint + "/api/models?search=x",
            HfHubClient.SearchUrl(null, "x"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchModels_ParsesIdStatsAndSkipsNonRepoIds()
    {
        using var server = new FakeHttpServer();
        server.AddText("/api/models", """
            [
              { "id": "meta-llama/Llama-3.1-8B", "downloads": 12345, "likes": 67,
                "pipeline_tag": "text-generation" },
              { "id": "no-owner", "downloads": 1 },
              { "id": "a/b/c", "downloads": 2 },
              { "id": "Qwen/Qwen2.5-7B-Instruct" }
            ]
            """);
        using var client = new HfHubClient();

        var models = await client.SearchModelsAsync(server.BaseUrl, "llama", null, 20, Ct);

        Assert.Collection(
            models,
            model =>
            {
                Assert.Equal("meta-llama/Llama-3.1-8B", model.Id);
                Assert.Equal(12345, model.Downloads);
                Assert.Equal(67, model.Likes);
                Assert.Equal("text-generation", model.PipelineTag);
            },
            model =>
            {
                Assert.Equal("Qwen/Qwen2.5-7B-Instruct", model.Id);
                Assert.Null(model.Downloads);
            });
        Assert.Equal("/api/models", Assert.Single(server.Requests).Path);
    }

    [Fact]
    public async Task SearchModels_SendsBearerToken()
    {
        using var server = new FakeHttpServer();
        server.AddText("/api/models", "[]");
        using var client = new HfHubClient();

        await client.SearchModelsAsync(server.BaseUrl, "qwen", "hf_secret", 10, Ct);

        Assert.Equal("Bearer hf_secret", Assert.Single(server.Requests).Headers["Authorization"]);
    }

    [Fact]
    public async Task SearchModels_EmptyKeyword_SkipsRequest()
    {
        using var server = new FakeHttpServer();
        using var client = new HfHubClient();

        var models = await client.SearchModelsAsync(server.BaseUrl, "   ", null, 20, Ct);

        Assert.Empty(models);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task SearchModels_NonJson_ThrowsHfException()
    {
        using var server = new FakeHttpServer();
        server.AddText("/api/models", "<html>login</html>");
        using var client = new HfHubClient();

        await Assert.ThrowsAsync<HfException>(() => client.SearchModelsAsync(server.BaseUrl, "llama", null, 20, Ct));
    }
}
