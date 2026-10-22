using System.Text.Json;
using System.Text.Json.Nodes;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Sync;

/// <summary>
/// 「合并恢复」的实现。
///
/// 为什么需要它：覆盖恢复等于把当前配置整份扔掉 ——
/// 用户在 A 电脑上添了几个宏、又在 B 电脑上改了键位，
/// 直接覆盖必然丢掉一边。合并是把两边的**并集**留下来。
///
/// 规则（按各实体天然的主键对齐）：
///   * 按键绑定 → 层内按 Combo；同一个 Combo 远端覆盖，本地独有的保留；
///   * 层 / 宏 / 触发键 → 按 Id / Key；
///   * 搜索项、启动项、网址、热字串 → 按 Key；
///   * 窗口绑定 → 按 Slot；
///   * **全局 / Qbar / 同步设置这类单值配置，一律保留本机** ——
///     尤其是 Sync：远端备份里存着别人的网盘密码和备份密码，
///     合并时把它灌进来，等于把同步目标换成别人的服务器。
///
/// 在 JSON 层做而不是逐个类写 Merge：实体字段以后会加，
/// 逐个实现必然漏字段（漏了就"合并完少半条配置"），
/// 这里只认主键，其余字段整体搬运。
/// </summary>
public static class BackupMerge
{
    /// <summary>「数组 + 主键」的清单表。</summary>
    private static readonly (string Prop, string Key)[] Lists =
    {
        ("Triggers", "Key"),
        ("Macros", "Id"),
        ("Search", "Key"),
        ("Run", "Key"),
        ("Web", "Key"),
        ("HotStrings", "Key"),
        ("Bindings", "Slot"),
    };

    /// <summary>合并一份备份进当前设置。返回合并结果（不修改入参）。</summary>
    public static (AppSettings Merged, int Added, int Replaced) Merge(
        AppSettings local, AppSettings remote)
    {
        var ln = JsonSerializer.SerializeToNode(local, SettingsStore.JsonOptions)!.AsObject();
        var rn = JsonSerializer.SerializeToNode(remote, SettingsStore.JsonOptions)!.AsObject();

        int added = 0, replaced = 0;

        foreach (var (prop, key) in Lists)
            MergeArray(ln, rn, prop, key, ref added, ref replaced);

        MergeLayers(ln, rn, ref added, ref replaced);

        var merged = JsonSerializer.Deserialize<AppSettings>(
            ln.ToJsonString(), SettingsStore.JsonOptions)
            ?? throw new InvalidOperationException("合并后的配置解析失败");

        SettingsStore.Normalize(merged);
        return (merged, added, replaced);
    }

    // ---------------- 通用数组合并 ----------------

    private static void MergeArray(JsonObject local, JsonObject remote, string prop,
        string key, ref int added, ref int replaced)
    {
        if (remote[prop] is not JsonArray ra) return;

        if (local[prop] is not JsonArray la)
        {
            local[prop] = ra.DeepClone();
            added += ra.Count;
            return;
        }

        MergeKeyed(la, ra, key, ref added, ref replaced);
    }

    /// <summary>按主键把远端数组合进本地数组。</summary>
    private static void MergeKeyed(JsonArray local, JsonArray remote, string key,
        ref int added, ref int replaced)
    {
        foreach (var rv in remote)
        {
            if (rv is not JsonObject ro) continue;
            string k = Text(ro[key]);
            if (k.Length == 0) continue;

            int idx = IndexOf(local, key, k);
            if (idx < 0)
            {
                local.Add(rv.DeepClone());
                added++;
            }
            else
            {
                /* 内容完全一样就不算"覆盖"，直接跳过。
                 *
                 * 两边的默认配置有几百条相同的内置项，全算成"覆盖"的话，
                 * 用户会看到"合并完成：覆盖 326 项"，以为配置被改得面目全非，
                 * 实际上一条都没变。计数得反映真实的改动量。 */
                if (JsonNode.DeepEquals(local[idx], rv)) continue;
                local[idx] = rv.DeepClone();
                replaced++;
            }
        }
    }

    private static int IndexOf(JsonArray arr, string key, string value)
    {
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonObject o && Same(Text(o[key]), value)) return i;
        }
        return -1;
    }

    private static string Text(JsonNode? node)
    {
        try { return node?.ToString()?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---------------- 层（带嵌套按键） ----------------

    private static void MergeLayers(JsonObject local, JsonObject remote,
        ref int added, ref int replaced)
    {
        if (remote["Layers"] is not JsonArray ra) return;

        if (local["Layers"] is not JsonArray la)
        {
            local["Layers"] = ra.DeepClone();
            added += ra.Count;
            return;
        }

        foreach (var rv in ra)
        {
            if (rv is not JsonObject ro) continue;
            string id = Text(ro["Id"]);
            if (id.Length == 0) continue;

            int idx = IndexOf(la, "Id", id);
            if (idx < 0)
            {
                la.Add(rv.DeepClone());
                added++;
                continue;
            }

            if (la[idx] is not JsonObject lo) continue;

            // 层名：远端空名不覆盖本地（远端常是老版本导出的无名层）
            string name = Text(ro["Name"]);
            if (name.Length > 0 && !Same(name, Text(lo["Name"])))
            {
                lo["Name"] = name;
                replaced++;
            }
            if (ro["Enabled"] != null && !JsonNode.DeepEquals(lo["Enabled"], ro["Enabled"]))
            {
                lo["Enabled"] = ro["Enabled"]!.DeepClone();
                replaced++;
            }

            if (ro["Keys"] is JsonArray rk)
            {
                if (lo["Keys"] is JsonArray lk)
                    MergeKeyed(lk, rk, "Combo", ref added, ref replaced);
                else
                {
                    lo["Keys"] = rk.DeepClone();
                    added += rk.Count;
                }
            }
        }
    }
}
