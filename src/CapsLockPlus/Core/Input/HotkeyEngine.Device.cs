using System;
using System.Collections.Generic;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 设备判定（partial 拆分之六）：这次按键/点击来自哪只设备、这台设备生不生效。
///
/// ## 为什么要单独一层
///
/// 设备信息只能靠 Raw Input 补齐，而"问一次设备"最多要等十几毫秒
/// （钩子回调往往比 WM_INPUT 先到，得等一等）。摊到每个按键上，打字会发涩。
///
/// 所以这里守一条红线：**只有"有规则"的键才去问设备**。
///   · 改键的源键（Caps、侧键…）；
///   · 设备方案里绑过的键；
///   · 有设备被取消勾选时，所有"有规则的键"（否则关不掉那台设备）。
///
/// 其余 99% 的按键走的是"不查设备、直接放行"，开销是一次哈希表命中。
/// </summary>
public sealed partial class HotkeyEngine
{
    /// <summary>最近一次解析出的设备（按下时记，抬起时复用，免得多等一次）。</summary>
    private readonly Dictionary<int, (string? Device, long At)> _deviceOfKey = new();

    /// <summary>设备记录的有效期：超过这个时间的抬起不再信任旧值。</summary>
    private const int DeviceCacheMs = 4000;

    /// <summary>"有规则"的键名集合（缓存，配置变了才重建）。</summary>
    private HashSet<string>? _ruleKeyNames;
    private string _ruleKeyStamp = "";

    /// <summary>
    /// 这次事件要不要去问设备。
    ///
    /// 注意 <see cref="DeviceProfiles.HasInactive"/> 为真时判断标准会放宽到
    /// "所有有规则的键" —— 那时用户明确说了"这台设备不受影响"，
    /// 我们不能因为"这个键只配了全局规则"就把它的按键漏改。
    /// </summary>
    private bool NeedsDevice(AppSettings s, int vk)
    {
        if (DeviceResolver == null) return false;

        if (DeviceProfiles.HasInactive(s)) return HasAnyRule(s, vk);

        if (EnsureRemapTable(s).DeviceScopedKeys.Contains(vk)) return true;

        string? name = KeyName.NameOf(vk);
        if (name == null) return false;

        return DeviceProfiles.ScopedKeyNames(s).Contains(name);
    }

    /// <summary>这个键在**任何**一层规则里出现过吗（全局改键 / 方案绑定 / 触发键）。</summary>
    private bool HasAnyRule(AppSettings s, int vk)
    {
        string? name = KeyName.NameOf(vk);
        if (name == null) return false;

        string stamp = KeyRemapMap.Stamp(s) + "#" + (s.Global?.BindingsVersion ?? 0) + "#" + s.Triggers.Count;
        if (_ruleKeyNames == null || !string.Equals(stamp, _ruleKeyStamp, StringComparison.Ordinal))
        {
            _ruleKeyNames = DeviceProfiles.RuleKeyNames(s);
            _ruleKeyStamp = stamp;
        }

        return _ruleKeyNames.Contains(name);
    }

    /// <summary>
    /// 解析这次事件来自哪只设备（拿不到返回 null = 按"所有设备"处理）。
    ///
    /// 抬起时优先用按下时记下的值：Raw Input 的抬起记录同样会到，
    /// 但再等一次没有意义，反而给"松手"加十几毫秒延迟。
    /// </summary>
    private string? ResolveDeviceFor(AppSettings s, int vk, bool down)
    {
        if (!NeedsDevice(s, vk)) return null;

        long now = Environment.TickCount64;

        if (!down && _deviceOfKey.TryGetValue(vk, out var cached) && now - cached.At <= DeviceCacheMs)
            return cached.Device;

        string? device;
        try { device = DeviceResolver?.Invoke(vk, down); }
        catch (Exception ex) { CoreLog.Swallow(ex, "解析输入设备"); device = null; }

        _deviceOfKey[vk] = (device, now);
        if (_deviceOfKey.Count > 64) _deviceOfKey.Clear();

        return device;
    }

    /// <summary>
    /// 这台设备现在受本软件影响吗。
    /// 拿不到设备（null）一律按"生效"处理 —— fail open，和改键的历史行为一致。
    /// </summary>
    private static bool IsDeviceActive(AppSettings s, string? device)
        => device == null || DeviceProfiles.IsActive(s, device);

    /// <summary>
    /// 鼠标按键事件（钩子线程调用）。
    ///
    /// 直接走 <see cref="OnKeyEvent"/> —— 用户要的是"鼠标键和键盘键功能一样"，
    /// 而"一样"最可靠的实现方式就是同一段代码：改键、层绑定、触发键、
    /// 宏录制、双击手势全都自动具备，将来修 bug 也只有一处。
    /// </summary>
    private bool OnMouseButton(int vk, bool down, bool injected)
    {
        try { return OnKeyEvent(vk, down, injected); }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "鼠标按键处理");
            return false;       // 出错就放行，别把用户的点击吃掉
        }
    }
}
