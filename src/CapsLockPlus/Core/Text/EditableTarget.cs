using System.Windows.Automation;

namespace CapsLockPlus.Core.Text;

/// <summary>
/// 判断"当前焦点能不能接受文字输入"——决定文本片段是否展开。
///
/// ## 为什么需要
///
/// 片段展开 = 删掉触发词（N 次 Backspace）+ 输入内容。
/// 如果焦点在按钮、菜单、只读文本框上：
///   · Backspace 可能触发"返回上一页"，或者根本删不掉；
///   · 内容会打到别处，或者干脆丢失。
/// 用户看到的是"按了个键，界面乱跳"。
///
/// ## 判定策略：只对"明确不可编辑"跳过
///
/// 拿不到信息时**默认允许**。原因是很多程序（游戏、自绘界面、部分 Electron）
/// 的 UIA 树是空的，如果"拿不到就跳过"，这些程序里的片段会整体失效 ——
/// 那比"偶尔错乱"更糟。
///
/// 所以只在三种**能确定**的情况下拒绝：
///   ① 焦点元素是按钮/菜单/列表项这类明显不接收文字的控件；
///   ② 支持 ValuePattern 且 IsReadOnly = true；
///   ③ 支持 TextPattern 且 IsReadOnly = true。
///
/// ## 线程
///
/// UIA 是 COM，必须 STA。调用点在动作队列线程（本身就是 STA），
/// **绝不能**放在键盘钩子里 —— 那会拖慢每一次按键。
/// </summary>
public static class EditableTarget
{
    /// <summary>这些控件类型一定不接收文字（小写，与 UIA 的 ProgrammaticName 后半段一致）。</summary>
    private static readonly HashSet<string> NonTextControls = new(StringComparer.OrdinalIgnoreCase)
    {
        "button", "checkbox", "radiobutton", "menuitem", "menubar", "menu",
        "tabitem", "tab", "toolbar", "slider", "progressbar", "image",
        "hyperlink", "listitem", "list", "treeitem", "tree", "scrollbar",
        "titlebar", "statusbar", "tooltip", "separator", "thumb",
    };

    /// <summary>
    /// 纯函数判定：给定控件类型与只读标记，是否应该跳过展开。
    /// 抽出来是为了能自检 —— UIA 在无桌面环境里取不到东西。
    /// </summary>
    public static bool ShouldSkip(string? controlTypeName, bool? isValueReadOnly, bool? isTextReadOnly)
    {
        if (isValueReadOnly == true) return true;
        if (isTextReadOnly == true) return true;

        if (string.IsNullOrEmpty(controlTypeName)) return false;      // 未知：允许
        return NonTextControls.Contains(controlTypeName);
    }

    /// <summary>当前焦点是否可接受文字输入。取不到信息时返回 true（默认允许）。</summary>
    public static bool AllowsTextInput()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null) return true;

            string type = focused.Current.ControlType?.ProgrammaticName ?? "";
            // ProgrammaticName 形如 "ControlType.Edit"，取后半段
            int dot = type.LastIndexOf('.');
            if (dot >= 0) type = type[(dot + 1)..];

            bool? valueReadOnly = null;
            try
            {
                if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out object vp))
                    valueReadOnly = ((ValuePattern)vp).Current.IsReadOnly;
            }
            catch { }   /* 预期内：探测型，取不到就按"未知"处理 */

            /* TextPattern 本身不提供"是否只读"，别在这里硬查 ——
             * 只读与否由 ValuePattern 与控件类型覆盖即可。 */
            bool skip = ShouldSkip(type, valueReadOnly, null);
            if (skip) CoreLog.Info($"[片段] 焦点控件不可编辑（{type}），跳过展开");
            return !skip;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[片段] 判断焦点可编辑性失败（按允许处理）：" + ex.Message);
            return true;
        }
    }
}
