namespace CapsLockPlus.Core.Input;

/// <summary>
/// 「正在等待用户按键」的窗口的统一入口（当前只有自定义按键捕获窗在用）。
///
/// ## 为什么需要这条路
///
/// 捕获窗原来**只**依赖自己临时装的低级键盘钩子（`SetWindowsHookEx(WH_KEYBOARD_LL)`）。
/// 而低级钩子在这台机器上是**不可靠**的：Windows 对钩子回调有硬性超时
/// （`LowLevelHooksTimeout`，默认 300ms），一旦超时，系统会**静默摘掉整条钩子**——
/// 句柄还在、`IsHookAlive` 照样是 true，但一个按键都收不到。日志里能直接看到：
///
/// ```text
/// [WARN] 键盘钩子已经 10 秒没收到任何按键，改由 Raw Input 触发热键
/// [WARN] 键盘钩子漏掉了 12 个**物理**按键（Raw Input 收到了）—— 判定钩子被系统摘掉
/// ```
///
/// 主程序的热键因此有 Raw Input 兜底，捕获窗却什么都没有 —— 表现就是
/// 「点开『按下要发送的键』，按任何键都没反应」。
///
/// 所以加这条通道：Raw Input 看到的按键（它不受钩子超时影响）直接送进捕获窗。
/// 捕获窗同时还接了 WPF 的 PreviewKeyDown（窗口有焦点就一定收得到），
/// 三路来源在窗口里按 (vk, down) 去重。
/// </summary>
public static class KeyCaptureSink
{
    /// <summary>当前打开着的捕获窗；没有窗口时为 null。</summary>
    public static Action<int, bool>? Current;

    /// <summary>
    /// 把一次 Raw Input 按键送给捕获窗。
    /// 捕获窗里的任何异常都不能影响输入链（这是钩子/热路径之外，但同理）。
    /// </summary>
    public static void Push(int vk, bool down)
    {
        var cur = Current;
        if (cur == null) return;
        try { cur(vk, down); }
        catch { /* 预期内：界面侧异常不该冒泡到输入链 */ }
    }
}
