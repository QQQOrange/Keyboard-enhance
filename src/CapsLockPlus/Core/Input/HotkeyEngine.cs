using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// CapsLock 组合键引擎 —— 整个产品的核心。
///
/// 行为与上游 CapsLock+ 对齐：
///   * 按住 CapsLock 期间，CapsLock 本身不再切换大小写，而是作为“功能键前缀”；
///   * Caps + X / Caps + LAlt + X / Caps + Win + 数字 触发绑定的功能；
///   * 短按（按下即松开、期间没按别的键）执行 press_caps 动作（默认切换大小写）；
///   * 组合键触发后，该按键不会再传给前台程序（不会打出字母）。
///
/// 相比上游的改进：
///   * 钩子回调里不做任何耗时操作，功能全部排队到工作线程执行，不会被系统摘钩；
///   * 组合键支持长按自动重复（移动 / 选择光标时手感更顺）；
///   * 支持双击 CapsLock 打开 Qbar；
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

    private bool _capsDown;
    private bool _capsUsedCombo;
    private bool _capsUsedOtherKey;
    private bool _laltDown;
    private bool _lwinDown;
    private bool _rwinDown;
    private DateTime _capsDownTime;
    private DateTime _lastCapsTap = DateTime.MinValue;
    private bool _hasLastTap;
    private int _lastRepeatVk = -1;
    private DateTime _lastRepeatTime = DateTime.MinValue;

    public bool Enabled { get; set; } = true;

    /// <summary>按键录制界面打开时置为 true，此时引擎完全放行。</summary>
    public bool Suspended { get; set; }

    /// <summary>是否处于“Caps 组合键按住中”。</summary>
    public bool IsCapsHeld => _capsDown;

    /// <summary>需要通知界面的提示（动作名称）。</summary>
    public event Action<string>? ActionTriggered;

    /// <summary>状态变化（按住/松开 Caps）。</summary>
    public event Action<bool>? CapsHeldChanged;

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
        CoreLog.Info("键盘钩子已启动");
    }

    public bool IsHookAlive => _hook.IsRunning;

    /// <summary>重装钩子（自愈用）。</summary>
    public void RestartHook() => _hook.Restart();

    /// <summary>直接喂一个按键给状态机（不经过真实钩子），用于自检。</summary>
    public bool SimulateKey(int vk, bool down) => OnKeyEvent(vk, down, injected: false);

    private bool IsCapsLockOn => (GetKeyState(VK.CapsLock) & 1) != 0;

    /// <summary>
    /// 钩子回调。返回值 = 是否吞掉该按键。
    /// 这里只做状态判断与入队，绝不做耗时操作（否则系统会摘钩）。
    /// </summary>
    private bool OnKeyEvent(int vk, bool down, bool injected)
    {
        if (injected) return false;                 // 自己注入的按键直接放行
        if (!Enabled || Suspended) return false;    // 暂停状态

        if (vk == VK.CapsLock)
        {
            if (down) OnCapsDown();
            else OnCapsUp();
            // Caps 键本身永远不交给前台：否则按住 Caps 时系统会打开大小写锁定，
            // 而且短按行为由 press_caps 决定（默认也是切换大小写）。
            return true;
        }

        if (!_capsDown) return false;
        return down ? OnKeyDownWhileCaps(vk) : OnKeyUpWhileCaps(vk);
    }

    private void OnCapsDown()
    {
        if (_capsDown) return;                // 自动重复
        _capsDown = true;
        _capsDownTime = DateTime.Now;
        _capsUsedCombo = false;
        _capsUsedOtherKey = false;
        _laltDown = _lwinDown = _rwinDown = false;
        _swallowedDown.Clear();
        _swallowedUp.Clear();
        CapsHeldChanged?.Invoke(true);
    }

    private void OnCapsUp()
    {
        if (!_capsDown) return;
        _capsDown = false;

        // 抬起 Caps 时清空“待吞掉的抬起”，避免下一次按住 Caps 时误吞正常按键
        _swallowedUp.Clear();
        _swallowedDown.Clear();

        bool shouldTap = !_capsUsedCombo && !_capsUsedOtherKey;
        var settings = _settings();

        // 双击判定：必须“两次都是干净的短按”，且间隔在阈值内。
        // 上一次短按的时间记录在 _lastCapsTap 里。
        bool doubleTap = settings.Global.DoubleTapCaps &&
                         shouldTap &&
                         _hasLastTap &&
                         (DateTime.Now - _lastCapsTap).TotalMilliseconds < DoubleTapWindowMs &&
                         (DateTime.Now - _capsDownTime).TotalMilliseconds < DoubleTapWindowMs;

        if (shouldTap)
        {
            if (doubleTap)
            {
                _hasLastTap = false;   // 双击只算一次，避免三连击继续触发
                string action = settings.Global.DoubleTapCapsAction;
                _queue.Post(() => RunTapAction(action));
            }
            else
            {
                _lastCapsTap = DateTime.Now;
                _hasLastTap = true;
                string action = settings.Global.TapCapsAction;
                _queue.Post(() => RunTapAction(action));
            }
        }
        CapsHeldChanged?.Invoke(false);
    }

    private const int DoubleTapWindowMs = 420;

    /// <summary>
    /// 执行短按动作。统一走动作表，这样“切换大小写 / Esc / 打开 Qbar”等
    /// 都能在按键映射界面里改，也能被自检捕获。
    /// </summary>
    private void RunTapAction(string action)
    {
        try
        {
            if (!string.IsNullOrEmpty(action) && action != "doNothing")
                _invoke(action, "");
        }
        catch (Exception ex) { CoreLog.Error("短按 Caps 动作失败", ex); }
    }

    /// <summary>按住 Caps 期间按下某个键。返回 true 表示吞掉（不传给前台程序）。</summary>
    private bool OnKeyDownWhileCaps(int vk)
    {
        // 修饰键：按住不生效，只记录状态，同时吞掉（否则会触发 Alt / Win 的系统行为）
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

        string? keyName = KeyName.NameOf(vk);
        if (keyName == null) return false;   // 不认识的功能键，放行

        var mods = KeyModifiers.None;
        if (_laltDown) mods |= KeyModifiers.Alt;
        if (_lwinDown || _rwinDown) mods |= KeyModifiers.Win;

        var settings = _settings();
        var comboId = KeyName.BuildCombo(mods, keyName);
        var binding = settings.FindBinding(comboId);

        if (binding == null || !binding.IsBound)
        {
            // 没有绑定：不吞掉，让按键正常输入；同时标记“这一按不是单纯点 Caps”
            if (mods == KeyModifiers.None) _capsUsedOtherKey = true;
            return false;
        }

        var action = binding.Action;
        var arg = binding.Arg;

        // 自动重复：只有“方向 / 删除 / 滚动”这类动作才适合连续触发。
        // 若不加这个限制，按住 Caps+F2 会不停弹出计算器、按住 Caps+Q 会反复打开 Qbar。
        bool canRepeat = settings.Global.RepeatWhenHeld && IsRepeatable(action);
        bool isRepeat = _swallowedDown.Contains(vk);
        if (isRepeat)
        {
            if (!canRepeat) return true;   // 已触发过，继续吞掉但不重复执行
            // 限制自动重复频率，避免手一抖光标飞出去
            if (vk == _lastRepeatVk && (DateTime.Now - _lastRepeatTime).TotalMilliseconds < 28) return true;
            _lastRepeatTime = DateTime.Now;
        }

        _lastRepeatVk = vk;
        _swallowedDown.Add(vk);
        _swallowedUp.Add(vk);
        _capsUsedCombo = true;

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

    /// <summary>按住 Caps 期间松开某个键。返回 true 表示吞掉。</summary>
    private bool OnKeyUpWhileCaps(int vk)
    {
        bool swallow = _swallowedUp.Remove(vk);
        _swallowedDown.Remove(vk);
        if (vk == VK.LMenu) _laltDown = false;
        if (vk == VK.LWin) _lwinDown = false;
        if (vk == VK.RWin) _rwinDown = false;
        return swallow;
    }

    private void OnWheel(int direction, bool shift, bool ctrl)
    {
        if (!_capsDown || !Enabled || Suspended) return;
        if (!_laltDown) return;
        var settings = _settings();
        var combo = direction > 0 ? "caps_lalt_wheelUp" : "caps_lalt_wheelDown";
        var binding = settings.FindBinding(combo);
        if (binding == null || !binding.IsBound) return;
        _capsUsedCombo = true;
        var action = binding.Action;
        var arg = binding.Arg;
        _queue.Post(() => _invoke(action, arg));
    }

    /// <summary>
    /// 该动作是否适合长按自动重复。
    /// 只有“连续移动 / 连续删除 / 连续滚动”才返回 true；
    /// 打开窗口、切换状态这类动作重复执行没有意义，甚至会造成困扰。
    /// </summary>
    public static bool IsRepeatable(string action) => action switch
    {
        // 光标移动
        "moveLeft" or "moveRight" or "moveUp" or "moveDown" => true,
        "moveWordLeft" or "moveWordRight" => true,
        "pageUp" or "pageDown" or "pageMoveLineUp" or "pageMoveLineDown" => true,
        // 选择
        "selectLeft" or "selectRight" or "selectUp" or "selectDown" => true,
        "selectWordLeft" or "selectWordRight" => true,
        // 删除
        "backspace" or "delete" or "deleteWord" or "forwardDeleteWord" => true,
        // 鼠标移动与滚动
        "mouseLeft" or "mouseRight" or "mouseUp" or "mouseDown" => true,
        "wheelUp" or "wheelDown" => true,
        _ => false,
    };

    public static void ToggleCapsLock() => InputSender.Tap(VK.CapsLock);

    public static bool CapsLockOn => (GetKeyState(VK.CapsLock) & 1) != 0;

    public void Dispose() => _hook.Dispose();
}
