namespace Xdm.Core.Http;

/// <summary>
/// 一次「按区间取字节」的请求。调度层只描述要哪一段，不拼 HTTP 头。
/// </summary>
public sealed class HttpRangeRequest
{
    /// <summary>初始 URL；重定向由客户端内部跟。</summary>
    public required Uri Url { get; init; }

    /// <summary>区间起点；<c>null</c> 表示整文件请求（单流模式）。</summary>
    public long? Offset { get; init; }

    /// <summary>区间长度；<c>null</c> 表示从 <see cref="Offset"/> 到文件末尾。</summary>
    public long? Length { get; init; }

    /// <summary>
    /// 续传校验用（<c>If-Range</c>）。必须用首次响应的 <c>Last-Modified</c>，
    /// 不能用 ETag —— 同文件不同镜像 ETag 不同（<c>ARCHITECTURE.md</c> §5.2）。
    /// </summary>
    public string? IfRange { get; init; }

    /// <summary>任务级 Cookie，原样附带（不启用 CookieContainer）。</summary>
    public string? Cookie { get; init; }

    /// <summary>自定义请求头。</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
