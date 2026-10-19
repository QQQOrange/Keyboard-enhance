using System.Windows;
using System.Windows.Media;

namespace CapsLockPlus.Core.Text;

/// <summary>
/// 文字度量：统一计算某个字号下文字的真实高度，用于给控件留出足够空间。
///
/// 为什么需要它：输入框高度如果写死，字号一旦变大（Qbar 字号可调到 30），
/// 文字高度就会超过内容区，表现为只显示字符的上半部分、下半部分被边框裁掉。
/// 而"字号 × 固定系数"并不可靠 —— 实测 200% DPI 下：
///   字号 21 → 文字高约 25.4px（比值 1.21）
///   字号 30 → 文字高约 38.1px（比值 1.27）
/// 系数是变化的，所以这里直接量，并统一多留一点余量。
/// </summary>
public static class TextMetrics
{
    /// <summary>输入框模板默认的上下内边距（与 TextBox.Base 保持一致）。</summary>
    public const double Padding = 6;

    /// <summary>输入框模板默认的边框厚度。</summary>
    public const double Border = 1;

    /// <summary>文字上下各自额外保留的安全余量。</summary>
    public const double Safety = 3;

    /// <summary>
    /// 单行输入框应有的最小高度。
    ///
    /// 高度必须覆盖：文字高 + 上下内边距 + 上下边框 + 上下安全余量。
    /// 只写 textH + 2×padding 是不够的 —— 那正好等于内容区高度，
    /// 文字会顶满内容区（余量 0），在整数像素对齐时仍会被裁掉一两个像素。
    /// </summary>
    public static double MinBoxHeight(double fontSize, string? text = null,
        double padding = Padding, double border = Border) =>
        MeasureHeight(fontSize, text) + 2 * (padding + border + Safety);

    /// <summary>
    /// 量出指定字号下 text 的真实高度（含行距与下降部）。
    /// 文本建议包含中文字与 g/q/y 这类带下降部的字母。
    /// </summary>
    public static double MeasureHeight(double fontSize, string? text = null)
    {
        string sample = string.IsNullOrEmpty(text) ? "汉字Agqy" : text;
        try
        {
            var family = Application.Current?.TryFindResource("Font.Ui") as FontFamily
                         ?? new FontFamily("Microsoft YaHei UI");
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            double pixelsPerDip = 1.0;
            try { pixelsPerDip = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip; }
            catch { }

            var ft = new FormattedText(sample, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, pixelsPerDip);
            return Math.Ceiling(ft.Height);
        }
        catch
        {
            // 量不出来时退回保守估算：1.35 比实测最大比值 1.27 更宽裕
            return Math.Ceiling(fontSize * 1.35);
        }
    }
}
