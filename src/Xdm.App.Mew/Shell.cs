using System.Diagnostics;

namespace Xdm.App.Mew;

/// <summary>壳层对系统的最少量调用（对齐 Avalonia 版 <c>MainWindow.OpenFolder</c> 的行为）。</summary>
internal static class Shell
{
    /// <summary>在资源管理器里打开目录；目录不存在或系统拒绝时静默忽略（与 Avalonia 版一致）。</summary>
    public static void OpenFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"[mew] open folder failed: {folder}{Environment.NewLine}{ex.Message}");
        }
    }
}
