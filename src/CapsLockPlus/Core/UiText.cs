using System;

namespace CapsLockPlus.Core;

/// <summary>
/// 界面上给用户看的短句，统一在这里生成（审视报告 P2-4）。
///
/// 背景：全仓的提示文案原来有三套口径 ——
///   · Toast：`已恢复默认设置`（无前缀、无标点）
///   · 页面状态行：`✓ 已删除` / `✗ 删除失败：xxx`（带符号）
///   · 对话框：多段正式说明
/// 同一个动作在三个地方说法都不一样，用户会觉得"不是一个人写的"。
///
/// 现在统一成三条规则：
///   1. **成功**用「已 + 动词」，不写"成功"二字 —— `已恢复默认设置`；
///   2. **失败**用「没能 + 动作 + ： + 原因」—— `没能打开网址：没有默认浏览器`。
///      比原来的「打开网址失败：xxx」更清楚"谁没做到"，读起来也短；
///   3. 页面内的状态行才加 ✓ / ✗ 前缀，Toast 与对话框不加（气泡本身就在说成败）。
///
/// 这类字符串拼接很便宜，但**必须只有一处定义**，否则下次改口径又要全仓搜。
/// </summary>
public static class UiText
{
    /// <summary>页面内状态行的成功前缀。</summary>
    public const string OkMark = "✓ ";

    /// <summary>页面内状态行的失败前缀。</summary>
    public const string FailMark = "✗ ";

    /// <summary>成功：`已 …`。</summary>
    public static string Done(string what) => "已" + what;

    /// <summary>
    /// 失败：`没能<做什么>：<原因>`。原因为空时只保留前半句。
    /// </summary>
    public static string Failed(string what, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "没能" + what;

        // 原因里可能已经带句号或冒号（来自系统异常），去掉重复标点
        string r = reason.Trim().TrimEnd('。', '.', '；', ';');
        return $"没能{what}：{r}";
    }

    /// <summary>状态行：成功（带 ✓）。</summary>
    public static string OkLine(string text) => OkMark + text;

    /// <summary>状态行：失败（带 ✗）。</summary>
    public static string FailLine(string text) => FailMark + text;
}
