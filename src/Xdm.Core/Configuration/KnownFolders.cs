using System.Runtime.InteropServices;

namespace Xdm.Core.Configuration;

/// <summary>
/// Windows「已知文件夹」解析（<c>shell32!SHGetKnownFolderPath</c>）。
/// </summary>
/// <remarks>
/// 不能拿 <c>%USERPROFILE%</c> 拼 <c>Downloads</c>：用户可以右键「下载」→ 属性 → 位置把整个目录搬到别的盘，
/// OneDrive 接管后路径也不是 <c>C:\Users\&lt;name&gt;\Downloads</c>。只有问 shell 才知道当前真实位置。
/// </remarks>
public static class KnownFolders
{
    /// <summary>FOLDERID_Downloads = {374DE290-123F-4565-9164-39C4925E467B}。</summary>
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>用户的「下载」目录；取不到时退回到 <c>%USERPROFILE%\Downloads</c>。</summary>
    public static string Downloads() => Resolve(DownloadsFolderId) ?? Fallback("Downloads");

    private static string? Resolve(Guid folderId)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var hr = SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out buffer);
            if (hr < 0 || buffer == IntPtr.Zero)
            {
                return null;
            }

            var path = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 非 Windows 跑单测 / Headless 时没有 shell32。
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
    }

    private static string Fallback(string name)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? name : Path.Combine(profile, name);
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        in Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
