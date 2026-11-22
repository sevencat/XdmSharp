using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Xdm.Core.Tests.Fakes;

/// <summary>文件的行为开关，用来造"不守规矩"的服务器。</summary>
public sealed class FileBehavior
{
    /// <summary>是否支持 Range；<c>false</c> → 恒回 200 全量。</summary>
    public bool SupportsRange { get; init; } = true;

    /// <summary>不写 <c>Content-Length</c>，靠关连接定界。</summary>
    public bool OmitContentLength { get; init; }

    /// <summary>凭空造一个 <c>Content-Length</c>（测试拒写）。</summary>
    public long? LengthOverride { get; init; }

    /// <summary>强制 206 的区间长度（与请求不符时客户端必须拒写）。</summary>
    public long? ForcedRangeLength { get; init; }

    /// <summary>写 N 字节后硬断连接。</summary>
    public long? DropAfterBytes { get; init; }

    /// <summary>直接回这个状态码（429 / 503 / 404 …）。</summary>
    public int? StatusCode { get; init; }

    /// <summary>429 / 503 的 <c>Retry-After</c>（秒）。</summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary><c>Content-Disposition</c> 原样串。</summary>
    public string? ContentDisposition { get; init; }

    /// <summary><c>Last-Modified</c>；<c>null</c> 用默认值。</summary>
    public string? LastModified { get; init; }

    /// <summary>每写完一块睡一会儿，模拟慢速连接。</summary>
    public TimeSpan PerChunkDelay { get; init; }

    /// <summary>慢速连接的分块大小。</summary>
    public int ChunkSize { get; init; } = 16 * 1024;

    /// <summary>回响应头之前先等这么久，用来把任务卡在「正在连接」阶段。</summary>
    public TimeSpan ResponseDelay { get; init; }
}

/// <summary>服务器收到的一次请求（断言用）。<paramref name="Headers"/> 大小写不敏感。</summary>
public sealed record RecordedRequest(
    string Path,
    string? Range,
    string? IfRange,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// 用 <see cref="TcpListener"/> 手写的最小 HTTP/1.1 服务器（<c>TESTING.md</c> §2）：
/// 能逐字节控制响应，才能造畸形响应、断流、无视 Range 这些场景。
/// </summary>
public sealed class FakeHttpServer : IDisposable
{
    /// <summary>默认 <c>Last-Modified</c>。</summary>
    public const string DefaultLastModified = "Sun, 22 Nov 2026 08:00:00 GMT";

    private sealed class FakeFile(byte[] content, FileBehavior behavior)
    {
        public byte[] Content { get; set; } = content;

        public FileBehavior Behavior { get; } = behavior;

        /// <summary>首次真下载响应要截断的字节数（探测的 1 字节请求不消耗它）。</summary>
        public long DropOnceRemaining = behavior.DropAfterBytes ?? 0;
    }

    private readonly TcpListener _listener;
    private readonly Thread _acceptThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, FakeFile> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public FakeHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "fake-http-accept" };
        _acceptThread.Start();
    }

    /// <summary>监听端口。</summary>
    public int Port { get; }

    /// <summary>形如 <c>http://127.0.0.1:12345</c>。</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>已记录的请求。</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    /// <summary>注册一个文件。</summary>
    public void AddFile(string path, byte[] content, FileBehavior? behavior = null)
        => _files[Normalize(path)] = new FakeFile(content, behavior ?? new FileBehavior());

    /// <summary>换掉文件内容（测试"服务器文件变了"）。</summary>
    public void UpdateFile(string path, byte[] content, FileBehavior? behavior = null)
    {
        var key = Normalize(path);
        if (behavior is null && _files.TryGetValue(key, out var existing))
        {
            existing.Content = content;
            return;
        }

        _files[key] = new FakeFile(content, behavior ?? new FileBehavior());
    }

    /// <summary>清掉请求记录（一个用例里分阶段断言时用）。</summary>
    public void ResetRequests()
    {
        while (_requests.TryDequeue(out _))
        {
        }
    }

    private static string Normalize(string path) => path.StartsWith('/') ? path : "/" + path;

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ => Handle(client));
        }
    }

    private void Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var head = ReadHead(stream);
                if (head is null)
                {
                    return;
                }

                var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0)
                {
                    return;
                }

                var parts = lines[0].Split(' ');
                var rawPath = parts.Length > 1 ? parts[1] : "/";
                var query = rawPath.IndexOf('?');
                if (query >= 0)
                {
                    rawPath = rawPath[..query];
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < lines.Length; i++)
                {
                    var colon = lines[i].IndexOf(':');
                    if (colon > 0)
                    {
                        headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
                    }
                }

                headers.TryGetValue("Range", out var range);
                headers.TryGetValue("If-Range", out var ifRange);
                _requests.Enqueue(new RecordedRequest(rawPath, range, ifRange, headers));

                if (!_files.TryGetValue(rawPath, out var file))
                {
                    WriteStatus(stream, "404 Not Found");
                    return;
                }

                Serve(stream, file, range, ifRange);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // 客户端取消了下载：关掉连接即可。
            }
        }
    }

    private static void Serve(NetworkStream stream, FakeFile file, string? rangeHeader, string? ifRange)
    {
        var behavior = file.Behavior;
        if (behavior.ResponseDelay > TimeSpan.Zero)
        {
            Thread.Sleep(behavior.ResponseDelay);
        }

        if (behavior.StatusCode is { } status && status != 200)
        {
            WriteStatus(stream, StatusLine(status), behavior.RetryAfterSeconds);
            return;
        }

        var validator = behavior.LastModified ?? DefaultLastModified;
        var content = file.Content;

        var rangeAllowed = behavior.SupportsRange
            && rangeHeader is not null
            && (ifRange is null || string.Equals(ifRange, validator, StringComparison.Ordinal))
            && content.Length > 0;

        long start = 0;
        long end = content.Length - 1;
        var partial = false;

        if (rangeAllowed && TryParseRange(rangeHeader!, content.Length, out var parsedStart, out var parsedEnd))
        {
            start = parsedStart;
            end = parsedEnd;
            partial = true;
            if (behavior.ForcedRangeLength is { } forced)
            {
                end = Math.Min(content.Length - 1, start + forced - 1);
            }
        }

        var length = end - start + 1;
        var builder = new StringBuilder();
        builder.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        builder.Append("Accept-Ranges: bytes\r\n");
        if (!behavior.OmitContentLength)
        {
            var declared = behavior.LengthOverride ?? length;
            builder.Append("Content-Length: ").Append(declared).Append("\r\n");
        }

        if (partial)
        {
            builder.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/')
                .Append(content.Length).Append("\r\n");
        }

        builder.Append("Last-Modified: ").Append(validator).Append("\r\n");
        if (!string.IsNullOrEmpty(behavior.ContentDisposition))
        {
            builder.Append("Content-Disposition: ").Append(behavior.ContentDisposition).Append("\r\n");
        }

        builder.Append("Connection: close\r\n\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
        stream.Write(headerBytes);

        var remaining = length;
        if (length > 1)
        {
            var drop = Interlocked.Exchange(ref file.DropOnceRemaining, 0);
            if (drop > 0)
            {
                remaining = Math.Min(remaining, drop);
            }
        }

        var offset = start;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(behavior.ChunkSize, remaining);
            stream.Write(content, (int)offset, chunk);
            offset += chunk;
            remaining -= chunk;

            if (behavior.PerChunkDelay > TimeSpan.Zero)
            {
                Thread.Sleep(behavior.PerChunkDelay);
            }
        }
    }

    private static void WriteStatus(NetworkStream stream, string statusLine, int? retryAfterSeconds = null)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(statusLine).Append("\r\n");
        builder.Append("Content-Length: 0\r\n");
        if (retryAfterSeconds is { } seconds)
        {
            builder.Append("Retry-After: ").Append(seconds).Append("\r\n");
        }

        builder.Append("Connection: close\r\n\r\n");
        stream.Write(Encoding.ASCII.GetBytes(builder.ToString()));
    }

    private static string StatusLine(int status) => status switch
    {
        404 => "404 Not Found",
        429 => "429 Too Many Requests",
        503 => "503 Service Unavailable",
        403 => "403 Forbidden",
        500 => "500 Internal Server Error",
        _ => $"{status} Status",
    };

    private static bool TryParseRange(string header, long total, out long start, out long end)
    {
        start = 0;
        end = total - 1;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = header[6..].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        if (!long.TryParse(spec[..dash], out start))
        {
            return false;
        }

        if (start >= total)
        {
            return false;
        }

        var endText = spec[(dash + 1)..].Trim();
        if (endText.Length == 0)
        {
            end = total - 1;
        }
        else if (!long.TryParse(endText, out end) || end < start)
        {
            return false;
        }

        end = Math.Min(end, total - 1);
        return true;
    }

    private static string? ReadHead(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var data = new List<byte>(1024);
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return null;
            }

            for (var i = 0; i < read; i++)
            {
                data.Add(buffer[i]);
            }

            var text = Encoding.ASCII.GetString(data.ToArray());
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                return text[..end];
            }

            if (data.Count > 64 * 1024)
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _acceptThread.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
