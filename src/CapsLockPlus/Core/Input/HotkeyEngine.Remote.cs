using System.Threading;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 钩子宿主回传的状态（partial 拆分之一）。
///
/// 钩子跑在独立宿主进程里，主程序只负责界面与动作执行。宿主通过命名管道回传三类事件：
///   · KEY   → <see cref="ApplyRemoteKey"/>
///   · TRIG  → <see cref="ApplyRemoteTrigger"/>
///   · LAYER → <see cref="ApplyRemoteLayer"/>
///
/// 这些成员只用于**显示与健康度统计**，吞键决策全在宿主那边。
/// 单独成文件，是因为它们和"本进程钩子"的状态机没有耦合，混在大型引擎文件里很难看清边界。
/// </summary>
public sealed partial class HotkeyEngine
{
    private volatile bool _remoteTriggerHeld;
    private int _remoteLayer = -1;
    private string? _remoteTriggerKey;

    /// <summary>
    /// 宿主回传的按键计数。
    ///
    /// ⚠ 宿主模式下**本进程没有钩子**，<see cref="KeyEventCount"/> 永远是 0。
    /// 健康检查如果拿它判断"钩子还活着没有"，就会永远判定"钩子死了" ——
    /// 进而发 SWALLOW 0 让宿主停止吞键：Tab 立刻恢复成原生 Tab、层也进不去。
    /// 这正是"更新后还是不行、甚至更糟"的原因。
    /// </summary>
    private long _remoteKeyCount;

    /// <summary>宿主回传：某个键被按下了（界面显示 + 健康度统计）。</summary>
    internal void ApplyRemoteKey(int vk, bool down)
    {
        Interlocked.Increment(ref _remoteKeyCount);
        try { KeySeen?.Invoke(vk, down, ActiveLayer); } catch { }
    }

    /// <summary>宿主回传：触发键按住/松开（只用于界面显示）。</summary>
    internal void ApplyRemoteTrigger(bool held, string? key, int layer)
    {
        _remoteTriggerHeld = held;
        _remoteTriggerKey = key;
        _remoteLayer = layer;
        try { TriggerHeldChanged?.Invoke(held); } catch { }
    }

    /// <summary>宿主回传：当前层变了（只用于界面显示）。</summary>
    internal void ApplyRemoteLayer(int layer)
    {
        if (_remoteLayer == layer) return;
        _remoteLayer = layer;
        /* 页面靠 TriggerHeldChanged 刷新整块状态（含"当前层"），这里借它通知一次。 */
        try { TriggerHeldChanged?.Invoke(_remoteTriggerHeld); } catch { }
    }
}
