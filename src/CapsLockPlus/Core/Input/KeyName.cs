namespace CapsLockPlus.Core.Input;

/// <summary>虚拟键码常量。</summary>
public static class VK
{
    public const int LButton = 0x01;
    public const int RButton = 0x02;
    public const int MButton = 0x04;
    public const int Back = 0x08;
    public const int Tab = 0x09;
    public const int Return = 0x0D;
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Menu = 0x12;
    public const int Pause = 0x13;
    public const int CapsLock = 0x14;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int PageUp = 0x21;
    public const int PageDown = 0x22;
    public const int End = 0x23;
    public const int Home = 0x24;
    public const int Left = 0x25;
    public const int Up = 0x26;
    public const int Right = 0x27;
    public const int Down = 0x28;
    public const int Insert = 0x2D;
    public const int Delete = 0x2E;
    public const int LWin = 0x5B;
    public const int RWin = 0x5C;
    public const int LShift = 0xA0;
    public const int RShift = 0xA1;
    public const int LControl = 0xA2;
    public const int RControl = 0xA3;
    public const int LMenu = 0xA4;
    public const int RMenu = 0xA5;
    public const int Oem1 = 0xBA;    // ;
    public const int OemPlus = 0xBB; // =
    public const int OemComma = 0xBC;
    public const int OemMinus = 0xBD;
    public const int OemPeriod = 0xBE;
    public const int Oem2 = 0xBF;    // /
    public const int Oem3 = 0xC0;    // `
    public const int Oem4 = 0xDB;    // [
    public const int Oem5 = 0xDC;    // \
    public const int Oem6 = 0xDD;    // ]
    public const int Oem7 = 0xDE;    // '

    // 编辑 / 系统键（108 键键盘上都有；Pause 已在上面定义）
    public const int PrintScreen = 0x2C;
    public const int ScrollLock = 0x91;
    public const int NumLock = 0x90;
    public const int Apps = 0x5D;         // 菜单键（右 Ctrl 左边那个）

    public const int MediaNextTrack = 0xB0;
    public const int MediaPrevTrack = 0xB1;
    public const int MediaStop = 0xB2;
    public const int MediaPlayPause = 0xB3;
    public const int VolumeMute = 0xAD;
    public const int VolumeDown = 0xAE;
    public const int VolumeUp = 0xAF;

    // 浏览器功能键：很多键盘上有，Windows 也会原样投递，
    // 用作触发键或宏步骤都很实用。
    public const int BrowserBack = 0xA6;
    public const int BrowserForward = 0xA7;
    public const int BrowserRefresh = 0xA8;
    public const int BrowserStop = 0xA9;
    public const int BrowserSearch = 0xAA;
    public const int BrowserFavorites = 0xAB;
    public const int BrowserHome = 0xAC;

    // 小键盘（注意：这几个是 NumLock 关闭时的方向/编辑语义）
    public const int Numpad0 = 0x60;
    public const int Numpad1 = 0x61;
    public const int Numpad2 = 0x62;
    public const int Numpad3 = 0x63;
    public const int Numpad4 = 0x64;
    public const int Numpad5 = 0x65;
    public const int Numpad6 = 0x66;
    public const int Numpad7 = 0x67;
    public const int Numpad8 = 0x68;
    public const int Numpad9 = 0x69;
    public const int NumpadMultiply = 0x6A;
    public const int NumpadAdd = 0x6B;
    public const int NumpadSubtract = 0x6D;
    public const int NumpadDecimal = 0x6E;
    public const int NumpadDivide = 0x6F;
    public const int NumpadEnter = 0x0D;   // 小键盘回车（与主回车同 VK，键名区分）
}

/// <summary>
/// 虚拟键码 &lt;-&gt; 设置文件里使用的按键名。
/// 键名沿用上游 CapsLock+ 的写法（a、f1、backquote、leftSquareBracket……），
/// 这样从旧版 CapsLock+settings.ini 迁移时不需要额外映射。
/// </summary>
public static class KeyName
{
    private static readonly Dictionary<int, string> ToName = new()
    {
        [0x41] = "a", [0x42] = "b", [0x43] = "c", [0x44] = "d", [0x45] = "e",
        [0x46] = "f", [0x47] = "g", [0x48] = "h", [0x49] = "i", [0x4A] = "j",
        [0x4B] = "k", [0x4C] = "l", [0x4D] = "m", [0x4E] = "n", [0x4F] = "o",
        [0x50] = "p", [0x51] = "q", [0x52] = "r", [0x53] = "s", [0x54] = "t",
        [0x55] = "u", [0x56] = "v", [0x57] = "w", [0x58] = "x", [0x59] = "y",
        [0x5A] = "z",
        [0x30] = "0", [0x31] = "1", [0x32] = "2", [0x33] = "3", [0x34] = "4",
        [0x35] = "5", [0x36] = "6", [0x37] = "7", [0x38] = "8", [0x39] = "9",
        [0x70] = "f1", [0x71] = "f2", [0x72] = "f3", [0x73] = "f4", [0x74] = "f5",
        [0x75] = "f6", [0x76] = "f7", [0x77] = "f8", [0x78] = "f9", [0x79] = "f10",
        [0x7A] = "f11", [0x7B] = "f12",
        // F13~F24：物理键盘上少见，但很多键盘驱动/自动化工具会用到，
        // 也是"想找个没被占用的触发键"时最理想的选择。
        [0x7C] = "f13", [0x7D] = "f14", [0x7E] = "f15", [0x7F] = "f16",
        [0x80] = "f17", [0x81] = "f18", [0x82] = "f19", [0x83] = "f20",
        [0x84] = "f21", [0x85] = "f22", [0x86] = "f23", [0x87] = "f24",
        [VK.Back] = "backspace",
        [VK.Tab] = "tab",
        [VK.Return] = "enter",
        [VK.Escape] = "esc",
        // CapsLock 必须在这里登记，否则触发键配置里写 "capslock" 时
        // VkOf 返回 null，触发键永远匹配不上（表现为整个层功能失效）。
        [VK.CapsLock] = "capslock",
        [VK.Space] = "space",

        // ---- 修饰键必须全部登记 ----
        //
        // 少了任何一个，"自定义按键"就录不到 Ctrl/Shift/Alt/Win：
        // 捕获窗口里 NameOf 返回 null，直接被当成"未识别按键"拒绝，
        // 用户看到的现象就是"只能录下小字母，录不了 control/shift/alt"。
        //
        // 左右分开登记（lctrl/rctrl…），这样既能区分左右，
        // 也让组合串可读；读取时 ReadModifiers 会把左右归并成同一个修饰键。
        [VK.LControl] = "lctrl",
        [VK.RControl] = "rctrl",
        [VK.LShift] = "lshift",
        [VK.RShift] = "rshift",
        [VK.LMenu] = "lalt",
        [VK.RMenu] = "ralt",
        [VK.LWin] = "lwin",
        [VK.RWin] = "rwin",
        // 通用名（有些键盘/驱动只发这三个 VK）
        [VK.Shift] = "shift",
        [VK.Control] = "ctrl",
        [VK.Menu] = "alt",
        [VK.Oem3] = "backquote",
        [VK.OemMinus] = "minus",
        [VK.OemPlus] = "equal",
        [VK.Oem4] = "leftSquareBracket",
        [VK.Oem6] = "rightSquareBracket",
        [VK.Oem5] = "backslash",
        [VK.Oem1] = "semicolon",
        [VK.Oem7] = "quote",
        [VK.OemComma] = "comma",
        [VK.OemPeriod] = "dot",
        [VK.Oem2] = "slash",
        [VK.Home] = "home",
        [VK.End] = "end",
        [VK.PageUp] = "pageUp",
        [VK.PageDown] = "pageDown",
        [VK.Insert] = "insert",
        [VK.Delete] = "delete",
        [VK.Left] = "left",
        [VK.Up] = "up",
        [VK.Right] = "right",
        [VK.Down] = "down",
        [VK.MediaPrevTrack] = "mediaPrev",
        [VK.MediaNextTrack] = "mediaNext",
        [VK.MediaPlayPause] = "mediaPlayPause",
        [VK.MediaStop] = "mediaStop",
        [VK.VolumeUp] = "volumeUp",
        [VK.VolumeDown] = "volumeDown",
        [VK.VolumeMute] = "volumeMute",

        // 浏览器键（键盘上常印成主页 / 后退 / 刷新）
        [VK.BrowserBack] = "browserBack",
        [VK.BrowserForward] = "browserForward",
        [VK.BrowserRefresh] = "browserRefresh",
        [VK.BrowserStop] = "browserStop",
        [VK.BrowserSearch] = "browserSearch",
        [VK.BrowserFavorites] = "browserFavorites",
        [VK.BrowserHome] = "browserHome",

        // 小键盘
        [VK.Numpad0] = "num0", [VK.Numpad1] = "num1", [VK.Numpad2] = "num2",
        [VK.Numpad3] = "num3", [VK.Numpad4] = "num4", [VK.Numpad5] = "num5",
        [VK.Numpad6] = "num6", [VK.Numpad7] = "num7", [VK.Numpad8] = "num8",
        [VK.Numpad9] = "num9",
        [VK.NumpadMultiply] = "numMultiply",
        [VK.NumpadAdd] = "numAdd",
        [VK.NumpadSubtract] = "numSubtract",
        [VK.NumpadDecimal] = "numDecimal",
        [VK.NumpadDivide] = "numDivide",

        // 编辑 / 系统键（108 键键盘）
        [VK.PrintScreen] = "printScreen",
        [VK.ScrollLock] = "scrollLock",
        [VK.Pause] = "pause",
        [VK.NumLock] = "numLock",
        [VK.Apps] = "apps",
    };

    private static readonly Dictionary<string, int> ToVk =
        ToName.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>该键是否可以作为 Caps 组合键的一部分（可绑定的按键）。</summary>
    public static bool IsBindable(int vk) => ToName.ContainsKey(vk);

    public static string? NameOf(int vk) => ToName.TryGetValue(vk, out var n) ? n : null;

    public static int? VkOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        if (ToVk.TryGetValue(name, out var vk)) return vk;

        /* 兜底：识别 `vk0x??` 形式。
         *
         * 宏录制时未知键名会写成 "vk0x58" 这种形式；播放端若解析不了，
         * 该步骤会被**静默跳过** —— 宏悄悄缺步，用户只觉得"回放不对劲"
         * 却查不出原因。这里让回解析成立，录制什么就播什么。 */
        if (name.Length == 6 && name.StartsWith("vk0x", StringComparison.OrdinalIgnoreCase)
            && byte.TryParse(name.AsSpan(4), System.Globalization.NumberStyles.HexNumber,
                null, out byte raw))
        {
            return raw;
        }
        return null;
    }

    /// <summary>用于界面显示的好看名字。</summary>
    public static string Display(string name) => name switch
    {
        "backquote" => "`",
        "minus" => "-",
        "equal" => "=",
        "leftSquareBracket" => "[",
        "rightSquareBracket" => "]",
        "backslash" => "\\",
        "semicolon" => ";",
        "quote" => "'",
        "comma" => ",",
        "dot" => ".",
        "slash" => "/",
        "backspace" => "Backspace",
        "tab" => "Tab",
        "enter" => "Enter",
        "esc" => "Esc",
        "space" => "Space",
        "capslock" => "CapsLock",
        "lctrl" => "左 Ctrl",
        "rctrl" => "右 Ctrl",
        "lshift" => "左 Shift",
        "rshift" => "右 Shift",
        "lalt" => "左 Alt",
        "ralt" => "RAlt",
        "lwin" => "左 Win",
        "rwin" => "右 Win",
        "ctrl" => "Ctrl",
        "shift" => "Shift",
        "alt" => "Alt",
        "pageUp" => "PgUp",
        "pageDown" => "PgDn",
        "mediaPrev" => "上一首",
        "mediaNext" => "下一首",
        "mediaPlayPause" => "播放/暂停",
        "mediaStop" => "停止播放",
        "browserBack" => "浏览器后退",
        "browserForward" => "浏览器前进",
        "browserRefresh" => "浏览器刷新",
        "browserStop" => "浏览器停止",
        "browserSearch" => "浏览器搜索",
        "browserFavorites" => "浏览器收藏",
        "browserHome" => "浏览器主页",
        "num0" => "小键盘 0",
        "num1" => "小键盘 1",
        "num2" => "小键盘 2",
        "num3" => "小键盘 3",
        "num4" => "小键盘 4",
        "num5" => "小键盘 5",
        "num6" => "小键盘 6",
        "num7" => "小键盘 7",
        "num8" => "小键盘 8",
        "num9" => "小键盘 9",
        "numMultiply" => "小键盘 *",
        "numAdd" => "小键盘 +",
        "numSubtract" => "小键盘 -",
        "numDecimal" => "小键盘 .",
        "numDivide" => "小键盘 /",
        "volumeUp" => "音量+",
        "volumeDown" => "音量-",
        "volumeMute" => "静音",
        _ => name.Length <= 2 ? name.ToUpperInvariant() : char.ToUpperInvariant(name[0]) + name[1..],
    };

    /// <summary>
    /// 把设置里的组合键名解析成 (修饰键, 按键名)。
    ///
    /// 这是 <see cref="BuildComboEx"/> 的逆运算，两种形式都要认：
    ///   caps_q              -> (None, "q")
    ///   caps_lalt_q         -> (Alt, "q")         ← 老布局
    ///   caps_win_1          -> (Win, "1")
    ///   caps_ctrl_shift_k   -> (Ctrl|Shift, "k")  ← 自定义按键捕获的
    ///
    /// 做法是剥掉 `caps_` 前缀后按 `_` 切，把开头的**修饰键记号**逐个吃掉，
    /// 剩下的拼回去就是键名（键名里不含下划线，所以拼回是无损的）。
    ///
    /// ⚠ 不能再用"前缀 startsWith"的写法：那样 `caps_ctrl_k` 会被当成
    /// 基础层的键名 `ctrl_k`，导致组合键既不显示也绑不上 —— 正是用户
    /// 反馈的"自定义按键不能用组合键"。
    /// </summary>
    public static (KeyModifiers Mods, string Key)? ParseCombo(string comboId)
    {
        if (string.IsNullOrEmpty(comboId)) return null;
        if (comboId.Equals("press_caps", StringComparison.OrdinalIgnoreCase))
            return (KeyModifiers.None, "capslock");
        if (!comboId.StartsWith("caps_", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = comboId["caps_".Length..].Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var mods = KeyModifiers.None;
        int i = 0;
        for (; i < parts.Length - 1; i++)          // 最后一段一定是键名，不能当修饰键吃
        {
            var m = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "lctrl" or "rctrl" => KeyModifiers.Ctrl,
                "shift" or "lshift" or "rshift" => KeyModifiers.Shift,
                "lalt" or "ralt" or "alt" => KeyModifiers.Alt,
                "win" or "lwin" or "rwin" => KeyModifiers.Win,
                _ => KeyModifiers.None,
            };
            if (m == KeyModifiers.None) break;     // 不是修饰键，后面整段都算键名
            mods |= m;
        }

        string key = string.Join('_', parts[i..]);
        return key.Length == 0 ? null : (mods, key);
    }

    /// <summary>
    /// 构造组合键 ID。
    ///
    /// 除引擎原有的 LAlt / Win 之外，也支持 Ctrl / Shift
    /// （自定义按键捕获到的组合键用），按 ctrl → shift → lalt → win 的顺序拼：
    ///   Caps + Ctrl+K       ->  caps_ctrl_k
    ///   Caps + Ctrl+Shift+K ->  caps_ctrl_shift_k
    /// </summary>
    public static string BuildComboEx(KeyModifiers mods, string key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("ctrl");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("shift");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("lalt");
        if (mods.HasFlag(KeyModifiers.Win)) parts.Add("win");
        parts.Add(key);
        return "caps_" + string.Join("_", parts);
    }

    public static string BuildCombo(KeyModifiers mods, string key) => mods switch
    {
        KeyModifiers.Alt => "caps_lalt_" + key,
        KeyModifiers.Win => "caps_win_" + key,
        _ => "caps_" + key,
    };
}

/// <summary>
/// 组合键的修饰键。
///
/// 位值沿用 Windows 的习惯：Ctrl / Shift / Alt / Win = 1 / 2 / 4 / 8，
/// 这样可以按位组合，也便于将来直接和 Win32 MOD_* 对齐。
/// </summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Win = 8,
}
