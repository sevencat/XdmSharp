using Microsoft.Win32.SafeHandles;

namespace Xdm.Core.Storage;

/// <summary>
/// 未完成文件（<c>.<id>.xdm-part</c>）的唯一写入口：**单句柄 + 偏移写**。
/// </summary>
/// <remarks>
/// 所有字节都必须经过这里（<c>ARCHITECTURE.md</c> §12.1 契约 5）：
/// 多段并发写同一个句柄，用 <see cref="RandomAccess"/> 指定偏移，天然不需要锁；
/// 将来 BT 的乱序 piece 也能直接落盘。
/// </remarks>
public sealed class PartFile : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly bool _bounded;
    private long _length;
    private bool _disposed;

    private PartFile(SafeFileHandle handle, string path, long length, bool bounded)
    {
        _handle = handle;
        Path = path;
        _length = length;
        _bounded = bounded;
    }

    /// <summary>文件路径。</summary>
    public string Path { get; }

    /// <summary>当前逻辑长度（字节）。</summary>
    public long Length => Interlocked.Read(ref _length);

    /// <summary>句柄是否仍然打开（测试用来确认资源已释放）。</summary>
    public bool IsOpen => !_disposed && !_handle.IsClosed;

    /// <summary>
    /// 建新文件并预置长度。<paramref name="length"/> 为 <c>null</c> 表示总大小未知（EOF 模式），
    /// 此时允许写到当前末尾之后，文件按实际写入增长。
    /// </summary>
    public static PartFile Create(string path, long? length)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var handle = File.OpenHandle(
            path,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            FileOptions.None);

        var bounded = length.HasValue;
        var initial = length ?? 0;
        if (bounded)
        {
            RandomAccess.SetLength(handle, initial);
        }

        return new PartFile(handle, path, initial, bounded);
    }

    /// <summary>打开已存在的续传文件；长度以磁盘为准。</summary>
    public static PartFile Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.None);
        var length = RandomAccess.GetLength(handle);
        return new PartFile(handle, path, length, bounded: true);
    }

    /// <summary>按偏移写入。<paramref name="offset"/> 必须非负，且不得越过已知长度。</summary>
    /// <exception cref="ArgumentOutOfRangeException">越过边界（防止段区间算出负数或越界把文件写坏）。</exception>
    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var end = offset + data.Length;
        if (_bounded && end > _length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"写入越界：offset={offset} len={data.Length} 超出文件长度 {_length}");
        }

        if (!_bounded && end > _length)
        {
            RandomAccess.SetLength(_handle, end);
            Interlocked.Exchange(ref _length, end);
        }

        RandomAccess.Write(_handle, data, offset);
    }

    /// <summary>把内存里的写操作落到磁盘（对应 Kotlin 的 fsync）。</summary>
    public void FlushToDisk()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RandomAccess.FlushToDisk(_handle);
    }

    /// <summary>调整文件长度（预分配 / 截断到 0 重下）。</summary>
    public void SetLength(long length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        RandomAccess.SetLength(_handle, length);
        Interlocked.Exchange(ref _length, length);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }
}
