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
///   | 项目 | 系统横条（实测） | 本横条 |
///   | --- | --- | --- |
///   | 卡片 | 192×47 DIP、深色、圆角 7.5、无边框、带柔光阴影 | 同 |
///   | 版式 | 左图标 + 中间进度条 + 右侧数字 | 同（图标为自绘太阳，见 OsdCard） |
///   | 进度条 | 110×4 DIP、填充=系统强调色浅档、槽=白色 55% | 同 |
///   | 文字 | 纯白，数字 14 | 同 |
///   | 位置 | 水平居中、贴工作区底边 14 DIP | 同 |
///   | 动画 | 淡入约 150ms、停留约 1.9s、淡出约 120ms（合计 ~2.1s） | 同 |
///
/// 卡片尺寸、贴底距离、缩放换算**全部集中在 <see cref="OsdPlacement"/>**：
/// 那里用 DIP 定义、用目标显示器的缩放换算成物理像素、用工作区的相对关系
/// 定位，所以换台电脑（4K/1080p、100%/150%/200%）横条的观感都一样。
/// 本类只负责"把它画出来 + 摆到那个矩形上"。
///
/// ⚠ 图标是唯一**不**复刻系统的一块：2026-10-25 起按用户要求改为自绘太阳
///   （圆心直径固定、放射线随亮度伸缩、亮度变化时绕圆心旋转且转速联动），
///   几何常量见 <see cref="OsdPlacement"/> 的 Sun* 系列，绘制见 OsdCard。
///
/// 这些数字不是拍脑袋：用全屏截图 + 连通域/边缘分析量出来的
/// （这台机器 3840×2160 @200%，对应 384×94 像素的卡片）。
///
/// ⚠ 2026-10-23 复测（铺一块纯黑全屏窗体 → 按音量键 → 截图量卡片边缘）：
///     卡片 384×94 px、水平居中，下沿距**屏幕**底边 28 px（= 14 DIP）。
///   本机任务栏是"自动隐藏"，此时工作区等于整屏。
///
/// ⚠ 2026-10-25 换到笔记本（3200×2000 @200%）后做了两组对照，把两件事钉死：
///     ① **贴的是工作区，不是屏幕底**：把任务栏临时改成"常显"
///        （工作区底边 2000 → 1904），系统横条跟着上移 96 px，
///        卡片仍在 1782..1875，距工作区底边还是 28 px。旧实现拿
///        ABM_GETTASKBARPOS 把基准又抬了 96 px，那才是"整条偏高一个横条"的元凶。
///     ② **高度是 47 DIP，不是 48**：亚像素边界给出 94.0 px = 47.0 DIP。
///        多算 1 DIP，我们在 200% 下就比系统高 2 px，并排时上沿明显错开。
///   同时把内部版式也逐项对齐了（图标槽/进度条 110×4/数字列），
///   常量全部集中在 <see cref="OsdPlacement"/>。
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
    /// 卡片圆角（DIP）。实测对齐系统横条 = 7.5。
    ///
    /// 怎么量出来的（<c>tools/measure_osd_radius.py</c>）：
    /// 铺纯黑全屏 → 触发横条 → 截图 → 沿左下角逐行求左边界
    /// → 用圆角矩形方程 d(t)=R-sqrt(R²-(R-t)²) 做最小二乘拟合。
    ///
    /// 2026-10-25 在笔记本（3200×2000 @200%）上复量：亚像素轮廓
    /// 11.36/8.56/6.80/5.39/4.19/3.41/2.76/2.03/1.53/1.03…，
    /// 拟合 **R = 15.10 px = 7.55 DIP**（rmse 0.07 px）—— 仍是 7.5。
    ///
    /// 以前写 6 是"看起来差不多"估的：200% 下只差 4 个像素，单看没感觉，
    /// 但和系统横条并排时"我们更方"很明显（用户就是这么发现的）。
    /// </summary>
    public const double CardRadius = OsdPlacement.CardRadiusDip;

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
    private (int Percent, double Icon, bool Light, double Scale) _cacheKey = (-1, -1, false, 0);

    /* ---- 太阳图标的补间 ----
     *
     * 图标不是"改一个数就完事"：从 20% 跳到 80% 时，如果一帧到位，
     * 看起来像闪了一下。这里用 ~150ms 的 ease-out 把进度推过去，
     * 连续按键时每次都从**当前已经显示到的位置**接着走，不会跳。
     * 进度同时驱动放射线长度与旋转角（360°×进度，见 OsdCard.RefreshIcon），
     * 所以亮度变化期间线一边伸缩一边转，停稳即静止。
     *
     * 两条宿主都要动起来：
     *   · WPF 窗口路径 —— 改属性即可，WPF 自己重绘；
     *   · band 16 路径 —— 位图是快照，必须每帧重渲染再 UpdateLayeredWindow
     *     （见 RepaintBandForIcon）。
     */
    private const int IconAnimMs = 150;
    private DispatcherTimer? _iconTimer;

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
                 * Loc.T("第一次离屏渲染")（字体、DropShadowEffect、DIB 创建）。
                 * 先按 100% 渲染一张丢掉，把这条链路焐热。 */
                _ = _instance.Render(50, 0.5, 1.0);
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

    protected override void OnClosed(EventArgs e)
    {
        _iconTimer?.Stop();
        _iconTimer = null;

        _bandFadeTimer?.Stop();
        _bandFadeTimer = null;
        _band?.Dispose();
        _band = null;
        _bandVisible = false;

        base.OnClosed(e);
    }
}