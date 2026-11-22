namespace Xdm.Core.Downloads;

/// <summary>
/// 滑动窗口速率 / ETA（对应参考实现的 <c>ProgressTracker</c>）。
/// 只做算术，不碰线程与 IO，方便单测用假时间戳。
/// </summary>
public sealed class ProgressTracker
{
    private readonly TimeSpan _window;
    private readonly Queue<Sample> _samples = new();
    private readonly object _gate = new();

    /// <summary>默认窗口 3 秒：够平滑，又不会被一分钟前的尖峰带偏。</summary>
    public ProgressTracker(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(3);
        if (_window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "窗口必须为正");
        }
    }

    /// <summary>记录一次采样（<paramref name="syncedTotal"/> 只增不减）。</summary>
    public void Add(long syncedTotal, DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            _samples.Enqueue(new Sample(timestamp, syncedTotal));
            var oldest = timestamp - _window;
            // 至少留两个点，否则窗口内的瞬时值永远算不出来。
            while (_samples.Count > 2 && _samples.Peek().Timestamp < oldest)
            {
                _samples.Dequeue();
            }
        }
    }

    /// <summary>按窗口内的首尾差算速率（字节/秒）。</summary>
    public double BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                if (_samples.Count < 2)
                {
                    return 0;
                }

                var first = _samples.Peek();
                var last = _samples.Last();
                var seconds = (last.Timestamp - first.Timestamp).TotalSeconds;
                if (seconds <= 0)
                {
                    return 0;
                }

                var delta = last.Total - first.Total;
                return delta <= 0 ? 0 : delta / seconds;
            }
        }
    }

    /// <summary>按速率与剩余字节估算剩余时间；算不出时返回 <c>null</c>。</summary>
    public TimeSpan? Eta(long? totalSize)
    {
        if (totalSize is not { } size)
        {
            return null;
        }

        long current;
        lock (_gate)
        {
            current = _samples.Count == 0 ? 0 : _samples.Last().Total;
        }

        var speed = BytesPerSecond;
        if (speed <= 0)
        {
            return null;
        }

        var remaining = size - current;
        return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(remaining / speed);
    }

    /// <summary>清空采样（暂停 / 恢复后重新计速）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
        }
    }

    private readonly record struct Sample(DateTimeOffset Timestamp, long Total);
}
