namespace Xdm.Core.Scheduling;

/// <summary>
/// 全局连接配额（`ARCHITECTURE.md` §12.2 第 6 条）：所有任务、所有分段共用一份名额，
/// 防止「任务数 × 段数」把连接数打爆（1000 文件 × 8 段 = 8000 连接，家用路由先崩）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="SpeedLimiter"/> 同为阻塞式闸门：拿不到名额的段线程在这里等，有名额释放就被唤醒。
/// 名额覆盖整个响应生命周期（发请求 → 读完响应体 → 关连接），所以它就是"同时在途的连接数"。
/// </para>
/// <para>
/// 容量可运行中调整：调大立即放行等待者；调小不打断在途连接，等它们读完自然让出名额
/// （用户改设置时正跑着的分段不该被强杀）。
/// </para>
/// <para>
/// <see cref="Dispose"/> 之后仍等待的线程收到 <see cref="OperationCanceledException"/>——引擎按取消收口，
/// 不会把退出过程记成「内部错误」。
/// </para>
/// </remarks>
public sealed class ConnectionSlots : IDisposable
{
    /// <summary>容量变小 / 外部门闩变化时的兜底复查间隔（正常路径由 <see cref="Monitor.PulseAll"/> 唤醒）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();

    private int _capacity;
    private int _inFlight;
    private int _waiting;
    private bool _disposed;

    /// <summary>按 <paramref name="capacity"/> 建配额（夹紧到 1–4096）。</summary>
    public ConnectionSlots(int capacity) => _capacity = Clamp(capacity);

    /// <summary>名额总数。调小不会打断在途连接，只影响之后的新请求。</summary>
    public int Capacity
    {
        get
        {
            lock (_gate)
            {
                return _capacity;
            }
        }

        set => SetCapacity(value);
    }

    /// <summary>当前占用的名额（= 在途连接数）。</summary>
    public int InFlight
    {
        get
        {
            lock (_gate)
            {
                return _inFlight;
            }
        }
    }

    /// <summary>正在等名额的段线程数（诊断 / 测试用）。</summary>
    public int Waiting
    {
        get
        {
            lock (_gate)
            {
                return _waiting;
            }
        }
    }

    /// <summary>名额是否已经用满（界面可据此提示"连接数已到上限"）。</summary>
    public bool IsSaturated
    {
        get
        {
            lock (_gate)
            {
                return _inFlight >= _capacity;
            }
        }
    }

    /// <summary>占用一个名额，名额不够就阻塞等待。取消 / 配额已关闭时抛 <see cref="OperationCanceledException"/>。</summary>
    public void Acquire(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            while (true)
            {
                if (_disposed)
                {
                    throw new OperationCanceledException("连接配额已关闭");
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (_inFlight < _capacity)
                {
                    _inFlight++;
                    return;
                }

                _waiting++;
                try
                {
                    // 有 Release / 调大容量 / Dispose 时会被立刻唤醒；超时只是兜底复查。
                    Monitor.Wait(_gate, PollInterval);
                }
                finally
                {
                    _waiting--;
                }
            }
        }
    }

    /// <summary>归还名额（多余调用只记日志性 clamp，不抛异常：段线程在异常路径上也会调用）。</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_inFlight > 0)
            {
                _inFlight--;
            }

            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>调整名额总数（夹紧到 1–4096），调大时立刻放行等待者。</summary>
    public void SetCapacity(int capacity)
    {
        lock (_gate)
        {
            _capacity = Clamp(capacity);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>关闭配额：等待者收到 <see cref="OperationCanceledException"/>，之后的 <see cref="Acquire"/> 同样。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>占用一个名额并返回作用域租约；<paramref name="slots"/> 为空时租约为空转（不限连接）。</summary>
    /// <remarks>
    /// 用 `using var lease = ConnectionSlots.AcquireLease(...)` 写，重试 / 取消 / 异常都会经
    /// <see cref="SlotLease.Dispose"/> 归还名额，不用在每个出口手写 Release。
    /// </remarks>
    public static SlotLease AcquireLease(ConnectionSlots? slots, CancellationToken cancellationToken)
    {
        if (slots is null)
        {
            return default;
        }

        slots.Acquire(cancellationToken);
        return new SlotLease(slots);
    }

    /// <summary>作用域租约：<see cref="Dispose"/> 归还名额；默认值（无限额）什么都不做。</summary>
    public readonly struct SlotLease : IDisposable
    {
        private readonly ConnectionSlots? _owner;

        internal SlotLease(ConnectionSlots owner) => _owner = owner;

        public void Dispose() => _owner?.Release();
    }

    private static int Clamp(int capacity) => Math.Clamp(capacity, 1, 4096);
}
