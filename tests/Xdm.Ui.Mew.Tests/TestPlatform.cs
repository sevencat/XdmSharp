using Aprillz.MewUI;

namespace Xdm.Ui.Mew.Tests;

/// <summary>
/// MewUI 0.22 没有 Headless 宿主，布局仍要一个渲染后端；这里按需注册 Win32 + Direct2D。
/// 注册失败的机器（无 GPU / 服务账户）只是跳过布局类测试，不让整个工程红掉。
/// </summary>
internal static class TestPlatform
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static bool EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return true;
            }

            try
            {
                Win32Platform.Register();
                Direct2DBackend.Register();
                _registered = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[test] render backend unavailable: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            return true;
        }
    }
}
