using System;
using System.IO;
using System.Linq;
using System.Text;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core;

/// <summary>
/// `--check-remaps`：把当前配置里的改键规则打印出来（排查"Ctrl 变 Alt"这类问题）。
/// 只读，不改配置。
/// </summary>
public static class RemapCheck
{
    public static int Run()
    {
        var sb = new StringBuilder();
        try
        {
            var s = SettingsStore.Load();

            sb.AppendLine("=== 基础改键（BaseRemaps）===");
            if (s.Global.BaseRemaps.Count == 0) sb.AppendLine("(空)");
            foreach (var r in s.Global.BaseRemaps)
                sb.AppendLine($"  {r.From} -> {r.To}  设备={((string.IsNullOrWhiteSpace(r.Device)) ? "所有" : r.Device)}  备注={r.Comment}");

            sb.AppendLine();
            sb.AppendLine("=== 按应用改键（AppLayers）===");
            if (s.Global.AppLayers.Count == 0) sb.AppendLine("(空)");
            foreach (var l in s.Global.AppLayers)
                sb.AppendLine($"  [{l.Process}] -> 叠加层 {l.LayerId}");

            sb.AppendLine();
            sb.AppendLine("=== 层内绑定里带 remap 的动作 ===");
            bool any = false;
            foreach (var layer in s.Layers)
            foreach (var k in layer.Keys)
            {
                if (k.Action != null && k.Action.StartsWith("remap", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"  层{layer.Id} {k.Combo} => {k.Action} {k.Arg}");
                    any = true;
                }
            }
            if (!any) sb.AppendLine("(空)");

            sb.AppendLine();
            sb.AppendLine("=== 触发键 ===");
            foreach (var t in s.Triggers)
                sb.AppendLine($"  {t.Key} -> 层 {t.LayerId}  启用={t.Enabled}");

            /* 设备方案：按设备分开设置之后，"我的 Caps 为什么在这只键盘上没改"这类问题
             * 八成答案在这里 —— 某台设备被取消勾选、或者它有一份独立方案没配这个键。 */
            sb.AppendLine();
            sb.AppendLine($"=== 设备方案（{s.Profiles.Count} 份，全部生效={s.Global.DeviceScopeAll}）===");
            if (s.Profiles.Count == 0) sb.AppendLine("(空：所有设备共用一份设置)");
            foreach (var p in s.Profiles)
            {
                sb.AppendLine($"  [{(p.Shared ? "通用" : p.Active ? "生效" : "不接管")}] " +
                              $"{(string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name)}  " +
                              $"继承={p.Inherit}  设备={string.Join(",", p.Devices)}");
                foreach (var r in p.Remaps)
                    sb.AppendLine($"      改键 {r.From} -> {r.To}");
                foreach (var l in p.Layers)
                {
                    sb.AppendLine($"      层{l.Id}：{l.Keys.Count} 条绑定");
                    foreach (var k in l.Keys)
                        sb.AppendLine($"        {k.Combo} => {k.Action} {k.Arg}");
                }
            }

            string path = Path.Combine(Path.GetTempPath(), "clp-remaps.txt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine(path);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("读取配置失败：" + ex.Message);
            return 1;
        }
    }
}
