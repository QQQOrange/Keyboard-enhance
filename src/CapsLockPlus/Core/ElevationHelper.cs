using System.Diagnostics;
using System.Security.Principal;

namespace CapsLockPlus.Core;

/// <summary>
/// 提权辅助。
///
/// 为什么需要管理员权限：
///   * 普通进程的键盘钩子**收不到更高完整性级别窗口的输入**（UIPI 限制），
///     所以对着"以管理员身份运行"的程序按 Caps 组合键会完全没反应 ——
///     而且不报错，很难排查；
///   * 窗口绑定要操作提权程序的窗口也需要管理员。
///
/// 因此默认启动时如果当前不是管理员，就用 runas 重新拉起自己（只弹一次 UAC）。
/// 用户拒绝 UAC 时继续以普通权限运行，功能照常，只是管不了提权窗口。
///
/// ⚠ 关于"锁屏时也能用快捷键"：这件事**管理员权限做不到**。
///    锁屏会切到独立的 Winlogon 安全桌面，Windows 从设计上禁止普通进程
///    （包括管理员 + uiAccess）在那个桌面上安装键盘钩子或注入输入。
///    这是安全边界，不是权限高低的问题。详见 README 与 app.manifest。
/// </summary>
public static class ElevationHelper
{
    private const string ElevatedFlag = "--elevated";

    /// <summary>
    /// 诊断 / 自检类参数：这些命令不弹 UAC，否则会打断命令行流程
    /// （自检要立即返回结果，弹个 UAC 对话框就卡住了）。
    /// </summary>
    private static readonly string[] NoElevateArgs =
    {
        "--selftest", "--diag", "--render", "--render-diag",
        "--qbar", "--trace", "--svg2png", "--webdav-test",
        "--noelevate", "--elevated", "--updated",
    };

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
        if (args.Any(a => NoElevateArgs.Contains(a, StringComparer.OrdinalIgnoreCase)))
            return false;

        return RelaunchElevated(args, out _);
    }

    /// <summary>
    /// 主动请求提权重启（用户在设置里点"以管理员身份重启"时用）。
    /// </summary>
    /// <returns>true = 已拉起新实例，调用方应当退出当前进程。</returns>
    public static bool RelaunchElevated(string[] args, out string? error)
    {
        error = null;
        if (IsElevated)
        {
            error = "当前已经是管理员权限";
            return false;
        }

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            error = "取不到程序路径";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",       // 触发 UAC
            };
            // 把原有参数带过去，但去掉 --elevated / --noelevate 避免冲突
            foreach (var a in args)
            {
                if (a.Equals(ElevatedFlag, StringComparison.OrdinalIgnoreCase)) continue;
                if (a.Equals("--noelevate", StringComparison.OrdinalIgnoreCase)) continue;
                psi.ArgumentList.Add(a);
            }
            psi.ArgumentList.Add(ElevatedFlag);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            CoreLog.Warn("提权失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>说明当前权限状态下哪些功能受限（给界面显示用）。</summary>
    public static string PermissionSummary() => IsElevated
        /* 两段各自走一次翻译再用 Loc.Cat 拼：语言包是按"一句一条"组织的，
         * 直接写成相邻字面量拼接的话，编译器会把两段合成一条**新**字符串，
         * 那条字符串在语言包里根本不存在 → 英文界面会原样显示中文。
         * 而用 `+` 拼两句译文又会缺空格（`recommended.Hotkeys`），所以走 Loc.Cat。 */
        ? Localization.Loc.Cat(
            Localization.Loc.T("管理员权限：可以操作管理员程序的窗口，键盘钩子能收到所有普通窗口的输入。"),
            Localization.Loc.T("但锁屏状态下快捷键无法使用 —— 那是 Windows 的安全桌面限制，管理员权限也绕不过。"))
        : Localization.Loc.Cat(
            Localization.Loc.T("普通权限：对着「以管理员身份运行」的程序按快捷键不会有反应，建议以管理员身份运行。"),
            Localization.Loc.T("另外锁屏状态下快捷键同样无法使用（Windows 安全限制）。"));
}