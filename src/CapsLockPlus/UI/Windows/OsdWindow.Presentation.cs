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
/// 显示流程：定位、激活、淡入淡出、图标补间。与 OsdWindow 同属 partial 类。
/// </summary>
public partial class OsdWindow
{

    private void Apply(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);

        // 动画起点 = 用户此刻看到的样子（连按时会接着上一帧走）
        double from = CardView.IconProgress;

        _percent = percent;
        CardView.SetPercent(percent);
        ApplyPalette();

        StartIconAnimation(from, percent / 100.0);
    }

    /// <summary>
    /// 把太阳的明暗从 <paramref name="from"/> 推到 <paramref name="to"/>（0~1）。
    ///
    /// 用 ease-out（1-(1-t)²）而不是线性：亮度映射本身是线性的（用户要求），
    /// 但**时间曲线**缓出更像"亮起来"，而且和横条本身的淡入用同一条曲线。
    /// </summary>
    private void StartIconAnimation(double from, double to)
    {
        _iconTimer?.Stop();
        _iconTimer = null;

        // 差距小于半个百分点：肉眼看不出来，不值得每帧重渲染一张位图
        if (Math.Abs(to - from) < 0.005)
        {
            CardView.SetIconProgress(to);
            return;
        }

        CardView.SetIconProgress(from);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };

        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / IconAnimMs);
            double eased = 1 - (1 - t) * (1 - t);

            CardView.SetIconProgress(from + (to - from) * eased);
            RepaintBandForIcon();

            if (t >= 1)
            {
                timer.Stop();
                if (ReferenceEquals(_iconTimer, timer)) _iconTimer = null;
                CardView.SetIconProgress(to);
                RepaintBandForIcon();      // 最后一步补齐，别停在 t=0.98 那一帧
            }
        };

        _iconTimer = timer;
        timer.Start();
    }

    /// <summary>立即结束补间，把图标停在当前档位的终值（离屏渲染/收尾用）。</summary>
    private void SettleIcon()
    {
        _iconTimer?.Stop();
        _iconTimer = null;
        CardView.SetIconProgress(_percent / 100.0);
        _offscreen?.SetIconProgress(_percent / 100.0);
    }

    private void Present()
    {
        bool wasFadingOut = _fadingOut;
        _fadingOut = false;

        if (_band != null)
        {
            PresentBand(wasFadingOut);
            return;
        }

        bool firstShow = !IsVisible;
        if (firstShow)
        {
            // 先透明再 Show：否则会看到它在旧位置闪一下
            Opacity = 0;
            Show();
        }

        /* 定位必须在 Show 之后：只有拿到 HWND 才知道窗口在哪块屏、
         * 那块屏的 DPI 是多少。顺序反了就会用系统 DPI 去算，
         * 在 200% 缩放的机器上直接算到屏幕外。 */
        Place();
        if (firstShow) RaiseToTop();
        if (firstShow) LogGeometry("首次显示");

        if (firstShow || wasFadingOut)
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(FadeInMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });

        if (firstShow)
        {
            // 动画启动后的实际状态（排查"窗口在但看不见"时全靠这两行）
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                CoreLog.Info($"OSD 淡入后：opacity={Opacity:F2} visible={IsVisible} "
                             + $"实际={Width:F0}x{Height:F0} 卡片={CardView.ActualWidth:F0}x{CardView.ActualHeight:F0}")));
        }

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(
            HoldOverrideMs > 0 ? HoldOverrideMs : HoldMs);
        _hideTimer.Start();

        if (!_raiseTimer.IsEnabled) _raiseTimer.Start();
    }

    private void StartFadeOut()
    {
        if (_fadingOut) return;
        _fadingOut = true;

        /* 停掉图标补间：横条正要淡出，再每帧重渲染 + 贴一张位图
         * 只会和淡出抢同一块 DIB（band 路径），让最后 120ms 发涩。
         * 补间本来就是 150ms 的短动画，正常早已结束；这只是收尾保险。 */
        _iconTimer?.Stop();
        _iconTimer = null;

        if (_band != null)
        {
            FadeBand(_bandAlpha, 0, FadeOutMs, onDone: () =>
            {
                // 淡出途中又被叫醒的话，_fadingOut 已被 Present 置回 false
                if (!_fadingOut) return;
                _band?.Hide();
                _bandVisible = false;
            });
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(FadeOutMs));
        fade.Completed += (_, _) =>
        {
            // 淡出途中又被叫醒的话，_fadingOut 已被 Present 置回 false
            if (!_fadingOut) return;
            _raiseTimer.Stop();
            Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// 水平居中、卡片下沿贴工作区底边 14 DIP（和系统那条实测一致）。
    ///
    /// **全程用物理像素 SetWindowPos**，不走 WPF 的 Left/Top：
    /// WPF 的 Left/Top 是"窗口所在显示器 DPI 下的 DIP"，
    /// <c>SystemParameters.WorkArea</c> 是"系统 DPI 下的 DIP"，
    /// 200% 缩放下这两个坐标系差一倍，按 DIP 算会把横条送到屏幕外（踩过）。
    ///
    /// 换台机器（不同分辨率 / 不同缩放）也不会错位，因为：
    ///   · 尺寸和间距都是 **DIP**，物理像素 = DIP × 目标显示器的缩放；
    ///   · 位置是**工作区的相对关系**（居中 + 贴底），不是写死的坐标。
    /// 具体换算见 <see cref="OsdPlacement.Compute"/>，那里是纯函数，
    /// 自检用一堆"分辨率 × 缩放"的组合跑过断言。
    ///
    /// 位置没变就不重复调 SetWindowPos：连按时这个方法每 45ms 跑一次，
    /// 多余的调用会让 DWM 反复重组图层，看起来像"横条在抖"。
    /// </summary>
    private void Place()
    {
        // 高带路径自己算位置（PresentBand → TryComputeRect），这里只管 WPF 窗口
        if (_band != null) return;

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (!TryComputeRect(out var r, out double scale)) return;

            var key = (r.X, r.Y, r.Width, r.Height, Math.Round(scale, 3));
            if (_placed == key) return;
            _placed = key;

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            /* 搬完再顶一次。带坐标的这次 SetWindowPos 虽然给了 HWND_TOPMOST，
             * 但那只能保证"在所有非 topmost 之上"，越不过同为 topmost 的窗口
             * （任务栏、播放器悬浮条）—— 详见 RaiseToTop 的注释。 */
            RaiseToTop();
        }
        catch (Exception e)
        {
            CoreLog.Warn("OSD 定位失败：" + e.Message);
        }
    }

    /// <summary>
    /// 重提升时依次使用的 hWndInsertAfter。**两步缺一不可**：
    ///
    ///   ① <c>HWND_TOPMOST</c>：只用来**保住 topmost 样式**（Show/Hide、外壳操作
    ///      可能把 WS_EX_TOPMOST 摘掉）。它在**已经 topmost** 的窗口之间
    ///      **不改变相对次序** —— 所以任务栏顶上来之后，光发它永远抢不回来。
    ///   ② <c>HWND_TOP</c>：这才是"放到 Z 序最顶"。窗口已经是 topmost，
    ///      于是它落在 topmost 组的最前面，也就是任务栏之上。
    ///
    /// 实测（tools/test_osd_topmost.py，真任务栏）：只发 ① 时，任务栏压上来
    /// 之后连发一秒 Z 序纹丝不动；补上 ② 立刻回到最前。
    ///
    /// 为什么单独抽成数组：自检拿它做断言，防的是"把两步简化成一步
    /// HWND_TOPMOST"这种看起来等价、实际会让 bug 复发的重构。
    /// </summary>
    internal static readonly IntPtr[] RaiseInsertAfterForTest =
    {
        NativeMethods.HWND_TOPMOST,
        NativeMethods.HWND_TOP,
    };

    /// <summary>
    /// 提到**整个 Z 序的最前面**（高于任务栏、高于其它 topmost 窗口）。
    ///
    /// 见 <see cref="RaiseInsertAfterForTest"/> 里两步的分工说明。
    ///
    /// 不发 WM_WINDOWPOSCHANGING（SWP_NOSENDCHANGING）：这个窗口自己会响应
    /// 位置变化，重提升纯属"重排"，没必要惊动消息链。
    /// </summary>
    private void RaiseToTop()
    {
        // band 16 高于任务栏(1)与开始菜单(6)，同带内也没有竞争者，不用抢
        if (_band != null) return;

        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !IsVisible) return;

            const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE
                             | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSENDCHANGING;

            foreach (IntPtr insertAfter in RaiseInsertAfterForTest)
                NativeMethods.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, flags);
        }
        catch (Exception e)
        {
            CoreLog.Warn("OSD 置顶失败：" + e.Message);
        }
    }

    /// <summary>
    /// 加上"不激活 + 点击穿透 + 不进 Alt+Tab"三个扩展样式。
    ///
    /// WPF 的 ShowActivated=False 只影响第一次 Show，
    /// 后续被别的窗口挤压时仍可能被激活；直接改扩展样式才是硬保证。
    /// </summary>
    private void ApplyNoActivateStyle()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_NOACTIVATE
                  | NativeMethods.WS_EX_TRANSPARENT
                  | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
        }
        catch (Exception e)
        {
            CoreLog.Warn("OSD 样式设置失败：" + e.Message);
        }
    }
}