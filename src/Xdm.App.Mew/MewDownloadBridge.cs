using System.Collections.Concurrent;
using Aprillz.MewUI;
using Xdm.Core.Downloads;
using Xdm.Ui.Services;
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

    /// <summary>
    /// 识别「刚变成完成」这一跳（引擎会重复推送同状态）。只在 UI 线程访问
    /// （<see cref="OnTaskAdded"/> 等的回调都经过 <see cref="Post"/> 切到 UI 线程）。
    /// </summary>
    private readonly DownloadCompletionWatcher _completion = new();

    private bool _disposed;

    /// <summary>
    /// 任务刚刚进入 <see cref="DownloadStatus.Completed"/>（同一任务只触发一次，重复推送不会重复报喜）。
    /// 在 UI 线程触发；窗口在前台时由订阅方决定是否忽略。
    /// </summary>
    public event Action<DownloadRecord>? TaskCompleted;

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

    private void OnTaskAdded(DownloadRecord record) => Post(() =>
    {
        // 子行（模型包里的单个文件）不参与「下载完成」通知，否则 12 个文件报 12 次；
        // 父行完成时会自己报一次。
        if (record.ParentId is null)
        {
            _completion.Observe(record.Id, record.Status);
        }

        _viewModel.Add(record);
    });

    private void OnTaskRemoved(long id) => Post(() =>
    {
        _completion.Forget(id);
        _viewModel.RemoveById(id);
    });

    private void OnTaskUpdated(DownloadRecord record) => _pending[record.Id] = record;

    private void Drain()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        foreach (var id in _pending.Keys)
        {
            if (!_pending.TryRemove(id, out var record))
            {
                continue;
            }

            _viewModel.UpdateRow(record);

            if (record.ParentId is not null)
            {
                // 子文件的状态变化已经在父行汇总里体现，不单独报喜。
                continue;
            }

            // 只在「非完成 → 完成」这一跳报一次：引擎会重复推送同状态的记录。
            if (_completion.Observe(id, record.Status))
            {
                TaskCompleted?.Invoke(record);
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
