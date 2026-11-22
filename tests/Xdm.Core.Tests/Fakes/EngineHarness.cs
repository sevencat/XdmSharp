using System.Collections.Concurrent;
using Xdm.Core.Downloads;
using Xdm.Core.Errors;
using Xdm.Core.Http;

namespace Xdm.Core.Tests.Fakes;

/// <summary>记录引擎回调，测试只读快照。</summary>
public sealed class RecordingObserver : IDownloadObserver
{
    private readonly ConcurrentQueue<(long Id, DownloadStatus Status)> _statuses = new();
    private readonly ConcurrentQueue<(long Synced, double Speed)> _progress = new();
    private readonly ConcurrentQueue<IReadOnlyList<SegmentSnapshot>> _segments = new();
    private readonly ConcurrentQueue<(long Id, string Path)> _completed = new();
    private readonly ConcurrentQueue<(long Id, DownloadError Error, string Detail)> _failed = new();

    public IReadOnlyList<(long Id, DownloadStatus Status)> Statuses => _statuses.ToArray();

    public IReadOnlyList<(long Synced, double Speed)> Progress => _progress.ToArray();

    public IReadOnlyList<IReadOnlyList<SegmentSnapshot>> SegmentUpdates => _segments.ToArray();

    public IReadOnlyList<(long Id, string Path)> Completed => _completed.ToArray();

    public IReadOnlyList<(long Id, DownloadError Error, string Detail)> Failed => _failed.ToArray();

    public void OnTaskStatusChanged(long id, DownloadStatus status) => _statuses.Enqueue((id, status));

    public void OnProgress(long id, long syncedTotal, double bytesPerSecond, TimeSpan? eta)
        => _progress.Enqueue((syncedTotal, bytesPerSecond));

    public void OnSegmentsChanged(long id, IReadOnlyList<SegmentSnapshot> segments) => _segments.Enqueue(segments);

    public void OnCompleted(long id, string finalPath) => _completed.Enqueue((id, finalPath));

    public void OnFailed(long id, DownloadError error, string detail) => _failed.Enqueue((id, error, detail));
}

/// <summary>每个用例一套：假服务器 + 临时目录 + 引擎。</summary>
public sealed class EngineHarness : IDisposable
{
    public EngineHarness(DownloadEngineOptions? options = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "xdm-core-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TargetFolder);
        Directory.CreateDirectory(StateDir);
        Server = new FakeHttpServer();
        Client = new RangeHttpClient();
        Engine = new DownloadEngine(Client, StateDir, Observer, options ?? FastOptions);
    }

    public FakeHttpServer Server { get; }

    public RangeHttpClient Client { get; }

    public DownloadEngine Engine { get; }

    public RecordingObserver Observer { get; } = new();

    public string Root { get; }

    public string TargetFolder => Path.Combine(Root, "downloads");

    public string StateDir => Path.Combine(Root, "state");

    /// <summary>测试用的快节奏参数：小分段、短超时，跑得快且仍走真实分支。</summary>
    public static DownloadEngineOptions FastOptions => new()
    {
        MaxSegments = 4,
        MinSegmentSize = 64 * 1024,
        BufferSize = 16 * 1024,
        ReadTimeout = TimeSpan.FromSeconds(5),
        FlushInterval = TimeSpan.FromMilliseconds(20),
        MaxRetries = 2,
        RetryBaseDelay = TimeSpan.FromMilliseconds(10),
        ProgressInterval = TimeSpan.FromMilliseconds(20),
        MaxBusyWaits = 2,
    };

    public DownloadTask CreateTask(string path, long id = 1, string? fileName = null, int maxSegments = 4)
        => new(id, Server.BaseUrl + path, fileName ?? string.Empty, TargetFolder, maxSegments);

    /// <summary>确定性字节序列：内容错位一眼能看出来。</summary>
    public static byte[] MakeBytes(int length, int seed = 7)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31 + seed) & 0xFF);
        }

        return bytes;
    }

    public bool WaitFor(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    public DownloadResult WaitForResult(DownloadTask task, TimeSpan? timeout = null)
    {
        Assert.True(
            task.Completion.Task.Wait(timeout ?? TimeSpan.FromSeconds(15)),
            $"任务 {task.Id} 在超时内既没成功也没失败（状态 {task.Status}，已落盘 {task.SyncedTotal}）");
        return task.Completion.Task.Result;
    }

    public void Dispose()
    {
        Engine.Dispose();
        Client.Dispose();
        Server.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不算测试失败。
        }
    }
}
