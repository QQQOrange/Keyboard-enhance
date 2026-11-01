using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 用「计划任务（最高权限）」做开机自启，替代 HKCU\...\Run。
///
/// ## 为什么需要它
///
/// 管理员模式下，`HKCU\Run` 里的自启项**每次登录都会弹一次 UAC** ——
/// 因为 Run 键无法携带"以最高权限启动"的意图，系统只能按普通权限拉起，
/// 再由程序自己 runas 一次。用户每天开机会被问一遍，体验极差。
///
/// 计划任务可以声明 `RunLevel = HighestAvailable`，由任务计划程序在登录时
/// **直接以提权身份**拉起，全程不弹 UAC。代价是：注册/删除任务需要管理员权限。
///
/// ## 设计约束
///
/// · 任务名固定，重复注册用 `/F` 覆盖 —— 程序被移动位置后能自愈；
/// · 只针对**当前用户**、只在**登录时**触发，不申请系统级驻留；
/// · 电池供电也启动（`DisallowStartIfOnBatteries=false`）—— 这是个常驻工具，
///   笔记本拔电后失去键盘增强比省那点电糟糕得多；
/// · 不设执行时限（`PT0S`）—— 否则任务计划程序会在 72 小时后强杀进程；
/// · 所有系统调用都可通过 <see cref="RunnerForTest"/> 注入，便于自检。
/// </summary>
public static class StartupTask
{
    /// <summary>任务名（也是任务计划程序里显示的名字）。</summary>
    public const string TaskName = "KeyboardEnhance";

    /// <summary>自启时带的参数：只驻留托盘，不弹主窗口。</summary>
    public const string StartupArgs = "--tray";

    /// <summary>
    /// 自检注入点：替换成假实现即可在不碰系统的前提下验证参数与 XML。
    /// 生产环境为 null。
    /// </summary>
    internal static Func<string[], (int Code, string Output)>? RunnerForTest { get; set; }

    /// <summary>
    /// 自检时必须只走 schtasks 分支 —— 否则在管理员身份下跑 `--selftest`
    /// 会真的往系统里注册一个开机任务。
    /// </summary>
    private static bool SkipCom => RunnerForTest != null;

    /// <summary>任务是否已存在。</summary>
    public static bool Exists()
    {
        /* COM 优先：不起外部进程、不受命令行黑名单影响。
         * 只有在 COM 不可用时才退回 schtasks。 */
        if (!SkipCom)
        {
            var svc = TryConnect();
            if (svc != null)
            {
                try
                {
                    dynamic root = svc.GetFolder("\\");
                    root.GetTask(TaskName);
                    return true;
                }
                catch { return false; }        // 不存在时 GetTask 抛 COM 异常
                finally { Release(svc); }
            }
        }

        var r = Run(new[] { "schtasks.exe", "/Query", "/TN", TaskName });
        return r.Code == 0;
    }

    /// <summary>
    /// 创建（或覆盖）任务。需要管理员权限。
    /// 返回 false 时调用方应回退到 Run 键。
    /// </summary>
    public static bool Create(string exePath, string? user = null)
    {
        string path = string.IsNullOrWhiteSpace(exePath) ? "" : exePath;
        if (path.Length == 0) return false;

        string who = user ?? CurrentUser();

        // ① COM：首选
        if (!SkipCom && CreateViaCom(path, who)) return true;

        // ② schtasks：COM 不可用（被策略禁用 / 精简系统）时的兜底
        return CreateViaSchtasks(path, who);
    }

    /// <summary>删除任务（需要管理员权限）。任务不存在也返回 true。</summary>
    public static bool Delete()
    {
        if (!SkipCom)
        {
            var svc = TryConnect();
            if (svc != null)
            {
                try
                {
                    dynamic root = svc.GetFolder("\\");
                    root.DeleteTask(TaskName, 0);
                    return true;
                }
                catch (Exception ex)
                {
                    // 任务不存在 = 已经是想要的状态；其它失败才继续往下兜底
                    CoreLog.Warn("COM 删除计划任务失败：" + ex.Message);
                }
                finally { Release(svc); }

                if (!Exists()) return true;
            }
        }

        var r = Run(new[] { "schtasks.exe", "/Delete", "/TN", TaskName, "/F" });
        return r.Code == 0;
    }

    /* ---------------- COM 实现 ---------------- */

    private static dynamic? TryConnect()
    {
        try
        {
            var t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) return null;
            dynamic svc = Activator.CreateInstance(t)!;
            svc.Connect();          // 本机无需参数；不需要管理员也能连上
            return svc;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("连接任务计划服务失败：" + ex.Message);
            return null;
        }
    }

    private static void Release(object? com)
    {
        try { if (com != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(com); } catch { }
    }

    /// <summary>
    /// 用任务计划 COM 接口注册。
    ///
    /// 常量（Task Scheduler 类型库）：
    ///   TASK_RUNLEVEL_HIGHEST = 1、TASK_LOGON_INTERACTIVE_TOKEN = 3、
    ///   TASK_TRIGGER_LOGON = 9、TASK_ACTION_EXEC = 0、
    ///   TASK_CREATE_OR_UPDATE = 6。
    /// </summary>
    private static bool CreateViaCom(string exe, string user)
    {
        var svc = TryConnect();
        if (svc == null) return false;

        try
        {
            dynamic root = svc.GetFolder("\\");
            dynamic td = svc.NewTask(0);

            td.RegistrationInfo.Description = "按键增强 开机自启（最高权限，不弹 UAC）";

            td.Principal.UserId = user;
            td.Principal.LogonType = 3;      // 交互式令牌：只在用户登录时运行
            td.Principal.RunLevel = 1;       // 最高权限：直接提权，不弹 UAC

            td.Settings.MultipleInstances = 0;          // IgnoreNew
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.AllowHardTerminate = false;
            td.Settings.StartWhenAvailable = true;
            td.Settings.ExecutionTimeLimit = "PT0S";     // 不设时限，否则 72 小时后被杀
            td.Settings.Enabled = true;
            td.Settings.Hidden = false;
            td.Settings.RunOnlyIfIdle = false;
            td.Settings.WakeToRun = false;

            dynamic trig = td.Triggers.Create(9);        // 登录时
            trig.UserId = user;
            trig.Enabled = true;

            dynamic act = td.Actions.Create(0);          // 启动程序
            act.Path = exe;
            act.Arguments = StartupArgs;

            /* RegisterTaskDefinition 在类型库里是 7 个参数（最后一个是 SDDL），
             * 但很多系统上按 6 个调用也能成功。两种都试一遍，
             * 避免因类型库版本差异导致"注册永远失败"。
             * 第三个参数 6 = TASK_CREATE_OR_UPDATE（同名覆盖）。 */
            try
            {
                root.RegisterTaskDefinition(TaskName, td, 6, user, null, 3);
            }
            catch
            {
                root.RegisterTaskDefinition(TaskName, td, 6, user, null, 3, null);
            }
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"COM 注册计划任务失败（将回退 schtasks）：{ex.Message}");
            return false;
        }
        finally
        {
            Release(svc);
        }
    }

    /// <summary>schtasks 兜底：写一份 UTF-16 清单再导入。</summary>
    private static bool CreateViaSchtasks(string path, string who)
    {
        string xml = BuildXml(path, who);
        string? tmp = null;
        try
        {
            tmp = Path.Combine(Path.GetTempPath(), $"ke-task-{Guid.NewGuid():N}.xml");

            /* 必须是 UTF-16：schtasks /XML 只认 Unicode 编码的清单，
             * 存成 UTF-8 会报"任务 XML 包含格式不正确的值"。 */
            File.WriteAllText(tmp, xml, new UnicodeEncoding(false, true));

            var r = Run(new[] { "schtasks.exe", "/Create", "/XML", tmp, "/TN", TaskName, "/F" });
            if (r.Code != 0)
                CoreLog.Warn($"注册计划任务失败（code={r.Code}）：{Trim(r.Output)}");
            return r.Code == 0;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("注册计划任务异常：" + ex.Message);
            return false;
        }
        finally
        {
            try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// 以提权方式删除任务（用于"关闭管理员模式"时清理遗留任务）。
    ///
    /// 会弹一次 UAC —— 但只在确实存在任务、且当前不是管理员时才会走到这里，
    /// 属于一次性收尾，不是日常路径。
    /// </summary>
    public static bool DeleteElevated()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /TN \"{TaskName}\" /F",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(15000);
            return p?.HasExited == true && p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            // 用户点了"否"也会走到这里（Win32Exception: 操作已被用户取消）
            CoreLog.Warn("提权删除计划任务失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>当前用户名（DOMAIN\User），计划任务的 UserId 用这个格式。</summary>
    public static string CurrentUser()
    {
        try { return WindowsIdentity.GetCurrent().Name; }
        catch { return Environment.UserName; }
    }

    /// <summary>
    /// 生成任务清单。抽成纯函数是为了能自检 —— 任务 XML 写错时
    /// schtasks 只会给一句"格式不正确的值"，根本不知道错在哪。
    /// </summary>
    public static string BuildXml(string exePath, string user)
    {
        string exe = Esc(exePath);
        string who = Esc(user);

        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>按键增强 开机自启（最高权限，不弹 UAC）</Description>
    <URI>\{TaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{who}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{who}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{exe}</Command>
      <Arguments>{StartupArgs}</Arguments>
    </Exec>
  </Actions>
</Task>";
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    private static string Trim(string s)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= 200 ? s : s[..200];
    }

    private static (int Code, string Output) Run(string[] args)
    {
        if (RunnerForTest != null) return RunnerForTest(args);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = args[0],
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            for (int i = 1; i < args.Length; i++) psi.ArgumentList.Add(args[i]);

            using var p = Process.Start(psi);
            if (p == null) return (-1, "");

            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, "timeout");
            }
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("调用 schtasks 失败：" + ex.Message);
            return (-1, ex.Message);
        }
    }
}
