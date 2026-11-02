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

        void Forward(string action, string arg)
        {
            try { writer?.WriteLine($"ACT\t{action}\t{arg}"); }
            catch { /* 主程序没了也不影响吞键 */ }
        }

        engine = new HotkeyEngine(queue, () => settings, (action, arg) => { Forward(action, arg); return true; });

        // 主程序要求重读配置
        var reload = new AutoResetEvent(false);
        _ = new Thread(() =>
        {
            try
            {
                using var srv = new NamedPipeServerStream(pipeName + "-ctl", PipeDirection.In);
                while (true)
                {
                    srv.WaitForConnection();
                    using var r = new StreamReader(srv, Encoding.UTF8);
                    string? line;
                    while ((line = r.ReadLine()) != null)
                        if (line.Trim() == "RELOAD") reload.Set();
                    srv.Disconnect();
                }
            }
            catch { /* 控制通道可有可无 */ }
        }) { IsBackground = true, Name = "HookHost.Ctl" };

        engine.Start();
        try { writer.WriteLine("READY"); } catch { }

        /* 钩子没装上要立刻告诉主程序：主程序据此决定是否改用普通权限重启
         * （实测：管理员权限下钩子可能装不上，普通权限下同一台机器是正常的）。 */
        if (!engine.IsHookAlive)
        {
            try { writer.WriteLine("HOOKFAIL"); } catch { }
            Trace("钩子未生效，已通知主程序");
        }
        Trace($"引擎已启动，钩子={(engine.IsHookAlive ? "正常" : "未生效")}");

        // 2) 主循环：只看有没有重读配置的请求
        while (true)
        {
            if (reload.WaitOne(1000))
            {
                try
                {
                    settings = SettingsStore.Load();
                    Trace("已重读配置");
                }
                catch { /* 配置读不了就继续用旧的 */ }
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
