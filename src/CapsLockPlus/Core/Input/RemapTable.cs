namespace CapsLockPlus.Core.Input;

/// <summary>
/// 按设备分组的改键表。
///
/// ## 为什么不是一张字典
///
/// 加设备维度最省事的写法是"把设备拼进 key"，但那会让每次按键都要拼字符串。
/// 这里改成"全局一张表 + 每只键盘一张表"，查表就是两次字典命中，没有分配。
///
/// ## 优先级
///
/// 这只键盘的规则 → 全局规则 → 不改。
/// 于是"键盘 A 上 Caps→Esc、其它键盘保持原样"这种配置可以写出来：
/// 只在 A 的那张表里放一条，全局表里不放。
/// </summary>
public sealed class RemapTable
{
    /// <summary>对所有键盘生效的规则。</summary>
    public Dictionary<int, int> All { get; } = new();

    /// <summary>设备标识（大写）→ 该设备专属规则。</summary>
    public Dictionary<string, Dictionary<int, int>> ByDevice { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 存在"设备专属规则"的源键集合。
    ///
    /// 用它避免"每个键都去问一次设备"：只有这些键才需要设备信息，
    /// 而问设备是可能要等 Raw Input 的（最多十几毫秒），不能摊到所有按键上。
    /// </summary>
    public HashSet<int> DeviceScopedKeys { get; } = new();

    public bool IsEmpty => All.Count == 0 && ByDevice.Count == 0;

    /// <summary>
    /// 查这个键在这只键盘上该变成什么。
    /// <paramref name="device"/> 为空表示拿不到设备 —— 此时只用全局规则。
    /// </summary>
    public bool TryGet(int vk, string? device, out int target)
    {
        string dev = KeyRemapMap.NormalizeDevice(device);
        if (dev.Length > 0 &&
            ByDevice.TryGetValue(dev, out var map) &&
            map.TryGetValue(vk, out target))
            return true;

        return All.TryGetValue(vk, out target);
    }
}
