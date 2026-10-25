using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CapsLockPlus.Core.Windows;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 亮度横条的视觉本体（卡片 + 图标 + 进度条 + 数字）。
///
/// 为什么单独做成 <see cref="UserControl"/> 而不是直接写在窗口里：
/// 横条有**两个宿主** ——
///   · <see cref="OsdWindow"/>：普通 WPF 窗口，band 进不去时用它兜底；
///   · <see cref="CapsLockPlus.Core.Windows.OsdBandSurface"/>：band 16 的
///     分层窗口，内容是这里 <c>RenderTargetBitmap</c> 出来的位图。
/// 两个宿主必须长得**一模一样**，所以视觉只能有一份定义。
///
/// 版式全部来自 <see cref="OsdPlacement"/> 的常量，那些数是从系统横条
/// 的截图上一个像素一个像素量出来的（见那边的注释）。这里只负责
/// 把它们套到控件上，并在亮度变化时改太阳的放射线与旋转。
/// </summary>
public partial class OsdCard : UserControl
{
    /* ── 太阳图标：自绘，几何完全照 Windows 11 原版（E706 字形） ──────
     *
     * 三代方案的来龙去脉，免得下次再走一遍：
     *   v4.5.23：直接用 Segoe Fluent Icons 的 E706 字形 + ScaleTransform
     *            0.70→1.00 整字形缩放，和系统横条逐像素一致 —— 但整枚图标
     *            跟着亮度一起缩放，用户不要（要"圆心不缩放"）。
     *   v4.5.24：改自绘。圆画成**实心圆盘**，射线内端画到圆**里面**
     *            （半径 − 0.1）让圆盖住接缝。结果圆和线连成一整块黑饼，
     *            用户评为"比之前更差" —— 这就是"黏连"。
     *   v4.5.25：把 E706 的几何重新量出来（见 OsdPlacement 里的量法注释），
     *            发现原版根本不是"实心盘 + 贴着的线"，而是
     *            **空心圆环 + 8 根离环 2 DIP 的胶囊射线**。
     *
     * 现在结构（逐条对应原版）：
     *   · SunRing 是**描边**椭圆：几何直径 7.0、线宽 1.0 → 墨迹外径 8.0，
     *     与环内径 6.0，环宽 1.0 —— 三个数都是原版量出来的；
     *   · 射线内端固定在 6.0 DIP（墨迹，含圆帽），所以**环线间隙恒为 2.0 DIP**，
     *     任何亮度下都不会重新黏上；
     *   · 射线外端随进度从 7.4 伸到 8.0（满档 = 原版 16 DIP 墨迹）；
     *   · 旋转角 = SunTwistDeg × 进度，纯函数 → 角速度与亮度变化速度成正比，
     *     停就停，且两个宿主（WPF 窗口 / band 16 离屏位图）同一进度同帧。
     *
     * 这个控件保持"给什么进度画什么样子"的纯函数本质：补间动画仍由窗口那边的
     * 定时器逐帧推 <see cref="SetIconProgress"/>（见 OsdWindow.StartIconAnimation），
     * 离屏渲染和自检拿到的永远是确定值，不会被"正在补间"干扰。
     */
    private readonly List<Line> _rays = new();
    private double _iconProgress;
    private double _sunReach;
    private Color _textColor = Colors.White;

    public OsdCard()
    {
        InitializeComponent();

        ApplyLayout();
        BuildSun();
        ApplyIconBrush();
        RefreshIcon();
    }

    /// <summary>
    /// 把 <see cref="OsdPlacement"/> 里的版式常量套到控件上。
    ///
    /// 为什么不在 XAML 里写死：XAML 没法引用 C# 常量，两边各写一份
    /// 迟早会漂移（这次的"横条比系统高 1 DIP"就是这么来的）。
    /// 现在 XAML 只描述结构，数值一律由这里给。
    /// </summary>
    private void ApplyLayout()
    {
        Card.CornerRadius = new CornerRadius(OsdPlacement.CardRadiusDip);
        Card.Padding = new Thickness(
            OsdPlacement.IconLeftDip, 0, OsdPlacement.RightPadDip, 0);

        Layout.Margin = new Thickness(0, 0, 0, OsdPlacement.ContentLiftDip);

        IconColumn.Width = new GridLength(OsdPlacement.IconSlotDip);
        IconGapColumn.Width = new GridLength(OsdPlacement.IconGapDip);
        ValueColumn.Width = new GridLength(OsdPlacement.ValueColumnDip);

        IconBox.Width = OsdPlacement.IconSlotDip;
        IconBox.Height = OsdPlacement.IconSlotDip;

        /* 太阳：射线层铺满槽（线在层内绕槽中心排布），圆环按原版比例定位。
         * 具体每根线的端点由 RefreshIcon 每帧算。 */
        SunRays.Width = OsdPlacement.IconSlotDip;
        SunRays.Height = OsdPlacement.IconSlotDip;

        // 环是**描边**椭圆：Width/Height 是几何线直径（外径 − 线宽），
        // 描边再向两侧各长半个线宽，墨迹才是 8.0 外径 / 6.0 内径。
        SunRing.Width = OsdPlacement.SunRingDiameterDip;
        SunRing.Height = OsdPlacement.SunRingDiameterDip;
        SunRing.StrokeThickness = OsdPlacement.SunRingStrokeDip;

        Track.Height = OsdPlacement.TrackHeightDip;
        TrackBar.CornerRadius = new CornerRadius(OsdPlacement.TrackHeightDip / 2);
        FillBar.CornerRadius = new CornerRadius(OsdPlacement.TrackHeightDip / 2);

        ValueText.FontFamily = new FontFamily(OsdPlacement.ValueFontFamilyName);
        ValueText.FontSize = OsdPlacement.ValueFontSizeDip;
        ValueText.MinWidth = OsdPlacement.ValueColumnDip;
        ValueText.Margin = new Thickness(0, 0, 0, OsdPlacement.ValueLiftDip);
    }

    /// <summary>
    /// 建出 8 根放射线（只建一次；之后每帧只改端点坐标，不增删元素）。
    ///
    /// 原版的射线是**两端圆帽的胶囊**：量出来的墨迹内端 6.0、外端 8.0，
    /// 两头都是圆弧。所以两端都用 <see cref="PenLineCap.Round"/>，
    /// 线端坐标取"圆帽圆心"（墨迹 ± 半个线粗），实际画出来的墨迹才和量到的一致。
    /// </summary>
    private void BuildSun()
    {
        SunRays.Children.Clear();
        _rays.Clear();

        for (int i = 0; i < OsdPlacement.SunRayCount; i++)
        {
            var line = new Line
            {
                StrokeThickness = OsdPlacement.SunRayThicknessDip,
                StrokeStartLineCap = PenLineCap.Round,    // 内端圆帽（原版如此）
                StrokeEndLineCap = PenLineCap.Round,      // 外端圆帽
            };
            _rays.Add(line);
            SunRays.Children.Add(line);
        }
    }

    /// <summary>卡片圆角（DIP）。两个宿主都从这里取，避免各写一份。</summary>
    public double Radius
    {
        get => Card.CornerRadius.TopLeft;
        set => Card.CornerRadius = new CornerRadius(value);
    }

    /// <summary>
    /// 百分比（0~100）。数字和进度条**立即**到位，图标也立刻落到终值 ——
    /// 动画由窗口那边驱动（<see cref="SetIconProgress"/> 逐帧推进），
    /// 这样离屏渲染和自检拿到的永远是确定值，不会被"正在补间"干扰。
    /// </summary>
    public void SetPercent(int percent)
    {
        int pct = Math.Clamp(percent, 0, 100);
        ValueText.Text = pct.ToString();

        double s = pct / 100.0;

        /* 用两列星号比例（s : 1-s）而不是算像素宽度：
         * 算像素要靠 Track.ActualWidth，离屏渲染时它还是 0，
         * 进度条就会停在空状态；比例交给布局去分，什么时候设都对。 */
        FillColumn.Width = new GridLength(s, GridUnitType.Star);
        RestColumn.Width = new GridLength(1 - s, GridUnitType.Star);

        SetIconProgress(s);
    }

    /// <summary>
    /// 单独推进太阳的进度（0~1）。运行时由窗口的补间定时器逐帧调用，
    /// 是**这个控件里唯一会每帧变的东西**；数字和进度条不受它影响。
    /// 进度决定两件事：放射线的长度、整组线的旋转角。
    /// </summary>
    public void SetIconProgress(double s)
    {
        _iconProgress = Math.Clamp(s, 0, 1);
        RefreshIcon();
    }

    /// <summary>当前太阳的进度（0~1）。窗口拿它当动画起点。</summary>
    public double IconProgress => _iconProgress;

    /// <summary>套一整套配色（卡片/文字/槽/填充）。</summary>
    public void SetPalette(Color card, Color text, Color track, Color fill)
    {
        Card.Background = new SolidColorBrush(card);
        ValueText.Foreground = new SolidColorBrush(text);
        TrackBar.Background = new SolidColorBrush(track);
        FillBar.Background = new SolidColorBrush(fill);

        _textColor = text;
        ApplyIconBrush();
        RefreshIcon();
    }

    /// <summary>
    /// 按当前进度画太阳。**圆环永远不动，只有射线的外端在动**：
    ///
    ///   · 圆环：几何直径恒为 <see cref="OsdPlacement.SunRingDiameterDip"/>，
    ///     不随进度缩放（用户要求，整字形缩放已废弃）；
    ///   · 射线内端：**恒定**在 <see cref="OsdPlacement.SunRayInnerDip"/>，
    ///     所以环线间隙恒为 <see cref="OsdPlacement.SunGapDip"/>（2.0 DIP）——
    ///     这是"圆与光芒分离"的结构性保证，不靠画上去盖；
    ///   · 射线外端：OuterMin → OuterMax，关于进度**线性**（一次函数，
    ///     无分段 —— "按两下长了多少"要说得清）；
    ///   · 旋转：SunSpin.Angle = SunTwistDeg × 进度，纯函数 ——
    ///     角速度天然与亮度变化速度成正比（360°×d进度/dt），
    ///     亮度停稳旋转就停，不需要额外的减速定时器。
    ///
    /// 线端坐标用"圆帽圆心"而不是墨迹端点：Round 帽会让墨迹两端各外扩
    /// 半个线粗，所以线段端点 = 墨迹 ∓ 0.5。这样 OsdPlacement 里的
    /// 常量就是**看得见的墨迹**，和原版实测值一一对应。
    /// </summary>
    private void RefreshIcon()
    {
        double s = _iconProgress;
        double half = OsdPlacement.SunRayThicknessDip / 2;

        // 外端（墨迹）在 OuterMin→OuterMax 之间线性伸缩；内端恒定
        _sunReach = OsdPlacement.SunRayOuterMinDip
                  + (OsdPlacement.SunRayOuterMaxDip - OsdPlacement.SunRayOuterMinDip) * s;

        // 槽中心即太阳中心（槽 IconSlotDip×IconSlotDip → 中心在正中间）
        double cx = OsdPlacement.IconSlotDip / 2;
        double cy = OsdPlacement.IconSlotDip / 2;

        double rIn = OsdPlacement.SunRayInnerDip + half;    // 内端圆帽圆心
        double rOut = _sunReach - half;                     // 外端圆帽圆心

        for (int i = 0; i < _rays.Count; i++)
        {
            double rad = (i * 360.0 / OsdPlacement.SunRayCount - 90.0) * Math.PI / 180.0;
            double dx = Math.Cos(rad);
            double dy = Math.Sin(rad);

            var line = _rays[i];
            line.X1 = cx + dx * rIn;
            line.Y1 = cy + dy * rIn;
            line.X2 = cx + dx * rOut;
            line.Y2 = cy + dy * rOut;
        }

        SunSpin.Angle = OsdPlacement.SunTwistDeg * s;
    }

    /// <summary>
    /// 太阳用文字色 —— 单色，没有色相变化。
    /// 系统横条里的图标就是纯色（夜间纯白、日间近黑），不透明、无光晕。
    /// 环是描边、线是描边，都吃这个画刷；环的 Fill 永远是 null。
    /// </summary>
    private void ApplyIconBrush()
    {
        var brush = new SolidColorBrush(_textColor);
        brush.Freeze();

        foreach (var line in _rays) line.Stroke = brush;
        SunRing.Stroke = brush;
    }

    /// <summary>仅供自检：放射线条数（设计值 8）。</summary>
    internal int SunRayCountForTest => _rays.Count;

    /// <summary>
    /// 仅供自检：圆环的**墨迹**外径（DIP）= 几何直径 + 线宽。
    /// 必须恒定，且等于原版量出来的 8.0。
    /// </summary>
    internal double SunRingOuterDipForTest => SunRing.Width + SunRing.StrokeThickness;

    /// <summary>仅供自检：圆环的**墨迹**内径（DIP）= 几何直径 − 线宽。</summary>
    internal double SunRingInnerDipForTest => SunRing.Width - SunRing.StrokeThickness;

    /// <summary>仅供自检：圆环是不是描边（Fill 必须为空，否则又成实心饼了）。</summary>
    internal bool SunRingIsStrokeOnlyForTest => SunRing.Fill == null && SunRing.Stroke != null;

    /// <summary>
    /// 仅供自检：射线**墨迹内端**离圆心的距离（DIP）——
    /// 直接取第一根线的起点（圆帽圆心）再**减去**半个线粗：圆帽是向圆心一侧
    /// 鼓出半个线粗的，所以墨迹比端点更靠近圆心。
    /// </summary>
    internal double SunRayInnerEdgeForTest
    {
        get
        {
            if (_rays.Count == 0) return 0;
            double cx = OsdPlacement.IconSlotDip / 2;
            double cy = OsdPlacement.IconSlotDip / 2;
            var l = _rays[0];
            double d = Math.Sqrt((l.X1 - cx) * (l.X1 - cx) + (l.Y1 - cy) * (l.Y1 - cy));
            return d - OsdPlacement.SunRayThicknessDip / 2;
        }
    }

    /// <summary>
    /// 仅供自检：**环线之间的净间隙**（DIP）。必须恒 > 0 ——
    /// 用户这次的核心诉求就是"圆与光线彼此分离"，这条是它的量化判据。
    /// </summary>
    internal double SunGapForTest =>
        SunRayInnerEdgeForTest - SunRingOuterDipForTest / 2;

    /// <summary>仅供自检：射线外端（墨迹）当前离圆心的距离（DIP）。</summary>
    internal double SunRayReachForTest => _sunReach;

    /// <summary>仅供自检：太阳墨迹总直径（DIP，含射线外端圆帽）。</summary>
    internal double SunInkDipForTest => _sunReach * 2;

    /// <summary>仅供自检：太阳当前旋转角（度）= SunTwistDeg × 进度。</summary>
    internal double SunSpinAngleForTest => SunSpin.Angle;

    /// <summary>仅供自检：太阳的画刷（查它是不是单色、有没有跟着主题走）。</summary>
    internal Brush IconBrushForTest => SunRing.Stroke;

    /// <summary>
    /// 仅供自检：图标里有没有"模糊光晕"这类元素。
    /// 用户明确不要光晕（径向渐变 / 模糊）—— 图标只能是干净的几何形。
    /// </summary>
    internal bool HasSoftGlowForTest =>
        IconBox.Effect != null || SunRays.Effect != null || SunRing.Effect != null;

    /// <summary>仅供自检：8 根手绘放射线都在位（新设计的地基）。</summary>
    internal bool HasSunRaysForTest => _rays.Count == OsdPlacement.SunRayCount;

    /// <summary>仅供自检：图标墨迹相对卡片左上角的位置（DIP）。</summary>
    internal (double Left, double Top, double Width, double Height) IconInkForTest()
    {
        double slotLeft = OsdPlacement.IconLeftDip;
        double slotTop = (OsdPlacement.CardHeightDip - OsdPlacement.IconSlotDip) / 2;
        double ink = SunInkDipForTest;
        double pad = (OsdPlacement.IconSlotDip - ink) / 2;

        return (slotLeft + pad, slotTop + pad, ink, ink);
    }
}
