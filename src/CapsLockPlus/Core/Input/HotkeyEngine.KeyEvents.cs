using CapsLockPlus.Core.Macro;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 钩子回调与按键分发：捕获模式、注入忽略、异常兜底，再转给触发键/层处理。拆分自 HotkeyEngine.cs（同属一个 partial 类，行为不变）。
/// </summary>
public sealed partial class HotkeyEngine
{
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
        /* 捕获模式：只上报、不吞键。 */
        if (CaptureMode)
        {
            if (!injected && KeySeen != null) { try { KeySeen(vk, down, ActiveLayer); } catch { } }
            return false;
        }

        if (injected && !AcceptInjectedForTest) return false;   // 自己注入的按键放行

        _lastKeyActivity = DateTime.Now;

        /* 先取"本次事件到来之前这个键是否已经处于按下状态"，再把本次事件
         * 写进物理状态表。顺序不能反 —— 先写后读的话，本次 down 永远
         * 显示"已按下"，就再也分不出首次按下和自动重复了。
         *
         * 钩子对**所有**按键（含被吞掉的触发键/层内键）都会回调到
         * OnKeyEvent，所以这张表是可靠的物理按下视图；
         * 也正因为如此，<see cref="OnTriggerDown"/> 可以不再依赖
         * "两次 down 间隔"这种会被键盘重复速率影响的猜测。 */
        bool wasPhysicallyDown = _physDown.ContainsKey(vk);
        if (down) _physDown[vk] = 0;
        else _physDown.TryRemove(vk, out _);

        /* 测试页专用：把真实按键抛出去。
         *
         * 放在最前面（暂停判断、触发键判断之前）——
         * 用户要排查"按了没反应"时，第一条要确认的就是
         * "钩子到底收到这个键没有"。放后面的话，
         * 暂停状态或提前 return 的按键就看不见了。
         *
         * 界面异常绝不能影响键盘钩子，所以吞掉所有异常。 */
        if (!injected && KeySeen != null)
        {
            try { KeySeen(vk, down, ActiveLayer); } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        }

        /* 外部拦截器（Qbar 的 Esc 等）：在物理状态记账、上报之后调用，
         * 这样界面的"按键测试"仍然看得到这个键。 */
        if (KeyInterceptor is { } interceptor)
        {
            try { if (interceptor(vk)) return true; }
            catch (Exception ex) { CoreLog.Swallow(ex, "按键拦截器"); }
        }

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

        /* ---- 0.2 钩子吞不住键：完全不介入 ----
         *
         * 这是"Tab 一直往后跳 + 松开后层卡住"的兜底：
         * 钩子一旦被系统摘掉，我们根本挡不住按键，就不该再假装能挡。
         * 把残留的触发键会话清掉，之后所有按键原样放行 —— 用户的键盘
         * 至少是"正常键盘"，而不是"随便按一下就触发层里功能"的坏键盘。 */
        if (SwallowUnavailable)
        {
            if (_triggerHeld || _layerActive) ResetTriggerState("钩子不可用");
            return false;
        }

        /* ---- 0.5 在判定触发键之前，先记录修饰键的按下状态 ----
         *
         * 组合触发键（Ctrl+Shift+K）需要知道"此刻按住了哪些修饰键"。
         * 之前 `_ctrlDown` 等字段只在**进层之后**（OnKeyDownInLayer）才维护，
         * 于是触发前按住 Ctrl 时状态仍是 false，组合触发键永远匹配不上。
         *
         * 这里在**每个按键事件**上同步修饰键状态，
         * 保证判定触发键时读到的是最新状态。
         * 抬起时也要清，否则松开 Ctrl 后状态会残留。
         *
         * ⚠ **触发键自己绝不能算"按住的修饰键"**。
         *
         * 把「右 Alt」设成触发键时，按住期间 Windows 会不断发来处理自动重复的
         * down 事件（长按 500ms 后开始，每 30~50ms 一次）。如果照样记进
         * `_laltDown`，层内每次按键都会被拼成 `caps_lalt_q` —— 而用户绑的是
         * `caps_q`，于是查不到绑定、按键原样放行。
         * 表现就是"右 Alt 当触发键，层里的绑定全都不生效，按键还是原功能"，
         * 而且**按住时间越长越必然出现**（自动重复一定已经开始了）。
         *
         * 同理，Ctrl / Shift / Win 做触发键时也会踩到这条。 */
        if (!(_triggerHeld && vk == _activeTriggerVk))
            UpdateModifierState(vk, down);

        /* ---- 0.9 阈值即时判定：不依赖界面线程的计时器 ----
         *
         * 长按判定原本只由界面线程的 DispatcherTimer 驱动，而它是
         * Background 优先级：界面一忙就顺延，200ms 的阈值实际可能
         * 300~400ms 才生效。这段"已按住、还没进层"的窗口是漏洞来源 ——
         * 子层键会漏给前台（Tab 连续跳焦点），松手还会被判成短按。
         *
         * 钩子每收到一个按键就先自己比一次时间戳：只要用户还在动键盘，
         * 切层就发生在下一个事件上，与界面线程调度无关。
         * 计时器仍然保留 —— 它负责"按住之后一个键都不按"的纯等待场景。 */
        if (down && _triggerHeld && !_layerActive)
        {
            int holdNow = _activeTrigger?.HoldMs ?? 200;
            if ((DateTime.Now - _triggerDownTime).TotalMilliseconds >= holdNow)
                OnHoldThresholdReached();
        }

        // ---- 1. 这个键是不是触发键？ ----
        /* ⚠ 片段缓冲区**不能**在这里无条件清空。
         *
         * 这一行原来是 `_snippetBuffer.Clear()`，写在"每个按键"上 ——
         * 而 ObserveSnippet 只追加当前这一个字符，于是缓冲区永远只有 1 个字，
         * `EndsWith(触发词)` 只可能匹配单字符触发词。
         * 用户配的 `;mail` / `;log` 这类长度大于 1 的片段**永远不展开**，
         * 而这个功能看起来"装好了"（配置界面、自检的纯函数都正常）。
         *
         * 现在只在"这个键被引擎吃掉"的地方清空 —— 被吞掉的键本来就不该
         * 参与拼接，放行的键才按顺序累加。 */
        if (IsTrigger(settings, vk))
        {
            _snippetBuffer.Clear();     // 被吞掉的键不参与片段拼接
            // 按下和抬起都吞掉：短按的原功能由 OnTriggerUp 里补发，
            // 长按则彻底不发出。这样不会污染系统的按键状态。
            if (down) OnTriggerDown(settings, vk, wasPhysicallyDown);
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
            if (down)
            {
                /* 子层键即使没开 HoldOnOtherKey 也要立刻进层。
                 *
                 * 否则在"触发键已按下、还没到长按阈值"这段窗口里，子层键会
                 * 走到下面第 3 步被放行 —— 按住就触发它的原生功能
                 * （最典型的就是 Tab 连续跳焦点），而层始终进不去。
                 * 用户关掉 HoldOnOtherKey 本意是"组合键要等一等"，
                 * 不是"子层键漏给前台"。 */
                string? pressedName = KeyName.NameOf(vk);
                bool isSubLayerKey = pressedName != null && IsSubLayerKey(settings, pressedName);

                if (_activeTrigger?.HoldOnOtherKey == true || isSubLayerKey)
                {
                    _layerActive = true;
                    _tapHandled = true;      // 已经不算"干净短按"了
                    CoreLog.Info($"触发键 {_activeTrigger?.Key} 期间按了" +
                                 $"{(isSubLayerKey ? "子层键" : "其它键")}，立即进入第 {_activeLayer} 层");
                }
            }
        }

        // ---- 3. 不在层模式：放行，但先把"欠着的抬起"结清 ----
        if (!_triggerHeld || !_layerActive)
        {
            /* 下面每个 `return true` 都是"这个键被引擎吃掉了"，
             * 都要顺手清掉片段缓冲区（被吞掉的键不参与拼接）。 */

            /* 组合键：命中就把这个键扣住等第二个键，超时再放行。 */
            /* 双击动作：只对配置过的键生效，单击完全保持原样。 */
            if (down && TryRunDoubleTapGesture(settings, vk, wasPhysicallyDown))
            {
                _snippetBuffer.Clear();
                return true;
            }
            if (!down && _gestureSwallowUp.Remove(vk)) { _snippetBuffer.Clear(); return true; }

            /* 按应用的叠加层：只接管**绑过**的组合，未绑定的键原样放行。 */
            if (down && TryRunAppLayerBinding(settings, vk))
            {
                _snippetBuffer.Clear();
                return true;
            }

            /* 基础改键：只要不在层里就生效。
             * 放在"放行"之前 —— 命中就吞掉原键并改成目标键。 */
            if (TryApplyRemap(settings, vk, down)) { _snippetBuffer.Clear(); return true; }

            /* 按下被我们吞过、抬起还没结账的键，这里必须补吞。
             *
             * 典型路径：用户先松开触发键、再松开层内键（Tab）。
             * OnTriggerUp 已经把层状态清掉，于是这个 up 落到这里 ——
             * 直接放行的话，前台会收到一个"从未按下过的抬起"。
             * 记事本这类程序无感，但游戏 / 远程桌面 / WebView2 会判定
             * 输入异常，个别实现甚至认为该键一直按着、反复自重复。
             *
             * ⚠ 这里的 `_swallowedDown` 也**必须**一起清：
             * 它是"这个键的按下被吞过"的账本，同时被「按应用叠加层」
             * 拿来当自动重复的判据（`ContainsKey` 就当作重复 → 直接吞掉）。
             * 只在抬起时清 `_swallowedUp`，账本会永远留着这个键 ——
             * 表现是：按应用叠加层里绑的快捷键**只生效一次**，
             * 第二次按下被静默吃掉、动作不执行，抬起还漏给前台。 */
            if (!down)
            {
                _swallowedDown.TryRemove(vk, out _);
                if (_swallowedUp.TryRemove(vk, out _)) { _snippetBuffer.Clear(); return true; }
                return false;
            }

            /* 文本片段：只有"真的会打出去"的键才计入缓冲区。
             * 带 Ctrl/Alt/Win 的是命令，不是输入，直接断开。 */
            if (_ctrlDown || _laltDown || _lwinDown || _rwinDown)
            {
                _snippetBuffer.Clear();
            }
            else if (ObserveSnippet(settings, vk))
            {
                /* 空格触发了片段展开：这个空格不能送到前台 ——
                 * 否则前台会多出一个空格，而 ExpandSnippet 按触发词长度回删，
                 * 会把空格连同触发词的最后一个字母一起删掉，留下残字。
                 * 记进 _swallowedUp，抬起也一并吞掉。 */
                _swallowedUp.TryAdd(vk, 0);
                return true;
            }

            return false;
        }

        // ---- 4. 层模式内的按键 ----
        _snippetBuffer.Clear();     // 层内按键一律被接管，不参与拼接
        return down ? OnKeyDownInLayer(vk, wasPhysicallyDown) : OnKeyUpInLayer(vk);
    }
}
