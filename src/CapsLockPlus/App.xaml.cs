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

        // 无控制台时 Console.WriteLine 会抛 IOException（"can't open file for write"），
        // 这里统一兜住，避免诊断命令打出无意义的噪声。
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);

        // 需要管理员权限才能操作提权窗口；普通启动时自动提权一次
        if (ElevationHelper.TryRelaunchElevated(e.Args))
        {
            Shutdown(0);
            return;
        }

        if (!SingleInstance.TryAcquire(out bool running))
        {
            SingleInstance.ActivateExisting();
            Shutdown(0);
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
            Host.Notify($"已更新到 {UpdateService.CurrentVersion}");
        }
        // 自动更新：延迟几秒再检查，避免和首屏抢资源
        Host.ScheduleUpdateCheck();
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
        try { Host?.Dispose(); } catch { }
        SingleInstance.Release();
        base.OnExit(e);
    }
}
