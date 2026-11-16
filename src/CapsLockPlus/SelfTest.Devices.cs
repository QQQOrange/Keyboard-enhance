using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus;

/// <summary>
/// 「按设备分别设置」的自检：方案解析、生效范围、改键合并、层绑定回退、鼠标键名。
///
/// 这一组盯的是**用户真能感觉到的错**：
///   · 配了不生效（设备规则没进表 / 没被记入 DeviceScopedKeys）；
///   · 关掉一台设备它还在改键（生效范围判定漏了某条路径）；
///   · 通用设置被设备方案吃掉（合并顺序反了）；
///   · 存盘后配置丢了（模型写成字段，序列化静默跳过）。
/// </summary>
public static partial class SelfTest
{
    private static void TestDeviceProfiles()
    {
        const string kbA = "VID_1532&PID_00A4";
        const string kbB = "VID_0483&PID_2031";
        const string msA = "MS:VID_046D&PID_C52B";

        // ---------------------------------------------------------------- ① 方案解析

        var s = new AppSettings();
        var shared = DeviceProfiles.Shared(s);

        Check("设备方案：通用设置只有一份", ReferenceEquals(shared, DeviceProfiles.Shared(s)));
        Check("设备方案：通用设置排在最前", ReferenceEquals(s.Profiles[0], shared));
        Check("设备方案：通用设置不绑定设备", shared.Devices.Count == 0);

        shared.Remaps.Add(new KeyRemap { From = "capslock", To = "esc" });

        Check("设备方案：没配过的设备回落到通用设置",
            ReferenceEquals(DeviceProfiles.Effective(s, kbA), shared), kbA);

        var ownA = DeviceProfiles.EnsureFor(s, kbA);
        Check("设备方案：按需建出的方案绑定到这台设备", ownA.Devices.Contains(kbA));
        Check("设备方案：默认继承通用设置", ownA.Inherit);
        Check("设备方案：同一台设备不会建出第二份", ReferenceEquals(ownA, DeviceProfiles.EnsureFor(s, kbA)));

        /* 一台设备只能属于一份方案：挪过去之后旧方案里必须摘掉，
         * 否则两份方案都自称"管这台键盘"，谁赢取决于遍历顺序 —— 那种 bug 没法解释。 */
        var ownB = DeviceProfiles.EnsureFor(s, kbB);
        DeviceProfiles.Bind(s, ownB, kbA);
        Check("设备方案：设备挪走后旧方案不再接管", !ownA.Devices.Contains(kbA));
        Check("设备方案：设备挪走后新方案接管", ownB.Devices.Contains(kbA));
        Check("设备方案：按设备反查只命中新方案", ReferenceEquals(DeviceProfiles.ForDevice(s, kbA), ownB));

        // ---------------------------------------------------------------- ② 生效范围

        Check("生效范围：默认全部生效", s.Global.DeviceScopeAll);

        DeviceProfiles.SetActive(s, msA, false);
        Check("生效范围：全部生效时逐台勾选不起作用", DeviceProfiles.IsActive(s, msA), msA);

        s.Global.DeviceScopeAll = false;
        Check("生效范围：取消全部生效后逐台勾选说话", !DeviceProfiles.IsActive(s, msA), msA);
        Check("生效范围：没配过的设备仍然生效", DeviceProfiles.IsActive(s, "VID_DEAD&PID_BEEF"));

        DeviceProfiles.SetActive(s, msA, true);
        Check("生效范围：重新打勾后恢复接管", DeviceProfiles.IsActive(s, msA));

        s.Global.DeviceScopeAll = true;
        s.Global.DeviceScopeAll = false;
        Check("生效范围：没勾全部生效就是 HasInactive", DeviceProfiles.HasInactive(s));

        /* 纯全局改键时不该为设备追踪付代价：每键多等十几毫秒，打字会发涩。
         * 这条断言盯的正是"性能红线"，改坏了会先在这里响。 */
        var plain = new AppSettings();
        plain.Global.BaseRemaps.Add(new KeyRemap { From = "capslock", To = "esc" });
        Check("性能：纯全局改键不需要设备追踪", !DeviceProfiles.NeedsDeviceTracking(plain));

        var tracked = new AppSettings();
        DeviceProfiles.EnsureFor(tracked, kbA).Remaps.Add(new KeyRemap { From = "a", To = "b" });
        Check("性能：有设备专属改键就需要设备追踪", DeviceProfiles.NeedsDeviceTracking(tracked));

        var inactive = new AppSettings();
        inactive.Global.DeviceScopeAll = false;
        Check("性能：有设备被划出去就需要设备追踪", DeviceProfiles.NeedsDeviceTracking(inactive));

        // ---------------------------------------------------------------- ③ 改键合并

        var remap = new AppSettings();
        remap.Global.BaseRemaps.Add(new KeyRemap { From = "capslock", To = "esc" });

        var rs = DeviceProfiles.Shared(remap);
        rs.Remaps.Add(new KeyRemap { From = "a", To = "b" });

        var rA = DeviceProfiles.EnsureFor(remap, kbA);
        rA.Remaps.Add(new KeyRemap { From = "capslock", To = "lctrl" });
        rA.Remaps.Add(new KeyRemap { From = "b", To = "c" });   // 只有 A 有这条

        var table = KeyRemapMap.BuildTable(remap);

        Check("改键合并：通用规则进全局表", table.All.ContainsKey(0x41), "a→b");
        Check("改键合并：只有设备才有的键不进全局表", !table.All.ContainsKey(0x42), "b→c");
        Check("改键合并：设备专属键在别的设备上不改",
            !table.TryGet(0x42, kbB, out _), "b on B");
        Check("改键合并：设备自己的规则优先于通用",
            table.TryGet(0x14, kbA, out int capsOnA) && capsOnA == 0xA2, "caps on A");
        Check("改键合并：别的设备落回全局规则",
            table.TryGet(0x14, kbB, out int capsOnB) && capsOnB == 0x1B, "caps on B");
        Check("改键合并：通用规则对设备也生效",
            table.TryGet(0x41, kbA, out int aOnA) && aOnA == 0x42, "a on A");

        /* DeviceScopedKeys 只该收"真的要和设备商量"的键。
         * 全局/通用规则也记进来的话，每个按键都会去等设备解析 —— 白等。 */
        Check("性能：设备规则键被记入 DeviceScopedKeys", table.DeviceScopedKeys.Contains(0x14));
        Check("性能：通用规则键不被记入 DeviceScopedKeys", !table.DeviceScopedKeys.Contains(0x41));

        /* 老数据的写法：设备写在 KeyRemap.Device 字段上（v4.20 之前的唯一做法）。
         * 这类规则只能进那台设备的表 —— 顺手倒进全局表的话，换个键盘 Caps 还是 Esc。 */
        var legacy = new AppSettings();
        legacy.Global.BaseRemaps.Add(new KeyRemap { From = "capslock", To = "esc", Device = kbA });

        var legacyTable = KeyRemapMap.BuildTable(legacy);
        Check("老式设备规则：不进全局表", !legacyTable.All.ContainsKey(0x14), "caps");
        Check("老式设备规则：在那台设备上生效",
            legacyTable.TryGet(0x14, kbA, out int legacyOnA) && legacyOnA == 0x1B, "caps on A");
        Check("老式设备规则：换台设备就不生效", !legacyTable.TryGet(0x14, kbB, out _), "caps on B");
        Check("老式设备规则：键被记入 DeviceScopedKeys", legacyTable.DeviceScopedKeys.Contains(0x14));

        var independent = new AppSettings();
        var is1 = DeviceProfiles.EnsureFor(independent, kbB);
        is1.Inherit = false;
        is1.Remaps.Add(new KeyRemap { From = "a", To = "b" });
        DeviceProfiles.Shared(independent).Remaps.Add(new KeyRemap { From = "capslock", To = "esc" });

        var indTable = KeyRemapMap.BuildTable(independent);
        Check("独立方案：记入 Independent 集合", indTable.Independent.Contains(kbB.ToUpperInvariant()));
        Check("独立方案：不继承通用改键", !indTable.TryGet(0x14, kbB, out _), "caps");
        Check("独立方案：自己的改键照常生效",
            indTable.TryGet(0x41, kbB, out int indA) && indA == 0x42, "a");
        Check("独立方案：不影响别的设备（别处仍吃通用）",
            indTable.TryGet(0x14, kbA, out int otherCaps) && otherCaps == 0x1B, "caps on A");

        // ---------------------------------------------------------------- ④ 鼠标按键

        CheckEqual("鼠标键名：mouseLeft", KeyName.VkOf("mouseLeft"), 0x01);
        CheckEqual("鼠标键名：mouseRight", KeyName.VkOf("mouseRight"), 0x02);
        CheckEqual("鼠标键名：mouseMiddle", KeyName.VkOf("mouseMiddle"), 0x04);
        CheckEqual("鼠标键名：mouseX1", KeyName.VkOf("mouseX1"), 0x05);
        CheckEqual("鼠标键名：mouseX2", KeyName.VkOf("mouseX2"), 0x06);
        CheckEqual("鼠标键名：反查 0x05", KeyName.NameOf(0x05), "mouseX1");
        Check("鼠标键名：可以绑进层里", KeyName.IsBindable(0x05) && KeyName.IsBindable(0x01));
        CheckEqual("鼠标键名：显示成中文", KeyName.Display("mouseX1"), Loc.T("鼠标侧键 1"));

        Check("鼠标键判定：0x01~0x06", InputDevices.IsMouseButton(0x01) && InputDevices.IsMouseButton(0x06));
        Check("鼠标键判定：0 与 0x07 不是", !InputDevices.IsMouseButton(0x00) && !InputDevices.IsMouseButton(0x07));
        Check("鼠标设备 key：带 MS: 前缀才算鼠标", InputDevices.IsMouse(msA) && !InputDevices.IsMouse(kbA));
        CheckEqual("鼠标设备 key：小写也归一", InputDevices.Normalize(" ms:vid_046d&pid_c52b "), msA);
        CheckEqual("鼠标设备 key：Strip 去掉前缀", MouseDevices.Strip(msA), "VID_046D&PID_C52B");
        CheckEqual("鼠标设备 key：空路径不产生 key", MouseDevices.KeyOf(""), "");

        /* 鼠标键改键：和键盘同一条链路，但设备维度必须能区分到"哪只鼠标"。
         * 侧键换成中键是最常见的用法（很多鼠标侧键默认是前进/后退）。 */
        var mouse = new AppSettings();
        var mProfile = DeviceProfiles.EnsureFor(mouse, msA);
        mProfile.Remaps.Add(new KeyRemap { From = "mouseX1", To = "mouseMiddle" });

        var mouseTable = KeyRemapMap.BuildTable(mouse);
        Check("鼠标改键：侧键被记入设备键集合", mouseTable.DeviceScopedKeys.Contains(0x05));
        Check("鼠标改键：这只鼠标上侧键变中键",
            mouseTable.TryGet(0x05, msA, out int x1) && x1 == 0x04, "x1");
        Check("鼠标改键：别的鼠标上侧键不变", !mouseTable.TryGet(0x05, "MS:VID_1234&PID_5678", out _));
        Check("鼠标改键：键盘上不会误触发", !mouseTable.TryGet(0x05, kbA, out _));

        /* 端到端：钩子线程送进来的鼠标事件走的是同一条 OnKeyEvent。
         * 只验表是不够的 —— 表算对了但运行时没接上，用户看到的是"配了没反应"。 */
        var mSent = new List<string>();
        var savedSender2 = InputSender.SenderForTest;
        InputSender.SenderForTest = (vk, up) => { lock (mSent) mSent.Add($"{vk:X2}{(up ? "↑" : "↓")}"); };
        try
        {
            var ms = new AppSettings();
            ms.Global.BaseRemaps.Clear();
            foreach (var t in ms.Triggers) t.Enabled = false;

            DeviceProfiles.EnsureFor(ms, msA).Remaps.Add(new KeyRemap { From = "mouseX1", To = "mouseMiddle" });

            var mQueue = new ActionQueue();
            var mEngine = new HotkeyEngine(mQueue, () => ms, (_, _) => true)
            {
                DeviceResolver = (_, _) => msA,
            };

            lock (mSent) mSent.Clear();
            Check("鼠标改键：侧键按下被接管", mEngine.SimulateKey(0x05, true));
            Check("鼠标改键：侧键抬起被接管", mEngine.SimulateKey(0x05, false));
            WaitUntil(() => { lock (mSent) return mSent.Count >= 2; });

            lock (mSent)
            {
                CheckEqual("鼠标改键：按下注入中键", mSent.FirstOrDefault() ?? "", "04↓");
                CheckEqual("鼠标改键：抬起注入中键抬起", mSent.LastOrDefault() ?? "", "04↑");
            }

            /* 换一只鼠标：规则不该跟着跑过去 —— 侧键原样放行。
             * 这一条正是"按设备分别设置"的意义所在。 */
            mEngine.DeviceResolver = (_, _) => "MS:VID_1234&PID_5678";
            lock (mSent) mSent.Clear();
            Check("鼠标改键：别的鼠标上侧键放行", !mEngine.SimulateKey(0x05, true));
            mEngine.SimulateKey(0x05, false);

            mEngine.Dispose();
            mQueue.Dispose();
        }
        finally
        {
            InputSender.SenderForTest = savedSender2;
        }

        // ---------------------------------------------------------------- ⑤ 层绑定回退

        var layers = new AppSettings();
        layers.Layers.Add(new LayerOptions
        {
            Id = 1,
            Keys = { new KeyBinding { Combo = "caps_a", Action = "volumeUp" } },
        });

        var lShared = DeviceProfiles.Shared(layers);
        lShared.EnsureLayer(1).Add(new KeyBinding { Combo = "caps_b", Action = "volumeDown" });

        var lA = DeviceProfiles.EnsureFor(layers, kbA);
        lA.EnsureLayer(1).Add(new KeyBinding { Combo = "caps_a", Action = "brightnessUp" });

        Check("层绑定：设备方案优先",
            DeviceProfiles.ResolveDeviceBinding(layers, 1, "caps_a", kbA, out _)?.Action == "brightnessUp");
        Check("层绑定：设备没覆盖的键落回通用",
            DeviceProfiles.ResolveDeviceBinding(layers, 1, "caps_b", kbA, out _)?.Action == "volumeDown");

        var miss = DeviceProfiles.ResolveDeviceBinding(layers, 1, "caps_c", kbA, out bool missBlocked);
        Check("层绑定：都没写的键交还给全局层", miss == null && !missBlocked, "caps_c");

        /* 独立方案：命中不到就不许再往全局看。否则"另起一套键位"永远做不到 ——
         * 全局里那些绑定会一直漏进来。 */
        var solo = DeviceProfiles.EnsureFor(layers, kbB);
        solo.Inherit = false;
        solo.EnsureLayer(1).Add(new KeyBinding { Combo = "caps_x", Action = "mute" });

        Check("独立方案：自己的绑定生效",
            DeviceProfiles.ResolveDeviceBinding(layers, 1, "caps_x", kbB, out _)?.Action == "mute");

        var soloShared = DeviceProfiles.ResolveDeviceBinding(layers, 1, "caps_b", kbB, out bool soloB1);
        Check("独立方案：不再继承通用绑定", soloShared == null && soloB1, "caps_b");
        var soloGlobal = DeviceProfiles.ResolveDeviceBinding(layers, 1, "caps_a", kbB, out bool soloB2);
        Check("独立方案：也不再继承全局绑定", soloGlobal == null && soloB2, "caps_a");

        /* 绑成 doNothing 的键算"显式清空"：设备方案里写了它，就该停在这里，
         * 不能继续往通用设置里找 —— 否则用户没法在单台设备上取消一条通用绑定。 */
        var cleared = new AppSettings();
        DeviceProfiles.Shared(cleared).EnsureLayer(1)
            .Add(new KeyBinding { Combo = "caps_m", Action = "volumeUp" });
        var clearedOwn = DeviceProfiles.ResolveDeviceBinding(cleared, 1, "caps_m", kbA, out _);
        Check("层绑定：没有设备方案时吃通用绑定", clearedOwn?.Action == "volumeUp", "caps_m");

        var cA = DeviceProfiles.EnsureFor(cleared, kbA);
        cA.EnsureLayer(1).Add(new KeyBinding { Combo = "caps_m", Action = "doNothing" });

        var clearedHit = DeviceProfiles.ResolveDeviceBinding(cleared, 1, "caps_m", kbA, out bool clearedBlocked);
        Check("层绑定：设备上清掉的键不会漏回通用", clearedHit == null && clearedBlocked, "caps_m");
        Check("层绑定：清掉只影响这一台设备",
            DeviceProfiles.ResolveDeviceBinding(cleared, 1, "caps_m", kbB, out _)?.Action == "volumeUp", "caps_m on B");

        // ---------------------------------------------------------------- ⑥ 规范化

        var messy = new AppSettings();
        messy.Profiles.Add(new DeviceProfile { Id = "", Name = "", Shared = true });
        messy.Profiles.Add(new DeviceProfile { Id = "p1", Name = "左手键盘", Shared = true });
        messy.Profiles.Add(new DeviceProfile
        {
            Id = "p2",
            Name = "",
            Devices = { "VID_1&PID_2", "vid_1&pid_2" },
            Layers =
            {
                new DeviceLayer { Id = 99, Keys = { new KeyBinding { Combo = "caps_q", Action = "qbar" } } },
                new DeviceLayer { Id = 2 },
            },
        });
        messy.Profiles.Add(new DeviceProfile { Id = "shared", Name = "通用设置", Shared = true });

        DeviceProfiles.Normalize(messy);

        CheckEqual("规范化：通用设置只剩一份",
            messy.Profiles.Count(p => p.Shared || p.Id == DeviceProfiles.SharedId), 1);
        CheckEqual("规范化：设备 key 去重并大写", messy.Profiles.First(p => p.Id == "p2").Devices.Count, 1);
        Check("规范化：设备 key 变大写",
            messy.Profiles.First(p => p.Id == "p2").Devices[0] == "VID_1&PID_2");
        Check("规范化：层 ID 夹进 1~10",
            messy.Profiles.First(p => p.Id == "p2").Layers.All(l => l.Id is >= 1 and <= 10));
        Check("规范化：空层被清掉",
            messy.Profiles.First(p => p.Id == "p2").Layers.All(l => l.Keys.Count > 0));
        Check("规范化：空 Id 被补上",
            messy.Profiles.All(p => p.Id.Length > 0));

        /* 通用设置的那份规则不能在规范化里被丢掉 ——
         * 两条重复方案合并时漏 merge，用户就会看到"我的通用设置没了"。 */
        var merge = new AppSettings();
        merge.Profiles.Add(new DeviceProfile { Id = "shared", Shared = true });
        merge.Profiles.Add(new DeviceProfile
        {
            Id = "shared2",
            Shared = true,
            Remaps = { new KeyRemap { From = "capslock", To = "esc" } },
        });
        DeviceProfiles.Normalize(merge);
        Check("规范化：合并通用方案时保留规则",
            merge.Profiles.Count(p => p.Shared) == 1 && merge.Profiles[0].Remaps.Count == 1);

        // ---------------------------------------------------------------- ⑦ 存盘

        /* DeviceProfile / DeviceLayer 必须是属性而不是字段：
         * JsonOptions 没开 IncludeFields，写成字段存盘时会被静默跳过。 */
        var save = new AppSettings();
        DeviceProfiles.Shared(save).EnsureLayer(1)
            .Add(new KeyBinding { Combo = "caps_z", Action = "screenshot" });
        var saveA = DeviceProfiles.EnsureFor(save, kbA);
        saveA.Remaps.Add(new KeyRemap { From = "a", To = "b" });
        saveA.EnsureLayer(3).Add(new KeyBinding { Combo = "caps_q", Action = "qbar" });
        DeviceProfiles.SetActive(save, msA, false);

        string json = JsonSerializer.Serialize(save, SettingsStore.JsonOptions);
        var back = JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;

        Check("存盘：方案列表被写进文件", back.Profiles.Count >= 2, back.Profiles.Count.ToString());
        var backA = DeviceProfiles.ForDevice(back, kbA);
        Check("存盘：设备绑定还原", backA != null && backA.Devices.Contains(kbA));
        Check("存盘：设备改键还原", backA?.Remaps.Any(r => r.From == "a" && r.To == "b") == true);
        Check("存盘：设备层绑定还原",
            backA?.KeysOf(3)?.Any(k => k.Combo == "caps_q" && k.Action == "qbar") == true);
        Check("存盘：通用方案的层绑定还原",
            DeviceProfiles.SharedOrNull(back)?.KeysOf(1)?.Any(k => k.Combo == "caps_z") == true);
        Check("存盘：取消勾选的设备还原",
            DeviceProfiles.ForDevice(back, msA) is { Active: false });

        // ---------------------------------------------------------------- ⑧ 规则键名

        var names = new AppSettings();
        names.Global.BaseRemaps.Add(new KeyRemap { From = "capslock", To = "esc" });
        var nA = DeviceProfiles.EnsureFor(names, msA);
        nA.Remaps.Add(new KeyRemap { From = "mouseX2", To = "mouseMiddle" });
        nA.EnsureLayer(1).Add(new KeyBinding { Combo = "caps_q", Action = "qbar" });

        var ruleKeys = DeviceProfiles.RuleKeyNames(names);
        Check("规则键名：含全局改键源键", ruleKeys.Contains("capslock"));
        Check("规则键名：含设备改键源键（鼠标）", ruleKeys.Contains("mouseX2"));
        Check("规则键名：含层绑定组合键的末段", ruleKeys.Contains("q"));

        // ------------------------------------------------- ⑨ 老 KeyRemap.Device 迁移

        /* 4.20.7 之前 Device 写了也不生效；现在真按设备分流，历史写法必须归一到
         * 运行时键，否则老规则会从"到处生效"变成"到处都不生效"。 */
        var mig = new AppSettings();
        mig.Global.BaseRemaps.Add(new KeyRemap
        {
            From = "capslock", To = "esc",
            Device = @"\\?\HID#VID_1532&PID_00A4&MI_00#8&2f4d5c8&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}",
        });
        mig.Global.BaseRemaps.Add(new KeyRemap { From = "a", To = "b", Device = "ms:vid_046d&pid_c52b" });
        mig.Global.BaseRemaps.Add(new KeyRemap { From = "c", To = "d", Device = "VID_1234&PID_5678" });

        DeviceProfiles.NormalizeLegacyDeviceKeys(mig);

        Check("迁移：完整路径收敛成 VID/PID",
            mig.Global.BaseRemaps[0].Device == "VID_1532&PID_00A4", mig.Global.BaseRemaps[0].Device);
        Check("迁移：鼠标键统一前缀与大小写",
            mig.Global.BaseRemaps[1].Device == "MS:VID_046D&PID_C52B", mig.Global.BaseRemaps[1].Device);
        Check("迁移：已经是设备键的原样保留（大小写归一）",
            mig.Global.BaseRemaps[2].Device == "VID_1234&PID_5678", mig.Global.BaseRemaps[2].Device);

        var migTable = KeyRemapMap.BuildTable(mig);
        Check("迁移后的老规则进设备表、不进全局表",
            !migTable.All.ContainsKey(0x14)
            && migTable.TryGet(0x14, "VID_1532&PID_00A4", out int legTarget) && legTarget == 0x1B,
            migTable.All.Count.ToString());
        Check("迁移后的老规则不在别的键盘上生效",
            !migTable.TryGet(0x14, "VID_0000&PID_0000", out _));

        /* 真正读盘时会走 SettingsStore 的 JsonOptions（属性名大小写不敏感、
         * 不序列化字段）。这里用一份"老设置文件"原文过一遍，确认反序列化
         * 本身不会抛 —— 抛了上层会把它备份成 .broken 再回默认值，
         * 用户看到的就是"我的配置全没了"。 */
        const string legacyJson = """
        {
          "SchemaVersion": 1,
          "Global": {
            "DeviceScopeAll": false,
            "BaseRemaps": [
              { "From": "capslock", "To": "esc", "Device": "\\\\?\\HID#VID_1532&PID_00A4&MI_00#8&2f4d5c8&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}" }
            ]
          },
          "Profiles": [
            { "Id": "p2", "Inherit": false, "Active": false,
              "Devices": [ "vid_1234&pid_5678" ], "Layers": [], "Remaps": [ { "From": "c", "To": "d" } ] }
          ]
        }
        """;

        AppSettings? fromJson;
        try
        {
            fromJson = JsonSerializer.Deserialize<AppSettings>(legacyJson, SettingsStore.JsonOptions);
        }
        catch (Exception ex)
        {
            fromJson = null;
            Check("老设置 JSON：能反序列化", false, ex.GetType().Name + "：" + ex.Message);
        }

        if (fromJson != null)
        {
            DeviceProfiles.Normalize(fromJson);
            DeviceProfiles.NormalizeLegacyDeviceKeys(fromJson);
            Check("老设置 JSON：规则读进来了", fromJson.Global.BaseRemaps.Count == 1);
            Check("老设置 JSON：全部生效开关保留", !fromJson.Global.DeviceScopeAll);
            Check("老设置 JSON：路径被归一",
                fromJson.Global.BaseRemaps[0].Device == "VID_1532&PID_00A4",
                fromJson.Global.BaseRemaps[0].Device);
            Check("老设置 JSON：设备方案读进来了", fromJson.Profiles.Count == 1);
            Check("老设置 JSON：方案里的设备 key 大小写归一",
                fromJson.Profiles[0].Devices[0] == "VID_1234&PID_5678",
                fromJson.Profiles[0].Devices.Count > 0 ? fromJson.Profiles[0].Devices[0] : "(空)");
        }
    }

    /// <summary>
    /// 设备备注 + 基础层 Q/R 的默认值补齐。
    ///
    /// 这两件事都是"用户看不见就以为坏了"的类型：
    ///   · 备注写不进设置 → 重启之后名字又变回「HID Keyboard Device」；
    ///   · Q/R 的默认值只在新建配置时生效 → 老用户升级上来 Caps+Q 还是没反应。
    /// 所以两边都要有断言，而且都要覆盖 JSON 往返。
    /// </summary>
    private static void TestDeviceAliasAndDefaults()
    {
        const string kbA = "VID_1532&PID_00A4";
        const string msA = "MS:VID_046D&PID_C52B";

        var s = new AppSettings { Global = new GlobalOptions() };

        // ---------------------------------------------------------------- ① 备注写入
        DeviceProfiles.SetAlias(s, " vid_1532&pid_00a4 ", "左手键盘");
        Check("备注：下标归一后存进设置", s.Global.DeviceAliases.ContainsKey(kbA));
        CheckEqual("备注：查询不区分大小写", InputDevices.AliasOf("vid_1532&PID_00A4"), "左手键盘");
        CheckEqual("备注：界面名字用备注", InputDevices.DisplayNameOf(kbA), "左手键盘");
        CheckEqual("备注：状态行短名也用备注", InputDevices.ShortNameOf(kbA), "左手键盘");

        // ② 鼠标备注：MS: 前缀归一
        DeviceProfiles.SetAlias(s, "ms:vid_046d&pid_c52b", "桌面鼠标");
        Check("备注：鼠标 key 归一带前缀", s.Global.DeviceAliases.ContainsKey(msA));
        CheckEqual("备注：鼠标显示名", InputDevices.DisplayNameOf(msA), "桌面鼠标");

        // ③ 清空 = 回到系统名
        DeviceProfiles.SetAlias(s, kbA, "   ");
        Check("备注：留空即删除", !s.Global.DeviceAliases.ContainsKey(kbA));
        Check("备注：清空后不再返回备注", InputDevices.AliasOf(kbA).Length == 0);

        // ④ 存盘往返：备注必须跟着配置走（同步、换机、导入导出都靠它）
        string json = JsonSerializer.Serialize(s, SettingsStore.JsonOptions);
        var back = JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions);
        Check("备注：JSON 能读回来", back != null);
        if (back != null)
        {
            DeviceProfiles.Normalize(back);
            CheckEqual("备注：JSON 往返后还在", InputDevices.AliasOf(msA), "桌面鼠标");
        }

        // ---------------------------------------------------------------- ⑤ 基础层默认值
        var d = new AppSettings { Global = new GlobalOptions() };
        d.Layers.Add(new LayerOptions { Id = 1, Name = "基础层" });
        d.Layers[0].Keys.Add(new KeyBinding { Combo = "caps_q", Action = "doNothing" });

        SettingsStore.Normalize(d);
        var l1 = d.LayerKeys(1);
        CheckEqual("默认值：Caps+Q 补成 Qbar",
            l1.FirstOrDefault(b => b.Combo == "caps_q")?.Action, "qbar");
        CheckEqual("默认值：Caps+R 补成 Delete",
            l1.FirstOrDefault(b => b.Combo == "caps_r")?.Action, "delete");
        Check("默认值：迁移标记已置位", d.Global.BaseLayerDefaultsFixed);

        /* 用户自己改成 copy 之后，再加载一次不能被迁移改回 delete ——
         * 这就是"一次性迁移"的意义：它只补洞，不吃设置。 */
        var r = l1.FirstOrDefault(b => b.Combo == "caps_r");
        Check("默认值：找到 Caps+R 那条", r != null);
        if (r != null) r.Action = "copy";
        SettingsStore.Normalize(d);
        CheckEqual("默认值：用户改过的 Caps+R 不动",
            d.LayerKeys(1).FirstOrDefault(b => b.Combo == "caps_r")?.Action, "copy");

        // 备注镜像会跟着这份设置走，测完清掉，别影响后面的用例
        InputDevices.SetAliases(null);

        TestDeviceTree();
    }

    /// <summary>
    /// 设备归并的纯函数部分：实例路径解析、驱动默认名识别、厂商名。
    /// 这几条决定了列表里到底显示「HID Keyboard Device」还是「Razer Mouse Dock Pro」。
    /// </summary>
    private static void TestDeviceTree()
    {
        CheckEqual("设备树：从 Raw Input 路径取实例 ID",
            DeviceTree.InstanceOf(@"\\?\HID#VID_046D&PID_C52B&MI_00#8&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"),
            @"HID\VID_046D&PID_C52B&MI_00\8&1&0&0000");

        Check("设备树：驱动默认名算通用", DeviceTree.IsGeneric("HID Keyboard Device"));
        Check("设备树：产品名不算通用", !DeviceTree.IsGeneric("Razer Mouse Dock Pro"));
        Check("设备树：USB Composite Device 算通用", DeviceTree.IsGeneric("USB Composite Device"));

        CheckEqual("设备树：Apple 厂商", DeviceTree.VendorOf("MS:VID_05AC&PID_0265"), "Apple");
        CheckEqual("设备树：罗技厂商（大小写无关）", DeviceTree.VendorOf("ms:vid_046d&pid_c52b"), "罗技");

        /* 一只物理设备只占一行：键必须唯一，否则勾选/方案会串到两行上。 */
        var all = InputDevices.All();
        var keys = all.Select(d => d.Key).ToList();
        CheckEqual("设备归并：键唯一", keys.Distinct(StringComparer.OrdinalIgnoreCase).Count(), keys.Count);

        Check("设备归并：ROOT 设备算虚拟",
            InputDevices.IsVirtual(new InputDeviceInfo(@"ROOT\FEIZHI_VIRTUAL_KEYBOARD", "Feizhi", "虚拟键盘", false)));
        Check("设备归并：真设备不算虚拟",
            !InputDevices.IsVirtual(new InputDeviceInfo("VID_1532&PID_00A4", "Razer", "外接键盘", false)));
    }
}
