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
    public const int MediaNextTrack = 0xB0;
    public const int MediaPrevTrack = 0xB1;
    public const int MediaPlayPause = 0xB3;
    public const int VolumeMute = 0xAD;
    public const int VolumeDown = 0xAE;
    public const int VolumeUp = 0xAF;
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
        [VK.Back] = "backspace",
        [VK.Tab] = "tab",
        [VK.Return] = "enter",
        [VK.Escape] = "esc",
        // CapsLock 必须在这里登记，否则触发键配置里写 "capslock" 时
        // VkOf 返回 null，触发键永远匹配不上（表现为整个层功能失效）。
        [VK.CapsLock] = "capslock",
        [VK.Space] = "space",
        [VK.RMenu] = "ralt",
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
        [VK.VolumeUp] = "volumeUp",
        [VK.VolumeDown] = "volumeDown",
        [VK.VolumeMute] = "volumeMute",
    };

    private static readonly Dictionary<string, int> ToVk =
        ToName.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>该键是否可以作为 Caps 组合键的一部分（可绑定的按键）。</summary>
    public static bool IsBindable(int vk) => ToName.ContainsKey(vk);

    public static string? NameOf(int vk) => ToName.TryGetValue(vk, out var n) ? n : null;

    public static int? VkOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return ToVk.TryGetValue(name.Trim(), out var vk) ? vk : null;
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
        "ralt" => "RAlt",
        "pageUp" => "PgUp",
        "pageDown" => "PgDn",
        "mediaPrev" => "上一首",
        "mediaNext" => "下一首",
        "mediaPlayPause" => "播放/暂停",
        "volumeUp" => "音量+",
        "volumeDown" => "音量-",
        "volumeMute" => "静音",
        _ => name.Length <= 2 ? name.ToUpperInvariant() : char.ToUpperInvariant(name[0]) + name[1..],
    };

    /// <summary>把设置里的组合键名（caps_a / caps_lalt_a / caps_win_1）解析成 (修饰键, 按键名)。</summary>
    public static (KeyModifiers Mods, string Key)? ParseCombo(string comboId)
    {
        if (string.IsNullOrEmpty(comboId)) return null;
        if (comboId.StartsWith("caps_lalt_", StringComparison.OrdinalIgnoreCase))
            return (KeyModifiers.Alt, comboId["caps_lalt_".Length..]);
        if (comboId.StartsWith("caps_win_", StringComparison.OrdinalIgnoreCase))
            return (KeyModifiers.Win, comboId["caps_win_".Length..]);
        if (comboId.StartsWith("caps_", StringComparison.OrdinalIgnoreCase))
            return (KeyModifiers.None, comboId["caps_".Length..]);
        if (comboId.Equals("press_caps", StringComparison.OrdinalIgnoreCase))
            return (KeyModifiers.None, "capslock");
        return null;
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
