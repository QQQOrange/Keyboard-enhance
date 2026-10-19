using System.Text.Json;
using System.Text.Json.Serialization;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Settings;

/// <summary>设置的读写（JSON，带注释友好的格式、原子写入与自动备份）。</summary>
public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object Gate = new();

    public static AppSettings Load()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                if (!File.Exists(AppPaths.SettingsFile))
                {
                    var fresh = DefaultSettings.Create();
                    Save(fresh);
                    return fresh;
                }
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? DefaultSettings.Create();
                Normalize(s);
                bool migrated = MigrateBuiltinEngines(s);
                if (migrated) Save(s);
                return s;
            }
            catch (Exception ex)
            {
                CoreLog.Error("读取设置失败，已回退到默认设置", ex);
                try
                {
                    if (File.Exists(AppPaths.SettingsFile))
                        File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".broken", true);
                }
                catch { }
                var s = DefaultSettings.Create();
                return s;
            }
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                var tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(AppPaths.SettingsFile))
                    File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bak", true);
                File.Move(tmp, AppPaths.SettingsFile, true);
            }
            catch (Exception ex)
            {
                CoreLog.Error("保存设置失败", ex);
            }
        }
    }

    /// <summary>供自检调用的迁移入口（与启动时走同一条逻辑）。</summary>
    internal static bool MigrateBuiltinEnginesForTest(AppSettings s) => MigrateBuiltinEngines(s);

    /// <summary>
    /// 把新增的内置搜索引擎补进用户已有设置。
    ///
    /// 为什么需要它：设置文件一旦存在，启动时走的就是"读取"分支，
    /// 不会再套用默认值。于是每次扩充引擎表，老用户都看不到新引擎。
    ///
    /// 策略（保守，不破坏用户自定义）：
    ///   * 用户已有的关键字：原样保留（哪怕改过链接或备注）；
    ///   * 用户没有的关键字：把内置引擎补上；
    ///   * 只补不删，用户自己新增的引擎不受影响。
    /// </summary>
    private static bool MigrateBuiltinEngines(AppSettings s)
    {
        if (s.BuiltinEnginesVersion >= DefaultSettings.BuiltinEnginesVersion) return false;

        // 旧版用过、现在已改名的关键字：直接删掉，免得用户看到 g 和 go 两个 Google。
        // 只删"还保持原始内容"的条目；如果用户改过链接，说明他自己在用，保留。
        var renamedAway = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["g"] = "go",    // Google
            ["b"] = "bi",    // Bing
            ["wk"] = "wi",   // Wikipedia
            ["m"] = "md",    // MDN
        };
        foreach (var (oldKey, newKey) in renamedAway)
        {
            var old = s.Search.FirstOrDefault(e => e.Key.Equals(oldKey, StringComparison.OrdinalIgnoreCase));
            if (old == null) continue;
            var target = DefaultSettings.Create().Search
                .FirstOrDefault(e => e.Key.Equals(newKey, StringComparison.OrdinalIgnoreCase));
            if (target != null && old.Value == target.Value)
            {
                s.Search.Remove(old);
                CoreLog.Info($"搜索引擎 {oldKey} 已重命名为 {newKey}，移除旧条目");
            }
        }

        var builtin = DefaultSettings.Create().Search
            .Where(e => !e.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int added = 0;
        foreach (var engine in builtin)
        {
            bool exists = s.Search.Any(e => e.Key.Equals(engine.Key, StringComparison.OrdinalIgnoreCase));
            if (exists) continue;
            s.Search.Add(new QEntry
            {
                Key = engine.Key,
                Value = engine.Value,
                Comment = engine.Comment,
                IconPath = engine.IconPath,
            });
            added++;
        }

        s.BuiltinEnginesVersion = DefaultSettings.BuiltinEnginesVersion;
        if (added > 0) CoreLog.Info($"已补充 {added} 个内置搜索引擎（引擎表版本 → {s.BuiltinEnginesVersion}）");
        return true;
    }

    /// <summary>补齐缺失字段、修正非法值。</summary>
    public static void Normalize(AppSettings s)
    {
        s.Global ??= new GlobalOptions();
        s.Qbar ??= new QbarOptions();
        s.Translate ??= new TranslateOptions();
        s.Keys ??= new List<KeyBinding>();
        s.Search ??= new List<QEntry>();
        s.Run ??= new List<QEntry>();
        s.Web ??= new List<QEntry>();
        s.HotStrings ??= new List<QEntry>();
        s.Bindings ??= new List<WinBinding>();
        s.Global.MouseSpeed = Math.Clamp(s.Global.MouseSpeed, 1, 20);
        s.Qbar.ListCount = Math.Clamp(s.Qbar.ListCount, 3, 12);
        if (string.IsNullOrWhiteSpace(s.Global.HotkeyScheme)) s.Global.HotkeyScheme = "capslox";
        // 去掉重复的绑定项，保留最后一条
        var dup = s.Keys.GroupBy(k => k.Combo, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        foreach (var g in dup)
        {
            var last = g.Last();
            s.Keys.RemoveAll(k => string.Equals(k.Combo, last.Combo, StringComparison.OrdinalIgnoreCase));
            s.Keys.Add(last);
        }
    }
}

/// <summary>默认设置：完整复刻上游 CapsLock+ 的 capslox 布局。</summary>
public static class DefaultSettings
{
    /// <summary>
    /// 内置搜索引擎表的版本号。新增内置引擎时 +1，老用户启动时会自动补齐缺失项。
    ///   1 = 最初的 10 个引擎
    ///   2 = 扩充到 29 个（国内用拼音首字母、国外用英文前两字母，全部带图标）
    /// </summary>
    public const int BuiltinEnginesVersion = 2;

    public static AppSettings Create()
    {
        var s = new AppSettings { BuiltinEnginesVersion = BuiltinEnginesVersion };
        s.Keys = CapsloxScheme();
        s.Search = new List<QEntry>
        {
            new() { Key = "default", Value = "https://www.bing.com/search?q={q}" },

            // ---- 国内引擎：关键字取名称的拼音首字母 ----
            new() { Key = "bd", Value = "https://www.baidu.com/s?wd={q}", Comment = "百度", IconPath = IconUrls.Baidu },
            new() { Key = "tb", Value = "https://s.taobao.com/search?q={q}", Comment = "淘宝", IconPath = IconUrls.Taobao },
            new() { Key = "zh", Value = "https://www.zhihu.com/search?type=content&q={q}", Comment = "知乎", IconPath = IconUrls.Zhihu },
            new() { Key = "bili", Value = "https://search.bilibili.com/all?keyword={q}", Comment = "哔哩哔哩", IconPath = IconUrls.Bilibili },
            new() { Key = "wx", Value = "https://weixin.sogou.com/weixin?type=2&query={q}", Comment = "微信", IconPath = IconUrls.Weixin },
            new() { Key = "wb", Value = "https://s.weibo.com/weibo?q={q}", Comment = "微博", IconPath = IconUrls.Weibo },
            new() { Key = "jd", Value = "https://search.jd.com/Search?keyword={q}", Comment = "京东", IconPath = IconUrls.Jd },
            new() { Key = "xhs", Value = "https://www.xiaohongshu.com/search_result?keyword={q}", Comment = "小红书", IconPath = IconUrls.Xiaohongshu },
            new() { Key = "dy", Value = "https://www.douyin.com/search/{q}", Comment = "抖音", IconPath = IconUrls.Douyin },
            new() { Key = "db", Value = "https://www.douban.com/search?q={q}", Comment = "豆瓣", IconPath = IconUrls.Douban },
            new() { Key = "csdn", Value = "https://so.csdn.net/so/search?q={q}", Comment = "CSDN", IconPath = IconUrls.Csdn },
            new() { Key = "juejin", Value = "https://juejin.cn/search?query={q}", Comment = "掘金", IconPath = IconUrls.Juejin },
            new() { Key = "gitee", Value = "https://search.gitee.com/?q={q}", Comment = "Gitee", IconPath = IconUrls.Gitee },
            new() { Key = "pdd", Value = "https://mobile.yangkeduo.com/search_result.html?search_key={q}", Comment = "拼多多", IconPath = IconUrls.Pinduoduo },
            new() { Key = "tx", Value = "https://v.qq.com/x/search/?q={q}", Comment = "腾讯视频", IconPath = IconUrls.TencentVideo },
            new() { Key = "iqy", Value = "https://so.iqiyi.com/so/q_{q}", Comment = "爱奇艺", IconPath = IconUrls.Iqiyi },

            // ---- 国外引擎：关键字取英文名前两个字母 ----
            new() { Key = "go", Value = "https://www.google.com/search?q={q}", Comment = "Google", IconPath = IconUrls.Google },
            new() { Key = "bi", Value = "https://www.bing.com/search?q={q}", Comment = "Bing", IconPath = IconUrls.Bing },
            new() { Key = "gh", Value = "https://github.com/search?q={q}", Comment = "GitHub", IconPath = IconUrls.GitHub },
            new() { Key = "wi", Value = "https://zh.wikipedia.org/w/index.php?search={q}", Comment = "Wikipedia", IconPath = IconUrls.Wikipedia },
            new() { Key = "yo", Value = "https://www.youtube.com/results?search_query={q}", Comment = "YouTube", IconPath = IconUrls.Youtube },
            new() { Key = "re", Value = "https://www.reddit.com/search/?q={q}", Comment = "Reddit", IconPath = IconUrls.Reddit },
            new() { Key = "st", Value = "https://stackoverflow.com/search?q={q}", Comment = "Stack Overflow", IconPath = IconUrls.StackOverflow },
            new() { Key = "md", Value = "https://developer.mozilla.org/zh-CN/search?q={q}", Comment = "MDN", IconPath = IconUrls.Mdn },
            new() { Key = "am", Value = "https://www.amazon.com/s?k={q}", Comment = "Amazon", IconPath = IconUrls.Amazon },
            new() { Key = "tw", Value = "https://twitter.com/search?q={q}", Comment = "X (Twitter)", IconPath = IconUrls.Twitter },
            new() { Key = "fa", Value = "https://www.facebook.com/search/top?q={q}", Comment = "Facebook", IconPath = IconUrls.Facebook },
            new() { Key = "np", Value = "https://www.npmjs.com/search?q={q}", Comment = "npm", IconPath = IconUrls.Npm },
            new() { Key = "nu", Value = "https://www.nuget.org/packages?q={q}", Comment = "NuGet", IconPath = IconUrls.Nuget },
            new() { Key = "tr", Value = "https://translate.google.com/?text={q}", Comment = "翻译", IconPath = IconUrls.Translate },
        };
        s.Run = new List<QEntry>
        {
            new() { Key = "notepad", Value = "notepad.exe", Comment = "记事本" },
            new() { Key = "calc", Value = "calc.exe", Comment = "计算器" },
            new() { Key = "mspaint", Value = "mspaint.exe", Comment = "画图" },
            new() { Key = "explorer", Value = "explorer.exe", Comment = "文件资源管理器" },
            new() { Key = "cmd", Value = "cmd.exe", Comment = "命令提示符" },
            new() { Key = "taskmgr", Value = "taskmgr.exe", Comment = "任务管理器" },
        };
        s.Web = new List<QEntry>
        {
            new() { Key = "gh", Value = "https://github.com", Comment = "GitHub" },
            new() { Key = "bili", Value = "https://www.bilibili.com", Comment = "B 站" },
        };
        s.HotStrings = new List<QEntry>
        {
            new() { Key = "clp", Value = "CapsLock+", Comment = "展开成 CapsLock+" },
            new() { Key = "@@", Value = "你的邮箱@example.com", Comment = "邮箱" },
        };
        s.Bindings = Enumerable.Range(1, 10).Select(i => new WinBinding { Slot = i }).ToList();
        return s;
    }

    /// <summary>Capslox 布局（CapsLock+ 3.0 之后的默认布局）。</summary>
    public static List<KeyBinding> CapsloxScheme() => new()
    {
        B("press_caps", "toggleCapsLock"),

        B("caps_a", "moveWordLeft"), B("caps_b", "moveDown", "10"), B("caps_c", "copy1"),
        B("caps_d", "moveDown"), B("caps_e", "moveUp"), B("caps_f", "moveRight"),
        B("caps_g", "moveWordRight"), B("caps_h", "selectWordLeft"), B("caps_i", "selectUp"),
        B("caps_j", "selectLeft"), B("caps_k", "selectDown"), B("caps_l", "selectRight"),
        B("caps_m", "doNothing"), B("caps_n", "selectDown", "10"), B("caps_o", "selectEnd"),
        B("caps_p", "home"), B("caps_q", "qbar"), B("caps_r", "delete"),
        B("caps_s", "moveLeft"), B("caps_t", "moveUp", "10"), B("caps_u", "selectHome"),
        B("caps_v", "paste1"), B("caps_w", "backspace"), B("caps_x", "cut1"),
        B("caps_y", "selectUp", "10"), B("caps_z", "doNothing"),

        B("caps_backquote", "doNothing"),
        B("caps_1", "winbindActivate1"), B("caps_2", "winbindActivate2"), B("caps_3", "winbindActivate3"),
        B("caps_4", "winbindActivate4"), B("caps_5", "winbindActivate5"), B("caps_6", "winbindActivate6"),
        B("caps_7", "winbindActivate7"), B("caps_8", "winbindActivate8"), B("caps_9", "winbindActivate9"),
        B("caps_0", "winbindActivate10"),
        B("caps_minus", "winSnapLeft"), B("caps_equal", "winSnapRight"),
        B("caps_backspace", "deleteLine"), B("caps_tab", "tabScript"),
        B("caps_leftSquareBracket", "deleteToLineBeginning"), B("caps_rightSquareBracket", "doNothing"),
        B("caps_backslash", "doNothing"), B("caps_semicolon", "end"), B("caps_quote", "doNothing"),
        B("caps_enter", "enterWherever"), B("caps_comma", "selectCurrentWord"),
        B("caps_dot", "selectWordRight"), B("caps_slash", "deleteToLineEnd"),
        B("caps_space", "enter"), B("caps_ralt", "doNothing"),

        B("caps_f1", "openDocs"), B("caps_f2", "mathBoard"), B("caps_f3", "translate"),
        B("caps_f4", "winTransparent"), B("caps_f5", "reload"), B("caps_f6", "winPin"),
        B("caps_f7", "doNothing"), B("caps_f8", "clipboardHistory"), B("caps_f9", "doNothing"),
        B("caps_f10", "doNothing"), B("caps_f11", "doNothing"), B("caps_f12", "toggleClipboard"),

        // Caps + LAlt
        B("caps_lalt_a", "moveWordLeft", "3"), B("caps_lalt_b", "moveDown", "30"), B("caps_lalt_c", "copy2"),
        B("caps_lalt_d", "moveDown", "3"), B("caps_lalt_e", "moveUp", "3"), B("caps_lalt_f", "moveRight", "5"),
        B("caps_lalt_g", "moveWordRight", "3"), B("caps_lalt_h", "selectWordLeft", "3"),
        B("caps_lalt_i", "selectUp", "3"), B("caps_lalt_j", "selectLeft", "5"), B("caps_lalt_k", "selectDown", "3"),
        B("caps_lalt_l", "selectRight", "5"), B("caps_lalt_m", "doNothing"), B("caps_lalt_n", "selectDown", "30"),
        B("caps_lalt_o", "selectToPageEnd"), B("caps_lalt_p", "moveToPageBeginning"), B("caps_lalt_q", "doNothing"),
        B("caps_lalt_r", "forwardDeleteWord"), B("caps_lalt_s", "moveLeft", "5"), B("caps_lalt_t", "moveUp", "30"),
        B("caps_lalt_u", "selectToPageBeginning"), B("caps_lalt_v", "paste2"), B("caps_lalt_w", "deleteWord"),
        B("caps_lalt_x", "cut2"), B("caps_lalt_y", "selectUp", "30"), B("caps_lalt_z", "doNothing"),

        B("caps_lalt_backquote", "doNothing"),
        B("caps_lalt_1", "winbindBind1"), B("caps_lalt_2", "winbindBind2"), B("caps_lalt_3", "winbindBind3"),
        B("caps_lalt_4", "winbindBind4"), B("caps_lalt_5", "winbindBind5"), B("caps_lalt_6", "winbindBind6"),
        B("caps_lalt_7", "winbindBind7"), B("caps_lalt_8", "winbindBind8"), B("caps_lalt_9", "winbindBind9"),
        B("caps_lalt_0", "winbindBind10"),
        B("caps_lalt_minus", "doNothing"), B("caps_lalt_equal", "doNothing"),
        B("caps_lalt_backspace", "deleteAll"), B("caps_lalt_tab", "doNothing"),
        B("caps_lalt_leftSquareBracket", "deleteToPageBeginning"), B("caps_lalt_rightSquareBracket", "doNothing"),
        B("caps_lalt_backslash", "doNothing"), B("caps_lalt_semicolon", "moveToPageEnd"),
        B("caps_lalt_quote", "doNothing"), B("caps_lalt_enter", "doNothing"),
        B("caps_lalt_comma", "selectCurrentLine"), B("caps_lalt_dot", "selectWordRight", "3"),
        B("caps_lalt_slash", "deleteToPageEnd"), B("caps_lalt_space", "doNothing"),
        B("caps_lalt_ralt", "doNothing"),
        B("caps_lalt_f1", "doNothing"), B("caps_lalt_f2", "doNothing"), B("caps_lalt_f3", "doNothing"),
        B("caps_lalt_f4", "doNothing"), B("caps_lalt_f5", "doNothing"), B("caps_lalt_f6", "doNothing"),
        B("caps_lalt_f7", "doNothing"), B("caps_lalt_f8", "doNothing"), B("caps_lalt_f9", "doNothing"),
        B("caps_lalt_f10", "doNothing"), B("caps_lalt_f11", "doNothing"), B("caps_lalt_f12", "doNothing"),

        B("caps_lalt_wheelUp", "mouseSpeedUp"), B("caps_lalt_wheelDown", "mouseSpeedDown"),

        // Caps + Win
        B("caps_win_1", "winbindActivate1"), B("caps_win_2", "winbindActivate2"), B("caps_win_3", "winbindActivate3"),
        B("caps_win_4", "winbindActivate4"), B("caps_win_5", "winbindActivate5"), B("caps_win_6", "winbindActivate6"),
        B("caps_win_7", "winbindActivate7"), B("caps_win_8", "winbindActivate8"), B("caps_win_9", "winbindActivate9"),
        B("caps_win_0", "winbindActivate10"),
    };

    /// <summary>经典布局（CapsLock+ 3.0 之前）。</summary>
    public static List<KeyBinding> ClassicScheme()
    {
        var list = CapsloxScheme();
        void Set(string combo, string action, string arg = "")
        {
            var b = list.FirstOrDefault(x => x.Combo == combo);
            if (b == null) list.Add(B(combo, action, arg));
            else { b.Action = action; b.Arg = arg; }
        }
        Set("caps_b", "moveDown", "5");
        Set("caps_m", "selectDown", "5");
        Set("caps_n", "selectWordRight");
        Set("caps_t", "translate");
        Set("caps_y", "moveUp", "5");
        Set("caps_z", "undoRedo");
        Set("caps_backquote", "winbindActivate9");
        Set("caps_9", "doNothing");
        Set("caps_0", "selectUp", "5");
        Set("caps_minus", "pageUp");
        Set("caps_equal", "pageDown");
        Set("caps_leftSquareBracket", "doNothing");
        Set("caps_rightSquareBracket", "doNothing");
        Set("caps_comma", "doNothing");
        Set("caps_dot", "doNothing");
        Set("caps_slash", "doNothing");
        Set("caps_f3", "mediaNext");
        Set("caps_lalt_a", "winLeft");
        Set("caps_lalt_d", "winDown");
        Set("caps_lalt_e", "winUp");
        Set("caps_lalt_f", "winRight");
        Set("caps_lalt_q", "winCenter");
        Set("caps_lalt_r", "winNextApp");
        Set("caps_lalt_w", "winPrevApp");
        Set("caps_lalt_z", "winSendToBottom");
        Set("caps_lalt_minus", "moveToPageBeginning");
        Set("caps_lalt_equal", "moveToPageEnd");
        Set("caps_lalt_backspace", "backspace");
        return list;
    }

    private static KeyBinding B(string combo, string action, string arg = "") =>
        new() { Combo = combo, Action = action, Arg = arg };
}
