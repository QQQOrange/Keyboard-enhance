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

    /* 这是一个 partial 类：按业务域拆成了 SelfTest.<域>.cs。
     * 这里只放**调度与基础设施**（Run/RunAll、断言、DoEvents…），
     * 具体用例在同名 partial 文件里。拆分的唯一目的是让单文件别再是
     * 七千多行 —— 找一个用例要先滚三屏的那种。 */

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

        Section("打开文件夹");
        TestFolderLeafName();

        Section("窗口激活与最小化");
        TestWindowActivateAndMinimize();

        Section("主题");
        TestTheme();

        Section("界面文案");
        TestUiText();

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
        TestQbarChipTextContrast();
        TestQbarOutsideClick();
        TestComplexTriggerKey();
        TestTriggerBadgeFollowsLayer();
        TestSubLayers();
        TestSubLayerCombos();
        TestDirectSearch();
        TestTypeTextAction();
        TestImeGuard();

        Section("自定义按键");
        TestCustomKey();

        Section("音量与亮度");
        TestVolumeCore();
        TestBrightnessMath();
        TestBrightnessPumpStarts();
        TestOsdAndTextInput();
        TestOsdAlwaysOnTop();
        TestOsdIconFollowsBrightness();
        TestOsdIconAnimates();
        TestOsdAboveShellMenu();
        TestOsdScalesAcrossDisplays();

        Section("托盘");
        TestTrayDoubleClick();

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
        TestLayerSurvivesIdle();
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
}