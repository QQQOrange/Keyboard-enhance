using System;
using System.Linq;
using System.Text;
using CapsLockPlus.Core.Settings;
using MacroModel = CapsLockPlus.Core.Settings.Macro;

namespace CapsLockPlus.Core;

/// <summary>
/// `--dump-macros [文件]`：只读打印宏及其步骤，排查“宏播放没效果”。
/// settings.json 是加密信封，不能用记事本看；这个命令把步骤的键名、按下/抬起、
/// 延迟都列出来，才能判断是宏录错了、还是播放时被目标程序过滤了。
/// </summary>
internal static class MacroDump
{
    public static int Run(string[] args)
    {
        string? path = ValueOf(args);
        if (path == null) path = AppPaths.SettingsFile;
        else if (!System.IO.Path.IsPathRooted(path))
            path = System.IO.Path.Combine(AppPaths.DataDir, path);

        if (!SettingsStore.TryLoadFile(path, out var settings, out string error))
        {
            Console.Out.WriteLine("读不出来：" + error + Environment.NewLine + "  " + path);
            return 2;
        }

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=== 宏清单 ===");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("文件: " + path);
        sb.AppendLine("宏数量: " + (settings.Macros?.Count ?? 0));

        foreach (var macro in settings.Macros ?? new System.Collections.Generic.List<MacroModel>())
        {
            sb.AppendLine();
            sb.AppendLine($"--- {macro.Name}  id={macro.Id}  repeat={macro.Repeat}  steps={macro.Steps.Count} ---");
            for (int i = 0; i < macro.Steps.Count; i++)
            {
                var step = macro.Steps[i];
                sb.Append("  ").Append(i.ToString().PadLeft(3)).Append("  ").Append(step.Kind);
                if (!string.IsNullOrEmpty(step.Key)) sb.Append("  key=").Append(step.Key);
                if (step.DelayMs > 0) sb.Append("  delay=").Append(step.DelayMs);
                if (!string.IsNullOrEmpty(step.Text)) sb.Append("  text=").Append(step.Text);
                if (!string.IsNullOrEmpty(step.ActionId)) sb.Append("  action=").Append(step.ActionId);
                if (!string.IsNullOrEmpty(step.ActionArg)) sb.Append("  arg=").Append(step.ActionArg);
                sb.AppendLine();
            }
        }

        string output = sb.ToString();
        Console.Out.Write(output);
        return 0;
    }

    private static string? ValueOf(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--dump-macros", StringComparison.OrdinalIgnoreCase)) continue;
            string next = args[i + 1];
            return next.StartsWith("--", StringComparison.Ordinal) ? null : next;
        }
        return null;
    }
}
