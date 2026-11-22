using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Xdm.Core.Errors;
using Xdm.Core.Http;
using Xdm.Core.Scheduling;
using Xdm.Core.Storage;

namespace Xdm.Core.Downloads;

/// <summary>单段下载结果。</summary>
internal readonly record struct SegmentRunResult(bool Success, DownloadError Error, string? Detail)
{
    public static SegmentRunResult Ok => new(true, DownloadError.None, null);

    public static SegmentRunResult Fail(DownloadError error, string? detail) => new(false, error, detail);
}

/// <summary>
/// 一段的下载循环（对应 Kotlin 的 <c>HttpChunkRetriever.retrieveChunk</c>）：
/// Range 请求 → 校验响应 → 流式写 <see cref="PartFile"/> → 定期 fsync →
/// 出错按退避重试，重试从 <see cref="Segment.Downloaded"/> 续写。
/// </summary>
internal sealed class SegmentRunner
{
    private readonly DownloadTask _task;
    private readonly Segment _segment;
    private readonly IRangeHttpClient _client;
    private readonly PartFile _part;
    private readonly DownloadEngineOptions _options;

    public SegmentRunner(
        DownloadTask task,
        Segment segment,
        IRangeHttpClient client,
        PartFile part,
        DownloadEngineOptions options)
    {
        _task = task;
        _segment = segment;
        _client = client;
        _part = part;
        _options = options;
    }

    public SegmentRunResult Run(CancellationToken cancellationToken)
    {
        // 恢复时从已落盘值起算：没 fsync 的字节重下覆盖，幂等。
        _segment.SetDownloaded(_segment.Synced);
        _segment.SetStatus(SegmentStatus.Downloading);

        var retries = 0;
        var busyWaits = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long? offset = _task.NoRangeSupport ? null : _segment.Offset + _segment.Downloaded;
            var remaining = RemainingForRequest();
            var request = new HttpRangeRequest
            {
                Url = new Uri(_task.FinalUrl ?? _task.Url),
                Offset = offset,
                Length = remaining,
                IfRange = _task.Validator,
                Headers = _task.Headers,
                Cookie = _task.Cookie,
            };

            try
            {
                // 全局连接配额：名额覆盖「发请求 → 读完响应体 → 关连接」整段，重试 / 取消都由 using 归还。
                using var lease = ConnectionSlots.AcquireLease(_options.ConnectionSlots, cancellationToken);
                using var response = _client.Send(request, cancellationToken);
                var info = response.Info;

                switch (info.Outcome)
                {
                    case SourceOutcome.Ok:
                    case SourceOutcome.Unsupported:
                        if (info.Outcome == SourceOutcome.Unsupported && offset is not null)
                        {
                            return SegmentRunResult.Fail(DownloadError.ValidatorMismatch, "服务器忽略 Range，文件可能已变化");
                        }

                        if (response.Body is null)
                        {
                            if (!ShouldRetry(ref retries, DownloadError.Network, "响应体为空", cancellationToken))
                            {
                                return SegmentRunResult.Fail(DownloadError.Network, "响应体为空");
                            }

                            continue;
                        }

                        CopyBody(response.Body, offset is null ? null : remaining, cancellationToken);
                        _part.FlushToDisk();
                        _segment.SetSynced(_segment.Downloaded);
                        _segment.SetStatus(SegmentStatus.Finished);
                        return SegmentRunResult.Ok;

                    case SourceOutcome.Busy:
                        if (++busyWaits > _options.MaxBusyWaits)
                        {
                            return SegmentRunResult.Fail(DownloadError.Busy, "服务器持续繁忙");
                        }

                        Wait(info.RetryAfter ?? TimeSpan.FromSeconds(1), cancellationToken);
                        continue;

                    case SourceOutcome.Retry:
                        if (!ShouldRetry(ref retries, info.Error, info.Detail, cancellationToken))
                        {
                            return SegmentRunResult.Fail(info.Error, info.Detail);
                        }

                        continue;

                    default:
                        return SegmentRunResult.Fail(
                            info.Error == DownloadError.None ? DownloadError.Rejected : info.Error,
                            info.Detail);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _segment.SetStatus(SegmentStatus.Cancelled);
                throw;
            }
            catch (TimeoutException ex)
            {
                if (!ShouldRetry(ref retries, DownloadError.Timeout, ex.Message, cancellationToken))
                {
                    return SegmentRunResult.Fail(DownloadError.Timeout, ex.Message);
                }
            }
            catch (IOException ex)
            {
                if (!ShouldRetry(ref retries, DownloadError.Network, ex.Message, cancellationToken))
                {
                    return SegmentRunResult.Fail(DownloadError.Network, ex.Message);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return SegmentRunResult.Fail(DownloadError.Internal, ex.Message);
            }
        }
    }

    private long? RemainingForRequest()
    {
        if (_segment.Length < 0)
        {
            return null;
        }

        var left = _segment.Length - _segment.Downloaded;
        return left <= 0 ? 0 : left;
    }

    private void CopyBody(Stream body, long? expectedLength, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        var lastFlush = Stopwatch.GetTimestamp();
        var written = 0L;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var want = expectedLength is { } expected
                    ? (int)Math.Min(buffer.Length, expected - written)
                    : buffer.Length;
                if (want <= 0)
                {
                    break;
                }

                var read = ReadWithTimeout(body, buffer, want, cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                // 全局限速：按实际要写的字节申请配额；等待期间读循环暂停，自然反压 TCP 窗口。
                _options.Limiter?.Acquire(read, cancellationToken);

                _part.Write(_segment.Offset + _segment.Downloaded, buffer.AsSpan(0, read));
                _segment.AddDownloaded(read);
                written += read;

                if (Stopwatch.GetElapsedTime(lastFlush) >= _options.FlushInterval)
                {
                    _part.FlushToDisk();
                    _segment.SetSynced(_segment.Downloaded);
                    lastFlush = Stopwatch.GetTimestamp();
                }
            }

            if (expectedLength is { } need && written < need)
            {
                throw new IOException($"响应体提前结束：期望 {need}，实际 {written}");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>阻塞读 + 每次读独立计时。超时后由调用方 Dispose 响应打断底层读。</summary>
    private int ReadWithTimeout(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var readTask = stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken).AsTask();
        try
        {
            if (!readTask.Wait(_options.ReadTimeout))
            {
                _ = readTask.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw new TimeoutException("读取超时");
            }
        }
        catch (AggregateException aggregate)
        {
            // Task.Wait 会把流异常包进 AggregateException；拆出来让上层按 IOException 重试。
            ExceptionDispatchInfo.Capture(aggregate.InnerException ?? aggregate).Throw();
        }

        return readTask.GetAwaiter().GetResult();
    }

    private bool ShouldRetry(ref int retries, DownloadError error, string? detail, CancellationToken cancellationToken)
    {
        if (retries >= _options.MaxRetries)
        {
            return false;
        }

        retries++;
        var delay = _options.RetryBaseDelay * Math.Pow(2, retries - 1);
        if (delay > TimeSpan.FromSeconds(10))
        {
            delay = TimeSpan.FromSeconds(10);
        }

        Wait(delay, cancellationToken);
        return true;
    }

    private static void Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        bool cancelled;
        try
        {
            cancelled = cancellationToken.WaitHandle.WaitOne(delay);
        }
        catch (ObjectDisposedException)
        {
            // 任务收尾已经释放 CTS：按取消处理，别让段线程抛未捕获异常。
            cancelled = true;
        }

        if (cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
