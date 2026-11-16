using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core;

/// <summary>
/// <c>--probe-key &lt;键名或 0xNN&gt;</c>：拿**当前配置**跑一遍按键判定，直接说出结论。
///
/// 为什么需要：用户报"这个键我根本没配过，凭什么被接管"时，只读代码只能猜是哪条规则接的。
/// 这里用真配置造一台引擎，把按下/抬起各喂一次，打印"吞/放行"和引擎记下的原因
/// （<see cref="HotkeyEngine.LastDecision"/>），再顺手列出所有可能命中的规则。
///
/// 例：
///   KeyboardEnhance.exe --probe-key 4        （鼠标中键）
///   KeyboardEnhance.exe --probe-key mouseX1
///   KeyboardEnhance.exe --probe-key 0x14     （CapsLock）
/// </summary>
internal static class KeyProbe
{
    public static int Run(string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 按键探针 ===");
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"数据目录: {AppPaths.DataDir}");

        int vk = ParseVk(args);
        if (vk <= 0)
        {
            sb.AppendLine();
            sb.AppendLine("用法: --probe-key <键名|0xNN|NN>，例如 --probe-key mouseMiddle 或 --probe-key 0x04");
            CliOutput.Report("按键增强 · 按键探针", "probe-key.txt", sb.ToString());
            return 2;
        }

        string name = KeyName.NameOf(vk) ?? KeyName.GeneratedName(vk);
        string display = KeyName.Display(name);
        sb.AppendLine();
        sb.AppendLine($"键: {display}（{name}，vk=0x{vk:X2}）");
        sb.AppendLine($"是鼠标键: {(InputDevices.IsMouseButton(vk) ? "是" : "否")}");

        AppSettings settings;
        try
        {
            settings = SettingsStore.Load();
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine("读配置失败: " + ex.Message);
            CliOutput.Report("按键增强 · 按键探针", "probe-key.txt", sb.ToString());
            return 3;
        }

        sb.AppendLine();
        sb.AppendLine("--- 配置里和这个键有关的规则 ---");
        DescribeRules(sb, settings, vk, name);

        sb.AppendLine();
        sb.AppendLine("--- 引擎判定 ---");
        try
        {
            var queue = new ActionQueue("CapsLockPlus.Probe");
            var engine = new HotkeyEngine(queue, () => settings, (_, _) => true)
            {
                /* 探针不接设备解析器：设备一律未知 → 按"所有设备"处理。
                 * 这样结论不依赖当下插着哪只键盘/鼠标。 */
                DeviceResolver = null,
            };

            bool swallowed = engine.SimulateKey(vk, down: true);
            string? whyDown = engine.LastDecision;
            sb.AppendLine(swallowed
                ? $"按下: 被吞 ← {whyDown}"
                : "按下: 放行（原功能照常传给前台程序）");

            bool swallowedUp = engine.SimulateKey(vk, down: false);
            string? whyUp = engine.LastDecision;
            sb.AppendLine(swallowedUp
                ? $"抬起: 被吞 ← {whyUp}"
                : "抬起: 放行");

            sb.AppendLine();
            sb.AppendLine(!swallowed && !swallowedUp
                ? "结论: 这个键**没有**被本程序接管，前台程序应该能正常收到它。"
                : "结论: 这个键被上面写的原因接管了；想恢复原功能，把对应规则删掉或取消勾选那台设备。");
        }
        catch (Exception ex)
        {
            sb.AppendLine("判定失败: " + ex.Message);
        }

        /* 数据目录里的报告是加密的（CliOutput 走 DataVault），用户在资源管理器里
         * 双击打不开。探针是用来"让人直接看懂"的，所以另存一份纯文本到 %TEMP%。 */
        TryWritePlainText("clp-probe.txt", sb.ToString());

        CliOutput.Report("按键增强 · 按键探针", "probe-key.txt", sb.ToString());
        return 0;
    }

    /// <summary>顺手写一份明文（失败不影响主流程）。</summary>
    private static void TryWritePlainText(string fileName, string text)
    {
        try
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);
            System.IO.File.WriteAllText(path, text, new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine(text);
            Console.WriteLine("（已保存到 " + path + "）");
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "探针写明文");
        }
    }

    /// <summary>把可能命中这个键的规则全列出来（改键 / 替换层 / 各层绑定 / 触发键 / 设备方案）。</summary>
    private static void DescribeRules(StringBuilder sb, AppSettings settings, int vk, string name)
    {
        var combo = KeyName.BuildComboEx(KeyModifiers.None, name);
        var bare = KeyName.BuildCombo(KeyModifiers.None, name);
        bool any = false;

        if (settings.Triggers != null && settings.Triggers.Any(t => KeyName.VkOf(t.Key) == vk))
        {
            any = true;
            foreach (var t in settings.Triggers.Where(t => KeyName.VkOf(t.Key) == vk))
                sb.AppendLine($"  触发键: {t.Key} → 层 {t.LayerId}（按住它进层）");
        }

        var table = KeyRemapMap.BuildTable(settings);
        if (table.TryGet(vk, null, out int target))
        {
            any = true;
            sb.AppendLine($"  基础改键: {name} → {(KeyName.NameOf(target) ?? KeyName.GeneratedName(target))}");
        }

        if (DeviceProfiles.FindGlobalReplacement(settings, bare) is { } repl)
        {
            any = true;
            sb.AppendLine($"  原键替换层: {name} → {repl.Action}");
        }

        foreach (var p in settings.Profiles ?? new List<DeviceProfile>())
        {
            if (p.Remaps.Any(r => KeyName.VkOf(r.From) == vk))
            {
                any = true;
                sb.AppendLine($"  设备方案「{p.Name}」改键: {name} → …");
            }
            if (!p.Shared && DeviceProfiles.FindReplacement(settings, p, bare) is { } pr)
            {
                any = true;
                sb.AppendLine($"  设备方案「{p.Name}」替换层: {name} → {pr.Action}");
            }
        }

        /* 层里的组合：caps_q、caps_ctrl_q 这种。鼠标键也走同一套命名。 */
        foreach (var layer in settings.Layers ?? new List<LayerOptions>())
        {
            foreach (var k in layer.Keys ?? new List<KeyBinding>())
            {
                if (!k.IsBound) continue;
                if (!string.Equals(k.Combo, combo, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(k.Combo, bare, StringComparison.OrdinalIgnoreCase)) continue;
                any = true;
                sb.AppendLine($"  层 {layer.Id} 的绑定: {k.Combo} → {k.Action}");
            }
        }

        foreach (var layerKey in settings.Keys ?? new List<KeyBinding>())
        {
            if (!layerKey.IsBound) continue;
            if (!string.Equals(layerKey.Combo, combo, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(layerKey.Combo, bare, StringComparison.OrdinalIgnoreCase)) continue;
            any = true;
            sb.AppendLine($"  第 1 层（基础）绑定: {layerKey.Combo} → {layerKey.Action}");
        }

        if (!any) sb.AppendLine("  （没有）");
    }

    /// <summary>解析参数：支持 0x04 形式的十六进制、纯数字、或键名（mouseMiddle / caps_q…）。</summary>
    private static int ParseVk(string[] args)
    {
        string? raw = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--probe-key", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 < args.Length) raw = args[i + 1];
            break;
        }
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        raw = raw.Trim();

        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber, null, out int hex))
            return hex;
        if (int.TryParse(raw, out int dec)) return dec;

        /* 键名：`caps_q` 这种组合键取最后一段。 */
        string tail = raw.Contains('_') ? raw[(raw.LastIndexOf('_') + 1)..] : raw;
        int? vk = KeyName.VkOf(tail) ?? KeyName.VkOf(raw);
        return vk ?? 0;
    }
}
