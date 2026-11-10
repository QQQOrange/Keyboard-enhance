using System.Windows;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Localization;
using CapsLockPlus.Core.Qbar;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Update;
using CapsLockPlus.Core.Windows;
using CapsLockPlus.UI.Theme;
using CapsLockPlus.UI.Windows;

namespace CapsLockPlus.UI;

/// <summary>
/// 钩子宿主与自愈：起停宿主进程、读它回传的按键计数、流量看护与探针重装。拆分自 AppHost.cs（同属一个 partial 类，行为不变）。
/// </summary>
public sealed partial class AppHost
{
    private ForegroundWatcher? _foreground;
    private KeyboardDeviceTracker? _deviceTracker;

    /// <summary>降级提示只弹一次。</summary>
    private bool _degradedNotified;


    /// <summary>钩子自检连续失败次数。</summary>
    private int _probeFailures;

    /// <summary>本次运行是否已经尝试过"重启自己救钩子"。</summary>
    private bool _selfRestartTried;

    /// <summary>发给宿主的管理命令（CAPTURE 1/0 之类）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _hookHostCommands = new();

    /// <summary>钩子宿主进程（最小进程，只装钩子跑热键）。</summary>
    private System.Diagnostics.Process? _hookHost;

    /// <summary>宿主是否已就绪（就绪后本进程不再装钩子）。</summary>
    private volatile bool _hookHostReady;

    /// <summary>宿主控制管道那条循环的取消源（重启宿主时要把旧循环停掉）。</summary>
    private System.Threading.CancellationTokenSource? _hostPipeCts;

    /// <summary>录键事件只订阅一次（重启宿主时不能重复挂）。</summary>
    private bool _captureSinkHooked;

    /// <summary>本场会话里"宿主意外退出 → 尝试重启"的次数（上限 2）。</summary>
    private int _hostRecoveries;

    /// <summary>上一次恢复尝试的时刻（TickCount64）。</summary>
    private long _lastHostRecoveryAtMs;

    /// <summary>恢复流程是否正在跑（0/1）。</summary>
    private int _hostRecovering;

    /// <summary>
    /// 钩子正在本进程与宿主之间交接。
    /// 交接期间"本进程钩子计数为 0"是预期状态：流量看护与探针都必须让路，
    /// 否则它们会抢先重装本进程钩子，与刚起来的宿主形成**两个钩子**。
    /// </summary>
    private volatile bool _hookHandover;


    /// <summary>钩子自检的计时器（首轮失败时要临时提前复查）。</summary>
    private System.Windows.Threading.DispatcherTimer? _probeTimer;

    /// <summary>Raw Input 收到的按键总数（和钩子的事件数对比，用来发现"钩子被摘"）。</summary>
    private long _rawKeyCount;

    /// <summary>其中能识别出物理设备的那些（日志用）。</summary>
    private long _rawPhysicalCount;

    private System.Windows.Threading.DispatcherTimer? _hookWatch;
    private long _lastHookCount, _lastRawCount;
    /// <summary>钩子没收到、但 Raw Input 收到了的按键数（累计到一定量就判定摘钩）。</summary>
    private long _rawSinceHook;

    /// <summary>从什么时候开始"钩子一个都没收到"。</summary>
    private long _hookSilentSince;

    /// <summary>已经重装过几次（一场会话最多两次，避免无限重装刷日志）。</summary>
    private int _hookHeals;

    /// <summary>
    /// 按流量比对，发现"钩子已经被系统摘掉"就重装。
    ///
    /// 低级钩子的老坑：回调一旦超时（默认 300ms），系统会**静默**把它摘掉 ——
    /// 句柄还在、`IsHookAlive` 还是 true、钩子线程还活着（探活也正常），
    /// 但从这一刻起一个按键都收不到。表现就是"宏录制没反应""组合键全失灵"，
    /// 而界面上一切正常。
    ///
    /// Raw Input 不受影响，所以拿两边的增量对比就能判定：
    /// 连续两轮（约 6 秒）Raw Input 收到了键、钩子却零，就重装钩子。
    /// </summary>
    private void StartHookTrafficWatch()
    {
        _hookWatch = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _hookWatch.Tick += (_, _) => CheckHookTraffic();
        _hookWatch.Start();
    }

    /// <summary>
    /// 钩子自检的定时器：启动 3 秒后探一次，之后每 60 秒一次。
    ///
    /// 探针 = 自己敲一个不带标记的 F24，再数钩子有没有收到。
    /// 这是唯一不依赖"用户按键"的判据：钩子被系统摘掉时，
    /// 句柄在、线程活、探活 ping 也正常，只看状态永远发现不了。
    /// 探不到就重装 —— 装回来后热键才真正"能吞键"。
    /// </summary>
    private void StartHookProbe()
    {
        /* 排查用：CLP_PROBE_LOG=1 时每 2 秒探一次，并且**通过也记**。 */
        bool verbose = Environment.GetEnvironmentVariable("CLP_PROBE_LOG") == "1";

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            /* 首次延迟 10 秒：启动那几秒窗口/页面在构建，注入按键不一定能立刻落地，
             * 早探会把"还没准备好"误判成"钩子坏了"—— 以前就是这么误触发重装的。 */
            Interval = TimeSpan.FromSeconds(verbose ? 2 : 10),
        };
        _probeTimer = timer;
        int round = 0;
        timer.Tick += (_, _) =>
        {
            round++;
            if (round == 1 && !verbose)
            {
                timer.Interval = TimeSpan.FromSeconds(60);
            }
            ProbeHookOnce(round == 1 ? "启动自检" : "定期自检", verbose);
        };
        timer.Start();
    }

    /// <summary>
    /// 钩子在这个进程里救不回来时，重启自己。
    ///
    /// 背景（实测）：同一个时刻，跑了几分钟的主进程里新装的钩子一个事件都收不到，
    /// 而新起一个进程装的钩子立刻就能收到 —— 这是进程级的故障，进程内怎么重装都没用。
    /// 用户看到的现象是"长按 Tab 一直跳焦点"（兜底能触发但吞不掉按键）。
    ///
    /// 约束：
    ///   · 只在普通运行时做（诊断/自检模式绝不重启）；
    ///   · 每次运行最多一次，且 15 分钟内不重复（用数据目录里的时间戳文件防抖），
    ///     否则一旦环境持续异常就会无限重启。
    /// </summary>
    private void RestartSelfForHookRecovery(bool force = false)
    {
        try
        {
            if (Core.CliFlags.IsDiagnostic(Environment.GetCommandLineArgs().Skip(1)))
            {
                CoreLog.Warn("诊断模式下不自动重启（避免打断诊断）");
                return;
            }

            string stamp = System.IO.Path.Combine(Core.AppPaths.DataDir, "hook-self-restart.txt");
            if (!force) try
            {
                if (System.IO.File.Exists(stamp) &&
                    DateTime.Now - System.IO.File.GetLastWriteTime(stamp) < TimeSpan.FromMinutes(15))
                {
                    CoreLog.Warn("15 分钟内已经自动重启过一次，这次不再重启（避免循环）");
                    return;
                }
                System.IO.File.WriteAllText(stamp, DateTime.Now.ToString("O"));
            }
            catch { /* 写不了时间戳就不做防抖，按"本次运行最多一次"限制 */ }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            var args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1));
            CoreLog.Warn($"键盘钩子已失效且无法在进程内恢复，自动重启程序以恢复（{exe} {args}）");

            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };

            /* 管理员权限下钩子反复装不上：必须真正降低完整性级别重启。
             * 直接 Process.Start 只会继承当前管理员令牌，CLP_SKIP_ELEVATE
             * 只是阻止新进程再次提权，并不能降权。交给普通权限的 explorer 拉起，
             * 并带 --noelevate，避免新进程按设置再次 UAC。 */
            bool wasElevated = Core.ElevationHelper.IsElevated;
            if (wasElevated)
            {
                string? shellExe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(shellExe)) return;
                try { SingleInstance.Release(); } catch { }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"\"{shellExe}\" --noelevate")
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                });
                CoreLog.Warn("当前是管理员权限：已通过 explorer 以普通权限重启（--noelevate）");
                _dispatcher.BeginInvoke(() => ExitApp());
                return;
            }

            foreach (var a in Environment.GetCommandLineArgs().Skip(1)) psi.ArgumentList.Add(a);
            try { SingleInstance.Release(); } catch { }
            System.Diagnostics.Process.Start(psi);

            _dispatcher.BeginInvoke(() => ExitApp());
        }
        catch (Exception ex) { CoreLog.Warn("自动重启失败：" + ex.Message); }
    }

    /// <summary>
    /// 启动钩子宿主进程并等它 READY（最多 2.5 秒）。
    /// 详见 <see cref="Core.HookHost"/>：宿主是最小进程，不受本进程 GC 停顿影响。
    /// </summary>
    /// <summary>给宿主发一条管理命令（宿主连上来后按序写入）。</summary>
    private void SendHookHostCommand(string command) => _hookHostCommands.Enqueue(command);

    /// <summary>
    /// 宿主的控制通道（本进程是服务端，宿主是客户端）。
    /// 目前只用来通知"开始/结束录键"：录键期间宿主不能吞键，否则触发键录不到。
    /// </summary>
    private void StartHookHostControlPipe(string pipeName, System.Threading.CancellationToken ct)
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new System.IO.Pipes.NamedPipeServerStream(
                        pipeName + "-cmd", System.IO.Pipes.PipeDirection.Out, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte,
                        System.IO.Pipes.PipeOptions.None);
                    server.WaitForConnection();
                    using var w = new System.IO.StreamWriter(server, System.Text.Encoding.UTF8) { AutoFlush = true };
                    while (server.IsConnected && !ct.IsCancellationRequested)
                    {
                        if (_hookHostCommands.TryDequeue(out string? cmd)) w.WriteLine(cmd);
                        else Thread.Sleep(30);
                    }
                }
                catch { Thread.Sleep(500); }
            }
        });
    }

    /// <summary>
    /// 启动钩子宿主（界面线程入口）：只负责把"设备专属改键"这个需要读设置的判断做完。
    /// </summary>
    private bool StartHookHost()
    {
        bool deviceScoped = false;
        try
        {
            deviceScoped = Settings.Global.BaseRemaps.Any(r => !string.IsNullOrWhiteSpace(r.Device));
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "读取设备专属改键设置"); }

        return StartHookHost(deviceScoped);
    }

    /// <param name="deviceScopedRemaps">
    /// 设置里是否存在"只在某只键盘上生效"的基础改键。为 true 时不能用宿主
    /// （宿主没有 Raw Input，拿不到设备信息），由主进程自己装钩子。
    /// 由调用方在界面线程算好再传进来 —— 宿主重启会在后台线程调用本方法，
    /// 那里不能去枚举可能正被界面修改的集合。
    /// </param>
    private bool StartHookHost(bool deviceScopedRemaps)
    {
        /* 自检 / 渲染 / 诊断模式不拉宿主：这些模式跑完就退，
         * 多一个常驻子进程只会在日志里制造噪音，也没有热键需求。 */
        if (Core.CliFlags.IsDiagnostic(Environment.GetCommandLineArgs().Skip(1)))
        {
            CoreLog.Info("诊断模式：不启动钩子宿主");
            return false;
        }

        if (deviceScopedRemaps)
        {
            CoreLog.Info("检测到设备专属改键：本次由主进程安装钩子，避免宿主丢失设备信息");
            return false;
        }

        string pipeName = $"clp-hook-{Environment.ProcessId}";
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--hookhost");
            psi.ArgumentList.Add(pipeName);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());

            _hookHost = System.Diagnostics.Process.Start(psi);
            if (_hookHost == null) return false;

            var ready = new ManualResetEventSlim(false);
            var hookFailed = new ManualResetEventSlim(false);
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var server = new System.IO.Pipes.NamedPipeServerStream(
                        pipeName, System.IO.Pipes.PipeDirection.In, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte,
                        System.IO.Pipes.PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new System.IO.StreamReader(server, System.Text.Encoding.UTF8);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line == "READY") { ready.Set(); continue; }
                        if (line == "BYE") break;
                        if (Environment.GetEnvironmentVariable("CLP_MIRROR_LOG") == "1")
                            CoreLog.Info($"[镜像] {line}");

                        if (line.StartsWith("KEY\t", StringComparison.Ordinal))
                        {
                            var p3 = line.Split('\t');
                            if (p3.Length >= 3 && int.TryParse(p3[1], out int k) && p3[2] == "1")
                            {
                                KeyCaptureSink.Push(k, true);
                                _dispatcher.BeginInvoke(() => Engine.ApplyRemoteKey(k, true));
                            }
                            else if (p3.Length >= 3 && int.TryParse(p3[1], out int k2))
                            {
                                KeyCaptureSink.Push(k2, false);
                                _dispatcher.BeginInvoke(() => Engine.ApplyRemoteKey(k2, false));
                            }
                            continue;
                        }
                        if (line.StartsWith("TRIG\t", StringComparison.Ordinal))
                        {
                            var p4 = line.Split('\t');
                            bool held = p4.Length > 1 && p4[1] == "1";
                            int layer = p4.Length > 2 && int.TryParse(p4[2], out int l0) ? l0 : -1;
                            string key = p4.Length > 3 ? p4[3] : "";
                            _dispatcher.BeginInvoke(() => Engine.ApplyRemoteTrigger(held, key, layer));
                            continue;
                        }
                        if (line.StartsWith("LAYER\t", StringComparison.Ordinal))
                        {
                            if (int.TryParse(line[6..], out int l1))
                                _dispatcher.BeginInvoke(() => Engine.ApplyRemoteLayer(l1));
                            continue;
                        }

                        if (line == "HOOKFAIL")
                        {
                            CoreLog.Warn("钩子宿主报告：钩子没有生效，改由主进程安装");
                            hookFailed.Set();

                            /* 启动阶段由 StartHookHost 立即回退，不再排队重启；
                             * 只有宿主运行中才走权限自愈。 */
                            if (_hookHostReady && Core.ElevationHelper.IsElevated && !_selfRestartTried)
                            {
                                _selfRestartTried = true;
                                _dispatcher.BeginInvoke(() => RestartSelfForHookRecovery());
                            }
                            continue;
                        }
                        if (!line.StartsWith("ACT\t", StringComparison.Ordinal)) continue;

                        var parts = line.Split('\t');
                        if (parts.Length < 2) continue;
                        string action = parts[1];
                        string arg = parts.Length > 2 ? parts[2] : "";

                        /* 新版宿主把参数 Base64 了；旧版是原文。先按新协议解，
                         * 解不开再按旧协议用，避免升级瞬间管道两端版本不一致。 */
                        if (arg.Length > 0)
                        {
                            try
                            {
                                arg = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(arg));
                            }
                            catch (FormatException)
                            {
                                /* 旧协议原文，保持原样 */
                            }
                        }

                        _dispatcher.BeginInvoke(() =>
                        {
                            try { InvokeAction(action, arg); }
                            catch (Exception ex) { CoreLog.Swallow(ex, "宿主动作"); }
                        });
                    }
                }
                catch (Exception ex) { CoreLog.Warn("钩子宿主管道异常：" + ex.Message); }

                if (_hookHostReady)
                {
                    _hookHostReady = false;
                    Engine.HookDelegatedToHost = false;
                    CoreLog.Warn("钩子宿主已退出，改由本进程自己装钩子");

                    /* 宿主没了 = 抬起的按键再也收不到：先把改键目标键松开，
                     * 否则用户按着 Ctrl 的那一刻宿主崩掉，Alt 会一直按着。 */
                    try { Engine.ReleaseRemapTargets(); } catch (Exception ex) { CoreLog.Swallow(ex, "宿主退出时松开改键"); }
                    _dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            /* 交接前把系统里可能悬空的键清一遍：宿主死掉的瞬间，
                             * 用户正按着的那几个键不会再有抬起事件。 */
                            Core.Input.InputSafety.ReleaseAll();
                            Engine.RestartHook();
                        }
                        catch (Exception ex) { CoreLog.Swallow(ex, "接管钩子"); }
                    });

                    /* 已经由本进程接管（热键不能有空窗），几秒后再试着把宿主拉起来；
                     * 成功则把钩子交还宿主，失败就永久留在本进程。 */
                    ScheduleHookHostRecovery();
                }
            });

            /* 重启宿主时先停掉上一轮的控制管道循环（否则它会带着旧管道名空转重试）。 */
            try { _hostPipeCts?.Cancel(); } catch { }
            _hostPipeCts?.Dispose();
            _hostPipeCts = new System.Threading.CancellationTokenSource();
            StartHookHostControlPipe(pipeName, _hostPipeCts.Token);

            /* 录键期间：本进程引擎与宿主都停止吞键，并把宿主的按键喂进捕获窗。 */
            if (!_captureSinkHooked)
            {
                _captureSinkHooked = true;
                KeyCaptureSink.Changed += open =>
                {
                    Engine.CaptureMode = open;
                    SendHookHostCommand(open ? "CAPTURE 1" : "CAPTURE 0");
                    CoreLog.Info(open ? "开始录键：已通知宿主暂停吞键" : "结束录键：宿主恢复吞键");
                };
            }

            int signaled = WaitHandle.WaitAny(
                new WaitHandle[] { ready.WaitHandle, hookFailed.WaitHandle }, 2500);

            if (signaled == 0 && ready.IsSet)
            {
                _hookHostReady = true;
                Engine.HookDelegatedToHost = true;
                CoreLog.Info($"钩子宿主已就绪（PID={_hookHost.Id}）：钩子由它负责");
                return true;
            }

            if (signaled == 1 || hookFailed.IsSet)
                CoreLog.Warn("钩子宿主未能安装钩子，改由本进程自己装钩子");
            else
                CoreLog.Warn("钩子宿主 2.5 秒内没有就绪，改由本进程自己装钩子");

            try { _hookHost.Kill(); } catch { }
            _hookHost = null;
            return false;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("启动钩子宿主失败，改由本进程自己装钩子：" + ex.Message);
            try { _hookHost?.Kill(); } catch { }
            _hookHost = null;
            return false;
        }
    }

    /// <summary>
    /// 宿主意外退出后的恢复：立刻由本进程接管（已在调用点做过），几秒后再尝试
    /// 把宿主拉起来；成功则把钩子交还宿主，失败就永久留在本进程。
    ///
    /// 为什么要设上限：宿主如果一启动就崩（环境问题、被杀软拦），无限重启只会
    /// 变成"每 4 秒起一个进程"。每场会话最多 2 次、且两次之间至少隔 5 分钟。
    /// 为什么先停本进程钩子再起宿主：两者同时装钩子时，同一个按键会被两个引擎
    /// 各执行一次（历史上"按一下打开两个程序"的事故）。
    /// </summary>
    private void ScheduleHookHostRecovery()
    {
        if (Exiting) return;
        long now = Environment.TickCount64;
        if (_hostRecoveries >= 2) return;
        if (_lastHostRecoveryAtMs != 0 && now - _lastHostRecoveryAtMs < 5 * 60_000) return;
        if (Interlocked.Exchange(ref _hostRecovering, 1) != 0) return;

        _hostRecoveries++;
        _lastHostRecoveryAtMs = now;
        CoreLog.Warn($"钩子宿主恢复：4 秒后尝试重启（第 {_hostRecoveries} 次，上限 2 次）");

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            bool detached = false;
            try
            {
                await System.Threading.Tasks.Task.Delay(4000).ConfigureAwait(false);
                if (Exiting || _hookHostReady) return;

                /* 需要读设置里的"设备专属改键"：只能回界面线程读（集合可能正被编辑）。 */
                bool deviceScoped = false;
                try
                {
                    _dispatcher.Invoke(() => deviceScoped =
                        Settings.Global.BaseRemaps.Any(r => !string.IsNullOrWhiteSpace(r.Device)));
                }
                catch (Exception ex)
                {
                    CoreLog.Swallow(ex, "恢复宿主前读取设备改键设置");
                    return;
                }

                if (deviceScoped)
                {
                    CoreLog.Warn("有设备专属改键：不再尝试宿主，继续由本进程装钩子");
                    return;
                }

                /* 先停本进程钩子，避免宿主就绪后短暂出现"双钩子"。
                 * 这一步会带来最多 2.5 秒的热键空窗 —— 只发生在恢复路径上，
                 * 比"一个按键执行两次动作"安全得多。 */
                _hookHandover = true;
                _dispatcher.Invoke(() =>
                {
                    try { Engine.DetachHookForHost(); detached = true; }
                    catch (Exception ex) { CoreLog.Swallow(ex, "恢复宿主前停用本进程钩子"); }
                });

                if (!detached || Exiting) return;

                /* StartHookHost 最多等 2.5 秒 READY，所以放后台线程，不卡界面。 */
                if (!StartHookHost(deviceScopedRemaps: false))
                {
                    CoreLog.Warn("钩子宿主重启失败，已恢复本进程钩子");
                    _ = _dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            Engine.HookDelegatedToHost = false;
                            Engine.RestartHook();
                        }
                        catch (Exception ex) { CoreLog.Swallow(ex, "恢复本进程钩子"); }
                    });
                    return;
                }

                CoreLog.Info("钩子宿主已重启并接管（本进程钩子已停用）" +
                             (Engine.HookInstalled ? "——警告：本进程钩子仍在，请用 --diag 反馈" : ""));
            }
            catch (Exception ex)
            {
                CoreLog.Warn("钩子宿主恢复异常：" + ex.Message);
                if (detached && !Exiting)
                {
                    try
                    {
                        _ = _dispatcher.BeginInvoke(() =>
                        {
                            Engine.HookDelegatedToHost = false;
                            Engine.RestartHook();
                        });
                    }
                    catch { /* 退出过程中忽略 */ }
                }
            }
            finally
            {
                _hookHandover = false;   // 交接结束，自愈逻辑恢复工作
                Interlocked.Exchange(ref _hostRecovering, 0);
            }
        });
    }

    private void ProbeHookOnce(string why, bool verbose = false)
    {
        /* 宿主模式下本进程没有钩子，探针无意义。交接过程中同理。 */
        if (_hookHostReady || _hookHandover) return;

        try
        {
            /* 单次失败不算数：启动期注入可能只是慢了一拍。
             * 连续三次都收不到才认为钩子真的坏了 —— 误判一次就要重装一次，
             * 而重装是有代价的（旧线程卸新钩子的竞态就是这么触发的）。 */
            bool ok = false;
            bool inconclusive = false;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var r = Engine.ProbeHook(attempt == 1 ? 600 : 900);
                if (r == true) { ok = true; break; }
                if (r == null) { inconclusive = true; break; }
                Thread.Sleep(150);
            }

            /* 注入本身失败（前台是提权窗口时 UIPI 会拒绝 SendInput）：
             * 这跟钩子坏没坏无关，什么都不能下结论。 */
            if (!ok && inconclusive)
            {
                CoreLog.Warn($"钩子自检无法进行（{why}）：注入按键被系统拒绝，错误码 {Core.Input.InputSender.LastInjectError}" +
                             "（前台是管理员窗口时是正常的），本次不重装");
                return;
            }
            if (ok)
            {
                _probeFailures = 0;
                if (_probeTimer != null && !verbose && _probeTimer.Interval != TimeSpan.FromSeconds(60))
                    _probeTimer.Interval = TimeSpan.FromSeconds(60);
            }
            if (ok && verbose)
            {
                CoreLog.Info($"钩子自检通过（{why}）, 累计事件={Engine.KeyEventCount}");
            }
            if (ok)
            {
                /* 通过时不写日志（每 60 秒一条太吵），失败才记。 */
                _probeFailures = 0;
                return;
            }

            _probeFailures++;

            /* 第一次失败只记一笔、不动手：启动期注入慢一拍很常见，
             * 而重装本身是有风险的（旧线程卸新钩子的竞态）。 */
            if (_probeFailures < 2)
            {
                CoreLog.Info($"钩子自检第一轮没过（{why}）：可能是启动期注入还没落地，5 秒后复查");
                if (_probeTimer != null) _probeTimer.Interval = TimeSpan.FromSeconds(5);
                return;
            }

            /* 管理员权限下连钩子都收不到键，而同一台机器上**普通权限**的 --hooktest 是 6/6 正常
             * （用户实测）—— 这种情况不该再折腾重装，直接换成普通权限重启一次。
             * 这是唯一"零操作"的自救路径：用户不用去设置里关管理员模式。 */
            if (Core.ElevationHelper.IsElevated && !_selfRestartTried)
            {
                _selfRestartTried = true;
                CoreLog.Warn("管理员权限下钩子收不到按键（普通权限下同一台机器是正常的），改用普通权限重启");
                RestartSelfForHookRecovery();
                return;
            }

            CoreLog.Warn($"钩子自检失败（{why}，连续第 {_probeFailures} 次）：收不到自己注入的按键" +
                         $"（句柄非零={Engine.HookInstalled}，线程={Engine.HookThreadState}，" +
                         $"探活={(Engine.PingHook(200) ? "有响应" : "无响应")}，" +
                         $"最长回调={Engine.HookMaxCallbackMs:F0}ms，" +
                         $"回调卡住={Engine.HookCallbackStuckMs}ms(vk=0x{Engine.HookStuckVk:X2})），正在重装");
            Engine.RestartHook();

            /* 重装后立刻复验一次：成功就当场恢复（用户不用等下一个 60 秒），
             * 仍然失败就说明不是"偶发摘钩"，继续计数。 */
            bool recovered = false;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                Thread.Sleep(200);
                if (Engine.ProbeHook(900) == true) { recovered = true; break; }
            }
            if (recovered)
            {
                CoreLog.Info("重装后自检通过，钩子已恢复");
                return;
            }
            CoreLog.Warn("重装后自检仍然失败（这次重装没救回来）");

            /* 连续多次救不回来：这个进程里的钩子已经废了（实测同一时刻新进程的钩子完全正常）。
             * 与其让用户面对"热键时灵时不灵、触发键还漏键"，不如自己重启一次 ——
             * 新进程的钩子是好的，这是唯一被证据支持的恢复手段。 */
            /* ⚠ 以前这里会自动重启程序。用户明确反对："没事就重启，影响正常使用"。
             *
             * 现在改成**只提示、不重启**：钩子救不回来时，热键确实用不了，
             * 但重启与否应该由用户决定 —— 托盘给出提示，用户自己在空闲时重启，
             * 而不是打字打到一半窗口突然没了。
             */
            if (_probeFailures >= 4 && !_degradedNotified)
            {
                _degradedNotified = true;
                CoreLog.Warn("钩子多次重装仍失效：不再自动重启（改为提示用户手动处理）");
                Notify(Loc.T("键盘钩子失效了，热键暂时用不了：可以继续用，也可以稍后重启程序恢复"));
                return;
            }

            /* 连续三次都装不回来：这不是"偶发摘钩"，是环境不允许（安全软件拦截提权进程的钩子
             * 之类）。热键此时靠 Raw Input 还能触发，但**吞不掉**按键，必须让用户知道。 */
            if (_probeFailures >= 3 && !_degradedNotified)
            {
                _degradedNotified = true;
                Notify(Loc.T("键盘钩子一直装不上（可能被安全软件拦截）：热键仍能用，但按下的键也会传给当前程序。建议重启程序或把本程序加入白名单"));
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "钩子自检"); }
    }

    private void CheckHookTraffic()
    {
        /* 正在把钩子在本进程与宿主之间交接：这期间"本进程钩子计数为 0"是正常的，
         * 不能据此判定钩子被摘掉，更不能顺手重装（那会和宿主形成双钩子）。 */
        if (_hookHandover) return;

        long raw = Interlocked.Read(ref _rawKeyCount);
        /* 宿主模式下钩子在**宿主进程**里，本进程的 KeyEventCount 恒为 0 ——
         * 必须用宿主回传的计数，否则会误判"钩子被摘"，把吞键整个关掉。 */
        long hook = _hookHostReady ? Engine.RemoteKeyCount : Engine.KeyEventCount;
        long rawDelta = raw - _lastRawCount;
        long hookDelta = hook - _lastHookCount;
        _lastRawCount = raw;
        _lastHookCount = hook;

        if (hookDelta > 0)
        {
            /* 钩子在工作，全部清零。 */
            /* 钩子确实在工作：清掉一切"怀疑"状态。
             * 也顺手让宿主恢复吞键 —— 老版本可能给它发过 SWALLOW 0。 */
            if (Engine.SwallowUnavailable) Engine.SwallowUnavailable = false;
            if (_hookHostReady) SendHookHostCommand("SWALLOW 1");
            _rawSinceHook = 0;
            _hookSilentSince = 0;
            _hookHeals = 0;
            return;
        }


        /* 钩子一个都没收到：把 Raw Input 期间收到的键累加起来。
         *
         * 不能"本轮没有按键就清零" —— 摘钩之后人往往是断续打字的，
         * 每轮之间空一拍就永远凑不够阈值。 */
        _rawSinceHook += rawDelta;
        if (_rawSinceHook == 0) return;

        long now = Environment.TickCount64;
        if (_hookSilentSince == 0) _hookSilentSince = now;
        if (_rawSinceHook < 6) return;
        if (now - _hookSilentSince < 3000) return;

        /* 只在"真实硬件按键被漏"时重装，而且一场会话最多两次：
         * 远程桌面 / 注入工具的按键根本进不了钩子，但它们不算进这里（不是物理设备）。 */
        if (_hookHeals >= 2)
        {
            _rawSinceHook = 0;
            _hookSilentSince = 0;
            return;
        }
        _hookHeals++;

        CoreLog.Warn($"键盘钩子漏掉了 {_rawSinceHook} 个**物理**按键（Raw Input 收到了）—— " +
                     $"判定钩子被系统摘掉，正在重装（第 {_hookHeals} 次）");
        _rawSinceHook = 0;
        _hookSilentSince = 0;

        /* ⚠ 这里**绝不能**把吞键关掉。
         *
         * 4.5.92 曾在这里发 SWALLOW 0 + SwallowUnavailable=true。而宿主模式的
         * 计数以前恒为 0，健康检查必然误判 → 一打字就"钩子死了" → 主动关掉吞键：
         * Tab 立刻恢复成原生 Tab、层永远进不去。用户感受到的就是
         * "怎么改都还是 Tab 一直跳"，而且日志里连原因都看不到。
         *
         * 现在：只让持有钩子的一侧重装（REHOOK），吞键始终保持开启。
         * 真的救不回来时，下面的探测/回退会把钩子搬到本进程，而不是"自废武功"。 */
        if (_hookHostReady)
        {
            /* 钩子在宿主进程里 —— 让宿主自己重装。 */
            SendHookHostCommand("REHOOK");
        }
        else
        {
            Engine.RestartHook();
        }

        /* 连着两次都救不回来：干脆放弃宿主，由本进程接管（能吞一个是一个）。 */
        if (_hookHeals >= 2 && _hookHostReady)
        {
            /* ⚠ 必须先杀掉宿主再自己装钩子。
             *
             * 否则同一时刻会有**两个**低层钩子、两套引擎在跑：
             * 同一个按键被两边各执行一次（打开两个 QQ、两个 WorkBuddy），
             * 吞键也各吞一半 —— 用户看到的就是"按一下触发一堆、然后键盘全废"。
             * 这是 4.5.9x 一个真实事故的直接原因。 */
            CoreLog.Warn("宿主重装两次仍漏键，改由本进程装钩子（先停掉宿主，避免两个钩子）");
            try { _hookHost?.Kill(); } catch { }
            _hookHost = null;
            _hookHostReady = false;
            Engine.HookDelegatedToHost = false;
            Engine.Start(installHook: true);
            Engine.SwallowUnavailable = false;
        }

        _lastHookCount = Engine.KeyEventCount;
        _lastRawCount = Interlocked.Read(ref _rawKeyCount);
    }

}
