using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core;

/// <summary>
/// `--dump-snippets [文件]`：只读地把别名（文本片段）清单打出来。
///
/// 为什么需要：`settings.json` 是加密信封，用记事本/grep 看不到任何内容；
/// 用户反馈"别名不见了"时，只能靠这个命令把当前配置和各个历史副本逐份对比，
/// 才谈得上判断是"没保存"还是"被覆盖"。
///
/// 不传文件 = 看当前正在用的配置；传了文件 = 看那一份（比如 <c>settings.json.bak</c>）。
/// </summary>
internal static class SnippetDump
{
    public static int Run(string[] args)
    {
        string? path = ValueOf(args);
        if (path == null)
        {
            path = AppPaths.SettingsFile;
        }
        else if (!System.IO.Path.IsPathRooted(path))
        {
            /* 相对路径按数据目录解析：用户最常写的就是 `settings.json.bak`。 */
            path = System.IO.Path.Combine(AppPaths.DataDir, path);
        }

        if (!SettingsStore.TryLoadFile(path, out var settings, out string error))
        {
            string msg = "读不出来：" + error + Environment.NewLine + "  " + path;
            Console.Out.WriteLine(msg);
            Write(msg);
            return 2;
        }

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=== 别名清单 ===");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("文件: " + path);
        sb.AppendLine("大小: " + SafeSize(path) + " 字节");
        sb.AppendLine("名字: " + System.IO.Path.GetFileName(path));

        var opt = settings.Hotstring ?? new HotstringOptions();
        sb.AppendLine("前缀: " + (opt.EffectivePrefixes.Count == 0
            ? "（无）"
            : string.Join(" ", opt.EffectivePrefixes.Select(p => "「" + p + "」"))));
        sb.AppendLine("空格展开: " + (opt.ExpandOnSpace ? "开" : "关"));

        var all = settings.Global?.Snippets ?? new List<Snippet>();
        sb.AppendLine("条数: " + all.Count);
        sb.AppendLine("分组: " + string.Join(" / ", settings.Global?.SnippetGroups ?? new List<string>()));
        sb.AppendLine();

        foreach (var group in all.GroupBy(s => (s.Group ?? "").Trim()).OrderBy(g => g.Key))
        {
            sb.AppendLine("--- " + (group.Key.Length == 0 ? "（未分组）" : group.Key) + "（" + group.Count() + " 条）---");
            foreach (var s in group.OrderBy(s => s.Trigger, StringComparer.Ordinal))
            {
                string trigger = s.Trigger ?? "";
                string text = (s.Text ?? "").Replace("\r", "").Replace("\n", "⏎");
                if (text.Length > 24) text = text[..24] + "…";
                sb.AppendLine("  " + trigger.PadRight(12) +
                              " 用" + s.UseCount.ToString().PadLeft(3) +
                              (s.Enabled ? "" : " [停用]") +
                              " 内容(" + (s.Text ?? "").Length + "字): " + text);
            }
            sb.AppendLine();
        }

        string output = sb.ToString();
        Console.Out.Write(output);
        Write(output);
        return 0;
    }

    /// <summary>`--dump-snippets` 后面那个参数（没有就返回 null）。</summary>
    private static string? ValueOf(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--dump-snippets", StringComparison.OrdinalIgnoreCase)) continue;
            string next = args[i + 1];
            return next.StartsWith("--", StringComparison.Ordinal) ? null : next;
        }
        return null;
    }

    private static long SafeSize(string path)
    {
        try { return new System.IO.FileInfo(path).Length; }
        catch { return 0; }
    }

    private static void Write(string text)
    {
        try
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clp-snippets.txt");
            System.IO.File.WriteAllText(path, text, Encoding.UTF8);
            Console.Out.WriteLine("（已写入 " + path + "）");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "写出别名清单"); }
    }
}
