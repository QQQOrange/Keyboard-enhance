using System.Diagnostics;
using System.Security.Principal;

namespace CapsLockPlus.Core;

/// <summary>
/// 提权辅助。CapsLock+ 需要管理员权限才能操作“以管理员身份运行”的窗口，
/// 但为了能跑自检、也为了不在便携场景下强行弹 UAC，这里采用“启动后按需提权”：
/// 普通启动时如果当前不是管理员，就用 runas 重新拉起自己（只弹一次 UAC）。
/// </summary>
public static class ElevationHelper
{
    private const string ElevatedFlag = "--elevated";

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>返回 true 表示已经拉起了提权实例，当前进程应当退出。</summary>
    public static bool TryRelaunchElevated(string[] args)
    {
        if (IsElevated) return false;
        if (args.Any(a => a.Equals(ElevatedFlag, StringComparison.OrdinalIgnoreCase))) return false;
        if (args.Any(a => a.Equals("--noelevate", StringComparison.OrdinalIgnoreCase))) return false;
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase))) return false;

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(ElevatedFlag);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            // 用户拒绝了 UAC：继续以普通权限运行，功能照常，只是管不了提权窗口
            CoreLog.Warn("提权失败，将以普通权限继续运行：" + ex.Message);
            return false;
        }
    }
}
