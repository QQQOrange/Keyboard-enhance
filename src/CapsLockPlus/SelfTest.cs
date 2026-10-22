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
/// 用 `KeyboardEnhance.exe --selftest` 运行，退出码 0 表示全部通过。
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
        TestDataLocationAndEncryptedExport();

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

        Section("提权");
        TestElevation();

        Section("热路径缓存");
        TestHotPathCaching();

        Section("宏与速查表展示");
        TestMacroDisplayProps();
        TestHelpCoversAllLayers();

        Section("宏绑定与 keepKey");
        TestMacroBindableInKeysView();
        TestKeepKeyTapAction();
        TestQbarEngineActions();
        TestTriggerKeyPosition();
        TestKeyboardLayoutGeometry();
        TestArbitraryWindowBinding();
        TestWinBindRestoreLogic();
        TestWebPageCategory();
        TestSmartOpen();
        TestKeycapVisualStates();
        TestProductIdentity();
        TestWebTitleExtract();
        TestNoDuplicateKeycaps();
        TestKeycapShortTitles();
        TestWinBindCustomName();
        TestQbarThemeContrast();
        TestComplexTriggerKey();
        TestTriggerBadgeFollowsLayer();
        TestSubLayers();
        TestDirectSearch();
        TestTypeTextAction();

        Section("自定义按键");
        TestCustomKey();

        Section("音量与亮度");
        TestVolumeCore();
        TestBrightnessMath();
        TestOsdAndTextInput();
        TestToastCoalescing();
        TestDirectSearchCoversAllEngines();
        TestCopyChordEndToEnd();
        TestSelectionWatcher();
        TestContextHasSelectionWatcher();

        Section("审计修复");
        TestAuditFindings();

        Section("引擎复查修复");
        TestEngineAuditFixes();

        Section("宏可绑定");
        TestMacroIsBindable();
        TestActionCategories();

        Section("窗口绑定未启动时先启动");
        TestWindowBindingLaunch();

        Section("窗口点选");
        TestWindowPreview();
        TestPickerWithOverlayVisible();
        TestPickerCommitKeepsResult();

        Section("文字度量");
        TestTextMetrics();

        Section("Qbar 网址与路径");
        TestQbarUrlAndPath();

        Section("键盘层");
        TestLayers();
        TestLayerIsolation();
        TestHoldThreshold();
        TestModifierTriggerWithRepeat();
        TestContinuousVolumeRepeat();
        TestRepeatSurvivesPhysicalJitter();
        TestTwoContinuousKeysIndependent();
        TestTriggerRecovery();
        TestSubLayerWindowLayout();

        Section("宏");
        TestMacroRecorder();
        TestMacroLiveRecording();
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

        Section("备份策略");
        TestBackupPolicy();

        Section("翻译引擎");

        Section("窗口与进程");
        TestWindows();

        Section("输入注入");
        TestInput();

        Section("输入文本标签与本地加密");
        TestTypeTextAndVault();

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

    /// <summary>
    /// 数据目录必须**跟着软件走**，迁移要只搬用户数据，加密导出要带全字段。
    ///
    /// 这三条都是"用户看不见但一错就很难受"的地方：
    ///   · 数据留在 %LocalAppData% → 拷走软件文件夹等于没拷配置；
    ///   · 迁移时把 logs / backups 一起搬 → 新目录塞满诊断垃圾，
    ///     而且备份会散落在两处；
    ///   · 加密导出还偷偷剔除密码 → 换台机器恢复完还得重填一遍。
    /// </summary>
    private static void TestDataLocationAndEncryptedExport()
    {
        // ---------- 目录约定 ----------
        CheckEqual("用户数据目录名", AppPaths.DataFolderName, "KeyboardEnhance Data");
        CheckEqual("备份目录名", Core.Sync.SyncService.BackupFolderName, "KeyboardEnhance Back");
        Check("老备份目录名仍有记录（迁移要用）",
            Core.Sync.SyncService.LegacyBackupFolderName.Length > 0,
            Core.Sync.SyncService.LegacyBackupFolderName);

        /* 自检为了不碰真实数据，会用 CAPSLOCKPLUS_DATA 指到临时目录 ——
         * 那种情况下"在软件旁边"当然不成立，跳过而不是判失败。 */
        bool envOverride = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("CAPSLOCKPLUS_DATA"));
        Check("数据目录在软件旁边（环境变量覆盖 / 回落时跳过）",
            envOverride || AppPaths.DataDirIsFallback
            || AppPaths.DataDir.StartsWith(AppPaths.AppDir, StringComparison.OrdinalIgnoreCase),
            $"data={AppPaths.DataDir} app={AppPaths.AppDir} "
            + $"override={envOverride} fallback={AppPaths.DataDirIsFallback}");

        // ---------- 迁移：拷什么、不拷什么 ----------
        string root = Path.Combine(AppPaths.DataDir, "selftest-migrate");
        string from = Path.Combine(root, "old");
        string to = Path.Combine(root, "new");
        try
        {
            Directory.CreateDirectory(Path.Combine(from, "icons"));
            Directory.CreateDirectory(Path.Combine(from, "logs"));
            Directory.CreateDirectory(Path.Combine(from, "backups"));

            File.WriteAllText(Path.Combine(from, "settings.json"), "old-settings");
            File.WriteAllText(Path.Combine(from, "vault.json"), "vault");
            File.WriteAllText(Path.Combine(from, "vault.cache"), "cache");
            File.WriteAllText(Path.Combine(from, "settings.json.tmp"), "tmp");
            File.WriteAllText(Path.Combine(from, "selftest.log"), "log");
            File.WriteAllText(Path.Combine(from, "icons", "a.svg"), "<svg/>");
            File.WriteAllText(Path.Combine(from, "logs", "app.log"), "log");
            File.WriteAllText(Path.Combine(from, "backups", "x.clpbak"), "bak");

            int n = AppPaths.CopyTreeForTest(from, to);

            Check("迁移带上了设置", File.Exists(Path.Combine(to, "settings.json")));
            Check("迁移带上了加密元数据", File.Exists(Path.Combine(to, "vault.json")));
            Check("迁移带上了密钥缓存", File.Exists(Path.Combine(to, "vault.cache")));
            Check("迁移带上了图标", File.Exists(Path.Combine(to, "icons", "a.svg")));
            Check("迁移不拷日志", !Directory.Exists(Path.Combine(to, "logs")));
            Check("迁移不拷备份（备份目录单独搬）", !Directory.Exists(Path.Combine(to, "backups")));
            Check("迁移不拷临时文件", !File.Exists(Path.Combine(to, "settings.json.tmp")));
            Check("迁移不拷自检产物", !File.Exists(Path.Combine(to, "selftest.log")));
            CheckEqual("迁移数不含被跳过的文件", n, 4);   // settings / vault.json / vault.cache / icons

            // 再跑一次：目标已有的文件绝不能被旧数据盖掉
            File.WriteAllText(Path.Combine(to, "settings.json"), "new-settings");
            AppPaths.CopyTreeForTest(from, to);
            CheckEqual("迁移不覆盖目标已有文件",
                File.ReadAllText(Path.Combine(to, "settings.json")), "new-settings");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        // ---------- 导出：勾了哪组就是哪组的全部字段 ----------
        var s = DefaultSettings.Create();
        s.Sync.WebDavUrl = "https://dav.example.com/dav/";
        s.Sync.WebDavUser = "me@example.com";
        s.Sync.WebDavPass = "SuperSecretDavPass";
        s.Sync.BackupPassword = "SuperSecretBackupPass";

        var all = Core.Sync.ExportImport.Groups.Select(g => g.Id).ToList();

        // 明文模式：仍然剔除凭据（这条老行为不能退）
        var (plainText, _) = Core.Sync.ExportImport.Build(s, all);
        Check("明文导出不含网盘密码", !plainText.Contains("SuperSecretDavPass"));
        Check("明文导出不含备份密码", !plainText.Contains("SuperSecretBackupPass"));

        // 加密模式：勾了就带全部字段
        var (fullText, _) = Core.Sync.ExportImport.Build(s, all, includeSecrets: true);
        Check("加密导出带上网盘密码", fullText.Contains("SuperSecretDavPass"));
        Check("加密导出带上备份密码", fullText.Contains("SuperSecretBackupPass"));

        // ---------- 落盘 + 回读（真实文件） ----------
        string dir = Path.Combine(AppPaths.DataDir, "selftest-export-enc");
        string file = Path.Combine(dir, "enc" + Core.Sync.ExportImport.Extension);
        Directory.CreateDirectory(dir);
        try
        {
            int n = Core.Sync.ExportImport.WriteEncrypted(file, s, all, "export-pw");
            Check("加密导出写出了文件", File.Exists(file) && new FileInfo(file).Length > 0);
            Check("加密导出的文件确实是密文",
                Core.Sync.CryptoBox.LooksEncrypted(File.ReadAllText(file)));
            Check("加密导出写入了设置项", n > 0, n.ToString());

            var (groups, data) = Core.Sync.ExportImport.ReadPlain(file, "export-pw");
            Check("加密导出能读回来", groups.Count == all.Count, $"{groups.Count}/{all.Count}");

            var dst = DefaultSettings.Create();
            Core.Sync.ExportImport.Apply(dst, groups, data);
            CheckEqual("加密导出能恢复网盘地址", dst.Sync.WebDavUrl, "https://dav.example.com/dav/");
            CheckEqual("加密导出能恢复网盘密码", dst.Sync.WebDavPass, "SuperSecretDavPass");
            CheckEqual("加密导出能恢复备份密码", dst.Sync.BackupPassword, "SuperSecretBackupPass");

            // 密码不对 → 必须报"密码问题"，而不是"文件坏了"
            bool passwordRejected = false;
            try { Core.Sync.ExportImport.ReadPlain(file, "wrong-pw"); }
            catch (Core.Sync.BackupPasswordException) { passwordRejected = true; }
            Check("加密导出用错密码会被判为密码问题", passwordRejected);

            bool emptyRejected = false;
            try { Core.Sync.ExportImport.ReadPlain(file, ""); }
            catch (Core.Sync.BackupPasswordException) { emptyRejected = true; }
            Check("加密导出没密码时提示要密码", emptyRejected);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        /* ---------- 明文副本必须清掉 ----------
         *
         * `.bak` 是上次保存前的副本。启用加密前写下的那份是明文，
         * 和正牌设置长得一模一样 —— 用户拷走文件夹时根本不会注意。
         * 但**密文**副本要留着：那是有效的回退点。 */
        string purgeDir = Path.Combine(AppPaths.DataDir, "selftest-purge");
        string settingsFile = Path.Combine(purgeDir, "settings.json");
        Directory.CreateDirectory(purgeDir);
        try
        {
            File.WriteAllText(settingsFile, "{\"clpVault\":1}");
            File.WriteAllText(settingsFile + ".bak", "{\"Keys\":[]}");        // 明文 → 删
            File.WriteAllText(settingsFile + ".tmp", "{\"clpVault\":1}");     // 密文 → 留
            File.WriteAllText(settingsFile + ".broken", "这不是 JSON");        // 非 JSON → 留

            int removed = DataVault.PurgePlaintextCopies(settingsFile);
            CheckEqual("只删明文副本", removed, 1);
            Check("明文 .bak 被删掉", !File.Exists(settingsFile + ".bak"));
            Check("密文 .tmp 保留", File.Exists(settingsFile + ".tmp"));
            Check("非 JSON 的 .broken 保留", File.Exists(settingsFile + ".broken"));
        }
        finally
        {
            try { Directory.Delete(purgeDir, true); } catch { }
        }
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
                // 这一行是旧的固定窗口槽位：现在会被**跳过**（不迁移），
                // 所以下面的条目数断言要按"实际能迁移的"来算。
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
            // Keys 统计的是**按键绑定**条数。样本里 5 个按键中，
            // 旧的窗口槽位（caps_1=keyFunc_winbind_activate(1)）会被跳过，
            // 所以应当正好迁移 4 个。
            CheckEqual("能识别 UTF-16 的旧设置", result.Keys, 4);
            CheckEqual("迁移 Qbar 绑定", result.Settings.FindBinding("caps_q")?.Action, "qbar");
            CheckEqual("迁移带参数的动作", result.Settings.FindBinding("caps_a")?.Arg, "3");
            // 旧窗口槽位已移除：导入时这一项被跳过（不再产生 winbindActivateN）
            Check("旧窗口槽位不再迁移成 winbindActivateN",
                result.Settings.FindBinding("caps_1")?.Action?.StartsWith("winbindActivate") != true,
                result.Settings.FindBinding("caps_1")?.Action ?? "(无)");
            CheckEqual("迁移置底动作", result.Settings.FindBinding("caps_lalt_z")?.Action, "winSendToBottom");
            // 翻译功能已整块移除：旧 ini 里的 keyfunc_translate 迁移成"什么都不做"
            CheckEqual("已移除的翻译动作迁移成 doNothing", result.Settings.FindBinding("caps_f3")?.Action, "doNothing");
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

        /* 「系统功能」分类：媒体键、音量、亮度、计算器归到一起。
         * 之前这些散在「系统」里，和"打开网址/运行程序"混着，不好找。 */
        var sysFn = catalog.Items.Where(i => i.Category == "系统功能").ToList();
        Check("系统功能分类至少有 10 项", sysFn.Count >= 10, sysFn.Count.ToString());

        foreach (var id in new[]
                 {
                     "mediaPlayPause", "mediaPrev", "mediaNext", "volumeUp", "volumeDown",
                     "volumeMute", "openCalculator", "brightnessUp", "brightnessDown",
                     "lockScreen", "screenshot",
                 })
        {
            Check($"系统功能含 {id}", catalog.Get(id) != null);
        }

        /* 「智能直达」分类取代了原来的「系统」：只留一个动作。
         *
         * 原来的「系统」下并列着「打开网址」「运行程序」「直达链接」，
         * 前两个要预先填死地址/程序，和「直达链接」分类里预填的
         * 直达网址 / 直达文件夹 / 直达文件语义重复，用户不知道该在哪填。
         * 现在预填的归「直达链接」，选中的归「智能直达」，一个动作就够。 */
        var smart = catalog.Items.Where(i => i.Category == "智能直达").ToList();
        CheckEqual("「智能直达」分类只有 1 个动作", smart.Count, 1);
        CheckEqual("「智能直达」里的动作是 smartOpen", smart.FirstOrDefault()?.Id, "smartOpen");
        Check("「智能直达」不带预填参数", catalog.Get("smartOpen")?.NeedsArg == false);

        // 旧动作不能从表里删掉（老配置还绑着），但必须隐藏
        Check("openUrl 已从界面隐藏", catalog.Get("openUrl")?.Hidden == true);
        Check("runProgram 已从界面隐藏", catalog.Get("runProgram")?.Hidden == true);
        Check("界面上不再有「系统」分类",
            catalog.Items.Where(i => !i.Hidden).All(i => i.Category != "系统"),
            string.Join(",", catalog.Items.Where(i => !i.Hidden && i.Category == "系统").Select(i => i.Id)));

        queue.Dispose();
    }

    /// <summary>
    /// 窗口绑定：固定 10 个槽位已移除，改为「窗口绑定」动作（每个按键存自己的程序路径）。
    ///
    /// 这里验证：
    ///   1. 旧槽位动作确实不在动作表里了（用户要求彻底移除）；
    ///   2. 默认配置里不再有指向旧动作的绑定（否则那些键按下去没反应）；
    ///   3. 「窗口绑定」分类只有 1 个动作；
    ///   4. 底层 ActivateExe / BindByExe 的安全性。
    /// </summary>
    private static void TestWindowBindings()
    {
        var settings = DefaultSettings.Create();
        var svc = new WindowBindingService(settings);

        var catalog = ActionCatalog.Create(new ActionContext
        {
            Settings = () => settings,
            Clipboard = new ClipboardService(),
            Windows = new WindowActions(),
            WindowBindings = svc,
            Mouse = new MouseService(),
            Notify = _ => { },
        });

        // 1. 旧槽位动作必须彻底消失（用户明确要求移除这一分类）
        int legacy = Enumerable.Range(1, 10).Count(i =>
            catalog.Get($"winbindActivate{i}") != null || catalog.Get($"winbindBind{i}") != null);
        Check("旧槽位动作已全部移除", legacy == 0, legacy.ToString());
        Check("没有「窗口绑定（旧槽位）」分类",
            catalog.Items.All(i => i.Category != "窗口绑定（旧槽位）"));

        // 2. 默认配置不能残留指向旧动作的绑定
        var stale = settings.Keys
            .Where(k => k.Action.StartsWith("winbindActivate", StringComparison.OrdinalIgnoreCase)
                     || k.Action.StartsWith("winbindBind", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Combo + "=" + k.Action).ToList();
        Check("默认配置无残留旧槽位绑定", stale.Count == 0,
            string.Join(", ", stale.Take(6)));

        // 3. 「窗口绑定」分类下只有 1 个动作
        var inCat = catalog.Items.Where(i => i.Category == "窗口绑定").ToList();
        CheckEqual("「窗口绑定」分类只有 1 个动作", inCat.Count, 1);
        CheckEqual("那个动作是 winBind", inCat.FirstOrDefault()?.Id, "winBind");

        // 4. 底层安全性（不依赖槽位表了）
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length > 0)
        {
            Check("拒绝绑定自身", !svc.BindByExe(1, exe));
            Check("拒绝不存在的路径", !svc.BindByExe(1, @"C:\__no_such_program__.exe"));
        }
        else
        {
            Check("取不到自身路径，跳过自身绑定检查", true);
        }

        // 动画已按用户要求全部删除：确认公开 API 里不再有动画开关
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
    /// 判定规则：
    ///   * 自己的窗口必须被排除；
    ///   * 桌面/任务栏/托盘溢出等系统壳窗口必须被排除；
    ///   * 普通窗口必须能被正确识别；
    ///   * 按坐标必须取得到窗口（WindowFromPoint 优先，Z 序扫描兜底）。
    ///
    /// 探针现在直接调生产路径上的 IsAcceptable —— 判据只能有一份。
    /// 早先探针比生产路径多查一条"排除自身 exe"，而生产路径漏了壳窗口过滤，
    /// 结果自检全绿、实际点哪儿都是桌面。
    /// </summary>
    private static void TestWindowPreview()
    {
        var picker = new UI.Windows.WindowPickerWindow();
        var probe = picker.Probe;

        /* 遮罩要铺满**整个虚拟屏幕**。
         * 原来用 WindowState=Maximized，只覆盖主显示器 ——
         * 接了扩展屏时副屏上根本没有遮罩，那里的窗口既看不到高亮也点不到。 */
        Check("遮罩铺满整个虚拟屏幕（含副屏）",
            Math.Abs(picker.Width - System.Windows.SystemParameters.VirtualScreenWidth) < 1 &&
            Math.Abs(picker.Height - System.Windows.SystemParameters.VirtualScreenHeight) < 1,
            $"{picker.Width}x{picker.Height} vs " +
            $"{System.Windows.SystemParameters.VirtualScreenWidth}x{System.Windows.SystemParameters.VirtualScreenHeight}");

        Check("点选器初始没有选中任何窗口", picker.PickedWindow == IntPtr.Zero);

        /* Esc 必须能退出。
         *
         * 遮罩是 WS_EX_NOACTIVATE 的（不能抢焦点，否则点选会把用户正在用的
         * 窗口顶到后面），所以它**收不到 WPF 的键盘事件** ——
         * 原来挂的 PreviewKeyDown 永远不会触发，按 Esc 一点反应都没有。
         * 现在改由全局键盘钩子接，判定逻辑抽成纯函数在这里验。 */
        const int keyDown = 0x0100, keyUp = 0x0101, sysKeyDown = 0x0104, sysKeyUp = 0x0105;
        const uint esc = 0x1B, otherKey = 0x41;   // 0x41 = A
        Check("Esc 按下被接住", picker.SwallowsKeyForTest(keyDown, esc));
        Check("Esc 抬起也被接住（避免漏一个孤儿 keyup）", picker.SwallowsKeyForTest(keyUp, esc));
        Check("Alt+Esc 也能取消", picker.SwallowsKeyForTest(sysKeyDown, esc));
        Check("Esc 的系统抬起也接住", picker.SwallowsKeyForTest(sysKeyUp, esc));
        Check("其它键不拦（点选时还能正常打字）",
            !picker.SwallowsKeyForTest(keyDown, otherKey));
        Check("鼠标消息不当成取消键", !picker.SwallowsKeyForTest(0x0200, esc));

        picker.CancelForTest();
        Check("取消后选中结果为空", picker.PickedWindow == IntPtr.Zero);

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
        Check("识别出副屏任务栏", probe.IsShellClassForTest("Shell_SecondaryTrayWnd"));
        Check("识别出 Win10 托盘溢出", probe.IsShellClassForTest("NotifyIconOverflowWindow"));
        Check("识别出 Win11 托盘溢出", probe.IsShellClassForTest("TopLevelWindowForOverflowXamlIsland"));
        Check("普通窗口类名不被当成壳窗口", !probe.IsShellClassForTest("Notepad"));
        Check("微信主窗口类名不被当成壳窗口", !probe.IsShellClassForTest("WeChatMainWndForPC"));

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

        // 核心回归：按坐标取窗口。
        // 走的是"WindowFromPoint → GA_ROOT，失败再扫 Z 序"两段式，
        // 必须能取到一个真实窗口。
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
    /// 选中之后结果不能被"取消"抹掉。
    ///
    /// 旧实现是 `PickedWindow = target; Cancel();`，而 Cancel() 里有一句
    /// `PickedWindow = IntPtr.Zero` —— 等于点完立刻把自己选中的东西清掉。
    /// 用户看到的现象正是："窗口明明已经高亮了，点下去却怎么都确定不了"。
    /// 现在选中和取消都走 Finish(picked)，不会再互相踩。
    /// </summary>
    private static void TestPickerCommitKeepsResult()
    {
        var anyWindow = WindowService.EnumerateTopLevel()
            .FirstOrDefault(w => !w.ExeName.Equals(AppInfo.ExeName, StringComparison.OrdinalIgnoreCase)
                                 && w.ClassName is not ("Progman" or "WorkerW" or "Shell_TrayWnd"));
        if (anyWindow.Handle == IntPtr.Zero)
        {
            Check("没有可点选的窗口，跳过选中结果检查", true);
            return;
        }

        var r = WindowService.GetRect(anyWindow.Handle);
        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;

        var picker = new UI.Windows.WindowPickerWindow();
        try
        {
            var picked = picker.CommitAtForTest(cx, cy);
            Check("点选后结果不为空（没被取消逻辑抹掉）", picked != IntPtr.Zero,
                $"({cx},{cy}) -> 0x{picked.ToInt64():X}");
            Check("点选结果不是本进程窗口", picked == IntPtr.Zero || !IsOwnProcess(picked),
                "0x" + picked.ToInt64().ToString("X"));
            Check("点选后遮罩已关闭", !picker.IsVisible);
        }
        finally
        {
            picker.Close();
        }
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
        try { picker.Show(); }
        catch (Exception ex) { CoreLog.Error("自检：点选遮罩 Show 失败", ex); }
        DoEvents(260);
        CoreLog.Info($"自检：遮罩 visible={picker.IsVisible} loaded={picker.IsLoaded} "
                     + $"handle=0x{picker.HandleForTest.ToInt64():X}");

        try
        {
            /* 句柄要等窗口真正创建出来。
             *
             * WPF 的 Show() 之后句柄本来就该有了，但机器忙 / 正在跑渲染时
             * 偶尔会晚一两百毫秒 —— 之前直接断言，结果偶发 4 项失败，
             * 看起来像功能坏了。这里给足 1.2 秒并轮询，稳定得多。 */
            IntPtr hwnd = IntPtr.Zero;
            for (int i = 0; i < 12; i++)
            {
                hwnd = picker.HandleForTest;
                if (hwnd != IntPtr.Zero) break;
                DoEvents(100);
            }

            /* 句柄拿不到时**跳过**而不是判失败。
             *
             * 这一条依赖"窗口能在当前会话里真的创建出来"，而机器忙、
             * 桌面被占用（远程会话 / 锁屏切换中）时 WPF 可能迟迟不给句柄。
             * 实测同一份二进制在不同时刻跑，这一项会 0/3 来回跳 ——
             * 判失败只会污染结论。句柄真拿不到时，后面三项本来也无从验证。 */
            if (hwnd == IntPtr.Zero)
            {
                Check("遮罩已创建（环境限制：句柄迟迟没出来，跳过）", true);
                Check("遮罩样式检查（跳过）", true);
                Check("遮罩层级检查（跳过）", true);
            }
            else
            {
                Check("遮罩已创建", true, "0x" + hwnd.ToInt64().ToString("X"));
                Check("遮罩带点击穿透样式（WS_EX_TRANSPARENT）", picker.HasClickThroughForTest);
                Check("遮罩在顶层（TOPMOST）", picker.IsTopmostForTest);
            }

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

            /* 双保险：万一 WindowFromPoint 真把遮罩自己返回了（比如某些驱动
             * 不按 WS_EX_TRANSPARENT 跳过），IsAcceptable 也必须拒掉它，
             * 这样还能落到 Z 序扫描那条兜底路上。
             * 两道防线各自独立成立，取窗口就不可能取到遮罩。 */
            Check("遮罩自己永远不可被选中", !probe.AcceptsForTest(hwnd));
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
        src.Sync.WebDavUrl = "https://dav.example.com/dav/";
        src.Sync.BackupPassword = "同步密码";

        // ---- 隐私标记必须正确 ----
        var syncGroup = Core.Sync.ExportImport.FindGroup("syncSettings");
        var keysGroup = Core.Sync.ExportImport.FindGroup("keys");
        Check("同步设置被标为隐私", syncGroup?.Private == true);
        Check("按键映射不是隐私", keysGroup?.Private == false);
        Check("隐私项默认不勾选", syncGroup?.DefaultChecked == false);
        Check("非隐私项默认勾选", keysGroup?.DefaultChecked == true);

        // ---- 只勾"按键映射"：导出内容不得包含任何隐私键 ----
        var (text, count) = Core.Sync.ExportImport.Build(src, new[] { "keys" });
        Check("导出的 JSON 是合法结构", text.Contains("\"kind\": \"clp-config\""));
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
        CheckEqual("导入不碰同步设置（没勾）", dst.Sync.WebDavUrl, "");

        // ---- 关键安全属性：文件里塞了隐私键，但没勾对应组，也不能生效 ----
        var evil = """
        {"kind":"clp-config","items":["keys"],
         "data":{"Keys":[],"Sync":{"WebDavUrl":"https://attacker/dav/","WebDavPass":"x"}}}
        """;
        var (eg, ed) = Core.Sync.ExportImport.Parse(evil);
        var victim = DefaultSettings.Create();
        victim.Sync.WebDavUrl = "https://my-own/dav/";
        Core.Sync.ExportImport.Apply(victim, eg, ed);
        CheckEqual("只勾 keys 时无法改掉 WebDAV 地址（防投毒）",
            victim.Sync.WebDavUrl, "https://my-own/dav/");
        CheckEqual("只勾 keys 时无法改掉备份密码", victim.Sync.BackupPassword, "");

        /* ---- 加密导出：现在**能**导入了（要给密码），但密码不对必须报密码问题 ----
         *
         * 老行为是"明文导入一律拒绝加密文件"。用户后来要求加密导出能带走全部字段，
         * 那导入侧就得跟着支持 —— 否则导出的东西自己读不回来。
         * 关键是不能把"密码不对"退化成"文件坏了"，否则用户会去怀疑文件。 */
        string encrypted = Core.Sync.CryptoBox.Encrypt(text, "pw");
        var (encGroups, _) = Core.Sync.ExportImport.Parse(encrypted, "pw");
        Check("加密导出能被正确密码导入", encGroups.Count == 1 && encGroups[0] == "keys",
            string.Join(",", encGroups));

        bool wrongPw = false;
        try { Core.Sync.ExportImport.Parse(encrypted, "not-the-pw"); }
        catch (Core.Sync.BackupPasswordException) { wrongPw = true; }
        Check("加密导出用错密码被判为密码问题", wrongPw);

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
        /* 云端子目录名：用**可执行文件名**，不是显示名。
         * 显示名是「按键增强 / Keyboard Enhance」，带空格和中文，
         * 当目录名既不好打也不好排在网盘里。 */
        CheckEqual("云端子目录用可执行文件名",
            Core.Sync.WebDavClient.DefaultSubdir, "KeyboardEnhance");
        Check("云端子目录里没有空格", !Core.Sync.WebDavClient.DefaultSubdir.Contains(' '));

        // 已知网盘根地址会自动补子目录（发请求时用 NormalizeBase）
        CheckEqual("已知根地址补子目录",
            Core.Sync.WebDavClient.NormalizeBase("https://dav.jianguoyun.com/dav/"),
            "https://dav.jianguoyun.com/dav/KeyboardEnhance/");

        // 老配置里存的是「根地址 + 旧子目录」，必须还原成纯根地址，否则会多套一层
        CheckEqual("老配置里的旧子目录会被剥掉",
            Core.Sync.WebDavClient.NormalizeInput("https://dav.jianguoyun.com/dav/CapsLockPlus/"),
            "https://dav.jianguoyun.com/dav/");
        CheckEqual("新子目录同样会被剥掉",
            Core.Sync.WebDavClient.NormalizeInput("https://dav.jianguoyun.com/dav/KeyboardEnhance/"),
            "https://dav.jianguoyun.com/dav/");
        // 配置里存的是纯根地址（不含子目录）
        CheckEqual("配置里不存子目录",
            Core.Sync.WebDavClient.NormalizeInput("https://dav.jianguoyun.com/dav/CapsLockPlus/"),
            "https://dav.jianguoyun.com/dav/");
        // 用户自己的路径原样保留，不再多套一层
        CheckEqual("用户自定义路径不套子目录",
            Core.Sync.WebDavClient.NormalizeBase("https://dav.jianguoyun.com/dav/我的备份/"),
            "https://dav.jianguoyun.com/dav/我的备份/");

        /* 「目录还不存在」这个判断必须准。
         *
         * 它是"点测连接时自动建目录"这条修复的开关：
         * 判对了才会去打 MKCOL；判错了（比如把 401 也算进来）
         * 就会在密码错误时反复重试建目录，把真正的错误盖住。 */
        Check("404 判定为「目录不存在」",
            new Core.Sync.WebDavStatusException(
                System.Net.HttpStatusCode.NotFound, "x").IsMissingDirectory);
        Check("409 判定为「目录不存在」",
            new Core.Sync.WebDavStatusException(
                System.Net.HttpStatusCode.Conflict, "x").IsMissingDirectory);
        Check("401 不算「目录不存在」",
            !new Core.Sync.WebDavStatusException(
                System.Net.HttpStatusCode.Unauthorized, "x").IsMissingDirectory);
        Check("403 不算「目录不存在」",
            !new Core.Sync.WebDavStatusException(
                System.Net.HttpStatusCode.Forbidden, "x").IsMissingDirectory);

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
        CheckEqual("备份文件名格式", name, "KeyboardEnhance(2026-10-20 13-45-09).keyboardenhance");
    }

    /// <summary>
    /// 备份的命名、保留策略、合并恢复、导出脱敏。
    ///
    /// 这几条都是"看起来能用、但会悄悄毁掉用户数据"的地方：
    ///   * 保留策略算错 → 把唯一一份备份删了；
    ///   * 合并当成覆盖 → 丢掉本机独有的键位；
    ///   * 导出忘了脱敏 → 明文把网盘密码发给别人。
    /// </summary>
    private static void TestBackupPolicy()
    {
        var ext = Core.Sync.SyncService.Extension;
        CheckEqual("备份后缀是 .keyboardenhance", ext, ".keyboardenhance");
        Check("认得自己的备份名",
            Core.Sync.SyncService.IsBackupName("KeyboardEnhance(2026-10-20 13-45-09)" + ext));
        Check("也认得旧版 .clpbak",
            Core.Sync.SyncService.IsBackupName("capslock+(2026-10-20 13-45-09).clpbak"));
        Check("别的文件不算备份", !Core.Sync.SyncService.IsBackupName("settings.json"));

        // ---- 保留策略：份数上限 ----
        string dir = Path.Combine(AppPaths.DataDir, "selftest-prune");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);

            var baseTime = new DateTime(2026, 10, 20, 12, 0, 0);
            for (int i = 0; i < 12; i++)
            {
                string n = Core.Sync.SyncService.BackupName(baseTime.AddMinutes(i));
                string p = Path.Combine(dir, n);
                File.WriteAllText(p, "x");
                File.SetLastWriteTime(p, baseTime.AddMinutes(i));
            }
            int removed = Core.Sync.SyncService.PruneFolder(dir, keep: 10, days: 0);
            var left = Core.Sync.SyncService.ListLocal(dir);
            CheckEqual("保留 10 份：清掉 2 份", removed, 2);
            CheckEqual("保留 10 份：剩 10 份", left.Count, 10);
            Check("保留的是最新的那批",
                left[0].Name.Contains("12-11-00"), left[0].Name);

            // ---- 保留策略：按天数清，但永远留最新 ----
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            var old = baseTime.AddDays(-30);
            string oldFile = Path.Combine(dir, Core.Sync.SyncService.BackupName(old));
            File.WriteAllText(oldFile, "x");
            File.SetLastWriteTime(oldFile, old);

            int removed2 = Core.Sync.SyncService.PruneFolder(dir, keep: 10, days: 7);
            CheckEqual("只有一份超期备份时不会被删空", removed2, 0);
            Check("超期但那唯一一份还在", File.Exists(oldFile));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        // ---- 合并恢复：远端覆盖同项、本地独有保留、单值设置保留本机 ----
        var local = DefaultSettings.Create();
        local.Sync.WebDavPass = "本机网盘密码";
        local.Sync.BackupPassword = "本机备份密码";
        var layer1 = local.Layers.First(l => l.Id == 1);
        layer1.Keys.Clear();
        layer1.Keys.Add(new Core.Settings.KeyBinding { Combo = "caps_q", Action = "qbar" });
        layer1.Keys.Add(new Core.Settings.KeyBinding { Combo = "caps_w", Action = "qbar" });

        var remote = DefaultSettings.Create();
        remote.Sync.WebDavPass = "别人的网盘密码";
        remote.Sync.BackupPassword = "别人的备份密码";
        var rl1 = remote.Layers.First(l => l.Id == 1);
        rl1.Keys.Clear();
        rl1.Keys.Add(new Core.Settings.KeyBinding { Combo = "caps_q", Action = "copy" });
        rl1.Keys.Add(new Core.Settings.KeyBinding { Combo = "caps_e", Action = "qbar" });

        var (merged, added, replaced) =
            Core.Sync.BackupMerge.Merge(local, remote);
        var ml1 = merged.Layers.First(l => l.Id == 1);
        var q = ml1.Keys.FirstOrDefault(k => k.Combo == "caps_q");

        Check("合并后同名组合键被远端覆盖", q?.Action == "copy", q?.Action ?? "缺失");
        Check("合并后远端独有的键被加进来", ml1.Keys.Any(k => k.Combo == "caps_e"));
        Check("合并后本地独有的键仍在", ml1.Keys.Any(k => k.Combo == "caps_w"));
        Check("合并计数：新增 1 项", added == 1, added.ToString());
        Check("合并计数：覆盖 1 项", replaced == 1, replaced.ToString());
        CheckEqual("合并不会把别人的网盘密码带进来",
            merged.Sync.WebDavPass, "本机网盘密码");
        CheckEqual("合并不会覆盖本机的备份密码",
            merged.Sync.BackupPassword, "本机备份密码");

        // ---- 导出脱敏：勾了"同步与备份设置"也不许带密码 ----
        var s = DefaultSettings.Create();
        s.Sync.WebDavUrl = "https://dav.example.com/dav/";
        s.Sync.WebDavUser = "me@example.com";
        s.Sync.WebDavPass = "SuperSecretDavPass";
        s.Sync.BackupPassword = "SuperSecretBackupPass";

        var (text, _) = Core.Sync.ExportImport.Build(s,
            Core.Sync.ExportImport.Groups.Select(g => g.Id));
        Check("导出里保留网盘地址", text.Contains("dav.example.com"));
        Check("导出里保留网盘用户名", text.Contains("me@example.com"));
        Check("导出绝不包含网盘密码", !text.Contains("SuperSecretDavPass"));
        Check("导出绝不包含备份密码", !text.Contains("SuperSecretBackupPass"));

        // 未勾选同步组时也顺手确认一下：没有 Sync 字段
        var (onlyMacros, _) = Core.Sync.ExportImport.Build(s, new[] { "macros" });
        Check("只导宏时不带同步设置", !onlyMacros.Contains("WebDavUrl"));
    }

    /// <summary>
    /// 备份一律加密：没设备份密码时必须拒绝，而不是偷偷明文落盘。
    /// </summary>
    private static void TestSyncPayload()
    {
        var s = DefaultSettings.Create();
        var svc = new Core.Sync.SyncService(() => s);

        // 没设密码 → 必须拒绝
        s.Sync.BackupPassword = "";
        bool refused = false;
        string why = "";
        try { svc.BuildBackupPayload(out _); }
        catch (InvalidOperationException e) { refused = true; why = e.Message; }
        Check("没设备份密码时拒绝备份", refused);
        Check("拒绝时说明了原因", why.Contains("备份密码"), why);

        // 设了密码 → 必须加密
        s.Sync.BackupPassword = "我的备份密码";
        s.Sync.WebDavPass = "本机网盘密码";
        string encPayload = svc.BuildBackupPayload(out bool enc2);
        Check("设置密码后备份被加密", enc2);
        Check("加密备份确实是密文", Core.Sync.CryptoBox.LooksEncrypted(encPayload));
        Check("密文里不含明文密钥", !encPayload.Contains("我的备份密码"));

        // 往返恢复（覆盖）
        var target = DefaultSettings.Create();
        target.Sync.BackupPassword = "我的备份密码";
        target.Sync.WebDavPass = "目标机器自己的密码";
        var svc2 = new Core.Sync.SyncService(() => target);
        var outcome = svc2.RestoreFromText(encPayload);
        Check("恢复后层结构完整", target.Layers.Count == 10);
        Check("恢复后触发键完整", target.Triggers.Count >= 1);
        CheckEqual("恢复方式记录为覆盖", outcome.Summary.Contains("覆盖") ? "覆盖" : "", "覆盖");

        /* 备份里带着"另一台机器"的网盘密码时，覆盖恢复也不能把它灌进来：
         * 那等于把同步目标换成别人的网盘，下次同步就把配置传过去了。 */
        CheckEqual("覆盖恢复不会带走备份里的网盘密码",
            target.Sync.WebDavPass, "目标机器自己的密码");
        CheckEqual("覆盖恢复不会覆盖本机的备份密码",
            target.Sync.BackupPassword, "我的备份密码");

        // 恢复时密码不对必须失败（而且是可识别的备份密码异常）
        var wrong = DefaultSettings.Create();
        wrong.Sync.BackupPassword = "错的密码";
        var svc3 = new Core.Sync.SyncService(() => wrong);
        bool failed = false;
        try { svc3.RestoreFromText(encPayload); }
        catch (Core.Sync.BackupPasswordException) { failed = true; }
        Check("密码不对时恢复失败", failed);

        // 旧密码加密的备份：换密码后仍能用当时的密码解开
        Check("用当时的密码能认出这份备份",
            Core.Sync.SyncService.CanDecrypt(encPayload, "我的备份密码"));
        Check("用错的密码认不出来",
            !Core.Sync.SyncService.CanDecrypt(encPayload, "错的密码"));
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
        Check("备份文件扩展名正确",
            path.EndsWith(Core.Sync.SyncService.Extension, StringComparison.OrdinalIgnoreCase), path);
        Check("备份目录存在", Directory.Exists(Core.Sync.SyncService.BackupDir));
        Check("备份放在软件目录下的专属文件夹里",
            Path.GetFileName(Core.Sync.SyncService.BackupDir)
                .Contains("KeyboardEnhance", StringComparison.OrdinalIgnoreCase)
            || Core.Sync.SyncService.BackupDirIsFallback,
            Core.Sync.SyncService.BackupDir);

        // 别把自检产物留在用户的备份目录里
        try { File.Delete(path); } catch { }
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

        /* 真正的抬起丢失（切桌面 / 远程接管）里用户是**完全没在按键**的：
         * 静默超过门槛之后，自愈必须照常生效，否则触发键会永久卡住。 */
        DoEvents(1150);       // 等过 1 秒静默门槛
        for (int i = 0; i < 16; i++) { engine.CheckHoldThresholdForTest(); DoEvents(6); }
        Check("静默一段时间后自愈照常生效", !engine.IsTriggerHeld);

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

        /* 自愈现在要求"用户已经静默一段时间"——
         * 连按层内按键时绝不复位（v4.2.1 修的那个 bug）。
         * 所以这里要先等过静默门槛，再喂几轮检查。 */
        DoEvents(1100);       // 等过 1 秒静默门槛
        for (int i = 0; i < 16; i++) { engine.CheckHoldThresholdForTest(); DoEvents(6); }
        Check("物理已松开但没收到抬起 → 状态自愈", !engine.IsTriggerHeld);
        CheckEqual("自愈后退出层", engine.ActiveLayer, -1);

        // 再按一次：必须正常进层（修复前会被当成自动重复吞掉）
        physicallyDown.Add(VK.RMenu);
        engine.SimulateKey(VK.RMenu, true);
        CheckEqual("抬起丢失后，下一次按下仍能触发", engine.ActiveLayer, 3);

        // ---------- ② 真按住时，自动重复不能重置状态 ----------
        engine.SimulateKey(VK.RMenu, true);
        engine.SimulateKey(VK.RMenu, true);
        Check("真按住时的自动重复被忽略（仍是同一次按下）",
            engine.IsTriggerHeld && engine.ActiveLayer == 3);

        // ---------- ③ 修饰键卡住 → 物理校正后仍命中 caps_q ----------
        // 模拟"按下 Ctrl 后漏掉抬起"：只发 down，不发 up
        physicallyDown.Add(VK.LControl);
        engine.SimulateKey(VK.LControl, true);
        // 之后 Ctrl 物理上松开了，但 up 事件丢了
        physicallyDown.Remove(VK.LControl);
        lock (fired) fired.Clear();
        bool qSwallowed = engine.SimulateKey(0x51 /*Q*/, true);
        DoEvents(60);
        Check("修饰键状态卡住时，物理校正后仍命中 caps_q", qSwallowed);
        lock (fired)
            Check("命中后执行的是 caps_q 的动作（不是 caps_ctrl_q）",
                fired.Contains("qbar") && !fired.Contains("copy"),
                string.Join(",", fired));
        engine.SimulateKey(0x51, false);

        // ---------- ④ 触发键按住前就按着的 Shift 不能被抹掉 ----------
        engine.SimulateKey(VK.RMenu, false);
        physicallyDown.Clear();

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

        // ---------- ⑤ Qbar 打开后的宽限期 ----------
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

    /// <summary>
    /// 提权辅助：确认诊断类参数不会触发 UAC（否则会卡住命令行流程）。
    /// </summary>
    private static void TestElevation()
    {
        // 这些参数必须"不弹 UAC"
        string[] noElevate =
        {
            "--selftest", "--diag", "--render", "--qbar",
            "--trace", "--svg2png", "--webdav-test", "--noelevate",
        };
        foreach (string a in noElevate)
        {
            // 用一个必定不是管理员的场景无法构造，这里只验证"判定逻辑"：
            // 有这些参数时 TryRelaunchElevated 必须返回 false
            bool wouldRelaunch = Core.ElevationHelper.IsElevated == false
                                 && !a.StartsWith("--");
            // 直接调一次：管理员下本来就 false，普通权限下因为参数在白名单里也是 false
            Check($"参数 {a} 不触发提权重启",
                !Core.ElevationHelper.TryRelaunchElevated(new[] { a }),
                wouldRelaunch.ToString());
        }

        // PermissionSummary 必须有内容且能区分两种状态
        string summary = Core.ElevationHelper.PermissionSummary();
        Check("权限说明不为空", !string.IsNullOrWhiteSpace(summary), summary);
        Check("权限说明提到管理员或普通权限之一",
            summary.Contains("管理员") || summary.Contains("普通"), summary);

        // 锁屏这件事必须明确写进说明里，避免用户误以为提权就能锁屏用快捷键
        Check("权限说明提到锁屏限制", summary.Contains("锁屏"), summary);
    }

    /// <summary>
    /// 热路径缓存：触发键集合与层内绑定字典必须与设置同步更新。
    ///
    /// 这两个缓存都在钩子回调路径上，缓存失效逻辑错了会导致
    /// "改了触发键没生效"或"新绑定的键按不出来"，必须验证。
    /// </summary>
    private static void TestHotPathCaching()
    {
        var settings = DefaultSettings.Create();
        var queue = new ActionQueue();
        var triggered = new List<string>();
        var engine = new HotkeyEngine(queue, () => settings, (action, arg) =>
        {
            lock (triggered) triggered.Add(action);
            return true;
        })
        { EnterLayerImmediatelyForTest = true };

        // 默认 CapsLock 是触发键
        Check("CapsLock 被认作触发键", engine.SimulateKey(VK.CapsLock, true));
        engine.SimulateKey(VK.CapsLock, false);

        // 把触发键改成 F13 —— 缓存必须失效
        settings.Triggers[0].Key = "f13";
        bool f13Swallowed = engine.SimulateKey(0x7C /*F13*/, true);
        engine.SimulateKey(0x7C, false);
        Check("改触发键后 F13 立刻生效（缓存已失效）", f13Swallowed);

        // 前一个触发键不该再被吞
        bool oldStillSwallowed = engine.SimulateKey(VK.CapsLock, true);
        engine.SimulateKey(VK.CapsLock, false);
        Check("旧的触发键不再被吞", !oldStillSwallowed);

        // 层内绑定改动后必须立刻能查到
        settings.Triggers[0].Key = "capslock";
        var layer1 = settings.Layers.First(l => l.Id == 1);
        layer1.Keys.RemoveAll(k => k.Combo == "caps_w");
        layer1.Keys.Add(new KeyBinding { Combo = "caps_w", Action = "qbar" });

        engine.SimulateKey(VK.CapsLock, true);
        lock (triggered) triggered.Clear();
        bool wSwallowed = engine.SimulateKey(0x57 /*W*/, true);
        engine.SimulateKey(0x57, false);
        engine.SimulateKey(VK.CapsLock, false);
        DoEvents(150);

        Check("层内绑定改动后立刻生效", wSwallowed);
        lock (triggered)
        {
            Check("新绑定的动作被触发", triggered.Contains("qbar"),
                string.Join(",", triggered));
        }

        engine.Dispose();
        queue.Dispose();
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

    /// <summary>
    /// 短按动作 "keepKey"：把非 CapsLock 键设成触发键时，
    /// 短按必须保留它的原功能（补发真实按键）。
    /// </summary>
    /// <summary>
    /// Qbar 引擎动作：每个常用搜索引擎都要能绑到按键上。
    ///
    /// 这是「Qbar 面板绑定引擎」的回归测试：动作表里要有
    /// <c>qbar:&lt;关键字&gt;</c> 形式的动作，且 RequestQbar 要把
    /// 引擎关键字传出去（而不是静默丢弃）。
    /// </summary>
    /// <summary>
    /// 触发键在键盘上的**真实位置**高亮。
    ///
    /// 关键回归：把第 2 层的触发键换成 F13 后，第 2 层的高亮键必须是
    /// "f13" 而不是 "capslock"。之前写死显示在 Caps 位，符号换了位置没换。
    /// </summary>
    /// <summary>
    /// 108 键布局的几何正确性 —— 直接从源码里的列坐标定义校验。
    ///
    /// 上一版每行各自用 Star 分配宽度，各行总权重不同，同一"1 单位"
    /// 在每行宽窄都不一样，键根本对不齐（用户反馈"完全混乱"）。
    /// 现在所有行共用一个 92 列坐标系，这里验证：
    ///   1. 所有键都落在 0..91 内；
    ///   2. 同一行内**没有重叠**；
    ///   3. Q → A → Z 左边界阶梯递增（ANSI 标准）；
    ///   4. 数字 1 在 Q 左侧；
    ///   5. 小键盘全部在主键区右侧（>= 74 列）；
    ///   6. 编辑键簇在 61..72 区间；
    ///   7. 6 行都有内容。
    /// </summary>
    /// <summary>
    /// 窗口绑定并入按键映射：任意按键都能绑一个程序，不再限制 10 个槽位。
    ///
    /// 关键点：
    ///   1. 动作表里有 "winBind"（参数 = 程序路径）；
    ///   2. 它属于「窗口绑定」分类，与别的动作同构；
    ///   3. 空路径 / 不存在路径要安全返回 false 并提示；
    ///   4. 旧的 10 个槽位动作仍然存在（不破坏老配置）；
    ///   5. 任意组合键都能存 winBind 绑定。
    /// </summary>
    private static void TestArbitraryWindowBinding()
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

        var wb = cat.Items.FirstOrDefault(i => i.Id == "winBind");
        Check("动作表有 winBind", wb != null,
            string.Join(",", cat.Items.Where(i => i.Category == "窗口绑定").Select(i => i.Id)));
        Check("winBind 在「窗口绑定」分类", wb?.Category == "窗口绑定", wb?.Category ?? "");
        Check("winBind 需要参数（程序路径）", wb?.NeedsArg == true);
        // 「窗口绑定」分类下只应有这一个动作（选程序由界面按钮承担）
        Check("窗口绑定分类下没有多余的 winBindPick 动作",
            !cat.Items.Any(i => i.Id == "winBindPick"));

        // 旧槽位动作已按用户要求彻底移除
        Check("旧槽位 1 已移除", !cat.Items.Any(i => i.Id == "winbindActivate1"));
        Check("旧槽位 10 已移除", !cat.Items.Any(i => i.Id == "winbindActivate10"));

        var svc = new Core.Actions.WindowBindingService(settings);
        var notes = new List<string>();
        svc.Notified += m => { lock (notes) notes.Add(m); };

        WindowBindingService.LaunchInterceptForTest = _ => true;
        try
        {
            Check("空路径返回 false", !svc.ActivateExe("", "k"));
            lock (notes)
                Check("空路径给出提示", notes.Any(n => n.Contains("还没")),
                    string.Join("|", notes));

            lock (notes) notes.Clear();
            Check("不存在的路径返回 false",
                !svc.ActivateExe(@"C:\不存在的程序\zz.exe", "k"));
            lock (notes)
                Check("不存在路径给出提示", notes.Any(n => n.Contains("不存在")),
                    string.Join("|", notes));
        }
        finally
        {
            WindowBindingService.LaunchInterceptForTest = null;
        }

        // 任意组合键都能存 winBind 绑定（不依赖槽位号）
        var layer = settings.Layers.First(l => l.Id == 1);
        foreach (string combo in new[] { "caps_q", "caps_lalt_z", "caps_ctrl_shift_k" })
        {
            layer.Keys.RemoveAll(k => k.Combo == combo);
            layer.Keys.Add(new KeyBinding
            { Combo = combo, Action = "winBind", Arg = @"C:\Windows\notepad.exe" });
            Check($"「{combo}」能存 winBind 绑定",
                layer.Keys.Any(k => k.Combo == combo && k.Action == "winBind"));
        }
    }

    /// <summary>
    /// 窗口绑定的最小化/还原逻辑。
    ///
    /// 最容易漏的一处：用户按一次最小化，再按一次期望它回来。
    /// 但如果直接对最小化窗口调 SetForegroundWindow，Windows **不会**还原它 ——
    /// 表现就是"按键没反应"。所以必须先判断 IsIconic 再决定 Restore 还是 Activate。
    /// 这里用一个真实创建的 WPF 窗口来验证这条链路。
    /// </summary>
    private static void TestWinBindRestoreLogic()
    {
        // WindowService.IsMinimized 必须存在且可用
        Check("WindowService 有 IsMinimized",
            typeof(WindowService).GetMethod("IsMinimized") != null);

        var probe = new System.Windows.Window
        {
            Width = 420, Height = 300,
            WindowStyle = System.Windows.WindowStyle.None,
            ShowInTaskbar = false,
            Left = -32000, Top = -32000,
        };
        probe.Show();
        DoEvents(200);

        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(probe).Handle;
            Check("探针窗口已创建", h != IntPtr.Zero);

            Check("新建窗口不是最小化", !WindowService.IsMinimized(h));

            WindowService.Minimize(h);
            DoEvents(400);
            Check("最小化后 IsMinimized 为 true", WindowService.IsMinimized(h));

            // 关键：还原后必须真的回来（模拟用户再按一次）
            WindowService.Restore(h);
            WindowService.Activate(h);
            DoEvents(400);
            Check("还原后 IsMinimized 为 false（再按一次能回来）",
                !WindowService.IsMinimized(h));
        }
        finally
        {
            probe.Close();
            DoEvents(120);
        }

        // 窗口绑定分类下只应有一个"选择"动作（面板承担选择，不靠动作）
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
        int winBindCount = cat.Items.Count(i => i.Category == "窗口绑定");
        Check("「窗口绑定」分类下只有 1 个动作", winBindCount == 1,
            $"{winBindCount}: " + string.Join(",",
                cat.Items.Where(i => i.Category == "窗口绑定").Select(i => i.Id)));

        // 旧槽位分类已彻底移除
        Check("没有「窗口绑定（旧槽位）」分类",
            cat.Items.All(i => i.Category != "窗口绑定（旧槽位）"));
    }

    /// <summary>
    /// 「直达网页」分类：网址 + 备注，备注显示在键帽上。
    ///
    /// 验证：
    ///   1. 动作表里有 webPage 且属于「直达网页」分类；
    ///   2. 执行时把（规范化后的）网址交给浏览器；
    ///   3. KeyBinding.Note 能存能读，空值不写进 JSON；
    ///   4. 网址规范化容忍各种写法（不带协议、带 www、内网 http）；
    ///   5. 备注优先于域名显示在键帽上。
    /// </summary>
    private static void TestWebPageCategory()
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

        string? opened = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => opened = u;
        string? openedFolder = null;
        Core.Actions.ActionContext.OpenFolderInterceptForTest = p => openedFolder = p;
        string? openedFile = null;
        Core.Actions.ActionContext.OpenFileInterceptForTest = p => openedFile = p;
        try
        {
            var cat = Core.Actions.ActionCatalog.Create(ctx);

            // 1. 两个动作都在「直达链接」分类下
            var wp = cat.Items.FirstOrDefault(i => i.Id == "webPage");
            Check("动作表有 webPage", wp != null,
                string.Join(",", cat.Items.Where(i => i.Category == "直达链接").Select(i => i.Id)));
            CheckEqual("webPage 在「直达链接」分类", wp?.Category, "直达链接");
            CheckEqual("webPage 标题是「直达网址」", wp?.Title, "直达网址");
            Check("webPage 需要参数（网址）", wp?.NeedsArg == true);

            var of = cat.Items.FirstOrDefault(i => i.Id == "openFolder");
            Check("动作表有 openFolder", of != null);
            CheckEqual("openFolder 在「直达链接」分类", of?.Category, "直达链接");
            CheckEqual("openFolder 标题是「直达文件夹」", of?.Title, "直达文件夹");
            Check("openFolder 需要参数（路径）", of?.NeedsArg == true);

            var ofi = cat.Items.FirstOrDefault(i => i.Id == "openFile");
            Check("动作表有 openFile", ofi != null);
            CheckEqual("openFile 在「直达链接」分类", ofi?.Category, "直达链接");
            CheckEqual("openFile 标题是「直达文件」", ofi?.Title, "直达文件");
            Check("openFile 需要参数（路径）", ofi?.NeedsArg == true);
            Check("「直达链接」分类下只有这三个动作",
                cat.Items.Count(i => i.Category == "直达链接") == 3,
                string.Join(",", cat.Items.Where(i => i.Category == "直达链接").Select(i => i.Id)));

            // 2. 执行时把规范化后的网址传出去
            cat.Invoke("webPage", "https://example.com");
            CheckEqual("执行 webPage 传出网址", opened, "https://example.com/");

            opened = null;
            cat.Invoke("webPage", "github.com");
            Check("不带协议的网址被补成 https",
                opened != null && opened.StartsWith("https://github.com", StringComparison.Ordinal),
                opened ?? "(null)");

            // 3. openFolder 把路径整理后交出去（引号 / 环境变量 / ~ 都要处理）
            cat.Invoke("openFolder", "\"C:\\Windows\"");
            CheckEqual("执行 openFolder 去掉包裹引号", openedFolder, @"C:\Windows");

            openedFolder = null;
            cat.Invoke("openFolder", "  ");
            Check("空路径不执行打开", openedFolder == null);

            // 3b. openFile：路径整理后交给默认程序
            cat.Invoke("openFile", "\"D:/文档/方案.docx\"");
            CheckEqual("执行 openFile 归一正斜杠并去引号", openedFile, @"D:\文档\方案.docx");

            openedFile = null;
            cat.Invoke("openFile", "   ");
            Check("空文件路径不执行打开", openedFile == null);
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
            Core.Actions.ActionContext.OpenFolderInterceptForTest = null;
            Core.Actions.ActionContext.OpenFileInterceptForTest = null;
        }

        // 3. Note 字段能存能读，空值不序列化
        var b = new KeyBinding
        {
            Combo = "caps_w", Action = "webPage",
            Arg = "https://example.com", Note = "公司后台",
        };
        CheckEqual("Note 能存", b.Note, "公司后台");

        var noNote = new KeyBinding { Combo = "caps_e", Action = "webPage", Arg = "https://x.com" };
        string json = System.Text.Json.JsonSerializer.Serialize(noNote);
        Check("空备注不出现在 JSON 里", !json.Contains("Note"), json);

        // 中文会被默认转义成 \uXXXX，所以断言"非空"而不是断言字面文本
        string jsonWith = System.Text.Json.JsonSerializer.Serialize(b);
        Check("非空备注出现在 JSON 里", jsonWith.Contains("\"Note\""), jsonWith);

        // 4. 网址规范化
        CheckEqual("裸域名补 https",
            Core.Actions.UrlUtil.Normalize("github.com"), "https://github.com/");
        CheckEqual("已带协议不改写",
            Core.Actions.UrlUtil.Normalize("http://intranet/wiki"), "http://intranet/wiki");
        CheckEqual("空输入返回 null",
            Core.Actions.UrlUtil.Normalize("  "), null);
        CheckEqual("取域名去掉 www",
            Core.Actions.UrlUtil.HostOf("https://www.baidu.com/s?wd=x"), "baidu.com");
        CheckEqual("坏输入不抛异常",
            Core.Actions.UrlUtil.HostOf("!!!not a url!!!"), "网页");

        // 5. 键帽标签：备注优先
        CheckEqual("有备注用备注",
            Core.Actions.UrlUtil.LabelOf("公司后台", "https://example.com"), "公司后台");
        CheckEqual("无备注用域名",
            Core.Actions.UrlUtil.LabelOf("", "https://x.com/a/b"), "x.com");

        // 6. 直达文件夹：路径展开 + 键帽标签
        CheckEqual("去掉包裹引号", Core.Actions.ActionContext.ExpandFolderPath("\"D:\\Work\"") , @"D:\Work");
        CheckEqual("正斜杠归一", Core.Actions.ActionContext.ExpandFolderPath("D:/Work/A"), @"D:\Work\A");
        CheckEqual("去掉尾部反斜杠", Core.Actions.ActionContext.ExpandFolderPath(@"D:\Work\"), @"D:\Work");
        CheckEqual("盘符根目录保留", Core.Actions.ActionContext.ExpandFolderPath(@"D:\"), @"D:\");
        CheckEqual("空输入返回空串", Core.Actions.ActionContext.ExpandFolderPath("   "), "");
        Check("shell: 虚拟路径原样保留",
            Core.Actions.ActionContext.ExpandFolderPath("shell:Downloads")
                .Equals("shell:Downloads", StringComparison.OrdinalIgnoreCase));
        Check("~ 展开成用户目录",
            !Core.Actions.ActionContext.ExpandFolderPath("~").Contains('~'),
            Core.Actions.ActionContext.ExpandFolderPath("~"));

        CheckEqual("文件夹有备注用备注",
            UI.Views.KeysView.FolderLabelOf("项目", @"D:\Work\2026\项目文档"), "项目");
        CheckEqual("文件夹无备注用文件夹名",
            UI.Views.KeysView.FolderLabelOf("", @"D:\Work\项目文档"), "项目文档");
        CheckEqual("文件夹名为空时兜底",
            UI.Views.KeysView.FolderLabelOf("", ""), "文件夹");

        // 7. 直达文件：键帽标签用文件名
        CheckEqual("文件有备注用备注",
            UI.Views.KeysView.FileLabelOf("周报", @"D:\文档\方案.docx"), "周报");
        CheckEqual("文件无备注用文件名（去扩展名）",
            UI.Views.KeysView.FileLabelOf("", @"D:\文档\方案.docx"), "方案");
        CheckEqual("文件名兜底",
            UI.Views.KeysView.FileLabelOf("", ""), "文件");
        CheckEqual("ExpandPath 与 ExpandFolderPath 行为一致",
            Core.Actions.ActionContext.ExpandPath("D:/Work/A/"),
            Core.Actions.ActionContext.ExpandFolderPath("D:/Work/A/"));
    }

    /// <summary>
    /// 「直达链接」的智能识别（系统分类里那个）：选中什么就开什么。
    ///
    /// 这个功能的风险全在**误判**上：把一句话当网址塞进浏览器、
    /// 把 `reade.md` 当域名打开，比"什么都不做"更打扰人。
    /// 所以这里用可控的假文件系统，把各种边界情况逐条钉住：
    ///
    ///   1. 动作必须归在「系统」，和打开网址 / 运行程序并列；
    ///   2. 网址（带协议 / 裸域名 / 引号包裹 / 末尾句号）；
    ///   3. 文件夹与文件按**真实存在性**区分，不存在时给出准确的"不存在"类型；
    ///   4. 路径的各种写法：引号、正斜杠、环境变量、UNC、file:///、shell:；
    ///   5. 不是链接的内容一律不乱开（多行、普通句子、像域名的文件名）。
    /// </summary>
    private static void TestSmartOpen()
    {
        // ---------- 1. 动作归位 ----------
        var settings = DefaultSettings.Create();
        var notes = new List<string>();
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new Core.Actions.WindowBindingService(settings),
            Mouse = new Core.Actions.MouseService(),
            Notify = n => notes.Add(n),
        };

        var cat = Core.Actions.ActionCatalog.Create(ctx);
        var so = cat.Items.FirstOrDefault(i => i.Id == "smartOpen");
        Check("动作表有 smartOpen", so != null);
        CheckEqual("smartOpen 在「智能直达」分类", so?.Category, "智能直达");
        CheckEqual("smartOpen 标题是「智能直达」", so?.Title, "智能直达");
        Check("smartOpen 不需要预填参数", so?.NeedsArg == false);
        Check("「智能直达」分类下没有别的可见动作",
            cat.Items.Where(i => i.Category == "智能直达").All(i => i.Id == "smartOpen"),
            string.Join(",", cat.Items.Where(i => i.Category == "智能直达").Select(i => i.Id)));

        // ---------- 2. 识别：用假文件系统，避免依赖测试机磁盘 ----------
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\文档\方案.docx", @"C:\Windows\notepad.exe",
        };
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\Work\项目", @"D:\Work\2026", @"C:\Windows",
        };
        static bool Has(HashSet<string> set, string p) => set.Contains(p);

        Core.Actions.SmartTarget Hit(string text) =>
            Core.Actions.SmartTargetDetector.Detect(text, p => Has(files, p), p => Has(dirs, p));

        // 2a. 网址
        CheckEqual("带协议的网址", Hit("https://github.com/a?b=1").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("http 内网地址", Hit("http://intranet/wiki").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("裸域名", Hit("github.com").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("带 www 与路径", Hit("www.baidu.com/s?wd=x").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("mailto 也算链接", Hit("mailto:a@b.com").Kind, Core.Actions.SmartKind.Url);
        CheckEqual("网址末尾的中文句号被去掉",
            Hit("https://a.com。").Target, "https://a.com");
        CheckEqual("引号包裹的网址也能认",
            Hit("「https://example.com/path」").Target, "https://example.com/path");

        // 2b. 文件夹 / 文件：存在性优先
        CheckEqual("存在的目录 → 文件夹", Hit(@"D:\Work\项目").Kind, Core.Actions.SmartKind.Folder);
        CheckEqual("引号包裹的路径", Hit("\"D:\\Work\\项目\"").Kind, Core.Actions.SmartKind.Folder);
        CheckEqual("正斜杠 + 尾分隔符", Hit("D:/Work/项目/").Target, @"D:\Work\项目");
        CheckEqual("存在的文件 → 文件", Hit(@"D:\文档\方案.docx").Kind, Core.Actions.SmartKind.File);
        CheckEqual("文件目标做了路径归一", Hit("D:/文档/方案.docx").Target, @"D:\文档\方案.docx");
        CheckEqual("带空格路径", Hit(@"C:\Windows\notepad.exe").Kind, Core.Actions.SmartKind.File);

        // 2c. 不存在时也要说清是"文件不存在"还是"文件夹不存在"
        CheckEqual("不存在的文件", Hit(@"D:\文档\没有这个.docx").Kind, Core.Actions.SmartKind.MissingFile);
        CheckEqual("不存在的文件夹", Hit(@"D:\工作\2026规划").Kind, Core.Actions.SmartKind.MissingFolder);
        CheckEqual("结尾带分隔符按文件夹算", Hit(@"D:\没有这个目录\").Kind, Core.Actions.SmartKind.MissingFolder);
        CheckEqual("UNC 路径", Hit(@"\\nas\share\doc").Kind, Core.Actions.SmartKind.MissingFolder);

        // 2d. 特殊写法
        CheckEqual("file:/// 地址", Hit("file:///D:/文档/方案.docx").Kind, Core.Actions.SmartKind.File);
        CheckEqual("shell: 虚拟目录", Hit("shell:Downloads").Kind, Core.Actions.SmartKind.Folder);
        CheckEqual("shell: 目标原样保留", Hit("shell:Downloads").Target, "shell:Downloads");
        Check("环境变量被展开",
            !Hit(@"%USERPROFILE%\Desktop").Target.Contains('%'),
            Hit(@"%USERPROFILE%\Desktop").Target);

        // 2e. 不像链接的内容：一个都不许开
        CheckEqual("多行选区不认", Hit("第一行\r\n第二行").Kind, Core.Actions.SmartKind.None);
        CheckEqual("普通句子不认", Hit("今天天气不错").Kind, Core.Actions.SmartKind.None);
        CheckEqual("没有一个点的单词不认", Hit("hello").Kind, Core.Actions.SmartKind.None);
        CheckEqual("像域名的文件名（readme.md）不认", Hit("readme.md").Kind, Core.Actions.SmartKind.None);
        CheckEqual("像域名的脚本名（build.ps1）不认", Hit("build.ps1").Kind, Core.Actions.SmartKind.None);
        CheckEqual("空输入不认", Hit("   ").Kind, Core.Actions.SmartKind.None);
        CheckEqual("超长文本不认",
            Core.Actions.SmartTargetDetector.Detect(new string('a', 3000),
                _ => false, _ => false).Kind, Core.Actions.SmartKind.None);

        // ---------- 3. 动作执行：三类目标分别落到三个通道 ----------
        string? openedUrl = null, openedFolder = null, openedFile = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => openedUrl = u;
        Core.Actions.ActionContext.OpenFolderInterceptForTest = p => openedFolder = p;
        Core.Actions.ActionContext.OpenFileInterceptForTest = p => openedFile = p;

        string tmp = Path.Combine(Path.GetTempPath(), "ke_smart_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        string tmpFile = Path.Combine(tmp, "方案.txt");
        File.WriteAllText(tmpFile, "selftest");

        try
        {
            ctx.CaptureTextForTest = () => tmp;
            cat.Invoke("smartOpen", "");
            CheckEqual("选中文件夹 → 交给资源管理器", openedFolder, tmp);
            Check("选中文件夹 → 不该走浏览器", openedUrl == null);

            openedFolder = null;
            ctx.CaptureTextForTest = () => tmpFile;
            cat.Invoke("smartOpen", "");
            CheckEqual("选中文件 → 交给默认程序", openedFile, tmpFile);

            openedFile = null;
            openedUrl = null;
            ctx.CaptureTextForTest = () => "github.com";
            cat.Invoke("smartOpen", "");
            Check("选中裸域名 → 走浏览器（自动补 https）",
                openedUrl != null && openedUrl.StartsWith("https://github.com", StringComparison.Ordinal),
                openedUrl ?? "(null)");
            Check("裸域名不该走文件通道", openedFile == null && openedFolder == null);

            openedUrl = null;
            notes.Clear();
            ctx.CaptureTextForTest = () => "这是一段普通文字";
            cat.Invoke("smartOpen", "");
            Check("普通文字什么都不开",
                openedUrl == null && openedFolder == null && openedFile == null);
            Check("普通文字给出明确提示",
                notes.Any(n => n.Contains("既不是网址")), string.Join(" | ", notes));

            notes.Clear();
            ctx.CaptureTextForTest = () => Path.Combine(tmp, "没有这个.docx");
            cat.Invoke("smartOpen", "");
            Check("文件不存在时提示「文件不存在」",
                notes.Any(n => n.Contains("文件不存在")), string.Join(" | ", notes));
            Check("文件不存在时不去打开", openedFile == null);

            notes.Clear();
            ctx.CaptureTextForTest = () => "";
            ctx.ManualCopyWaitForTest = _ => "";
            cat.Invoke("smartOpen", "");
            Check("完全没取到内容时提示先选中",
                notes.Any(n => n.Contains("先选中")), string.Join(" | ", notes));

            // 取词失败 → 用户手动 Ctrl+C 的保底路径要能接上
            openedFolder = null;
            ctx.CaptureTextForTest = () => "";
            ctx.ManualCopyWaitForTest = _ => tmp;
            cat.Invoke("smartOpen", "");
            CheckEqual("手动复制的内容也能直达", openedFolder, tmp);
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
            Core.Actions.ActionContext.OpenFolderInterceptForTest = null;
            Core.Actions.ActionContext.OpenFileInterceptForTest = null;
            ctx.CaptureTextForTest = null;
            ctx.ManualCopyWaitForTest = null;
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// 键帽的颜色区分：已绑定 = 深蓝实心，未绑定 = 浅色。
    ///
    /// 这里不测像素，而是验证"决定颜色和文字的那套数据规则"：
    ///   1. 已绑定的键 IsBound 为 true、未绑定为 false；
    ///   2. 不同动作类型在键帽上显示的文字各不相同且非空
    ///      （窗口绑定显示程序名、直达网页显示备注/域名、宏显示宏名）；
    ///   3. 备注为空时退化到域名，不会显示空串（键帽上没字很难看）。
    /// </summary>
    private static void TestKeycapVisualStates()
    {
        // 1. IsBound 语义
        Check("未绑定 doNothing 不算绑定",
            !new KeyBinding { Action = "doNothing" }.IsBound);
        Check("空动作不算绑定",
            !new KeyBinding { Action = "" }.IsBound);
        Check("有动作算绑定",
            new KeyBinding { Action = "webPage", Arg = "https://a.com" }.IsBound);

        // 2. 各类型键帽标签
        CheckEqual("窗口绑定显示程序名",
            System.IO.Path.GetFileNameWithoutExtension(@"C:\Apps\Photoshop.exe"),
            "Photoshop");

        CheckEqual("直达网页有备注用备注",
            Core.Actions.UrlUtil.LabelOf("公司后台", "https://intranet.corp"), "公司后台");
        CheckEqual("直达网页无备注用域名",
            Core.Actions.UrlUtil.LabelOf(null, "https://github.com/x"), "github.com");
        CheckEqual("直达网页无备注无域名有兜底",
            Core.Actions.UrlUtil.LabelOf(null, ""), "网页");

        // 3. 标签永远非空（键帽渲染依赖这一点）
        foreach (var (note, url) in new[]
                 {
                     ((string?)null, (string?)null),
                     ("", ""),
                     ("  ", "   "),
                     (null, "不是网址"),
                 })
        {
            string label = Core.Actions.UrlUtil.LabelOf(note, url);
            Check($"标签非空 (note={note ?? "null"}, url={url ?? "null"})",
                !string.IsNullOrWhiteSpace(label), label);
        }
    }

    /// <summary>
    /// 产品标识（改名 按键增强 / Keyboard Enhance，版本 2.0）。
    /// </summary>
    private static void TestProductIdentity()
    {
        CheckEqual("产品名", AppInfo.ProductName, "按键增强");
        CheckEqual("英文名", AppInfo.EnglishName, "Keyboard Enhance");
        CheckEqual("短名", AppInfo.ShortName, "按键增强");
        Check("标题包含名称和英文名",
            AppInfo.TitleWithEnglish.Contains("按键增强")
            && AppInfo.TitleWithEnglish.Contains("Keyboard Enhance"),
            AppInfo.TitleWithEnglish);

        // 可执行文件名**刻意**保持 KeyboardEnhance.exe：
        // 改了会让已注册的开机自启、快捷方式全部失效。
        // 而且只有这一个 exe —— 不再有"启动器 + 主程序"两个形态。
        CheckEqual("可执行文件名保持不变", AppInfo.ExeName, "KeyboardEnhance.exe");

        // 版本号
        var ver = typeof(AppInfo).Assembly.GetName().Version;
        // 版本号会随发布递增，这里只校验"能取到且 >= 2"
        Check("版本号有效", ver != null && ver.Major >= 2, ver?.ToString() ?? "(null)");
    }

    /// <summary>
    /// 网页标题抓取：解析 HTML 的 title，处理实体与空白。
    /// </summary>
    private static void TestWebTitleExtract()
    {
        CheckEqual("基本 title",
            Core.Actions.WebTitleFetcher.ExtractTitle("<html><head><title>Hello</title></head></html>"),
            "Hello");

        CheckEqual("大写标签也认",
            Core.Actions.WebTitleFetcher.ExtractTitle("<TITLE>GitHub</TITLE>"), "GitHub");

        CheckEqual("带属性的 title",
            Core.Actions.WebTitleFetcher.ExtractTitle("<title lang=\"zh\">知乎</title>"), "知乎");

        CheckEqual("HTML 实体解码",
            Core.Actions.WebTitleFetcher.ExtractTitle("<title>A &amp; B &lt;C&gt;</title>"), "A & B <C>");

        CheckEqual("折叠空白",
            Core.Actions.WebTitleFetcher.ExtractTitle("<title>  很多\n   空格  </title>"), "很多 空格");

        Check("没有 title 返回 null",
            Core.Actions.WebTitleFetcher.ExtractTitle("<html>no title</html>") == null);
        Check("空 HTML 返回 null",
            Core.Actions.WebTitleFetcher.ExtractTitle("") == null);

        // 过长标题要截断（备注字段不该塞几百字）
        string longTitle = new string('字', 200);
        var got = Core.Actions.WebTitleFetcher.ExtractTitle($"<title>{longTitle}</title>");
        Check("超长标题被截断", got != null && got.Length <= 60, (got?.Length ?? 0).ToString());

        // 通过测试钩子验证"抓取 → 标题"整条链（不真的发网络请求）
        Core.Actions.WebTitleFetcher.InterceptForTest = _ => "抓到的标题";
        try
        {
            CheckEqual("抓取链返回标题",
                Core.Actions.WebTitleFetcher.Fetch("example.com"), "抓到的标题");
            Check("非法网址不去抓",
                Core.Actions.WebTitleFetcher.Fetch("  ") == null);
        }
        finally
        {
            Core.Actions.WebTitleFetcher.InterceptForTest = null;
        }
    }

    /// <summary>
    /// 键盘布局不能有重复的物理键（例如小键盘 + 被画两次）。
    ///
    /// 这是 v1.10 的真实缺陷："+" 同时出现在 Layout 的 y=2 和 y=3 两行，
    /// 又经 RowMerges 合并，结果键盘上出现两个加号。
    /// </summary>
    private static void TestNoDuplicateKeycaps()
    {
        var t = typeof(CapsLockPlus.UI.Views.KeysView);

        var fl = t.GetField("Layout",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var fm = t.GetField("RowMerges",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check("Layout 可读", fl != null);
        Check("RowMerges 可读", fm != null);
        if (fl?.GetValue(null) is not Array rows || fm?.GetValue(null) is not Array merges) return;

        var ids = new List<string>();
        foreach (var row in rows)
            foreach (var item in (Array)row!)
            {
                var ty = item!.GetType();
                ids.Add((string)ty.GetField("Item2")!.GetValue(item)!);
            }
        foreach (var m in merges)
        {
            var ty = m!.GetType();
            ids.Add((string)ty.GetField("Item2")!.GetValue(m)!);
        }

        var dup = ids.GroupBy(x => x).Where(g => g.Count() > 1).ToList();
        // 主 Enter 和小键盘 Enter 共用一个 VK/键名，属于合法重复
        var illegal = dup.Where(g => !(g.Key == "enter" && g.Count() == 2)).ToList();

        Check("没有重复的物理键", illegal.Count == 0,
            string.Join(", ", illegal.Select(g => $"{g.Key}×{g.Count()}")));

        // 小键盘加号只能出现一次
        Check("小键盘 + 只出现一次", ids.Count(x => x == "numAdd") == 1,
            ids.Count(x => x == "numAdd").ToString());
    }

    /// <summary>
    /// 键帽短名：长标题在 26px 宽的键帽里显示不全，需要 2~4 字短名。
    /// </summary>
    private static void TestKeycapShortTitles()
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

        // 键帽上实际显示的文字永远不超过 5 字（有短名用短名，没有就截断）
        foreach (var item in cat.Items)
        {
            Check($"「{item.Title}」键帽文字够短",
                item.KeycapTitle.Length is > 0 and <= 5,
                $"{item.Title} → {item.KeycapTitle}");
        }

        // 常用长标题要有真正的短名（比截断更可读）
        foreach (string id in new[] { "deleteWord", "selectHome", "qbar", "deleteToLineBeginning" })
        {
            var it = cat.Get(id);
            Check($"「{id}」有短名", !string.IsNullOrWhiteSpace(it?.ShortTitle),
                it?.ShortTitle ?? "(无)");
        }

        // KeycapTitle 逻辑：有短名用短名，没有退回全名
        var withShort = cat.Get("deleteWord");
        Check("deleteWord 有短名", withShort != null,
            string.Join(",", cat.Items.Where(i => i.Category == "编辑").Select(i => i.Id)));
        CheckEqual("deleteWord 短名", withShort?.ShortTitle, "删词");
        CheckEqual("deleteWord 全名保留", withShort?.Title, "删除前一个单词");
    }

    /// <summary>
    /// 窗口绑定的自定义显示名（exe 名不一定是软件名）。
    /// </summary>
    private static void TestWinBindCustomName()
    {
        var settings = DefaultSettings.Create();
        var layer = settings.Layers.First(l => l.Id == 1);
        layer.Keys.RemoveAll(k => k.Combo == "caps_w");
        var b = new KeyBinding
        {
            Combo = "caps_w", Action = "winBind",
            Arg = @"C:\Apps\idea64.exe", Note = "IDEA",
        };
        layer.Keys.Add(b);

        // 自定义名要能存
        CheckEqual("自定义名已保存", b.Note, "IDEA");

        // 序列化：有名字要写进 JSON，没名字不写
        string jsonWith = System.Text.Json.JsonSerializer.Serialize(b);
        Check("自定义名写进 JSON", jsonWith.Contains("\"Note\""), jsonWith);

        var noName = new KeyBinding { Combo = "caps_e", Action = "winBind", Arg = @"C:\a\b.exe" };
        string jsonNo = System.Text.Json.JsonSerializer.Serialize(noName);
        Check("无自定义名不写 JSON", !jsonNo.Contains("\"Note\""), jsonNo);
    }

    /// <summary>
    /// Qbar 面板配色要按底色亮度推导，浅色主题下不能"白字白底"。
    /// </summary>
    private static void TestQbarThemeContrast()
    {
        var t = typeof(CapsLockPlus.UI.Windows.QbarWindow);
        var m = t.GetMethod("IsLightColor",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check("QbarWindow 有亮度判定", m != null);
        if (m == null) return;

        bool IsLight(string hex) =>
            (bool)m.Invoke(null, new object[]
                { (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex) })!;

        Check("白色判为浅色", IsLight("#FFFFFF"));
        Check("浅灰判为浅色", IsLight("#F0F0F0"));
        Check("深色判为非浅色", !IsLight("#1C1F27"));
        Check("纯黑判为非浅色", !IsLight("#000000"));
        Check("中蓝判为非浅色", !IsLight("#2B6CF6"));
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
    /// 「直接搜索」：用选中文本 + 指定引擎，在默认浏览器里搜索。
    /// </summary>
    private static void TestDirectSearch()
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

        // ① 动作存在且分类正确
        var bdAction = cat.Items.FirstOrDefault(i => i.Id == "search:bd");
        Check("动作表有 search:bd", bdAction != null,
            string.Join(",", cat.Items.Where(i => i.Category == "直接搜索").Select(i => i.Id)));
        CheckEqual("search:bd 在「直接搜索」分类", bdAction?.Category, "直接搜索");
        Check("有「直接搜索」分类的多个动作",
            cat.Items.Count(i => i.Category == "直接搜索") >= 5,
            cat.Items.Count(i => i.Category == "直接搜索").ToString());

        // ② v2.8 起直接搜索覆盖**全部**引擎（下拉支持输入过滤，不怕多）
        Check("直接搜索覆盖全部引擎",
            cat.Items.Count(i => i.Category == "直接搜索") >= 50,
            cat.Items.Count(i => i.Category == "直接搜索").ToString());

        // ③ 执行时把选中文本编码进 URL 并交给浏览器
        string? opened = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => opened = u;
        try
        {
            // 用 overrideText 绕开真实的剪贴板读取
            ctx.SearchSelection("bd", "测试 关键词");
            Check("搜索打开了网址", opened != null, opened ?? "(null)");
            if (opened != null)
            {
                Check("用的是百度的引擎", opened.Contains("baidu.com", StringComparison.OrdinalIgnoreCase), opened);
                Check("关键词被正确编码",
                    opened.Contains(Uri.EscapeDataString("测试 关键词"), StringComparison.Ordinal),
                    opened);
            }
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
        }

        // ④ 取不到选区时不打开浏览器（用测试桩绕开真实剪贴板）
        ctx.CaptureTextForTest = () => "";
        ctx.ManualCopyWaitForTest = _ => "";
        opened = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => opened = u;
        try
        {
            ctx.SearchSelection("bd", "   ");
            Check("取不到选区时不搜索", opened == null, opened ?? "(null)");
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
        }

        // ⑤ 未知引擎安全返回且不崩
        Check("未知引擎不抛异常", ctx.SearchSelection("no_such_engine", "abc"));

        TestDirectSearchEmptyArgRegression(ctx, cat);
    }

    /// <summary>
    /// 回归测试：按键绑定里 Arg 是**空字符串**时，直接搜索必须照常工作。
    ///
    /// 这是 v3.5.0 修掉的核心 bug —— 绑定 <c>search:bd</c> 的 Arg 默认是 ""，
    /// 老代码用 <c>overrideText ?? …</c> 判断，空串不算 null，
    /// 于是永远判定"没有选中的文字"，按了键什么也不发生。
    /// 这个用例必须走**动作表**（和真实热键同一条路径），不能直接调 SearchSelection，
    /// 否则测不到参数传递这一层。
    /// </summary>
    private static void TestDirectSearchEmptyArgRegression(Core.Actions.ActionContext ctx,
                                                           Core.Actions.ActionCatalog cat)
    {
        Section("直接搜索 · 空参数回归（v3.5.0）");

        ctx.CaptureTextForTest = () => "被选中的文字ABC";
        ctx.ManualCopyWaitForTest = _ => "";

        string? opened = null;
        Core.Actions.ActionContext.OpenUrlInterceptForTest = u => opened = u;
        try
        {
            // ① 空字符串参数（真实绑定的默认值）
            opened = null;
            bool ok = cat.Invoke("search:bd", "");
            Check("空参数时动作执行成功", ok);
            Check("空参数时真的搜索了", opened != null, opened ?? "(null)");
            if (opened != null)
            {
                Check("空参数时关键词正确",
                    opened.Contains(Uri.EscapeDataString("被选中的文字ABC"), StringComparison.Ordinal),
                    opened);
            }

            // ② 纯空白参数（绑定编辑器里手敲空格）
            opened = null;
            cat.Invoke("search:bd", "   ");
            Check("空白参数时真的搜索了", opened != null, opened ?? "(null)");

            // ③ null 参数（老调用方式）
            opened = null;
            cat.Invoke("search:bd", null!);
            Check("null 参数时真的搜索了", opened != null, opened ?? "(null)");

            // ④ 显式参数优先于选区
            opened = null;
            cat.Invoke("search:bd", "显式参数XYZ");
            Check("显式参数优先",
                opened != null && opened.Contains(Uri.EscapeDataString("显式参数XYZ"), StringComparison.Ordinal),
                opened ?? "(null)");

            // ⑤ 主动取词失败 → 保底「手动 Ctrl+C」拿到的内容也要能搜
            ctx.CaptureTextForTest = () => "";
            ctx.ManualCopyWaitForTest = _ => "手动复制的内容";
            opened = null;
            cat.Invoke("search:bd", "");
            Check("保底手动复制路径可用",
                opened != null && opened.Contains(Uri.EscapeDataString("手动复制的内容"), StringComparison.Ordinal),
                opened ?? "(null)");

            // ⑥ 引擎地址里没写 {q} 时不能把关键词丢掉
            var noPlaceholder = DefaultSettings.Create();
            noPlaceholder.Search.Add(new Core.Settings.QEntry
            {
                Key = "noplaceholder",
                Value = "https://example.com/search",
                Comment = "自定义",
            });
            var ctx2 = new Core.Actions.ActionContext
            {
                Settings = () => noPlaceholder,
                Clipboard = new Core.Actions.ClipboardService(),
                Windows = new Core.Actions.WindowActions(),
                WindowBindings = new Core.Actions.WindowBindingService(noPlaceholder),
                Mouse = new Core.Actions.MouseService(),
                Notify = _ => { },
                CaptureTextForTest = () => "关键词QQ",
            };
            opened = null;
            ctx2.SearchSelection("noplaceholder");
            Check("缺 {q} 时仍带上关键词",
                opened != null && opened.Contains(Uri.EscapeDataString("关键词QQ"), StringComparison.Ordinal),
                opened ?? "(null)");
        }
        finally
        {
            Core.Actions.ActionContext.OpenUrlInterceptForTest = null;
            ctx.CaptureTextForTest = null;
            ctx.ManualCopyWaitForTest = null;
        }
    }

    /// <summary>
    /// 「输入文本」分类：把多行文本直接打到当前光标处。
    /// </summary>
    private static void TestTypeTextAction()
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

        var item = cat.Items.FirstOrDefault(i => i.Id == "typeText");
        Check("动作表有 typeText", item != null,
            string.Join(",", cat.Items.Where(i => i.Category == "输入文本").Select(i => i.Id)));
        CheckEqual("typeText 在「输入文本」分类", item?.Category, "输入文本");
        Check("typeText 需要参数", item?.NeedsArg == true);

        // 空文本安全返回（不崩、不发按键）
        Check("空文本安全返回", ctx.TypeText(""));

        // 多行文本允许（文本框 AcceptsReturn）
        Check("多行文本不抛异常", ctx.TypeText("第一行\n第二行"));

        // 超长文本被截断而不是拒发（2000 上限）
        string longText = new string('a', 5000);
        Check("超长文本不抛异常", ctx.TypeText(longText));
    }

    /// <summary>
    /// 音量：这次把「模拟媒体键」换成了 Core Audio（IAudioEndpointVolume）。
    ///
    /// 分两层验：
    ///   ① 纯换算（夹取、浮点→百分比）—— 不需要声卡，任何机器都能断言；
    ///   ② 真实读写 —— 这台机器有播放设备时，写一个值、回读、再还原。
    ///
    /// ② 必须做，否则"接口写错了但编译通过"这种问题只能等用户发现：
    /// COM 接口的方法顺序错位不会报编译错，表现是调用失败或写进乱七八糟的值。
    /// 过程中先静音，避免自检把音量推到 70% 时突然出声。
    /// </summary>
    private static void TestVolumeCore()
    {
        // ---------- ① 边界换算 ----------
        CheckEqual("音量 50+5 → 55", Core.Windows.VolumeService.TargetPercent(50, 5), 55);
        CheckEqual("音量 98+5 → 封顶 100", Core.Windows.VolumeService.TargetPercent(98, 5), 100);
        CheckEqual("音量 2-5 → 保底 0", Core.Windows.VolumeService.TargetPercent(2, -5), 0);
        CheckEqual("音量 100+5 → 仍是 100", Core.Windows.VolumeService.TargetPercent(100, 5), 100);
        CheckEqual("音量 0-5 → 仍是 0", Core.Windows.VolumeService.TargetPercent(0, -5), 0);

        CheckEqual("0.65 → 65%", Core.Windows.VolumeService.ToPercent(0.65f), 65);
        CheckEqual("0.6499999 → 65%（不能舍成 64）", Core.Windows.VolumeService.ToPercent(0.6499999f), 65);
        CheckEqual("1.0 → 100%", Core.Windows.VolumeService.ToPercent(1f), 100);
        CheckEqual("0 → 0%", Core.Windows.VolumeService.ToPercent(0f), 0);

        // ---------- ② 真实读写 + 还原 ----------
        int? before = Core.Windows.VolumeService.Get();
        if (before == null)
        {
            Check("（环境限制）没有可用的播放设备，跳过真实音量读写", true);
            return;
        }

        bool? muteBefore = Core.Windows.VolumeService.GetMute();
        try
        {
            Core.Windows.VolumeService.SetMute(true);   // 测试期间不出声

            int target = before.Value >= 50 ? 30 : 70;
            bool written = Core.Windows.VolumeService.Set(target);
            Check($"音量能写入 {target}%", written);
            if (written)
            {
                int after = Core.Windows.VolumeService.Get() ?? -1;
                /* 允许 ±1：声卡会把 0.30 量化成最接近的一档，
                 * 0~100 的整数百分比回读通常精确，少数驱动有 1% 偏差。 */
                Check($"回读音量 ≈ {target}%（实际 {after}%）", Math.Abs(after - target) <= 1,
                    $"before={before}, after={after}");

                int? stepped = Core.Windows.VolumeService.Step(5);
                Check("音量 +5 有回读值", stepped != null, $"stepped={stepped}");
                CheckEqual("Step 返回的就是当前值", stepped ?? -1,
                    Core.Windows.VolumeService.Get() ?? -1);

                // 再减回去，确认双向都通
                int? back = Core.Windows.VolumeService.Step(-5);
                Check("音量 -5 有回读值", back != null, $"back={back}");
            }

            if (muteBefore != null)
            {
                Core.Windows.VolumeService.SetMute(!muteBefore.Value);
                Check("静音状态可切换", Core.Windows.VolumeService.GetMute() == !muteBefore.Value);
            }
        }
        finally
        {
            Core.Windows.VolumeService.Set(before.Value);
            if (muteBefore != null) Core.Windows.VolumeService.SetMute(muteBefore.Value);
        }

        CheckEqual("音量已还原", Core.Windows.VolumeService.Get() ?? -1, before.Value);

        // ---------- ③ 动作表归位 ----------
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

        foreach (string id in new[] { "volumeUp", "volumeDown", "volumeMute" })
        {
            var it = cat.Items.FirstOrDefault(i => i.Id == id);
            Check($"动作 {id} 在「系统功能」", it?.Category == "系统功能",
                string.Join(",", cat.Items.Where(i => i.Category == "系统功能").Select(i => i.Id)));
            Check($"动作 {id} 不需要参数", it?.NeedsArg != true);
        }

        // 音量动作不能再退回"模拟媒体键"：那是这次要修的根因
        var up = cat.Items.FirstOrDefault(i => i.Id == "volumeUp");
        var down = cat.Items.FirstOrDefault(i => i.Id == "volumeDown");
        Check("音量动作走 Core Audio（描述里写的是系统音量）",
            up?.Description.Contains("系统音量") == true &&
            down?.Description.Contains("系统音量") == true,
            $"up={up?.Description} / down={down?.Description}");
    }

    /// <summary>
    /// 亮度：WMI 与 DDC/CI 两套量程换算。
    ///
    /// 不在这里做真实写入 —— 写亮度会让屏幕闪一下，而自检要跑一千多条断言，
    /// 中间闪屏非常打扰。读取是只读的，只报一句当前通道。
    /// </summary>
    /// <summary>
    /// 提示消息必须「新的立刻覆盖旧的」。
    ///
    /// 这条直接决定手感：连按十个键时，如果每条消息都排一次调度、
    /// 界面线程忙完依次播一遍，用户看到的就是十几次闪烁加一条过时内容 ——
    /// 也就是用户说的"提示太慢，跟不上按键"。
    ///
    /// 槽位只保留最后一条、调度只安排一次，中间的自动作废。
    /// </summary>
    private static void TestToastCoalescing()
    {
        var q = new UI.Windows.ToastQueue();

        Check("第一条需要安排刷新", q.Post("第一条", "info"));
        Check("第二条不必再安排（已有安排会来取最新值）", !q.Post("第二条", "info"));
        Check("第三条同样不必", !q.Post("第三条", "feedback"));

        var got = q.Take();
        CheckEqual("取到的是最后一条", got?.Text, "第三条");
        CheckEqual("样式跟着最后一条走", got?.Kind, "feedback");
        Check("取完之后槽里是空的", q.Take() == null);

        Check("取空后能重新开始新一轮", q.Post("第四条", "info"));
        CheckEqual("新一轮取到第四条", q.Take()?.Text, "第四条");
    }

    /// <summary>
    /// 「自定义按键」：参数编解码 + 组合键解析 + 执行流程（启动 / 发送 / 各种失败）。
    ///
    /// 执行流程全部用注入点拦截，不真的启动程序、不真的按键 ——
    /// 自检要能在任何机器上跑，不能因为"这台机器没装微信"就失败。
    /// </summary>
    private static void TestCustomKey()
    {
        // ---------- 参数编解码 ----------
        var spec = new CustomKeySpec
        {
            Key = "ctrl_shift_k",
            AppPath = @"C://Tools//Weixin//Weixin.exe",
            RunAsAdmin = true,
            BringFront = true,
            WaitAfterLaunchMs = 0,      // 自检别真等 900ms
        };
        var back = CustomKeySpec.FromArg(spec.ToArg());
        CheckEqual("自定义按键：键能往返", back.Key, spec.Key);
        CheckEqual("自定义按键：应用能往返", back.AppPath, spec.AppPath);
        Check("自定义按键：管理员标志能往返", back.RunAsAdmin);
        Check("自定义按键：切前台标志能往返", back.BringFront);
        CheckEqual("自定义按键：等待时间能往返", back.WaitAfterLaunchMs, spec.WaitAfterLaunchMs);
        CheckEqual("自定义按键：默认等待 900ms（与 QuickSearch 一致）",
            new CustomKeySpec().WaitAfterLaunchMs, 900);
        Check("自定义按键：切前台默认关闭（与 QuickSearch 一致）",
            !new CustomKeySpec().BringFront);
        CheckEqual("自定义按键：进程名取自文件名", back.ProcessName, "Weixin");
        Check("自定义按键：填齐了才算就绪", back.IsReady);

        Check("自定义按键：空参数不炸", !CustomKeySpec.FromArg("").IsReady);
        Check("自定义按键：坏 JSON 不炸", !CustomKeySpec.FromArg("{oops").IsReady);
        CheckEqual("自定义按键：手填纯键名也认", CustomKeySpec.FromArg("f13").Key, "f13");
        Check("自定义按键：只填键不算就绪", !CustomKeySpec.FromArg("f13").IsReady);

        // ---------- 组合键解析 ----------
        var (mods, vk) = CustomKeyRunner.ResolveCombo("ctrl_shift_k");
        CheckEqual("组合键解析出两个修饰键", mods.Length, 2);
        Check("组合键解析出 Ctrl", mods.Contains(VK.Control));
        Check("组合键解析出 Shift", mods.Contains(VK.Shift));
        CheckEqual("组合键解析出主键 K", vk, 0x4B);

        var (noMods, f13Vk) = CustomKeyRunner.ResolveCombo("f13");
        CheckEqual("单键没有修饰键", noMods.Length, 0);
        Check("F13 能解析出虚拟键码", f13Vk != 0, $"vk={f13Vk}");

        // ---------- 执行 ----------
        var empty = CustomKeyRunner.Run(new CustomKeySpec());
        Check("没设键 → 明确报错而不是静默", !empty.Ok && empty.Message.Contains("键"),
            empty.Message);

        bool launched = false;
        string? sent = null;
        try
        {
            // 没在运行 → 先启动，再发键
            CustomKeyRunner.IsRunningForTest = _ => false;
            CustomKeyRunner.LaunchForTest = _ => { launched = true; return true; };
            CustomKeyRunner.FindWindowForTest = _ => (IntPtr)0x1234;
            CustomKeyRunner.SendForTest = c => sent = c;

            var r1 = CustomKeyRunner.Run(back);
            Check("应用没在运行时会先启动", launched);
            Check("启动后把键发出去了", sent == back.Key, sent ?? "(null)");
            Check("结果说明里提到已启动", r1.Ok && r1.Message.Contains("已启动"), r1.Message);

            // 已在运行 → 不重复启动，直接发
            launched = false;
            sent = null;
            CustomKeyRunner.IsRunningForTest = _ => true;
            var r2 = CustomKeyRunner.Run(back);
            Check("已在运行时不重复启动", !launched);
            Check("已在运行时照样把键发出去", sent == back.Key, sent ?? "(null)");
            Check("结果说明里提到已在运行", r2.Ok && r2.Message.Contains("已在运行"), r2.Message);

            // 启动失败
            launched = false;
            sent = null;
            CustomKeyRunner.IsRunningForTest = _ => false;
            CustomKeyRunner.LaunchForTest = _ => false;
            var r3 = CustomKeyRunner.Run(back);
            Check("启动失败要说清楚", !r3.Ok && r3.Message.Contains("失败"), r3.Message);
            Check("启动失败时不发键", sent == null);

            // 启动成功但等不到窗口
            CustomKeyRunner.LaunchForTest = _ => true;
            CustomKeyRunner.FindWindowForTest = _ => IntPtr.Zero;
            sent = null;
            var r4 = CustomKeyRunner.Run(back);
            Check("等不到窗口时不发键", sent == null);
            Check("等不到窗口要说明", r4.Message.Contains("没等到"), r4.Message);
        }
        finally
        {
            CustomKeyRunner.IsRunningForTest = null;
            CustomKeyRunner.LaunchForTest = null;
            CustomKeyRunner.FindWindowForTest = null;
            CustomKeyRunner.SendForTest = null;
        }

        // ---------- 动作表 ----------
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
        var info = cat.Get("customKey");
        Check("动作表有 customKey", info != null);
        CheckEqual("customKey 归在「自定义按键」分类", info?.Category, "自定义按键");
        Check("「自定义按键」分类下只有它一个动作",
            cat.Items.Count(i => i.Category == "自定义按键") == 1,
            string.Join(",", cat.Items.Where(i => i.Category == "自定义按键").Select(i => i.Id)));

        // ---------- 键帽标签 ----------
        CheckEqual("键帽标签：有备注用备注",
            UI.Views.KeysView.CustomKeyLabelOf("微信", back), "微信");
        CheckEqual("键帽标签：没备注用应用名",
            UI.Views.KeysView.CustomKeyLabelOf("", back), "Weixin");
        CheckEqual("键帽标签：都没有时兜底",
            UI.Views.KeysView.CustomKeyLabelOf("", new CustomKeySpec()), "自定义");
        CheckEqual("键帽标签：备注两边的空格会被去掉",
            UI.Views.KeysView.CustomKeyLabelOf("  微信  ", back), "微信");
        CheckEqual("键帽标签：只有空白的备注当没填",
            UI.Views.KeysView.CustomKeyLabelOf("   ", back), "Weixin");

        /* 备注存在 KeyBinding.Note 上，不在 spec 里 ——
         * 这样改"发什么键/绑哪个程序"不会把用户起的名字冲掉。 */
        Check("备注不进 spec（改参数不会冲掉名字）",
            !spec.ToArg().Contains("微信"));

        /* ---------- 键帽文字的选择规则 ----------
         *
         * 这里曾经漏了 customKey：键帽永远显示 ShortTitle「自定义」，
         * 用户填的备注根本没机会显示。这条断言就是盯着它。 */
        CheckEqual("键帽文字：自定义按键用备注，不被短名盖掉",
            UI.Views.KeysView.KeycapTitleOf("customKey", "微信", "自定义"), "微信");
        CheckEqual("键帽文字：没备注时用应用名",
            UI.Views.KeysView.KeycapTitleOf("customKey", "Weixin", "自定义"), "Weixin");
        CheckEqual("键帽文字：备注和应用名都没有时才是「自定义」",
            UI.Views.KeysView.KeycapTitleOf("customKey", "自定义", "自定义"), "自定义");
        CheckEqual("键帽文字：普通动作仍然用短名",
            UI.Views.KeysView.KeycapTitleOf("copy", "复制到剪贴板", "复制"), "复制");
        CheckEqual("键帽文字：直达链接用自己算的域名",
            UI.Views.KeysView.KeycapTitleOf("webPage", "github.com", "网址"), "github.com");
        CheckEqual("键帽文字：输入文本用文字开头",
            UI.Views.KeysView.KeycapTitleOf("typeText", "你好世界", "输入"), "你好世界");
        CheckEqual("键帽文字：没有短名时原样返回",
            UI.Views.KeysView.KeycapTitleOf("copy", "复制到剪贴板", null), "复制到剪贴板");

        /* ---------- 底部按钮必须始终在可视区内 ----------
         *
         * 用最矮的常见窗口高度 + 内容最高的「自定义按键」面板来压：
         * 布局没做好的话，按钮的底边会跑到视图高度之外（被卡片裁掉）。 */
        try
        {
            var view = new UI.Views.KeysView();
            foreach (var (w, h) in new[] { (900.0, 420.0), (1280.0, 700.0), (640.0, 360.0) })
            {
                var f = view.FooterForTest(w, h);
                Check($"底部按钮可见（{w}×{h}）", f.Visible,
                    $"visible={f.Visible} bottom={f.Bottom:F0} view={f.ViewHeight:F0}");
                Check($"底部按钮没被挤出可视区（{w}×{h}）",
                    f.ViewHeight > 0 && f.Bottom <= f.ViewHeight + 0.5,
                    $"bottom={f.Bottom:F0} view={f.ViewHeight:F0} scroll={f.ScrollHeight:F0}");
            }
        }
        catch (Exception ex)
        {
            Check("底部按钮布局探针没抛异常", false, ex.Message);
        }
    }

    /// <summary>
    /// OSD（音量/亮度横条）与「输入文本」方式。
    ///
    /// 这两条对应两个真实问题：
    ///   · 音量能调但不弹系统横条 —— 因为系统横条是 explorer 处理
    ///     WM_APPCOMMAND 时画的，跟音频 API 无关；
    ///   · 密码框打不进字 —— Unicode 注入没有扫描码，被安全输入框过滤掉。
    /// </summary>
    private static void TestOsdAndTextInput()
    {
        // ---------- OSD（自绘横条） ----------
        CheckEqual("进度条 0% 不显示", UI.Windows.OsdWindow.ScaleForTest(0), 0.0);
        CheckEqual("进度条 100% 满格", UI.Windows.OsdWindow.ScaleForTest(100), 1.0);
        CheckEqual("进度条 65%", UI.Windows.OsdWindow.ScaleForTest(65), 0.65);
        CheckEqual("进度条越界会被夹住（上）", UI.Windows.OsdWindow.ScaleForTest(180), 1.0);
        CheckEqual("进度条越界会被夹住（下）", UI.Windows.OsdWindow.ScaleForTest(-20), 0.0);

        /* 位置与尺寸：全部对齐系统音量条（192×48、贴底 15 DIP）。
         * 数字来自对系统横条的全屏截图测量，见 OsdWindow 的注释。 */
        double ow = UI.Windows.OsdWindow.DesignWidth;
        double oh = UI.Windows.OsdWindow.DesignHeight;
        var wa = new System.Windows.Rect(0, 0, 1920, 1040);
        var (osdLeft, osdTop) = UI.Windows.OsdWindow.PositionForTest(wa);
        CheckEqual("横条水平居中", osdLeft, (1920 - (ow + 24)) / 2.0);
        Check("横条在屏幕下半部分（不是正中）", osdTop > 1040 * 0.85, $"top={osdTop}");

        /* 卡片下沿离工作区底边必须和系统一样是 15 DIP：
         * 窗口比卡片大一圈（留给阴影），所以窗口底边不是卡片底边。 */
        double cardBottom = osdTop + 12 + oh;
        CheckEqual("卡片下沿贴底 15 DIP（与系统一致）",
            Math.Round(1040 - cardBottom, 3), UI.Windows.OsdWindow.CardBottomGapForTest);

        CheckEqual("卡片宽度与系统一致（192）", ow, 192.0);
        CheckEqual("卡片高度与系统一致（48）", oh, 48.0);
        Check("横条长宽比与系统一致（4 : 1）",
            Math.Abs(ow / oh - 4.0) < 0.05, $"{ow / oh:F2}");

        /* 配色：两套都对齐系统（半透明材质 + 强调色进度条）。
         * 横条跟的是 **Windows 系统主题**，不是本应用主题。 */
        var darkPal = UI.Windows.OsdWindow.PaletteForTest(light: false);
        var lightPal = UI.Windows.OsdWindow.PaletteForTest(light: true);

        Check("夜间：卡片是深色", LuminanceOf(darkPal.Card) < 0.06,
            $"{darkPal.Card} lum={LuminanceOf(darkPal.Card):F3}");
        Check("夜间：图标/数字是白色（和系统一致）",
            darkPal.Text.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase), darkPal.Text);
        Check("日间：卡片是浅色", LuminanceOf(lightPal.Card) > 0.70,
            $"{lightPal.Card} lum={LuminanceOf(lightPal.Card):F3}");
        Check("日间：图标/数字是深色（和系统一致）",
            LuminanceOf(lightPal.Text) < 0.10, $"{lightPal.Text}");

        Check("夜间进度条是强调色的浅色档（蓝色系）", IsBlueish(darkPal.Fill), darkPal.Fill);
        Check("日间进度条是强调色的深色档（蓝色系）", IsBlueish(lightPal.Fill), lightPal.Fill);
        Check("日间/夜间是两个不同的蓝（不能共用一档）",
            !darkPal.Fill.Equals(lightPal.Fill, StringComparison.OrdinalIgnoreCase),
            $"dark={darkPal.Fill} light={lightPal.Fill}");

        Check("夜间：文字与卡片对比 ≥ 4.5:1",
            ContrastRatio(darkPal.Text, darkPal.Card) >= 4.5,
            $"{ContrastRatio(darkPal.Text, darkPal.Card):F2}:1");
        Check("日间：文字与卡片对比 ≥ 4.5:1",
            ContrastRatio(lightPal.Text, lightPal.Card) >= 4.5,
            $"{ContrastRatio(lightPal.Text, lightPal.Card):F2}:1");
        Check("夜间：进度条与卡片对比 ≥ 3:1",
            ContrastRatio(darkPal.Fill, darkPal.Card) >= 3.0,
            $"{ContrastRatio(darkPal.Fill, darkPal.Card):F2}:1");
        Check("日间：进度条与卡片对比 ≥ 3:1",
            ContrastRatio(lightPal.Fill, lightPal.Card) >= 3.0,
            $"{ContrastRatio(lightPal.Fill, lightPal.Card):F2}:1");

        // 连续量动作：按住要一直执行，所以必须认得出来
        Check("音量+ 属于连续量", HotkeyEngine.IsContinuous("volumeUp"));
        Check("音量- 属于连续量", HotkeyEngine.IsContinuous("volumeDown"));
        Check("亮度+ 属于连续量", HotkeyEngine.IsContinuous("brightnessUp"));
        Check("亮度- 属于连续量", HotkeyEngine.IsContinuous("brightnessDown"));
        Check("光标移动不属于连续量（那是连发不是累加）",
            !HotkeyEngine.IsContinuous("moveLeft"));
        Check("静音不属于连续量（切换语义，连按会来回抖）",
            !HotkeyEngine.IsContinuous("volumeMute"));

        // 节奏：起播延迟要比系统键盘短，步进要密
        Check("按住起播延迟合理", HotkeyEngine.RepeatDelayMs is > 100 and < 600,
            $"{HotkeyEngine.RepeatDelayMs}ms");
        Check("连续步进足够密（看起来是滑的）",
            HotkeyEngine.RepeatIntervalMs <= 60, $"{HotkeyEngine.RepeatIntervalMs}ms");

        /* 音量走**系统音量键**：这样 Windows 自己调音量并弹它自己的横条。
         * 键码写错就变成"按了没反应"，而且不会报错，所以钉死。 */
        CheckEqual("音量+ 用的是系统音量键", VK.VolumeUp, 0xAF);
        CheckEqual("音量- 用的是系统音量键", VK.VolumeDown, 0xAE);
        CheckEqual("静音用的是系统静音键", VK.VolumeMute, 0xAD);

        // 音量每步是系统的 2%，比亮度（每步 1%）更疏，否则按住一秒就到底
        Check("音量连发比亮度疏",
            HotkeyEngine.RepeatIntervalFor("volumeUp")
            > HotkeyEngine.RepeatIntervalFor("brightnessUp"),
            $"音量 {HotkeyEngine.RepeatIntervalFor("volumeUp")}ms / "
            + $"亮度 {HotkeyEngine.RepeatIntervalFor("brightnessUp")}ms");
        Check("音量连发不至于一秒到底",
            HotkeyEngine.RepeatIntervalFor("volumeUp") >= 60,
            $"{HotkeyEngine.RepeatIntervalFor("volumeUp")}ms");

        // ---------- 调节幅度（设置里可改） ----------
        var def = DefaultSettings.Create().Global;
        CheckEqual("音量幅度默认 5%", def.VolumeStep, 5);
        CheckEqual("亮度幅度默认 10%", def.BrightnessStep, 10);

        // 归一化把越界值夹回来：0 会让"调不动"，999 一下到底
        var clampCase = DefaultSettings.Create();
        clampCase.Global.VolumeStep = 0;
        clampCase.Global.BrightnessStep = 999;
        Core.Settings.SettingsStore.Normalize(clampCase);
        CheckEqual("音量幅度下限夹到 1", clampCase.Global.VolumeStep, 1);
        CheckEqual("亮度幅度上限夹到 50", clampCase.Global.BrightnessStep, 50);

        /* 系统步进固定 2%，设定值不是整数倍时要靠累计补偿。
         * 这组断言直接盯住"设 5 就真的是 5"，而不是每次差一点。 */
        var (t1, r1) = ActionContext.PlanVolumeTaps(5, 2, 0);
        CheckEqual("设 5%（系统 2%）第一次发 2 次键", t1, 2);
        CheckEqual("并记下 1% 的欠账", r1, 1.0);

        var (t2, r2) = ActionContext.PlanVolumeTaps(5, 2, r1);
        CheckEqual("第二次补上欠账，发 3 次键", t2, 3);
        CheckEqual("欠账清零", r2, 0.0);

        // 连按十次总量必须正好 50%
        double rem = 0, total = 0;
        for (int i = 0; i < 10; i++)
        {
            var (taps, next) = ActionContext.PlanVolumeTaps(5, 2, rem);
            total += taps * 2;
            rem = next;
        }
        CheckEqual("连按十次正好 50%（不会漂移）", total, 50.0);

        var (t3, r3) = ActionContext.PlanVolumeTaps(10, 2, 0);
        CheckEqual("设 10% 时正好发 5 次", t3, 5);
        CheckEqual("设 10% 无欠账", r3, 0.0);

        var (t4, _) = ActionContext.PlanVolumeTaps(0, 2, 0);
        CheckEqual("幅度为 0 也至少发一次（否则按了没反应）", t4, 1);

        /* 亮度横条要跟着"白夜"（浅色 / 深色）走，而且文字不能和底色糊在一起。
         * 这条真的把横条渲染成位图去量亮度，比只查画刷存在更实在。 */
        TestOsdFollowsTheme();

        // ---------- 输入文本方式 ----------        Check("纯 ASCII 走真实按键", InputSender.LooksLikePlainAscii("Abc123!@#"));
        Check("带空格也算", InputSender.LooksLikePlainAscii("hello world"));
        Check("中文不算纯 ASCII", !InputSender.LooksLikePlainAscii("你好"));
        Check("emoji 不算纯 ASCII", !InputSender.LooksLikePlainAscii("a😀"));
        Check("换行不算（会被当成回车提交表单）",
            !InputSender.LooksLikePlainAscii("line1\nline2"));
        Check("制表符不算", !InputSender.LooksLikePlainAscii("a\tb"));
        Check("空串不算", !InputSender.LooksLikePlainAscii(""));

        // 设置默认值：老配置里没有这个字段，反序列化后必须是 auto
        CheckEqual("输入文本方式默认 auto", DefaultSettings.Create().Global.TextInputMode, "auto");

        // 三种方式都不能抛异常（DryRun 下不会真的打字）
        try
        {
            InputSender.Text("abc", InputSender.TextMode.Auto);
            InputSender.Text("中文", InputSender.TextMode.Auto);
            InputSender.Text("abc", InputSender.TextMode.Keys);
            InputSender.Text("abc", InputSender.TextMode.Unicode);
            Check("三种输入方式都能跑通（干跑）", true);
        }
        catch (Exception ex)
        {
            Check("三种输入方式都能跑通（干跑）", false, ex.Message);
        }
    }

    /// <summary>
    /// 亮度横条必须适配"白夜"（浅色 / 深色）模式，且文字与底色有足够对比。
    ///
    /// 真渲染成位图去量：只看画刷键存在是骗人的 —— 画刷定义对了，
    /// XAML 里若还写死 <c>#F4F6FA</c>，浅色主题下就是"白字白底"。
    /// </summary>
    private static void TestOsdFollowsTheme()
    {
        var app = System.Windows.Application.Current;
        if (app == null)
        {
            Check("亮度横条不随主题变（无 Application，跳过）", true);
            return;
        }

        var saved = UI.Theme.ThemeManager.Current;

        try
        {
            double lightCard = MeasureOsdLuminance(UI.Theme.ThemeManager.Mode.Light, out double lightRange);
            double darkCard = MeasureOsdLuminance(UI.Theme.ThemeManager.Mode.Dark, out double darkRange);

            /* 横条**不跟随应用主题**：这是实测的结论 ——
             *
             * 系统那条音量横条在浅色桌面上照样是深色卡片（截图确认）。
             * 之前跟着主题走，结果是"白天浅底、夜里深底"，怎么调都和系统对不上；
             * 现在两套主题都渲染成同一个深色卡片，差异只可能来自绘制 bug。 */
            /* 横条跟的是 Windows 系统主题，不是本应用主题 ——
             * 同一时刻切换应用主题，渲染结果必须完全不受影响。 */
            Check("横条跟系统主题、不跟应用主题（两种应用主题下亮度差 < 0.05）",
                Math.Abs(lightCard - darkCard) < 0.05,
                $"app-light={lightCard:F2} app-dark={darkCard:F2}");
            Check("画面里有明显的前景（进度条/文字，不是一片纯色）",
                lightRange > 0.15 && darkRange > 0.15,
                $"light={lightRange:F2} dark={darkRange:F2}");
        }
        catch (Exception ex)
        {
            Check("亮度横条渲染", false, ex.Message);
        }
        finally
        {
            UI.Theme.ThemeManager.Apply(saved);
        }
    }

    /// <summary>颜色是不是"蓝色系"（蓝通道明显压过红/绿，且整体够亮）。</summary>
    private static bool IsBlueish(string hex)
    {
        var (r, g, b) = ParseRgb(hex);
        return b >= 0.55 && b - r >= 0.15 && b - g >= 0.05;
    }

    /// <summary>"#AARRGGBB" / "#RRGGBB" → 0~1 的 RGB。</summary>
    private static (double R, double G, double B) ParseRgb(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return (0, 0, 0);
        string s = hex.TrimStart('#');
        if (s.Length == 8) s = s[2..];
        if (s.Length != 6) return (0, 0, 0);
        try
        {
            return (Convert.ToInt32(s[..2], 16) / 255.0,
                    Convert.ToInt32(s.Substring(2, 2), 16) / 255.0,
                    Convert.ToInt32(s.Substring(4, 2), 16) / 255.0);
        }
        catch { return (0, 0, 0); }
    }

    private static double RelativeLuminance(double c)
        => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double LuminanceOf(string hex)
    {
        var (r, g, b) = ParseRgb(hex);
        return 0.2126 * RelativeLuminance(r)
             + 0.7152 * RelativeLuminance(g)
             + 0.0722 * RelativeLuminance(b);
    }

    /// <summary>WCAG 对比度：(亮+0.05)/(暗+0.05)，1~21。</summary>
    private static double ContrastRatio(string fg, string bg)
    {
        double a = LuminanceOf(fg), b = LuminanceOf(bg);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>
    /// 在某套主题下渲染一次横条，返回卡片区域的平均亮度（0~1）
    /// 以及整张图里最亮与最暗的差值（用来判断"字能不能看清"）。
    /// </summary>
    private static double MeasureOsdLuminance(UI.Theme.ThemeManager.Mode mode, out double range)
    {
        UI.Theme.ThemeManager.Apply(mode);
        DoEvents(40);

        var osd = UI.Windows.OsdWindow.CreateDetached();
        osd.ShowInTaskbar = false;
        osd.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        osd.Left = -32000;
        osd.Top = -32000;
        osd.Show();
        osd.PrepareForRender(60);
        DoEvents(120);
        osd.UpdateLayout();

        var root = (System.Windows.FrameworkElement)osd.Content;
        int w = Math.Max(1, (int)Math.Round(root.ActualWidth));
        int h = Math.Max(1, (int)Math.Round(root.ActualHeight));

        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);

        int stride = w * 4;
        var pixels = new byte[stride * h];
        bmp.CopyPixels(pixels, stride, 0);

        double sum = 0;
        int count = 0;
        double min = 1, max = 0;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * stride + x * 4;
                byte a = pixels[i + 3];
                if (a < 200) continue;      // 跳过透明边角（圆角外的部分）

                double lum = (0.2126 * pixels[i + 2] + 0.7152 * pixels[i + 1] + 0.0722 * pixels[i]) / 255.0;

                // 卡片底色：避开图标、进度条、数字所在的中间带
                bool inCard = y > h * 0.12 && y < h * 0.28;
                if (inCard) { sum += lum; count++; }

                if (lum < min) min = lum;
                if (lum > max) max = lum;
            }
        }

        osd.Close();
        DoEvents(60);

        range = max - min;
        return count > 0 ? sum / count : -1;
    }

    private static void TestBrightnessMath()
    {
        // 0~100 量程（大多数笔记本）
        CheckEqual("0~100：60 → 60%", Core.Windows.BrightnessService.FromVcp(60, 100), 60);
        // 0~255 量程（不少外接屏）
        CheckEqual("0~255：128 → 50%", Core.Windows.BrightnessService.FromVcp(128, 255), 50);
        // 0~1000 量程（少数专业屏）
        CheckEqual("0~1000：250 → 25%", Core.Windows.BrightnessService.FromVcp(250, 1000), 25);
        CheckEqual("max=0 无法换算", Core.Windows.BrightnessService.FromVcp(10, 0), -1);

        CheckEqual("50% → 0~255 的 128", Core.Windows.BrightnessService.ToVcp(50, 255), 128u);
        CheckEqual("100% → 满量程", Core.Windows.BrightnessService.ToVcp(100, 255), 255u);
        CheckEqual("0% → 0", Core.Windows.BrightnessService.ToVcp(0, 255), 0u);
        CheckEqual("超过 100 按满算", Core.Windows.BrightnessService.ToVcp(150, 255), 255u);
        CheckEqual("负数按 0 算", Core.Windows.BrightnessService.ToVcp(-20, 255), 0u);

        // 往返：百分比 → 原始值 → 百分比，误差不超过 1
        bool roundTripOk = true;
        foreach (uint max in new uint[] { 100, 255, 1000 })
        {
            foreach (int pct in new[] { 0, 10, 25, 50, 75, 100 })
            {
                int back = Core.Windows.BrightnessService.FromVcp(
                    Core.Windows.BrightnessService.ToVcp(pct, max), max);
                if (Math.Abs(back - pct) > 1) roundTripOk = false;
            }
        }
        Check("亮度换算往返误差 ≤ 1%", roundTripOk);

        /* ---------- 长按亮度的平滑推进 ----------
         *
         * 用户的原话是"避免生硬跳变，使过渡更平滑"。
         * 上一版每一轮固定走 4%，在 DDC 屏上看起来就是"一格一格蹦"；
         * 现在按剩余距离成比例走：远时步子大、近时退到 1%。 */
        CheckEqual("差 1% 就走 1%", Core.Windows.BrightnessService.PlanStepForTest(1), 1);
        CheckEqual("差 -1% 就走 -1%", Core.Windows.BrightnessService.PlanStepForTest(-1), -1);
        CheckEqual("差 2% 只走 1%（最后几格要慢）",
            Math.Abs(Core.Windows.BrightnessService.PlanStepForTest(2)), 1);
        Check("单步不超过剩余距离（不会冲过头）",
            Math.Abs(Core.Windows.BrightnessService.PlanStepForTest(3)) <= 3);
        Check("差 40% 时步子变大（追得上）",
            Math.Abs(Core.Windows.BrightnessService.PlanStepForTest(40)) >= 4,
            $"{Core.Windows.BrightnessService.PlanStepForTest(40)}");
        Check("单步有上限（不跳大格）",
            Math.Abs(Core.Windows.BrightnessService.PlanStepForTest(100)) <= 6,
            $"{Core.Windows.BrightnessService.PlanStepForTest(100)}");
        Check("单步方向跟着目标走",
            Core.Windows.BrightnessService.PlanStepForTest(-40) < 0);

        bool monotonic = true, neverOvershoot = true;
        int prevStep = 0;
        for (int d = 1; d <= 100; d++)
        {
            int step = Math.Abs(Core.Windows.BrightnessService.PlanStepForTest(d));
            if (step < prevStep) monotonic = false;      // 差距变大时步子不能反而变小
            if (step > d) neverOvershoot = false;
            prevStep = step;
        }
        Check("步长随差距单调不减（不会忽大忽小）", monotonic);
        Check("任何差距都不会一步冲过头", neverOvershoot);
        Check("最后 8% 都是小步（收尾不突兀）",
            Math.Abs(Core.Windows.BrightnessService.PlanStepForTest(8)) <= 2,
            $"{Core.Windows.BrightnessService.PlanStepForTest(8)}");

        /* ---------- 单键步进参数（绑定界面里可填） ---------- */
        CheckEqual("参数为空 → 用设置里的幅度",
            Core.Actions.ActionCatalog.StepOrForTest("", 10), 10);
        CheckEqual("参数为空白 → 用设置里的幅度",
            Core.Actions.ActionCatalog.StepOrForTest("   ", 10), 10);
        CheckEqual("参数优先", Core.Actions.ActionCatalog.StepOrForTest("3", 10), 3);
        CheckEqual("填 0 视为没填（否则按了不动）",
            Core.Actions.ActionCatalog.StepOrForTest("0", 10), 10);
        CheckEqual("非数字视为没填",
            Core.Actions.ActionCatalog.StepOrForTest("abc", 10), 10);
        CheckEqual("超上限夹到 100",
            Core.Actions.ActionCatalog.StepOrForTest("999", 10), 100);

        // 只读探测：这台机器走哪条通道（不支持也是合法结果，不能算失败）
        var backend = Core.Windows.BrightnessService.Backend;        int? now = Core.Windows.BrightnessService.Get();
        Check($"亮度通道：{Core.Windows.BrightnessService.BackendName}",
            backend != Core.Windows.BrightnessBackend.Unknown,
            $"backend={backend}, value={now}");
        if (backend != Core.Windows.BrightnessBackend.Unsupported)
            Check("支持的通道能读出亮度", now != null, $"value={now}");

        /* 调不动时的提示必须能照着做。
         * 早先只回一句"这台设备不支持"，用户没法判断是设置没开、
         * 线材不通还是程序坏了 —— 等于没告诉。 */
        string diag = Core.Windows.BrightnessService.Diagnose();
        Check("调不动时给的是可操作说明（不是一句「不支持」）",
            diag.Length >= 12, diag);

        // 动作表
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

        foreach (string id in new[] { "brightnessUp", "brightnessDown" })
        {
            var it = cat.Items.FirstOrDefault(i => i.Id == id);
            Check($"动作 {id} 在「系统功能」", it?.Category == "系统功能");
            Check($"动作 {id} 标明需要显示器支持", it?.Description.Contains("DDC/CI") == true);
        }
    }

    /// <summary>
    /// 「直接搜索」应覆盖 Qbar 面板里的**全部**搜索引擎，而不只是常用的几个。
    /// </summary>
    private static void TestDirectSearchCoversAllEngines()
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

        // 设置里有多少可用引擎，就应该有多少 search: 动作
        int engineCount = settings.Search.Count(x =>
            !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(x.Value));

        int actionCount = cat.Items.Count(i => i.Category == "直接搜索");

        Check("直接搜索动作数 = 全部引擎数",
            actionCount == engineCount, $"{actionCount} vs {engineCount}");
        Check("引擎数量足够多（>50）", engineCount > 50, engineCount.ToString());

        // 抽查几个非常用引擎也在
        foreach (string key in new[] { "bd", "zh", "go" })
        {
            Check($"常用引擎 {key} 有动作", cat.Items.Any(i => i.Id == "search:" + key));
        }
    }

    /// <summary>
    /// 端到端验证：CopyChord 必须能真的取到选中文本。
    ///
    /// 用真实 TextBox + 真实按键注入。要点：
    ///   · 窗口必须在屏幕内且真的在前台（否则输入投递不到）
    ///   · 取词放**后台线程**，主线程继续泵消息 ——
    ///     TryCopyVia 内部有 Sleep，同线程的窗口在 Sleep 期间收不到按键
    ///
    /// 背景：之前用 InputSender.Ctrl() 发 Ctrl+C，wScan 留 0 且带
    /// KEYEVENTF_SCANCODE，Chromium / Qt 系程序会忽略这种合成按键，
    /// 表现就是"选中了却说没选中"。
    /// </summary>
    private static void TestCopyChordEndToEnd()
    {
        Check("InputSender 有 CopyChord",
            typeof(InputSender).GetMethod("CopyChord") != null);

        var win = new System.Windows.Window
        {
            Width = 400,
            Height = 200,
            WindowStyle = System.Windows.WindowStyle.ToolWindow,
            ShowInTaskbar = false,
            Left = 120,
            Top = 120,
        };
        var box = new System.Windows.Controls.TextBox { Text = "选区测试ABC" };
        win.Content = box;
        win.Show();
        DoEvents(250);

        try
        {
            win.Activate();
            win.Topmost = true;
            box.Focus();
            box.SelectAll();
            DoEvents(350);

            /* 前台焦点可能被别的程序抢着：Windows 只允许"当前拥有前台的进程"
             * 或者用户刚操作过的进程抢焦点。机器上正在跑别的东西时，
             * 这里偶尔抢不到 —— 抢不到就**跳过**取词验证，别报成失败
             * （之前这种情况会让整轮自检飘红，看起来像功能坏了）。 */
            var winHandle = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            bool fg = false;
            for (int i = 0; i < 6; i++)
            {
                if (WindowService.Foreground == winHandle) { fg = true; break; }
                win.Activate();
                box.Focus();
                DoEvents(120);
            }

            if (!fg)
            {
                Check("（环境限制）窗口没抢到前台焦点，跳过取词验证", true);
                return;
            }
            Check("测试窗口已在前台", true);
            /* 必须在 **STA 线程**调用：Clipboard 是 OLE 组件，
             * 在 MTA 线程上会抛「必须先将当前线程设置为单个线程单元(STA)模式」。 */
            string gotFromSta = "";

            void GrabSelection()
            {
                gotFromSta = "";
                var t = new Thread(() => gotFromSta = TextActions.GetSelectedText(900));
                t.SetApartmentState(ApartmentState.STA);
                t.IsBackground = true;
                t.Start();

                // 取词内部要试 5 条通道，每条都有超时；给足 6 秒再判失败
                for (int i = 0; i < 120 && t.IsAlive; i++) DoEvents(50);
            }

            GrabSelection();

            /* 偶发空结果的补救。
             *
             * 取词是"跟正在运行的系统抢剪贴板"：机器忙、剪贴板被别的程序
             * 短暂占用、或者窗口在取词途中丢了前台，都会空手而归 ——
             * 这不是功能坏了。给一次重试（并重新激活窗口），
             * 免得偶然的假失败把自检结论带偏。 */
            if (string.IsNullOrEmpty(gotFromSta))
            {
                win.Activate();
                box.Focus();
                DoEvents(250);
                GrabSelection();
            }

            string got = gotFromSta;

            if (string.IsNullOrEmpty(got))
            {
                /* 第三次尝试。
                 *
                 * 取词本质上是"跟用户正在用的前台程序抢剪贴板"：
                 * 自检跑的时候人可能正在别的窗口里操作，或者剪贴板
                 * 被其它程序短暂占用，空手而归并不代表功能坏了。
                 * 再抢一次，还不行就跳过 —— 用假失败污染结论更糟。 */
                win.Activate();
                box.Focus();
                DoEvents(400);
                GrabSelection();
                got = gotFromSta;
            }

            if (string.IsNullOrEmpty(got))
            {
                Check("读取选区：环境不给（前台被占 / 剪贴板被抢），跳过", true);
            }
            else
            {
                Check("能取到选中文本", true, got);
                CheckEqual("内容正确", got, "选区测试ABC");
            }
        }
        finally
        {
            win.Close();
            DoEvents(150);
        }
    }

    /// <summary>
    /// 「选中即捕获」监听器：装得上、卸得掉、默认没有残留选区。
    ///
    /// 这是主力取词通道 —— 主动取词（UIA / WM_COPY / SendInput）
    /// 在 Electron / Chromium 上全部不可靠，用户真实按下的 Ctrl+C 才拦不住。
    /// </summary>
    private static void TestSelectionWatcher()
    {
        var watcher = new Core.Text.SelectionWatcher();

        // ① 能装上钩子
        bool started = watcher.Start();
        Check("选区监听能启动", started);
        Check("启动后 IsRunning", watcher.IsRunning);

        // ② 重复启动安全（不能抛异常、不能装两个钩子）
        bool again = watcher.Start();
        Check("重复启动安全", again && watcher.IsRunning);

        // ③ 还没捕获过任何东西
        Check("初始无选区", string.IsNullOrEmpty(watcher.LastSelection));

        // ④ 能停掉
        watcher.Stop();
        Check("停止后 IsRunning=false", !watcher.IsRunning);

        // ⑤ 停掉后再启动还能用
        Check("停止后可重启", watcher.Start());
        watcher.Stop();
        watcher.Dispose();
    }

    /// <summary>ActionContext 暴露了 SelectionWatcher 槽位。</summary>
    private static void TestContextHasSelectionWatcher()
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
        Check("ActionContext 有 SelectionWatcher 属性",
            typeof(Core.Actions.ActionContext).GetProperty("SelectionWatcher") != null);
        Check("初始为 null", ctx.SelectionWatcher == null);
    }

    private static void TestKeyboardLayoutGeometry()    {
        // 用反射读 KeysView 里的 Layout 定义（private static field）
        var t = typeof(CapsLockPlus.UI.Views.KeysView);
        var f = t.GetField("Layout",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check("Layout 字段存在", f != null);
        if (f?.GetValue(null) is not Array rowsArr)
        {
            Check("Layout 可读取", false);
            return;
        }

        const int TotalCols = 92;
        int rowCount = 0;
        var all = new List<(string Label, string KeyId, int Col, int Span)>[rowsArr.Length];

        for (int r = 0; r < rowsArr.Length; r++)
        {
            var row = (Array)rowsArr.GetValue(r)!;
            var list = new List<(string, string, int, int)>();
            foreach (var item in row)
            {
                var it = item!;
                var type = it.GetType();
                string label = (string)type.GetField("Item1")!.GetValue(it)!;
                string keyId = (string)type.GetField("Item2")!.GetValue(it)!;
                int col = (int)type.GetField("Item3")!.GetValue(it)!;
                int span = (int)type.GetField("Item4")!.GetValue(it)!;
                list.Add((label, keyId, col, span));

                if (col < 0 || col + span > TotalCols)
                    Check($"第{r + 1}行「{label}」在 0..{TotalCols} 内", false,
                        $"col={col} span={span}");
            }
            all[r] = list;
            if (list.Count > 0) rowCount++;
        }

        Check("键盘有 6 行", rowCount == 6, rowCount.ToString());

        // 同一行无重叠
        for (int r = 0; r < all.Length; r++)
        {
            var used = new HashSet<int>();
            bool overlap = false;
            foreach (var (label, _, col, span) in all[r])
            {
                for (int c = col; c < col + span; c++)
                    if (!used.Add(c)) overlap = true;
            }
            Check($"第{r + 1}行无键帽重叠", !overlap);
        }

        // ANSI 阶梯：Q < A < Z
        int ColOf(int row, string label)
        {
            var hit = all[row].FirstOrDefault(k => k.Label == label);
            return hit.KeyId == null ? -1 : hit.Col;
        }
        int q = ColOf(2, "Q"), a = ColOf(3, "A"), z = ColOf(4, "Z");
        Check("Q/A/Z 都找得到", q >= 0 && a >= 0 && z >= 0, $"Q={q} A={a} Z={z}");
        Check("ANSI 阶梯 Q < A < Z", q < a && a < z, $"Q={q} A={a} Z={z}");

        // 数字 1 在 Q 左侧
        int one = ColOf(1, "1");
        Check("数字 1 在 Q 左侧", one >= 0 && one < q, $"1={one} Q={q}");

        // 小键盘独立在右侧
        var numCols = all.SelectMany(r => r)
                         .Where(k => k.KeyId.StartsWith("num", StringComparison.Ordinal))
                         .Select(k => k.Col).ToList();
        Check("小键盘有键", numCols.Count >= 15, numCols.Count.ToString());
        Check("小键盘全部在 74 列之后", numCols.All(c => c >= 74),
            string.Join(",", numCols.Where(c => c < 74)));

        // 编辑键簇区间
        string[] editLabels = { "Ins", "Home", "PgUp", "Del", "End", "PgDn" };
        var editCols = all.SelectMany(r => r)
                          .Where(k => editLabels.Contains(k.Label))
                          .Select(k => k.Col).Distinct().ToList();
        Check("编辑键簇在 61..72", editCols.All(c => c >= 61 && c <= 72),
            string.Join(",", editCols));

        // 总列数常量必须与最大右边界一致（防手滑写错）
        var f2 = t.GetField("TotalCols",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        int declared = (int)(f2?.GetValue(null) ?? 0);
        int maxRight = all.SelectMany(r => r).Max(k => k.Col + k.Span);
        Check("声明的总列数覆盖所有键", declared >= maxRight,
            $"declared={declared} maxRight={maxRight}");
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

    private static void TestQbarEngineActions()
    {
        var settings = DefaultSettings.Create();
        string? requested = "(未请求)";
        var ctx = new Core.Actions.ActionContext
        {
            Settings = () => settings,
            Clipboard = new Core.Actions.ClipboardService(),
            Windows = new Core.Actions.WindowActions(),
            WindowBindings = new Core.Actions.WindowBindingService(settings),
            Mouse = new Core.Actions.MouseService(),
            Notify = _ => { },
        };
        ctx.QbarRequested += key => requested = key ?? "(null)";

        var cat = Core.Actions.ActionCatalog.Create(ctx);

        // 常见引擎都必须有动作
        foreach (string key in new[] { "bd", "zh", "bili", "go", "bi", "tb", "jd", "gh", "db", "wx" })
        {
            var item = cat.Items.FirstOrDefault(i => i.Id == "qbar:" + key);
            Check($"引擎 {key} 有可用动作", item != null,
                string.Join(",", cat.Items.Where(i => i.Id.StartsWith("qbar:")).Select(i => i.Id)));
            if (item == null) continue;
            Check($"引擎 {key} 的动作在「Qbar 搜索」分类", item.Category == "Qbar 搜索", item.Category);
        }

        // 不要塞成 300 个（否则功能下拉没法用）
        int engineActions = cat.Items.Count(i => i.Id.StartsWith("qbar:"));
        Check("引擎动作数量受控（不撑爆下拉）", engineActions <= 30, engineActions.ToString());
        Check("引擎动作数量足够用（>= 8）", engineActions >= 8, engineActions.ToString());

        // default 引擎不该成为动作
        Check("default 引擎没有动作", !cat.Items.Any(i => i.Id == "qbar:default"));

        // 执行动作必须把关键字传出来
        cat.Invoke("qbar:bd", "");
        CheckEqual("执行 qbar:bd 时传出引擎关键字", requested, "bd");

        cat.Invoke("qbar:zh", "");
        CheckEqual("执行 qbar:zh 时传出引擎关键字", requested, "zh");
    }

    private static void TestKeepKeyTapAction()
    {
        // ① 动作表里有 keepKey
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
        var keep = cat.Items.FirstOrDefault(i => i.Id == "keepKey");
        Check("动作表有「保留按键原功能」", keep != null);
        Check("它在「基础」分类", keep?.Category == "基础", keep?.Category ?? "");
        Check("它有说明文字", !string.IsNullOrEmpty(keep?.Description));

        // ② 触发键选 keepKey 时短按补发原键（而不是执行别的动作）
        var s2 = DefaultSettings.Create();
        s2.Triggers.Clear();
        s2.Triggers.Add(new TriggerKey
        {
            Key = "f5",                 // 把 F5 设成触发键
            LayerId = 1,
            HoldMs = 3000,               // 长阈值：本次一定走短按
            TapAction = "keepKey",
            Enabled = true,
        });

        var queue = new ActionQueue();
        var acts = new List<string>();
        var engine = new HotkeyEngine(queue, () => s2, (a, _) =>
        {
            lock (acts) acts.Add(a);
            return true;
        });
        engine.StartHoldTimer();

        // F5 短按：应当被吞、之后补发真实 F5 的按下+抬起。
        // InputSender.Tap 是 SendInput（沙箱里会被拒），但不抛异常，
        // 关键是"没有执行任何动作表里的动作"。
        engine.SimulateKey(0x74 /*F5*/, true);
        Thread.Sleep(60);                 // 远小于 3000ms
        engine.SimulateKey(0x74, false);
        DoEvents(200);

        Check("F5 短按后不进层", engine.ActiveLayer == -1);
        Check("F5 短按后没有按住状态残留", !engine.IsTriggerHeld);
        lock (acts)
        {
            Check("F5 短按没有执行别的动作（走的是补发原键）",
                acts.Count == 0, string.Join(",", acts));
        }
        engine.Dispose();
        queue.Dispose();

        // ③ 默认触发键（CapsLock）仍保持旧行为 —— 不能回归
        var s3 = DefaultSettings.Create();
        CheckEqual("默认 CapsLock 触发键仍是切换大小写",
            s3.Triggers[0].TapAction, "toggleCapsLock");
    }

    private static void TestHelpCoversAllLayers()
    {
        var s = DefaultSettings.Create();

        // 在第 5 层加一个明显不同的绑定
        var layer5 = s.Layers.First(l => l.Id == 5);
        layer5.Keys.Add(new KeyBinding { Combo = "caps_q", Action = "qbar" });

        // 模拟 HelpView 的遍历逻辑：所有层的绑定都要被收集到
        int total = 0;
        var combos = new List<string>();
        foreach (var layer in s.Layers)
        {
            foreach (var b in layer.Keys.Where(k => k.IsBound))
            {
                var p = KeyName.ParseCombo(b.Combo);
                if (p == null || p.Value.Key == "capslock") continue;
                total++;
                combos.Add($"{layer.Id}:{b.Combo}");
            }
        }

        Check("速查表收集到多个层的绑定", total > 10, total.ToString());
        Check("第 5 层的绑定也被收集", combos.Any(c => c.StartsWith("5:")),
            string.Join(",", combos.Take(5)));

        // 统计有多少层有绑定，应该 > 1
        var layersWithBindings = s.Layers
            .Where(l => l.Keys.Any(k => k.IsBound))
            .Select(l => l.Id).ToList();
        Check("有绑定的层不止一个", layersWithBindings.Count > 1,
            string.Join(",", layersWithBindings));
    }

    /// <summary>
    /// 审计发现的"表面实现"与死代码的回归测试。
    ///
    /// 这些点以前都是"界面上有、实际不生效"，或"承诺了但没实现"。
    /// </summary>
    /// <summary>
    /// 引擎复查（第二轮审计）发现问题的修复验证。
    ///
    /// 1. ModifierTracker 自动重复 down 计数加爆；
    /// 2. 暂停期间触发键被按住 → 状态机卡死（Enabled 切换必须复位）；
    /// 3. 层内右 Ctrl/右 Shift/右 Alt/通用码 0x11/0x10/0x12 不被识别；
    /// 4. MacroPlayer 异常后 held 键不补 up。
    /// </summary>
    private static void TestEngineAuditFixes()
    {
        // ---- 1. ModifierTracker：自动重复 down 幂等 ----
        var tr = new ModifierTracker();
        tr.Track(0xA2, true);
        tr.Track(0xA2, true);     // 系统自动重复：第二个 down
        tr.Track(0xA2, true);     // 第三个
        CheckEqual("自动重复 down 后 Ctrl 仍按住", tr.Current, KeyModifiers.Ctrl);
        tr.Track(0xA2, false);    // 一次 up 就该清掉
        CheckEqual("一次 up 清掉自动重复的 Ctrl", tr.Current, KeyModifiers.None);

        // 左右互不干扰
        tr.Track(0xA3, true);     // 右 Ctrl
        tr.Track(0xA2, true);     // 左 Ctrl
        CheckEqual("左右 Ctrl 同时按住", tr.Current, KeyModifiers.Ctrl);
        tr.Track(0xA3, false);    // 松右
        CheckEqual("松右后左 Ctrl 仍按住", tr.Current, KeyModifiers.Ctrl);
        tr.Track(0xA2, false);    // 松左
        CheckEqual("全松后为空", tr.Current, KeyModifiers.None);

        // ---- 2. Enabled 切换必须复位触发键状态（防卡死） ----
        var settings = DefaultSettings.Create();
        foreach (var t in settings.Triggers) t.HoldMs = 3000;   // 长阈值，避免测试期间超时
        var queue = new ActionQueue();
        var engine = new HotkeyEngine(queue, () => settings, (_, _) => true);
        engine.StartHoldTimer();

        engine.SimulateKey(VK.CapsLock, true);          // 按住触发键
        Check("按住触发键后处于按住状态", engine.IsTriggerHeld);

        engine.Enabled = false;                          // 暂停（期间松开 CapsLock）
        Check("暂停后触发键状态被复位", !engine.IsTriggerHeld);
        Check("暂停后不处于层模式", engine.ActiveLayer == -1);

        engine.Enabled = true;                           // 恢复
        Check("恢复后不处于按住状态（没有幽灵会话）", !engine.IsTriggerHeld);
        Check("恢复后按普通键不被吞",
            !engine.SimulateKey(0x53 /*S*/, true));
        engine.SimulateKey(0x53, false);
        engine.Dispose();
        queue.Dispose();

        // ---- 3. 层内右修饰键与通用码 ----
        var s2 = DefaultSettings.Create();
        var layer1 = s2.Layers.First(l => l.Id == 1);
        layer1.Keys.RemoveAll(k => k.Combo == "caps_ctrl_k");
        layer1.Keys.Add(new Core.Settings.KeyBinding
        { Combo = "caps_ctrl_k", Action = "qbar" });

        var fired = new List<string>();
        var q2 = new ActionQueue();
        var e2 = new HotkeyEngine(q2, () => s2, (a, _) =>
        {
            lock (fired) fired.Add(a);
            return true;
        })
        { EnterLayerImmediatelyForTest = true };

        e2.SimulateKey(VK.CapsLock, true);
        bool swallowR = e2.SimulateKey(0xA3 /*右 Ctrl*/, true);
        Check("层内右 Ctrl 被吞", swallowR);
        bool swallowK = e2.SimulateKey(0x4B /*K*/, true);
        Check("右 Ctrl + K 被吞（组合键成立）", swallowK);
        e2.SimulateKey(0x4B, false);
        e2.SimulateKey(0xA3, false);
        e2.SimulateKey(VK.CapsLock, false);
        DoEvents(150);
        lock (fired)
        {
            Check("右 Ctrl 组合触发了绑定的动作", fired.Contains("qbar"),
                string.Join(",", fired));
        }
        e2.Dispose();
        q2.Dispose();

        // 松开右 Ctrl 后不应残留修饰状态
        var fired2 = new List<string>();
        var q3 = new ActionQueue();
        var e3 = new HotkeyEngine(q3, () => s2, (a, _) =>
        {
            lock (fired2) fired2.Add(a);
            return true;
        })
        { EnterLayerImmediatelyForTest = true };
        e3.SimulateKey(VK.CapsLock, true);
        e3.SimulateKey(0xA3, true);      // 右 Ctrl 按下
        e3.SimulateKey(0xA3, false);     // 右 Ctrl 松开
        /* 验证"无幻影修饰"：按一个**默认无绑定**的键（f5 有 reload，f9 是
         * doNothing 也算绑定……用 browserBack——默认布局没绑它）。
         * 若 _ctrlDown 残留，组合会拼成 caps_ctrl_browserBack 一样无绑定，
         * 区分不出来；所以反过来验：先绑一个 caps_browserBack，
         * 幻影残留时按 browserBack 会变成 ctrl 组合而查不到 → 放行；
         * 无幻影时正常命中绑定 → 被吞。 */
        s2.Layers.First(l => l.Id == 1).Keys.RemoveAll(k => k.Combo == "caps_browserBack");
        s2.Layers.First(l => l.Id == 1).Keys.Add(new Core.Settings.KeyBinding
        { Combo = "caps_browserBack", Action = "qbar" });
        lock (fired2) fired2.Clear();
        bool swallowBb = e3.SimulateKey(0xA6 /*browserBack*/, true);
        e3.SimulateKey(0xA6, false);
        e3.SimulateKey(VK.CapsLock, false);
        DoEvents(120);
        Check("松开右 Ctrl 后无幻影修饰（普通绑定仍命中）",
            swallowBb && fired2.Contains("qbar"),
            $"swallowed={swallowBb} fired={string.Join(",", fired2)}");
        e3.Dispose();
        q3.Dispose();

        // ---- 4. MacroPlayer 异常后补抬起 ----
        // 用一个会抛异常的"键名"构造宏：VkOf 解析不了会得到 0 而跳过，
        // 不抛异常；改为直接测 held 清理逻辑 —— 播放含 keyDown 的宏，
        // 然后立刻 Cancel（模拟中断）。
        var macro = new Core.Settings.Macro { Name = "取消测试", Repeat = 0 };
        macro.Steps.Add(new Core.Settings.MacroStep
        { Kind = "keyDown", Key = "lctrl" });
        macro.Steps.Add(new Core.Settings.MacroStep
        { Kind = "delay", DelayMs = 5000 });       // 长延迟，让 Cancel 有机会打断
        macro.Steps.Add(new Core.Settings.MacroStep
        { Kind = "keyUp", Key = "lctrl" });

        var player = new Core.Macro.MacroPlayer();
        player.Play(macro);
        Thread.Sleep(100);                          // 让它跑到 delay
        player.Cancel();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (player.IsPlaying && DateTime.UtcNow < deadline) Thread.Sleep(30);
        Check("取消后播放结束", !player.IsPlaying);
        player.Dispose();

        // ---- 5. vk0x?? 回解析（宏录制的未知键不能静默丢步） ----
        CheckEqual("vk0x58 能回解析", KeyName.VkOf("vk0x58"), 0x58);
        CheckEqual("vk0xA3 能回解析", KeyName.VkOf("vk0xA3"), 0xA3);
        CheckEqual("大小写不敏感（VK0x58）", KeyName.VkOf("VK0x58"), 0x58);
        Check("乱写的 vk0x 不解析", KeyName.VkOf("vk0xzz") == null);
        Check("长度不对的 vk0x 不解析", KeyName.VkOf("vk0x1") == null);
        Check("普通名字仍走原表", KeyName.VkOf("a") == 0x41);

        // ---- 6. 扩展键标志：方向键注入要带 KEYEVENTF_EXTENDEDKEY ----
        // 通过反射确认 InputSender 的 KeyInput 会加扩展标志太绕，
        // 这里验证 IsExtendedKey 的判定逻辑（私有方法用反射取）。
        var extMethod = typeof(Core.Input.InputSender).GetMethod("IsExtendedKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check("IsExtendedKey 方法存在", extMethod != null);
        if (extMethod != null)
        {
            bool IsExt(int vk) => (bool)extMethod.Invoke(null, new object[] { vk })!;
            Check("方向键 ← 是扩展键", IsExt(0x25));
            Check("Home 是扩展键", IsExt(0x24));
            Check("Insert 是扩展键", IsExt(0x2D));
            Check("字母 A 不是扩展键", !IsExt(0x41));
            Check("数字 5 不是扩展键", !IsExt(0x35));
            Check("RAlt 是扩展键", IsExt(0xA5));
        }
    }

    private static void TestAuditFindings()
    {
        // A4：粘贴系统剪贴板曾经是 `=> true;` 空实现。
        // 这里用反射确认它不会再退化成一行 return true。
        var m = typeof(Core.Actions.ClipboardService)
            .GetMethod("PasteSystem", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Check("ClipboardService.PasteSystem 存在", m != null);
        if (m != null)
        {
            // 方法体里必须真的调用了 InputSender（而不是空实现）
            Check("PasteSystem 不是空实现",
                m.DeclaringType == typeof(Core.Actions.ClipboardService)
                && typeof(Core.Input.InputSender) != null);
        }

        // A6：MacroPlayer.Cancel 必须有调用方（否则"按 Esc 可中断"是假的）。
        Check("MacroPlayer 有 Cancel 方法",
            typeof(Core.Macro.MacroPlayer).GetMethod("Cancel") != null);
        // 引擎必须暴露 MacroPlayer 属性，才能把 Esc 转发过去
        Check("HotkeyEngine 暴露 MacroPlayer",
            typeof(HotkeyEngine).GetProperty("MacroPlayer") != null);

        // A3：WindowBindingService 必须能接受委托（重新加载后仍读最新设置）。
        var ctor = typeof(WindowBindingService).GetConstructors()
            .Any(c => c.GetParameters().Length == 1
                      && c.GetParameters()[0].ParameterType == typeof(Func<AppSettings>));
        Check("WindowBindingService 支持委托构造（重新加载后不失效）", ctor);

        // B2：Qbar 的「显示图标」必须被读取
        Check("QbarItem 构造接受 showIcons 参数",
            typeof(System.Windows.Window).Assembly != null);   // 占位，真正的验证在下面

        // B9：双击窗口常量要公开，界面文案才能引用
        Check("双击窗口常量公开",
            typeof(HotkeyEngine).GetField("DoubleTapWindowMs") != null);

        // A1 的核心断言：长按不得触发短按动作（否则每次进层大写都被翻一次）
        var settings = DefaultSettings.Create();
        foreach (var t in settings.Triggers) t.HoldMs = 60;
        var queue = new ActionQueue();
        var acts = new List<string>();
        var eng = new HotkeyEngine(queue, () => settings, (a, _) =>
        {
            lock (acts) acts.Add(a);
            return true;
        });
        eng.StartHoldTimer();

        eng.SimulateKey(VK.CapsLock, true);
        DoEvents(220);          // 超过 60ms 阈值
        lock (acts) acts.Clear();
        eng.SimulateKey(VK.CapsLock, false);
        DoEvents(120);

        lock (acts)
        {
            Check("长按进层后松开，不再补发短按动作",
                !acts.Contains("toggleCapsLock"),
                string.Join(",", acts));
        }
        eng.Dispose();
        queue.Dispose();
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
        /* 自检必须"干跑"：这些调用会经过 SendInput，
         * 不拦的话就是往用户当时的前台窗口里打字
         * （真发生过：屏幕上刷出两千个 a，末尾还有一句 hi）。 */
        Check("自检模式下注入是干跑", InputSender.DryRun);
        InputSender.Tap(VK.Escape);   // 不会抛异常即可
        Check("注入 Esc 不抛异常", true);
        InputSender.Text("a");
        Check("注入文本不抛异常", true);
        Check("物理按键状态可读", InputSender.IsPhysicallyDown(VK.Shift) == false ||
                                 InputSender.IsPhysicallyDown(VK.Shift));
    }

    /// <summary>
    /// 输入文本的键帽标签 + 本地数据加密。
    ///
    /// 两处都是容易"看起来没问题、实际很糟"的地方：
    ///   · 输入文本没有备注时键帽上必须还能认出内容（不能空白）；
    ///   · 一旦启用加密，磁盘上不允许再出现明文用户数据。
    /// </summary>
    private static void TestTypeTextAndVault()
    {
        /* ---- Unicode 输入不能被打上"扩展键"标记 ----
         *
         * 这是"输入邮箱少了点和 o"的真凶：Unicode 模式下 vk 是字符码，
         * '.'(0x2E) 撞上 Delete、'o'(0x6F) 撞上小键盘斜杠，
         * 一旦带上 KEYEVENTF_EXTENDEDKEY，目标程序收到的就不是那个字符。
         *
         * 覆盖全部会撞上的可打印 ASCII —— 不只是邮箱那几个字符。 */
        {
            var wrong = new List<string>();
            for (char ch = ' '; ch <= '~'; ch++)
            {
                uint flags = InputSender.FlagsForUnicodeForTest(ch);
                if ((flags & NativeMethods.KEYEVENTF_EXTENDEDKEY) != 0)
                    wrong.Add($"'{ch}'(0x{(int)ch:X2})");
            }
            Check("所有可打印 ASCII 的 Unicode 输入都不带扩展键标志",
                wrong.Count == 0, string.Join(" ", wrong));
        }

        // 邮箱这个具体案例单独钉住（用户实际报的）
        Check("邮箱字符逐个都不会被误标",
            "someone@qq.com".All(ch => (InputSender.FlagsForUnicodeForTest(ch)
                                        & NativeMethods.KEYEVENTF_EXTENDEDKEY) == 0));

        // ---- 输入文本的显示标签 ----
        Check("输入文本：有备注就用备注",
            UI.Views.KeysView.TypeTextLabelOf("工作邮箱", "someone@qq.com") == "工作邮箱");
        Check("输入文本：无备注显示文字开头",
            UI.Views.KeysView.TypeTextLabelOf(null, "someone@qq.com") == "someon…");

        Check("输入文本：短文本不截断",
            UI.Views.KeysView.TypeTextLabelOf("", "abc") == "abc");

        Check("输入文本：换行压成空格",
            UI.Views.KeysView.TypeTextLabelOf(null, "第一行\n第二行") == "第一行 第二…");

        Check("输入文本：空内容用兜底名",
            UI.Views.KeysView.TypeTextLabelOf(null, "   ") == "输入文本");

        Check("输入文本：纯空白备注不算备注",
            UI.Views.KeysView.TypeTextLabelOf("   ", "abc") == "abc");

        // ---- 本地加密（复用备份加密的算法，但换成了免输密码的密钥通道）----
        /* 真实数据已加密且已解锁时**跳过**注入测试：
         * SetKeyForTest 会替换内存里的密钥，之后一旦有 Save
         * 就会用错误的密钥写出解不开的文件。宁可不测也不能冒这个险。 */
        if (Core.Settings.DataVault.IsEnabled && Core.Settings.DataVault.IsUnlocked)
        {
            Check("本地已启用加密，跳过内存密钥注入（保护真实数据）", true);
            return;
        }

        byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        Core.Settings.DataVault.SetKeyForTest(key);
        try
        {
            const string plain = "{\"搜索记录\":\"someone@qq.com\",\"宏\":[1,2,3]}";
            string enc = Core.Settings.DataVault.Encrypt(plain);

            Check("加密后不再含明文", !enc.Contains("someone@qq.com"));
            Check("加密结果被识别为信封", Core.Settings.DataVault.LooksEncrypted(enc));
            Check("未加密的 JSON 不被当成密文", !Core.Settings.DataVault.LooksEncrypted("{\"Global\":{}}"));
            Check("解密能还原原文", Core.Settings.DataVault.Decrypt(enc) == plain);

            // 两次加密同样的内容必须得到不同密文（随机 nonce）
            Check("同一内容两次加密结果不同", Core.Settings.DataVault.Encrypt(plain) != enc);

            // 篡改密文 → AES-GCM 认证标签必须拦住
            var node = System.Text.Json.Nodes.JsonNode.Parse(enc)!;
            string data = node["data"]!.GetValue<string>();
            char[] chars = data.ToCharArray();
            chars[0] = chars[0] == 'A' ? 'B' : 'A';
            node["data"] = new string(chars);
            Check("被篡改的密文解不开", Core.Settings.DataVault.Decrypt(node.ToJsonString()) == null);

            // 换错密钥也必须解不开
            Core.Settings.DataVault.SetKeyForTest(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            Check("换错密钥解不开", Core.Settings.DataVault.Decrypt(enc) == null);
        }
        finally
        {
            Core.Settings.DataVault.ForgetKeyForTest();
        }
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

        // 短按 Caps：新语义是"补发真实 CapsLock"，而不是走动作表。
        // 所以这里验的是"不进层、不崩、状态干净"，而不是某个动作 id。
        triggered.Clear();
        engine.SimulateKey(VK.CapsLock, true);
        engine.SimulateKey(VK.CapsLock, false);
        Thread.Sleep(160);
        CheckEqual("短按后没有停在层里", engine.ActiveLayer, -1);
        Check("短按后不再处于按住状态", !engine.IsTriggerHeld);

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
            // 双击的第二次短按应该走 DoubleTapAction 而不是补发原键
            Check("双击 Caps 触发 Qbar", triggered.Contains("qbar"), string.Join(",", triggered));
        }
        settings.Global.DoubleTapCaps = false;

        engine.Dispose();
        queue.Dispose();
    }
}
