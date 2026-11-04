using System;
using System.Collections.Generic;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 连续量重复（partial 拆分之二）。
///
/// 指"按住不放就连续变化"的动作：音量、亮度、滚轮。
/// 它们不能靠系统自动重复驱动 —— 那套速率由用户的键盘设置决定
/// （首次延迟 250~1000ms、之后每 30~50ms），还会和引擎自己的节奏叠加成"按住后突然加速"。
///
/// 所以这里自己维护一份计时表：按下记一笔，之后由 <see cref="CheckContinuousRepeat"/>
/// 按固定节奏执行，松开或退出层立刻清掉。每个被按住的键各有一份状态，互不打断。
/// </summary>
public sealed partial class HotkeyEngine
{
    /* ---- 连续量动作（音量 / 亮度）的按住重复 ----
     *
     * 为什么不用系统的键盘自动重复：那个延迟和速率是用户在
     * "键盘属性"里设的，可以设成 2.5 次/秒甚至关掉；
     * 而且远程桌面 / 部分 KVM 里根本不来重复事件。
     * 用户要的是"按住就一直在动、一点一点地动"，
     * 那节奏就得自己掌握，不能交给环境。
     *
     * 节奏取 380ms 起播、45ms 一步（约 22 步/秒）：
     * 配合每次 1% 的细调，看起来是一条平滑的滑条。
     *
     * ⚠ v4.3.0：从"一个槽位"改成"按按键分别记录"。
     *
     * 旧实现只有一个 `_repeatVk`。用户按住亮度+ 时（正在连发），
     * 再按一下音量+，槽位就被音量顶掉 —— 松开音量时
     * `OnKeyUpInLayer` 把整条重复取消，亮度明明还按着却不再涨；
     * 反过来也一样。连续调两个量时互相打断，看起来就像"没反应"。
     * 现在每个被按住的连续键各有自己的节奏，互不干扰。 */
    private sealed class RepeatState
    {
        public string Action = "";
        public string Arg = "";
        public DateTime StartedAt = DateTime.MinValue;
        public DateTime NextAt = DateTime.MinValue;
    }

    /* 连续重复的"账本"。
     *
     * 写入在**钩子线程**（按下 / 抬起），读取在**界面线程**（20ms 计时器）。
     * 字典本身不是线程安全的，所以两边都过 <see cref="_repeatGate"/>；
     * 计时器那侧先取快照、在锁外执行动作，钩子回调里不会等它。 */
    private readonly object _repeatGate = new();
    private readonly Dictionary<int, RepeatState> _repeats = new();

    /// <summary>按住多久之后开始连续（毫秒）。</summary>
    public const int RepeatDelayMs = 380;

    /// <summary>连续时每一步的间隔（毫秒）。默认给亮度这种"每步 1%"的动作。</summary>
    public const int RepeatIntervalMs = 45;

    /// <summary>
    /// 驱动"按住连续变化"。
    ///
    /// 每 20ms 由长按计时器调一次：到了时间就再执行一次绑定动作。
    /// 物理上已经松开（抬起事件丢了）就立刻停 —— 否则松开手音量还会一直涨。
    /// </summary>
    private void CheckContinuousRepeat()
    {
        /* 取一份快照再遍历：字典可能正被钩子线程改（用户松开 / 又按下另一个键），
         * 直接遍历会抛"集合已修改"。快照只复制几个引用，开销可以忽略。 */
        KeyValuePair<int, RepeatState>[] snapshot;
        lock (_repeatGate)
        {
            if (_repeats.Count == 0) return;
            snapshot = _repeats.ToArray();
        }

        if (!Enabled || Suspended)
        {
            ClearContinuousRepeat();
            return;
        }

        var now = DateTime.Now;
        List<int>? expired = null;

        /* ---- 主判据：钩子自己记的"这个键按下过、还没收到抬起" ----
         *
         * 不能用 GetAsyncKeyState 当主判据：注入按键之后、切桌面、
         * 远程会话切换时它都会短暂读不准。拿它做主判据的后果是
         * "按住音量键刚开始动一下就停了" —— 比不连续更糟。
         *
         * `_swallowedDown` 是我们自己在按下时加、收到抬起时删的，
         * 它才真正代表"用户还按着"。 */
        foreach (var (vk, state) in snapshot)
        {
            if (!_swallowedDown.ContainsKey(vk))
            {
                (expired ??= new List<int>()).Add(vk);
                continue;
            }

            /* ---- 兜底：抬起事件丢了 ----
             *
             * 主判据是 `_swallowedDown`（钩子收到 up 才会删），它足够可靠。
             * 真正的风险是钩子**收不到** up（切桌面 / 远程接管 / 睡眠唤醒），
             * 那时它一直留着，用户松手了音量还在涨。
             *
             * 这里不用物理读数做兜底 —— 理由有两条：
             *   · 亮度动作根本不注入按键，`GetAsyncKeyState` 对这类"只改内部状态"
             *     的键没有参考价值；
             *   · 注入媒体键时读数也会短暂失真，反而会把正在长按的用户打断
             *     （v4.2.1 / v4.2.2 连续踩过两次）。
             *
             * 改用"最长持续时间"：正常人按住不会超过十秒，超过就认为
             * 抬起事件丢了，强制收手。既不会无限涨，也不会误伤长按。 */
            if ((now - state.StartedAt).TotalSeconds > MaxContinuousSeconds)
            {
                CoreLog.Warn($"连续调节已持续 {MaxContinuousSeconds} 秒，强制停止（抬起事件可能丢了）");
                (expired ??= new List<int>()).Add(vk);
                continue;
            }

            if (now < state.NextAt) continue;

            string action = state.Action;
            string arg = state.Arg;
            state.NextAt = now.AddMilliseconds(RepeatIntervalFor(action));

            /* 连续调节本身就是"用户还在按住"的证据，当然算按键活动。
             *
             * 不更新的话，长按 400ms 后就没有活动记录了：如果此时
             * `GetAsyncKeyState(触发键)` 恰好读不准（注入媒体键、CapsLock
             * 这类被系统特殊处理的键），自愈会把整个层复位 ——
             * 表现是"按住音量键正在涨，突然就不涨了，而且层也没了"。 */
            _lastKeyActivity = now;

            _queue.Post(() =>
            {
                try
                {
                    _invoke(action, arg);
                    ActionTriggered?.Invoke(action);
                }
                catch (Exception ex) { CoreLog.Error($"动作 {action} 连续执行失败", ex); }
            });
        }

        if (expired != null)
        {
            // 遍历中不能改字典，攒起来最后一起删
            lock (_repeatGate)
            {
                foreach (int vk in expired) _repeats.Remove(vk);
            }
        }
    }

    /// <summary>全部停掉（暂停 / 复位 / 禁用时用）。</summary>
    private void ClearContinuousRepeat()
    {
        lock (_repeatGate) _repeats.Clear();
    }
}
