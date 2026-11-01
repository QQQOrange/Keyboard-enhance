using System.IO;
using System.Text;
using System.Xml.Linq;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Macro;
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
    /// <summary>
    /// 场景模板：合并语义。
    ///
    /// 关键要求是"重复点击不叠加"——否则用户点两次就会多出一堆重复绑定，
    /// 而重复绑定的表现是"按键行为变得莫名其妙"，极难排查。
    /// </summary>
    private static void TestScenarioPacks()
    {
        var s = DefaultSettings.Create();
        var pack = ScenarioPacks.Find("vim");
        Check("模板：内置包存在", pack != null);
        if (pack == null) return;

        int layerId = s.Layers.First().Id;
        // 清空默认绑定：出厂配置自带 WASD 等绑定，不清掉测不出"新增"的真实行为
        s.Layers.First().Keys.Clear();
        var (added1, _) = ScenarioPacks.Apply(s, pack, layerId);
        Check("模板：首次有新增", added1 > 0, added1.ToString());

        var (added2, skipped2) = ScenarioPacks.Apply(s, pack, layerId);
        Check("模板：二次不重复添加", added2 == 0, added2.ToString());
        Check("模板：二次全部被跳过", skipped2 > 0, skipped2.ToString());

        var layer = s.Layers.First();
        string combo = KeyName.BuildComboEx(KeyModifiers.None, "h");
        Check("模板：层内绑定已写入",
            layer.Keys.Any(k => k.Combo == combo && k.Action == "moveLeft"));
        Check("模板：基础改键已写入",
            s.Global.BaseRemaps.Any(r => r.From == "capslock" && r.To == "esc"));
    }
    /// <summary>
    /// 规则分享：导出 → 导入 → 重复导入。
    ///
    /// 关键要求：导入只能"加法"，且重复导入不叠加。
    /// 分享码是别人给的，不能因为对方多写一条就把我的配置改坏。
    /// </summary>
    private static void TestRuleShare()
    {
        var src = DefaultSettings.Create();
        src.Global.BaseRemaps.Add(new KeyRemap { From = "capslock", To = "esc" });
        src.Global.Snippets.Add(new Snippet { Trigger = ";mail", Text = "a@b.com" });

        string code = RuleShare.Export(src);
        Check("分享：导出含改键", code.Contains("capslock") && code.Contains("\"esc\""), code);
        Check("分享：导出含片段", code.Contains(";mail"));

        var dst = DefaultSettings.Create();
        dst.Global.BaseRemaps.Clear();
        dst.Global.Snippets.Clear();

        var (added1, _) = RuleShare.Import(dst, code, out string? err1);
        Check("分享：导入无错误", err1 == null, err1 ?? "");
        CheckEqual("分享：导入条数", added1, 2);
        Check("分享：改键已落地", dst.Global.BaseRemaps.Any(r => r.From == "capslock" && r.To == "esc"));
        Check("分享：片段已落地", dst.Global.Snippets.Any(x => x.Trigger == ";mail"));

        var (added2, skipped2) = RuleShare.Import(dst, code, out _);
        Check("分享：重复导入不叠加", added2 == 0, added2.ToString());
        Check("分享：重复导入全跳过", skipped2 == 2, skipped2.ToString());

        var (added3, _) = RuleShare.Import(dst, "{ 这不是 json", out string? err3);
        Check("分享：坏码返回错误", err3 != null);
        CheckEqual("分享：坏码不改配置", added3, 0);
    }
    /// <summary>
    /// 片段展开前的"可编辑性"判定。
    ///
    /// 策略是"只对明确不可编辑的跳过"：拿不到信息时一律允许，
    /// 否则游戏/自绘界面里的片段会整体失效。
    /// </summary>
    private static void TestEditableTargetRule()
    {
        // 明确拒绝的
        Check("可编辑性：按钮上跳过", EditableTarget.ShouldSkip("Button", null, null));
        Check("可编辑性：菜单项上跳过", EditableTarget.ShouldSkip("MenuItem", null, null));
        Check("可编辑性：只读值跳过", EditableTarget.ShouldSkip("Edit", true, null));
        Check("可编辑性：只读文本跳过", EditableTarget.ShouldSkip("Document", null, true));

        // 允许的
        Check("可编辑性：编辑框允许", !EditableTarget.ShouldSkip("Edit", false, null));
        Check("可编辑性：文档允许", !EditableTarget.ShouldSkip("Document", null, false));
        Check("可编辑性：未知类型默认允许", !EditableTarget.ShouldSkip("Pane", null, null));
        Check("可编辑性：完全取不到信息也允许", !EditableTarget.ShouldSkip(null, null, null));
    }
    /// <summary>
    /// 宏的流程控制：配对表 + 跳转 + 条件求值。
    ///
    /// 这里验的是**播放器实际调用的那几个函数**（SkipTo / AfterElse / LoopOf /
    /// ShouldRepeat / Matches），而不是另写一套模拟逻辑 —— 否则测过了也不代表播放对。
    /// </summary>
    private static void TestMacroFlow()
    {
        static MacroStep S(string kind) => new() { Kind = kind };

        // ---- 简单 if / else / endIf ----
        var steps = new List<MacroStep> { S("if"), S("text"), S("else"), S("text"), S("endIf"), S("delay") };
        var flow = MacroFlow.Compile(steps);
        Check("宏流程：简单配对成立", flow.Ok, flow.Error ?? "");
        CheckEqual("宏流程：else 位置", flow.ElseOf(0), 2);
        CheckEqual("宏流程：endIf 位置", flow.EndOfIf(0), 4);
        CheckEqual("宏流程：条件不成立跳到 else 之后", flow.SkipTo(0), 3);
        CheckEqual("宏流程：then 走完跳到 endIf 之后", flow.AfterElse(2), 5);

        // ---- 没有 else ----
        var noElse = MacroFlow.Compile(new List<MacroStep> { S("if"), S("text"), S("endIf") });
        Check("宏流程：无 else 也成立", noElse.Ok, noElse.Error ?? "");
        CheckEqual("宏流程：无 else 时跳到 endIf 之后", noElse.SkipTo(0), 3);

        // ---- 嵌套：if 里套 loop ----
        var nested = MacroFlow.Compile(new List<MacroStep>
        {
            S("if"), S("loop"), S("text"), S("endLoop"), S("else"), S("endIf"),
        });
        Check("宏流程：嵌套配对成立", nested.Ok, nested.Error ?? "");
        CheckEqual("宏流程：loop 与 endLoop 配对", nested.LoopOf(3), 1);
        CheckEqual("宏流程：嵌套里的 endIf", nested.EndOfIf(0), 5);
        CheckEqual("宏流程：嵌套循环不成立时跳到 else 之后", nested.SkipTo(0), 5);

        // ---- 配对错误 ----
        var loneElse = MacroFlow.Compile(new List<MacroStep> { S("else") });
        Check("宏流程：孤立的否则报错", !loneElse.Ok);
        Check("宏流程：报错含步号", (loneElse.Error ?? "").Contains("第 1 步"), loneElse.Error ?? "");

        Check("宏流程：孤立的结束如果报错",
            !MacroFlow.Compile(new List<MacroStep> { S("endIf") }).Ok);
        Check("宏流程：未闭合的如果报错",
            !MacroFlow.Compile(new List<MacroStep> { S("if"), S("text") }).Ok);
        Check("宏流程：未闭合的循环报错",
            !MacroFlow.Compile(new List<MacroStep> { S("loop"), S("text") }).Ok);
        Check("宏流程：结束循环配到如果上要报错",
            !MacroFlow.Compile(new List<MacroStep> { S("if"), S("endLoop") }).Ok);

        // ---- 循环次数 ----
        Check("宏流程：次数未到要继续", flow.ShouldRepeat(0, 1, 3));
        Check("宏流程：次数到了要退出", !flow.ShouldRepeat(0, 3, 3));
        Check("宏流程：0 表示一直循环", flow.ShouldRepeat(0, 999, 0));

        // ---- 无流程标记的老宏：不能因为编译而改变行为 ----
        var plain = MacroFlow.Compile(new List<MacroStep> { S("text"), S("delay") });
        Check("宏流程：老宏没有流程标记", plain.Ok && !plain.HasFlow);

        // ---- 条件求值 ----
        var ctx = new MacroContext("code.exe", "未保存 - 项目", "hello world");
        Check("宏条件：进程包含（不区分大小写）",
            MacroCondition.Matches(MacroCondition.App, "Code", false, ctx.Process, ctx.Title, ctx.Clipboard));
        Check("宏条件：标题包含",
            MacroCondition.Matches(MacroCondition.Title, "未保存", false, ctx.Process, ctx.Title, ctx.Clipboard));
        Check("宏条件：剪贴板包含",
            MacroCondition.Matches(MacroCondition.Clipboard, "WORLD", false, ctx.Process, ctx.Title, ctx.Clipboard));
        Check("宏条件：不匹配", 
            !MacroCondition.Matches(MacroCondition.App, "notepad", false, ctx.Process, ctx.Title, ctx.Clipboard));
        Check("宏条件：取反",
            MacroCondition.Matches(MacroCondition.App, "notepad", true, ctx.Process, ctx.Title, ctx.Clipboard));

        Check("宏条件：空值恒不成立（避免刚建就命中）",
            !MacroCondition.Matches(MacroCondition.App, "", false, ctx.Process, ctx.Title, ctx.Clipboard));
        Check("宏条件：空值取反则恒成立",
            MacroCondition.Matches(MacroCondition.App, "   ", true, ctx.Process, ctx.Title, ctx.Clipboard));

        Check("宏条件：剪贴板为空",
            MacroCondition.Matches(MacroCondition.ClipboardEmpty, "", false, "", "", ""));
        Check("宏条件：剪贴板非空则不成立",
            !MacroCondition.Matches(MacroCondition.ClipboardEmpty, "", false, "", "", "x"));
        Check("宏条件：未知类型当不成立",
            !MacroCondition.Matches("weird", "x", false, ctx.Process, ctx.Title, ctx.Clipboard));

        // ---- 克隆要带上新字段（漏了会导致改一步影响别处）----
        var src = new MacroStep
        {
            Kind = "if", Cond = MacroCondition.Title, CondValue = "abc", Not = true, Count = 7,
        };
        var copy = src.Clone();
        Check("宏步骤：克隆保留条件",
            copy.Cond == MacroCondition.Title && copy.CondValue == "abc" && copy.Not);
        CheckEqual("宏步骤：克隆保留次数", copy.Count, 7);

        // ---- 时长估算要把循环体乘起来 ----
        var m = new Macro { Steps = new List<MacroStep>
        {
            S("loop"), new() { Kind = "delay", DelayMs = 100 }, S("endLoop"),
        }};
        m.Steps[0].Count = 5;
        CheckEqual("宏时长：循环体按次数放大", m.EstimatedMs, 500);

        /* ---- 真实播放一次（宏里只有流程标记，不发送任何按键）----
         * 单测跳转函数不够：还要证明"播放器真的会沿着配对表走、并且会结束"。
         * 配错的宏走的是"拒绝播放"分支，一个字都不会打出去。 */
        using (var player = new MacroPlayer())
        {
            var flowMacro = new Macro
            {
                Name = "自检-流程",
                Steps = new List<MacroStep>
                {
                    new() { Kind = "loop", Count = 2 },
                    new() { Kind = "if", Cond = MacroCondition.App, CondValue = "__ke_never__" },
                    new() { Kind = "delay", DelayMs = 1 },
                    new() { Kind = "endIf" },
                    new() { Kind = "endLoop" },
                },
            };

            var done = new System.Threading.ManualResetEventSlim(false);
            Check("宏播放：含流程标记的宏能播", player.TryPlay(flowMacro, onCompleted: () => done.Set()));
            Check("宏播放：能正常结束（没有死循环）", done.Wait(3000));
        }

        using (var player = new MacroPlayer())
        {
            var broken = new Macro { Steps = new List<MacroStep> { new() { Kind = "loop" } } };
            Check("宏播放：配对错误时拒绝播放", !player.TryPlay(broken));
            Check("宏播放：拒绝时给出原因", !string.IsNullOrEmpty(player.LastError), player.LastError ?? "");
        }
    }
}
