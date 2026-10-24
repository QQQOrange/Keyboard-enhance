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
/// 离屏渲染与配色：把卡片渲成位图、按系统明暗取色。与 OsdWindow 同属 partial 类。
/// </summary>
public partial class OsdWindow
{

    /// <summary>
    /// 按 **Windows 系统主题**（浅色/深色）套用一整套配色。
    ///
    /// 为什么不跟本应用的主题：系统那条横条是跟着 Windows 的明暗走的，
    /// 而本应用可以单独选主题。两者不一致时，用户看到的就会是
    /// "系统浅、我们深"这种对不上。所以这里直接读系统的 AppsUseLightTheme。
    ///
    /// 缓存 5 秒：长按亮度时 Show 每秒被调几十次，没必要每次都读注册表。
    /// </summary>
    private void ApplyPalette(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - _paletteTick < AccentCacheMs) return;

        _paletteTick = now;
        bool light = IsSystemLightTheme();

        Color card = light ? LightCard : DarkCard;
        Color text = light ? LightText : DarkText;
        Color track = light ? LightTrack : DarkTrack;
        Color fill = ReadAccent(light);

        if (light != _lightPalette)
        {
            /* 明暗切换了：高带路径的位图缓存必须作废，
             * 否则下一次按键还会用旧配色渲染。 */
            _lightPalette = light;
            _cacheKey = (-1, -1, false, 0);
        }

        _cardColor = card;
        _textColor = text;
        _trackColor = track;
        _fillColor = fill;

        CardView.SetPalette(card, text, track, fill);
        _offscreen?.SetPalette(card, text, track, fill);
    }

    /// <summary>Windows 当前是不是浅色模式。</summary>
    private static bool IsSystemLightTheme()
    {
        try
        {
            object? v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", null);

            if (v is int i) return i != 0;
        }
        catch (Exception e)
        {
            CoreLog.Warn("读取系统明暗模式失败：" + e.Message);
        }

        return false;
    }

    /// <summary>
    /// 读强调色调色板：白天用"深色档"（索引 4），夜间用"浅色档"（索引 1）。
    ///
    /// HKCU\…\Explorer\Accent 的 AccentPalette 是 8 组 RGBX 共 32 字节：
    ///   0=更浅、1=浅、2/3=标准…（本机 1=#4CC2FF、4=#0067C0）。
    /// 系统横条正是按当前明暗取不同档位，这里照做；读不到就回退默认蓝。
    /// </summary>
    private static Color ReadAccent(bool light)
    {
        try
        {
            object? raw = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",
                "AccentPalette", null);

            if (raw is byte[] b)
            {
                int offset = light ? 16 : 4;      // 索引 4（白天）/ 1（夜间）
                if (b.Length >= offset + 3)
                    return Color.FromRgb(b[offset], b[offset + 1], b[offset + 2]);
            }
        }
        catch (Exception e)
        {
            CoreLog.Warn("读取强调色失败：" + e.Message);
        }

        return light ? Color.FromRgb(0x00, 0x67, 0xC0) : Color.FromRgb(0x4C, 0xC2, 0xFF);
    }

    /// <summary>
    /// 算出横条该占的物理像素矩形，以及**实际生效**的缩放。
    ///
    /// 两条路径共用（WPF 窗口用 SetWindowPos，高带路径交给 UpdateLayeredWindow），
    /// 所以位置逻辑只有这一份。缩放从结果反推而不是直接用 area.DpiScale：
    /// <see cref="OsdPlacement.Compute"/> 里有一条"极小屏等比缩小"的兜底，
    /// 真触发时倍率已经不是原值了，位图必须按真实倍率渲染才不会糊。
    /// </summary>
    private bool TryComputeRect(out OsdRect rect, out double scale)
    {
        rect = default;
        scale = 1.0;

        IntPtr fallback = _band?.Handle ?? IntPtr.Zero;
        if (fallback == IntPtr.Zero) fallback = new WindowInteropHelper(this).Handle;
        if (fallback == IntPtr.Zero) return false;

        if (!OsdPlacement.TryGetTargetWorkArea(fallback, out var area) || !area.IsValid)
            return false;

        rect = OsdPlacement.Compute(area.Left, area.Top, area.Right, area.Bottom, area.DpiScale);
        if (rect.Width <= 0 || rect.Height <= 0) return false;

        scale = rect.Height / (DesignHeight + ShadowPad * 2);
        return scale > 0.05;
    }

    /// <summary>
    /// 把卡片渲染成一张位图（Pbgra32，预乘 alpha —— UpdateLayeredWindow 要的就是它）。
    ///
    /// 缓存键是"百分比 + 图标进度 + 明暗 + 缩放"：静态档位只渲染一次；
    /// 补间期间 <paramref name="iconProgress"/> 每帧都不同，自然全部 miss，
    /// 这正是我们要的（不缓存动画帧）。
    /// </summary>
    private RenderTargetBitmap Render(int percent, double iconProgress, double scale)
    {
        double wDip = DesignWidth + ShadowPad * 2;
        double hDip = DesignHeight + ShadowPad * 2;

        int pw = Math.Max(1, (int)Math.Round(wDip * scale));
        int ph = Math.Max(1, (int)Math.Round(hDip * scale));
        double rounded = Math.Round(scale, 3);
        double iconKey = Math.Round(iconProgress, 4);

        if (_cachedBitmap != null
            && _cacheKey == (percent, iconKey, _lightPalette, rounded)
            && _cachedBitmap.PixelWidth == pw
            && _cachedBitmap.PixelHeight == ph)
        {
            return _cachedBitmap;
        }

        if (_offscreen == null)
        {
            _offscreen = new OsdCard { Radius = CardRadius };
            _offscreen.SetPalette(_cardColor, _textColor, _trackColor, _fillColor);
        }

        _offscreen.Width = wDip;
        _offscreen.Height = hDip;
        _offscreen.SetPercent(percent);
        _offscreen.SetIconProgress(iconProgress);
        _offscreen.Measure(new Size(wDip, hDip));
        _offscreen.Arrange(new Rect(0, 0, wDip, hDip));
        _offscreen.UpdateLayout();

        /* dpi 必须等于 96×缩放：RenderTargetBitmap 用 dpi 把"像素"和"DIP"对上，
         * 写死 96 会让 200% 的屏上内容只有一半大（也可能糊）。 */
        double dpi = 96.0 * scale;
        var bmp = new RenderTargetBitmap(pw, ph, dpi, dpi, PixelFormats.Pbgra32);
        bmp.Render(_offscreen);
        bmp.Freeze();

        _cachedBitmap = bmp;
        _cacheKey = (percent, iconKey, _lightPalette, rounded);
        return bmp;
    }

    /// <summary>把实际尺寸 / 位置 / 缩放 / 扩展样式记一笔，出问题时能对账。</summary>
    private void LogGeometry(string when)
    {
        try
        {
            /* 高带路径下这个窗口根本没建 HWND，真正在屏幕上的是 band 宿主，
             * 日志里必须是它的句柄，否则排查时对不上号。 */
            IntPtr hwnd = _band?.Handle ?? new WindowInteropHelper(this).Handle;
            int ex = hwnd == IntPtr.Zero ? 0 : NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);

            string where = OsdPlacement.TryGetTargetWorkArea(hwnd, out var area)
                ? $"{area.Left},{area.Top} {area.Width}x{area.Height} @{area.DpiScale:F2}x"
                : "(未知)";

            var r = area.IsValid
                ? OsdPlacement.Compute(area.Left, area.Top, area.Right, area.Bottom, area.DpiScale)
                : default;
            var (cw, ch) = OsdPlacement.CardSizeForTest(r);

            CoreLog.Info($"OSD {when}：卡片={cw}x{ch}px 窗口={r.Width}x{r.Height}px "
                         + $"pos={r.X},{r.Y} 工作区={where} "
                         + $"ex=0x{ex:X8} hwnd=0x{hwnd.ToInt64():X} "
                         + $"卡片控件={CardView.ActualWidth:F1}x{CardView.ActualHeight:F1}"
                         + $" opacity={Opacity:F2}");
        }
        catch (Exception e)
        {
            CoreLog.Warn("OSD 几何信息记录失败：" + e.Message);
        }
    }
}