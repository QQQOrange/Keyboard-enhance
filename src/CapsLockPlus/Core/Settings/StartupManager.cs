using Microsoft.Win32;

namespace CapsLockPlus.Core.Settings;

/// <summary>开机自启当前由谁承载。</summary>
public enum StartupMode
{
    /// <summary>没有自启项。</summary>
    None,
    /// <summary>HKCU\...\Run（普通权限，管理员模式下每次登录会弹 UAC）。</summary>
    RunKey,
    /// <summary>计划任务（最高权限，登录时静默提权启动）。</summary>
    Task,
}

/// <summary>
/// 开机自启。
///
/// 两种载体：
///   · **Run 键**（HKCU\...\Run）—— 不需要管理员权限，但无法表达"以管理员启动"，
///     管理员模式下每次登录都会弹一次 UAC；
///   · **计划任务**（<see cref="StartupTask"/>）—— 需要管理员权限注册，
///     但登录时可静默提权启动，不弹 UAC。
///
/// 由 <see cref="Sync"/> 按"是否开启管理员模式"自动选择，并在两者间互斥切换，
/// 避免同时存在两条自启项导致开两个实例。
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>注册表值名（新名称）。</summary>
    private const string ValueName = "KeyboardEnhance";

    /// <summary>
    /// 改名前的注册表值名。
    ///
    /// 软件从 CapsLock+ 改名为按键增强后，用户机器上可能还留着一条
    /// 指向旧 exe 路径的自启项。不清理的话，开机会弹"找不到文件"，
    /// 而设置页又显示"未启用"，用户会莫名其妙。
    /// 所以启动时顺手把旧值删掉（见 <see cref="MigrateLegacyValue"/>）。
    /// </summary>
    private const string LegacyValueName = "CapsLockPlus";

    /// <summary>是否存在**任意**一种自启项（计划任务也算）。</summary>
    public static bool IsEnabled()
    {
        try { if (StartupTask.Exists()) return true; } catch { }
        return IsRunKeySet();
    }

    /// <summary>Run 键存在，且指向的文件真的存在。</summary>
    public static bool IsRunKeySet()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string s) return false;

            /* 不只看有没有值，还要看**指向的文件真的存在**。
             * 用户可能手工把 exe 挪走了，那种情况下"已启用"是假象。 */
            string? path = ExtractExePath(s);
            return path != null && File.Exists(path);
        }
        catch { return false; }
    }

    /// <summary>当前实际生效的自启方式。</summary>
    public static StartupMode CurrentMode()
    {
        try { if (StartupTask.Exists()) return StartupMode.Task; } catch { }
        return IsRunKeySet() ? StartupMode.RunKey : StartupMode.None;
    }

    /// <summary>
    /// 按设置推出"应该用哪种方式"。
    ///
    /// 纯函数、不看当前状态 —— 这样自检可以直接验证四种组合，
    /// 不需要真的去注册计划任务。
    /// </summary>
    public static StartupMode Decide(bool enabled, bool adminMode)
        => !enabled ? StartupMode.None
           : adminMode ? StartupMode.Task
           : StartupMode.RunKey;

    /// <summary>从命令行里取出 exe 路径（可能带引号、可能带参数）。</summary>
    private static string? ExtractExePath(string commandLine)
    {
        string s = commandLine.Trim();
        if (s.Length == 0) return null;

        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }

        // 无引号：取到第一个空格前的部分
        int sp = s.IndexOf(' ');
        return sp > 0 ? s[..sp] : s;
    }

    /// <summary>
    /// 清理改名前的自启项。
    ///
    /// 只在**新值还没设置**时才迁移，避免覆盖用户当前的选择。
    /// </summary>
    public static void MigrateLegacyValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (key.GetValue(ValueName) != null) return;      // 新值已在，不动

            if (key.GetValue(LegacyValueName) != null)
            {
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                CoreLog.Info("已清理旧的 CapsLockPlus 开机自启项");
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("清理旧自启项失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 开机自启该指向哪个 exe。
    ///
    /// 优先**启动器**（KeyboardEnhance.exe）：它先检查 .NET 运行时，
    /// 缺了会弹中文提示 —— 否则开机后什么都没发生，用户根本不知道原因。
    /// 没有启动器（老版本 / 手工只拷了主程序）时退回当前进程。
    /// </summary>
    private static string PreferredLaunchPath()
    {
        try
        {
            // 只有一个 exe，自启就写它自己。
            // 早期版本这里会去找"启动器"，现在没有那东西了。
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

        return Environment.ProcessPath ?? "";
    }

    /// <summary>
    /// 兼容旧调用：只改 Run 键。
    ///
    /// 新代码请用 <see cref="Sync"/> —— 它会根据"是否管理员模式"选择载体，
    /// 并在两种载体之间互斥切换。
    /// </summary>
    public static void SetEnabled(bool enabled, string? exePath = null)
        => SetRunValue(enabled, exePath);

    private static void SetRunValue(bool enabled, string? exePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;
            if (enabled)
            {
                string path = exePath ?? PreferredLaunchPath();
                if (!string.IsNullOrEmpty(path)) key.SetValue(ValueName, $"\"{path}\" {StartupTask.StartupArgs}");
            }
            else
            {
                key.DeleteValue(ValueName, false);
                // 顺手清掉可能残留的旧值，避免"关了自启却还在启动"
                key.DeleteValue(LegacyValueName, false);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Error("设置开机自启失败", ex);
        }
    }

    /* ---------------- 两种载体的同步 ---------------- */

    private const string StateKey = @"Software\KeyboardEnhance";
    private const string TaskExeValue = "TaskExe";

    /// <summary>
    /// 是否需要重建/切换自启项。
    ///
    /// 三种情况要修：
    ///   · 应该用任务、实际用注册表（刚打开管理员模式）；
    ///   · 应该用注册表、实际是任务（刚关闭管理员模式，任务还在）；
    ///   · 任务指向的 exe 换了位置（用户挪了安装目录）。
    ///
    /// 都不是就什么都不做 —— 避免每次启动都重建任务。
    /// </summary>
    public static bool NeedsRepair(bool enabled, bool adminMode, string? exePath = null)
    {
        var desired = Decide(enabled, adminMode);
        if (desired != CurrentMode()) return true;

        if (desired == StartupMode.Task)
        {
            string exe = exePath ?? PreferredLaunchPath();
            return !string.Equals(ReadTaskExe(), exe, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// 让"实际自启方式"与设置一致。幂等，启动时与切换开关时都可调用。
    ///
    /// 关键约束：**两种载体互斥**。任务和 Run 键同时存在会在开机时拉起两个实例，
    /// 所以每次切换都要把另一种删干净。
    /// </summary>
    public static StartupMode Sync(bool enabled, bool adminMode, string? exePath = null)
    {
        var desired = Decide(enabled, adminMode);
        string exe = exePath ?? PreferredLaunchPath();

        if (desired == StartupMode.None)
        {
            SetRunValue(false);
            RemoveTask(allowElevated: true);
            CoreLog.Info("开机自启：已关闭");
            return StartupMode.None;
        }

        if (desired == StartupMode.Task)
        {
            if (ElevationHelper.IsElevated && EnsureTask(exe))
            {
                /* 任务已经能静默提权启动，Run 键必须删掉，
                 * 否则同一个账号登录时会启动两次（第二个实例被单实例逻辑踢掉，
                 * 但会闪一下托盘图标，看起来像"自己重启了"）。 */
                SetRunValue(false);
                CoreLog.Info("开机自启：使用计划任务（登录时静默提权，不弹 UAC）");
                return StartupMode.Task;
            }

            /* 注册失败：没提权（用户拒绝了 UAC）或被组策略禁止。
             * 退回 Run 键，保证"至少还能自启"；下次以管理员启动时会自动转回任务。 */
            if (!StartupTask.Exists())
            {
                SetRunValue(true, exe);
                CoreLog.Warn("开机自启：计划任务不可用，退回注册表自启（登录时会弹一次 UAC）");
                return StartupMode.RunKey;
            }
            return StartupMode.Task;      // 已有任务：不要再叠加 Run 键
        }

        // 普通权限模式
        SetRunValue(true, exe);
        RemoveTask(allowElevated: true);
        CoreLog.Info("开机自启：使用注册表（普通权限）");
        return StartupMode.RunKey;
    }

    /// <summary>
    /// 确保任务存在且指向当前 exe。
    ///
    /// 每次启动都对一次路径：用户可能把 exe 挪了位置，或换了安装目录。
    /// 用注册表记号避免每次都重建（重建本身只要几十毫秒，但会让
    /// 任务计划程序里的"上次运行结果"一直重置）。
    /// </summary>
    private static bool EnsureTask(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;

        bool samePath = string.Equals(ReadTaskExe(), exe, StringComparison.OrdinalIgnoreCase);
        if (samePath && StartupTask.Exists()) return true;

        if (!StartupTask.Create(exe)) return false;
        WriteTaskExe(exe);
        return true;
    }

    private static bool RemoveTask(bool allowElevated)
    {
        try
        {
            if (!StartupTask.Exists()) { ClearTaskExe(); return true; }

            bool ok = ElevationHelper.IsElevated
                ? StartupTask.Delete()
                : (allowElevated && StartupTask.DeleteElevated());

            if (ok) ClearTaskExe();
            else CoreLog.Warn("计划任务未删除（缺少权限或被拒绝），下次以管理员启动时会再试");
            return ok;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("删除计划任务失败：" + ex.Message);
            return false;
        }
    }

    private static string ReadTaskExe()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(StateKey);
            return (k?.GetValue(TaskExeValue) as string) ?? "";
        }
        catch { return ""; }
    }

    private static void WriteTaskExe(string exe)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(StateKey);
            k?.SetValue(TaskExeValue, exe);
        }
        catch { }
    }

    private static void ClearTaskExe()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(StateKey, writable: true);
            k?.DeleteValue(TaskExeValue, throwOnMissingValue: false);
        }
        catch { }
    }
}
