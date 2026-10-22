using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Interop;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 亮度横条（屏幕中下方、任务栏偏上）。
///
/// 为什么只有亮度还留着自绘：音量交给系统音量键，Windows 自己会弹它那条
/// 横条（自绘只会多出一条重复的）；而亮度没有通用 OSD —— 系统那条只对
/// 内置屏存在，是 OEM 驱动画的，外接屏走 DDC/CI 时系统根本不知道值变了。
///
/// 四条硬性要求（缺一条就会被用户当成 bug）：
///   1. **不抢焦点** —— 用户可能正在打字，横条跳出来抢焦点会把输入打断
///      （<c>ShowActivated=False</c> + <c>WS_EX_NOACTIVATE</c>）；
///   2. **点击穿透** —— 横条挡在鼠标位置上时不能吃掉点击
///      （<c>WS_EX_TRANSPARENT</c> + <c>IsHitTestVisible=False</c>）；
///   3. **永远在最上层** —— 显示期间反复 <c>SetWindowPos(HWND_TOPMOST)</c>，
///      因为别的 topmost 窗口（播放器、悬浮球、任务栏）随时可能压上来；
///   4. **只建一个窗口** —— 连按亮度键时复用同一个，反复 new 会闪烁。
///
/// v4.3.0 的三处改动：
///   · **尺寸缩小**（248×52，原 368×98），排版对齐系统音量条：图标 + 条 + 数字；
///   · **跟随主题**（白夜 / 深浅模式）：配色全部走主题画刷，深色主题深底浅字，
///     浅色主题浅底深字，不再写死深色；
///   · **降延迟**：提前建好 HWND（<see cref="Warmup"/>）、用 Input 优先级
///     回到 UI 线程、位置没变就不重复 SetWindowPos —— 长按亮度时每次刷新
///     都要走这条路，省下的每一毫秒都直接体现在"跟手"上。
/// </summary>
public partial class OsdWindow : Window
{
    /// <summary>
    /// 横条尺寸（DIP）。代码里定死，不依赖 XAML 上的值 ——
    /// 尺寸会同时参与物理像素定位计算，两处写死迟早对不上。
    ///
    /// 208×44（约 4.7:1）参照 Windows 自带音量条的体量：那条是横长条，
    /// 高度只有字高的两倍出头。上一版 248×52 在 200% 缩放下显得又宽又厚，
    /// 挡住的内容比它本身还多。
    /// </summary>
    public const double DesignWidth = 208;
    public const double DesignHeight = 44;

    /// <summary>
    /// 距工作区底边的距离（DIP）。
    ///
    /// Windows 11 的音量 / 亮度横条就贴在任务栏上方一点点，
    /// 之前留 96 太靠上了，看起来像悬在屏幕中间。
    /// 28 是"压在任务栏上沿、又不贴着边"的位置。
    /// </summary>
    private const double BottomGap = 28;

    /// <summary>淡入时间。太慢会觉得"迟钝"，太快会闪。</summary>
    private const int FadeInMs = 80;

    private const int HoldMs = 1100;
    private const int FadeOutMs = 200;

    /// <summary>显示期间多久重新提一次置顶。太频繁没必要，太慢会被压住一会儿。</summary>
    private const int RaiseIntervalMs = 320;

    private static OsdWindow? _instance;

    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _raiseTimer;

    private double _scale;
    private bool _fadingOut;

    /// <summary>上次摆放的物理坐标，用来跳过无意义的 SetWindowPos。</summary>
    private (int X, int Y, int W, int H) _placed;

    private OsdWindow()
    {
        InitializeComponent();

        Width = DesignWidth;
        Height = DesignHeight;

        _hideTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(HoldMs),
        };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); StartFadeOut(); };

        _raiseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RaiseIntervalMs) };
        _raiseTimer.Tick += (_, _) => RaiseToTop();

        // 扩展样式必须在拿到 HWND 之后设置，否则 WPF 会用自己的值盖回去
        SourceInitialized += (_, _) => ApplyNoActivateStyle();

        Loaded += (_, _) => RefreshFill();
    }

    /// <summary>
    /// 预热：只把窗口和 HWND 建出来，不显示。
    ///
    /// 第一次 <c>new Window()</c> 要解析 XAML、建 HWND，冷启动 50~120ms ——
    /// 用户第一次按亮度键会明显觉得"慢半拍"。启动后空闲时先建好，
    /// 之后第一次显示就没有这笔开销。
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
            // Input 优先级：和按键同一档，比 Normal 更早被执行 —— 长按连发时
            // 每次刷新都要抢这一下，用 Background/Normal 会排在后台任务后面。
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

    /// <summary>仅供自检：横条应该出现在屏幕哪个位置。</summary>
    public static (double Left, double Top) PositionForTest(Rect workArea, double width, double height)
    {
        return (Math.Round(workArea.Left + (workArea.Width - width) / 2),
                Math.Round(workArea.Bottom - height - BottomGap));
    }

    private void Apply(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        _scale = percent / 100.0;
        ValueText.Text = percent.ToString();
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
            BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(FadeInMs)));

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
    /// 水平居中，垂直落在任务栏上方。
    ///
    /// **用物理像素直接 SetWindowPos**，不走 WPF 的 Left/Top。
    ///
    /// 原因：WPF 的 Left/Top 是"窗口所在显示器 DPI 下的 DIP"，
    /// 而 <c>SystemParameters.WorkArea</c> 给的是"系统 DPI 下的 DIP"。
    /// 这台机器 200% 缩放，两个坐标系差一倍 —— 实测按 DIP 算出来的位置
    /// 是 864，乘 2 之后落到 1728，屏幕只有 1080 高，横条直接不见了。
    /// 物理像素只有一个坐标系，乘一下缩放就对齐了，不会错。
    ///
    /// 位置没变就**不再调** SetWindowPos：连按亮度时这个方法每 45ms 跑一次，
    /// 多余的调用会让 DWM 反复重组图层，看起来就是"横条在抖"。
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
                // 拿不到显示器信息时退回 WPF 的值，再换算成物理像素
                var wa = SystemParameters.WorkArea;
                workLeft = (int)Math.Round(wa.Left * scale);
                workTop = (int)Math.Round(wa.Top * scale);
                workWidth = (int)Math.Round(wa.Width * scale);
                workHeight = (int)Math.Round(wa.Height * scale);
            }

            int w = (int)Math.Round(Width * scale);
            int h = (int)Math.Round(Height * scale);
            int x = workLeft + (workWidth - w) / 2;
            int y = workTop + workHeight - h - (int)Math.Round(BottomGap * scale);

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
    /// 只靠 <c>Topmost=True</c> 不够：那只是给窗口加了 WS_EX_TOPMOST，
    /// 其它同样是 topmost 的窗口（播放器、悬浮工具、任务栏）后来者居上，
    /// 就会盖住横条。所以显示期间要周期性重新提一次。
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
