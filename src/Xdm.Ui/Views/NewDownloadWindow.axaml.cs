using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Xdm.Core.Downloads;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Views;

public partial class NewDownloadWindow : Window
{
    private readonly NewDownloadViewModel _viewModel;

    public NewDownloadWindow()
        : this(new NewDownloadViewModel())
    {
    }

    public NewDownloadWindow(NewDownloadViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>主窗口用它在弹窗前预设保存目录。</summary>
    public string SaveFolder
    {
        set => _viewModel.SaveFolder = value;
    }

    /// <summary>主窗口用它在弹窗前预设分段数（来自设置页）。</summary>
    public int Segments
    {
        set => _viewModel.Segments = Math.Clamp(value, 1, 256);
    }

    /// <summary>确认添加时生成的下载请求（<see cref="ShowDialog{TResult}"/> 返回 true 时有效）。</summary>
    public IReadOnlyList<DownloadRequest> CreatedRequests { get; private set; } = [];

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryCreate(out var requests))
        {
            return;
        }

        CreatedRequests = requests;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择保存目录",
            AllowMultiple = false,
        });

        if (folders.Count > 0)
        {
            var path = folders[0].Path.LocalPath;
            if (!string.IsNullOrEmpty(path))
            {
                _viewModel.SaveFolder = path;
            }
        }
    }
}
