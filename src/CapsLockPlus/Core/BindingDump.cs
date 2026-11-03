using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core;

/// <summary>
/// <c>--dump-bindings</c>：把每一层的按键绑定原样打印出来。
///
/// 为什么需要：用户反馈"我只点了『自定义按键』但没录成功，重启后那个键还是显示自定义按键"。
/// 光看界面只能看到"显示成什么"，看不到"配置里存的到底是什么"，
/// 而这个 bug 的关键恰恰在后者（动作 id 是否写进去了、Arg 是什么、在哪一层）。
/// </summary>
internal static class BindingDump
{
    /// <summary>备注（没有就不输出）。</summary>
    private static string NoteText(KeyBinding k)
        => string.IsNullOrWhiteSpace(k.Note) ? "" : $"  note=[{k.Note}]";

    /// <summary>没有动作、参数也空，却还留着备注 —— 「清除绑定」没清干净的表现。</summary>
    private static bool HasStaleNote(KeyBinding k)
        => !string.IsNullOrWhiteSpace(k.Note)
           && (k.Action is null or "doNothing")
           && string.IsNullOrWhiteSpace(k.Arg);

    public static void Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 按键绑定快照 ===");
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"数据目录: {AppPaths.DataDir}");
        sb.AppendLine();

        AppSettings s;
        try { s = SettingsStore.Load(); }
        catch (Exception ex)
        {
            sb.AppendLine("读取配置失败：" + ex.Message);
            CliOutput.Report("按键增强 · 绑定快照", "bindings.txt", sb.ToString());
            return;
        }

        sb.AppendLine($"触发键: {string.Join(", ", s.Triggers.Select(t => $"{t.Key}→层{t.LayerId}({t.HoldMs}ms)"))}");
        sb.AppendLine($"层数: {s.Layers.Count}");
        sb.AppendLine();

        int incomplete = 0;
        int staleNotes = 0;
        foreach (var layer in s.Layers)
        {
            var keys = layer.Keys.Where(k => !string.IsNullOrWhiteSpace(k.Combo)).ToList();
            sb.AppendLine($"--- 第 {layer.Id} 层「{layer.Name}」：{keys.Count} 个绑定，{layer.EffectiveSubLayers.Count} 个子层 ---");

            foreach (var k in keys)
            {
                string arg = k.Arg ?? "";
                if (arg.Length > 60) arg = arg[..60] + "…";

                string flag = "";
                if (CapsLockPlus.UI.AppHost.IsIncompleteAction(k))
                {
                    flag = "   <<<< 未完成（空壳动作）";
                    incomplete++;
                }
                if (HasStaleNote(k))
                {
                    flag += "   <<<< 残留备注（没有动作却还有备注）";
                    staleNotes++;
                }

                sb.AppendLine($"  {k.Combo,-18} action={k.Action,-14} arg=[{arg}]{NoteText(k)}{flag}");
            }

            foreach (var sub in layer.EffectiveSubLayers)
            {
                var subKeys = sub.Keys.Where(k => !string.IsNullOrWhiteSpace(k.Combo)).ToList();
                if (subKeys.Count == 0) continue;
                sb.AppendLine($"  ── 子层 {sub.Key}「{sub.Name}」：{subKeys.Count} 个绑定 ──");
                foreach (var k in subKeys)
                {
                    string arg = k.Arg ?? "";
                    if (arg.Length > 60) arg = arg[..60] + "…";
                    string flag = CapsLockPlus.UI.AppHost.IsIncompleteAction(k) ? "   <<<< 未完成（空壳动作）" : "";
                    if (CapsLockPlus.UI.AppHost.IsIncompleteAction(k)) incomplete++;
                    if (HasStaleNote(k)) { flag += "   <<<< 残留备注"; staleNotes++; }
                    sb.AppendLine($"    {k.Combo,-16} action={k.Action,-14} arg=[{arg}]{NoteText(k)}{flag}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine($"未完成的空壳动作合计: {incomplete} 个（启动时应被清理成 doNothing）");
        sb.AppendLine($"残留备注（没有动作却还留着备注）: {staleNotes} 个");

        CliOutput.Report("按键增强 · 绑定快照", "bindings.txt", sb.ToString());
    }
}
