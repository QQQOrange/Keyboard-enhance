using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

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

        // 3) 实测：让用户按键，统计钩子能否收到
        if (hook != IntPtr.Zero)
        {
            sb.AppendLine($"【实测】请在 {listenSeconds} 秒内随便敲几个键（建议敲一下 CapsLock）…");
            WriteAndFlush(sb.ToString());
            sb = new StringBuilder();

            var end = DateTime.Now.AddSeconds(listenSeconds);
            while (DateTime.Now < end)
            {
                if (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    // 只是为了让钩子有机会被调用
                }
                else
                {
                    Thread.Sleep(10);
                }
            }

            sb.AppendLine($"  收到按键事件：{_observed} 个");
            if (_observed == 0)
            {
                sb.AppendLine("  ✗ 一个都没收到 —— 全局键盘钩子收不到任何按键。");
                sb.AppendLine("    请检查：");
                sb.AppendLine("      1. 是否有安全软件（火绒、360、卡巴斯基等）拦截了键盘钩子；");
                sb.AppendLine("      2. 是否用了“筛选键/粘滞键”等辅助功能；");
                sb.AppendLine("      3. 是否在远程桌面/虚拟机里运行（需在物理桌面运行）；");
                sb.AppendLine("      4. 键盘是否被其他改键软件独占（如 PowerToys Keyboard Manager、某些游戏外设驱动）。");
                problems++;
            }
            else
            {
                sb.AppendLine("  ✓ 钩子能正常收到按键。");
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

    private static void WriteAndFlush(string text)
    {
        try { Console.Write(text); Console.Out.Flush(); } catch { }   /* 预期内：清理动作，失败无副作用 */
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "diagnose.log"), text);
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
    }
}
