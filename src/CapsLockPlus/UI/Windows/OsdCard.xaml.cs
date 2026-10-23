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

    /// <summary>百分比（0~100）。同时更新数字和进度条填充比例。</summary>
    public void SetPercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        ValueText.Text = percent.ToString();

        double s = percent / 100.0;

        /* 用两列星号比例（s : 1-s）而不是算像素宽度：
         * 算像素要靠 Track.ActualWidth，离屏渲染时它还是 0，
         * 进度条就会停在空状态；比例交给布局去分，什么时候设都对。 */
        FillColumn.Width = new GridLength(s, GridUnitType.Star);
        RestColumn.Width = new GridLength(1 - s, GridUnitType.Star);
    }

    /// <summary>套一整套配色（卡片/文字/槽/填充）。</summary>
    public void SetPalette(Color card, Color text, Color track, Color fill)
    {
        Card.Background = new SolidColorBrush(card);
        IconText.Foreground = new SolidColorBrush(text);
        ValueText.Foreground = new SolidColorBrush(text);
        TrackBar.Background = new SolidColorBrush(track);
        FillBar.Background = new SolidColorBrush(fill);
    }
}
