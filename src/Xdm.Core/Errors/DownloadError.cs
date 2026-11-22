namespace Xdm.Core.Errors;

/// <summary>
/// 与协议无关的失败原因。界面按它出中文文案（<c>UI-DESIGN.md</c> §4），
/// 引擎内部不把 HTTP 状态码外泄（<c>ARCHITECTURE.md</c> §12.1 契约 3）。
/// </summary>
public enum DownloadError
{
    /// <summary>没有错误。</summary>
    None,

    /// <summary>网络层失败：连不上、连接被重置、DNS 失败。</summary>
    Network,

    /// <summary>读写超时。</summary>
    Timeout,

    /// <summary>对端繁忙（429 / 503），按 Retry-After 等待即可，不算失败。</summary>
    Busy,

    /// <summary>链接失效（403 / 410 / 签名 URL 过期），需要重新解析来源。</summary>
    Expired,

    /// <summary>确定性拒绝：404、认证失败、响应头不合法。</summary>
    Rejected,

    /// <summary>响应长度与请求区间不符，拒绝写入（防止写坏数据）。</summary>
    LengthMismatch,

    /// <summary>服务器文件已变化（If-Range 校验失败），需要整任务重来。</summary>
    ValidatorMismatch,

    /// <summary>磁盘错误：目录不可写、空间不足。</summary>
    Disk,

    /// <summary>发布时目标文件已存在，且策略是「不覆盖」。</summary>
    TargetExists,

    /// <summary>用户取消 / 暂停。</summary>
    Cancelled,

    /// <summary>内部错误（不变量被破坏）。</summary>
    Internal,

    /// <summary>
    /// 落盘内容与来源给的摘要不符（HF 的 LFS sha256）。
    /// 追加在末尾：本枚举按 int 存库，插到中间会让旧库的错误码错位。
    /// </summary>
    Checksum,
}

/// <summary>错误码 → 中文文案的唯一入口。</summary>
public static class DownloadErrorText
{
    public static string Describe(DownloadError error) => error switch
    {
        DownloadError.None => string.Empty,
        DownloadError.Network => "网络连接失败",
        DownloadError.Timeout => "连接超时",
        DownloadError.Busy => "服务器繁忙",
        DownloadError.Expired => "下载链接已失效",
        DownloadError.Rejected => "服务器拒绝请求",
        DownloadError.LengthMismatch => "响应长度与请求不符",
        DownloadError.ValidatorMismatch => "服务器文件已变化",
        DownloadError.Disk => "磁盘写入失败",
        DownloadError.TargetExists => "目标文件已存在",
        DownloadError.Cancelled => "已取消",
        DownloadError.Checksum => "内容校验失败",
        _ => "未知错误",
    };
}
