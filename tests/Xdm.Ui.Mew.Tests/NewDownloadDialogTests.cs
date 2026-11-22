using System.Net;
using System.Net.Http;
using Aprillz.MewUI.Controls;
using Xdm.Core.Configuration;
using Xdm.Core.Downloads;
using Xdm.Core.HuggingFace;
using Xdm.Ui.Mew.Dialogs;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Mew.Tests;

/// <summary>
/// 新建下载对话框：设置里的默认目录/分段数必须落到控件与 ViewModel 上（PLAN-MEWUI.md 坑 22）。
/// 点「添加」的逻辑抽成了 <see cref="NewDownloadDialog.Submit"/>，测试不必模拟鼠标。
/// </summary>
public class NewDownloadDialogTests
{
    [Fact]
    public void Applies_Initial_Url()
    {
        var dialog = new NewDownloadDialog("https://example.com/a.bin");

        Assert.Equal("https://example.com/a.bin", dialog.ViewModel.Urls);
    }

    [Fact]
    public void Applies_SaveFolder_From_Settings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xdm-settings-folder");
        var dialog = new NewDownloadDialog(null, folder, null);

        Assert.Equal(folder, dialog.ViewModel.SaveFolder);
        Assert.True(VisualTree.ContainsText(dialog.Content, folder));
    }

    [Fact]
    public void Applies_Segments_From_Settings()
    {
        Assert.Equal(6m, new NewDownloadDialog(null, null, 6).ViewModel.Segments);
    }

    [Fact]
    public void Clamps_Out_Of_Range_Segments()
    {
        // 设置页允许手填，落进对话框时统一夹到 [1, 256]。
        Assert.Equal(1m, new NewDownloadDialog(null, null, 0).ViewModel.Segments);
        Assert.Equal(256m, new NewDownloadDialog(null, null, 9999).ViewModel.Segments);
    }

    [Fact]
    public void Submit_Builds_Request_From_Url_And_Folder()
    {
        var dialog = new NewDownloadDialog("https://example.com/a.bin", Path.GetTempPath(), 6);

        Assert.True(dialog.Submit());
        var request = Assert.Single(dialog.Requests!);

        Assert.Equal("https://example.com/a.bin", request.Url);
        Assert.Equal("a.bin", request.FileName);
        Assert.Equal(Path.GetTempPath(), request.TargetFolder);
        Assert.Equal(6, request.MaxSegments);
    }

    [Fact]
    public void Submit_Creates_One_Request_Per_Line()
    {
        var dialog = new NewDownloadDialog(null, Path.GetTempPath());
        dialog.ViewModel.Urls = "https://example.com/a.bin\nhttps://example.com/b.bin";

        Assert.True(dialog.Submit());
        Assert.Equal(2, dialog.Requests!.Count);
    }

    [Fact]
    public void Submit_Rejects_Invalid_Url_And_Shows_Error()
    {
        var dialog = new NewDownloadDialog("not a url", Path.GetTempPath());

        Assert.False(dialog.Submit());
        Assert.Null(dialog.Requests);
        Assert.True(dialog.ViewModel.HasError);
        Assert.True(VisualTree.ContainsText(dialog.Content, dialog.ViewModel.ErrorText));
    }

    [Fact]
    public void Submit_Rejects_Missing_SaveFolder()
    {
        var missing = Path.Combine(Path.GetTempPath(), "xdm-missing-" + Guid.NewGuid().ToString("N"));
        var dialog = new NewDownloadDialog("https://example.com/a.bin", missing);

        Assert.False(dialog.Submit());
        Assert.Contains("保存目录不存在", dialog.ViewModel.ErrorText, StringComparison.Ordinal);
    }

    // ── 任务级代理：设置里的代理表要落到单选组，选中的那条要进请求 ──────────────────

    [Fact]
    public void Renders_One_Radio_Per_Proxy_Choice()
    {
        var dialog = NewWithProxies();

        var radios = VisualTree.Descendants(dialog.Content).OfType<RadioButton>().ToList();

        // 「默认（公司代理）」+「直连」+ 具名代理 一条，共 3 个。
        Assert.Equal(3, radios.Count);
        Assert.True(VisualTree.ContainsText(dialog.Content, "默认（公司代理）"));
        Assert.True(VisualTree.ContainsText(dialog.Content, "直连"));
    }

    [Fact]
    public void Default_Choice_Is_Preselected_And_Submits_Null_Proxy()
    {
        var dialog = NewWithProxies();

        Assert.True(dialog.Submit());

        // null = 跟随设置里的默认代理；在这里就是「公司代理」，但请求上不写死。
        Assert.Null(Assert.Single(dialog.Requests!).ProxyId);
    }

    [Fact]
    public void Selecting_Proxy_Radio_Puts_Its_Id_On_Request()
    {
        var dialog = NewWithProxies();
        var radios = VisualTree.Descendants(dialog.Content).OfType<RadioButton>().ToList();

        radios[2].IsChecked = true;   // 第 3 项 = 具名代理「公司代理」

        Assert.Equal(2, dialog.ViewModel.SelectedProxyIndex);
        Assert.True(dialog.Submit());
        Assert.Equal("corp", Assert.Single(dialog.Requests!).ProxyId);
    }

    [Fact]
    public void Selecting_Direct_Radio_Puts_Direct_Id_On_Request()
    {
        var dialog = NewWithProxies();

        VisualTree.Descendants(dialog.Content).OfType<RadioButton>().ElementAt(1).IsChecked = true;

        Assert.True(dialog.Submit());
        Assert.Equal(ProxyIds.Direct, Assert.Single(dialog.Requests!).ProxyId);
    }

    [Fact]
    public void Falls_Back_To_Default_When_No_Proxies_Configured()
    {
        // 设置里一条具名代理都没有时只有「默认 / 直连」两项，不能出现空的单选组。
        var dialog = new NewDownloadDialog("https://example.com/a.bin", Path.GetTempPath());

        Assert.Equal(2, VisualTree.Descendants(dialog.Content).OfType<RadioButton>().Count());
        Assert.True(dialog.Submit());
        Assert.Null(Assert.Single(dialog.Requests!).ProxyId);
    }

    // ── HuggingFace 页 ────────────────────────────────────────────────────

    [Fact]
    public void HuggingFace_Submit_Builds_One_Request_Per_Selected_File()
    {
        var folder = NewTempFolder();
        var dialog = NewHuggingFaceDialog(folder);

        Assert.True(dialog.HuggingFace.LoadFiles(TestContext.Current.CancellationToken));
        dialog.SelectHuggingFaceTab();
        dialog.RefreshHuggingFaceFiles();   // 拉取成功后默认全选

        Assert.True(dialog.Submit());

        // 一个模型包 = 一条父行 + 每文件一条子任务（列表里只显示父行）。
        Assert.Null(dialog.Requests);
        var group = dialog.Group!;
        Assert.Equal("owner/name", group.Name);
        Assert.Equal(TaskKind.HuggingFace, group.Kind);
        Assert.Equal(Path.Combine(folder, "owner", "name"), group.TargetFolder);
        Assert.Equal(2, group.Files.Count);
        Assert.All(group.Files, request => Assert.Equal(TaskKind.HuggingFace, request.Kind));
        Assert.Equal(
            "https://hf.test/owner/name/resolve/main/onnx/model.onnx?download=true",
            group.Files[1].Url);
        Assert.Equal(Path.Combine(folder, "owner", "name", "onnx"), group.Files[1].TargetFolder);
        Assert.Equal("huggingface", KindOf(group.Files[1]));
    }

    [Fact]
    public void HuggingFace_Submit_Without_Selection_Reports_Hint()
    {
        var folder = NewTempFolder();
        var dialog = NewHuggingFaceDialog(folder);

        Assert.True(dialog.HuggingFace.LoadFiles(TestContext.Current.CancellationToken));
        dialog.SelectHuggingFaceTab();
        dialog.RefreshHuggingFaceFiles();
        dialog.ClearHuggingFaceSelection();

        Assert.False(dialog.Submit());
        Assert.Null(dialog.Requests);
        Assert.True(VisualTree.ContainsText(dialog.Content, dialog.HuggingFace.StatusText));
    }

    [Fact]
    public void HuggingFace_Submit_Uses_Selected_Proxy()
    {
        var folder = NewTempFolder();
        var dialog = NewHuggingFaceDialog(folder);

        Assert.True(dialog.HuggingFace.LoadFiles(TestContext.Current.CancellationToken));
        dialog.SelectHuggingFaceTab();
        dialog.RefreshHuggingFaceFiles();
        VisualTree.Descendants(dialog.Content).OfType<RadioButton>().ElementAt(2).IsChecked = true;

        Assert.True(dialog.Submit());
        Assert.All(dialog.Group!.Files, request => Assert.Equal("corp", request.ProxyId));
        Assert.Equal("corp", dialog.Group!.ProxyId);
    }

    [Fact]
    public void HuggingFaceProxy_FromSettings_IsPreselected_And_ReachesGroup()
    {
        // 设置里给 HF 单配了 "hf"：打开对话框就该选中它，HF 模型包也按它建任务。
        var folder = NewTempFolder();
        var settings = new AppSettings
        {
            Proxies =
            [
                new ProxyProfile { Id = "corp", Name = "公司代理", Url = "http://127.0.0.1:7890" },
                new ProxyProfile { Id = "hf", Name = "HF 代理", Url = "socks5://127.0.0.1:1080" },
            ],
            DefaultProxyId = "corp",
            HuggingFaceProxyId = "hf",
        };

        var client = new HfHubClient(new HttpClient(new FixedJsonHandler("""
            [
              { "type": "file", "path": "config.json", "size": 100 }
            ]
            """)));

        var dialog = new NewDownloadDialog(
            null,
            folder,
            4,
            ProxyChoice.Build(settings),
            "https://hf.test",
            null,
            client,
            hfProxyId: "hf");

        // 选项顺序：默认、直连、公司、HF → HF 是第 4 项。
        Assert.Equal(3, dialog.ViewModel.SelectedProxyIndex);
        var radios = VisualTree.Descendants(dialog.Content).OfType<RadioButton>().ToList();
        Assert.True(radios[3].IsChecked);

        dialog.HuggingFace.Repo = "owner/name";
        Assert.True(dialog.HuggingFace.LoadFiles(TestContext.Current.CancellationToken));
        dialog.SelectHuggingFaceTab();
        dialog.RefreshHuggingFaceFiles();

        Assert.True(dialog.Submit());
        Assert.Equal("hf", dialog.Group!.ProxyId);
        Assert.All(dialog.Group!.Files, request => Assert.Equal("hf", request.ProxyId));
    }

    [Fact]
    public void HuggingFace_Submit_Rejects_Missing_SaveFolder()
    {
        var missing = Path.Combine(Path.GetTempPath(), "xdm-missing-" + Guid.NewGuid().ToString("N"));
        var dialog = NewHuggingFaceDialog(missing);

        Assert.True(dialog.HuggingFace.LoadFiles(TestContext.Current.CancellationToken));
        dialog.SelectHuggingFaceTab();
        dialog.RefreshHuggingFaceFiles();

        Assert.False(dialog.Submit());
        Assert.Null(dialog.Requests);
    }

    [Fact]
    public void HuggingFace_Search_ListsModelsInPlaceOfFiles()
    {
        var folder = NewTempFolder();
        var dialog = NewSearchableDialog(folder);
        dialog.SelectHuggingFaceTab();
        dialog.HuggingFace.Repo = "llama";

        dialog.SearchHuggingFace();

        Assert.True(dialog.IsShowingSearchResults);
        Assert.Equal(1, dialog.SearchResultRows);
        var model = Assert.Single(dialog.HuggingFace.SearchResults);
        Assert.Equal("meta-llama/Llama-3.1-8B", model.Id);
        Assert.Contains("找到 1 个模型", dialog.HuggingFace.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void HuggingFace_Submit_WhileShowingSearchResults_IsRejected()
    {
        var folder = NewTempFolder();
        var dialog = NewSearchableDialog(folder);
        dialog.SelectHuggingFaceTab();
        dialog.HuggingFace.Repo = "llama";
        dialog.SearchHuggingFace();

        Assert.False(dialog.Submit());
        Assert.Null(dialog.Group);
        // 提示要指到「下载此模型」这个新按钮上，而不是含糊的「点一条搜索结果」。
        Assert.True(VisualTree.ContainsText(dialog.Content, "或在文件页拉取文件清单"));
    }

    [Fact]
    public void HuggingFace_PickSearchResult_OnlySelects_AndEnablesDetails()
    {
        var folder = NewTempFolder();
        var dialog = NewSearchableDialog(folder);
        dialog.SelectHuggingFaceTab();
        dialog.HuggingFace.Repo = "llama";
        dialog.SearchHuggingFace();

        // 刚搜完还没选：两个按钮都不可用。
        Assert.False(dialog.CanShowDetails);
        Assert.False(dialog.CanDownloadPicked);

        dialog.SelectSearchResult(0);

        // 选中只给按钮解锁，不自动拉清单（还能接着看别的模型）。
        Assert.True(dialog.IsShowingSearchResults);
        Assert.True(dialog.CanShowDetails);
        Assert.True(dialog.CanDownloadPicked);
        Assert.Empty(dialog.HuggingFace.Files);
    }

    [Fact]
    public void HuggingFace_Details_ReadsFileCountAndReadmeSummary()
    {
        var folder = NewTempFolder();
        var dialog = NewSearchableDialog(folder);
        dialog.SelectHuggingFaceTab();
        dialog.HuggingFace.Repo = "llama";
        dialog.SearchHuggingFace();
        dialog.SelectSearchResult(0);

        var details = dialog.LoadDetailsForPick();

        Assert.NotNull(details);
        Assert.Equal("meta-llama/Llama-3.1-8B", details!.Id);
        Assert.Equal(1, details.FileCount);
        Assert.Equal(100, details.TotalBytes);
        Assert.Equal("text-generation", details.PipelineTag);
        Assert.Equal(5, details.Downloads);
        Assert.Equal("Qwen 的镜像说明。", details.Summary);
        Assert.Contains("1 个文件", dialog.HuggingFace.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void HuggingFace_Details_WorksForManuallyTypedRepo()
    {
        var folder = NewTempFolder();
        var dialog = NewSearchableDialog(folder);
        dialog.SelectHuggingFaceTab();
        dialog.HuggingFace.Repo = "owner/name";
        dialog.RefreshHuggingFaceFiles();

        Assert.True(dialog.CanShowDetails);

        var details = dialog.LoadDetailsForPick();

        // 刚拉过清单 → 详情直接复用，不会再请求一遍 tree。
        Assert.NotNull(details);
        Assert.Equal("owner/name", details!.Id);
        Assert.Equal(1, details.FileCount);
    }

    [Fact]
    public void HuggingFace_Details_WithoutTarget_IsRejected()
    {
        var dialog = NewSearchableDialog(NewTempFolder());
        dialog.SelectHuggingFaceTab();

        Assert.False(dialog.CanShowDetails);
        Assert.Null(dialog.LoadDetailsForPick());
    }

    private static NewDownloadDialog NewSearchableDialog(string saveFolder)
    {
        var client = new HfHubClient(new HttpClient(new SearchAndTreeHandler()));

        return new NewDownloadDialog(
            null,
            saveFolder,
            4,
            ProxyChoice.Build(new AppSettings()),
            "https://hf.test",
            null,
            client);
    }

    /// <summary>搜索走 <c>/api/models</c>，拉清单走 <c>/tree/…</c>，两套 JSON 分开给。</summary>
    private sealed class SearchAndTreeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;

            var path = request.RequestUri!.AbsolutePath;
            var json = path.EndsWith("/api/models", StringComparison.Ordinal)
                ? """[{ "id": "meta-llama/Llama-3.1-8B", "downloads": 5, "pipeline_tag": "text-generation" }]"""
                : path.EndsWith("/README.md", StringComparison.Ordinal)
                    ? "---\nlicense: mit\n---\n\nQwen 的镜像说明。"
                    : """[{ "type": "file", "path": "config.json", "size": 100 }]""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }

    private static string KindOf(DownloadRequest request)
    {
        using var meta = System.Text.Json.JsonDocument.Parse(request.SourceMetaJson!);
        return meta.RootElement.GetProperty("kind").GetString()!;
    }

    private static string NewTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xdm-hf-dlg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static NewDownloadDialog NewHuggingFaceDialog(string saveFolder)
    {
        var settings = new AppSettings
        {
            Proxies = [new ProxyProfile { Id = "corp", Name = "公司代理", Url = "http://127.0.0.1:7890" }],
            DefaultProxyId = "corp",
        };

        var client = new HfHubClient(new HttpClient(new FixedJsonHandler("""
            [
              { "type": "file", "path": "config.json", "size": 100 },
              { "type": "file", "path": "onnx/model.onnx", "size": 200 }
            ]
            """)));

        var dialog = new NewDownloadDialog(
            null,
            saveFolder,
            4,
            ProxyChoice.Build(settings),
            "https://hf.test",
            null,
            client);

        dialog.HuggingFace.Repo = "owner/name";
        return dialog;
    }

    private sealed class FixedJsonHandler(string json) : HttpMessageHandler
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

    private static NewDownloadDialog NewWithProxies()
    {
        var settings = new AppSettings
        {
            Proxies = [new ProxyProfile { Id = "corp", Name = "公司代理", Url = "http://127.0.0.1:7890" }],
            DefaultProxyId = "corp",
        };

        return new NewDownloadDialog(
            "https://example.com/a.bin",
            Path.GetTempPath(),
            null,
            ProxyChoice.Build(settings));
    }
}
