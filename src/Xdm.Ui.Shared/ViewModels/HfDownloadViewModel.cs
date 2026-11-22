using Xdm.Core.Configuration;
using Xdm.Core.Downloads;
using Xdm.Core.HuggingFace;
using Xdm.Core.Http;
using Xdm.Ui.Formatting;

namespace Xdm.Ui.ViewModels;

/// <summary>
/// 「新建下载 → HuggingFace」页的 ViewModel：填模型 id → 拉文件清单 → 勾选 → 生成下载请求
/// （<c>ARCHITECTURE.md</c> §12.1）。网络调用是同步的，由界面放到后台线程跑，
/// 免得把 <c>SynchronizationContext</c> 带进 UI 层。
/// </summary>
public sealed class HfDownloadViewModel : ViewModelBase, IDisposable
{
    public const string DefaultRevision = "main";

    /// <summary>一次搜索返回的条数（够挑就行，避免列表拖太长）。</summary>
    public const int SearchLimit = 20;

    /// <summary>空闲时的提示行（清空/重建后恢复成这句）。</summary>
    public const string Hint = "输入模型关键字（空格或逗号分开多个即「同时包含」）搜索，或直接填 owner/模型名后拉取文件清单";

    private HfHubClient _client;
    private readonly bool _ownsClient;
    private readonly string _endpoint;
    private readonly string? _token;
    private string _repo = string.Empty;
    private string _revision = DefaultRevision;
    private string _statusText = Hint;
    private bool _isBusy;
    private IReadOnlyList<HfFile> _files = [];
    private IReadOnlyList<HfModelSummary> _searchResults = [];
    private string? _filesRepo;

    /// <param name="endpoint">设置里的 HuggingFace 站点根。</param>
    /// <param name="token">令牌（可为空；调用方负责先取设置再取 <c>HF_TOKEN</c>）。</param>
    /// <param name="client">注入用（测试）；不传则自建。</param>
    /// <param name="proxy">清单请求走的代理路线（设置里的默认代理）；为空跟随系统代理。</param>
    public HfDownloadViewModel(
        string? endpoint,
        string? token,
        HfHubClient? client = null,
        ProxyRoute? proxy = null)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? HfRepo.DefaultEndpoint : endpoint.Trim();

        // 设置里没填就用环境变量 HF_TOKEN（与 huggingface-cli 一致）。
        _token = !string.IsNullOrWhiteSpace(token)
            ? token.Trim()
            : AppSettings.SanitizeToken(Environment.GetEnvironmentVariable("HF_TOKEN"));
        _ownsClient = client is null;
        _client = client ?? new HfHubClient(proxy: proxy);
    }

    /// <summary>模型 id / 搜索关键字输入（<c>owner/name</c>、模型页 URL 或关键字片段）。</summary>
    public string Repo
    {
        get => _repo;
        set => SetProperty(ref _repo, value ?? string.Empty);
    }

    /// <summary>分支 / tag / commit。</summary>
    public string Revision
    {
        get => _revision;
        set => SetProperty(ref _revision, value ?? string.Empty);
    }

    /// <summary>当前仓库的文件（已按路径排序）。</summary>
    public IReadOnlyList<HfFile> Files
    {
        get => _files;
        private set
        {
            if (SetProperty(ref _files, value))
            {
                RaisePropertyChanged(nameof(HasFiles));
            }
        }
    }

    public bool HasFiles => _files.Count > 0;

    /// <summary>搜索命中的模型（按下载量降序，服务端已排好）。</summary>
    public IReadOnlyList<HfModelSummary> SearchResults
    {
        get => _searchResults;
        private set
        {
            if (SetProperty(ref _searchResults, value))
            {
                RaisePropertyChanged(nameof(HasSearchResults));
            }
        }
    }

    public bool HasSearchResults => _searchResults.Count > 0;

    /// <summary>提示行（成功是清单摘要，失败是原因）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>解析当前输入；失败时写 <see cref="StatusText"/>。</summary>
    public bool TryParseRepo(out HfRepo repo)
    {
        var revision = HfRepo.RevisionFromUrl(_repo, Revision.Trim());
        if (HfRepo.TryParse(_repo, _endpoint, revision, out repo, out var error))
        {
            return true;
        }

        StatusText = error;
        return false;
    }

    /// <summary>
    /// 按 <see cref="Repo"/> 里的输入当关键字搜模型（同步，调用方放后台线程）。
    /// 输入里的空格/逗号会把关键字拆开，多个关键字是「同时包含」（见 <see cref="HfHubClient.SplitKeywords"/>）。
    /// 返回是否命中；失败原因写 <see cref="StatusText"/>。
    /// </summary>
    public bool Search(CancellationToken cancellationToken = default)
    {
        var keywords = HfHubClient.SplitKeywords(_repo);
        if (keywords.Count == 0)
        {
            StatusText = "请输入模型关键字或 owner/模型名再搜索";
            return false;
        }

        // 一个关键字沿用老文案；多个关键字说清是「同时匹配」。
        var single = keywords.Count == 1;
        var label = single ? $"「{keywords[0]}」" : $"「{string.Join("」「", keywords)}」";
        var all = string.Join("、", keywords);

        IsBusy = true;
        StatusText = $"正在搜索{label}…";
        try
        {
            var results = _client
                .SearchModelsAsync(_endpoint, string.Join(' ', keywords), _token, SearchLimit, cancellationToken)
                .GetAwaiter()
                .GetResult();

            SearchResults = results;
            StatusText = (results.Count, single) switch
            {
                (0, true) => $"没有找到{label}，换个关键字或直接填 owner/模型名",
                (0, false) => $"没有找到同时匹配 {all} 的模型，换个关键字或直接填 owner/模型名",
                (_, true) => $"找到 {results.Count} 个模型，点一条选中后可看详情或拉取文件清单",
                _ => $"找到 {results.Count} 个同时匹配 {all} 的模型，点一条选中后可看详情或拉取文件清单",
            };
            return results.Count > 0;
        }
        catch (HfException ex)
        {
            SearchResults = [];
            StatusText = ex.Message;
            return false;
        }
        catch (HttpRequestException ex)
        {
            SearchResults = [];
            StatusText = $"连接 HuggingFace 失败：{ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>选中一条搜索结果：填进模型 id 并清空结果（接着点「拉取文件」或由界面直接拉）。</summary>
    public void SelectModel(HfModelSummary model)
    {
        ArgumentNullException.ThrowIfNull(model);

        Repo = model.Id;
        SearchResults = [];
    }

    /// <summary>同步拉清单（调用方放后台线程）。返回是否成功。</summary>
    public bool LoadFiles(CancellationToken cancellationToken = default)
    {
        if (!TryParseRepo(out var repo))
        {
            return false;
        }

        IsBusy = true;
        StatusText = $"正在拉取 {repo.Repo}@{repo.Revision} 的文件清单…";
        try
        {
            var files = _client
                .GetFilesAsync(repo, _token, cancellationToken)
                .GetAwaiter()
                .GetResult();

            Files = files;
            _filesRepo = repo.Repo;
            StatusText = files.Count == 0
                ? "这个仓库在指定版本下没有文件"
                : $"{files.Count} 个文件，共 {TextFormat.Bytes(files.Sum(static f => f.Size ?? 0))}";
            return files.Count > 0;
        }
        catch (HfException ex)
        {
            Files = [];
            StatusText = ex.Message;
            return false;
        }
        catch (HttpRequestException ex)
        {
            Files = [];
            StatusText = $"连接 HuggingFace 失败：{ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 拉「模型详情」（文件数 / 总大小 / README 摘要），同步，调用方放后台线程。
    /// <paramref name="model"/> 是搜索结果里的那条（可空：手填 id 时没有下载量/任务类型）。
    /// 清单刚拉过且是同一个仓库时直接复用，不再请求一遍。
    /// 失败写 <see cref="StatusText"/> 并返回 <see langword="null"/>。
    /// </summary>
    public HfModelDetails? LoadDetails(
        string modelId,
        HfModelSummary? model = null,
        CancellationToken cancellationToken = default)
    {
        var revision = HfRepo.RevisionFromUrl(modelId, Revision.Trim());
        if (!HfRepo.TryParse(modelId, _endpoint, revision, out var repo, out var error))
        {
            StatusText = error;
            return null;
        }

        IsBusy = true;
        StatusText = $"正在读取 {repo.Repo} 的详情…";
        try
        {
            var files = string.Equals(_filesRepo, repo.Repo, StringComparison.OrdinalIgnoreCase) && _files.Count > 0
                ? _files
                : _client.GetFilesAsync(repo, _token, cancellationToken).GetAwaiter().GetResult();

            string? readme = null;
            try
            {
                readme = _client.GetReadmeAsync(repo, _token, cancellationToken).GetAwaiter().GetResult();
            }
            catch (HfException)
            {
                // README 只是锦上添花：拉不到就别让整个详情失败，文件数照常显示。
            }

            StatusText = files.Count == 0
                ? "这个仓库在指定版本下没有文件"
                : $"{files.Count} 个文件，共 {TextFormat.Bytes(files.Sum(static f => f.Size ?? 0))}";
            return HfModelDetails.Create(repo, model, files, readme);
        }
        catch (HfException ex)
        {
            StatusText = ex.Message;
            return null;
        }
        catch (HttpRequestException ex)
        {
            StatusText = $"连接 HuggingFace 失败：{ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 把勾选的文件转成一个多文件任务（父行 + 子任务）。本地已有同大小文件的直接跳过；
    /// 全被跳过时返回 <see langword="false"/> 并写 <see cref="StatusText"/>。
    /// </summary>
    public bool TryCreateGroup(
        string saveRoot,
        int maxSegments,
        string? proxyId,
        IReadOnlyCollection<int> selectedIndices,
        bool startImmediately,
        out DownloadGroupRequest group)
    {
        group = null!;

        if (!TryParseRepo(out var repo))
        {
            return false;
        }

        var selected = selectedIndices
            .Where(i => i >= 0 && i < _files.Count)
            .Distinct()
            .Order()
            .Select(i => _files[i])
            .ToArray();

        if (selected.Length == 0)
        {
            StatusText = "先勾选要下载的文件";
            return false;
        }

        var plan = HfDownloadPlanner.Plan(repo, selected, saveRoot);
        if (plan.Files.Count == 0)
        {
            StatusText = $"选中的 {plan.SkippedCount} 个文件本地已存在，无需下载";
            return false;
        }

        group = HfDownloadPlanner.ToGroup(repo, plan, maxSegments, proxyId, _token, startImmediately);
        StatusText = plan.SkippedCount > 0
            ? $"跳过 {plan.SkippedCount} 个已存在的文件，新建 {group.Files.Count} 个文件的任务"
            : $"新建 {group.Files.Count} 个文件的任务，共 {TextFormat.Bytes(plan.TotalBytes)}";
        return true;
    }

    /// <summary>只要文件级请求（列表预览 / 测试用）；父子关系交给 <see cref="TryCreateGroup"/>。</summary>
    public bool TryCreateRequests(
        string saveRoot,
        int maxSegments,
        string? proxyId,
        IReadOnlyCollection<int> selectedIndices,
        bool startImmediately,
        out IReadOnlyList<DownloadRequest> requests)
    {
        var ok = TryCreateGroup(saveRoot, maxSegments, proxyId, selectedIndices, startImmediately, out var group);
        requests = ok ? group.Files : [];
        return ok;
    }

    /// <summary>
    /// 换代理路线重建清单客户端：对话框里的代理是单选组，用户可能在打开窗口后改选。
    /// 注入进来的客户端不动（那是测试/调用方的），此时返回 <see langword="false"/>。
    /// </summary>
    public bool TryUseProxy(ProxyRoute route)
    {
        if (!_ownsClient)
        {
            return false;
        }

        var replacement = new HfHubClient(proxy: route);
        _client.Dispose();
        _client = replacement;
        return true;
    }

    /// <summary>换仓库后清掉旧清单与旧搜索结果（避免勾到上一个仓库的文件）。</summary>
    public void ClearFiles()
    {
        Files = [];
        _filesRepo = null;
        SearchResults = [];
        StatusText = Hint;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
