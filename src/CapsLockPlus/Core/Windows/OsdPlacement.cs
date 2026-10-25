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
     * 于是 12.0 + 16 + 14.0 + 110 + 27 + 13 = 192，横条宽度分毫不差。
     * （12.0/14.0 是 v4.5.25 起的拆分：图标槽中心 20.0 = 系统墨迹中心，
     *  进度条起点仍是 42.0；旧值是 12.25/13.75，见 IconLeftDip 的注释。）
     * 注意进度条宽度**不随数字位数变化**：100% 时系统那条仍是 42.0~152.0，
     * 数字只是往左长，右边距恒为 13。
     */
    public const double CardRadiusDip = 7.5;

    /// <summary>图标槽宽度（DIP）。字形绕槽中心缩放，槽本身不变。</summary>
    public const double IconSlotDip = 16;

    /// <summary>
    /// 图标槽的左边距（DIP）。
    ///
    /// **12.0**：槽落在 12.0~28.0 → 槽中心 20.0，正好是系统图标墨迹
    /// （x 14.0~26.0）的中心。
    ///
    /// 旧值 12.25 是给 E706 字形留的补偿 —— 那个字形的墨迹在 em 盒里
    /// 略微偏左，居中进槽后墨迹中心才落到 20.0。v4.5.25 起太阳改成自绘、
    /// 几何严格对称，偏左的前提不存在了；再留 0.25 就会整体右偏 0.25 DIP
    /// （200% 下 1 个物理像素，并排看能看出来）。
    /// </summary>
    public const double IconLeftDip = 12.0;

    /// <summary>
    /// 图标槽到进度条的间距（DIP）：槽右边 28.0 → 进度条起点 42.0。
    /// 比旧值大 0.25，正好抵掉 <see cref="IconLeftDip"/> 的减小，
    /// 进度条起点仍是系统实测的 42.0。
    /// </summary>
    public const double IconGapDip = 14.0;

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

    /* ── 太阳图标（自绘，几何照 Windows 11 原版） ─────────────────
     *
     * 原版是什么：Win11 亮度横条上的太阳就是 Segoe Fluent Icons 的
     * **E706「Brightness」** 字形。它是一个**空心圆环 + 8 根分离的胶囊状
     * 射线**：环和射线之间有近半个环宽的空气，射线两端是圆帽。
     *
     * 怎么量的（tools 里没有留脚本，方法记在这里以便复现）：
     *   把 C:\Windows\Fonts\SegoeIcons.ttf 的 E706 以 1024 em 字号 ×4 超采样
     *   渲染成位图，从墨迹中心沿半径按 0.5‰ 步长扫亮段，得到 em 千分比：
     *     环   内半径 188‰、外半径 250‰（环线宽 62‰）
     *     射线 内端 375‰、外端 500‰、厚度 62‰（横平竖直的四根）
     *     环→线 间隙 125‰
     *   墨迹整体正好 1.000 em（上下左右各贴 em 盒边界）。
     *
     * 换算：本版墨迹直径取 16 DIP（用户上一条要求"整体放大"，
     * 原版是 12 DIP，这里等比放大 4/3），1‰ em = 0.016 DIP：
     *     环外径 8.0、环线宽 1.0、环内径 6.0
     *     射线 6.0 → 8.0、厚 1.0
     *     间隙 2.0
     * 全都落在整数上 —— 这不是巧合，字形本身就是按 1/16 em 的网格设计的。
     *
     * ⚠ 与上一条要求的冲突怎么处理的：
     *   用户上一条要求"圆固定、放射线伸缩 + 旋转"，这一条要求"照原版"。
     *   两者只在"射线长短"上打架，所以取折中：
     *     · 圆环尺寸**恒定**，任何亮度都不变（诉求①原样保留）；
     *     · 射线**内端固定**在 6.0 DIP，环线间隙永远是 2.0 DIP ——
     *       "圆与线分离"在任何亮度下都成立，不会因为变短而重新黏上；
     *     · 射线只在外端伸缩，满档 8.0（= 原版），0% 收短到 7.4；
     *     · 旋转仍是 360°×进度。
     *
     * ⚠ 原版的对角射线比横竖射线长一点（外端 517‰ vs 500‰，
     *   内端 367‰ vs 375‰，是字形做的视觉补偿）。这里**没有**复刻：
     * 整组射线要绕圆心旋转，8 根必须一模一样，否则转过 45° 后长短互换、
     * 图标会随旋转"呼吸"。差异只有 0.27 DIP（200% 下半个像素），
     * 统一成横竖射线的值 —— 这也是 8 根里 4 根的真实值。
     */

    /// <summary>
    /// 太阳**满档**的墨迹直径（DIP）。原版 12 DIP，本版等比放大到 16
    /// （用户要求"整体图标偏小，需适当放大"），形状比例不变。
    /// </summary>
    public const double SunInkMaxDip = 16.0;

    /// <summary>
    /// 圆环的外径（DIP）。原版 250‰ em × 16 = 8.0。
    /// ⚠ 这是**墨迹**外径；WPF 的 Ellipse 描边以几何线为中心向两侧各长
    /// 半个线宽，所以控件的 Width/Height 要用 <see cref="SunRingDiameterDip"/>。
    /// </summary>
    public const double SunRingOuterDip = 8.0;

    /// <summary>圆环线宽（DIP）。原版 62‰ em × 16 ≈ 1.0。</summary>
    public const double SunRingStrokeDip = 1.0;

    /// <summary>
    /// 圆环的**几何**直径（DIP）= 外径 − 线宽 = 7.0。
    /// 描边居中：7.0 ± 0.5 → 墨迹落在 6.0~8.0 直径之间，与实测一致。
    /// </summary>
    public const double SunRingDiameterDip = SunRingOuterDip - SunRingStrokeDip;

    /// <summary>
    /// 射线**内端**（墨迹，含圆帽）离圆心的距离（DIP）：原版 375‰ × 16 = 6.0。
    /// **恒定值** —— 不管亮度多少，射线内端都不动，所以环线间隙恒为 2.0 DIP。
    /// 这正是"圆与光芒不黏连"的保证：旧版把内端画在圆**里面**（= 半径 − 0.1），
    /// 圆和线必然连成一整块。
    /// </summary>
    public const double SunRayInnerDip = 6.0;

    /// <summary>
    /// 射线**外端**（墨迹，含圆帽）离圆心的距离（DIP），满档：
    /// 原版 500‰ × 16 = 8.0 —— 与圆环外径的 2 倍半径重合，整枚图标墨迹 16 DIP。
    /// </summary>
    public const double SunRayOuterMaxDip = 8.0;

    /// <summary>
    /// 射线**外端**离圆心的距离（DIP），0% 时最短。
    ///
    /// 7.4 = 射线由满档 2.0 长收到 1.4 长（各含两端圆帽）。收得克制是**故意的**：
    /// 原版射线总共才 2 DIP 长，再往下收就成了一个个圆点，和原版的相似度掉得比
    /// 多出来的那点动感更明显。旋转（360°×进度）才是主要的运动线索。
    /// </summary>
    public const double SunRayOuterMinDip = 7.4;

    /// <summary>射线粗细（DIP）。原版 62‰ em × 16 ≈ 1.0，与圆环线宽同宽。</summary>
    public const double SunRayThicknessDip = 1.0;

    /// <summary>放射线条数：8 根，每 45° 一根，第一根指向正上方。</summary>
    public const int SunRayCount = 8;

    /// <summary>
    /// 圆环与射线之间的**净间隙**（DIP）= 射线内端 − 环外半径 = 6.0 − 4.0 = 2.0。
    /// 自检直接断言它 > 0 且等于原版比例，防止有人再把射线往里挪。
    /// </summary>
    public const double SunGapDip = SunRayInnerDip - SunRingOuterDip / 2;

    /// <summary>
    /// 满量程旋转角（度）：亮度 0% → 100% 整组放射线转过一整圈。
    ///
    /// 旋转角 = <see cref="SunTwistDeg"/> × 进度，是进度的**纯函数**：
    /// 角速度 = 360°×d(进度)/dt，亮度变化越快转得越快，停下来就静止 ——
    /// 正好是"转速与亮度变化速度联动"。纯函数还保证两个宿主
    /// （WPF 窗口 / band 16 离屏位图）同一进度画出同一帧，位图缓存不会错。
    /// </summary>
    public const double SunTwistDeg = 360.0;

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
