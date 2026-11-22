using System.Runtime.InteropServices;

namespace Xdm.App.Mew;

/// <summary>
/// 前台窗口判定。不能用 <c>Window.IsActive</c>：MewUI 0.22 里窗口最小化之后它仍然是 <c>true</c>
/// （实测最小化再完成下载时 <c>IsActive=True</c>，完成提示会被误判成「用户正看着」而吞掉）。
/// </summary>
internal static class ForegroundWindow
{
    /// <summary>指定窗口是否是当前前台窗口（最小化/被别的程序挡住时为 false）。</summary>
    public static bool Is(IntPtr hwnd) => hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
