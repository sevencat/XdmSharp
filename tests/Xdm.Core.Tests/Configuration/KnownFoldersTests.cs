using Xdm.Core.Configuration;

namespace Xdm.Core.Tests.Configuration;

public class KnownFoldersTests
{
    [Fact]
    public void Downloads_IsAbsolutePath()
    {
        var path = KnownFolders.Downloads();

        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.True(Path.IsPathFullyQualified(path), $"默认下载目录不是绝对路径：{path}");
        Assert.DoesNotContain(path, "Downloads\\Downloads", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Downloads_MatchesShellResolution_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 重定向过的用户（下载目录搬到 D:\、或 OneDrive 接管）只能问 shell 要，
        // 这里用注册表里的 Shell Folders 作为独立来源对一下。
        var registry = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders",
            "{374DE290-123F-4565-9164-39C4925E467B}",
            null) as string;

        if (string.IsNullOrWhiteSpace(registry))
        {
            return; // 极简 / Server Core 环境没有这一项
        }

        Assert.Equal(registry, KnownFolders.Downloads(), ignoreCase: true);
    }
}
