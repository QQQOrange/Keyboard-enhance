using System.Collections;
using System.Text;
using System.Text.RegularExpressions;

namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 旧版 CapsLock+settings.ini 迁移器。
/// 上游用 AutoHotkey 的 IniRead 读 UTF-16 的 ini；这里手工解析，
/// 把 keyFunc_xxx(参数) 映射成新版的“动作 ID + 参数”。
/// </summary>
public static class LegacyIniImporter
{
    /// <summary>AHK 函数名 -> 新动作 ID。</summary>
    private static readonly Dictionary<string, string> ActionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["keyfunc_donothing"] = "doNothing",
        ["keyfunc_togglecapslock"] = "toggleCapsLock",
        ["keyfunc_esc"] = "esc",
        ["keyfunc_enter"] = "enter",
        ["keyfunc_enterwherever"] = "enterWherever",
        ["keyfunc_backspace"] = "backspace",
        ["keyfunc_delete"] = "delete",
        ["keyfunc_deleteall"] = "deleteAll",
        ["keyfunc_deleteword"] = "deleteWord",
        ["keyfunc_forwarddeleteword"] = "forwardDeleteWord",
        ["keyfunc_deleteline"] = "deleteLine",
        ["keyfunc_deletetolinebeginning"] = "deleteToLineBeginning",
        ["keyfunc_deletetolineend"] = "deleteToLineEnd",
        ["keyfunc_deletetopagebeginning"] = "deleteToPageBeginning",
        ["keyfunc_deletetopageend"] = "deleteToPageEnd",
        ["keyfunc_moveleft"] = "moveLeft",
        ["keyfunc_moveright"] = "moveRight",
        ["keyfunc_moveup"] = "moveUp",
        ["keyfunc_movedown"] = "moveDown",
        ["keyfunc_movewordleft"] = "moveWordLeft",
        ["keyfunc_movewordright"] = "moveWordRight",
        ["keyfunc_selectleft"] = "selectLeft",
        ["keyfunc_selectright"] = "selectRight",
        ["keyfunc_selectup"] = "selectUp",
        ["keyfunc_selectdown"] = "selectDown",
        ["keyfunc_selectwordleft"] = "selectWordLeft",
        ["keyfunc_selectwordright"] = "selectWordRight",
        ["keyfunc_selecthome"] = "selectHome",
        ["keyfunc_selectend"] = "selectEnd",
        ["keyfunc_selecttopagebeginning"] = "selectToPageBeginning",
        ["keyfunc_selecttopageend"] = "selectToPageEnd",
        ["keyfunc_selectcurrentword"] = "selectCurrentWord",
        ["keyfunc_selectcurrentline"] = "selectCurrentLine",
        ["keyfunc_home"] = "home",
        ["keyfunc_end"] = "end",
        ["keyfunc_movetopagebeginning"] = "moveToPageBeginning",
        ["keyfunc_movetopageend"] = "moveToPageEnd",
        ["keyfunc_pageup"] = "pageUp",
        ["keyfunc_pagedown"] = "pageDown",
        ["keyfunc_pagemoveup"] = "pageMoveLineUp",
        ["keyfunc_pagemovedown"] = "pageMoveLineDown",
        ["keyfunc_pagemovelineup"] = "pageMoveLineUp",
        ["keyfunc_pagemovelineDown"] = "pageMoveLineDown",
        ["keyfunc_jumppagetop"] = "moveToPageBeginning",
        ["keyfunc_jumppagebottom"] = "moveToPageEnd",
        ["keyfunc_copy_1"] = "copy1",
        ["keyfunc_cut_1"] = "cut1",
        ["keyfunc_paste_1"] = "paste1",
        ["keyfunc_copy_2"] = "copy2",
        ["keyfunc_cut_2"] = "cut2",
        ["keyfunc_paste_2"] = "paste2",
        ["keyfunc_pastesystem"] = "pasteSystem",
        ["keyfunc_switchclipboard"] = "toggleClipboard",
        ["keyfunc_qbar"] = "qbar",
        ["keyfunc_mathboard"] = "mathBoard",
        ["keyfunc_tabscript"] = "tabScript",
        ["keyfunc_translate"] = "translate",
        ["keyfunc_opencpasdocs"] = "openDocs",
        ["keyfunc_reload"] = "reload",
        ["keyfunc_winpin"] = "winPin",
        ["keyfunc_wintransparent"] = "winTransparent",
        ["keyfunc_undoredo"] = "undoRedo",
        ["keyfunc_winbind_activate"] = "winbindActivate",
        ["keyfunc_winbind_binding"] = "winbindBind",
        ["keyfunc_mediaprev"] = "mediaPrev",
        ["keyfunc_medianext"] = "mediaNext",
        ["keyfunc_mediaplaypause"] = "mediaPlayPause",
        ["keyfunc_volumeup"] = "volumeUp",
        ["keyfunc_volumedown"] = "volumeDown",
        ["keyfunc_volumemute"] = "volumeMute",
        ["keyfunc_mousespeedincrease"] = "mouseSpeedUp",
        ["keyfunc_mousespeeddecrease"] = "mouseSpeedDown",
        ["keyfunc_click_left"] = "clickLeft",
        ["keyfunc_click_right"] = "clickRight",
        ["keyfunc_mouse_up"] = "mouseUp",
        ["keyfunc_mouse_down"] = "mouseDown",
        ["keyfunc_mouse_left"] = "mouseLeft",
        ["keyfunc_mouse_right"] = "mouseRight",
        ["keyfunc_wheel_up"] = "wheelUp",
        ["keyfunc_wheel_down"] = "wheelDown",
        ["keyfunc_doublechar"] = "wrapSelection",
        ["keyfunc_tabnext"] = "winNextApp",
        ["keyfunc_tabprve"] = "winPrevApp",
        ["keyfunc_activatessidewin"] = "activateSideWin",
        ["keyfunc_putwintobottom"] = "winSendToBottom",
        ["keyfunc_getjsevalstring"] = "doNothing",
    };

    public sealed record ImportResult(AppSettings Settings, int Keys, int Search, int Run, int Web, int HotStrings, List<string> Warnings);

    public static bool LooksLikeLegacyIni(string path) =>
        File.Exists(path) &&
        Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase);

    /// <summary>读取旧 ini（UTF-16 / UTF-8 自动识别）并生成新版设置。</summary>
    public static ImportResult Import(string iniPath)
    {
        var warnings = new List<string>();
        var sections = ParseIni(iniPath);
        var settings = DefaultSettings.Create();
        int keys = 0, search = 0, run = 0, web = 0, hs = 0;

        if (sections.TryGetValue("Global", out var global))
        {
            if (global.TryGetValue("autostart", out var auto) && auto.Trim() == "1")
                settings.Global.StartWithWindows = true;
            if (global.TryGetValue("allowClipboard", out var clip))
                settings.Global.AllowClipboard = clip.Trim() != "0";
            if (global.TryGetValue("mouseSpeed", out var ms) && int.TryParse(ms.Trim(), out int speed))
                settings.Global.MouseSpeed = Math.Clamp(speed, 1, 20);
            if (global.TryGetValue("default_hotkey_scheme", out var scheme) && !string.IsNullOrWhiteSpace(scheme))
                settings.Global.HotkeyScheme = scheme.Trim();
        }

        if (sections.TryGetValue("Keys", out var keysSection))
        {
            foreach (var (key, value) in keysSection)
            {
                string combo = key.Trim();
                string func = value.Trim();
                if (string.IsNullOrEmpty(func)) continue;
                var (action, arg) = MapFunction(func);
                if (action == null)
                {
                    warnings.Add($"无法识别的按键功能：{combo} = {func}");
                    continue;
                }
                if (action == "wrapSelection")
                {
                    warnings.Add($"{combo} 的“包裹字符”功能需要在新界面里重新选择");
                    continue;
                }
                var existing = settings.FindBinding(combo);
                if (existing != null)
                {
                    existing.Action = action;
                    existing.Arg = arg;
                }
                else
                {
                    settings.Keys.Add(new KeyBinding { Combo = combo, Action = action, Arg = arg });
                }
                keys++;
            }
        }

        if (sections.TryGetValue("QSearch", out var qs))
        {
            settings.Search = qs.Where(kv => !kv.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
                .Select(kv => new QEntry { Key = StripComment(kv.Key), Value = kv.Value.Trim(), Comment = ExtractComment(kv.Key) })
                .ToList();
            if (qs.TryGetValue("default", out var def) && !string.IsNullOrWhiteSpace(def))
                settings.Search.Insert(0, new QEntry { Key = "default", Value = def.Trim(), Comment = "默认搜索" });
            search = settings.Search.Count;
        }

        if (sections.TryGetValue("QRun", out var qr))
        {
            settings.Run = qr.Select(kv =>
            {
                var e = new QEntry { Key = StripComment(kv.Key), Comment = ExtractComment(kv.Key) };
                string v = kv.Value.Trim();
                if (v.StartsWith("*RunAs", StringComparison.OrdinalIgnoreCase))
                {
                    e.RunAsAdmin = true;
                    v = v[6..].Trim();
                }
                var m = Regex.Match(v, "^(?:\"([^\"]+)\"|'([^']+)')\\s*(.*)$");
                if (m.Success)
                {
                    e.Value = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    e.Arguments = m.Groups[3].Value;
                }
                else e.Value = v;
                return e;
            }).ToList();
            run = settings.Run.Count;
        }

        if (sections.TryGetValue("QWeb", out var qw))
        {
            settings.Web = qw.Select(kv => new QEntry
            {
                Key = StripComment(kv.Key), Value = kv.Value.Trim(), Comment = ExtractComment(kv.Key),
            }).ToList();
            web = settings.Web.Count;
        }

        if (sections.TryGetValue("TabHotString", out var th))
        {
            settings.HotStrings = th.Select(kv => new QEntry
            {
                Key = StripComment(kv.Key), Value = kv.Value, Comment = ExtractComment(kv.Key),
            }).ToList();
            hs = settings.HotStrings.Count;
        }

        if (sections.TryGetValue("TTranslate", out var tt))
        {
            if (tt.TryGetValue("appPaidID", out var id) && !id.Contains("xxx")) settings.Translate.AppId = id.Trim();
            if (tt.TryGetValue("appPaidKey", out var key2) && !key2.Contains("xxx")) settings.Translate.AppSecret = key2.Trim();
        }

        if (sections.TryGetValue("QStyle", out var qstyle))
        {
            if (qstyle.TryGetValue("borderBackgroundColor", out var bc)) settings.Qbar.BorderColor = ToHex(bc);
            if (qstyle.TryGetValue("listCount", out var lc) && int.TryParse(lc.Trim(), out int count))
                settings.Qbar.ListCount = Math.Clamp(count, 3, 12);
            if (qstyle.TryGetValue("textFontSize", out var fs) && double.TryParse(fs.Trim(), out double size))
                settings.Qbar.TextFontSize = Math.Clamp(size + 6, 14, 32);
        }

        SettingsStore.Normalize(settings);
        return new ImportResult(settings, keys, search, run, web, hs, warnings);
    }

    private static string ToHex(string value)
    {
        string v = value.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = v[2..];
        if (v.Length == 6 && v.All(Uri.IsHexDigit)) return "#" + v.ToUpperInvariant();
        return value.Trim().ToLowerInvariant() switch
        {
            "red" => "#E5484D",
            "green" => "#30A46C",
            "blue" => "#2B6CF6",
            "black" => "#111111",
            "white" => "#FFFFFF",
            "yellow" => "#F5D90A",
            _ => "#2B6CF6",
        };
    }

    private static string StripComment(string key)
    {
        int i = key.IndexOf('<');
        return (i > 0 ? key[..i] : key).Trim();
    }

    private static string ExtractComment(string key)
    {
        int i = key.IndexOf('<');
        if (i < 0) return "";
        int j = key.LastIndexOf('>');
        return j > i ? key[(i + 1)..j].Trim() : key[(i + 1)..].Trim();
    }

    private static (string? Action, string Arg) MapFunction(string func)
    {
        var m = Regex.Match(func, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:\((.*)\))?\s*$");
        if (!m.Success) return (null, "");
        string name = m.Groups[1].Value;
        string args = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
        if (!ActionMap.TryGetValue(name, out var action)) return (null, "");

        /* 旧版的窗口槽位（winbindActivate 1~10 / winbindBind 1~10）
         * 已经移除 —— 槽位机制换成了「窗口绑定」动作（靠程序路径，不靠槽位号）。
         * 两者语义不同，无法自动转换：旧配置只知道"槽位 3"，
         * 而槽位 3 到底绑的是哪个程序存在**另一个文件**里，导入时读不到。
         * 所以这里返回 null（跳过这一项），由用户自己在按键映射页重绑。 */
        if (action is "winbindActivate" or "winbindBind") return (null, "");
        if (action == "activateSideWin")
        {
            string dir = args.Trim('"', '\'', ' ');
            return (dir switch
            {
                "l" => "winLeft", "r" => "winRight", "u" => "winUp", "d" => "winDown",
                "fl" => "winSnapTopLeft", "fr" => "winSnapTopRight",
                "c" => "winCenter", _ => "doNothing",
            }, "");
        }
        return (action, args.Trim('"', '\''));
    }

    /// <summary>一个 INI 段：既保序又能按名查找。</summary>
    public sealed class IniSection : IEnumerable<KeyValuePair<string, string>>
    {
        private readonly List<KeyValuePair<string, string>> _items = new();
        private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string key, string value)
        {
            _items.Add(new KeyValuePair<string, string>(key, value));
            _map[key] = value;
        }

        public bool TryGetValue(string key, out string value) => _map.TryGetValue(key, out value!);
        public int Count => _items.Count;
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>极简 INI 解析：支持 [Section]、key=value、; 注释。</summary>
    public static Dictionary<string, IniSection> ParseIni(string path)
    {
        var result = new Dictionary<string, IniSection>(StringComparer.OrdinalIgnoreCase);
        var lines = ReadAllLinesSmart(path);
        IniSection? current = null;
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                string name = line[1..^1].Trim();
                if (!result.TryGetValue(name, out current))
                {
                    current = new IniSection();
                    result[name] = current;
                }
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0 || current == null) continue;
            current.Add(line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
        return result;
    }

    private static string[] ReadAllLinesSmart(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).Split('\n');
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split('\n');
        int zeros = bytes.Take(2000).Count(b => b == 0);
        return zeros > 100
            ? Encoding.Unicode.GetString(bytes).Split('\n')
            : Encoding.UTF8.GetString(bytes).Split('\n');
    }
}
