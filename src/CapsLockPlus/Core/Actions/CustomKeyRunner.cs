using System.Diagnostics;
using System.Runtime.InteropServices;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Actions;

/// <summary>
/// 执行「自定义按键」：确保绑定的应用在运行，然后把指定的键发给它。
///
/// 为什么需要"先确保在运行"：这类绑定通常是给**常驻托盘**的程序用的
/// （微信、QQ、笔记软件）。它们没启动时直接发键是白发 —— 没有任何窗口接收。
/// 所以先按进程名查一遍，没在跑就先拉起来，等窗口出现再发。
///
/// 参考了 QuickSearch 的做法：
///   * 用 <c>ShellExecute</c>（ProcessStartInfo.UseShellExecute=true）启动，
///     这样 .lnk、协议、UWP 别名都能用；提权时加 <c>Verb="runas"</c>；
///   * 启动后**轮询等窗口**，而不是 sleep 一个固定值 —— 冷启动 200ms 到 5s 都可能；
///   * 抢前台用 AttachThreadInput 兜底（Windows 有前台锁定，直接 SetForegroundWindow 常被拒）。
/// </summary>
public static class CustomKeyRunner
{
    /// <summary>等窗口出现的上限。冷启动大程序（IDE、微信）5~6 秒是够的。</summary>
    public const int WaitWindowMs = 6000;

    /// <summary>轮询间隔。</summary>
    private const int PollMs = 100;

    // ---------------- 自检注入点 ----------------

    /// <summary>自检用：假装应用在/不在运行。null = 查真实进程。</summary>
    public static Func<string, bool>? IsRunningForTest { get; set; }

    /// <summary>自检用：拦截启动。返回 true = 假装启动成功。</summary>
    public static Func<CustomKeySpec, bool>? LaunchForTest { get; set; }

    /// <summary>自检用：拦截"发送按键"，记下发了什么。设置后不会真的按键。</summary>
    public static Action<string>? SendForTest { get; set; }

    /// <summary>自检用：拦截"找窗口"。返回 IntPtr.Zero 表示没找到。</summary>
    public static Func<string, IntPtr>? FindWindowForTest { get; set; }

    /// <summary>自检用：把等窗口的超时改短，别让自检真的等 6 秒。</summary>
    public static int WaitWindowMsForTest { get; set; } = WaitWindowMs;

    /// <summary>执行结果。</summary>
    public readonly record struct Result(bool Ok, string Message);

    // ---------------- 主流程 ----------------

    /// <summary>
    /// 执行一次自定义按键。
    ///
    /// 返回的 Message 是给用户看的一句话（调用方决定用提示条还是日志）。
    /// </summary>
    public static Result Run(CustomKeySpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Key))
            return new Result(false, "自定义按键还没设置要发送的键");
        if (string.IsNullOrWhiteSpace(spec.AppPath))
            return new Result(false, "自定义按键还没绑定应用");

        string proc = spec.ProcessName;
        if (proc.Length == 0)
            return new Result(false, "绑定的应用路径不对");

        // ① 先看它在不在跑
        bool running = IsRunningForTest?.Invoke(proc) ?? IsRunning(proc);
        string prefix;

        if (!running)
        {
            if (!Launch(spec, out string err))
                return new Result(false, $"启动「{proc}」失败：{err}");

            prefix = $"已启动「{proc}」";

            // ② 等窗口。等不到就到此为止 —— 没有窗口，键发出去也没人接。
            IntPtr hwnd = WaitForWindow(proc, WaitWindowMsForTest);
            if (hwnd == IntPtr.Zero)
                return new Result(true, $"{prefix}，但没等到它的窗口，按键没有发送");

            /* ③ 窗口建出来 ≠ 能收键盘：程序还在读配置、建 UI、加载数据。
             * QuickSearch 里这一拍默认 900ms 且可按程序调，这里保持一致。 */
            int extra = Math.Clamp(spec.WaitAfterLaunchMs, 0, 10000);
            if (extra > 0) Thread.Sleep(extra);

            return Deliver(spec, hwnd, prefix);
        }

        prefix = $"「{proc}」已在运行";
        IntPtr existing = FindWindowForTest?.Invoke(proc) ?? FindWindow(proc);
        return Deliver(spec, existing, prefix);
    }

    /// <summary>
    /// 发按键。
    ///
    /// 与 QuickSearch 的 <c>app_bring_front</c> 保持一致：
    ///   * 勾了 → 先把目标应用主窗口调到前台再发（有些键只在它激活时才有意义）；
    ///   * 没勾（默认）→ **不动它的窗口**，直接把键交给系统路由。
    ///     目标应用若把那个键注册成全局热键，在后台一样能收到。
    /// </summary>
    private static Result Deliver(CustomKeySpec spec, IntPtr hwnd, string prefix)
    {
        if (SendForTest != null)
        {
            SendForTest(spec.Key);
            return new Result(true, $"{prefix}，已发送 {ComboDisplay(spec.Key)}");
        }

        if (spec.BringFront && hwnd != IntPtr.Zero)
            WindowService.Activate(hwnd);

        SendCombo(spec.Key);
        return new Result(true,
            $"{prefix}，已发送 {ComboDisplay(spec.Key)}" + (spec.BringFront ? "（已切到前台）" : ""));
    }

    // ---------------- 进程 / 窗口 ----------------

    /// <summary>按进程名（不带 .exe）判断有没有在运行。</summary>
    public static bool IsRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("查进程失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>找应用的主窗口；找不到返回 Zero。</summary>
    public static IntPtr FindWindow(string processName)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("取主窗口失败：" + ex.Message);
        }

        /* MainWindowHandle 有时是 0（程序把主窗口交给子线程创建、
         * 或者窗口没有标题）。退回按 exe 名枚举顶层窗口。 */
        try
        {
            var hit = WindowService.EnumerateTopLevel()
                .FirstOrDefault(w => w.ExeName.Equals(processName + ".exe",
                    StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit.Handle;
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "按进程名查找顶层窗口"); }

        return IntPtr.Zero;
    }

    /// <summary>轮询等窗口出现，超时返回 Zero。</summary>
    public static IntPtr WaitForWindow(string processName, int timeoutMs)
    {
        if (FindWindowForTest != null) return FindWindowForTest(processName);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var h = FindWindow(processName);
            if (h != IntPtr.Zero) return h;
            Thread.Sleep(PollMs);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 启动应用。
    ///
    /// 用 UseShellExecute=true：.lnk 快捷方式、商店应用别名、带工作目录的程序
    /// 都能正确启动（Process.Start 的 CreateProcess 路径对 .lnk 会直接失败）。
    /// </summary>
    private static bool Launch(CustomKeySpec spec, out string error)
    {
        error = "";

        if (LaunchForTest != null)
            return LaunchForTest(spec) || Fail(out error, "被自检拦截");

        try
        {
            var psi = new ProcessStartInfo(spec.AppPath)
            {
                UseShellExecute = true,
                WorkingDirectory = SafeDirectoryOf(spec.AppPath),
            };
            if (spec.RunAsAdmin) psi.Verb = "runas";   // 触发 UAC
            Process.Start(psi);
            CoreLog.Info($"自定义按键：已启动 {spec.AppPath}（管理员：{spec.RunAsAdmin}）");
            return true;
        }
        catch (Exception ex)
        {
            // 用户在 UAC 上点"否"时是 Win32Exception（1223 = 操作被取消）
            error = ex is System.ComponentModel.Win32Exception w && w.NativeErrorCode == 1223
                ? "你取消了管理员授权"
                : ex.Message;
            CoreLog.Warn($"自定义按键：启动失败 {spec.AppPath} —— {error}");
            return false;
        }
    }

    private static bool Fail(out string error, string message)
    {
        error = message;
        return false;
    }

    private static string SafeDirectoryOf(string path)
    {
        try
        {
            return Path.GetDirectoryName(path) ?? "";
        }
        catch
        {
            return "";
        }
    }

    // ---------------- 发键 ----------------

    /// <summary>
    /// 把组合键拆成"修饰键虚拟键码 + 主键虚拟键码"。
    ///
    /// 两种写法都认：
    ///   * <c>ctrl+shift+k</c> —— 捕获窗口给的就是这种；
    ///   * <c>caps_ctrl_shift_k</c> —— 引擎内部的动作 ID 形式（带触发键前缀）。
    /// 只认一种的话，用户在两种入口录同一个键会得到不同结果。
    /// </summary>
    public static (int[] Modifiers, int Vk) ResolveCombo(string combo)
    {
        string s = (combo ?? "").Trim();
        if (s.StartsWith("caps_", StringComparison.OrdinalIgnoreCase)) s = s["caps_".Length..];

        var parts = s.Split(new[] { '+', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (Array.Empty<int>(), 0);

        var mods = KeyModifiers.None;
        int i = 0;
        for (; i < parts.Length - 1; i++)          // 最后一段一定是键名
        {
            var m = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "lctrl" or "rctrl" => KeyModifiers.Ctrl,
                "shift" or "lshift" or "rshift" => KeyModifiers.Shift,
                "alt" or "lalt" or "ralt" => KeyModifiers.Alt,
                "win" or "lwin" or "rwin" => KeyModifiers.Win,
                _ => KeyModifiers.None,
            };
            if (m == KeyModifiers.None) break;
            mods |= m;
        }

        string key = i < parts.Length ? parts[i] : "";
        int vk = KeyName.VkOf(key) ?? 0;

        var list = new List<int>();
        if (mods.HasFlag(KeyModifiers.Ctrl)) list.Add(VK.Control);
        if (mods.HasFlag(KeyModifiers.Shift)) list.Add(VK.Shift);
        if (mods.HasFlag(KeyModifiers.Alt)) list.Add(VK.Menu);      // VK.Menu 就是通用 Alt
        if (mods.HasFlag(KeyModifiers.Win)) list.Add(VK.LWin);
        return (list.ToArray(), vk);
    }

    /// <summary>给人看的写法：Ctrl + Shift + K。</summary>
    public static string ComboDisplay(string combo)
    {
        var (mods, vk) = ResolveCombo(combo);
        if (vk == 0) return combo ?? "";

        var parts = new List<string>();
        if (mods.Contains(VK.Control)) parts.Add("Ctrl");
        if (mods.Contains(VK.Shift)) parts.Add("Shift");
        if (mods.Contains(VK.Menu)) parts.Add("Alt");
        if (mods.Contains(VK.LWin)) parts.Add("Win");

        string name = KeyName.NameOf(vk) ?? "";
        parts.Add(name.Length > 0 ? KeyName.Display(name) : $"VK{vk:X2}");
        return string.Join(" + ", parts);
    }

    /// <summary>抢前台后发键（SendInput）。</summary>
    private static void SendCombo(string combo)
    {
        var (mods, vk) = ResolveCombo(combo);
        if (vk == 0) return;
        InputSender.WithModifiers(mods, () => InputSender.Tap(vk));
    }
}
