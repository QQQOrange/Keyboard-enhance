using System;
using System.Collections.Generic;
using System.Linq;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus;

/// <summary>
/// 「原键替换层」的自检：不用触发键的绑定。
///
/// 用户的原话是"把原来需要触发键才能触发的功能，现在变成不用触发键按了也能触发"。
/// 这一组盯的是这句话最容易走样的几个地方：
///   · 配了但不生效（组合键拼错、没进设备判定、被层语义挡住）；
///   · 生效过头（没配过的键也被吞，打字全乱）；
///   · 设备方案里配的替换跑到别的键盘/鼠标上（这是"按设备分别设置"的核心承诺）；
///   · 抬起没结账（前台收到一个从未按下过的 up，游戏里表现为"键卡住"）。
/// </summary>
public static partial class SelfTest
{
    private static void TestReplacementLayer()
    {
        const string kbA = "VID_1532&PID_00A4";
        const string kbB = "VID_0483&PID_2031";
        const string msA = "MS:VID_046D&PID_C52B";

        // ---------------------------------------------------------------- ① 解析

        var s = new AppSettings();
        s.Replacements.Add(new KeyBinding { Combo = "caps_space", Action = "customKey", Arg = "1" });

        Check("原键替换：全局替换层能查到",
            DeviceProfiles.FindGlobalReplacement(s, "caps_space") != null);
        Check("原键替换：组合键大小写不敏感",
            DeviceProfiles.FindGlobalReplacement(s, "CAPS_SPACE") != null);
        Check("原键替换：没配过的键查不到",
            DeviceProfiles.FindGlobalReplacement(s, "caps_press_caps") == null);

        var hit = DeviceProfiles.ResolveDeviceReplacement(s, "caps_space", kbA, out bool blocked);
        Check("原键替换：设备没配过时不拦截（交给全局兜底）", hit == null && !blocked);
        Check("原键替换：兜底用的就是全局那一份",
            !blocked && DeviceProfiles.FindGlobalReplacement(s, "caps_space")?.Action == "customKey");

        /* 设备方案自己配了 → 用设备那份，而不是全局那份。 */
        var ownA = DeviceProfiles.EnsureFor(s, kbA);
        ownA.Replacements.Add(new KeyBinding { Combo = "caps_space", Action = "typeText", Arg = "A" });

        hit = DeviceProfiles.ResolveDeviceReplacement(s, "caps_space", kbA, out blocked);
        Check("原键替换：设备方案优先于全局", hit?.Action == "typeText");

        hit = DeviceProfiles.ResolveDeviceReplacement(s, "caps_space", kbB, out blocked);
        Check("原键替换：另一台设备不拦截、仍用全局那份",
            hit == null && !blocked && DeviceProfiles.FindGlobalReplacement(s, "caps_space") != null);

        /* 独立方案：设备上没配 → 到此为止，不许回退全局。 */
        ownA.Replacements.Clear();
        ownA.Inherit = false;
        hit = DeviceProfiles.ResolveDeviceReplacement(s, "caps_space", kbA, out blocked);
        Check("原键替换：独立方案不回退全局", hit == null && blocked);

        ownA.Inherit = true;

        /* doNothing 不再等于"在这台设备上把这个键清掉"：它表示"没有规则"，
         * 所以仍然会回退到通用设置 / 全局那一份（规则要清掉就直接删条目）。 */
        ownA.Replacements.Add(new KeyBinding { Combo = "caps_space", Action = "doNothing" });
        hit = DeviceProfiles.ResolveDeviceReplacement(s, "caps_space", kbA, out blocked);
        Check("原键替换：doNothing 不算规则，不会挡住通用设置", hit == null && !blocked);
        ownA.Replacements.Clear();

        Check("原键替换：编辑路径拿到全局那一份",
            ReferenceEquals(DeviceProfiles.ReplacementsFor(s, ""), s.Replacements));
        Check("原键替换：编辑某台设备会按需建方案",
            ReferenceEquals(DeviceProfiles.ReplacementsFor(s, kbB),
                            DeviceProfiles.EnsureFor(s, kbB).Replacements));

        /* 设备方案里只配了替换、没配改键，也照样要问设备 ——
         * 漏了这一步，鼠标侧键、空格这类"没在改键表里"的键就永远按不到设备方案。 */
        DeviceProfiles.EnsureFor(s, kbB).Replacements
            .Add(new KeyBinding { Combo = "caps_f1", Action = "customKey", Arg = "f2" });

        Check("原键替换：设备方案里有替换就要问设备",
            DeviceProfiles.HasDeviceReplacements(s));
        Check("原键替换：替换层的键名进 ScopedKeyNames",
            DeviceProfiles.ScopedKeyNames(s).Contains("f1"));

        // ---------------------------------------------------------------- ② 运行时

        var ms = new AppSettings();
        ms.Global.BaseRemaps.Clear();
        foreach (var t in ms.Triggers) t.Enabled = false;
        ms.Replacements.Add(new KeyBinding { Combo = "caps_space", Action = "customKey", Arg = "1" });
        ms.Replacements.Add(new KeyBinding { Combo = "caps_mouseMiddle", Action = "customKey", Arg = "ctrl_c" });
        ms.Replacements.Add(new KeyBinding { Combo = "caps_f9", Action = "doNothing" });

        var fired = new List<string>();
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => ms, (action, arg) =>
        {
            lock (fired) fired.Add($"{action}|{arg}");
            return true;
        });

        Check("原键替换：空格按下被接管", engine.SimulateKey(0x20, true));
        Check("原键替换：空格抬起被接管", engine.SimulateKey(0x20, false));
        WaitUntil(() => { lock (fired) return fired.Count >= 1; });
        lock (fired) CheckEqual("原键替换：按下执行了绑定的动作",
            fired.FirstOrDefault() ?? "", "customKey|1");

        /* 没配过的键必须原样放行 —— 这条一旦坏了，用户整块键盘都打不出字。 */
        Check("原键替换：没配过的键放行", !engine.SimulateKey(0x51, true));
        engine.SimulateKey(0x51, false);

        /* doNothing 不再吞键：它表示"这里没有规则"，按键必须原样传下去。
         * （历史事故：界面落过一条 doNothing，用户的鼠标中键就再也到不了 wGesture。） */
        lock (fired) fired.Clear();
        Check("原键替换：doNothing 不吞键（放行给前台程序）", !engine.SimulateKey(0x78, true));
        Check("原键替换：doNothing 抬起也放行", !engine.SimulateKey(0x78, false));
        WaitUntil(() => { lock (fired) return fired.Count > 0; }, 120);
        lock (fired) Check("原键替换：doNothing 不执行动作", fired.Count == 0);

        /* 「禁用这个键」才是"吞掉但什么都不做"的显式表达。 */
        ms.Replacements.Add(new KeyBinding { Combo = "caps_f10", Action = "swallowKey" });
        lock (fired) fired.Clear();
        Check("原键替换：禁用动作吞键", engine.SimulateKey(0x79, true));
        Check("原键替换：禁用动作抬起也吞", engine.SimulateKey(0x79, false));
        WaitUntil(() => { lock (fired) return fired.Count > 0; }, 120);
        lock (fired) Check("原键替换：禁用动作不执行任何东西", fired.Count == 0);
        ms.Replacements.RemoveAll(b => b.Combo == "caps_f10");

        /* 鼠标键：中键 → 一组快捷键，与键盘键走同一段代码。 */
        lock (fired) fired.Clear();
        Check("原键替换：鼠标中键被接管", engine.SimulateKey(0x04, true));
        Check("原键替换：鼠标中键抬起被接管", engine.SimulateKey(0x04, false));
        WaitUntil(() => { lock (fired) return fired.Count >= 1; });
        lock (fired) CheckEqual("原键替换：鼠标中键执行了绑定的动作",
            fired.FirstOrDefault() ?? "", "customKey|ctrl_c");

        engine.Dispose();
        queue.Dispose();

        // ---------------------------------------------------------------- ③ 按设备分开

        var ds = new AppSettings();
        ds.Global.BaseRemaps.Clear();
        foreach (var t in ds.Triggers) t.Enabled = false;
        DeviceProfiles.EnsureFor(ds, kbA).Replacements
            .Add(new KeyBinding { Combo = "caps_space", Action = "customKey", Arg = "1" });

        var hitList = new List<string>();
        var dq = new ActionQueue();
        var dEngine = new HotkeyEngine(dq, () => ds, (action, arg) =>
        {
            lock (hitList) hitList.Add($"{action}|{arg}");
            return true;
        })
        {
            DeviceResolver = (_, _) => kbA,
        };

        Check("原键替换：配了替换的那台键盘生效", dEngine.SimulateKey(0x20, true));
        dEngine.SimulateKey(0x20, false);
        WaitUntil(() => { lock (hitList) return hitList.Count >= 1; });
        lock (hitList) CheckEqual("原键替换：动作来自设备方案",
            hitList.FirstOrDefault() ?? "", "customKey|1");

        /* 换一只键盘：这条规则不该跟过去（设备方案的意义所在）。 */
        dEngine.DeviceResolver = (_, _) => kbB;
        lock (hitList) hitList.Clear();
        Check("原键替换：别的键盘上空格照常输入", !dEngine.SimulateKey(0x20, true));
        dEngine.SimulateKey(0x20, false);
        WaitUntil(() => { lock (hitList) return hitList.Count > 0; }, 120);
        lock (hitList) Check("原键替换：别的键盘上不执行动作", hitList.Count == 0);

        /* 鼠标设备键同理：同一个键名、不同设备互不影响。 */
        DeviceProfiles.EnsureFor(ds, msA).Replacements
            .Add(new KeyBinding { Combo = "caps_mouseX1", Action = "customKey", Arg = "alt_tab" });

        dEngine.DeviceResolver = (_, _) => msA;
        lock (hitList) hitList.Clear();
        Check("原键替换：鼠标方案里侧键生效", dEngine.SimulateKey(0x05, true));
        dEngine.SimulateKey(0x05, false);
        WaitUntil(() => { lock (hitList) return hitList.Count >= 1; });
        lock (hitList) CheckEqual("原键替换：鼠标侧键执行了设备方案的动作",
            hitList.FirstOrDefault() ?? "", "customKey|alt_tab");

        dEngine.DeviceResolver = (_, _) => "MS:VID_1234&PID_5678";
        lock (hitList) hitList.Clear();
        Check("原键替换：另一只鼠标上侧键放行", !dEngine.SimulateKey(0x05, true));
        dEngine.SimulateKey(0x05, false);

        dEngine.Dispose();
        dq.Dispose();

        // ---------------------------------------------------------------- ④ 存盘往返

        var saved = new AppSettings();
        saved.Replacements.Add(new KeyBinding { Combo = "caps_space", Action = "customKey", Arg = "1" });
        DeviceProfiles.EnsureFor(saved, kbA).Replacements
            .Add(new KeyBinding { Combo = "caps_mouseMiddle", Action = "typeText", Arg = "hello" });

        string json = System.Text.Json.JsonSerializer.Serialize(saved, SettingsStore.JsonOptions);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions);
        Check("原键替换：存盘后全局条目还在",
            back?.Replacements.FirstOrDefault()?.Combo == "caps_space", json.Length.ToString());
        Check("原键替换：存盘后设备条目还在",
            back?.Profiles.FirstOrDefault(p => p.Devices.Contains(kbA))?.Replacements.Count == 1);

        // ---------------------------------------------------------------- ⑤ 归一化

        /* 老配置里"没有动作"的替换条目会被清掉（它们曾把键锁死），
         * 而明确的「禁用这个键」要留着。 */
        var dirty = new AppSettings();
        dirty.Replacements.Add(new KeyBinding { Combo = "caps_space", Action = "customKey", Arg = "1" });
        dirty.Replacements.Add(new KeyBinding { Combo = "caps_mouseMiddle", Action = "doNothing" });
        dirty.Replacements.Add(new KeyBinding { Combo = "caps_mouseX1" });
        dirty.Replacements.Add(new KeyBinding { Combo = "caps_mouseX2", Action = "swallowKey" });
        DeviceProfiles.EnsureFor(dirty, kbA).Replacements
            .Add(new KeyBinding { Combo = "caps_f8", Action = "doNothing" });

        DeviceProfiles.Normalize(dirty);

        CheckEqual("原键替换：归一后只剩真规则", "caps_space,caps_mouseX2",
            string.Join(",", dirty.Replacements.Select(b => b.Combo)));
        Check("原键替换：设备方案里的空条目也被清掉",
            DeviceProfiles.ForDevice(dirty, kbA)?.Replacements.Count == 0);
        Check("原键替换：禁用动作算真规则（不会误删）",
            dirty.Replacements.Any(b => b.Action == "swallowKey"));
    }
}
