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
/// band 16 宿主路径：建宿主、贴位图、淡入淡出。与 OsdWindow 同属 partial 类。
/// </summary>
public partial class OsdWindow
{

    /// <summary>
    /// 选定横条的宿主：优先 **band 16 的分层窗口**（能压过开始菜单），
    /// 不可用时退回本窗口 + 周期重提升（能压过任务栏，但压不过开始菜单）。
    ///
    /// 只尝试一次：band 是系统能力，运行期间不会变；每次显示都试一遍
    /// 只会让按键路径多背一次 P/Invoke。
    /// </summary>
    private void EnsureSurface()
    {
        if (_surfaceTried) return;
        _surfaceTried = true;

        /* 逃生口：某些系统上 band 16 的窗口会挡住游戏的独占全屏，
         * 或者用户就是不想要"浮在开始菜单之上"。设 CAPSLOCKPLUS_OSD_BAND=0
         * 即可退回原来的 WPF 窗口（横条仍压得住任务栏，但压不过开始菜单）。
         * 自检也用它验证回退路径没被改坏。 */
        string? forced = Environment.GetEnvironmentVariable("CAPSLOCKPLUS_OSD_BAND")?.Trim().ToLowerInvariant();
        if (forced is "0" or "off" or "false")
        {
            CoreLog.Info("OSD 宿主：按 CAPSLOCKPLUS_OSD_BAND 要求使用普通 WPF 窗口");
            return;
        }

        _band = OsdBandSurface.TryCreate(NativeMethods.ZBID_ABOVE_SHELL, out string error);
        if (_band != null)
        {
            CoreLog.Info($"OSD 宿主：band {NativeMethods.ZBID_ABOVE_SHELL} 分层窗口"
                         + $"（高于开始菜单的 band 6），hwnd=0x{_band.Handle.ToInt64():X}");
            return;
        }

        CoreLog.Info("OSD 宿主：回退到普通 WPF 窗口 —— " + error
                     + "；此路径能压过任务栏，但压不过开始菜单");
    }

    /// <summary>
    /// band 路径的"只重画图标"：位图是快照，属性变了不会自己刷新，
    /// 所以补间的每一帧都要重新渲染 + 贴一次。
    /// 尺寸、位置都不变，复用同一块 DIB（<see cref="OsdBandSurface.Present"/> 内部判断）。
    /// </summary>
    private void RepaintBandForIcon()
    {
        if (_band == null || !_bandVisible) return;
        if (!TryComputeRect(out var rect, out double scale)) return;

        _band.Present(Render(_percent, CardView.IconProgress, scale),
                      rect.X, rect.Y, _bandAlpha);

        IconRepaintsForTest++;
    }

    /// <summary>
    /// 高带路径的显示流程：算位置 → 渲成位图 → 贴到 band 16 的分层窗口。
    ///
    /// 和 WPF 路径的三点不同：
    ///   1. **不 Show 本窗口**（窗口只是渲染器和状态机）；
    ///   2. 不需要周期性重提升 —— band 16 天然高于 band 1 的任务栏和 band 6 的开始菜单；
    ///   3. 淡入淡出改的是位图的 SourceConstantAlpha，不重画内容。
    /// </summary>
    private void PresentBand(bool wasFadingOut)
    {
        if (_band == null) return;

        bool firstShow = !_bandVisible;
        _bandVisible = true;

        if (!TryComputeRect(out var rect, out double scale))
        {
            CoreLog.Warn("OSD 高带路径：没有可用的工作区，跳过本次显示");
            return;
        }

        var bitmap = Render(_percent, CardView.IconProgress, scale);
        byte alpha = firstShow || wasFadingOut ? (byte)0 : _bandAlpha;

        _band.Present(bitmap, rect.X, rect.Y, alpha);
        if (firstShow) LogGeometry("首次显示（band 16）");

        if (firstShow || wasFadingOut || _bandAlpha < 255)
            FadeBand(alpha, 255, FadeInMs, onDone: null);

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(
            HoldOverrideMs > 0 ? HoldOverrideMs : HoldMs);
        _hideTimer.Start();
    }

    /// <summary>
    /// band 路径的淡入淡出：每 ~16ms 改一次 SourceConstantAlpha。
    ///
    /// 为什么不用 WPF 的 Opacity 动画：窗口根本没显示，动画作用在窗口上没人看。
    /// 为什么不用 CompositionTarget.Rendering：横条只活 2 秒，定时器足够，
    /// 也不用担心帧率不稳定时抢渲染线程。
    /// </summary>
    private void FadeBand(byte from, byte to, int durationMs, Action? onDone)
    {
        _bandFadeTimer?.Stop();

        if (_band == null)
        {
            onDone?.Invoke();
            return;
        }

        if (durationMs <= 0 || from == to)
        {
            _bandAlpha = to;
            _band.SetAlpha(to);
            onDone?.Invoke();
            return;
        }

        _bandAlpha = from;
        _band.SetAlpha(from);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };

        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / durationMs);

            // 和 WPF 那条一样用 ease-out，看起来才是"弹出来"而不是匀速
            double eased = 1 - (1 - t) * (1 - t);
            byte a = (byte)Math.Round(from + (to - from) * eased);

            _bandAlpha = a;
            _band?.SetAlpha(a);

            if (t >= 1)
            {
                timer.Stop();
                if (ReferenceEquals(_bandFadeTimer, timer)) _bandFadeTimer = null;
                onDone?.Invoke();
            }
        };

        _bandFadeTimer = timer;
        timer.Start();
    }
}