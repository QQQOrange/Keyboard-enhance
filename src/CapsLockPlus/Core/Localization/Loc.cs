using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace CapsLockPlus.Core.Localization;

/// <summary>一种可用语言。</summary>
public sealed record LocLanguage(string Code, string DisplayName, bool IsBuiltIn);

/// <summary>
/// 界面语言的唯一入口。
///
/// ── 为什么"用原文当键"（gettext 风格）──────────────────────────
///
/// 传统做法是给每条文案起个 ID（`settings.save`），代码里写 `T("settings.save")`。
/// 本项目的实际情况是：文案几乎全是中文短句、单人维护、只有两三种语言。
/// 起 ID 的代价是每加一句界面文字都要"先想名字、再登记、再翻译"，
/// 忘了登记就静默退化成 ID；而用**中文原文当键**则：
///   · 代码里读得懂：`Loc.T("保存")` 一眼知道是什么；
///   · 漏翻自动兜底：英文包里没有这条 → 显示原文，不会出现 `settings.save`；
///   · 改文案不用同步改 ID（改完中文，英文条目自动失效回退，一眼能看出来）。
///
/// 代价是"同一句中文只能有一种译法"。对本项目（按钮/提示/说明短句）完全够用。
///
/// ── 语言包 ────────────────────────────────────────────────────
///
/// `Assets/Lang/<code>.json`，**嵌入资源**（单文件发布后也在 exe 里）：
/// <code>
/// {
///   "__name": "English",          // 设置界面里显示的名字（必填）
///   "保存": "Save",               // 原文 → 译文
///   "已复制 {0} 项": "Copied {0} item(s)"
/// }
/// </code>
/// 扫描到哪个文件就有哪种语言，**加语言不用改代码**（见 README）。
/// 中文包（zh-CN）可以只写 `__name`：查不到条目时 T() 直接返回原文，
/// 而原文就是中文。
///
/// ── 实时切换 ──────────────────────────────────────────────────
///
/// · XAML：`Text="{loc:T '保存'}"` —— 标记扩展返回一个绑定到
///   <see cref="LocToken"/> 的 Binding，切语言时令牌发通知，界面自己重画；
/// · C#：`Loc.T("保存")`。**一次性的**文本（对话框、通知、日志）不用管；
///   常驻的列表/菜单在 <see cref="Changed"/> 里重建（各 View 自己订阅）。
/// </summary>
public static class Loc
{
    /// <summary>默认语言。内置中文包，任何语言包都缺条目时也退回中文原文。</summary>
    public const string DefaultLanguage = "zh-CN";

    /// <summary>语言包里的元数据键：这种语言在设置界面里的显示名。</summary>
    private const string NameKey = "__name";

    /// <summary>语言包里的元数据键：对应的 <see cref="CultureInfo"/>（可选）。</summary>
    private const string CultureKey = "__culture";

    private static Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private static volatile string _language = DefaultLanguage;
    private static CultureInfo _culture = CultureInfo.GetCultureInfo(DefaultLanguage);
    private static List<LocLanguage>? _available;

    /// <summary>当前语言代码（zh-CN / en-US …）。</summary>
    public static string Language => _language;

    /// <summary>
    /// 当前语言的 <see cref="CultureInfo"/>。**日期、时间、数字、货币一律用它**格式化
    /// （`DateTime.Now.ToString("d", Loc.Culture)`），这样英文界面下日期是
    /// MM/dd/yyyy、中文界面下是 yyyy/M/d，跟着语言一起切。
    /// </summary>
    public static CultureInfo Culture => _culture;

    /// <summary>语言切换后触发。常驻文本（菜单/列表/托盘）在订阅里重建。</summary>
    public static event Action? Changed;

    /// <summary>可用语言（中文固定排第一）。</summary>
    public static IReadOnlyList<LocLanguage> Available => _available ??= Discover();

    /// <summary>
    /// 取译文。找不到（或译文为空）时**返回原文** —— 中文界面就是靠这条兜底的，
    /// 所以 zh-CN 包可以只有一行 `__name`。
    /// </summary>
    public static string T(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return _map.TryGetValue(text, out string? v) && !string.IsNullOrEmpty(v) ? v : text;
    }

    /// <summary>带参数的译文：`Loc.F("已复制 {0} 项", n)`。格式化用 <see cref="Culture"/>。</summary>
    public static string F(string text, params object?[] args)
    {
        string fmt = T(text);
        try
        {
            return string.Format(_culture, fmt, args);
        }
        catch (FormatException)
        {
            // 译文里占位符写错了也不能让界面崩：原样返回，自检会盯着这种包
            return fmt;
        }
    }

    // ── 日期时间 ──────────────────────────────────────────────────
    //
    // 这几个方法存在的意义：格式**本身**是语言的一部分，不是装饰。
    // 中文习惯"2026-10-25 21:07"，英文习惯"10/25/2026 9:07 PM" ——
    // 一个写死的 "yyyy-MM-dd HH:mm" 在英文界面下虽然看得懂，但不地道，
    // 24 小时制尤其明显（老外看到 21:07 会停一下）。
    //
    // 中文/日文/韩文语境保持原来的格式**完全不变**（不给老用户改观感），
    // 其它语言走该区域的通用格式。

    /// <summary>当前语言是否用"年-月-日 + 24 小时制"这套习惯。</summary>
    private static bool UsesCjkDateOrder => _culture.TwoLetterISOLanguageName is "zh" or "ja" or "ko";

    /// <summary>时:分:秒（按键测试记录、宏默认名）。</summary>
    public static string FormatTime(DateTime t)
        => UsesCjkDateOrder ? t.ToString("HH:mm:ss", _culture) : t.ToString("h:mm:ss tt", _culture);

    /// <summary>时:分（剪贴板历史）。</summary>
    public static string FormatShortTime(DateTime t)
        => UsesCjkDateOrder ? t.ToString("HH:mm", _culture) : t.ToString("h:mm tt", _culture);

    /// <summary>年-月-日 时:分（备份列表、恢复点）。</summary>
    public static string FormatDateTime(DateTime t)
        => UsesCjkDateOrder ? t.ToString("yyyy-MM-dd HH:mm", _culture) : t.ToString("g", _culture);

    /// <summary>同上，给文件时间戳这类 <see cref="DateTimeOffset"/> 用。</summary>
    public static string FormatDateTime(DateTimeOffset t) => FormatDateTime(t.DateTime);

    /// <summary>
    /// 应用某种语言。**不重启**：换字典 → 通知所有 <see cref="LocToken"/> → 触发
    /// <see cref="Changed"/> 让各 View 重建常驻文本。
    ///
    /// 未知语言代码回退默认语言；抛异常也只影响翻译，不影响功能。
    /// </summary>
    public static void Apply(string? code)
    {
        string want = Normalize(code);
        _map = Load(want);
        _language = want;

        string? cultureName = _map.TryGetValue(CultureKey, out string? c) && !string.IsNullOrWhiteSpace(c)
            ? c
            : want;
        try
        {
            _culture = CultureInfo.GetCultureInfo(cultureName);
        }
        catch (CultureNotFoundException)
        {
            _culture = CultureInfo.GetCultureInfo(DefaultLanguage);
        }

        // 令牌缓存对所有语言通用：让每个令牌重新读一次
        LocTokens.InvalidateAll();
        try { Changed?.Invoke(); }
        catch (Exception ex) { Core.CoreLog.Error("语言切换回调抛异常（界面可能只更新了一部分）", ex); }
    }

    /// <summary>启动时调用一次（App.OnStartup）。</summary>
    public static void Initialize(string? code) => Apply(code);

    /// <summary>把各种写法的语言代码洗成内置的规范写法（zh-cn → zh-CN）。</summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return DefaultLanguage;
        foreach (var l in Available)
            if (string.Equals(l.Code, code.Trim(), StringComparison.OrdinalIgnoreCase))
                return l.Code;

        // 只给了母语（"en"）时挑一个同语种的（"en-US"）
        string head = code.Trim().Split('-', '_')[0];
        foreach (var l in Available)
            if (string.Equals(l.Code.Split('-')[0], head, StringComparison.OrdinalIgnoreCase))
                return l.Code;

        return DefaultLanguage;
    }

    /// <summary>扫描嵌入的语言包，按代码排序（默认语言固定第一）。</summary>
    private static List<LocLanguage> Discover()
    {
        var list = new List<LocLanguage>();
        foreach (string code in PackCodes())
        {
            var map = Load(code);
            string name = map.TryGetValue(NameKey, out string? n) && !string.IsNullOrWhiteSpace(n)
                ? n
                : code;
            list.Add(new LocLanguage(code, name, code == DefaultLanguage));
        }

        if (list.All(l => l.Code != DefaultLanguage))
            list.Insert(0, new LocLanguage(DefaultLanguage, "简体中文", true));   // 连包都没有也要能用

        return list
            .OrderByDescending(l => l.Code == DefaultLanguage)
            .ThenBy(l => l.Code, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>嵌入资源里所有 `Assets/Lang/<code>.json` 的 code。</summary>
    private static IEnumerable<string> PackCodes()
    {
        Assembly asm = typeof(Loc).Assembly;
        foreach (string res in asm.GetManifestResourceNames())
        {
            // CapsLockPlus.Assets.Lang.en-US.json
            int i = res.IndexOf(".Lang.", StringComparison.Ordinal);
            if (i < 0 || !res.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            string code = res[(i + ".Lang.".Length)..^".json".Length];
            if (code.Length > 0) yield return code;
        }
    }

    /// <summary>读一个语言包；读不到/坏掉都返回空表（= 全部显示原文）。</summary>
    private static Dictionary<string, string> Load(string code)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            Assembly asm = typeof(Loc).Assembly;
            string suffix = $".Lang.{code}.json";
            string? name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (name == null) return map;

            using Stream? s = asm.GetManifestResourceStream(name);
            if (s == null) return map;

            using var reader = new StreamReader(s, System.Text.Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
            if (parsed == null) return map;

            foreach (var kv in parsed)
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                    map[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            Core.CoreLog.Error($"语言包 {code} 读取失败，界面将显示原文", ex);
        }
        return map;
    }

    /// <summary>仅供自检：当前语言包里有多少条译文（含元数据）。</summary>
    internal static int EntryCountForTest => _map.Count;

    /// <summary>仅供自检：某条原文在**指定**语言包里有没有译文（用于查漏）。</summary>
    internal static string? LookupInForTest(string code, string text)
    {
        var map = Load(Normalize(code));
        return map.TryGetValue(text, out string? v) ? v : null;
    }

    /// <summary>仅供自检：直接读某个包的全部键（查重复、查空值、查占位符）。</summary>
    internal static IReadOnlyDictionary<string, string> PackForTest(string code)
        => Load(Normalize(code));
}
