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

    /* Win11 起可以单独指定标题栏 / 边框 / 标题文字的颜色。
     * 不指定的话，深色模式是**纯黑**、浅色模式是**纯白** —— 跟应用里
     * #1A1E27 / #F5F8FC 这样的皮肤颜色放在一起就是"一条突兀的横杠"。 */
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

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

            /* 标题栏颜色跟皮肤走：用「比正文稍亮/稍暗一档」的 SurfaceAlt，
             * 既不纯黑也不纯白，和对话框正文是同一条色阶。 */
            if (ThemeColor("Brush.SurfaceAlt") is int caption)
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            if (ThemeColor("Brush.Border") is int border)
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            if (ThemeColor("Brush.TextPrimary") is int text)
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }
        catch (Exception ex)
        {
            CoreLog.Warn("设置窗口外观失败：" + ex.Message);
        }
    }

    /// <summary>当前是不是深色主题（跟系统走时按系统实际明暗算）。</summary>
    public static bool IsDarkTheme()
        => ThemeManager.Current == ThemeManager.Mode.Dark
           || (ThemeManager.Current == ThemeManager.Mode.System && ThemeManager.IsDarkSystem);

    /// <summary>
    /// 让一个普通窗口（对话框）的标题栏跟随主题。
    ///
    /// 只做两件事：HWND 建好后调一次 <see cref="Apply"/>；主题切换时再调一次。
    /// 用 AttachTo 而不是让每个对话框自己写 —— 以前只有 AppDialog 做了，
    /// 其它弹窗在深色界面里顶着一根白条，非常突兀。
    /// </summary>
    public static void AttachTo(Window window)
    {
        try
        {
            window.SourceInitialized += (_, _)
                => Apply(window, IsDarkTheme(), BackdropType.None);

            void OnThemeChanged(bool dark) => Apply(window, dark, BackdropType.None);
            ThemeManager.ThemeChanged += OnThemeChanged;
            window.Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "对话框标题栏跟随主题");
        }
    }

    /// <summary>从主题里取一个颜色，转成 DWM 要的 COLORREF（0x00BBGGRR）。</summary>
    private static int? ThemeColor(string brushKey)
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource(brushKey)
                is System.Windows.Media.SolidColorBrush brush)
            {
                var c = brush.Color;
                return c.R | (c.G << 8) | (c.B << 16);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "读取主题颜色");
        }

        return null;
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
