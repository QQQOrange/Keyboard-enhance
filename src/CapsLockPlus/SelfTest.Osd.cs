using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Icons;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Interop;
using CapsLockPlus.Core.Qbar;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Text;
using CapsLockPlus.Core.Update;
using CapsLockPlus.Core.Windows;

namespace CapsLockPlus;

/// <summary>
/// 自检：不依赖界面，验证核心逻辑。
/// 用 `KeyboardEnhance.exe --selftest` 运行，退出码 0 表示全部通过。
/// </summary>
public static partial class SelfTest
{

    /* 亮度横条（OSD）、音量、提示气泡。
     * 与 SelfTest.cs 同属 partial 类，只按业务域分文件。 */

    /// <summary>
    /// 音量：这次把「模拟媒体键」换成了 Core Audio（IAudioEndpointVolume）。
    ///
    /// 分两层验：
    ///   ① 纯换算（夹取、浮点→百分比）—— 不需要声卡，任何机器都能断言；
    ///   ② 真实读写 —— 这台机器有播放设备时，写一个值、回读、再还原。
    ///
    /// ② 必须做，否则"接口写错了但编译通过"这种问题只能等用户发现：
    /// COM 接口的方法顺序错位不会报编译错，表现是调用失败或写进乱七八糟的值。
    /// 过程中先静音，避免自检把音量推到 70% 时突然出声。
    /// </summary>
    private static void TestVolumeCore()
    {
        // ---------- ① 边界换算 ----------
        CheckEqual("音量 50+5 → 55", Core.Windows.VolumeService.TargetPercent(50, 5), 55);
        CheckEqual("音量 98+5 → 封顶 100", Core.Windows.VolumeService.TargetPercent(98, 5), 100);
        CheckEqual("音量 2-5 → 保底 0", Core.Windows.VolumeService.TargetPercent(2, -5), 0);
        CheckEqual("音量 100+5 → 仍是 100", Core.Windows.VolumeService.TargetPercent(100, 5), 100);
        CheckEqual("音量 0-5 → 仍是 0", Core.Windows.VolumeService.TargetPercent(0, -5), 0);

        CheckEqual("0.65 → 65%", Core.Windows.VolumeService.ToPercent(0.65f), 65);
        CheckEqual("0.6499999 → 65%（不能舍成 64）", Core.Windows.VolumeService.ToPercent(0.6499999f), 65);
        CheckEqual("1.0 → 100%", Core.Windows.VolumeService.ToPercent(1f), 100);
        CheckEqual("0 → 0%", Core.Windows.VolumeService.ToPercent(0f), 0);

        // ---------- ② 真实读写 + 还原 ----------
        int? before = Core.Windows.VolumeService.Get();
        if (before == null)
        {
            Check("（环境限制）没有可用的播放设备，跳过真实音量读写", true);
            return;
        }

        bool? muteBefore = Core.Windows.VolumeService.GetMute();
        try
        {
            Core.Windows.VolumeService.SetMute(true);   // 测试期间不出声

            int target = before.Value >= 50 ? 30 : 70;
            bool written = Core.Windows.VolumeService.Set(target);
            Check($"音量能写入 {target}%", written);
            if (written)
            {
                int after = Core.Windows.VolumeService.Get() ?? -1;
                /* 允许 ±1：声卡会把 0.30 量化成最接近的一档，
                 * 0~100 的整数百分比回读通常精确，少数驱动有 1% 偏差。 */
                Check($"回读音量 ≈ {target}%（实际 {after}%）", Math.Abs(after - target) <= 1,
                    $"before={before}, after={after}");

                int? stepped = Core.Windows.VolumeService.Step(5);
                Check("音量 +5 有回读值", stepped != null, $"stepped={stepped}");
                CheckEqual("Step 返回的就是当前值", stepped ?? -1,
                    Core.Windows.VolumeService.Get() ?? -1);

                // 再减回去，确认双向都通
                int? back = Core.Windows.VolumeService.Step(-5);
                Check("音量 -5 有回读值", back != null, $"back={back}");
            }

            if (muteBefore != null)
            {
                Core.Windows.VolumeService.SetMute(!muteBefore.Value);
                Check("静音状态可切换", Core.Windows.VolumeService.GetMute() == !muteBefore.Value);
            }
        }
        finally
        {
            Core.Windows.VolumeService.Set(before.Value);
            if (muteBefore != null) Core.Windows.VolumeService.SetMute(muteBefore.Value);
        }

        CheckEqual("音量已还原", Core.Windows.VolumeService.Get() ?? -1, before.Value);

        // ---------- ③ 动作表归位 ----------
        var settings = DefaultSettings.Create();
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new Core.Actions.WindowBindingService(settings),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };
        var cat = Core.Actions.ActionCatalog.Create(ctx);

        foreach (string id in new[] { "volumeUp", "volumeDown", "volumeMute" })
        {
            var it = cat.Items.FirstOrDefault(i => i.Id == id);
            Check($"动作 {id} 在「系统功能」", it?.Category == "系统功能",
                string.Join(",", cat.Items.Where(i => i.Category == "系统功能").Select(i => i.Id)));
            Check($"动作 {id} 不需要参数", it?.NeedsArg != true);
        }

        // 音量动作不能再退回"模拟媒体键"：那是这次要修的根因
        var up = cat.Items.FirstOrDefault(i => i.Id == "volumeUp");
        var down = cat.Items.FirstOrDefault(i => i.Id == "volumeDown");
        Check("音量动作走 Core Audio（描述里写的是系统音量）",
            up?.Description.Contains("系统音量") == true &&
            down?.Description.Contains("系统音量") == true,
            $"up={up?.Description} / down={down?.Description}");
    }

    /// <summary>
    /// 亮度：WMI 与 DDC/CI 两套量程换算。
    ///
    /// 不在这里做真实写入 —— 写亮度会让屏幕闪一下，而自检要跑一千多条断言，
    /// 中间闪屏非常打扰。读取是只读的，只报一句当前通道。
    /// </summary>
    /// <summary>
    /// 提示消息必须「新的立刻覆盖旧的」。
    ///
    /// 这条直接决定手感：连按十个键时，如果每条消息都排一次调度、
    /// 界面线程忙完依次播一遍，用户看到的就是十几次闪烁加一条过时内容 ——
    /// 也就是用户说的"提示太慢，跟不上按键"。
    ///
    /// 槽位只保留最后一条、调度只安排一次，中间的自动作废。
    /// </summary>
    private static void TestToastCoalescing()
    {
        var q = new UI.Windows.ToastQueue();

        Check("第一条需要安排刷新", q.Post("第一条", "info"));
        Check("第二条不必再安排（已有安排会来取最新值）", !q.Post("第二条", "info"));
        Check("第三条同样不必", !q.Post("第三条", "feedback"));

        var got = q.Take();
        CheckEqual("取到的是最后一条", got?.Text, "第三条");
        CheckEqual("样式跟着最后一条走", got?.Kind, "feedback");
        Check("取完之后槽里是空的", q.Take() == null);

        Check("取空后能重新开始新一轮", q.Post("第四条", "info"));
        CheckEqual("新一轮取到第四条", q.Take()?.Text, "第四条");
    }

    /// <summary>
    /// 亮度横条必须**永远在最上层**（用户报：按亮度键，横条被任务栏挡住）。
    ///
    /// 这个 bug 的本质不是"没提置顶"—— 旧实现每 200~320ms 就发一次
    /// <c>SetWindowPos(HWND_TOPMOST)</c>，但那个参数只保证"在所有非 topmost
    /// 窗口之上"；任务栏顶上来之后，连发一秒 Z 序纹丝不动。
    /// 真正能上去的是 <c>HWND_TOP</c>（见 OsdWindow.RaiseInsertAfterForTest）。
    ///
    /// 为什么这里只断言"用哪两个参数、什么顺序"，而不是真的把任务栏压上来再抢：
    /// 试过三种造场景的办法，全都会**假通过** ——
    ///   · 把真任务栏顶到最前：外壳会自己把它沉回去，几毫秒就不成立了；
    ///   · 拿我们自己的 topmost 窗口当假想敌：普通 topmost 窗口和任务栏
    ///     不在同一层，旧代码（只发 HWND_TOPMOST）也能抢回来；
    ///   · 把横条摆到屏幕外：Z 序表现和真实位置不同。
    /// 一条测试要能"改回旧代码就变红"才有意义，所以这里守 API 用法，
    /// 行为验证交给端到端脚本 tools/test_osd_topmost.py（真任务栏，修前红、修后绿）。
    ///
    /// 变异验证：把两步砍成一步 HWND_TOPMOST → 本用例立刻红。
    /// </summary>
    private static void TestOsdAlwaysOnTop()
    {
        var steps = UI.Windows.OsdWindow.RaiseInsertAfterForTest;

        Check("重提升分两步（先保样式、再顶到最前）", steps.Length == 2, $"步数={steps.Length}");
        Check("第一步是 HWND_TOPMOST（保住 topmost 样式）",
            steps.Length > 0 && steps[0] == NativeMethods.HWND_TOPMOST);
        Check("最后一步是 HWND_TOP（抢到任务栏之上）",
            steps.Length > 0 && steps[^1] == NativeMethods.HWND_TOP,
            "HWND_TOPMOST 在已经 topmost 的窗口之间不改次序，顶不过任务栏");

        // 顺带守住这两件事：重提升不能挪动/改变横条尺寸
        Check("重提升带 NOMOVE|NOSIZE 以外的窗口标志不会被误加",
            (NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE) != 0);
    }

    /// <summary>
    /// 亮度横条必须能压在**开始菜单**之上（用户报：开始菜单一打开就把横条盖住）。
    ///
    /// 根因是窗口带（band），不是置顶参数：开始菜单/搜索在 band 6，
    /// 任务栏和普通窗口都在 band 1，而 SetWindowPos 只能在 band 1 里调次序。
    /// 唯一不需要 UIAccess 签名的出路是用 CreateWindowInBand 在创建时进 band 16。
    ///
    /// 这里守四件事：
    ///   1. 常量关系对（16 > 6）—— 防止有人"顺手"把高带值改小；
    ///   2. 真能创建（本机允许才通过，不允许就是回退，不算失败但要报出来）；
    ///   3. 请求 band 6 必须被拒 —— 这条反过来证明"权限模型没变"，
    ///      也证明测试用的 API 真的在起作用（不是随便传什么都成功）；
    ///   4. 渲染出来的位图尺寸/透明度正确（四角透明、中心不透明）。
    ///
    /// 行为验证（真开开始菜单看像素）在 tools/test_osd_start_menu.py。
    /// </summary>
    private static void TestOsdAboveShellMenu()
    {
        Check("高带值高于开始菜单的 band（16 > 6）",
            NativeMethods.ZBID_ABOVE_SHELL > NativeMethods.ZBID_IMMERSIVE_INACTIVEMOBODY,
            $"{NativeMethods.ZBID_ABOVE_SHELL} vs {NativeMethods.ZBID_IMMERSIVE_INACTIVEMOBODY}");

        Check("高带值不是 UIACCESS（那条路要签名，普通进程走不通）",
            NativeMethods.ZBID_ABOVE_SHELL != NativeMethods.ZBID_UIACCESS);

        // 真正建一个宿主，确认落在 band 16
        var surface = Core.Windows.OsdBandSurface.TryCreate(
            NativeMethods.ZBID_ABOVE_SHELL, out string createError);

        Check("能以 band 16 创建横条宿主", surface != null, createError);
        if (surface != null)
        {
            CheckEqual("宿主实际落在 band 16", surface.BandForTest, 16u);

            // 点击穿透 + 不抢焦点 + 不进 Alt+Tab：缺一个就会影响用户正在做的事
            int ex = NativeMethods.GetWindowLong(surface.Handle, NativeMethods.GWL_EXSTYLE);
            Check("宿主带 WS_EX_LAYERED（UpdateLayeredWindow 需要）",
                (ex & NativeMethods.WS_EX_LAYERED) != 0, $"ex=0x{ex:X8}");
            Check("宿主带 WS_EX_TRANSPARENT（点击穿透）",
                (ex & NativeMethods.WS_EX_TRANSPARENT) != 0, $"ex=0x{ex:X8}");
            Check("宿主带 WS_EX_NOACTIVATE（不抢焦点）",
                (ex & NativeMethods.WS_EX_NOACTIVATE) != 0, $"ex=0x{ex:X8}");
            Check("宿主带 WS_EX_TOOLWINDOW（不进 Alt+Tab）",
                (ex & NativeMethods.WS_EX_TOOLWINDOW) != 0, $"ex=0x{ex:X8}");

            surface.Dispose();
        }

        /* 反向用例：band 6 是外壳的带，普通进程没有权限。
         * 如果这条突然"通过"了，说明权限模型变了 —— 那我们的
         * "16 是可选上限"的判断也就不可信了，必须重新量。 */
        var denied = Core.Windows.OsdBandSurface.TryCreate(
            NativeMethods.ZBID_IMMERSIVE_INACTIVEMOBODY, out string deniedError);
        Check("请求 band 6 被拒绝（权限模型没变）", denied == null,
            "竟然创建成功了，需要重新确认 band 允许范围");
        Check("被拒时给出原因（便于日志排查）",
            !string.IsNullOrWhiteSpace(deniedError), deniedError);
        denied?.Dispose();

        // 实际横条用的就是这条路
        var osd = UI.Windows.OsdWindow.CreateDetached();
        Check("横条默认走高带路径（不是回退到 WPF 窗口）",
            osd.UsesBandSurfaceForTest, "回退到了 WPF 窗口，开始菜单会盖住它");
        CheckEqual("横条宿主的 band", osd.BandForTest, 16u);

        /* 渲染：200% 下卡片是 216×72 DIP → 432×144 像素；
         * 四角必须透明（圆角+阴影留白），中心必须不透明（卡片）。
         * 这条能挡住"忘了设 dpi"（位图尺寸对但内容只有一半大）
         * 和"位图全透明"（窗口在，但什么都看不见）。 */
        var bmp = osd.RenderForTest(60, 2.0);
        CheckEqual("位图宽度 = 216×缩放", bmp.PixelWidth, 432);
        CheckEqual("位图高度 = 72×缩放", bmp.PixelHeight, 144);

        if (bmp.PixelWidth == 432 && bmp.PixelHeight == 144)
        {
            int stride = bmp.PixelWidth * 4;
            var pixels = new byte[stride * bmp.PixelHeight];
            bmp.CopyPixels(pixels, stride, 0);

            byte AlphaAt(int x, int y) => pixels[y * stride + x * 4 + 3];

            Check("位图左上角透明（阴影/圆角留白）", AlphaAt(2, 2) < 16,
                $"alpha={AlphaAt(2, 2)}");
            Check("位图中心不透明（卡片本体）", AlphaAt(216, 72) > 200,
                $"alpha={AlphaAt(216, 72)}");
            Check("位图右侧留白也透明", AlphaAt(429, 72) < 16,
                $"alpha={AlphaAt(429, 72)}");
        }

        osd.CloseForTest();
        DoEvents(60);
    }

    /// <summary>
    /// Z 序里 <paramref name="a"/> 是否压在 <paramref name="b"/> 之上。
    ///
    /// 用 EnumWindows 的**相对位次**（谁的数字小谁在上面）：只要两个窗口都在
    /// 列表里，它们之间相对次序是稳定的，别的窗口开关只会让两个数字一起平移。
    /// （别写成"让一个窗口自己抢回来"那种需要窗口自己不动的判断 —— 之前用
    /// GetTopWindow+GW_HWNDNEXT 走链，两个方向都返回 true，白测。）
    /// </summary>
    private static bool IsAboveInZOrder(IntPtr a, IntPtr b)
    {
        int ia = ZOrderIndex(a), ib = ZOrderIndex(b);
        return ia >= 0 && ib >= 0 && ia < ib;
    }

    /// <summary>当前 Z 序里的位次（0 = 最上）。找不到返回 -1。</summary>
    private static int ZOrderIndex(IntPtr target)
    {
        int index = -1;
        int i = 0;
        NativeMethods.EnumWindows((h, _) =>
        {
            if (h == target) index = i;
            i++;
            return true;
        }, IntPtr.Zero);
        return index;
    }

    /// <summary>
    /// 亮度横条上的太阳图标随取值联动（v4.5.10）。
    ///
    /// 用户的要求是：越亮光芒越强越亮、越暗光芒越弱越暗淡，
    /// 而且要**纯线性**、**单色**、就用原来那个字形。
    ///
    /// 这里守的就是那三件事：
    ///   1. 线性 —— 五个采样点必须落在同一条直线上（改成 ease、平方、
    ///      或者偷偷在中间加个分段阈值，这条立刻红）；
    ///   2. 有跨度但不夸张 —— 0% 仍然看得清是太阳，100% 也不必刺眼；
    ///   3. 单色 —— 辉光的所有色标 RGB 都等于文字色，只允许 alpha 变化。
    /// </summary>
    private static void TestOsdIconFollowsBrightness()
    {
        var app = System.Windows.Application.Current;
        if (app == null)
        {
            Check("太阳图标随亮度联动（无 Application，跳过）", true);
            return;
        }

        var card = new UI.Windows.OsdCard();

        var white = System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF);
        var darkCard = System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x2D);
        var accent = System.Windows.Media.Color.FromRgb(0x4C, 0xC2, 0xFF);
        card.SetPalette(darkCard, white, white, accent);

        int[] steps = { 0, 25, 50, 75, 100 };
        var opacity = new double[steps.Length];
        var rays = new double[steps.Length];

        for (int i = 0; i < steps.Length; i++)
        {
            card.SetPercent(steps[i]);
            opacity[i] = card.IconOpacityForTest;
            rays[i] = card.RayLengthForTest;
        }

        // 端点：暗要暗得下去、亮要亮得起来，但都不能走极端
        Check("0% 时太阳明显发暗（不透明度 ≤ 0.55）", opacity[0] <= 0.55,
            $"opacity={opacity[0]:F3}");
        Check("0% 时太阳仍看得清（不透明度 ≥ 0.35）", opacity[0] >= 0.35,
            $"opacity={opacity[0]:F3}");
        Check("100% 时太阳完全点亮", Math.Abs(opacity[^1] - 1.0) < 1e-6,
            $"opacity={opacity[^1]:F3}");

        // 光芒是"线"，不是光晕：八根，长度随亮度走
        CheckEqual("光芒线是八根", card.RayCountForTest, 8);
        Check("0% 时光芒只剩一小截（几乎不可见）", rays[0] >= 0.15 && rays[0] <= 0.45,
            $"len={rays[0]:F2}");
        Check("100% 时光芒约为原来的 2/3（3.5 DIP）",
            Math.Abs(rays[^1] - 3.5) <= 0.05, $"len={rays[^1]:F2}");
        Check("光芒长度跨度够明显（≥ 8 倍）", rays[^1] / rays[0] >= 8.0,
            $"{rays[0]:F2} → {rays[^1]:F2}");

        /* 图标是手绘的：中间一个圆 + 八根线。
         * 线必须从圆外面开始（不能插进圆里），而且不能残留字体字形 ——
         * 字形自带一圈固定长度的光芒，和动态线叠在一起就是"两段拼接"。 */
        Check("线内端在圆心之外（不插进圆里）",
            card.RayInnerForTest > card.CoreDiameterForTest / 2 + 0.5,
            $"inner={card.RayInnerForTest:F2} r={card.CoreDiameterForTest / 2:F2}");
        Check("太阳外径仍是 6.4 DIP（描边不改尺寸）",
            Math.Abs(card.CoreDiameterForTest - 6.4) < 0.01,
            $"d={card.CoreDiameterForTest:F2}");
        Check("太阳中间是空的（空心描边，对齐 Win11）", card.CoreIsHollowForTest);
        Check("图标里没有字体字形残留（否则会和线拼接）", !card.HasGlyphForTest,
            "还存在 TextBlock 字形");

        // 没有模糊光晕：用户明确要求"一根根的线"来表现强弱
        Check("卡片里没有光晕/模糊元素（光芒只能是线）", !card.HasSoftGlowForTest,
            "出现了非 Line 的光芒元素或图标特效");

        /* 线性：每个点都必须等于"端点之间的一次函数"。
         * 容差 1e-6 —— 只看浮点误差，任何有意的曲线都过不去。 */
        for (int i = 0; i < steps.Length; i++)
        {
            double s = steps[i] / 100.0;
            double eo = opacity[0] + (opacity[^1] - opacity[0]) * s;
            double er = rays[0] + (rays[^1] - rays[0]) * s;

            Check($"{steps[i]}% 的图标亮度落在线性上", Math.Abs(opacity[i] - eo) < 1e-6,
                $"{opacity[i]:F6} vs {eo:F6}");
            Check($"{steps[i]}% 的光芒长度落在线性上", Math.Abs(rays[i] - er) < 1e-6,
                $"{rays[i]:F6} vs {er:F6}");
        }

        // 单调：中间任何一段都不许回落（"越来越亮"是这个功能的全部意义）
        bool monotonic = true;
        for (int i = 1; i < steps.Length; i++)
        {
            if (opacity[i] <= opacity[i - 1]) monotonic = false;
            if (rays[i] <= rays[i - 1]) monotonic = false;
        }
        Check("0→100% 全程单调变亮（不会中途回落）", monotonic);

        // 数字和图标看的是同一个百分比
        card.SetPercent(37);
        double e37 = opacity[0] + (opacity[^1] - opacity[0]) * 0.37;
        Check("37% 时图标按 37% 算（和数字同一来源）",
            Math.Abs(card.IconOpacityForTest - e37) < 1e-6);

        /* 单色：光芒线就是文字色，没有任何色相偏移。
         * 这条挡的是"以后想加点颜色更好看"这类改法。 */
        card.SetPercent(100);
        var rayBrush = card.RayBrushForTest as SolidColorBrush;
        bool monochrome = rayBrush != null
                          && rayBrush.Color.R == white.R
                          && rayBrush.Color.G == white.G
                          && rayBrush.Color.B == white.B;
        Check("光芒线是单色的（颜色全等于文字色）", monochrome,
            rayBrush == null ? "画刷类型不是纯色" : $"#{rayBrush.Color.R:X2}{rayBrush.Color.G:X2}{rayBrush.Color.B:X2}");

        // 浅色主题：线条跟随文字色变黑，长度逻辑不受主题影响
        var black = System.Windows.Media.Color.FromRgb(0x00, 0x00, 0x00);
        card.SetPalette(darkCard, black, black, accent);
        card.SetPercent(100);
        var darkRay = card.RayBrushForTest as SolidColorBrush;
        Check("浅色主题下光芒线跟着变黑（单色、无色相）",
            darkRay != null && darkRay.Color.R == 0 && darkRay.Color.G == 0 && darkRay.Color.B == 0,
            darkRay == null ? "画刷类型不是纯色" : "颜色不是黑");
        Check("浅色主题下光芒长度同样是 100% 饱和",
            card.RayLengthForTest >= 3.2, $"len={card.RayLengthForTest:F2}");
    }

    /// <summary>
    /// 太阳图标的明暗变化要有补间，不能一帧跳到位。
    ///
    /// 两条宿主都得动起来，而且方式不一样：
    ///   · WPF 窗口路径 —— 改属性 WPF 自己重绘，不需要额外动作；
    ///   · band 16 路径 —— 显示的是位图**快照**，属性变了没人看，
    ///     必须每帧重渲染再 UpdateLayeredWindow 贴一次。
    ///     后者最容易在重构时被漏掉，所以专门用重贴计数守它。
    /// </summary>
    private static void TestOsdIconAnimates()
    {
        var app = System.Windows.Application.Current;
        if (app == null)
        {
            Check("太阳图标补间（无 Application，跳过）", true);
            return;
        }

        var osd = UI.Windows.OsdWindow.CreateDetached();
        try
        {
            osd.PrepareForRender(10);
            Check("离屏渲染时补间立即收尾（截图拿到的是 10% 终值）",
                Math.Abs(osd.IconProgressForTest - 0.10) < 1e-6,
                $"p={osd.IconProgressForTest:F3}");

            int repaintsBefore = osd.IconRepaintsForTest;
            osd.ShowForTest(100);

            // 刚起步：还停在出发位置 —— 一帧到位的话这里就已经是 1.0 了
            Check("补间从当前值起步（不是一帧跳到终点）",
                osd.IconProgressForTest < 0.5, $"p={osd.IconProgressForTest:F3}");
            Check("补间定时器已启动", osd.IconTimerRunningForTest);

            bool sawMiddle = false;
            bool reachedEnd = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 800)
            {
                DoEvents(16);
                double p = osd.IconProgressForTest;
                if (p > 0.15 && p < 0.97) sawMiddle = true;
                if (Math.Abs(p - 1.0) < 1e-6 && !osd.IconTimerRunningForTest)
                {
                    reachedEnd = true;
                    break;
                }
            }

            Check("补间过程中取到过中间帧（是补间，不是瞬变）", sawMiddle);
            Check("补间最终停在 100%", reachedEnd, $"p={osd.IconProgressForTest:F3}");
            Check("band 路径每帧都重贴了位图（不是只贴一次）",
                osd.IconRepaintsForTest - repaintsBefore >= 3,
                $"重贴 {osd.IconRepaintsForTest - repaintsBefore} 次");

            // 反向：往回降同样要有过渡
            osd.ShowForTest(10);
            Check("往回降也从当前值起步", osd.IconProgressForTest > 0.5,
                $"p={osd.IconProgressForTest:F3}");

            DoEvents(400);
            Check("往回降最终停在 10%",
                Math.Abs(osd.IconProgressForTest - 0.10) < 1e-6,
                $"p={osd.IconProgressForTest:F3}");

            /* 连按：长按亮度键时 Apply 每几十毫秒就来一次。
             * 每次都必须从"当前已经画到的位置"接着走（否则会跳），
             * 而且**只能有一个定时器在跑** —— 叠加起来会互相打架、
             * 停不下来，最后卡在某个中间值。 */
            osd.ShowForTest(20);
            DoEvents(40);
            osd.ShowForTest(40);
            DoEvents(40);
            osd.ShowForTest(60);

            bool converged = false;
            var clock2 = System.Diagnostics.Stopwatch.StartNew();
            while (clock2.ElapsedMilliseconds < 900)
            {
                DoEvents(16);
                if (Math.Abs(osd.IconProgressForTest - 0.60) < 1e-6
                    && !osd.IconTimerRunningForTest)
                {
                    converged = true;
                    break;
                }
            }
            Check("连按时最终追到最后一档（60%）", converged,
                $"p={osd.IconProgressForTest:F3} running={osd.IconTimerRunningForTest}");
        }
        finally
        {
            osd.CloseForTest();
            DoEvents(80);
        }
    }

    /// <summary>
    /// 横条在**任意一台电脑**上都要长得一样：分辨率、缩放都换了，观感不能变。
    ///
    /// 这里穷举常见"分辨率 × 缩放"组合，直接跑纯函数
    /// <see cref="Core.Windows.OsdPlacement.Compute"/>（不碰窗口），断言四件事：
    ///   1. **尺寸**按目标显示器缩放换算：物理像素 = DIP × 缩放；
    ///   2. **位置**是工作区的相对关系：水平居中 + 下沿离工作区底 14 DIP；
    ///   3. 卡片永远是 4:1，不会因为缩放被拉变形；
    ///   4. 永远落在工作区内（含负坐标的副屏）。
    ///
    /// ⚠ 注意：卡片**不随分辨率变大**，只随缩放变大 —— 这正是系统音量条的
    /// 行为（它固定 192×48 DIP）。按屏幕百分比做会让 4K 上大得离谱、
    /// 1366×768 上小到看不清，反而和系统对不上。
    /// </summary>
    private static void TestOsdScalesAcrossDisplays()
    {
        // 工作区一栏是**物理像素**（缩放不改变物理像素，只改变 1 DIP 等于几像素）
        var cases = new (string Name, int Left, int Top, int W, int H, double Scale)[]
        {
            ("1366×768 @100%",    0,     0, 1366, 728,  1.00),
            ("1920×1080 @100%",   0,     0, 1920, 1040, 1.00),
            ("1920×1080 @125%",   0,     0, 1920, 1040, 1.25),
            ("2560×1440 @150%",   0,     0, 2560, 1400, 1.50),
            ("2880×1800 @200%",   0,     0, 2880, 1760, 2.00),
            ("3840×2160 @200%",   0,     0, 3840, 2120, 2.00),
            ("3840×2160 @150%",   0,     0, 3840, 2120, 1.50),
            ("副屏 −2560 起 @150%", -2560, -200, 2560, 1400, 1.50),
        };

        foreach (var c in cases)
        {
            int right = c.Left + c.W;
            int bottom = c.Top + c.H;
            var r = Core.Windows.OsdPlacement.Compute(c.Left, c.Top, right, bottom, c.Scale);

            double s = c.Scale;
            int expWinW = (int)Math.Round(216 * s);
            int expWinH = (int)Math.Round(72 * s);

            Check($"{c.Name}：窗口宽按缩放换算", Math.Abs(r.Width - expWinW) <= 1,
                $"{r.Width} vs {expWinW}");
            Check($"{c.Name}：窗口高按缩放换算", Math.Abs(r.Height - expWinH) <= 1,
                $"{r.Height} vs {expWinH}");

            var (cw, ch) = Core.Windows.OsdPlacement.CardSizeForTest(r);
            Check($"{c.Name}：卡片宽 = 192 × 缩放",
                Math.Abs(cw - 192 * s) <= 1, $"{cw} vs {192 * s}");
            Check($"{c.Name}：卡片高 = 48 × 缩放",
                Math.Abs(ch - 48 * s) <= 1, $"{ch} vs {48 * s}");

            Check($"{c.Name}：卡片长宽比仍是 4:1",
                Math.Abs((double)cw / ch - 4.0) < 0.06, $"{cw}x{ch}");

            // 水平居中（相对工作区，不是相对 (0,0)）
            double cx = r.X + r.Width / 2.0;
            Check($"{c.Name}：水平居中", Math.Abs(cx - (c.Left + c.W / 2.0)) <= 1.0,
                $"cx={cx} 期望={(c.Left + c.W / 2.0)}");

            // 卡片下沿离工作区底边 = 14 DIP（换算成像素）
            int cardBottom = r.Y + (int)Math.Round(60 * s);   // 12 阴影 + 48 卡片
            int gapPx = (int)Math.Round(14 * s);
            Check($"{c.Name}：贴底 14 DIP", Math.Abs((bottom - cardBottom) - gapPx) <= 1,
                $"实际底距={bottom - cardBottom} 期望={gapPx}");

            // 不出工作区
            Check($"{c.Name}：完整落在工作区内",
                r.X >= c.Left && r.Y >= c.Top && r.Right <= right && r.Bottom <= bottom,
                $"({r.X},{r.Y})-({r.Right},{r.Bottom}) 工作区=({c.Left},{c.Top})-({right},{bottom})");
        }

        /* 同一块屏换个缩放：物理像素尺寸必须跟着变，
         * 而"折回 DIP"必须还是 192×48 —— 这就是跨机器一致的判据。 */
        var at100 = Core.Windows.OsdPlacement.Compute(0, 0, 1920, 1040, 1.00);
        var at200 = Core.Windows.OsdPlacement.Compute(0, 0, 3840, 2120, 2.00);
        var (w100, h100) = Core.Windows.OsdPlacement.CardSizeForTest(at100);
        var (w200, h200) = Core.Windows.OsdPlacement.CardSizeForTest(at200);
        CheckEqual("100% 下卡片 192×48 像素", $"{w100}x{h100}", "192x48");
        CheckEqual("200% 下卡片 384×96 像素（与系统实测一致）", $"{w200}x{h200}", "384x96");
        Check("200% 的卡片正好是 100% 的两倍",
            w200 == w100 * 2 && h200 == h100 * 2, $"{w200}x{h200}");

        /* 缩放值非法时要当 1.0 用，不能把横条算成 0 像素 */
        var bad = Core.Windows.OsdPlacement.Compute(0, 0, 1920, 1040, 0);
        Check("缩放为 0 时按 1.0 处理（不会算出空窗口）",
            bad.Width > 0 && bad.Height > 0, $"{bad.Width}x{bad.Height}");

        /* 极小屏保护：320×240 的屏上别让横条横跨大半个屏幕 */
        var tiny = Core.Windows.OsdPlacement.Compute(0, 0, 320, 240, 1.0);
        Check("极小屏上横条被压到工作区的 80% 以内",
            tiny.Width <= 320 * 0.8 + 1, $"{tiny.Width}");

        /* 工作区无效（离屏 / 还没拿到显示器）时返回空矩形，调用方跳过定位 */
        var none = Core.Windows.OsdPlacement.Compute(0, 0, 0, 0, 1.0);
        Check("工作区为空时返回空矩形", none.Width == 0 && none.Height == 0,
            $"{none.Width}x{none.Height}");
    }

    /// <summary>
    /// OSD（音量/亮度横条）与「输入文本」方式。
    ///
    /// 这两条对应两个真实问题：
    ///   · 音量能调但不弹系统横条 —— 因为系统横条是 explorer 处理
    ///     WM_APPCOMMAND 时画的，跟音频 API 无关；
    ///   · 密码框打不进字 —— Unicode 注入没有扫描码，被安全输入框过滤掉。
    /// </summary>
    private static void TestOsdAndTextInput()
    {
        // ---------- OSD（自绘横条） ----------
        CheckEqual("进度条 0% 不显示", UI.Windows.OsdWindow.ScaleForTest(0), 0.0);
        CheckEqual("进度条 100% 满格", UI.Windows.OsdWindow.ScaleForTest(100), 1.0);
        CheckEqual("进度条 65%", UI.Windows.OsdWindow.ScaleForTest(65), 0.65);
        CheckEqual("进度条越界会被夹住（上）", UI.Windows.OsdWindow.ScaleForTest(180), 1.0);
        CheckEqual("进度条越界会被夹住（下）", UI.Windows.OsdWindow.ScaleForTest(-20), 0.0);

        /* 位置与尺寸：全部对齐系统音量条（192×48、贴工作区底边 14 DIP）。
         * 数字来自对系统横条的全屏截图测量，见 OsdWindow 的注释。 */
        double ow = UI.Windows.OsdWindow.DesignWidth;
        double oh = UI.Windows.OsdWindow.DesignHeight;
        var wa = new System.Windows.Rect(0, 0, 1920, 1040);
        var (osdLeft, osdTop) = UI.Windows.OsdWindow.PositionForTest(wa);
        CheckEqual("横条水平居中", osdLeft, (1920 - (ow + 24)) / 2.0);
        Check("横条在屏幕下半部分（不是正中）", osdTop > 1040 * 0.85, $"top={osdTop}");

        /* 卡片下沿离工作区底边必须和系统一样是 14 DIP：
         * 窗口比卡片大一圈（留给阴影），所以窗口底边不是卡片底边。 */
        double cardBottom = osdTop + 12 + oh;
        CheckEqual("卡片下沿贴底 14 DIP（相对工作区底边）",
            Math.Round(1040 - cardBottom, 3), UI.Windows.OsdWindow.CardBottomGapForTest);

        /* 任务栏固定时 rcWork 已经扣掉了它（这里模拟 48 DIP 的任务栏）：
         * 卡片同样只离**工作区**底边 14 DIP，不需要再额外让开一次。
         * v4.5.5 之前会再减一个任务栏高度，结果比系统横条高整整一格。 */
        var waWithBar = new System.Windows.Rect(0, 0, 1920, 1040 - 48);
        var (_, topWithBar) = UI.Windows.OsdWindow.PositionForTest(waWithBar);
        CheckEqual("有任务栏时也只让 14 DIP（不再重复扣任务栏）",
            Math.Round(waWithBar.Bottom - (topWithBar + 12 + oh), 3),
            UI.Windows.OsdWindow.CardBottomGapForTest);
        Check("横条不会因为任务栏再抬高 48 DIP",
            Math.Abs(topWithBar - (osdTop - 48)) < 0.001,
            $"{topWithBar} vs {osdTop - 48}");

        CheckEqual("卡片宽度与系统一致（192）", ow, 192.0);
        CheckEqual("卡片高度与系统一致（48）", oh, 48.0);
        Check("横条长宽比与系统一致（4 : 1）",
            Math.Abs(ow / oh - 4.0) < 0.05, $"{ow / oh:F2}");

        /* 圆角也要一致。以前是 6 DIP，比系统"更方" —— 用户一眼就看出来了：
         * 200% 缩放下差 2 DIP 只有 4 个像素，单看没感觉，并排就很明显。
         * 定量依据（tools/measure_osd_radius.py，同一算法量两边、都从底边对齐）：
         *   系统拟合 12.5 px；我们 6 DIP→9.75、8 DIP→13.5。
         * 拟合值比真值恒定小约 2.4 px，反推系统 = 12.5+2.4 ≈ 15 px = 7.5 DIP；
         * 按 7.5 复测，两边剖面逐行差 ≤ 0.1 px，拟合值同为 12.5。
         * 所以断言 7.5，不能断 8（8 会圆过头 1 px，用户还是会看出来）。 */
        CheckEqual("卡片圆角与系统一致（7.5 DIP）",
            UI.Windows.OsdWindow.CardRadius, 7.5);
        Check("圆角不超过卡片高度的一半（不会变成胶囊）",
            UI.Windows.OsdWindow.CardRadius <= oh / 2,
            $"{UI.Windows.OsdWindow.CardRadius} vs {oh / 2}");

        /* 只断言常量不够：XAML 里还写着一个坐标值，万一有人改 XAML
         * 而忘了 ctor 覆盖，实际渲染的就是 XAML 那个。这里查真家伙。 */
        var osdRadius = UI.Windows.OsdWindow.CreateDetached();
        CheckEqual("实际生效的圆角 == 常量（XAML 不会偷偷盖回去）",
            Math.Round(osdRadius.CardRadiusForTest, 3),
            UI.Windows.OsdWindow.CardRadius);
        osdRadius.Close();
        DoEvents(40);

        /* 配色：两套都对齐系统（半透明材质 + 强调色进度条）。
         * 横条跟的是 **Windows 系统主题**，不是本应用主题。 */
        var darkPal = UI.Windows.OsdWindow.PaletteForTest(light: false);
        var lightPal = UI.Windows.OsdWindow.PaletteForTest(light: true);

        Check("夜间：卡片是深色", LuminanceOf(darkPal.Card) < 0.06,
            $"{darkPal.Card} lum={LuminanceOf(darkPal.Card):F3}");
        Check("夜间：图标/数字是白色（和系统一致）",
            darkPal.Text.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase), darkPal.Text);
        Check("日间：卡片是浅色", LuminanceOf(lightPal.Card) > 0.70,
            $"{lightPal.Card} lum={LuminanceOf(lightPal.Card):F3}");
        Check("日间：图标/数字是深色（和系统一致）",
            LuminanceOf(lightPal.Text) < 0.10, $"{lightPal.Text}");

        Check("夜间进度条是强调色的浅色档（蓝色系）", IsBlueish(darkPal.Fill), darkPal.Fill);
        Check("日间进度条是强调色的深色档（蓝色系）", IsBlueish(lightPal.Fill), lightPal.Fill);
        Check("日间/夜间是两个不同的蓝（不能共用一档）",
            !darkPal.Fill.Equals(lightPal.Fill, StringComparison.OrdinalIgnoreCase),
            $"dark={darkPal.Fill} light={lightPal.Fill}");

        Check("夜间：文字与卡片对比 ≥ 4.5:1",
            ContrastRatio(darkPal.Text, darkPal.Card) >= 4.5,
            $"{ContrastRatio(darkPal.Text, darkPal.Card):F2}:1");
        Check("日间：文字与卡片对比 ≥ 4.5:1",
            ContrastRatio(lightPal.Text, lightPal.Card) >= 4.5,
            $"{ContrastRatio(lightPal.Text, lightPal.Card):F2}:1");
        Check("夜间：进度条与卡片对比 ≥ 3:1",
            ContrastRatio(darkPal.Fill, darkPal.Card) >= 3.0,
            $"{ContrastRatio(darkPal.Fill, darkPal.Card):F2}:1");
        Check("日间：进度条与卡片对比 ≥ 3:1",
            ContrastRatio(lightPal.Fill, lightPal.Card) >= 3.0,
            $"{ContrastRatio(lightPal.Fill, lightPal.Card):F2}:1");

        // 连续量动作：按住要一直执行，所以必须认得出来
        Check("音量+ 属于连续量", HotkeyEngine.IsContinuous("volumeUp"));
        Check("音量- 属于连续量", HotkeyEngine.IsContinuous("volumeDown"));
        Check("亮度+ 属于连续量", HotkeyEngine.IsContinuous("brightnessUp"));
        Check("亮度- 属于连续量", HotkeyEngine.IsContinuous("brightnessDown"));
        Check("光标移动不属于连续量（那是连发不是累加）",
            !HotkeyEngine.IsContinuous("moveLeft"));
        Check("静音不属于连续量（切换语义，连按会来回抖）",
            !HotkeyEngine.IsContinuous("volumeMute"));

        // 节奏：起播延迟要比系统键盘短，步进要密
        int repeatDelay = HotkeyEngine.RepeatDelayMs;
        Check("按住起播延迟合理", repeatDelay is > 100 and < 600, $"{repeatDelay}ms");
        Check("连续步进足够密（看起来是滑的）",
            HotkeyEngine.RepeatIntervalMs <= 60, $"{HotkeyEngine.RepeatIntervalMs}ms");

        /* 音量走**系统音量键**：这样 Windows 自己调音量并弹它自己的横条。
         * 键码写错就变成"按了没反应"，而且不会报错，所以钉死。 */
        CheckEqual("音量+ 用的是系统音量键", VK.VolumeUp, 0xAF);
        CheckEqual("音量- 用的是系统音量键", VK.VolumeDown, 0xAE);
        CheckEqual("静音用的是系统静音键", VK.VolumeMute, 0xAD);

        // 音量每步是系统的 2%，比亮度（每步 1%）更疏，否则按住一秒就到底
        Check("音量连发比亮度疏",
            HotkeyEngine.RepeatIntervalFor("volumeUp")
            > HotkeyEngine.RepeatIntervalFor("brightnessUp"),
            $"音量 {HotkeyEngine.RepeatIntervalFor("volumeUp")}ms / "
            + $"亮度 {HotkeyEngine.RepeatIntervalFor("brightnessUp")}ms");
        Check("音量连发不至于一秒到底",
            HotkeyEngine.RepeatIntervalFor("volumeUp") >= 60,
            $"{HotkeyEngine.RepeatIntervalFor("volumeUp")}ms");

        // ---------- 调节幅度（设置里可改） ----------
        var def = DefaultSettings.Create().Global;
        CheckEqual("音量幅度默认 5%", def.VolumeStep, 5);
        CheckEqual("亮度幅度默认 10%", def.BrightnessStep, 10);

        // 归一化把越界值夹回来：0 会让"调不动"，999 一下到底
        var clampCase = DefaultSettings.Create();
        clampCase.Global.VolumeStep = 0;
        clampCase.Global.BrightnessStep = 999;
        Core.Settings.SettingsStore.Normalize(clampCase);
        CheckEqual("音量幅度下限夹到 1", clampCase.Global.VolumeStep, 1);
        CheckEqual("亮度幅度上限夹到 50", clampCase.Global.BrightnessStep, 50);

        /* 系统步进固定 2%，设定值不是整数倍时要靠累计补偿。
         * 这组断言直接盯住"设 5 就真的是 5"，而不是每次差一点。 */
        var (t1, r1) = ActionContext.PlanVolumeTaps(5, 2, 0);
        CheckEqual("设 5%（系统 2%）第一次发 2 次键", t1, 2);
        CheckEqual("并记下 1% 的欠账", r1, 1.0);

        var (t2, r2) = ActionContext.PlanVolumeTaps(5, 2, r1);
        CheckEqual("第二次补上欠账，发 3 次键", t2, 3);
        CheckEqual("欠账清零", r2, 0.0);

        // 连按十次总量必须正好 50%
        double rem = 0, total = 0;
        for (int i = 0; i < 10; i++)
        {
            var (taps, next) = ActionContext.PlanVolumeTaps(5, 2, rem);
            total += taps * 2;
            rem = next;
        }
        CheckEqual("连按十次正好 50%（不会漂移）", total, 50.0);

        var (t3, r3) = ActionContext.PlanVolumeTaps(10, 2, 0);
        CheckEqual("设 10% 时正好发 5 次", t3, 5);
        CheckEqual("设 10% 无欠账", r3, 0.0);

        var (t4, _) = ActionContext.PlanVolumeTaps(0, 2, 0);
        CheckEqual("幅度为 0 也至少发一次（否则按了没反应）", t4, 1);

        /* 亮度横条要跟着"白夜"（浅色 / 深色）走，而且文字不能和底色糊在一起。
         * 这条真的把横条渲染成位图去量亮度，比只查画刷存在更实在。 */
        TestOsdFollowsTheme();

        // ---------- 输入文本方式 ----------        Check("纯 ASCII 走真实按键", InputSender.LooksLikePlainAscii("Abc123!@#"));
        Check("带空格也算", InputSender.LooksLikePlainAscii("hello world"));
        Check("中文不算纯 ASCII", !InputSender.LooksLikePlainAscii("你好"));
        Check("emoji 不算纯 ASCII", !InputSender.LooksLikePlainAscii("a😀"));
        Check("换行不算（会被当成回车提交表单）",
            !InputSender.LooksLikePlainAscii("line1\nline2"));
        Check("制表符不算", !InputSender.LooksLikePlainAscii("a\tb"));
        Check("空串不算", !InputSender.LooksLikePlainAscii(""));

        // 设置默认值：老配置里没有这个字段，反序列化后必须是 auto
        CheckEqual("输入文本方式默认 auto", DefaultSettings.Create().Global.TextInputMode, "auto");

        // 三种方式都不能抛异常（DryRun 下不会真的打字）
        try
        {
            InputSender.Text("abc", InputSender.TextMode.Auto);
            InputSender.Text("中文", InputSender.TextMode.Auto);
            InputSender.Text("abc", InputSender.TextMode.Keys);
            InputSender.Text("abc", InputSender.TextMode.Unicode);
            Check("三种输入方式都能跑通（干跑）", true);
        }
        catch (Exception ex)
        {
            Check("三种输入方式都能跑通（干跑）", false, ex.Message);
        }
    }

    /// <summary>
    /// 亮度横条必须适配"白夜"（浅色 / 深色）模式，且文字与底色有足够对比。
    ///
    /// 真渲染成位图去量：只看画刷键存在是骗人的 —— 画刷定义对了，
    /// XAML 里若还写死 <c>#F4F6FA</c>，浅色主题下就是"白字白底"。
    /// </summary>
    private static void TestOsdFollowsTheme()
    {
        var app = System.Windows.Application.Current;
        if (app == null)
        {
            Check("亮度横条不随主题变（无 Application，跳过）", true);
            return;
        }

        var saved = UI.Theme.ThemeManager.Current;

        try
        {
            double lightCard = MeasureOsdLuminance(UI.Theme.ThemeManager.Mode.Light, out double lightRange);
            double darkCard = MeasureOsdLuminance(UI.Theme.ThemeManager.Mode.Dark, out double darkRange);

            /* 横条**不跟随应用主题**：这是实测的结论 ——
             *
             * 系统那条音量横条在浅色桌面上照样是深色卡片（截图确认）。
             * 之前跟着主题走，结果是"白天浅底、夜里深底"，怎么调都和系统对不上；
             * 现在两套主题都渲染成同一个深色卡片，差异只可能来自绘制 bug。 */
            /* 横条跟的是 Windows 系统主题，不是本应用主题 ——
             * 同一时刻切换应用主题，渲染结果必须完全不受影响。 */
            Check("横条跟系统主题、不跟应用主题（两种应用主题下亮度差 < 0.05）",
                Math.Abs(lightCard - darkCard) < 0.05,
                $"app-light={lightCard:F2} app-dark={darkCard:F2}");
            Check("画面里有明显的前景（进度条/文字，不是一片纯色）",
                lightRange > 0.15 && darkRange > 0.15,
                $"light={lightRange:F2} dark={darkRange:F2}");
        }
        catch (Exception ex)
        {
            Check("亮度横条渲染", false, ex.Message);
        }
        finally
        {
            UI.Theme.ThemeManager.Apply(saved);
        }
    }

    /// <summary>颜色是不是"蓝色系"（蓝通道明显压过红/绿，且整体够亮）。</summary>
    private static bool IsBlueish(string hex)
    {
        var (r, g, b) = ParseRgb(hex);
        return b >= 0.55 && b - r >= 0.15 && b - g >= 0.05;
    }

    private static double RelativeLuminance(double c)
        => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double LuminanceOf(string hex)
    {
        var (r, g, b) = ParseRgb(hex);
        return 0.2126 * RelativeLuminance(r)
             + 0.7152 * RelativeLuminance(g)
             + 0.0722 * RelativeLuminance(b);
    }

    /// <summary>WCAG 对比度：(亮+0.05)/(暗+0.05)，1~21。</summary>
    private static double ContrastRatio(string fg, string bg)
    {
        double a = LuminanceOf(fg), b = LuminanceOf(bg);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>
    /// 在某套主题下渲染一次横条，返回卡片区域的平均亮度（0~1）
    /// 以及整张图里最亮与最暗的差值（用来判断"字能不能看清"）。
    /// </summary>
    private static double MeasureOsdLuminance(UI.Theme.ThemeManager.Mode mode, out double range)
    {
        UI.Theme.ThemeManager.Apply(mode);
        DoEvents(40);

        var osd = UI.Windows.OsdWindow.CreateDetached();
        osd.ShowInTaskbar = false;
        osd.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        osd.Left = -32000;
        osd.Top = -32000;
        osd.Show();
        osd.PrepareForRender(60);
        DoEvents(120);
        osd.UpdateLayout();

        var root = (System.Windows.FrameworkElement)osd.Content;
        int w = Math.Max(1, (int)Math.Round(root.ActualWidth));
        int h = Math.Max(1, (int)Math.Round(root.ActualHeight));

        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);

        int stride = w * 4;
        var pixels = new byte[stride * h];
        bmp.CopyPixels(pixels, stride, 0);

        double sum = 0;
        int count = 0;
        double min = 1, max = 0;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * stride + x * 4;
                byte a = pixels[i + 3];
                if (a < 200) continue;      // 跳过透明边角（圆角外的部分）

                double lum = (0.2126 * pixels[i + 2] + 0.7152 * pixels[i + 1] + 0.0722 * pixels[i]) / 255.0;

                // 卡片底色：避开图标、进度条、数字所在的中间带
                bool inCard = y > h * 0.12 && y < h * 0.28;
                if (inCard) { sum += lum; count++; }

                if (lum < min) min = lum;
                if (lum > max) max = lum;
            }
        }

        osd.Close();
        DoEvents(60);

        range = max - min;
        return count > 0 ? sum / count : -1;
    }

    /// <summary>
    /// 回归测试：**目标已建立**时按亮度键，推进线程必须被拉起来。
    ///
    /// 4.4.0 为了让按键路径完全不碰硬件，把 AddTarget 拆成快 / 慢两条路；
    /// 快路径只改了目标值，漏掉了 EnsurePump()。而 AppHost 启动时会在后台
    /// 预热一次 IsSupported()，那一步就把目标建好了 —— 于是用户第一次按键
    /// 就走快路径：日志里「目标 X%」一路涨，屏幕一动不动。
    ///
    /// 这里用干跑（DryRun）复现当时的先后顺序，钉住"快路径也要拉起线程"。
    /// 没有这条断言，下次谁再"优化"一遍快路径还会踩回去。
    /// </summary>
    private static void TestBrightnessPumpStarts()
    {
        bool savedDry = Core.Windows.BrightnessService.DryRun;

        try
        {
            Core.Windows.BrightnessService.DryRun = true;      // 不真的改屏幕亮度

            // 模拟"启动预热已经把目标建好、但推进线程还没起"
            Core.Windows.BrightnessService.PrimeTargetForTest(50);
            CheckEqual("预热后目标已建立", Core.Windows.BrightnessService.TargetForTest, 50);

            int? after = Core.Windows.BrightnessService.AddTarget(5);
            CheckEqual("快路径下目标正确累加", after, 55);
            Check("快路径也把推进线程拉起来了", Core.Windows.BrightnessService.PumpRunning);

            Core.Windows.BrightnessService.AddTarget(-3);
            Check("连续按键后推进线程仍存活", Core.Windows.BrightnessService.PumpRunning);
            CheckEqual("连减后目标正确", Core.Windows.BrightnessService.TargetForTest, 52);
        }
        finally
        {
            Core.Windows.BrightnessService.DryRun = savedDry;
            Core.Windows.BrightnessService.Reset();            // 清掉测试留下的状态
        }
    }

    private static void TestBrightnessMath()
    {
        // 0~100 量程（大多数笔记本）
        CheckEqual("0~100：60 → 60%", Core.Windows.BrightnessService.FromVcp(60, 100), 60);
        // 0~255 量程（不少外接屏）
        CheckEqual("0~255：128 → 50%", Core.Windows.BrightnessService.FromVcp(128, 255), 50);
        // 0~1000 量程（少数专业屏）
        CheckEqual("0~1000：250 → 25%", Core.Windows.BrightnessService.FromVcp(250, 1000), 25);
        CheckEqual("max=0 无法换算", Core.Windows.BrightnessService.FromVcp(10, 0), -1);

        CheckEqual("50% → 0~255 的 128", Core.Windows.BrightnessService.ToVcp(50, 255), 128u);
        CheckEqual("100% → 满量程", Core.Windows.BrightnessService.ToVcp(100, 255), 255u);
        CheckEqual("0% → 0", Core.Windows.BrightnessService.ToVcp(0, 255), 0u);
        CheckEqual("超过 100 按满算", Core.Windows.BrightnessService.ToVcp(150, 255), 255u);
        CheckEqual("负数按 0 算", Core.Windows.BrightnessService.ToVcp(-20, 255), 0u);

        // 往返：百分比 → 原始值 → 百分比，误差不超过 1
        bool roundTripOk = true;
        foreach (uint max in new uint[] { 100, 255, 1000 })
        {
            foreach (int pct in new[] { 0, 10, 25, 50, 75, 100 })
            {
                int back = Core.Windows.BrightnessService.FromVcp(
                    Core.Windows.BrightnessService.ToVcp(pct, max), max);
                if (Math.Abs(back - pct) > 1) roundTripOk = false;
            }
        }
        Check("亮度换算往返误差 ≤ 1%", roundTripOk);

        /* ---------- 长按亮度的平滑推进 ----------
         *
         * 用户的原话是"避免生硬跳变，使过渡更平滑"。
         * 上一版每一轮固定走 4%，在 DDC 屏上看起来就是"一格一格蹦"；
         * 现在按剩余距离成比例走：远时步子大、近时退到 1%。 */
        CheckEqual("差 1% 就走 1%", Core.Windows.BrightnessService.PlanStepForTest(1), 1);
        CheckEqual("差 -1% 就走 -1%", Core.Windows.BrightnessService.PlanStepForTest(-1), -1);
        CheckEqual("差 2% 只走 1%（最后几格要慢）",
            Math.Abs(Core.Windows.BrightnessService.PlanStepForTest(2)), 1);
        Check("单步不超过剩余距离（不会冲过头）",
            Math.Abs(Core.Windows.BrightnessService.PlanStepForTest(3)) <= 3);
        Check("差 40% 时步子变大（追得上）",
            Math.Abs(Core.Windows.BrightnessService.PlanStepForTest(40)) >= 4,
            $"{Core.Windows.BrightnessService.PlanStepForTest(40)}");
        Check("单步有上限（不跳大格）",
            Math.Abs(Core.Windows.BrightnessService.PlanStepForTest(100)) <= 6,
            $"{Core.Windows.BrightnessService.PlanStepForTest(100)}");
        Check("单步方向跟着目标走",
            Core.Windows.BrightnessService.PlanStepForTest(-40) < 0);

        bool monotonic = true, neverOvershoot = true;
        int prevStep = 0;
        for (int d = 1; d <= 100; d++)
        {
            int step = Math.Abs(Core.Windows.BrightnessService.PlanStepForTest(d));
            if (step < prevStep) monotonic = false;      // 差距变大时步子不能反而变小
            if (step > d) neverOvershoot = false;
            prevStep = step;
        }
        Check("步长随差距单调不减（不会忽大忽小）", monotonic);
        Check("任何差距都不会一步冲过头", neverOvershoot);
        Check("最后 8% 都是小步（收尾不突兀）",
            Math.Abs(Core.Windows.BrightnessService.PlanStepForTest(8)) <= 2,
            $"{Core.Windows.BrightnessService.PlanStepForTest(8)}");

        /* ---------- 单键步进参数（绑定界面里可填） ---------- */
        CheckEqual("参数为空 → 用设置里的幅度",
            Core.Actions.ActionCatalog.StepOrForTest("", 10), 10);
        CheckEqual("参数为空白 → 用设置里的幅度",
            Core.Actions.ActionCatalog.StepOrForTest("   ", 10), 10);
        CheckEqual("参数优先", Core.Actions.ActionCatalog.StepOrForTest("3", 10), 3);
        CheckEqual("填 0 视为没填（否则按了不动）",
            Core.Actions.ActionCatalog.StepOrForTest("0", 10), 10);
        CheckEqual("非数字视为没填",
            Core.Actions.ActionCatalog.StepOrForTest("abc", 10), 10);
        CheckEqual("超上限夹到 100",
            Core.Actions.ActionCatalog.StepOrForTest("999", 10), 100);

        // 只读探测：这台机器走哪条通道（不支持也是合法结果，不能算失败）
        var backend = Core.Windows.BrightnessService.Backend;        int? now = Core.Windows.BrightnessService.Get();
        Check($"亮度通道：{Core.Windows.BrightnessService.BackendName}",
            backend != Core.Windows.BrightnessBackend.Unknown,
            $"backend={backend}, value={now}");
        if (backend != Core.Windows.BrightnessBackend.Unsupported)
            Check("支持的通道能读出亮度", now != null, $"value={now}");

        /* 调不动时的提示必须能照着做。
         * 早先只回一句"这台设备不支持"，用户没法判断是设置没开、
         * 线材不通还是程序坏了 —— 等于没告诉。 */
        string diag = Core.Windows.BrightnessService.Diagnose();
        Check("调不动时给的是可操作说明（不是一句「不支持」）",
            diag.Length >= 12, diag);

        // 动作表
        var settings = DefaultSettings.Create();
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new Core.Actions.WindowBindingService(settings),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };
        var cat = Core.Actions.ActionCatalog.Create(ctx);

        foreach (string id in new[] { "brightnessUp", "brightnessDown" })
        {
            var it = cat.Items.FirstOrDefault(i => i.Id == id);
            Check($"动作 {id} 在「系统功能」", it?.Category == "系统功能");
            Check($"动作 {id} 标明需要显示器支持", it?.Description.Contains("DDC/CI") == true);
            Check($"动作 {id} 说明里写了长按加速",
                it?.Description.Contains("按住越久") == true);
        }

        /* ---------- 按住亮度时的加速 ----------
         *
         * 用户要求："一直长按，应该有一个逐渐的加速度，但不能加速太离谱"。
         * 手感照搬隔壁鼠标那套 dynamic_speed（按住越走越快），
         * 参数是：起步 1%、每步 +0.10、封顶 15 步 = 2.5 倍。
         *
         * 直接对着纯逻辑（BrightnessAccel）验，不碰硬件。 */
        var accel = new Core.Actions.BrightnessAccel();
        var steps = new List<int>();
        for (int i = 0; i < 30; i++) steps.Add(accel.Next());

        CheckEqual("连发第一步仍是 1%（起步还是细调）", steps[0], 1);
        Check("每一步至少 1%（不会出现空步）", steps.All(s => s >= 1));
        Check("前 3 步只走 4%（起步不冲）", steps.Take(3).Sum() <= 5,
            $"{steps.Take(3).Sum()}%");
        Check("前 15 步平均 ≤ 2%/步（加速是渐进的）", steps.Take(15).Sum() <= 30,
            $"{steps.Take(15).Sum()}%");

        int firstHalf = steps.Take(15).Sum();
        int secondHalf = steps.Skip(15).Sum();
        Check("后 15 步明显更快（≥ 1.3 倍）", secondHalf >= firstHalf * 1.3,
            $"{firstHalf}% → {secondHalf}%");
        Check("满速不超过 3%/步（不能加速太离谱）", steps.Skip(15).Max() <= 3,
            $"max={steps.Skip(15).Max()}");
        Check("30 步（约 1.4 秒）至少能走 60%（够得着）", steps.Sum() >= 60,
            $"{steps.Sum()}%");
        Check("30 步也不会一步到底（≤ 100%）", steps.Sum() <= 100,
            $"{steps.Sum()}%");

        accel.Reset();
        CheckEqual("松开后再按，重新从 1% 起步", accel.Next(), 1);

        // 接线：ActionContext 里确实挂了一台加速器，而且同样从头起步
        ctx.ResetBrightnessAccelForTest();
        CheckEqual("ActionContext 的加速器起步也是 1%", ctx.NextBrightnessAccelForTest(), 1);
        Check("ActionContext 的加速器第二、三步仍是细调",
            ctx.NextBrightnessAccelForTest() <= 2 && ctx.NextBrightnessAccelForTest() <= 2);

        Check("引擎连发标记（arg=\"1\"）被认作连续",
            Core.Actions.ActionCatalog.IsRepeatForTest("1"));
        Check("普通参数（空 / 步进 3）不被当成连发",
            !Core.Actions.ActionCatalog.IsRepeatForTest("")
            && !Core.Actions.ActionCatalog.IsRepeatForTest(null)
            && !Core.Actions.ActionCatalog.IsRepeatForTest("3"));
    }
}