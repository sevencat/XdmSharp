using System.Collections.Concurrent;
using Avalonia.Threading;
using Xdm.Core.Downloads;
using Xdm.Ui.ViewModels;

namespace Xdm.App;

/// <summary>
/// 引擎事件 → UI 线程。结构变化（新增/删除）立即投递；进度按 250ms 合并成 4Hz，
/// 跑满 100 个任务时也不会把 UI 线程压死（<c>ARCHITECTURE.md</c> §6）。
/// </summary>
internal sealed class DownloadUiBridge : IDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(250);

    private readonly DownloadManager _manager;
    private readonly MainWindowViewModel _viewModel;
    private readonly ConcurrentDictionary<long, DownloadRecord> _pending = new();
    private readonly DispatcherTimer _timer;

    public DownloadUiBridge(DownloadManager manager, MainWindowViewModel viewModel)
    {
        _manager = manager;
        _viewModel = viewModel;

        manager.TaskAdded += OnTaskAdded;
        manager.TaskRemoved += OnTaskRemoved;
        manager.TaskUpdated += OnTaskUpdated;

        _timer = new DispatcherTimer(
            FrameInterval,
            DispatcherPriority.Background,
            (_, _) => Drain());
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _manager.TaskAdded -= OnTaskAdded;
        _manager.TaskRemoved -= OnTaskRemoved;
        _manager.TaskUpdated -= OnTaskUpdated;
    }

    private void OnTaskAdded(DownloadRecord record)
        => Dispatcher.UIThread.Post(() => _viewModel.Add(record));

    private void OnTaskRemoved(long id)
        => Dispatcher.UIThread.Post(() => _viewModel.RemoveById(id));

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
}
