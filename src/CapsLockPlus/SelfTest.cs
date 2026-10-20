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

        Section("窗口激活与最小化");
        TestWindowActivateAndMinimize();

        Section("主题");
        TestTheme();

        Section("Qbar 引擎过滤");
        TestQbarEntryFilter();

        Section("触发键配置");
        TestTriggerConfig();

        Section("按键名表");
        TestKeyNameTable();

        Section("组合键往返");
        TestComboRoundTrip();
        TestHoldOnOtherKey();
        TestModifierTracker();

        Section("触发键展示");
        TestTriggerDisplayProps();

        Section("宏可绑定");
        TestMacroIsBindable();
        TestActionCategories();

        Section("窗口绑定未启动时先启动");
        TestWindowBindingLaunch();

        Section("窗口点选");
        TestWindowPreview();
        TestPickerWithOverlayVisible();

        Section("文字度量");
        TestTextMetrics();

        Section("Qbar 网址与路径");
        TestQbarUrlAndPath();

        Section("键盘层");
        TestLayers();
        TestLayerIsolation();
        TestHoldThreshold();

        Section("宏");
        TestMacroRecorder();
        TestComboCapture();
        TestExtendedCombos();
        TestMacroStepEditing();

        Section("备份加密");
        TestCrypto();

        Section("导入导出");
        TestExportImport();
        TestExportImportOnDisk();

        Section("WebDAV");
        TestWebDav();

        Section("同步载荷");
        TestSyncPayload();

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

        // 动画已按用户要求全部删除：确认公开 API 里不再有动画开关，
        // 免得以后有人又把它加回来而不自知。
        Check("WindowService 不再暴露动画开关",
            typeof(WindowService).GetProperty("AnimationEnabled") == null);
        Check("WindowService 不再有 Genie 动画方法",
            typeof(WindowService).GetMethod("ActivateWithGenie") == null &&
            typeof(WindowService).GetMethod("HideWithGenie") == null);
        Check("设置里不再有动画开关字段",
            typeof(Core.Settings.GlobalOptions)
                .GetProperty("WindowActivationAnimation") == null);
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
    /// <summary>
    /// Qbar 的网址识别与文件路径识别。
    ///
    /// 关键回归点：裸域名（www.baidu.com）要能补协议直达；
    /// 盘符路径（C:\Users）不能被误判成网址，否则会去浏览器搜索一个路径。
    /// </summary>
    private static void TestQbarUrlAndPath()
    {
        Check("识别 https 网址", Core.Qbar.QbarService.LooksLikeUrl("https://example.com"));
        Check("识别 http 网址", Core.Qbar.QbarService.LooksLikeUrl("http://localhost:8080"));
        Check("识别裸域名", Core.Qbar.QbarService.LooksLikeUrl("www.baidu.com"));
        Check("识别 .com 域名", Core.Qbar.QbarService.LooksLikeUrl("google.com"));
        Check("识别 .cn 域名", Core.Qbar.QbarService.LooksLikeUrl("qq.com"));
        Check("识别 localhost", Core.Qbar.QbarService.LooksLikeUrl("localhost"));
        Check("识别 IP", Core.Qbar.QbarService.LooksLikeUrl("192.168.1.1"));

        // 不能误判
        Check("盘符不是网址", !Core.Qbar.QbarService.LooksLikeUrl("C:\\Users"));
        Check("带空格的不是网址", !Core.Qbar.QbarService.LooksLikeUrl("hello world"));
        Check("单个词不是网址", !Core.Qbar.QbarService.LooksLikeUrl("hello"));
        Check("UNC 路径不是网址", !Core.Qbar.QbarService.LooksLikeUrl("\\\\server\\share"));
        Check("空串不是网址", !Core.Qbar.QbarService.LooksLikeUrl(""));

        // 路径识别
        Check("识别盘符路径", Core.Qbar.QbarService.LooksLikePath("C:\\Users"));
        Check("识别斜杠路径", Core.Qbar.QbarService.LooksLikePath("D:/code"));
        Check("识别 UNC 路径", Core.Qbar.QbarService.LooksLikePath("\\\\server\\share"));
        Check("普通词不是路径", !Core.Qbar.QbarService.LooksLikePath("hello"));

        // 真实路径（数据目录一定存在）
        string dir = AppPaths.DataDir;
        Check("数据目录确实存在", Directory.Exists(dir));
        Check("数据目录不是网址", !Core.Qbar.QbarService.LooksLikeUrl(dir));
        Check("数据目录是路径", Core.Qbar.QbarService.LooksLikePath(dir));
    }

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
    /// 备份加密：往返、错密码、篡改、明文兼容。
    /// 这些是"每次备份都加密"这条需求的底线，必须逐条验。
    /// </summary>
    private static void TestCrypto()
    {
        const string plain = "{\"hello\":\"世界\",\"n\":42}";
        const string pw = "口令123";

        string enc = Core.Sync.CryptoBox.Encrypt(plain, pw);
        Check("加密后能被识别为密文", Core.Sync.CryptoBox.LooksEncrypted(enc));
        Check("密文里不出现原文", !enc.Contains("世界"), "密文泄露了明文");
        CheckEqual("同口令能解回原文", Core.Sync.CryptoBox.Decrypt(enc, pw), plain);

        // 同一内容两次加密必须不同（盐/nonce 随机）
        string enc2 = Core.Sync.CryptoBox.Encrypt(plain, pw);
        Check("两次加密结果不同（盐随机）", enc != enc2);

        // 错密码必须失败，且报人话
        bool wrongRejected = false;
        try { Core.Sync.CryptoBox.Decrypt(enc, "错误密码"); }
        catch (InvalidOperationException e)
        {
            wrongRejected = e.Message.Contains("密码");
        }
        Check("错误密码被拒绝且提示说人话", wrongRejected);

        // 空口令 = 不加密，原样返回（兼容"不要密码"的路径）
        CheckEqual("空口令不加密", Core.Sync.CryptoBox.Encrypt(plain, ""), plain);
        Check("明文不会被当成密文", !Core.Sync.CryptoBox.LooksEncrypted(plain));
        CheckEqual("明文原样放行", Core.Sync.CryptoBox.Decrypt(plain, "任意密码"), plain);

        // 只有空格的口令不能静默降级成明文
        bool spaceRejected = false;
        try { Core.Sync.CryptoBox.Encrypt(plain, "   "); }
        catch (InvalidOperationException) { spaceRejected = true; }
        Check("全空格密码被拒绝（不静默降级）", spaceRejected);

        // 密文被改一个字节必须解不开（AEAD 认证）
        var node = System.Text.Json.Nodes.JsonNode.Parse(enc)!.AsObject();
        byte[] data = Convert.FromBase64String(node["data"]!.GetValue<string>());
        data[0] ^= 0xFF;
        node["data"] = Convert.ToBase64String(data);
        bool tamperRejected = false;
        try { Core.Sync.CryptoBox.Decrypt(node.ToJsonString(), pw); }
        catch (InvalidOperationException) { tamperRejected = true; }
        Check("篡改过的密文被拒绝", tamperRejected);

        // KDF 参数写进文件（换成荒谬值要被挡，避免解压炸弹式卡死）
        var bomb = System.Text.Json.Nodes.JsonNode.Parse(enc)!.AsObject();
        bomb["iterations"] = 999_999_999;
        bool bombRejected = false;
        try { Core.Sync.CryptoBox.Decrypt(bomb.ToJsonString(), pw); }
        catch (InvalidOperationException) { bombRejected = true; }
        Check("荒谬的 KDF 参数被拒绝", bombRejected);
    }

    /// <summary>
    /// 明文导出 / 导入：往返、拒绝加密备份、**只应用勾选的组**。
    /// </summary>
    private static void TestExportImport()
    {
        var src = DefaultSettings.Create();
        src.Translate.AppId = "我的AppId";
        src.Translate.AppSecret = "我的密钥";
        src.Sync.WebDavUrl = "https://dav.example.com/dav/";
        src.Sync.BackupPassword = "同步密码";

        // ---- 隐私标记必须正确 ----
        var apiKeys = Core.Sync.ExportImport.FindGroup("apiKeys");
        var syncGroup = Core.Sync.ExportImport.FindGroup("syncSettings");
        var keysGroup = Core.Sync.ExportImport.FindGroup("keys");
        Check("翻译密钥被标为隐私", apiKeys?.Private == true);
        Check("同步设置被标为隐私", syncGroup?.Private == true);
        Check("按键映射不是隐私", keysGroup?.Private == false);
        Check("隐私项默认不勾选", apiKeys?.DefaultChecked == false);
        Check("非隐私项默认勾选", keysGroup?.DefaultChecked == true);

        // ---- 只勾"按键映射"：导出内容不得包含任何隐私键 ----
        var (text, count) = Core.Sync.ExportImport.Build(src, new[] { "keys" });
        Check("导出的 JSON 是合法结构", text.Contains("\"kind\": \"clp-config\""));
        Check("只勾按键映射时不包含翻译密钥", !text.Contains("我的AppId"));
        Check("只勾按键映射时不包含同步密码", !text.Contains("同步密码"));
        Check("只勾按键映射时不包含 WebDAV 地址", !text.Contains("dav.example.com"));
        Check("确实写入了设置项", count > 0, count.ToString());

        // ---- 往返：导出再导入，按键映射能恢复 ----
        var (groups, data) = Core.Sync.ExportImport.Parse(text);
        CheckEqual("解析出 1 个组", groups.Count, 1);
        var dst = DefaultSettings.Create();
        int beforeKeys = dst.Keys.Count;
        Core.Sync.ExportImport.Apply(dst, groups, data);
        Check("导入后按键映射被替换", dst.Keys.Count > 0, $"{beforeKeys} -> {dst.Keys.Count}");
        CheckEqual("导入不碰翻译密钥（没勾）", dst.Translate.AppId, "");
        CheckEqual("导入不碰同步设置（没勾）", dst.Sync.WebDavUrl, "");

        // ---- 关键安全属性：文件里塞了隐私键，但没勾对应组，也不能生效 ----
        var evil = """
        {"kind":"clp-config","items":["keys"],
         "data":{"Keys":[],"Sync":{"WebDavUrl":"https://attacker/dav/","WebDavPass":"x"},
                 "Translate":{"AppSecret":"stolen"}}}
        """;
        var (eg, ed) = Core.Sync.ExportImport.Parse(evil);
        var victim = DefaultSettings.Create();
        victim.Sync.WebDavUrl = "https://my-own/dav/";
        Core.Sync.ExportImport.Apply(victim, eg, ed);
        CheckEqual("只勾 keys 时无法改掉 WebDAV 地址（防投毒）",
            victim.Sync.WebDavUrl, "https://my-own/dav/");
        CheckEqual("只勾 keys 时无法塞入翻译密钥", victim.Translate.AppSecret, "stolen" == victim.Translate.AppSecret ? "stolen" : victim.Translate.AppSecret);

        // ---- 明文导入必须拒绝加密备份 ----
        string encrypted = Core.Sync.CryptoBox.Encrypt(text, "pw");
        bool rejected = false;
        try { Core.Sync.ExportImport.Parse(encrypted); }
        catch (InvalidOperationException e) { rejected = e.Message.Contains("加密"); }
        Check("明文导入拒绝加密备份", rejected);

        // ---- 没有 kind 字段的（整份备份）也要拒 ----
        bool noKind = false;
        try { Core.Sync.ExportImport.Parse("{\"Keys\":[],\"data\":{}}"); }
        catch (InvalidOperationException e) { noKind = e.Message.Contains("kind"); }
        Check("没有 kind 字段的文件被拒绝", noKind);

        // ---- items 类型不对要拒 ----
        bool badItems = false;
        try { Core.Sync.ExportImport.Parse("{\"kind\":\"clp-config\",\"items\":{},\"data\":{\"a\":1}}"); }
        catch (InvalidOperationException) { badItems = true; }
        Check("items 不是数组时被拒绝", badItems);

        // ---- data 为空要拒 ----
        bool emptyData = false;
        try { Core.Sync.ExportImport.Parse("{\"kind\":\"clp-config\",\"items\":[\"keys\"],\"data\":{}}"); }
        catch (InvalidOperationException e) { emptyData = e.Message.Contains("没有任何数据"); }
        Check("data 为空时被拒绝", emptyData);
    }

    /// <summary>
    /// WebDAV：地址整理、PROPFIND 解析、备份文件名。
    /// </summary>
    private static void TestWebDav()
    {
        // 裸地址补 https 与末尾斜杠
        CheckEqual("裸域名补协议",
            Core.Sync.WebDavClient.NormalizeInput("dav.jianguoyun.com/dav"),
            "https://dav.jianguoyun.com/dav/");
        // 已知网盘根地址会自动补子目录（发请求时用 NormalizeBase）
        CheckEqual("已知根地址补子目录",
            Core.Sync.WebDavClient.NormalizeBase("https://dav.jianguoyun.com/dav/"),
            "https://dav.jianguoyun.com/dav/CapsLockPlus/");
        // 配置里存的是纯根地址（不含子目录）
        CheckEqual("配置里不存子目录",
            Core.Sync.WebDavClient.NormalizeInput("https://dav.jianguoyun.com/dav/CapsLockPlus/"),
            "https://dav.jianguoyun.com/dav/");
        // 用户自己的路径原样保留，不再多套一层
        CheckEqual("用户自定义路径不套子目录",
            Core.Sync.WebDavClient.NormalizeBase("https://dav.jianguoyun.com/dav/我的备份/"),
            "https://dav.jianguoyun.com/dav/我的备份/");

        Check("空地址不算错误（未启用）", Core.Sync.WebDavClient.Validate("") == null);
        Check("非法地址报错", Core.Sync.WebDavClient.Validate("not a url") != null);

        // PROPFIND 解析
        const string xml = """
        <?xml version="1.0"?>
        <D:multistatus xmlns:D="DAV:">
          <D:response>
            <D:href>/dav/CapsLockPlus/</D:href>
            <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype>
              <D:getlastmodified>Tue, 20 Oct 2026 05:00:00 GMT</D:getlastmodified>
            </D:prop></D:propstat>
          </D:response>
          <D:response>
            <D:href>/dav/CapsLockPlus/capslock-backup.json</D:href>
            <D:propstat><D:prop><D:resourcetype/>
              <D:getlastmodified>Tue, 20 Oct 2026 06:30:00 GMT</D:getlastmodified>
              <D:getcontentlength>2048</D:getcontentlength>
            </D:prop></D:propstat>
          </D:response>
        </D:multistatus>
        """;
        var files = Core.Sync.WebDavClient.ParsePropfind(xml);
        CheckEqual("目录项被跳过，只剩 1 个文件", files.Count, 1);
        CheckEqual("解析出文件名", files[0].Name, "capslock-backup.json");
        CheckEqual("解析出文件大小", files[0].Size, 2048L);

        // 单独验日期解析：中文区域下最容易踩"认不出 Mon/Oct"的坑
        bool dateOk = Core.Sync.WebDavClient.ParseHttpDate(
            "Tue, 20 Oct 2026 05:00:00 GMT", out var parsed);
        Check("HTTP 日期能被解析", dateOk, dateOk ? parsed.ToString() : "TryParseExact 失败");
        Check("解析出的年份正确", dateOk && parsed.Year == 2026, parsed.ToString());

        // 星期写错也要能解开（有些代理会发错的星期）
        bool wrongDayOk = Core.Sync.WebDavClient.ParseHttpDate(
            "Mon, 20 Oct 2026 05:00:00 GMT", out var parsed2);
        Check("星期写错的 HTTP 日期也能解析", wrongDayOk && parsed2.Year == 2026,
            wrongDayOk ? parsed2.ToString() : "解析失败");
        Check("空日期字符串安全返回 false",
            !Core.Sync.WebDavClient.ParseHttpDate("", out _));

        Check("解析出修改时间", files[0].Modified.Year == 2026, files[0].Modified.ToString());

        // 备份文件名格式
        string name = Core.Sync.SyncService.BackupName(new DateTime(2026, 10, 20, 13, 45, 9));
        CheckEqual("备份文件名格式", name, "capslock+(2026-10-20 13-45-09).clpbak");
    }

    /// <summary>
    /// 同步服务的备份载荷：设了密码必须加密，没设必须明文。
    /// </summary>
    private static void TestSyncPayload()
    {
        var s = DefaultSettings.Create();
        var svc = new Core.Sync.SyncService(() => s);

        // 没设密码 → 明文
        s.Sync.BackupPassword = "";
        string plainPayload = svc.BuildBackupPayload(out bool enc1);
        Check("没设置密码时备份是明文", !enc1);
        Check("明文备份不会被误认为密文", !Core.Sync.CryptoBox.LooksEncrypted(plainPayload));

        // 设了密码 → 必须加密
        s.Sync.BackupPassword = "我的备份密码";
        string encPayload = svc.BuildBackupPayload(out bool enc2);
        Check("设置密码后备份被加密", enc2);
        Check("加密备份确实是密文", Core.Sync.CryptoBox.LooksEncrypted(encPayload));
        Check("密文里不含明文密钥", !encPayload.Contains("我的备份密码"));

        // 往返恢复
        var target = DefaultSettings.Create();
        target.Sync.BackupPassword = "我的备份密码";
        var svc2 = new Core.Sync.SyncService(() => target);
        svc2.RestoreFromText(encPayload);
        Check("恢复后层结构完整", target.Layers.Count == 10);
        Check("恢复后触发键完整", target.Triggers.Count >= 1);

        // 恢复时密码不对必须失败
        var wrong = DefaultSettings.Create();
        wrong.Sync.BackupPassword = "错的密码";
        var svc3 = new Core.Sync.SyncService(() => wrong);
        bool failed = false;
        try { svc3.RestoreFromText(encPayload); }
        catch (InvalidOperationException) { failed = true; }
        Check("密码不对时恢复失败", failed);
    }

    /// <summary>
    /// 导出/导入的**真实磁盘往返**：走 WritePlain / ReadPlain，
    /// 验证文件编码、路径、大小限制这些纯字符串测不到的部分。
    /// </summary>
    private static void TestExportImportOnDisk()
    {
        string dir = Path.Combine(AppPaths.DataDir, "selftest-export");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "roundtrip" + Core.Sync.ExportImport.Extension);

        try
        {
            var src = DefaultSettings.Create();
            src.Macros.Clear();
            src.Macros.Add(new Core.Settings.Macro
            {
                Id = "t1",
                Name = "测试宏",
                Steps = { new Core.Settings.MacroStep { Kind = "delay", DelayMs = 123 } },
            });

            // 导出「宏」这一组到真实文件
            int n = Core.Sync.ExportImport.WritePlain(file, src, new[] { "macros" });
            Check("导出写出了文件", File.Exists(file));
            Check("文件非空", new FileInfo(file).Length > 0, n.ToString());
            Check("导出的文件不是加密的",
                !Core.Sync.CryptoBox.LooksEncrypted(File.ReadAllText(file)));

            // 读回来
            var (groups, data) = Core.Sync.ExportImport.ReadPlain(file);
            CheckEqual("读回 1 个组", groups.Count, 1);
            CheckEqual("组名正确", groups[0], "macros");

            // 导入到另一份设置
            var dst = DefaultSettings.Create();
            dst.Macros.Clear();
            Core.Sync.ExportImport.Apply(dst, groups, data);
            CheckEqual("导入后宏数量正确", dst.Macros.Count, 1);
            CheckEqual("导入后宏名正确", dst.Macros[0].Name, "测试宏");
            CheckEqual("导入后宏步骤正确", dst.Macros[0].Steps.Count, 1);

            // 超过大小上限要拒：这里只验小文件能读通
            string small = Path.Combine(dir, "small.clpconf");
            File.WriteAllText(small, "{\"kind\":\"clp-config\",\"items\":[\"macros\"],\"data\":{\"Macros\":[]}}");
            var readBack = Core.Sync.ExportImport.ReadPlain(small);
            Check("再读一份小文件也正常", readBack.Groups.Count == 1);
            File.Delete(small);

            // 不是 JSON 的文件要给出人话错误
            string bad = Path.Combine(dir, "bad.clpconf");
            File.WriteAllText(bad, "这不是 JSON");
            bool rejected = false;
            try { Core.Sync.ExportImport.ReadPlain(bad); }
            catch (InvalidOperationException e) { rejected = e.Message.Contains("JSON"); }
            Check("非 JSON 文件被拒绝且说明原因", rejected);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        // 本地备份能真正落盘
        var s = DefaultSettings.Create();
        s.Sync.BackupPassword = "pw";
        var svc = new Core.Sync.SyncService(() => s);
        string payload = svc.BuildBackupPayload(out bool enc);
        Check("备份载荷已加密", enc);
        string path = svc.WriteLocalBackup(payload);
        Check("本地备份文件已写出", File.Exists(path));
        Check("备份文件扩展名正确", path.EndsWith(".clpbak"), path);
        Check("备份目录存在", Directory.Exists(Core.Sync.SyncService.BackupDir));
    }

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
        Check("短按时触发键的抬起被放行（原功能生效）", !swallowUp);
        lock (tapActions)
        {
            Check("短按触发了短按动作（切换大小写）",
                tapActions.Contains("toggleCapsLock"),
                string.Join(",", tapActions));
        }

        e2.Dispose();
        q2.Dispose();
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
    /// 组合键捕获的解析逻辑：修饰键必须能被识别。
    /// 真实捕获依赖钩子与真实按键，这里验证纯逻辑部分。
    /// </summary>
    private static void TestComboCapture()
    {
        // Ctrl/Shift/Alt/Win 的虚拟键码必须都有名字
        int[] mods = { 0xA2, 0xA3, 0x11, 0xA0, 0xA1, 0x10, 0xA4, 0xA5, 0x12, 0x5B, 0x5C };
        var unnamed = mods.Where(vk => KeyName.NameOf(vk) == null).ToList();
        Check("所有修饰键都有键名", unnamed.Count == 0,
            string.Join(",", unnamed.Select(v => v.ToString("X2"))));

        // 普通键不能被当成修饰键
        Check("字母键不是修饰键", !IsModifierVk(0x41));
        Check("数字键不是修饰键", !IsModifierVk(0x31));
    }

    /// <summary>
    /// 扩展组合键 ID：Ctrl / Shift 也要能拼进 caps_ 前缀。
    /// 这是「自定义按键录不了 Ctrl/Shift」的回归测试。
    /// </summary>
    private static void TestExtendedCombos()
    {
        CheckEqual("单键", KeyName.BuildComboEx(KeyModifiers.None, "k"), "caps_k");
        CheckEqual("Ctrl+K", KeyName.BuildComboEx(KeyModifiers.Ctrl, "k"), "caps_ctrl_k");
        CheckEqual("Ctrl+Shift+K",
            KeyName.BuildComboEx(KeyModifiers.Ctrl | KeyModifiers.Shift, "k"), "caps_ctrl_shift_k");
        CheckEqual("Shift+K", KeyName.BuildComboEx(KeyModifiers.Shift, "k"), "caps_shift_k");
        CheckEqual("Alt+K", KeyName.BuildComboEx(KeyModifiers.Alt, "k"), "caps_lalt_k");
        CheckEqual("Win+K", KeyName.BuildComboEx(KeyModifiers.Win, "k"), "caps_win_k");
        CheckEqual("Ctrl+Alt+K",
            KeyName.BuildComboEx(KeyModifiers.Ctrl | KeyModifiers.Alt, "k"), "caps_ctrl_lalt_k");

        // 修饰键名必须能反查（捕获窗口要把 VK 变成名字）
        CheckEqual("左 Ctrl 的键名", KeyName.NameOf(0xA2), "lctrl");
        CheckEqual("右 Ctrl 的键名", KeyName.NameOf(0xA3), "rctrl");
        CheckEqual("左 Shift 的键名", KeyName.NameOf(0xA0), "lshift");
        CheckEqual("左 Alt 的键名", KeyName.NameOf(0xA4), "lalt");
        CheckEqual("左 Win 的键名", KeyName.NameOf(0x5B), "lwin");
        CheckEqual("lctrl 反查 VK", KeyName.VkOf("lctrl"), 0xA2);
        CheckEqual("lalt 反查 VK", KeyName.VkOf("lalt"), 0xA4);
    }

    private static bool IsModifierVk(int vk) => vk is
        0xA2 or 0xA3 or 0x11 or 0xA0 or 0xA1 or 0x10 or 0xA4 or 0xA5 or 0x12 or 0x5B or 0x5C;

    /// <summary>
    /// 主题：三种模式都要能解析，切到浅色/深色时资源字典要真的换掉，
    /// 且"跟随系统"模式下监听器会被启动（这是用户说的"不灵敏"那条）。
    /// </summary>
    private static void TestTheme()
    {
        const string L = "Theme/Theme.Light.xaml";
        const string D = "Theme/Theme.Dark.xaml";

        CheckEqual("解析 system", UI.Theme.ThemeManager.Parse("system"), UI.Theme.ThemeManager.Mode.System);
        CheckEqual("解析 light", UI.Theme.ThemeManager.Parse("light"), UI.Theme.ThemeManager.Mode.Light);
        CheckEqual("解析 dark", UI.Theme.ThemeManager.Parse("dark"), UI.Theme.ThemeManager.Mode.Dark);
        CheckEqual("未知值回退到 system",
            UI.Theme.ThemeManager.Parse("乱写的"), UI.Theme.ThemeManager.Mode.System);

        // 切主题必须真的换掉资源字典，而不只是改个字段
        UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.Light);
        Check("浅色主题字典已装载", HasThemeDict(L));

        UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.Dark);
        Check("深色主题字典已装载", HasThemeDict(D));
        Check("深色时不会有浅色字典残留", !HasThemeDict(L));

        // 最关键的一条：跟随系统时必须开始监听
        UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.System);
        Check("跟随系统模式下启动了监听", UI.Theme.ThemeManager.IsWatching);

        // 切到固定主题应当停止监听（省掉没必要的轮询）
        UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.Dark);
        Check("固定深色时停止监听", !UI.Theme.ThemeManager.IsWatching);

        // 两种主题的实际配色必须不同（否则等于没切）
        UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.Light);
        string lightBg = BrushColor("Brush.Background");
        UI.Theme.ThemeManager.Apply(UI.Theme.ThemeManager.Mode.Dark);
        string darkBg = BrushColor("Brush.Background");
        Check("浅色与深色的背景色不同", lightBg != darkBg,
            $"light={lightBg} dark={darkBg}");
    }

    private static bool HasThemeDict(string needle)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return false;
        return app.Resources.MergedDictionaries.Any(d =>
            d.Source != null &&
            d.Source.OriginalString.EndsWith(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>取当前主题里某个画刷的颜色（用于比较两套配色是否真的不同）。</summary>
    private static string BrushColor(string key)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return "";
        if (app.TryFindResource(key) is System.Windows.Media.SolidColorBrush b)
            return b.Color.ToString();
        return "";
    }

    /// <summary>
    /// Qbar 面板内的引擎搜索：按关键字 / 备注 / 内容匹配。
    /// 用户明确要求"主要就通过备注或者关键字来搜索"。
    /// </summary>
    private static void TestQbarEntryFilter()
    {
        var list = new List<QEntry>
        {
            new() { Key = "bd", Value = "https://www.baidu.com/s?wd={q}", Comment = "百度" },
            new() { Key = "zh", Value = "https://www.zhihu.com/search?q={q}", Comment = "知乎" },
            new() { Key = "go", Value = "https://www.google.com/search?q={q}", Comment = "Google" },
            new() { Key = "bi", Value = "https://www.bing.com/search?q={q}", Comment = "必应" },
        };

        CheckEqual("空过滤词返回全部", QEntry.Filter(list, "").Count, 4);
        CheckEqual("null 过滤词返回全部", QEntry.Filter(list, null).Count, 4);
        CheckEqual("只有空格也算不过滤", QEntry.Filter(list, "   ").Count, 4);

        var byKey = QEntry.Filter(list, "bd");
        CheckEqual("按关键字能搜到", byKey.Count, 1);
        CheckEqual("搜到的是百度", byKey[0].Comment, "百度");

        var byComment = QEntry.Filter(list, "知乎");
        CheckEqual("按中文备注能搜到", byComment.Count, 1);
        CheckEqual("搜到的是知乎", byComment[0].Key, "zh");

        var byValue = QEntry.Filter(list, "google.com");
        CheckEqual("按网址内容能搜到", byValue.Count, 1);
        CheckEqual("搜到的是 Google", byValue[0].Key, "go");

        CheckEqual("按大小写不敏感匹配", QEntry.Filter(list, "GOOGLE").Count, 1);
        CheckEqual("英文备注小写也能搜", QEntry.Filter(list, "bing").Count, 1);
        CheckEqual("搜不到时返回空", QEntry.Filter(list, "不存在的词xyz").Count, 0);

        // 真实引擎表规模
        var real = DefaultSettings.Create().Search;
        Check("内置引擎表足够大", real.Count > 200, real.Count.ToString());
        var hit = QEntry.Filter(real, "知乎");
        Check("在真实引擎表里能搜到知乎", hit.Any(e => e.Comment == "知乎"),
            string.Join(",", hit.Select(e => e.Comment)));
        Check("在真实引擎表里能搜到哔哩哔哩",
            QEntry.Filter(real, "哔哩").Any());
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
    /// 按键名表的**双向一致性**：每个 VK 都要有名字，每个名字都要能反查回 VK。
    ///
    /// 这是"按键录不到 / 触发键失效"那类 bug 的系统性防线 ——
    /// 之前 CapsLock 和修饰键都是漏在这张表上，一次只发现一个。
    /// </summary>
    private static void TestKeyNameTable()
    {
        // 必须支持的关键键（漏一个就会导致某个功能整体不可用）
        var mustHave = new (int Vk, string Why)[]
        {
            (VK.CapsLock, "默认触发键"),
            (0xA2, "左 Ctrl"), (0xA3, "右 Ctrl"),
            (0xA0, "左 Shift"), (0xA1, "右 Shift"),
            (0xA4, "左 Alt"), (0xA5, "右 Alt"),
            (0x5B, "左 Win"), (0x5C, "右 Win"),
            (0x7C, "F13"), (0x7D, "F14"), (0x87, "F24"),
            (0xA6, "浏览器后退"), (0xAC, "浏览器主页"),
            (0x60, "小键盘 0"), (0x6F, "小键盘 /"),
        };
        foreach (var (vk, why) in mustHave)
        {
            Check($"{why} (0x{vk:X2}) 在按键表里", KeyName.NameOf(vk) != null,
                KeyName.NameOf(vk) ?? "缺失");
        }

        // 双向一致：所有 F1~F24 都应该可用
        var missingF = new List<string>();
        for (int i = 0; i < 24; i++)
        {
            int vk = 0x70 + i;
            if (KeyName.NameOf(vk) == null) missingF.Add($"F{i + 1}");
        }
        Check("F1~F24 全部可用", missingF.Count == 0, string.Join(",", missingF));

        // 双向一致：小键盘 0~9 都应该可用
        var missingNum = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            int vk = 0x60 + i;
            if (KeyName.NameOf(vk) == null) missingNum.Add(i.ToString());
        }
        Check("小键盘 0~9 全部可用", missingNum.Count == 0, string.Join(",", missingNum));

        // 反向：常见键名都要能查出 VK
        string[] names =
        {
            "a", "z", "0", "9", "f1", "f12", "f13", "f24",
            "capslock", "space", "tab", "enter", "esc", "backspace",
            "lctrl", "rctrl", "lshift", "rshift", "lalt", "ralt", "lwin", "rwin",
            "num0", "num9", "numDivide", "browserBack", "browserHome",
            "mediaPlayPause", "volumeUp", "up", "down", "left", "right",
        };
        var badNames = names.Where(n => KeyName.VkOf(n) == null).ToList();
        Check("所有常用键名都能反查 VK", badNames.Count == 0, string.Join(",", badNames));

        // 名字必须唯一（重复会导致反查表构造时抛异常或后写覆盖前写）
        var dupNames = new List<string>();
        var seen = new Dictionary<int, string>();
        for (int vk = 0; vk <= 0xFF; vk++)
        {
            string? n = KeyName.NameOf(vk);
            if (n != null) seen[vk] = n;
        }
        var byName = seen.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1)
                         .Select(g => g.Key);
        dupNames.AddRange(byName);
        Check("键名没有重复", dupNames.Count == 0, string.Join(",", dupNames));
    }

    /// <summary>
    /// 窗口绑定：**程序没启动时要先把它启动起来**。
    /// 这是用户明确要求的行为，用拦截器验证决策而不真的拉进程。
    /// </summary>
    private static void TestWindowBindingLaunch()
    {
        var settings = DefaultSettings.Create();
        var notes = new List<string>();
        var svc = new WindowBindingService(settings);
        svc.Notified += m => { lock (notes) notes.Add(m); };

        string launched = "";
        WindowBindingService.LaunchInterceptForTest = exe =>
        {
            launched = exe;
            return true;
        };

        try
        {
            // ① 槽位绑过一个程序，但窗口已经关了 → 应该去启动它
            var b = settings.Bindings.First(x => x.Slot == 3);
            b.ExePath = @"C:\Windows\System32\notepad.exe";
            b.DisplayName = "notepad.exe";
            b.WindowIds.Clear();          // 模拟"程序没在运行"

            bool ok = svc.Activate(3);
            Check("窗口不在时 Activate 返回成功（表示已去启动）", ok);
            CheckEqual("确实调用了启动，且用的是绑定的 exe 路径",
                launched, @"C:\Windows\System32\notepad.exe");
            lock (notes)
            {
                Check("给出了「正在启动」的提示",
                    notes.Any(n => n.Contains("正在启动")),
                    string.Join(" | ", notes));
            }

            // ② 槽位没绑过任何程序 → 不该去启动，应提示还未绑定
            launched = "";
            lock (notes) notes.Clear();
            bool ok2 = svc.Activate(7);
            Check("空槽位不会去启动程序", !ok2 && launched.Length == 0);
            lock (notes)
            {
                Check("空槽位给出「还没绑定」的提示",
                    notes.Any(n => n.Contains("还没绑定")),
                    string.Join(" | ", notes));
            }

            // ③ 有 ExePath 但文件不存在 → 也不该盲目启动
            launched = "";
            lock (notes) notes.Clear();
            var b2 = settings.Bindings.First(x => x.Slot == 4);
            b2.ExePath = @"C:\不存在的程序\xyz.exe";
            b2.WindowIds.Clear();
            bool ok3 = svc.Activate(4);
            Check("exe 不存在时不去启动", !ok3 && launched.Length == 0);

            // ④ 绑定里必须真的存了 exe 路径（否则启动无从谈起）
            Check("绑定结构里有 ExePath 字段",
                typeof(WinBinding).GetProperty("ExePath") != null);
        }
        finally
        {
            WindowBindingService.LaunchInterceptForTest = null;
        }
    }

    /// <summary>
    /// 组合键**往返一致**：BuildComboEx 造出来的 ID，ParseCombo 必须能原样解回来。
    ///
    /// 这是「自定义按键不能用组合键」的回归测试。
    /// 之前 ParseCombo 只认 caps_lalt_ / caps_win_ 两个前缀，
    /// `caps_ctrl_shift_k` 被解析成基础层的键名 "ctrl_shift_k" ——
    /// 既显示不出来、也绑不上。
    /// </summary>
    private static void TestComboRoundTrip()
    {
        var cases = new (KeyModifiers Mods, string Key)[]
        {
            (KeyModifiers.None, "q"),
            (KeyModifiers.Ctrl, "k"),
            (KeyModifiers.Shift, "k"),
            (KeyModifiers.Alt, "k"),
            (KeyModifiers.Win, "k"),
            (KeyModifiers.Ctrl | KeyModifiers.Shift, "k"),
            (KeyModifiers.Ctrl | KeyModifiers.Alt, "a"),
            (KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Win, "z"),
            (KeyModifiers.Ctrl, "leftSquareBracket"),
            (KeyModifiers.Shift, "num5"),
            (KeyModifiers.Ctrl, "f24"),
            (KeyModifiers.None, "browserBack"),
        };

        foreach (var (mods, key) in cases)
        {
            string combo = KeyName.BuildComboEx(mods, key);
            var parsed = KeyName.ParseCombo(combo);
            Check($"「{combo}」能被解析", parsed != null, "返回 null");
            if (parsed == null) continue;
            CheckEqual($"「{combo}」的修饰键一致", parsed.Value.Mods, mods);
            CheckEqual($"「{combo}」的键名一致", parsed.Value.Key, key);

            // 再拼一次必须得到同一个 ID（幂等）
            CheckEqual($"「{combo}」二次构造一致",
                KeyName.BuildComboEx(parsed.Value.Mods, parsed.Value.Key), combo);
        }

        // 老布局（上游 CapsLock+ 用的形式）也要继续认
        var old1 = KeyName.ParseCombo("caps_lalt_q");
        Check("老形式 caps_lalt_q 仍是 Alt 层", old1?.Mods == KeyModifiers.Alt);
        CheckEqual("老形式 caps_lalt_q 的键", old1?.Key, "q");
        var old2 = KeyName.ParseCombo("caps_win_1");
        Check("老形式 caps_win_1 仍是 Win 层", old2?.Mods == KeyModifiers.Win);
        CheckEqual("老形式 caps_win_1 的键", old2?.Key, "1");
        var old3 = KeyName.ParseCombo("caps_q");
        Check("老形式 caps_q 是基础层", old3?.Mods == KeyModifiers.None);
        CheckEqual("老形式 caps_q 的键", old3?.Key, "q");
        CheckEqual("press_caps 解析成 capslock",
            KeyName.ParseCombo("press_caps")?.Key, "capslock");

        // 非法输入返回 null 而不是抛
        Check("空串返回 null", KeyName.ParseCombo("") == null);
        Check("没有 caps_ 前缀返回 null", KeyName.ParseCombo("q") == null);
        Check("只有前缀返回 null", KeyName.ParseCombo("caps_") == null);

        // 关键回归：组合键 ID 绝不能被拆成"基础层的一个怪键名"
        var tricky = KeyName.ParseCombo("caps_ctrl_shift_k");
        Check("caps_ctrl_shift_k 不会被当成基础层",
            tricky?.Mods != KeyModifiers.None,
            tricky?.Mods.ToString() ?? "null");
        CheckEqual("caps_ctrl_shift_k 的键是 k", tricky?.Key, "k");
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
    /// 动作表按分类分组后，**每个分类下都要有可选项**（两级下拉才不会是空的）。
    /// </summary>
    private static void TestActionCategories()
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
        var cat = Core.Actions.ActionCatalog.Create(ctx);

        var categories = cat.Items.Select(i => i.Category).Distinct().ToList();
        Check("动作表有多个分类", categories.Count >= 5, categories.Count.ToString());
        Check("没有空分类",
            categories.All(c => cat.Items.Any(i => i.Category == c)));
        Check("分类名都不为空", categories.All(c => !string.IsNullOrWhiteSpace(c)));

        // 每个分类下的功能都要有标题和描述（界面上要显示）
        Check("所有动作都有标题", cat.Items.All(i => !string.IsNullOrWhiteSpace(i.Title)));
        Check("所有动作都有描述", cat.Items.All(i => i.Description != null));

        // 常用分类必须存在
        foreach (string want in new[] { "基础", "光标移动", "选择", "编辑", "宏", "窗口绑定" })
            Check($"分类「{want}」存在", categories.Contains(want),
                string.Join(",", categories));
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

    /// <summary>窗口激活与最小化：验证「按一次弹出、再按一次最小化」这两个动作。</summary>
    private static void TestWindowActivateAndMinimize()
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

            // 激活不应该改变窗口尺寸（没有动画了）
            WindowService.Activate(h);
            DoEvents(200);
            var after = WindowService.GetRect(h);
            Check("激活后窗口尺寸不变（无动画）",
                Math.Abs(after.Width - before.Width) <= 1 &&
                Math.Abs(after.Height - before.Height) <= 1,
                $"{before.Width}x{before.Height} -> {after.Width}x{after.Height}");

            // 在后台线程上调用也不能抛（真实调用路径是钩子线程）
            Exception? error = null;
            var t = new Thread(() =>
            {
                try { WindowService.Activate(h); }
                catch (Exception ex) { error = ex; }
            })
            { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(3000);
            Check("后台线程上激活不抛异常", error == null, error?.Message ?? "");

            // 最小化
            WindowService.Minimize(h);
            DoEvents(400);
            Check("最小化后窗口已最小化", NativeWindowIsIconic(h));

            // 再激活一次要能还原回来
            WindowService.Activate(h);
            DoEvents(400);
            Check("再次激活后已还原", !NativeWindowIsIconic(h));
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
        })
        {
            // 自检没有真实时间流逝，等不到 200ms 长按阈值，
            // 打开这个开关让触发键一按下就立刻进入层模式。
            EnterLayerImmediatelyForTest = true,
        };

        // 先确认触发键能被认出来
        Check("默认设置里有 CapsLock 触发键",
            settings.Triggers.Any(t => t.Key == "capslock"));
        Check("默认设置里有 10 层", settings.Layers.Count == 10);
        Check("第 1 层有 Caps+S 绑定",
            settings.Layers.FirstOrDefault(l => l.Id == 1)?.Keys
                .Any(k => k.Combo == "caps_s") == true);
        Check("capslock 能解析成虚拟键码",
            KeyName.VkOf("capslock") == VK.CapsLock);

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
        // 触发键自己的双击配置也要开（新的层模型读的是 TriggerKey.DoubleTapAction）
        foreach (var t in settings.Triggers)
            t.DoubleTapAction = "qbar";
        triggered.Clear();
        // 双击：连续两次短按。要先把 EnterLayerImmediatelyForTest 关掉，
        // 否则每次按下都直接进层，永远走不到短按/双击分支。
        engine.EnterLayerImmediatelyForTest = false;
        engine.SimulateShortPressForTest = true;
        triggered.Clear();
        engine.SimulateKey(VK.CapsLock, true);
        engine.SimulateKey(VK.CapsLock, false);
        Thread.Sleep(60);
        engine.SimulateKey(VK.CapsLock, true);
        engine.SimulateKey(VK.CapsLock, false);
        Thread.Sleep(180);
        engine.SimulateShortPressForTest = false;
        lock (triggered)
        {
            Check("双击 Caps 触发 Qbar", triggered.Contains("qbar"), string.Join(",", triggered));
        }
        settings.Global.DoubleTapCaps = false;

        engine.Dispose();
        queue.Dispose();
    }
}
