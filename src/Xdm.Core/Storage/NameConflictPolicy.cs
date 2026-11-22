namespace Xdm.Core.Storage;

/// <summary>发布时目标目录里已有同名文件的处理策略（<c>UI-DESIGN.md</c> §6）。</summary>
public enum NameConflictPolicy
{
    /// <summary>自动改名：<c>a.zip</c> → <c>a (1).zip</c>。默认。</summary>
    Rename,

    /// <summary>覆盖已有文件（整文件下载完才替换，过程不产生半个文件）。</summary>
    Overwrite,

    /// <summary>视为失败，保留 <c>.xdm-part</c> 以便改名后重试。</summary>
    Fail,
}
