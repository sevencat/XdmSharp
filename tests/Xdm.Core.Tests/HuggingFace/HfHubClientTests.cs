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
}
