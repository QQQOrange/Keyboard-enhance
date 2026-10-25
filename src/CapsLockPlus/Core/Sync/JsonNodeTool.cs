using System.Text.Json.Nodes;

namespace CapsLockPlus.Core.Sync;

/// <summary>
/// JsonNode 的深拷贝 / 深度相等比较。
///
/// 为什么不用 .NET 8 起自带的 <c>JsonNode.DeepClone()</c> / <c>JsonNode.DeepEquals()</c>：
/// 本项目有一条 Win7 专用线（net7.0-windows，.NET 10 不支持 Win7），那两个 API 在
/// .NET 7 上不存在。写成两条线都能编译的共用实现，行为就永远一致。
///
/// DeepClone 用「序列化再解析」：JsonNode 本身就是 JSON 文档树，走一遍
/// ToJsonString/Parse 是最小惊奇的深拷贝（属性顺序、数字写法原样保留）。
/// DeepEquals 按结构递归比：对象比键集合、数组比长度、值比序列化文本。
/// </summary>
internal static class JsonNodeTool
{
    public static JsonNode? DeepClone(JsonNode? node)
    {
        if (node is null) return null;
        return JsonNode.Parse(node.ToJsonString());
    }

    public static bool DeepEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;

        if (a is JsonObject oa && b is JsonObject ob)
        {
            if (oa.Count != ob.Count) return false;
            foreach (var kv in oa)
            {
                if (!ob.TryGetPropertyValue(kv.Key, out var other)) return false;
                if (!DeepEquals(kv.Value, other)) return false;
            }
            return true;
        }

        if (a is JsonArray aa && b is JsonArray ab)
        {
            if (aa.Count != ab.Count) return false;
            for (int i = 0; i < aa.Count; i++)
            {
                if (!DeepEquals(aa[i], ab[i])) return false;
            }
            return true;
        }

        if (a is JsonValue && b is JsonValue)
        {
            // JsonValue 的序列化文本保留了原始数字写法与字符串转义，
            // 文本相同即视为相等（配置合并场景里数字都由本程序自己序列化，
            // 不会出现 "1" 与 "1.0" 并存却要求相等的情况）。
            return a.ToJsonString() == b.ToJsonString();
        }

        return false;   // 类型不同（对象 vs 数组 vs 值）
    }
}
