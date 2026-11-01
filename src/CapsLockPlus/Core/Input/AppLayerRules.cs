using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 「按应用自动启用层」的匹配规则。
///
/// 抽成纯函数是为了能自检：匹配错了的表现是"在某个软件里按键突然变样"，
/// 现场很难判断是不是这条规则干的。
/// </summary>
public static class AppLayerRules
{
    /// <summary>
    /// 把进程名规范化成可比较的形式：只取文件名、补 .exe、小写。
    ///
    /// 用户可能填 `Code`、`code.exe`、`C:\...\Code.exe` 三种写法，
    /// 这里统一成 `code.exe`，避免"填了却不生效"。
    /// </summary>
    public static string Normalize(string? process)
    {
        if (string.IsNullOrWhiteSpace(process)) return "";

        string s = process.Trim().Trim('"');

        int slash = s.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) s = s[(slash + 1)..];

        s = s.ToLowerInvariant();
        if (s.Length == 0) return "";
        if (!s.EndsWith(".exe", StringComparison.Ordinal)) s += ".exe";
        return s;
    }

    /// <summary>返回命中的层 id；没命中返回 0。</summary>
    public static int Match(IEnumerable<AppLayerRule>? rules, string? foregroundProcess)
    {
        if (rules == null) return 0;

        string target = Normalize(foregroundProcess);
        if (target.Length == 0) return 0;

        foreach (var r in rules)
        {
            if (r == null || r.LayerId <= 0) continue;
            if (Normalize(r.Process) == target) return r.LayerId;
        }
        return 0;
    }
}
