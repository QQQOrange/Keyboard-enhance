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

        // 诊断模式：不走 WPF 的消息循环，直接跑完用 Environment.Exit 硬退出。
        // 用 Shutdown() 依赖消息循环正常运转，一旦中途抛异常就会永远等下去
        // （之前 %TEMP% 不可写时就出现过命令行挂死）。
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
        ThemeManager.Apply(ThemeManager.Parse(settings.Global.Theme));

        Host = new AppHost(settings, Dispatcher);
        Host.Start();

        // 离屏渲染（文档 / 回归用），渲染完直接退出
        int renderIndex = Array.FindIndex(e.Args, a => a.Equals("--render", StringComparison.OrdinalIgnoreCase));
        if (renderIndex >= 0)
        {
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

        try { Host?.Dispose(); } catch { }
        SingleInstance.Release();
        base.OnExit(e);
    }
}
