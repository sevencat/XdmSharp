namespace Xdm.Core.Scheduling;

/// <summary>
/// 全局令牌桶限速（对应 Kotlin 的 <c>SpeedLimiter.kt</c>，改成跨任务共享一个实例）。
/// </summary>
/// <remarks>
/// <para>
/// 所有任务、所有段线程向同一个桶申请配额，因此"全局限速 1 MB/s"就是进程总下载上限，
/// 而不是每任务各 1 MB/s。桶容量取速率的 10%（最少 4 KB），允许小幅突发。
/// </para>
/// <para>
/// 余额够就立刻扣掉并返回，不够就按欠额睡眠后重算；多个线程同时申请也不会互相插队，
/// 总量严格受速率约束。任意时刻把 <see cref="Rate"/> 设为 0 即恢复不限速，
/// 正在等待的线程最多 100 ms 后放行。
/// </para>
/// </remarks>
public sealed class SpeedLimiter
{
    /// <summary>单次等待上限：便于及时响应取消与速率变更。</summary>
    private static readonly TimeSpan MaxSleep = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly TimeProvider _time;

    private double _tokens;
    private long _rate;
    private long _capacity;
    private long _lastTimestamp;

    /// <summary>创建限速器；<paramref name="bytesPerSecond"/> 为 0 表示不限速。</summary>
    public SpeedLimiter(long bytesPerSecond = 0, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _lastTimestamp = _time.GetTimestamp();
        SetRate(bytesPerSecond);
    }

    /// <summary>当前速率（字节/秒）；0 表示不限速。修改即时生效。</summary>
    public long Rate
    {
        get
        {
            lock (_gate)
            {
                return _rate;
            }
        }

        set => SetRate(value);
    }

    /// <summary>是否启用了限速。</summary>
    public bool IsEnabled => Rate > 0;

    /// <summary>设置速率并重置桶（负值按 0 处理）。</summary>
    public void SetRate(long bytesPerSecond)
    {
        var rate = Math.Max(0, bytesPerSecond);
        lock (_gate)
        {
            _rate = rate;
            _capacity = rate == 0 ? 0 : Math.Max(rate / 10, 4 * 1024);
            _tokens = Math.Min(_tokens, _capacity);
            _lastTimestamp = _time.GetTimestamp();
        }
    }

    /// <summary>
    /// 申请 <paramref name="bytes"/> 字节的发送配额，阻塞到额度到账。
    /// 不限速时立即返回；取消时抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    public void Acquire(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0)
        {
            return;
        }

        var remaining = bytes;
        while (remaining > 0)
        {
            TimeSpan wait;
            lock (_gate)
            {
                var rate = _rate;
                if (rate <= 0)
                {
                    _tokens = 0;
                    return;
                }

                Refill(rate);

                // 桶里有多少先拿多少：单次申请可能大于桶容量（100 KB/s 时容量只有 10 KB），
                // 必须允许分多轮凑够，否则余额被容量截断，永远等不到。
                var take = (int)Math.Min(Math.Max(_tokens, 0), remaining);
                if (take > 0)
                {
                    _tokens -= take;
                    remaining -= take;
                }

                if (remaining == 0)
                {
                    return;
                }

                wait = TimeSpan.FromSeconds(remaining / rate);
            }

            if (wait > MaxSleep)
            {
                wait = MaxSleep;
            }

            if (cancellationToken.WaitHandle.WaitOne(wait))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private void Refill(long rate)
    {
        var elapsed = _time.GetElapsedTime(_lastTimestamp);
        _tokens = Math.Min(_capacity, _tokens + elapsed.TotalSeconds * rate);
        _lastTimestamp = _time.GetTimestamp();
    }
}
