using System.Windows;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Update;
using CapsLockPlus.UI;
using CapsLockPlus.UI.Theme;

namespace CapsLockPlus;

public partial class App : Application
{
    public static AppHost Host { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CoreLog.Error("未处理异常", args.ExceptionObject as Exception);

        bool isSelfTest = e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        bool isDiag = e.Args.Any(a => a.Equals("--diag", StringComparison.OrdinalIgnoreCase));
        bool isHwTest = e.Args.Any(a => a.Equals("--hwtest", StringComparison.OrdinalIgnoreCase));

        /* 测试类模式一律"干跑"：只走逻辑，不真的往系统里发按键。
         *
         * 这些流程会经过「输入文本」「播放宏」「复制」这些真实动作 ——
         * 一旦真发出去，就等于往用户当时的前台窗口里打字。
         * 曾经在 `--selftest` 里漏过一次：屏幕上突然刷出两千个 a，
         * 最后还跟了一句 hi（宏播放测试干的）。
         *
         * 放在最前面，任何模式分支之前，避免以后新加开关又忘。 */
        if (e.Args.Any(a =>
                a.Equals("--selftest", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--render", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--render-diag", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--diag", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--webdav-test", StringComparison.OrdinalIgnoreCase)))
        {
            Core.Input.InputSender.DryRun = true;
            CoreLog.Info("测试模式：注入干跑（不会真的发送按键）");
        }

        // 诊断模式：不走 WPF 的消息循环，直接跑完用 Environment.Exit 硬退出。
        // 用 Shutdown() 依赖消息循环正常运转，一旦中途抛异常就会永远等下去
        // （之前 %TEMP% 不可写时就出现过命令行挂死）。
#if NO_DIAGNOSTICS
        // 精简发布（-p:IncludeDiagnostics=false）：自检没编进来，给出明确提示
        if (isSelfTest)
        {
            CoreLog.Warn("这个版本不含自检（精简发布）。完整版可从 Releases 下载。");
            Environment.Exit(3);
            return;
        }
#else
        if (isSelfTest)
        {
            int code;
            try
            {
                code = SelfTest.Run();
            }
            catch (Exception ex)
            {
                CoreLog.Error("自检异常", ex);
                code = 2;
            }
            Environment.Exit(code);
            return;
        }
#endif

        /* 诊断：打开一个文件夹并报告它有没有到前台。
         *
         * 用法：KeyboardEnhance.exe --reveal "D:\某个文件夹"
         *
         * 为什么要这个入口：用户在后台按了直达文件夹，窗口却压在任务栏 ——
         * 这类问题只能"真的开一次、看前台是谁"才能确认。
         * 有了它就不必去注入按键（那会误触正在用的实例的绑定）。
         * 结果同时写到 reveal.txt，方便在没有控制台时查看。 */
        int revealIndex = Array.FindIndex(e.Args,
            a => a.Equals("--reveal", StringComparison.OrdinalIgnoreCase));
        if (revealIndex >= 0 && revealIndex + 1 < e.Args.Length)
        {
            string target = e.Args[revealIndex + 1];
            var lines = new List<string>();

            try
            {
                var beforeFg = Core.Windows.WindowService.ForegroundInfo();
                lines.Add($"打开前的前台：{beforeFg?.Title} [{beforeFg?.ClassName}] {beforeFg?.ExeName}");

                bool isUrl = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                             || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                bool isFolder = !isUrl && Directory.Exists(target);
                bool isFile = !isUrl && !isFolder && File.Exists(target);
                lines.Add($"目标：{target}（网址={isUrl} 文件夹={isFolder} 文件={isFile}）");

                if (!isUrl && !isFolder && !isFile)
                {
                    lines.Add("结论：目标不存在，无法测试");
                }
                else
                {
                    /* 预期程序：
                     *   网址 → 默认浏览器（注册表 UserChoice）
                     *   文件夹 → explorer
                     *   文件 → 文件关联（AssocQueryString） */
                    string expectExe = isUrl
                        ? Core.Windows.WindowService.DefaultBrowserExe()
                        : isFolder ? "explorer.exe"
                        : Core.Windows.WindowService.AssociatedExe(target);
                    string expectName = expectExe.Length > 0 ? Path.GetFileName(expectExe) : "";
                    lines.Add($"预期程序：{(expectName.Length > 0 ? expectName : "(没能解析)")}");

                    if (isUrl)
                        Core.Windows.WindowService.LaunchInForeground(
                            target, Core.Windows.WindowService.ShellTargetKind.Url);
                    else if (isFolder)
                        Core.Windows.WindowService.OpenFolderInForeground(target);
                    else
                        Core.Windows.WindowService.LaunchInForeground(
                            target, Core.Windows.WindowService.ShellTargetKind.File);

                    Thread.Sleep(3200);       // 等 shell 启动 + 置前重试

                    var fg = Core.Windows.WindowService.ForegroundInfo();
                    lines.Add($"打开后的前台：{fg?.Title} [{fg?.ClassName}] {fg?.ExeName}");

                    bool onTop = fg != null &&
                        (isFolder
                            ? string.Equals(fg.ClassName, "CabinetWClass", StringComparison.OrdinalIgnoreCase)
                            : expectName.Length > 0 &&
                              Core.Windows.WindowService.SameExeName(fg.ExeName, expectName));
                    lines.Add(onTop
                        ? $"结论：{(isFolder ? "资源管理器" : expectName)} 已在前台 ✓"
                        : "结论：前台不是目标窗口 ✗");
                }
            }
            catch (Exception ex)
            {
                lines.Add("异常：" + ex);
            }

            foreach (string line in lines) Console.WriteLine(line);
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.DataDir, "reveal.txt"),
                    string.Join(Environment.NewLine, lines));
            }
            catch { /* 预期内：探测/重试型，失败即视为无结果 */ }

            Environment.Exit(0);
            return;
        }

        /* 诊断：只显示一次亮度横条，停几秒后退出。
         *
         * 用法：KeyboardEnhance.exe --osd 45
         *
         * 为什么需要它：横条的位置只能"真的显示出来再量"。
         * 用户报"比系统音量条高一截"时，铺一块纯黑全屏窗体再跑这个，
         * 截图量卡片边缘，就能和系统那条逐像素对齐（见 tools/measure_osd_dark.py）。
         * 顺带也验证 Place() 走的物理像素 + DPI 那条路径。 */
        int osdIndex = Array.FindIndex(e.Args,
            a => a.Equals("--osd", StringComparison.OrdinalIgnoreCase));
        if (osdIndex >= 0)
        {
            int percent = int.TryParse(e.Args.ElementAtOrDefault(osdIndex + 1), out int pv)
                ? Math.Clamp(pv, 0, 100) : 50;

            /* --osd-hold <毫秒>：横条停留多久（默认 1.9 秒，和系统一致）。
             *
             * 只给测试用：验证"永远在最上层"时，要在横条还亮着的时候
             * 把另一个 topmost 窗口压上去、截图、再等它自己抢回来。
             * 1.9 秒不够做这几步（见 tools/test_osd_topmost.py）。 */
            int holdIndex = Array.FindIndex(e.Args,
                a => a.Equals("--osd-hold", StringComparison.OrdinalIgnoreCase));
            if (holdIndex >= 0 &&
                int.TryParse(e.Args.ElementAtOrDefault(holdIndex + 1), out int holdMs))
                UI.Windows.OsdWindow.HoldOverrideMs = Math.Clamp(holdMs, 500, 60000);

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            UI.Windows.OsdWindow.Show(percent);

            int lifeMs = Math.Max(3000, UI.Windows.OsdWindow.HoldOverrideMs + 1200);
            var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(lifeMs) };
            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                Shutdown();
            };
            closeTimer.Start();
            return;
        }

        /* 硬件自检：真的调一次音量与亮度再还原。
         * 用来区分"代码没生效"和"这台设备不支持" —— 用户报
         * 音量/亮度按键没反应时，让他跑这个看输出。 */
        if (isHwTest)
        {
            int code;
            try
            {
                code = Core.Windows.HardwareProbe.Run();
            }
            catch (Exception ex)
            {
                CoreLog.Error("硬件自检异常", ex);
                code = 2;
            }
            Environment.Exit(code);
            return;
        }

        if (isDiag)
        {
            int code;
            try
            {
                code = Core.KeyDiagnostics.Run();
            }
            catch (Exception ex)
            {
                CoreLog.Error("诊断异常", ex);
                code = 2;
            }
            Environment.Exit(code);
            return;
        }

        // WebDAV 真实往返测试：--webdav-test <地址> [用户名] [密码]
        // 用来在本地起一个 stub 服务器后，端到端验证上传/下载/比时间这条链路。
        int wdIndex = Array.FindIndex(e.Args,
            a => a.Equals("--webdav-test", StringComparison.OrdinalIgnoreCase));
        if (wdIndex >= 0)
        {
            int code;
            try
            {
                code = Core.Sync.WebDavSelfTest.Run(
                    e.Args.ElementAtOrDefault(wdIndex + 1) ?? "",
                    e.Args.ElementAtOrDefault(wdIndex + 2) ?? "",
                    e.Args.ElementAtOrDefault(wdIndex + 3) ?? "");
            }
            catch (Exception ex)
            {
                CoreLog.Error("WebDAV 测试异常", ex);
                code = 2;
            }
            Environment.Exit(code);
            return;
        }

        // 批量把 SVG 图标渲染成 PNG（构建期工具：把外部图标库转成本程序可用的位图）
        int svgIndex = Array.FindIndex(e.Args, a => a.Equals("--svg2png", StringComparison.OrdinalIgnoreCase));
        if (svgIndex >= 0 && svgIndex + 2 < e.Args.Length)
        {
            int code;
            try
            {
                code = Core.Icons.SvgBatch.Convert(e.Args[svgIndex + 1], e.Args[svgIndex + 2], 64);
            }
            catch (Exception ex)
            {
                CoreLog.Error("SVG 批量转换异常", ex);
                code = 2;
            }
            Environment.Exit(code);
            return;
        }

        // 无控制台时 Console.WriteLine 会抛 IOException（"can't open file for write"），
        // 这里统一兜住，避免诊断命令打出无意义的噪声。
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);

        // ---- 单实例：提权实例同样必须持有锁 ----
        //
        // 顺序：先判定"是否已有实例"，再决定要不要提权重启。
        // 反过来会出现竞态：旧进程发起 runas 后立即退出，新进程同时启动，
        // 旧进程还没释放互斥体 → 新进程误判"已有实例"而退出 → 两个进程都没了。
        //
        // 注意：提权实例_也_必须获取互斥体。
        // 否则它不持锁，之后再启动一个普通实例就能成功拿到锁并又拉起一个提权实例，
        // 结果是同一时间跑着好几个实例。
        if (!SingleInstance.TryAcquire(out bool running))
        {
            CoreLog.Info("已有实例在运行，唤醒它并退出");
            SingleInstance.ActivateExisting();
            Environment.Exit(0);
            return;
        }

        /* 是否提权，**看用户设置**，不再无条件自动提权。
         *
         * ⚠ 之前这里是无条件 `TryRelaunchElevated`，等于强制所有用户都以
         * 管理员身份运行。副作用是**取词功能全废**：
         * 管理员进程向普通权限的浏览器 / 编辑器注入输入会被 UIPI 拦掉，
         * 跨进程 UIA 查询也被拒 —— 用户在任何程序里都取不到选区。
         *
         * 现在只有用户明确在「设置」里勾了"以管理员身份运行"才提权。
         * 默认普通权限运行，取词、窗口绑定等全都正常；
         * 确实需要操作提权窗口时再手动开。 */
        /* 本地数据加密：先解锁再读设置。
         *
         * 日常路径走 Windows DPAPI 缓存的密钥，**不会**弹密码框；
         * 只有缓存失效（换机器 / 换账户 / 删了 vault.cache）才问一次。
         * 用户选择不解锁时 SettingsStore 进入只读保护，
         * 不会用空白配置覆盖掉那份还等着解锁的数据。 */
        EnsureVaultUnlocked();

        bool relaunched = false;
        try
        {
            var s = SettingsStore.Load();
            if (s.Global.RunAsAdmin)
                relaunched = ElevationHelper.TryRelaunchElevated(e.Args);
            else
                CoreLog.Info("以普通权限运行（设置里未开启管理员模式）");
        }
        catch (Exception ex)
        {
            CoreLog.Error("提权重启失败，将按普通权限继续", ex);
        }
        if (relaunched)
        {
            CoreLog.Info("已拉起提权实例，当前进程退出");
            SingleInstance.Release();   // 让提权实例能顺利接管
            Environment.Exit(0);
            return;
        }

        var settings = SettingsStore.Load();

        /* 老用户迁移：已经设过备份密码、但本地数据还是明文 → 立刻加密。
         *
         * 升级前"备份密码"只管云端备份文件，本地 settings.json 一直是明文 JSON。
         * 用户既然设过密码，就说明他期望"这些数据是加密的" ——
         * 这里补上这一步，不需要他再跑一遍设置页去重新输入。
         * 密码为空（用户主动关掉加密）时不启用，保持明文。 */
        MigrateVaultIfPasswordSet(settings);

        ThemeManager.Apply(ThemeManager.Parse(settings.Global.Theme));

        /* ---- 启动密码门：密码不对，软件就不启动 ----
         *
         * 位置是刻意的：在提权重启、数据解锁、主题应用之后，
         * 建 Host（钩子 / 托盘 / 主窗口）之前 —— 校验不过就到此为止，
         * 键盘钩子都不会装，更不会出现"界面拦下来了、热键还能用"的半开状态。
         *
         * 校验真相在数据目录的 startup.json（见 StartupPassword），
         * 与备份密码完全独立：那一个管数据加密，这一个只卡启动。
         * 忘记密码的退路 = 删掉 startup.json（其他设置不受影响）。 */
        if (!StartupPassword.ShouldSkip(e.Args) && StartupPassword.IsSet())
        {
            StartupPassword.MirrorTo(settings.Security);   // 镜像与文件对齐（用户可能手动删过）

            if (!AskStartupPassword(settings))
            {
                CoreLog.Info("启动密码校验未通过 —— 本次启动终止");
                try { CoreLog.Flush(); } catch { }
                SingleInstance.Release();
                Environment.Exit(0);
                return;
            }
            CoreLog.Info("启动密码校验通过");
        }

        Host = new AppHost(settings, Dispatcher);
        Host.Start();

        // 离屏渲染（文档 / 回归用），渲染完直接退出
#if NO_DIAGNOSTICS
        int renderIndex = -1;
#else
        int renderIndex = Array.FindIndex(e.Args, a => a.Equals("--render", StringComparison.OrdinalIgnoreCase));
#endif
        if (renderIndex >= 0)
        {
#if !NO_DIAGNOSTICS
            // --render-diag 额外输出输入框布局诊断，用于排查文字被裁切
            RenderTools.DiagnosticsRequested =
                e.Args.Any(a => a.Equals("--render-diag", StringComparison.OrdinalIgnoreCase));
            string outDir = renderIndex + 1 < e.Args.Length
                ? e.Args[renderIndex + 1]
                : Path.Combine(AppPaths.DataDir, "render");
            int renderCode;
            try
            {
                renderCode = RenderTools.Run(Host, outDir);
            }
            catch (Exception ex)
            {
                CoreLog.Error("离屏渲染异常", ex);
                renderCode = 2;
            }
            Environment.Exit(renderCode);
#else
            Environment.Exit(3);
#endif
            return;
        }

        bool startHidden = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        string? page = ParsePageArg(e.Args);
        bool justUpdated = e.Args.Any(a => a.Equals("--updated", StringComparison.OrdinalIgnoreCase));

        // 诊断：--trace 会把钩子收到的每个按键写进日志，用于排查“按键没反应”
        if (e.Args.Any(a => a.Equals("--trace", StringComparison.OrdinalIgnoreCase)))
        {
            Host.EnableKeyTrace();
            CoreLog.Info("已开启按键追踪（--trace）");
        }

        // 清理上次更新留下的 .old-*.exe
        UpdateService.CleanupLeftovers();

        if (e.Args.Any(a => a.Equals("--qbar", StringComparison.OrdinalIgnoreCase)))
        {
            Host.ShowQbar(e.Args.FirstOrDefault(x => x.StartsWith("--text="))?["--text=".Length..]);
        }
        else if (page != null)
        {
            Host.ShowMainWindow(page);
        }
        else if (!startHidden && (settings.Global.OpenWindowOnStartup || !settings.Global.OnboardingDone))
        {
            Host.ShowMainWindow();
        }
        else if (!settings.Global.ShowTrayIcon)
        {
            Host.ShowMainWindow();
        }

        if (justUpdated)
        {
            // “已更新到 x.x.x”只在用户没关掉提示时才弹。
            // 开机自启场景下每次更新都弹一次通知很烦，所以把它做成可选项。
            if (settings.Global.ShowUpdateToast)
                Host.Notify($"已更新到 {UpdateService.CurrentVersion}");
            else
                CoreLog.Info($"已更新到 {UpdateService.CurrentVersion}（提示已在设置中关闭）");
        }
        // 自动更新：延迟几秒再检查，避免和首屏抢资源
        Host.ScheduleUpdateCheck();

        // 启动同步：延后 4 秒，等界面和钩子都就绪再联网。
        // 同步本身在后台线程跑，不阻塞界面。
        Host.ScheduleStartupSync();

        // 自动备份：与"启动/退出同步"独立，两个开关可以同时开着互不干扰。
        Host.RestartAutoBackupTimer();
    }

    /// <summary>
    /// 弹启动密码窗，返回用户是否给出了正确密码。
    ///
    /// 打不开窗口时返回 false（不许进）：宁可这次进不去，
    /// 也不能把"弹窗失败"当成"没有密码"悄悄放行。
    /// </summary>
    private static bool AskStartupPassword(AppSettings settings)
    {
        try
        {
            var dlg = new UI.Windows.StartupPasswordWindow(
                p => Core.Settings.StartupPassword.Verify(p),
                "启动密码",
                "这个软件已设置启动密码，请输入密码进入主界面。\n\n" +
                "启动密码与备份密码相互独立：这一个只决定谁能打开本软件，" +
                "改了它也不会影响备份与数据加密。",
                "进入", "退出");
            dlg.ShowDialog();
            return dlg.Passed;
        }
        catch (Exception ex)
        {
            CoreLog.Error("启动密码窗口打开失败", ex);
            return false;
        }
    }

    /// <summary>支持 --page=keys 这样的参数，直接打开某个页面（也方便做截图验收）。</summary>
    private static string? ParsePageArg(string[] args)
    {
        foreach (var a in args)
        {
            if (a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
                return a["--page=".Length..].Trim().ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// 已经设过备份密码、但本地还没加密 → 自动迁移成密文。
    ///
    /// 只做一次：迁移后 vault.json 存在，<see cref="DataVault.IsEnabled"/> 为 true，
    /// 后续启动直接跳过。
    /// </summary>
    private static void MigrateVaultIfPasswordSet(AppSettings settings)
    {
        try
        {
            if (DataVault.IsEnabled) return;

            string pass = settings.Sync.BackupPassword ?? "";
            if (string.IsNullOrWhiteSpace(pass)) return;

            DataVault.Enable(pass);
            SettingsStore.Save(settings);
            CoreLog.Info("[加密] 检测到已有备份密码，已自动把本地数据转为密文");
        }
        catch (Exception ex)
        {
            CoreLog.Error("自动启用本地加密失败", ex);
        }
    }

    /// <summary>
    /// 确保加密的本地数据已解锁（只在启用加密时有意义）。
    ///
    /// 顺序：DPAPI 缓存 → 密码输入框 → 放弃（只读保护）。
    /// 自检模式不走弹窗，否则跑 `--selftest` 会卡在对话框上。
    /// </summary>
    private static void EnsureVaultUnlocked()
    {
        if (!DataVault.IsEnabled || DataVault.IsUnlocked) return;

        // 日常路径：Windows 凭据缓存的密钥，不用输密码
        if (DataVault.TryUnlockCached()) return;

        if (Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            CoreLog.Warn("自检模式：跳过密码输入，设置以默认值运行");
            return;
        }

        try
        {
            var dlg = new UI.Windows.PasswordPromptWindow();
            if (dlg.ShowDialog() == true) return;
            CoreLog.Warn("用户未解锁本地数据 —— 进入只读保护，不会覆盖原文件");
        }
        catch (Exception ex)
        {
            CoreLog.Error("解锁窗口打开失败", ex);
        }
    }

    private void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CoreLog.Error("界面线程异常", e.Exception);
        e.Handled = true;
        Host?.Notify("出错了：" + e.Exception.Message);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出同步必须放在 Dispose 之前：那之后钩子和设置都拆了。
        // SyncOnExit 内部有 8 秒硬上限，网盘不通也不会卡住关软件。
        try
        {
            var r = Host?.RunExitSync();
            if (r is { Ok: false })
                CoreLog.Warn("退出同步未完成：" + r.Message);
        }
        catch (Exception ex) { CoreLog.Warn("退出同步异常：" + ex.Message); }

        try { Host?.Dispose(); } catch { }   /* 预期内：清理动作，失败无副作用 */

        /* 收尾：把防抖中的设置落盘、把日志缓冲刷出去。
         * 顺序放在 Dispose 之后 —— Dispose 里可能还会写日志。 */
        try { SettingsStore.FlushPending(); } catch (Exception ex) { CoreLog.Warn("退出前保存设置失败：" + ex.Message); }
        try { CoreLog.Flush(); CoreLog.Shutdown(); } catch { }   /* 预期内：清理动作，失败无副作用 */

        SingleInstance.Release();
        base.OnExit(e);
    }
}
