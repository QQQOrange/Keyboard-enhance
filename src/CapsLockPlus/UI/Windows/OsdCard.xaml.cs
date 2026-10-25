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
    /* ── 太阳图标：自绘，圆心固定、放射线伸缩 + 旋转 ──────────────
     *
     * 2026-10-25 之前这里用的是 Segoe Fluent Icons 的 E706 字形
     * （"空心圆 + 八根短线"）配 ScaleTransform 0.70→1.00 整字形缩放，
     * 为了和 Win11 系统横条逐像素一致。用户随后点名改了设计：
     *   ① 中心圆直径必须**固定**，禁止随亮度缩放 —— 只有外围放射线可以动；
     *   ② 整体图标偏小，要放大；
     *   ③ 亮度变化时放射线除长度变化外还要绕中心圆旋转，
     *      转速与亮度变化速度联动。
     *
     * 实现（逐条对应）：
     *   · SunCore 是直径恒定的椭圆（OsdPlacement.SunCoreDip），
     *     任何亮度下都不碰它的大小 —— 旧方案整字形缩放等于圆心也跟着
     *     放大，正是用户不要的；
     *   · 8 根放射线在 ReachMin→ReachMax 之间随进度**线性**伸缩，
     *     满档墨迹 16 DIP（旧字形满档 12 DIP），整体大一圈；
     *   · 旋转角 = SunTwistDeg × 进度，是进度的**纯函数**：
     *     角速度 = 360°×d(进度)/dt，亮度变化快就转得快、停就停；
     *     纯函数还保证 WPF 窗口与 band 16 离屏位图在同一进度画出
     *     同一帧，位图缓存（Render 的 cacheKey 含进度）不会错。
     *
     * 这个控件保持"给什么进度画什么样子"的纯函数本质：补间动画仍由
     * 窗口那边的定时器逐帧推 <see cref="SetIconProgress"/>
     * （见 OsdWindow.StartIconAnimation），离屏渲染和自检拿到的
     * 永远是确定值，不会被"正在补间"干扰。
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

        /* 太阳：放射线层铺满槽（线在层内绕槽中心排布），
         * 中心圆按恒定直径定位。具体每根线的端点由 RefreshIcon 每帧算。 */
        SunRays.Width = OsdPlacement.IconSlotDip;
        SunRays.Height = OsdPlacement.IconSlotDip;
        SunCore.Width = OsdPlacement.SunCoreDip;
        SunCore.Height = OsdPlacement.SunCoreDip;

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
    /// 内端点藏到圆芯轮廓内侧 <see cref="OsdPlacement.SunRayInsetDip"/>，
    /// 由画在上层的 <c>SunCore</c> 盖住接缝；外端点带圆帽，
    /// 视觉半径比端点距离多半个线粗。
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
                StrokeStartLineCap = PenLineCap.Flat,     // 内端平帽，贴圆芯
                StrokeEndLineCap = PenLineCap.Round,      // 外端圆帽，柔和不戳
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
    /// 按当前进度画太阳。**只有放射线在动，中心圆永远不动**：
    ///
    ///   · 放射线长度：ReachMin → ReachMax，关于进度**线性**（一次函数，
    ///     无分段 —— "按两下大了多少"要说得清）；
    ///   · 中心圆：直径恒定 <see cref="OsdPlacement.SunCoreDip"/>，
    ///     不随进度缩放（用户点名要求，旧版整字形缩放已废弃）；
    ///   · 旋转：SunSpin.Angle = SunTwistDeg × 进度，纯函数 ——
    ///     角速度天然与亮度变化速度成正比（360°×d进度/dt），
    ///     亮度停稳旋转就停，不需要额外的减速定时器。
    ///
    /// 线在槽中心呈风车状排布（第一根指正上方，每 45° 一根），
    /// 槽位尺寸不变，所以图标永远居中、右边进度条的起点也不会跟着抖。
    /// </summary>
    private void RefreshIcon()
    {
        double s = _iconProgress;

        _sunReach = OsdPlacement.SunRayReachMinDip
                  + (OsdPlacement.SunRayReachMaxDip - OsdPlacement.SunRayReachMinDip) * s;

        // 槽中心即太阳中心（槽 IconSlotDip×IconSlotDip → 中心在正中间）
        double cx = OsdPlacement.IconSlotDip / 2;
        double cy = OsdPlacement.IconSlotDip / 2;
        double rIn = OsdPlacement.SunCoreDip / 2 - OsdPlacement.SunRayInsetDip;

        for (int i = 0; i < _rays.Count; i++)
        {
            double rad = (i * 360.0 / OsdPlacement.SunRayCount - 90.0) * Math.PI / 180.0;
            double dx = Math.Cos(rad);
            double dy = Math.Sin(rad);

            var line = _rays[i];
            line.X1 = cx + dx * rIn;
            line.Y1 = cy + dy * rIn;
            line.X2 = cx + dx * _sunReach;
            line.Y2 = cy + dy * _sunReach;
        }

        SunSpin.Angle = OsdPlacement.SunTwistDeg * s;
    }

    /// <summary>
    /// 太阳用文字色 —— 单色，没有色相变化。
    /// 系统横条里的图标就是纯色（夜间纯白、日间近黑），不透明、无光晕。
    /// </summary>
    private void ApplyIconBrush()
    {
        var brush = new SolidColorBrush(_textColor);
        brush.Freeze();

        foreach (var line in _rays) line.Stroke = brush;
        SunCore.Fill = brush;
    }

    /// <summary>仅供自检：放射线条数（设计值 8）。</summary>
    internal int SunRayCountForTest => _rays.Count;

    /// <summary>仅供自检：中心圆**实际生效**的直径（DIP）—— 必须恒定。</summary>
    internal double SunCoreDipForTest => SunCore.Width;

    /// <summary>仅供自检：放射线外端点当前离圆心的距离（DIP）。</summary>
    internal double SunRayReachForTest => _sunReach;

    /// <summary>仅供自检：太阳墨迹总直径（DIP，含放射线外端圆帽）。</summary>
    internal double SunInkDipForTest =>
        (_sunReach + OsdPlacement.SunRayThicknessDip / 2) * 2;

    /// <summary>仅供自检：太阳当前旋转角（度）= SunTwistDeg × 进度。</summary>
    internal double SunSpinAngleForTest => SunSpin.Angle;

    /// <summary>仅供自检：太阳的画刷（查它是不是单色、有没有跟着主题走）。</summary>
    internal Brush IconBrushForTest => SunCore.Fill;

    /// <summary>
    /// 仅供自检：图标里有没有"模糊光晕"这类元素。
    /// 用户明确不要光晕（径向渐变 / 模糊）—— 图标只能是干净的几何形。
    /// </summary>
    internal bool HasSoftGlowForTest =>
        IconBox.Effect != null || SunRays.Effect != null || SunCore.Effect != null;

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
