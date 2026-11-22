using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Xdm.Core.Configuration;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly Func<AppSettings, string?>? _onSave;

    /// <summary>设计器/测试用无参构造：不落盘。</summary>
    public SettingsWindow()
        : this(new SettingsViewModel(new AppSettings(), string.Empty), null)
    {
    }

    /// <param name="onSave">落盘 + 应用；返回非空字符串表示失败原因（窗口保持打开）。</param>
    public SettingsWindow(SettingsViewModel viewModel, Func<AppSettings, string?>? onSave = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _onSave = onSave;
        DataContext = viewModel;
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.TryBuild(out var settings))
        {
            return;
        }

        var error = _onSave?.Invoke(settings);
        if (error is not null)
        {
            _viewModel.ShowError(error);
            return;
        }

        Close(true);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择默认保存目录",
            AllowMultiple = false,
        });

        if (folders.Count > 0)
        {
            var path = folders[0].Path.LocalPath;
            if (!string.IsNullOrEmpty(path))
            {
                _viewModel.DefaultSaveFolder = path;
            }
        }
    }
}
