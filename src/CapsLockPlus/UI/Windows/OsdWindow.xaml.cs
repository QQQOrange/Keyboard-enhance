using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Interop;
using CapsLockPlus.Core.Windows;
using Microsoft.Win32;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 亮度横条。
///
/// 造型**逐项复刻 Windows 自己的音量横条**（功能不同：一个是亮度，一个是音量）：
///
///   | 项目 | 系统音量条（实测） | 本横条 |
///   | --- | --- | --- |
///   | 卡片 | 192×48 DIP、深色、圆角 7.5、无边框、带柔光阴影 | 同 |
///   | 版式 | 左图标 + 中间进度条 + 右侧数字 | 同（图标换成亮度） |
///   | 进度条 | 高 5 DIP、填充=系统强调色浅档、槽=白色 55% | 同 |
///   | 文字 | 纯白，图标 16、数字 14 | 同 |
///   | 位置 | 水平居中、贴工作区底边 14 DIP | 同 |
///   | 动画 | 淡入约 150ms、停留约 1.9s、淡出约 120ms（合计 ~2.1s） | 同 |
///
/// 卡片尺寸、贴底距离、缩放换算**全部集中在 <see cref="OsdPlacement"/>**：
/// 那里用 DIP 定义、用目标显示器的缩放换算成物理像素、用工作区的相对关系
/// 定位，所以换台电脑（4K/1080p、100%/150%/200%）横条的观感都一样。
/// 本类只负责"把它画出来 + 摆到那个矩形上"。
///
/// 这些数字不是拍脑袋：用全屏截图 + 连通域/边缘分析量出来的
/// （这台机器 3840×2160 @200%，对应 384×94 像素的卡片）。
///
/// ⚠ 2026-10-23 复测（铺一块纯黑全屏窗体 → 按音量键 → 截图量卡片边缘）：
///     卡片 384×94 px、水平居中，下沿距**屏幕**底边 28 px（= 14 DIP）。
///   本机任务栏是"自动隐藏"，此时工作区等于整屏；系统横条**不会**额外让开
///   任务栏。旧实现却拿 ABM_GETTASKBARPOS 把基准抬到任务栏上沿，
///   结果整条比系统高 96 px（整整一个横条的高度）—— 用户一眼就看出来了。
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
    /// <summary>
    /// 卡片尺寸（DIP）。与系统音量条一致 —— 定义在 <see cref="OsdPlacement"/>，
    /// 那里是唯一的一份真相（尺寸、阴影余量、贴底距离、缩放换算都在那儿）。
    /// </summary>
    public const double DesignWidth = OsdPlacement.CardWidthDip;
    public const double DesignHeight = OsdPlacement.CardHeightDip;

    /// <summary>
    /// 卡片圆角（DIP）。实测对齐系统音量条 = 7.5。
    ///
    /// 怎么量出来的（<c>tools/measure_osd_radius.py</c>）：
    /// 铺纯黑全屏 → 触发横条 → 截图 → 沿左下角逐行求左边界
    /// → 用圆角矩形方程 d(t)=R-sqrt(R²-(R-t)²) 做最小二乘拟合。
    ///
    /// 三个关键点，少一个就会量错：
    ///   1. **从底边量，不从顶边量**：两条横条都贴工作区底 14 DIP，底边必然对齐；
    ///      顶边会差 1~2 px（WPF 与 DWM 抗锯齿起点不同），从顶边量等于把这
    ///      点偏差塞进半径里。
    ///   2. **只认比值/同算法对比，不认绝对拟合值**：拟合值比真值小约 2.4 px
    ///      （50% 阈值 + 抗锯齿让边界内收）。旧值 6 DIP 真值 12 px、拟合 9.75，
    ///      偏差 2.25；8 DIP 真值 16 px、拟合 13.5，偏差 2.5 —— 偏差基本恒定。
    ///   3. **拿系统的拟合值反推**：系统拟合 12.5 px + 偏差 ≈ 15 px = 7.5 DIP。
    ///
    /// 按 7.5 DIP 复测，我们左下剖面 10.8/8.0/6.3/5.0/3.7…，
    /// 系统 10.9/8.1/6.3/4.9/3.7…，逐行差 ≤ 0.1 px，拟合值同为 12.50。
    ///
    /// 以前写 6 是"看起来差不多"估的：200% 下只差 4 个像素，单看没感觉，
    /// 但和系统横条并排时"我们更方"很明显（用户就是这么发现的）。
    /// </summary>
    public const double CardRadius = 7.5;

    /// <summary>
    /// 卡片四周留给阴影的余量（DIP）。
    ///
    /// 窗口必须比卡片大一圈，否则 DropShadowEffect 会被窗口边界裁掉 ——
    /// 系统那条横条是有柔和投影的，没有它看起来像"贴上去的色块"。
    /// </summary>
    private const double ShadowPad = OsdPlacement.ShadowPadDip;

    /// <summary>
    /// 卡片下沿到工作区底边的距离（DIP）。
    ///
    /// 实测系统音量条：屏幕底边往上 28 物理像素，200% 缩放下正好 14 DIP。
    /// （旧值 15 是"看起来差不多"估的，差 1 DIP 会在放大镜级别露馅。）
    /// </summary>
    private const double BottomGap = OsdPlacement.BottomGapDip;

    /// <summary>淡入（毫秒）。实测系统 ~150ms。</summary>
    private const int FadeInMs = 150;

    /// <summary>停留（毫秒）。实测系统条在 2.1s 左右消失，扣掉淡出约 1.9s。</summary>
    private const int HoldMs = 1900;

    /// <summary>淡出（毫秒）。实测系统收得很快。</summary>
    private const int FadeOutMs = 120;

    /// <summary>
    /// 显示期间多久重新提一次置顶。
    ///
    /// 200ms 而不是更长的原因：任务栏从下方滑出时**会**插到我们前面
    /// （它是 topmost，而且比我们的横条更晚"动"）。这个周期就是"被挡住"
    /// 的最长可见时间 —— 320ms 能看出来卡了一下，200ms 基本无感。
    /// 横条本身只活 2.1 秒，多提几次的代价可以忽略。
    /// </summary>
    private const int RaiseIntervalMs = 200;

    /// <summary>
    /// 仅供测试：覆盖"停留多久"（毫秒），0 = 用默认值。
    /// 见 <c>--osd-hold</c>，验证置顶行为时要把横条多留一会儿。
    /// </summary>
    internal static int HoldOverrideMs;

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
    /* 实测方法：给桌面铺一块纯黑 / 纯白窗体，各截一帧系统横条，反解
     *     c_black = a*base
     *     c_white = a*base + (1-a)*255
     *
     * 结果（两套都是"半透明磨砂板"，只是明暗相反）：
     *   · 日间：base #F9F9F9，不透明度 85.5%（黑底实测 (212,212,212)、白底 (249,249,249)）
     *   · 夜间：base #2D2D2D，不透明度 96.1%（黑底 (43,43,43)、白底 (53,53,53)）
     *
     * 文字：日间黑（合成后约 #1A1A1A）、夜间纯白；
     * 槽：日间黑 45%（实测 (121,122,124)）、夜间白 55%（实测 (159,160,160)）；
     * 进度条：日间强调色深档 #0067C0、夜间浅档 #4CC2FF（实测 (76,194,255)）。 */
    private static readonly Color LightCard = Color.FromArgb(0xDA, 0xF9, 0xF9, 0xF9);
    private static readonly Color LightText = Color.FromArgb(0xE6, 0x00, 0x00, 0x00);
    private static readonly Color LightTrack = Color.FromArgb(0x73, 0x00, 0x00, 0x00);

    private static readonly Color DarkCard = Color.FromArgb(0xF5, 0x2D, 0x2D, 0x2D);
    private static readonly Color DarkText = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color DarkTrack = Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF);

    private static OsdWindow? _instance;

    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _raiseTimer;

    private int _percent;
    private bool _fadingOut;

    private long _paletteTick;
    private bool _lightPalette;

    /* 最近一次套用的四个颜色。高带路径的离屏卡片要跟窗口里的那份同步，
     * 但它可能比 ApplyPalette 晚创建，所以颜色得留一份在这里。 */
    private Color _cardColor = DarkCard;
    private Color _textColor = DarkText;
    private Color _trackColor = DarkTrack;
    private Color _fillColor = Color.FromRgb(0x4C, 0xC2, 0xFF);

    /// <summary>上次摆放的物理坐标 + 缩放，用来跳过无意义的 SetWindowPos。</summary>
    private (int X, int Y, int W, int H, double Scale) _placed;

    /* ---- 高带（band 16）路径 ----
     *
     * 非 null 表示横条由 <see cref="OsdBandSurface"/> 显示，
     * 本窗口**从不 Show**，只负责算位置、渲染位图、掌管动画状态。
     * null 表示回退到"本窗口直接显示"的老路径（band 16 不可用时）。 */
    private OsdBandSurface? _band;
    private bool _surfaceTried;
    private bool _bandVisible;
    private byte _bandAlpha = 255;
    private DispatcherTimer? _bandFadeTimer;

    /// <summary>新路径的渲染源：不挂在任何窗口上的卡片实例。</summary>
    private OsdCard? _offscreen;
    private RenderTargetBitmap? _cachedBitmap;
    private (int Percent, bool Light, double Scale) _cacheKey = (-1, false, 0);

    private OsdWindow()
    {
        InitializeComponent();

        Width = DesignWidth + ShadowPad * 2;
        Height = DesignHeight + ShadowPad * 2;

        /* 圆角只在代码里定一次。XAML 里给设计器看的那个值不算数，
         * 这里覆盖成常量 —— 免得以后改了一个忘了另一个。 */
        CardView.Radius = CardRadius;

        _hideTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(HoldMs),
        };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); StartFadeOut(); };

        _raiseTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(RaiseIntervalMs),
        };
        _raiseTimer.Tick += (_, _) => RaiseToTop();

        // 扩展样式必须在拿到 HWND 之后设置，否则 WPF 会用自己的值盖回去
        SourceInitialized += (_, _) => ApplyNoActivateStyle();

        /* 跨屏时 WPF 会收到 WM_DPICHANGED 并按新缩放重排内容。
         * 窗口尺寸也必须跟着重算一次 —— 横条可能从 200% 的屏挪到 150% 的屏，
         * 位置和像素尺寸都要按新倍率来。这里清掉缓存强制重摆。 */
        DpiChanged += (_, e) =>
        {
            _placed = default;
            Place();
        };

        Loaded += (_, _) => ApplyPalette(force: true);

        ApplyPalette(force: true);
        EnsureSurface();
    }

    /// <summary>
    /// 选定横条的宿主：优先 **band 16 的分层窗口**（能压过开始菜单），
    /// 不可用时退回本窗口 + 周期重提升（能压过任务栏，但压不过开始菜单）。
    ///
    /// 只尝试一次：band 是系统能力，运行期间不会变；每次显示都试一遍
    /// 只会让按键路径多背一次 P/Invoke。
    /// </summary>
    private void EnsureSurface()
    {
        if (_surfaceTried) return;
        _surfaceTried = true;

        /* 逃生口：某些系统上 band 16 的窗口会挡住游戏的独占全屏，
         * 或者用户就是不想要"浮在开始菜单之上"。设 CAPSLOCKPLUS_OSD_BAND=0
         * 即可退回原来的 WPF 窗口（横条仍压得住任务栏，但压不过开始菜单）。
         * 自检也用它验证回退路径没被改坏。 */
        string? forced = Environment.GetEnvironmentVariable("CAPSLOCKPLUS_OSD_BAND");
        if (forced is "0" or "off" or "false")
        {
            CoreLog.Info("OSD 宿主：按 CAPSLOCKPLUS_OSD_BAND 要求使用普通 WPF 窗口");
            return;
        }

        _band = OsdBandSurface.TryCreate(NativeMethods.ZBID_ABOVE_SHELL, out string error);
        if (_band != null)
        {
            CoreLog.Info($"OSD 宿主：band {NativeMethods.ZBID_ABOVE_SHELL} 分层窗口"
                         + $"（高于开始菜单的 band 6），hwnd=0x{_band.Handle.ToInt64():X}");
            return;
        }

        CoreLog.Info("OSD 宿主：回退到普通 WPF 窗口 —— " + error
                     + "；此路径能压过任务栏，但压不过开始菜单");
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

            if (_instance._band == null)
            {
                _ = new WindowInteropHelper(_instance).EnsureHandle();   // 先把 HWND 建出来
            }
            else
            {
                /* 高带路径没有 WPF 窗口要建，真正的冷启动开销在
                 * "第一次离屏渲染"（字体、DropShadowEffect、DIB 创建）。
                 * 先按 100% 渲染一张丢掉，把这条链路焐热。 */
                _ = _instance.Render(50, 1.0);
            }
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

    /// <summary>
    /// 仅供自检：用**和真实按键完全一样**的路径（Present）显示出来，但不自动消失。
    ///
    /// 为什么不能摆到屏幕外去测：窗口挪到 (-4000,-4000) 之后，Z 序的表现和
    /// 真实位置**不一样**（实测：屏幕外时只发 HWND_TOPMOST 也能"看起来"抢回
    /// 最前，一放到屏幕底部就复现不出问题）。这种"在测试环境里自己好了"的
    /// 用例比没有还糟，所以宁可让横条在自检时真亮一下。
    /// </summary>
    internal void ShowForTest(int percent)
    {
        int old = HoldOverrideMs;
        HoldOverrideMs = 20_000;      // 别在断言跑完之前自己淡出
        try
        {
            Apply(percent);
            Present();
        }
        finally
        {
            HoldOverrideMs = old;
        }
    }

    /// <summary>仅供自检：窗口句柄（高带路径给 band 宿主，老路径给 WPF 窗口）。</summary>
    internal IntPtr HandleForTest =>
        _band?.Handle ?? new WindowInteropHelper(this).Handle;

    /// <summary>仅供自检：当前走的是哪条路径（true = band 16 分层窗口）。</summary>
    internal bool UsesBandSurfaceForTest => _band != null;

    /// <summary>仅供自检：band 宿主实际所在的带（老路径返回 0）。</summary>
    internal uint BandForTest => _band?.BandForTest ?? 0;

    /// <summary>仅供自检：按指定缩放渲染一张卡片位图（走的是真实渲染路径）。</summary>
    internal RenderTargetBitmap RenderForTest(int percent, double scale) => Render(percent, scale);

    /// <summary>
    /// 仅供自检：开/关"周期性置顶"。
    ///
    /// 验证置顶需要一个"横条已经被压下去"的初始状态 —— 这个状态只能趁
    /// 重提升没跑的时候制造出来，所以先关掉定时器，压完再打开。
    /// </summary>
    internal bool RaiseTimerForTest
    {
        get => _raiseTimer.IsEnabled;
        set { if (value) _raiseTimer.Start(); else _raiseTimer.Stop(); }
    }

    /// <summary>仅供自检：收尾。</summary>
    internal void CloseForTest()
    {
        _raiseTimer.Stop();
        _hideTimer.Stop();
        _bandFadeTimer?.Stop();
        try { Close(); } catch { /* 自检里关不掉不算失败 */ }
    }

    /// <summary>仅供自检：百分比 → 进度条填充比例。</summary>
    public static double ScaleForTest(int percent) => Math.Clamp(percent, 0, 100) / 100.0;

    /// <summary>仅供自检：卡片下沿离工作区底边多少 DIP。</summary>
    internal static double CardBottomGapForTest => BottomGap;

    /// <summary>仅供自检：卡片**实际生效**的圆角（XAML 可能被单独改过）。</summary>
    internal double CardRadiusForTest => CardView.Radius;

    /// <summary>仅供自检：当前系统是浅色还是深色模式。</summary>
    internal static bool SystemLightForTest => IsSystemLightTheme();

    /// <summary>
    /// 仅供自检：横条（窗口）应该出现在屏幕哪个位置。
    ///
    /// 阴影留白不参与定位 —— 否则改了阴影大小横条会跟着漂。
    ///
    /// 参数按 **96 DPI 基准**给（1 单位 = 1 像素 = 1 DIP），
    /// 所以返回的就是"100% 缩放下会落在哪个像素"。
    /// 真实运行时由 <see cref="OsdPlacement.Compute"/> 乘上目标显示器的缩放。
    ///
    /// 基准就是**工作区底边**，不再考虑任务栏矩形：
    /// 任务栏固定时 rcWork 已经扣掉它；自动隐藏时 rcWork 等于整屏，
    /// 而实测系统横条此时照样贴着屏幕底（28 px），并不会让开任务栏。
    /// </summary>
    public static (double Left, double Top) PositionForTest(Rect workArea)
    {
        var r = OsdPlacement.Compute(
            (int)workArea.Left, (int)workArea.Top,
            (int)workArea.Right, (int)workArea.Bottom, 1.0);
        return (r.X, r.Y);
    }

    private void Apply(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        _percent = percent;
        CardView.SetPercent(percent);
        ApplyPalette();
    }

    private void Present()
    {
        bool wasFadingOut = _fadingOut;
        _fadingOut = false;

        if (_band != null)
        {
            PresentBand(wasFadingOut);
            return;
        }

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

        if (firstShow)
        {
            // 动画启动后的实际状态（排查"窗口在但看不见"时全靠这两行）
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                CoreLog.Info($"OSD 淡入后：opacity={Opacity:F2} visible={IsVisible} "
                             + $"实际={Width:F0}x{Height:F0} 卡片={CardView.ActualWidth:F0}x{CardView.ActualHeight:F0}")));
        }

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(
            HoldOverrideMs > 0 ? HoldOverrideMs : HoldMs);
        _hideTimer.Start();

        if (!_raiseTimer.IsEnabled) _raiseTimer.Start();
    }

    /// <summary>
    /// 高带路径的显示流程：算位置 → 渲成位图 → 贴到 band 16 的分层窗口。
    ///
    /// 和 WPF 路径的三点不同：
    ///   1. **不 Show 本窗口**（窗口只是渲染器和状态机）；
    ///   2. 不需要周期性重提升 —— band 16 天然高于 band 1 的任务栏和 band 6 的开始菜单；
    ///   3. 淡入淡出改的是位图的 SourceConstantAlpha，不重画内容。
    /// </summary>
    private void PresentBand(bool wasFadingOut)
    {
        if (_band == null) return;

        bool firstShow = !_bandVisible;
        _bandVisible = true;

        if (!TryComputeRect(out var rect, out double scale))
        {
            CoreLog.Warn("OSD 高带路径：没有可用的工作区，跳过本次显示");
            return;
        }

        var bitmap = Render(_percent, scale);
        byte alpha = firstShow || wasFadingOut ? (byte)0 : _bandAlpha;

        _band.Present(bitmap, rect.X, rect.Y, alpha);
        if (firstShow) LogGeometry("首次显示（band 16）");

        if (firstShow || wasFadingOut || _bandAlpha < 255)
            FadeBand(alpha, 255, FadeInMs, onDone: null);

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(
            HoldOverrideMs > 0 ? HoldOverrideMs : HoldMs);
        _hideTimer.Start();
    }

    /// <summary>
    /// band 路径的淡入淡出：每 ~16ms 改一次 SourceConstantAlpha。
    ///
    /// 为什么不用 WPF 的 Opacity 动画：窗口根本没显示，动画作用在窗口上没人看。
    /// 为什么不用 CompositionTarget.Rendering：横条只活 2 秒，定时器足够，
    /// 也不用担心帧率不稳定时抢渲染线程。
    /// </summary>
    private void FadeBand(byte from, byte to, int durationMs, Action? onDone)
    {
        _bandFadeTimer?.Stop();

        if (_band == null)
        {
            onDone?.Invoke();
            return;
        }

        if (durationMs <= 0 || from == to)
        {
            _bandAlpha = to;
            _band.SetAlpha(to);
            onDone?.Invoke();
            return;
        }

        _bandAlpha = from;
        _band.SetAlpha(from);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };

        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / durationMs);

            // 和 WPF 那条一样用 ease-out，看起来才是"弹出来"而不是匀速
            double eased = 1 - (1 - t) * (1 - t);
            byte a = (byte)Math.Round(from + (to - from) * eased);

            _bandAlpha = a;
            _band?.SetAlpha(a);

            if (t >= 1)
            {
                timer.Stop();
                if (ReferenceEquals(_bandFadeTimer, timer)) _bandFadeTimer = null;
                onDone?.Invoke();
            }
        };

        _bandFadeTimer = timer;
        timer.Start();
    }

    private void StartFadeOut()
    {
        if (_fadingOut) return;
        _fadingOut = true;

        if (_band != null)
        {
            FadeBand(_bandAlpha, 0, FadeOutMs, onDone: () =>
            {
                // 淡出途中又被叫醒的话，_fadingOut 已被 Present 置回 false
                if (!_fadingOut) return;
                _band?.Hide();
                _bandVisible = false;
            });
            return;
        }

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
        bool light = IsSystemLightTheme();

        Color card = light ? LightCard : DarkCard;
        Color text = light ? LightText : DarkText;
        Color track = light ? LightTrack : DarkTrack;
        Color fill = ReadAccent(light);

        if (light != _lightPalette)
        {
            /* 明暗切换了：高带路径的位图缓存必须作废，
             * 否则下一次按键还会用旧配色渲染。 */
            _lightPalette = light;
            _cacheKey = (-1, false, 0);
        }

        _cardColor = card;
        _textColor = text;
        _trackColor = track;
        _fillColor = fill;

        CardView.SetPalette(card, text, track, fill);
        _offscreen?.SetPalette(card, text, track, fill);
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
    /// 水平居中、卡片下沿贴工作区底边 14 DIP（和系统那条实测一致）。
    ///
    /// **全程用物理像素 SetWindowPos**，不走 WPF 的 Left/Top：
    /// WPF 的 Left/Top 是"窗口所在显示器 DPI 下的 DIP"，
    /// <c>SystemParameters.WorkArea</c> 是"系统 DPI 下的 DIP"，
    /// 200% 缩放下这两个坐标系差一倍，按 DIP 算会把横条送到屏幕外（踩过）。
    ///
    /// 换台机器（不同分辨率 / 不同缩放）也不会错位，因为：
    ///   · 尺寸和间距都是 **DIP**，物理像素 = DIP × 目标显示器的缩放；
    ///   · 位置是**工作区的相对关系**（居中 + 贴底），不是写死的坐标。
    /// 具体换算见 <see cref="OsdPlacement.Compute"/>，那里是纯函数，
    /// 自检用一堆"分辨率 × 缩放"的组合跑过断言。
    ///
    /// 位置没变就不重复调 SetWindowPos：连按时这个方法每 45ms 跑一次，
    /// 多余的调用会让 DWM 反复重组图层，看起来像"横条在抖"。
    /// </summary>
    private void Place()
    {
        // 高带路径自己算位置（PresentBand → TryComputeRect），这里只管 WPF 窗口
        if (_band != null) return;

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (!TryComputeRect(out var r, out double scale)) return;

            var key = (r.X, r.Y, r.Width, r.Height, Math.Round(scale, 3));
            if (_placed == key) return;
            _placed = key;

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            /* 搬完再顶一次。带坐标的这次 SetWindowPos 虽然给了 HWND_TOPMOST，
             * 但那只能保证"在所有非 topmost 之上"，越不过同为 topmost 的窗口
             * （任务栏、播放器悬浮条）—— 详见 RaiseToTop 的注释。 */
            RaiseToTop();
        }
        catch (Exception e)
        {
            CoreLog.Warn("OSD 定位失败：" + e.Message);
        }
    }

    /// <summary>
    /// 算出横条该占的物理像素矩形，以及**实际生效**的缩放。
    ///
    /// 两条路径共用（WPF 窗口用 SetWindowPos，高带路径交给 UpdateLayeredWindow），
    /// 所以位置逻辑只有这一份。缩放从结果反推而不是直接用 area.DpiScale：
    /// <see cref="OsdPlacement.Compute"/> 里有一条"极小屏等比缩小"的兜底，
    /// 真触发时倍率已经不是原值了，位图必须按真实倍率渲染才不会糊。
    /// </summary>
    private bool TryComputeRect(out OsdRect rect, out double scale)
    {
        rect = default;
        scale = 1.0;

        IntPtr fallback = _band?.Handle ?? IntPtr.Zero;
        if (fallback == IntPtr.Zero) fallback = new WindowInteropHelper(this).Handle;
        if (fallback == IntPtr.Zero) return false;

        if (!OsdPlacement.TryGetTargetWorkArea(fallback, out var area) || !area.IsValid)
            return false;

        rect = OsdPlacement.Compute(area.Left, area.Top, area.Right, area.Bottom, area.DpiScale);
        if (rect.Width <= 0 || rect.Height <= 0) return false;

        scale = rect.Height / (DesignHeight + ShadowPad * 2);
        return scale > 0.05;
    }

    /// <summary>
    /// 把卡片渲染成一张位图（Pbgra32，预乘 alpha —— UpdateLayeredWindow 要的就是它）。
    ///
    /// 缓存键是"百分比 + 明暗 + 缩放"：连按亮度键时同一档位只会渲染一次，
    /// 中间那些 1% 的变化才需要重画（一张 432×144 大约 1~2ms）。
    /// </summary>
    private RenderTargetBitmap Render(int percent, double scale)
    {
        double wDip = DesignWidth + ShadowPad * 2;
        double hDip = DesignHeight + ShadowPad * 2;

        int pw = Math.Max(1, (int)Math.Round(wDip * scale));
        int ph = Math.Max(1, (int)Math.Round(hDip * scale));
        double rounded = Math.Round(scale, 3);

        if (_cachedBitmap != null
            && _cacheKey == (percent, _lightPalette, rounded)
            && _cachedBitmap.PixelWidth == pw
            && _cachedBitmap.PixelHeight == ph)
        {
            return _cachedBitmap;
        }

        if (_offscreen == null)
        {
            _offscreen = new OsdCard { Radius = CardRadius };
            _offscreen.SetPalette(_cardColor, _textColor, _trackColor, _fillColor);
        }

        _offscreen.Width = wDip;
        _offscreen.Height = hDip;
        _offscreen.SetPercent(percent);
        _offscreen.Measure(new Size(wDip, hDip));
        _offscreen.Arrange(new Rect(0, 0, wDip, hDip));
        _offscreen.UpdateLayout();

        /* dpi 必须等于 96×缩放：RenderTargetBitmap 用 dpi 把"像素"和"DIP"对上，
         * 写死 96 会让 200% 的屏上内容只有一半大（也可能糊）。 */
        double dpi = 96.0 * scale;
        var bmp = new RenderTargetBitmap(pw, ph, dpi, dpi, PixelFormats.Pbgra32);
        bmp.Render(_offscreen);
        bmp.Freeze();

        _cachedBitmap = bmp;
        _cacheKey = (percent, _lightPalette, rounded);
        return bmp;
    }

    protected override void OnClosed(EventArgs e)
    {
        _bandFadeTimer?.Stop();
        _bandFadeTimer = null;
        _band?.Dispose();
        _band = null;
        _bandVisible = false;

        base.OnClosed(e);
    }

    /// <summary>
    /// 重提升时依次使用的 hWndInsertAfter。**两步缺一不可**：
    ///
    ///   ① <c>HWND_TOPMOST</c>：只用来**保住 topmost 样式**（Show/Hide、外壳操作
    ///      可能把 WS_EX_TOPMOST 摘掉）。它在**已经 topmost** 的窗口之间
    ///      **不改变相对次序** —— 所以任务栏顶上来之后，光发它永远抢不回来。
    ///   ② <c>HWND_TOP</c>：这才是"放到 Z 序最顶"。窗口已经是 topmost，
    ///      于是它落在 topmost 组的最前面，也就是任务栏之上。
    ///
    /// 实测（tools/test_osd_topmost.py，真任务栏）：只发 ① 时，任务栏压上来
    /// 之后连发一秒 Z 序纹丝不动；补上 ② 立刻回到最前。
    ///
    /// 为什么单独抽成数组：自检拿它做断言，防的是"把两步简化成一步
    /// HWND_TOPMOST"这种看起来等价、实际会让 bug 复发的重构。
    /// </summary>
    internal static readonly IntPtr[] RaiseInsertAfterForTest =
    {
        NativeMethods.HWND_TOPMOST,
        NativeMethods.HWND_TOP,
    };

    /// <summary>
    /// 提到**整个 Z 序的最前面**（高于任务栏、高于其它 topmost 窗口）。
    ///
    /// 见 <see cref="RaiseInsertAfterForTest"/> 里两步的分工说明。
    ///
    /// 不发 WM_WINDOWPOSCHANGING（SWP_NOSENDCHANGING）：这个窗口自己会响应
    /// 位置变化，重提升纯属"重排"，没必要惊动消息链。
    /// </summary>
    private void RaiseToTop()
    {
        // band 16 高于任务栏(1)与开始菜单(6)，同带内也没有竞争者，不用抢
        if (_band != null) return;

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !IsVisible) return;

            const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
                             | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSENDCHANGING;

            foreach (IntPtr insertAfter in RaiseInsertAfterForTest)
                NativeMethods.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, flags);
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

    /// <summary>把实际尺寸 / 位置 / 缩放 / 扩展样式记一笔，出问题时能对账。</summary>
    private void LogGeometry(string when)
    {
        try
        {
            /* 高带路径下这个窗口根本没建 HWND，真正在屏幕上的是 band 宿主，
             * 日志里必须是它的句柄，否则排查时对不上号。 */
            IntPtr hwnd = _band?.Handle ?? new WindowInteropHelper(this).Handle;
            int ex = hwnd == IntPtr.Zero ? 0 : NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);

            string where = OsdPlacement.TryGetTargetWorkArea(hwnd, out var area)
                ? $"{area.Left},{area.Top} {area.Width}x{area.Height} @{area.DpiScale:F2}x"
                : "(未知)";

            var r = area.IsValid
                ? OsdPlacement.Compute(area.Left, area.Top, area.Right, area.Bottom, area.DpiScale)
                : default;
            var (cw, ch) = OsdPlacement.CardSizeForTest(r);

            CoreLog.Info($"OSD {when}：卡片={cw}x{ch}px 窗口={r.Width}x{r.Height}px "
                         + $"pos={r.X},{r.Y} 工作区={where} "
                         + $"ex=0x{ex:X8} hwnd=0x{hwnd.ToInt64():X} "
                         + $"卡片控件={CardView.ActualWidth:F1}x{CardView.ActualHeight:F1}"
                         + $" opacity={Opacity:F2}");
        }
        catch (Exception e)
        {
            CoreLog.Warn("OSD 几何信息记录失败：" + e.Message);
        }
    }

    /// <summary>
    /// 按百分比设置进度条填充 —— 已搬进 <see cref="OsdCard.SetPercent"/>。
    ///
    /// 搬走的原因：高带路径渲染的是**另一个** <see cref="OsdCard"/> 实例，
    /// 填充比例如果留在窗口这边算，两条路径迟早会算出不一样的宽度。
    /// </summary>
}
