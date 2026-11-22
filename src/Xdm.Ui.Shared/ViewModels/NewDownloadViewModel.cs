using Xdm.Core.Downloads;

namespace Xdm.Ui.ViewModels;

/// <summary>新建下载窗口（`UI-DESIGN.md` §6）。M1 只做「链接」页，校验与请求构造可单测。</summary>
public sealed class NewDownloadViewModel : ViewModelBase
{
    private string _urls = string.Empty;
    private string _saveFolder = string.Empty;
    private string _fileName = string.Empty;
    private decimal _segments = 8;
    private bool _startImmediately = true;
    private int _selectedProxyIndex;
    private string _errorText = string.Empty;

    public NewDownloadViewModel()
    {
        _saveFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
    }

    /// <summary>多行 URL（每行一个）。</summary>
    public string Urls
    {
        get => _urls;
        set => SetProperty(ref _urls, value ?? string.Empty);
    }

    public string SaveFolder
    {
        get => _saveFolder;
        set => SetProperty(ref _saveFolder, value ?? string.Empty);
    }

    /// <summary>可空：仅单链接时生效，空则按 URL 推导。</summary>
    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value ?? string.Empty);
    }

    /// <summary>分段数 1–256。</summary>
    public decimal Segments
    {
        get => _segments;
        set => SetProperty(ref _segments, value);
    }

    public bool StartImmediately
    {
        get => _startImmediately;
        set => SetProperty(ref _startImmediately, value);
    }

    /// <summary>可选代理（第一个是「默认」，由设置页的默认代理决定）。</summary>
    public IReadOnlyList<ProxyChoice> ProxyChoices { get; set; } = [];

    /// <summary>选中的代理选项下标；越界或未配置时按「默认」处理。</summary>
    public int SelectedProxyIndex
    {
        get => _selectedProxyIndex;
        set => SetProperty(ref _selectedProxyIndex, value);
    }

    /// <summary>当前选中的代理 id（越界/未配置按默认代理处理，即 <see langword="null"/>）。</summary>
    public string? SelectedProxyId =>
        SelectedProxyIndex >= 0 && SelectedProxyIndex < ProxyChoices.Count
            ? ProxyChoices[SelectedProxyIndex].ProxyId
            : null;

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _errorText.Length > 0;

    /// <summary>校验并构造下载请求；失败时把原因写进 <see cref="ErrorText"/>。</summary>
    public bool TryCreate(out IReadOnlyList<DownloadRequest> requests)
    {
        requests = [];

        var urls = Urls
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (urls.Count == 0)
        {
            ErrorText = "请至少输入一个链接";
            return false;
        }

        foreach (var text in urls)
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                ErrorText = $"不是有效的 http/https 链接：{text}";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(SaveFolder))
        {
            ErrorText = "请选择保存目录";
            return false;
        }

        if (!Directory.Exists(SaveFolder))
        {
            ErrorText = $"保存目录不存在：{SaveFolder}";
            return false;
        }

        var segments = (int)Math.Clamp(Segments, 1, 256);
        var proxyId = SelectedProxyId;
        var created = new List<DownloadRequest>(urls.Count);

        foreach (var text in urls)
        {
            var uri = new Uri(text);
            var name = urls.Count == 1 && !string.IsNullOrWhiteSpace(FileName)
                ? FileName.Trim()
                : DeriveFileName(uri);

            created.Add(new DownloadRequest
            {
                Url = uri.AbsoluteUri,
                FileName = name,
                TargetFolder = SaveFolder,
                MaxSegments = segments,
                StartImmediately = StartImmediately,
                ProxyId = proxyId,
            });
        }

        ErrorText = string.Empty;
        requests = created;
        return true;
    }

    private static string DeriveFileName(Uri uri)
    {
        var name = Path.GetFileName(uri.LocalPath);
        name = Uri.UnescapeDataString(name);
        return string.IsNullOrWhiteSpace(name) || name is "/" ? $"download-{DateTime.Now:HHmmss}" : name;
    }
}
