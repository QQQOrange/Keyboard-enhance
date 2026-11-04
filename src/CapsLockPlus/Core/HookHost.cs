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

        /* ---- 管道写入：绝不阻塞钩子回调 ----
         *
         * ⚠ 这是"Tab 一直往后跳、层卡住不回"的真正根因（用户日志里
         * "键盘钩子漏掉了 132 个物理按键……正在重装" 就是它）：
         *
         * 以前每次按键都在**低层钩子回调里**直接 WriteLine。命名管道缓冲区
         * 满了（主程序在忙：渲染页面、同步、GC……）时这次写会阻塞，
         * 钩子回调一旦超过系统的 LowLevelHooksTimeout(~300ms)，
         * Windows 就会**静默把钩子摘掉**且不通知我们：
         *   · 之后所有按键都不再被吞 —— 按住 Tab 就是系统自己连发 Tab（焦点一直跳）；
         *   · up 事件也收不到 —— 引擎以为触发键还按着，层就"卡住"了。
         *
         * 现在改成：回调里只把消息塞进队列（O(1)，永不阻塞），
         * 由单独线程慢慢写；队列满时**只丢显示用的 KEY/TRIG**，动作不丢。
         */
        var outQueue = new System.Collections.Concurrent.BlockingCollection<string>(
            new System.Collections.Concurrent.ConcurrentQueue<string>(), 512);

        void Enqueue(string line, bool droppableWhenFull)
        {
            if (outQueue.TryAdd(line)) return;
            if (!droppableWhenFull) return;      // 队列满：显示消息直接丢，不阻塞

            /* 队列满一般是因为主程序卡住了。这里也不排队、不等待，
             * 直接丢 —— 显示流水少几条无所谓，钩子绝对不能超时。 */
        }

        void Forward(string action, string arg)
        {
            Enqueue($"ACT\t{action}\t{arg}", droppableWhenFull: false);
        }

        var writerThread = new Thread(() =>
        {
            try
            {
                foreach (string line in outQueue.GetConsumingEnumerable())
                {
                    try { writer?.WriteLine(line); }
                    catch { /* 主程序没了也不影响吞键 */ }
                }
            }
            catch { }
        })
        { IsBackground = true, Name = "HookHost.Writer" };
        writerThread.Start();

        engine = new HotkeyEngine(queue, () => settings, (action, arg) => { Forward(action, arg); return true; });

        /* 诊断：`--trace` 只设了主进程的开关，而钩子在宿主里 ——
         * 不把开关传过来，用户开 --trace 也看不到任何"钩子收到"的日志。
         * 主程序通过环境变量传（子进程天然继承）。 */
        if (Environment.GetEnvironmentVariable("CLP_TRACE") == "1")
        {
            engine.EnableKeyTrace();
            CoreLog.Info("钩子宿主：已开启按键追踪（CLP_TRACE=1）");
        }

        /* 把按键 / 触发键状态回传给主程序：「测试」页靠它显示实时流水与当前层。
         * 这里只做显示，吞键决策全在宿主这边。 */
        engine.KeySeen += (vk, down, _)
            => Enqueue($"KEY\t{vk}\t{(down ? 1 : 0)}", droppableWhenFull: true);

        engine.TriggerHeldChanged += held
            => Enqueue($"TRIG\t{(held ? 1 : 0)}\t{engine.ActiveLayer}\t{engine.ActiveTriggerKey ?? ""}",
                       droppableWhenFull: true);

        /* 控制通道（主程序 → 宿主）：CAPTURE 1/0 —— 录键期间宿主必须停止吞键，
         * 否则用户想录 Caps/Tab 这类触发键会永远录不到。 */
        _ = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var cmd = new NamedPipeClientStream(".", pipeName + "-cmd", PipeDirection.In);
                    cmd.Connect(2000);
                    using var reader2 = new StreamReader(cmd, Encoding.UTF8);
                    string? cmdLine;
                    while ((cmdLine = reader2.ReadLine()) != null)
                    {
                        if (cmdLine.StartsWith("CAPTURE", StringComparison.Ordinal))
                        {
                            engine!.CaptureMode = cmdLine.EndsWith("1", StringComparison.Ordinal);
                        }
                        else if (cmdLine.StartsWith("SWALLOW", StringComparison.Ordinal))
                        {
                            /* 主程序判定"钩子已经吞不住键"：宿主立刻停止演层，
                             * 按键全部原样放行，并把残留的触发键会话清掉。
                             * 这样用户的键盘至少是正常的（Tab 就是 Tab）。 */
                            bool on = cmdLine.Trim().EndsWith("1", StringComparison.Ordinal);
                            engine!.SwallowUnavailable = !on;
                            if (!on) engine.ResetForRecovery("主程序判定钩子失效");
                            CoreLog.Info($"钩子宿主：吞键{(on ? "恢复" : "暂停")}（SWALLOW {(on ? 1 : 0)}）");
                        }
                        else if (cmdLine.StartsWith("REHOOK", StringComparison.Ordinal))
                        {
                            /* 主程序发现"物理按键漏了"时让宿主重装。
                             * 以前主程序自己 RestartHook() —— 可钩子根本不在它那儿，
                             * 于是日志里一直重装、用户那边一直没恢复。 */
                            try { engine!.RestartHook(); } catch { }
                            CoreLog.Info("钩子宿主：收到 REHOOK，已重装键盘钩子");
                        }
                    }
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "HookHost.Ctl" };

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
                Enqueue($"LAYER\t{layerNow}", droppableWhenFull: true);
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
