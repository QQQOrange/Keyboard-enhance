using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Gestures;

/// <summary>
/// 把 WGestures 的 gestures.json 翻译成本软件的手势。
///
/// 翻译是"尽力而为"：WGestures 的动作类型里，按键、启动程序、打开网址、
/// 窗口控制都能一一对上；它自家的 Lua 脚本没有对应物，只能跳过并在
/// 结果里计数，绝不硬塞一条看起来像、按下去却是错的手势。
/// 每条导入的手势都带 <see cref="MouseGesture.FromWGestures"/>，
/// 列表里显示 WG 徽章，重复导入时按签名去重。
/// </summary>
public static class WGesturesImporter
{
    /// <summary>读一个 gestures.json，把能翻译的手势追加到 settings.Gestures。</summary>
    public static (int Imported, int Skipped) ImportFile(string path, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("配置路径为空", nameof(path));
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        string text = File.ReadAllText(path, Encoding.UTF8);   // 自动识别 BOM
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        settings.Gestures ??= new List<MouseGesture>();
        var seen = new HashSet<string>(
            settings.Gestures.Where(g => g.FromWGestures).Select(Signature),
            StringComparer.Ordinal);

        var fresh = new List<MouseGesture>();
        int skipped = 0;

        if (root.TryGetProperty("General", out var general))
            ReadIntents(general, "", fresh, seen, ref skipped);

        if (root.TryGetProperty("Apps", out var apps) && apps.ValueKind == JsonValueKind.Array)
        {
            foreach (var app in apps.EnumerateArray())
            {
                string appPath = Str(app, "Path");
                ReadIntents(app, appPath, fresh, seen, ref skipped);
            }
        }

        foreach (var g in fresh) settings.Gestures.Add(g);
        return (fresh.Count, skipped);
    }

    // ───────────────────────── 解析 ─────────────────────────

    private static void ReadIntents(
        JsonElement owner, string appPath, List<MouseGesture> fresh,
        HashSet<string> seen, ref int skipped)
    {
        if (!owner.TryGetProperty("Intents", out var intents) || intents.ValueKind != JsonValueKind.Array)
            return;

        foreach (var intent in intents.EnumerateArray())
        {
            var gesture = Parse(intent, appPath);
            if (gesture == null) { skipped++; continue; }

            string sig = Signature(gesture);
            if (!seen.Add(sig)) continue;        // 同一份配置里重复的条目，或之前已经导入过
            fresh.Add(gesture);
        }
    }

    /// <summary>一条 WGestures intent → 一条手势；翻译不了返回 null。</summary>
    private static MouseGesture? Parse(JsonElement intent, string appPath)
    {
        string name = Str(intent, "Name");
        if (string.IsNullOrWhiteSpace(name)) name = "WG 手势";

        string? button = null;                   // left/right/middle/x1/x2
        int wheel = 0;                            // -1 上、+1 下
        int corner = 0;
        List<GestureMath.Pt>? path = null;

        if (intent.TryGetProperty("Gesture", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                switch (Str(part, "$type"))
                {
                    case "KeyDown":
                        string key = Str(part, "Key");
                        if (key.StartsWith("MOUSE:", StringComparison.OrdinalIgnoreCase))
                            button ??= MouseButton(key[6..]);
                        else if (key.StartsWith("VSCROLL:", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(key[8..], out int delta))
                                wheel = delta > 0 ? -1 : 1;
                        }
                        break;

                    case "Stroke":
                        var pts = new List<GestureMath.Pt>();
                        if (part.TryGetProperty("P", out var raw) && raw.ValueKind == JsonValueKind.Array)
                        {
                            int x = 0; bool hasX = false;
                            foreach (var n in raw.EnumerateArray())
                            {
                                if (n.ValueKind != JsonValueKind.Number) continue;
                                if (!hasX) { x = n.GetInt32(); hasX = true; }
                                else { pts.Add(new GestureMath.Pt(x, n.GetInt32())); hasX = false; }
                            }
                        }
                        if (pts.Count >= 2) path = pts;
                        break;

                    case "MoveToEdgeCorner":
                        corner = ParseCorner(part);
                        break;
                }
            }
        }

        if (!intent.TryGetProperty("Command", out var cmd)) return null;

        var (action, arg, note) = TranslateCommand(cmd);
        if (string.IsNullOrEmpty(action)) return null;

        var g = new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Name = name,
            Button = button ?? (wheel != 0 ? "" : GestureButtons.Right),
            Action = action,
            Arg = arg,
            AppPath = appPath ?? "",
            FromWGestures = true,
            Note = note
        };

        if (wheel != 0)
        {
            g.Kind = "wheel";
            g.Wheel = wheel;
            g.Corner = corner;
            if (corner != 0)
                g.Note = Join(g.Note, "原触发位置：屏幕角落 + 滚轮");
        }
        else if (corner != 0 && path == null)
        {
            g.Kind = "corner";
            g.Corner = corner;
        }
        else if (path != null)
        {
            g.Kind = "stroke";
            g.Path = GestureMath.ToFlat(GestureMath.Normalize(path));
            if (g.Path.Count < 4) return null;      // 起止点重合，画不出一条线
        }
        else if (button != null)
        {
            g.Kind = "click";                       // 只写了起始键 = "单击这个键"
        }
        else
        {
            return null;                            // 既没有键也没有笔画/角落/滚轮，无从还原
        }

        return g;
    }

    private static int ParseCorner(JsonElement part)
    {
        // Value 是位掩码：1 左上、2 右上、4 左下、8 右下；3/6/9/12 是整条边。
        if (!part.TryGetProperty("EdgeCorner", out var edge)) return 0;
        if (!edge.TryGetProperty("Value", out var value) || value.ValueKind != JsonValueKind.Number) return 0;
        return value.GetInt32() switch
        {
            1 => 1, 2 => 2, 4 => 3, 8 => 4,
            _ => 0                                   // 边 → 任意角，加说明
        };
    }

    private static string MouseButton(string code) => code switch
    {
        "0" => GestureButtons.Left,
        "1" => GestureButtons.Right,
        "2" => GestureButtons.Middle,
        "3" => GestureButtons.X1,
        "4" => GestureButtons.X2,
        _ => GestureButtons.Right
    };

    // ───────────────────────── 命令翻译 ─────────────────────────

    private static (string Action, string Arg, string Note) TranslateCommand(JsonElement cmd)
    {
        string type = Str(cmd, "$type");

        switch (type)
        {
            case "SendKeys":
                return TranslateSendKeys(Str(cmd, "Keys"));

            case "RunApp":
            {
                string path = Str(cmd, "Path").Trim();
                string extra = Str(cmd, "Cmd").Trim();
                if (path.Length == 0 && extra.Length > 0) return ("runProgram", extra, "");
                if (path.Length == 0) return ("", "", "");
                string arg = extra.Length > 0 ? $"\"{path}\" {extra}" : path;
                return ("runProgram", arg, "");
            }

            case "WebSearch":
            {
                string url = Str(cmd, "Engine").Trim();
                if (url.Length == 0) return ("", "", "");
                // {WG_SELECTED_TEXT} 在本软件里没有对应物，留空比乱填一段文字诚实。
                bool hadSelection = url.Contains("{WG_SELECTED_TEXT}", StringComparison.OrdinalIgnoreCase);
                url = url.Replace("{WG_SELECTED_TEXT}", "", StringComparison.OrdinalIgnoreCase);
                return ("openUrl", url, hadSelection ? "原动作会带上选中的文字搜索，这里只打开网址" : "");
            }

            case "Cmd":
            {
                string code = Str(cmd, "Code").Trim();
                if (code.Length == 0) return ("", "", "");
                return ("runProgram", "cmd.exe /c " + code, "原来是无窗口执行的命令，现在会闪一下命令行窗口");
            }

            case "WindowControl":
                return Str(cmd, "Command") switch
                {
                    "WINDOW_MINIMIZE" => ("winMinimize", "", ""),
                    "WINDOW_TOGGLE_MAXIMIZE" => ("winMaximize", "", ""),
                    "WINDOW_TOGGLE_TOPMOST" => ("winPin", "", ""),
                    "WINDOW_CLOSE" => ("winClose", "", ""),
                    "WINDOW_CENTER" => ("winCenter", "", ""),
                    _ => ("", "", "")
                };

            case "Script":
            {
                string code = Str(cmd, "Code");
                if (code.Contains("ShowSettings", StringComparison.OrdinalIgnoreCase))
                    return ("openSettings", "", "原动作是 WGestures 脚本，已改为打开设置页");
                return ("", "", "");
            }

            default:
                return ("", "", "");
        }
    }

    /// <summary>
    /// WGestures 的 SendKeys 是 AutoHotkey 语法（<c>^v</c>、<c>%{F4}</c>、
    /// <c>{LCtrl}(c)</c>、<c>{VOLUME_DOWN 2}</c>）。
    ///
    /// 只有整条命令就是单个音量/媒体键时，才映射到本软件的专用动作
    /// （这样音量条、Toast 和键盘快捷键完全一致）；其余一律翻译成
    /// <c>sendKeys</c> 的"空格分隔组合键"格式，例如 <c>ctrl+v enter</c>。
    /// </summary>
    private static (string Action, string Arg, string Note) TranslateSendKeys(string keys)
    {
        string raw = (keys ?? "").Trim();
        if (raw.Length == 0) return ("", "", "");

        string single = raw.ToUpperInvariant() switch
        {
            "{VOLUME_MUTE}" => "volumeMute",
            "{VOLUME_UP}" => "volumeUp",
            "{VOLUME_DOWN}" => "volumeDown",
            "{MEDIA_PLAY_PAUSE}" => "mediaPlayPause",
            "{MEDIA_NEXT}" => "mediaNext",
            "{MEDIA_PREV}" => "mediaPrev",
            _ => ""
        };
        if (single.Length > 0) return (single, "", "");

        string seq = ConvertKeys(raw);
        if (seq.Length == 0) return ("", "", "按键写法无法翻译");
        return ("sendKeys", seq, "");
    }

    /// <summary>AutoHotkey 的按键串 → <c>ctrl+shift+k space enter</c> 这种格式。</summary>
    internal static string ConvertKeys(string keys)
    {
        var tokens = new List<string>();
        var mods = new List<string>();                 // ctrl / shift / alt / win
        int i = 0;

        void Flush(string key, int times = 1)
        {
            if (key.Length == 0) return;
            string token = mods.Count > 0 ? string.Join('+', mods) + "+" + key : key;
            for (int n = 0; n < times && n < 8; n++) tokens.Add(token);
        }

        while (i < keys.Length)
        {
            char c = keys[i];

            if (c == '^') { AddMod(mods, "ctrl"); i++; continue; }
            if (c == '+') { AddMod(mods, "shift"); i++; continue; }
            if (c == '%') { AddMod(mods, "alt"); i++; continue; }
            if (c == '#') { AddMod(mods, "win"); i++; continue; }

            if (c == '{')
            {
                int end = keys.IndexOf('}', i + 1);
                if (end < 0) break;
                string inner = keys[(i + 1)..end].Trim();
                i = end + 1;

                string[] bits = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string name = bits.Length > 0 ? bits[0] : "";
                int times = bits.Length > 1 && int.TryParse(bits[1], out int n) ? n : 1;

                if (IsModifierName(name)) { AddMod(mods, ModifierOf(name)); continue; }

                string? key = MapKey(name);
                if (key != null) { Flush(key, times); mods.Clear(); }
                continue;
            }

            if (c == '(')
            {
                int end = keys.IndexOf(')', i + 1);
                string inner = end < 0 ? keys[(i + 1)..] : keys[(i + 1)..end];
                foreach (char ch in inner)
                {
                    string? key = MapChar(ch);
                    if (key != null) Flush(key);
                }
                mods.Clear();
                i = end < 0 ? keys.Length : end + 1;
                continue;
            }

            if (c == ' ') { i++; continue; }

            string? plain = MapChar(c);
            if (plain != null) { Flush(plain); mods.Clear(); }
            i++;
        }

        return string.Join(' ', tokens);
    }

    private static void AddMod(List<string> mods, string mod)
    {
        if (!mods.Contains(mod)) mods.Add(mod);
    }

    private static bool IsModifierName(string name) => name.ToUpperInvariant() switch
    {
        "LCTRL" or "RCTRL" or "CTRL" => true,
        "LSHIFT" or "RSHIFT" or "SHIFT" => true,
        "LALT" or "RALT" or "ALT" => true,
        "LWIN" or "RWIN" => true,
        _ => false
    };

    private static string ModifierOf(string name) => name.ToUpperInvariant() switch
    {
        "LCTRL" or "RCTRL" or "CTRL" => "ctrl",
        "LSHIFT" or "RSHIFT" or "SHIFT" => "shift",
        "LALT" or "RALT" or "ALT" => "alt",
        _ => "win"
    };

    /// <summary>AHK 键名 → 本软件的键名；音量/媒体键用专用名字（见 sendKeys 动作）。</summary>
    private static string? MapKey(string name) => name.ToUpperInvariant() switch
    {
        "ENTER" or "RETURN" => "enter",
        "TAB" => "tab",
        "SPACE" => "space",
        "ESC" or "ESCAPE" => "esc",
        "BACKSPACE" => "backspace",
        "DELETE" or "DEL" => "delete",
        "INSERT" or "INS" => "insert",
        "HOME" => "home",
        "END" => "end",
        "UP" => "up",
        "DOWN" => "down",
        "LEFT" => "left",
        "RIGHT" => "right",
        "PGUP" => "pageup",
        "PGDN" => "pagedown",
        "CAPSLOCK" => "capslock",
        "PRINTSCREEN" => "printscreen",
        "VOLUME_MUTE" => "volumemute",
        "VOLUME_UP" => "volumeup",
        "VOLUME_DOWN" => "volumedown",
        "MEDIA_PLAY_PAUSE" => "mediaplay",
        "MEDIA_NEXT" => "medianext",
        "MEDIA_PREV" => "mediaprev",
        "MEDIA_STOP" => "mediastop",
        _ when name.Length == 1 => MapChar(name[0]),
        _ when name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out int fn) && fn is >= 1 and <= 24
            => "f" + fn,
        _ => null
    };

    private static string? MapChar(char c) => c switch
    {
        >= 'a' and <= 'z' => c.ToString(),
        >= 'A' and <= 'Z' => char.ToLowerInvariant(c).ToString(),
        >= '0' and <= '9' => c.ToString(),
        '/' => "slash",
        '.' => "dot",
        ',' => "comma",
        ';' => "semicolon",
        '\'' => "quote",
        '-' => "minus",
        '=' => "equal",
        '\\' => "backslash",
        '`' => "backquote",
        ' ' => "space",
        _ => null
    };

    private static string Signature(MouseGesture g) => string.Join('|',
        g.Name, g.Button, g.Kind, g.Wheel, g.Corner, g.AppPath, string.Join(',', g.Path));

    private static string Join(string a, string b)
        => string.IsNullOrWhiteSpace(a) ? b : string.IsNullOrWhiteSpace(b) ? a : a + "；" + b;

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
}
