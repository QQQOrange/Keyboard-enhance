using System;
using System.Collections.Generic;
using System.Linq;
using CapsLockPlus.Core.Settings;
using MacroModel = CapsLockPlus.Core.Settings.Macro;

namespace CapsLockPlus.Core;

/// <summary>
/// `--repair-quickachieve-macro`：修复“只录到 Ctrl/Alt/Shift，没录到字母”的
/// Quick Achieve 网格宏。录制时如果字母键被目标程序先吞掉，宏会变成
/// “按住三个修饰键再松开”，播放时自然唤不出网格。这里补上 H 的按下/抬起。
/// </summary>
internal static class MacroRepair
{
    private static readonly HashSet<string> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ctrl", "control", "lctrl", "rctrl",
        "alt", "lalt", "ralt",
        "shift", "lshift", "rshift",
        "win", "lwin", "rwin",
    };

    public static int Run(string[] args)
    {
        if (!SettingsStore.TryLoadFile(AppPaths.SettingsFile, out var settings, out string error))
        {
            Console.Out.WriteLine("读不出来：" + error);
            return 2;
        }

        int fixedCount = 0;
        foreach (var macro in settings.Macros ?? new List<MacroModel>())
        {
            if (!macro.Name.Contains("Quick", StringComparison.OrdinalIgnoreCase) &&
                !macro.Name.Contains("网格", StringComparison.OrdinalIgnoreCase) &&
                !macro.Name.Contains("Achieve", StringComparison.OrdinalIgnoreCase))
                continue;

            var letterDowns = macro.Steps
                .Where(s => s.Kind == "keyDown" && !Modifiers.Contains(s.Key))
                .ToList();
            if (letterDowns.Count > 0) continue; // 已经录到字母，不动

            int insertAt = -1;
            for (int i = 0; i < macro.Steps.Count; i++)
            {
                var step = macro.Steps[i];
                if (step.Kind == "keyUp") { insertAt = i; break; }
                if (step.Kind == "keyDown" && Modifiers.Contains(step.Key)) insertAt = i + 1;
            }
            if (insertAt < 0) insertAt = macro.Steps.Count;

            /* 三个修饰键先按住，再按 H，停 40ms 让目标程序的热键处理完，
             * 最后才抬起修饰键。40ms 是 SendKeys 默认按键间隔的两倍左右。 */
            macro.Steps.InsertRange(insertAt, new[]
            {
                new MacroStep { Kind = "keyDown", Key = "h" },
                new MacroStep { Kind = "delay", DelayMs = 40 },
                new MacroStep { Kind = "keyUp", Key = "h" },
            });
            fixedCount++;
            Console.Out.WriteLine("已修复宏：" + macro.Name + "（补上 H 的按下/抬起）");
        }

        if (fixedCount == 0)
        {
            Console.Out.WriteLine("没有找到需要修复的 Quick Achieve 宏。");
            return 0;
        }

        if (!SettingsStore.Save(settings))
        {
            Console.Out.WriteLine("保存失败，设置未改动。");
            return 3;
        }

        Console.Out.WriteLine("已保存，共修复 " + fixedCount + " 个宏。");
        return 0;
    }
}
