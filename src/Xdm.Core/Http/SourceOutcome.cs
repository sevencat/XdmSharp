namespace Xdm.Core.Http;

/// <summary>
/// 协议无关的连接结果。HTTP 状态码只允许出现在 <see cref="RangeHttpClient"/> 内部，
/// 调度层只看这些语义（<c>ARCHITECTURE.md</c> §12.1 契约 3）。
/// </summary>
public enum SourceOutcome
{
    /// <summary>成功，可以开始读响应体。</summary>
    Ok,

    /// <summary>瞬时错误（超时 / 5xx / 连接重置），消耗重试预算。</summary>
    Retry,

    /// <summary>对端限流（429 / 503），按 <see cref="HttpResponseInfo.RetryAfter"/> 等待，不消耗预算。</summary>
    Busy,

    /// <summary>不支持区间（请求了 Range 却回 200），退化单流。</summary>
    Unsupported,

    /// <summary>链接失效（403 / 410 / 签名 URL 过期），需要重新解析来源。</summary>
    Expired,

    /// <summary>确定性失败（404 / 认证失败 / 响应头非法）。</summary>
    Rejected,
}
