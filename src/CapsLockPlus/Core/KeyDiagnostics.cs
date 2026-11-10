using CapsLockPlus.Core.Security;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CapsLockPlus.Core.Input;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core;

/// <summary>
/// 按键诊断：检查本机环境是否允许按键增强正常工作。
///
/// 用 `KeyboardEnhance.exe --diag` 运行，会输出一份人类可读的报告，
/// 说明：钩子能否安装、前台会话状态、以及最可能的失败原因与解决办法。
/// </summary>
public static class KeyDiagnostics
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

    private static int _observed;

    private static HookProc? _proc;

    private static IntPtr Probe(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP)
                _observed++;
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>返回 0 表示环境正常，非 0 表示发现问题。</summary>
    public static int Run(int listenSeconds = 8)
    {
        var sb = new StringBuilder();
        sb.AppendLine("按键增强 按键诊断报告");
        sb.AppendLine("======================");
        sb.AppendLine($"时间        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"管理员权限  : {(ElevationHelper.IsElevated ? "是" : "否")}");
        sb.AppendLine($"进程架构    : {RuntimeInformation.ProcessArchitecture} / " +
                      $"{(Environment.Is64BitProcess ? "64 位" : "32 位")}");
        sb.AppendLine($"系统        : {Environment.OSVersion.VersionString}");
        sb.AppendLine($"数据目录    : {AppPaths.DataDir}");
        sb.AppendLine();

        int problems = 0;

        // 1) 前台窗口与会话
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint fgPid);
        sb.AppendLine("【前台窗口】");
        sb.AppendLine($"  句柄      : 0x{fg.ToInt64():X}");
        sb.AppendLine($"  进程 ID   : {fgPid}");
        if (fg == IntPtr.Zero)
        {
            sb.AppendLine("  ⚠ 取不到前台窗口：可能运行在无桌面的会话中（服务/任务计划），按键无法被接收。");
            problems++;
        }
        sb.AppendLine();

        // 2) 钩子能否安装
        sb.AppendLine("【键盘钩子】");
        _proc = Probe;
        var hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            sb.AppendLine($"  ✗ 安装失败（错误码 {err}）");
            sb.AppendLine("    常见原因：安全软件/杀毒软件拦截了全局钩子，或系统策略禁止。");
            problems++;
        }
        else
        {
            sb.AppendLine("  ✓ 安装成功");
        }
        sb.AppendLine();

        /* 3) 实测：先自己发一个无害按键（F24），确认"钩子 → 回调"这条链路通。
         *
         * 以前这里只有"请在 8 秒内随便敲几个键"。问题是：用户去倒了杯水、
         * 或者根本没看懂要按 —— 结果也是 0 个事件，报告就直接下结论
         * "全局键盘钩子收不到任何按键，请检查安全软件"。
         * 那是**冤枉**安全软件的假警报，比不给结论更糟。
         *
         * 现在分两步：
         *   ① 自测注入（F24，没有任何程序会响应它）—— 能收到就说明链路是通的；
         *   ② 自测失败才真的等用户按键 —— 这种情况多半是 SendInput 被 UIPI/安全软件
         *      挡了，人工按键仍可能正常，值得再给一次机会。 */
        if (hook != IntPtr.Zero)
        {
            sb.AppendLine("【实测】正在自动验证钩子链路…");
            WriteAndFlush(sb.ToString());
            sb = new StringBuilder();

            int before = _observed;
            /* `--diag` 在 CliFlags.DryRunFlags 里（诊断模式一律不许真的发按键），
             * 但这一项**必须**真的注入 —— 否则测的就是"DoWork 被跳过"这件事本身。
             * 所以只在这一小段临时关掉干跑，发完立刻恢复。 */
            bool dry = InputSender.DryRun;
            InputSender.DryRun = false;
            try { InputSender.Tap(0x87 /*F24*/, 3); }   // 6 个事件（3 次按下 + 3 次抬起）
            catch (Exception ex) { CoreLog.Warn("诊断：自发按键失败 " + ex.Message); }
            finally { InputSender.DryRun = dry; }
            PumpFor(800);
            int autoSeen = _observed - before;

            sb.AppendLine($"  自发按键收到：{autoSeen} / 6 个");
            if (autoSeen > 0)
            {
                sb.AppendLine("  ✓ 钩子链路正常（钩子装得上、回调能进来）。");
            }
            else
            {
                sb.AppendLine($"  ⚠ 自发按键没被收到，改为等你手工按（{listenSeconds} 秒内敲几下，建议敲 CapsLock）…");
                WriteAndFlush(sb.ToString());
                sb = new StringBuilder();

                int manualBefore = _observed;
                PumpFor(listenSeconds * 1000);
                int manualSeen = _observed - manualBefore;
                sb.AppendLine($"  手工按键收到：{manualSeen} 个");

                if (manualSeen == 0)
                {
                    sb.AppendLine("  ✗ 自动注入和手工按键都没收到 —— 全局键盘钩子被挡住了。");
                    sb.AppendLine("    请检查：");
                    sb.AppendLine("      1. 是否有安全软件（火绒、360、卡巴斯基等）拦截了键盘钩子；");
                    sb.AppendLine("      2. 是否用了“筛选键/粘滞键”等辅助功能；");
                    sb.AppendLine("      3. 是否在远程桌面/虚拟机里运行（需在物理桌面运行）；");
                    sb.AppendLine("      4. 键盘是否被其他改键软件独占（如 PowerToys Keyboard Manager、某些游戏外设驱动）。");
                    problems++;
                }
                else
                {
                    sb.AppendLine("  ✓ 手工按键能收到：钩子本身正常，只是程序化的按键注入被挡了。");
                    sb.AppendLine("    这会影响「输入一段文字 / 发送按键」这类功能，但不影响你手敲组合键。");
                }
            }
            UnhookWindowsHookEx(hook);
        }

        sb.AppendLine();
        sb.AppendLine("【CapsLock 状态】");
        sb.AppendLine($"  当前是否开启: {((GetAsyncKeyState(0x14) & 1) != 0 ? "是" : "否")}");
        sb.AppendLine();
        sb.AppendLine("【结论】");
        sb.AppendLine(problems == 0
            ? "  环境正常。若组合键仍无反应，请在“设置 → 显示按键反馈”打开开关后再试："
              + "屏幕角落出现功能名就说明按键被识别了。"
            : $"  发现 {problems} 项问题，请按上面的提示处理。");

        WriteAndFlush(sb.ToString());
        return problems;
    }

    /// <summary>
    /// 泵消息若干毫秒，让低级钩子回调有机会执行。
    ///
    /// 低级键盘钩子挂在**装钩子的线程**的消息队列上：不取消息就永远收不到事件。
    /// 这也是"自动化实测"能成立的前提。
    /// </summary>
    private static void PumpFor(int milliseconds)
    {
        var end = DateTime.Now.AddMilliseconds(milliseconds);
        while (DateTime.Now < end)
        {
            if (!PeekMessage(out _, IntPtr.Zero, 0, 0, 1)) Thread.Sleep(5);
        }
    }

    private static void WriteAndFlush(string text)
    {
        try { Console.Write(text); Console.Out.Flush(); } catch { }   /* 预期内：清理动作，失败无副作用 */
        try
        {
            SecureFile.AppendBlock(Path.Combine(AppPaths.DataDir, "diagnose.log"), text);
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }
}
