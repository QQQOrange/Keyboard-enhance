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

    private static void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        SendInput((uint)inputs.Length, inputs, InputSize);
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
    public static void KeyDown(int vk) => Send(KeyInput(vk, false));
    public static void KeyUp(int vk) => Send(KeyInput(vk, true));

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
        Tap(vk);
        KeyUp(modifier);
    }

    public static void Ctrl(int vk) => Chord(VK.Control, vk);
    public static void Shift(int vk) => Chord(VK.Shift, vk);
    public static void CtrlShift(int vk)
    {
        KeyDown(VK.Control);
        KeyDown(VK.Shift);
        Tap(vk);
        KeyUp(VK.Shift);
        KeyUp(VK.Control);
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
            SendInput(1, new[] { input }, InputSize);
            if (gapMs > 0) Thread.Sleep(gapMs);
        }

        Send(VK.Control, ctrlScan, up: false);
        Send(vk, keyScan, up: false);
        Send(vk, keyScan, up: true);
        Send(VK.Control, ctrlScan, up: true);
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

    /// <summary>输入任意文本（走 Unicode 注入，不经过键盘布局）。</summary>
    public static void Text(string text)
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
