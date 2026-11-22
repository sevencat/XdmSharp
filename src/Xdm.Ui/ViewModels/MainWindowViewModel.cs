using System.Collections.ObjectModel;
using System.Windows.Input;
using Xdm.Core.Downloads;
using Xdm.Ui.Formatting;
using Xdm.Ui.Services;

namespace Xdm.Ui.ViewModels;

/// <summary>主窗口 ViewModel：分类过滤、搜索、工具栏命令、状态栏汇总。</summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private static long _demoId = 100_000;
    private readonly List<DownloadRowViewModel> _all = [];
    private CategoryViewModel _selectedCategory;
    private DownloadRowViewModel? _selectedRow;
    private string _searchText = string.Empty;
    private string _dbPath = string.Empty;

    public MainWindowViewModel(IEnumerable<DownloadRecord> records)
    {
        Categories =
        [
            new CategoryViewModel(CategoryKind.All, "全部"),
            new CategoryViewModel(CategoryKind.Active, "正在下载"),
            new CategoryViewModel(CategoryKind.Completed, "已完成"),
            new CategoryViewModel(CategoryKind.Failed, "失败"),
        ];
        _selectedCategory = Categories[0];

        PauseSelectedCommand = new RelayCommand(PauseSelected, () => SelectedRow is not null);
        ResumeSelectedCommand = new RelayCommand(ResumeSelected, () => SelectedRow is not null);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected, () => SelectedRow is not null);

        AddRange(records);
    }

    /// <summary>
    /// 引擎出口；为空表示「演示 / 单测」模式，命令只改本地状态，不会有真实下载。
    /// </summary>
    public IDownloadActions? Actions { get; set; }

    /// <summary>主列表绑定的集合（虚拟化容器读取的就是它）。</summary>
    public ObservableCollection<DownloadRowViewModel> Rows { get; } = [];

    public ObservableCollection<CategoryViewModel> Categories { get; }

    public CategoryViewModel SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is not null && SetProperty(ref _selectedCategory, value))
            {
                ApplyFilter();
            }
        }
    }

    public DownloadRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                (PauseSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ResumeSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (DeleteSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>任务库位置，状态栏右侧显示（也可用来验证 <c>--db</c>）。</summary>
    public string DbPath
    {
        get => _dbPath;
        set => SetProperty(ref _dbPath, value);
    }

    public string StatusSummary
    {
        get
        {
            var active = _all.Count(r => r.IsActive);
            var speed = _all.Sum(r => r.Record.SpeedBps);
            return $"{active} 个下载中 · 总速度 {TextFormat.Speed(speed)}";
        }
    }

    public string ListSummary => $"显示 {Rows.Count} / 共 {_all.Count} 个任务";

    /// <summary>新建下载窗口的默认保存目录（由设置页的「目录」页签决定）。</summary>
    public string DefaultSaveFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads");

    /// <summary>新建下载窗口的默认分段数（由设置页的「连接」页签决定）。</summary>
    public int DefaultSegments { get; set; } = 8;

    public bool IsEmpty => Rows.Count == 0;

    public ICommand PauseSelectedCommand { get; }

    public ICommand ResumeSelectedCommand { get; }

    public ICommand DeleteSelectedCommand { get; }

    /// <summary>请求用资源管理器打开目录（由窗口订阅，ViewModel 不直接启动进程）。</summary>
    public event EventHandler<string>? OpenFolderRequested;

    public void AddRange(IEnumerable<DownloadRecord> records)
    {
        foreach (var record in records)
        {
            _all.Add(new DownloadRowViewModel(record, TogglePause, Delete, OpenFolder));
        }

        RefreshCounts();
        ApplyFilter();
    }

    public void Add(DownloadRecord record) => AddRange([record]);

    /// <summary>用最新快照刷新对应行（引擎回调 → 4Hz 节流后调用）。</summary>
    public void UpdateRow(DownloadRecord record)
    {
        var row = _all.Find(r => r.Id == record.Id);
        if (row is null)
        {
            return;
        }

        row.Update(record);
        RefreshSummaries();
    }

    public void TogglePause(DownloadRowViewModel row)
    {
        if (row.IsPaused)
        {
            ResumeRow(row);
        }
        else
        {
            PauseRow(row);
        }
    }

    public void Delete(DownloadRowViewModel row)
    {
        Actions?.Delete(row.Id);
        RemoveRow(row);
    }

    /// <summary>管理器事件回调：按 id 摘行（与 <see cref="Delete"/> 幂等）。</summary>
    public void RemoveById(long id)
    {
        if (_all.Find(r => r.Id == id) is { } row)
        {
            RemoveRow(row);
        }
    }

    /// <summary>
    /// 新建窗口确认后：交给管理器建任务；没有引擎（演示 / 单测）时按请求造占位行。
    /// </summary>
    public void StartDownloads(IReadOnlyList<DownloadRequest> requests)
    {
        if (requests.Count == 0)
        {
            return;
        }

        if (Actions is { } actions)
        {
            actions.Start(requests);
            return;
        }

        var created = new List<DownloadRecord>(requests.Count);
        foreach (var request in requests)
        {
            created.Add(new DownloadRecord
            {
                Id = Interlocked.Increment(ref _demoId),
                Url = request.Url,
                FileName = string.IsNullOrWhiteSpace(request.FileName) ? request.Url : request.FileName,
                TargetFolder = request.TargetFolder,
                SegmentCount = request.MaxSegments,
                Status = request.StartImmediately ? DownloadStatus.Queued : DownloadStatus.Paused,
            });
        }

        AddRange(created);
    }

    /// <summary>按当前分类 + 搜索词重建 <see cref="Rows"/>。</summary>
    public void ApplyFilter()
    {
        var filter = SelectedCategory;
        var keyword = SearchText.Trim();

        Rows.Clear();
        foreach (var row in _all)
        {
            if (!filter.Matches(row.Record))
            {
                continue;
            }

            if (keyword.Length > 0
                && !row.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                && !row.Url.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Rows.Add(row);
        }

        RaisePropertyChanged(nameof(IsEmpty));
        RaisePropertyChanged(nameof(ListSummary));
    }

    private void PauseSelected()
    {
        if (SelectedRow is { } row)
        {
            PauseRow(row);
        }
    }

    private void ResumeSelected()
    {
        if (SelectedRow is { } row)
        {
            ResumeRow(row);
        }
    }

    private void PauseRow(DownloadRowViewModel row)
    {
        if (Actions is { } actions)
        {
            actions.Pause(row.Id);
            return;
        }

        ApplyStatus(row, DownloadStatus.Paused);
    }

    private void ResumeRow(DownloadRowViewModel row)
    {
        if (Actions is { } actions)
        {
            actions.Resume(row.Id);
            return;
        }

        ApplyStatus(row, DownloadStatus.Downloading);
    }

    private void RemoveRow(DownloadRowViewModel row)
    {
        _all.Remove(row);
        Rows.Remove(row);
        if (ReferenceEquals(SelectedRow, row))
        {
            SelectedRow = null;
        }

        RefreshCounts();
        RefreshSummaries();
    }

    private void ApplyStatus(DownloadRowViewModel row, DownloadStatus status)
    {
        var record = row.Record with
        {
            Status = status,
            SpeedBps = status is DownloadStatus.Downloading ? row.Record.SpeedBps : 0,
            RemainingSeconds = status is DownloadStatus.Downloading ? row.Record.RemainingSeconds : null,
        };

        row.Update(record);
        RefreshCounts();
        RefreshSummaries();
    }

    private void DeleteSelected()
    {
        if (SelectedRow is { } row)
        {
            Delete(row);
        }
    }

    private void OpenFolder(DownloadRowViewModel row) => OpenFolderRequested?.Invoke(this, row.TargetFolder);

    private void RefreshCounts()
    {
        foreach (var category in Categories)
        {
            category.Count = _all.Count(r => category.Matches(r.Record));
        }
    }

    private void RefreshSummaries()
    {
        RaisePropertyChanged(nameof(StatusSummary));
        RaisePropertyChanged(nameof(ListSummary));
        RaisePropertyChanged(nameof(IsEmpty));
    }
}
