using CapsLockPlus.Core.Macro;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 通用"触发键 + 键盘层"组合键引擎 —— 整个产品的核心。
///
/// 模型（参照键盘驱动）：
///   * 用户可配置多个**触发键**，每个触发键绑定一个**层**；
///   * 短按（按住未超过该键的 HoldMs）→ 执行原键盘功能 / 自定义短按动作；
///   * 长按超过 HoldMs → 进入"层模式"，此时触发键本身不再输出原功能；
///   * 层模式下按任意键 → 查该层的绑定表，命中则执行（并吞键），
///     未命中则放行，让按键正常输入；
///   * 一个层可以有 10 个（默认），层内还支持 LAlt / Win 修饰键，
///     组合键 ID 形如 <c>caps_q</c> / <c>caps_lalt_q</c> / <c>caps_win_1</c>。
///
/// 默认只有一个触发键 = CapsLock 绑第 1 层，行为与上一版完全一致。
///
/// 相比上游 AutoHotkey 版的改进：
///   * 钩子回调里不做耗时操作，全部排队到工作线程执行，不会被系统摘钩；
///   * 组合键支持长按自动重复（移动 / 选择光标时手感更顺）；
///   * 状态机可被界面实时暂停 / 恢复。
/// </summary>
public sealed class HotkeyEngine : IDisposable
{
    private readonly KeyboardHook _hook = new();
    private readonly ActionQueue _queue;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string, string, bool> _invoke;

    private readonly HashSet<int> _swallowedDown = new();
    private readonly HashSet<int> _swallowedUp = new();

    // 当前被按住的触发键（同一时刻只认定一个，避免冲突）
    private int _activeTriggerVk = -1;
    private TriggerKey? _activeTrigger;
    private int _activeLayer = -1;
    private bool _triggerHeld;
    private bool _layerActive;             // 已进入层模式（超过 HoldMs）
    private bool _usedCombo;               // 层内触发过绑定
    private bool _usedOtherKey;            // 层内按过未绑定的键
    private bool _tapHandled;              // 短按动作是否已触发（超时那一刻触发）

    private DateTime _triggerDownTime = DateTime.MinValue;
    private DateTime _lastTapTime = DateTime.MinValue;
    private bool _hasLastTap;

    private bool _laltDown;
    private bool _lwinDown;
    private bool _rwinDown;
    private bool _ctrlDown;
    private bool _shiftDown;

    private int _lastRepeatVk = -1;
    private DateTime _lastRepeatTime = DateTime.MinValue;

    /// <summary>长按判定的最短间隔：短于此视为抖动，不进入层模式。</summary>
    private const int DoubleTapWindowMs = 420;

    public bool Enabled { get; set; } = true;

    /// <summary>按键录制界面打开时置为 true，此时引擎完全放行。</summary>
    public bool Suspended { get; set; }

    /// <summary>是否正处于某个触发键的按住状态。</summary>
    public bool IsTriggerHeld => _triggerHeld;

    /// <summary>当前激活的层（未激活时为 -1）。</summary>
    public int ActiveLayer => _layerActive ? _activeLayer : -1;

    /// <summary>当前激活的触发键名（未激活时为 null）。</summary>
    public string? ActiveTriggerKey => _triggerHeld ? _activeTrigger?.Key : null;

    /// <summary>需要通知界面的提示（动作名称）。</summary>
    public event Action<string>? ActionTriggered;

    /// <summary>状态变化（按住/松开触发键）。</summary>
    public event Action<bool>? TriggerHeldChanged;

    /// <summary>兼容旧名称：按住/松开 CapsLock。</summary>
    public event Action<bool>? CapsHeldChanged
    {
        add => TriggerHeldChanged += value;
        remove => TriggerHeldChanged -= value;
    }

    /// <summary>CapsLock 大写锁定当前是否开启。</summary>
    public static bool CapsLockOn => (GetKeyState(VK.CapsLock) & 1) != 0;

    /// <summary>切换 CapsLock 大写锁定状态。</summary>
    public static void ToggleCapsLock() => InputSender.Tap(0x14);

    public HotkeyEngine(ActionQueue queue, Func<AppSettings> settings, Func<string, string, bool> invoke)
    {
        _queue = queue;
        _settings = settings;
        _invoke = invoke;
        _hook.KeyEvent += OnKeyEvent;
        _hook.WheelEvent += OnWheel;
    }

    public void Start()
    {
        _hook.Start();
        // 长按判定计时器：钩子回调里不能做定时，放到界面线程的计时器上跑。
        // 每 20ms 检查一次"当前触发的键是否已按住超过阈值"，
        // 一旦超过就切到层模式。
        _holdTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        _holdTimer.Tick += (_, _) =>
        {
            if (!_triggerHeld || _layerActive) return;
            var trigger = _activeTrigger;
            if (trigger == null) return;
            if ((DateTime.Now - _triggerDownTime).TotalMilliseconds >= trigger.HoldMs)
                OnHoldThresholdReached();
        };
        _holdTimer.Start();
        CoreLog.Info("键盘钩子已启动");
    }

    private System.Windows.Threading.DispatcherTimer? _holdTimer;

    public bool IsHookAlive => _hook.IsRunning;

    /// <summary>重装钩子（自愈用）。</summary>
    public void RestartHook() => _hook.Restart();

    /// <summary>诊断：让钩子把收到的按键写进日志。</summary>
    public void EnableKeyTrace() => _hook.TraceKeys = true;

    /// <summary>诊断：钩子累计收到的按键数。</summary>
    public long KeyEventCount => _hook.KeyEventCount;

    /// <summary>
    /// 宏录制器。非 null 时，所有按键**照常透传**（不影响用户操作），
    /// 同时喂给录制器记录时间戳 —— 这就是宏录制的工作原理。
    /// </summary>
    public MacroRecorder? MacroRecorder { get; set; }

    /// <summary>直接喂一个按键给状态机（不经过真实钩子），用于自检。</summary>
    public bool SimulateKey(int vk, bool down) => OnKeyEvent(vk, down, injected: false);

    /// <summary>
    /// 仅供自检 / 探针：把注入的按键也当作真实按键处理。
    ///
    /// 正常运行时必须保持 false —— 否则自己发出的合成按键会被自己再次拦截，
    /// 形成自我触发。测试无法产生真实硬件按键，只能靠合成，故测试时打开。
    /// </summary>
    public bool AcceptInjectedForTest { get; set; }

    /// <summary>
    /// 仅供自检：跳过长按阈值，触发键一按下就立即进入层模式。
    ///
    /// 真实运行靠 <see cref="_holdTimer"/> 在阈值到点时切层；
    /// 自检没有真实时间流逝，无法等 200ms，所以用这个开关直接进层。
    /// </summary>
    public bool EnterLayerImmediatelyForTest { get; set; }

    /// <summary>
    /// 仅供自检：模拟一次"短按"（不进入层模式）。
    ///
    /// <see cref="EnterLayerImmediatelyForTest"/> 会让每次按下都进层，
    /// 那样就没法测短按 / 双击路径了。自检在需要测短按时打开这个开关，
    /// 让本次按下按"未超时"处理。
    /// </summary>
    public bool SimulateShortPressForTest { get; set; }

    private bool IsCapsLockOn => (GetKeyState(VK.CapsLock) & 1) != 0;

    /// <summary>
    /// 钩子回调。返回值 = 是否吞掉该按键。
    /// 这里只做状态判断与入队，绝不做耗时操作（否则系统会摘钩）。
    ///
    /// 触发键的吞键策略（参考 kanata 的 tap-hold）：
    ///   * 按下瞬间**先放行**，让原功能（切换大小写、Shift 上档等）正常发出；
    ///   * 一旦长按超过阈值，立即用 SendInput **补发一次该键的 up**，
    ///     把刚触发的原功能"关回去"，然后才转入层模式；
    ///   * 这样短按完全无损，长按也不会残留副作用。
    ///
    /// 旧实现是"按下就吞"，短按时再自己合成一次 down/up ——
    /// 对 CapsLock 这种切换态按键会闪一下大写，手感明显差一截。
    /// </summary>
    private bool OnKeyEvent(int vk, bool down, bool injected)
    {
        if (injected && !AcceptInjectedForTest) return false;   // 自己注入的按键放行

        // 录制中：照常放行（不影响用户正常操作），只记录事件
        MacroRecorder?.Record(vk, down);

        if (!Enabled || Suspended) return false;                // 暂停状态

        var settings = _settings();

        // ---- 1. 这个键是不是触发键？ ----
        if (IsTrigger(settings, vk))
        {
            if (down)
            {
                OnTriggerDown(settings, vk);
                // 未进入层模式前一律放行（原功能照常生效）
                return _layerActive;
            }
            OnTriggerUp(settings, vk);
            // 进入过层模式才吞这次抬起；纯短按要放行，让原键正常抬起
            return _swallowLastUp;
        }

        // ---- 2. 不在层模式：全部放行 ----
        if (!_triggerHeld || !_layerActive) return false;

        // ---- 3. 层模式内的按键 ----
        return down ? OnKeyDownInLayer(vk) : OnKeyUpInLayer(vk);
    }

    private static bool IsTrigger(AppSettings s, int vk)
    {
        foreach (var t in s.Triggers)
        {
            if (!t.Enabled) continue;
            if (KeyName.VkOf(t.Key) is int v && v == vk) return true;
        }
        return false;
    }

    private void OnTriggerDown(AppSettings s, int vk)
    {
        if (_triggerHeld) return;              // 自动重复
        _triggerHeld = true;
        _triggerDownTime = DateTime.Now;
        _usedCombo = false;
        _usedOtherKey = false;
        _tapHandled = false;
        _laltDown = _lwinDown = _rwinDown = _ctrlDown = _shiftDown = false;
        _swallowedDown.Clear();
        _swallowedUp.Clear();

        var trigger = s.Triggers.FirstOrDefault(t => t.Enabled && KeyName.VkOf(t.Key) == vk);
        _activeTriggerVk = vk;
        _activeTrigger = trigger;
        _activeLayer = trigger?.LayerId ?? 1;

        // 自检模式：立刻进层，不等真实时间流逝
        if (EnterLayerImmediatelyForTest && !SimulateShortPressForTest)
            OnHoldThresholdReached();
        else
            _layerActive = false;

        TriggerHeldChanged?.Invoke(true);
    }

    private void OnTriggerUp(AppSettings s, int vk)
    {
        if (!_triggerHeld || vk != _activeTriggerVk) return;
        _triggerHeld = false;

        // 层内触发过绑定 → 这一按不算短按
        bool cleanTap = !_usedCombo && !_usedOtherKey && !_layerActive;

        // 未超时的抬起 = 短按
        if (cleanTap && !_tapHandled)
        {
            FireTap(singleTap: true);
        }

        _layerActive = false;
        _activeTriggerVk = -1;
        _activeTrigger = null;
        _activeLayer = -1;

        // 注意：进入过层模式就必须吞掉这次抬起。
        // 因为按下时我们故意放行了原键（短按需要），系统认为它还按着；
        // 进入层模式时补发的那次 up 是合成键、会被自己过滤掉，
        // 所以这里必须把"真实的这次抬起"吃掉，否则原键会再触发一次
        // （表现为 CapsLock 大小写被切两次、Shift 粘一下）。
        bool wasInLayer = !cleanTap;
        _swallowedUp.Clear();
        _swallowedDown.Clear();
        TriggerHeldChanged?.Invoke(false);

        // 返回值由调用方使用，这里通过字段传递
        _swallowLastUp = wasInLayer;
    }

    /// <summary>供调用方取"这一次触发键抬起是否要吞掉"。</summary>
    private bool _swallowLastUp;

    /// <summary>
    /// 长按判定：由 UI 线程的计时器调用（钩子回调里不能做定时）。
    /// 超过阈值就进入层模式，并触发一次短按动作（因为用户没松手，
    /// 不会再有机会走"抬起时短按"分支）。
    /// </summary>
    private void OnHoldThresholdReached()
    {
        if (!_triggerHeld || _layerActive) return;
        _layerActive = true;
        _tapHandled = true;                    // 短按已在进入层模式时触发

        var s = _settings();
        var trigger = _activeTrigger;
        if (trigger != null && !string.IsNullOrEmpty(trigger.TapAction))
        {
            _queue.Post(() => RunTapAction(trigger.TapAction));
        }
    }

    private void FireTap(bool singleTap)
    {
        var s = _settings();
        var trigger = _activeTrigger;
        if (trigger == null) return;

        // 双击判定
        bool doubleTap = singleTap
                         && !string.IsNullOrEmpty(trigger.DoubleTapAction)
                         && _hasLastTap
                         && (DateTime.Now - _lastTapTime).TotalMilliseconds < DoubleTapWindowMs;

        if (doubleTap)
        {
            _hasLastTap = false;
            _queue.Post(() => RunTapAction(trigger.DoubleTapAction));
            return;
        }

        _lastTapTime = DateTime.Now;
        _hasLastTap = true;
        _queue.Post(() => RunTapAction(trigger.TapAction));
    }

    /// <summary>执行短按动作。统一走动作表，便于在按键映射界面里改。</summary>
    private void RunTapAction(string action)
    {
        try
        {
            if (!string.IsNullOrEmpty(action) && action != "doNothing")
                _invoke(action, "");
        }
        catch (Exception ex) { CoreLog.Error("短按动作失败", ex); }
    }

    /// <summary>层模式下按下某个键。返回 true 表示吞掉。</summary>
    private bool OnKeyDownInLayer(int vk)
    {
        // 修饰键：记录状态并吞掉（否则会触发系统级 Alt / Win 菜单）
        if (vk is VK.LMenu or VK.RMenu)
        {
            if (vk == VK.LMenu) _laltDown = true;
            _swallowedUp.Add(vk);
            return true;
        }
        if (vk is VK.LWin or VK.RWin)
        {
            if (vk == VK.LWin) _lwinDown = true; else _rwinDown = true;
            _swallowedUp.Add(vk);
            return true;
        }
        if (vk is VK.LControl or VK.RControl)
        {
            if (vk == VK.LControl) _ctrlDown = true;
            _swallowedUp.Add(vk);
            return true;
        }
        if (vk is VK.LShift or VK.RShift)
        {
            if (vk == VK.LShift) _shiftDown = true;
            _swallowedUp.Add(vk);
            return true;
        }

        string? keyName = KeyName.NameOf(vk);
        if (keyName == null) return false;   // 不认识的功能键，放行

        var mods = KeyModifiers.None;
        if (_laltDown) mods |= KeyModifiers.Alt;
        if (_lwinDown || _rwinDown) mods |= KeyModifiers.Win;
        if (_ctrlDown) mods |= KeyModifiers.Ctrl;
        if (_shiftDown) mods |= KeyModifiers.Shift;

        var settings = _settings();
        string comboId = KeyName.BuildComboEx(mods, keyName);

        var binding = FindInLayer(settings, _activeLayer, comboId);
        if (binding == null || !binding.IsBound)
        {
            // 没有绑定：放行，让按键正常输入
            if (mods == KeyModifiers.None) _usedOtherKey = true;
            return false;
        }

        string action = binding.Action;
        string arg = binding.Arg;

        bool canRepeat = settings.Global.RepeatWhenHeld && IsRepeatable(action);
        bool isRepeat = _swallowedDown.Contains(vk);
        if (isRepeat)
        {
            if (!canRepeat) return true;
            if (vk == _lastRepeatVk && (DateTime.Now - _lastRepeatTime).TotalMilliseconds < 28) return true;
            _lastRepeatTime = DateTime.Now;
        }

        _lastRepeatVk = vk;
        _swallowedDown.Add(vk);
        _swallowedUp.Add(vk);
        _usedCombo = true;

        _queue.Post(() =>
        {
            try
            {
                _invoke(action, arg);
                ActionTriggered?.Invoke(action);
            }
            catch (Exception ex) { CoreLog.Error($"动作 {action} 执行失败", ex); }
        });

        return true;
    }

    /// <summary>层模式下松开某个键。返回 true 表示吞掉。</summary>
    private bool OnKeyUpInLayer(int vk)
    {
        bool swallow = _swallowedUp.Remove(vk);
        _swallowedDown.Remove(vk);
        if (vk == VK.LMenu) _laltDown = false;
        if (vk == VK.LWin) _lwinDown = false;
        if (vk == VK.RWin) _rwinDown = false;
        if (vk == VK.LControl) _ctrlDown = false;
        if (vk == VK.LShift) _shiftDown = false;
        return swallow;
    }

    private static KeyBinding? FindInLayer(AppSettings s, int layerId, string comboId)
    {
        List<KeyBinding>? keys = s.Layers.FirstOrDefault(l => l.Id == layerId)?.Keys;
        if (keys == null || keys.Count == 0)
        {
            // 老设置没有层结构时，Keys 即第 1 层
            keys = layerId == 1 ? s.Keys : null;
        }
        return keys?.FirstOrDefault(k => string.Equals(k.Combo, comboId, StringComparison.OrdinalIgnoreCase));
    }

    private void OnWheel(int direction, bool shift, bool ctrl)
    {
        if (!_triggerHeld || !_layerActive || !Enabled || Suspended) return;
        if (!_laltDown) return;
        var settings = _settings();
        string combo = direction > 0 ? "caps_lalt_wheelUp" : "caps_lalt_wheelDown";
        var binding = FindInLayer(settings, _activeLayer, combo);
        if (binding == null || !binding.IsBound) return;
        _usedCombo = true;
        _queue.Post(() => _invoke(binding.Action, binding.Arg));
    }

    /// <summary>
    /// 该动作是否适合长按自动重复。
    /// </summary>
    public static bool IsRepeatable(string action) => action switch
    {
        "moveLeft" or "moveRight" or "moveUp" or "moveDown" => true,
        "moveWordLeft" or "moveWordRight" => true,
        "pageUp" or "pageDown" or "pageMoveLineUp" or "pageMoveLineDown" => true,
        "selectLeft" or "selectRight" or "selectUp" or "selectDown" => true,
        "selectWordLeft" or "selectWordRight" => true,
        "backspace" or "delete" or "deleteWord" or "forwardDeleteWord" => true,
        "mouseLeft" or "mouseRight" or "mouseUp" or "mouseDown" => true,
        "wheelUp" or "wheelDown" => true,
        _ => false,
    };

    public void Dispose()
    {
        if (_holdTimer != null)
        {
            _holdTimer.Stop();
            _holdTimer = null;
        }
        _hook.Dispose();
    }
}
