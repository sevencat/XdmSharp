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

    // ── 多关键字（空格/逗号分隔 → 各查一次再取交集）────────────────────────

    [Fact]
    public void SplitKeywords_SplitsOnSpacesAndCommas_AndDedupesIgnoringCase()
    {
        var keywords = HfHubClient.SplitKeywords("  qwen, 7b\tQWEN，instruct;  ");

        Assert.Equal(["qwen", "7b", "instruct"], keywords);
    }

    [Fact]
    public void SplitKeywords_CapsAtMaxKeywords()
    {
        var keywords = HfHubClient.SplitKeywords("a b c d e f g");

        Assert.Equal(HfHubClient.MaxKeywords, keywords.Count);
        Assert.Equal(["a", "b", "c", "d", "e"], keywords);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t , ，; ")]
    public void SplitKeywords_Blank_ReturnsEmpty(string? query)
    {
        Assert.Empty(HfHubClient.SplitKeywords(query));
    }

    [Fact]
    public async Task SearchModels_MultipleKeywords_IntersectsById_AndKeepsLargerCounts()
    {
        using var server = new FakeHttpServer();
        server.AddText(
            "/api/models?search=qwen&limit=20&sort=downloads&direction=-1",
            """
            [{ "id": "Qwen/Qwen2.5-7B-Instruct", "downloads": 900, "likes": 3 },
             { "id": "Qwen/Qwen2.5-1.5B", "downloads": 800 }]
            """);
        server.AddText(
            "/api/models?search=7b&limit=20&sort=downloads&direction=-1",
            """
            [{ "id": "Qwen/Qwen2.5-7B-Instruct", "downloads": 950, "likes": 10 },
             { "id": "other/Llama-7B", "downloads": 990 }]
            """);
        using var client = new HfHubClient();

        var models = await client.SearchModelsAsync(server.BaseUrl, "qwen, 7b", null, 20, Ct);

        var model = Assert.Single(models);
        Assert.Equal("Qwen/Qwen2.5-7B-Instruct", model.Id);
        Assert.Equal(950, model.Downloads);
        Assert.Equal(10, model.Likes);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task SearchModels_MultipleKeywords_StopsWhenFirstKeywordIsEmpty()
    {
        using var server = new FakeHttpServer();
        server.AddText("/api/models", "[]");
        using var client = new HfHubClient();

        var models = await client.SearchModelsAsync(server.BaseUrl, "zzz yyy", null, 20, Ct);

        // 交集只会越查越小：第一个关键字就没货，后面不用再打。
        Assert.Empty(models);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task SearchModels_NonJson_ThrowsHfException()
    {
        using var server = new FakeHttpServer();
        server.AddText("/api/models", "<html>login</html>");
        using var client = new HfHubClient();

        await Assert.ThrowsAsync<HfException>(() => client.SearchModelsAsync(server.BaseUrl, "llama", null, 20, Ct));
    }

    // ── README（模型详情简介用）───────────────────────────────────────────

    [Fact]
    public async Task GetReadme_ReturnsText_WithBearerToken()
    {
        using var server = new FakeHttpServer();
        server.AddText("/owner/name/raw/main/README.md", "# 标题\n\n正文。");
        using var client = new HfHubClient();

        var readme = await client.GetReadmeAsync(Repo(server), "hf_secret", Ct);

        Assert.Equal("# 标题\n\n正文。", readme);
        var request = Assert.Single(server.Requests);
        Assert.Equal("/owner/name/raw/main/README.md", request.Path);
        Assert.Equal("Bearer hf_secret", request.Headers["Authorization"]);
    }

    [Fact]
    public async Task GetReadme_NotFound_IsNull_NotEmptyError()
    {
        using var server = new FakeHttpServer();
        using var client = new HfHubClient();

        // 仓库没写 README 是常态，不该当失败抛出去。
        Assert.Null(await client.GetReadmeAsync(Repo(server), null, Ct));
    }

    [Fact]
    public async Task GetReadme_Forbidden_ThrowsWithTokenHint()
    {
        using var server = new FakeHttpServer();
        server.AddText("/owner/name/raw/main/README.md", "{}", statusCode: 403);
        using var client = new HfHubClient();

        var error = await Assert.ThrowsAsync<HfException>(() => client.GetReadmeAsync(Repo(server), null, Ct));

        Assert.Contains("token", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadmeUrl_UsesRevisionAndEscapesIt()
    {
        var repo = new HfRepo { Endpoint = "https://hf.test", Repo = "owner/name", Revision = "refs/pr 1" };

        Assert.Equal("https://hf.test/owner/name/raw/refs%2Fpr%201/README.md", repo.ReadmeUrl);
    }
}
