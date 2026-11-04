using System.Runtime.InteropServices;
using CapsLockPlus.Core.Interop;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 统一的输入注入器。所有“替用户按键”的动作都走这里，保证：
/// 1) 注入事件带标记，钩子会忽略自己注入的按键，避免自我触发；
/// 2) 修饰键状态被正确还原（按下后一定抬起）；
/// 3) 支持 Unicode 文本直接输入，中文/emoji 也能发。
/// </summary>
public static class InputSender
{
    /// <summary>注入事件附加标记，钩子里用 dwExtraInfo 判断。</summary>
    public static readonly IntPtr InjectionTag = new(0x434C5021); // "CLP!"

    private static readonly int InputSize = System.Runtime.InteropServices.Marshal.SizeOf<INPUT>();

    /// <summary>
    /// 干跑：只跑逻辑，不真的把按键发到系统里。
    ///
    /// 自检 / 离屏渲染时必须打开。这些流程会走「输入文本」「播放宏」
    /// 之类的真实动作 —— 一旦真发出去，就等于往用户当时的前台窗口里
    /// 打字（曾经在 `--selftest` 里打过两千个 a，还顺手打了一句 hi）。
    ///
    /// 放在这里而不是在每个调用点判断：注入只有这一个出口，
    /// 关掉出口就不可能漏，将来新增的测试也不会重犯。
    /// </summary>
    public static bool DryRun { get; set; }

    /// <summary>
    /// 最近一次注入失败的错误码（0 = 没失败过）。
    ///
    /// SendInput 在**前台窗口属于更高完整性级别**时会被 UIPI 拒绝（错误 5），
    /// 这跟"键盘钩子坏了"完全不是一回事。以前注入失败被静默吞掉，
    /// 于是自检判成"钩子收不到按键" → 重装 → 兜底接管，一路把正常状态搞坏。
    /// </summary>
    public static int LastInjectError { get; private set; }

    /// <summary>
    /// 最近一次真正注入按键的时间。
    ///
    /// 引擎的自愈逻辑要参考它：注入的按键会占用输入队列、也会让
    /// `GetAsyncKeyState` 在极短时间内读不准，此时绝不能判"触发键松开了"
    /// 去复位层 —— 那正是"第一次有效、第二次变回原生功能"的根因。
    /// </summary>
    public static DateTime LastInjectAt { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// 启动静默期：这段时间里不往系统里注入任何按键（钩子探针除外）。
    ///
    /// 为什么要它：有用户反馈"程序一启动就莫名打出一个字母 C / 像按着 Ctrl"。
    /// 我们查不到是哪条路径发的（日志里没有痕迹）。与其继续猜，不如在启动的
    /// 头两秒**物理上不许注入**，并且把"试图注入"记进日志 ——
    /// 真有问题时日志会直接指出是哪一次调用，没有问题则用户也看不到副作用。
    /// </summary>
    public static DateTime QuietUntil { get; set; } = DateTime.MinValue;

    /// <summary>注入审计：本次会话注入了几次（只用于日志与自检）。</summary>
    public static int InjectCount { get; private set; }

    /// <summary>注入序号：日志里带 #n，两个进程的日志交错时也能排出先后。</summary>
    public static long InjectSeq => Interlocked.Read(ref _injectSeq);

    private static long _injectSeq;

    /// <summary>本次会话最近一次注入的简述（诊断用）。</summary>
    public static string LastInjectSummary { get; private set; } = "";

    /// <summary>前 N 次注入写进日志，之后不再刷屏。</summary>
    private const int InjectLogLimit = 40;

    private static int _injectLogged;
    private static bool _quietBlockLogged;
    private static bool _allowDuringQuiet;

    private static void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0 || DryRun) return;

        if (!_allowDuringQuiet && DateTime.Now < QuietUntil)
        {
            if (!_quietBlockLogged)
            {
                _quietBlockLogged = true;
                CoreLog.Warn("[注入] 启动静默期内拦下一次注入（可能是这次启动的杂键来源）");
            }
            return;
        }

        SendInput((uint)inputs.Length, inputs, InputSize);
        LastInjectAt = DateTime.Now;
        Interlocked.Increment(ref _injectSeq);
        InjectCount++;

        if (_injectLogged < InjectLogLimit)
        {
            _injectLogged++;
            try
            {
                var parts = new List<string>();
                foreach (var input in inputs)
                {
                    if (input.type == INPUT_KEYBOARD)
                    {
                        int vk = (int)input.U.ki.wVk;
                        bool up = (input.U.ki.dwFlags & KEYEVENTF_KEYUP) != 0;
                        bool uni = (input.U.ki.dwFlags & KEYEVENTF_UNICODE) != 0;
                        parts.Add(uni ? $"U+{vk:X4}" : KeyName.NameOf(vk) + (up ? "↑" : "↓"));
                    }
                    else
                    {
                        parts.Add("mouse");
                    }
                }
                LastInjectSummary = string.Join(" ", parts);
                CoreLog.Info($"[注入#{Interlocked.Read(ref _injectSeq)}] {LastInjectSummary}");
            }
            catch { }
        }
    }

    private static INPUT KeyInput(int vk, bool up, bool unicode = false, ushort scan = 0)
    {
        uint flags = 0;
        if (up) flags |= KEYEVENTF_KEYUP;
        if (unicode) flags |= KEYEVENTF_UNICODE;

        /* ⚠ 这里**不能**加 KEYEVENTF_SCANCODE。
         *
         * 一旦指定 KEYEVENTF_SCANCODE，系统就只按 wScan 投递、忽略 wVk，
         * 目标程序拿到的按键信息不完整 —— Chromium / Qt 系程序
         * （Chrome、Edge、微信）会判定"这不是一次正常快捷键"而不复制。
         *
         * 正确做法：**同时**填 wVk 和 wScan，但**不带** SCANCODE 标志。
         * 系统按虚拟键走正常投递路径，wScan 作为附带信息一并送达，
         * 需要看扫描码的程序同样能认出来。
         *
         * 扩展键必须带 KEYEVENTF_EXTENDEDKEY，否则方向键、Home/End、
         * Ins/Del、小键盘会被"按扫描码识别按键"的程序当成小键盘变体。 */
        /* ⚠ Unicode 模式下 vk 参数其实是**字符码**，不是虚拟键码，
         * 绝不能按虚拟键的规则判断"是不是扩展键"。
         *
         * 否则会踩一串隐蔽的坑（IsExtendedKey 的判定值域和 ASCII 完全重叠）：
         *   '.' = 0x2E → 被当成 Delete
         *   'o' = 0x6F → 被当成小键盘 /
         *   ',' = 0x2C → 被当成 PrintScreen
         *   '!' = 0x21 → 被当成 PgUp ……
         * 加了 EXTENDEDKEY 之后目标程序收到的就不是这个字符，
         * 表现是**凭空少字**：输入邮箱 `@qq.com` 会变成 `@qqcm`。
         * 这正是"邮箱输不全"的根因。 */
        if (!unicode && IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;

        /* 扫描码：不传就自己查一个。
         *
         * 为什么不能留 0：靠**扫描码**认键的程序拿到 wScan=0 的合成按键会直接
         * 认不出来。RustDesk 这类远程桌面客户端就是典型 —— 它把本地按键的
         * 扫描码原样转给被控端，扫描码为 0 的按键只有一部分能落地：
         * 实测 14 个字符的密码，落进去的正好是**不需要 Shift 的那 7 个**，
         * 大写字母与符号（要 Shift 的）全丢 —— 用户看到的就是
         * "明明 14 位，只输了 11 位，密码框报大小写/长度不对"。
         *
         * 同一个坑在 Ctrl+C 上已经踩过一次（见 CopyChord 的注释），
         * 那次只修了复制；凡是"要目标程序认出这是一个正常按键"的场合，
         * 扫描码都不能缺。所以放在这里统一补，而不是每个调用点各写一遍。
         *
         * Unicode 模式例外：那时 vk 是字符码，不是虚拟键码，查不了也不该查。 */
        if (!unicode && scan == 0) scan = ScanOf(vk);

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)(unicode ? 0 : vk),
                    wScan = unicode ? (ushort)vk : scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = InjectionTag,
                },
            },
        };
    }

    /// <summary>
    /// 该虚拟键是否属于"扩展键"（需要 KEYEVENTF_EXTENDEDKEY 的那批）。
    ///
    /// 判定规则：VK &gt;= 0x21 且**不在**主键盘区的那些导航 / 编辑键，
    /// 加上小键盘（NumLock 关闭时它们是方向/编辑语义）、右边的修饰键、
    /// Break / PrintScreen。
    /// </summary>
    private static bool IsExtendedKey(int vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 => true,   // PgUp/PgDn/End/Home
        0x25 or 0x26 or 0x27 or 0x28 => true,   // ←↑→↓
        0x2D or 0x2E => true,                   // Insert/Delete
        0x5B or 0x5C => true,                   // LWin/RWin
        0x6F => true,                           // Numpad /（数字Enter/小键盘运算符在部分布局也是）
        0x90 or 0x91 => true,                   // NumLock/ScrollLock
        0xA0 or 0xA1 or 0xA3 or 0xA5 => true,   // RShift/LControl(极少数布局)/RControl/RAlt
        0x13 => true,                           // Pause
        0x2C => true,                           // PrintScreen
        _ => false,
    };

    /// <summary>按下或抬起一个虚拟键。</summary>
    /// <summary>
    /// 自检用：替换"真实注入"。默认 null = 正常发键。
    /// 有了它才能验证改键的按下/抬起是否成对 —— 否则自检会真的往系统里打字。
    /// </summary>
    internal static Action<int, bool>? SenderForTest { get; set; }

    public static void KeyDown(int vk) => SendKey(vk, up: false);
    public static void KeyUp(int vk) => SendKey(vk, up: true);

    private static void SendKey(int vk, bool up)
    {
        if (SenderForTest is { } spy) { spy(vk, up); return; }
        Send(KeyInput(vk, up));
    }

    /// <summary>
    /// 敲一个**不带本程序标记**的键（dwExtraInfo = 0）。
    ///
    /// 只给"钩子自检"用：普通注入带 <see cref="InjectionTag"/>，会被钩子当成
    /// "自己发的"放行 —— 那样探不到"钩子到底还能不能收键"。
    /// 自检键固定用 F24（0x87）：常规程序不认这个键，按下去不会有副作用。
    /// </summary>
    public static bool TapUntagged(int vk)
    {
        if (DryRun) return false;

        var down = KeyInput(vk, up: false);
        down.U.ki.dwExtraInfo = IntPtr.Zero;     // 关键：不带标记
        if (SendInput(1, new[] { down }, InputSize) == 0)
        {
            LastInjectError = Marshal.GetLastWin32Error();
            return false;
        }

        System.Threading.Thread.Sleep(12);

        var upInput = KeyInput(vk, up: true);
        upInput.U.ki.dwExtraInfo = IntPtr.Zero;
        if (SendInput(1, new[] { upInput }, InputSize) == 0)
        {
            LastInjectError = Marshal.GetLastWin32Error();
            return false;
        }

        LastInjectAt = DateTime.Now;
        return true;
    }

    /// <summary>完整敲击一个虚拟键（按下+抬起）。</summary>
    public static void Tap(int vk)
    {
        Send(KeyInput(vk, false), KeyInput(vk, true));
    }

    /// <summary>敲击一个虚拟键 N 次（等同于 SendInput {Left 5}）。</summary>
    public static void Tap(int vk, int times)
    {
        if (times <= 0) return;
        var arr = new INPUT[times * 2];
        for (int i = 0; i < times; i++)
        {
            arr[i * 2] = KeyInput(vk, false);
            arr[i * 2 + 1] = KeyInput(vk, true);
        }
        Send(arr);
    }

    /// <summary>按住一组修饰键，执行动作，再按相反顺序释放。</summary>
    public static void WithModifiers(IEnumerable<int> modifiers, Action action)
    {
        var mods = modifiers.Distinct().ToList();
        foreach (var m in mods) KeyDown(m);
        try
        {
            action();
        }
        finally
        {
            for (int i = mods.Count - 1; i >= 0; i--) KeyUp(mods[i]);
        }
    }

    public static void Chord(int modifier, int vk)
    {
        KeyDown(modifier);
        try { Tap(vk); }
        finally { KeyUp(modifier); }
    }

    public static void Ctrl(int vk) => Chord(VK.Control, vk);
    public static void Shift(int vk) => Chord(VK.Shift, vk);
    public static void CtrlShift(int vk)
    {
        KeyDown(VK.Control);
        KeyDown(VK.Shift);
        try { Tap(vk); }
        finally
        {
            KeyUp(VK.Shift);
            KeyUp(VK.Control);
        }
    }

    /// <summary>
    /// 发一次 Ctrl+&lt;vk&gt;，但**带扫描码、分四次发送、每次间隔 8ms**。
    ///
    /// 为什么不能直接用 <see cref="Ctrl"/>：那条路径的 <c>wScan</c> 是 0，
    /// 而 Chromium（Chrome / Edge）与 Qt（微信 4.x）这类程序在处理快捷键时
    /// 会看扫描码 —— 留 0 的合成按键有被忽略的风险，表现就是
    /// "在浏览器里选中文字后按复制，程序收不到"。
    ///
    /// 另外四个按键必须分开发：塞在同一次 SendInput 里，
    /// 部分程序的按键队列会漏掉后半截（Ctrl 按下但 C 没到）。
    /// </summary>
    /// <param name="vk">与 Ctrl 组合的主键</param>
    /// <param name="gapMs">每次发送之间的间隔（毫秒）</param>
    public static void CopyChord(int vk, int gapMs = 8)
    {
        ushort ctrlScan = ScanOf(VK.Control);
        ushort keyScan = ScanOf(vk);

        void Send(int k, ushort scan, bool up)
        {
            var input = KeyInput(k, up, unicode: false, scan: scan);
            if (!DryRun) SendInput(1, new[] { input }, InputSize);
            if (gapMs > 0) Thread.Sleep(gapMs);
        }

        Send(VK.Control, ctrlScan, up: false);
        try
        {
            Send(vk, keyScan, up: false);
            Send(vk, keyScan, up: true);
        }
        finally
        {
            /* 无论如何都要把 Ctrl 抬起来：漏掉这一下，用户会看到
             * "程序像是一直按着 Ctrl"，随后点什么都变成快捷键。 */
            Send(VK.Control, ctrlScan, up: true);
        }
    }

    /// <summary>取虚拟键对应的扫描码（取不到则返回 0，退回不带扫描码的行为）。</summary>
    private static ushort ScanOf(int vk)
    {
        try
        {
            ushort scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
            return (ushort)(scan & 0xFF);
        }
        catch { return 0; }
    }

    private const uint MAPVK_VK_TO_VSC = 0;

    /// <summary>文本输入方式。</summary>
    public enum TextMode
    {
        /// <summary>
        /// 自动（默认）：长文本走剪贴板粘贴；短文本纯 ASCII 用**真实按键**逐字敲，
        /// 其它用 Unicode 注入。
        ///
        /// 为什么短文本用"真实按键"：密码框（浏览器、网银控件、部分安全软件）
        /// 会过滤掉 <c>KEYEVENTF_UNICODE</c> 这种"没有扫描码"的合成输入 ——
        /// 表现就是"文字打不进去，但普通输入框正常"。真实按键带完整扫描码，
        /// 和手打没有区别，这类框都认。
        ///
        /// 为什么长文本改用剪贴板：逐字注入在长文本上会被目标程序的输入吞吐
        /// 卡住，实测会零星丢字/错字符（见 <see cref="ClipboardPaster"/>）。
        /// </summary>
        Auto,

        /// <summary>强制用真实按键（受键盘布局与输入法影响，但最像手打）。</summary>
        Keys,

        /// <summary>强制 Unicode 注入（不经过布局与输入法，中文/emoji 用它）。</summary>
        Unicode,
    }

    /// <summary>输入任意文本。</summary>
    public static void Text(string text, TextMode mode = TextMode.Auto)
    {
        if (string.IsNullOrEmpty(text)) return;

        /* 长文本改走剪贴板粘贴。
         *
         * 逐字注入在长文本上会被目标程序的输入吞吐卡住 —— 实测 Win11 记事本
         * 86 字一批能丢 10%，同一段文字在**英文布局**下也照样丢，所以这不是
         * 输入法的问题，是那条通道本身的上限。剪贴板粘贴一次完成，长度无关。
         *
         * 只改 Auto（默认）这一档：显式选了 Keys / Unicode 的用户是有具体理由的
         * （密码框不许粘贴、目标程序不认 Ctrl+V），不擅自把他们选的通道换掉。
         * 短文本也不受影响，仍然走原来的路 —— 那条路不碰剪贴板。
         *
         * TryPaste 返回 false（剪贴板拿不全 / 写不进去 / 不在 STA 线程）时
         * 原路继续，一个字都不会少。 */
        if (mode == TextMode.Auto && ShouldUseClipboard(text) && ClipboardPaster.TryPaste(text)) return;

        if (mode == TextMode.Keys || (mode == TextMode.Auto && LooksLikePlainAscii(text)))
        {
            if (TryTextAsKeys(text)) return;

            /* 按键模拟不可用（布局映射不出来 / 没有前台窗口）时退回 Unicode。
             * 强制 Keys 也一样退回 —— 什么都不输入比输入得"不够完美"更糟。 */
        }

        TextUnicode(text);
    }

    /// <summary>
    /// 这段文字够不够长、值得改走剪贴板粘贴。
    ///
    /// 只看长度：换行、中文、emoji 在剪贴板路径里都没有额外风险，
    /// 反而是逐字注入更难处理的那几种。
    /// </summary>
    internal static bool ShouldUseClipboard(string text)
        => text.Length >= ClipboardPaster.Threshold;

    /// <summary>
    /// 这段文字适不适合用"真实按键"逐字敲。
    ///
    /// 只放行可打印 ASCII（0x20~0x7E）且不含换行：
    ///   · 中文/emoji 必须走 Unicode（没有对应的按键）；
    ///   · 换行会变成 Enter，可能把表单提交掉 —— 多行一律走 Unicode 粘贴式注入。
    /// </summary>
    public static bool LooksLikePlainAscii(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char ch in text)
            if (ch < 0x20 || ch > 0x7E) return false;
        return true;
    }

    // ---------------- 大写锁定 ----------------

    /// <summary>
    /// 大写锁定当前是否开启。
    ///
    /// 必须用 <c>GetKeyState</c>：它的最低位是真正的 toggle 位。
    /// <c>GetAsyncKeyState</c> 的最低位是"上次调用之后按过没有"，
    /// 连读两次第二次就变成 false —— 拿它当开关用会时对时错。
    /// </summary>
    internal static bool CapsLockOn => (GetKeyState(VK.CapsLock) & 1) != 0;

    /// <summary>
    /// 这个字符是不是"大写锁定会翻转 Shift 语义"的键（A–Z / a–z）。
    ///
    /// 大写锁定只影响**字母键**：Caps + 1 还是 1，Caps + Shift + 1 还是 !。
    /// 所以数字与符号一律不参与翻转 —— 这也是判断规则必须按"字符是不是字母"
    /// 而不是"这个键有没有 Shift 位"的原因（'!' 同样需要 Shift，但不翻转）。
    ///
    /// 本类只处理可打印 ASCII（见 <see cref="LooksLikePlainAscii"/>），
    /// 判断 ASCII 字母就够了，不必操心非拉丁字母布局。
    /// </summary>
    internal static bool IsCapsSensitive(char ch)
        => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    /// <summary>
    /// 实际要不要按 Shift = 布局给出的 Shift ⊕ 大写锁定（只对字母生效）。
    /// </summary>
    internal static bool NeedsShift(bool shiftFromLayout, bool capsSensitive, bool capsOn)
        => shiftFromLayout ^ (capsSensitive && capsOn);

    /// <summary>
    /// 自检用：按英文（美国）布局把一段 ASCII 算成"每个字符要不要按 Shift"。
    /// 返回 null = 布局取不到或字符映射不了（极少数环境），调用方跳过断言。
    /// </summary>
    internal static bool[]? ShiftPlanForTest(string text, bool capsOn)
    {
        IntPtr layout = ImeGuard.EnglishLayout;
        if (layout == IntPtr.Zero) return null;

        var result = new bool[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            short scan = VkKeyScanEx(text[i], layout);
            if (scan == -1) return null;
            bool shiftFromLayout = (((scan >> 8) & 0xFF) & 0x01) != 0;
            result[i] = NeedsShift(shiftFromLayout, IsCapsSensitive(text[i]), capsOn);
        }
        return result;
    }

    /// <summary>
    /// 用真实按键逐字敲一段纯 ASCII。返回 false = 这条路走不通，调用方应改用 Unicode。
    ///
    /// 五个关键点：
    ///   1. 用**前台窗口所在线程的键盘布局**做映射（<c>VkKeyScanEx</c>），
    ///      否则德语键盘上打 `@` 会出错；
    ///   2. 需要 Ctrl/Alt 才能打出的字符直接放弃（会干扰系统快捷键）；
    ///   3. 输入语言是中文/日文/韩文/越南文时，字母会被输入法拼成候选 ——
    ///      临时把窗口切到英文布局，打完再切回（见 <see cref="ImeGuard"/>）；
    ///      切不动就放弃，让调用方走 Unicode（那条路不经过布局，同样能落字）；
    ///   4. 老式 IMM32 程序额外摘一次输入法上下文，便宜且更彻底；
    ///   5. **大写锁定开着时字母的 Shift 语义是反的**，每批发送前把
    ///      「布局给的 Shift」与「CapsLock」异或一次，否则整串字母的大小写
    ///      会对调（见 <see cref="NeedsShift"/>）。
    /// </summary>
    public static bool TryTextAsKeys(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (!LooksLikePlainAscii(text)) return false;

        IntPtr target = GetForegroundWindow();
        if (target == IntPtr.Zero) return false;

        uint tid = GetWindowThreadProcessId(target, out _);
        if (tid == 0) return false;

        IntPtr original = GetKeyboardLayout(tid);
        if (original == IntPtr.Zero) return false;

        /* 中文输入法开着时替用户敲 "hello"，输入法会当成拼音弹候选框，
         * 文本框里一个字都没有。切到英文布局再敲 —— 也是唯一对 TSF 程序
         * （Win11 记事本 / Edge / VS Code）有效的办法。
         *
         * 干跑（自检 / 离屏渲染）时**不能**真去切：
         * 那会把用户当时前台窗口的输入语言改掉，等于测试干扰了桌面。 */
        IntPtr typing = original;
        bool switched = false;

        /* ⚠ 输入法开着时，**不再**去切目标窗口的输入语言。
         *
         * 老做法是 WM_INPUTLANGCHANGEREQUEST → 切到英文布局 → 敲字 → 切回来。
         * 问题是：切换是异步的，目标程序一旦不理会（或切回失败），
         * 用户就被**永久留在英文布局**上 —— 用户的原话是"输入法也被变了"。
         * 这比"字没打出来"更烦人，而且用户往往不知道去哪儿改回来。
         *
         * 现在：输入法开着就直接走 Unicode 注入（不经过布局和输入法，
         * 中文/emoji 都能落字，也不会动用户的输入法状态）。
         * TryTextAsKeys 返回 false，上层会自动退回 TextUnicode。 */
        if (!DryRun && ImeGuard.IsComposing(original) && !ForceKeyTyping)
            return false;

        try
        {
            /* 先把整段文字映射成 (虚拟键, 布局是否需要 Shift, 是否受大写锁定影响)，
             * 有一个映射不了就整体放弃。 */
            var plan = new List<(int Vk, bool Shift, bool CapsSensitive)>(text.Length);
            foreach (char ch in text)
            {
                short scan = VkKeyScanEx(ch, typing);
                if (scan == -1) return false;

                int vk = scan & 0xFF;
                int state = (scan >> 8) & 0xFF;
                if (vk == 0) return false;
                if ((state & 0x06) != 0) return false;      // 需要 Ctrl / Alt
                plan.Add((vk, (state & 0x01) != 0, IsCapsSensitive(ch)));
            }

            IntPtr oldIme = SuppressIme(target);
            try
            {
                const int Batch = 32;
                for (int start = 0; start < plan.Count; start += Batch)
                {
                    int len = Math.Min(Batch, plan.Count - start);
                    var arr = new List<INPUT>(len * 4);

                    /* 每批读一次大写锁定。
                     *
                     * 为什么必须读：布局给的 Shift 是"大写锁定关着"时的答案。
                     * 大写锁定开着时字母键的语义是**反的** ——
                     *   Caps + A = a，Caps + Shift + A = A，
                     * 于是整串字母的大小写照原样翻个面：用户要 "Abc"，
                     * 打出来是 "aBC"，密码框立刻报"大小写错误"。
                     * 这在密码场景里最要命：错一次就可能被计一次失败。
                     *
                     * 为什么按批读而不是全程读一次：2000 字的文本要发几百毫秒，
                     * 期间用户真去按了 CapsLock 也能立刻跟上（代价只是一次
                     * GetKeyState，可以忽略）。 */
                    bool capsOn = CapsLockOn;

                    for (int i = start; i < start + len; i++)
                    {
                        var (vk, shiftFromLayout, capsSensitive) = plan[i];
                        bool shift = NeedsShift(shiftFromLayout, capsSensitive, capsOn);

                        if (shift) arr.Add(KeyInput(VK.Shift, false));
                        arr.Add(KeyInput(vk, false));
                        arr.Add(KeyInput(vk, true));
                        if (shift) arr.Add(KeyInput(VK.Shift, true));
                    }

                    Send(arr.ToArray());
                    if (start + len < plan.Count) Thread.Sleep(6);
                }
            }
            finally
            {
                RestoreIme(target, oldIme);
            }
        }
        finally
        {
            /* 还原顺序是硬约束，踩过：
             * 注入的按键进的是**输入队列**，而 PostMessage 的消息优先级
             * **高于**输入消息（GetMessage 的取用顺序是 发送消息 > 投递消息 >
             * 输入消息）。所以 post 完立刻还原，剩下的字母会带着中文布局
             * 重新翻译 —— 现象是"只有第一个字母落了字，后面全被拼成拼音"。
             * 必须等目标线程把按键消化完再还。 */
            if (switched)
            {
                Thread.Sleep(TypingSettleMs);
                ImeGuard.Restore(target, original, LayoutSwitchTimeoutMs);
            }
        }

        return true;
    }

    /// <summary>
    /// 是否强制用"真实按键"输入文本（会临时切换目标的输入语言）。
    ///
    /// 默认 false：输入法开着时改走 Unicode 注入，绝不碰用户的输入法。
    /// 只有在"目标程序不认 Unicode 注入"（个别游戏、老式控件）时才需要打开。
    /// </summary>
    public static bool ForceKeyTyping { get; set; }

    /// <summary>切换输入语言的等待上限。切换是异步的，要轮询到真正生效。</summary>
    private const int LayoutSwitchTimeoutMs = 200;

    /// <summary>
    /// 敲完字到还原输入语言之间必须留的间隔。
    ///
    /// 投递消息的优先级高于输入消息，间隔为 0 会让还没被消化的字母
    /// 在中文布局下重新翻译（被输入法拼成拼音）。100ms 对响应正常的
    /// 程序有足够余量，慢程序也有逐批 6ms 的节流兜着。
    /// </summary>
    private const int TypingSettleMs = 100;

    // ---------------- 输入法临时挂起 ----------------

    [System.Runtime.InteropServices.DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

    /// <summary>
    /// 临时把输入法从前台窗口上摘下来，返回原来的上下文（用于恢复）。
    ///
    /// 不摘的话，中文输入法会把逐字发送的字母当成拼音组合，
    /// 屏幕上出现候选框而不是要输入的文字。
    /// </summary>
    private static IntPtr SuppressIme(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            return ImmAssociateContext(hwnd, IntPtr.Zero);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static void RestoreIme(IntPtr hwnd, IntPtr old)
    {
        try
        {
            if (hwnd != IntPtr.Zero && old != IntPtr.Zero) ImmAssociateContext(hwnd, old);
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }

    /// <summary>
    /// 自检用：这个虚拟键发出去时带的扫描码。
    ///
    /// 存在的意义是守住"扫描码不能是 0"这条底线（见 <see cref="KeyInput"/> 里的长注释）：
    /// 缺扫描码时，靠扫描码认键的程序（远程桌面客户端、部分密码控件）会漏键 ——
    /// 14 位密码只落 11 位就是这么来的。
    /// </summary>
    internal static ushort ScanForTest(int vk) => KeyInput(vk, up: false).U.ki.wScan;

    /// <summary>自检用：按英文布局把字符翻成 (vk|shift 位)，用于核对扫描码。</summary>
    internal static short VkKeyScanForTest(char ch)
    {
        IntPtr layout = ImeGuard.EnglishLayout;
        return layout == IntPtr.Zero ? (short)-1 : VkKeyScanEx(ch, layout);
    }

    /// <summary>自检用：Unicode 模式下 wScan/字符码不能被"补扫描码"动过。</summary>
    internal static ushort UnicodeScanForTest(char ch)
        => KeyInput(ch, up: false, unicode: true).U.ki.wScan;

    /// <summary>输入任意文本（走 Unicode 注入，不经过键盘布局）。</summary>
    private static void TextUnicode(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        /* 分批发送。
         *
         * 一次 SendInput 塞进几百上千个 INPUT，慢程序（Electron / 远程桌面 /
         * 老式编辑框）的消息队列消化不完，后半截会被丢掉 —— 用户看到的就是
         * "长文本只打出来一半"。
         * 每批 32 字符（64 个 INPUT），批次之间让出几毫秒。
         */
        const int Batch = 32;
        for (int start = 0; start < text.Length; start += Batch)
        {
            int len = Math.Min(Batch, text.Length - start);

            // 别把代理对（emoji）劈在两批之间：末尾是高位代理就留到下一批
            if (start + len < text.Length && len > 1 && char.IsHighSurrogate(text[start + len - 1]))
                len--;

            var arr = new List<INPUT>(len * 2);
            for (int i = start; i < start + len; i++)
            {
                char ch = text[i];
                arr.Add(KeyInput(ch, false, unicode: true));
                arr.Add(KeyInput(ch, true, unicode: true));
            }
            Send(arr.ToArray());

            if (start + len < text.Length) Thread.Sleep(6);
        }
    }

    /// <summary>
    /// 仅供自检：返回"用 Unicode 方式输入这个字符"时实际带上的 KEYEVENTF_* 标志。
    ///
    /// 存在的意义是守住一个很隐蔽的回归：Unicode 模式下 <c>vk</c> 参数是字符码，
    /// 一旦被当成虚拟键去判"扩展键"，'.'(0x2E)、'o'(0x6F)、','(0x2C) 这些
    /// 就会被打上 EXTENDEDKEY，目标程序收到的就不是原字符 ——
    /// 表现是输入邮箱少了字符。
    /// </summary>
    internal static uint FlagsForUnicodeForTest(char ch)
        => KeyInput(ch, false, unicode: true).U.ki.dwFlags;

    // ---------------- 鼠标 ----------------

    public static void Wheel(int notches, bool horizontal = false)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    mouseData = unchecked((uint)(notches * 120)),
                    dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL,
                    dwExtraInfo = InjectionTag,
                },
            },
        };
        Send(input);
    }

    public static void Click(bool right = false)
    {
        var down = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dwFlags = right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN, dwExtraInfo = InjectionTag },
            },
        };
        var up = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dwFlags = right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP, dwExtraInfo = InjectionTag },
            },
        };
        Send(down, up);
    }

    /// <summary>
    /// 只发鼠标键的"抬起"。
    ///
    /// 善后必须用这个（而不是裸 mouse_event）：抬起带上 <see cref="InjectionTag"/> 之后，
    /// 我们自己的钩子会按"注入"直接放行，不会被改键 / 层逻辑误当成用户按下的键吞掉。
    /// </summary>
    public static void MouseUp(int vk)
    {
        uint flag = vk switch
        {
            0x01 => MOUSEEVENTF_LEFTUP,
            0x02 => MOUSEEVENTF_RIGHTUP,
            0x04 => MOUSEEVENTF_MIDDLEUP,
            _ => 0,
        };
        if (flag == 0) return;

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dwFlags = flag, dwExtraInfo = InjectionTag },
            },
        };
        Send(input);
    }

    /// <summary>相对移动鼠标（像素）。</summary>
    public static void MoveRelative(int dx, int dy)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = 0x0001 /*MOVE*/, dwExtraInfo = InjectionTag },
            },
        };
        Send(input);
    }

    /// <summary>当前物理按键是否按下（用于判断用户是否仍按住修饰键）。</summary>
    public static bool IsPhysicallyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
