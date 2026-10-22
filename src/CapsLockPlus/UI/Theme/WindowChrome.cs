using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CapsLockPlus.Core;

namespace CapsLockPlus.UI.Theme;

/// <summary>给无边框窗口加上 Win11 的云母 / 圆角 / 深色标题栏效果。</summary>
public static class WindowChrome
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public enum BackdropType
    {
        Auto = 0,
        None = 1,
        Mica = 2,
        Acrylic = 3,
        Tabbed = 4,
    }

    public static void Apply(Window window, bool dark, BackdropType backdrop = BackdropType.Mica)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int darkValue = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));

            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            int type = (int)backdrop;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
        }
        catch (Exception ex)
        {
            CoreLog.Warn("设置窗口外观失败：" + ex.Message);
        }
    }

    public static void EnableRounded(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int round = 2;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }
}
