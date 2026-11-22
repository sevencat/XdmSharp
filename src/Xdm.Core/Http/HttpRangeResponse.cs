namespace Xdm.Core.Http;

/// <summary>
/// 一次区间请求的结果：响应头 + 可读的响应体流。<see cref="Dispose"/> 会释放底层连接。
/// </summary>
public sealed class HttpRangeResponse : IDisposable
{
    private readonly HttpResponseMessage? _message;

    internal HttpRangeResponse(HttpResponseInfo info, Stream? body, HttpResponseMessage? message)
    {
        Info = info;
        Body = body;
        _message = message;
    }

    /// <summary>响应头解析结果。</summary>
    public HttpResponseInfo Info { get; }

    /// <summary>响应体流；失败时为 <c>null</c>。</summary>
    public Stream? Body { get; }

    /// <summary>构造一个不带响应体的结果（连接失败 / 被拒绝）。</summary>
    public static HttpRangeResponse Failure(HttpResponseInfo info) => new(info, null, null);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Body?.Dispose();
        }
        finally
        {
            _message?.Dispose();
        }
    }
}
