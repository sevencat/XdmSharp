using System.Collections.Concurrent;
using Aprillz.MewUI;
using Xdm.Core.Downloads;
using Xdm.Ui.ViewModels;

namespace Xdm.App.Mew;

/// <summary>
/// 引擎事件 → UI 线程（Avalonia 版 <c>DownloadUiBridge</c> 的 MewUI 对应物）。
/// 结构变化（新增/删除）立即投递；进度按 250ms 合并成 4Hz，跑满 100 个任务也不会把 UI 线程压死
/// （<c>ARCHITECTURE.md</c> §6）。
/// </summary>
internal sealed class MewDownloadBridge : IDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(250);

    private readonly DownloadManager _manager;
    private readonly MainWindowViewModel _viewModel;
    private readonly ConcurrentDictionary<long, DownloadRecord> _pending = new();
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public MewDownloadBridge(DownloadManager manager, MainWindowViewModel viewModel)
    {
        _manager = manager;
        _viewModel = viewModel;

        manager.TaskAdded += OnTaskAdded;
        manager.TaskRemoved += OnTaskRemoved;
        manager.TaskUpdated += OnTaskUpdated;

        // 必须在 UI 线程创建：构造函数由组合根在 Application.Run 的 startup 回调里调用。
        _timer = new DispatcherTimer(FrameInterval);
        _timer.Tick += Drain;
        _timer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _manager.TaskAdded -= OnTaskAdded;
        _manager.TaskRemoved -= OnTaskRemoved;
        _manager.TaskUpdated -= OnTaskUpdated;
    }

    private void OnTaskAdded(DownloadRecord record) => Post(() => _viewModel.Add(record));

    private void OnTaskRemoved(long id) => Post(() => _viewModel.RemoveById(id));

    private void OnTaskUpdated(DownloadRecord record) => _pending[record.Id] = record;

    private void Drain()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var record))
            {
                _viewModel.UpdateRow(record);
            }
        }
    }

    /// <summary>投递到 UI 线程；循环已退出（窗口关闭后引擎仍在收尾）时直接丢弃。</summary>
    private void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || _disposed)
        {
            return;
        }

        if (dispatcher.IsOnUIThread)
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
