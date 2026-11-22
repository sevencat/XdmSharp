namespace Xdm.Core.Http;

/// <summary>
/// 「按区间取字节」的传输层抽象。实现必须是**阻塞**调用（线程模型见
/// <c>ARCHITECTURE.md</c> §6），并且不得让调用方判断 HTTP 状态码。
/// </summary>
public interface IRangeHttpClient
{
    /// <summary>
    /// 发一次请求并读回响应头（响应体留给调用方流式读取）。
    /// 只有连接层异常会被转成 <see cref="HttpRangeResponse.Info"/> 里的失败语义，
    /// 本方法不抛网络异常（取消除外）。
    /// </summary>
    HttpRangeResponse Send(HttpRangeRequest request, CancellationToken cancellationToken);
}
