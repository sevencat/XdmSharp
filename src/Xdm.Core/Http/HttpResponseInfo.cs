using Xdm.Core.Errors;

namespace Xdm.Core.Http;

/// <summary>
/// 响应头解析结果。<see cref="Outcome"/> 是给调度层看的语义，
/// <see cref="StatusCode"/> 仅用于日志与测试断言。
/// </summary>
public sealed class HttpResponseInfo
{
    /// <summary>连接结果（协议无关）。</summary>
    public SourceOutcome Outcome { get; init; } = SourceOutcome.Ok;

    /// <summary>原始 HTTP 状态码，仅供日志/测试；业务分支一律按 <see cref="Outcome"/>。</summary>
    public int StatusCode { get; init; }

    /// <summary>本次响应体的长度（206 时是区间长度；无 <c>Content-Length</c> 时为 <c>null</c>）。</summary>
    public long? ContentLength { get; init; }

    /// <summary>完整文件大小（取自 <c>Content-Range</c> 的 <c>/total</c>，或 200 的 <c>Content-Length</c>）。</summary>
    public long? TotalSize { get; init; }

    /// <summary><c>Content-Range</c> 的起点。</summary>
    public long? RangeStart { get; init; }

    /// <summary><c>Content-Range</c> 的终点（含）。</summary>
    public long? RangeEnd { get; init; }

    /// <summary><c>Last-Modified</c> 原样串，作为 <c>If-Range</c> 校验值。</summary>
    public string? Validator { get; init; }

    /// <summary><c>Content-Disposition</c> 原样串，文件名推导用。</summary>
    public string? ContentDisposition { get; init; }

    /// <summary>跟完重定向后的最终 URL。</summary>
    public Uri? FinalUrl { get; init; }

    /// <summary>429 / 503 的建议等待时间。</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>请求了区间但服务器回了 200 全量 —— 说明不支持 Range。</summary>
    public bool RangeIgnored { get; init; }

    /// <summary>失败原因（<see cref="Outcome"/> 为 Ok 时是 <see cref="DownloadError.None"/>）。</summary>
    public DownloadError Error { get; init; } = DownloadError.None;

    /// <summary>给日志看的细节（不直接展示给用户）。</summary>
    public string? Detail { get; init; }

    /// <summary>本次连接是否可用于读响应体。</summary>
    public bool HasBody => Outcome is SourceOutcome.Ok or SourceOutcome.Unsupported;
}
