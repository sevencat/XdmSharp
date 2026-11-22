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
        viewModel.ConfirmDelete = ConfirmDeleteAsync;
        KeyDown += OnWindowKeyDown;
    }

    /// <summary>点击「设置」时抛出；组合根负责弹窗并把保存结果落盘（Ui 不依赖 App）。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>关闭窗口前是否弹确认（组合根设为 true；无头测试保持 false 以免挂住）。</summary>
    public bool ConfirmOnExit { get; set; }

    private bool _exitConfirmed;
    private bool _exitDialogOpen;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (ConfirmOnExit && !_exitConfirmed)
        {
            e.Cancel = true;
            _ = ConfirmAndExitAsync();
            return;
        }

        base.OnClosing(e);
    }

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
                _ = _viewModel.DeleteAsync(row);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 弹「新建下载」并预填链接。工具栏「新建」、命令行 URL、第二个实例转发都走这一条路径。
    /// </summary>
    /// <param name="initialUrl">预填的链接；空则等同普通新建。</param>
    public async Task BeginNewDownloadAsync(string? initialUrl = null)
    {
        var dialog = new NewDownloadWindow
        {
            SaveFolder = _viewModel.DefaultSaveFolder,
            Segments = _viewModel.DefaultSegments,
            InitialUrl = initialUrl ?? string.Empty,
        };

        var confirmed = await dialog.ShowDialog<bool>(this);
        if (confirmed)
        {
            _viewModel.StartDownloads(dialog.CreatedRequests);
        }
    }

    private async void OnNewDownloadClick(object? sender, RoutedEventArgs e)
        => await BeginNewDownloadAsync();

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new AboutWindow();
        await dialog.ShowDialog(this);
    }

    private async void OnExitClick(object? sender, RoutedEventArgs e)
        => await ConfirmAndExitAsync();

    /// <summary>
    /// 删除确认：只有已完成的任务才有「已下载的文件」可删，其它状态只删临时文件；
    /// 返回 <c>null</c> = 用户取消。
    /// </summary>
    private async Task<bool?> ConfirmDeleteAsync(DownloadRowViewModel row)
    {
        var name = string.IsNullOrWhiteSpace(row.FileName) ? row.Url : row.FileName;

        if (!row.IsCompleted)
        {
            var message = $"确定删除任务「{name}」？\n未完成的临时文件（.xdm-part）会一起删除。";
            return await ConfirmDialog.ShowAsync(this, "删除任务", message, "删除", "取消") ? false : null;
        }

        var result = await ConfirmDialog.ShowWithOptionAsync(
            this,
            "删除任务",
            $"确定删除任务「{name}」？\n任务记录会从列表中移除。",
            "同时删除已下载的文件",
            "删除",
            "取消");
        return result.Confirmed ? result.OptionChecked : null;
    }

    /// <summary>退出确认：点「退出」按钮与关窗口（含标题栏关闭键 / Alt+F4）共用。</summary>
    private async Task ConfirmAndExitAsync()
    {
        if (_exitDialogOpen || _exitConfirmed)
        {
            return;
        }

        _exitDialogOpen = true;
        try
        {
            var active = _viewModel.ActiveCount;
            var message = active > 0
                ? $"还有 {active} 个任务正在下载。\n退出后进度会保存，下次启动可继续。\n\n确定退出？"
                : "确定退出 XDM Sharp？";

            if (!await ConfirmDialog.ShowAsync(this, "退出", message, "退出", "取消"))
            {
                return;
            }

            _exitConfirmed = true;
            Close();
        }
        finally
        {
            _exitDialogOpen = false;
        }
    }

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
