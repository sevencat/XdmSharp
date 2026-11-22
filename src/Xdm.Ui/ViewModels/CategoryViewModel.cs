using Xdm.Core.Downloads;

namespace Xdm.Ui.ViewModels;

/// <summary>左侧分类过滤条件。</summary>
public enum CategoryKind
{
    All,
    Active,
    Completed,
    Failed,
}

/// <summary>左侧分类项（名称 + 实时计数）。</summary>
public sealed class CategoryViewModel(CategoryKind kind, string name) : ViewModelBase
{
    private int _count;

    public CategoryKind Kind { get; } = kind;

    public string Name { get; } = name;

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    public bool Matches(DownloadRecord record) => Kind switch
    {
        CategoryKind.All => true,
        CategoryKind.Active => record.IsActive,
        CategoryKind.Completed => record.Status is DownloadStatus.Completed,
        CategoryKind.Failed => record.Status is DownloadStatus.Failed or DownloadStatus.DiskError,
        _ => true,
    };
}
