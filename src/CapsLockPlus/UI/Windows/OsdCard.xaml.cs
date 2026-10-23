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
    /* ── 太阳图标的联动参数 ────────────────────────────────────────
     *
     * 用户要的是"越亮，光芒越强越亮；越暗，光芒越弱越暗淡"，
     * 并且明确要求：**纯线性**、**单色**、**就用当前这个字形**，
     * 而且光芒必须是"一根根的线，用长短表示强弱" ——
     * 不要模糊光晕（光晕的观感不是线性的，这一点被明确否掉了）。
     *
     * 所以只动两个量，都是一次函数 a + (b-a)·s（s = 百分比/100）：
     *
     *   ① 本体不透明度：0.45 → 1.00
     *      0% 时压在 45%，看得出是太阳但明显发灰；100% 时纯实。
     *      最小值没有取 0：图标是"这是亮度"的唯一标识，全灭会看不懂。
     *
     *   ② 八根光芒线的**长度**：0.9 → 5.2 DIP（内端固定，只往外伸）
     *      0% 时只剩贴着本体的一小截，100% 时伸到最开。
     *      线条本身的颜色和透明度不变 —— 强弱只由长短表达，
     *      这样"线性"是肉眼能直接读出来的，不用猜。
     */
    internal const double IconMinOpacity = 0.45;

    /// <summary>光芒线的根数（45° 一根，避开字形自带的四角小刺）。</summary>
    internal const int RayCount = 8;

    /// <summary>线条内端离图标中心的距离（DIP）。在字形外缘之外，不压到太阳本体。</summary>
    internal const double RayInner = 9.0;

    /// <summary>0% 时的外端距离（≈ 只剩 0.9 DIP 的一小段）。</summary>
    internal const double RayMinOuter = 9.9;

    /// <summary>100% 时的外端距离（≈ 5.2 DIP 的射线）。</summary>
    internal const double RayMaxOuter = 14.2;

    /// <summary>线宽（DIP）。1.4 在 200% 缩放下是 3 像素，细但仍清楚。</summary>
    internal const double RayThickness = 1.4;

    private readonly Line[] _rays = new Line[RayCount];
    private int _percent;
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

        _textColor = text;
        ApplyRayBrush();
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
    /// 按当前进度重算太阳的亮度与光芒长度。**全部线性**，无分段：
    ///
    ///   本体不透明度 = 0.45 + 0.55·s
    ///   光芒长度     = 0.9 + 4.3·s   （DIP）
    /// </summary>
    private void RefreshIcon()
    {
        double s = _iconProgress;

        IconText.Opacity = IconMinOpacity + (1 - IconMinOpacity) * s;

        double outer = RayMinOuter + (RayMaxOuter - RayMinOuter) * s;

        for (int i = 0; i < RayCount; i++)
        {
            /* 从正上方开始、每 45° 一根。字形自带的是四个斜向小刺，
             * 这里正好错开，看起来才是"外加的一圈光芒"。 */
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
    /// 光芒线用文字色 —— 和太阳本体同一个颜色，仍然是单色。
    ///
    /// 注意线的透明度**不随亮度变**：强弱只由长短表达。
    /// 两样一起变会让"线性"变得说不清，也容易看起来像在闪烁。
    /// </summary>
    private void ApplyRayBrush()
    {
        var brush = new SolidColorBrush(_textColor);
        brush.Freeze();

        foreach (var line in _rays) line.Stroke = brush;
    }

    /// <summary>仅供自检：图标本体的不透明度。</summary>
    internal double IconOpacityForTest => IconText.Opacity;

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

    /// <summary>仅供自检：光芒线的画刷（查它是不是单色）。</summary>
    internal Brush RayBrushForTest => _rays[0].Stroke;

    /// <summary>
    /// 仅供自检：卡片里有没有"模糊光晕"这类元素。
    ///
    /// 用户明确不要光晕（径向渐变 / 模糊）—— 光芒只能是线。
    /// 这条守的是"以后又有人觉得加个 glow 更好看"。
    /// </summary>
    internal bool HasSoftGlowForTest
    {
        get
        {
            foreach (var child in Rays.Children)
                if (child is not Line) return true;

            return IconText.Effect != null;
        }
    }
}
