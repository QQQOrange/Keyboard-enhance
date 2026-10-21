namespace CapsLockPlus.Core.Input;

/// <summary>
/// 修饰键按住状态跟踪器。
///
/// 为什么需要它：在 Windows 低级键盘钩子（WH_KEYBOARD_LL）的回调里
/// **不能用 <c>GetAsyncKeyState</c> 判断修饰键是否按着** ——
/// 微软文档写明，回调被调用时异步按键状态尚未更新，查到的永远是上一个键的状态。
///
/// 症状：按 Ctrl+K 只能录到 K，按 Shift+K 也只能录到 K。
///
/// 唯一可靠的做法是自己从钩子的 down / up 事件里维护按住集合。
/// 这个类就是这个集合，单独抽出来是为了能脱离钩子做单元测试。
///
/// 内部记的是**物理键码集合**而不是"逻辑键计数"：
///   * 按住键时系统会自动重复发 down，HashSet.Add 天然幂等，
///     不会像计数那样被加爆（早先的计数实现就踩了这个坑，
///     按住 Ctrl 一秒后松开，计数还残留，录到幻影组合键）；
///   * 左右 Ctrl / 通用 Ctrl 码（0x11）各占一个槽位，互不干扰，
///     松开右手不影响左手。
/// </summary>
public sealed class ModifierTracker
{
    /// <summary>当前按住的物理修饰键码集合。</summary>
    private readonly HashSet<int> _physicalDown = new();

    /// <summary>把一个物理修饰键归一到逻辑修饰键。</summary>
    public static int Normalize(int vk) => vk switch
    {
        0xA2 or 0xA3 or 0x11 => 0x11,     // L/R Ctrl  -> Ctrl
        0xA0 or 0xA1 or 0x10 => 0x10,     // L/R Shift -> Shift
        0xA4 or 0xA5 or 0x12 => 0x12,     // L/R Alt   -> Alt
        0x5B or 0x5C => 0x5B,             // L/R Win   -> Win
        _ => vk,
    };

    /// <summary>这个虚拟键码是不是修饰键。</summary>
    public static bool IsModifier(int vk) => vk is
        0xA2 or 0xA3 or 0x11 or      // Ctrl
        0xA0 or 0xA1 or 0x10 or      // Shift
        0xA4 or 0xA5 or 0x12 or      // Alt
        0x5B or 0x5C;                // Win

    /// <summary>
    /// 记录一次修饰键事件。
    ///
    /// down 用 <see cref="HashSet{T}.Add"/>（幂等）：系统自动重复发的
    /// 一串 down 不会让集合变大，一次 up 就能真正清掉。
    /// </summary>
    public void Track(int vk, bool down)
    {
        if (!IsModifier(vk)) return;
        if (down) _physicalDown.Add(vk);
        else _physicalDown.Remove(vk);
    }

    /// <summary>清空。</summary>
    public void Clear() => _physicalDown.Clear();

    /// <summary>某个逻辑修饰键（0x11/0x10/0x12/0x5B）是否有物理键按着。</summary>
    public bool IsDown(int normalizedVk) => normalizedVk switch
    {
        0x11 => ContainsAny(0x11, 0xA2, 0xA3),
        0x10 => ContainsAny(0x10, 0xA0, 0xA1),
        0x12 => ContainsAny(0x12, 0xA4, 0xA5),
        0x5B => ContainsAny(0x5B, 0x5C),
        _ => _physicalDown.Contains(normalizedVk),
    };

    private bool ContainsAny(params int[] vks)
    {
        foreach (int v in vks)
            if (_physicalDown.Contains(v)) return true;
        return false;
    }

    /// <summary>当前按住的修饰键集合。</summary>
    public KeyModifiers Current
    {
        get
        {
            var m = KeyModifiers.None;
            if (IsDown(0x11)) m |= KeyModifiers.Ctrl;
            if (IsDown(0x10)) m |= KeyModifiers.Shift;
            if (IsDown(0x12)) m |= KeyModifiers.Alt;
            if (IsDown(0x5B)) m |= KeyModifiers.Win;
            return m;
        }
    }

    /// <summary>
    /// 用 <c>GetAsyncKeyState</c> 播种一次初始状态。
    ///
    /// 只在两种时机用：对话框刚打开时（用户可能已经按着 Ctrl）、
    /// 或者钩子重装后。**不能在钩子回调里用它判断** —— 那时数据是旧的。
    /// </summary>
    public void Seed(Func<int, bool> isPhysicallyDown)
    {
        _physicalDown.Clear();
        foreach (int vk in new[] { 0x11, 0xA2, 0xA3, 0x10, 0xA0, 0xA1, 0x12, 0xA4, 0xA5, 0x5B, 0x5C })
            if (isPhysicallyDown(vk)) _physicalDown.Add(vk);
    }

    /// <summary>把修饰键集合拼成可读的组合串（"ctrl+shift+k"）。</summary>
    public static string BuildCombo(KeyModifiers mods, string? key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("ctrl");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("shift");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("alt");
        if (mods.HasFlag(KeyModifiers.Win)) parts.Add("win");
        parts.Add(key ?? "?");
        return string.Join("+", parts);
    }
}
