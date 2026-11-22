using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xdm.Core.Downloads;
using Xdm.Ui.ViewModels;
using Xdm.Ui.Views;

namespace Xdm.Ui.Tests;

public class MainWindowTests
{
    [AvaloniaFact]
    public void Window_Shows_Title()
    {
        var window = new MainWindow();

        Assert.Equal("XDM Sharp", window.Title);
    }

    [AvaloniaFact]
    public void Window_Shows_DbPath_InStatusBar()
    {
        var window = new MainWindow(@"D:\XdmData\xdm.db");
        var status = window.FindControl<TextBlock>("StatusText");

        Assert.NotNull(status);
        Assert.Contains(@"D:\XdmData\xdm.db", status!.Text!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Window_CanShow_Headless()
    {
        var window = new MainWindow();
        window.Show();

        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void TaskList_VirtualizesThousandRows()
    {
        var records = Enumerable.Range(1, 1000).Select(i => new DownloadRecord
        {
            Id = i,
            FileName = $"file-{i}.bin",
            Url = $"https://example.com/file-{i}.bin",
            TargetFolder = @"D:\Downloads",
            SizeBytes = 1000,
            SyncedBytes = 500,
            Status = DownloadStatus.Downloading,
            SegmentCount = 8,
            CompletedSegments = 4,
        });

        var viewModel = new MainWindowViewModel(records);
        var window = new MainWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var list = window.FindControl<ListBox>("TaskList");
        Assert.NotNull(list);
        Assert.Equal(1000, viewModel.Rows.Count);

        // 虚拟化生效：1000 条数据只实例化一屏（约十余行），远小于 100
        var realized = list!.GetVisualDescendants().OfType<ListBoxItem>().Count();
        Assert.InRange(realized, 1, 100);
    }
}
