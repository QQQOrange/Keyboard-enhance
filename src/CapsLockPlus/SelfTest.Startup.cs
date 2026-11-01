using System.IO;
using System.Text;
using System.Xml.Linq;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus;

public static partial class SelfTest
{
    /// <summary>
    /// 自启载体的选择逻辑。
    ///
    /// 背景：管理员模式下用 HKCU\Run 自启，每次登录都会弹 UAC；改用计划任务
    /// （HighestAvailable）可以静默提权启动。两种载体必须互斥，否则开机会起两个实例。
    /// </summary>
    private static void TestStartupModeDecision()
    {
        CheckEqual("自启：关闭时不用任何载体",
            StartupManager.Decide(false, false), StartupMode.None);
        CheckEqual("自启：关闭时即使管理员模式也不用任务",
            StartupManager.Decide(false, true), StartupMode.None);
        CheckEqual("自启：普通权限用注册表",
            StartupManager.Decide(true, false), StartupMode.RunKey);
        CheckEqual("自启：管理员模式用计划任务",
            StartupManager.Decide(true, true), StartupMode.Task);
    }

    /// <summary>
    /// 任务清单的生成。
    ///
    /// 这份 XML 写错时，schtasks 只会回一句"格式不正确的值"，
    /// 完全不知道错在哪 —— 所以必须在自检里把它拆开逐项验证。
    /// </summary>
    private static void TestStartupTaskXml()
    {
        const string exe = @"C:\Program Files\Key & Enhance\KeyboardEnhance.exe";
        string xml = StartupTask.BuildXml(exe, @"DESKTOP-1\Tom & Jerry");

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex)
        {
            Check($"任务 XML 可解析：{ex.Message}", false);
            return;
        }
        Check("任务 XML 可解析", true);

        var ns = doc.Root!.GetDefaultNamespace();
        string Val(string name) =>
            doc.Descendants(ns + name).FirstOrDefault()?.Value ?? "";

        CheckEqual("任务：声明最高权限", Val("RunLevel"), "HighestAvailable");
        CheckEqual("任务：登录时触发", Val("LogonTrigger").Length > 0 ? "有" : "无", "有");
        CheckEqual("任务：启动参数", Val("Arguments"), StartupTask.StartupArgs);
        Check("任务：exe 路径已转义",
            Val("Command") == exe, Val("Command"));
        Check("任务：用户名已转义",
            Val("UserId").Contains("&amp;") || Val("UserId").Contains("Tom & Jerry"),
            Val("UserId"));
        CheckEqual("任务：电池供电也启动", Val("DisallowStartIfOnBatteries"), "false");
        CheckEqual("任务：不设执行时限", Val("ExecutionTimeLimit"), "PT0S");
        Check("任务：任务名一致", Val("URI").Contains(StartupTask.TaskName), Val("URI"));
    }

    /// <summary>
    /// 注册/删除任务时的命令行与文件编码。
    ///
    /// 用注入的假 runner 跑：既能验证参数，又不需要真的提权。
    /// 特别验证 XML 以 UTF-16（带 BOM）落盘 —— schtasks 只认这种编码。
    /// </summary>
    private static void TestStartupTaskCommands()
    {
        var saved = StartupTask.RunnerForTest;
        try
        {
            var calls = new List<string[]>();
            bool firstByteOfBom = false;
            string capturedXml = "";

            StartupTask.RunnerForTest = args =>
            {
                calls.Add(args);
                if (args.Length >= 5 && args[1] == "/Create")
                {
                    byte[] head = File.ReadAllBytes(args[3]);
                    firstByteOfBom = head.Length > 1 && head[0] == 0xFF && head[1] == 0xFE;
                    capturedXml = File.ReadAllText(args[3], Encoding.Unicode);
                }
                return (0, "");
            };

            bool ok = StartupTask.Create(@"D:\x\KeyboardEnhance.exe", @"PC\U");
            Check("注册任务：调用成功", ok);
            Check("注册任务：覆盖同名任务", calls.Any(c => c.Contains("/F")), string.Join(" ", calls.LastOrDefault() ?? Array.Empty<string>()));
            Check("注册任务：带任务名", calls.Any(c => c.Contains(StartupTask.TaskName)));
            Check("注册任务：XML 为 UTF-16 且带 BOM", firstByteOfBom);
            Check("注册任务：XML 含完整路径",
                capturedXml.Contains(@"D:\x\KeyboardEnhance.exe"), capturedXml.Length.ToString());

            StartupTask.RunnerForTest = _ => (1, "拒绝访问");
            Check("注册任务：失败时返回 false",
                !StartupTask.Create(@"D:\x\KeyboardEnhance.exe", @"PC\U"));

            StartupTask.RunnerForTest = _ => (0, "");
            Check("删除任务：成功", StartupTask.Delete());
        }
        finally
        {
            StartupTask.RunnerForTest = saved;
        }
    }
}
