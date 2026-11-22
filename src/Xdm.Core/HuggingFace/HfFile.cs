namespace Xdm.Core.HuggingFace;

/// <summary>仓库里的一个文件（来自 <c>/api/models/{repo}/tree/{rev}?recursive=true</c>）。</summary>
public sealed record HfFile
{
    /// <summary>仓库内相对路径，形如 <c>onnx/model.onnx</c>。</summary>
    public required string Path { get; init; }

    /// <summary>字节数；服务端没给（极少）时为 <see langword="null"/>。</summary>
    public long? Size { get; init; }

    /// <summary>LFS 对象的 sha256；非 LFS 小文件为 <see langword="null"/>（暂不校验）。</summary>
    public string? Sha256 { get; init; }

    /// <summary>是否走 LFS（大权重）。</summary>
    public bool IsLfs => !string.IsNullOrEmpty(Sha256);

    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>仓库内的相对目录（<c>null</c> 表示根目录）。</summary>
    public string? RelativeFolder =>
        Path.LastIndexOf('/') is var index && index > 0 ? Path[..index] : null;

    /// <summary>路径安全：不允许绝对路径、盘符与 <c>..</c> 段（清单里的路径也要防穿越）。</summary>
    public bool IsSafePath =>
        Path.Length > 0 &&
        !System.IO.Path.IsPathRooted(Path) &&
        Path.IndexOfAny(['\\', ':', '\0']) < 0 &&
        Path.Split('/').All(static segment => segment is not ("" or "." or ".."));
}
