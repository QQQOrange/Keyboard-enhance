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

    /// <summary>
    /// 双击触发键的判定窗口（毫秒）。
    ///
    /// 公开出来给界面文案引用 —— 之前界面上两处都写"600 毫秒内"，
    /// 而实现是 420ms，用户按 500ms 的间隔双击会失败，还以为双击坏了。
    /// </summary>
    public const int DoubleTapWindowMs = 420;

    private bool _enabled = true;
    private bool _suspended;

    /// <summary>
    /// 是否启用。切到 false（托盘"暂停"）时**必须复位触发键状态**：
    /// 按住触发键的期间被暂停的话，up 事件到不了状态机（OnKeyEvent 早退），
    /// <c>_triggerHeld</c> 会卡 true，恢复后计时器立刻把层激活，
    /// 整个键盘被层逻辑劫持 —— 只能再完整按一次触发键才能解锁。
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            ResetTriggerState("Enabled 切换");
        }
    }

    /// <summary>
    /// 挂起（录制宏时置 true，引擎完全放行、只记录不拦截）。
    /// 切换时同样要复位触发键状态，理由同 <see cref="Enabled"/>。
    /// </summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            ResetTriggerState("Suspended 切换");
        }
    }

    /// <summary>
    /// 复位触发键状态机（不补发任何按键）。
    ///
    /// 用于暂停 / 恢复 / 挂起切换、以及钩子自愈重装之后 ——
    /// 这些时刻我们可能错过了触发键的抬起事件，状态若不复位，
    /// 计时器会在下一次 tick 时把一个"幽灵按住"升级成层模式。
    /// </summary>
    private void ResetTriggerState(string reason)
    {
        if (!_triggerHeld && !_layerActive) return;
        CoreLog.Info($"引擎状态复位（{reason}）：丢弃未完成的触发键会话");
        _triggerHeld = false;
        _layerActive = false;
        _activeTriggerVk = -1;
        _activeTrigger = null;
        _activeLayer = -1;
        _swallowedDown.Clear();
        _swallowedUp.Clear();
        _laltDown = _lwinDown = _rwinDown = _ctrlDown = _shiftDown = false;
    }

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
        StartHoldTimer();
        CoreLog.Info("键盘钩子已启动");
    }

    /// <summary>
    /// 启动长按判定计时器。
    ///
    /// 钩子回调里不能做定时，所以阈值判定放在界面线程的计时器上：
    /// 每 20ms 检查一次"当前触发的键是否已按住超过阈值"，超过就切到层模式。
    ///
    /// 单独抽出来是为了让自检能**只启计时器、不装钩子**地验证真实长按路径
    /// （之前的自检用 EnterLayerImmediatelyForTest 直接跳过了计时器，
    ///  等于这条最关键的链路从来没被测过）。
    /// </summary>
    public void StartHoldTimer()
    {
        if (_holdTimer != null) return;
        _holdTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        _holdTimer.Tick += (_, _) => CheckHoldThreshold();
        _holdTimer.Start();
    }

    /// <summary>阈值检查（计时器每 20ms 调一次；自检也可直接调）。</summary>
    private void CheckHoldThreshold()
    {
        if (!_triggerHeld || _layerActive) return;
        var trigger = _activeTrigger;
        if (trigger == null) return;
        if ((DateTime.Now - _triggerDownTime).TotalMilliseconds >= trigger.HoldMs)
            OnHoldThresholdReached();
    }

    /// <summary>仅供自检：手动跑一次阈值检查，不依赖计时器。</summary>
    public void CheckHoldThresholdForTest() => CheckHoldThreshold();

    private System.Windows.Threading.DispatcherTimer? _holdTimer;

    public bool IsHookAlive => _hook.IsRunning;

    /// <summary>重装钩子（自愈用）。</summary>
    public void RestartHook() => _hook.Restart();

    /// <summary>诊断：让钩子把收到的按键写进日志。</summary>
    public void EnableKeyTrace() => _hook.TraceKeys = true;

    /// <summary>诊断：钩子累计收到的按键数。</summary>
    public long KeyEventCount => _hook.KeyEventCount;

    /// <summary>
    /// 挂起 / 恢复的真正入口之一：宏录制器被设置时自动挂起，
    /// 被清空（null）时自动恢复。录制期间层逻辑完全不拦截按键 ——
    /// 否则用户按 Caps+X 想录"打开 Qbar"，屏幕上却真弹出了 Qbar，
    /// 录到的原始键序与回放时的行为对不上。
    /// </summary>
    public MacroRecorder? MacroRecorder
    {
        get => _recorder;
        set
        {
            _recorder = value;
            // 挂起/恢复走属性 setter，复用状态复位逻辑
            Suspended = value != null;
        }
    }

    private MacroRecorder? _recorder;

    /// <summary>
    /// 宏播放器。引擎用它做两件事：播放中按 Esc 取消、以及判断"当前是否在播放宏"。
    /// 由 AppHost 在启动时接上。
    /// </summary>
    public MacroPlayer? MacroPlayer { get; set; }

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

    /// <summary>
    /// 钩子回调。返回值 = 是否吞掉该按键。
    /// 这里只做状态判断与入队，绝不做耗时操作（否则系统会摘钩）。
    ///
    /// 触发键的吞键策略（标准 tap-hold，与 QMK / kanata 一致）：
    ///   * 按下瞬间**吞掉**，不让原功能立刻发出（此时还不知是短按还是长按）；
    ///   * 若在阈值内松开 = **短按** → 用 SendInput 补发一次真实的按下+抬起，
    ///     原功能（切换大小写等）正常生效；
    ///   * 若超过阈值 = **长按** → 进入层模式，原功能永不发出。
    ///
    /// ⚠ 早先的实现是"按下放行、超阈值再补发 up"，看似更简单，但有致命问题：
    ///    按下时 CapsLock 已经被系统处理（大写已切换），而补发 up 又无法撤销
    ///    那次切换 —— 结果是"长按进层时大小写被莫名切了一下"，
    ///    而且系统认为 CapsLock 一直按着（因为真实 up 被吞了）。
    ///    现在的做法从根上避免了这两个问题。
    /// </summary>
    private bool OnKeyEvent(int vk, bool down, bool injected)
    {
        if (injected && !AcceptInjectedForTest) return false;   // 自己注入的按键放行

        // 录制中：照常放行（不影响用户正常操作），只记录事件
        MacroRecorder?.Record(vk, down);

        /* ---- 0. 宏播放中按 Esc = 取消 ----
         *
         * 必须放在 Enabled/Suspended 判断**之前**：无限循环的宏唯一的退出
         * 通道就是这个，引擎一旦被暂停，Esc 再也转发不到 Cancel，
         * 用户只能杀进程 —— 而进程被杀时播放线程不执行 finally，
         * 已按下的修饰键会粘住。
         *
         * 不吞掉 Esc，让它照常传给前台程序（用户可能确实想按 Esc）。 */
        if (down && vk == VK.Escape && MacroPlayer?.IsPlaying == true)
        {
            MacroPlayer.Cancel();
        }

        if (!Enabled || Suspended) return false;                // 暂停状态

        var settings = _settings();

        // ---- 1. 这个键是不是触发键？ ----
        if (IsTrigger(settings, vk))
        {
            // 按下和抬起都吞掉：短按的原功能由 OnTriggerUp 里补发，
            // 长按则彻底不发出。这样不会污染系统的按键状态。
            if (down) OnTriggerDown(settings, vk);
            else OnTriggerUp(settings, vk);
            return true;
        }

        // ---- 2. 触发键按住中，但还没到长按阈值 ----
        if (_triggerHeld && !_layerActive)
        {
            /* 按住触发键期间又按了别的键 → **立刻进层**，不等阈值。
             *
             * 为什么必须这样：如果非要等满 200ms 才进层，
             * 那用户每次想按组合键（Caps+Q、Caps+Ctrl+K）都得先"等一下"，
             * 手感非常别扭 —— 组合键会显得"时灵时不灵"。
             *
             * 判据也很自然：又按了别的键，说明用户明确是想用组合键，
             * 而不是单纯点一下触发键。纯点一下（没碰别的键）仍然走短按，
             * 保留切换大小写这类原功能。
             *
             * 这是 QMK / kanata 里 tap-hold 的标准做法（hold-on-other-key-press）。 */
            if (down && _activeTrigger?.HoldOnOtherKey == true)
            {
                _layerActive = true;
                _tapHandled = true;      // 已经不算"干净短按"了
                CoreLog.Info($"触发键 {_activeTrigger.Key} 期间按了其它键，立即进入第 {_activeLayer} 层");
            }
        }

        // ---- 3. 不在层模式：全部放行 ----
        if (!_triggerHeld || !_layerActive) return false;

        // ---- 4. 层模式内的按键 ----
        return down ? OnKeyDownInLayer(vk) : OnKeyUpInLayer(vk);
    }

    /// <summary>
    /// 触发键的虚拟键码集合（缓存）。
    ///
    /// <see cref="IsTrigger"/> 在**每一个按键**的钩子回调里都要跑一次。
    /// 原实现每次都遍历 Triggers 并对每个 Key 做一次
    /// `KeyName.VkOf()`（字符串 → 字典查找），也就是每次按键都做
    /// 1~N 次字符串比较 + 字典查找 —— 纯浪费，而钩子回调对延迟极其敏感
    /// （超过 LowLevelHooksTimeout 会被系统静默摘钩）。
    ///
    /// 失效判据用**内容指纹**而不是"列表实例 + 条数"：
    /// 用户在界面上改触发键的 Key 时列表实例和条数都不变，
    /// 只看那两项会导致"改了触发键不生效"。指纹里带上每个键名和启用状态。
    /// </summary>
    private HashSet<int>? _triggerVkCache;
    private string _triggerVkCacheStamp = "";

    private static string TriggerStamp(List<TriggerKey> list)
    {
        // 参数很少（通常 1~3 个），拼串开销远小于每次按键都查字典
        var sb = new System.Text.StringBuilder(32);
        foreach (var t in list)
        {
            sb.Append(t.Enabled ? '1' : '0').Append(t.Key).Append('|');
        }
        return sb.ToString();
    }

    private HashSet<int> TriggerVkSet(AppSettings s)
    {
        var list = s.Triggers;
        string stamp = TriggerStamp(list);
        if (_triggerVkCache != null && _triggerVkCacheStamp == stamp)
            return _triggerVkCache;

        var set = new HashSet<int>();
        foreach (var t in list)
        {
            if (!t.Enabled) continue;
            if (KeyName.VkOf(t.Key) is int vk) set.Add(vk);
        }

        _triggerVkCache = set;
        _triggerVkCacheStamp = stamp;
        return set;
    }

    private bool IsTrigger(AppSettings s, int vk) => TriggerVkSet(s).Contains(vk);

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

        // 层内触发过绑定 / 进过层 → 这一按不算短按
        bool cleanTap = !_usedCombo && !_usedOtherKey && !_layerActive;

        var trigger = _activeTrigger;

        // 未超时的干净抬起 = 短按
        if (cleanTap && !_tapHandled && trigger != null)
        {
            /* 短按：**补发一次真实的按下+抬起**，让原功能生效。
             *
             * 因为整个按住过程都被我们吞掉了（见 OnKeyEvent 的注释），
             * 系统完全没收到这个键，所以这里必须自己补一次。
             *
             * 特殊处理「短按动作」：若用户把短按行为改成了别的动作
             * （比如改成 Esc、打开 Qbar），那就执行那个动作，不补发原键；
             * 若短按行为是"切换大小写"（默认），补发真实 CapsLock 即可 ——
             * 效果完全一致，但系统的大写状态是真实翻转的，
             * 不像自己合成按键那样可能与实际状态脱节。
             */
            string tapAction = trigger.TapAction ?? "";

            /* 双击判定必须在"补发原键"之前。
             *
             * 早先的写法把双击判定放在补发分支里面，导致默认配置
             * （TapAction = toggleCapsLock，会走补发原键那条路）时
             * **永远进不到双击分支** —— 双击打开 Qbar 因此失效。 */
            bool doubleTap = !string.IsNullOrEmpty(trigger.DoubleTapAction)
                             && _hasLastTap
                             && (DateTime.Now - _lastTapTime).TotalMilliseconds < DoubleTapWindowMs
                             && (DateTime.Now - _triggerDownTime).TotalMilliseconds < DoubleTapWindowMs;

            if (doubleTap)
            {
                _hasLastTap = false;            // 双击只算一次，避免三连击继续触发
                string dt = trigger.DoubleTapAction;
                _queue.Post(() => RunTapAction(dt));
            }
            else
            {
                _lastTapTime = DateTime.Now;
                _hasLastTap = true;

                if (tapAction is "toggleCapsLock" or "" or "doNothing")
                {
                    // 补发原键：对 CapsLock 就是翻转大写，对别的键就是它的正常功能
                    int vkToSend = vk;
                    _queue.Post(() => InputSender.Tap(vkToSend));
                }
                else
                {
                    string ta = tapAction;
                    _queue.Post(() => RunTapAction(ta));
                }
            }
        }

        _layerActive = false;
        _activeTriggerVk = -1;
        _activeTrigger = null;
        _activeLayer = -1;
        _swallowedUp.Clear();
        _swallowedDown.Clear();
        TriggerHeldChanged?.Invoke(false);
    }

    /// <summary>
    /// 长按判定：由 UI 线程的计时器调用（钩子回调里不能做定时）。
    /// 超过阈值就进入层模式，并触发一次短按动作（因为用户没松手，
    /// 不会再有机会走"抬起时短按"分支）。
    /// </summary>
    private void OnHoldThresholdReached()
    {
        if (!_triggerHeld || _layerActive) return;
        _layerActive = true;
        _tapHandled = true;      // 已经算长按，抬起时不再补发原功能

        /* ⚠ 这里**不能**执行短按动作。
         *
         * 早先的实现除了切层，还顺手跑了一次 TapAction（切换大小写）——
         * 结果就是"每次长按进层，大写状态都被莫名翻一次"。
         * 长按的语义就是"这个键变成功能键前缀"，它本身什么都不该做。 */
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
        /* 修饰键：记录状态并吞掉（否则会触发系统级 Alt / Win 菜单）。
         *
         * ⚠ 左右与通用码都要认。早先只认 LControl/LShift/LMenu：
         *   · 按**右** Ctrl 组合会去查无修饰的 caps_k，绑了就错误执行；
         *   · 部分键盘/驱动只发 0x11/0x10/0x12 这三个通用码，
         *     完全没有分支处理，组合键永远拼不出来。 */
        if (vk is VK.LMenu or VK.RMenu or 0x12)
        {
            _laltDown = true;
            _swallowedUp.Add(vk);
            return true;
        }
        if (vk is VK.LWin or VK.RWin)
        {
            if (vk == VK.LWin) _lwinDown = true; else _rwinDown = true;
            _swallowedUp.Add(vk);
            return true;
        }
        if (vk is VK.LControl or VK.RControl or 0x11)
        {
            _ctrlDown = true;
            _swallowedUp.Add(vk);
            return true;
        }
        if (vk is VK.LShift or VK.RShift or 0x10)
        {
            _shiftDown = true;
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

        /* ⚠ 左右修饰键和通用码都要清。
         *
         * 早先只写 LControl/LShift/LMenu —— 用户按**右** Ctrl 组合后松手，
         * _ctrlDown 还是 true，之后按任何键都会被拼成 Ctrl 组合键
         * （caps_ctrl_q 之类），看起来就是"组合键时灵时不灵"。
         * 很多人习惯用右手小指按右 Ctrl，这条路径并不罕见。 */
        if (vk is VK.LMenu or VK.RMenu or 0x12) _laltDown = false;
        if (vk is VK.LWin or VK.RWin) { _lwinDown = false; _rwinDown = false; }
        if (vk is VK.LControl or VK.RControl or 0x11) _ctrlDown = false;
        if (vk is VK.LShift or VK.RShift or 0x10) _shiftDown = false;

        return swallow;
    }

    /// <summary>
    /// 层内绑定的查找缓存：层 ID → (组合键 ID → 绑定)。
    ///
    /// <see cref="FindInLayer"/> 在层模式下**每一次按键**都要跑。
    /// 原实现是 `Layers.FirstOrDefault(...)` 再 `Keys.FirstOrDefault(...)`，
    /// 即两次线性扫描（第 1 层通常有上百条绑定），而且用
    /// `StringComparison.OrdinalIgnoreCase` 做逐字符比较 ——
    /// 单次开销不大，但它在钩子回调路径上，累积起来会顶到
    /// LowLevelHooksTimeout（默认 300ms）的风险。
    ///
    /// 这里预先把每层的绑定建成**大小写不敏感的字典**，查找降为 O(1)。
    /// 用"层对象 + Keys 列表实例 + 条数"作为失效判据：
    /// 重新加载配置会换新实例，用户增删绑定会改变条数，两种情况都能自动重建。
    /// </summary>
    private readonly Dictionary<int, (Dictionary<string, KeyBinding> Map, List<KeyBinding> Src)>
        _layerMapCache = new();

    private KeyBinding? FindInLayer(AppSettings s, int layerId, string comboId)
    {
        var map = LayerBindingMap(s, layerId);
        return map != null && map.TryGetValue(comboId, out var hit) ? hit : null;
    }

    private Dictionary<string, KeyBinding>? LayerBindingMap(AppSettings s, int layerId)
    {
        var layer = s.Layers.FirstOrDefault(l => l.Id == layerId);
        List<KeyBinding>? keys = layer?.Keys;
        if (keys == null || keys.Count == 0)
            keys = layerId == 1 ? s.Keys : null;      // 老设置没有层结构时，Keys 即第 1 层
        if (keys == null) return null;

        if (_layerMapCache.TryGetValue(layerId, out var cached)
            && ReferenceEquals(cached.Src, keys)
            && cached.Map.Count == keys.Count)
            return cached.Map;

        var map = new Dictionary<string, KeyBinding>(keys.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
        {
            if (string.IsNullOrEmpty(k.Combo)) continue;
            map[k.Combo] = k;      // 重复时保留最后一条，与 SettingsStore.Normalize 的行为一致
        }

        _layerMapCache[layerId] = (map, keys);
        return map;
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
