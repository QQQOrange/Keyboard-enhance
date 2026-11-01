using System.IO;
using System.Text;
using System.Xml.Linq;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Qbar;
using CapsLockPlus.Core.Localization;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Text;

namespace CapsLockPlus;

public static partial class SelfTest
{
    /// <summary>
    /// 自启载体的选择逻辑。
    ///
    /// 背景：管理员模式下用 HKCU\Run 自启，每次登录都会弹 UAC；改用计划任务
    /// （HighestAvailable）可以静默提权启动。两种载体必须互斥，否则开机会起两个实例。
    /// </summary>
    private static void TestStartupModeDecision()
    {
        CheckEqual("自启：关闭时不用任何载体",
            StartupManager.Decide(false, false), StartupMode.None);
        CheckEqual("自启：关闭时即使管理员模式也不用任务",
            StartupManager.Decide(false, true), StartupMode.None);
        CheckEqual("自启：普通权限用注册表",
            StartupManager.Decide(true, false), StartupMode.RunKey);
        CheckEqual("自启：管理员模式用计划任务",
            StartupManager.Decide(true, true), StartupMode.Task);
    }

    /// <summary>
    /// 任务清单的生成。
    ///
    /// 这份 XML 写错时，schtasks 只会回一句"格式不正确的值"，
    /// 完全不知道错在哪 —— 所以必须在自检里把它拆开逐项验证。
    /// </summary>
    private static void TestStartupTaskXml()
    {
        const string exe = @"C:\Program Files\Key & Enhance\KeyboardEnhance.exe";
        string xml = StartupTask.BuildXml(exe, @"DESKTOP-1\Tom & Jerry");

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex)
        {
            Check($"任务 XML 可解析：{ex.Message}", false);
            return;
        }
        Check("任务 XML 可解析", true);

        var ns = doc.Root!.GetDefaultNamespace();
        string Val(string name) =>
            doc.Descendants(ns + name).FirstOrDefault()?.Value ?? "";

        CheckEqual("任务：声明最高权限", Val("RunLevel"), "HighestAvailable");
        CheckEqual("任务：登录时触发", Val("LogonTrigger").Length > 0 ? "有" : "无", "有");
        CheckEqual("任务：启动参数", Val("Arguments"), StartupTask.StartupArgs);
        Check("任务：exe 路径已转义",
            Val("Command") == exe, Val("Command"));
        Check("任务：用户名已转义",
            Val("UserId").Contains("&amp;") || Val("UserId").Contains("Tom & Jerry"),
            Val("UserId"));
        CheckEqual("任务：电池供电也启动", Val("DisallowStartIfOnBatteries"), "false");
        CheckEqual("任务：不设执行时限", Val("ExecutionTimeLimit"), "PT0S");
        Check("任务：任务名一致", Val("URI").Contains(StartupTask.TaskName), Val("URI"));
    }

    /// <summary>
    /// 注册/删除任务时的命令行与文件编码。
    ///
    /// 用注入的假 runner 跑：既能验证参数，又不需要真的提权。
    /// 特别验证 XML 以 UTF-16（带 BOM）落盘 —— schtasks 只认这种编码。
    /// </summary>
    private static void TestStartupTaskCommands()
    {
        var saved = StartupTask.RunnerForTest;
        try
        {
            var calls = new List<string[]>();
            bool firstByteOfBom = false;
            string capturedXml = "";

            StartupTask.RunnerForTest = args =>
            {
                calls.Add(args);
                if (args.Length >= 5 && args[1] == "/Create")
                {
                    byte[] head = File.ReadAllBytes(args[3]);
                    firstByteOfBom = head.Length > 1 && head[0] == 0xFF && head[1] == 0xFE;
                    capturedXml = File.ReadAllText(args[3], Encoding.Unicode);
                }
                return (0, "");
            };

            bool ok = StartupTask.Create(@"D:\x\KeyboardEnhance.exe", @"PC\U");
            Check("注册任务：调用成功", ok);
            Check("注册任务：覆盖同名任务", calls.Any(c => c.Contains("/F")), string.Join(" ", calls.LastOrDefault() ?? Array.Empty<string>()));
            Check("注册任务：带任务名", calls.Any(c => c.Contains(StartupTask.TaskName)));
            Check("注册任务：XML 为 UTF-16 且带 BOM", firstByteOfBom);
            Check("注册任务：XML 含完整路径",
                capturedXml.Contains(@"D:\x\KeyboardEnhance.exe"), capturedXml.Length.ToString());

            StartupTask.RunnerForTest = _ => (1, "拒绝访问");
            Check("注册任务：失败时返回 false",
                !StartupTask.Create(@"D:\x\KeyboardEnhance.exe", @"PC\U"));

            StartupTask.RunnerForTest = _ => (0, "");
            Check("删除任务：成功", StartupTask.Delete());
        }
        finally
        {
            StartupTask.RunnerForTest = saved;
        }
    }

    /// <summary>
    /// 取词代理的命令行解析。
    ///
    /// 这个开关决定"当前进程是主程序还是一次性代理"，解析错了会出现
    /// "代理把自己当主程序启动"或"主程序被当成代理直接退出"，
    /// 两者都很难看出原因，所以单独测。
    /// </summary>
    private static void TestSelectionProxyArgs()
    {
        Check("代理参数：普通启动不识别",
            SelectionProxy.ParseHelperArg(new[] { "--tray", "--trace" }) == null);

        CheckEqual("代理参数：识别 --pick 与管道名",
            SelectionProxy.ParseHelperArg(new[] { "--pick", "KeyEnhancePick_abc" }),
            "KeyEnhancePick_abc");

        CheckEqual("代理参数：参数顺序无关",
            SelectionProxy.ParseHelperArg(new[] { "--trace", "--pick", "P1" }),
            "P1");

        CheckEqual("代理参数：只给开关不给名字也不启动主程序",
            SelectionProxy.ParseHelperArg(new[] { "--pick", "--tray" }),
            "");

        Check("代理参数：大小写不敏感",
            SelectionProxy.ParseHelperArg(new[] { "--PICK", "P2" }) == "P2");
    }
    /// <summary>
    /// 动作失败通知的文案。
    ///
    /// 要求：必须能看出"这是失败"、带动作名、带原因、带发生时间，
    /// 且与成功提示共用同一条出口（同一个 Toast）。
    /// 这里只测文案组装 —— 出口是 UI，自检环境不能真弹窗。
    /// </summary>
    private static void TestActionFailureText()
    {
        var at = new DateTime(2026, 11, 1, 14, 37, 2);
        string title = Loc.T("打开网址");

        string withReason = UiText.FailedAt("打开网址", "拒绝访问", at);
        Check("失败文案：带动作名", withReason.Contains(title), withReason);
        Check("失败文案：带原因", withReason.Contains("拒绝访问"), withReason);
        Check("失败文案：带时间", withReason.Contains("14:37:02"), withReason);
        // 通知实际用的是 FailLine(FailedAt(...))，这里按同样的组合验证
        string toast = UiText.FailLine(withReason);
        Check("失败通知：带失败标记 ✗", toast.StartsWith(UiText.FailMark), toast);
        Check("失败通知：标记后紧跟动作名", toast.Contains(title), toast);

        string noReason = UiText.FailedAt("打开网址", null, at);
        Check("失败文案：没有原因时仍完整",
            noReason.Contains(title) && noReason.Contains("14:37:02"), noReason);

        string trimmed = UiText.FailedAt("打开网址", "拒绝访问。", at);
        Check("失败文案：原因末尾句号被去掉",
            trimmed.Contains("拒绝访问 ·") || trimmed.Contains("拒绝访问 "), trimmed);
    }

    /// <summary>
    /// 基础改键（不按住前缀键也生效的重映射）。
    ///
    /// 重点验证三件事：
    ///   ① 预设能成对写入、再点一次能整体取消；
    ///   ② 同一个源键只保留一条（否则字典里后者覆盖前者，界面却显示两条）；
    ///   ③ 非法键名与"自己映射自己"必须被忽略，不能让钩子白吞按键。
    /// </summary>
    private static void TestBaseRemap()
    {
        var list = new List<KeyRemap>();
        var capsEsc = KeyRemapMap.FindPreset("capsEsc");
        Check("改键：预设存在", capsEsc != null);
        if (capsEsc == null) return;

        Check("改键：启用返回 true", KeyRemapMap.Toggle(list, capsEsc));
        CheckEqual("改键：成对写入两条", list.Count, 2);

        var map = KeyRemapMap.Build(list);
        Check("改键：Caps 映射到 Esc",
            map.TryGetValue(0x14, out int toEsc) && toEsc == 0x1B, map.Count.ToString());

        Check("改键：再点一次返回 false", !KeyRemapMap.Toggle(list, capsEsc));
        CheckEqual("改键：取消后清空", list.Count, 0);

        // 同源键只保留一条
        list.Add(new KeyRemap { From = "capslock", To = "esc" });
        var capsCtrl = KeyRemapMap.FindPreset("capsCtrl");
        if (capsCtrl != null) KeyRemapMap.Toggle(list, capsCtrl);
        CheckEqual("改键：同源键只保留一条",
            list.Count(r => string.Equals(r.From, "capslock", StringComparison.OrdinalIgnoreCase)), 1);
        Check("改键：保留后写入的目标",
            KeyRemapMap.Build(list).TryGetValue(0x14, out int toCtrl) && toCtrl == 0xA2);

        // 非法与自映射
        var bad = KeyRemapMap.Build(new[]
        {
            new KeyRemap { From = "", To = "esc" },
            new KeyRemap { From = "tab", To = "" },
            new KeyRemap { From = "tab", To = "tab" },
            new KeyRemap { From = "不存在的键", To = "esc" },
        });
        CheckEqual("改键：非法与自映射被忽略", bad.Count, 0);

        Check("改键：指纹随内容变化",
            KeyRemapMap.Stamp(new[] { new KeyRemap { From = "a", To = "b" } }) !=
            KeyRemapMap.Stamp(new[] { new KeyRemap { From = "a", To = "c" } }));

        Check("改键：描述含箭头",
            KeyRemapMap.Describe(new[] { new KeyRemap { From = "capslock", To = "esc" } }).Contains("→"));
    }
    /// <summary>
    /// 使用统计：累计、排行、今日、空 id 不计数，以及"写坏文件不影响主流程"。
    ///
    /// 用临时目录写，避免污染真实数据目录；结束前恢复覆盖点。
    /// </summary>
    private static void TestUsageStats()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-usage-" + Guid.NewGuid().ToString("N"));
        string? saved = UsageStats.PathOverride;
        try
        {
            Directory.CreateDirectory(dir);
            UsageStats.PathOverride = dir;
            UsageStats.Reset();

            UsageStats.Record("tab");
            UsageStats.Record("tab");
            UsageStats.Record("qbar");

            var sum = UsageStats.Summary();
            CheckEqual("统计：累计次数", sum.Total, 3);
            CheckEqual("统计：今日次数", sum.Today, 3);
            CheckEqual("统计：最常用动作", sum.TopAction, "tab");
            CheckEqual("统计：最常用次数", sum.TopCount, 2);

            UsageStats.Record("");
            UsageStats.Record(null);
            CheckEqual("统计：空动作不计数", UsageStats.Summary().Total, 3);

            UsageStats.Flush();
            Check("统计：落盘后文件存在",
                File.Exists(Path.Combine(dir, "usage.json")));

            // 坏文件不能把统计模块带崩
            File.WriteAllText(Path.Combine(dir, "usage.json"), "{ not json");
            UsageStats.Reset();
            UsageStats.Record("x");
            CheckEqual("统计：坏文件后仍可继续累计", UsageStats.Summary().Total, 1);
        }
        finally
        {
            UsageStats.PathOverride = saved;
            try { Directory.Delete(dir, true); } catch { }   /* 清理失败无害 */
        }
    }
    /// <summary>
    /// 按应用自动启用层（叠加层）。
    ///
    /// 两条硬要求：
    ///   ① 命中规则时，**只接管该层绑过的组合**；
    ///   ② 没绑过的键必须原样放行 —— 否则在 IDE 里打字会被吞掉。
    /// </summary>
    private static void TestAppLayerOverlay()
    {
        CheckEqual("按应用层：规范化完整路径",
            AppLayerRules.Normalize(@"C://Program Files//X//Code.EXE"), "code.exe");
        CheckEqual("按应用层：自动补 exe", AppLayerRules.Normalize("Code"), "code.exe");
        CheckEqual("按应用层：空值", AppLayerRules.Normalize("  "), "");

        var rules = new[] { new AppLayerRule { Process = "code.exe", LayerId = 2 } };
        CheckEqual("按应用层：命中返回层", AppLayerRules.Match(rules, "Code.exe"), 2);
        CheckEqual("按应用层：不命中返回 0", AppLayerRules.Match(rules, "notepad.exe"), 0);
        CheckEqual("按应用层：空规则返回 0", AppLayerRules.Match(null, "code.exe"), 0);

        var s = DefaultSettings.Create();
        foreach (var t in s.Triggers) t.Enabled = false;

        var layer = s.Layers.First();
        /* 清空默认绑定：出厂配置里有 WASD 之类的现成绑定，
         * 不清掉的话"未绑定的键"其实是有绑定的，测不出真实行为。 */
        layer.Keys.Clear();
        // 组合键 id 的存储约定是 caps_ 前缀（历史原因），不要手写 "q"
        layer.Keys.Add(new KeyBinding { Combo = KeyName.BuildComboEx(KeyModifiers.None, "q"), Action = "qbar" });

        var fired = new List<string>();
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => s, (a, arg) =>
        {
            lock (fired) fired.Add(a);
            return true;
        });

        engine.AppLayerId = 0;
        Check("按应用层：未启用时 Q 放行", !engine.SimulateKey(0x51, true));
        engine.SimulateKey(0x51, false);

        engine.AppLayerId = layer.Id;
        Check("按应用层：命中绑定被吞", engine.SimulateKey(0x51, true));
        engine.SimulateKey(0x51, false);
        DoEvents(60);
        lock (fired) Check("按应用层：绑定的动作被执行", fired.Contains("qbar"), string.Join(",", fired));

        Check("按应用层：未绑定的键照常放行", !engine.SimulateKey(0x57, true));
        engine.SimulateKey(0x57, false);

        engine.Dispose();
        queue.Dispose();
    }
    /// <summary>
    /// 双击动作：任意键连按两次触发动作。
    ///
    /// 三条语义：
    ///   ① 第一次按下必须**放行**（该打字就打字）；
    ///   ② 窗口内的第二次按下被吞掉，并执行动作；
    ///   ③ 超过窗口不算双击，回落成普通按键。
    /// </summary>
    private static void TestDoubleTapGesture()
    {
        var s = DefaultSettings.Create();
        foreach (var t in s.Triggers) t.Enabled = false;
        s.Global.KeyGestures.Add(new KeyGesture { Key = "esc", DoubleTapAction = "qbar" });

        var fired = new List<string>();
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => s, (a, arg) =>
        {
            lock (fired) fired.Add(a);
            return true;
        });

        Check("双击：第一次按下放行", !engine.SimulateKey(0x1B, true));
        engine.SimulateKey(0x1B, false);

        Check("双击：第二次按下被吞", engine.SimulateKey(0x1B, true));
        Check("双击：第二次抬起也被吞", engine.SimulateKey(0x1B, false));
        DoEvents(60);
        lock (fired) Check("双击：动作被执行", fired.Contains("qbar"), string.Join(",", fired));

        // 超过窗口：回落为普通按键
        engine.SimulateKey(0x1B, true);
        engine.SimulateKey(0x1B, false);
        System.Threading.Thread.Sleep(HotkeyEngine.DoubleTapWindowMs + 150);
        Check("双击：超过窗口不算双击", !engine.SimulateKey(0x1B, true));
        engine.SimulateKey(0x1B, false);

        engine.Dispose();
        queue.Dispose();
    }
    /// <summary>
    /// 文本片段的匹配规则。
    ///
    /// 这是全仓最容易"误伤正常输入"的功能，所以规则必须能自证：
    ///   ① 触发词要以非字母数字开头；
    ///   ② 必须落在词边界（`x;mail` 不能触发 `;mail`）；
    ///   ③ 多个命中取最长。
    /// </summary>
    private static void TestSnippets()
    {
        Check("片段：空触发词非法", !SnippetMatcher.IsValidTrigger(""));
        Check("片段：纯字母触发词非法", !SnippetMatcher.IsValidTrigger("mail"));
        Check("片段：含空格非法", !SnippetMatcher.IsValidTrigger(";ma il"));
        Check("片段：符号开头合法", SnippetMatcher.IsValidTrigger(";mail"));

        var list = new List<Snippet>
        {
            new() { Trigger = ";mail", Text = "a@b.com" },
            new() { Trigger = ";m", Text = "short" },
        };

        CheckEqual("片段：命中较长者", SnippetMatcher.Match(list, ";mail")?.Text, "a@b.com");
        CheckEqual("片段：短触发词也能命中", SnippetMatcher.Match(list, ";m")?.Text, "short");
        Check("片段：词中间不触发", SnippetMatcher.Match(list, "x;mail") == null);
        Check("片段：无匹配返回空", SnippetMatcher.Match(list, "hello") == null);
        Check("片段：大小写敏感", SnippetMatcher.Match(list, ";Mail") == null);

        string longBuf = new string('a', SnippetMatcher.MaxBuffer + 30);
        CheckEqual("片段：缓冲区截断到上限",
            SnippetMatcher.Trim(longBuf).Length, SnippetMatcher.MaxBuffer);
    }
    /// <summary>
    /// 组合键（两键同按）。
    ///
    /// 关键是要证明"扣住 → 补发"这条链路不会吞掉用户的正常输入：
    ///   ① 两个键都配置过时，第一个键被扣住不输出；
    ///   ② 配对的第二个键到来 → 执行动作，两个键都不输出；
    ///   ③ 来了不相干的键 → 先补发扣住的键，再放行新键；
    ///   ④ 单独点按 → 抬起时立刻补发（按下+抬起）。
    /// </summary>
    private static void TestChords()
    {
        var s = DefaultSettings.Create();
        foreach (var t in s.Triggers) t.Enabled = false;
        s.Global.Chords.Add(new ChordRule { Key1 = "j", Key2 = "k", Action = "qbar" });

        var fired = new List<string>();
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => s, (a, arg) =>
        {
            lock (fired) fired.Add(a);
            return true;
        });

        // ① 配对：j 被扣，k 触发
        Check("组合键：第一个键被扣住", engine.SimulateKey(0x4A, true));          // j
        Check("组合键：配对的第二个键被吞", engine.SimulateKey(0x4B, true));      // k
        engine.SimulateKey(0x4B, false);
        engine.SimulateKey(0x4A, false);
        DoEvents(60);
        lock (fired) Check("组合键：动作被执行", fired.Contains("qbar"), string.Join(",", fired));

        // ② 不相干的键：先补发扣住的键，再放行
        lock (fired) fired.Clear();
        Check("组合键：重新扣住第一个键", engine.SimulateKey(0x4A, true));
        Check("组合键：不相干的键照常放行", !engine.SimulateKey(0x4C, true));    // l
        engine.SimulateKey(0x4C, false);
        engine.SimulateKey(0x4A, false);
        DoEvents(60);
        lock (fired) Check("组合键：不相干时不触发动作", !fired.Contains("qbar"), string.Join(",", fired));

        // ③ 单独点按：抬起时立刻补发
        Check("组合键：单击也先扣住", engine.SimulateKey(0x4A, true));
        Check("组合键：单击的抬起被接管", engine.SimulateKey(0x4A, false));
        DoEvents(60);

        engine.Dispose();
        queue.Dispose();
    }
    /// <summary>
    /// Qbar 搜动作：把动作目录接进检索，并确保回车能真的执行。
    ///
    /// 这条链路以前是断的 —— 124 个动作只能靠在层里一个一个绑，
    /// 用户根本不知道有哪些；接进 Qbar 之后"想起来就能用"。
    /// </summary>
    private static void TestQbarActionSearch()
    {
        var s = DefaultSettings.Create();
        var actions = new List<ActionInfo>
        {
            new("qbar", "面板", "打开 Qbar", "命令面板"),
            new("tab", "基础", "Tab", "发送 Tab"),
        };

        var svc = new QbarService(() => s, () => new List<string>(), () => actions);

        var hits = svc.Query("Qbar", null, 12);
        Check("Qbar：能搜到动作",
            hits.Any(r => r.Kind == QbarKind.Action && r.Payload == "qbar"),
            string.Join(",", hits.Select(h => h.Title)));

        var byCategory = svc.Query("面板", null, 12);
        Check("Qbar：按分类也能搜到", byCategory.Any(r => r.Payload == "qbar"));

        var none = svc.Query("zzzz-不存在", null, 12);
        Check("Qbar：无匹配时不硬塞动作", none.All(r => r.Kind != QbarKind.Action));

        string? ran = null;
        var cb = new QbarService.QbarCallbacks(
            Notify: _ => { }, CopyToClipboard: _ => { }, Paste: _ => { },
            OpenTarget: _ => { }, RunTarget: (_, _, _) => { }, RunCommand: _ => { },
            RunAction: a => ran = a);

        var actionHit = hits.First(r => r.Kind == QbarKind.Action);
        svc.Execute(actionHit, cb);
        CheckEqual("Qbar：回车执行到动作", ran, "qbar");
    }
}
