using CapsLockPlus.Core.Localization;

namespace CapsLockPlus.Core.Input;

/// <summary>一只输入设备（键盘或鼠标），用于界面上的"按设备配置"。</summary>
public sealed record InputDeviceInfo(string Key, string Name, string Kind, bool IsMouse)
{
    /// <summary>这台设备是否也报键盘集合（触控板、带多媒体键的鼠标两头都报）。</summary>
    public bool HasKeyboard { get; init; }

    /// <summary>界面显示名：「名字（类别）」。类别为空时只显示名字。</summary>
    public string Label => Kind.Length == 0 ? Name : Loc.F("{0}（{1}）", Name, Kind);
}

/// <summary>
/// 键盘与鼠标的统一入口。
///
/// ## 为什么需要这一层
///
/// 设备标识有两套来源（<see cref="KeyboardDevices"/> / <see cref="MouseDevices"/>），
/// 但所有上层逻辑（改键、设备方案、生效范围勾选）都只关心三件事：
///   ① 这个标识是不是鼠标；
///   ② 它叫什么（给用户看）；
///   ③ 现在插着哪些设备。
///
/// 把"两套来源 + 前缀规则"收在这一个文件里，运行时热路径就只需要比较前缀，
/// 不用到处 `if (isMouse)` 分支。
/// </summary>
public static class InputDevices
{
    /// <summary>鼠标标识前缀（与 <see cref="MouseDevices.Prefix"/> 同一个常量）。</summary>
    public const string MousePrefix = MouseDevices.Prefix;

    /// <summary>键盘/鼠标按键的虚拟码范围判断。</summary>
    public static bool IsMouseButton(int vk)
        => vk is >= 0x01 and <= 0x06 or VK.WheelLeft or VK.WheelRight;

    public static bool IsMouse(string? key)
        => (key ?? "").TrimStart().StartsWith(MousePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>把裸路径/裸标识规范化成最终存储用的设备标识。</summary>
    public static string Normalize(string? key)
    {
        string k = (key ?? "").Trim();
        if (k.Length == 0) return "";
        return IsMouse(k) ? MouseDevices.KeyOf(MouseDevices.Strip(k)) : KeyboardDevices.KeyOf(k);
    }

    /// <summary>标识 → 用户看得懂的名字。查不到原样返回（展示逻辑绝不能因此报错）。</summary>
    public static string DisplayNameOf(string? key)
    {
        string k = (key ?? "").Trim();
        if (k.Length == 0) return "";
        if (AliasOf(k) is { Length: > 0 } alias) return alias;
        return IsMouse(k) ? MouseDevices.DisplayNameOf(k) : KeyboardDevices.DisplayNameOf(k);
    }

    // ---------------------------------------------------------------- 设备备注

    /// <summary>
    /// 设备备注：设备标识 → 用户自己起的名字。
    ///
    /// 存在 <c>GlobalOptions.DeviceAliases</c> 里（跟着配置走、能同步），
    /// 但**所有显示设备名的地方都走 <see cref="DisplayNameOf"/>** ——
    /// 所以只要这里同步一份内存镜像，胶囊、清单、改键说明、托盘就一起改了，
    /// 不用挨个界面传 settings。
    ///
    /// 只在设置加载/保存、以及用户改备注时写，运行时不碰，读的时候是一条字典查询。
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object AliasGate = new();

    /// <summary>用设置里的备注表整体替换内存镜像（设置加载、导入、保存后调用）。</summary>
    public static void SetAliases(IEnumerable<KeyValuePair<string, string>>? aliases)
    {
        lock (AliasGate)
        {
            Aliases.Clear();
            if (aliases == null) return;
            foreach (var kv in aliases)
            {
                string key = Normalize(kv.Key);
                string name = (kv.Value ?? "").Trim();
                if (key.Length == 0 || name.Length == 0) continue;
                Aliases[key] = name;
            }
        }
    }

    /// <summary>这台设备有没有备注。空 = 用系统里的名字。</summary>
    public static string AliasOf(string? key)
    {
        string k = Normalize(key);
        if (k.Length == 0) return "";
        lock (AliasGate) return Aliases.TryGetValue(k, out var name) ? name : "";
    }

    /// <summary>标识 → 类别（内置/外接/虚拟）。</summary>
    public static string KindOf(string? key)
    {
        string k = (key ?? "").Trim();
        if (k.Length == 0) return "";
        if (IsMouse(k)) return MouseDevices.KindOf(MouseDevices.Strip(k));
        return KeyboardDevices.KindOf(k);
    }

    /// <summary>
    /// 现在插着的全部设备：**一只物理设备一行**。
    ///
    /// 键盘集合只报键盘、鼠标集合只报鼠标，而触控板/带多媒体键的鼠标两头都报；
    /// 归并后同一台设备只出现一次，界面才符合"我桌上只有三只设备"的直觉。
    /// 键盘先入表：名字与类别以键盘那条为准（同一台设备两边拿到的是同一个产品名）。
    /// </summary>
    public static IReadOnlyList<InputDeviceInfo> All()
    {
        var byKey = new Dictionary<string, InputDeviceInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var g in KeyboardDevices.Group(KeyboardDevices.List()))
            {
                string key = KeyboardDevices.KeyOf(g.Sample);
                if (key.Length == 0) continue;
                byKey[key] = new InputDeviceInfo(key, g.Name, g.Kind, false) { HasKeyboard = true };
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "枚举键盘"); }

        try
        {
            foreach (var g in MouseDevices.Group(MouseDevices.List()))
            {
                string key = MouseDevices.KeyOf(g.Sample);
                if (key.Length == 0) continue;

                /* 已有同名键盘条目 = 同一台设备，只加个"也报鼠标"的标记。
                 * 名字例外：触控板在键盘集合那边常叫「HID Keyboard Device」，
                 * 在鼠标集合这边才叫「HID-compliant touch pad」—— 后者才说明这是什么。 */
                if (byKey.TryGetValue(key, out var existing))
                {
                    bool useMouseName = g.TouchPad && !LooksLikeTouchPad(existing.Name);
                    byKey[key] = existing with
                    {
                        IsMouse = true,
                        Name = useMouseName ? g.Name : existing.Name,
                        Kind = useMouseName ? (g.TouchPad ? Loc.T("触控板") : g.Kind) : existing.Kind,
                    };
                }
                else
                    byKey[key] = new InputDeviceInfo(key, g.Name, g.Kind, true);
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "枚举鼠标"); }

        /* 真设备在前、虚拟设备在后；真设备里按「键盘 → 键盘+鼠标 → 鼠标」排，
         * 分节标题才会顺次出现，不会出现"键盘+鼠标 / 鼠标 / 键盘"这种跳跃。 */
        return byKey.Values
            .OrderBy(d => IsVirtual(d) ? 3 : 0)
            .ThenBy(d => d.HasKeyboard ? (d.IsMouse ? 1 : 0) : 2)
            .ToList();
    }

    /// <summary>虚拟/软件设备（ROOT 枚举器、Virtual HID、名字里带"虚拟"）。</summary>
    public static bool IsVirtual(InputDeviceInfo d)
    {
        string key = d.Key ?? "";
        if (key.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) return true;
        if (key.Contains("VIRTUAL_HID", StringComparison.OrdinalIgnoreCase)) return true;
        return (d.Name ?? "").Contains("虚拟", StringComparison.Ordinal)
            || (d.Name ?? "").Contains("virtual", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>名字像是在说"我是触控板"（键盘集合那边常报成 HID Keyboard Device）。</summary>
    private static bool LooksLikeTouchPad(string? name)
    {
        string n = (name ?? "").ToLowerInvariant();
        return n.Contains("touch pad") || n.Contains("touchpad")
            || n.Contains("trackpad") || n.Contains("触摸板") || n.Contains("触控板");
    }

    /// <summary>只列鼠标（界面上"鼠标按键"一节用）。</summary>
    public static IReadOnlyList<InputDeviceInfo> Mice()
        => All().Where(d => d.IsMouse).ToList();

    /// <summary>设备标识 → 界面短名（用于状态行/通知，名字太长时退回类别）。</summary>
    public static string ShortNameOf(string? key)
    {
        string name = DisplayNameOf(key);
        if (name.Length == 0) return Loc.T("所有设备");
        return name.Length > 24 ? name[..24] + "…" : name;
    }
}
