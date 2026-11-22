using System.Runtime.InteropServices;

namespace Xdm.App.Mew;

/// <summary>
/// 托盘图标（Win32 <c>Shell_NotifyIcon</c>）。MewUI 0.22 没有托盘控件，所以自己开一个
/// message-only 窗口收托盘回调消息——注意不能用 <c>Window.NativeMessage</c> 事件：
/// 它的 <c>NativeMessageEventArgs</c> 只带 <c>Handled</c>/<c>Result</c>，拿不到消息号与 wParam。
/// </summary>
/// <remarks>
/// 生命周期必须在 UI 线程：窗口创建/销毁、<c>TrackPopupMenu</c> 都要求消息泵在同一线程，
/// 而 MewUI 的 Win32 消息循环正是跑在那个线程上（所以自定义窗口的 WndProc 也能被泵到）。
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;

    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NifInfo = 0x10;

    private const uint NiifInfo = 0x1;

    private const uint WmApp = 0x8000;
    private const uint CallbackMessage = WmApp + 1;
    private const uint WmNull = 0x0000;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDoubleClick = 0x0203;

    private const uint MfString = 0x0;
    private const uint MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2;
    private const uint TpmReturnCmd = 0x100;

    private const int IdShow = 1;
    private const int IdExit = 2;

    private const int TipLength = 128;
    private const int InfoLength = 256;
    private const int InfoTitleLength = 64;

    /// <summary>message-only 窗口的父句柄（HWND_MESSAGE）。</summary>
    private static readonly IntPtr HwndMessage = new(-3);

    /// <summary>
    /// hwnd → 实例表。Win32 的 WndProc 只认裸函数指针，没有 user data 传参，只能靠句柄反查；
    /// 顺带把委托本身钉在这里，避免被 GC 回收后消息循环踩空。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, (TrayIcon? Instance, WindowProc Proc)> Instances = new();

    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly string _className;
    private readonly Action _onShow;
    private readonly Action _onExit;

    private bool _disposed;

    private TrayIcon(IntPtr hwnd, IntPtr icon, string className, Action onShow, Action onExit, string tooltip)
    {
        _hwnd = hwnd;
        _icon = icon;
        _className = className;
        _onShow = onShow;
        _onExit = onExit;

        var data = CreateData(NifMessage | NifIcon | NifTip);
        data.hIcon = icon;
        data.szTip = Truncate(tooltip, TipLength);

        if (!Shell_NotifyIconW(NimAdd, ref data))
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Shell_NotifyIcon(NIM_ADD) 失败，Win32 错误 {error}");
        }
    }

    /// <summary>
    /// 创建托盘图标。只允许在 UI 线程调用，且要等主窗口句柄有效之后（首帧之后）。
    /// </summary>
    /// <param name="tooltip">悬停提示，超长自动截断。</param>
    /// <param name="onShow">左键单击/双击、或菜单「打开」时回调（把主窗口提到前面）。</param>
    /// <param name="onExit">菜单「退出」时回调（由壳决定怎么退出）。</param>
    public static TrayIcon Attach(string tooltip, Action onShow, Action onExit)
    {
        ArgumentNullException.ThrowIfNull(onShow);
        ArgumentNullException.ThrowIfNull(onExit);

        var className = $"XdmTrayHost-{Guid.NewGuid():N}";
        var module = GetModuleHandleW(null);

        var windowClass = new WindowClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WindowClassEx>(),
            lpszClassName = className,
            hInstance = module,
        };

        // WndProc 必须是静态函数指针；实例回调通过下面注册前绑定的委托完成。
        var proc = new WindowProc(ForwardToInstance);
        windowClass.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc);

        if (RegisterClassExW(ref windowClass) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"RegisterClassEx 失败，Win32 错误 {error}");
        }

        var hwnd = CreateWindowExW(
            0,
            className,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            HwndMessage,
            IntPtr.Zero,
            module,
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            UnregisterClassW(className, module);
            throw new InvalidOperationException($"CreateWindowEx 失败，Win32 错误 {error}");
        }

        // 先登记实例，保证第一条消息到达时能转发到正确的对象。
        Instances[hwnd] = (null, proc);

        var icon = LoadApplicationIcon();
        TrayIcon tray;
        try
        {
            tray = new TrayIcon(hwnd, icon, className, onShow, onExit, tooltip);
        }
        catch
        {
            Instances.TryRemove(hwnd, out _);
            DestroyWindow(hwnd);
            UnregisterClassW(className, module);
            if (icon != IntPtr.Zero)
            {
                DestroyIcon(icon);
            }

            throw;
        }

        Instances[hwnd] = (tray, proc);
        Console.Error.WriteLine("[tray] 托盘图标已注册");
        Console.Error.Flush();
        return tray;
    }

    /// <summary>弹系统气泡（Win10/11 会走通知中心）。窗口在前台时不要弹，由调用方判断。</summary>
    /// <returns>系统是否接受了这次通知（被系统「专注助手」「通知关闭」吞掉时也会返回 true）。</returns>
    public bool ShowBalloon(string title, string message)
    {
        if (_disposed)
        {
            return false;
        }

        var data = CreateData(NifInfo);
        data.szInfoTitle = Truncate(title, InfoTitleLength);
        data.szInfo = Truncate(message, InfoLength);
        data.dwInfoFlags = NiifInfo;

        if (Shell_NotifyIconW(NimModify, ref data))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        Console.Error.WriteLine($"[tray] 弹气泡失败，Win32 错误 {error}");
        Console.Error.Flush();
        return false;
    }

    /// <summary>任务栏按钮闪烁，直到窗口被激活（<c>FLASHW_TIMERNOFG</c>）。</summary>
    public static void FlashTaskbar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var info = new FlashInfo
        {
            cbSize = (uint)Marshal.SizeOf<FlashInfo>(),
            hwnd = hwnd,
            dwFlags = FlashwTray | FlashwTimerNoFg,
            uCount = 3,
            dwTimeout = 0,
        };

        FlashWindowEx(ref info);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var data = CreateData(0);
        Shell_NotifyIconW(NimDelete, ref data);

        Instances.TryRemove(_hwnd, out _);
        DestroyWindow(_hwnd);
        UnregisterClassW(_className, GetModuleHandleW(null));

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }
    }

    /// <summary>悬停提示最长 127 字符（<c>szTip</c> 含结尾 0）。</summary>
    internal static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var limit = Math.Max(0, maxLength - 1);
        return value.Length <= limit ? value : value[..limit];
    }

    /// <summary>托盘菜单文案（测试用：保证不会出现空项或重复 id）。</summary>
    internal static IReadOnlyList<(int Id, string Text)> MenuItems() =>
    [
        (IdShow, "打开 XDM Sharp"),
        (IdExit, "退出"),
    ];

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            var mouseMessage = (uint)(lParam.ToInt64() & 0xFFFF);
            switch (mouseMessage)
            {
                case WmLButtonUp:
                case WmLButtonDoubleClick:
                    _onShow();
                    return IntPtr.Zero;

                case 0x0205: // WM_RBUTTONUP
                    ShowMenu();
                    return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            foreach (var (id, text) in MenuItems())
            {
                AppendMenuW(menu, MfString, (nuint)id, text);
            }

            AppendMenuW(menu, MfSeparator, 0, null);

            if (!GetCursorPos(out var point))
            {
                return;
            }

            // 不先抢前台焦点，菜单点外面不会自动关闭（TrackPopupMenu 的经典要求）。
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case IdShow:
                    _onShow();
                    break;

                case IdExit:
                    _onExit();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private NotifyIconData CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,

        // ByValTStr 字段不能留 null，否则封送时会写出非法内容。
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static IntPtr LoadApplicationIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                // 0 号图标就是 csproj 里 ApplicationIcon 嵌入的那个。
                if (ExtractIconExW(exe, 0, out _, out var small, 1) > 0 && small != IntPtr.Zero)
                {
                    return small;
                }

                if (ExtractIconExW(exe, 0, out var large, out _, 1) > 0 && large != IntPtr.Zero)
                {
                    return large;
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"[tray] 取应用图标失败：{ex.Message}");
        }

        return LoadIconW(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION 兜底
    }

    /// <summary>静态入口：按 hwnd 找到实例再转发（Win32 只认裸函数指针）。</summary>
    private static IntPtr ForwardToInstance(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        => Instances.TryGetValue(hwnd, out var entry) && entry.Instance is { } instance
            ? instance.WndProc(hwnd, msg, wParam, lParam)
            : DefWindowProcW(hwnd, msg, wParam, lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = TipLength)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = InfoLength)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = InfoTitleLength)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private const uint FlashwTray = 0x2;
    private const uint FlashwTimerNoFg = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
