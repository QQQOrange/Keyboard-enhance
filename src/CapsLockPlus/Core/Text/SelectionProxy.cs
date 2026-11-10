using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Text;

/// <summary>
/// 「普通权限取词代理」：把取词这一步交给一个**非提权**的一次性进程去做。
///
/// ## 为什么需要它
///
/// 取词的 5 条通道（UIA → WM_COPY → SendInput → 线程级注入 → Ctrl+Insert）
/// 全部运行在本进程里。管理员模式下本进程是高完整性，于是：
///
///   · Chromium 系（Chrome / Edge / WebView2）会**拒绝向提权客户端**暴露
///     无障碍树 —— UIA 这条最可靠的路直接没了；
///   · 它同时会过滤合成输入，SendInput / 线程级注入也拿不到；
///   · Chromium 不实现 WM_COPY，窗口消息那条也无效。
///
/// 结果就是"管理员模式下取词全都不工作"。注意这**不是** UIPI 拦了注入
/// （UIPI 只拦低→高），而是目标程序对"高完整性客户端"的主动拒绝。
///
/// 把这一步交给一个普通权限的子进程，客户端完整性就与浏览器相同，
/// 三条通道立刻恢复 —— 而拦截键盘仍然由提权的主进程负责，两边都不牺牲。
///
/// ## 成本控制
///
/// · **非常驻**：只在主进程所有通道都失败时才拉起，用完即退；
/// · **不弹 UAC**：用 explorer.exe 的令牌启动，本来就是普通权限；
/// · **权限不变**：子进程比主进程权限更低，不构成提权通道；
/// · **无证书、无配置**：就是同一个 exe 加一个 `--pick` 参数。
/// </summary>
public static class SelectionProxy
{
    /// <summary>子进程命令行开关。</summary>
    public const string ArgFlag = "--pick";

    /// <summary>取词代理的整体预算（含进程启动）。</summary>
    public const int DefaultBudgetMs = 1500;

    private static int _busy;

    /// <summary>当前进程是不是"取词代理"角色（是的话不能再递归调代理）。</summary>
    public static bool InHelper { get; private set; }

    /// <summary>从命令行里取出管道名；不是代理角色时返回 null。</summary>
    public static string? ParseHelperArg(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(ArgFlag, StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return args[i + 1];
            return "";      // 有开关没名字：视为无效，但仍然不该继续启动主程序
        }
        return null;
    }

    /// <summary>
    /// 主进程侧：拉起一次代理并等它把选区送回来。
    /// 失败返回空串（调用方继续走后面的保底逻辑）。
    ///
    /// **管道角色是反的**：由代理创建服务端、主进程当客户端。
    /// 原因：高完整性进程创建的对象默认带"禁止向上写"的强制策略，
    /// 中完整性客户端去连它很可能被拒绝；反过来（高连低、读）是允许的。
    /// </summary>
    public static string TryGetSelectedText(int budgetMs = DefaultBudgetMs)
    {
        if (InHelper) return string.Empty;
        if (Interlocked.Exchange(ref _busy, 1) != 0) return string.Empty;

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Volatile.Write(ref _busy, 0);
            return string.Empty;
        }

        string pipeName = "KeyEnhancePick_" + Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        try
        {
            if (!LaunchHelper(exe, pipeName))
            {
                CoreLog.Warn("[取词代理] 无法以普通权限拉起子进程");
                return string.Empty;
            }

            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.In);
            int connectBudget = Math.Max(400, budgetMs - 200);
            try { client.Connect(connectBudget); }
            catch
            {
                CoreLog.Warn($"[取词代理] 未能在预算内连上子进程");
                return string.Empty;
            }

            using var ms = new MemoryStream();

            /* Connect 只是第一段；代理取词本身卡住时，CopyTo 会永久等待，
             * 而本方法跑在动作队列线程，会让所有后续热键一起失灵。
             * 给读取阶段也加硬超时。 */
            int remaining = budgetMs - (int)sw.ElapsedMilliseconds;
            if (remaining <= 0) return string.Empty;

            using var cts = new CancellationTokenSource(remaining);
            try
            {
                client.CopyToAsync(ms, cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                CoreLog.Warn("[取词代理] 读取结果超时（预算 " + budgetMs + "ms）");
                return string.Empty;
            }

            string text = Encoding.UTF8.GetString(ms.ToArray());
            CoreLog.Info($"[取词代理] 返回长度={text.Length}");
            return text.TrimEnd('\0');
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[取词代理] 失败：" + ex.Message);
            return string.Empty;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>
    /// 子进程侧：取一次词，写进管道，然后退出。
    /// 返回进程退出码（0 = 有结果，2 = 没取到）。
    /// </summary>
    public static int RunHelper(string pipeName)
    {
        InHelper = true;
        if (string.IsNullOrEmpty(pipeName)) return 2;

        string text = "";
        try
        {
            /* ⚠ 管道服务端必须在**取词之前**建好。
             *
             * 主进程在启动子进程之后只等 1.3 秒就要 Connect 上；
             * 而取词（UIA / WM_COPY / SendInput 五条通道挨个试）本身
             * 就要 0.7~1.2 秒。先取词再建服务端 = 主进程早就超时放弃了，
             * 这条"管理员模式下用普通权限取词"的保底通道几乎从不生效。
             *
             * 现在先建服务端并异步等连接，再去取词；取完直接写回去。 */
            using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte);

            var wait = server.WaitForConnectionAsync();

            try
            {
                // 用"本地"实现，避免代理再把自己拉起来
                text = TextActions.GetSelectedTextLocal(1200);
            }
            catch (Exception ex)
            {
                CoreLog.Warn("[取词代理] 取词异常：" + ex.Message);
            }

            // 主进程可能还在启动中，最多再等 3 秒
            if (!wait.Wait(3000))
            {
                CoreLog.Warn("[取词代理] 主进程没有在预期时间内连上来");
                return 2;
            }

            byte[] payload = Encoding.UTF8.GetBytes(text ?? "");
            server.Write(payload, 0, payload.Length);
            server.Flush();
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[取词代理] 回传失败：" + ex.Message);
            return 2;
        }

        return string.IsNullOrEmpty(text) ? 2 : 0;
    }

    /* ---------------- 以普通权限启动 ---------------- */

    private static bool LaunchHelper(string exe, string pipeName)
    {
        string cmdArgs = $"{ArgFlag} {pipeName}";

        /* 只有一条路：复制 explorer 的令牌后启动。
         *
         * 不走 `explorer.exe <exe> --pick <pipe>` 那种兜底 —— 实测它会丢掉
         * 命令行参数，子进程拿不到管道名，于是"看起来启动了、其实没回传"，
         * 比干脆失败更难排查。失败就如实返回 false，调用方退回本地结果。 */
        if (LaunchWithShellToken(exe, cmdArgs)) return true;

        CoreLog.Warn("[取词代理] 无法以普通权限启动子进程（需要管理员权限才能拿到令牌）");
        return false;
    }

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const int TOKEN_DUPLICATE = 0x0002;
    private const int TOKEN_QUERY = 0x0008;
    private const int MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int LOGON_WITH_PROFILE = 0x00000001;
    private const int CREATE_NO_WINDOW = 0x08000000;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(
        IntPtr token, int logonFlags, string? app, string cmdLine, int flags,
        IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    /// <summary>
    /// 用 explorer.exe 的令牌启动子进程 —— 这就是"以普通权限启动"的标准做法。
    ///
    /// 需要一个 SeImpersonatePrivilege（管理员默认有），但**不会**降低安全性：
    /// 子进程拿到的就是用户桌面本来应有的中完整性令牌。
    /// </summary>
    private static bool LaunchWithShellToken(string exe, string cmdArgs)
    {
        IntPtr shell = IntPtr.Zero, proc = IntPtr.Zero, tok = IntPtr.Zero, dup = IntPtr.Zero;
        try
        {
            shell = GetShellWindow();
            if (shell == IntPtr.Zero) return false;

            GetWindowThreadProcessId(shell, out uint pid);
            if (pid == 0) return false;

            proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (proc == IntPtr.Zero) return false;

            if (!OpenProcessToken(proc, TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY, out tok)) return false;
            if (!DuplicateTokenEx(tok, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out dup)) return false;

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = "winsta0\\default" };
            string cmd = $"\"{exe}\" {cmdArgs}";
            if (!CreateProcessWithTokenW(dup, LOGON_WITH_PROFILE, exe, cmd, CREATE_NO_WINDOW,
                    IntPtr.Zero, null, ref si, out PROCESS_INFORMATION pi))
            {
                CoreLog.Warn($"[取词代理] CreateProcessWithTokenW 失败，错误码 {Marshal.GetLastWin32Error()}");
                return false;
            }

            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[取词代理] 令牌启动异常：" + ex.Message);
            return false;
        }
        finally
        {
            if (dup != IntPtr.Zero) CloseHandle(dup);
            if (tok != IntPtr.Zero) CloseHandle(tok);
            if (proc != IntPtr.Zero) CloseHandle(proc);
        }
    }

}
