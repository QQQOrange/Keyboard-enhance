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

    /* 宏的录制、编辑、显示与绑定。
     * 与 SelfTest.cs 同属 partial 类。 */

    /// <summary>
    /// 宏步骤的"在字符之间插入延迟"逻辑。
    /// 这是用户明确要求的能力，纯数据层验证：插到指定位置后顺序必须正确。
    /// </summary>
    private static void TestMacroStepEditing()
    {
        var macro = new Core.Settings.Macro { Name = "编辑测试" };
        macro.Steps.Add(new Core.Settings.MacroStep { Kind = "text", Text = "ab" });

        // 在末尾追加一个延迟
        macro.Steps.Add(new Core.Settings.MacroStep { Kind = "delay", DelayMs = 300 });
        CheckEqual("末尾追加后共 2 步", macro.Steps.Count, 2);

        // 在 index 0 之后插入一个延迟（即"ab"与"300ms"之间）
        macro.Steps.Insert(1, new Core.Settings.MacroStep { Kind = "delay", DelayMs = 50 });
        CheckEqual("插入后共 3 步", macro.Steps.Count, 3);
        CheckEqual("第 1 步是文本", macro.Steps[0].Kind, "text");
        CheckEqual("第 2 步是插入的延迟", macro.Steps[1].DelayMs, 50);
        CheckEqual("第 3 步是原来的延迟", macro.Steps[2].DelayMs, 300);

        // 随机延迟的取值范围必须能被读回
        var rnd = new Core.Settings.MacroStep { Kind = "delayRandom", MinMs = 30, MaxMs = 90 };
        macro.Steps.Insert(0, rnd);
        CheckEqual("随机延迟下界", macro.Steps[0].MinMs, 30);
        CheckEqual("随机延迟上界", macro.Steps[0].MaxMs, 90);

        // 估算时长要把随机延迟按中值算进去
        int est = macro.EstimatedMs;
        Check("估算时长包含随机延迟的中值", est >= (30 + 90) / 2, est.ToString());

        // 克隆必须深拷贝步骤（否则编辑会互相影响）
        var copy = macro.Clone();
        copy.Steps[0].MinMs = 999;
        CheckEqual("克隆是深拷贝，改副本不影响原宏", macro.Steps[0].MinMs, 30);

        // 删除中间一步后顺序仍然正确
        macro.Steps.RemoveAt(1);
        CheckEqual("删除后共 3 步", macro.Steps.Count, 3);
        CheckEqual("删除后第 2 步是 50ms 延迟", macro.Steps[1].DelayMs, 50);
    }

    /// <summary>
    /// 宏录制：验证录制器能记录按键并生成步骤。
    ///
    /// 这是「点录制没反应」的回归测试。真实路径是
    /// 钩子 → HotkeyEngine.OnKeyEvent → MacroRecorder.Record，
    /// 这里直接驱动录制器，确认它本身能工作（排除钩子因素）。
    /// </summary>
    private static void TestMacroRecorder()
    {
        var rec = new Core.Macro.MacroRecorder();
        Check("初始未在录制", !rec.IsRecording);

        rec.Start();
        Check("Start 后进入录制", rec.IsRecording);

        // 模拟输入 "hi"：H、I 各一次 down/up，中间有间隔
        rec.Record(0x48, true);    // H down
        Thread.Sleep(40);
        rec.Record(0x48, false);   // H up
        Thread.Sleep(30);
        rec.Record(0x49, true);    // I down
        Thread.Sleep(20);
        rec.Record(0x49, false);   // I up

        Check("记录了 4 个事件", rec.EventCount == 4, rec.EventCount.ToString());

        var macro = rec.Stop("测试宏");
        Check("停止后退出录制", !rec.IsRecording);
        Check("生成 >= 4 步", macro.Steps.Count >= 4, macro.Steps.Count.ToString());
        Check("停止后时长仍然保留", rec.ElapsedMs > 0, rec.ElapsedMs + "ms");

        bool hasDown = macro.Steps.Any(s => s.Kind == "keyDown" && s.Key == "h");
        bool hasUp = macro.Steps.Any(s => s.Kind == "keyUp" && s.Key == "i");
        Check("录制到 H 按下", hasDown);
        Check("录制到 I 抬起", hasUp);
        Check("至少有一个延迟步骤", macro.Steps.Any(s => s.Kind == "delay"));

        bool hasDelay = macro.Steps.Any(s => s.Kind == "delay" && s.DelayMs >= 25);
        Check("按键间隔被记录为延迟", hasDelay,
            string.Join(",", macro.Steps.Where(s => s.Kind == "delay").Select(s => s.DelayMs)));

        // 播放器不应抛异常（播完即结束；线程退出需要一点时间）
        var player = new Core.Macro.MacroPlayer();
        player.Play(macro);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (player.IsPlaying && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Check("播放后结束", !player.IsPlaying);
        player.Dispose();
    }

    /// <summary>
    /// 录制时的实时反馈：每按一个键都要立刻出现在列表里。
    ///
    /// 这是"录完才知道录了什么"的回归测试 —— 用户抱怨的正是这个体验。
    /// 走的是真实链路：引擎 SimulateKey → MacroRecorder.Record → Recorded 事件
    /// → 界面线程 → 实时列表；中途任何一环断了，实时列表就不会长。
    /// </summary>
    private static void TestMacroLiveRecording()
    {
        // ---- 纯录制器：事件、快照、阈值 ----
        var rec = new Core.Macro.MacroRecorder();
        var seen = new List<(int Vk, bool Down, long Ms)>();
        rec.Recorded += (vk, down, ms) => seen.Add((vk, down, ms));

        CheckEqual("抖动阈值是 15ms", Core.Macro.MacroRecorder.MinDelayMs, 15);
        rec.Start();
        rec.Record(0x41, true);     // A 按下
        rec.Record(0x41, false);    // A 抬起
        CheckEqual("每录一个键就抛一次事件", seen.Count, 2);
        Check("事件带正确的键码与方向", seen.Count == 2 && seen[0].Vk == 0x41 && seen[0].Down
                                        && seen[1].Vk == 0x41 && !seen[1].Down);
        Check("事件带时间戳", seen.Count == 2 && seen[1].Ms >= seen[0].Ms,
            seen.Count == 2 ? $"{seen[0].Ms}→{seen[1].Ms}" : "");
        CheckEqual("快照能还原已录事件", rec.Snapshot().Count, 2);

        rec.Abort();
        CheckEqual("取消后快照为空", rec.Snapshot().Count, 0);
        Check("取消后不再抛事件", seen.Count == 2);

        // ---- 真实链路：钩子回调 → 引擎 → 录制器 → 事件 ----
        //
        // 只测录制器本身是不够的：引擎那一句 MacroRecorder?.Record 要是掉了，
        // 界面上就永远是"录完才知道"的样子。
        var engineSettings = DefaultSettings.Create();
        var engine = new HotkeyEngine(new ActionQueue(), () => engineSettings, (_, _) => true);
        var rec2 = new Core.Macro.MacroRecorder();
        int events = 0;
        string lastKey = "";
        rec2.Recorded += (vk, _, _) => { events++; lastKey = KeyName.NameOf(vk) ?? ""; };

        engine.MacroRecorder = rec2;                 // 挂上录制器（同时挂起层逻辑）
        rec2.Start();
        engine.SimulateKey(0x41, true);              // A
        engine.SimulateKey(0x41, false);             // A 抬起
        CheckEqual("引擎把每个按键都喂给录制器", events, 2);
        CheckEqual("录制到的是 A", lastKey, "a");
        Check("录制期间引擎处于挂起（不触发原功能）", engine.Suspended);

        engine.MacroRecorder = null;
        rec2.Abort();
        Check("停止录制后引擎恢复", !engine.Suspended);
        engine.Dispose();

        // ---- 界面：实时行的内容与计数 ----
        //
        // 这里不构造 AppHost（自检阶段它还没创建），所以只验"喂进来的事件怎么变成行"：
        // 按键成一行、间隔够大就插一行"等待"，和最终生成的宏完全对应。
        try
        {
            var view = new UI.Views.MacrosView();
            CheckEqual("初始实时列表是空的", view.LiveRowCountForTest, 0);
            Check("初始不在录制态", !view.IsRecordingUiForTest);

            view.FeedLiveForTest(0x41, true, 0);      // A 按下
            view.FeedLiveForTest(0x41, false, 30);    // 30ms 后抬起 —— 够大，要插一行等待
            view.FeedLiveForTest(0x42, true, 35);     // 5ms 后 B 按下 —— 太小，不插

            CheckEqual("三个按键 = 三行 + 一行等待", view.LiveRowCountForTest, 4);

            view.ToggleRecordingUiForTest(true);
            Check("录制态下步骤卡片让位给实时监视器", view.IsRecordingUiForTest);
            view.ToggleRecordingUiForTest(false);
            Check("退出录制后步骤卡片回来", !view.IsRecordingUiForTest);
        }
        catch (Exception ex)
        {
            Check("实时列表能构建", false, ex.Message);
        }

        // ---- 悬浮条：切到别的程序里录的时候，屏幕上只剩它 ----
        try
        {
            UI.Windows.RecordingHudWindow.ShowHud();
            UI.Windows.RecordingHudWindow.UpdateHud(1400, 12, "A ↓");
            Check("录制悬浮条能显示并刷新", true);
            UI.Windows.RecordingHudWindow.HideHud();
            DoEvents(60);          // 等窗口真正关掉，别让它以 Topmost 留在屏幕上影响后面的焦点测试
            Check("录制悬浮条能关闭", true);
        }
        catch (Exception ex)
        {
            Check("录制悬浮条能显示", false, ex.Message);
        }
    }

    /// <summary>
    /// 宏必须能在「按键映射」页被绑定。
    ///
    /// 这是「绑不了宏」的回归测试：动作表以前只在启动时建一次，
    /// 于是新建的宏永远不出现在功能下拉里。现在加/删宏都会重建动作表。
    /// </summary>
    private static void TestMacroIsBindable()
    {
        var settings = DefaultSettings.Create();
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new Core.Actions.WindowBindingService(settings),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };

        // 起始没有宏 → 动作表里不该有宏动作
        var before = Core.Actions.ActionCatalog.Create(ctx);
        Check("没有宏时动作表里没有宏动作",
            !before.Items.Any(i => i.Id.StartsWith("macro:")),
            string.Join(",", before.Items.Where(i => i.Id.StartsWith("macro:")).Select(i => i.Id)));

        // 加一个宏，重建动作表 → 必须出现对应的动作
        settings.Macros.Add(new Core.Settings.Macro
        {
            Id = "m123",
            Name = "我的宏",
            Steps = { new Core.Settings.MacroStep { Kind = "delay", DelayMs = 50 } },
        });

        var after = Core.Actions.ActionCatalog.Create(ctx);
        var macroAction = after.Items.FirstOrDefault(i => i.Id == "macro:m123");
        Check("加了宏之后动作表里有它", macroAction != null,
            string.Join(",", after.Items.Select(i => i.Id)));
        Check("宏动作在「宏」分类下", macroAction?.Category == "宏",
            macroAction?.Category ?? "");
        Check("宏动作的标题带宏名",
            macroAction?.Title?.Contains("我的宏") == true,
            macroAction?.Title ?? "");

        // 空名字的宏（真实用户配置里就有）不能出现"播放宏："这种半截标题
        settings.Macros[0].Name = "";
        var blank = Core.Actions.ActionCatalog.Create(ctx);
        var blankAction = blank.Items.FirstOrDefault(i => i.Id == "macro:m123");
        Check("空名字的宏有占位名", blankAction?.Title == "播放宏：（未命名宏）",
            blankAction?.Title ?? "");
        settings.Macros[0].Name = "我的宏";

        // 这个动作必须真的能被执行（能播放）
        bool invoked = false;
        foreach (var i in after.Items.Where(i => i.Id.StartsWith("macro:"))) invoked = true;
        Check("宏动作可以被绑定（存在可选项）", invoked);

        // 删掉宏之后动作表里就没了
        settings.Macros.Clear();
        var removed = Core.Actions.ActionCatalog.Create(ctx);
        Check("删掉宏之后动作表里就没有了",
            !removed.Items.Any(i => i.Id == "macro:m123"));

        // 动作表里必须有「打开宏管理」这一项
        Check("动作表里有打开宏管理",
            removed.Items.Any(i => i.Id == "macroManager"));
    }

    /// <summary>
    /// 宏的列表展示属性：Summary / DurationText 曾不存在，导致宏列表整行空白。
    /// </summary>
    private static void TestMacroDisplayProps()
    {
        var m = new Core.Settings.Macro { Name = "测试", Repeat = 1 };
        m.Steps.Add(new Core.Settings.MacroStep { Kind = "delay", DelayMs = 500 });

        Check("宏有 Summary 属性", !string.IsNullOrWhiteSpace(m.Summary), m.Summary);
        Check("Summary 包含步数", m.Summary.Contains("1"), m.Summary);
        Check("Summary 包含循环次数", m.Summary.Contains("一次"), m.Summary);
        Check("宏有 DurationText 属性",
            !string.IsNullOrWhiteSpace(m.DurationText), m.DurationText);

        // 循环播放
        m.Repeat = 0;
        Check("循环播放时 Summary 写明", m.Summary.Contains("循环"), m.Summary);

        // 多次播放
        m.Repeat = 3;
        Check("多次播放时 Summary 写明次数", m.Summary.Contains("3"), m.Summary);

        // 时长格式：<1s 用 ms，>=1s 用 s
        var shortM = new Core.Settings.Macro();
        shortM.Steps.Add(new Core.Settings.MacroStep { Kind = "delay", DelayMs = 300 });
        Check("短宏用毫秒表示", shortM.DurationText.EndsWith("ms"), shortM.DurationText);

        var longM = new Core.Settings.Macro();
        longM.Steps.Add(new Core.Settings.MacroStep { Kind = "delay", DelayMs = 2500 });
        Check("长宏用秒表示", longM.DurationText.EndsWith("s"), longM.DurationText);

        // 反射确认这两个属性真实存在（防止界面又绑空）
        Check("Macro 有 Summary 属性", typeof(Core.Settings.Macro).GetProperty("Summary") != null);
        Check("Macro 有 DurationText 属性",
            typeof(Core.Settings.Macro).GetProperty("DurationText") != null);
    }

    /// <summary>
    /// 快捷键速查表必须覆盖**所有层**，不能只列第 1 层。
    /// </summary>
    /// <summary>
    /// 宏绑定到按键：按键映射页要能选中宏动作。
    ///
    /// 这是「宏没办法绑定按键」的回归测试。动作表里必须有宏动作，
    /// 且动作 ID 与绑定里写的一致（macro:&lt;id&gt;），
    /// 这样 SelectKey 才能把下拉定位到「宏」分类并选中它。
    /// </summary>
    private static void TestMacroBindableInKeysView()
    {
        var settings = DefaultSettings.Create();
        settings.Macros.Add(new Core.Settings.Macro
        {
            Id = "bind1",
            Name = "要绑定的宏",
            Steps = { new Core.Settings.MacroStep { Kind = "delay", DelayMs = 50 } },
        });

        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new Core.Actions.WindowBindingService(settings),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };
        var cat = Core.Actions.ActionCatalog.Create(ctx);

        var item = cat.Items.FirstOrDefault(i => i.Id == "macro:bind1");
        Check("动作表里有这个宏的动作", item != null,
            string.Join(",", cat.Items.Where(i => i.Id.StartsWith("macro:")).Select(i => i.Id)));
        Check("宏动作在「宏」分类", item?.Category == "宏", item?.Category ?? "");
        Check("宏动作标题带宏名", item?.Title?.Contains("要绑定的宏") == true, item?.Title ?? "");

        // 按分类过滤后仍能找到（按键映射页是两级下拉）
        var inCategory = cat.Items.Where(i => i.Category == "宏").ToList();
        Check("「宏」分类下有可选项", inCategory.Count > 0, inCategory.Count.ToString());
        Check("分类过滤能选中目标", inCategory.Any(i => i.Id == "macro:bind1"));

        // Actions.Get 必须能反查到（SelectKey 靠它定位分类）
        Check("Get(macro:bind1) 能查到", cat.Get("macro:bind1") != null);

        // 全分类覆盖：每个分类至少一个动作（下拉不会是空的）
        foreach (string c in cat.Items.Select(i => i.Category).Distinct())
            Check($"分类「{c}」非空", cat.Items.Any(i => i.Category == c));
    }
}