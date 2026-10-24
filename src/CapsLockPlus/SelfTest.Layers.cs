using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Icons;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Interop;
using CapsLockPlus.Core.Qbar;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Text;
using CapsLockPlus.Core.Update;
using CapsLockPlus.Core.Windows;

namespace CapsLockPlus;

/// <summary>
/// 自检：不依赖界面，验证核心逻辑。
/// 用 `KeyboardEnhance.exe --selftest` 运行，退出码 0 表示全部通过。
/// </summary>
public static partial class SelfTest
{

    /* 层、触发键、按住判定、连续量重复。
     * 与 SelfTest.cs 同属 partial 类。 */

    /// <summary>
    /// 键盘层：默认 10 层、默认触发键 = CapsLock 绑第 1 层，
    /// 且老设置的 Keys 会被当作第 1 层（保证升级后行为不变）。
    /// </summary>
    private static void TestLayers()
    {
        var s = DefaultSettings.Create();
        Check("默认 10 个层", s.Layers.Count == 10, s.Layers.Count.ToString());
        Check("层 ID 从 1 到 10",
            s.Layers.Select(l => l.Id).SequenceEqual(Enumerable.Range(1, 10)));
        Check("第 1 层名为基础层", s.Layers[0].Name == "基础层");
        Check("默认 1 个触发键", s.Triggers.Count == 1);
        Check("默认触发键是 CapsLock", s.Triggers[0].Key == "capslock");
        Check("默认触发键绑第 1 层", s.Triggers[0].LayerId == 1);
        Check("默认长按阈值 200ms", s.Triggers[0].HoldMs == 200);
        Check("默认短按切换大小写", s.Triggers[0].TapAction == "toggleCapsLock");

        // 老设置（没有 Triggers/Layers）Normalize 之后要补齐，且 Keys 变第 1 层
        var legacy = new AppSettings();
        legacy.Keys.Add(new KeyBinding { Combo = "caps_q", Action = "qbar" });
        SettingsStore.Normalize(legacy);
        Check("老设置补齐出 10 层", legacy.Layers.Count == 10);
        Check("老设置补齐出 1 个触发键", legacy.Triggers.Count == 1);
        Check("老设置的 Keys 成为第 1 层",
            legacy.Layers.First(l => l.Id == 1).Keys.Any(k => k.Combo == "caps_q"));
        Check("LayerKeys(1) 能取到老绑定",
            legacy.LayerKeys(1).Any(k => k.Combo == "caps_q"));
    }

    /// <summary>
    /// 按住触发键、触发一个功能、**停下来几秒**、再按另一个键 ——
    /// 第二个键必须仍然是层内绑定。
    ///
    /// 这是用户报的问题（v4.5.4 修）：原来的"抬起事件丢失自愈"每 20ms
    /// 读一次 GetAsyncKeyState，而触发键的按下被我们自己的钩子吞掉了，
    /// 系统层面它从未被按下 —— 于是只要停顿超过 1 秒就被判成"已松开"，
    /// 层被踢掉，后续按键全部变回原生功能。
    ///
    /// 断言方式：模拟 3 秒空闲后，层必须还在，且再按键仍然被吞、仍然触发动作。
    /// </summary>
    private static void TestLayerSurvivesIdle()
    {
        var settings = DefaultSettings.Create();
        foreach (var t in settings.Triggers) t.HoldMs = 120;

        var triggered = new List<string>();
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => settings, (action, arg) =>
        {
            lock (triggered) triggered.Add(action);
            return true;
        });
        engine.StartHoldTimer();

        engine.SimulateKey(VK.CapsLock, true);
        DoEvents(260);
        CheckEqual("空闲测试：已进入第 1 层", engine.ActiveLayer, 1);

        // 触发第一个功能（caps_q）
        engine.SimulateKey(0x51 /*Q*/, true);
        engine.SimulateKey(0x51, false);
        DoEvents(100);

        /* 用户停下来看结果：触发键还按着，但三秒没碰其它键。
         * 把时间往前拨，再跑两次 tick —— 老代码在这里会复位状态机。 */
        engine.SimulateIdleForTest(3000);
        engine.CheckHoldThresholdForTest();
        engine.CheckHoldThresholdForTest();
        DoEvents(80);

        Check("停顿 3 秒后层还在", engine.ActiveLayer == 1, $"ActiveLayer={engine.ActiveLayer}");
        Check("停顿后仍视为按住触发键", engine.IsTriggerHeld);

        // 再按另一个键：必须仍然走层内绑定
        int before;
        lock (triggered) before = triggered.Count;

        bool swallowed = engine.SimulateKey(0x53 /*S*/, true);   // caps_s
        engine.SimulateKey(0x53, false);
        DoEvents(120);

        Check("停顿后再按键仍被层吞掉", swallowed);
        lock (triggered)
        {
            Check("停顿后仍触发层内动作", triggered.Count > before,
                string.Join(",", triggered));
        }

        engine.SimulateKey(VK.CapsLock, false);
        CheckEqual("松开触发键后退出层", engine.ActiveLayer, -1);
        engine.Dispose();
    }

    /// <summary>
    /// **真实的长按路径**：走计时器，而不是 EnterLayerImmediatelyForTest。
    ///
    /// 之前的自检用 EnterLayerImmediatelyForTest 直接跳过了计时器，
    /// 等于"短按保留原功能、长按进层"这条最核心的链路从来没被验证过。
    /// 这里真的等时间流逝，并泵消息循环让 DispatcherTimer 触发。
    /// </summary>
    private static void TestHoldThreshold()
    {
        var settings = DefaultSettings.Create();
        // 阈值设成 120ms，测试跑得快一点
        foreach (var t in settings.Triggers) t.HoldMs = 120;

        var triggered = new List<string>();
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => settings, (action, arg) =>
        {
            lock (triggered) triggered.Add(action);
            return true;
        });
        // 不装钩子，只启计时器 —— 真实的长按判定就走这条
        engine.StartHoldTimer();

        // ① 按下后立刻检查：还没到阈值，不应该进层
        engine.SimulateKey(VK.CapsLock, true);
        engine.CheckHoldThresholdForTest();
        CheckEqual("刚按下时还没进层", engine.ActiveLayer, -1);
        Check("按下时触发键保持按住状态", engine.IsTriggerHeld);

        // ② 等过阈值，让计时器真的跑起来
        DoEvents(260);
        CheckEqual("超过阈值后进入第 1 层", engine.ActiveLayer, 1);
        CheckEqual("激活的是 CapsLock", engine.ActiveTriggerKey, "capslock");

        // ③ 层模式下按键应该被吞掉并触发动作
        bool swallowed = engine.SimulateKey(0x53 /*S*/, true);   // caps_s = 左移
        Check("层模式下已绑定的键被吞掉", swallowed);
        engine.SimulateKey(0x53, false);
        DoEvents(120);
        lock (triggered)
        {
            Check("层模式下触发了绑定动作", triggered.Count > 0,
                string.Join(",", triggered));
        }

        // ④ 松开触发键后退出层
        engine.SimulateKey(VK.CapsLock, false);
        CheckEqual("松开后退出层", engine.ActiveLayer, -1);
        Check("松开后不再处于按住状态", !engine.IsTriggerHeld);

        engine.Dispose();
        queue.Dispose();

        // ⑤ 短按（没到阈值就松开）必须保留原功能，且不进层
        var s2 = DefaultSettings.Create();
        foreach (var t in s2.Triggers) t.HoldMs = 400;
        var tapActions = new List<string>();
        var q2 = new ActionQueue();
        var e2 = new HotkeyEngine(q2, () => s2, (action, arg) =>
        {
            lock (tapActions) tapActions.Add(action);
            return true;
        });
        e2.StartHoldTimer();

        e2.SimulateKey(VK.CapsLock, true);
        Thread.Sleep(60);                       // 远小于 400ms
        bool swallowUp = e2.SimulateKey(VK.CapsLock, false);
        DoEvents(160);

        Check("短按时不进层", e2.ActiveLayer == -1);
        // 新语义：按下和抬起都被吞掉，但短按时会**补发一次真实按键**，
        // 所以原功能照样生效（且不会污染系统按键状态）。
        Check("短按时抬起也被吞掉（由引擎补发原键）", swallowUp);

        e2.Dispose();
        q2.Dispose();
    }

    /// <summary>
    /// **修饰键当触发键**：右 Alt / 右 Ctrl 按住时的自动重复不能把自己
    /// 算成"按住的修饰键"，否则层内绑定全部查不到。
    ///
    /// 用户报的现象：把右 Alt 设成触发键绑第 3 层，层里配了自定义键，
    /// 按右 Alt 再按那个键 —— 一点反应都没有，按键还是原功能。
    ///
    /// 根因：按住右 Alt 超过约 500ms 后系统开始发自动重复的 down，
    /// 每个 down 都会把 `_laltDown` 置 true；而 `OnTriggerDown` 对重复
    /// 事件早退（不清状态）。于是层内按 Q 拼出来的是 `caps_lalt_q`，
    /// 用户绑的却是 `caps_q` —— 查不到就放行，看起来就是"完全没生效"。
    /// 按住越久越必然复现，正好对应"有时候行有时候不行"。
    /// </summary>
    /// <summary>
    /// 按住音量键要"一直涨、一点一点涨"。
    ///
    /// 这条是用户直接提的：以前按一下只走一步，按住也不会连续，
    /// 想让音量从 20% 到 80% 得按 30 次。
    ///
    /// 关键点：
    ///   · 节奏由引擎自己掌握（380ms 起播、45ms 一步），
    ///     不依赖系统键盘的自动重复 —— 那个可以设成 2.5 次/秒甚至关掉；
    ///   · 松开必须**立刻**停，晚一步就是"手松了音量还在涨"。
    /// </summary>
    private static void TestContinuousVolumeRepeat()
    {
        var s = DefaultSettings.Create();
        foreach (var t in s.Triggers) t.Enabled = false;
        s.Triggers.Add(new TriggerKey
        {
            Key = "capslock",
            LayerId = 1,
            HoldMs = 60,
            Enabled = true,
        });

        var layer1 = s.Layers.First(l => l.Id == 1);
        layer1.Keys.RemoveAll(b => b.Combo == "caps_up");
        layer1.Keys.Add(new KeyBinding { Combo = "caps_up", Action = "volumeUp" });

        var fired = new List<string>();
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => s, (a, arg) =>
        {
            lock (fired) fired.Add(a);
            return true;
        });
        engine.StartHoldTimer();
        engine.EnterLayerImmediatelyForTest = true;

        int Count()
        {
            lock (fired) return fired.Count(x => x == "volumeUp");
        }

        engine.SimulateKey(VK.CapsLock, true);
        CheckEqual("按住触发键进第 1 层", engine.ActiveLayer, 1);

        // ---- 短按：只执行一次 ----
        engine.SimulateKey(VK.Up, true);
        DoEvents(70);
        int afterTap = Count();
        CheckEqual("短按音量+ 只执行一次", afterTap, 1);

        // ---- 按住不动：起播之后开始连续 ----
        DoEvents(560);
        int afterHold = Count();
        Check("按住约半秒后开始连续执行", afterHold >= afterTap + 2,
            $"{afterTap} → {afterHold}");

        // ---- 继续按住：一直在涨（线性累加，不是只跳一次） ----
        DoEvents(220);
        int later = Count();
        Check("按住期间一直在执行", later > afterHold, $"{afterHold} → {later}");

        // ---- 系统自动重复不能叠加（否则速率翻倍、且随键盘设置变） ----
        for (int i = 0; i < 5; i++) engine.SimulateKey(VK.Up, true);
        DoEvents(40);
        int afterSysRepeat = Count();
        Check("系统自动重复被吞掉（不会双倍速率）", afterSysRepeat <= later + 2,
            $"{later} → {afterSysRepeat}");

        // ---- 松开：立刻停 ----
        engine.SimulateKey(VK.Up, false);
        DoEvents(110);
        int atRelease = Count();
        DoEvents(320);
        int afterRelease = Count();
        CheckEqual("松开后立刻停止", afterRelease, atRelease);

        engine.SimulateKey(VK.CapsLock, false);
        engine.Dispose();
    }

    /// <summary>
    /// 连按层内按键时，**绝不能**因为物理读数抖动把层复位。
    ///
    /// 这是 v4.2.1 修的那个"第一次有效、第二次变回原生功能"：
    /// 用户按住右 Alt 进层、按 Up 调音量，注入媒体键后
    /// `GetAsyncKeyState(触发键)` 会短暂读不到按下，
    /// 自愈逻辑连续 3 轮（60ms）就判"抬起事件丢了"→ 复位 → 层被踢掉。
    ///
    /// 现在的规则：只要用户还在按键（或刚注入过），就不做自愈判定。
    /// </summary>
    private static void TestRepeatSurvivesPhysicalJitter()
    {
        var s = DefaultSettings.Create();
        foreach (var t in s.Triggers) t.Enabled = false;
        s.Triggers.Add(new TriggerKey
        {
            Key = "ralt",
            LayerId = 1,
            HoldMs = 60,
            Enabled = true,
        });

        var layer1 = s.Layers.First(l => l.Id == 1);
        layer1.Keys.RemoveAll(b => b.Combo == "caps_up");
        layer1.Keys.Add(new KeyBinding { Combo = "caps_up", Action = "volumeUp" });

        var fired = new List<string>();
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => s, (a, arg) =>
        {
            lock (fired) fired.Add(a);
            return true;
        });
        engine.StartHoldTimer();
        engine.EnterLayerImmediatelyForTest = true;

        // 触发键物理按下 → 之后模拟"读数抖成没按下"
        bool triggerDown = true;
        bool upDown = false;      // 层内的上箭头究竟按着没有
        engine.PhysicalDownProbe = vk => vk switch
        {
            0xA4 or 0xA5 or 0x12 => triggerDown,
            0x26 => upDown,
            _ => false,
        };

        engine.SimulateKey(VK.RMenu, true);
        CheckEqual("右 Alt 进第 1 层", engine.ActiveLayer, 1);

        // 用户开始连按：物理读数立刻"抖成没按下"
        triggerDown = false;
        for (int i = 0; i < 8; i++)
        {
            engine.SimulateKey(VK.Up, true);      // 持续按键活动
            engine.SimulateKey(VK.Up, false);
            engine.CheckHoldThresholdForTest();
            DoEvents(15);
        }

        Check("连按期间物理读数抖动不会把层复位", engine.IsTriggerHeld);
        CheckEqual("层还在", engine.ActiveLayer, 1);

        // 用户继续按第二下 —— 这一下以前会变成"原生上箭头"
        engine.SimulateKey(VK.Up, true);
        DoEvents(90);
        int times;
        lock (fired) times = fired.Count(x => x == "volumeUp");
        Check("第二次按键仍然执行绑定动作", times >= 2, $"执行了 {times} 次");

        engine.SimulateKey(VK.Up, false);

        /* 长按连续调节期间同样不能被自愈打断：
         * 连续重复本身就是"用户还按着"的证据，它会持续刷新活动时间。 */
        triggerDown = false;
        upDown = true;                             // 上箭头真按着
        engine.SimulateKey(VK.Up, true);           // 按住不放
        DoEvents(700);                             // 跨过 400ms 静默门槛
        for (int i = 0; i < 10; i++) { engine.CheckHoldThresholdForTest(); DoEvents(6); }
        Check("长按连续调节期间不会被自愈打断", engine.IsTriggerHeld);
        upDown = false;
        engine.SimulateKey(VK.Up, false);
        DoEvents(80);

        /* v4.5.4：**不再**靠 GetAsyncKeyState 猜"触发键还按着没有"。
         *
         * 触发键的按下被我们自己的钩子吞掉了（不吞 CapsLock 会切大小写），
         * 系统层面它从未被按下 —— 物理状态读出来永远是"未按下"。
         * 旧实现在静默 1 秒后就判"抬起事件丢失"，把层踢掉；
         * 用户按住触发键、触发功能、停顿几秒再按键，绑定就全失效了。
         *
         * 现在的约定：静默不复位；重新按下触发键时才复位旧会话。 */
        DoEvents(1150);
        for (int i = 0; i < 16; i++) { engine.CheckHoldThresholdForTest(); DoEvents(6); }
        Check("静默期间层保持（不再按物理状态误杀）", engine.IsTriggerHeld);
        CheckEqual("静默期间层号不变", engine.ActiveLayer, 1);

        /* 用户重新按了一次触发键 —— 上一次会话没正常结束（抬起事件丢了）。
         * 靠"两次 down 的间隔"识别：>100ms 说明中间松开过，按新会话处理。 */
        DoEvents(150);
        engine.SimulateKey(VK.CapsLock, true);
        Check("重新按下触发键后仍是按住状态", engine.IsTriggerHeld);
        DoEvents(260);
        CheckEqual("重新按下后能再次进层", engine.ActiveLayer, 1);

        engine.PhysicalDownProbe = null;
        engine.Dispose();
    }

    /// <summary>
    /// 按住亮度键的同时再按音量键 —— 两个连续量必须**各自**继续生效。
    ///
    /// v4.2.x 只有一个"连续调节槽位"：后按下的键会把前面的顶掉，
    /// 松开其中任何一个又会把整条重复清掉。用户的实际感受是
    /// "调完亮度，手还按着触发键，再按音量完全没反应"，
    /// 或者"按住音量时按一下亮度，音量就不涨了"。
    ///
    /// 现在每个键各存一份节奏，互不干扰。
    /// </summary>
    private static void TestTwoContinuousKeysIndependent()
    {
        var s = DefaultSettings.Create();
        foreach (var t in s.Triggers) t.Enabled = false;
        s.Triggers.Add(new TriggerKey
        {
            Key = "capslock",
            LayerId = 1,
            HoldMs = 60,
            Enabled = true,
        });

        var layer1 = s.Layers.First(l => l.Id == 1);
        layer1.Keys.RemoveAll(b => b.Combo is "caps_up" or "caps_down");
        layer1.Keys.Add(new KeyBinding { Combo = "caps_up", Action = "brightnessUp" });
        layer1.Keys.Add(new KeyBinding { Combo = "caps_down", Action = "volumeUp" });

        var fired = new List<string>();
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => s, (a, arg) =>
        {
            lock (fired) fired.Add(a);
            return true;
        });
        engine.StartHoldTimer();
        engine.EnterLayerImmediatelyForTest = true;

        int Count(string action)
        {
            lock (fired) return fired.Count(x => x == action);
        }

        engine.SimulateKey(VK.CapsLock, true);
        CheckEqual("按住触发键进第 1 层（双连续量）", engine.ActiveLayer, 1);

        // 先按住亮度+
        engine.SimulateKey(VK.Up, true);
        DoEvents(560);
        int bright1 = Count("brightnessUp");
        Check("按住亮度会连续执行", bright1 >= 2, $"{bright1} 次");

        // 再按住音量+（另一个键，同时按着）
        engine.SimulateKey(VK.Down, true);
        DoEvents(560);

        int vol1 = Count("volumeUp");
        int bright2 = Count("brightnessUp");
        Check("音量键自己也开始连续执行", vol1 >= 1, $"{vol1} 次");
        Check("后按的音量没有顶掉亮度", bright2 > bright1, $"{bright1} → {bright2}");

        // 松开音量：亮度必须继续
        engine.SimulateKey(VK.Down, false);
        DoEvents(120);
        int bright3 = Count("brightnessUp");
        DoEvents(320);
        int bright4 = Count("brightnessUp");
        Check("松开音量后亮度仍在继续", bright4 > bright3, $"{bright3} → {bright4}");

        int vol2 = Count("volumeUp");
        DoEvents(220);
        CheckEqual("松开音量后音量确实停了", Count("volumeUp"), vol2);

        // 松开亮度：两条都停
        engine.SimulateKey(VK.Up, false);
        DoEvents(110);
        int bright5 = Count("brightnessUp");
        DoEvents(320);
        CheckEqual("松开亮度后亮度也停了", Count("brightnessUp"), bright5);

        engine.SimulateKey(VK.CapsLock, false);
        engine.Dispose();
    }

    private static void TestModifierTriggerWithRepeat()
    {
        var s = DefaultSettings.Create();
        foreach (var t in s.Triggers) t.Enabled = false;      // 只留右 Alt，排除 CapsLock 干扰
        s.Triggers.Add(new TriggerKey
        {
            Key = "ralt",
            LayerId = 3,
            HoldMs = 100,
            TapAction = "keepKey",
            Enabled = true,
        });
        s.Layers.First(l => l.Id == 3).Keys.Add(
            new KeyBinding { Combo = "caps_q", Action = "qbar" });

        var fired = new List<string>();
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => s, (a, arg) =>
        {
            lock (fired) fired.Add(a);
            return true;
        });
        engine.StartHoldTimer();
        engine.EnterLayerImmediatelyForTest = true;   // 不依赖真实时间

        // ① 按下右 Alt → 立刻进第 3 层
        engine.SimulateKey(VK.RMenu, true);
        CheckEqual("右 Alt 作触发键进第 3 层", engine.ActiveLayer, 3);
        CheckEqual("激活的触发键是右 Alt", engine.ActiveTriggerKey, "ralt");

        // ② 自动重复：按住超过 500ms 后系统每 30~50ms 发一次 down
        for (int i = 0; i < 4; i++) engine.SimulateKey(VK.RMenu, true);

        // ③ 层内按 Q：必须命中 caps_q
        bool swallowed = engine.SimulateKey(0x51 /*Q*/, true);
        Check("右 Alt 触发键：层内 Q 被吞掉（没被拼成 caps_lalt_q）", swallowed);
        engine.SimulateKey(0x51, false);
        DoEvents(80);
        lock (fired)
            Check("右 Alt 触发键：层内 Q 执行了绑定动作", fired.Contains("qbar"),
                string.Join(",", fired));

        // ④ 松开右 Alt → 退出层
        engine.SimulateKey(VK.RMenu, false);
        CheckEqual("松开右 Alt 后退出层", engine.ActiveLayer, -1);

        /* ⑤ 有些键盘/驱动只发通用 Alt 码 0x12，也有"按下 0xA5、抬起 0x12"
         *    的混发情况 —— 触发键判定与松手判定都要认。 */
        engine.SimulateKey(0x12, true);
        CheckEqual("通用 Alt 码也能触发右 Alt 触发键", engine.ActiveLayer, 3);
        engine.SimulateKey(0x12, false);
        CheckEqual("通用 Alt 码抬起也能退出层", engine.ActiveLayer, -1);
        Check("混发后不残留按住状态", !engine.IsTriggerHeld);

        // ⑥ 右 Ctrl / 右 Shift 同理（左右专用码与通用码互认）
        s.Triggers.Clear();
        s.Triggers.Add(new TriggerKey { Key = "rctrl", LayerId = 4, HoldMs = 100, Enabled = true });
        s.Layers.First(l => l.Id == 4).Keys.Add(
            new KeyBinding { Combo = "caps_w", Action = "esc" });
        engine.SimulateKey(VK.RControl, true);
        engine.SimulateKey(VK.RControl, true);        // 自动重复
        engine.SimulateKey(VK.RControl, true);
        CheckEqual("右 Ctrl 作触发键进第 4 层", engine.ActiveLayer, 4);
        bool wSwallowed = engine.SimulateKey(0x57 /*W*/, true);
        Check("右 Ctrl 触发键：层内 W 被吞掉", wSwallowed);
        engine.SimulateKey(0x57, false);
        engine.SimulateKey(VK.RControl, false);

        engine.Dispose();
        queue.Dispose();
    }

    /// <summary>
    /// 层内子层窗口里的输入框不能把字裁掉一半。
    ///
    /// 症状：名字输入框里只看到字符的上半部分，下半部分被边框切掉。
    /// 根因是 XAML 里给 TextBox 写死了 <c>Height="34"</c> ——
    /// 内容区 = 34 − Padding(6+6) − 边框(1+1) = 20px，比一行字还矮。
    /// 现在改成只用样式里的 MinHeight，框会随字号自然长高。
    ///
    /// 这条必须真的把窗口排版出来才量得到，所以在屏幕外 Show 一下再关掉。
    /// </summary>
    private static void TestSubLayerWindowLayout()
    {
        UI.Windows.SubLayerEditorWindow? w = null;
        try
        {
            w = new UI.Windows.SubLayerEditorWindow("基础层", new List<Core.Settings.SubLayerDef>())
            {
                ShowInTaskbar = false,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            w.Show();

            var m = w.MeasureRowTextBox();
            Check("子层编辑窗口的输入框真的排版出来了", m.Box > 0, $"box={m.Box}");
            Check("子层输入框内容区放得下一行字（不会只显示上半部分）",
                m.Content >= m.Text * 1.2,
                $"内容区={m.Content:F1} 行高={m.Text:F1} 框高={m.Box:F1}");

            // 全量审计：窗口里任何一处文字被压扁都要抓出来
            string clipped = w.AuditTextClipping();
            Check("子层编辑窗口里没有文字被裁掉", clipped.Length == 0, clipped);

            /* 小屏笔记本上窗口不能高过工作区 ——
             * 620 高在 1366×768 + 125% 缩放下会超出屏幕下沿，
             * 底部按钮和最后一行被切掉，看起来就是"下半部分没显示全"。 */
            var wa = System.Windows.SystemParameters.WorkArea;
            Check("子层窗口高度不超过屏幕工作区",
                w.Height <= wa.Height, $"窗口={w.Height:F0} 工作区={wa.Height:F0}");
        }
        catch (Exception ex)
        {
            Check("子层编辑窗口能打开并排版", false, ex.Message);
        }
        finally
        {
            try { w?.Close(); } catch { }
        }
    }

    /// <summary>
    /// 触发键的**自愈**与**修饰键物理校正**。
    ///
    /// 这两条对应两个真实症状：
    ///   ① "触发键偶尔按一下没反应，再按一次又好了" ——
    ///      上一次的抬起事件丢了，`_triggerHeld` 卡在 true，
    ///      下一次按下被当成自动重复直接吞掉；
    ///   ② "进层了，但层里的键全都不灵" ——
    ///      某个修饰键的抬起事件丢了，`_ctrlDown` 一直是 true，
    ///      每个键都被拼成 caps_ctrl_xxx，用户绑的 caps_q 永远查不到。
    ///
    /// 两条都靠物理按键状态（GetAsyncKeyState）纠偏，自检用
    /// <see cref="HotkeyEngine.PhysicalDownProbe"/> 模拟物理键盘。
    /// </summary>
    private static void TestTriggerRecovery()
    {
        var s = DefaultSettings.Create();
        foreach (var t in s.Triggers) t.Enabled = false;
        s.Triggers.Add(new TriggerKey
        {
            Key = "ralt", LayerId = 3, HoldMs = 100, TapAction = "keepKey", Enabled = true,
        });
        var layer3 = s.Layers.First(l => l.Id == 3);
        layer3.Keys.Add(new KeyBinding { Combo = "caps_q", Action = "qbar" });
        layer3.Keys.Add(new KeyBinding { Combo = "caps_shift_q", Action = "copy" });
        // Ctrl 子层：用来验证"层内按住的修饰键能正确参与 combo"
        layer3.Keys.Add(new KeyBinding { Combo = "caps_ctrl_q", Action = "copyAll" });

        var fired = new List<string>();
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => s, (a, arg) =>
        {
            lock (fired) fired.Add(a);
            return true;
        });
        engine.StartHoldTimer();
        engine.EnterLayerImmediatelyForTest = true;

        // 模拟的物理键盘：放进集合里 = 这个键真的按着
        var physicallyDown = new HashSet<int>();
        engine.PhysicalDownProbe = vk => physicallyDown.Contains(vk);

        // ---------- ① 抬起事件丢失 → 下一次按下必须还能触发 ----------
        physicallyDown.Add(VK.RMenu);
        engine.SimulateKey(VK.RMenu, true);
        CheckEqual("按下右 Alt 进第 3 层", engine.ActiveLayer, 3);

        // 用户松手了，但 up 事件丢了（钩子被摘/切安全桌面…）
        physicallyDown.Clear();

        /* 单次采样抖动不能取消真实长按 —— 连续 3 轮才认账。 */
        engine.CheckHoldThresholdForTest();
        Check("单次采样「没按着」不会立刻取消按住状态", engine.IsTriggerHeld);

        /* v4.5.4：不再按物理状态自愈。
         *
         * 触发键的按下被我们自己的钩子吞掉了，系统层面它从未被按下，
         * 物理读数永远是 false —— 旧实现因此会在"按住停顿 1 秒"后
         * 误判成松开、把层踢掉（用户按住触发键触发功能、看几秒再按别的键，
         * 绑定就全失效了）。现在静默期间一律不复位。 */
        DoEvents(1100);
        for (int i = 0; i < 16; i++) { engine.CheckHoldThresholdForTest(); DoEvents(6); }
        Check("静默期间不会误判成松开", engine.IsTriggerHeld);
        CheckEqual("静默期间仍停在第 3 层", engine.ActiveLayer, 3);

        // 恢复路径：用户重新按下触发键时，用 down 间隔识别出"新的一次按下"
        physicallyDown.Add(VK.RMenu);
        DoEvents(150);                       // 保证与上次 down 的间隔 > 100ms
        engine.SimulateKey(VK.RMenu, true);
        CheckEqual("抬起丢失后，重新按下仍能进层", engine.ActiveLayer, 3);

        // ---------- ② 真按住时，自动重复不能重置状态 ----------
        engine.SimulateKey(VK.RMenu, true);
        engine.SimulateKey(VK.RMenu, true);
        Check("真按住时的自动重复被忽略（仍是同一次按下）",
            engine.IsTriggerHeld && engine.ActiveLayer == 3);

        // ---------- ③ 进层前按下的修饰键卡住 → 物理校正后仍命中 caps_q ----------
        /* 只有"没被我们吞过"的修饰键才能靠物理状态纠偏。
         * 层内按下的修饰键会被吞掉（系统看不到），物理读数不可信 ——
         * 这种走 ⑤ 的路径，绝不能在这里校正。 */
        engine.SimulateKey(VK.RMenu, false);       // 先退出层，Shift 要在层外按下
        physicallyDown.Clear();
        DoEvents(150);                             // 与上次触发键 down 拉开 >100ms
        physicallyDown.Add(VK.LShift);
        engine.SimulateKey(VK.LShift, true);       // Shift 在层外按下（不吞）
        physicallyDown.Add(VK.RMenu);
        engine.SimulateKey(VK.RMenu, true);        // 再按触发键进层
        CheckEqual("按住 Shift 后仍能进第 3 层", engine.ActiveLayer, 3);

        // Shift 物理上松开了，但 up 事件丢了
        physicallyDown.Remove(VK.LShift);
        lock (fired) fired.Clear();
        bool qSwallowed = engine.SimulateKey(0x51 /*Q*/, true);
        DoEvents(60);
        Check("进层前按下的修饰键卡住时，物理校正后仍命中 caps_q", qSwallowed);
        lock (fired)
            Check("命中后执行的是 caps_q 的动作（不是 caps_shift_q）",
                fired.Contains("qbar") && !fired.Contains("copy"),
                string.Join(",", fired));
        engine.SimulateKey(0x51, false);

        // ---------- ④ 触发键按住前就按着的 Shift 不能被抹掉 ----------
        engine.SimulateKey(VK.RMenu, false);
        physicallyDown.Clear();
        DoEvents(150);

        physicallyDown.Add(VK.LShift);
        engine.SimulateKey(VK.LShift, true);      // Shift 先按住（不在层里，放行）
        physicallyDown.Add(VK.RMenu);
        engine.SimulateKey(VK.RMenu, true);       // 再按触发键

        lock (fired) fired.Clear();
        bool shiftQ = engine.SimulateKey(0x51, true);
        DoEvents(60);
        Check("触发键不会抹掉先按住的 Shift（caps_shift_q 仍可命中）", shiftQ);
        lock (fired)
            Check("执行的是 caps_shift_q 的动作", fired.Contains("copy"),
                string.Join(",", fired));
        engine.SimulateKey(0x51, false);
        engine.SimulateKey(VK.RMenu, false);
        engine.SimulateKey(VK.LShift, false);

        /* ---------- ⑤ 层内按下的修饰键 → 必须命中修饰子层 ----------
         *
         * 这是 v4.5.5 修的真实 bug：层内的 Ctrl/Shift/Alt/Win 会被钩子吞掉
         * （不吞会弹 Alt 菜单 / 开始菜单），系统层面它们从未按下，
         * GetAsyncKeyState 读出来是 false。旧实现每次按键前都用物理状态
         * 覆盖修饰标记，于是刚记下的 ctrl 立刻被抹掉 ——
         * 用户按住 Ctrl 再按 Q，执行的是 caps_q（基础层），
         * 修饰子层的绑定整层不生效。
         *
         * 这里把物理探测设成"什么都没按"来复现真实环境。 */
        physicallyDown.Clear();
        physicallyDown.Add(VK.RMenu);
        DoEvents(150);
        engine.SimulateKey(VK.RMenu, true);
        CheckEqual("重新进第 3 层（修饰子层测试）", engine.ActiveLayer, 3);

        engine.SimulateKey(VK.LControl, true);     // 被吞：物理读数仍是"没按下"
        Check("层内按下的 Ctrl 被记为按住", engine.ModifierHeldForTest(KeyModifiers.Ctrl));

        lock (fired) fired.Clear();
        bool ctrlQ = engine.SimulateKey(0x51 /*Q*/, true);
        DoEvents(60);
        Check("Ctrl 子层的键被执行", ctrlQ);
        lock (fired)
            Check("执行的是 caps_ctrl_q 的动作（不是 caps_q）",
                fired.Contains("copyAll") && !fired.Contains("qbar"),
                string.Join(",", fired));

        engine.SimulateKey(0x51, false);
        engine.SimulateKey(VK.LControl, false);
        Check("松开 Ctrl 后修饰状态清零", !engine.ModifierHeldForTest(KeyModifiers.Ctrl));
        engine.SimulateKey(VK.RMenu, false);
        physicallyDown.Clear();

        // ---------- ⑥ Qbar 打开后的宽限期 ----------
        Check("Qbar 打开瞬间的 Deactivated 不会关面板",
            !UI.Windows.QbarWindow.ShouldHideOnDeactivate(0));
        Check("Qbar 打开 100ms 内仍不关",
            !UI.Windows.QbarWindow.ShouldHideOnDeactivate(100));
        Check("Qbar 过了宽限期后正常收起",
            UI.Windows.QbarWindow.ShouldHideOnDeactivate(500));

        engine.Dispose();
        queue.Dispose();
    }

    /// <summary>
    /// 层的隔离性：在不同层里新建的绑定必须写进各自的列表，
    /// 不能串到第 1 层去（这个 bug 真实存在过）。
    /// </summary>
    private static void TestLayerIsolation()
    {
        var s = DefaultSettings.Create();
        int layer1Before = s.Layers.First(l => l.Id == 1).Keys.Count;

        // 模拟"在第 5 层新建一条绑定"
        var layer5 = s.Layers.First(l => l.Id == 5);
        layer5.Keys.Add(new Core.Settings.KeyBinding { Combo = "caps_ctrl_k", Action = "qbar" });

        CheckEqual("第 5 层多了一条绑定", layer5.Keys.Count, 1);
        CheckEqual("第 1 层的绑定数没变",
            s.Layers.First(l => l.Id == 1).Keys.Count, layer1Before);
        Check("第 1 层没有出现第 5 层的绑定",
            s.Layers.First(l => l.Id == 1).Keys.All(k => k.Combo != "caps_ctrl_k"));

        // Settings.Keys 必须始终是第 1 层那一份（老代码依赖这个别名）
        Check("Settings.Keys 就是第 1 层的列表",
            ReferenceEquals(s.Keys, s.Layers.First(l => l.Id == 1).Keys));

        // LayerKeys(5) 能取到第 5 层的内容
        Check("LayerKeys(5) 取到的是第 5 层",
            s.LayerKeys(5).Any(k => k.Combo == "caps_ctrl_k"));

        // 归一化之后这个别名关系必须保持
        SettingsStore.Normalize(s);
        Check("归一化后别名关系仍保持",
            ReferenceEquals(s.Keys, s.Layers.First(l => l.Id == 1).Keys));
    }

    /// <summary>
    /// 触发键配置：默认 CapsLock、可改、每个绑一层、不能重复。
    /// </summary>
    private static void TestTriggerConfig()
    {
        var s = DefaultSettings.Create();
        CheckEqual("默认只有一个触发键", s.Triggers.Count, 1);
        CheckEqual("默认触发键是 capslock", s.Triggers[0].Key, "capslock");
        CheckEqual("默认绑第 1 层", s.Triggers[0].LayerId, 1);
        Check("默认是启用的", s.Triggers[0].Enabled);

        // 改触发键（用户要求"默认是 CapsLock，但可以自己修改成别的"）
        s.Triggers[0].Key = "f13";
        SettingsStore.Normalize(s);
        CheckEqual("触发键可以改成别的键", s.Triggers[0].Key, "f13");

        // 加第二个触发键，绑第 5 层
        s.Triggers.Add(new TriggerKey
        {
            Key = "f14", LayerId = 5, HoldMs = 250,
            TapAction = "esc", Enabled = true,
        });
        SettingsStore.Normalize(s);
        CheckEqual("可以有多个触发键", s.Triggers.Count, 2);
        CheckEqual("第二个触发键绑第 5 层", s.Triggers[1].LayerId, 5);
        CheckEqual("第二个触发键的阈值", s.Triggers[1].HoldMs, 250);
        CheckEqual("第二个触发键的短按动作", s.Triggers[1].TapAction, "esc");

        // 阈值要被夹到合理范围
        s.Triggers[1].HoldMs = 99999;
        SettingsStore.Normalize(s);
        Check("过大的阈值被夹到上限", s.Triggers[1].HoldMs <= 2000,
            s.Triggers[1].HoldMs.ToString());
        s.Triggers[1].HoldMs = 1;
        SettingsStore.Normalize(s);
        Check("过小的阈值被夹到下限", s.Triggers[1].HoldMs >= 50,
            s.Triggers[1].HoldMs.ToString());

        // 层 ID 越界要回退（否则永远进不了层）
        s.Triggers[1].LayerId = 99;
        SettingsStore.Normalize(s);
        Check("越界的层 ID 回退到第 1 层", s.Triggers[1].LayerId == 1,
            s.Triggers[1].LayerId.ToString());

        // 触发键的键名必须能解析成 VK，否则永远匹配不上
        foreach (var t in s.Triggers)
            Check($"触发键「{t.Key}」能解析成虚拟键码", KeyName.VkOf(t.Key) != null);
        Check("capslock 能解析", KeyName.VkOf("capslock") == VK.CapsLock);
    }

    /// <summary>
    /// 「按住触发键期间按了别的键 → 立即进层」。
    ///
    /// 没有这一条的话，用户每次想按组合键都得先按住等满阈值，
    /// 组合键会显得"时灵时不灵" —— 这是 tap-hold 方案的经典手感问题。
    /// 这里用的是**真实计时器**路径（阈值设很长，确保不是靠超时进层的）。
    /// </summary>
    private static void TestHoldOnOtherKey()
    {
        var settings = DefaultSettings.Create();
        // 阈值设成 3 秒：如果进层了，那一定是"按了别的键"触发的，不是超时
        foreach (var t in settings.Triggers)
        {
            t.HoldMs = 3000;
            t.HoldOnOtherKey = true;
        }

        var triggered = new List<string>();
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => settings, (action, arg) =>
        {
            lock (triggered) triggered.Add(action);
            return true;
        });
        engine.StartHoldTimer();

        // 按下触发键，然后在远小于阈值的时间里按 S（caps_s = 左移）
        engine.SimulateKey(VK.CapsLock, true);
        Check("刚按下触发键时还没进层（阈值 3 秒）", engine.ActiveLayer == -1);

        bool swallowed = engine.SimulateKey(0x53 /*S*/, true);
        CheckEqual("按下别的键后立即进入了第 1 层", engine.ActiveLayer, 1);
        Check("这个键被层接管（吞掉）", swallowed);

        engine.SimulateKey(0x53, false);
        engine.SimulateKey(VK.CapsLock, false);
        DoEvents(150);
        lock (triggered)
        {
            Check("组合动作被触发", triggered.Count > 0, string.Join(",", triggered));
        }
        engine.Dispose();
        queue.Dispose();

        // 关掉这个开关时：别的键不该让它进层，而应正常放行
        var s2 = DefaultSettings.Create();
        foreach (var t in s2.Triggers)
        {
            t.HoldMs = 3000;
            t.HoldOnOtherKey = false;
        }
        var q2 = new ActionQueue();
        var e2 = new HotkeyEngine(q2, () => s2, (_, _) => true);
        e2.StartHoldTimer();

        e2.SimulateKey(VK.CapsLock, true);
        bool s2Swallowed = e2.SimulateKey(0x53, true);
        Check("关掉开关后按别的键不进层", e2.ActiveLayer == -1);
        Check("关掉开关后按键被放行（正常输入）", !s2Swallowed);
        e2.SimulateKey(0x53, false);
        e2.SimulateKey(VK.CapsLock, false);
        e2.Dispose();
        q2.Dispose();
    }

    /// <summary>
    /// 修饰键跟踪：这是「能识别按键但录不到组合键」的回归测试。
    ///
    /// 根因是原实现用 GetAsyncKeyState 判断修饰键 —— 在低级钩子回调里
    /// 这个 API 返回的是**上一个键**的状态，所以按 Ctrl+K 只能录到 K。
    /// 现在改为从钩子事件自己维护按住集合。
    /// </summary>
    private static void TestModifierTracker()
    {
        var t = new ModifierTracker();

        CheckEqual("初始没有修饰键", t.Current, KeyModifiers.None);

        // 按下左 Ctrl → 应该是 Ctrl
        t.Track(0xA2, true);
        CheckEqual("按下左 Ctrl", t.Current, KeyModifiers.Ctrl);

        // 按下左 Shift → Ctrl|Shift
        t.Track(0xA0, true);
        Check("Ctrl+Shift 同时按住",
            t.Current == (KeyModifiers.Ctrl | KeyModifiers.Shift),
            t.Current.ToString());

        // 松开 Shift → 只剩 Ctrl
        t.Track(0xA0, false);
        CheckEqual("松开 Shift 后只剩 Ctrl", t.Current, KeyModifiers.Ctrl);

        // 松开 Ctrl → 空
        t.Track(0xA2, false);
        CheckEqual("全部松开后为空", t.Current, KeyModifiers.None);

        // 左右修饰键归一到同一个逻辑键
        t.Clear();
        t.Track(0xA3, true);          // 右 Ctrl
        CheckEqual("右 Ctrl 也算 Ctrl", t.Current, KeyModifiers.Ctrl);
        t.Track(0xA2, true);          // 再按左 Ctrl
        t.Track(0xA3, false);         // 松开右 Ctrl
        CheckEqual("松开右边仍按住左边时还是 Ctrl", t.Current, KeyModifiers.Ctrl);
        t.Track(0xA2, false);
        CheckEqual("两个都松开后为空", t.Current, KeyModifiers.None);

        // 四个修饰键全按住
        t.Clear();
        t.Track(0xA2, true); t.Track(0xA0, true);
        t.Track(0xA4, true); t.Track(0x5B, true);
        var all = KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Win;
        CheckEqual("四个修饰键全按住", t.Current, all);

        // 非修饰键不该影响集合
        t.Clear();
        t.Track(0x41, true);          // A
        CheckEqual("普通键不进修饰键集合", t.Current, KeyModifiers.None);

        // 归一化函数
        CheckEqual("0xA2 归一到 Ctrl", ModifierTracker.Normalize(0xA2), 0x11);
        CheckEqual("0xA3 归一到 Ctrl", ModifierTracker.Normalize(0xA3), 0x11);
        CheckEqual("0xA0 归一到 Shift", ModifierTracker.Normalize(0xA0), 0x10);
        CheckEqual("0xA4 归一到 Alt", ModifierTracker.Normalize(0xA4), 0x12);
        CheckEqual("0x5C 归一到 Win", ModifierTracker.Normalize(0x5C), 0x5B);

        // IsModifier 判定
        foreach (int vk in new[] { 0x11, 0xA2, 0xA3, 0x10, 0xA0, 0xA1, 0x12, 0xA4, 0xA5, 0x5B, 0x5C })
            Check($"0x{vk:X2} 被认作修饰键", ModifierTracker.IsModifier(vk));
        foreach (int vk in new[] { 0x41, 0x31, 0x70 })
            Check($"0x{vk:X2} 不是修饰键", !ModifierTracker.IsModifier(vk));

        // 组合串构造
        CheckEqual("构造 Ctrl+Shift+K",
            ModifierTracker.BuildCombo(KeyModifiers.Ctrl | KeyModifiers.Shift, "k"),
            "ctrl+shift+k");
        CheckEqual("构造单键",
            ModifierTracker.BuildCombo(KeyModifiers.None, "k"), "k");

        // Seed 播种（模拟"用户已经按着 Ctrl 才打开对话框"）
        var t2 = new ModifierTracker();
        t2.Seed(vk => vk == 0xA2);    // 只有左 Ctrl 是按着的
        CheckEqual("播种后识别出 Ctrl", t2.Current, KeyModifiers.Ctrl);
    }

    /// <summary>
    /// 触发键列表展示属性：列表里要能直接看到「按哪个键 / 进第几层」。
    /// 之前 XAML 绑了 KeyLabel/Summary/Detail/Index，但类上没有这些属性，整行空白。
    /// </summary>
    private static void TestTriggerDisplayProps()
    {
        var t = new TriggerKey
        {
            Key = "capslock",
            LayerId = 3,
            HoldMs = 220,
            TapAction = "toggleCapsLock",
            HoldOnOtherKey = true,
            Index = 0,
        };

        CheckEqual("键帽文字", t.KeyLabel, "CapsLock");
        CheckEqual("主标题显示层号", t.Summary, "第 3 层");
        Check("副标题包含短按动作", t.Detail.Contains("切换大小写"), t.Detail);
        Check("副标题包含进层方式", t.Detail.Contains("进层"), t.Detail);
        CheckEqual("Index 可写", t.Index, 0);

        // 未设置键时要有占位符，不能是空白
        var empty = new TriggerKey { Key = "" };
        CheckEqual("空键显示占位符", empty.KeyLabel, "?");

        // 关闭 hold-on-other-key 时要显示阈值
        var t2 = new TriggerKey { Key = "f13", LayerId = 5, HoldMs = 350, HoldOnOtherKey = false };
        Check("关闭即时进层时显示阈值", t2.Detail.Contains("350"), t2.Detail);

        // 反射确认这些属性都存在（防止 XAML 绑定又对不上）
        foreach (string prop in new[] { "KeyLabel", "Summary", "Detail", "Index", "Enabled" })
            Check($"TriggerKey 有 {prop} 属性",
                typeof(TriggerKey).GetProperty(prop) != null);
    }

    /// <summary>
    /// 触发键只允许单一按键。
    ///
    /// v2.5 起明确：组合键不能当触发键 —— 那会和"层内修饰键子层"语义冲突
    /// （同样是 Ctrl+K，到底算触发还是算子层绑定？）。
    /// 组合键保留给**层内绑定**：进层后按住 Ctrl 再按 K。
    ///
    /// 老配置里若写了组合键，会退化成"取主键当单键触发"，不会彻底失效。
    /// </summary>
    private static void TestComplexTriggerKey()
    {
        // ① 组合键 ID 仍能被解析（用于层内绑定，不再用于触发）
        var parsed = KeyName.ParseCombo("caps_ctrl_shift_k");
        Check("组合键 ID 可解析", parsed != null);
        if (parsed != null)
        {
            var (mods, key) = parsed.Value;
            CheckEqual("主键是 k", key, "k");
            Check("含 Ctrl", mods.HasFlag(KeyModifiers.Ctrl));
            Check("含 Shift", mods.HasFlag(KeyModifiers.Shift));
        }

        // ② 单键走 VkOf 路径
        Check("单键 f13 能取到 VK", KeyName.VkOf("f13") != null);
        Check("单键 capslock 能取到 VK", KeyName.VkOf("capslock") != null);

        // ③ 组合触发键退化为单键触发：只按 K（不按 Ctrl）也能进层
        var s = DefaultSettings.Create();
        s.Triggers.Clear();
        s.Triggers.Add(new TriggerKey
        {
            Key = "caps_ctrl_k", LayerId = 3, HoldMs = 200,
            TapAction = "keepKey", Enabled = true,
        });

        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => s, (_, _) => true);
        engine.StartHoldTimer();
        engine.EnterLayerImmediatelyForTest = true;

        // 不按 Ctrl，直接按 K
        engine.SimulateKey(0x4B, true);
        Thread.Sleep(60);

        CheckEqual("组合触发键退化为单键（只按 K 也进层）", engine.ActiveLayer, 3);
        engine.SimulateKey(0x4B, false);
        DoEvents(150);
        engine.Dispose();
        queue.Dispose();

        // ④ 纯单键触发仍然正常
        var s2 = DefaultSettings.Create();
        s2.Triggers.Clear();
        s2.Triggers.Add(new TriggerKey
        {
            Key = "f13", LayerId = 5, HoldMs = 200,
            TapAction = "keepKey", Enabled = true,
        });
        var q2 = new ActionQueue();
        var e2 = new HotkeyEngine(q2, () => s2, (_, _) => true);
        e2.StartHoldTimer();
        e2.EnterLayerImmediatelyForTest = true;
        e2.SimulateKey(0x7C, true);          // F13 = 0x7C
        Thread.Sleep(60);
        CheckEqual("纯单键触发进层正确", e2.ActiveLayer, 5);
        e2.SimulateKey(0x7C, false);
        DoEvents(120);
        e2.Dispose();
        q2.Dispose();
    }

    /// <summary>
    /// 顶部「按住 XX + 下面的键」的文字必须跟着**当前层**的触发键变。
    ///
    /// 之前这里写死了 "按住 CapsLock"：第 2 层配的是 F13，切过去
    /// 仍然提示按住大写键，与实际触发方式完全对不上。
    /// 这里验证 DescribeTriggerKey 能把各种触发键转成可读文字。
    /// </summary>
    private static void TestTriggerBadgeFollowsLayer()
    {
        var t = typeof(CapsLockPlus.UI.Views.KeysView);
        var m = t.GetMethod("DescribeTriggerKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check("KeysView 有触发键描述方法", m != null);
        if (m == null) return;

        string Describe(string key) => (string)m.Invoke(null, new object[] { key })!;

        // 单键
        CheckEqual("CapsLock 触发键", Describe("capslock"), "CapsLock");
        CheckEqual("F13 触发键", Describe("f13"), "F13");

        // 组合键：要展开成 Ctrl+Shift+K，而不是一串编码
        string combo = Describe("caps_ctrl_shift_k");
        Check("组合触发键展开为可读文字",
            combo.Contains("Ctrl") && combo.Contains("Shift") && combo.Contains("K"),
            combo);

        // 空值有兜底，不能显示空白
        Check("空触发键有兜底", !string.IsNullOrWhiteSpace(Describe("")), Describe(""));

        // 不同层的触发键应当描述出不同结果
        var settings = DefaultSettings.Create();
        settings.Triggers.Clear();
        settings.Triggers.Add(new TriggerKey { Key = "capslock", LayerId = 1, Enabled = true });
        settings.Triggers.Add(new TriggerKey { Key = "f13", LayerId = 2, Enabled = true });
        settings.Triggers.Add(new TriggerKey { Key = "caps_ctrl_shift_k", LayerId = 3, Enabled = true });

        string d1 = Describe(settings.Triggers.First(x => x.LayerId == 1).Key);
        string d2 = Describe(settings.Triggers.First(x => x.LayerId == 2).Key);
        string d3 = Describe(settings.Triggers.First(x => x.LayerId == 3).Key);

        Check("三层触发键描述各不相同", d1 != d2 && d2 != d3 && d1 != d3,
            $"{d1} / {d2} / {d3}");
        CheckEqual("第1层描述为 CapsLock", d1, "CapsLock");
        CheckEqual("第2层描述为 F13", d2, "F13");
    }

    /// <summary>
    /// 层内子层：每层可配 0~10 个，子层键可用任意物理键，也能自定义名字。
    /// </summary>
    private static void TestSubLayers()
    {
        // ① 未配置时回退到默认 4 个（老配置行为不变）
        var plain = new LayerOptions { Id = 1, Name = "基础层" };
        CheckEqual("未配置时默认 4 个子层", plain.EffectiveSubLayers.Count, 4);

        var keys = plain.EffectiveSubLayers.Select(s => s.Key).ToList();
        Check("默认含 ctrl", keys.Contains("ctrl"), string.Join(",", keys));
        Check("默认含 shift", keys.Contains("shift"));
        Check("默认含 alt", keys.Contains("alt"));
        Check("默认含 win", keys.Contains("win"));

        // ② 子层键可以是任意物理键（不限于修饰键）
        var custom = new LayerOptions
        {
            Id = 2,
            SubLayers = new List<SubLayerDef>
            {
                new() { Key = "ctrl", Name = "左侧功能" },
                new() { Key = "tab" },
                new() { Key = "capslock" },
            },
        };
        CheckEqual("配置了 3 个就有 3 个", custom.EffectiveSubLayers.Count, 3);
        Check("Tab 可当子层键", custom.EffectiveSubLayers.Any(s => s.Key == "tab"));
        Check("CapsLock 可当子层键", custom.EffectiveSubLayers.Any(s => s.Key == "capslock"));

        // ③ 自定义名字生效；没名字时退回键名
        var named = custom.EffectiveSubLayers.First(s => s.Key == "ctrl");
        CheckEqual("自定义名字生效", named.Display, "左侧功能");

        var unnamed = custom.EffectiveSubLayers.First(s => s.Key == "tab");
        Check("无名字时显示键名", !string.IsNullOrWhiteSpace(unnamed.Display), unnamed.Display);

        // ④ 上限 10 个
        var tooMany = new LayerOptions
        {
            Id = 3,
            SubLayers = Enumerable.Range(1, 15)
                .Select(i => new SubLayerDef { Key = "f" + i }).ToList(),
        };
        Check("超过 10 个会被截断",
            tooMany.EffectiveSubLayers.Count == LayerOptions.MaxSubLayers,
            tooMany.EffectiveSubLayers.Count.ToString());
        CheckEqual("上限常量是 10", LayerOptions.MaxSubLayers, 10);

        // ⑤ 旧字段兼容：位标记仍能转换成子层定义
        var legacy = new LayerOptions
        {
            Id = 4,
            SubLayerModifiers = new List<int> { (int)KeyModifiers.Ctrl },
        };
        CheckEqual("旧位标记仍可用", legacy.EffectiveSubLayers.Count, 1);
        CheckEqual("旧位标记转成 ctrl 键", legacy.EffectiveSubLayers[0].Key, "ctrl");

        // ⑥ 每个子层有独立绑定
        var layer = new LayerOptions { Id = 5 };
        layer.Keys.Add(new KeyBinding { Combo = "caps_q", Action = "qbar" });
        layer.Keys.Add(new KeyBinding
        {
            Combo = KeyName.BuildComboEx(KeyModifiers.Ctrl, "q"),
            Action = "deleteWord",
        });
        layer.Keys.Add(new KeyBinding
        {
            Combo = KeyName.BuildComboEx(KeyModifiers.Ctrl | KeyModifiers.Shift, "q"),
            Action = "copy1",
        });

        var direct = layer.Keys.FirstOrDefault(k => k.Combo == "caps_q");
        var ctrl = layer.Keys.FirstOrDefault(k =>
            k.Combo == KeyName.BuildComboEx(KeyModifiers.Ctrl, "q"));
        var ctrlShift = layer.Keys.FirstOrDefault(k =>
            k.Combo == KeyName.BuildComboEx(KeyModifiers.Ctrl | KeyModifiers.Shift, "q"));

        Check("直接按的绑定独立", direct?.Action == "qbar", direct?.Action ?? "(无)");
        Check("Ctrl 子层绑定独立", ctrl?.Action == "deleteWord", ctrl?.Action ?? "(无)");
        Check("Ctrl+Shift 子层绑定独立", ctrlShift?.Action == "copy1", ctrlShift?.Action ?? "(无)");

        // ⑦ 子层配置能存进 JSON 并读回
        var s = DefaultSettings.Create();
        s.Layers[1].SubLayers = new List<SubLayerDef>
        {
            new() { Key = "alt", Name = "我的子层" },
        };
        string json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
        Check("子层配置能序列化往返",
            back?.Layers[1].EffectiveSubLayers.Count == 1,
            back?.Layers[1].EffectiveSubLayers.Count.ToString() ?? "(null)");
        CheckEqual("自定义名字也保留",
            back?.Layers[1].EffectiveSubLayers[0].Name, "我的子层");
    }

    /// <summary>
    /// 子层的 combo 规则与**实际查找**。
    ///
    /// 两个真实问题：
    ///   ① 界面拼 combo 时只用了修饰位，没带非修饰键子层的前缀 ——
    ///      点「+ Tab」之后键帽读写的还是 caps_q，等于在改基础层，
    ///      而引擎查的是 caps_tab_q，那个子层永远绑不上；
    ///   ② 子层里没绑的键以前会退回基础层绑定 ——
    ///      按 Tab+W 却执行了 caps_w，用户觉得"子层根本没生效"。
    ///      现在的语义和触发层一致：按住子层键 = 键盘交给子层，没绑就什么都不做。
    /// </summary>
    private static void TestSubLayerCombos()
    {
        // ---------- ① 拼 combo 的规则：界面与引擎必须一致 ----------
        CheckEqual("直接按", UI.Views.KeysView.ComboFor("", KeyModifiers.None, "q"), "caps_q");
        CheckEqual("Ctrl 子层", UI.Views.KeysView.ComboFor("ctrl", KeyModifiers.Ctrl, "q"),
            "caps_ctrl_q");
        CheckEqual("Ctrl+Shift 子层",
            UI.Views.KeysView.ComboFor("ctrl_shift", KeyModifiers.Ctrl | KeyModifiers.Shift, "q"),
            "caps_ctrl_shift_q");
        CheckEqual("Tab 子层（非修饰键）",
            UI.Views.KeysView.ComboFor("tab", KeyModifiers.None, "q"), "caps_tab_q");
        CheckEqual("F13 子层（非修饰键）",
            UI.Views.KeysView.ComboFor("f13", KeyModifiers.None, "q"), "caps_f13_q");

        Check("tab 不是修饰键子层", !UI.Views.KeysView.IsModifierOnlySubLayer("tab"));
        Check("f13 不是修饰键子层", !UI.Views.KeysView.IsModifierOnlySubLayer("f13"));
        Check("ctrl 是修饰键子层", UI.Views.KeysView.IsModifierOnlySubLayer("ctrl"));
        Check("ctrl_shift 是修饰键组合", UI.Views.KeysView.IsModifierOnlySubLayer("ctrl_shift"));
        Check("空子层键不算修饰键", !UI.Views.KeysView.IsModifierOnlySubLayer(""));

        // ---------- ② 引擎按同一套 combo 找绑定 ----------
        var s = DefaultSettings.Create();
        foreach (var t in s.Triggers) t.Enabled = false;
        s.Triggers.Add(new TriggerKey
        {
            Key = "capslock", LayerId = 1, HoldMs = 60, Enabled = true,
        });

        var layer = s.Layers.First(l => l.Id == 1);
        layer.SubLayers = new List<SubLayerDef>
        {
            new() { Key = "tab" },
            new() { Key = "ctrl" },
        };
        layer.SubLayerModifiers.Clear();

        layer.Keys.RemoveAll(b => b.Combo is "caps_q" or "caps_w"
                                    or "caps_tab_q" or "caps_tab_w" or "caps_ctrl_q");
        layer.Keys.Add(new KeyBinding { Combo = "caps_q", Action = "qbar" });
        // W 只在基础层绑了功能：按住 Tab 时应"什么都没发生"，而不是执行它
        layer.Keys.Add(new KeyBinding { Combo = "caps_w", Action = "backspace" });
        layer.Keys.Add(new KeyBinding { Combo = "caps_tab_q", Action = "copy" });
        layer.Keys.Add(new KeyBinding { Combo = "caps_ctrl_q", Action = "copyAll" });

        var fired = new List<string>();
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => s, (a, arg) =>
        {
            lock (fired) fired.Add(a);
            return true;
        });
        engine.StartHoldTimer();
        engine.EnterLayerImmediatelyForTest = true;

        int Count(string action)
        {
            lock (fired) return fired.Count(x => x == action);
        }

        engine.SimulateKey(VK.CapsLock, true);
        CheckEqual("按住触发键进第 1 层（子层测试）", engine.ActiveLayer, 1);

        // 直接按 Q → qbar
        engine.SimulateKey(0x51, true);
        engine.SimulateKey(0x51, false);
        DoEvents(60);
        Check("直接按 Q 命中 caps_q", Count("qbar") == 1, string.Join(",", fired));

        // Tab 子层 + Q → copy（不是 qbar）
        engine.SimulateKey(VK.Tab, true);
        engine.SimulateKey(0x51, true);
        engine.SimulateKey(0x51, false);
        DoEvents(60);
        Check("Tab 子层 + Q 命中 caps_tab_q", Count("copy") == 1, string.Join(",", fired));
        Check("Tab 子层 + Q 不会再执行基础层的 caps_q", Count("qbar") == 1,
            string.Join(",", fired));

        // Tab 子层 + W（两个 combo 都没绑）→ 吞掉、什么都不执行
        bool wSwallowed = engine.SimulateKey(0x57 /*W*/, true);
        engine.SimulateKey(0x57, false);
        DoEvents(60);
        Check("子层里没绑的键被吞掉（不恢复原生输入）", wSwallowed);
        lock (fired)
            Check("子层里没绑的键不回退到基础层绑定",
                fired.Count == 2, string.Join(",", fired));

        // 松开 Tab，回到直接按
        engine.SimulateKey(VK.Tab, false);
        engine.SimulateKey(0x51, true);
        engine.SimulateKey(0x51, false);
        DoEvents(60);
        Check("松开子层键后回到 caps_q", Count("qbar") == 2, string.Join(",", fired));

        // Ctrl 子层（修饰键）：按 Tab 时不会被当成子层前缀干扰
        engine.SimulateKey(VK.LControl, true);
        engine.SimulateKey(0x51, true);
        engine.SimulateKey(0x51, false);
        DoEvents(60);
        Check("Ctrl 子层 + Q 命中 caps_ctrl_q", Count("copyAll") == 1, string.Join(",", fired));
        engine.SimulateKey(VK.LControl, false);

        engine.SimulateKey(VK.CapsLock, false);
        engine.Dispose();
        queue.Dispose();
    }

    private static void TestTriggerKeyPosition()
    {
        var s = DefaultSettings.Create();

        // 第 1 层默认 CapsLock；给第 2 层加一个 F13 触发键
        s.Triggers.Add(new TriggerKey
        {
            Key = "f13", LayerId = 2, HoldMs = 200,
            TapAction = "keepKey", Enabled = true,
        });

        SettingsStore.Normalize(s);

        // 每层应当解析出各自的触发键名（界面靠这个决定高亮哪个键）
        string? TriggerOf(int layer) => s.Triggers
            .FirstOrDefault(t => t.LayerId == layer && t.Enabled)?.Key;

        CheckEqual("第 1 层触发键是 capslock", TriggerOf(1), "capslock");
        CheckEqual("第 2 层触发键是 f13", TriggerOf(2), "f13");
        Check("第 3 层没有触发键", TriggerOf(3) == null);

        // 触发键名必须能在键盘布局里找到对应键位
        Check("capslock 在键盘布局里", KeyName.VkOf("capslock") != null);
        Check("f13 在键盘布局里", KeyName.VkOf("f13") != null);

        // 关键：两层的触发键不同 → 高亮的键位必须不同
        Check("两层的触发键不是同一个键", TriggerOf(1) != TriggerOf(2),
            $"{TriggerOf(1)} vs {TriggerOf(2)}");
    }
}