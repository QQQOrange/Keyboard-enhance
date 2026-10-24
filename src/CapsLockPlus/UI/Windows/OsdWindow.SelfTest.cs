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
/// 仅供自检的钩子：不参与运行时逻辑，只把内部状态暴露给断言。
/// </summary>
public partial class OsdWindow
{

    /// <summary>仅供离屏渲染：直接把内容摆好，不走显示流程。</summary>
    internal void PrepareForRender(int percent)
    {
        Apply(percent);

        /* 补间要立刻收尾：截图拍的是"这一档长什么样"，
         * 不是"刚开始过渡的那个瞬间"。 */
        SettleIcon();

        _hideTimer.Stop();
        _raiseTimer.Stop();
        _fadingOut = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    /// <summary>
    /// 仅供自检：用**和真实按键完全一样**的路径（Present）显示出来，但不自动消失。
    ///
    /// 为什么不能摆到屏幕外去测：窗口挪到 (-4000,-4000) 之后，Z 序的表现和
    /// 真实位置**不一样**（实测：屏幕外时只发 HWND_TOPMOST 也能"看起来"抢回
    /// 最前，一放到屏幕底部就复现不出问题）。这种"在测试环境里自己好了"的
    /// 用例比没有还糟，所以宁可让横条在自检时真亮一下。
    /// </summary>
    internal void ShowForTest(int percent)
    {
        int old = HoldOverrideMs;
        HoldOverrideMs = 20_000;      // 别在断言跑完之前自己淡出
        try
        {
            Apply(percent);
            Present();
        }
        finally
        {
            HoldOverrideMs = old;
        }
    }

    /// <summary>仅供自检：窗口句柄（高带路径给 band 宿主，老路径给 WPF 窗口）。</summary>
    internal IntPtr HandleForTest =>
        _band?.Handle ?? new WindowInteropHelper(this).Handle;

    /// <summary>仅供自检：当前走的是哪条路径（true = band 16 分层窗口）。</summary>
    internal bool UsesBandSurfaceForTest => _band != null;

    /// <summary>仅供自检：band 宿主实际所在的带（老路径返回 0）。</summary>
    internal uint BandForTest => _band?.BandForTest ?? 0;

    /// <summary>仅供自检：按指定缩放渲染一张卡片位图（走的是真实渲染路径）。</summary>
    internal RenderTargetBitmap RenderForTest(int percent, double scale) => Render(percent, percent / 100.0, scale);

    /// <summary>仅供自检：太阳此刻的明暗进度（0~1，动画途中是中间值）。</summary>
    internal double IconProgressForTest => CardView.IconProgress;

    /// <summary>仅供自检：band 路径为重画图标贴过几次位图。</summary>
    internal int IconRepaintsForTest { get; private set; }

    /// <summary>仅供自检：补间定时器是否还在跑。</summary>
    internal bool IconTimerRunningForTest => _iconTimer != null;

    /// <summary>
    /// 仅供自检：开/关"周期性置顶"。
    ///
    /// 验证置顶需要一个"横条已经被压下去"的初始状态 —— 这个状态只能趁
    /// 重提升没跑的时候制造出来，所以先关掉定时器，压完再打开。
    /// </summary>
    internal bool RaiseTimerForTest
    {
        get => _raiseTimer.IsEnabled;
        set { if (value) _raiseTimer.Start(); else _raiseTimer.Stop(); }
    }

    /// <summary>仅供自检：收尾。</summary>
    internal void CloseForTest()
    {
        _raiseTimer.Stop();
        _hideTimer.Stop();
        _bandFadeTimer?.Stop();
        try { Close(); } catch { /* 自检里关不掉不算失败 */ }
    }

    /// <summary>仅供自检：百分比 → 进度条填充比例。</summary>
    public static double ScaleForTest(int percent) => Math.Clamp(percent, 0, 100) / 100.0;

    /// <summary>仅供自检：卡片下沿离工作区底边多少 DIP。</summary>
    internal static double CardBottomGapForTest => BottomGap;

    /// <summary>仅供自检：卡片**实际生效**的圆角（XAML 可能被单独改过）。</summary>
    internal double CardRadiusForTest => CardView.Radius;

    /// <summary>仅供自检：当前系统是浅色还是深色模式。</summary>
    internal static bool SystemLightForTest => IsSystemLightTheme();

    /// <summary>
    /// 仅供自检：横条（窗口）应该出现在屏幕哪个位置。
    ///
    /// 阴影留白不参与定位 —— 否则改了阴影大小横条会跟着漂。
    ///
    /// 参数按 **96 DPI 基准**给（1 单位 = 1 像素 = 1 DIP），
    /// 所以返回的就是"100% 缩放下会落在哪个像素"。
    /// 真实运行时由 <see cref="OsdPlacement.Compute"/> 乘上目标显示器的缩放。
    ///
    /// 基准就是**工作区底边**，不再考虑任务栏矩形：
    /// 任务栏固定时 rcWork 已经扣掉它；自动隐藏时 rcWork 等于整屏，
    /// 而实测系统横条此时照样贴着屏幕底（28 px），并不会让开任务栏。
    /// </summary>
    public static (double Left, double Top) PositionForTest(Rect workArea)
    {
        var r = OsdPlacement.Compute(
            (int)workArea.Left, (int)workArea.Top,
            (int)workArea.Right, (int)workArea.Bottom, 1.0);
        return (r.X, r.Y);
    }

    /// <summary>仅供自检：按明暗取整套配色（十六进制，便于断言）。</summary>
    internal static (string Card, string Text, string Track, string Fill) PaletteForTest(bool light)
    {
        Color card = light ? LightCard : DarkCard;
        Color text = light ? LightText : DarkText;
        Color track = light ? LightTrack : DarkTrack;
        Color fill = ReadAccent(light);

        return ($"#{card.R:X2}{card.G:X2}{card.B:X2}",
                $"#{text.R:X2}{text.G:X2}{text.B:X2}",
                $"#{track.R:X2}{track.G:X2}{track.B:X2}",
                $"#{fill.R:X2}{fill.G:X2}{fill.B:X2}");
    }
}