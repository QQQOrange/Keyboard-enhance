using CapsLockPlus.Core.Localization;
using CapsLockPlus.Core.Windows;

namespace CapsLockPlus.Core.Macro;

/// <summary>
/// 宏条件求值时看到的外部状态快照。
///
/// 做成不可变快照而不是"边判断边问系统"，是为了：
///   · 同一次求值里多次读到的前台窗口是同一时刻的，不会前后矛盾；
///   · 求值逻辑是纯函数，自检里能直接构造，不需要真的有那个窗口。
/// </summary>
public readonly record struct MacroContext(string Process, string Title, string Clipboard)
{
    /// <summary>读一次真实状态。取不到的字段留空串，不影响其它条件。</summary>
    public static MacroContext Capture()
    {
        string process = "";
        string title = "";
        string clip = "";

        try
        {
            IntPtr hwnd = ForegroundWatcher.ForegroundWindow();
            process = ForegroundWatcher.ProcessNameOf(hwnd);
            title = ForegroundWatcher.TitleOf(hwnd);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "宏条件读取前台窗口");
        }

        try
        {
            /* 播放线程被显式设成 STA，所以能直接用 WPF 的 Clipboard。
             * 剪贴板被别的程序锁住时会抛 COMException —— 当作"读不到"而不是失败。 */
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA &&
                System.Windows.Clipboard.ContainsText())
            {
                clip = System.Windows.Clipboard.GetText() ?? "";
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "宏条件读取剪贴板");
        }

        return new MacroContext(process, title, clip);
    }
}

/// <summary>
/// 宏里的条件表达式：一个类型 + 一个比较值 + 是否取反。
///
/// 刻意只做"包含"这一种比较：
/// 完整正则/通配符听起来更强，但用户真正要的是"在编辑器里"、"标题里有「未保存」"，
/// 而写错一个正则的代价是宏静默走错分支 —— 比不支持更难查。
/// </summary>
public static class MacroCondition
{
    public const string App = "app";
    public const string Title = "title";
    public const string Clipboard = "clipboard";
    public const string ClipboardEmpty = "clipboardEmpty";

    /// <summary>可选的类型（界面下拉用）。</summary>
    public static readonly string[] Kinds = { App, Title, Clipboard, ClipboardEmpty };

    /// <summary>判断这个 if 步骤在当前状态下成立与否。</summary>
    public static bool Evaluate(Core.Settings.MacroStep step, MacroContext ctx)
        => Matches(step.Cond, step.CondValue, step.Not,
                   ctx.Process, ctx.Title, ctx.Clipboard);

    /// <summary>
    /// 纯函数版本（自检直接调这个）。
    ///
    /// 一条硬规则：**比较值为空时，含空判断恒为假**。
    /// 否则用户新建一个 if 还没来得及填值，条件就是"永远成立"，
    /// 宏会静默走错分支 —— 空值表现为"不成立"更容易被发现。
    /// </summary>
    public static bool Matches(string? kind, string? value, bool not,
        string? process, string? title, string? clipboard)
    {
        string v = (value ?? "").Trim();
        bool hit = (kind ?? App) switch
        {
            App => v.Length > 0 && Contains(process, v),
            Title => v.Length > 0 && Contains(title, v),
            Clipboard => v.Length > 0 && Contains(clipboard, v),
            ClipboardEmpty => string.IsNullOrEmpty(clipboard),
            _ => false,
        };
        return not ? !hit : hit;
    }

    private static bool Contains(string? haystack, string needle)
        => !string.IsNullOrEmpty(haystack) &&
           haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>条件的可读描述。</summary>
    public static string Describe(string? kind, string? value, bool not)
    {
        string body = (kind ?? App) switch
        {
            App => Loc.F("前台是「{0}」", Trim(value)),
            Title => Loc.F("标题含「{0}」", Trim(value)),
            Clipboard => Loc.F("剪贴板含「{0}」", Trim(value)),
            ClipboardEmpty => Loc.T("剪贴板为空"),
            _ => kind ?? "",
        };
        return not ? Loc.F("不满足：{0}", body) : body;
    }

    private static string Trim(string? s)
    {
        string t = (s ?? "").Trim();
        return t.Length <= 20 ? t : t[..20] + "…";
    }
}
