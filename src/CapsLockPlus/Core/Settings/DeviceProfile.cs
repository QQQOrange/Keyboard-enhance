using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Localization;

namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 一个「设备方案」：按键盘 / 鼠标分开的一套设置。
///
/// ## 三种方案分别是干什么的
///
///   ① **通用设置**（<see cref="Shared"/> = true）—— 只有一份。里面的规则
///      对**所有**设备生效，包括自己有方案的设备（那些设备没覆盖的键就落在这里）。
///      用户的原话是"我在这个键盘下做的一切设置，在其他键盘下都能够使用"，
///      实现方式就是：把这只键盘的方案设为通用，其它键盘自然就继承到了。
///   ② **设备方案**（绑定若干设备标识）—— 只对 <see cref="Devices"/> 里的设备生效。
///      同名键优先于通用设置。
///   ③ **独立方案**（<see cref="Inherit"/> = false）—— 连通用设置都不继承，
///      这台设备只认自己方案里的键。用来做"完全不同的两套键位"。
///
/// ## 为什么 Keys / Remaps 分两份
///
/// <see cref="Keys"/> 是按键映射页上的绑定（基础层），<see cref="Remaps"/> 是
/// "把 A 键直接换成 B 键"的改键表（含鼠标按键）。两者在运行时走的是两条链路，
/// 但都属于"这台设备的设置"，所以放在同一个方案里，用户只需要选一次设备。
///
/// ## 兼容性
///
/// 老配置里没有这个列表时，一切照旧：全部按键走全局 <see cref="AppSettings.Keys"/>
/// 与 <see cref="GlobalOptions.BaseRemaps"/>。方案只是**叠加**在上面的一层，
/// 不会让已有配置失效。
/// </summary>
public sealed class DeviceProfile
{
    /* 全部用属性而不是字段：SettingsStore.JsonOptions 没有开 IncludeFields，
     * 写成 public 字段的话序列化时会被静默跳过 —— 存盘后配置全丢，且不报错。 */

    /// <summary>稳定标识（"shared" 或 "p" + 8 位十六进制）。设备绑定改名后仍然有效。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名：设备名，或用户改过的名字（"通用设置"、"左边的键盘"）。</summary>
    public string Name { get; set; } = "";

    /// <summary>是不是「通用设置」。全局最多一份，且不绑定具体设备。</summary>
    public bool Shared { get; set; }

    /// <summary>生效勾选：这个方案（也就是这些设备）是否受本软件影响。</summary>
    public bool Active { get; set; } = true;

    /// <summary>是否继承通用设置。false = 独立方案。</summary>
    public bool Inherit { get; set; } = true;

    /// <summary>绑定到的设备标识（键盘 = VID/PID，鼠标 = "MS:" + VID/PID）。</summary>
    public List<string> Devices { get; set; } = new();

    /// <summary>
    /// 各层的按键映射（第 1~10 层各一份，按需创建）。
    ///
    /// 为什么按层分开存：按键映射页本身是"选层再改键"的，
    /// 设备方案如果只覆盖基础层，用户切到第 3 层改一个键，会以为改的是这只键盘 ——
    /// 实际改的是全局。要么全支持，要么别开这个口子。
    /// </summary>
    public List<DeviceLayer> Layers { get; set; } = new();

    /// <summary>基础改键（把某个键/鼠标键换成另一个），只对本方案的设备生效。</summary>
    public List<KeyRemap> Remaps { get; set; } = new();

    /// <summary>
    /// 原键替换层的绑定，只对本方案的设备生效。
    ///
    /// 与 <see cref="Remaps"/> 的关系：Remaps 只能换成"另一个键"，
    /// 这里能绑任意动作（组合键、宏、程序、文本…），而且不留底层痕迹 ——
    /// 按下去就是那个功能，没有"先换成某键再打出去"的中间态。
    /// </summary>
    public List<KeyBinding> Replacements { get; set; } = new();

    /// <summary>这台设备的原键替换页是否处理鼠标键。</summary>
    public bool ReplaceMouseKeysEnabled { get; set; } = true;

    /// <summary>取某一层的绑定列表（编辑时按需创建；查询时不存在返回 null）。</summary>
    public List<KeyBinding>? KeysOf(int layerId)
        => Layers.FirstOrDefault(l => l.Id == layerId)?.Keys;

    /// <summary>取（或新建）某一层的绑定列表 —— **只在编辑路径上用**。</summary>
    public List<KeyBinding> EnsureLayer(int layerId)
    {
        var layer = Layers.FirstOrDefault(l => l.Id == layerId);
        if (layer == null)
        {
            layer = new DeviceLayer { Id = layerId };
            Layers.Add(layer);
            Layers.Sort((a, b) => a.Id.CompareTo(b.Id));
        }
        return layer.Keys;
    }

    /// <summary>界面上的一句话说明（"通用：所有设备共用" / "2 只设备"）。</summary>
    public string Describe()
    {
        if (Shared) return Loc.T("通用设置：所有设备共用");
        if (Devices.Count == 0) return Loc.T("还没有绑定设备");
        return Devices.Count == 1
            ? InputDevices.DisplayNameOf(Devices[0])
            : Loc.F("{0} 只设备", Devices.Count);
    }
}

/// <summary>设备方案里的一层绑定。</summary>
public sealed class DeviceLayer
{
    public int Id { get; set; } = 1;
    public List<KeyBinding> Keys { get; set; } = new();

    /// <summary>这一页的鼠标键改动是否生效。老配置没有这个字段时默认开。</summary>
    public bool MouseKeysEnabled { get; set; } = true;
}

/// <summary>
/// 设备方案的解析与维护。
///
/// 这里集中回答四个问题（其余代码不再自己判断）：
///   ① 这台设备用哪份方案？          → <see cref="ForDevice"/> / <see cref="Effective"/>
///   ② 这台设备现在生效吗？          → <see cref="IsActive"/>
///   ③ 这个键需要问设备吗？          → <see cref="ScopedKeys"/> / <see cref="ScopedKeyNames"/>
///   ④ 这个组合键在方案里绑了什么？  → <see cref="FindBinding"/>
/// </summary>
public static class DeviceProfiles
{
    public const string SharedId = "shared";

    /// <summary>「通用设置」那一份；不存在返回 null（运行时只读，**不能**顺手建）。</summary>
    public static DeviceProfile? SharedOrNull(AppSettings s)
        => s.Profiles.FirstOrDefault(x => x.Shared || string.Equals(x.Id, SharedId, StringComparison.OrdinalIgnoreCase));

    /// <summary>「通用设置」那一份（没有就建一份）。只在编辑/保存路径上用。</summary>
    public static DeviceProfile Shared(AppSettings s)
    {
        var p = SharedOrNull(s);
        if (p != null)
        {
            p.Shared = true;
            if (string.IsNullOrWhiteSpace(p.Id)) p.Id = SharedId;
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = Loc.T("通用设置");
            return p;
        }

        p = new DeviceProfile { Id = SharedId, Name = Loc.T("通用设置"), Shared = true, Inherit = true };
        s.Profiles.Insert(0, p);
        return p;
    }

    /// <summary>这台设备专属的方案；没有返回 null（表示"用通用设置"）。</summary>
    public static DeviceProfile? ForDevice(AppSettings s, string? deviceKey)
    {
        string key = InputDevices.Normalize(deviceKey);
        if (key.Length == 0) return null;

        foreach (var p in s.Profiles)
        {
            if (p.Shared) continue;
            foreach (var d in p.Devices)
                if (string.Equals(InputDevices.Normalize(d), key, StringComparison.OrdinalIgnoreCase))
                    return p;
        }
        return null;
    }

    /// <summary>实际生效的方案：设备方案优先，否则通用设置（都没有返回 null）。</summary>
    public static DeviceProfile? Effective(AppSettings s, string? deviceKey)
        => ForDevice(s, deviceKey) ?? SharedOrNull(s);

    public static DeviceProfile? ById(AppSettings s, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return s.Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 这台设备现在是否受本软件影响。
    ///
    /// 「全部生效」勾上时一律 true —— 那是用户明确表达的"所有插着的设备都算"，
    /// 比逐台勾选优先。
    /// </summary>
    public static bool IsActive(AppSettings s, string? deviceKey)
    {
        if (s.Global.DeviceScopeAll) return true;

        var p = ForDevice(s, deviceKey);
        return p == null || p.Active;
    }

    /// <summary>
    /// 取（或新建）一台设备的方案，并把它绑定过来。
    /// 界面上"给这只设备单独配置"就是从这里进。
    /// </summary>
    public static DeviceProfile EnsureFor(AppSettings s, string deviceKey)
    {
        string key = InputDevices.Normalize(deviceKey);
        if (key.Length == 0) return Shared(s);

        var p = ForDevice(s, key);
        if (p != null) return p;

        p = new DeviceProfile
        {
            Id = NewId(),
            Name = InputDevices.DisplayNameOf(key) is { Length: > 0 } name ? name : key,
        };
        p.Devices.Add(key);
        s.Profiles.Add(p);
        return p;
    }

    public static string NewId() => "p" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>把一台设备挪到指定方案下（会先从原方案里摘掉，保证"一台设备只属于一份方案"）。</summary>
    public static void Bind(AppSettings s, DeviceProfile target, string deviceKey)
    {
        string key = InputDevices.Normalize(deviceKey);
        if (key.Length == 0) return;

        foreach (var p in s.Profiles)
            p.Devices.RemoveAll(d => string.Equals(InputDevices.Normalize(d), key, StringComparison.OrdinalIgnoreCase));

        target.Devices.Add(key);
    }

    /// <summary>设备勾选（生效/不生效）。</summary>
    public static void SetActive(AppSettings s, string deviceKey, bool active)
    {
        var p = ForDevice(s, deviceKey);
        if (p != null) { p.Active = active; return; }

        /* 没有专属方案 = 用通用设置。通用设置是所有设备共用的，
         * 不能因为"取消勾选一只"就把它整个关掉 —— 那就等于关掉全部。
         * 所以给这台设备建一份独立方案，把勾选落在自己身上。 */
        if (!active)
        {
            p = EnsureFor(s, deviceKey);
            p.Active = false;
        }
    }

    /// <summary>
    /// 维护性清理：保证 Id 唯一、一台设备只属于一份方案、通用设置只有一份。
    ///
    /// 只在加载配置和保存前调用，热路径不碰。
    /// </summary>
    public static void Normalize(AppSettings s)
    {
        if (s.Profiles == null) { s.Profiles = new List<DeviceProfile>(); }
        if (s.Replacements == null) s.Replacements = new List<KeyBinding>();

        /* 替换层里"没有动作"的条目等于没有规则，顺手清掉。
         *
         * 历史版本会把「不绑定」写成一条 Action=doNothing 的绑定（切分类、清空
         * 绑定都会），而替换层又把它当成"吞掉这个键" —— 用户什么都没配，某个键
         * 却被永久吃掉了（wGesture 的鼠标中键）。现在这类条目既不生效也没意义，
         * 归一化时直接删，打开软件就自愈。真正的禁用请用「禁用这个键」。 */
        s.Replacements.RemoveAll(b => b == null || (!b.IsBound && !IsSwallow(b)));
        NormalizeAliases(s);

        // 通用设置最多一份：多出来的合并进第一份
        var shareds = s.Profiles.Where(p => p.Shared).ToList();
        if (shareds.Count > 1)
        {
            var keep = shareds[0];
            for (int i = 1; i < shareds.Count; i++)
            {
                var extra = shareds[i];
                foreach (var l in extra.Layers)
                    keep.EnsureLayer(l.Id).AddRange(l.Keys);
                keep.Remaps.AddRange(extra.Remaps);
                keep.Replacements.AddRange(extra.Replacements);
                s.Profiles.Remove(extra);
            }
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in s.Profiles)
        {
            if (p.Devices == null) p.Devices = new List<string>();
            if (p.Layers == null) p.Layers = new List<DeviceLayer>();
            p.Layers.RemoveAll(l => l == null);
            foreach (var l in p.Layers)
            {
                if (l.Keys == null) l.Keys = new List<KeyBinding>();
                if (l.Id is < 1 or > 10) l.Id = Math.Clamp(l.Id, 1, 10);
            }
            /* 空层不保留：一个"存在但一条绑定都没有"的层，语义上等于"没覆盖"，
             * 留着只会让"是否继承通用设置"的判断变复杂。 */
            p.Layers.RemoveAll(l => l.Keys.Count == 0);
            if (p.Remaps == null) p.Remaps = new List<KeyRemap>();
            if (p.Replacements == null) p.Replacements = new List<KeyBinding>();
            p.Replacements.RemoveAll(b => b == null || (!b.IsBound && !IsSwallow(b)));

            if (string.IsNullOrWhiteSpace(p.Id) || !ids.Add(p.Id)) p.Id = NewId();
            ids.Add(p.Id);

            if (p.Shared)
            {
                p.Devices.Clear();          // 通用设置不绑设备，靠"继承"覆盖所有设备
                p.Inherit = true;
                p.Active = true;
                continue;
            }

            // 一台设备只能属于一份方案：先到先得，后面的重复项丢掉
            /* 先就地归一（大小写 / 空格 / 鼠标前缀），否则同一个 key 会因为
             * 写法不同在"按设备查方案"时对不上 —— 表里用的是 OrdinalIgnoreCase，
             * 但界面显示、复制到剪贴板、导出文件都会把这份原样带出去。 */
            for (int i = 0; i < p.Devices.Count; i++)
                p.Devices[i] = InputDevices.Normalize(p.Devices[i]);
            p.Devices.RemoveAll(d => !claimed.Add(d));
        }
    }

    /// <summary>
    /// 把老设置里 <see cref="KeyRemap.Device"/> 的历史写法收敛成运行时用的设备键。
    ///
    /// 4.20.7 之前这个字段是"写进 JSON 却没人读"的半成品：老界面存的是
    /// <see cref="KeyboardDevices.KeyOf"/> 的归一值，手改过配置的人却可能写成
    /// 完整设备路径（`\\?\HID#VID_1532&amp;PID_00A4…`）甚至友好名。
    /// 修好按设备分流之后，这些值一旦和运行时键对不上，规则就会**静默失效**
    /// —— 比"所有键盘都生效"更糟，所以读盘时统一归一。
    /// </summary>
    public static void NormalizeLegacyDeviceKeys(AppSettings s)
    {
        foreach (var r in s.Global?.BaseRemaps ?? new List<KeyRemap>()) Migrate(r);
        foreach (var p in s.Profiles)
        {
            foreach (var r in p.Remaps ?? new List<KeyRemap>()) Migrate(r);
        }

        static void Migrate(KeyRemap? r)
        {
            string dev = (r?.Device ?? "").Trim();
            if (r == null || dev.Length == 0) return;

            /* 鼠标键已经是我们自己的前缀，Normalize 会做大小写收敛；
             * 键盘则可能是路径，KeyOf 能从任意文本里抠出 VID/PID。 */
            if (InputDevices.IsMouse(dev))
            {
                r.Device = InputDevices.Normalize(dev);
                return;
            }

            string key = KeyboardDevices.KeyOf(dev);
            if (key.Length > 0)
            {
                r.Device = key.ToUpperInvariant();
                return;
            }

            /* 实在认不出来（用户手写了个友好名）：只有名字能对上某台在插设备时
             * 才替换，否则原样留着 —— 那条规则本来就不会命中，别把原值也毁了。 */
            var hit = InputDevices.All().FirstOrDefault(d =>
                string.Equals(d.Name, dev, StringComparison.OrdinalIgnoreCase));
            if (hit != null) r.Device = hit.Key;
        }
    }

    /// <summary>
    /// 需要"问是哪台设备"的源键（鼠标键 + 键盘键的虚拟码）。
    ///
    /// 只有这些键才值得等候 Raw Input（最多十几毫秒）。和设备方案无关的键
    /// 一律不查设备，打字手感不受影响。
    /// </summary>
    public static HashSet<int> ScopedKeys(AppSettings s)
    {
        var set = new HashSet<int>();

        foreach (var p in s.Profiles)
        {
            if (p.Shared || p.Devices.Count == 0) continue;
            foreach (var r in p.Remaps ?? new List<KeyRemap>())
            {
                if (string.IsNullOrWhiteSpace(r.From) || string.IsNullOrWhiteSpace(r.To)) continue;
                if (KeyName.VkOf(r.From) is int vk) set.Add(vk);
            }
        }

        return set;
    }

    /// <summary>
    /// 需要问设备的**键名**（按键映射页的设备专属绑定用）。
    ///
    /// 组合键 ID 形如 `caps_ctrl_q` / `caps_tab_f1`，最后一段才是物理键。
    /// 这里按最后一段收集：宁可多问几个键，也不能漏 —— 漏了就是"配了没用"。
    /// </summary>
    public static HashSet<string> ScopedKeyNames(AppSettings s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in s.Profiles)
        {
            if (p.Shared || p.Devices.Count == 0) continue;
            foreach (var layer in p.Layers)
            {
                foreach (var b in layer.Keys)
                {
                    string combo = b?.Combo ?? "";
                    if (combo.Length == 0) continue;
                    int at = combo.LastIndexOf('_');
                    set.Add(at >= 0 ? combo[(at + 1)..] : combo);
                }
            }

            /* 原键替换层同理：设备方案里配了 space → 1，
             * 空格这个键就必须去问设备，否则"这台键盘的空格才是 1"永远判不出来。 */
            foreach (var b in p.Replacements)
            {
                string combo = b?.Combo ?? "";
                if (combo.Length == 0) continue;
                int at = combo.LastIndexOf('_');
                set.Add(at >= 0 ? combo[(at + 1)..] : combo);
            }
        }

        return set;
    }

    /// <summary>设备方案里有没有任何"真实的"覆盖（键或改键）。用于决定是否值得开设备追踪。</summary>
    public static bool AnyDeviceOverrides(AppSettings s)
    {
        foreach (var p in s.Profiles)
        {
            if (p.Shared) continue;
            if (p.Devices.Count == 0) continue;
            if (p.Layers.Any(l => l.Keys.Count > 0) || p.Remaps.Count > 0 || p.Replacements.Count > 0) return true;
        }
        return false;
    }

    /// <summary>缓存：方案 ID + 层 → 组合键字典（用列表实例做失效判据，与引擎里的层缓存一致）。</summary>
    private static readonly Dictionary<(string, int), (List<KeyBinding> Src, Dictionary<string, KeyBinding> Map, int Version)>
        _binding = new();

    public static void InvalidateBindings()
    {
        lock (_binding) _binding.Clear();
        lock (_repl) _repl.Clear();
    }

    /// <summary>
    /// 在某个方案里查绑定。方案里没有这个组合键 → 返回 null，
    /// 调用方再按 <see cref="DeviceProfile.Inherit"/> 决定继承还是无视。
    /// </summary>
    public static KeyBinding? FindBinding(AppSettings s, DeviceProfile profile, int layerId, string comboId)
    {
        var map = BindingMap(s, profile, layerId);
        return map != null && map.TryGetValue(comboId, out var hit) ? hit : null;
    }

    /// <summary>
    /// 组合键在"这台设备"上最终绑了什么 —— 继承规则都在这里，别处不必再判断。
    ///
    /// 顺序：设备自己的方案 → 通用设置 → （调用方再看全局层绑定）。
    /// <paramref name="blocked"/> 为真表示"到此为止，不许再回退"：独立方案，
    /// 或者这份方案里显式写了 doNothing（用户在这台设备上把这个键清掉了，
    /// 通用设置里那条绑定就不该再漏进来）。
    /// </summary>
    public static KeyBinding? ResolveDeviceBinding(
        AppSettings s, int layerId, string comboId, string? deviceKey, out bool blocked)
    {
        blocked = false;

        var own = ForDevice(s, deviceKey);
        if (own != null && !own.Shared)
        {
            var hit = FindBinding(s, own, layerId, comboId);
            if (hit is { IsBound: true }) return hit;

            /* 显式清空（doNothing）算"这台设备上这个键没功能"：
             * 不停在这里的话，用户在单台设备上取消一条通用绑定永远做不到。 */
            if (hit != null && string.Equals(hit.Action, "doNothing", StringComparison.OrdinalIgnoreCase))
            {
                blocked = true;
                return null;
            }

            if (!own.Inherit) { blocked = true; return null; }
        }

        if (SharedOrNull(s) is { } shared)
        {
            var hit = FindBinding(s, shared, layerId, comboId);
            if (hit is { IsBound: true }) return hit;
            if (hit != null && string.Equals(hit.Action, "doNothing", StringComparison.OrdinalIgnoreCase))
            {
                blocked = true;
                return null;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // 原键替换层
    // ------------------------------------------------------------------

    /// <summary>
    /// 这台设备上"按这个组合键"应该执行什么（原键替换层）。
    ///
    /// 顺序与层绑定完全一致：设备自己的方案 → 通用设置 → 调用方再看全局
    /// <see cref="AppSettings.Replacements"/>。<paramref name="blocked"/> 的含义
    /// 也一样：独立方案、或这份方案里显式写了 doNothing，就不再往下回退。
    /// </summary>
    /// <summary>
    /// 这台设备上"按这个组合键"应该执行什么（原键替换层）。
    ///
    /// 顺序与层绑定一致：设备自己的方案 → 通用设置 → 调用方再看全局
    /// <see cref="AppSettings.Replacements"/>。<paramref name="blocked"/> 的含义
    /// 是"这台设备独立、不许再往下回退"。
    ///
    /// doNothing 在这里**不算阻断**：它表示"没有规则"，而不是"这条规则赢了"。
    /// （层绑定里 doNothing 仍然阻断——那里它是"这个键在本层什么都不做"的显式意图。）
    /// </summary>
    public static KeyBinding? ResolveDeviceReplacement(
        AppSettings s, string comboId, string? deviceKey, out bool blocked)
    {
        blocked = false;

        var own = ForDevice(s, deviceKey);
        if (own != null && !own.Shared)
        {
            var hit = FindReplacement(s, own, comboId);
            if (hit is { IsBound: true }) return hit;
            if (!own.Inherit) { blocked = true; return null; }
        }

        if (SharedOrNull(s) is { } shared)
        {
            var hit = FindReplacement(s, shared, comboId);
            if (hit is { IsBound: true }) return hit;
        }

        return null;
    }

    /// <summary>明确的"吞掉这个键"动作（区别于 doNothing＝没有规则）。</summary>
    private static bool IsSwallow(KeyBinding? b)
        => b != null && string.Equals(b.Action, "swallowKey", StringComparison.OrdinalIgnoreCase);

    private static bool IsDoNothing(KeyBinding? b)
        => b != null && string.Equals(b.Action, "doNothing", StringComparison.OrdinalIgnoreCase);

    /// <summary>在某个方案的替换层里查（没配过 → null）。</summary>
    public static KeyBinding? FindReplacement(AppSettings s, DeviceProfile profile, string comboId)
    {
        var map = ReplacementMap(s, profile);
        return map != null && map.TryGetValue(comboId, out var hit) ? hit : null;
    }

    /// <summary>全局替换层（不属于任何设备方案的那一份）。</summary>
    public static KeyBinding? FindGlobalReplacement(AppSettings s, string comboId)
    {
        var map = GlobalReplacementMap(s);
        return map != null && map.TryGetValue(comboId, out var hit) ? hit : null;
    }

    /// <summary>
    /// 编辑路径：拿到"这台设备"的替换层列表。
    /// 设备键为空 = 全局那一份；否则没有方案就建一个（用户明确要在这台设备上配）。
    /// </summary>
    public static List<KeyBinding> ReplacementsFor(AppSettings s, string? deviceKey)
        => string.IsNullOrEmpty(deviceKey) ? s.Replacements : EnsureFor(s, deviceKey).Replacements;

    /// <summary>
    /// 某台设备某一页的鼠标键开关。设备方案里没写这一层时回退到全局层设置，
    /// 全局也没有就默认开 —— 老配置升级后行为不变。
    /// </summary>
    public static bool MouseKeysEnabled(AppSettings s, int layerId, string? deviceKey)
    {
        if (!string.IsNullOrEmpty(deviceKey))
        {
            var own = ForDevice(s, deviceKey);
            var layer = own?.Layers.FirstOrDefault(l => l.Id == layerId);
            if (layer != null) return layer.MouseKeysEnabled;
        }
        return s.Layers.FirstOrDefault(l => l.Id == layerId)?.MouseKeysEnabled ?? true;
    }

    /// <summary>原键替换页的鼠标键开关。</summary>
    public static bool ReplaceMouseKeysEnabled(AppSettings s, string? deviceKey)
    {
        if (!string.IsNullOrEmpty(deviceKey))
        {
            var own = ForDevice(s, deviceKey);
            if (own != null) return own.ReplaceMouseKeysEnabled;
        }
        return s.ReplaceMouseKeysEnabled;
    }

    /// <summary>
    /// 设备备注：这台设备叫什么。
    ///
    /// 空 = 用系统里的名字（<see cref="InputDevices.DisplayNameOf"/> 自己会去查）。
    /// </summary>
    public static string Alias(AppSettings s, string? deviceKey)
        => InputDevices.AliasOf(deviceKey);

    /// <summary>
    /// 给一台设备起名 / 改名 / 恢复系统名（传空）。
    ///
    /// 同一型号的键盘在系统里全叫「HID Keyboard Device」，备注是唯一能把它们
    /// 分开的办法，所以这里顺手把内存镜像刷新一下 —— 界面下一次重画就是新名字。
    /// </summary>
    public static void SetAlias(AppSettings s, string? deviceKey, string? alias)
    {
        if (s.Global == null) return;
        string key = InputDevices.Normalize(deviceKey);
        if (key.Length == 0) return;

        s.Global.DeviceAliases ??= new Dictionary<string, string>();
        string name = (alias ?? "").Trim();
        if (name.Length == 0) s.Global.DeviceAliases.Remove(key);
        else s.Global.DeviceAliases[key] = name;

        NormalizeAliases(s);
    }

    /// <summary>
    /// 备注表归一：键按设备标识归一（大写/统一前缀），值去空白，空值丢掉。
    ///
    /// 顺便把结果同步给 <see cref="InputDevices"/> —— 那是所有界面取名字的唯一入口。
    /// </summary>
    public static void NormalizeAliases(AppSettings s)
    {
        s.Global ??= new GlobalOptions();
        s.Global.DeviceAliases ??= new Dictionary<string, string>();

        Dictionary<string, string> clean = new(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in s.Global.DeviceAliases)
        {
            string key = InputDevices.Normalize(kv.Key);
            string name = (kv.Value ?? "").Trim();
            if (key.Length == 0 || name.Length == 0) continue;
            clean[key] = name;
        }

        s.Global.DeviceAliases = clean;
        InputDevices.SetAliases(clean);
    }

    /// <summary>有没有哪份设备方案配了替换层 —— 有就必须问设备，否则判不出该不该生效。</summary>
    public static bool HasDeviceReplacements(AppSettings s)
    {
        foreach (var p in s.Profiles)
        {
            if (p.Shared || p.Devices.Count == 0) continue;
            if (p.Replacements.Count > 0) return true;
        }
        return false;
    }

    /// <summary>替换层字典缓存：键是组合键（`space` / `ctrl_c` / `mouseMiddle`）。</summary>
    private static readonly Dictionary<string, (List<KeyBinding> Src, Dictionary<string, KeyBinding> Map, int Version, int Sig)>
        _repl = new();

    private const string GlobalReplKey = "#global";

    private static Dictionary<string, KeyBinding>? ReplacementMap(AppSettings s, DeviceProfile profile)
        => Bake(s, profile.Id ?? "", profile.Replacements);

    private static Dictionary<string, KeyBinding>? GlobalReplacementMap(AppSettings s)
        => Bake(s, GlobalReplKey, s.Replacements);

    private static Dictionary<string, KeyBinding>? Bake(AppSettings s, string key, List<KeyBinding> src)
    {
        if (src == null || src.Count == 0) return null;

        int version = s.Global?.BindingsVersion ?? 0;
        int sig = SignatureOf(src);
        lock (_repl)
        {
            if (_repl.TryGetValue(key, out var hit)
                && ReferenceEquals(hit.Src, src)
                && hit.Sig == sig
                && hit.Version == version)
                return hit.Map;

            var map = new Dictionary<string, KeyBinding>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in src)
            {
                if (b == null || string.IsNullOrWhiteSpace(b.Combo)) continue;
                map[b.Combo] = b;      // 后写的覆盖先写的：与界面"改同一行"的直觉一致
            }
            _repl[key] = (src, map, version, sig);
            return map;
        }
    }

    /// <summary>
    /// 列表内容指纹：用来判断"同一个 List 对象被改过"。
    ///
    /// 只用 Count 判断是不够的 —— 界面里"改同一行的动作"会让列表长度不变，
    /// 那时缓存会把旧绑定一直发给引擎（表现为"改了没反应，重启才好"）。
    /// </summary>
    private static int SignatureOf(List<KeyBinding> src)
    {
        int hash = 17;
        foreach (var b in src)
        {
            hash = hash * 31 + (b?.Combo?.GetHashCode() ?? 0);
            hash = hash * 31 + (b?.Action?.GetHashCode() ?? 0);
            hash = hash * 31 + (b?.Arg?.GetHashCode() ?? 0);
        }
        return hash * 31 + src.Count;
    }

    private static Dictionary<string, KeyBinding>? BindingMap(AppSettings s, DeviceProfile profile, int layerId)
    {
        var keys = profile.KeysOf(layerId);
        if (keys == null || keys.Count == 0) return null;

        int version = s.Global?.BindingsVersion ?? 0;
        var cacheKey = (profile.Id ?? "", layerId);

        lock (_binding)
        {
            if (_binding.TryGetValue(cacheKey, out var hit)
                && ReferenceEquals(hit.Src, keys)
                && hit.Map.Count == keys.Count
                && hit.Version == version)
                return hit.Map;
        }

        var map = new Dictionary<string, KeyBinding>(keys.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
        {
            if (string.IsNullOrEmpty(k?.Combo)) continue;
            map[k.Combo] = k;
        }

        lock (_binding) _binding[cacheKey] = (keys, map, version);
        return map;
    }

    /// <summary>
    /// 这台设备实际使用的改键表（不含全局）：先通用设置，再这台设备的覆盖。
    /// 返回"由低到高"的顺序，调用方按顺序往字典里塞即可。
    /// </summary>
    public static IEnumerable<IEnumerable<KeyRemap>> RemapsByLayer(AppSettings s, string? deviceKey)
    {
        /* 运行时只读：这里绝不能顺手建出通用方案 —— 那会在按键回调里改配置。 */
        if (SharedOrNull(s) is { } shared) yield return shared.Remaps;

        var own = ForDevice(s, deviceKey);
        if (own != null && !own.Shared) yield return own.Remaps;
    }

    /// <summary>界面编辑用：这次改动应该写进哪一份改键列表。</summary>
    public static List<KeyRemap> RemapsFor(AppSettings s, string? deviceKey)
    {
        /* 选了具体设备 → 必须写进它自己的方案。没有就现在建：
         * 用户正要往这台设备里加规则，"先建一个空方案"是这个动作的一部分，
         * 不是副作用。（运行时的 RemapsByLayer 才是只读的那一份。） */
        if (!string.IsNullOrWhiteSpace(deviceKey)) return EnsureFor(s, deviceKey!).Remaps;

        return Shared(s).Remaps;
    }

    /// <summary>
    /// 有没有设备被明确"划出去"了。
    ///
    /// 两种来源：
    ///   · 用户没勾「全部生效」，于是只有打勾的设备归本软件管；
    ///   · 某台设备的方案被取消勾选。
    ///
    /// 为真时，运行时要把判定范围放宽到"所有有规则的键" —— 否则
    /// 一台被划出去的键盘，它的 Caps 仍然会触发全局改键，
    /// 用户会觉得"我明明关掉了它"。
    /// </summary>
    public static bool HasInactive(AppSettings s)
    {
        if (s.Global != null && !s.Global.DeviceScopeAll) return true;

        foreach (var p in s.Profiles)
        {
            if (p.Shared) continue;
            if (p.Devices.Count == 0) continue;
            if (!p.Active) return true;
        }
        return false;
    }

    /// <summary>
    /// 是否值得为设备追踪付出代价（主进程装钩子、每键多等十几毫秒）。
    ///
    /// 有设备专属方案、有设备被划出去、或者用户没勾「全部生效」，
    /// 三者任一成立就需要 —— 否则设备信息根本拿不到，配了也不生效。
    /// </summary>
    public static bool NeedsDeviceTracking(AppSettings s)
    {
        if (HasInactive(s)) return true;

        foreach (var p in s.Profiles)
        {
            if (p.Shared) continue;
            if (p.Remaps.Count > 0) return true;
            if (p.Replacements.Count > 0) return true;
            if (p.Layers.Any(l => l.Keys.Count > 0)) return true;
        }
        return false;
    }

    /// <summary>
    /// 所有"配过东西"的键名（全局改键的源键、各方案改键的源键、
    /// 各层绑定的物理键、触发键）。
    ///
    /// 只在 <see cref="HasInactive"/> 为真时用来判断"这个键要不要问设备"，
    /// 所以宁可多收几个键，也不能漏 —— 漏了那台设备就关不干净。
    /// </summary>
    public static HashSet<string> RuleKeyNames(AppSettings s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCombo(string? combo)
        {
            if (string.IsNullOrWhiteSpace(combo)) return;
            int at = combo.LastIndexOf('_');
            set.Add(at >= 0 ? combo[(at + 1)..] : combo);
        }

        void AddRemaps(IEnumerable<KeyRemap>? list)
        {
            foreach (var r in list ?? Array.Empty<KeyRemap>())
                if (r != null && !string.IsNullOrWhiteSpace(r.From)) set.Add(r.From);
        }

        void AddBindings(IEnumerable<KeyBinding>? list)
        {
            foreach (var b in list ?? Array.Empty<KeyBinding>())
                AddCombo(b?.Combo);
        }

        AddRemaps(s.Global?.BaseRemaps);

        foreach (var t in s.Triggers)
            if (t != null && !string.IsNullOrWhiteSpace(t.Key)) set.Add(t.Key);

        AddBindings(s.Keys);
        AddBindings(s.Replacements);
        foreach (var layer in s.Layers) AddBindings(layer?.Keys);

        foreach (var p in s.Profiles)
        {
            AddRemaps(p.Remaps);
            AddBindings(p.Replacements);
            foreach (var layer in p.Layers) AddBindings(layer?.Keys);
        }

        return set;
    }
}
