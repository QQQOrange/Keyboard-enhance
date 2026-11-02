using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core;

/// <summary>
/// 钩子宿主进程（<c>--hookhost &lt;管道名&gt;</c>）。
///
/// 为什么要独立进程：
///   实测同一台机器上，**最小进程**里的低级键盘钩子 6/6 正常、跑几十秒也不掉；
///   而完整程序（带云同步、自动备份、界面渲染）里，钩子会**间歇性收不到任何按键**，
///   句柄还在、线程还活着、状态全正常。
///   根因是低级钩子是**托管回调**：进程里任何一次 GC 停顿（启动期同步/渲染最容易触发）
///   都会让回调线程停住，超过系统限制（默认 300ms）后 Windows 直接摘钩并丢弃输入。
///   这个进程什么都不做，只装钩子，就不会被别的功能拖累。
///
/// 分工：
///   · 这里负责：装钩子、跑 <see cref="HotkeyEngine"/>（决定吞不吞键）、把动作名通过管道发出去；
///   · 主程序负责：执行动作（打开 Qbar、调音量……）、界面、同步、备份。
///
/// 管道协议（每行一条，UTF-8）：
///   <c>ACT\t动作名\t参数</c> —— 请主程序执行一个动作
///   <c>READY</c>              —— 宿主已装好钩子、开始工作
///   <c>BYE</c>                —— 宿主即将退出
/// 主程序 → 宿主：<c>RELOAD</c> 让宿主重新读配置。
/// </summary>
internal static class HookHost
{
    public static int Run(string pipeName, int parentPid = 0)
    {
        var log = new StringBuilder();
        void Trace(string s) { log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }

        NamedPipeClientStream? pipe = null;
        StreamWriter? writer = null;

        try
        {
            // 1) 连上主程序的管道（主程序可能刚起，最多等 5 秒）
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            pipe.Connect(5000);
            writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            Trace("管道已连接");
        }
        catch (Exception ex)
        {
            // 连不上就不干活：主程序会退回"自己装钩子"
            Trace("连接主程序失败：" + ex.Message);
            TryWriteReport(log);
            return 2;
        }

        var settings = SettingsStore.Load();
        var queue = new ActionQueue("HookHost.Actions");
        HotkeyEngine? engine = null;

        /* 写管道要加锁：引擎回调线程（ACT/KEY/TRIG）和主循环线程（LAYER）会并发写，
         * StreamWriter 不是线程安全的，不加锁会互相踩（表现为"界面偶尔收不到按键"）。 */
        var pipeGate = new object();

        void Forward(string action, string arg)
        {
            try { lock (pipeGate) writer?.WriteLine($"ACT\t{action}\t{arg}"); }
            catch { /* 主程序没了也不影响吞键 */ }
        }

        engine = new HotkeyEngine(queue, () => settings, (action, arg) => { Forward(action, arg); return true; });

        /* 把按键 / 触发键状态回传给主程序：「测试」页靠它显示实时流水与当前层。
         * 这里只做显示，吞键决策全在宿主这边。 */
        engine.KeySeen += (vk, down, _) =>
        {
            try { lock (pipeGate) writer?.WriteLine($"KEY\t{vk}\t{(down ? 1 : 0)}"); } catch { }
        };
        engine.TriggerHeldChanged += held =>
        {
            try { lock (pipeGate) writer?.WriteLine($"TRIG\t{(held ? 1 : 0)}\t{engine.ActiveLayer}\t{engine.ActiveTriggerKey ?? ""}"); } catch { }
        };

        /* 配置改动靠**文件修改时间**发现：主程序每次保存都会重写 settings.json，
         * 这里每秒看一眼有没有变，变了就重读。比再开一条控制管道简单也更稳。 */
        DateTime settingsStamp = SafeStamp();
        long lastReloadLog = 0;
        int lastLayer = -1;

        engine.Start();

        /* 宿主没有 WPF 消息循环，引擎里的 DispatcherTimer 不会跑 ——
         * 必须自己驱动长按判定，否则「按住触发键不放」永远进不了层。 */
        using var holdTimer = new Timer(_ =>
        {
            try { engine.TickHold(); } catch { }
        }, null, 20, 20);
        try { writer.WriteLine("READY"); } catch { }

        /* 钩子没装上要立刻告诉主程序：主程序据此决定是否改用普通权限重启
         * （实测：管理员权限下钩子可能装不上，普通权限下同一台机器是正常的）。 */
        if (!engine.IsHookAlive)
        {
            try { writer.WriteLine("HOOKFAIL"); } catch { }
            Trace("钩子未生效，已通知主程序");
        }
        Trace($"引擎已启动，钩子={(engine.IsHookAlive ? "正常" : "未生效")}");

        // 2) 主循环：配置文件变了就重读
        while (true)
        {
            Thread.Sleep(100);

            var stamp = SafeStamp();
            if (stamp != settingsStamp && stamp != default)
            {
                settingsStamp = stamp;
                try
                {
                    settings = SettingsStore.Load();
                    Trace("已重读配置");
                    if (Environment.TickCount64 - lastReloadLog > 5000)
                    {
                        lastReloadLog = Environment.TickCount64;
                        CoreLog.Info("钩子宿主：已重读配置（设置文件有更新）");
                    }
                }
                catch { /* 配置读不了就继续用旧的 */ }
            }

            // 层变化：每秒比对一次，变了就回传（界面靠它显示"当前层"）
            int layerNow = engine.ActiveLayer;
            if (layerNow != lastLayer)
            {
                lastLayer = layerNow;
                try { lock (pipeGate) writer?.WriteLine($"LAYER\t{layerNow}"); } catch { }
            }

            /* 主程序没了就自尽，避免留孤儿进程占着钩子。
             *
             * 不能只看 pipe.IsConnected —— NamedPipeClientStream 的这个属性
             * 在对方被杀掉之后仍会返回 true（实测），于是宿主会一直活着、
             * 和下一次启动的主程序抢同一个键盘钩子。改成直接看父进程是否还在。 */
            if (parentPid > 0)
            {
                try
                {
                    if (Process.GetProcessById(parentPid).HasExited) break;
                }
                catch { break; }        // 进程不存在 = 已退出
            }
        }

        try { writer?.WriteLine("BYE"); } catch { }
        engine.Dispose();
        queue.Dispose();
        TryWriteReport(log);
        return 0;
    }

    /// <summary>设置文件的最后修改时间（读不到就返回 default）。</summary>
    private static DateTime SafeStamp()
    {
        try
        {
            string path = AppPaths.SettingsFile;
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        }
        catch { return default; }
    }

    /// <summary>把宿主的运行情况写一份到 logs\hookhost.log，便于事后排查。</summary>
    private static void TryWriteReport(StringBuilder log)
    {
        try
        {
            string path = Path.Combine(AppPaths.DataDir, "logs", "hookhost.log");
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(path, log.ToString() + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }
}
