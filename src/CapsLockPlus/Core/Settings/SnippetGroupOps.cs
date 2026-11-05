using System;
using System.Collections.Generic;
using System.Linq;

namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 别名替换的「分组」操作（纯数据，方便自检）。
///
/// 分组名单独存在 <see cref="GlobalOptions.SnippetGroups"/> 里，
/// 与别名身上实际用到的组名取并集显示 —— 这样"空分组"也能存在，
/// 用户可以先建组、再往里放东西。
/// </summary>
public static class SnippetGroupOps
{
    /// <summary>去空白后的分组名比较（不区分大小写，符合中文用户直觉）。</summary>
    private static bool Same(string? a, string? b)
        => string.Equals(a?.Trim(), b?.Trim(), StringComparison.CurrentCultureIgnoreCase);

    /// <summary>所有分组：手动建的 ∪ 别名里用到的，去重后按名称排序。</summary>
    public static List<string> All(AppSettings s)
    {
        return s.Global.SnippetGroups
            .Concat(s.Global.Snippets.Select(x => x.Group?.Trim())!)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g!)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 把 <paramref name="dragged"/> 移动到 <paramref name="before"/> 之前
    /// （before 为 null 表示移到最后），结果写进 <see cref="GlobalOptions.SnippetGroupOrder"/>。
    ///
    /// 拖拽排序的核心逻辑放在这里（纯数据），界面只负责把坐标换成"拖到谁头上"。
    /// </summary>
    public static List<string> Reorder(AppSettings s, string dragged, string? before)
    {
        var order = s.Global.SnippetGroupOrder?.Count > 0
            ? s.Global.SnippetGroupOrder.ToList()
            : All(s);

        /* 把还没登记过的分组补进来，保证一次拖动不会把别的组弄丢。 */
        foreach (string g in All(s))
            if (!order.Any(x => Same(x, g))) order.Add(g);

        order.RemoveAll(x => Same(x, dragged));

        int idx = before == null ? -1 : order.FindIndex(x => Same(x, before));
        if (idx < 0) order.Add(dragged);
        else order.Insert(idx, dragged);

        s.Global.SnippetGroupOrder = order;
        return order;
    }

    /// <summary>新建分组；已存在返回 false（不重复添加）。</summary>
    public static bool Add(AppSettings s, string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return false;
        if (s.Global.SnippetGroups.Any(g => Same(g, name))) return false;

        s.Global.SnippetGroups.Add(name!);
        return true;
    }

    /// <summary>
    /// 重命名分组：把它下面的别名一起改过去。
    /// 返回跟着改名的别名条数；新名字为空或已存在则返回 -1（表示没做）。
    /// </summary>
    public static int Rename(AppSettings s, string oldName, string? newName)
    {
        newName = newName?.Trim();
        if (string.IsNullOrEmpty(newName)) return -1;
        if (Same(oldName, newName)) return 0;

        /* 目标名字已经被别的分组用着：不动，交给界面提示"已存在"。 */
        if (All(s).Any(g => Same(g, newName) && !Same(g, oldName))) return -1;

        int i = s.Global.SnippetGroups.FindIndex(g => Same(g, oldName));
        if (i >= 0) s.Global.SnippetGroups[i] = newName!;
        else s.Global.SnippetGroups.Add(newName!);   // 只在别名里出现过的组，改名后也要登记

        int moved = 0;
        foreach (var snippet in s.Global.Snippets.Where(x => Same(x.Group, oldName)))
        {
            snippet.Group = newName!;
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// 删除分组：组里的别名**回到未分组**（不删别名）。返回受影响的别名条数。
    /// </summary>
    public static int Delete(AppSettings s, string name)
    {
        s.Global.SnippetGroups.RemoveAll(g => Same(g, name));

        int moved = 0;
        foreach (var snippet in s.Global.Snippets.Where(x => Same(x.Group, name)))
        {
            snippet.Group = "";
            moved++;
        }

        return moved;
    }

    /// <summary>把一条别名移到某个分组（空字符串 = 未分组）。</summary>
    public static void Move(AppSettings s, Snippet snippet, string? group)
    {
        string target = group?.Trim() ?? "";
        snippet.Group = target;

        if (target.Length > 0) Add(s, target);
    }
}
