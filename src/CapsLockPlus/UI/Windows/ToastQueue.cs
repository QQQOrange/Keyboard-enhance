namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 提示消息的「单槽位」队列：不管塞进来多少条，永远只保留**最新的一条**。
///
/// 为什么要这么个东西：动作在钩子线程/动作线程上跑，提示要显示在界面线程。
/// 连按十个键时如果每条都排一次调度，界面线程忙完会把十条依次执行一遍 ——
/// 用户看到的是十几次闪烁，最后停在一条已经过时的消息上，观感就是"提示很慢"。
///
/// 这里的做法：消息只写进槽位（覆盖旧的），调度标记只置一次。
/// 界面线程来取的时候拿到的自然是最后那条，中间的自动作废。
///
/// 单独抽成类是为了能被自检直接断言 —— 窗口本身在无头环境里没法验。
/// </summary>
public sealed class ToastQueue
{
    private readonly object _gate = new();
    private (string Text, string Kind)? _slot;
    private bool _scheduled;

    /// <summary>
    /// 放入一条消息（覆盖掉还没被取走的旧消息）。
    /// 返回 true 表示调用方需要安排一次刷新；返回 false 表示已经安排过了，
    /// 那次刷新会顺手取走这条，不用再排。
    /// </summary>
    public bool Post(string text, string kind)
    {
        lock (_gate)
        {
            _slot = (text, kind);
            if (_scheduled) return false;
            _scheduled = true;
            return true;
        }
    }

    /// <summary>取走当前最新的一条；槽里是空的就返回 null。</summary>
    public (string Text, string Kind)? Take()
    {
        lock (_gate)
        {
            var item = _slot;
            _slot = null;
            _scheduled = false;
            return item;
        }
    }
}
