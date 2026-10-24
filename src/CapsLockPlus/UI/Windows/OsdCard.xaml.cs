using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

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
    /* ── 太阳图标：手绘，不用字形 ──────────────────────────────────
     *
     * 用户的三条硬要求（v4.5.11）：
     *   ① 亮度最低时线几乎不可见；
     *   ② 亮度满时只有"原来长度"的 2/3；
     *   ③ 每根光芒必须是**一根连续的线** —— 之前用 U+E706 字形叠加
     *      动态线，看起来像两段拼接，线缩短时字形那截留着还像底槽。
     *
     * 所以整只图标改成手绘：中间一个圆 + 八根线。
     *   · 圆：直径 6.4 DIP，固定大小；
     *   · 线：内端固定在 4.7（圆外留 1.5 的缝），外端 = 内端 + 长度；
     *         长度 0.25 → 3.5 DIP（线性），满格 3.5 正是原来 5.2 的 2/3。
     *
     * 只有两个量随亮度变，都是一次函数 a + (b-a)·s（s = 百分比/100）：
     *   ① 整组不透明度：0.45 → 1.00（越暗越淡）
     *   ② 光芒长度：    0.25 → 3.50 DIP
     * 线条本身的粗细和颜色不变 —— 强弱只由长短表达，线性才一眼读得出来。
     */
    internal const double IconMinOpacity = 0.45;

    /// <summary>太阳本体直径（DIP）。和 Windows 亮度图标的大致比例一致。</summary>
    internal const double CoreDiameter = 6.4;

    /// <summary>光芒线的根数（45° 一根）。</summary>
    internal const int RayCount = 8;

    /// <summary>线条内端离图标中心的距离（DIP）：圆外留 1.5 的缝。</summary>
    internal const double RayInner = 4.7;

    /// <summary>0% 时的线长：只剩一小截，几乎不可见。</summary>
    internal const double RayMinLength = 0.25;

    /// <summary>100% 时的线长：原长度 5.2 DIP 的 2/3 ≈ 3.5。</summary>
    internal const double RayMaxLength = 3.5;

    /// <summary>线宽（DIP）。线变短后太粗会显得胖，1.2 和 Windows 那条最接近。</summary>
    internal const double RayThickness = 1.2;

    private readonly Line[] _rays = new Line[RayCount];
    private double _iconProgress;
    private Color _textColor = Colors.White;

    public OsdCard()
    {
        InitializeComponent();
        BuildRays();
        RefreshIcon();
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
        /* 只在这里用一次，没必要存成字段 —— 图标那边另有 _iconProgress
         * 保存"当前动画进度"，两者不是一回事（补间期间数字已经到位、
         * 图标还在中间帧）。 */
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
        ValueText.Foreground = new SolidColorBrush(text);
        TrackBar.Background = new SolidColorBrush(track);
        FillBar.Background = new SolidColorBrush(fill);

        _textColor = text;
        ApplyIconBrush();
        RefreshIcon();
    }

    /// <summary>
    /// 一次性建好八根线。之后每帧只改端点坐标（X2/Y2），
    /// 不新建对象 —— 补间是一帧一次，不能每帧分配八个 Line。
    /// </summary>
    private void BuildRays()
    {
        for (int i = 0; i < RayCount; i++)
        {
            var line = new Line
            {
                StrokeThickness = RayThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,

                /* 斜线不要 SnapToDevicePixels：吸附会让 45° 的线在
                 * 200% 下粗细一跳一跳，看起来像在抖。 */
                SnapsToDevicePixels = false,
            };

            RenderOptions.SetEdgeMode(line, EdgeMode.Unspecified);
            _rays[i] = line;
            Rays.Children.Add(line);
        }
    }

    /// <summary>
    /// 按当前进度重算太阳的明暗与光芒长度。**全部线性**，无分段：
    ///
    ///   整组不透明度 = 0.45 + 0.55·s
    ///   光芒长度     = 0.25 + 3.25·s   （DIP）
    /// </summary>
    private void RefreshIcon()
    {
        double s = _iconProgress;

        IconBox.Opacity = IconMinOpacity + (1 - IconMinOpacity) * s;

        double outer = RayInner + RayMinLength + (RayMaxLength - RayMinLength) * s;

        for (int i = 0; i < RayCount; i++)
        {
            /* 从正上方开始、每 45° 一根。
             * 每根都从内端画到外端，中间**不拆段** —— 这才是"一根连续的线"。 */
            double angle = i * (2 * Math.PI / RayCount) - Math.PI / 2;
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);

            var line = _rays[i];
            line.X1 = RayInner * cos;
            line.Y1 = RayInner * sin;
            line.X2 = outer * cos;
            line.Y2 = outer * sin;
        }
    }

    /// <summary>
    /// 圆和光芒都用文字色 —— 单色，没有色相变化。
    ///
    /// 注意线的透明度**不随亮度变**（整组已经有一个线性 Opacity）：
    /// 强弱只由长短表达，两样一起变会让"线性"变得说不清。
    /// </summary>
    private void ApplyIconBrush()
    {
        var brush = new SolidColorBrush(_textColor);
        brush.Freeze();

        /* 圆是**空心描边**（见 XAML），所以这里设 Stroke 而不是 Fill。
         * 以前设 Fill：XAML 里改成 Transparent 也会被运行时重新填成实心 ——
         * 改了等于没改，图上一看还是实心圆。 */
        Core.Stroke = brush;
        foreach (var line in _rays) line.Stroke = brush;
    }

    /// <summary>仅供自检：整只图标的当前不透明度。</summary>
    internal double IconOpacityForTest => IconBox.Opacity;

    /// <summary>仅供自检：光芒线的根数。</summary>
    internal int RayCountForTest => _rays.Length;

    /// <summary>仅供自检：第 0 根光芒线的长度（DIP）。</summary>
    internal double RayLengthForTest
    {
        get
        {
            var line = _rays[0];
            return Math.Sqrt(
                (line.X2 - line.X1) * (line.X2 - line.X1)
                + (line.Y2 - line.Y1) * (line.Y2 - line.Y1));
        }
    }

    /// <summary>仅供自检：线条内端半径（必须大于圆的半径，不能插进圆里）。</summary>
    internal double RayInnerForTest => RayInner;

    /// <summary>仅供自检：太阳本体直径。</summary>
    /// <summary>
    /// 太阳圆的**外径**（描边外缘到外缘）。
    ///
    /// 描边是骑在路径上的，所以视觉外径 = Width + StrokeThickness ——
    /// 只看 Width 会把"外径没变"这件事算错。
    /// </summary>
    internal double CoreDiameterForTest => Core.Width + Core.StrokeThickness;

    /// <summary>仅供自检：圆心是不是空的（透明填充 = 空心描边）。</summary>
    internal bool CoreIsHollowForTest =>
        Core.Fill is not System.Windows.Media.SolidColorBrush b || b.Color.A == 0;

    /// <summary>仅供自检：光芒线的画刷（查它是不是单色）。</summary>
    internal Brush RayBrushForTest => _rays[0].Stroke;

    /// <summary>
    /// 仅供自检：图标里还有没有字体字形残留。
    ///
    /// 字形自带一圈固定长度的光芒，跟动态线叠在一起就会"两段拼接"、
    /// 线缩短时还留着一截像底槽 —— 这条守的就是"别再把字形加回来"。
    /// </summary>
    internal bool HasGlyphForTest
    {
        get
        {
            foreach (var child in IconBox.Children)
                if (child is TextBlock) return true;

            return false;
        }
    }

    /// <summary>
    /// 仅供自检：卡片里有没有"模糊光晕"这类元素。
    ///
    /// 用户明确不要光晕（径向渐变 / 模糊）—— 光芒只能是线。
    /// </summary>
    internal bool HasSoftGlowForTest
    {
        get
        {
            foreach (var child in Rays.Children)
                if (child is not Line) return true;

            return IconBox.Effect != null || Core.Effect != null;
        }
    }
}
