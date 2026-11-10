using System.IO;
using System.Text;
using System.Windows;
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
    /// UIA 取词的超时保护（v4.20.5）。
    ///
    /// UIA 是跨进程 COM 调用，目标程序卡死时会一直等下去，而这条路径跑在
    /// 单线程动作队列上 —— 卡住就是"所有快捷键几秒钟没反应"。
    /// 现在调用方只等固定预算，超时后通道冷却 60 秒。
    /// </summary>
    private static void TestUiaReaderTimeout()
    {
        Check("UIA 超时预算在合理区间",
            Core.Text.UiaSelectionReader.DefaultTimeoutMs >= 200
            && Core.Text.UiaSelectionReader.DefaultTimeoutMs <= 1500,
            Core.Text.UiaSelectionReader.DefaultTimeoutMs.ToString());

        Check("UIA 冷却中判定为跳过",
            Core.Text.UiaSelectionReader.IsSkippingAt(nowMs: 1000, untilMs: 2000));
        Check("UIA 冷却结束恢复可用",
            !Core.Text.UiaSelectionReader.IsSkippingAt(nowMs: 3000, untilMs: 2000));

        /* 真跑一次完整链路（工作线程 + 等待 + 结果回传）：必须**在预算内**返回，
         * 且绝不抛异常。取不到文本是完全正常的（自检环境不一定有选区）。 */
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string text = Core.Text.UiaSelectionReader.GetSelectedText(1200);
        sw.Stop();
        Check("UIA 调用不抛异常且在预算内返回",
            sw.ElapsedMilliseconds < 5000,
            $"{sw.ElapsedMilliseconds}ms，长度={text.Length}");

        // 这次若真的超时了，会把通道冷却 60 秒；必须清掉，否则后面的取词用例全跳过。
        Core.Text.UiaSelectionReader.ResetSkipForTest();
    }

    /// <summary>
    /// 剪贴板"先备份、之后还原"。
    ///
    /// 这条链路是"取词 / 替换选区 / 算式结果写回"的中转站：中转完必须把用户
    /// **原来**的剪贴板内容还回去。早先只记纯文本，原内容是截图或一批文件时
    /// 会被 `Clipboard.Clear()` 永久删掉 —— 所以这里重点验证位图与文件列表，
    /// 而不是"文字能还回去"（那是最容易也对的那一半）。
    ///
    /// 自检会临时改写剪贴板，结束时把用户原来的内容还回去（用同一套快照机制）。
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();

    /// <summary>当前是否有别的窗口正独占剪贴板（OpenClipboard 未释放）。</summary>
    private static bool ClipboardBusy()
    {
        try { return GetOpenClipboardWindow() != IntPtr.Zero; } catch { return false; }
    }

    /// <summary>剪贴板序号：任何程序改动内容都会变。</summary>
    private static uint ClipboardSeq()
    {
        try { return GetClipboardSequenceNumber(); } catch { return 0; }
    }

    private static void TestClipboardBackup()
    {
        var userBackup = Core.Text.ClipboardBackup.Capture();

        /* 剪贴板是全局独占资源：本程序自己（运行中的实例）还在监听剪贴板历史，
         * 别的程序也可能随时打开它 —— 实测会出现 CLIPBRD_E_CANT_OPEN。
         * 所以每一步都重试几次；实在拿不到就把这一步标成"环境限制"跳过，
         * 不能让它变成一条假的失败。 */
        static bool TryOp(Action op, int attempts = 6)
        {
            for (int i = 0; i < attempts; i++)
            {
                try { op(); return true; }
                catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(40); }
                catch (Exception) { return false; }
            }
            return false;
        }

        static string ReadText()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch { return ""; }
        }

        /* 读回时轮询一下：剪贴板是全局资源，本程序自己的历史监听、
         * 其它程序都可能在两个调用之间动它，刚写完立刻读偶尔会读到空的
         * （实测出现过一次）。400ms 内稳定下来都算通过。 */
        static string WaitText(string expected, int timeoutMs = 400)
        {
            string now = ReadText();
            long deadline = Environment.TickCount64 + timeoutMs;
            while (!string.Equals(now, expected, StringComparison.Ordinal)
                   && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(40);
                now = ReadText();
            }
            return now;
        }

        /// <summary>能不能读到"有没有图片"；读不到返回 null（= 环境问题，不是结论）。</summary>
        static string? ClipboardHasImageOrNull()
        {
            for (int i = 0; i < 6; i++)
            {
                try { return Clipboard.ContainsImage() ? "yes" : "no"; }
                catch { System.Threading.Thread.Sleep(60); }
            }
            return null;
        }

        /* 断言里的"详情"是**立即求值**的，所以这些读操作也必须兜住异常 ——
         * 剪贴板被别的进程短暂占用时 OpenClipboard 会失败并抛 CLIPBRD_E_CANT_OPEN，
         * 那会变成一条假的崩溃（不是被测逻辑的错）。 */
        static string SafeFormats()
        {
            try { return string.Join(",", Clipboard.GetDataObject()?.GetFormats() ?? Array.Empty<string>()); }
            catch { return "(剪贴板被占用)"; }
        }

        static int SafeFileCount()
        {
            try { return Clipboard.GetFileDropList()?.Count ?? -1; }
            catch { return -1; }
        }

        try
        {
            /* ---- ① 纯文本 ---- */
            if (!TryOp(() => Clipboard.SetText("原始文字A")))
            {
                Check("（环境限制）剪贴板被占用，跳过快照验证", true);
                return;
            }

            var textBackup = Core.Text.ClipboardBackup.Capture();
            if (!TryOp(() => Clipboard.SetText("中转文字B")))
            {
                Check("（环境限制）剪贴板被占用，跳过文本验证", true);
                return;
            }
            textBackup.Restore();
            uint seqAfterRestore = ClipboardSeq();

            string textBack = WaitText("原始文字A");
            if (textBack.Length == 0)
            {
                /* 机器上有剪贴板管理器时，刚写进去的内容可能被它立刻改掉。
                 * 再还原一次（内容还在备份里），别把环境问题算成功能坏了。 */
                textBackup.Restore();
                textBack = WaitText("原始文字A");
            }

            /* 还读不到，且期间剪贴板被别人动过 / 正被占用 → 按环境限制跳过。
             * 这条曾经随机报假失败，实测是剪贴板管理器抢了内容（不是还原逻辑坏了）。 */
            if (textBack.Length == 0 && (ClipboardSeq() != seqAfterRestore || ClipboardBusy()))
            {
                Check("（环境限制）剪贴板被别的程序占用/改写，跳过文本还原验证", true);
            }
            else
            {
                CheckEqual("文本内容能被还原", textBack, "原始文字A");
            }

            /* ---- ② 位图：当初就是这里丢的 ----
             * 原剪贴板是截图时，旧实现 `hadText == false` 直接 Clear()，
             * 那张截图就永久没了。 */
            var bmp = new System.Windows.Media.Imaging.WriteableBitmap(
                8, 8, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            var pixels = new byte[8 * 8 * 4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = 0x60;
            bmp.WritePixels(new Int32Rect(0, 0, 8, 8), pixels, 8 * 4, 0);

            if (TryOp(() => Clipboard.SetImage(bmp)))
            {
                var imageBackup = Core.Text.ClipboardBackup.Capture();
                if (TryOp(() => Clipboard.SetText("中转文字C")))
                {
                    imageBackup.Restore();
                    /* 剪贴板是所有程序共享的：被别的程序长时间占住时，"读不到图片"可能只是
                 * 读不到（不是被顶掉）。这种情况按环境限制跳过，别报假失败。 */
                /* 剪贴板是全系统共享的：别的程序（截图工具、剪贴板增强）随时可能把它换掉。
                 *
                 * 所以这里不能"读一次就判死刑"：
                 *   ① 先重试几次，避开瞬时占用；
                 *   ② 记录还原后的剪贴板序号，读的时候序号变了说明**别人**动过它 ——
                 *      按环境限制跳过，否则自检会随机报一个假失败（实测复现过一次）。 */
                uint seqAfter = ClipboardSeq();

                string? looksEmpty = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    looksEmpty = ClipboardHasImageOrNull();
                    if (looksEmpty == null || looksEmpty == "yes") break;
                    Thread.Sleep(40);
                }

                bool hijacked = ClipboardSeq() != seqAfter;

                if (looksEmpty == null ||
                    (looksEmpty != "yes" && (hijacked || SafeFormats().Contains("被占用"))))
                {
                    Check("（环境限制）剪贴板被别的程序占用/改写，跳过图片还原验证", true);
                }
                else
                {
                    Check("图片不会被中转文字顶掉", looksEmpty == "yes", SafeFormats());
                }
                }
                else
                {
                    Check("（环境限制）剪贴板被占用，跳过图片验证", true);
                }
            }
            else
            {
                Check("（环境限制）写不进位图，跳过图片验证", true);
            }

            /* ---- ③ 文件列表 ---- */
            var files = new System.Collections.Specialized.StringCollection { @"C:\Windows\notepad.exe" };
            if (TryOp(() => Clipboard.SetFileDropList(files)))
            {
                var fileBackup = Core.Text.ClipboardBackup.Capture();
                if (TryOp(() => Clipboard.SetText("中转文字D")))
                {
                    fileBackup.Restore();
                    int fileCount = SafeFileCount();
                    if (fileCount == 0)
                    {
                        // 剪贴板管理器抢走时再还原一次（同文本那条的理由）
                        fileBackup.Restore();
                        fileCount = SafeFileCount();
                    }
                    Check("文件列表能被还原", fileCount == 1, fileCount.ToString());
                }
                else
                {
                    Check("（环境限制）剪贴板被占用，跳过文件列表验证", true);
                }
            }
            else
            {
                Check("（环境限制）写不进文件列表，跳过该项", true);
            }

            /* ---- ④ 守卫：期间用户自己复制了别的，不能再盖回去 ---- */
            if (TryOp(() => Clipboard.SetText("用户原内容")))
            {
                var guarded = Core.Text.ClipboardBackup.Capture();
                if (TryOp(() => Clipboard.SetText("中转内容")) && TryOp(() => Clipboard.SetText("用户后来复制")))
                {
                    guarded.Restore(currentTextGuard: "中转内容");
                    CheckEqual("期间用户复制的内容不会被覆盖", WaitText("用户后来复制"), "用户后来复制");
                }
                else
                {
                    Check("（环境限制）剪贴板被占用，跳过守卫验证", true);
                }
            }
        }
        catch (Exception ex)
        {
            Check("剪贴板快照/还原", false, ex.Message);
        }
        finally
        {
            userBackup.Restore();       // 把用户原来的剪贴板还回去
        }


        /* 设置页那两个按钮走的是"复制到系统剪贴板"（不是 Paste）：
         * 在设置窗口里点按钮时前台窗口就是设置窗口自己，Paste 只会往设置页发 Ctrl+V。 */
        var clip = new Core.Actions.ClipboardService();
        clip.ClipboardFeatureOn = true;
        clip.ClearSlot(1);
        Check("空槽位复制不出东西", !clip.CopySlotToSystem(1));
        Check("空文本复制直接拒绝", !clip.CopyTextToSystem(""));
        if (ClipboardPaster.TrySetText(""))
        {
            /* 写剪贴板本身也可能失败：别的程序（输入法、剪贴板工具、截图工具）
             * 会用 OpenClipboard 短暂独占它，这时 CopyTextToSystem 直接返回 false。
             * 这类失败是环境噪声，不是功能坏了 —— 先重试，仍失败再按环境限制跳过。 */
            bool copied = false;
            for (int i = 0; i < 5 && !copied; i++)
            {
                copied = clip.CopyTextToSystem("selftest-copy-slot");
                if (!copied) System.Threading.Thread.Sleep(80);
            }

            uint seqBefore = ClipboardSeq();
            string got = "";
            for (int i = 0; i < 8; i++)
            {
                try { got = Clipboard.GetText(); } catch { got = ""; }
                if (got == "selftest-copy-slot") break;
                System.Threading.Thread.Sleep(60);
            }

            bool takenOver = ClipboardSeq() != seqBefore;

            if (!copied && (takenOver || ClipboardBusy()))
            {
                Check("（环境限制）剪贴板被别的程序占用，跳过复制验证", true);
            }
            else
            {
                Check("复制到系统剪贴板返回成功", copied);

                if (got != "selftest-copy-slot" && takenOver)
                    Check("（环境限制）剪贴板被别的程序改写，跳过内容校验", true);
                else
                    Check("系统剪贴板里确实是那段文字", got == "selftest-copy-slot", got);
            }
        }
        else
        {
            Check("（环境限制）剪贴板被占用，跳过复制验证", true);
        }
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
        WaitUntil(() => { lock (fired) return fired.Contains("qbar"); });
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
        WaitUntil(() => { lock (fired) return fired.Contains("qbar"); });
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

        // ---- 按应用限定 ----
        CheckEqual("片段：进程名规范化补 .exe", SnippetMatcher.Normalize("Code"), "code.exe");
        CheckEqual("片段：进程名规范化转小写", SnippetMatcher.Normalize("  Notepad.EXE "), "notepad.exe");
        CheckEqual("片段：空进程名保持空", SnippetMatcher.Normalize(""), "");

        var scoped = new Snippet { Trigger = ";log", Text = "X", Apps = "code.exe, notepad" };
        Check("片段：列表内命中（写全名）", SnippetMatcher.AllowedIn(scoped, "code.exe"));
        Check("片段：列表内命中（不写 .exe）", SnippetMatcher.AllowedIn(scoped, "Notepad"));
        Check("片段：列表外不命中", !SnippetMatcher.AllowedIn(scoped, "chrome.exe"));

        var global = new Snippet { Trigger = ";g", Text = "Y" };
        Check("片段：没配限定则到处可用", SnippetMatcher.AllowedIn(global, "chrome.exe"));

        /* fail open：拿不到前台进程时不能拦。
         * 否则受保护进程（部分安全软件/游戏）里的片段会一起失效，
         * 而用户完全看不出原因。 */
        Check("片段：拿不到前台进程时放行", SnippetMatcher.AllowedIn(scoped, ""));
        Check("片段：前台进程为 null 时放行", SnippetMatcher.AllowedIn(scoped, null));

        var scopedOnly = new List<Snippet> { scoped };
        CheckEqual("片段：限定的程序里能匹配", SnippetMatcher.Match(scopedOnly, ";log", "code.exe")?.Text, "X");
        Check("片段：其它程序里匹配不到", SnippetMatcher.Match(scopedOnly, ";log", "chrome.exe") == null);
        CheckEqual("片段：不传进程时按旧行为匹配", SnippetMatcher.Match(scopedOnly, ";log")?.Text, "X");
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
    /// <summary>
    /// 宏的变量与循环计数。
    ///
    /// 分两层验证：
    ///   · 纯数据层（展开、比较、名字合法性）—— 出错会静默打错字，必须逐条钉住；
    ///   · 播放层（循环计数真的会走）—— 只测 Expand 证明不了播放器把轮次写进去了。
    /// </summary>
    private static void TestMacroVars()
    {
        var vars = new MacroVars();

        // ---- 名字合法性 ----
        Check("宏变量：普通名字合法", MacroVars.IsValidName("i"));
        Check("宏变量：带下划线与数字合法", MacroVars.IsValidName("n_2"));
        Check("宏变量：空名字不合法", !MacroVars.IsValidName(""));
        Check("宏变量：含空格不合法", !MacroVars.IsValidName("a b"));
        Check("宏变量：含花括号不合法（会和占位符冲突）", !MacroVars.IsValidName("a{b"));
        Check("宏变量：过长不合法",
            !MacroVars.IsValidName(new string('x', MacroVars.MaxNameLength + 1)));

        Check("宏变量：写入成功", vars.TrySet("i", "7"));
        CheckEqual("宏变量：读回", vars.Get("i"), "7");
        CheckEqual("宏变量：名字不区分大小写", vars.Get("I"), "7");
        Check("宏变量：非法名字写入被拒", !vars.TrySet("a b", "1"));
        Check("宏变量：未定义变量读出空串", vars.Get("nope") == "");
        Check("宏变量：空值也算已定义",
            vars.TrySet("empty", "") && vars.Has("empty") && !vars.Has("nope"));

        // ---- 展开 ----
        vars.Set("name", "张三");
        CheckEqual("宏变量：单个占位符", vars.Expand("你好 {name}"), "你好 张三");
        CheckEqual("宏变量：多个占位符", vars.Expand("{name}-{i}"), "张三-7");
        CheckEqual("宏变量：无占位符原样返回", vars.Expand("纯文本"), "纯文本");
        CheckEqual("宏变量：未知占位符原样保留（拼错要看得见）",
            vars.Expand("{nmae}"), "{nmae}");
        CheckEqual("宏变量：未知占位符不影响后面的已知变量",
            vars.Expand("{nmae}{i}"), "{nmae}7");
        CheckEqual("宏变量：左花括号没有右括号时保留", vars.Expand("a{b"), "a{b");
        CheckEqual("宏变量：空模板返回空串", vars.Expand(""), "");

        /* 只展开一层：值里含 `{i}` 时不再展开。
         * 允许递归的话，"变量值里引用自己"会把播放线程卡死。 */
        vars.Set("loop", "{i}");
        CheckEqual("宏变量：值不递归展开", vars.Expand("{loop}"), "{i}");

        // ---- 比较 ----
        vars.Set("i", "3");
        Check("宏变量：数值大于",
            vars.TryCompare("i > 2", out bool gt) && gt);
        Check("宏变量：数值等于（数字不该按字符串比）",
            vars.TryCompare("i = 3", out bool eq) && eq);
        Check("宏变量：两边的变量都能比较（右侧写 {名字}）",
            vars.TrySet("big", "10") && vars.TrySet("small", "9") &&
            vars.TryCompare("big > {small}", out bool numCmp) && numCmp);
        Check("宏变量：大于等于用长运算符",
            vars.TryCompare("i >= 3", out bool ge) && ge);
        Check("宏变量：不等于",
            vars.TryCompare("i != 3", out bool ne) && !ne);
        Check("宏变量：字符串比较",
            vars.TrySet("who", "张三") && vars.TryCompare("who = 张三", out bool strEq) && strEq);
        Check("宏变量：只写名字表示有值",
            vars.TryCompare("who", out bool has) && has);
        Check("宏变量：未定义变量只写名字为假",
            vars.TryCompare("nope", out bool noVal) && !noVal);
        Check("宏变量：空表达式无法求值", !vars.TryCompare("   ", out _));

        // ---- 条件类型接上变量 ----
        var ctx = new MacroContext("", "", "");
        var varStep = new MacroStep { Kind = "if", Cond = MacroCondition.Var, CondValue = "i >= 3" };
        Check("宏变量：条件类型可用", MacroCondition.Evaluate(varStep, ctx, vars));
        varStep.CondValue = "i >= 9";
        Check("宏变量：条件不成立时为假", !MacroCondition.Evaluate(varStep, ctx, vars));
        Check("宏变量：没有变量表时一律不成立",
            !MacroCondition.Evaluate(
                new MacroStep { Kind = "if", Cond = MacroCondition.Var, CondValue = "i >= 0" }, ctx));

        // ---- 静态检查：静默失败要能提前看见 ----
        CheckEqual("宏变量：正常的宏没有提示",
            MacroVars.Audit(new List<MacroStep>
            {
                new() { Kind = "loop", Count = 2, VarName = "i" },
                new() { Kind = "text", Text = "第 {i} 次" },
                new() { Kind = "setVar", VarName = "n", VarValue = "{i}" },
                new() { Kind = "text", Text = "{n}" },
                new() { Kind = "endLoop" },
            }).Count, 0);

        var missingName = MacroVars.Audit(new List<MacroStep>
            { new() { Kind = "setVar", VarName = "", VarValue = "x" } });
        Check("宏变量：没填变量名会提示", missingName.Count == 1, string.Join(" | ", missingName));

        var badName = MacroVars.Audit(new List<MacroStep>
            { new() { Kind = "setVar", VarName = "a b", VarValue = "x" } });
        Check("宏变量：变量名非法会提示", badName.Count == 1, string.Join(" | ", badName));

        var badRef = MacroVars.Audit(new List<MacroStep>
            { new() { Kind = "text", Text = "{nmae}" } });
        Check("宏变量：引用未定义变量会提示", badRef.Count == 1, string.Join(" | ", badRef));
        Check("宏变量：提示里带第几步", badRef.Count == 1 && badRef[0].Contains("第 1 步"), 
            badRef.Count == 1 ? badRef[0] : "");

        var okDefault = MacroVars.Audit(new List<MacroStep>
            { new() { Kind = "text", Text = "第 {i} 次" } });
        CheckEqual("宏变量：默认计数变量 i 不需要定义", okDefault.Count, 0);

        var varCond = MacroVars.Audit(new List<MacroStep>
            { new() { Kind = "if", Cond = MacroCondition.Var, CondValue = "nope > 1" } });
        Check("宏变量：条件里引用未定义变量会提示", varCond.Count == 1, string.Join(" | ", varCond));

        // ---- 克隆要带上新字段 ----
        var src = new MacroStep { Kind = "setVar", VarName = "n", VarValue = "x{i}" };
        var copy = src.Clone();
        Check("宏变量：克隆保留名字与值",
            copy.VarName == "n" && copy.VarValue == "x{i}");

        // ---- 播放：循环计数真的会逐轮推进 ----
        var macro = new Macro
        {
            Name = "自检-变量",
            Steps = new List<MacroStep>
            {
                new() { Kind = "loop", Count = 3, VarName = "i" },
                new() { Kind = "setVar", VarName = "last", VarValue = "{i}" },
                new() { Kind = "endLoop" },
            },
        };

        string? seenI = null;
        string? seenLast = null;
        var done = new System.Threading.ManualResetEventSlim(false);

        using (var player = new MacroPlayer())
        {
            Check("宏变量：能播放",
                player.TryPlay(macro, onCompleted: () =>
                {
                    // 在播放线程上读，避免跨线程可见性问题
                    seenI = player.LastVars?.Get("i");
                    seenLast = player.LastVars?.Get("last");
                    done.Set();
                }));
            Check("宏变量：播放结束", done.Wait(4000));
        }

        CheckEqual("宏变量：循环计数停在最后一轮", seenI, "3");
        CheckEqual("宏变量：循环体里能引用当前轮次", seenLast, "3");

        // 没填计数变量时退回默认名 i，`{i}` 开箱可用
        var defaultVar = new Macro
        {
            Name = "自检-默认计数变量",
            Steps = new List<MacroStep>
            {
                new() { Kind = "loop", Count = 2 },
                new() { Kind = "endLoop" },
            },
        };

        string? fallback = null;
        var done2 = new System.Threading.ManualResetEventSlim(false);
        using (var player = new MacroPlayer())
        {
            player.TryPlay(defaultVar, onCompleted: () =>
            {
                fallback = player.LastVars?.Get(MacroVars.DefaultLoopVar);
                done2.Set();
            });
            done2.Wait(3000);
        }
        CheckEqual("宏变量：没填名字时用默认 i", fallback, "2");
    }

    /// <summary>
    /// 宏的等待条件与重试块。
    ///
    /// 这里必须动**真实的播放线程**：`waitFor` 是唯一会失败的步骤，
    /// 而"失败 → 重试 → 再失败 → 中止并通知"这条链路只在播放线程里成立。
    /// 为了不依赖真实的前台窗口，用 <c>ConditionForTest</c> 注入求值结果。
    /// </summary>
    private static void TestMacroWaitAndRetry()
    {
        static MacroStep S(string kind) => new() { Kind = kind };

        // ---- 配对与归属 ----
        var nested = MacroFlow.Compile(new List<MacroStep>
        {
            S("retry"),                 // 0
            S("retry"),                 // 1
            S("waitFor"),               // 2
            S("endRetry"),              // 3
            S("endRetry"),              // 4
            S("text"),                  // 5
        });
        Check("宏重试：嵌套配对成立", nested.Ok, nested.Error ?? "");
        CheckEqual("宏重试：最内层归属", nested.RetryOf(2), 1);
        CheckEqual("宏重试：外层是更外那层", nested.ParentRetry(1), 0);
        CheckEqual("宏重试：最外层没有父", nested.ParentRetry(0), -1);
        CheckEqual("宏重试：块外没有归属", nested.RetryOf(5), -1);

        Check("宏重试：孤立的结束重试报错",
            !MacroFlow.Compile(new List<MacroStep> { S("endRetry") }).Ok);
        Check("宏重试：未闭合的重试报错",
            !MacroFlow.Compile(new List<MacroStep> { S("retry"), S("text") }).Ok);

        // ---- 等待超时 → 失败事件 ----
        var saved = MacroPlayer.ConditionForTest;
        try
        {
            // ① 没有重试块：直接中止并通知
            MacroPlayer.ConditionForTest = (_, _) => false;

            var noRetry = new Macro
            {
                Name = "自检-等待超时",
                Steps = new List<MacroStep>
                {
                    new() { Kind = "waitFor", Cond = MacroCondition.App, CondValue = "x", TimeoutMs = 100 },
                },
            };

            string? reason = null;
            var done = new System.Threading.ManualResetEventSlim(false);
            using (var player = new MacroPlayer())
            {
                player.Failed += r => { reason = r; };
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Check("宏等待：能开始播放", player.TryPlay(noRetry, onCompleted: () => done.Set()));
                Check("宏等待：超时后结束", done.Wait(4000));
                clock.Stop();

                Check("宏等待：超时不算卡死", clock.ElapsedMilliseconds < 3000, clock.ElapsedMilliseconds.ToString());
                Check("宏等待：发出了失败通知", reason != null, reason ?? "无");
                Check("宏等待：失败原因含超时", (reason ?? "").Contains("超时"), reason ?? "");
                Check("宏等待：LastError 也记下", !string.IsNullOrEmpty(player.LastError));
            }

            // ② 有重试块：应当整体重来，耗时应明显变长
            var withRetry = new Macro
            {
                Name = "自检-重试",
                Steps = new List<MacroStep>
                {
                    new() { Kind = "retry", Count = 3 },
                    new() { Kind = "waitFor", Cond = MacroCondition.App, CondValue = "y", TimeoutMs = 100 },
                    new() { Kind = "endRetry" },
                },
            };

            string? retryReason = null;
            var retryDone = new System.Threading.ManualResetEventSlim(false);
            using (var player = new MacroPlayer())
            {
                player.Failed += r => { retryReason = r; };
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Check("宏重试：能开始播放", player.TryPlay(withRetry, onCompleted: () => retryDone.Set()));
                Check("宏重试：最终结束", retryDone.Wait(6000));
                clock.Stop();

                // 3 次 × 约 100ms：明显长于一次，说明真的重试了
                Check("宏重试：确实重试了（耗时变长）",
                    clock.ElapsedMilliseconds >= 200, clock.ElapsedMilliseconds.ToString());
                Check("宏重试：次数用尽后仍然通知", retryReason != null, retryReason ?? "无");
            }

            // ③ 条件立刻成立：不失败、不等待
            MacroPlayer.ConditionForTest = (_, _) => true;
            var okMacro = new Macro
            {
                Name = "自检-条件成立",
                Steps = new List<MacroStep>
                {
                    new() { Kind = "retry", Count = 3 },
                    new() { Kind = "waitFor", Cond = MacroCondition.App, CondValue = "z", TimeoutMs = 5000 },
                    new() { Kind = "endRetry" },
                },
            };

            string? okReason = null;
            var okDone = new System.Threading.ManualResetEventSlim(false);
            using (var player = new MacroPlayer())
            {
                player.Failed += r => { okReason = r; };
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Check("宏等待：条件成立时能播", player.TryPlay(okMacro, onCompleted: () => okDone.Set()));
                Check("宏等待：条件成立时正常结束", okDone.Wait(4000));
                clock.Stop();

                Check("宏等待：条件成立时不报失败", okReason == null, okReason ?? "");
                Check("宏等待：条件成立时几乎不等待",
                    clock.ElapsedMilliseconds < 2000, clock.ElapsedMilliseconds.ToString());
            }
        }
        finally
        {
            MacroPlayer.ConditionForTest = saved;
        }
    }
    /// <summary>
    /// 宏里的「执行动作」步骤。
    ///
    /// 重点不是"能不能调到"，而是三条边界：
    ///   ① 顺序与参数要对得上（否则"先关窗口再输入"会变成乱序）；
    ///   ② 动作返回 false 就算失败，后续步骤不再执行；
    ///   ③ 失败能被重试块接住，且重试次数与设置一致。
    /// </summary>
    private static void TestMacroActionStep()
    {
        // ---- 克隆要带上动作字段 ----
        var src = new MacroStep { Kind = "action", ActionId = "openUrl", ActionArg = "https://a" };
        var copy = src.Clone();
        Check("宏动作：克隆保留 id", copy.ActionId == "openUrl");
        Check("宏动作：克隆保留参数", copy.ActionArg == "https://a");

        // ---- 顺序与参数 ----
        var calls = new List<string>();
        var two = new Macro
        {
            Name = "自检-动作顺序",
            Steps = new List<MacroStep>
            {
                new() { Kind = "action", ActionId = "closeWindow" },
                new() { Kind = "action", ActionId = "openUrl", ActionArg = "https://b" },
            },
        };

        var done1 = new System.Threading.ManualResetEventSlim(false);
        using (var player = new MacroPlayer())
        {
            player.ActionInvoker = (id, arg) => { lock (calls) calls.Add($"{id}|{arg}"); return true; };
            Check("宏动作：能开始播放", player.TryPlay(two, onCompleted: () => done1.Set()));
            Check("宏动作：正常结束", done1.Wait(4000));
        }

        lock (calls)
        {
            CheckEqual("宏动作：执行了两步", calls.Count, 2);
            CheckEqual("宏动作：第一步 id", calls.Count > 0 ? calls[0].Split('|')[0] : "", "closeWindow");
            CheckEqual("宏动作：第二步参数", calls.Count > 1 ? calls[1].Split('|')[1] : "", "https://b");
        }

        // ---- 返回 false → 失败，后续不执行 ----
        var order = new List<string>();
        var failing = new Macro
        {
            Name = "自检-动作失败",
            Steps = new List<MacroStep>
            {
                new() { Kind = "action", ActionId = "boom" },
                new() { Kind = "action", ActionId = "after" },
            },
        };

        string? reason = null;
        var done2 = new System.Threading.ManualResetEventSlim(false);
        using (var player = new MacroPlayer())
        {
            player.ActionInvoker = (id, _) => { lock (order) order.Add(id); return false; };
            player.ActionTitle = id => id == "boom" ? "会失败的动作" : id;
            player.Failed += r => reason = r;
            Check("宏动作：能开始播放（会失败）", player.TryPlay(failing, onCompleted: () => done2.Set()));
            Check("宏动作：失败后结束", done2.Wait(4000));
        }

        lock (order)
        {
            CheckEqual("宏动作：失败后不再执行后续步骤", order.Count, 1);
            CheckEqual("宏动作：失败的是第一步", order.Count > 0 ? order[0] : "", "boom");
        }
        Check("宏动作：发出失败通知", reason != null, reason ?? "无");
        Check("宏动作：失败原因用动作名", (reason ?? "").Contains("会失败的动作"), reason ?? "");

        // ---- 重试块接住 ----
        int attempts = 0;
        var retried = new Macro
        {
            Name = "自检-动作重试",
            Steps = new List<MacroStep>
            {
                new() { Kind = "retry", Count = 3 },
                new() { Kind = "action", ActionId = "boom" },
                new() { Kind = "endRetry" },
            },
        };

        var done3 = new System.Threading.ManualResetEventSlim(false);
        using (var player = new MacroPlayer())
        {
            player.ActionInvoker = (_, _) => { System.Threading.Interlocked.Increment(ref attempts); return false; };
            Check("宏动作：重试宏能播放", player.TryPlay(retried, onCompleted: () => done3.Set()));
            Check("宏动作：重试宏结束", done3.Wait(4000));
        }

        CheckEqual("宏动作：重试次数与设置一致", attempts, 3);

        // ---- 没有执行器：跳过而不是失败 ----
        var noInvoker = new Macro
        {
            Name = "自检-无执行器",
            Steps = new List<MacroStep> { new() { Kind = "action", ActionId = "openUrl" } },
        };

        string? noReason = null;
        var done4 = new System.Threading.ManualResetEventSlim(false);
        using (var player = new MacroPlayer())
        {
            player.Failed += r => noReason = r;
            Check("宏动作：无执行器也能播", player.TryPlay(noInvoker, onCompleted: () => done4.Set()));
            Check("宏动作：无执行器时正常结束", done4.Wait(4000));
        }
        Check("宏动作：无执行器不算失败", noReason == null, noReason ?? "");
    }
    /// <summary>
    /// 规则体检：必须和引擎里的判定顺序说同一件事。
    ///
    /// 这里每条断言都对应一个真实的行为差异（谁先命中谁生效）：
    /// 组合键 → 双击 → 按应用层 → 基础改键 → 片段；层内重复取最后一条。
    /// 顺序如果变了，这些结论就会变成误报 —— 所以引擎改顺序时这里要一起改。
    /// </summary>
    private static void TestRuleConflicts()
    {
        Check("体检：干净配置没有提示", RuleConflicts.Analyze(new AppSettings()).Count == 0);

        // ---- 触发键被其它规则挡住 ----
        var s1 = new AppSettings();
        s1.Triggers.Add(new TriggerKey { Key = "tab", Enabled = true, LayerId = 1 });
        s1.Global.BaseRemaps.Add(new KeyRemap { From = "tab", To = "esc" });
        var f1 = RuleConflicts.Analyze(s1);
        Check("体检：触发键被改键挡住", f1.Any(c => c.Message.Contains("触发键优先")), string.Join(" | ", f1.Select(c => c.Message)));
        Check("体检：这条指向基础改键卡片",
            f1.Any(c => c.Message.Contains("触发键优先") && c.Target == ConflictTarget.BaseRemap));

        s1.Global.BaseRemaps.Clear();
        s1.Global.KeyGestures.Add(new KeyGesture { Key = "tab", DoubleTapAction = "qbar" });
        Check("体检：触发键上的双击无效", RuleConflicts.Analyze(s1).Any(c => c.Message.Contains("双击动作永远不会触发")));
        Check("体检：这条指向双击卡片",
            RuleConflicts.Analyze(s1).Any(c => c.Message.Contains("双击动作永远不会触发") && c.Target == ConflictTarget.Gesture));

        s1.Global.KeyGestures.Clear();

        // ---- 组合键先于双击 ----
        var s2 = new AppSettings();
        s2.Global.KeyGestures.Add(new KeyGesture { Key = "j", DoubleTapAction = "qbar" });

        // ---- 改键 vs 双击 / 组合键 ----
        var s3 = new AppSettings();
        s3.Global.BaseRemaps.Add(new KeyRemap { From = "a", To = "b" });
        s3.Global.KeyGestures.Add(new KeyGesture { Key = "a", DoubleTapAction = "qbar" });
        Check("体检：同键既改键又双击", RuleConflicts.Analyze(s3).Any(c => c.Message.Contains("第二次按下会被双击吞掉")));

        s3.Global.KeyGestures.Clear();

        // ---- 改键目标是触发键：Info 而非 Warn ----
        var s4 = new AppSettings();
        s4.Triggers.Add(new TriggerKey { Key = "capslock", Enabled = true, LayerId = 1 });
        s4.Global.BaseRemaps.Add(new KeyRemap { From = "a", To = "capslock" });
        var f4 = RuleConflicts.Analyze(s4);
        Check("体检：改键到触发键给提示", f4.Any(c => c.Message.Contains("不会切层")));
        Check("体检：改键到触发键只是提示级", f4.All(c => c.Message.Contains("不会切层") ? c.Level == ConflictLevel.Info : true));

        // ---- 不可识别的键名 ----
        var s5 = new AppSettings();
        s5.Global.BaseRemaps.Add(new KeyRemap { From = "不存在的键", To = "esc" });
        Check("体检：坏键名被指出", RuleConflicts.Analyze(s5).Any(c => c.Message.Contains("不是可识别的键名")));
        Check("体检：坏键名也能定位",
            RuleConflicts.Analyze(s5).Any(c => c.Message.Contains("不是可识别的键名") && c.Target == ConflictTarget.BaseRemap));

        // ---- 层内重复绑定 ----
        var s6 = new AppSettings();
        var layer = new LayerOptions { Id = 2, Name = "开发" };
        string combo = KeyName.BuildComboEx(KeyModifiers.None, "q");
        layer.Keys.Add(new KeyBinding { Combo = combo, Action = "qbar" });
        layer.Keys.Add(new KeyBinding { Combo = combo, Action = "tab" });
        s6.Layers.Add(layer);
        var f6 = RuleConflicts.Analyze(s6);
        Check("体检：层内重复绑定", f6.Any(c => c.Message.Contains("绑了两次")));
        // 层内绑定不在设置页 —— 必须是 Layer，界面对它走换页而不是滚动
        Check("体检：层内重复指向层页面",
            f6.Any(c => c.Message.Contains("绑了两次") && c.Target == ConflictTarget.Layer));

        // ---- 按应用层指向不存在的层 ----
        var s7 = new AppSettings();
        s7.Global.AppLayers.Add(new AppLayerRule { Process = "code.exe", LayerId = 9 });
        Check("体检：按应用层指向不存在的层", RuleConflicts.Analyze(s7).Any(c => c.Message.Contains("不存在或已停用")));
        Check("体检：这条指向按应用层卡片",
            RuleConflicts.Analyze(s7).Any(c => c.Message.Contains("不存在或已停用") && c.Target == ConflictTarget.AppLayer));

        // ---- 片段重复 ----
        var s8 = new AppSettings();
        s8.Global.Snippets.Add(new Snippet { Trigger = ";a", Text = "1" });
        s8.Global.Snippets.Add(new Snippet { Trigger = ";a", Text = "2" });
        Check("体检：片段触发词重复", RuleConflicts.Analyze(s8).Any(c => c.Message.Contains("只有最后一条")));
        Check("体检：这条指向片段卡片",
            RuleConflicts.Analyze(s8).Any(c => c.Message.Contains("只有最后一条") && c.Target == ConflictTarget.Snippet));

        /* 同触发词但限定不同程序是正当配置（一个在 IDE 展开、一个在聊天里展开），
         * 不能报重复 —— 否则用户会去删掉一条本来有用的。 */
        var s8b = new AppSettings();
        s8b.Global.Snippets.Add(new Snippet { Trigger = ";log", Text = "1", Apps = "code.exe" });
        s8b.Global.Snippets.Add(new Snippet { Trigger = ";log", Text = "2", Apps = "notepad.exe" });
        Check("体检：同触发词不同适用范围不算重复",
            !RuleConflicts.Analyze(s8b).Any(c => c.Message.Contains("只有最后一条")));

        // 但写法不同、范围相同（顺序/大小写不同）仍应算重复
        var s8c = new AppSettings();
        s8c.Global.Snippets.Add(new Snippet { Trigger = ";log", Text = "1", Apps = "code.exe, notepad" });
        s8c.Global.Snippets.Add(new Snippet { Trigger = ";log", Text = "2", Apps = "Notepad.exe, Code" });
        Check("体检：范围相同只是写法不同仍算重复",
            RuleConflicts.Analyze(s8c).Any(c => c.Message.Contains("只有最后一条")));

        // ---- 可复制报告 ----
        var sample = new List<Conflict>
        {
            new(ConflictLevel.Warn, "第一条冲突", ConflictTarget.BaseRemap),
            new(ConflictLevel.Info, "第二条提示", ConflictTarget.None),
        };
        string report = RuleConflicts.BuildReport(sample, "HEADER-XYZ");
        Check("体检报告：含头部", report.Contains("HEADER-XYZ"), report);
        Check("体检报告：含总数", report.Contains("共 2 条"), report);
        Check("体检报告：Warn 前缀", report.Contains("[WARN] 第一条冲突"), report);
        Check("体检报告：Info 前缀", report.Contains("[INFO] 第二条提示"), report);

        string emptyReport = RuleConflicts.BuildReport(new List<Conflict>(), "H2");
        Check("体检报告：无冲突时也有结论", emptyReport.Contains("没有发现问题"), emptyReport);

    }

    /// <summary>
    /// 键盘设备枚举（多键盘的第一步：只读，不影响任何按键行为）。
    ///
    /// 两条底线：
    ///   ① 枚举失败绝不能抛 —— 它是展示功能，不能拖累启动；
    ///   ② 路径解析要稳 —— 注册表路径拼错就查不到设备名，界面上全是"其它"。
    /// </summary>
    private static void TestKeyboardDevices()
    {
        // ---- 纯函数：路径解析 ----
        const string hid = @"\\?\HID#VID_046D&PID_C31C&MI_00#8&2f1a2b3c&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";
        CheckEqual("设备：注册表路径",
            KeyboardDevices.RegistryPathOf(hid),
            @"SYSTEM\CurrentControlSet\Enum\HID\VID_046D&PID_C31C&MI_00\8&2f1a2b3c&0&0000");


        Check("设备：ACPI 是内置", KeyboardDevices.IsBuiltIn(@"\\?\ACPI#PNP0303#4&2b3c#0"));
        Check("设备：HID 不是内置", !KeyboardDevices.IsBuiltIn(hid));

        CheckEqual("设备：HID 类别", KeyboardDevices.KindOf(hid), Loc.T("外接键盘（HID）"));
        CheckEqual("设备：ACPI 类别", KeyboardDevices.KindOf(@"\\?\ACPI#PNP0303#4&2b3c#0"),
            Loc.T("内置键盘（ACPI）"));
        CheckEqual("设备：RDP 类别", KeyboardDevices.KindOf(@"\\?\Root#RDP_KBD#0000"),
            Loc.T("远程桌面虚拟键盘"));

        CheckEqual("设备：抠出 VID/PID", KeyboardDevices.VidPidOf(hid), "VID_046D&PID_C31C");
        CheckEqual("设备：没有 VID/PID 时为空", KeyboardDevices.VidPidOf(@"\\?\ACPI#PNP0303#0"), "");
        CheckEqual("设备：空路径不给注册表路径", KeyboardDevices.RegistryPathOf(""), "");

        // ---- 归并：复合键盘不能显示成好几只 ----
        var raw = new List<KeyboardDevice>
        {
            new(@"\\?\HID#VID_0483&PID_2031&MI_00#8&1&0&0000#{g}", "VID_0483&PID_2031",
                Loc.T("外接键盘（HID）"), false),
            new(@"\\?\HID#VID_0483&PID_2031&MI_01&Col01#8&2&0&0000#{g}", "VID_0483&PID_2031",
                Loc.T("外接键盘（HID）"), false),
            new(@"\\?\ACPI#PNP0303#4&2b3c#0", Loc.T("内置键盘（ACPI）"),
                Loc.T("内置键盘（ACPI）"), true),
        };

        var groups = KeyboardDevices.Group(raw);
        CheckEqual("设备：复合键盘归并成一条", groups.Count, 2);
        CheckEqual("设备：归并后计入条目数", groups.First(g => g.Count > 1).Count, 2);
        Check("设备：内置键盘单独一条",
            groups.Any(g => g.Kind == Loc.T("内置键盘（ACPI）")), string.Join(" | ", groups.Select(g => g.Kind)));
        CheckEqual("设备：空输入返回空", KeyboardDevices.Group(null).Count, 0);

        // ---- 真实枚举：能跑通即可 ----
        // List() 的契约是"失败返回空列表、绝不返回 null"，所以这里不判空 ——
        // 判空反而会让编译器以为它可能是 null（CS8604），也掩盖了契约本身。
        var devices = KeyboardDevices.List();
        Check("设备：路径都非空", devices.All(d => d.Path.Length > 0),
            string.Join(" | ", devices.Select(d => d.Path)));

        /* 数量不做硬断言：CI/虚拟机/远程桌面下差异很大。
         * 但把结果打进日志 —— 排查"为什么第二个键盘没生效"时这是第一手资料。 */
        CoreLog.Info($"键盘设备枚举：共 {devices.Count} 个 —— " +
            string.Join("\n  ", devices.Select(d => $"{d.Kind} / {d.Name}\n    {d.Path}")));
    }
    /// <summary>
    /// 按设备区分改键：表结构、优先级、预设切换、设备关联匹配。
    ///
    /// 三条语义必须钉死：
    ///   ① 设备专属规则**不能漏进全局表**（否则一只键盘的规则会影响所有键盘）；
    ///   ② 优先级是"这只键盘 → 全局 → 不改"；
    ///   ③ 关联不上设备时退回全局规则，绝不能猜。
    /// </summary>
    private static void TestDeviceRemap()
    {
        const string devA = "VID_1532&PID_00A4";
        const string devB = "VID_0483&PID_2031";

        // ---- 表结构 ----
        var list = new List<KeyRemap>
        {
            new() { From = "capslock", To = "esc" },                    // 所有键盘
            new() { From = "capslock", To = "lctrl", Device = devA },   // 只对 A
            new() { From = "a", To = "b", Device = devB },              // 只对 B
        };

        var table = KeyRemapMap.BuildTable(list);
        Check("按设备改键：全局表里没有设备规则", table.All.Count == 1, table.All.Count.ToString());
        Check("按设备改键：A 有自己的表", table.ByDevice.ContainsKey(devA));
        Check("按设备改键：设备专属键被记下", table.DeviceScopedKeys.Contains(0x14), "");

        Check("按设备改键：A 上优先设备规则",
            table.TryGet(0x14, devA, out int onA) && onA == 0xA2, "caps");
        Check("按设备改键：B 上退回全局规则",
            table.TryGet(0x14, devB, out int onB) && onB == 0x1B, "caps");
        Check("按设备改键：未知设备也用全局规则",
            table.TryGet(0x14, null, out int onNull) && onNull == 0x1B, "caps");

        // 只有设备规则、没有全局规则时：别的键盘上这个键"不改"
        Check("按设备改键：别的键盘上不生效", !table.TryGet(0x41, devA, out _), "a");

        // ---- 值指纹要带设备，否则改了设备不会重建表 ----
        Check("按设备改键：指纹区分设备",
            KeyRemapMap.Stamp(new[] { new KeyRemap { From = "a", To = "b", Device = devA } }) !=
            KeyRemapMap.Stamp(new[] { new KeyRemap { From = "a", To = "b" } }));

        // ---- 预设切换要按设备独立 ----
        var presets = new List<KeyRemap>();
        var capsEsc = KeyRemapMap.FindPreset("capsEsc");
        Check("按设备改键：预设存在", capsEsc != null);
        if (capsEsc != null)
        {
            Check("按设备改键：先在 A 上启用", KeyRemapMap.Toggle(presets, capsEsc, devA));
            Check("按设备改键：再在所有键盘启用", KeyRemapMap.Toggle(presets, capsEsc, ""));
            CheckEqual("按设备改键：两组规则并存", presets.Count, 4);

            // 关掉 A 的那组，不能把全局那组一起关掉
            Check("按设备改键：关掉 A 的预设", !KeyRemapMap.Toggle(presets, capsEsc, devA));
            CheckEqual("按设备改键：全局那组还在", presets.Count, 2);
            Check("按设备改键：剩下的都是全局规则",
                presets.All(r => KeyRemapMap.NormalizeDevice(r.Device).Length == 0));
        }

        /* 同一只键盘的第二条规则也必须被记入"要问设备"的集合。
         * 漏记的表现是"配了没用"，而这正是这条断言要盯的地方。 */
        var twoOnA = KeyRemapMap.BuildTable(new[]
        {
            new KeyRemap { From = "capslock", To = "esc", Device = devA },
            new KeyRemap { From = "a", To = "b", Device = devA },
        });
        Check("按设备改键：同一键盘的多条规则都记入集合",
            twoOnA.DeviceScopedKeys.Contains(0x14) && twoOnA.DeviceScopedKeys.Contains(0x41),
            string.Join(",", twoOnA.DeviceScopedKeys.Select(v => v.ToString("X2"))));

        // ---- 分享码必须带上设备与片段范围 ----
        var shareSrc = new AppSettings();
        shareSrc.Global.BaseRemaps.Add(new KeyRemap { From = "capslock", To = "esc", Device = devA });
        shareSrc.Global.BaseRemaps.Add(new KeyRemap { From = "capslock", To = "lctrl" });
        shareSrc.Global.Snippets.Add(new Snippet { Trigger = ";log", Text = "X", Apps = "code.exe" });

        string shareCode = RuleShare.Export(shareSrc);
        Check("分享码：导出含设备", shareCode.Contains(devA), shareCode);
        Check("分享码：导出含片段范围", shareCode.Contains("code.exe"), shareCode);

        var shareDst = new AppSettings();
        var (sharedAdded, _) = RuleShare.Import(shareDst, shareCode, out string? shareErr);
        Check("分享码：导入无错误", shareErr == null, shareErr ?? "");
        CheckEqual("分享码：两条改键都导入（同源不同设备）", sharedAdded, 3);
        Check("分享码：设备字段还原",
            shareDst.Global.BaseRemaps.Any(r => KeyRemapMap.NormalizeDevice(r.Device) == devA));
        Check("分享码：片段范围还原",
            shareDst.Global.Snippets.Any(x => x.Trigger == ";log" && x.Apps == "code.exe"));

        // ---- 设备关联：同键同方向 + 时间窗 ----
        long now = 10_000;
        var entries = new List<KeyboardDeviceTracker.Entry>
        {
            new(devA, 0x14, true, now - 5),
            new(devB, 0x14, true, now - 400),      // 太旧，不该匹配
        };
        CheckEqual("设备关联：取最近的一条",
            KeyboardDeviceTracker.MatchRecent(entries, 0x14, true, now), 0);
        CheckEqual("设备关联：方向不同不匹配",
            KeyboardDeviceTracker.MatchRecent(entries, 0x14, false, now), -1);
        CheckEqual("设备关联：键不同不匹配",
            KeyboardDeviceTracker.MatchRecent(entries, 0x41, true, now), -1);

        var stale = new List<KeyboardDeviceTracker.Entry> { new(devA, 0x14, true, now - 500) };
        CheckEqual("设备关联：超时不匹配",
            KeyboardDeviceTracker.MatchRecent(stale, 0x14, true, now), -1);

        /* ---- 改键的按下/抬起必须成对 ----
         * 用注入钩子拦下来看，否则自检会真的往系统里发键。
         * 重点验"设备专属规则在抬起时查表会落空"这个坑。 */
        var sent = new List<string>();
        var savedSender = InputSender.SenderForTest;
        InputSender.SenderForTest = (vk, up) => { lock (sent) sent.Add($"{vk:X2}{(!up ? "↓" : "↑")}"); };

        try
        {
            var s10 = DefaultSettings.Create();
            foreach (var t in s10.Triggers) t.Enabled = false;
            s10.Global.BaseRemaps.Clear();
            s10.Global.BaseRemaps.Add(new KeyRemap { From = "capslock", To = "esc", Device = devA });

            var queue10 = new ActionQueue();
            var engine10 = new HotkeyEngine(queue10, () => s10, (_, _) => true)
            {
                DeviceResolver = (_, _) => devA,
            };

            lock (sent) sent.Clear();
            Check("改键：设备规则按下被接管", engine10.SimulateKey(0x14, true));
            Check("改键：设备规则抬起也被接管", engine10.SimulateKey(0x14, false));
            WaitUntil(() => { lock (sent) return sent.Count >= 2; });

            lock (sent)
            {
                CheckEqual("改键：按下注入 Esc", sent.FirstOrDefault() ?? "", "1B↓");
                CheckEqual("改键：抬起注入 Esc 抬起（不卡键）", sent.LastOrDefault() ?? "", "1B↑");
            }

            // 拿不到设备时不该改键（fail open 到"全局规则"，而这里没有全局规则）
            engine10.DeviceResolver = (_, _) => null;
            lock (sent) sent.Clear();
            Check("改键：设备未知时不改键", !engine10.SimulateKey(0x14, true), "caps");
            engine10.SimulateKey(0x14, false);
            WaitUntil(() => { lock (sent) return sent.Count > 0; });

            engine10.Dispose();
            queue10.Dispose();
        }
        finally
        {
            InputSender.SenderForTest = savedSender;
        }

        // ---- 设备标识一致性 ----
        CheckEqual("设备标识：VID/PID 优先",
            KeyboardDevices.KeyOf(@"\\?\HID#VID_1532&PID_00A4&MI_01#x#{g}"), devA);
        CheckEqual("设备标识：没有 VID/PID 用路径",
            KeyboardDevices.KeyOf(@"\\?\ACPI#PNP0303#0"), @"\\?\ACPI#PNP0303#0");

        // ---- 体检：规则指向的键盘不在线 ----
        var offline = new AppSettings();
        offline.Global.BaseRemaps.Add(new KeyRemap { From = "capslock", To = "esc", Device = devA });
        Check("按设备改键：设备不在线会被指出",
            RuleConflicts.Analyze(offline, new[] { devB })
                .Any(c => c.Message.Contains("现在没接上")));
        Check("按设备改键：设备在线时不报",
            !RuleConflicts.Analyze(offline, new[] { devA })
                .Any(c => c.Message.Contains("现在没接上")));
        Check("按设备改键：不传设备清单就不检查",
            !RuleConflicts.Analyze(offline).Any(c => c.Message.Contains("现在没接上")));
    }
}
