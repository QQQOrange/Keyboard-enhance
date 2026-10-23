using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
/// 控件整体 216×72 DIP（卡片 192×48 + 四周 12 的阴影余量），
/// 尺寸/圆角/贴底距离的实测依据见 OsdWindow 的注释。
/// </summary>
public partial class OsdCard : UserControl
{
    /* ── 太阳图标的联动参数 ────────────────────────────────────────
     *
     * 用户要的是"越亮，光芒越强越亮；越暗，光芒越弱越暗淡"，
     * 并且明确要求**纯线性**、**不加彩色**，就动当前这个字形。
     *
     * 所以只调两个量，都是一次函数 a + (b-a)·s（s = 百分比/100）：
     *
     *   ① 本体不透明度：0.45 → 1.00
     *      0% 时压在 45%，看得出是太阳但明显发灰；100% 时纯实。
     *      最小值没有取 0：图标是"这是亮度"的唯一标识，全灭会看不懂。
     *
     *   ② 辉光不透明度：0 → 上限（深色 0.58 / 浅色 0.30）
     *      0% 时完全没有光晕；100% 时铺满。跟着一起线性变的有**半径**
     *      （0.62 → 1.12 倍），两者相乘在视觉上就是"光越亮、铺得越开"，
     *      但每个量单独看仍然是一条直线。
     *
     * 光晕颜色不另起一套：直接取文字色（深色主题白、浅色主题黑），
     * 仍然是单色，只是明暗。
     */
    internal const double IconMinOpacity = 0.45;
    internal const double IconGlowMaxOnDark = 0.52;
    internal const double IconGlowMaxOnLight = 0.28;
    internal const double IconGlowMinScale = 0.62;
    internal const double IconGlowMaxScale = 1.12;

    private int _percent;
    private double _iconProgress;
    private double _glowCeiling = IconGlowMaxOnDark;

    public OsdCard()
    {
        InitializeComponent();
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
        _percent = Math.Clamp(percent, 0, 100);
        ValueText.Text = _percent.ToString();

        double s = _percent / 100.0;

        /* 用两列星号比例（s : 1-s）而不是算像素宽度：
         * 算像素要靠 Track.ActualWidth，离屏渲染时它还是 0，
         * 进度条就会停在空状态；比例交给布局去分，什么时候设都对。 */
        FillColumn.Width = new GridLength(s, GridUnitType.Star);
        RestColumn.Width = new GridLength(1 - s, GridUnitType.Star);

        SetIconProgress(s);
    }

    /// <summary>
    /// 单独推进太阳的明暗（0~1）。运行时由窗口的补间定时器逐帧调用，
    /// 是**这个控件里唯一会每帧变的东西**；数字和进度条不受它影响。
    /// </summary>
    public void SetIconProgress(double s)
    {
        _iconProgress = Math.Clamp(s, 0, 1);
        RefreshIcon();
    }

    /// <summary>当前太阳的明暗进度（0~1）。窗口拿它当动画起点。</summary>
    public double IconProgress => _iconProgress;

    /// <summary>套一整套配色（卡片/文字/槽/填充）。</summary>
    public void SetPalette(Color card, Color text, Color track, Color fill)
    {
        Card.Background = new SolidColorBrush(card);
        IconText.Foreground = new SolidColorBrush(text);
        ValueText.Foreground = new SolidColorBrush(text);
        TrackBar.Background = new SolidColorBrush(track);
        FillBar.Background = new SolidColorBrush(fill);

        /* 辉光跟着文字走：深色主题是白色外发光，浅色主题是淡淡的墨晕。
         * 浅色下必须**压低上限** —— 白光晕在浅底上等于没有，而黑光晕
         * 一旦给到 0.58 就会糊成一团脏印子。 */
        _glowCeiling = Luma(text) >= 0.5 ? IconGlowMaxOnDark : IconGlowMaxOnLight;
        ApplyGlowBrush(text);
        RefreshIcon();
    }

    /// <summary>
    /// 按当前进度重算太阳的亮度与辉光。**全部线性**，无分段。
    /// </summary>
    private void RefreshIcon()
    {
        double s = _iconProgress;

        // ① 越亮越实
        IconText.Opacity = IconMinOpacity + (1 - IconMinOpacity) * s;

        // ② 越亮光芒越强、铺得越开
        IconGlow.Opacity = _glowCeiling * s;
        double scale = IconGlowMinScale + (IconGlowMaxScale - IconGlowMinScale) * s;
        IconGlowScale.ScaleX = scale;
        IconGlowScale.ScaleY = scale;
    }

    /// <summary>
    /// 用文字色做一圈径向渐隐的辉光：中心满，中段 34%，外圈 9%，边缘全透明。
    /// 这几档是**颜色**的软硬，不随百分比变；强度只由 Opacity 控制 ——
    /// 这样"线性"只有一个出口，不会因为两处相乘变得不可预期。
    /// </summary>
    private void ApplyGlowBrush(Color text)
    {
        var brush = new RadialGradientBrush { MappingMode = BrushMappingMode.RelativeToBoundingBox };
        brush.GradientStops.Add(new GradientStop(WithAlpha(text, 1.00), 0.00));
        brush.GradientStops.Add(new GradientStop(WithAlpha(text, 0.34), 0.45));
        brush.GradientStops.Add(new GradientStop(WithAlpha(text, 0.09), 0.75));
        brush.GradientStops.Add(new GradientStop(WithAlpha(text, 0.00), 1.00));
        brush.Freeze();

        IconGlow.Fill = brush;
    }

    private static Color WithAlpha(Color c, double alpha)
        => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

    private static double Luma(Color c)
        => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    /// <summary>仅供自检：图标本体的不透明度。</summary>
    internal double IconOpacityForTest => IconText.Opacity;

    /// <summary>仅供自检：辉光的不透明度。</summary>
    internal double IconGlowOpacityForTest => IconGlow.Opacity;

    /// <summary>仅供自检：辉光的缩放倍数。</summary>
    internal double IconGlowScaleForTest => IconGlowScale.ScaleX;

    /// <summary>仅供自检：辉光的画刷（查它是不是单色）。</summary>
    internal Brush GlowBrushForTest => IconGlow.Fill;
}
