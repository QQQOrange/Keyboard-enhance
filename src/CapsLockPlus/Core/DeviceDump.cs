using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CapsLockPlus.Core.Input;

namespace CapsLockPlus.Core;

/// <summary>
/// `--devices`：把 Raw Input 看到的键盘/鼠标、归并后的标识、以及它们在设备树里的
/// 物理父设备一起打印出来。列表里"设备太多"时用它定位到底是哪一层在重复。
/// </summary>
internal static class DeviceDump
{
    public static int Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=== 输入设备 ===");
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine();

        var keyboards = KeyboardDevices.List();
        var mice = MouseDevices.List();

        Dump(sb, "键盘（" + keyboards.Count + " 个集合）",
            keyboards.Select(k => (k.Path, k.Name, k.Kind)));

        Dump(sb, "鼠标（" + mice.Count + " 个集合）",
            mice.Select(m => (m.Path, m.Name, m.Kind)));

        sb.AppendLine("--- 归并后（用户看到的行）---");
        var groups = InputDevices.All().ToList();
        sb.AppendLine("共 " + groups.Count + " 行");
        foreach (var g in groups)
        {
            sb.AppendLine("  " + (g.HasKeyboard && g.IsMouse ? "[键盘+鼠标] " : g.IsMouse ? "[鼠标] " : "[键盘] ") + g.Key);
            sb.AppendLine("      名字: " + g.Name);
            sb.AppendLine("      类别: " + g.Kind);
            sb.AppendLine("      短名: " + InputDevices.ShortNameOf(g.Key));
        }

        string text = sb.ToString();
        Console.Out.Write(text);

        try
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clp-devices.txt");
            System.IO.File.WriteAllText(path, text, Encoding.UTF8);
            Console.Out.WriteLine("（已写入 " + path + "）");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "写出设备清单"); }

        return 0;
    }

    private static void Dump(StringBuilder sb, string title,
        IEnumerable<(string Path, string Name, string Kind)> items)
    {
        var list = items.ToList();
        sb.AppendLine("--- " + title + " ---");
        if (list.Count == 0) { sb.AppendLine("(空)"); sb.AppendLine(); return; }

        foreach (var it in list)
        {
            string instance = DeviceTree.InstanceOf(it.Path);
            string physical = DeviceTree.Physical(instance);

            sb.AppendLine("  " + it.Name + "  [" + it.Kind + "]");
            sb.AppendLine("      key     : " + KeyboardDevices.KeyOf(it.Path));
            sb.AppendLine("      instance: " + instance);
            sb.AppendLine("      physical: " + (physical == instance ? "(即自身)" : physical));
            sb.AppendLine("      parent  : " + Clip(DeviceTree.Parent(physical)));
        }

        sb.AppendLine();
    }

    private static string Clip(string text)
        => string.IsNullOrEmpty(text) ? "(无)" : text;
}
