using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core;

/// <summary>
/// `--audit-qbar [文件]`：只读检查“搜索指令 / 启动程序 / 常用网址”三张表的关键字。
/// 关键字是 Qbar 唯一的分派依据，重复会让其中一张表永远选不到；升级后用这个命令
/// 检查老配置是否还有历史遗留的重复项。
/// </summary>
internal static class QbarKeyAudit
{
    public static int Run(string[] args)
    {
        string? path = ValueOf(args);
        if (path == null) path = AppPaths.SettingsFile;
        else if (!System.IO.Path.IsPathRooted(path))
            path = System.IO.Path.Combine(AppPaths.DataDir, path);

        if (!SettingsStore.TryLoadFile(path, out var s, out string error))
        {
            Console.Out.WriteLine("读不出来：" + error + Environment.NewLine + "  " + path);
            return 2;
        }

        var rows = new List<(string Table, string Key, string Value)>();
        foreach (var e in s.Search.Where(x => !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase)))
            rows.Add(("搜索指令", e.Key, e.Value));
        foreach (var e in s.Run) rows.Add(("启动程序", e.Key, e.Value));
        foreach (var e in s.Web) rows.Add(("常用网址", e.Key, e.Value));

        var dup = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Key))
            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Table).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 ||
                        g.Count() > 1)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=== Qbar 关键字审计 ===");
        sb.AppendLine("文件: " + path);
        sb.AppendLine($"搜索指令: {s.Search.Count(x => !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase))} 条");
        sb.AppendLine($"启动程序: {s.Run.Count} 条");
        sb.AppendLine($"常用网址: {s.Web.Count} 条");
        sb.AppendLine($"重复关键字: {dup.Count} 组");

        foreach (var g in dup)
        {
            sb.AppendLine("  ! " + g.Key);
            foreach (var row in g)
                sb.AppendLine($"      {row.Table}  =  {row.Value}");
        }

        Console.Out.Write(sb.ToString());
        return dup.Count == 0 ? 0 : 1;
    }

    private static string? ValueOf(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--audit-qbar", StringComparison.OrdinalIgnoreCase)) continue;
            string next = args[i + 1];
            return next.StartsWith("--", StringComparison.Ordinal) ? null : next;
        }
        return null;
    }
}
