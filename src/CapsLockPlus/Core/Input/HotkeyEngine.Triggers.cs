using CapsLockPlus.Core.Macro;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 触发键：VK 集合缓存、按下/抬起/长按/轻触判定与短按补发。
/// </summary>
public sealed partial class HotkeyEngine
{
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
    private List<TriggerMatch>? _triggerMatchCache;
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

    /// <summary>
    /// 一个触发键的匹配条件。
    ///
    /// 触发键**只允许单一按键**：一个物理键按下即进层。
    /// 组合键（Ctrl+K 之类）不能当触发键 —— 那会和"层内修饰键子层"
    /// 语义冲突：同样是 Ctrl+K，到底算触发还是算子层绑定？
    ///
    /// 组合键保留给**层内绑定**（进层后按住 Ctrl 再按 K），
    /// 两套机制职责清晰。
    /// </summary>
    private readonly record struct TriggerMatch(int Vk)
    {
        /// <summary>
        /// 用 <see cref="SameKey"/> 而不是直接比大小：
        /// 有些驱动发通用修饰码（0x12），有些发左右专用码（0xA5），
        /// 触发键配置里写的又是其中一种 —— 只比大小会"配置对了却按不出来"。
        /// </summary>
        public bool Matches(int vk) => SameKey(Vk, vk);
    }

    private List<TriggerMatch> TriggerMatchList(AppSettings s)
    {
        var list = s.Triggers;
        string stamp = TriggerStamp(list);
        if (_triggerMatchCache != null && _triggerVkCacheStamp == stamp)
            return _triggerMatchCache;

        var matches = new List<TriggerMatch>();
        foreach (var t in list)
        {
            if (!t.Enabled) continue;
            if (ParseTrigger(t.Key) is { } m) matches.Add(m);
        }

        _triggerMatchCache = matches;
        _triggerVkCacheStamp = stamp;
        return matches;
    }

    /// <summary>
    /// 把触发键字符串解析成匹配条件。
    ///
    /// **只接受单一按键**：如果配置里写了组合键（如 <c>caps_ctrl_k</c>），
    /// 这里会取出主键 K 并当作单键触发 —— 这样老配置不会彻底失效，
    /// 但也明确表达"组合触发已不再支持"。
    /// </summary>
    /// <summary>
    /// 诊断用：把外部配置里的键名解析结果描述出来（--dump-triggers）。
    /// 返回人类可读的结论，顺便回答"这个触发键会不会匹配 Tab(0x09)"。
    /// </summary>
    internal static string DescribeTriggerForDiag(string? key)
    {
        var m = ParseTrigger(key);
        if (m == null) return "解析失败（键名认不出来，永远不会触发）";
        return m.Value.Matches(0x09) ? "解析成功，且匹配 Tab(0x09)" : "解析成功";
    }

    private static TriggerMatch? ParseTrigger(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        // 组合键 ID 形式：取主键，忽略修饰部分
        var parsed = KeyName.ParseCombo(key);
        if (parsed != null)
        {
            var (_, k) = parsed.Value;
            if (KeyName.VkOf(k) is int vk && vk != 0) return new TriggerMatch(vk);
        }

        if (KeyName.VkOf(key) is int single && single != 0)
            return new TriggerMatch(single);

        return null;
    }

    /// <summary>
    /// 同步修饰键的按下/抬起状态。
    ///
    /// 必须对**左右两个键码和通用码**都认：有些键盘/驱动只发 0x11/0x10/0x12，
    /// 完全不发 LControl/RControl 这些具体码。
    /// </summary>
    private void UpdateModifierState(int vk, bool down)
    {
        switch (vk)
        {
            case VK.LControl or VK.RControl or 0x11:
                _ctrlDown = down;
                break;
            case VK.LShift or VK.RShift or 0x10:
                _shiftDown = down;
                break;
            case VK.LMenu or VK.RMenu or 0x12:
                _laltDown = down;
                break;
            case VK.LWin:
                _lwinDown = down;
                break;
            case VK.RWin:
                _rwinDown = down;
                break;
            default:
                return;      // 不是修饰键，什么都不做
        }

        /* 这里**不能**从 _swallowedUp 里删。
         *
         * 层内按下 Ctrl/Shift/Alt 时，OnKeyDownInLayer 会把它记进 _swallowedUp，
         * 抬起必须由 OnKeyUpInLayer 原子地取走并吞掉；本方法在按键处理链的最前面
         * 执行，先删掉的话 OnKeyUpInLayer 查不到 → 前台收到一个"从没按下过的抬起"，
         * 游戏 / 远程桌面会判定输入异常。 */
    }

    /// <summary>
    /// 把某个键对应的修饰键标记清零（不管它当前是什么状态）。
    ///
    /// 专供"触发键本身就是修饰键"的场景：右 Alt 按住期间的自动重复
    /// 不能把自己算成"按住的 Alt"，否则层内绑定全部查不到。
    /// </summary>
    private void ClearModifierFlag(int vk)
    {
        switch (vk)
        {
            case VK.LControl or VK.RControl or 0x11:
                _ctrlDown = false;
                break;
            case VK.LShift or VK.RShift or 0x10:
                _shiftDown = false;
                break;
            case VK.LMenu or VK.RMenu or 0x12:
                _laltDown = false;
                break;
            case VK.LWin:
                _lwinDown = false;
                break;
            case VK.RWin:
                _rwinDown = false;
                break;
        }
    }

    /// <summary>
    /// 两个键码是否指同一个物理键。
    ///
    /// 修饰键有三种写法：左右专用码（0xA4 / 0xA5）、通用码（0x12）。
    /// 不同键盘 / 驱动 / 远程桌面会话发的并不一致 ——
    /// 触发键配的是 "ralt"（0xA5），某些环境却只发 0x12，
    /// 或者按下发专用码、抬发送通用码。
    ///
    /// 只做**同一修饰键内部**的等价：左 Alt 和右 Alt 仍然互不相等，
    /// 用户想分别绑两个键的需求不受影响。
    /// </summary>
    private static bool SameKey(int a, int b) => a == b || (a, b) switch
    {
        (0x12, 0xA4) or (0xA4, 0x12) => true,       // Alt   ↔ 左 Alt
        (0x12, 0xA5) or (0xA5, 0x12) => true,       // Alt   ↔ 右 Alt
        (0x11, 0xA2) or (0xA2, 0x11) => true,       // Ctrl  ↔ 左 Ctrl
        (0x11, 0xA3) or (0xA3, 0x11) => true,       // Ctrl  ↔ 右 Ctrl
        (0x10, 0xA0) or (0xA0, 0x10) => true,       // Shift ↔ 左 Shift
        (0x10, 0xA1) or (0xA1, 0x10) => true,       // Shift ↔ 右 Shift
        _ => false,
    };





    private bool IsTrigger(AppSettings s, int vk)
    {
        return TriggerMatchList(s).Any(m => m.Matches(vk));
    }

    /// <summary>找出这个 VK 对应的触发键配置（组合键也认）。</summary>
    private static TriggerKey? FindTrigger(AppSettings s, int vk)
        => s.Triggers.FirstOrDefault(t =>
            t.Enabled && ParseTrigger(t.Key) is { } m && m.Matches(vk));

    private void OnTriggerDown(AppSettings s, int vk, bool wasPhysicallyDown)
    {
        /* 自动重复：按住触发键时系统会反复发 down。
         *
         * 这里必须先把这个键的修饰键状态清掉 —— 触发键若是右 Alt / 右 Ctrl，
         * 重复的 down 会把 `_laltDown` 又置成 true（上面虽然拦了一道，
         * 但状态可能被其它路径写脏，这里再兜一次底），
         * 层内绑定就会被拼成 caps_lalt_xxx 而永远查不到。
         *
         * ⚠ 但"按住"这个判断本身可能是假的：上一次的抬起事件如果丢了，
         * `_triggerHeld` 会卡在 true，于是**这一次真实的按下被当成自动重复
         * 直接吞掉** —— 用户感受到的就是"触发键偶尔按一下没反应"。
         * 所以先拿物理状态核对：这个键现在其实没被按住 → 按全新按下处理。 */
        long nowTick = Environment.TickCount64;

        if (_triggerHeld)
        {
            /* 区分"系统自动重复"和"用户重新按下"。
             *
             * 主判据改为**物理按下状态**：钩子已经看到过这个键的 down、
             * 还没看到 up（wasPhysicallyDown == true），那它就是按住不放
             * 产生的重复 down，与键盘重复速率无关。
             *
             * 旧实现只看"两次 down 间隔 < 100ms"：Windows 的首次重复延迟
             * 默认 250~500ms 且可调，用户把重复速率调慢后，第二次起的 down
             * 全部落在 100ms 之外，被误判成"重新按下"→ 复位 → 重新进层，
             * 表现就是"层反复被踢掉、停不在目标层"。
             *
             * 兜底：极少数环境里 up 事件会丢，物理状态还残留；但 100ms 内
             * 不可能有人真的"松开再按下"，这种情况仍按重复处理。 */
            bool isAutoRepeat = wasPhysicallyDown
                                || (_lastTriggerDownTick != 0
                                    && nowTick - _lastTriggerDownTick < 100);

            if (isAutoRepeat)
            {
                _lastTriggerDownTick = nowTick;
                ClearModifierFlag(vk);
                return;      // 真的自动重复
            }

            CoreLog.Info($"触发键 0x{_activeTriggerVk:X2} 重新按下（上次会话没有正常结束），复位后重新开始");
            ResetTriggerState("触发键重新按下");
        }

        _lastTriggerDownTick = nowTick;

        /* ⚠ 先确定是哪个触发键，**再**清零修饰键状态。
         *
         * 组合键触发（Ctrl+Shift+K）依赖"当前按住哪些修饰键"来判断，
         * 一旦先清零就永远匹配不上 —— 组合触发键会彻底失效。 */
        var trigger = FindTrigger(s, vk);

        _triggerHeld = true;
        _triggerDownTime = DateTime.Now;
        RequestFastHoldTick();
        _usedCombo = false;
        _usedOtherKey = false;
        _tapHandled = false;

        /* 只清**自己**的修饰键标记，不能把五个全清。
         *
         * 早先这里是 `_laltDown = _lwinDown = ... = false` ——
         * 用户先按住 Shift 再按触发键时，Shift 明明还按着，
         * 却被这一行抹掉了，层内的 caps_shift_xxx 绑定全部查不到。
         * 而 Shift 的 down 早就放行给了系统，用户以为自己在按 Shift。 */
        ClearModifierFlag(vk);

        _swallowedDown.Clear();          // 上一次会话的"重复判定"作废
        /* _swallowedUp 不能整体清：它是"按下被吞、抬起还没来"的欠账。
         * 清掉之后，这些键的 up 会漏给前台（见 OnKeyEvent 第 3 步）。
         * 只在异常堆积（up 真的丢了）时兜底。 */
        if (_swallowedUp.Count > MaxPendingUps) _swallowedUp.Clear();

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
        /* 先清掉自己的修饰键标记：触发键是修饰键时（右 Alt / 右 Ctrl），
         * 抬起绝不能被当成"释放了一个层内修饰键"。 */
        ClearModifierFlag(vk);

        // 允许"按下 0xA5、抬起 0x12"这种混发（见 SameKey）
        if (!_triggerHeld || !SameKey(vk, _activeTriggerVk)) return;

        /* ---- 松手前补做最后一次长按判定 ----
         *
         * 长按判定有两路：钩子在每个按键事件上比对时间戳（主判据）、
         * 界面线程每 20ms 轮询（纯按住时的兜底）。但"按住之后一个键都不按"
         * 的时候只有计时器在跑，界面一忙它就晚到 —— 于是明明按满了阈值，
         * 松手这一刻 _layerActive 仍是 false，被当成短按补发一次原键
         * （用户看到"切完层松手，焦点又跳一下"）。
         *
         * 在清掉 _triggerHeld **之前**补判一次：按满阈值就一定算长按，
         * 该吞的键一个都不会漏出去；也不会出现"长按了却什么都没发生"。
         */
        int holdLimit = _activeTrigger?.HoldMs ?? 200;
        if (!_layerActive &&
            (DateTime.Now - _triggerDownTime).TotalMilliseconds >= holdLimit)
        {
            OnHoldThresholdReached();      // 置 _layerActive / _tapHandled
        }

        _triggerHeld = false;

        var trigger = _activeTrigger;

        /* 这一按算不算"干净短按"。
         *
         * 判据就是"有没有真的进层"：进过层（_layerActive）或已按长按处理过
         * （_tapHandled）就绝不补发；只有从头到尾没进层，才把原键补发出去。
         *
         * 于是"短按发 Tab、长按一个 Tab 都不发"是**同一条判定**推出来的结果，
         * 不需要再额外拿时长去猜 —— 时长只用来决定"进不进层"。 */
        bool cleanTap = !_usedCombo && !_usedOtherKey && !_layerActive;

        // 未进层的干净抬起 = 短按
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

                /* 短按行为的判定。
                 *
                 * 新增 "keepKey" = **保留按键原功能**（显式选项）。
                 * 以前只有 toggleCapsLock / 空 / doNothing 才走补发原键那条路，
                 * 用户把 F5、问号这类键设成触发键时，短按就不知道该干嘛了
                 * （默认值还是 toggleCapsLock，跟那些键完全不搭）。
                 * 现在「触发键与层」页可以直接选"按键原功能"，
                 * 语义明确：短按 = 当这个键没被改造过一样用。 */
                if (tapAction is "toggleCapsLock" or "" or "doNothing" or "keepKey")
                {
                    /* 补发原键：对 CapsLock 是翻转大写，对 F5 是打开刷新，
                     * 对问号是正常输入 "?" —— 系统状态是真实产生的。 */
                    int vkToSend = vk;
                    _queue.Post(() =>
                    {
                        /* 只挡"层已经生效"这一种情况 —— 那是唯一会让原键
                         * 从层里漏出去的时刻。
                         *
                         * 不能挡 _triggerHeld：用户快速连按两下时，第二次按下
                         * 会把第一次的补发一起吞掉，表现为"按了没反应"。
                         * 补发属于**已经结束的那一次**按键，与新的按住无关。 */
                        if (_layerActive) return;
                        TapReplayCountForTest++;
                        InputSender.Tap(vkToSend);
                    });
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
        /* 只清"重复判定"表。欠着的抬起继续留在 _swallowedUp 里，
         * 等它自己的 up 到达时由 OnKeyEvent 第 3 步吞掉 ——
         * 否则"先松触发键、后松层内键"就会漏一个孤儿 keyup 给前台。 */
        _swallowedDown.Clear();
        if (_swallowedUp.Count > MaxPendingUps) _swallowedUp.Clear();
        /* 子层状态跟着触发键一起退出：否则松开触发键后 ActiveSubLayer
         * 还停在 "tab"，界面会显示一个不存在的子层。 */
        if (!string.IsNullOrEmpty(_subLayerKeyDown))
        {
            _subLayerKeyDown = "";
            SubLayerChanged?.Invoke("");
        }
        ClearContinuousRepeat();      // 层都没了，连续调节当然要停
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
                LogAction(action, "");
            _invoke(action, "");
        }
        catch (Exception ex) { CoreLog.Error("短按动作失败", ex); }
    }

    /// <summary>层模式下按下某个键。返回 true 表示吞掉。</summary>
    /// <summary>
    /// 叠加层：前台命中"按应用启用"的规则时，先查这一层有没有绑定。
    ///
    /// 与触发键的层的**根本区别**：这里不接管未绑定的键。
    /// 触发层的语义是"键盘交给这一层"，而叠加层是"在原有键盘上多几个快捷键"——
    /// 否则在 IDE 里打字会被整片吞掉。
    /// </summary>
    private bool TryRunAppLayerBinding(AppSettings s, int vk)
    {
        int layer = _appLayerId;
        if (layer <= 0) return false;

        // 系统自动重复：已经执行过，吞掉但不再触发
        if (_swallowedDown.ContainsKey(vk)) return true;

        string? keyName = KeyName.NameOf(vk);
        if (keyName == null) return false;

        var mods = KeyModifiers.None;
        if (_laltDown) mods |= KeyModifiers.Alt;
        if (_lwinDown || _rwinDown) mods |= KeyModifiers.Win;
        if (_ctrlDown) mods |= KeyModifiers.Ctrl;
        if (_shiftDown) mods |= KeyModifiers.Shift;

        var binding = FindInLayer(s, layer, KeyName.BuildComboEx(mods, keyName));
        if (binding == null || !binding.IsBound) return false;   // 没绑 → 直通

        string action = binding.Action;
        string arg = binding.Arg;

        _swallowedDown.TryAdd(vk, 0);
        _swallowedUp.TryAdd(vk, 0);

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



    /// <summary>
    /// 文本片段：把这次按键折算成字符、累计到缓冲区，命中就展开。
    ///
    /// **保守实现**的两条底线：
    ///   · 认不出的键（方向键、功能键、非 US 布局字符）一律**清零**缓冲区，
    ///     宁可丢掉一次展开，也不要跨键拼出假触发词；
    ///   · 只在"不按任何修饰键"的纯文本输入时累计。
    /// </summary>
    /// <returns>true = 这次按键已被片段展开消费，调用方应吞掉它。</returns>
    private bool ObserveSnippet(AppSettings s, int vk)
    {
        var all = s.Global.Snippets;
        if (all == null || all.Count == 0)
        {
            _snippetBuffer.Clear();
            return false;
        }

        /* 只认一个前缀符号（用户可改、可清空、可切全角）。
         * 过滤放在匹配之前：既实现"只认一个符号"，又不影响匹配器本身的语义。 */
        var opt = s.Hotstring ?? new HotstringOptions();
        string prefix = opt.EffectivePrefix;
        var list = string.IsNullOrEmpty(prefix)
            ? all
            : all.Where(x => x.Trigger != null && x.Trigger.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        if (list.Count == 0)
        {
            _snippetBuffer.Clear();
            return false;
        }

        /* 空格触发：打完触发词**再按空格**才替换（默认）。 */
        if (opt.ExpandOnSpace && vk == 0x20)
        {
            string typed = _snippetBuffer.ToString();
            var ready = SnippetMatcher.Match(list, typed, ForegroundProcess);
            if (ready == null)
            {
                _snippetBuffer.Clear();
                return false;               // 没命中 → 空格照常输入
            }

            ExpandSnippet(ready);
            return true;                    // 通知调用方把这次空格吞掉
        }

        char? ch = AsciiCharOf(vk, _shiftDown);
        if (ch == null)
        {
            _snippetBuffer.Clear();      // 非文本键：断开
            return false;
        }

        _snippetBuffer.Append(ch.Value);
        if (_snippetBuffer.Length > SnippetMatcher.MaxBuffer)
        {
            string trimmed = SnippetMatcher.Trim(_snippetBuffer.ToString());
            _snippetBuffer.Clear();
            _snippetBuffer.Append(trimmed);
        }

        var hit = SnippetMatcher.Match(list, _snippetBuffer.ToString(), ForegroundProcess);
        if (hit == null) return false;

        /* 有更长的候选（例如配了 ;m 和 ;mail，现在命中 ;m）就先别展开：
         * 等 120ms，用户接着打 'a' 就说明他要的是 ;mail。
         * 没有更长的候选时立刻展开 —— 常规场景保持"打完就变"的手感。 */
        /* 空格触发模式：命中先攒着，等空格；不在这里展开。 */
        if (opt.ExpandOnSpace) return false;

        if (SnippetMatcher.HasLongerCandidate(list, _snippetBuffer.ToString(), ForegroundProcess))
        {
            _snippetPending = hit;
            _snippetPendingAt = DateTime.Now;
            _snippetPendingBuffer = _snippetBuffer.ToString();
            return false;
        }

        ExpandSnippet(hit);
        return false;
    }

}
