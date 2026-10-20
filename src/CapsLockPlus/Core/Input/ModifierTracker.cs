namespace CapsLockPlus.Core.Input;

/// <summary>
/// 修饰键按住状态跟踪器。
///
/// 为什么需要它：在 Windows 低级键盘钩子（WH_KEYBOARD_LL）的回调里
/// **不能用 <c>GetAsyncKeyState</c> 判断修饰键是否按着** ——
/// 微软文档写明，回调被调用时异步按键状态尚未更新，查到的永远是上一个键的状态。
///
/// 症状：按 Ctrl+K 只能录到 K，按 Shift+K 也只能录到 K；
/// 用户看到的现象就是"能识别按键，但录不到组合键"。
///
/// 唯一可靠的做法是自己从钩子的 down / up 事件里维护按住集合。
/// 这个类就是这个集合，单独抽出来是为了能脱离钩子做单元测试。
/// </summary>
public sealed class ModifierTracker
{
    /// <summary>
    /// 每个逻辑修饰键当前按住了几个物理键。
    ///
    /// ⚠ 必须记**计数**而不是布尔值：左右两个 Ctrl 归一到同一个逻辑键，
    /// 如果只记 bool，那么"先按右 Ctrl 再按左 Ctrl、然后松开右 Ctrl"
    /// 会把整个 Ctrl 标记清掉 —— 用户明明还按着左 Ctrl，组合键却失效了。
    /// 这是很常见的操作（很多人习惯用右手小指按右 Ctrl）。
    /// </summary>
    private readonly Dictionary<int, int> _down = new();

    /// <summary>把一个物理修饰键归一到逻辑修饰键（左右 Ctrl 都归成 Ctrl）。</summary>
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

    /// <summary>记录一次修饰键事件。</summary>
    public void Track(int vk, bool down)
    {
        if (!IsModifier(vk)) return;
        int norm = Normalize(vk);

        if (down)
        {
            // 同一个物理键的重复 down（自动重复）不能把计数加爆
            _down[norm] = _down.TryGetValue(norm, out int n) ? n + 1 : 1;
        }
        else if (_down.TryGetValue(norm, out int cur))
        {
            if (cur <= 1) _down.Remove(norm);
            else _down[norm] = cur - 1;
        }
    }

    /// <summary>清空。</summary>
    public void Clear() => _down.Clear();

    /// <summary>某个逻辑修饰键是否按着。</summary>
    public bool IsDown(int normalizedVk) => _down.ContainsKey(normalizedVk);

    /// <summary>当前按住的修饰键集合。</summary>
    public KeyModifiers Current
    {
        get
        {
            var m = KeyModifiers.None;
            if (_down.ContainsKey(0x11)) m |= KeyModifiers.Ctrl;
            if (_down.ContainsKey(0x10)) m |= KeyModifiers.Shift;
            if (_down.ContainsKey(0x12)) m |= KeyModifiers.Alt;
            if (_down.ContainsKey(0x5B)) m |= KeyModifiers.Win;
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
        _down.Clear();
        if (isPhysicallyDown(0x11) || isPhysicallyDown(0xA2) || isPhysicallyDown(0xA3))
            _down[0x11] = 1;
        if (isPhysicallyDown(0x10) || isPhysicallyDown(0xA0) || isPhysicallyDown(0xA1))
            _down[0x10] = 1;
        if (isPhysicallyDown(0x12) || isPhysicallyDown(0xA4) || isPhysicallyDown(0xA5))
            _down[0x12] = 1;
        if (isPhysicallyDown(0x5B) || isPhysicallyDown(0x5C))
            _down[0x5B] = 1;
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