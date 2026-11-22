using System.Collections.ObjectModel;
using System.Windows.Input;
using Xdm.Core.Configuration;
using Xdm.Core.Downloads;
using Xdm.Ui.Formatting;
using Xdm.Ui.Services;

namespace Xdm.Ui.ViewModels;

/// <summary>主窗口 ViewModel：分类过滤、搜索、工具栏命令、状态栏汇总。</summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private static long _demoId = 100_000;
    private readonly List<DownloadRowViewModel> _all = [];

    /// <summary>
    /// 已展开的父行 → 子文件行（顺序同引擎快照）。只在这里留一份：收起来就丢掉，
    /// 再次展开重新向引擎拉快照，不缓存陈旧进度。
    /// </summary>
    private readonly Dictionary<long, List<DownloadRowViewModel>> _expanded = [];
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

    /// <summary>
    /// 演示模式下的子文件来源（<c>XDM_DEMO</c> 不接引擎，但行首展开视图得能看）。
    /// 真实运行只走 <see cref="Actions"/>，这里保持 null。
    /// </summary>
    public Func<long, IReadOnlyList<DownloadRecord>>? DemoChildren { get; init; }

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

    /// <summary>只数父行：展开出来的子文件行不是独立任务，不该把「显示 N」撑大。</summary>
    public string ListSummary => $"显示 {Rows.Count(r => r.IsParent)} / 共 {_all.Count} 个任务";

    /// <summary>进行中任务数（不受分类过滤影响，退出确认文案用）。</summary>
    public int ActiveCount => _all.Count(r => r.IsActive);

    /// <summary>新建下载窗口的默认保存目录（由设置页的「目录」页签决定）。</summary>
    public string DefaultSaveFolder { get; set; } = KnownFolders.Downloads();

    /// <summary>新建下载窗口的默认分段数（由设置页的「连接」页签决定）。</summary>
    public int DefaultSegments { get; set; } = 8;

    /// <summary>新建下载窗口可选的代理（第一项是「默认代理」，其余是设置里的具名代理）。</summary>
    public IReadOnlyList<ProxyChoice> ProxyChoices { get; set; } = [];

    public bool IsEmpty => Rows.Count == 0;

    public ICommand PauseSelectedCommand { get; }

    public ICommand ResumeSelectedCommand { get; }

    public ICommand DeleteSelectedCommand { get; }

    /// <summary>请求用资源管理器打开目录（由窗口订阅，ViewModel 不直接启动进程）。</summary>
    public event EventHandler<string>? OpenFolderRequested;

    /// <summary>
    /// 删除前的确认（窗口注入真实对话框；为空则直接删，供演示 / 单测）。
    /// 返回 <c>null</c> 表示用户取消，<c>false</c> 只删任务，<c>true</c> 连已下载的文件一起删。
    /// </summary>
    public Func<DownloadRowViewModel, Task<bool?>>? ConfirmDelete { get; set; }

    private readonly HashSet<long> _deleting = [];

    public void AddRange(IEnumerable<DownloadRecord> records)
    {
        var changed = false;
        foreach (var record in records)
        {
            // 子行不单独出列表：父行已经把它们的进度/状态汇总了（`ARCHITECTURE.md` §12.1 契约 2）。
            if (record.ParentId is not null)
            {
                continue;
            }

            _all.Add(new DownloadRowViewModel(record, TogglePause, Delete, OpenFolder));
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        RefreshCounts();
        ApplyFilter();
    }

    public void Add(DownloadRecord record) => AddRange([record]);

    /// <summary>用最新快照刷新对应行（引擎回调 → 4Hz 节流后调用）。</summary>
    public void UpdateRow(DownloadRecord record)
    {
        // 子任务只更新「已展开的父行」里那一份行；没展开时父行汇总已经把它算进去了。
        if (record.ParentId is { } parentId)
        {
            UpdateChildRow(parentId, record);
            return;
        }

        var row = _all.Find(r => r.Id == record.Id);
        if (row is null)
        {
            return;
        }

        var statusChanged = row.Record.Status != record.Status;
        row.Update(record);
        RefreshSummaries();

        // 接真引擎后状态会自己走（下载中 → 已完成/失败），不跟着刷计数会让左侧和状态栏长期停在旧值。
        if (statusChanged)
        {
            RefreshCounts();
        }
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

    /// <summary>同步删除入口（行内按钮、Del 键）：内部转异步，不等确认结果。</summary>
    public void Delete(DownloadRowViewModel row) => _ = DeleteAsync(row);

    /// <summary>
    /// 删除行：有 <see cref="ConfirmDelete"/> 时先弹确认框，用户取消则什么都不做；
    /// 同一个任务重复点击（确认框还开着）直接忽略。
    /// </summary>
    public async Task DeleteAsync(DownloadRowViewModel row)
    {
        if (!_deleting.Add(row.Id))
        {
            return;
        }

        try
        {
            bool? deleteFile = false;
            if (ConfirmDelete is { } confirm)
            {
                deleteFile = await confirm(row);
                if (deleteFile is null)
                {
                    return;
                }
            }

            Actions?.Delete(row.Id, deleteFile.Value);
            RemoveRow(row);
        }
        finally
        {
            _deleting.Remove(row.Id);
        }
    }

    /// <summary>管理器事件回调：按 id 摘行（与 <see cref="Delete"/> 幂等）。</summary>
    public void RemoveById(long id)
    {
        if (_all.Find(r => r.Id == id) is { } row)
        {
            RemoveRow(row);
            return;
        }

        // 展开视图里删单个文件：引擎推的是子任务 id，父行仍在。
        foreach (var children in _expanded.Values)
        {
            if (children.Find(c => c.Id == id) is { } child)
            {
                RemoveRow(child);
                return;
            }
        }
    }

    /// <summary>
    /// 展开/收起多文件任务的子文件行（`UI-DESIGN.md` §4）。子行插在父行之后、跟着父行进同一个
    /// <see cref="Rows"/>，所以行高仍是统一 56；收起只是把它们从集合里摘掉。
    /// </summary>
    public void ToggleExpand(DownloadRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanExpand)
        {
            return;
        }

        if (_expanded.Remove(row.Id))
        {
            row.IsExpanded = false;
            ApplyFilter();
            return;
        }

        var children = new List<DownloadRowViewModel>();
        foreach (var record in Actions?.Children(row.Id) ?? DemoChildren?.Invoke(row.Id) ?? [])
        {
            children.Add(NewChildRow(record));
        }

        _expanded[row.Id] = children;
        row.IsExpanded = true;
        ApplyFilter();
    }

    private DownloadRowViewModel NewChildRow(DownloadRecord record)
        => new(record, TogglePause, Delete, OpenFolder);

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

    /// <summary>
    /// 新建窗口确认后（HF 模型包这类多文件任务）：交给管理器建「父行 + 子任务」；
    /// 没有引擎（演示 / 单测）时造一行父行占位。
    /// </summary>
    public void StartGroup(DownloadGroupRequest group)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (Actions is { } actions)
        {
            actions.StartGroup(group);
            return;
        }

        AddRange([new DownloadRecord
        {
            Id = Interlocked.Increment(ref _demoId),
            Url = group.Url,
            FileName = group.Name,
            TargetFolder = group.TargetFolder,
            Status = group.StartImmediately ? DownloadStatus.Queued : DownloadStatus.Paused,
            IsContainer = true,
            ChildCount = group.Files.Count,
        }]);
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

            // 展开状态跟着父行走：换分类、改搜索词重建集合后子行还在原位。
            if (_expanded.TryGetValue(row.Id, out var children))
            {
                foreach (var child in children)
                {
                    Rows.Add(child);
                }
            }
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
        if (row.IsChild)
        {
            // 子行不在 _all 里，也不进分类计数；摘掉它只影响展开出来的那一段。
            if (row.Record.ParentId is { } parentId && _expanded.TryGetValue(parentId, out var siblings))
            {
                siblings.Remove(row);
            }

            if (ReferenceEquals(SelectedRow, row))
            {
                SelectedRow = null;
            }

            ApplyFilter();
            return;
        }

        _all.Remove(row);
        _expanded.Remove(row.Id);
        if (ReferenceEquals(SelectedRow, row))
        {
            SelectedRow = null;
        }

        RefreshCounts();
        RefreshSummaries();

        // 整表重建：父行没了，它展开出来的子行也必须一起消失。
        ApplyFilter();
    }

    /// <summary>展开期间引擎推来的子任务快照 → 对应子行。</summary>
    private void UpdateChildRow(long parentId, DownloadRecord record)
    {
        if (!_expanded.TryGetValue(parentId, out var children))
        {
            return;
        }

        foreach (var child in children)
        {
            if (child.Id == record.Id)
            {
                child.Update(record);
                return;
            }
        }

        // 展开发生在子任务建好之前：第一条进度推过来时补上这一行（追加到末尾，顺序影响很小）。
        children.Add(NewChildRow(record));
        ApplyFilter();
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
