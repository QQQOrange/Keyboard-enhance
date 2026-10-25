using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
/// 把它们套到控件上，并在亮度变化时改太阳的大小。
/// </summary>
public partial class OsdCard : UserControl
{
    /* ── 太阳图标：用 Windows 自己的字形，不手绘 ────────────────────
     *
     * 系统横条里的图标就是 **Segoe Fluent Icons** 的字形：
     * 音量是 E767 系列，亮度是 **E706 "Brightness"**（一个空心圆 + 八根短线）。
     * 所以这里也直接用这个字形 —— 同一个字体、同一个码位，
     * 形状（圆的大小、线的粗细/长短/角度）必然是逐点一致的，
     * 手绘再准也只能"接近"。
     *
     * 随亮度变化的**只有大小**：Windows 在调亮度时太阳会跟着变大变小。
     * 所以用 ScaleTransform 绕图标中心缩放，槽位尺寸不变 ——
     * 图标永远居中，右边进度条的起点也不会跟着抖。
     *
     * 以前是手绘八根线、长度 0.25→3.5 DIP 线性生长、整组不透明度 0.45→1.0：
     * 结果是暗的时候几乎看不见、也没有"字形"该有的观感，
     * 而且和系统那条并排时一眼就能看出不是一套东西。
     */
    internal const string IconGlyphChar = "\uE706";

    /// <summary>字形名字。Segoe Fluent Icons 的 E706 官方名就是 Brightness。</summary>
    internal const string IconFontFamilyName = "Segoe Fluent Icons, Segoe MDL2 Assets";

    /// <summary>满格时太阳缩放到几成（1.0 = 字号原样）。</summary>
    internal const double IconMaxScale = 1.0;

    /// <summary>0% 时太阳缩放到几成（见 OsdPlacement.IconMinScale 的说明）。</summary>
    internal const double IconMinScale = OsdPlacement.IconMinScale;

    private readonly ScaleTransform _iconScale;
    private double _iconProgress;
    private Color _textColor = Colors.White;

    public OsdCard()
    {
        InitializeComponent();

        _iconScale = IconScale;

        ApplyLayout();
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
        IconGlyph.FontFamily = new FontFamily(IconFontFamilyName);
        IconGlyph.FontSize = OsdPlacement.IconGlyphSizeDip;
        IconGlyph.Text = IconGlyphChar;

        Track.Height = OsdPlacement.TrackHeightDip;
        TrackBar.CornerRadius = new CornerRadius(OsdPlacement.TrackHeightDip / 2);
        FillBar.CornerRadius = new CornerRadius(OsdPlacement.TrackHeightDip / 2);

        ValueText.FontFamily = new FontFamily(OsdPlacement.ValueFontFamilyName);
        ValueText.FontSize = OsdPlacement.ValueFontSizeDip;
        ValueText.MinWidth = OsdPlacement.ValueColumnDip;
        ValueText.Margin = new Thickness(0, 0, 0, OsdPlacement.ValueLiftDip);
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
    /// 单独推进太阳的大小（0~1）。运行时由窗口的补间定时器逐帧调用，
    /// 是**这个控件里唯一会每帧变的东西**；数字和进度条不受它影响。
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
    /// 按当前进度改太阳的大小。**一次函数**，无分段：
    ///
    ///   缩放 = 0.70 + 0.30·s     （s = 百分比/100，两端取自系统实测 17→24 px）
    ///
    /// 线性和单调是用户点名要的（"越亮越大"），任何曲线都会让
    /// "按两下大了多少"变得说不清。字号本身不变 —— 缩放绕图标中心走，
    /// 所以墨迹永远居中，槽位尺寸恒定。
    /// </summary>
    private void RefreshIcon()
    {
        double s = _iconProgress;
        double scale = IconMinScale + (IconMaxScale - IconMinScale) * s;

        _iconScale.ScaleX = scale;
        _iconScale.ScaleY = scale;
    }

    /// <summary>
    /// 字形用文字色 —— 单色，没有色相变化。
    /// 系统横条里的图标就是纯色（夜间纯白、日间近黑），不透明。
    /// </summary>
    private void ApplyIconBrush()
    {
        var brush = new SolidColorBrush(_textColor);
        brush.Freeze();
        IconGlyph.Foreground = brush;
    }

    /// <summary>仅供自检：整只图标的当前不透明度（字形时代恒为 1）。</summary>
    internal double IconOpacityForTest => IconGlyph.Opacity;

    /// <summary>仅供自检：当前画出来的字形（必须是 Windows 的 E706）。</summary>
    internal string IconGlyphForTest => IconGlyph.Text;

    /// <summary>仅供自检：图标用的字体（必须是 Segoe Fluent Icons）。</summary>
    internal string IconFontForTest => IconGlyph.FontFamily.Source;

    /// <summary>仅供自检：太阳当前的缩放倍数。</summary>
    internal double IconScaleForTest => _iconScale.ScaleX;

    /// <summary>仅供自检：字形的画刷（查它是不是单色、有没有跟着主题走）。</summary>
    internal Brush IconGlyphBrushForTest => IconGlyph.Foreground;

    /// <summary>仅供自检：图标里还有没有手绘的线。</summary>
    internal bool HasHandDrawnRaysForTest
    {
        get
        {
            foreach (var child in IconBox.Children)
                if (child is not TextBlock) return true;

            return false;
        }
    }

    /// <summary>
    /// 仅供自检：图标里有没有"模糊光晕"这类元素。
    /// 用户明确不要光晕（径向渐变 / 模糊）—— 图标只能是字形本身。
    /// </summary>
    internal bool HasSoftGlowForTest =>
        IconBox.Effect != null || IconGlyph.Effect != null;

    /// <summary>仅供自检：图标墨迹相对卡片左上角的位置（DIP）。</summary>
    internal (double Left, double Top, double Width, double Height) IconInkForTest()
    {
        double slotLeft = OsdPlacement.IconLeftDip;
        double slotTop = (OsdPlacement.CardHeightDip - OsdPlacement.IconSlotDip) / 2;
        double ink = OsdPlacement.IconGlyphSizeDip * _iconScale.ScaleX;
        double pad = (OsdPlacement.IconSlotDip - ink) / 2;

        return (slotLeft + pad, slotTop + pad, ink, ink);
    }
}
