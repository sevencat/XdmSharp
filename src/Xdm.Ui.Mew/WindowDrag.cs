using System.Runtime.InteropServices;

namespace Xdm.Ui.Mew;

/// <summary>
/// 自绘标题栏的拖动：发 <c>WM_NCLBUTTONDOWN/HTCAPTION</c>，让系统把它当标题栏处理
/// （双击最大化、贴边吸附随之免费获得，S1 已验证）。
/// </summary>
internal static class WindowDrag
{
    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private static readonly IntPtr HTCAPTION = 2;

    public static void Begin(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
