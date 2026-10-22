using System.Windows;
using System.Windows.Media.Animation;
using CapsLockPlus.Core;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 录制悬浮条：屏幕顶部中央一条「● 录制中 0:03.4 · 14 个事件 · 最后 A」。
///
/// 只用静态方法驱动（<see cref="ShowHud"/> / <see cref="UpdateHud"/> / <see cref="HideHud"/>），
/// 因为它的生命周期跟着"宏录制器在不在录"，不属于某一个页面。
/// </summary>
public partial class RecordingHudWindow : Window
{
    public RecordingHudWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Position();
    }

    /// <summary>
    /// 顶部居中。按**前台窗口所在显示器**的工作区算 ——
    /// 混 DPI 多屏下用 SystemParameters.WorkArea 会算到主屏，浮层就跑到别的屏幕上。
    /// </summary>
    private void Position()
    {
        UpdateLayout();

        var fg = Core.Interop.NativeMethods.GetForegroundWindow();
        if (Core.Windows.ScreenPlacement.Place(this,
                Core.Windows.ScreenPlacement.Anchor.TopCenter, marginY: 14, target: fg))
            return;

        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top + 14;
    }

    private static RecordingHudWindow? _win;

    /// <summary>显示（已在显示则忽略）。</summary>
    public static void ShowHud()
    {
        if (_win != null) return;
        try
        {
            var w = new RecordingHudWindow();
            _win = w;

            // 呼吸效果：让人一眼看出"还在录"
            var pulse = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(620))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            w.Dot.BeginAnimation(OpacityProperty, pulse);

            w.Show();
        }
        catch (Exception ex)
        {
            CoreLog.Warn("显示录制悬浮条失败：" + ex.Message);
            _win = null;
        }
    }

    /// <summary>刷新文字。每 100ms 调一次即可。</summary>
    public static void UpdateHud(long elapsedMs, int events, string lastKey)
    {
        if (_win == null) return;
        try
        {
            _win.Clock.Text = Format(elapsedMs);
            _win.Count.Text = $"{events} 个事件";
            _win.LastKey.Text = string.IsNullOrEmpty(lastKey) ? "" : $"最后 {lastKey}";
        }
        catch { /* 窗口正在关，忽略 */ }
    }

    public static void HideHud()
    {
        var w = _win;
        _win = null;
        try
        {
            w?.Dot.BeginAnimation(OpacityProperty, null);
            w?.Close();
        }
        catch { }
    }

    /// <summary>0:03.4 这种短格式（宏一般录几秒到几十秒，不需要小时位）。</summary>
    public static string Format(long ms)
    {
        if (ms < 0) ms = 0;
        long totalSeconds = ms / 1000;
        return $"{totalSeconds / 60}:{totalSeconds % 60:00}.{ms % 1000 / 100}";
    }
}
