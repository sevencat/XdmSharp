using System.Net;
using System.Net.Http;
using Xdm.Core.HuggingFace;
using Xdm.Core.Http;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Mew.Tests;

public sealed class HfDownloadViewModelTests : IDisposable
{
    private const string TreeJson = """
        [
          { "type": "file", "path": "config.json", "size": 100 },
          { "type": "file", "path": "onnx/model.onnx", "size": 200 }
        ]
        """;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "xdm-hf-ui-" + Guid.NewGuid().ToString("N"));

    public HfDownloadViewModelTests() => Directory.CreateDirectory(_root);

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

    private static HfDownloadViewModel NewViewModel(string json = TreeJson) =>
        new("https://hf.test", null, new HfHubClient(new HttpClient(new StubHandler(json))))
        {
            Repo = "owner/name",
        };

    [Fact]
    public void TryUseProxy_OnlyRebuildsSelfOwnedClient()
    {
        // 注入的客户端归调用方（测试）管：换代理不能把它释放掉。
        using var injected = NewViewModel();
        Assert.False(injected.TryUseProxy(ProxyRoute.Direct));

        using var owned = new HfDownloadViewModel("https://hf.test", null);
        Assert.True(owned.TryUseProxy(ProxyRoute.Direct));
        Assert.True(owned.TryUseProxy(ProxyRoute.System));
    }

    [Fact]
    public void LoadFiles_FillsListAndStatus()
    {
        using var viewModel = NewViewModel();

        Assert.True(viewModel.LoadFiles(TestContext.Current.CancellationToken));

        Assert.True(viewModel.HasFiles);
        Assert.Equal(2, viewModel.Files.Count);
        Assert.Contains("2 个文件", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public void LoadFiles_InvalidRepo_WritesStatusWithoutCallingHub()
    {
        using var viewModel = NewViewModel();
        viewModel.Repo = "nodash";

        Assert.False(viewModel.LoadFiles(TestContext.Current.CancellationToken));

        Assert.Contains("owner/模型名", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasFiles);
    }

    [Fact]
    public void LoadFiles_HubFailure_WritesReason()
    {
        using var viewModel = NewViewModel("<html>login</html>");

        Assert.False(viewModel.LoadFiles(TestContext.Current.CancellationToken));

        Assert.Contains("JSON", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasFiles);
    }

    [Fact]
    public void TryCreateRequests_MapsSelectedFiles()
    {
        using var viewModel = NewViewModel();
        Assert.True(viewModel.LoadFiles(TestContext.Current.CancellationToken));

        Assert.True(viewModel.TryCreateRequests(
            _root, 8, "corp", [0, 1], startImmediately: false, out var requests));

        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal("corp", r.ProxyId));
        Assert.Equal(
            "https://hf.test/owner/name/resolve/main/onnx/model.onnx?download=true",
            requests[1].Url);
        Assert.Equal(Path.Combine(_root, "owner", "name", "onnx"), requests[1].TargetFolder);
    }

    [Fact]
    public void TryCreateRequests_WithoutSelection_WritesHint()
    {
        using var viewModel = NewViewModel();
        Assert.True(viewModel.LoadFiles(TestContext.Current.CancellationToken));

        Assert.False(viewModel.TryCreateRequests(
            _root, 8, null, [], startImmediately: true, out var requests));

        Assert.Empty(requests);
        Assert.Contains("勾选", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreateRequests_AllFilesExist_SkipsWithoutRequests()
    {
        using var viewModel = NewViewModel();
        Assert.True(viewModel.LoadFiles(TestContext.Current.CancellationToken));

        var first = Path.Combine(_root, "owner", "name");
        Directory.CreateDirectory(first);
        File.WriteAllBytes(Path.Combine(first, "config.json"), new byte[100]);
        File.WriteAllBytes(Path.Combine(first, "onnx.bin"), []);

        Assert.False(viewModel.TryCreateRequests(
            _root, 8, null, [0], startImmediately: true, out var requests));

        Assert.Empty(requests);
        Assert.Contains("已存在", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreateRequests_BadSelectionIndex_IsIgnored()
    {
        using var viewModel = NewViewModel();
        Assert.True(viewModel.LoadFiles(TestContext.Current.CancellationToken));

        Assert.True(viewModel.TryCreateRequests(
            _root, 8, null, [99, 0], startImmediately: true, out var requests));

        Assert.Single(requests);
        Assert.Equal("config.json", requests[0].FileName);
    }

    // ── 搜索 ──────────────────────────────────────────────────────────────

    private const string SearchJson = """
        [
          { "id": "meta-llama/Llama-3.1-8B", "downloads": 1200000, "likes": 4321,
            "pipeline_tag": "text-generation" }
        ]
        """;

    private static HfDownloadViewModel NewSearchViewModel() =>
        new("https://hf.test", null, new HfHubClient(new HttpClient(new StubHandler(SearchJson))));

    [Fact]
    public void Search_FillsResultsAndStatus()
    {
        using var viewModel = NewSearchViewModel();
        viewModel.Repo = "llama";

        Assert.True(viewModel.Search(TestContext.Current.CancellationToken));

        Assert.True(viewModel.HasSearchResults);
        var model = Assert.Single(viewModel.SearchResults);
        Assert.Equal("meta-llama/Llama-3.1-8B", model.Id);
        Assert.Equal(1200000, model.Downloads);
        Assert.Equal("text-generation", model.PipelineTag);
        Assert.Contains("找到 1 个模型", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public void Search_EmptyKeyword_WritesHintWithoutCallingHub()
    {
        using var viewModel = NewSearchViewModel();
        viewModel.Repo = "   ";

        Assert.False(viewModel.Search(TestContext.Current.CancellationToken));

        Assert.Contains("关键字", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasSearchResults);
    }

    [Fact]
    public void Search_NothingFound_WritesHint()
    {
        using var viewModel = new HfDownloadViewModel(
            "https://hf.test", null, new HfHubClient(new HttpClient(new StubHandler("[]"))));
        viewModel.Repo = "zzz";

        Assert.False(viewModel.Search(TestContext.Current.CancellationToken));

        Assert.Contains("没有找到", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_MultipleKeywords_IntersectsAndSaysSo()
    {
        // 假服务器对任何 search 都回同一条 → 交集就是那一条。
        using var viewModel = new HfDownloadViewModel(
            "https://hf.test", null, new HfHubClient(new HttpClient(new StubHandler(SearchJson))));
        viewModel.Repo = "llama, 3.1";

        Assert.True(viewModel.Search(TestContext.Current.CancellationToken));

        Assert.Single(viewModel.SearchResults);
        Assert.Contains("同时匹配 llama、3.1", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_MultipleKeywords_NothingFound_SaysNoOverlap()
    {
        using var viewModel = new HfDownloadViewModel(
            "https://hf.test", null, new HfHubClient(new HttpClient(new StubHandler("[]"))));
        viewModel.Repo = "zzz yyy";

        Assert.False(viewModel.Search(TestContext.Current.CancellationToken));

        Assert.Contains("同时匹配 zzz、yyy", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasSearchResults);
    }

    [Fact]
    public void SelectModel_FillsRepoAndClearsResults()
    {
        using var viewModel = NewSearchViewModel();
        viewModel.Repo = "llama";
        Assert.True(viewModel.Search(TestContext.Current.CancellationToken));

        viewModel.SelectModel(viewModel.SearchResults[0]);

        Assert.Equal("meta-llama/Llama-3.1-8B", viewModel.Repo);
        Assert.False(viewModel.HasSearchResults);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }
}
