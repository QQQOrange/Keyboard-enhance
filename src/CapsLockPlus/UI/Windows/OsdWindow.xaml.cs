using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Interop;
using Microsoft.Win32;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 亮度横条。
///
/// 造型**逐项复刻 Windows 自己的音量横条**（功能不同：一个是亮度，一个是音量）：
///
///   | 项目 | 系统音量条（实测） | 本横条 |
///   | --- | --- | --- |
///   | 卡片 | 192×48 DIP、深色、圆角 6、无边框、带柔光阴影 | 同 |
///   | 版式 | 左图标 + 中间进度条 + 右侧数字 | 同（图标换成亮度） |
///   | 进度条 | 高 5 DIP、填充=系统强调色浅档、槽=白色 55% | 同 |
///   | 文字 | 纯白，图标 16、数字 14 | 同 |
///   | 位置 | 水平居中、贴工作区底边 15 DIP | 同 |
///   | 动画 | 淡入约 150ms、停留约 1.9s、淡出约 120ms（合计 ~2.1s） | 同 |
///
/// 这些数字不是拍脑袋：用全屏截图 + 连通域/边缘分析量出来的
/// （这台机器 3840×2160 @200%，对应 384×96 像素的卡片）。
///
/// **不跟随应用主题**：实测系统那条横条在浅色桌面上照样是深色卡片。
/// 之前跟着主题走，结果是"白天蓝、夜里白"，和系统对不上。
///
/// 四条硬性要求（缺一条就会被当成 bug）：
///   1. 不抢焦点（WS_EX_NOACTIVATE）—— 用户可能正在打字；
///   2. 点击穿透（WS_EX_TRANSPARENT）；
///   3. 永远在最上层（显示期间周期性重提 HWND_TOPMOST）；
///   4. 只建一个窗口（连按时复用，避免闪烁）。
/// </summary>
public partial class OsdWindow : Window
{
    /// <summary>卡片尺寸（DIP）。与系统音量条一致。</summary>
    public const double DesignWidth = 192;
    public const double DesignHeight = 48;

    /// <summary>
    /// 卡片四周留给阴影的余量（DIP）。
    ///
    /// 窗口必须比卡片大一圈，否则 DropShadowEffect 会被窗口边界裁掉 ——
    /// 系统那条横条是有柔和投影的，没有它看起来像"贴上去的色块"。
    /// </summary>
    private const double ShadowPad = 12;

    /// <summary>卡片下沿到工作区底边的距离（DIP）。实测系统是 15。</summary>
    private const double BottomGap = 15;

    /// <summary>淡入（毫秒）。实测系统 ~150ms。</summary>
    private const int FadeInMs = 150;

    /// <summary>停留（毫秒）。实测系统条在 2.1s 左右消失，扣掉淡出约 1.9s。</summary>
    private const int HoldMs = 1900;

    /// <summary>淡出（毫秒）。实测系统收得很快。</summary>
    private const int FadeOutMs = 120;

    /// <summary>显示期间多久重新提一次置顶。</summary>
    private const int RaiseIntervalMs = 320;

    /// <summary>强调色的缓存有效期：连按亮度时 Show 每秒会被调几十次。</summary>
    private const int AccentCacheMs = 5000;

    /* ---- 两套配色：跟 **Windows 系统主题** 走（不是本应用的主题） ----
     *
     * 系统那条横条是**半透明材质**（acrylic）：底色带 alpha，会透出一点背后的画面。
     * 直接写死不透明色会显得"死板"，也和系统对不上 —— 用户特意提过这一点。
     *
     *   · 夜间：卡片 ≈ #2D2D2D @94%（实测净色 (44,45,45)），图标/数字纯白，
     *           槽 = 白色 55%，进度条 = 强调色"浅色档"（本机 #4CC2FF）；
     *   · 日间：卡片 ≈ #EBEDF0 @90%（实测 (220,223,226)），图标/数字黑 89%，
     *           槽 = 黑色 45%，进度条 = 强调色"深色档"（本机 #0067C0）。
     *
     * 用 alpha 而不是预先混合好的实色：这样浅色壁纸下不会发灰、
     * 深色壁纸下不会发亮，和系统那条的表现一致。 */
    private static readonly Color LightCard = Color.FromArgb(0xE6, 0xEB, 0xED, 0xF0);
    private static readonly Color LightText = Color.FromArgb(0xE4, 0x00, 0x00, 0x00);
    private static readonly Color LightTrack = Color.FromArgb(0x73, 0x00, 0x00, 0x00);

    private static readonly Color DarkCard = Color.FromArgb(0xF0, 0x2D, 0x2D, 0x2D);
    private static readonly Color DarkText = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color DarkTrack = Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF);

    private static OsdWindow? _instance;

    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _raiseTimer;

    private double _scale;
    private bool _fadingOut;

    private long _paletteTick;
    private bool _lightPalette;

    /// <summary>上次摆放的物理坐标，用来跳过无意义的 SetWindowPos。</summary>
    private (int X, int Y, int W, int H) _placed;

    private OsdWindow()
    {
        InitializeComponent();

        Width = DesignWidth + ShadowPad * 2;
        Height = DesignHeight + ShadowPad * 2;

        _hideTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(HoldMs),
        };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); StartFadeOut(); };

        _raiseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RaiseIntervalMs) };
        _raiseTimer.Tick += (_, _) => RaiseToTop();

        // 扩展样式必须在拿到 HWND 之后设置，否则 WPF 会用自己的值盖回去
        SourceInitialized += (_, _) => ApplyNoActivateStyle();

        Loaded += (_, _) => { ApplyPalette(force: true); RefreshFill(); };

        ApplyPalette(force: true);
    }

    /// <summary>
    /// 预热：只把窗口和 HWND 建出来，不显示。
    ///
    /// 第一次 <c>new Window()</c> 要解析 XAML、建 HWND，冷启动 50~120ms ——
    /// 用户第一次按亮度键会明显觉得"慢半拍"。启动后空闲时先建好。
    /// </summary>
    public static void Warmup()
    {
        var app = Application.Current;
        if (app == null) return;

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Warmup));
            return;
        }

        try
        {
            _instance ??= new OsdWindow();
            _ = new WindowInteropHelper(_instance).EnsureHandle();   // 先把 HWND 建出来
        }
        catch (Exception e)
        {
            CoreLog.Warn("横条预热失败：" + e.Message);
        }
    }

    /// <summary>显示亮度横条。可在任意线程调用。</summary>
    public static void Show(int percent)
    {
        var app = Application.Current;
        if (app == null) return;

        if (!app.Dispatcher.CheckAccess())
        {
            // Input 优先级：和按键同一档 —— 长按连发时每次刷新都要抢这一下
            app.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Show(percent)));
            return;
        }

        _instance ??= new OsdWindow();
        _instance.Apply(percent);
        _instance.Present();
    }

    /// <summary>仅供离屏渲染：造一个不参与单例的实例。</summary>
    internal static OsdWindow CreateDetached() => new();

    /// <summary>仅供离屏渲染：直接把内容摆好，不走显示流程。</summary>
    internal void PrepareForRender(int percent)
    {
        Apply(percent);
        _hideTimer.Stop();
        _raiseTimer.Stop();
        _fadingOut = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    /// <summary>仅供自检：百分比 → 进度条填充比例。</summary>
    public static double ScaleForTest(int percent) => Math.Clamp(percent, 0, 100) / 100.0;

    /// <summary>仅供自检：卡片下沿离工作区底边多少 DIP。</summary>
    internal static double CardBottomGapForTest => BottomGap;

    /// <summary>仅供自检：当前系统是浅色还是深色模式。</summary>
    internal static bool SystemLightForTest => IsSystemLightTheme();

    /// <summary>
    /// 仅供自检：横条（窗口）应该出现在屏幕哪个位置。
    ///
    /// 阴影留白不参与定位 —— 否则改了阴影大小横条会跟着漂。
    /// </summary>
    public static (double Left, double Top) PositionForTest(Rect workArea)
    {
        double winW = DesignWidth + ShadowPad * 2;

        double left = Math.Round(workArea.Left + (workArea.Width - winW) / 2);
        double top = Math.Round(workArea.Bottom - BottomGap - DesignHeight - ShadowPad);
        return (left, top);
    }

    private void Apply(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        _scale = percent / 100.0;
        ValueText.Text = percent.ToString();
        ApplyPalette();
        RefreshFill();
    }

    private void Present()
    {
        bool wasFadingOut = _fadingOut;
        _fadingOut = false;

        bool firstShow = !IsVisible;
        if (firstShow)
        {
            // 先透明再 Show：否则会看到它在旧位置闪一下
            Opacity = 0;
            Show();
        }

        /* 定位必须在 Show 之后：只有拿到 HWND 才知道窗口在哪块屏、
         * 那块屏的 DPI 是多少。顺序反了就会用系统 DPI 去算，
         * 在 200% 缩放的机器上直接算到屏幕外。 */
        Place();
        if (firstShow) RaiseToTop();
        if (firstShow) LogGeometry("首次显示");

        if (firstShow || wasFadingOut)
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(FadeInMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(HoldMs);
        _hideTimer.Start();

        if (!_raiseTimer.IsEnabled) _raiseTimer.Start();
    }

    private void StartFadeOut()
    {
        if (_fadingOut) return;
        _fadingOut = true;

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(FadeOutMs));
        fade.Completed += (_, _) =>
        {
            // 淡出途中又被叫醒的话，_fadingOut 已被 Present 置回 false
            if (!_fadingOut) return;
            _raiseTimer.Stop();
            Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// 按 **Windows 系统主题**（浅色/深色）套用一整套配色。
    ///
    /// 为什么不跟本应用的主题：系统那条横条是跟着 Windows 的明暗走的，
    /// 而本应用可以单独选主题。两者不一致时，用户看到的就会是
    /// "系统浅、我们深"这种对不上。所以这里直接读系统的 AppsUseLightTheme。
    ///
    /// 缓存 5 秒：长按亮度时 Show 每秒被调几十次，没必要每次都读注册表。
    /// </summary>
    private void ApplyPalette(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - _paletteTick < AccentCacheMs) return;

        _paletteTick = now;
        _lightPalette = IsSystemLightTheme();

        Color card = _lightPalette ? LightCard : DarkCard;
        Color text = _lightPalette ? LightText : DarkText;
        Color track = _lightPalette ? LightTrack : DarkTrack;
        Color fill = ReadAccent(_lightPalette);

        Card.Background = new SolidColorBrush(card);
        IconText.Foreground = new SolidColorBrush(text);
        ValueText.Foreground = new SolidColorBrush(text);
        TrackBar.Background = new SolidColorBrush(track);
        FillBar.Background = new SolidColorBrush(fill);
    }

    /// <summary>Windows 当前是不是浅色模式。</summary>
    private static bool IsSystemLightTheme()
    {
        try
        {
            object? v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", null);

            if (v is int i) return i != 0;
        }
        catch (Exception e)
        {
            CoreLog.Warn("读取系统明暗模式失败：" + e.Message);
        }

        return false;
    }

    /// <summary>
    /// 读强调色调色板：白天用"深色档"（索引 4），夜间用"浅色档"（索引 1）。
    ///
    /// HKCU\…\Explorer\Accent 的 AccentPalette 是 8 组 RGBX 共 32 字节：
    ///   0=更浅、1=浅、2/3=标准…（本机 1=#4CC2FF、4=#0067C0）。
    /// 系统横条正是按当前明暗取不同档位，这里照做；读不到就回退默认蓝。
    /// </summary>
    private static Color ReadAccent(bool light)
    {
        try
        {
            object? raw = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",
                "AccentPalette", null);

            if (raw is byte[] b)
            {
                int offset = light ? 16 : 4;      // 索引 4（白天）/ 1（夜间）
                if (b.Length >= offset + 3)
                    return Color.FromRgb(b[offset], b[offset + 1], b[offset + 2]);
            }
        }
        catch (Exception e)
        {
            CoreLog.Warn("读取强调色失败：" + e.Message);
        }

        return light ? Color.FromRgb(0x00, 0x67, 0xC0) : Color.FromRgb(0x4C, 0xC2, 0xFF);
    }

    /// <summary>仅供自检：按明暗取整套配色（十六进制，便于断言）。</summary>
    internal static (string Card, string Text, string Track, string Fill) PaletteForTest(bool light)
    {
        Color card = light ? LightCard : DarkCard;
        Color text = light ? LightText : DarkText;
        Color track = light ? LightTrack : DarkTrack;
        Color fill = ReadAccent(light);

        return ($"#{card.R:X2}{card.G:X2}{card.B:X2}",
                $"#{text.R:X2}{text.G:X2}{text.B:X2}",
                $"#{track.R:X2}{track.G:X2}{track.B:X2}",
                $"#{fill.R:X2}{fill.G:X2}{fill.B:X2}");
    }

    /// <summary>
    /// 水平居中，卡片下沿贴工作区底边 15 DIP（和系统那条一样）。
    ///
    /// **用物理像素直接 SetWindowPos**，不走 WPF 的 Left/Top：
    /// WPF 的 Left/Top 是"窗口所在显示器 DPI 下的 DIP"，而
    /// <c>SystemParameters.WorkArea</c> 是"系统 DPI 下的 DIP"，
    /// 200% 缩放下这两个坐标系差一倍，按 DIP 算会把横条送到屏幕外（踩过）。
    ///
    /// 位置没变就不重复调 SetWindowPos：连按时这个方法每 45ms 跑一次，
    /// 多余的调用会让 DWM 反复重组图层，看起来像"横条在抖"。
    /// </summary>
    private void Place()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            double scale = DpiScale();
            if (!TryMonitorWorkArea(hwnd, out int workLeft, out int workTop,
                                    out int workWidth, out int workHeight))
            {
                var wa = SystemParameters.WorkArea;
                workLeft = (int)Math.Round(wa.Left * scale);
                workTop = (int)Math.Round(wa.Top * scale);
                workWidth = (int)Math.Round(wa.Width * scale);
                workHeight = (int)Math.Round(wa.Height * scale);
            }

            int w = (int)Math.Round((DesignWidth + ShadowPad * 2) * scale);
            int h = (int)Math.Round((DesignHeight + ShadowPad * 2) * scale);
            int x = workLeft + (workWidth - w) / 2;
            int y = workTop + workHeight
                    - (int)Math.Round((BottomGap + DesignHeight + ShadowPad) * scale);

            if (_placed == (x, y, w, h)) return;
            _placed = (x, y, w, h);

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
        catch (Exception e)
        {
            CoreLog.Warn("OSD 定位失败：" + e.Message);
        }
    }

    /// <summary>显示器工作区（物理像素）。</summary>
    private static bool TryMonitorWorkArea(IntPtr hwnd,
        out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        try
        {
            IntPtr monitor = NativeMethods.MonitorFromWindow(
                hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            var mi = new NativeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal
                    .SizeOf<NativeMethods.MONITORINFO>(),
            };
            if (!NativeMethods.GetMonitorInfo(monitor, ref mi)) return false;

            left = mi.rcWork.Left;
            top = mi.rcWork.Top;
            width = mi.rcWork.Width;
            height = mi.rcWork.Height;
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>窗口所在显示器的缩放（1.0 = 96 DPI）。</summary>
    private double DpiScale()
    {
        double scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        return scale > 0 ? scale : 1.0;
    }

    /// <summary>
    /// 提到 topmost 组的最前面。
    ///
    /// 只靠 <c>Topmost=True</c> 不够：其它同样是 topmost 的窗口
    /// （播放器、悬浮工具、任务栏）后来者居上，就会盖住横条。
    /// </summary>
    private void RaiseToTop()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
                | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
        catch (Exception e)
        {
            CoreLog.Warn("OSD 置顶失败：" + e.Message);
        }
    }

    /// <summary>
    /// 加上"不激活 + 点击穿透 + 不进 Alt+Tab"三个扩展样式。
    ///
    /// WPF 的 ShowActivated=False 只影响第一次 Show，
    /// 后续被别的窗口挤压时仍可能被激活；直接改扩展样式才是硬保证。
    /// </summary>
    private void ApplyNoActivateStyle()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_NOACTIVATE
                  | NativeMethods.WS_EX_TRANSPARENT
                  | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
        }
        catch (Exception e)
        {
            CoreLog.Warn("OSD 样式设置失败：" + e.Message);
        }
    }

    /// <summary>把实际尺寸 / 位置 / 扩展样式记一笔，出问题时能对账。</summary>
    private void LogGeometry(string when)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = hwnd == IntPtr.Zero ? 0 : NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            string work = TryMonitorWorkArea(hwnd, out int wl, out int wt, out int ww, out int wh)
                ? $"{wl},{wt} {ww}x{wh}"
                : "(未知)";
            CoreLog.Info($"OSD {when}：DIP={ActualWidth:F0}x{ActualHeight:F0} "
                         + $"pos={Left:F0},{Top:F0} dpi={dpi:F2} 工作区={work} "
                         + $"ex=0x{ex:X8} hwnd=0x{hwnd.ToInt64():X}");
        }
        catch (Exception e)
        {
            CoreLog.Warn("OSD 几何信息记录失败：" + e.Message);
        }
    }

    /// <summary>
    /// 按百分比设置进度条填充。
    ///
    /// 用两列星号比例（s : 1-s）而不是算像素宽度：
    /// 算像素要靠 Track.ActualWidth，窗口刚显示时它还是 0，
    /// 进度条就会停在空状态；比例交给 WPF 的布局去分，什么时候设都对。
    /// </summary>
    private void RefreshFill()
    {
        double s = Math.Max(0, Math.Min(1, _scale));
        FillColumn.Width = new GridLength(s, GridUnitType.Star);
        RestColumn.Width = new GridLength(1 - s, GridUnitType.Star);
    }
}
