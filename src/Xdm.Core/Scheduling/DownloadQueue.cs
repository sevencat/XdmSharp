namespace Xdm.Core.Scheduling;

/// <summary>
/// 待启动任务队列（FIFO）。只管顺序，不管并发闸门——闸门在
/// <see cref="Xdm.Core.Downloads.DownloadManager"/> 里按 <c>MaxConcurrent</c> 放行。
/// </summary>
public sealed class DownloadQueue
{
    private readonly LinkedList<long> _items = new();
    private readonly object _gate = new();

    /// <summary>排队中的任务数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>入队；已存在则忽略（避免重复启动）。</summary>
    public bool Enqueue(long id)
    {
        lock (_gate)
        {
            if (_items.Contains(id))
            {
                return false;
            }

            _items.AddLast(id);
            return true;
        }
    }

    /// <summary>取队首。</summary>
    public bool TryDequeue(out long id)
    {
        lock (_gate)
        {
            if (_items.First is null)
            {
                id = 0;
                return false;
            }

            id = _items.First.Value;
            _items.RemoveFirst();
            return true;
        }
    }

    /// <summary>移出队列（暂停 / 删除排队中的任务）。</summary>
    public bool Remove(long id)
    {
        lock (_gate)
        {
            var node = _items.Find(id);
            if (node is null)
            {
                return false;
            }

            _items.Remove(node);
            return true;
        }
    }

    /// <summary>清空。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }

    /// <summary>快照（按入队顺序）。</summary>
    public IReadOnlyList<long> ToArray()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }
}
