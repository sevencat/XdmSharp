using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xdm.Core.Downloads;
using Xdm.Ui.Mew;
using Xdm.Ui.ViewModels;

namespace Xdm.Ui.Mew.Tests;

/// <summary>
/// 布局类断言。MewUI 的 <see cref="ListBox"/> 把行直接画出来、不为每行建元素
/// （所以 <c>TryGetItemBounds</c> 才存在），这与 Avalonia 的容器虚拟化等价：
/// 断言「300 行不会让元素树膨胀」即可守住性能红线。
/// </summary>
public class LayoutTests
{
    [Fact]
    public void Large_List_Does_Not_Inflate_Element_Tree()
    {
        if (!TestPlatform.EnsureRegistered())
        {
            Assert.Skip("没有可用的渲染后端");
        }

        var window = CreateWindow(300);
        Layout(window);

        var nodes = VisualTree.Descendants(window.Content).Count();

        // 行是绘制出来的、不是元素：300 行时整棵树仍在百级以内。
        Assert.InRange(nodes, 1, 100);
    }

    [Fact]
    public void TaskList_Binds_All_Rows_And_Keeps_Row_Height()
    {
        if (!TestPlatform.EnsureRegistered())
        {
            Assert.Skip("没有可用的渲染后端");
        }

        var window = CreateWindow(300);
        Layout(window);

        var list = VisualTree.TaskList(window);

        Assert.Equal(300, list.ItemsSource?.Count);
        Assert.Equal(MewTheme.RowHeight, list.ItemHeight);
    }

    [Fact]
    public void Layout_Survives_Empty_List()
    {
        if (!TestPlatform.EnsureRegistered())
        {
            Assert.Skip("没有可用的渲染后端");
        }

        var window = CreateWindow(0);
        Layout(window);

        Assert.Equal(0, VisualTree.TaskList(window).ItemsSource?.Count ?? 0);
        Assert.True(VisualTree.ContainsText(window.Content, "没有任务"));
    }

    private static MainWindow CreateWindow(int count)
    {
        var records = Enumerable.Range(1, count).Select(index => new DownloadRecord
        {
            Id = index,
            FileName = $"file-{index}.bin",
            Url = $"https://example.com/file-{index}.bin",
            TargetFolder = @"D:\Downloads",
            SizeBytes = 1024 * 1024,
            SyncedBytes = 512 * 1024,
            Status = DownloadStatus.Downloading,
            SegmentCount = 8,
            CompletedSegments = 4,
        }).ToList();

        return new MainWindow(new MainWindowViewModel(records));
    }

    private static void Layout(Window window)
    {
        var root = window.Content!;
        root.Measure(new Size(1200, 700));
        root.Arrange(new Rect(0, 0, 1200, 700));
    }
}
