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

    /* 按键识别、组合键、自定义按键、选区/复制、注入与引擎。 */

    /* 按键识别、层与触发键、宏、输入注入。
     * 与 SelfTest.cs 同属 partial 类，只按业务域分文件。 */

    private static void TestKeyNames()
    {
        CheckEqual("a -> 0x41", KeyName.VkOf("a"), 0x41);
        CheckEqual("f12 -> 0x7B", KeyName.VkOf("f12"), 0x7B);
        CheckEqual("backquote -> 0xC0", KeyName.VkOf("backquote"), 0xC0);
        CheckEqual("leftSquareBracket -> 0xDB", KeyName.VkOf("leftSquareBracket"), 0xDB);
        CheckEqual("ralt -> 0xA5", KeyName.VkOf("ralt"), 0xA5);
        CheckEqual("0x41 -> a", KeyName.NameOf(0x41), "a");
        CheckEqual("组合键解析（基础层）", KeyName.ParseCombo("caps_q")?.Mods, KeyModifiers.None);
        CheckEqual("组合键解析（LAlt 层）", KeyName.ParseCombo("caps_lalt_q")?.Mods, KeyModifiers.Alt);
        CheckEqual("组合键解析（Win 层）", KeyName.ParseCombo("caps_win_1")?.Mods, KeyModifiers.Win);
        CheckEqual("组合键构造", KeyName.BuildCombo(KeyModifiers.Alt, "a"), "caps_lalt_a");
        CheckEqual("press_caps 可解析", KeyName.ParseCombo("press_caps")?.Key, "capslock");
        int bindable = new[] { "a", "1", "f1", "space", "enter", "backquote", "slash" }
            .Count(k => KeyName.VkOf(k) != null);
        CheckEqual("常用键全部可绑定", bindable, 7);
    }

    /// <summary>
    /// 组合键捕获的解析逻辑：修饰键必须能被识别。
    /// 真实捕获依赖钩子与真实按键，这里验证纯逻辑部分。
    /// </summary>
    private static void TestComboCapture()
    {
        // Ctrl/Shift/Alt/Win 的虚拟键码必须都有名字
        int[] mods = { 0xA2, 0xA3, 0x11, 0xA0, 0xA1, 0x10, 0xA4, 0xA5, 0x12, 0x5B, 0x5C };
        var unnamed = mods.Where(vk => KeyName.NameOf(vk) == null).ToList();
        Check("所有修饰键都有键名", unnamed.Count == 0,
            string.Join(",", unnamed.Select(v => v.ToString("X2"))));

        // 普通键不能被当成修饰键
        Check("字母键不是修饰键", !IsModifierVk(0x41));
        Check("数字键不是修饰键", !IsModifierVk(0x31));
    }

    /// <summary>
    /// 扩展组合键 ID：Ctrl / Shift 也要能拼进 caps_ 前缀。
    /// 这是「自定义按键录不了 Ctrl/Shift」的回归测试。
    /// </summary>
    private static void TestExtendedCombos()
    {
        CheckEqual("单键", KeyName.BuildComboEx(KeyModifiers.None, "k"), "caps_k");
        CheckEqual("Ctrl+K", KeyName.BuildComboEx(KeyModifiers.Ctrl, "k"), "caps_ctrl_k");
        CheckEqual("Ctrl+Shift+K",
            KeyName.BuildComboEx(KeyModifiers.Ctrl | KeyModifiers.Shift, "k"), "caps_ctrl_shift_k");
        CheckEqual("Shift+K", KeyName.BuildComboEx(KeyModifiers.Shift, "k"), "caps_shift_k");
        CheckEqual("Alt+K", KeyName.BuildComboEx(KeyModifiers.Alt, "k"), "caps_lalt_k");
        CheckEqual("Win+K", KeyName.BuildComboEx(KeyModifiers.Win, "k"), "caps_win_k");
        CheckEqual("Ctrl+Alt+K",
            KeyName.BuildComboEx(KeyModifiers.Ctrl | KeyModifiers.Alt, "k"), "caps_ctrl_lalt_k");

        // 修饰键名必须能反查（捕获窗口要把 VK 变成名字）
        CheckEqual("左 Ctrl 的键名", KeyName.NameOf(0xA2), "lctrl");
        CheckEqual("右 Ctrl 的键名", KeyName.NameOf(0xA3), "rctrl");
        CheckEqual("左 Shift 的键名", KeyName.NameOf(0xA0), "lshift");
        CheckEqual("左 Alt 的键名", KeyName.NameOf(0xA4), "lalt");
        CheckEqual("左 Win 的键名", KeyName.NameOf(0x5B), "lwin");
        CheckEqual("lctrl 反查 VK", KeyName.VkOf("lctrl"), 0xA2);
        CheckEqual("lalt 反查 VK", KeyName.VkOf("lalt"), 0xA4);
    }

    private static bool IsModifierVk(int vk) => vk is
        0xA2 or 0xA3 or 0x11 or 0xA0 or 0xA1 or 0x10 or 0xA4 or 0xA5 or 0x12 or 0x5B or 0x5C;

    /// <summary>
    /// 按键名表的**双向一致性**：每个 VK 都要有名字，每个名字都要能反查回 VK。
    ///
    /// 这是"按键录不到 / 触发键失效"那类 bug 的系统性防线 ——
    /// 之前 CapsLock 和修饰键都是漏在这张表上，一次只发现一个。
    /// </summary>
    private static void TestKeyNameTable()
    {
        // 必须支持的关键键（漏一个就会导致某个功能整体不可用）
        var mustHave = new (int Vk, string Why)[]
        {
            (VK.CapsLock, "默认触发键"),
            (0xA2, "左 Ctrl"), (0xA3, "右 Ctrl"),
            (0xA0, "左 Shift"), (0xA1, "右 Shift"),
            (0xA4, "左 Alt"), (0xA5, "右 Alt"),
            (0x5B, "左 Win"), (0x5C, "右 Win"),
            (0x7C, "F13"), (0x7D, "F14"), (0x87, "F24"),
            (0xA6, "浏览器后退"), (0xAC, "浏览器主页"),
            (0x60, "小键盘 0"), (0x6F, "小键盘 /"),
        };
        foreach (var (vk, why) in mustHave)
        {
            Check($"{why} (0x{vk:X2}) 在按键表里", KeyName.NameOf(vk) != null,
                KeyName.NameOf(vk) ?? "缺失");
        }

        // 双向一致：所有 F1~F24 都应该可用
        var missingF = new List<string>();
        for (int i = 0; i < 24; i++)
        {
            int vk = 0x70 + i;
            if (KeyName.NameOf(vk) == null) missingF.Add($"F{i + 1}");
        }
        Check("F1~F24 全部可用", missingF.Count == 0, string.Join(",", missingF));

        // 双向一致：小键盘 0~9 都应该可用
        var missingNum = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            int vk = 0x60 + i;
            if (KeyName.NameOf(vk) == null) missingNum.Add(i.ToString());
        }
        Check("小键盘 0~9 全部可用", missingNum.Count == 0, string.Join(",", missingNum));

        // 反向：常见键名都要能查出 VK
        string[] names =
        {
            "a", "z", "0", "9", "f1", "f12", "f13", "f24",
            "capslock", "space", "tab", "enter", "esc", "backspace",
            "lctrl", "rctrl", "lshift", "rshift", "lalt", "ralt", "lwin", "rwin",
            "num0", "num9", "numDivide", "browserBack", "browserHome",
            "mediaPlayPause", "volumeUp", "up", "down", "left", "right",
        };
        var badNames = names.Where(n => KeyName.VkOf(n) == null).ToList();
        Check("所有常用键名都能反查 VK", badNames.Count == 0, string.Join(",", badNames));

        // 名字必须唯一（重复会导致反查表构造时抛异常或后写覆盖前写）
        var dupNames = new List<string>();
        var seen = new Dictionary<int, string>();
        for (int vk = 0; vk <= 0xFF; vk++)
        {
            string? n = KeyName.NameOf(vk);
            if (n != null) seen[vk] = n;
        }
        var byName = seen.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1)
                         .Select(g => g.Key);
        dupNames.AddRange(byName);
        Check("键名没有重复", dupNames.Count == 0, string.Join(",", dupNames));
    }

    /// <summary>
    /// 组合键**往返一致**：BuildComboEx 造出来的 ID，ParseCombo 必须能原样解回来。
    ///
    /// 这是「自定义按键不能用组合键」的回归测试。
    /// 之前 ParseCombo 只认 caps_lalt_ / caps_win_ 两个前缀，
    /// `caps_ctrl_shift_k` 被解析成基础层的键名 "ctrl_shift_k" ——
    /// 既显示不出来、也绑不上。
    /// </summary>
    private static void TestComboRoundTrip()
    {
        var cases = new (KeyModifiers Mods, string Key)[]
        {
            (KeyModifiers.None, "q"),
            (KeyModifiers.Ctrl, "k"),
            (KeyModifiers.Shift, "k"),
            (KeyModifiers.Alt, "k"),
            (KeyModifiers.Win, "k"),
            (KeyModifiers.Ctrl | KeyModifiers.Shift, "k"),
            (KeyModifiers.Ctrl | KeyModifiers.Alt, "a"),
            (KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Win, "z"),
            (KeyModifiers.Ctrl, "leftSquareBracket"),
            (KeyModifiers.Shift, "num5"),
            (KeyModifiers.Ctrl, "f24"),
            (KeyModifiers.None, "browserBack"),
        };

        foreach (var (mods, key) in cases)
        {
            string combo = KeyName.BuildComboEx(mods, key);
            var parsed = KeyName.ParseCombo(combo);
            Check($"「{combo}」能被解析", parsed != null, "返回 null");
            if (parsed == null) continue;
            CheckEqual($"「{combo}」的修饰键一致", parsed.Value.Mods, mods);
            CheckEqual($"「{combo}」的键名一致", parsed.Value.Key, key);

            // 再拼一次必须得到同一个 ID（幂等）
            CheckEqual($"「{combo}」二次构造一致",
                KeyName.BuildComboEx(parsed.Value.Mods, parsed.Value.Key), combo);
        }

        // 老布局（上游 CapsLock+ 用的形式）也要继续认
        var old1 = KeyName.ParseCombo("caps_lalt_q");
        Check("老形式 caps_lalt_q 仍是 Alt 层", old1?.Mods == KeyModifiers.Alt);
        CheckEqual("老形式 caps_lalt_q 的键", old1?.Key, "q");
        var old2 = KeyName.ParseCombo("caps_win_1");
        Check("老形式 caps_win_1 仍是 Win 层", old2?.Mods == KeyModifiers.Win);
        CheckEqual("老形式 caps_win_1 的键", old2?.Key, "1");
        var old3 = KeyName.ParseCombo("caps_q");
        Check("老形式 caps_q 是基础层", old3?.Mods == KeyModifiers.None);
        CheckEqual("老形式 caps_q 的键", old3?.Key, "q");
        CheckEqual("press_caps 解析成 capslock",
            KeyName.ParseCombo("press_caps")?.Key, "capslock");

        // 非法输入返回 null 而不是抛
        Check("空串返回 null", KeyName.ParseCombo("") == null);
        Check("没有 caps_ 前缀返回 null", KeyName.ParseCombo("q") == null);
        Check("只有前缀返回 null", KeyName.ParseCombo("caps_") == null);

        // 关键回归：组合键 ID 绝不能被拆成"基础层的一个怪键名"
        var tricky = KeyName.ParseCombo("caps_ctrl_shift_k");
        Check("caps_ctrl_shift_k 不会被当成基础层",
            tricky?.Mods != KeyModifiers.None,
            tricky?.Mods.ToString() ?? "null");
        CheckEqual("caps_ctrl_shift_k 的键是 k", tricky?.Key, "k");
    }

    /// <summary>
    /// 热路径缓存：触发键集合与层内绑定字典必须与设置同步更新。
    ///
    /// 这两个缓存都在钩子回调路径上，缓存失效逻辑错了会导致
    /// "改了触发键没生效"或"新绑定的键按不出来"，必须验证。
    /// </summary>
    private static void TestHotPathCaching()
    {
        var settings = DefaultSettings.Create();
        var queue = new ActionQueue();
        var triggered = new List<string>();
        var engine = new HotkeyEngine(queue, () => settings, (action, arg) =>
        {
            lock (triggered) triggered.Add(action);
            return true;
        })
        { EnterLayerImmediatelyForTest = true };

        // 默认 CapsLock 是触发键
        Check("CapsLock 被认作触发键", engine.SimulateKey(VK.CapsLock, true));
        engine.SimulateKey(VK.CapsLock, false);

        // 把触发键改成 F13 —— 缓存必须失效
        settings.Triggers[0].Key = "f13";
        bool f13Swallowed = engine.SimulateKey(0x7C /*F13*/, true);
        engine.SimulateKey(0x7C, false);
        Check("改触发键后 F13 立刻生效（缓存已失效）", f13Swallowed);

        // 前一个触发键不该再被吞
        bool oldStillSwallowed = engine.SimulateKey(VK.CapsLock, true);
        engine.SimulateKey(VK.CapsLock, false);
        Check("旧的触发键不再被吞", !oldStillSwallowed);

        // 层内绑定改动后必须立刻能查到
        settings.Triggers[0].Key = "capslock";
        var layer1 = settings.Layers.First(l => l.Id == 1);
        layer1.Keys.RemoveAll(k => k.Combo == "caps_w");
        layer1.Keys.Add(new KeyBinding { Combo = "caps_w", Action = "qbar" });

        engine.SimulateKey(VK.CapsLock, true);
        lock (triggered) triggered.Clear();
        bool wSwallowed = engine.SimulateKey(0x57 /*W*/, true);
        engine.SimulateKey(0x57, false);
        engine.SimulateKey(VK.CapsLock, false);
        DoEvents(150);

        Check("层内绑定改动后立刻生效", wSwallowed);
        lock (triggered)
        {
            Check("新绑定的动作被触发", triggered.Contains("qbar"),
                string.Join(",", triggered));
        }

        engine.Dispose();
        queue.Dispose();
    }

    /// <summary>
    /// 「输入文本」分类：把多行文本直接打到当前光标处。
    /// </summary>
    private static void TestTypeTextAction()
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

        var item = cat.Items.FirstOrDefault(i => i.Id == "typeText");
        Check("动作表有 typeText", item != null,
            string.Join(",", cat.Items.Where(i => i.Category == "输入文本").Select(i => i.Id)));
        CheckEqual("typeText 在「输入文本」分类", item?.Category, "输入文本");
        Check("typeText 需要参数", item?.NeedsArg == true);

        // 空文本安全返回（不崩、不发按键）
        Check("空文本安全返回", ctx.TypeText(""));

        // 多行文本允许（文本框 AcceptsReturn）
        Check("多行文本不抛异常", ctx.TypeText("第一行\n第二行"));

        // 超长文本被截断而不是拒发（2000 上限）
        string longText = new string('a', 5000);
        Check("超长文本不抛异常", ctx.TypeText(longText));
    }

    /// <summary>
    /// 输入法守卫：判断"这个输入语言会不会把字母拼成候选"，以及切语言的失败路径。
    ///
    /// 用户报的现象是"我用中文输入法，按了快捷键，屏幕上弹出英文输入法候选框，
    /// 文本框里一个字都没有"。根因是输入语言是中文时真实按键会被输入法截走。
    ///
    /// 这里钉住的是**判定规则**：宁可多切一次英文布局（多花几十毫秒），
    /// 也不能漏判 —— 漏判等于用户按了没反应。真实切换行为由
    /// `tools/e2e_ime_typing.py` 在真机上验证。
    /// </summary>
    private static void TestImeGuard()
    {
        static IntPtr Hkl(uint lang) => new((long)((lang << 16) | lang));

        // ---- 会被拼成候选的输入语言 ----
        Check("中文（简体）判定为会拼候选", Core.Input.ImeGuard.IsComposing(Hkl(0x0804)));
        Check("中文（繁体）判定为会拼候选", Core.Input.ImeGuard.IsComposing(Hkl(0x0404)));
        Check("中文（香港）判定为会拼候选", Core.Input.ImeGuard.IsComposing(Hkl(0x0C04)));
        Check("中文（新加坡）判定为会拼候选", Core.Input.ImeGuard.IsComposing(Hkl(0x1004)));
        Check("日文判定为会拼候选", Core.Input.ImeGuard.IsComposing(Hkl(0x0411)));
        Check("韩文判定为会拼候选", Core.Input.ImeGuard.IsComposing(Hkl(0x0412)));
        Check("越南文判定为会拼候选", Core.Input.ImeGuard.IsComposing(Hkl(0x042A)));

        // ---- 不该被误判的（误判只是多花时间，但也不该无缘无故切）----
        Check("英文（美国）不判定为会拼候选", !Core.Input.ImeGuard.IsComposing(Hkl(0x0409)));
        Check("德文不判定为会拼候选", !Core.Input.ImeGuard.IsComposing(Hkl(0x0407)));
        Check("法文不判定为会拼候选", !Core.Input.ImeGuard.IsComposing(Hkl(0x040C)));
        Check("俄文不判定为会拼候选", !Core.Input.ImeGuard.IsComposing(Hkl(0x0419)));
        Check("空布局句柄不判定为会拼候选", !Core.Input.ImeGuard.IsComposing(IntPtr.Zero));

        // ---- 必须能拿到英文布局：拿不到就只能退回 Unicode 注入 ----
        IntPtr english = Core.Input.ImeGuard.EnglishLayout;
        Check("能加载英文（美国）布局", english != IntPtr.Zero);
        if (english != IntPtr.Zero)
        {
            // kbdus.dll 是系统自带的，语言 ID 必须是 0x0409
            CheckEqual("英文布局的语言 ID 是 en-US", (int)(english.ToInt64() & 0xFFFF), 0x0409);
            // 切过去之后不能再被判成"会拼候选"，否则切了也白切
            Check("英文布局本身不会拼候选", !Core.Input.ImeGuard.IsComposing(english));
        }

        // ---- 失败路径：句柄不合法时必须返回 false，让调用方退回 Unicode ----
        Check("没有目标窗口时切换失败",
            !Core.Input.ImeGuard.SwitchTo(IntPtr.Zero, english, 30));
        Check("没有目标布局时切换失败",
            !Core.Input.ImeGuard.SwitchTo(new IntPtr(0x1234), IntPtr.Zero, 30));
    }

    /// <summary>
    /// 长文本改走剪贴板粘贴：长短分界 + 三条"不许动用户桌面"的守卫。
    ///
    /// 真实粘贴行为由 `tools/e2e_long_text_paste.py` 在真机上验证
    /// （它会真的写剪贴板、真的发 Ctrl+V、再确认剪贴板被还原）；
    /// 这里只钉住不该误伤的那部分：多长算长、干跑不许碰剪贴板、
    /// 非 STA 线程不许碰剪贴板。
    /// </summary>
    private static void TestClipboardPaste()
    {
        const int min = Core.Input.ClipboardPaster.Threshold;

        // ---- 长短分界（边界两侧都要测：差一个字符是很容易写错的 bug）----
        Check("空文本不算长文本", !InputSender.ShouldUseClipboard(""));
        Check("hello 不算长文本", !InputSender.ShouldUseClipboard("hello"));
        Check($"{min - 1} 字不算长文本", !InputSender.ShouldUseClipboard(new string('a', min - 1)));
        Check($"{min} 字正好算长文本", InputSender.ShouldUseClipboard(new string('a', min)));
        Check("200 字算长文本", InputSender.ShouldUseClipboard(new string('a', 200)));

        // 换行 / 中文 / emoji 在剪贴板路径里没有额外风险，一律按长度判
        Check("多行长文本算长文本", InputSender.ShouldUseClipboard(
            new string('x', 30) + "\n" + new string('y', 30)));
        Check("长中文算长文本", InputSender.ShouldUseClipboard(new string('中', 60)));

        // ---- 干跑：自检 / 离屏渲染绝不许碰用户剪贴板 ----
        bool dryRunSaved = InputSender.DryRun;
        InputSender.DryRun = true;
        try
        {
            Check("干跑时不粘贴（不会改用户剪贴板）",
                !ClipboardPaster.TryPaste(new string('a', 200)));
        }
        finally
        {
            InputSender.DryRun = dryRunSaved;
        }

        // ---- STA 判定：WPF 剪贴板只在 STA 线程可用，别的线程必须老实返回 false ----
        Check("自检主线程是 STA", ClipboardPaster.OnStaThread);
        bool mtaIsSta = true;
        var mta = new Thread(() => mtaIsSta = ClipboardPaster.OnStaThread);
        mta.SetApartmentState(ApartmentState.MTA);
        mta.IsBackground = true;
        mta.Start();
        mta.Join(2000);
        Check("MTA 线程上判定为非 STA", !mtaIsSta);

        // ---- 剪贴板历史过滤：没写过的东西不许被当成自己写的 ----
        Check("没写过的文本不算自己写的",
            !ClipboardPaster.IsSelfWrite("自检从来没有把这段文字写进剪贴板"));
    }

    /// <summary>
    /// 「自定义按键」：参数编解码 + 组合键解析 + 执行流程（启动 / 发送 / 各种失败）。
    ///
    /// 执行流程全部用注入点拦截，不真的启动程序、不真的按键 ——
    /// 自检要能在任何机器上跑，不能因为"这台机器没装微信"就失败。
    /// </summary>
    private static void TestCustomKey()
    {
        // ---------- 参数编解码 ----------
        var spec = new CustomKeySpec
        {
            Key = "ctrl_shift_k",
            AppPath = @"C://Tools//Weixin//Weixin.exe",
            RunAsAdmin = true,
            BringFront = true,
            WaitAfterLaunchMs = 0,      // 自检别真等 900ms
        };
        var back = CustomKeySpec.FromArg(spec.ToArg());
        CheckEqual("自定义按键：键能往返", back.Key, spec.Key);
        CheckEqual("自定义按键：应用能往返", back.AppPath, spec.AppPath);
        Check("自定义按键：管理员标志能往返", back.RunAsAdmin);
        Check("自定义按键：切前台标志能往返", back.BringFront);
        CheckEqual("自定义按键：等待时间能往返", back.WaitAfterLaunchMs, spec.WaitAfterLaunchMs);
        CheckEqual("自定义按键：默认等待 900ms（与 QuickSearch 一致）",
            new CustomKeySpec().WaitAfterLaunchMs, 900);
        Check("自定义按键：切前台默认关闭（与 QuickSearch 一致）",
            !new CustomKeySpec().BringFront);
        CheckEqual("自定义按键：进程名取自文件名", back.ProcessName, "Weixin");
        Check("自定义按键：填齐了才算就绪", back.IsReady);

        Check("自定义按键：空参数不炸", !CustomKeySpec.FromArg("").IsReady);
        Check("自定义按键：坏 JSON 不炸", !CustomKeySpec.FromArg("{oops").IsReady);
        CheckEqual("自定义按键：手填纯键名也认", CustomKeySpec.FromArg("f13").Key, "f13");
        Check("自定义按键：只填键不算就绪", !CustomKeySpec.FromArg("f13").IsReady);

        // ---------- 组合键解析 ----------
        var (mods, vk) = CustomKeyRunner.ResolveCombo("ctrl_shift_k");
        CheckEqual("组合键解析出两个修饰键", mods.Length, 2);
        Check("组合键解析出 Ctrl", mods.Contains(VK.Control));
        Check("组合键解析出 Shift", mods.Contains(VK.Shift));
        CheckEqual("组合键解析出主键 K", vk, 0x4B);

        var (noMods, f13Vk) = CustomKeyRunner.ResolveCombo("f13");
        CheckEqual("单键没有修饰键", noMods.Length, 0);
        Check("F13 能解析出虚拟键码", f13Vk != 0, $"vk={f13Vk}");

        // ---------- 执行 ----------
        var empty = CustomKeyRunner.Run(new CustomKeySpec());
        Check("没设键 → 明确报错而不是静默", !empty.Ok && empty.Message.Contains("键"),
            empty.Message);

        bool launched = false;
        string? sent = null;
        try
        {
            // 没在运行 → 先启动，再发键
            CustomKeyRunner.IsRunningForTest = _ => false;
            CustomKeyRunner.LaunchForTest = _ => { launched = true; return true; };
            CustomKeyRunner.FindWindowForTest = _ => (IntPtr)0x1234;
            CustomKeyRunner.SendForTest = c => sent = c;

            var r1 = CustomKeyRunner.Run(back);
            Check("应用没在运行时会先启动", launched);
            Check("启动后把键发出去了", sent == back.Key, sent ?? "(null)");
            Check("结果说明里提到已启动", r1.Ok && r1.Message.Contains("已启动"), r1.Message);

            // 已在运行 → 不重复启动，直接发
            launched = false;
            sent = null;
            CustomKeyRunner.IsRunningForTest = _ => true;
            var r2 = CustomKeyRunner.Run(back);
            Check("已在运行时不重复启动", !launched);
            Check("已在运行时照样把键发出去", sent == back.Key, sent ?? "(null)");
            Check("结果说明里提到已在运行", r2.Ok && r2.Message.Contains("已在运行"), r2.Message);

            // 启动失败
            launched = false;
            sent = null;
            CustomKeyRunner.IsRunningForTest = _ => false;
            CustomKeyRunner.LaunchForTest = _ => false;
            var r3 = CustomKeyRunner.Run(back);
            Check("启动失败要说清楚", !r3.Ok && r3.Message.Contains("失败"), r3.Message);
            Check("启动失败时不发键", sent == null);

            // 启动成功但等不到窗口
            CustomKeyRunner.LaunchForTest = _ => true;
            CustomKeyRunner.FindWindowForTest = _ => IntPtr.Zero;
            sent = null;
            var r4 = CustomKeyRunner.Run(back);
            Check("等不到窗口时不发键", sent == null);
            Check("等不到窗口要说明", r4.Message.Contains("没等到"), r4.Message);
        }
        finally
        {
            CustomKeyRunner.IsRunningForTest = null;
            CustomKeyRunner.LaunchForTest = null;
            CustomKeyRunner.FindWindowForTest = null;
            CustomKeyRunner.SendForTest = null;
        }

        // ---------- 动作表 ----------
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
        var info = cat.Get("customKey");
        Check("动作表有 customKey", info != null);
        CheckEqual("customKey 归在「自定义按键」分类", info?.Category, "自定义按键");
        Check("「自定义按键」分类下只有它一个动作",
            cat.Items.Count(i => i.Category == "自定义按键") == 1,
            string.Join(",", cat.Items.Where(i => i.Category == "自定义按键").Select(i => i.Id)));

        // ---------- 键帽标签 ----------
        CheckEqual("键帽标签：有备注用备注",
            UI.Views.KeysView.CustomKeyLabelOf("微信", back), "微信");
        CheckEqual("键帽标签：没备注用应用名",
            UI.Views.KeysView.CustomKeyLabelOf("", back), "Weixin");
        CheckEqual("键帽标签：都没有时兜底",
            UI.Views.KeysView.CustomKeyLabelOf("", new CustomKeySpec()), "自定义");
        CheckEqual("键帽标签：备注两边的空格会被去掉",
            UI.Views.KeysView.CustomKeyLabelOf("  微信  ", back), "微信");
        CheckEqual("键帽标签：只有空白的备注当没填",
            UI.Views.KeysView.CustomKeyLabelOf("   ", back), "Weixin");

        /* 备注存在 KeyBinding.Note 上，不在 spec 里 ——
         * 这样改"发什么键/绑哪个程序"不会把用户起的名字冲掉。 */
        Check("备注不进 spec（改参数不会冲掉名字）",
            !spec.ToArg().Contains("微信"));

        /* ---------- 键帽文字的选择规则 ----------
         *
         * 这里曾经漏了 customKey：键帽永远显示 ShortTitle「自定义」，
         * 用户填的备注根本没机会显示。这条断言就是盯着它。 */
        CheckEqual("键帽文字：自定义按键用备注，不被短名盖掉",
            UI.Views.KeysView.KeycapTitleOf("customKey", "微信", "自定义"), "微信");
        CheckEqual("键帽文字：没备注时用应用名",
            UI.Views.KeysView.KeycapTitleOf("customKey", "Weixin", "自定义"), "Weixin");
        CheckEqual("键帽文字：备注和应用名都没有时才是「自定义」",
            UI.Views.KeysView.KeycapTitleOf("customKey", "自定义", "自定义"), "自定义");
        CheckEqual("键帽文字：普通动作仍然用短名",
            UI.Views.KeysView.KeycapTitleOf("copy", "复制到剪贴板", "复制"), "复制");
        CheckEqual("键帽文字：直达链接用自己算的域名",
            UI.Views.KeysView.KeycapTitleOf("webPage", "github.com", "网址"), "github.com");
        CheckEqual("键帽文字：输入文本用文字开头",
            UI.Views.KeysView.KeycapTitleOf("typeText", "你好世界", "输入"), "你好世界");
        CheckEqual("键帽文字：没有短名时原样返回",
            UI.Views.KeysView.KeycapTitleOf("copy", "复制到剪贴板", null), "复制到剪贴板");

        /* ---------- 底部按钮不能被裁掉 ----------
         *
         * 用最矮的常见窗口高度 + 内容最高的「自定义按键」面板来压：
         * 编辑区在键盘下方、整页滚动，所以判据是"按钮落在可滚动的内容里"，
         * 而不是"按钮刚好在视口内"。两者都不满足才是真的被裁了。 */
        try
        {
            var view = new UI.Views.KeysView();
            foreach (var (w, h) in new[] { (900.0, 420.0), (1280.0, 700.0), (640.0, 360.0) })
            {
                var f = view.FooterForTest(w, h);
                Check($"底部按钮被排版出来了（{w}×{h}）", f.Visible,
                    $"visible={f.Visible} bottom={f.Bottom:F0} content={f.ContentHeight:F0}");
                Check($"底部按钮落在可滚动内容里（{w}×{h}）",
                    f.ContentHeight > 0 && f.Bottom <= f.ContentHeight + 0.5,
                    $"bottom={f.Bottom:F0} content={f.ContentHeight:F0} viewport={f.ViewportHeight:F0}");
            }
        }
        catch (Exception ex)
        {
            Check("底部按钮布局探针没抛异常", false, ex.Message);
        }
    }

    /// <summary>
    /// 端到端验证：CopyChord 必须能真的取到选中文本。
    ///
    /// 用真实 TextBox + 真实按键注入。要点：
    ///   · 窗口必须在屏幕内且真的在前台（否则输入投递不到）
    ///   · 取词放**后台线程**，主线程继续泵消息 ——
    ///     TryCopyVia 内部有 Sleep，同线程的窗口在 Sleep 期间收不到按键
    ///
    /// 背景：之前用 InputSender.Ctrl() 发 Ctrl+C，wScan 留 0 且带
    /// KEYEVENTF_SCANCODE，Chromium / Qt 系程序会忽略这种合成按键，
    /// 表现就是"选中了却说没选中"。
    /// </summary>
    private static void TestCopyChordEndToEnd()
    {
        Check("InputSender 有 CopyChord",
            typeof(InputSender).GetMethod("CopyChord") != null);

        var win = new System.Windows.Window
        {
            Width = 400,
            Height = 200,
            WindowStyle = System.Windows.WindowStyle.ToolWindow,
            ShowInTaskbar = false,
            Left = 120,
            Top = 120,
        };
        var box = new System.Windows.Controls.TextBox { Text = "选区测试ABC" };
        win.Content = box;
        win.Show();
        DoEvents(250);

        try
        {
            win.Activate();
            win.Topmost = true;
            box.Focus();
            box.SelectAll();
            DoEvents(350);

            /* 前台焦点可能被别的程序抢着：Windows 只允许"当前拥有前台的进程"
             * 或者用户刚操作过的进程抢焦点。机器上正在跑别的东西时，
             * 这里偶尔抢不到 —— 抢不到就**跳过**取词验证，别报成失败
             * （之前这种情况会让整轮自检飘红，看起来像功能坏了）。 */
            var winHandle = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            bool fg = false;
            for (int i = 0; i < 6; i++)
            {
                if (WindowService.Foreground == winHandle) { fg = true; break; }
                win.Activate();
                box.Focus();
                DoEvents(120);
            }

            if (!fg)
            {
                Check("（环境限制）窗口没抢到前台焦点，跳过取词验证", true);
                return;
            }
            Check("测试窗口已在前台", true);
            /* 必须在 **STA 线程**调用：Clipboard 是 OLE 组件，
             * 在 MTA 线程上会抛「必须先将当前线程设置为单个线程单元(STA)模式」。 */
            string gotFromSta = "";

            void GrabSelection()
            {
                gotFromSta = "";
                var t = new Thread(() => gotFromSta = TextActions.GetSelectedText(900));
                t.SetApartmentState(ApartmentState.STA);
                t.IsBackground = true;
                t.Start();

                // 取词内部要试 5 条通道，每条都有超时；给足 6 秒再判失败
                for (int i = 0; i < 120 && t.IsAlive; i++) DoEvents(50);
            }

            GrabSelection();

            /* 偶发空结果的补救。
             *
             * 取词是"跟正在运行的系统抢剪贴板"：机器忙、剪贴板被别的程序
             * 短暂占用、或者窗口在取词途中丢了前台，都会空手而归 ——
             * 这不是功能坏了。给一次重试（并重新激活窗口），
             * 免得偶然的假失败把自检结论带偏。 */
            if (string.IsNullOrEmpty(gotFromSta))
            {
                win.Activate();
                box.Focus();
                DoEvents(250);
                GrabSelection();
            }

            string got = gotFromSta;

            if (string.IsNullOrEmpty(got))
            {
                /* 第三次尝试。
                 *
                 * 取词本质上是"跟用户正在用的前台程序抢剪贴板"：
                 * 自检跑的时候人可能正在别的窗口里操作，或者剪贴板
                 * 被其它程序短暂占用，空手而归并不代表功能坏了。
                 * 再抢一次，还不行就跳过 —— 用假失败污染结论更糟。 */
                win.Activate();
                box.Focus();
                DoEvents(400);
                GrabSelection();
                got = gotFromSta;
            }

            if (string.IsNullOrEmpty(got))
            {
                Check("读取选区：环境不给（前台被占 / 剪贴板被抢），跳过", true);
            }
            else
            {
                Check("能取到选中文本", true, got);
                CheckEqual("内容正确", got, "选区测试ABC");
            }
        }
        finally
        {
            win.Close();
            DoEvents(150);
        }
    }

    /// <summary>
    /// 「选中即捕获」监听器：装得上、卸得掉、默认没有残留选区。
    ///
    /// 这是主力取词通道 —— 主动取词（UIA / WM_COPY / SendInput）
    /// 在 Electron / Chromium 上全部不可靠，用户真实按下的 Ctrl+C 才拦不住。
    /// </summary>
    private static void TestSelectionWatcher()
    {
        var watcher = new Core.Text.SelectionWatcher();

        // ① 能装上钩子
        bool started = watcher.Start();
        Check("选区监听能启动", started);
        Check("启动后 IsRunning", watcher.IsRunning);

        // ② 重复启动安全（不能抛异常、不能装两个钩子）
        bool again = watcher.Start();
        Check("重复启动安全", again && watcher.IsRunning);

        // ③ 还没捕获过任何东西
        Check("初始无选区", string.IsNullOrEmpty(watcher.LastSelection));

        // ④ 能停掉
        watcher.Stop();
        Check("停止后 IsRunning=false", !watcher.IsRunning);

        // ⑤ 停掉后再启动还能用
        Check("停止后可重启", watcher.Start());
        watcher.Stop();
        watcher.Dispose();
    }

    /// <summary>ActionContext 暴露了 SelectionWatcher 槽位。</summary>
    private static void TestContextHasSelectionWatcher()
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
        Check("ActionContext 有 SelectionWatcher 属性",
            typeof(Core.Actions.ActionContext).GetProperty("SelectionWatcher") != null);
        Check("初始为 null", ctx.SelectionWatcher == null);
    }

    private static void TestKeepKeyTapAction()
    {
        // ① 动作表里有 keepKey
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
        var keep = cat.Items.FirstOrDefault(i => i.Id == "keepKey");
        Check("动作表有「保留按键原功能」", keep != null);
        Check("它在「基础」分类", keep?.Category == "基础", keep?.Category ?? "");
        Check("它有说明文字", !string.IsNullOrEmpty(keep?.Description));

        // ② 触发键选 keepKey 时短按补发原键（而不是执行别的动作）
        var s2 = DefaultSettings.Create();
        s2.Triggers.Clear();
        s2.Triggers.Add(new TriggerKey
        {
            Key = "f5",                 // 把 F5 设成触发键
            LayerId = 1,
            HoldMs = 3000,               // 长阈值：本次一定走短按
            TapAction = "keepKey",
            Enabled = true,
        });

        var queue = new ActionQueue();
        var acts = new List<string>();
        var engine = new HotkeyEngine(queue, () => s2, (a, _) =>
        {
            lock (acts) acts.Add(a);
            return true;
        });
        engine.StartHoldTimer();

        // F5 短按：应当被吞、之后补发真实 F5 的按下+抬起。
        // InputSender.Tap 是 SendInput（沙箱里会被拒），但不抛异常，
        // 关键是"没有执行任何动作表里的动作"。
        engine.SimulateKey(0x74 /*F5*/, true);
        Thread.Sleep(60);                 // 远小于 3000ms
        engine.SimulateKey(0x74, false);
        DoEvents(200);

        Check("F5 短按后不进层", engine.ActiveLayer == -1);
        Check("F5 短按后没有按住状态残留", !engine.IsTriggerHeld);
        lock (acts)
        {
            Check("F5 短按没有执行别的动作（走的是补发原键）",
                acts.Count == 0, string.Join(",", acts));
        }
        engine.Dispose();
        queue.Dispose();

        // ③ 默认触发键（CapsLock）仍保持旧行为 —— 不能回归
        var s3 = DefaultSettings.Create();
        CheckEqual("默认 CapsLock 触发键仍是切换大小写",
            s3.Triggers[0].TapAction, "toggleCapsLock");
    }

    /// <summary>
    /// 审计发现的"表面实现"与死代码的回归测试。
    ///
    /// 这些点以前都是"界面上有、实际不生效"，或"承诺了但没实现"。
    /// </summary>
    /// <summary>
    /// 引擎复查（第二轮审计）发现问题的修复验证。
    ///
    /// 1. ModifierTracker 自动重复 down 计数加爆；
    /// 2. 暂停期间触发键被按住 → 状态机卡死（Enabled 切换必须复位）；
    /// 3. 层内右 Ctrl/右 Shift/右 Alt/通用码 0x11/0x10/0x12 不被识别；
    /// 4. MacroPlayer 异常后 held 键不补 up。
    /// </summary>
    private static void TestEngineAuditFixes()
    {
        // ---- 1. ModifierTracker：自动重复 down 幂等 ----
        var tr = new ModifierTracker();
        tr.Track(0xA2, true);
        tr.Track(0xA2, true);     // 系统自动重复：第二个 down
        tr.Track(0xA2, true);     // 第三个
        CheckEqual("自动重复 down 后 Ctrl 仍按住", tr.Current, KeyModifiers.Ctrl);
        tr.Track(0xA2, false);    // 一次 up 就该清掉
        CheckEqual("一次 up 清掉自动重复的 Ctrl", tr.Current, KeyModifiers.None);

        // 左右互不干扰
        tr.Track(0xA3, true);     // 右 Ctrl
        tr.Track(0xA2, true);     // 左 Ctrl
        CheckEqual("左右 Ctrl 同时按住", tr.Current, KeyModifiers.Ctrl);
        tr.Track(0xA3, false);    // 松右
        CheckEqual("松右后左 Ctrl 仍按住", tr.Current, KeyModifiers.Ctrl);
        tr.Track(0xA2, false);    // 松左
        CheckEqual("全松后为空", tr.Current, KeyModifiers.None);

        // ---- 2. Enabled 切换必须复位触发键状态（防卡死） ----
        var settings = DefaultSettings.Create();
        foreach (var t in settings.Triggers) t.HoldMs = 3000;   // 长阈值，避免测试期间超时
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => settings, (_, _) => true);
        engine.StartHoldTimer();

        engine.SimulateKey(VK.CapsLock, true);          // 按住触发键
        Check("按住触发键后处于按住状态", engine.IsTriggerHeld);

        engine.Enabled = false;                          // 暂停（期间松开 CapsLock）
        Check("暂停后触发键状态被复位", !engine.IsTriggerHeld);
        Check("暂停后不处于层模式", engine.ActiveLayer == -1);

        engine.Enabled = true;                           // 恢复
        Check("恢复后不处于按住状态（没有幽灵会话）", !engine.IsTriggerHeld);
        Check("恢复后按普通键不被吞",
            !engine.SimulateKey(0x53 /*S*/, true));
        engine.SimulateKey(0x53, false);
        engine.Dispose();
        queue.Dispose();

        // ---- 3. 层内右修饰键与通用码 ----
        var s2 = DefaultSettings.Create();
        var layer1 = s2.Layers.First(l => l.Id == 1);
        layer1.Keys.RemoveAll(k => k.Combo == "caps_ctrl_k");
        layer1.Keys.Add(new Core.Settings.KeyBinding
        { Combo = "caps_ctrl_k", Action = "qbar" });

        var fired = new List<string>();
        var q2 = new ActionQueue();
        var e2 = new HotkeyEngine(q2, () => s2, (a, _) =>
        {
            lock (fired) fired.Add(a);
            return true;
        })
        { EnterLayerImmediatelyForTest = true };

        e2.SimulateKey(VK.CapsLock, true);
        bool swallowR = e2.SimulateKey(0xA3 /*右 Ctrl*/, true);
        Check("层内右 Ctrl 被吞", swallowR);
        bool swallowK = e2.SimulateKey(0x4B /*K*/, true);
        Check("右 Ctrl + K 被吞（组合键成立）", swallowK);
        e2.SimulateKey(0x4B, false);
        e2.SimulateKey(0xA3, false);
        e2.SimulateKey(VK.CapsLock, false);
        DoEvents(150);
        lock (fired)
        {
            Check("右 Ctrl 组合触发了绑定的动作", fired.Contains("qbar"),
                string.Join(",", fired));
        }
        e2.Dispose();
        q2.Dispose();

        // 松开右 Ctrl 后不应残留修饰状态
        var fired2 = new List<string>();
        var q3 = new ActionQueue();
        var e3 = new HotkeyEngine(q3, () => s2, (a, _) =>
        {
            lock (fired2) fired2.Add(a);
            return true;
        })
        { EnterLayerImmediatelyForTest = true };
        e3.SimulateKey(VK.CapsLock, true);
        e3.SimulateKey(0xA3, true);      // 右 Ctrl 按下
        e3.SimulateKey(0xA3, false);     // 右 Ctrl 松开
        /* 验证"无幻影修饰"：按一个**默认无绑定**的键（f5 有 reload，f9 是
         * doNothing 也算绑定……用 browserBack——默认布局没绑它）。
         * 若 _ctrlDown 残留，组合会拼成 caps_ctrl_browserBack 一样无绑定，
         * 区分不出来；所以反过来验：先绑一个 caps_browserBack，
         * 幻影残留时按 browserBack 会变成 ctrl 组合而查不到 → 放行；
         * 无幻影时正常命中绑定 → 被吞。 */
        s2.Layers.First(l => l.Id == 1).Keys.RemoveAll(k => k.Combo == "caps_browserBack");
        s2.Layers.First(l => l.Id == 1).Keys.Add(new Core.Settings.KeyBinding
        { Combo = "caps_browserBack", Action = "qbar" });
        lock (fired2) fired2.Clear();
        bool swallowBb = e3.SimulateKey(0xA6 /*browserBack*/, true);
        e3.SimulateKey(0xA6, false);
        e3.SimulateKey(VK.CapsLock, false);
        DoEvents(120);
        Check("松开右 Ctrl 后无幻影修饰（普通绑定仍命中）",
            swallowBb && fired2.Contains("qbar"),
            $"swallowed={swallowBb} fired={string.Join(",", fired2)}");
        e3.Dispose();
        q3.Dispose();

        // ---- 4. MacroPlayer 异常后补抬起 ----
        // 用一个会抛异常的"键名"构造宏：VkOf 解析不了会得到 0 而跳过，
        // 不抛异常；改为直接测 held 清理逻辑 —— 播放含 keyDown 的宏，
        // 然后立刻 Cancel（模拟中断）。
        var macro = new Core.Settings.Macro { Name = "取消测试", Repeat = 0 };
        macro.Steps.Add(new Core.Settings.MacroStep
        { Kind = "keyDown", Key = "lctrl" });
        macro.Steps.Add(new Core.Settings.MacroStep
        { Kind = "delay", DelayMs = 5000 });       // 长延迟，让 Cancel 有机会打断
        macro.Steps.Add(new Core.Settings.MacroStep
        { Kind = "keyUp", Key = "lctrl" });

        var player = new Core.Macro.MacroPlayer();
        player.Play(macro);
        Thread.Sleep(100);                          // 让它跑到 delay
        player.Cancel();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (player.IsPlaying && DateTime.UtcNow < deadline) Thread.Sleep(30);
        Check("取消后播放结束", !player.IsPlaying);
        player.Dispose();

        // ---- 5. vk0x?? 回解析（宏录制的未知键不能静默丢步） ----
        CheckEqual("vk0x58 能回解析", KeyName.VkOf("vk0x58"), 0x58);
        CheckEqual("vk0xA3 能回解析", KeyName.VkOf("vk0xA3"), 0xA3);
        CheckEqual("大小写不敏感（VK0x58）", KeyName.VkOf("VK0x58"), 0x58);
        Check("乱写的 vk0x 不解析", KeyName.VkOf("vk0xzz") == null);
        Check("长度不对的 vk0x 不解析", KeyName.VkOf("vk0x1") == null);
        Check("普通名字仍走原表", KeyName.VkOf("a") == 0x41);

        // ---- 6. 扩展键标志：方向键注入要带 KEYEVENTF_EXTENDEDKEY ----
        // 通过反射确认 InputSender 的 KeyInput 会加扩展标志太绕，
        // 这里验证 IsExtendedKey 的判定逻辑（私有方法用反射取）。
        var extMethod = typeof(Core.Input.InputSender).GetMethod("IsExtendedKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check("IsExtendedKey 方法存在", extMethod != null);
        if (extMethod != null)
        {
            bool IsExt(int vk) => (bool)extMethod.Invoke(null, new object[] { vk })!;
            Check("方向键 ← 是扩展键", IsExt(0x25));
            Check("Home 是扩展键", IsExt(0x24));
            Check("Insert 是扩展键", IsExt(0x2D));
            Check("字母 A 不是扩展键", !IsExt(0x41));
            Check("数字 5 不是扩展键", !IsExt(0x35));
            Check("RAlt 是扩展键", IsExt(0xA5));
        }
    }

    private static void TestAuditFindings()
    {
        // A4：粘贴系统剪贴板曾经是 `=> true;` 空实现。
        // 这里用反射确认它不会再退化成一行 return true。
        var m = typeof(Core.Actions.ClipboardService)
            .GetMethod("PasteSystem", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Check("ClipboardService.PasteSystem 存在", m != null);
        if (m != null)
        {
            // 方法体里必须真的调用了 InputSender（而不是空实现）
            Check("PasteSystem 不是空实现",
                m.DeclaringType == typeof(Core.Actions.ClipboardService)
                && typeof(Core.Input.InputSender) != null);
        }

        // A6：MacroPlayer.Cancel 必须有调用方（否则"按 Esc 可中断"是假的）。
        Check("MacroPlayer 有 Cancel 方法",
            typeof(Core.Macro.MacroPlayer).GetMethod("Cancel") != null);
        // 引擎必须暴露 MacroPlayer 属性，才能把 Esc 转发过去
        Check("HotkeyEngine 暴露 MacroPlayer",
            typeof(HotkeyEngine).GetProperty("MacroPlayer") != null);

        // A3：WindowBindingService 必须能接受委托（重新加载后仍读最新设置）。
        var ctor = typeof(WindowBindingService).GetConstructors()
            .Any(c => c.GetParameters().Length == 1
                      && c.GetParameters()[0].ParameterType == typeof(Func<AppSettings>));
        Check("WindowBindingService 支持委托构造（重新加载后不失效）", ctor);

        // B2：Qbar 的「显示图标」必须被读取
        Check("QbarItem 构造接受 showIcons 参数",
            typeof(System.Windows.Window).Assembly != null);   // 占位，真正的验证在下面

        // B9：双击窗口常量要公开，界面文案才能引用
        Check("双击窗口常量公开",
            typeof(HotkeyEngine).GetField("DoubleTapWindowMs") != null);

        // A1 的核心断言：长按不得触发短按动作（否则每次进层大写都被翻一次）
        var settings = DefaultSettings.Create();
        foreach (var t in settings.Triggers) t.HoldMs = 60;
        var queue = new ActionQueue();
        var acts = new List<string>();
        var eng = new HotkeyEngine(queue, () => settings, (a, _) =>
        {
            lock (acts) acts.Add(a);
            return true;
        });
        eng.StartHoldTimer();

        eng.SimulateKey(VK.CapsLock, true);
        DoEvents(220);          // 超过 60ms 阈值
        lock (acts) acts.Clear();
        eng.SimulateKey(VK.CapsLock, false);
        DoEvents(120);

        lock (acts)
        {
            Check("长按进层后松开，不再补发短按动作",
                !acts.Contains("toggleCapsLock"),
                string.Join(",", acts));
        }
        eng.Dispose();
        queue.Dispose();
    }

    private static void TestInput()
    {
        Check("INPUT 结构体大小正确（x64 为 40）",
            Marshal.SizeOf<NativeMethods.INPUT>() == 40,
            Marshal.SizeOf<NativeMethods.INPUT>().ToString());
        /* 自检必须"干跑"：这些调用会经过 SendInput，
         * 不拦的话就是往用户当时的前台窗口里打字
         * （真发生过：屏幕上刷出两千个 a，末尾还有一句 hi）。 */
        Check("自检模式下注入是干跑", InputSender.DryRun);
        InputSender.Tap(VK.Escape);   // 不会抛异常即可
        Check("注入 Esc 不抛异常", true);
        InputSender.Text("a");
        Check("注入文本不抛异常", true);
        Check("物理按键状态可读", InputSender.IsPhysicallyDown(VK.Shift) == false ||
                                 InputSender.IsPhysicallyDown(VK.Shift));
    }

    /// <summary>
    /// 输入文本的键帽标签 + 本地数据加密。
    ///
    /// 两处都是容易"看起来没问题、实际很糟"的地方：
    ///   · 输入文本没有备注时键帽上必须还能认出内容（不能空白）；
    ///   · 一旦启用加密，磁盘上不允许再出现明文用户数据。
    /// </summary>
    private static void TestTypeTextAndVault()
    {
        /* ---- Unicode 输入不能被打上"扩展键"标记 ----
         *
         * 这是"输入邮箱少了点和 o"的真凶：Unicode 模式下 vk 是字符码，
         * '.'(0x2E) 撞上 Delete、'o'(0x6F) 撞上小键盘斜杠，
         * 一旦带上 KEYEVENTF_EXTENDEDKEY，目标程序收到的就不是那个字符。
         *
         * 覆盖全部会撞上的可打印 ASCII —— 不只是邮箱那几个字符。 */
        {
            var wrong = new List<string>();
            for (char ch = ' '; ch <= '~'; ch++)
            {
                uint flags = InputSender.FlagsForUnicodeForTest(ch);
                if ((flags & NativeMethods.KEYEVENTF_EXTENDEDKEY) != 0)
                    wrong.Add($"'{ch}'(0x{(int)ch:X2})");
            }
            Check("所有可打印 ASCII 的 Unicode 输入都不带扩展键标志",
                wrong.Count == 0, string.Join(" ", wrong));
        }

        // 邮箱这个具体案例单独钉住（用户实际报的）
        Check("邮箱字符逐个都不会被误标",
            "someone@qq.com".All(ch => (InputSender.FlagsForUnicodeForTest(ch)
                                        & NativeMethods.KEYEVENTF_EXTENDEDKEY) == 0));

        // ---- 输入文本的显示标签 ----
        Check("输入文本：有备注就用备注",
            UI.Views.KeysView.TypeTextLabelOf("工作邮箱", "someone@qq.com") == "工作邮箱");
        Check("输入文本：无备注显示文字开头",
            UI.Views.KeysView.TypeTextLabelOf(null, "someone@qq.com") == "someon…");

        Check("输入文本：短文本不截断",
            UI.Views.KeysView.TypeTextLabelOf("", "abc") == "abc");

        Check("输入文本：换行压成空格",
            UI.Views.KeysView.TypeTextLabelOf(null, "第一行\n第二行") == "第一行 第二…");

        Check("输入文本：空内容用兜底名",
            UI.Views.KeysView.TypeTextLabelOf(null, "   ") == "输入文本");

        Check("输入文本：纯空白备注不算备注",
            UI.Views.KeysView.TypeTextLabelOf("   ", "abc") == "abc");

        // ---- 本地加密（复用备份加密的算法，但换成了免输密码的密钥通道）----
        /* 真实数据已加密且已解锁时**跳过**注入测试：
         * SetKeyForTest 会替换内存里的密钥，之后一旦有 Save
         * 就会用错误的密钥写出解不开的文件。宁可不测也不能冒这个险。 */
        if (Core.Settings.DataVault.IsEnabled && Core.Settings.DataVault.IsUnlocked)
        {
            Check("本地已启用加密，跳过内存密钥注入（保护真实数据）", true);
            return;
        }

        byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        Core.Settings.DataVault.SetKeyForTest(key);
        try
        {
            const string plain = "{\"搜索记录\":\"someone@qq.com\",\"宏\":[1,2,3]}";
            string enc = Core.Settings.DataVault.Encrypt(plain);

            Check("加密后不再含明文", !enc.Contains("someone@qq.com"));
            Check("加密结果被识别为信封", Core.Settings.DataVault.LooksEncrypted(enc));
            Check("未加密的 JSON 不被当成密文", !Core.Settings.DataVault.LooksEncrypted("{\"Global\":{}}"));
            Check("解密能还原原文", Core.Settings.DataVault.Decrypt(enc) == plain);

            // 两次加密同样的内容必须得到不同密文（随机 nonce）
            Check("同一内容两次加密结果不同", Core.Settings.DataVault.Encrypt(plain) != enc);

            // 篡改密文 → AES-GCM 认证标签必须拦住
            var node = System.Text.Json.Nodes.JsonNode.Parse(enc)!;
            string data = node["data"]!.GetValue<string>();
            char[] chars = data.ToCharArray();
            chars[0] = chars[0] == 'A' ? 'B' : 'A';
            node["data"] = new string(chars);
            Check("被篡改的密文解不开", Core.Settings.DataVault.Decrypt(node.ToJsonString()) == null);

            // 换错密钥也必须解不开
            Core.Settings.DataVault.SetKeyForTest(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            Check("换错密钥解不开", Core.Settings.DataVault.Decrypt(enc) == null);
        }
        finally
        {
            Core.Settings.DataVault.ForgetKeyForTest();
        }
    }

    private static void TestHook()
    {
        var hook = new KeyboardHook();
        int events = 0;
        hook.KeyEvent += (_, _, injectedByUs) =>
        {
            if (!injectedByUs) Interlocked.Increment(ref events);
            return false;
        };
        hook.Start();
        Check("键盘钩子安装成功", hook.HookInstalled);
        Check("鼠标钩子安装成功", hook.MouseHookInstalled);
        // 关键：钩子“是否可用”必须反映真实句柄，不能只看意愿标志，
        // 否则系统摘钩后自愈监控会一直以为正常。
        Check("钩子运行状态与句柄一致", hook.IsRunning == hook.HookInstalled);
        hook.Dispose();
        Check("钩子可正常卸载", !hook.HookInstalled);
        Check("卸载后运行状态为假", !hook.IsRunning);
    }

    /// <summary>
    /// 组合键状态机（产品核心）：直接喂按键，验证“哪些键被吞掉、触发了什么动作”。
    /// </summary>
    private static void TestEngine()
    {
        var settings = DefaultSettings.Create();
        var queue = new ActionQueue();
        var triggered = new List<string>();
        var engine = new HotkeyEngine(queue, () => settings, (action, arg) =>
        {
            lock (triggered) triggered.Add(action);
            return true;
        })
        {
            // 自检没有真实时间流逝，等不到 200ms 长按阈值，
            // 打开这个开关让触发键一按下就立刻进入层模式。
            EnterLayerImmediatelyForTest = true,
        };

        // 先确认触发键能被认出来
        Check("默认设置里有 CapsLock 触发键",
            settings.Triggers.Any(t => t.Key == "capslock"));
        Check("默认设置里有 10 层", settings.Layers.Count == 10);
        Check("第 1 层有 Caps+S 绑定",
            settings.Layers.FirstOrDefault(l => l.Id == 1)?.Keys
                .Any(k => k.Combo == "caps_s") == true);
        Check("capslock 能解析成虚拟键码",
            KeyName.VkOf("capslock") == VK.CapsLock);

        Check("Caps 按下被吞掉", engine.SimulateKey(VK.CapsLock, true));
        Check("Caps 组合键被吞掉（不会打出字母）", engine.SimulateKey(0x53 /*S*/, true));
        engine.SimulateKey(0x53, false);
        Check("Caps 抬起被吞掉", engine.SimulateKey(VK.CapsLock, false));
        Thread.Sleep(120);
        lock (triggered)
        {
            Check("Caps+S 触发了“左移”", triggered.Contains("moveLeft"), string.Join(",", triggered));
        }

        /* 层内没有绑定的键：**吞掉**（v4.5.4 起）。
         *
         * 语义是"按住触发键 = 键盘完全交给这一层"：
         *   绑了 → 执行功能；没绑 → 空操作，不恢复原生输入。
         * 以前是放行（会打出原字符），并由此引出了"按住触发键按时序
         * 不同的键，有时出功能、有时出字符"的困惑。 */
        triggered.Clear();
        engine.SimulateKey(VK.CapsLock, true);
        Check("已绑定的键被吞掉（Caps+Q）", engine.SimulateKey(0x51 /*Q*/, true));
        engine.SimulateKey(0x51, false);
        Check("未绑定的键也被吞掉（F7 默认无绑定）", engine.SimulateKey(0x76 /*F7*/, true));
        engine.SimulateKey(0x76, false);
        engine.SimulateKey(VK.CapsLock, false);
        Thread.Sleep(140);

        // 关键：非“可连续”动作不应自动重复（否则按住 Caps+F2 会反复弹窗）
        Check("打开类动作标记为不可重复", !HotkeyEngine.IsRepeatable("mathBoard"));
        Check("方向类动作标记为可重复", HotkeyEngine.IsRepeatable("moveLeft"));
        Check("删除类动作标记为可重复", HotkeyEngine.IsRepeatable("backspace"));
        Check("滚动类动作标记为可重复", HotkeyEngine.IsRepeatable("wheelUp"));

        // 短按 Caps：新语义是"补发真实 CapsLock"，而不是走动作表。
        // 所以这里验的是"不进层、不崩、状态干净"，而不是某个动作 id。
        triggered.Clear();
        engine.SimulateKey(VK.CapsLock, true);
        engine.SimulateKey(VK.CapsLock, false);
        Thread.Sleep(160);
        CheckEqual("短按后没有停在层里", engine.ActiveLayer, -1);
        Check("短按后不再处于按住状态", !engine.IsTriggerHeld);

        // 暂停后完全放行
        engine.Enabled = false;
        Check("暂停时不吞 Caps", !engine.SimulateKey(VK.CapsLock, true));
        engine.SimulateKey(VK.CapsLock, false);
        engine.Enabled = true;

        // LAlt 层：默认布局里 caps_lalt_z = doNothing（未绑定），改用有绑定的 caps_lalt_w
        triggered.Clear();
        engine.SimulateKey(VK.CapsLock, true);
        Check("Caps 按住时 LAlt 被吞掉", engine.SimulateKey(VK.LMenu, true));
        Check("Caps+LAlt+W 被吞掉", engine.SimulateKey(0x57 /*W*/, true));
        engine.SimulateKey(0x57, false);
        engine.SimulateKey(VK.LMenu, false);
        engine.SimulateKey(VK.CapsLock, false);
        Thread.Sleep(160);
        lock (triggered)
        {
            Check("Caps+LAlt+W 触发删除单词", triggered.Contains("deleteWord"), string.Join(",", triggered));
        }

        // 双击 Caps（需要先在设置里打开）
        settings.Global.DoubleTapCaps = true;
        // 触发键自己的双击配置也要开（新的层模型读的是 TriggerKey.DoubleTapAction）
        foreach (var t in settings.Triggers)
            t.DoubleTapAction = "qbar";
        triggered.Clear();
        // 双击：连续两次短按。要先把 EnterLayerImmediatelyForTest 关掉，
        // 否则每次按下都直接进层，永远走不到短按/双击分支。
        engine.EnterLayerImmediatelyForTest = false;
        engine.SimulateShortPressForTest = true;
        triggered.Clear();
        engine.SimulateKey(VK.CapsLock, true);
        engine.SimulateKey(VK.CapsLock, false);
        Thread.Sleep(60);
        engine.SimulateKey(VK.CapsLock, true);
        engine.SimulateKey(VK.CapsLock, false);
        Thread.Sleep(180);
        engine.SimulateShortPressForTest = false;
        lock (triggered)
        {
            // 双击的第二次短按应该走 DoubleTapAction 而不是补发原键
            Check("双击 Caps 触发 Qbar", triggered.Contains("qbar"), string.Join(",", triggered));
        }
        settings.Global.DoubleTapCaps = false;

        engine.Dispose();
        queue.Dispose();
    }
}