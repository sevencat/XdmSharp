namespace Xdm.Core.Downloads;

/// <summary>
/// 一个下载段：文件区间 <c>[Offset, Offset + Length)</c>。
/// </summary>
/// <remarks>
/// 两个计数必须分清（<c>ARCHITECTURE.md</c> §5.1）：
/// <list type="bullet">
/// <item><see cref="Downloaded"/>：内存计数，收到字节就加；</item>
/// <item><see cref="Synced"/>：**已 fsync** 的字节数，进度、ETA、状态文件只认它。</item>
/// </list>
/// 恢复时 <c>Downloaded := Synced</c>，之前没同步的部分重下即可（覆盖写是幂等的）。
/// </remarks>
public sealed class Segment
{
    private long _downloaded;
    private long _synced;
    private int _status;

    /// <summary>构造一个段。<paramref name="length"/> 为 <c>-1</c> 表示总大小未知（EOF 模式，仅单流）。</summary>
    public Segment(long id, long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (length < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "段长度只能是正数或 -1（未知）");
        }

        Id = id;
        Offset = offset;
        Length = length;
    }

    /// <summary>段号（从 0 递增，持久化后用于匹配快照）。</summary>
    public long Id { get; }

    /// <summary>起点（含）。</summary>
    public long Offset { get; }

    /// <summary>长度（<c>-1</c> 表示未知）。</summary>
    public long Length { get; }

    /// <summary>内存已收字节数。</summary>
    public long Downloaded => Interlocked.Read(ref _downloaded);

    /// <summary>已落盘字节数（进度只认它）。</summary>
    public long Synced => Interlocked.Read(ref _synced);

    /// <summary>段状态。</summary>
    public SegmentStatus Status => (SegmentStatus)Volatile.Read(ref _status);

    /// <summary>终点（不含）；未知长度时无意义。</summary>
    public long EndExclusive => Length < 0 ? -1 : Offset + Length;

    /// <summary>还剩多少字节未落盘；未知长度时为 0。</summary>
    public long Remaining => Length < 0 ? 0 : Math.Max(0, Length - Synced);

    /// <summary>该段是否已完整落盘。</summary>
    public bool IsDone => Length >= 0 && Synced >= Length;

    /// <summary>加内存计数。</summary>
    public void AddDownloaded(long count) => Interlocked.Add(ref _downloaded, count);

    /// <summary>直接用已知值设内存计数（恢复时用）。</summary>
    public void SetDownloaded(long value) => Interlocked.Exchange(ref _downloaded, value);

    /// <summary>加已落盘计数。</summary>
    public void AddSynced(long count) => Interlocked.Add(ref _synced, count);

    /// <summary>直接设已落盘计数（恢复时用）。</summary>
    public void SetSynced(long value) => Interlocked.Exchange(ref _synced, value);

    /// <summary>设置状态。</summary>
    public void SetStatus(SegmentStatus status) => Volatile.Write(ref _status, (int)status);

    /// <summary>把 <see cref="Status"/> 从 <see cref="SegmentStatus.Ready"/> 抢成下载中；已被抢过返回 <c>false</c>。</summary>
    public bool TryBegin()
        => Interlocked.CompareExchange(ref _status, (int)SegmentStatus.Downloading, (int)SegmentStatus.Ready)
            == (int)SegmentStatus.Ready;

    /// <inheritdoc />
    public override string ToString()
        => $"Segment#{Id}[{Offset},{EndExclusive}) synced={Synced} {Status}";
}
