using System.Collections.ObjectModel;
using System.Windows.Input;
using Xdm.Core.Downloads;
using Xdm.Ui.Formatting;

namespace Xdm.Ui.ViewModels;

/// <summary>
/// 列表行（`UI-DESIGN.md` §4）。多文件任务只渲染父行（子行不单独出列表），
/// 父行的进度/速度/状态由引擎汇总成一条 <see cref="DownloadRecord"/>。
/// </summary>
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

    /// <summary>扩展名（大写，不含点），用于行左图标；无扩展名时为 <c>文件</c>，父行是 <c>包</c>。</summary>
    public string Extension
    {
        get
        {
            if (IsContainer)
            {
                // 仓库名里常带版本号点号（Qwen2.5-7B），按扩展名取会显示成 "5-7B"。
                return "包";
            }

            var ext = Path.GetExtension(_record.FileName);
            return ext.Length > 1 ? ext[1..Math.Min(5, ext.Length)].ToUpperInvariant() : "文件";
        }
    }

    public int Segments => _record.SegmentCount;

    public bool IsParent => _record.ParentId is null;

    public bool IsChild => !IsParent;

    /// <summary>多文件任务的父行（HF 模型包）：第二行显示「文件数」，不是「段数」。</summary>
    public bool IsContainer => _record.IsContainer;

    /// <summary>多文件子任务的子行；列表里不显示，留给以后的展开视图。</summary>
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

    /// <summary>
    /// 第二行中部：进行中 `3/12 段 · D:\Downloads`；完成态 `D:\Downloads · 完成于 …`；
    /// 父行把「段」换成「个文件」。
    /// </summary>
    public string SegmentAndFolderText
    {
        get
        {
            if (IsCompleted)
            {
                return _record.CompletedAt is null
                    ? _record.TargetFolder
                    : $"{_record.TargetFolder} · 完成于 {TextFormat.CompletedAt(_record.CompletedAt)}";
            }

            return IsContainer
                ? $"{_record.CompletedChildren}/{_record.ChildCount} 个文件 · {_record.TargetFolder}"
                : $"{_record.CompletedSegments}/{_record.SegmentCount} 段 · {_record.TargetFolder}";
        }
    }

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
