using System.Collections.ObjectModel;
using System.Windows.Input;
using Xdm.Core.Downloads;
using Xdm.Ui.Formatting;

namespace Xdm.Ui.ViewModels;

/// <summary>列表行（`UI-DESIGN.md` §4）。字段按"父行 + 可选子行"形状定义，首期子集合恒为空。</summary>
public sealed class DownloadRowViewModel : ViewModelBase
{
    private DownloadRecord _record;
    private double _progress;

    public DownloadRowViewModel(
        DownloadRecord record,
        Action<DownloadRowViewModel>? onTogglePause = null,
        Action<DownloadRowViewModel>? onDelete = null,
        Action<DownloadRowViewModel>? onOpenFolder = null)
    {
        _record = record;
        _progress = record.Progress;

        TogglePauseCommand = new RelayCommand(() => onTogglePause?.Invoke(this));
        DeleteCommand = new RelayCommand(() => onDelete?.Invoke(this));
        OpenFolderCommand = new RelayCommand(() => onOpenFolder?.Invoke(this));
    }

    public DownloadRecord Record => _record;

    public long Id => _record.Id;

    public string FileName => _record.FileName;

    public string Url => _record.Url;

    public string TargetFolder => _record.TargetFolder;

    /// <summary>扩展名（大写，不含点），用于行左图标；无扩展名时为 <c>文件</c>。</summary>
    public string Extension
    {
        get
        {
            var ext = Path.GetExtension(_record.FileName);
            return ext.Length > 1 ? ext[1..Math.Min(5, ext.Length)].ToUpperInvariant() : "文件";
        }
    }

    public int Segments => _record.SegmentCount;

    public bool IsParent => _record.ParentId is null;

    public bool IsChild => !IsParent;

    /// <summary>V2/V3 的多文件子任务；首期恒为空。</summary>
    public ObservableCollection<DownloadRowViewModel> Children { get; } = [];

    /// <summary>0..1；按 ≥1% 变化才通知（`UI-DESIGN.md` §4 进度刷新约束）。</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value))
            {
                RaisePropertyChanged(nameof(ProgressText));
            }
        }
    }

    public string ProgressText => TextFormat.Percent(_progress);

    public string SizeText => TextFormat.Bytes(_record.SizeBytes);

    /// <summary>只有运行中的任务显示速度，其余留空（避免一排"—"占位）。</summary>
    public string SpeedText => _record.IsActive ? TextFormat.Speed(_record.SpeedBps) : string.Empty;

    public string RemainingText => _record.IsActive ? TextFormat.Remaining(_record.RemainingSeconds) : string.Empty;

    /// <summary>第二行中部：进行中 `3/12 段 · D:\Downloads`；完成态 `D:\Downloads · 完成于 …`。</summary>
    public string SegmentAndFolderText => IsCompleted
        ? (_record.CompletedAt is null
            ? _record.TargetFolder
            : $"{_record.TargetFolder} · 完成于 {TextFormat.CompletedAt(_record.CompletedAt)}")
        : $"{_record.CompletedSegments}/{_record.SegmentCount} 段 · {_record.TargetFolder}";

    /// <summary>状态徽标文案（唯一来源，便于测试断言）。</summary>
    public string StatusText => DescribeStatus(_record);

    public string? ErrorText => _record.ErrorText;

    public bool IsCompleted => _record.Status is DownloadStatus.Completed;

    public bool IsFailed => _record.Status is DownloadStatus.Failed or DownloadStatus.DiskError;

    public bool IsPaused => _record.Status is DownloadStatus.Paused;

    public bool IsActive => _record.IsActive;

    /// <summary>完成态隐藏进度条。</summary>
    public bool ShowProgress => !IsCompleted;

    /// <summary>行内主按钮文案：运行中 → 暂停，暂停 → 恢复，完成 → 打开。</summary>
    public string ToggleText => IsPaused ? "恢复" : "暂停";

    public string CompletedAtText => TextFormat.CompletedAt(_record.CompletedAt);

    public string ToolTipText => $"{_record.FileName}\n{_record.Url}";

    public ICommand TogglePauseCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand OpenFolderCommand { get; }

    /// <summary>状态 → 徽标文案（`UI-DESIGN.md` §4 状态徽标色表）。</summary>
    public static string DescribeStatus(DownloadRecord record) => record.Status switch
    {
        DownloadStatus.Downloading => "下载中",
        DownloadStatus.Connecting => "正在连接…",
        DownloadStatus.Queued => "等待中",
        DownloadStatus.Paused => "已暂停",
        DownloadStatus.Completed => "已完成",
        DownloadStatus.Failed => $"失败：{record.ErrorText ?? "未知错误"}",
        DownloadStatus.DiskError => record.ErrorText ?? "磁盘错误",
        _ => "未知",
    };

    /// <summary>用最新快照刷新自己；进度变化 &lt; 1% 时不重绘进度条，但文字照更。</summary>
    public void Update(DownloadRecord record)
    {
        var previous = _record;
        _record = record;

        var newProgress = record.Progress;
        if (Math.Abs(newProgress - _progress) >= 0.01 || newProgress >= 1)
        {
            Progress = newProgress;
        }

        if (!string.Equals(previous.FileName, record.FileName, StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(FileName));
            RaisePropertyChanged(nameof(Extension));
            RaisePropertyChanged(nameof(ToolTipText));
        }

        RaisePropertyChanged(nameof(SizeText));
        RaisePropertyChanged(nameof(SpeedText));
        RaisePropertyChanged(nameof(RemainingText));
        RaisePropertyChanged(nameof(SegmentAndFolderText));
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(ErrorText));
        RaisePropertyChanged(nameof(IsCompleted));
        RaisePropertyChanged(nameof(IsFailed));
        RaisePropertyChanged(nameof(IsPaused));
        RaisePropertyChanged(nameof(IsActive));
        RaisePropertyChanged(nameof(ShowProgress));
        RaisePropertyChanged(nameof(ToggleText));
        RaisePropertyChanged(nameof(CompletedAtText));
    }
}
