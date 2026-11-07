using System;
using System.IO;
using System.Text;
using CapsLockPlus.Core.Security;

namespace CapsLockPlus.Core;

/// <summary>
/// `--export-log [输出文件]`：把加密日志导出成一份**明文**文件，方便发出去排查。
///
/// 为什么需要它：日志在磁盘上是密文（CLP2: 记录），用户想反馈问题时
/// 直接把 app.log 发出来只会得到一堆乱码。这个开关用程序自己的密钥解密，
/// 把 app.log / hookhost.log / diagnose.log 拼成一份可读文本。
/// </summary>
public static class LogExport
{
    public static int Run(string[] args)
    {
        string? output = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals("--export-log", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                output = args[i + 1];
            break;
        }

        output ??= Path.Combine(Path.GetTempPath(),
            $"KeyboardEnhance-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        var sb = new StringBuilder();
        sb.AppendLine("=== 按键增强 日志导出 ===");
        sb.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"数据目录: {AppPaths.DataDir}");
        sb.AppendLine();

        string logs = Path.Combine(AppPaths.DataDir, "logs");
        foreach (string name in new[] { "app.log", "hookhost.log", "diagnose.log" })
        {
            string path = Path.Combine(logs, name);
            if (!File.Exists(path)) continue;

            sb.AppendLine($"===== {name} =====");
            sb.AppendLine(SecureFile.ReadLog(path));
            sb.AppendLine();
        }

        string selftest = Path.Combine(AppPaths.DataDir, "selftest.log");
        if (File.Exists(selftest))
        {
            sb.AppendLine("===== selftest.log =====");
            sb.AppendLine(SecureFile.ReadLog(selftest));
            sb.AppendLine();
        }

        try
        {
            string? dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(output, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine(output);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("导出日志失败：" + ex.Message);
            return 1;
        }
    }
}
