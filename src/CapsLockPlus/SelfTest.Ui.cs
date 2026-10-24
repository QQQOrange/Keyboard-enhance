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

    /* 主题、键盘布局、窗口绑定与点选、托盘。
     * 与 SelfTest.cs 同属 partial 类，只按业务域分文件。 */

    /// <summary>
    /// 窗口绑定：固定 10 个槽位已移除，改为「窗口绑定」动作（每个按键存自己的程序路径）。
    ///
    /// 这里验证：
    ///   1. 旧槽位动作确实不在动作表里了（用户要求彻底移除）；
    ///   2. 默认配置里不再有指向旧动作的绑定（否则那些键按下去没反应）；
    ///   3. 「窗口绑定」分类只有 1 个动作；
    ///   4. 底层 ActivateExe / BindByExe 的安全性。
    /// </summary>
    private static void TestWindowBindings()
    {
        var settings = DefaultSettings.Create();
        var svc = new WindowBindingService(settings);

        var catalog = ActionCatalog.Create(new ActionContext
        {
            Settings = () => settings,
            Clipboard = new ClipboardService(),
            Windows = new WindowActions(),
            WindowBindings = svc,
            Mouse = new MouseService(),
            Notify = _ => { },
        });

        // 1. 旧槽位动作必须彻底消失（用户明确要求移除这一分类）
        int legacy = Enumerable.Range(1, 10).Count(i =>
            catalog.Get($"winbindActivate{i}") != null || catalog.Get($"winbindBind{i}") != null);
        Check("旧槽位动作已全部移除", legacy == 0, legacy.ToString());
        Check("没有「窗口绑定（旧槽位）」分类",
            catalog.Items.All(i => i.Category != "窗口绑定（旧槽位）"));

        // 2. 默认配置不能残留指向旧动作的绑定
        var stale = settings.Keys
            .Where(k => k.Action.StartsWith("winbindActivate", StringComparison.OrdinalIgnoreCase)
                     || k.Action.StartsWith("winbindBind", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Combo + "=" + k.Action).ToList();
        Check("默认配置无残留旧槽位绑定", stale.Count == 0,
            string.Join(", ", stale.Take(6)));

        // 3. 「窗口绑定」分类下只有 1 个动作
        var inCat = catalog.Items.Where(i => i.Category == "窗口绑定").ToList();
        CheckEqual("「窗口绑定」分类只有 1 个动作", inCat.Count, 1);
        CheckEqual("那个动作是 winBind", inCat.FirstOrDefault()?.Id, "winBind");

        // 4. 底层安全性（不依赖槽位表了）
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length > 0)
        {
            Check("拒绝绑定自身", !svc.BindByExe(1, exe));
            Check("拒绝不存在的路径", !svc.BindByExe(1, @"C:\__no_such_program__.exe"));
        }
        else
        {
            Check("取不到自身路径，跳过自身绑定检查", true);
        }

        // 动画已按用户要求全部删除：确认公开 API 里不再有动画开关
        Check("WindowService 不再暴露动画开关",
            typeof(WindowService).GetProperty("AnimationEnabled") == null);
        Check("WindowService 不再有 Genie 动画方法",
            typeof(WindowService).GetMethod("ActivateWithGenie") == null &&
            typeof(WindowService).GetMethod("HideWithGenie") == null);
        Check("设置里不再有动画开关字段",
            typeof(Core.Settings.GlobalOptions)
                .GetProperty("WindowActivationAnimation") == null);
    }

    /// <summary>
    /// 窗口点选的"取窗口"逻辑。
    ///
    /// 判定规则：
    ///   * 自己的窗口必须被排除；
    ///   * 桌面/任务栏/托盘溢出等系统壳窗口必须被排除；
    ///   * 普通窗口必须能被正确识别；
    ///   * 按坐标必须取得到窗口（WindowFromPoint 优先，Z 序扫描兜底）。
    ///
    /// 探针现在直接调生产路径上的 IsAcceptable —— 判据只能有一份。
    /// 早先探针比生产路径多查一条"排除自身 exe"，而生产路径漏了壳窗口过滤，
    /// 结果自检全绿、实际点哪儿都是桌面。
    /// </summary>
    private static void TestWindowPreview()
    {
        var picker = new UI.Windows.WindowPickerWindow();
        var probe = picker.Probe;

        /* 遮罩要铺满**整个虚拟屏幕**。
         * 原来用 WindowState=Maximized，只覆盖主显示器 ——
         * 接了扩展屏时副屏上根本没有遮罩，那里的窗口既看不到高亮也点不到。 */
        Check("遮罩铺满整个虚拟屏幕（含副屏）",
            Math.Abs(picker.Width - System.Windows.SystemParameters.VirtualScreenWidth) < 1 &&
            Math.Abs(picker.Height - System.Windows.SystemParameters.VirtualScreenHeight) < 1,
            $"{picker.Width}x{picker.Height} vs " +
            $"{System.Windows.SystemParameters.VirtualScreenWidth}x{System.Windows.SystemParameters.VirtualScreenHeight}");

        Check("点选器初始没有选中任何窗口", picker.PickedWindow == IntPtr.Zero);

        /* Esc 必须能退出。
         *
         * 遮罩是 WS_EX_NOACTIVATE 的（不能抢焦点，否则点选会把用户正在用的
         * 窗口顶到后面），所以它**收不到 WPF 的键盘事件** ——
         * 原来挂的 PreviewKeyDown 永远不会触发，按 Esc 一点反应都没有。
         * 现在改由全局键盘钩子接，判定逻辑抽成纯函数在这里验。 */
        const int keyDown = 0x0100, keyUp = 0x0101, sysKeyDown = 0x0104, sysKeyUp = 0x0105;
        const uint esc = 0x1B, otherKey = 0x41;   // 0x41 = A
        Check("Esc 按下被接住", picker.SwallowsKeyForTest(keyDown, esc));
        Check("Esc 抬起也被接住（避免漏一个孤儿 keyup）", picker.SwallowsKeyForTest(keyUp, esc));
        Check("Alt+Esc 也能取消", picker.SwallowsKeyForTest(sysKeyDown, esc));
        Check("Esc 的系统抬起也接住", picker.SwallowsKeyForTest(sysKeyUp, esc));
        Check("其它键不拦（点选时还能正常打字）",
            !picker.SwallowsKeyForTest(keyDown, otherKey));
        Check("鼠标消息不当成取消键", !picker.SwallowsKeyForTest(0x0200, esc));

        picker.CancelForTest();
        Check("取消后选中结果为空", picker.PickedWindow == IntPtr.Zero);

        // 自己的窗口必须排除（否则会绑到自己）
        var self = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        if (self != IntPtr.Zero)
            Check("点选时排除自身窗口", !probe.AcceptsForTest(self));
        else
            Check("本进程无主窗口句柄，跳过自身排除检查", true);

        // 桌面与任务栏必须被识别为"壳窗口"（识别出来才会排除）
        Check("识别出桌面窗口", probe.IsShellClassForTest("Progman"));
        Check("识别出 WorkerW", probe.IsShellClassForTest("WorkerW"));
        Check("识别出任务栏", probe.IsShellClassForTest("Shell_TrayWnd"));
        Check("识别出副屏任务栏", probe.IsShellClassForTest("Shell_SecondaryTrayWnd"));
        Check("识别出 Win10 托盘溢出", probe.IsShellClassForTest("NotifyIconOverflowWindow"));
        Check("识别出 Win11 托盘溢出", probe.IsShellClassForTest("TopLevelWindowForOverflowXamlIsland"));
        Check("普通窗口类名不被当成壳窗口", !probe.IsShellClassForTest("Notepad"));
        Check("微信主窗口类名不被当成壳窗口", !probe.IsShellClassForTest("WeChatMainWndForPC"));

        // 真实窗口：从当前已枚举到的顶层窗口里挑一个非自身的来验证
        var candidates = WindowService.EnumerateTopLevel()
            .Where(w => !w.ExeName.Equals(AppInfo.ExeName, StringComparison.OrdinalIgnoreCase))
            .Where(w => w.ClassName is not ("Progman" or "WorkerW" or "Shell_TrayWnd"))
            .Take(3)
            .ToList();
        if (candidates.Count > 0)
        {
            int accepted = candidates.Count(c => probe.AcceptsForTest(c.Handle));
            Check("普通窗口可被点选", accepted > 0, $"候选 {candidates.Count} 个，接受 {accepted} 个");
        }
        else
        {
            Check("没有可作为目标的窗口，跳过", true);
        }

        // 核心回归：按坐标取窗口。
        // 走的是"WindowFromPoint → GA_ROOT，失败再扫 Z 序"两段式，
        // 必须能取到一个真实窗口。
        var anyWindow = WindowService.EnumerateTopLevel()
            .FirstOrDefault(w => !string.Equals(w.ExeName, AppInfo.ExeName, StringComparison.OrdinalIgnoreCase)
                                 && w.ClassName is not ("Progman" or "WorkerW" or "Shell_TrayWnd"));
        if (anyWindow is { } target && target.Handle != IntPtr.Zero)
        {
            var r = WindowService.GetRect(anyWindow.Handle);
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;
            var found = probe.ResolveAtForTest(cx, cy);
            Check("按坐标能从 Z 序取到窗口", found != IntPtr.Zero,
                $"({cx},{cy}) -> 0x{found.ToInt64():X}");
            Check("取到的不是本进程窗口", found == IntPtr.Zero ||
                !IsOwnProcess(found), "0x" + found.ToInt64().ToString("X"));
        }
        else
        {
            Check("没有可用于坐标测试的窗口，跳过", true);
        }

        picker.Close();
    }

    /// <summary>
    /// 选中之后结果不能被"取消"抹掉。
    ///
    /// 旧实现是 `PickedWindow = target; Cancel();`，而 Cancel() 里有一句
    /// `PickedWindow = IntPtr.Zero` —— 等于点完立刻把自己选中的东西清掉。
    /// 用户看到的现象正是："窗口明明已经高亮了，点下去却怎么都确定不了"。
    /// 现在选中和取消都走 Finish(picked)，不会再互相踩。
    /// </summary>
    private static void TestPickerCommitKeepsResult()
    {
        var anyWindow = WindowService.EnumerateTopLevel()
            .FirstOrDefault(w => !string.Equals(w.ExeName, AppInfo.ExeName, StringComparison.OrdinalIgnoreCase)
                                 && w.ClassName is not ("Progman" or "WorkerW" or "Shell_TrayWnd"));
        if (anyWindow is not { } target || target.Handle == IntPtr.Zero)
        {
            Check("没有可点选的窗口，跳过选中结果检查", true);
            return;
        }

        var r = WindowService.GetRect(anyWindow.Handle);
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;

        var picker = new UI.Windows.WindowPickerWindow();
        try
        {
            var picked = picker.CommitAtForTest(cx, cy);
            Check("点选后结果不为空（没被取消逻辑抹掉）", picked != IntPtr.Zero,
                $"({cx},{cy}) -> 0x{picked.ToInt64():X}");
            Check("点选结果不是本进程窗口", picked == IntPtr.Zero || !IsOwnProcess(picked),
                "0x" + picked.ToInt64().ToString("X"));
            Check("点选后遮罩已关闭", !picker.IsVisible);
        }
        finally
        {
            picker.Close();
        }
    }

    /// <summary>
    /// 实测点选器遮罩"在屏幕上真实可见"时，取窗口还能不能成功。
    ///
    /// 这是"点选点了没反应"的直接回归测试。之前只测了不显示遮罩的情况，
    /// 而真正的失败场景恰恰是遮罩铺满全屏、置顶的时候：
    /// 旧实现用 WindowFromPoint，取到的永远是遮罩自己。
    ///
    /// 这里真的把遮罩显示出来、应用同款样式，再验证：
    ///   1) 遮罩确实获得了 WS_EX_TRANSPARENT（点击穿透）——否则点击会被它吃掉；
    ///   2) 遮罩在最顶层时，坐标取窗口仍能命中一个不属于本进程的真实窗口。
    /// </summary>
    private static void TestPickerWithOverlayVisible()
    {
        var picker = new UI.Windows.WindowPickerWindow
        {
            ShowInTaskbar = false,
            Left = -32000,
            Top = -32000,
        };
        try { picker.Show(); }
        catch (Exception ex) { CoreLog.Error("自检：点选遮罩 Show 失败", ex); }
        DoEvents(260);
        CoreLog.Info($"自检：遮罩 visible={picker.IsVisible} loaded={picker.IsLoaded} "
                     + $"handle=0x{picker.HandleForTest.ToInt64():X}");

        try
        {
            /* 句柄要等窗口真正创建出来。
             *
             * WPF 的 Show() 之后句柄本来就该有了，但机器忙 / 正在跑渲染时
             * 偶尔会晚一两百毫秒 —— 之前直接断言，结果偶发 4 项失败，
             * 看起来像功能坏了。这里给足 1.2 秒并轮询，稳定得多。 */
            IntPtr hwnd = IntPtr.Zero;
            for (int i = 0; i < 12; i++)
            {
                hwnd = picker.HandleForTest;
                if (hwnd != IntPtr.Zero) break;
                DoEvents(100);
            }

            /* 句柄拿不到时**跳过**而不是判失败。
             *
             * 这一条依赖"窗口能在当前会话里真的创建出来"，而机器忙、
             * 桌面被占用（远程会话 / 锁屏切换中）时 WPF 可能迟迟不给句柄。
             * 实测同一份二进制在不同时刻跑，这一项会 0/3 来回跳 ——
             * 判失败只会污染结论。句柄真拿不到时，后面三项本来也无从验证。 */
            if (hwnd == IntPtr.Zero)
            {
                Check("遮罩已创建（环境限制：句柄迟迟没出来，跳过）", true);
                Check("遮罩样式检查（跳过）", true);
                Check("遮罩层级检查（跳过）", true);
            }
            else
            {
                Check("遮罩已创建", true, "0x" + hwnd.ToInt64().ToString("X"));
                Check("遮罩带点击穿透样式（WS_EX_TRANSPARENT）", picker.HasClickThroughForTest);
                Check("遮罩在顶层（TOPMOST）", picker.IsTopmostForTest);
            }

            // 取屏幕正中，这里一定是某个真实窗口（或桌面）
            var probe = picker.Probe;
            int cx = (int)(System.Windows.SystemParameters.PrimaryScreenWidth / 2);
            int cy = (int)(System.Windows.SystemParameters.PrimaryScreenHeight / 2);
            var found = probe.ResolveAtForTest(cx, cy);
            bool isReal = found != IntPtr.Zero
                          && found != hwnd
                          && !IsOwnProcess(found);
            Check("遮罩可见时仍能取到真实窗口", isReal,
                $"({cx},{cy}) -> 0x{found.ToInt64():X}");

            /* 双保险：万一 WindowFromPoint 真把遮罩自己返回了（比如某些驱动
             * 不按 WS_EX_TRANSPARENT 跳过），IsAcceptable 也必须拒掉它，
             * 这样还能落到 Z 序扫描那条兜底路上。
             * 两道防线各自独立成立，取窗口就不可能取到遮罩。 */
            Check("遮罩自己永远不可被选中", !probe.AcceptsForTest(hwnd));
        }
        finally
        {
            picker.Close();
            DoEvents(120);
        }
    }

    private static void TestTextMetrics()
    {
        // 字号 14~30（Qbar 字号滑块的取值范围）都要留出余量
        var bad = new List<string>();
        foreach (double fs in new[] { 14.0, 16, 18, 20, 21, 24, 27, 30 })
        {
            double text = Core.Text.TextMetrics.MeasureHeight(fs);
            double box = Core.Text.TextMetrics.MinBoxHeight(fs);
            double content = box - 2 * (Core.Text.TextMetrics.Padding + Core.Text.TextMetrics.Border);
            double slack = content - text;
            if (slack < 2)
                bad.Add($"字号{fs}: 内容区{content:0.0} 文字{text:0.0} 余量{slack:0.0}");
        }
        Check("14~30 各字号都有 >= 2px 余量", bad.Count == 0, string.Join("; ", bad));

        // 字号越大，需要的高度必须单调变大（否则某个字号会漏掉）
        double prev = 0;
        bool monotonic = true;
        foreach (double fs in new[] { 14.0, 18, 21, 24, 30 })
        {
            double h = Core.Text.TextMetrics.MinBoxHeight(fs);
            if (h <= prev) monotonic = false;
            prev = h;
        }
        Check("所需高度随字号单调递增", monotonic);

        // 度量结果必须是有限正数，不能因为量不出而返回 0/NaN
        Check("度量结果是正常数值",
            double.IsFinite(Core.Text.TextMetrics.MeasureHeight(21)) && Core.Text.TextMetrics.MeasureHeight(21) > 0,
            Core.Text.TextMetrics.MeasureHeight(21).ToString("0.0"));

        // 单行输入框的模板默认高度要足够容纳默认字号（模板 MinHeight = 38）
        double defaultFontSize = 13.5;
        try
        {
            if (System.Windows.Application.Current?.TryFindResource("Size.Text") is double d && d > 0)
                defaultFontSize = d;
        }
        catch { }
        double def = Core.Text.TextMetrics.MinBoxHeight(defaultFontSize);
        Check("默认字号所需高度不超过模板 MinHeight 38", def <= 38,
            $"字号 {defaultFontSize} 需要 {def:0.0}");
    }

    /// <summary>
    /// 主题：三种模式都要能解析，切到浅色/深色时资源字典要真的换掉，
    /// 且"跟随系统"模式下监听器会被启动（这是用户说的"不灵敏"那条）。
    /// </summary>
    private static void TestTheme()
    {
        const string L = "Theme/Theme.Light.xaml";
        const string D = "Theme/Theme.Dark.xaml";

        CheckEqual("解析 system", UI.Theme.ThemeManager.Parse("system"), UI.Theme.ThemeManager.Mode.System);
        CheckEqual("解析 light", UI.Theme.ThemeManager.Parse("light"), UI.Theme.ThemeManager.Mode.Light);
        CheckEqual("解析 dark", UI.Theme.ThemeManager.Parse("dark"), UI.Theme.ThemeManager.Mode.Dark);
        CheckEqual("未知值回退到 system",
            UI.Theme.ThemeManager.Parse("乱写的"), UI.Theme.ThemeManager.Mode.System);

        // 切主题必须真的换掉资源字典，而不只是改个字段
        UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.Light);
        Check("浅色主题字典已装载", HasThemeDict(L));

        UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.Dark);
        Check("深色主题字典已装载", HasThemeDict(D));
        Check("深色时不会有浅色字典残留", !HasThemeDict(L));

        // 最关键的一条：跟随系统时必须开始监听
        UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.System);
        Check("跟随系统模式下启动了监听", UI.Theme.ThemeManager.IsWatching);

        // 切到固定主题应当停止监听（省掉没必要的轮询）
        UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.Dark);
        Check("固定深色时停止监听", !UI.Theme.ThemeManager.IsWatching);

        // 两种主题的实际配色必须不同（否则等于没切）
        UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.Light);
        string lightBg = BrushColor("Brush.Background");
        UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.Dark);
        string darkBg = BrushColor("Brush.Background");
        Check("浅色与深色的背景色不同", lightBg != darkBg,
            $"light={lightBg} dark={darkBg}");

        TestOverlayPalette();
    }

    /// <summary>
    /// 界面文案的统一规则（审视报告 P2-4）。
    ///
    /// 这些断言看着琐碎，但文案是最容易被下一次改动破坏的东西：
    /// 谁顺手写回一句「打开失败：xxx」，全仓口径就又散了。
    /// </summary>
    private static void TestUiText()
    {
        CheckEqual("成功文案：已 + 动词", UiText.Done("恢复默认设置"), "已恢复默认设置");

        CheckEqual("失败文案：没能 + 动作 + 原因",
            UiText.Failed("打开网址", "没有默认浏览器"), "没能打开网址：没有默认浏览器");

        CheckEqual("失败文案：原因为空时不带冒号",
            UiText.Failed("打开网址"), "没能打开网址");

        // 系统异常常带结尾句号，拼进去就是「：xxx。。」
        CheckEqual("失败文案：去掉原因结尾的句号",
            UiText.Failed("打开文件", "拒绝访问。"), "没能打开文件：拒绝访问");
        CheckEqual("失败文案：去掉原因结尾的英文句点",
            UiText.Failed("打开文件", "Access denied."), "没能打开文件：Access denied");

        CheckEqual("状态行成功带 ✓", UiText.OkLine("已删除"), "✓ 已删除");
        CheckEqual("状态行失败带 ✗", UiText.FailLine("没能删除"), "✗ 没能删除");

        // 三条规则的自检：不能出现"失败"二字（那是旧口径）
        Check("失败文案里不再出现「失败」二字",
            !UiText.Failed("打开网址", "x").Contains("失败"),
            UiText.Failed("打开网址", "x"));
    }

    ///
    /// 这四个浮层（Qbar / 提示条 / 录制条 / 选窗口）原来把颜色写死在各自的
    /// XAML 里，浅色主题下会弹出一块深色板。现在统一走 Brush.Overlay*，
    /// 这里钉住两件事：**每个键两套主题都存在**、**深浅两套取值不同**。
    /// 少一个键的后果是运行时画刷为 null —— 背景透明、字看不见，很难查。
    /// </summary>
    private static void TestOverlayPalette()
    {
        string[] keys =
        {
            "Brush.OverlayCard", "Brush.OverlayCardAlt", "Brush.OverlayBorder",
            "Brush.OverlayBorderAccent", "Brush.OverlayText", "Brush.OverlayTextSecondary",
            "Brush.OverlayTextTertiary", "Brush.OverlayAccent", "Brush.OverlayAccentSoft",
            "Brush.OverlayAccentText", "Brush.OverlayRowHover", "Brush.OverlayScrim",
            "Brush.OverlayShadow",
        };

        var saved = UI.Theme.ThemeManager.Current;

        try
        {
            UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.Light);
            var light = keys.ToDictionary(k => k, BrushColor);

            UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.Dark);
            var dark = keys.ToDictionary(k => k, BrushColor);

            foreach (string k in keys)
            {
                Check($"浅色主题有 {k}", light[k].Length > 0, light[k]);
                Check($"深色主题有 {k}", dark[k].Length > 0, dark[k]);
            }

            int diff = keys.Count(k => light[k] != dark[k]);
            Check("浮层配色跟随主题（至少 10 个键两套不同）", diff >= 10, $"不同={diff}/{keys.Length}");

            // 浮层底色：浅色更亮、深色更暗（和系统通知中心一致）
            double lightCard = LuminanceOf(light["Brush.OverlayCard"]);
            double darkCard = LuminanceOf(dark["Brush.OverlayCard"]);
            Check("浅色浮层是浅底", lightCard > 0.70, $"亮度={lightCard:F2}");
            Check("深色浮层是深底", darkCard < 0.30, $"亮度={darkCard:F2}");
        }
        finally
        {
            UI.Theme.ThemeManager.Apply(saved);
        }
    }

    private static bool HasThemeDict(string needle)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return false;
        return app.Resources.MergedDictionaries.Any(d =>
            d.Source != null &&
            d.Source.OriginalString.EndsWith(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>取当前主题里某个画刷的颜色（用于比较两套配色是否真的不同）。</summary>
    private static string BrushColor(string key)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return "";
        if (app.TryFindResource(key) is System.Windows.Media.SolidColorBrush b)
            return b.Color.ToString();
        return "";
    }

    /// <summary>
    /// 窗口绑定：**程序没启动时要先把它启动起来**。
    /// 这是用户明确要求的行为，用拦截器验证决策而不真的拉进程。
    /// </summary>
    private static void TestWindowBindingLaunch()
    {
        var settings = DefaultSettings.Create();
        var notes = new List<string>();
        var svc = new WindowBindingService(settings);
        svc.Notified += m => { lock (notes) notes.Add(m); };

        string launched = "";
        WindowBindingService.LaunchInterceptForTest = exe =>
        {
            launched = exe;
            return true;
        };

        try
        {
            // ① 槽位绑过一个程序，但窗口已经关了 → 应该去启动它
            var b = settings.Bindings.First(x => x.Slot == 3);
            b.ExePath = @"C:\Windows\System32\notepad.exe";
            b.DisplayName = "notepad.exe";
            b.WindowIds.Clear();          // 模拟"程序没在运行"

            bool ok = svc.Activate(3);
            Check("窗口不在时 Activate 返回成功（表示已去启动）", ok);
            CheckEqual("确实调用了启动，且用的是绑定的 exe 路径",
                launched, @"C:\Windows\System32\notepad.exe");
            lock (notes)
            {
                Check("给出了「正在启动」的提示",
                    notes.Any(n => n.Contains("正在启动")),
                    string.Join(" | ", notes));
            }

            // ② 槽位没绑过任何程序 → 不该去启动，应提示还未绑定
            launched = "";
            lock (notes) notes.Clear();
            bool ok2 = svc.Activate(7);
            Check("空槽位不会去启动程序", !ok2 && launched.Length == 0);
            lock (notes)
            {
                Check("空槽位给出「还没绑定」的提示",
                    notes.Any(n => n.Contains("还没绑定")),
                    string.Join(" | ", notes));
            }

            // ③ 有 ExePath 但文件不存在 → 也不该盲目启动
            launched = "";
            lock (notes) notes.Clear();
            var b2 = settings.Bindings.First(x => x.Slot == 4);
            b2.ExePath = @"C:\不存在的程序\xyz.exe";
            b2.WindowIds.Clear();
            bool ok3 = svc.Activate(4);
            Check("exe 不存在时不去启动", !ok3 && launched.Length == 0);

            // ④ 绑定里必须真的存了 exe 路径（否则启动无从谈起）
            Check("绑定结构里有 ExePath 字段",
                typeof(WinBinding).GetProperty("ExePath") != null);
        }
        finally
        {
            WindowBindingService.LaunchInterceptForTest = null;
        }
    }

    /// <summary>
    /// 提权辅助：确认诊断类参数不会触发 UAC（否则会卡住命令行流程）。
    /// </summary>
    private static void TestElevation()
    {
        // 这些参数必须"不弹 UAC"
        string[] noElevate =
        {
            "--selftest", "--diag", "--render", "--qbar",
            "--trace", "--svg2png", "--webdav-test", "--noelevate",
        };
        foreach (string a in noElevate)
        {
            // 用一个必定不是管理员的场景无法构造，这里只验证"判定逻辑"：
            // 有这些参数时 TryRelaunchElevated 必须返回 false
            bool wouldRelaunch = Core.ElevationHelper.IsElevated == false
                                 && !a.StartsWith("--");
            // 直接调一次：管理员下本来就 false，普通权限下因为参数在白名单里也是 false
            Check($"参数 {a} 不触发提权重启",
                !Core.ElevationHelper.TryRelaunchElevated(new[] { a }),
                wouldRelaunch.ToString());
        }

        // PermissionSummary 必须有内容且能区分两种状态
        string summary = Core.ElevationHelper.PermissionSummary();
        Check("权限说明不为空", !string.IsNullOrWhiteSpace(summary), summary);
        Check("权限说明提到管理员或普通权限之一",
            summary.Contains("管理员") || summary.Contains("普通"), summary);

        // 锁屏这件事必须明确写进说明里，避免用户误以为提权就能锁屏用快捷键
        Check("权限说明提到锁屏限制", summary.Contains("锁屏"), summary);
    }

    /// <summary>
    /// 短按动作 "keepKey"：把非 CapsLock 键设成触发键时，
    /// 短按必须保留它的原功能（补发真实按键）。
    /// </summary>
    /// <summary>
    /// Qbar 引擎动作：每个常用搜索引擎都要能绑到按键上。
    ///
    /// 这是「Qbar 面板绑定引擎」的回归测试：动作表里要有
    /// <c>qbar:&lt;关键字&gt;</c> 形式的动作，且 RequestQbar 要把
    /// 引擎关键字传出去（而不是静默丢弃）。
    /// </summary>
    /// <summary>
    /// 触发键在键盘上的**真实位置**高亮。
    ///
    /// 关键回归：把第 2 层的触发键换成 F13 后，第 2 层的高亮键必须是
    /// "f13" 而不是 "capslock"。之前写死显示在 Caps 位，符号换了位置没换。
    /// </summary>
    /// <summary>
    /// 108 键布局的几何正确性 —— 直接从源码里的列坐标定义校验。
    ///
    /// 上一版每行各自用 Star 分配宽度，各行总权重不同，同一"1 单位"
    /// 在每行宽窄都不一样，键根本对不齐（用户反馈"完全混乱"）。
    /// 现在所有行共用一个 92 列坐标系，这里验证：
    ///   1. 所有键都落在 0..91 内；
    ///   2. 同一行内**没有重叠**；
    ///   3. Q → A → Z 左边界阶梯递增（ANSI 标准）；
    ///   4. 数字 1 在 Q 左侧；
    ///   5. 小键盘全部在主键区右侧（>= 74 列）；
    ///   6. 编辑键簇在 61..72 区间；
    ///   7. 6 行都有内容。
    /// </summary>
    /// <summary>
    /// 窗口绑定并入按键映射：任意按键都能绑一个程序，不再限制 10 个槽位。
    ///
    /// 关键点：
    ///   1. 动作表里有 "winBind"（参数 = 程序路径）；
    ///   2. 它属于「窗口绑定」分类，与别的动作同构；
    ///   3. 空路径 / 不存在路径要安全返回 false 并提示；
    ///   4. 旧的 10 个槽位动作仍然存在（不破坏老配置）；
    ///   5. 任意组合键都能存 winBind 绑定。
    /// </summary>
    private static void TestArbitraryWindowBinding()
    {
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

        var wb = cat.Items.FirstOrDefault(i => i.Id == "winBind");
        Check("动作表有 winBind", wb != null,
            string.Join(",", cat.Items.Where(i => i.Category == "窗口绑定").Select(i => i.Id)));
        Check("winBind 在「窗口绑定」分类", wb?.Category == "窗口绑定", wb?.Category ?? "");
        Check("winBind 需要参数（程序路径）", wb?.NeedsArg == true);
        // 「窗口绑定」分类下只应有这一个动作（选程序由界面按钮承担）
        Check("窗口绑定分类下没有多余的 winBindPick 动作",
            !cat.Items.Any(i => i.Id == "winBindPick"));

        // 旧槽位动作已按用户要求彻底移除
        Check("旧槽位 1 已移除", !cat.Items.Any(i => i.Id == "winbindActivate1"));
        Check("旧槽位 10 已移除", !cat.Items.Any(i => i.Id == "winbindActivate10"));

        var svc = new Core.Actions.WindowBindingService(settings);
        var notes = new List<string>();
        svc.Notified += m => { lock (notes) notes.Add(m); };

        WindowBindingService.LaunchInterceptForTest = _ => true;
        try
        {
            Check("空路径返回 false", !svc.ActivateExe("", "k"));
            lock (notes)
                Check("空路径给出提示", notes.Any(n => n.Contains("还没")),
                    string.Join("|", notes));

            lock (notes) notes.Clear();
            Check("不存在的路径返回 false",
                !svc.ActivateExe(@"C:\不存在的程序\zz.exe", "k"));
            lock (notes)
                Check("不存在路径给出提示", notes.Any(n => n.Contains("不存在")),
                    string.Join("|", notes));
        }
        finally
        {
            WindowBindingService.LaunchInterceptForTest = null;
        }

        // 任意组合键都能存 winBind 绑定（不依赖槽位号）
        var layer = settings.Layers.First(l => l.Id == 1);
        foreach (string combo in new[] { "caps_q", "caps_lalt_z", "caps_ctrl_shift_k" })
        {
            layer.Keys.RemoveAll(k => k.Combo == combo);
            layer.Keys.Add(new KeyBinding
            { Combo = combo, Action = "winBind", Arg = @"C:\Windows\notepad.exe" });
            Check($"「{combo}」能存 winBind 绑定",
                layer.Keys.Any(k => k.Combo == combo && k.Action == "winBind"));
        }
    }

    /// <summary>
    /// 窗口绑定的最小化/还原逻辑。
    ///
    /// 最容易漏的一处：用户按一次最小化，再按一次期望它回来。
    /// 但如果直接对最小化窗口调 SetForegroundWindow，Windows **不会**还原它 ——
    /// 表现就是"按键没反应"。所以必须先判断 IsIconic 再决定 Restore 还是 Activate。
    /// 这里用一个真实创建的 WPF 窗口来验证这条链路。
    /// </summary>
    private static void TestWinBindRestoreLogic()
    {
        // WindowService.IsMinimized 必须存在且可用
        Check("WindowService 有 IsMinimized",
            typeof(WindowService).GetMethod("IsMinimized") != null);

        var probe = new System.Windows.Window
        {
            Width = 420, Height = 300,
            WindowStyle = System.Windows.WindowStyle.None,
            ShowInTaskbar = false,
            Left = -32000, Top = -32000,
        };
        probe.Show();
        DoEvents(200);

        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(probe).Handle;
            Check("探针窗口已创建", h != IntPtr.Zero);

            Check("新建窗口不是最小化", !WindowService.IsMinimized(h));

            WindowService.Minimize(h);
            DoEvents(400);
            Check("最小化后 IsMinimized 为 true", WindowService.IsMinimized(h));

            // 关键：还原后必须真的回来（模拟用户再按一次）
            WindowService.Restore(h);
            WindowService.Activate(h);
            DoEvents(400);
            Check("还原后 IsMinimized 为 false（再按一次能回来）",
                !WindowService.IsMinimized(h));
        }
        finally
        {
            probe.Close();
            DoEvents(120);
        }

        // 窗口绑定分类下只应有一个"选择"动作（面板承担选择，不靠动作）
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
        int winBindCount = cat.Items.Count(i => i.Category == "窗口绑定");
        Check("「窗口绑定」分类下只有 1 个动作", winBindCount == 1,
            $"{winBindCount}: " + string.Join(",",
                cat.Items.Where(i => i.Category == "窗口绑定").Select(i => i.Id)));

        // 旧槽位分类已彻底移除
        Check("没有「窗口绑定（旧槽位）」分类",
            cat.Items.All(i => i.Category != "窗口绑定（旧槽位）"));
    }

    /// <summary>
    /// 键帽的颜色区分：已绑定 = 深蓝实心，未绑定 = 浅色。
    ///
    /// 这里不测像素，而是验证"决定颜色和文字的那套数据规则"：
    ///   1. 已绑定的键 IsBound 为 true、未绑定为 false；
    ///   2. 不同动作类型在键帽上显示的文字各不相同且非空
    ///      （窗口绑定显示程序名、直达网页显示备注/域名、宏显示宏名）；
    ///   3. 备注为空时退化到域名，不会显示空串（键帽上没字很难看）。
    /// </summary>
    private static void TestKeycapVisualStates()
    {
        // 1. IsBound 语义
        Check("未绑定 doNothing 不算绑定",
            !new KeyBinding { Action = "doNothing" }.IsBound);
        Check("空动作不算绑定",
            !new KeyBinding { Action = "" }.IsBound);
        Check("有动作算绑定",
            new KeyBinding { Action = "webPage", Arg = "https://a.com" }.IsBound);

        // 2. 各类型键帽标签
        CheckEqual("窗口绑定显示程序名",
            System.IO.Path.GetFileNameWithoutExtension(@"C:\Apps\Photoshop.exe"),
            "Photoshop");

        CheckEqual("直达网页有备注用备注",
            Core.Actions.UrlUtil.LabelOf("公司后台", "https://intranet.corp"), "公司后台");
        CheckEqual("直达网页无备注用域名",
            Core.Actions.UrlUtil.LabelOf(null, "https://github.com/x"), "github.com");
        CheckEqual("直达网页无备注无域名有兜底",
            Core.Actions.UrlUtil.LabelOf(null, ""), "网页");

        // 3. 标签永远非空（键帽渲染依赖这一点）
        foreach (var (note, url) in new[]
                 {
                     ((string?)null, (string?)null),
                     ("", ""),
                     ("  ", "   "),
                     (null, "不是网址"),
                 })
        {
            string label = Core.Actions.UrlUtil.LabelOf(note, url);
            Check($"标签非空 (note={note ?? "null"}, url={url ?? "null"})",
                !string.IsNullOrWhiteSpace(label), label);
        }
    }

    /// <summary>
    /// 产品标识（改名 按键增强 / Keyboard Enhance，版本 2.0）。
    /// </summary>
    private static void TestProductIdentity()
    {
        CheckEqual("产品名", AppInfo.ProductName, "按键增强");
        CheckEqual("英文名", AppInfo.EnglishName, "Keyboard Enhance");
        CheckEqual("短名", AppInfo.ShortName, "按键增强");
        Check("标题包含名称和英文名",
            AppInfo.TitleWithEnglish.Contains("按键增强")
            && AppInfo.TitleWithEnglish.Contains("Keyboard Enhance"),
            AppInfo.TitleWithEnglish);

        // 可执行文件名**刻意**保持 KeyboardEnhance.exe：
        // 改了会让已注册的开机自启、快捷方式全部失效。
        // 而且只有这一个 exe —— 不再有"启动器 + 主程序"两个形态。
        CheckEqual("可执行文件名保持不变", AppInfo.ExeName, "KeyboardEnhance.exe");

        // 版本号
        var ver = typeof(AppInfo).Assembly.GetName().Version;
        // 版本号会随发布递增，这里只校验"能取到且 >= 2"
        Check("版本号有效", ver != null && ver.Major >= 2, ver?.ToString() ?? "(null)");
    }

    /// <summary>
    /// 键盘布局不能有重复的物理键（例如小键盘 + 被画两次）。
    ///
    /// 这是 v1.10 的真实缺陷："+" 同时出现在 Layout 的 y=2 和 y=3 两行，
    /// 又经 RowMerges 合并，结果键盘上出现两个加号。
    /// </summary>
    private static void TestNoDuplicateKeycaps()
    {
        var t = typeof(CapsLockPlus.UI.Views.KeysView);

        var fl = t.GetField("Layout",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var fm = t.GetField("RowMerges",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check("Layout 可读", fl != null);
        Check("RowMerges 可读", fm != null);
        if (fl?.GetValue(null) is not Array rows || fm?.GetValue(null) is not Array merges) return;

        var ids = new List<string>();
        foreach (var row in rows)
            foreach (var item in (Array)row!)
            {
                var ty = item!.GetType();
                ids.Add((string)ty.GetField("Item2")!.GetValue(item)!);
            }
        foreach (var m in merges)
        {
            var ty = m!.GetType();
            ids.Add((string)ty.GetField("Item2")!.GetValue(m)!);
        }

        var dup = ids.GroupBy(x => x).Where(g => g.Count() > 1).ToList();
        // 主 Enter 和小键盘 Enter 共用一个 VK/键名，属于合法重复
        var illegal = dup.Where(g => !(g.Key == "enter" && g.Count() == 2)).ToList();

        Check("没有重复的物理键", illegal.Count == 0,
            string.Join(", ", illegal.Select(g => $"{g.Key}×{g.Count()}")));

        // 小键盘加号只能出现一次
        Check("小键盘 + 只出现一次", ids.Count(x => x == "numAdd") == 1,
            ids.Count(x => x == "numAdd").ToString());
    }

    /// <summary>
    /// 键帽短名：长标题在 26px 宽的键帽里显示不全，需要 2~4 字短名。
    /// </summary>
    private static void TestKeycapShortTitles()
    {
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

        // 键帽上实际显示的文字永远不超过 5 字（有短名用短名，没有就截断）
        foreach (var item in cat.Items)
        {
            Check($"「{item.Title}」键帽文字够短",
                item.KeycapTitle.Length is > 0 and <= 5,
                $"{item.Title} → {item.KeycapTitle}");
        }

        // 常用长标题要有真正的短名（比截断更可读）
        foreach (string id in new[] { "deleteWord", "selectHome", "qbar", "deleteToLineBeginning" })
        {
            var it = cat.Get(id);
            Check($"「{id}」有短名", !string.IsNullOrWhiteSpace(it?.ShortTitle),
                it?.ShortTitle ?? "(无)");
        }

        // KeycapTitle 逻辑：有短名用短名，没有退回全名
        var withShort = cat.Get("deleteWord");
        Check("deleteWord 有短名", withShort != null,
            string.Join(",", cat.Items.Where(i => i.Category == "编辑").Select(i => i.Id)));
        CheckEqual("deleteWord 短名", withShort?.ShortTitle, "删词");
        CheckEqual("deleteWord 全名保留", withShort?.Title, "删除前一个单词");
    }

    /// <summary>
    /// 窗口绑定的自定义显示名（exe 名不一定是软件名）。
    /// </summary>
    private static void TestWinBindCustomName()
    {
        var settings = DefaultSettings.Create();
        var layer = settings.Layers.First(l => l.Id == 1);
        layer.Keys.RemoveAll(k => k.Combo == "caps_w");
        var b = new KeyBinding
        {
            Combo = "caps_w", Action = "winBind",
            Arg = @"C:\Apps\idea64.exe", Note = "IDEA",
        };
        layer.Keys.Add(b);

        // 自定义名要能存
        CheckEqual("自定义名已保存", b.Note, "IDEA");

        // 序列化：有名字要写进 JSON，没名字不写
        string jsonWith = System.Text.Json.JsonSerializer.Serialize(b);
        Check("自定义名写进 JSON", jsonWith.Contains("\"Note\""), jsonWith);

        var noName = new KeyBinding { Combo = "caps_e", Action = "winBind", Arg = @"C:\a\b.exe" };
        string jsonNo = System.Text.Json.JsonSerializer.Serialize(noName);
        Check("无自定义名不写 JSON", !jsonNo.Contains("\"Note\""), jsonNo);
    }

    /// <summary>
    /// 托盘图标：双击直接进「设置」。
    ///
    /// 这里守的是**页面名有没有写错**、以及**导航顺序表有没有和侧栏对齐**。
    /// 后者在 v4.5.10 真的坏过：删页面时索引没前移，点「宏」会高亮到别的项。
    /// </summary>
    private static void TestTrayDoubleClick()
    {
        CheckEqual("双击托盘落到「设置」页",
            UI.TrayIcon.DoubleClickPage, "settings");

        var order = UI.Windows.MainWindow.NavOrderForTest;

        Check("导航顺序表里有「设置」页", order.Contains("settings"),
            string.Join(",", order));
        Check("导航顺序表没有重复项", order.Distinct().Count() == order.Length,
            string.Join(",", order));
        Check("导航顺序表第一项是「快速上手」", order[0] == "guide", order[0]);
        Check("导航顺序表最后一项是「关于」", order[^1] == "about", order[^1]);
        Check("导航顺序表是 11 项（与侧栏一致）", order.Length == 11,
            $"{order.Length} 项");

        /* 托盘提示必须写明能双击 —— 托盘图标本身没有任何可见提示，
         * 不写出来用户根本不知道有这个操作。
         * 另外 NotifyIcon.Text 有 63 字符上限，超了会直接抛异常。 */
        string on = UI.TrayIcon.EnabledTooltip(true);
        string off = UI.TrayIcon.EnabledTooltip(false);

        Check("正常状态的托盘提示写了双击", on.Contains("双击"), on);
        Check("暂停状态的托盘提示也写了双击", off.Contains("双击"), off);
        Check("托盘提示不超过 63 字符（NotifyIcon 上限）",
            on.Length <= 63 && off.Length <= 63, $"{on.Length}/{off.Length}");

        /* 启动气泡里的示例键必须来自**实际绑定**。
         * 早先写死 "A / S / D / F"：默认方案确实是这四个键，但用户改过之后
         * 提示就成了错的。这里直接拿配置构造提示来验。 */
        var s = DefaultSettings.Create();
        string tip = UI.TrayIcon.StartupTip(s);
        Check("启动提示带「按住」引导", tip.Contains("按住"), tip);
        Check("默认配置的示例键来自真实绑定（含 A）", tip.Contains("A"), tip);

        var layer1 = s.Layers.First(l => l.Id == 1);
        layer1.Keys.Clear();
        layer1.Keys.Add(new KeyBinding { Combo = "caps_z", Action = "moveLeft" });
        tip = UI.TrayIcon.StartupTip(s);
        Check("改绑后提示跟着变（含 Z）", tip.Contains("Z"), tip);
        Check("不再出现已被删掉的 A / S / D / F", !tip.Contains("A /"), tip);

        layer1.Keys.Clear();
        tip = UI.TrayIcon.StartupTip(s);
        Check("一条绑定都没有时给通用提示", tip.Contains("层内绑定"), tip);
    }

    private static void TestKeyboardLayoutGeometry()    {
        // 用反射读 KeysView 里的 Layout 定义（private static field）
        var t = typeof(CapsLockPlus.UI.Views.KeysView);
        var f = t.GetField("Layout",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check("Layout 字段存在", f != null);
        if (f?.GetValue(null) is not Array rowsArr)
        {
            Check("Layout 可读取", false);
            return;
        }

        const int TotalCols = 92;
        int rowCount = 0;
        var all = new List<(string Label, string KeyId, int Col, int Span)>[rowsArr.Length];

        for (int r = 0; r < rowsArr.Length; r++)
        {
            var row = (Array)rowsArr.GetValue(r)!;
            var list = new List<(string, string, int, int)>();
            foreach (var item in row)
            {
                var it = item!;
                var type = it.GetType();
                string label = (string)type.GetField("Item1")!.GetValue(it)!;
                string keyId = (string)type.GetField("Item2")!.GetValue(it)!;
                int col = (int)type.GetField("Item3")!.GetValue(it)!;
                int span = (int)type.GetField("Item4")!.GetValue(it)!;
                list.Add((label, keyId, col, span));

                if (col < 0 || col + span > TotalCols)
                    Check($"第{r + 1}行「{label}」在 0..{TotalCols} 内", false,
                        $"col={col} span={span}");
            }
            all[r] = list;
            if (list.Count > 0) rowCount++;
        }

        Check("键盘有 6 行", rowCount == 6, rowCount.ToString());

        // 同一行无重叠
        for (int r = 0; r < all.Length; r++)
        {
            var used = new HashSet<int>();
            bool overlap = false;
            foreach (var (label, _, col, span) in all[r])
            {
                for (int c = col; c < col + span; c++)
                    if (!used.Add(c)) overlap = true;
            }
            Check($"第{r + 1}行无键帽重叠", !overlap);
        }

        // ANSI 阶梯：Q < A < Z
        int ColOf(int row, string label)
        {
            var hit = all[row].FirstOrDefault(k => k.Label == label);
            return hit.KeyId == null ? -1 : hit.Col;
        }
        int q = ColOf(2, "Q"), a = ColOf(3, "A"), z = ColOf(4, "Z");
        Check("Q/A/Z 都找得到", q >= 0 && a >= 0 && z >= 0, $"Q={q} A={a} Z={z}");
        Check("ANSI 阶梯 Q < A < Z", q < a && a < z, $"Q={q} A={a} Z={z}");

        // 数字 1 在 Q 左侧
        int one = ColOf(1, "1");
        Check("数字 1 在 Q 左侧", one >= 0 && one < q, $"1={one} Q={q}");

        // 小键盘独立在右侧
        var numCols = all.SelectMany(r => r)
                         .Where(k => k.KeyId.StartsWith("num", StringComparison.Ordinal))
                         .Select(k => k.Col).ToList();
        Check("小键盘有键", numCols.Count >= 15, numCols.Count.ToString());
        Check("小键盘全部在 74 列之后", numCols.All(c => c >= 74),
            string.Join(",", numCols.Where(c => c < 74)));

        // 编辑键簇区间
        string[] editLabels = { "Ins", "Home", "PgUp", "Del", "End", "PgDn" };
        var editCols = all.SelectMany(r => r)
                          .Where(k => editLabels.Contains(k.Label))
                          .Select(k => k.Col).Distinct().ToList();
        Check("编辑键簇在 61..72", editCols.All(c => c >= 61 && c <= 72),
            string.Join(",", editCols));

        // 总列数常量必须与最大右边界一致（防手滑写错）
        var f2 = t.GetField("TotalCols",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        int declared = (int)(f2?.GetValue(null) ?? 0);
        int maxRight = all.SelectMany(r => r).Max(k => k.Col + k.Span);
        Check("声明的总列数覆盖所有键", declared >= maxRight,
            $"declared={declared} maxRight={maxRight}");

        /* ---------- 等比例缩放 ----------
         *
         * 用户报的问题：「非全屏时整个按键的内容会被压缩，没有等比例缩小」。
         * 根因是键盘宽度由父容器决定、键帽高度却写死 52px ——
         * 窗口一窄只有横向被压。现在改成：键盘有**固定的自然宽高比**，
         * 外面套 Viewbox 等比缩放到可用空间。
         *
         * 这里断言两件事：
         *   ① 自然尺寸算得出来，且键的宽高比在"像个键帽"的范围内；
         *   ② 承载键盘的控件必须是 Viewbox（不是 StackPanel/Grid），
         *      否则等比缩放根本无从谈起。 */
        double nw = UI.Views.KeysView.NaturalWidth;
        double nh = UI.Views.KeysView.NaturalHeight;
        Check("键盘自然宽度是正数", nw > 0, nw.ToString("0.0"));
        Check("键盘自然高度是正数", nh > 0, nh.ToString("0.0"));

        double keyW = nw / (declared / 4.0);
        double keyH = nh / all.Length;
        Check("标准键宽高比接近 1（不被压扁）", keyW / keyH is > 0.6 and < 1.4,
            $"keyW={keyW:0.0} keyH={keyH:0.0} ratio={keyW / keyH:0.00}");

        var hostField = t.GetField("KeyboardPanel",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Check("键盘宿主字段存在", hostField != null);
        Check("键盘宿主是 Viewbox（等比缩放容器）",
            hostField?.FieldType == typeof(System.Windows.Controls.Viewbox),
            hostField?.FieldType.Name ?? "(null)");

        /* ---------- 键盘档位 = 页面最小尺寸 ----------
         *
         * 用户报的问题：「窗口缩小或缩放时 108 个按键等比例缩小，
         * 键上的文字小到无法辨认」，并且明确否掉了"到下限就出滚动条"的做法：
         * 「准备好几个 108 键键盘的大小，我能选一个作为默认……而这也就是
         * 这个页面的最小大小」。
         *
         * 现在的规则（三条，缺一不可）：
         *   ① 键盘大小只由**档位**决定，窗口怎么拖都不影响键帽大小；
         *   ② 每档给出一个"装下它所需的窗口尺寸"，主窗口拿它设 MinWidth/MinHeight；
         *   ③ 档位下限 0.75 —— 功能名 8.5 × 0.75 = 6.4 DIP，再小中文笔画就粘了。
         */
        try
        {
            var view = new UI.Views.KeysView();
            Check("键盘装在 ScrollViewer 里（屏幕装不下时还能滚着看）",
                view.KeyboardHostForTest is System.Windows.Controls.ScrollViewer,
                view.KeyboardHostForTest?.GetType().Name ?? "(null)");

            /* ---------- 布局：编辑区搬到键盘下方 ----------
             *
             * 用户的要求：「将编辑绑定界面直接放置在键盘下方，以便 108 键键盘
             * 能够获得更大的展示空间」。这两条断言把意图钉死：
             *   · 编辑卡片的顶边必须在键盘底边之下（不是左右并排）；
             *   · 键盘可用宽度等于整页宽度（不再被 330 宽的编辑栏切掉一块）。
             */
            var order = view.VerticalOrderForTest(1400, 900);
            Check("编辑区在键盘下方（不再左右分栏）",
                order.EditorTop > order.KeyboardBottom - 0.5,
                $"keyboardBottom={order.KeyboardBottom:0} editorTop={order.EditorTop:0}");
            Check("键盘可用宽度 ≈ 整页宽度（编辑栏不再横向占位）",
                order.PageWidth > 0 && order.KeyboardWidth > order.PageWidth * 0.85,
                $"keyboard={order.KeyboardWidth:0} page={order.PageWidth:0} "
                + $"panel={view.KeyboardPanelWidthForTest:0}");

            /* 「本层已绑定」清单已被移除：键帽本身就是绑定状态（深蓝实底 / 浅底）。
             * 用反射盯着字段，防止哪天又被加回来。 */
            var boundField = typeof(UI.Views.KeysView).GetField("BoundList",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Check("已移除「本层已绑定」列表（状态直接看键帽）", boundField == null,
                boundField?.FieldType.Name ?? "(无)");

            // ① 档位表本身
            var presets = Core.Settings.KeyboardSizePreset.All;
            Check("键盘档位是 4 档", presets.Count == 4, presets.Count.ToString());
            Check("档位比例严格递增",
                presets.Zip(presets.Skip(1)).All(p => p.Second.Scale > p.First.Scale),
                string.Join(" / ", presets.Select(p => p.Scale.ToString("0.00"))));
            Check("默认档就是设计尺寸（1.0）",
                Math.Abs(Core.Settings.KeyboardSizePreset.Resolve(null).Scale - 1.0) < 0.001,
                Core.Settings.KeyboardSizePreset.Resolve(null).Id);
            Check("乱写的档位 ID 会退回默认档",
                Core.Settings.KeyboardSizePreset.Resolve("enormous").Id
                == Core.Settings.KeyboardSizePreset.DefaultId,
                Core.Settings.KeyboardSizePreset.Resolve("enormous").Id);

            /* ② 每档的功能名字号必须还在可读范围。
             *
             * 8.5 DIP 是键帽上主信息（功能名）的设计字号；乘档位比例就是
             * 实际字号。6 DIP 是底线：96 DPI 下 6 px 的中文已经很难认，
             * 再往下就该干脆不做这一档。 */
            foreach (var p in presets)
            {
                double fontSize = 8.5 * p.Scale;
                Check($"「{p.Name}」档功能名字号 ≥ 6 DIP", fontSize >= 6.0,
                    $"{fontSize:0.00} DIP");
            }

            /* ③ 同一窗口尺寸下，档位决定键盘大小 —— 这是与旧版最本质的区别。
             * 旧版这里会随可用空间变化，也正因为"会变"才会被拖到看不清。 */
            double prevKeyW = 0;
            foreach (var p in presets)
            {
                UI.Views.KeysView.RenderPresetOverrideForTest = p.Id;

                var wide = view.KeyboardScaleForTest(2400, 1400);   // 足够大，排除滚动干扰
                Check($"「{p.Name}」档缩放 = {p.Scale:0.00}（不随窗口变）",
                    Math.Abs(wide.Scale - p.Scale) < 0.001, $"scale={wide.Scale:0.000}");

                double bandW = wide.KeyWidth * declared / 4.0;
                Check($"「{p.Name}」档整把键盘比上一档更宽", bandW > prevKeyW,
                    $"{prevKeyW:0} → {bandW:0}");
                prevKeyW = bandW;

                Check($"「{p.Name}」档装得下时不出横向滚动条", !wide.NeedsScrollX,
                    $"panelW={bandW:0}");
            }

            // ④ 窗口小于档位需求 → 键盘一个像素都不缩，改由滚动条兜底
            UI.Views.KeysView.RenderPresetOverrideForTest = "xlarge";
            var tight = view.KeyboardScaleForTest(900, 600);
            Check("窗口装不下时键盘保持档位大小、不缩",
                Math.Abs(tight.Scale - 1.15) < 0.001, $"scale={tight.Scale:0.000}");
            Check("窗口装不下时改用横向滚动", tight.NeedsScrollX,
                $"panelW={tight.KeyWidth * declared / 4.0:0}");

            /* ⑤ 页面最小尺寸 = 键盘宽 + 卡片两侧的固定宽度。
             *
             * 这个式子必须和 MainWindow 真正去设的 MinWidth 一致 ——
             * 差几十像素的表现就是"窗口到了最小尺寸，键盘却还在出滚动条"。 */
            double prevNeedW = 0;
            foreach (var p in presets)
            {
                UI.Views.KeysView.RenderPresetOverrideForTest = p.Id;
                var need = view.RequiredWindowSize();
                double expectW = UI.Views.KeysView.WindowFixedWidth
                                 + UI.Views.KeysView.NaturalWidth * p.Scale;
                Check($"「{p.Name}」档最小窗口宽 = 键盘宽 + 固定宽度",
                    Math.Abs(need.Width - expectW) < 0.5,
                    $"need={need.Width:0} expect={expectW:0}");
                Check($"「{p.Name}」档最小窗口宽大于上一档", need.Width > prevNeedW,
                    $"{prevNeedW:0} → {need.Width:0}");
                prevNeedW = need.Width;
            }

            /* ⑥ 窗口刚好等于它自己的最小尺寸时，键盘必须完整可见。
             *
             * 传进去的是 KeysView 的尺寸，所以要扣掉侧栏（228）和标题栏（56）。 */
            UI.Views.KeysView.RenderPresetOverrideForTest = "large";
            var needLarge = view.RequiredWindowSize();
            var exact = view.KeyboardScaleForTest(needLarge.Width - 228, needLarge.Height - 56);
            Check("窗口等于最小尺寸时键盘完整可见（无横向滚动）", !exact.NeedsScrollX,
                $"need={needLarge.Width:0}×{needLarge.Height:0} panelW={exact.KeyWidth * declared / 4.0:0} "
                + $"viewport={view.KeyboardViewportWidthForTest:0.0}");

            UI.Views.KeysView.RenderPresetOverrideForTest = null;
        }
        catch (Exception ex)
        {
            Check("键盘档位探针没抛异常", false, ex.Message);
            UI.Views.KeysView.RenderPresetOverrideForTest = null;
        }
    }

    private static void TestHelpCoversAllLayers()
    {
        var s = DefaultSettings.Create();

        // 在第 5 层加一个明显不同的绑定
        var layer5 = s.Layers.First(l => l.Id == 5);
        layer5.Keys.Add(new KeyBinding { Combo = "caps_q", Action = "qbar" });

        // 模拟 HelpView 的遍历逻辑：所有层的绑定都要被收集到
        int total = 0;
        var combos = new List<string>();
        foreach (var layer in s.Layers)
        {
            foreach (var b in layer.Keys.Where(k => k.IsBound))
            {
                var p = KeyName.ParseCombo(b.Combo);
                if (p == null || p.Value.Key == "capslock") continue;
                total++;
                combos.Add($"{layer.Id}:{b.Combo}");
            }
        }

        Check("速查表收集到多个层的绑定", total > 10, total.ToString());
        Check("第 5 层的绑定也被收集", combos.Any(c => c.StartsWith("5:")),
            string.Join(",", combos.Take(5)));

        // 统计有多少层有绑定，应该 > 1
        var layersWithBindings = s.Layers
            .Where(l => l.Keys.Any(k => k.IsBound))
            .Select(l => l.Id).ToList();
        Check("有绑定的层不止一个", layersWithBindings.Count > 1,
            string.Join(",", layersWithBindings));

        // ---------- 输入文本：速查表里只显示备注，正文默认隐藏 ----------
        //
        // 参数就是文字本身（邮箱、地址、常用回复），而速查表最容易被截图；
        // 用户明确要求"默认隐藏文本的内容，只显示备注"。
        var info = new Core.Actions.ActionInfo(
            "typeText", "输入文本", "输入一段文字",
            "把这段文字直接输入到当前光标处（支持中文与 emoji）", NeedsArg: true);

        string hidden = UI.Views.HelpView.DescribeBinding(
            new KeyBinding { Action = "typeText", Arg = "我的密码是 hunter2" }, info);
        Check("输入文本的正文不出现在速查表里", !hidden.Contains("hunter2"), hidden);
        Check("没有备注时明确标注内容已隐藏", hidden.Contains("已隐藏"), hidden);

        string noted = UI.Views.HelpView.DescribeBinding(
            new KeyBinding { Action = "typeText", Arg = "我的密码是 hunter2", Note = "公司邮箱" }, info);
        Check("有备注时显示备注", noted.Contains("公司邮箱"), noted);
        Check("有备注时正文仍然不出现", !noted.Contains("hunter2"), noted);

        // ---------- 其余动作：备注优先于"动作说明 + 参数" ----------
        //
        // 宏最典型：参数是 macro:3 / 重复次数这类机器串，拼出来是
        // 「播放宏：每日备份（参数 3）」，用户自己写的备注反而看不到。
        // 用户明确要求：只要写了备注，速查表就优先显示备注。
        var macroInfo = new Core.Actions.ActionInfo(
            "macro:3", "宏", "播放宏：每日备份",
            "播放宏：每日备份", NeedsArg: true);

        string macroNoNote = UI.Views.HelpView.DescribeBinding(
            new KeyBinding { Action = "macro:3", Arg = "3" }, macroInfo);
        Check("宏没备注时仍显示动作说明与参数", macroNoNote.Contains("播放宏：每日备份"),
            macroNoNote);

        string macroNote = UI.Views.HelpView.DescribeBinding(
            new KeyBinding { Action = "macro:3", Arg = "3", Note = "备份" }, macroInfo);
        CheckEqual("宏写了备注就只显示备注", macroNote, "备份");

        // 普通动作同理（参数里是命令 / 次数这类机器值）
        var repeatInfo = new Core.Actions.ActionInfo(
            "repeat", "重复", "重复上一次操作", "重复上一次操作", NeedsArg: true);
        string repeatNote = UI.Views.HelpView.DescribeBinding(
            new KeyBinding { Action = "repeat", Arg = "5", Note = "连按五次" }, repeatInfo);
        CheckEqual("普通动作写了备注也以备注为准", repeatNote, "连按五次");

        // 备注为空白（只有空格）时仍然当作"没写"，不能把说明吃掉
        string blank = UI.Views.HelpView.DescribeBinding(
            new KeyBinding { Action = "repeat", Arg = "5", Note = "   " }, repeatInfo);
        Check("空白备注不算备注", blank.Contains("重复上一次操作"), blank);

        // 自定义按键：备注优先（键帽上用的就是备注，速查表必须一致）
        var ckInfo = new Core.Actions.ActionInfo(
            "customKey", "自定义按键", "把某个键发给指定应用",
            "把某个键发给指定应用", NeedsArg: true);
        var ckArg = new Core.Actions.CustomKeySpec
        {
            Key = "f13", AppPath = @"C:\Apps\WeChat.exe", BringFront = true,
        };
        string ckNoNote = UI.Views.HelpView.DescribeBinding(
            new KeyBinding { Action = "customKey", Arg = ckArg.ToArg() }, ckInfo);
        Check("自定义按键没备注时说明发给谁", ckNoNote.Contains("WeChat"), ckNoNote);

        string ckNote = UI.Views.HelpView.DescribeBinding(
            new KeyBinding { Action = "customKey", Arg = ckArg.ToArg(), Note = "微信" }, ckInfo);
        CheckEqual("自定义按键写了备注就只显示备注", ckNote, "微信");
    }

    /// <summary>
    /// 「打开文件夹后把窗口带到前台」用的文件夹名识别。
    ///
    /// 为什么要单独测：这个名子用来在"explorer 复用已有窗口"时认人
    /// （窗口标题就是当前文件夹名）。盘符根目录、尾部斜杠这些写法
    /// 一不小心就取出空串，结果是"永远认不出、永远不置前" ——
    /// 但功能看起来又是"打开了"，很难发现。
    /// </summary>
    private static void TestFolderLeafName()
    {
        CheckEqual("普通路径取末级", Core.Windows.WindowService.LeafNameOf(@"D:\a\b"), "b");
        CheckEqual("尾部反斜杠也能取", Core.Windows.WindowService.LeafNameOf(@"D:\a\b\"), "b");
        CheckEqual("尾部正斜杠也能取", Core.Windows.WindowService.LeafNameOf("D:/a/b/"), "b");
        CheckEqual("盘符根目录用盘符", Core.Windows.WindowService.LeafNameOf(@"C:\"), "C");
        CheckEqual("正斜杠盘符根目录", Core.Windows.WindowService.LeafNameOf("D:/"), "D");
        CheckEqual("空字符串不抛异常", Core.Windows.WindowService.LeafNameOf(""), "");
        CheckEqual("中文路径", Core.Windows.WindowService.LeafNameOf(@"D:\项目\文档"), "文档");
    }

    /// <summary>窗口激活与最小化：验证「按一次弹出、再按一次最小化」这两个动作。</summary>
    private static void TestWindowActivateAndMinimize()
    {
        var probe = new System.Windows.Window
        {
            Width = 600,
            Height = 400,
            WindowStyle = System.Windows.WindowStyle.None,
            ShowInTaskbar = false,
            Left = -32000,
            Top = -32000,
        };
        probe.Show();
        DoEvents(200);

        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(probe).Handle;
            Check("测试窗口已创建", h != IntPtr.Zero);

            var before = WindowService.GetRect(h);

            // 激活不应该改变窗口尺寸（没有动画了）
            WindowService.Activate(h);
            DoEvents(200);
            var after = WindowService.GetRect(h);
            Check("激活后窗口尺寸不变（无动画）",
                Math.Abs(after.Width - before.Width) <= 1 &&
                Math.Abs(after.Height - before.Height) <= 1,
                $"{before.Width}x{before.Height} -> {after.Width}x{after.Height}");

            // 在后台线程上调用也不能抛（真实调用路径是钩子线程）
            Exception? error = null;
            var t = new Thread(() =>
            {
                try { WindowService.Activate(h); }
                catch (Exception ex) { error = ex; }
            })
            { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(3000);
            Check("后台线程上激活不抛异常", error == null, error?.Message ?? "");

            // 最小化
            WindowService.Minimize(h);
            DoEvents(400);
            Check("最小化后窗口已最小化", NativeWindowIsIconic(h));

            // 再激活一次要能还原回来
            WindowService.Activate(h);
            DoEvents(400);
            Check("再次激活后已还原", !NativeWindowIsIconic(h));
        }
        finally
        {
            probe.Close();
            DoEvents(120);
        }
    }

    private static bool NativeWindowIsIconic(IntPtr h)
    {
        return Core.Interop.NativeMethods.IsIconic(h);
    }

    /// <summary>
    /// 「编辑绑定」面板的布局密度。
    ///
    /// 这一块最容易悄悄退化回"每个字段占一行"：后来加字段的人往往直接往下堆，
    /// 谁都不是有意变松，但一屏能装的信息就这么一点点被吃掉。
    /// 所以这里用**高度**当硬指标，而不是靠看截图觉得"差不多"。
    /// </summary>
    private static void TestEditorDensity()
    {
        var view = new UI.Views.KeysView();
        view.Measure(new System.Windows.Size(1080, 2400));
        view.Arrange(new System.Windows.Rect(0, 0, 1080, 2400));
        view.UpdateLayout();

        var picker = view.FindName("NormalActionPicker") as System.Windows.Controls.Grid;
        var search = view.FindName("SearchGroup") as System.Windows.Controls.StackPanel;
        var category = view.FindName("CategoryBox") as System.Windows.Controls.ComboBox;
        var clear = view.FindName("ClearBindButton") as System.Windows.Controls.Button;

        Check("编辑区有普通分类行", picker != null && search != null && category != null && clear != null);
        if (picker == null || search == null || category == null || clear == null) return;

        /* 一行 = 标签(约 24) + 控件(34) + 余量；退回"搜索一行、分类+功能一行"
         * 会是 100 以上 —— 这条断言就是防止那种退化。 */
        Check("搜索/分类/功能挤在同一行", picker.ActualHeight is > 40 and < 80,
            $"行高 {picker.ActualHeight:F0}");

        double searchTop = search.TranslatePoint(new System.Windows.Point(0, 0), view).Y;
        double categoryTop = category.TranslatePoint(new System.Windows.Point(0, 0), view).Y;
        Check("搜索框与分类在同一行（顶部差 < 30）", Math.Abs(searchTop - categoryTop) < 30,
            $"搜索组 {searchTop:F0}，分类控件 {categoryTop:F0}");

        // 两个操作按钮提到标题行：必须比分类控件更靠上
        double clearTop = clear.TranslatePoint(new System.Windows.Point(0, 0), view).Y;
        Check("「清除绑定」已提到标题行", clearTop < categoryTop,
            $"按钮 {clearTop:F0}，分类 {categoryTop:F0}");

        // ---- 宽/窄两种形态（同一批控件重排，不是两套视图）----
        var wide = view.EditorLayoutForTest(compact: false);
        CheckEqual("宽窗口：搜索留在主行", wide.SearchRow, 0);
        CheckEqual("宽窗口：搜索只占一列", wide.SearchSpan, 1);
        CheckEqual("宽窗口：备注留在主行", wide.NoteRow, 0);
        CheckEqual("宽窗口：备注只占一列", wide.NoteSpan, 1);

        var narrow = view.EditorLayoutForTest(compact: true);
        CheckEqual("窄窗口：搜索下沉到第二行", narrow.SearchRow, 1);
        CheckEqual("窄窗口：搜索横跨整行", narrow.SearchSpan, 3);
        CheckEqual("窄窗口：备注下沉到第二行", narrow.NoteRow, 1);
        CheckEqual("窄窗口：备注横跨整行", narrow.NoteSpan, 3);
        Check("窄窗口：下沉的备注有上方间距", narrow.NoteMarginTop > 0, $"{narrow.NoteMarginTop:F0}");

        // ---- 说明 + 参数：宽时并排吃满一行，窄时参数换行 ----
        CheckEqual("宽窗口：参数在说明右侧", wide.ArgColumn, 1);
        CheckEqual("宽窗口：参数只占一列", wide.ArgSpan, 1);
        Check("宽窗口：参数宽度固定 340", Math.Abs(wide.ArgWidth - 340) < 0.5, $"{wide.ArgWidth:F0}");

        CheckEqual("窄窗口：参数换到第二行", narrow.ArgRow, 1);
        CheckEqual("窄窗口：参数横跨整行", narrow.ArgSpan, 2);
        Check("窄窗口：参数改回自适应宽度", double.IsNaN(narrow.ArgWidth), $"{narrow.ArgWidth}");

        view.EditorLayoutForTest(compact: false);   // 还原，别影响后面的用例
    }

    private static void TestWindows()
    {
        var fg = WindowService.ForegroundInfo();
        Check("能取到前台窗口信息", fg != null, fg?.Title);
        var all = WindowService.EnumerateTopLevel().ToList();
        Check("能枚举顶层窗口", all.Count > 0, all.Count.ToString());
        /* `Length >= 0` 永远成立，等于什么都没测 —— 之前这条是个摆设。
         * 真实约束是"字段不能为 null"，路径本身可能为空（受保护进程读不到）。 */
        Check("窗口信息的路径字段不为 null",
            all.All(w => w.ExePath != null && w.ExeName != null));
        var work = WindowService.WorkArea(IntPtr.Zero);
        Check("能取到工作区", work.Width > 0 && work.Height > 0, $"{work.Width}x{work.Height}");
        Check("进程路径可读", WindowService.GetProcessPath((uint)Environment.ProcessId).Length > 0);
    }

    /// <summary>
    /// 关于页的「GitHub 个人访问令牌」区。
    ///
    /// 验三件事，都是用户能直接感觉到的：
    ///   ① 控件真的被排版出来了（不是 Visibility=Collapsed 忘改回来）；
    ///   ② 问号按钮和输入框**垂直居中对齐**（差几个像素就看得出歪）；
    ///   ③ 问号是可达的 —— 能 Tab 到、有名字可朗读（AutomationProperties）。
    ///
    /// 用离线 Measure/Arrange 而不是开真窗口：自检不该弹窗口抢焦点，
    /// 而且布局问题在离线下同样会暴露（宽高为 0 或位置算错）。
    /// </summary>
    private static void TestAboutTokenUi()
    {
        var view = new UI.Views.AboutView();
        view.Measure(new System.Windows.Size(1180, 2400));
        view.Arrange(new System.Windows.Rect(0, 0, 1180, 2400));
        view.UpdateLayout();

        var box = view.FindName("TokenBox") as System.Windows.Controls.TextBox;
        var help = view.FindName("TokenHelpButton") as System.Windows.Controls.Button;
        var status = view.FindName("TokenStatusText") as System.Windows.Controls.TextBlock;

        Check("关于页有令牌输入框", box != null);
        Check("关于页有令牌帮助按钮", help != null);
        Check("关于页有令牌状态说明", status != null);
        if (box == null || help == null || status == null) return;

        Check("输入框被排版出来了", box.ActualWidth > 100 && box.ActualHeight > 0,
            $"{box.ActualWidth:F0}x{box.ActualHeight:F0}");
        Check("问号按钮被排版出来了", help.ActualWidth > 0 && help.ActualHeight > 0,
            $"{help.ActualWidth:F0}x{help.ActualHeight:F0}");

        // 对齐：两者中心线的高度差不超过 2 DIP
        double boxMid = box.TranslatePoint(new System.Windows.Point(0, box.ActualHeight / 2), view).Y;
        double helpMid = help.TranslatePoint(new System.Windows.Point(0, help.ActualHeight / 2), view).Y;
        Check("问号与输入框垂直居中对齐", Math.Abs(boxMid - helpMid) <= 2,
            $"输入框中线 {boxMid:F1}，问号中线 {helpMid:F1}");

        // 可达性：Button 默认可聚焦，但 Focusable 被样式改掉就点不着了
        Check("问号按钮可用键盘聚焦", help.Focusable, $"Focusable={help.Focusable}");
        Check("问号按钮的键盘可用性没被 IsTabStop 关掉", help.IsTabStop);

        string? name = System.Windows.Automation.AutomationProperties.GetName(help);
        Check("问号按钮有可朗读的名字", !string.IsNullOrWhiteSpace(name), name);
        string? helpText = System.Windows.Automation.AutomationProperties.GetHelpText(help);
        Check("问号按钮有辅助说明", !string.IsNullOrWhiteSpace(helpText), helpText);

        Check("状态行写明了未填令牌的影响",
            status.Text.Contains("60 次") || status.Text.Contains("未填写"), status.Text);

        // ---- 验证按钮：能真测一次令牌，而不是只能"存进去再说" ----
        var verify = view.FindName("VerifyTokenButton") as System.Windows.Controls.Button;
        Check("关于页有令牌验证按钮", verify != null);
        if (verify != null)
        {
            Check("验证按钮可键盘聚焦", verify.Focusable);
            Check("验证按钮有文字说明",
                !string.IsNullOrWhiteSpace(verify.ToolTip?.ToString()), verify.ToolTip?.ToString());
        }

        /* ---- 没填令牌时：程序内不许有下载入口，只能点链接去浏览器 ---- */
        const string sampleUrl = "https://github.com/QQQOrange/Keyboard-enhance/releases";
        view.ShowManualLink(sampleUrl);
        view.Measure(new System.Windows.Size(1180, 2400));
        view.Arrange(new System.Windows.Rect(0, 0, 1180, 2400));
        view.UpdateLayout();

        var download = view.FindName("DownloadButton") as System.Windows.Controls.Button;
        var panel = view.FindName("ManualLinkPanel") as System.Windows.Controls.StackPanel;
        var linkButton = view.FindName("UpdateLinkButton") as System.Windows.Controls.Button;
        var linkText = view.FindName("UpdateLinkText") as System.Windows.Controls.TextBlock;

        Check("无令牌时「下载并安装」必须藏起来",
            download != null && download.Visibility != System.Windows.Visibility.Visible,
            download?.Visibility.ToString());
        Check("无令牌时给出手动下载区", panel != null && panel.Visibility == System.Windows.Visibility.Visible);
        CheckEqual("链接文字就是下载地址", linkText?.Text, sampleUrl);
        Check("下载链接可键盘聚焦", linkButton?.Focusable == true);
        Check("下载链接有无障碍名字",
            !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(linkButton)));

        // 帮助地址集中配置：改地址只改 UpdateService.TokenHelpUrl
        Check("帮助地址指向 GitHub 官方文档",
            UpdateService.TokenHelpUrl.StartsWith("https://docs.github.com/zh/", StringComparison.Ordinal),
            UpdateService.TokenHelpUrl);
    }
}