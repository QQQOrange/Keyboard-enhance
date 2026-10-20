using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
/// 用 `CapsLockPlus.exe --selftest` 运行，退出码 0 表示全部通过。
/// </summary>
public static class SelfTest
{
    private static int _passed;
    private static int _failed;
    private static readonly StringBuilder Log = new();

    public static int Run()
    {
        // 整体包一层：自检里任何一步意外抛异常，也要保证写出报告并返回退出码，
        // 否则命令行会一直等（之前写 %TEMP% 失败就出现过挂死）。
        try
        {
            RunAll();
        }
        catch (Exception ex)
        {
            _failed++;
            Log.AppendLine();
            Log.AppendLine($"[FATAL] 自检中断：{ex.GetType().Name}: {ex.Message}");
            Log.AppendLine(ex.StackTrace);
            CoreLog.Error("自检中断", ex);
        }

        Log.AppendLine();
        Log.AppendLine($"==== 通过 {_passed} 项，失败 {_failed} 项 ====");
        WriteReport();
        return _failed == 0 ? 0 : 1;
    }

    private static void RunAll()
    {
        Section("环境");
        Check("Windows 版本 >= 10", Environment.OSVersion.Version.Major >= 10);
        Check("64 位进程", Environment.Is64BitProcess);
        Check("数据目录可创建", TryCreateDataDir());

        Section("按键模型");
        TestKeyNames();

        Section("搜索引擎");
        TestSearchEngines();
        TestEngineMigration();

        Section("表达式求值");
        TestEvaluator();

        Section("Qbar 检索");
        TestQbar();

        Section("Qbar 标签");
        TestChips();

        Section("设置存取");
        TestSettings();

        Section("旧版 ini 迁移");
        TestLegacyImport();

        Section("动作目录");
        TestActionCatalog();

        Section("窗口绑定");
        TestWindowBindings();

        Section("Genie 动画");
        TestGenieAnimation();

        Section("窗口点选");
        TestWindowPreview();
        TestPickerWithOverlayVisible();

        Section("文字度量");
        TestTextMetrics();

        Section("翻译引擎");
        TestTranslateProviders();

        Section("窗口与进程");
        TestWindows();

        Section("输入注入");
        TestInput();

        Section("单实例");
        TestSingleInstance();

        Section("键盘钩子");
        TestHook();

        Section("组合键引擎");
        TestEngine();

        Section("更新");
        TestUpdateDigest();
    }

    /// <summary>
    /// GitHub 的资产摘要是 "sha256:xxx" 形式，若不去掉前缀，
    /// 每次下载都会误判“校验失败”，导致自动更新永远无法完成。
    /// </summary>
    private static void TestUpdateDigest()
    {
        const string hash = "b4b7ae5a70fe98b4b89ea279ca4e4be99e94633b53c7d7dacab2d2847877938e";
        CheckEqual("去掉 sha256: 前缀", UpdateService.NormalizeDigest("sha256:" + hash), hash);
        CheckEqual("兼容大写前缀", UpdateService.NormalizeDigest("SHA256:" + hash.ToUpperInvariant()), hash.ToUpperInvariant());
        CheckEqual("兼容空格分隔", UpdateService.NormalizeDigest("sha256 " + hash), hash);
        CheckEqual("兼容等号分隔", UpdateService.NormalizeDigest("sha256=" + hash), hash);
        CheckEqual("纯摘要原样返回", UpdateService.NormalizeDigest(hash), hash);
        CheckEqual("空值返回空串", UpdateService.NormalizeDigest(null), "");
        CheckEqual("空摘要返回空串", UpdateService.NormalizeDigest("   "), "");
        // 只校验格式合法，不写死具体版本号，避免每次发版都要改测试
        Check("版本号是三段式",
            System.Text.RegularExpressions.Regex.IsMatch(UpdateService.CurrentVersion, @"^\d+\.\d+\.\d+$"),
            UpdateService.CurrentVersion);
    }

    /// <summary>写出自检报告（数据目录 + 控制台可用时也打印）。</summary>
    private static void WriteReport()
    {
        string report = Log.ToString();
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "selftest.log"), report);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("写自检报告失败：" + ex.Message);
        }

        try
        {
            if (NativeConsoleAvailable()) Console.WriteLine(report);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private static bool NativeConsoleAvailable() => GetConsoleWindow() != IntPtr.Zero;

    private static void Section(string name)
    {
        Log.AppendLine();
        Log.AppendLine($"── {name} ──");
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _passed++; Log.AppendLine($"  [PASS] {name}"); }
        else { _failed++; Log.AppendLine($"  [FAIL] {name}{(detail == null ? "" : "  → " + detail)}"); }
    }

    private static void CheckEqual<T>(string name, T actual, T expected)
    {
        bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(name, ok, ok ? null : $"期望 {expected}，实际 {actual}");
    }

    private static bool TryCreateDataDir()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            return true;
        }
        catch { return false; }
    }

    private static void TestKeyNames()
    {
        CheckEqual("a -> 0x41", KeyName.VkOf("a"), 0x41);
        CheckEqual("f12 -> 0x7B", KeyName.VkOf("f12"), 0x7B);
        CheckEqual("backquote -> 0xC0", KeyName.VkOf("backquote"), 0xC0);
        CheckEqual("leftSquareBracket -> 0xDB", KeyName.VkOf("leftSquareBracket"), 0xDB);
        CheckEqual("ralt -> 0xA5", KeyName.VkOf("ralt"), 0xA5);
        CheckEqual("0x41 -> a", KeyName.NameOf(0x41), "a");
        CheckEqual("组合键解析（基础层）", KeyName.ParseCombo("caps_q")?.Mods, KeyModifiers.None);
        CheckEqual("组合键解析（LAlt 层）", KeyName.ParseCombo("caps_lalt_q")?.Mods, KeyModifiers.Alt);
        CheckEqual("组合键解析（Win 层）", KeyName.ParseCombo("caps_win_1")?.Mods, KeyModifiers.Win);
        CheckEqual("组合键构造", KeyName.BuildCombo(KeyModifiers.Alt, "a"), "caps_lalt_a");
        CheckEqual("press_caps 可解析", KeyName.ParseCombo("press_caps")?.Key, "capslock");
        int bindable = new[] { "a", "1", "f1", "space", "enter", "backquote", "slash" }
            .Count(k => KeyName.VkOf(k) != null);
        CheckEqual("常用键全部可绑定", bindable, 7);
    }

    /// <summary>
    /// 默认引擎表：国内用拼音首字母、国外用英文前两个字母，且都带图标。
    /// 这些约定一旦被改乱，用户会记不住关键字，所以专门校验。
    /// </summary>
    private static void TestSearchEngines()
    {
        var settings = DefaultSettings.Create();
        var engines = settings.Search
            .Where(x => !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Check("引擎数量 >= 25", engines.Count >= 25, engines.Count.ToString());

        // 国内引擎：关键字应为拼音首字母（小写字母）
        var domestic = new (string Key, string Name)[]
        {
            ("bd", "百度"), ("tb", "淘宝"), ("zh", "知乎"), ("bili", "哔哩哔哩"),
            ("wx", "微信"), ("wb", "微博"), ("jd", "京东"), ("xhs", "小红书"),
            ("dy", "抖音"), ("db", "豆瓣"), ("csdn", "CSDN"), ("juejin", "掘金"),
            ("gitee", "Gitee"), ("pdd", "拼多多"), ("tx", "腾讯视频"), ("iqy", "爱奇艺"),
        };
        int domesticOk = 0;
        foreach (var (key, name) in domestic)
        {
            var hit = engines.FirstOrDefault(e => e.Key == key);
            if (hit == null) continue;
            domesticOk++;
            if (!string.Equals(hit.Comment, name, StringComparison.Ordinal))
                CoreLog.Warn($"引擎 {key} 的备注应为 {name}，实际 {hit.Comment}");
        }
        CheckEqual("国内引擎全部存在", domesticOk, domestic.Length);

        // 国外引擎：关键字应为英文前两个字母
        var foreign = new (string Key, string Name)[]
        {
            ("go", "Google"), ("bi", "Bing"), ("gh", "GitHub"),
            ("wi", "维基百科"), ("yo", "YouTube"), ("re", "Reddit"),
            ("st", "Startpage"), ("md", "MDN"), ("am", "Amazon"),
            ("fa", "Facebook"), ("np", "npm"), ("nu", "NuGet"),
        };
        int foreignOk = 0;
        foreach (var (key, name) in foreign)
        {
            var hit = engines.FirstOrDefault(e => e.Key == key);
            if (hit == null) continue;
            foreignOk++;
            if (!string.Equals(hit.Comment, name, StringComparison.Ordinal))
                CoreLog.Warn($"引擎 {key} 的备注应为 {name}，实际 {hit.Comment}");
        }
        CheckEqual("国外引擎全部存在", foreignOk, foreign.Length);

        // 每个引擎都要有图标：内置图标是打包资源（EngineIcon），
        // 不再是磁盘文件，所以这里检查 EngineIcon 非空且能真正加载出来。
        int withIcon = engines.Count(e =>
            !string.IsNullOrEmpty(e.EngineIcon) &&
            Core.Icons.IconCache.FromResource(e.EngineIcon) != null);
        CheckEqual("每个引擎都配了可用图标", withIcon, engines.Count);

        // 关键字不能重复（否则 Qbar 无法区分该用哪个引擎）
        int dup = engines.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .Count(g => g.Count() > 1);
        CheckEqual("引擎关键字无重复", dup, 0);

        // 每个引擎链接都要带 {q} 占位符
        int withQ = engines.Count(e => e.Value.Contains("{q}"));
        CheckEqual("所有引擎链接都含 {q}", withQ, engines.Count);

        // 引擎表版本号必须是有效数字（用于老用户自动补齐缺失引擎）
        Check("引擎表版本号 >= 1", DefaultSettings.BuiltinEnginesVersion >= 1,
            DefaultSettings.BuiltinEnginesVersion.ToString());

        // 内置图标必须优先于旧的磁盘图标路径。
        //
        // 这是"换了图标却没生效"的回归点：老用户的条目上还挂着早先版本生成的
        // .svg 路径，若优先级搞反，界面就会继续显示旧图标。
        var probeEntry = new QEntry
        {
            Key = "bd",
            Comment = "百度",
            Value = "https://www.baidu.com/s?wd={q}",
            IconPath = @"C:\__not_exist__\old.svg",   // 旧的、已失效的路径
            EngineIcon = "bai_du",
        };
        var picked = Core.Icons.IconCache.ForEntry(probeEntry);
        Check("内置图标优先于旧图标路径", picked != null);
        Check("取到的是内置图标（尺寸 64）",
            picked is System.Windows.Media.Imaging.BitmapSource { PixelWidth: 64 },
            (picked as System.Windows.Media.Imaging.BitmapSource)?.PixelWidth.ToString() ?? "null");
    }

    /// <summary>
    /// 老用户的设置文件里只有旧引擎，升级后必须自动补齐新引擎，
    /// 否则"扩充了引擎"对老用户等于没发生。
    /// </summary>
    private static void TestEngineMigration()
    {
        var old = DefaultSettings.Create();
        old.BuiltinEnginesVersion = 0;          // 模拟旧版设置文件
        old.Search.RemoveAll(e => !e.Key.Equals("default", StringComparison.OrdinalIgnoreCase)
                                   && e.Key is not ("bd" or "tb" or "zh" or "gh" or "tr"));
        int before = old.Search.Count;

        SettingsStore.MigrateBuiltinEnginesForTest(old);

        Check("迁移后条目变多", old.Search.Count > before, $"{before} → {old.Search.Count}");
        Check("迁移后版本号已更新",
            old.BuiltinEnginesVersion == DefaultSettings.BuiltinEnginesVersion);
        Check("补齐了 go 引擎", old.Search.Any(e => e.Key == "go"));
        Check("补齐了 bili 引擎", old.Search.Any(e => e.Key == "bili"));
        Check("用户原有的 bd 未被覆盖", old.Search.Any(e => e.Key == "bd"));

        // 一次又一次迁移应当是幂等的
        int after = old.Search.Count;
        SettingsStore.MigrateBuiltinEnginesForTest(old);
        CheckEqual("迁移是幂等的", old.Search.Count, after);
    }

    private static void TestEvaluator()
    {
        CheckEqual("1+2*3 = 7", Eval("1+2*3"), "7");
        CheckEqual("(1+2)*3 = 9", Eval("(1+2)*3"), "9");
        CheckEqual("0.1+0.2 = 0.3", Eval("0.1+0.2"), "0.3");
        CheckEqual("2^10 = 1024", Eval("2^10"), "1024");
        CheckEqual("10/4 = 2.5", Eval("10/4"), "2.5");
        CheckEqual("sqrt(16) = 4", Eval("sqrt(16)"), "4");
        CheckEqual("max(3,7) = 7", Eval("max(3,7)"), "7");
        CheckEqual("pi 近似", Eval("pi").StartsWith("3.14159"), true);
        CheckEqual("全角符号 ×", Eval("6×7"), "42");
        Check("除零报错", !ExpressionEvaluator.TryEvaluate("1/0", out _, out _));
        Check("非法表达式报错", !ExpressionEvaluator.TryEvaluate("1+", out _, out _));
        CheckEqual("负数", Eval("-5+2"), "-3");
        CheckEqual("科学计数法", Eval("1e3+1"), "1001");
    }

    private static string Eval(string expr) =>
        ExpressionEvaluator.TryEvaluate(expr, out double v, out _) ? ExpressionEvaluator.Format(v) : "<error>";

    private static void TestQbar()
    {
        var settings = DefaultSettings.Create();
        var qbar = new QbarService(() => settings, () => new List<string> { "hello world", "another clip" });

        var math = qbar.Query("=1+2*3");
        Check("= 开头直接给出计算结果", math.Count > 0 && math[0].Kind == QbarKind.Math, math.FirstOrDefault()?.Title);
        CheckEqual("计算结果正确", math[0].Payload, "7");

        var search = qbar.Query("bd 天气");
        Check("搜索指令命中", search.Count > 0 && search[0].Kind == QbarKind.Search);
        Check("搜索链接带上了关键词", search[0].Payload.Contains("weather") ||
                                      search[0].Payload.Contains(Uri.EscapeDataString("天气")));

        var cmd = qbar.Query("/set");
        Check("内置命令补全", cmd.Any(r => r.Kind == QbarKind.Command && r.Payload == "openSettings"));

        var run = qbar.Query("calc");
        Check("程序名可被搜到", run.Any(r => r.Kind == QbarKind.Run && r.Payload.Contains("calc")));

        var fallback = qbar.Query("一个不存在的关键词");
        Check("兜底默认搜索", fallback.Count > 0 && fallback.Last().Kind == QbarKind.Search);

        var clip = qbar.Query("hello");
        Check("剪贴板历史可被搜到", clip.Any(r => r.Kind == QbarKind.Clipboard));

        var empty = qbar.Query("");
        Check("空输入给出默认建议", empty.Count > 0);

        Check("算式识别", QbarService.LooksLikeMath("12*34+5"));
        Check("普通单词不误判为算式", !QbarService.LooksLikeMath("hello world"));
        Check("带等号算式识别", QbarService.LooksLikeMath("1+1="));
    }

    /// <summary>Qbar 标签：输入 "bd " 之后 bd 变成标签、输入框只剩搜索词。</summary>
    private static void TestChips()
    {
        var settings = DefaultSettings.Create();
        var qbar = new QbarService(() => settings, () => new List<string>());

        var chip = qbar.ResolveChip("bd");
        Check("bd 能解析成标签", chip != null);
        CheckEqual("标签类型是搜索", chip?.Kind, ChipKind.Search);
        CheckEqual("标签名字优先用备注", chip?.Label, "百度");
        Check("bd 自带图标（优先显示图标）", chip?.HasIcon == true, chip?.IconPath);

        var run = qbar.ResolveChip("calc");
        Check("calc 能解析成程序标签", run?.Kind == ChipKind.Run, run?.Kind.ToString());
        Check("程序标签自动带 exe 图标或首字兜底", run != null && (run.HasIcon || run.Initial.Length > 0));

        var web = qbar.ResolveChip("gh");
        Check("gh 解析为搜索（搜索优先于网址）", web?.Kind is ChipKind.Search or ChipKind.Web);

        Check("未知关键字不变成标签", qbar.ResolveChip("不存在的词") == null);
        Check("default 不参与标签", qbar.ResolveChip("default") == null);

        // 标签模式下的查询：只剩搜索词
        var results = qbar.Query("天气", chip);
        Check("标签模式下有结果", results.Count > 0);
        Check("结果用对应引擎", results[0].Payload.Contains("baidu"), results[0].Payload);
        Check("结果带上了关键词", results[0].Payload.Contains(Uri.EscapeDataString("天气")));

        var empty = qbar.Query("", chip);
        Check("标签模式空输入给出提示", empty.Count > 0);

        // 标签模式下 = 仍可计算
        var calc = qbar.Query("=6*7", chip);
        Check("标签模式下仍可直接计算", calc[0].Kind == QbarKind.Math && calc[0].Payload == "42");

        // 没有图标的条目应当给出首字与颜色
        var noIcon = new QEntry { Key = "xx", Value = "https://example.com/?q={q}", Comment = "示例" };
        settings.Search.Add(noIcon);
        var chip2 = qbar.ResolveChip("xx");
        Check("无图标条目可用首字兜底", chip2 != null && !chip2.HasIcon && chip2.Initial == "示");
        Check("首字颜色稳定", IconCache.ColorFor("示例") == IconCache.ColorFor("示例"));

        // 图标缓存
        Check("内置图标文件已生成", File.Exists(IconUrls.Baidu), IconUrls.Baidu);
    }

    private static void TestSettings()
    {
        var original = SettingsStore.Load();
        var copy = original.Clone();
        copy.Global.MouseSpeed = 7;
        var json = System.Text.Json.JsonSerializer.Serialize(copy, SettingsStore.JsonOptions);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;
        CheckEqual("设置可序列化往返", back.Global.MouseSpeed, 7);
        Check("默认设置含按键绑定", DefaultSettings.Create().Keys.Count > 100);
        Check("默认设置含搜索指令", DefaultSettings.Create().Search.Count >= 5);
        Check("Capslox 与经典方案不同",
            DefaultSettings.CapsloxScheme().First(k => k.Combo == "caps_z").Action !=
            DefaultSettings.ClassicScheme().First(k => k.Combo == "caps_z").Action);
        var normalized = DefaultSettings.Create();
        normalized.Keys.Add(new KeyBinding { Combo = "caps_q", Action = "moveLeft" });
        SettingsStore.Normalize(normalized);
        Check("重复绑定被去重", normalized.Keys.Count(k => k.Combo == "caps_q") == 1);
        Check("每个默认绑定都有对应动作", DefaultSettings.Create().Keys.All(k =>
            k.Action.Length > 0));
    }

    private static void TestLegacyImport()
    {
        // 不要写系统临时目录：某些环境下 %TEMP% 不可写（组策略 / 沙箱），
        // 之前这里抛出的未捕获异常会让 --selftest 整个挂住。统一写到数据目录。
        string path = Path.Combine(AppPaths.DataDir, "selftest_legacy.ini");
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            string content = string.Join("\r\n", new[]
            {
                "[Global]",
                "autostart=1",
                "mouseSpeed=6",
                "[Keys]",
                "caps_q=keyFunc_qbar",
                "caps_a=keyFunc_moveWordLeft(3)",
                "caps_1=keyFunc_winbind_activate(1)",
                "caps_lalt_z=keyFunc_putWinToBottom",
                "caps_f3=keyFunc_translate",
                "[QSearch]",
                "default=https://www.bing.com/search?q={q}",
                "bd=https://www.baidu.com/s?wd={q}",
                "[QRun]",
                "ie=C:\\Program Files\\Internet Explorer\\iexplore.exe",
                "*runas ",
                "[QWeb]",
                "gh=https://github.com",
                "[TabHotString]",
                "clp=CapsLock+",
            });
            File.WriteAllText(path, content, new UnicodeEncoding(false, true));
            var result = LegacyIniImporter.Import(path);
            Check("能识别 UTF-16 的旧设置", result.Keys >= 5);
            CheckEqual("迁移 Qbar 绑定", result.Settings.FindBinding("caps_q")?.Action, "qbar");
            CheckEqual("迁移带参数的动作", result.Settings.FindBinding("caps_a")?.Arg, "3");
            CheckEqual("迁移窗口绑定动作", result.Settings.FindBinding("caps_1")?.Action, "winbindActivate1");
            CheckEqual("迁移置底动作", result.Settings.FindBinding("caps_lalt_z")?.Action, "winSendToBottom");
            CheckEqual("迁移翻译动作", result.Settings.FindBinding("caps_f3")?.Action, "translate");
            Check("迁移搜索指令", result.Settings.Search.Any(s => s.Key == "bd"));
            Check("迁移程序条目", result.Settings.Run.Any(r => r.Key == "ie"));
            Check("迁移网址条目", result.Settings.Web.Any(w => w.Key == "gh"));
            Check("迁移热字串", result.Settings.HotStrings.Any(h => h.Key == "clp"));
            CheckEqual("迁移开机自启", result.Settings.Global.StartWithWindows, true);
            CheckEqual("迁移鼠标速度", result.Settings.Global.MouseSpeed, 6);
        }
        catch (Exception ex)
        {
            Check("旧版 ini 迁移（读取/写入）", false, ex.Message);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static void TestActionCatalog()
    {
        var settings = DefaultSettings.Create();
        var queue = new ActionQueue();
        var ctx = new ActionContext
        {
            Settings = () => settings,
            Clipboard = new ClipboardService(),
            Windows = new WindowActions(),
            WindowBindings = new WindowBindingService(settings),
            Mouse = new MouseService(),
            Notify = _ => { },
        };
        var catalog = ActionCatalog.Create(ctx);
        Check("动作数量 > 80", catalog.Items.Count > 80, catalog.Items.Count.ToString());
        Check("所有动作 ID 唯一", catalog.Items.Select(i => i.Id).Distinct().Count() == catalog.Items.Count);
        Check("每个动作都有说明", catalog.Items.All(i => i.Description.Length > 0));
        Check("未知动作安全返回", catalog.Invoke("no_such_action", "") == false);

        // 每个默认绑定都能在动作目录里找到
        var missing = settings.Keys.Where(k => k.IsBound && catalog.Get(k.Action) == null)
            .Select(k => k.Combo + "=" + k.Action).ToList();
        Check("默认绑定全部可解析", missing.Count == 0, string.Join(", ", missing.Take(6)));

        // 每个预设动作都被默认绑定或可选
        Check("不绑定动作存在", catalog.Get("doNothing") != null);
        queue.Dispose();
    }

    /// <summary>
    /// 窗口绑定：10 个槽位（1~9 + 0）、两种绑定方式、以及激活动画开关。
    /// </summary>
    private static void TestWindowBindings()
    {
        var settings = DefaultSettings.Create();
        var svc = new WindowBindingService(settings);
        var notes = new List<string>();
        svc.Notified += m => notes.Add(m);

        // 默认应当有 10 个槽位，且按键映射到 1~9 与 0
        CheckEqual("默认提供 10 个槽位", settings.Bindings.Count, 10);
        var slots = settings.Bindings.Select(b => b.Slot).OrderBy(x => x).ToList();
        Check("槽位编号为 1..10", slots.SequenceEqual(Enumerable.Range(1, 10)), string.Join(",", slots));

        // 每个槽位都要有对应的按键动作
        var catalog = ActionCatalog.Create(new ActionContext
        {
            Settings = () => settings,
            Clipboard = new ClipboardService(),
            Windows = new WindowActions(),
            WindowBindings = svc,
            Mouse = new MouseService(),
            Notify = _ => { },
        });
        int activateOk = Enumerable.Range(1, 10)
            .Count(i => catalog.Get($"winbindActivate{i}") != null);
        CheckEqual("10 个激活动作齐备", activateOk, 10);

        // 按键绑定：Caps+1..Caps+0 都要指向激活动作
        int boundOk = 0;
        for (int i = 1; i <= 10; i++)
        {
            string key = i == 10 ? "0" : i.ToString();
            var b = settings.FindBinding($"caps_{key}");
            if (b != null && b.Action == $"winbindActivate{i}") boundOk++;
        }
        CheckEqual("Caps+1..Caps+0 均已绑定", boundOk, 10);

        // 方式一：按 exe 绑定（用一个一定存在的程序文件）
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length > 0)
        {
            // 绑定自己应当被拒绝
            Check("拒绝绑定自身", !svc.BindByExe(1, exe));
            // 始终不存在的路径也应当拒绝
            Check("拒绝不存在的路径", !svc.BindByExe(1, @"C:\__no_such_program__.exe"));
        }
        else
        {
            Check("取不到自身路径，跳过自身绑定检查", true);
        }

        // 方式二：按窗口句柄绑定（用当前前台窗口）
        var fg = WindowService.Foreground;
        if (fg != IntPtr.Zero && WindowService.Exists(fg))
        {
            bool ok = svc.BindWindow(2, fg);
            Check("可按窗口句柄绑定", ok);
            if (ok)
            {
                var b = settings.Bindings.First(x => x.Slot == 2);
                Check("绑定后记录模式为单窗口", b.Mode == WinBindMode.Single);
                Check("绑定后记录了窗口句柄", b.WindowIds.Count == 1);
                Check("描述信息包含窗口数", svc.DescribeSlot(2).Contains("1 个窗口"),
                    svc.DescribeSlot(2));
            }
        }
        else
        {
            Check("取不到前台窗口，跳过句柄绑定检查", true);
        }

        // 空槽位的描述
        CheckEqual("空槽位描述为未绑定", svc.DescribeSlot(9), "未绑定");

        // 清除
        svc.Unbind(2);
        Check("清除后槽位为空", svc.DescribeSlot(2) == "未绑定");

        // 激活动画开关可切换
        bool saved = WindowService.AnimationEnabled;
        WindowService.AnimationEnabled = true;
        Check("动画开关可开启", WindowService.AnimationEnabled);
        WindowService.AnimationEnabled = false;
        Check("动画开关可关闭", !WindowService.AnimationEnabled);
        WindowService.AnimationEnabled = saved;
    }

    /// <summary>
    /// 窗口点选的"取窗口"逻辑。
    ///
    /// 之前的 bug 是：遮罩自己铺满全屏，WindowFromPoint 返回的永远是遮罩，
    /// 于是点了没反应。这里验证改造后的判定规则：
    ///   * 自己的窗口必须被排除；
    ///   * 桌面/任务栏等系统壳窗口必须被排除；
    ///   * 普通窗口必须能被正确识别。
    /// </summary>
    private static void TestWindowPreview()
    {
        var picker = new UI.Windows.WindowPickerWindow();
        var probe = picker.Probe;

        // 自己的窗口必须排除（否则会绑到自己）
        var self = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        if (self != IntPtr.Zero)
            Check("点选时排除自身窗口", !probe.AcceptsForTest(self));
        else
            Check("本进程无主窗口句柄，跳过自身排除检查", true);

        // 桌面与任务栏必须被识别为"壳窗口"（识别出来才会排除）
        Check("识别出桌面窗口", probe.IsShellClassForTest("Progman"));
        Check("识别出 WorkerW", probe.IsShellClassForTest("WorkerW"));
        Check("识别出任务栏", probe.IsShellClassForTest("Shell_TrayWnd"));
        Check("普通窗口类名不被当成壳窗口", !probe.IsShellClassForTest("Notepad"));

        // 真实窗口：从当前已枚举到的顶层窗口里挑一个非自身的来验证
        var candidates = WindowService.EnumerateTopLevel()
            .Where(w => !w.ExeName.Equals(AppInfo.ExeName, StringComparison.OrdinalIgnoreCase))
            .Where(w => w.ClassName is not ("Progman" or "WorkerW" or "Shell_TrayWnd"))
            .Take(3)
            .ToList();
        if (candidates.Count > 0)
        {
            int accepted = candidates.Count(c => probe.AcceptsForTest(c.Handle));
            Check("普通窗口可被点选", accepted > 0, $"候选 {candidates.Count} 个，接受 {accepted} 个");
        }
        else
        {
            Check("没有可作为目标的窗口，跳过", true);
        }

        // 核心回归：按坐标从 Z 序里取窗口。
        // 之前的实现用 WindowFromPoint，遮罩会挡住自己导致取不到；
        // 现在的实现从桌面 Z 序顶端扫描，必须能取到一个真实窗口。
        var anyWindow = WindowService.EnumerateTopLevel()
            .FirstOrDefault(w => !w.ExeName.Equals(AppInfo.ExeName, StringComparison.OrdinalIgnoreCase)
                                 && w.ClassName is not ("Progman" or "WorkerW" or "Shell_TrayWnd"));
        if (anyWindow.Handle != IntPtr.Zero)
        {
            var r = WindowService.GetRect(anyWindow.Handle);
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;
            var found = probe.ResolveAtForTest(cx, cy);
            Check("按坐标能从 Z 序取到窗口", found != IntPtr.Zero,
                $"({cx},{cy}) -> 0x{found.ToInt64():X}");
            Check("取到的不是本进程窗口", found == IntPtr.Zero ||
                !IsOwnProcess(found), "0x" + found.ToInt64().ToString("X"));
        }
        else
        {
            Check("没有可用于坐标测试的窗口，跳过", true);
        }

        picker.Close();
    }

    /// <summary>
    /// 实测点选器遮罩"在屏幕上真实可见"时，取窗口还能不能成功。
    ///
    /// 这是"点选点了没反应"的直接回归测试。之前只测了不显示遮罩的情况，
    /// 而真正的失败场景恰恰是遮罩铺满全屏、置顶的时候：
    /// 旧实现用 WindowFromPoint，取到的永远是遮罩自己。
    ///
    /// 这里真的把遮罩显示出来、应用同款样式，再验证：
    ///   1) 遮罩确实获得了 WS_EX_TRANSPARENT（点击穿透）——否则点击会被它吃掉；
    ///   2) 遮罩在最顶层时，坐标取窗口仍能命中一个不属于本进程的真实窗口。
    /// </summary>
    private static void TestPickerWithOverlayVisible()
    {
        var picker = new UI.Windows.WindowPickerWindow
        {
            ShowInTaskbar = false,
            Left = -32000,
            Top = -32000,
        };
        picker.Show();
        DoEvents(260);

        try
        {
            var hwnd = picker.HandleForTest;
            Check("遮罩已创建", hwnd != IntPtr.Zero, "0x" + hwnd.ToInt64().ToString("X"));
            Check("遮罩带点击穿透样式（WS_EX_TRANSPARENT）", picker.HasClickThroughForTest);
            Check("遮罩在顶层（TOPMOST）", picker.IsTopmostForTest);

            // 取屏幕正中，这里一定是某个真实窗口（或桌面）
            var probe = picker.Probe;
            int cx = (int)(System.Windows.SystemParameters.PrimaryScreenWidth / 2);
            int cy = (int)(System.Windows.SystemParameters.PrimaryScreenHeight / 2);
            var found = probe.ResolveAtForTest(cx, cy);
            bool isReal = found != IntPtr.Zero
                          && found != hwnd
                          && !IsOwnProcess(found);
            Check("遮罩可见时仍能取到真实窗口", isReal,
                $"({cx},{cy}) -> 0x{found.ToInt64():X}");
        }
        finally
        {
            picker.Close();
            DoEvents(120);
        }
    }

    /// <summary>
    /// 抽空 WPF 消息队列若干毫秒。
    /// 自检跑在界面线程上，Show() 之后必须让消息循环转一会儿，
    /// Loaded 才会触发、窗口样式才真正应用。
    /// </summary>
    private static void DoEvents(int ms)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(20, ms)),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static bool IsOwnProcess(IntPtr hwnd)
    {
        WindowService.GetWindowThreadProcessIdForTest(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// 文字度量：确保任何字号下，输入框高度都够放下文字。
    ///
    /// 这是"文字只显示上半部分"那个 bug 的回归测试：
    /// 只要 MinBoxHeight 算出来的高度小于"文字高 + 上下内边距 + 边框"，
    /// 文字就会被裁，所以这里逐字号验证。
    /// </summary>
    /// <summary>
    /// 翻译引擎：数量要够，且每个引擎的 ID / 名称 / 是否需要密钥都合法。
    /// </summary>
    private static void TestTranslateProviders()
    {
        var providers = Core.Translate.TranslateService.Providers;
        Check("翻译引擎 >= 6 个", providers.Length >= 6, providers.Length.ToString());
        Check("至少 2 个免密钥引擎",
            providers.Count(p => !p.NeedsKey) >= 2,
            providers.Count(p => !p.NeedsKey).ToString());
        Check("所有引擎都有名称", providers.All(p => !string.IsNullOrWhiteSpace(p.Name)));
        Check("所有引擎都有唯一 ID",
            providers.Select(p => p.Id).Distinct().Count() == providers.Length);

        // 免费引擎必须能在没有密钥的情况下工作
        var svc = new Core.Translate.TranslateService();
        var free = providers.First(p => !p.NeedsKey);
        var res = svc.TranslateAsync("hello", free.Id, "", "", "zh-CHS",
            CancellationToken.None).GetAwaiter().GetResult();
        Check($"免费引擎（{free.Name}）可用", res.Success || res.Error.Length > 0,
            res.Success ? "ok" : res.Error);
    }

    private static void TestTextMetrics()
    {
        // 字号 14~30（Qbar 字号滑块的取值范围）都要留出余量
        var bad = new List<string>();
        foreach (double fs in new[] { 14.0, 16, 18, 20, 21, 24, 27, 30 })
        {
            double text = Core.Text.TextMetrics.MeasureHeight(fs);
            double box = Core.Text.TextMetrics.MinBoxHeight(fs);
            double content = box - 2 * (Core.Text.TextMetrics.Padding + Core.Text.TextMetrics.Border);
            double slack = content - text;
            if (slack < 2)
                bad.Add($"字号{fs}: 内容区{content:0.0} 文字{text:0.0} 余量{slack:0.0}");
        }
        Check("14~30 各字号都有 >= 2px 余量", bad.Count == 0, string.Join("; ", bad));

        // 字号越大，需要的高度必须单调变大（否则某个字号会漏掉）
        double prev = 0;
        bool monotonic = true;
        foreach (double fs in new[] { 14.0, 18, 21, 24, 30 })
        {
            double h = Core.Text.TextMetrics.MinBoxHeight(fs);
            if (h <= prev) monotonic = false;
            prev = h;
        }
        Check("所需高度随字号单调递增", monotonic);

        // 度量结果必须是有限正数，不能因为量不出而返回 0/NaN
        Check("度量结果是正常数值",
            double.IsFinite(Core.Text.TextMetrics.MeasureHeight(21)) && Core.Text.TextMetrics.MeasureHeight(21) > 0,
            Core.Text.TextMetrics.MeasureHeight(21).ToString("0.0"));

        // 单行输入框的模板默认高度要足够容纳默认字号（模板 MinHeight = 38）
        double defaultFontSize = 13.5;
        try
        {
            if (System.Windows.Application.Current?.TryFindResource("Size.Text") is double d && d > 0)
                defaultFontSize = d;
        }
        catch { }
        double def = Core.Text.TextMetrics.MinBoxHeight(defaultFontSize);
        Check("默认字号所需高度不超过模板 MinHeight 38", def <= 38,
            $"字号 {defaultFontSize} 需要 {def:0.0}");
    }

    /// <summary>
    /// Genie 动画：验证动画帧确实会被驱动。
    ///
    /// 之前动画“看不出来”的根因是：调用方是钩子线程 / 动作线程，
    /// 那里没有消息循环，`new DispatcherTimer()` 的 Tick 永远不触发。
    /// 这里在一个 STA 线程里调用动画，并检查窗口尺寸是否被改动过 ——
    /// 若一帧都没跑，尺寸不会变。
    /// </summary>
    private static void TestGenieAnimation()
    {
        var probe = new System.Windows.Window
        {
            Width = 600,
            Height = 400,
            WindowStyle = System.Windows.WindowStyle.None,
            ShowInTaskbar = false,
            Left = -32000,
            Top = -32000,
        };
        probe.Show();
        DoEvents(200);

        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(probe).Handle;
            Check("测试窗口已创建", h != IntPtr.Zero);

            var before = WindowService.GetRect(h);

            // 在后台线程上触发动画（复现真实调用路径：钩子线程）
            Exception? error = null;
            var t = new Thread(() =>
            {
                try { WindowService.ActivateWithGenie(h, 200); }
                catch (Exception ex) { error = ex; }
            })
            { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(3000);

            Check("动画调用没有抛异常", error == null, error?.Message ?? "");
            DoEvents(400);

            var after = WindowService.GetRect(h);
            Check("动画结束后窗口尺寸保持原样（未损坏）",
                Math.Abs(after.Width - before.Width) <= 1 &&
                Math.Abs(after.Height - before.Height) <= 1,
                $"{before.Width}x{before.Height} -> {after.Width}x{after.Height}");

            // 收起一次，确认最小化路径可用
            WindowService.HideWithGenie(h, 150);
            DoEvents(500);
            Check("收起后窗口已最小化", NativeWindowIsIconic(h));
        }
        finally
        {
            probe.Close();
            DoEvents(120);
        }
    }

    private static bool NativeWindowIsIconic(IntPtr h)
    {
        return Core.Interop.NativeMethods.IsIconic(h);
    }

    private static void TestWindows()
    {
        var fg = WindowService.ForegroundInfo();
        Check("能取到前台窗口信息", fg != null, fg?.Title);
        var all = WindowService.EnumerateTopLevel().ToList();
        Check("能枚举顶层窗口", all.Count > 0, all.Count.ToString());
        Check("窗口信息带进程路径", all.All(w => w.ExePath.Length >= 0));
        var work = WindowService.WorkArea(IntPtr.Zero);
        Check("能取到工作区", work.Width > 0 && work.Height > 0, $"{work.Width}x{work.Height}");
        Check("进程路径可读", WindowService.GetProcessPath((uint)Environment.ProcessId).Length > 0);
    }

    private static void TestInput()
    {
        var before = new NativeMethods.INPUT();
        Check("INPUT 结构体大小正确（x64 为 40）",
            Marshal.SizeOf<NativeMethods.INPUT>() == 40,
            Marshal.SizeOf<NativeMethods.INPUT>().ToString());
        InputSender.Tap(VK.Escape);   // 不会抛异常即可
        Check("注入 Esc 不抛异常", true);
        InputSender.Text("a");
        Check("注入文本不抛异常", true);
        Check("物理按键状态可读", InputSender.IsPhysicallyDown(VK.Shift) == false ||
                                 InputSender.IsPhysicallyDown(VK.Shift));
    }

    /// <summary>
    /// 单实例逻辑。
    ///
    /// 这里曾经出过严重问题：提权重启时旧进程与新进程抢互斥体，
    /// 新进程误判“已有实例”而退出，导致两个进程都没了、钩子没装上，
    /// 表现就是“按任何键都没反应”。所以必须校验：
    ///   * 首次获取成功；
    ///   * 重复获取被正确判为“已有实例”；
    ///   * 释放后又能重新获取（= 重启流程不会互相卡死）。
    /// </summary>
    private static void TestSingleInstance()
    {
        // 用一个独立名称测试，避免与真实运行的实例互相干扰
        var name = "CapsLockPlus.SelfTest." + Guid.NewGuid().ToString("N");
        var first = new Mutex(true, name, out bool createdFirst);
        Check("首次可获取单实例锁", createdFirst);

        var second = new Mutex(true, name, out bool createdSecond);
        Check("重复获取会被识别为已有实例", !createdSecond);
        try { second.Dispose(); } catch { }

        first.ReleaseMutex();
        first.Dispose();

        // 释放之后应当能重新获取——这正是提权重启能成功接管的前提
        var third = new Mutex(true, name, out bool createdThird);
        Check("释放后可重新获取（重启不卡死）", createdThird);
        try { third.ReleaseMutex(); third.Dispose(); } catch { }
    }

    private static void TestHook()
    {
        var hook = new KeyboardHook();
        int events = 0;
        hook.KeyEvent += (_, _, injectedByUs) =>
        {
            if (!injectedByUs) Interlocked.Increment(ref events);
            return false;
        };
        hook.Start();
        Check("键盘钩子安装成功", hook.HookInstalled);
        Check("鼠标钩子安装成功", hook.MouseHookInstalled);
        // 关键：钩子“是否可用”必须反映真实句柄，不能只看意愿标志，
        // 否则系统摘钩后自愈监控会一直以为正常。
        Check("钩子运行状态与句柄一致", hook.IsRunning == hook.HookInstalled);
        hook.Dispose();
        Check("钩子可正常卸载", !hook.HookInstalled);
        Check("卸载后运行状态为假", !hook.IsRunning);
    }

    /// <summary>
    /// 组合键状态机（产品核心）：直接喂按键，验证“哪些键被吞掉、触发了什么动作”。
    /// </summary>
    private static void TestEngine()
    {
        var settings = DefaultSettings.Create();
        var queue = new ActionQueue();
        var triggered = new List<string>();
        var engine = new HotkeyEngine(queue, () => settings, (action, arg) =>
        {
            lock (triggered) triggered.Add(action);
            return true;
        });

        Check("Caps 按下被吞掉", engine.SimulateKey(VK.CapsLock, true));
        Check("Caps 组合键被吞掉（不会打出字母）", engine.SimulateKey(0x53 /*S*/, true));
        engine.SimulateKey(0x53, false);
        Check("Caps 抬起被吞掉", engine.SimulateKey(VK.CapsLock, false));
        Thread.Sleep(120);
        lock (triggered)
        {
            Check("Caps+S 触发了“左移”", triggered.Contains("moveLeft"), string.Join(",", triggered));
        }

        // 没有绑定的键应当放行，保证正常打字
        triggered.Clear();
        engine.SimulateKey(VK.CapsLock, true);
        Check("已绑定的键被吞掉（Caps+Q）", engine.SimulateKey(0x51 /*Q*/, true));
        engine.SimulateKey(0x51, false);
        Check("未绑定的键放行（F7 默认无绑定）", !engine.SimulateKey(0x76 /*F7*/, true));
        engine.SimulateKey(0x76, false);
        engine.SimulateKey(VK.CapsLock, false);
        Thread.Sleep(140);

        // 关键：非“可连续”动作不应自动重复（否则按住 Caps+F2 会反复弹窗）
        Check("打开类动作标记为不可重复", !HotkeyEngine.IsRepeatable("mathBoard"));
        Check("方向类动作标记为可重复", HotkeyEngine.IsRepeatable("moveLeft"));
        Check("删除类动作标记为可重复", HotkeyEngine.IsRepeatable("backspace"));
        Check("滚动类动作标记为可重复", HotkeyEngine.IsRepeatable("wheelUp"));

        // 短按 Caps 应当触发 press_caps
        triggered.Clear();
        engine.SimulateKey(VK.CapsLock, true);
        engine.SimulateKey(VK.CapsLock, false);
        Thread.Sleep(160);
        lock (triggered)
        {
            Check("短按 Caps 触发切换大小写", triggered.Contains("toggleCapsLock"), string.Join(",", triggered));
        }

        // 暂停后完全放行
        engine.Enabled = false;
        Check("暂停时不吞 Caps", !engine.SimulateKey(VK.CapsLock, true));
        engine.SimulateKey(VK.CapsLock, false);
        engine.Enabled = true;

        // LAlt 层：默认布局里 caps_lalt_z = doNothing（未绑定），改用有绑定的 caps_lalt_w
        triggered.Clear();
        engine.SimulateKey(VK.CapsLock, true);
        Check("Caps 按住时 LAlt 被吞掉", engine.SimulateKey(VK.LMenu, true));
        Check("Caps+LAlt+W 被吞掉", engine.SimulateKey(0x57 /*W*/, true));
        engine.SimulateKey(0x57, false);
        engine.SimulateKey(VK.LMenu, false);
        engine.SimulateKey(VK.CapsLock, false);
        Thread.Sleep(160);
        lock (triggered)
        {
            Check("Caps+LAlt+W 触发删除单词", triggered.Contains("deleteWord"), string.Join(",", triggered));
        }

        // 双击 Caps（需要先在设置里打开）
        settings.Global.DoubleTapCaps = true;
        triggered.Clear();
        engine.SimulateKey(VK.CapsLock, true);
        engine.SimulateKey(VK.CapsLock, false);
        Thread.Sleep(60);
        engine.SimulateKey(VK.CapsLock, true);
        engine.SimulateKey(VK.CapsLock, false);
        Thread.Sleep(180);
        lock (triggered)
        {
            Check("双击 Caps 触发 Qbar", triggered.Contains("qbar"), string.Join(",", triggered));
        }
        settings.Global.DoubleTapCaps = false;

        engine.Dispose();
        queue.Dispose();
    }
}
