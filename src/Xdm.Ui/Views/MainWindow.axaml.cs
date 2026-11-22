using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
        : this(new MainWindowViewModel([]), null)
    {
    }

    public MainWindow(string dbPath)
        : this(new MainWindowViewModel([]), dbPath)
    {
    }

    public MainWindow(MainWindowViewModel viewModel, string? dbPath = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        if (!string.IsNullOrEmpty(dbPath))
        {
            viewModel.DbPath = dbPath;
        }

        viewModel.OpenFolderRequested += (_, path) => OpenFolder(path);
        KeyDown += OnWindowKeyDown;
    }

    /// <summary>点击「设置」时抛出；组合根负责弹窗并把保存结果落盘（Ui 不依赖 App）。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>状态栏显示当前任务库位置，用来验证 <c>--db</c> 生效。</summary>
    public void ShowDbPath(string dbPath) => _viewModel.DbPath = dbPath;

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel.SelectedRow is not { } row)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                _viewModel.TogglePause(row);
                e.Handled = true;
                break;
            case Key.Delete:
                _viewModel.Delete(row);
                e.Handled = true;
                break;
        }
    }

    private async void OnNewDownloadClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new NewDownloadWindow
        {
            SaveFolder = _viewModel.DefaultSaveFolder,
            Segments = _viewModel.DefaultSegments,
        };
        var confirmed = await dialog.ShowDialog<bool>(this);
        if (confirmed)
        {
            _viewModel.StartDownloads(dialog.CreatedRequests);
        }
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private static void OpenFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // 打不开目录不该让进程崩：M1 只静默，后续接状态栏提示
        }
    }
}
