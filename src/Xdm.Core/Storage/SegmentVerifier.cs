using System.Security.Cryptography;

namespace Xdm.Core.Storage;

/// <summary>
/// 内容校验挂点：首期给发布前校验用，V2 的 LFS <c>sha256</c>、V3 的 piece 校验都接这里
/// （<c>ARCHITECTURE.md</c> §12.1 契约 4）。首期就要有测试，免得将来接上时是死代码。
/// </summary>
public static class SegmentVerifier
{
    /// <summary>算文件的 SHA256（小写十六进制）。</summary>
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>算一段字节的 SHA256（小写十六进制）。</summary>
    public static string ComputeSha256(ReadOnlySpan<byte> data)
        => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>文件摘要是否与期望一致；期望值为空时视为不需要校验。</summary>
    public static bool VerifyFile(string path, string? expectedSha256)
        => string.IsNullOrWhiteSpace(expectedSha256)
            || string.Equals(ComputeSha256(path), expectedSha256, StringComparison.OrdinalIgnoreCase);
}
