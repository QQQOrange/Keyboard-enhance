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
        else if (scan != 0) flags |= KEYEVENTF_SCANCODE;
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

    /// <summary>输入任意文本（走 Unicode 注入，不经过键盘布局）。</summary>
    public static void Text(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var arr = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            arr.Add(KeyInput(ch, false, unicode: true));
            arr.Add(KeyInput(ch, true, unicode: true));
        }
        Send(arr.ToArray());
    }

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
