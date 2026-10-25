using System;
using System.Runtime.InteropServices;
using CapsLockPlus.Core.Interop;

namespace CapsLockPlus.Core.Windows;

/// <summary>横条在**物理像素**上的矩形（SetWindowPos 用）。</summary>
internal readonly record struct OsdRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>目标显示器的工作区（物理像素）与它的缩放倍率。</summary>
internal readonly record struct OsdWorkArea(
    IntPtr Monitor, int Left, int Top, int Right, int Bottom, double DpiScale)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>
/// 亮度横条的位置与尺寸计算。**和窗口、和 WPF 都无关的纯函数**，
/// 所以能在自检里用任意"分辨率 + 缩放"组合跑断言。
///
/// ── 为什么要这么写 ─────────────────────────────────────────────
///
/// 系统音量横条的量纲是 **DIP（96 DPI 基准的逻辑像素）**，不是屏幕百分比：
/// 它固定 192×48 DIP，所以在 100% 的屏上是 192×48 像素，在 200% 的屏上是
/// 384×96 像素 —— 占屏比例随分辨率变，但**物理观感**（几厘米宽）在各机器上
/// 一致。这正是"不同电脑看起来一样"的正确含义。
///
/// 因此本横条的做法是：
///   · 尺寸用 DIP 定义（192×48 + 四周 12 的阴影余量），
///     物理像素 = DIP × **目标显示器**的缩放；
///   · 位置用**工作区的相对关系**定义（水平居中、下沿离工作区底 14 DIP），
///     而不是写死屏幕坐标 —— 任务栏多高、屏幕多大、缩放多少都会自动跟上；
///   · 缩放取自目标显示器（GetDpiForMonitor），不是"窗口当前所在显示器"，
///     否则多屏不同缩放时会拿 A 屏的倍率去算 B 屏的像素。
///
/// ⚠ 为什么不按"屏幕宽度的百分比"来做：那样 4K 上横条会大得离谱、
///   1366×768 上小得看不清，反而和系统那条对不上。系统是按物理尺寸，
///   我们只能跟它同量纲。
/// </summary>
internal static class OsdPlacement
{
    /// <summary>卡片宽度（DIP）。实测系统横条 192×47（384×94 物理像素 @200%）。</summary>
    public const double CardWidthDip = 192;

    /// <summary>
    /// 卡片高度（DIP）。实测 **47**，不是 48。
    ///
    /// 2026-10-25 在 3200×2000 @200% 的笔记本上重新量了一遍系统横条：
    /// 亚像素边界 677.50 / 771.50，正好 94 px = 47.0 DIP；宽度 384 px = 192.0 DIP。
    /// 旧值 48 是当年把"384×94"里的 94 四舍五入成 48 得来的 ——
    /// 少算没关系，多算 1 DIP 会让我们比系统那条高 2 个像素，
    /// 并排时上面那条边明显压着系统横条的边。
    /// </summary>
    public const double CardHeightDip = 47;

    /// <summary>
    /// 卡片四周留给阴影的余量（DIP）。
    /// 窗口必须比卡片大一圈，否则 DropShadowEffect 会被窗口边界裁掉。
    /// </summary>
    public const double ShadowPadDip = 12;

    /// <summary>
    /// 卡片下沿到**工作区**底边的距离（DIP）。
    /// 实测系统横条：200% 下距工作区底边 28 物理像素 = 14 DIP。
    ///
    /// 2026-10-25 复验（把任务栏临时改成"常显"，工作区底边从 2000 抬到 1904）：
    /// 系统横条跟着上移，卡片仍在 1782..1875，距工作区底边还是 28 px。
    /// 所以它贴的是**工作区**，不是屏幕底 —— 这一点由
    /// <see cref="Compute"/> 直接吃 rcWork 保证。
    /// </summary>
    public const double BottomGapDip = 14;

    /* ── 卡片**内部**的版式（全部 DIP） ──────────────────────────────
     *
     * 同一批截图（384×94 px @200%，亮度横条）上量出来的，量法是把卡片内部
     * 和卡片底色做差、按列找连通区间，再换算成 DIP：
     *
     *   · 图标墨迹：x 14.0~26.0、y 17.5~29.5（100% 档）→ 中心 (20.0, 23.5)
     *   · 进度条：  x 42.0~152.0、y 21.5~25.5 → 宽 110、高 4、圆角 2（胶囊）
     *   · 数字：    右边缘 179.0（"50" x 164.5~179.0，"100" x 158.5~178.5）
     *
     * 于是 12.25 + 16 + 13.75 + 110 + 27 + 13 = 192，横条宽度分毫不差。
     * 注意进度条宽度**不随数字位数变化**：100% 时系统那条仍是 42.0~152.0，
     * 数字只是往左长，右边距恒为 13。
     */
    public const double CardRadiusDip = 7.5;

    /// <summary>图标槽宽度（DIP）。字形绕槽中心缩放，槽本身不变。</summary>
    public const double IconSlotDip = 16;

    /// <summary>
    /// 图标槽的左边距（DIP）。
    ///
    /// 12.25 让槽落在 12.25~28.25 → 槽中心 20.25，而系统图标墨迹
    /// （x 14.0~26.0）的中心是 20.0。差 0.25 是有意留的：E706 的墨迹
    /// 在 em 里略微偏左，居中进槽后墨迹中心正好落在 20.0。
    /// </summary>
    public const double IconLeftDip = 12.25;

    /// <summary>图标槽到进度条的间距（DIP）：槽右边 28.25 → 进度条起点 42.0。</summary>
    public const double IconGapDip = 13.75;

    /// <summary>进度条高度（DIP）。系统那条是 8 物理像素 @200%。</summary>
    public const double TrackHeightDip = 4;

    /// <summary>进度条宽度（DIP）。系统那条是 220 物理像素 @200%。</summary>
    public const double TrackWidthDip = 110;

    /// <summary>
    /// 数字列宽度（DIP）。右边界 179.0，与系统那条的数字墨迹右缘完全重合；
    /// 27 也刚好塞得下三位数（"100" 墨迹 20 DIP）。
    /// </summary>
    public const double ValueColumnDip = 27;

    /// <summary>卡片右内边距（DIP）：数字列的右边界 179 → 离卡片右边 13。</summary>
    public const double RightPadDip = 13;

    /// <summary>
    /// 内容整体相对卡片几何中心的偏移量（DIP，正数 = 底边距 = 内容上移）。
    ///
    /// **取 0**：逐元素量过系统那条（3200×2000 @200%，100% 档）——
    /// 图标墨迹 y 17.5~29.5、进度条 y 21.5~25.5，中心都是 23.5，
    /// 正好是 47 DIP 卡片的几何中心。所以内容就是纯居中，不用抬。
    /// （早先误以为系统把内容抬了 0.5，那是在 71 DIP 的离屏位图上量的，
    ///  离屏渲染少了阴影 padding，坐标系和真机差半像素。）
    /// </summary>
    public const double ContentLiftDip = 0;

    /// <summary>
    /// 数字字号（DIP）。系统那条的 "56" 墨迹是 27×20 px @200%，
    /// 用 **Segoe UI 14 DIP** 画出来正好也是 27×20（像素级拟合 MSE 最小）。
    ///
    /// ⚠ 字号对了还不够，**字体也必须指定**：本应用的全局 UI 字体是
    /// "Microsoft YaHei UI"，它的数字比 Segoe 宽一号（14 DIP 会画出 32×22），
    /// 之前横条的数字就是这么"胖"起来的。所以这里显式指定 Segoe。
    /// </summary>
    public const double ValueFontSizeDip = 14;

    /// <summary>
    /// 数字用的字体。系统横条的数字是 **Segoe UI Variable**（Win11 的界面字体），
    /// 不是经典 Segoe UI，更不是界面里那套中文字体。
    ///
    /// 这个区别肉眼看不出来，但量得出来：28 px 下经典 Segoe UI 的 "1" advance 是
    /// 15 px，Segoe UI Variable 是 11 px。系统那条 "100" 里"1"到第一个"0"的
    /// 墨迹间距实测 6.0 DIP（= 12 px），只有 Variable 对得上 —— 用经典 Segoe UI
    /// 会画出 13 px，"100" 整体胖 1 DIP。
    ///
    /// Display 排在 Text 前面也是量出来的：Text 光学尺寸下 "1" 再窄半像素，
    /// 三位数会整体左移 0.5 DIP；用 Display 时 "100" 的三段墨迹
    /// （158.5~162.5 / 164.5~171.0 / 172.0~178.5）与系统逐像素重合。
    ///
    /// 后面挂经典 Segoe UI 和中文兜底：前者防老系统没有 Variable，
    /// 后者防数字旁边混进别的字符时变豆腐块。
    /// </summary>
    public const string ValueFontFamilyName =
        "Segoe UI Variable Display, Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI";

    /// <summary>
    /// 数字基线相对图标/进度条的偏移（DIP，底边距 = 数字下移）。
    ///
    /// 系统那条里数字**比图标和进度条低 0.5 DIP**：图标/进度条中心 23.5，
    /// 数字墨迹 y 19.0~29.0、中心 24.0（实测 100% 档）。
    /// Segoe 的行框含下伸部，垂直居中时墨迹本来就略偏上，
    /// 这里用一个 0.5 DIP 的下边距把行框压矮，正好把墨迹压到 24.0。
    /// </summary>
    public const double ValueLiftDip = 0.5;

    /// <summary>
    /// 太阳字形的字号（DIP）。E706 的墨迹正好铺满一个 em，所以字号就是墨迹高度。
    ///
    /// 取 12 的依据是实测（3200×2000 @200%，逐档抓图求墨迹外框）：
    /// 系统那条在 100% 时墨迹 24×24 px = 12×12 DIP，0% 时 18×17 px。
    /// 太阳是满 em 字形 → 字号 12 DIP 画出来刚好 24 px，分毫不差。
    /// （13 DIP 会画出 26 px，比系统大一圈。）
    /// </summary>
    public const double IconGlyphSizeDip = 12;

    /// <summary>
    /// 0% 时太阳缩到满格的几成。系统调亮度时太阳会跟着变大变小。
    ///
    /// 实测系统：0% 墨迹高 17 px、100% 墨迹高 24 px → 17/24 = 0.708，
    /// 取 0.70（24 × 0.70 = 16.8 ≈ 17 px），中间档 50% 得 20.4 ≈ 20 px，
    /// 与系统实测的 20 px 一致。缩放是**关于亮度线性的**（不是分档跳变）。
    /// </summary>
    public const double IconMinScale = 0.70;

    /* 极小屏保护：正常显示器永远不会触发。
     * 存在的意义是别在 640×480 这种屏上让横条横跨大半个屏幕
     * （192 DIP 在 100% 下就是 192 px）。真触发时按同一比例缩，
     * 长宽比不变。 */
    private const double MaxWorkWidthRatio = 0.80;
    private const double MaxWorkHeightRatio = 0.45;

    /// <summary>把"缩放值"洗成一个能用的数（0 / 非法值都当 1.0）。</summary>
    public static double EffectiveScale(double dpiScale) =>
        dpiScale > 0.05 && !double.IsNaN(dpiScale) ? dpiScale : 1.0;

    /// <summary>
    /// 核心：给定工作区（物理像素）+ 目标显示器缩放，算出窗口矩形。
    ///
    ///   x = 工作区左边 + (工作区宽 − 窗口宽) / 2        ← 水平居中（相对，不是写死）
    ///   y = 工作区底边 − (14 + 48 + 12) × 缩放          ← 贴底（相对，不是写死）
    ///   w = (192 + 24) × 缩放 ,  h = (47 + 24) × 缩放   ← 尺寸按缩放走
    /// </summary>
    public static OsdRect Compute(
        int workLeft, int workTop, int workRight, int workBottom, double dpiScale)
    {
        double s = EffectiveScale(dpiScale);

        int workW = workRight - workLeft;
        int workH = workBottom - workTop;
        if (workW <= 0 || workH <= 0) return new OsdRect(0, 0, 0, 0);

        double wDip = CardWidthDip + ShadowPadDip * 2;   // 216
        double hDip = CardHeightDip + ShadowPadDip * 2;  // 71

        // 极小屏保护（见上面的常量注释），正常分辨率下 fit == 1，不改变任何值
        double fit = Math.Min(1.0, Math.Min(
            workW * MaxWorkWidthRatio / (wDip * s),
            workH * MaxWorkHeightRatio / (hDip * s)));
        s *= fit;

        int w = (int)Math.Round(wDip * s);
        int h = (int)Math.Round(hDip * s);

        int x = workLeft + (workW - w) / 2;
        int y = workBottom - (int)Math.Round((BottomGapDip + CardHeightDip + ShadowPadDip) * s);

        // 兜底：万一算出来跑到工作区外面（超小屏 + 极端缩放），贴住上边
        if (y < workTop) y = workTop;
        if (x < workLeft) x = workLeft;

        return new OsdRect(x, y, w, h);
    }

    /// <summary>
    /// 横条该出现在哪块屏：**前台窗口所在的那块** → 鼠标所在 → 主屏。
    ///
    /// 为什么不是永远主屏：笔记本外接显示器时，人在外接屏上干活，
    /// 横条跑到笔记本屏上就成了"没反应"。前台窗口拿不到（锁屏、
    /// 桌面获得焦点）就退回鼠标位置，再退回主屏。
    /// </summary>
    public static bool TryGetTargetWorkArea(IntPtr fallbackWindow, out OsdWorkArea area)
    {
        area = default;

        IntPtr mon = IntPtr.Zero;

        try
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero)
                mon = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST);
        }
        catch { /* 取不到就往下走 */ }

        if (mon == IntPtr.Zero)
        {
            try
            {
                if (NativeMethods.GetCursorPos(out var pt))
                    mon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
            }
            catch { }
        }

        if (mon == IntPtr.Zero && fallbackWindow != IntPtr.Zero)
        {
            try
            {
                mon = NativeMethods.MonitorFromWindow(
                    fallbackWindow, NativeMethods.MONITOR_DEFAULTTONEAREST);
            }
            catch { }
        }

        if (mon == IntPtr.Zero) return false;

        var mi = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return false;

        double dpi = MonitorScale(mon, fallbackWindow);

        area = new OsdWorkArea(mon,
            mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom, dpi);

        return area.IsValid;
    }

    /// <summary>
    /// 目标显示器的缩放。优先 GetDpiForMonitor（问的是**这块屏**），
    /// 失败退 GetDpiForWindow（问的是窗口所在屏），再失败 1.0。
    /// </summary>
    private static double MonitorScale(IntPtr monitor, IntPtr fallbackWindow)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI,
                                               out uint dx, out _) == 0 && dx > 0)
                return dx / 96.0;
        }
        catch { /* Shcore 不可用（Win7 之类） */ }

        try
        {
            if (fallbackWindow != IntPtr.Zero)
            {
                uint d = NativeMethods.GetDpiForWindow(fallbackWindow);
                if (d > 0) return d / 96.0;
            }
        }
        catch { }

        return 1.0;
    }

    /// <summary>仅供自检：卡片（不含阴影余量）的物理尺寸。</summary>
    public static (int Width, int Height) CardSizeForTest(OsdRect r)
    {
        double s = r.Width / (CardWidthDip + ShadowPadDip * 2);
        return ((int)Math.Round(CardWidthDip * s), (int)Math.Round(CardHeightDip * s));
    }
}
