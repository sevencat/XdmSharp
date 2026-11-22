using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Xdm.Core.Shell;

/// <summary>
/// 单实例闸门：同一个任务库只应有一个进程在跑（PLAN.md M6）。
/// 命名 Mutex 决定谁是主实例，命名管道负责把第二个实例的诉求交给主实例：
/// 载荷就是待下载的链接，空串表示「只是把窗口提到前面」。
/// </summary>
/// <remarks>
/// Mutex 名带 <c>Local\</c> 前缀，按登录会话隔离——远程桌面里同一用户的两个会话互不干扰。
/// 名字由库路径哈希而来，所以不同 <c>--db</c> 的实例可以并存（正是多配置并行下载的需要）。
/// </remarks>
public sealed class SingleInstanceHost : IDisposable
{
    /// <summary>客户端等主实例建好管道的最长时间。</summary>
    public const int DefaultSendTimeoutMs = 3000;

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly Action<string?> _onActivated;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _listener;
    private bool _disposed;

    private SingleInstanceHost(Mutex mutex, string pipeName, Action<string?> onActivated)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _onActivated = onActivated;

        _listener = new Thread(Listen)
        {
            IsBackground = true,
            Name = "xdm-single-instance",
        };
        _listener.Start();
    }

    /// <summary>抢主实例；已有实例在跑时返回 <see langword="null"/>（调用方应把参数转发过去后退出）。</summary>
    public static SingleInstanceHost? TryAcquire(string dbPath, Action<string?> onActivated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(onActivated);

        // 不取所有权：谁先创建谁就是主实例。句柄活着期间，后来的进程看到的 createdNew 恒为 false。
        // 这样也避免了「Mutex 归创建线程所有、换线程 Dispose 会抛」的坑。
        var mutex = new Mutex(initiallyOwned: false, MutexNameFor(dbPath), out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceHost(mutex, PipeNameFor(dbPath), onActivated);
    }

    /// <summary>把第二个实例的诉求送给主实例；主实例没在监听时返回 <see langword="false"/>。</summary>
    public static bool TrySend(string dbPath, string? url, int timeoutMs = DefaultSendTimeoutMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        try
        {
            using var client = new NamedPipeClientStream(".", PipeNameFor(dbPath), PipeDirection.Out);
            client.Connect(timeoutMs);

            using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(url ?? string.Empty);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>同一任务库的互斥体名（按登录会话隔离）。</summary>
    public static string MutexNameFor(string dbPath) => $@"Local\xdm-sharp-{KeyOf(dbPath)}";

    /// <summary>同一任务库的管道名。</summary>
    public static string PipeNameFor(string dbPath) => $"xdm-sharp-{KeyOf(dbPath)}";

    /// <summary>库路径 → 稳定的 16 位十六进制键（忽略大小写，路径先取全路径）。</summary>
    public static string KeyOf(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        var normalized = Path.GetFullPath(dbPath).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        // 监听线程可能正阻塞在 WaitForConnection 上：自己连一下把它叫醒（取消后不再派发）。
        try
        {
            using var waker = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            waker.Connect(200);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // 只是叫醒，叫不醒也有 Join 超时兜底。
        }

        _listener.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
        _mutex.Dispose();
    }

    private void Listen()
    {
        var token = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                server.WaitForConnectionAsync(token).GetAwaiter().GetResult();

                string payload;
                using (var reader = new StreamReader(server, Encoding.UTF8))
                {
                    payload = reader.ReadToEnd();
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    _onActivated(payload.Length == 0 ? null : payload);
                }
                catch (Exception ex)
                {
                    // 回调里的异常不能带走监听线程，否则后续转发全部失效。
                    Console.Error.WriteLine($"[single-instance] 处理转发请求失败：{ex}");
                    Console.Error.Flush();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
