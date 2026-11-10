using CapsLockPlus.Core.Macro;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 片段观察与展开、层内按键处理、层绑定表、滚轮与释放。
/// </summary>
public sealed partial class HotkeyEngine
{
    /// <summary>等长候选时的等待窗口。</summary>
    public const int SnippetWaitMs = 120;

    private Snippet? _snippetPending;
    private DateTime _snippetPendingAt;
    private string _snippetPendingBuffer = "";

    /// <summary>
    /// 定时器每 tick 调一次：等待窗口过了、而且缓冲没再增长（说明用户没继续打），
    /// 就把刚才挂起的那条片段展开。
    /// </summary>
    internal void FlushPendingSnippet()
    {
        var pending = _snippetPending;
        if (pending == null) return;
        if ((DateTime.Now - _snippetPendingAt).TotalMilliseconds < SnippetWaitMs) return;

        _snippetPending = null;

        /* 缓冲变了 = 用户在继续打（或换了键），交给后面新的匹配。
         * 这里在界面线程读、钩子线程写，理论上可能撞上 —— StringBuilder 不保证线程安全，
         * 所以整段兜住：读不出来就当"没命中"，绝不能把异常抛到计时器里。 */
        string now;
        try { now = _snippetBuffer.ToString(); }
        catch { return; }

        if (!string.Equals(now, _snippetPendingBuffer, StringComparison.Ordinal)) return;
        ExpandSnippet(pending);
    }

    /// <summary>真正执行展开（删触发词 + 打内容）。</summary>
    private void ExpandSnippet(Snippet hit)
    {
        SnippetMatchesForTest++;
        _snippetBuffer.Clear();
        _snippetPending = null;

        /* 使用统计：只在内存里加，落盘交给调用方（AppHost 的防抖保存）。 */
        try
        {
            hit.UseCount++;
            hit.LastUsed = DateTime.Now;
            SnippetUsed?.Invoke();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "记录别名使用次数"); }

        int deleteCount = hit.Trigger.Length;
        string text = hit.Text ?? "";

        /* 触发词已经打进前台了，所以要先删掉再输入。
         * 投递到动作队列：钩子里做 IO/发送会拖慢每一次按键。 */
        _queue.Post(() =>
        {
            try
            {
                /* 焦点不在可编辑控件上就放弃展开：
                 * 否则 Backspace 会去删别的东西、内容会打到别处。
                 * 判断放在队列里（STA），不能放钩子里。 */
                if (!Text.EditableTarget.AllowsTextInput()) return;

                for (int i = 0; i < deleteCount; i++)
                {
                    InputSender.Tap(0x08);          // Backspace
                    System.Threading.Thread.Sleep(6);
                }
                InputSender.Text(text);
                CoreLog.Info($"片段展开：{hit.Trigger} → {text.Length} 字");
            }
            catch (Exception ex) { CoreLog.Warn("片段展开失败：" + ex.Message); }
        });
    }

    /// <summary>
    /// 虚拟键 → 字符（US 布局）。认不出返回 null。
    ///
    /// 不走 ToUnicodeEx：那个 API 会改变死键状态，可能干扰输入法。
    /// 这里只认最基本的 ASCII，认不出就让缓冲区断开 —— 安全优先。
    /// </summary>
    private static char? AsciiCharOf(int vk, bool shift)
    {
        if (vk >= 0x41 && vk <= 0x5A) return (char)((shift ? 'A' : 'a') + (vk - 0x41));
        if (vk >= 0x30 && vk <= 0x39)
        {
            if (!shift) return (char)vk;
            const string shiftedDigits = ")!@#$%^&*(";
            return shiftedDigits[vk - 0x30];
        }

        return vk switch
        {
            0x20 => ' ',
            0xBA => shift ? ':' : ';',
            0xBB => shift ? '+' : '=',
            0xBC => shift ? '<' : ',',
            0xBD => shift ? '_' : '-',
            0xBE => shift ? '>' : '.',
            0xBF => shift ? '?' : '/',
            0xC0 => shift ? '~' : '`',
            0xDB => shift ? '{' : '[',
            0xDC => shift ? '|' : '\\',
            0xDD => shift ? '}' : ']',
            0xDE => shift ? '"' : '\'',
            _ => null,
        };
    }

    private bool OnKeyDownInLayer(int vk, bool wasPhysicallyDown)
    {
        /* 「欠着的抬起」只在**本次按下的第一次**记账。
         *
         * 系统自动重复的 down 不能重复记账：用户可能先按住 Down 不放，
         * 再按住触发键进层 —— 这时 Down 的原始 down 早就放给前台了，
         * 若在层内把它的每个重复 down 都记成"按下被吞过"，
         * 松手时那次 up 就会被补吞，前台只收到 down、收不到 up
         * （游戏 / 远程桌面会一直认为这个键按着）。
         * wasPhysicallyDown 正是"这不可能是第一次按下"的判据。 */
        void OweUp(int code)
        {
            if (!wasPhysicallyDown) _swallowedUp.TryAdd(code, 0);
        }

        /* 修饰键：记录状态并吞掉（否则会触发系统级 Alt / Win 菜单）。
         *
         * ⚠ 左右与通用码都要认。早先只认 LControl/LShift/LMenu：
         *   · 按**右** Ctrl 组合会去查无修饰的 caps_k，绑了就错误执行；
         *   · 部分键盘/驱动只发 0x11/0x10/0x12 这三个通用码，
         *     完全没有分支处理，组合键永远拼不出来。 */
        if (vk is VK.LMenu or VK.RMenu or 0x12)
        {
            _laltDown = true;
            OweUp(vk);
            return true;
        }
        if (vk is VK.LWin or VK.RWin)
        {
            if (vk == VK.LWin) _lwinDown = true; else _rwinDown = true;
            OweUp(vk);
            return true;
        }
        if (vk is VK.LControl or VK.RControl or 0x11)
        {
            _ctrlDown = true;
            OweUp(vk);
            return true;
        }
        if (vk is VK.LShift or VK.RShift or 0x10)
        {
            _shiftDown = true;
            OweUp(vk);
            return true;
        }

        /* ---- 安全阀：Esc 永远退出层 ----
         *
         * 层里"没绑定的键也吞掉"，一旦状态卡住，用户会觉得整个键盘失灵，
         * 只能退出程序。给一个一定能按到的出口：Esc。
         * 吞掉它（不发给前台），避免 Esc 在游戏/对话框里触发别的行为。 */
        if (vk == VK.Escape)
        {
            CoreLog.Info("层里按了 Esc：退出层");
            ResetTriggerState("按 Esc 退出层");
            return true;
        }

        string? keyName = KeyName.NameOf(vk);

        /* 不认识的键（键名表里没有）→ 同样吞掉。
         *
         * 用户的要求是"按住触发键时，按的任何键都是层的按键；层里没绑定
         * 就等于空操作"。以前这里放行，结果是按了个没收录的键会漏到前台，
         * 和"层完全接管键盘"的预期不符。 */
        if (keyName == null)
        {
            _usedOtherKey = true;
            _swallowedDown.TryAdd(vk, 0);
            OweUp(vk);
            return true;
        }

        /* 用物理状态校正一遍修饰键标记再拼 combo。
         * 漏掉一次修饰键抬起事件就会让整层的组合键永久错位
         * （全部拼成 caps_ctrl_xxx），这里是唯一的纠偏机会。 */
        SyncModifierFlagsWithPhysical();

        var settings = _settings();

        /* ---- 子层键：可以是任意物理键（Tab / CapsLock / F13…） ----
         *
         * 早期的"层内修饰键"只认 Ctrl/Shift/Alt/Win 四个修饰键；
         * 现在子层键允许任意键，所以要先检查这个键是不是当前层配置的子层键。
         *
         * 命中就**吞掉并记住**，接下来的按键会带上这个前缀去查绑定
         * （例如 Tab 子层 + Q → caps_tab_q）。 */
        /* 已按住的子层键的**系统自动重复**：直接吞掉。
         *
         * 不能让它落进下面的 combo 查找 —— 那会把重复 down 拼成
         * caps_tab_tab 这种不存在的组合键；万一用户真的绑了这个 ID，
         * 按住子层键就会反复触发它。 */
        if (!string.IsNullOrEmpty(_subLayerKeyDown) &&
            string.Equals(keyName, _subLayerKeyDown, StringComparison.OrdinalIgnoreCase))
        {
            _swallowedDown.TryAdd(vk, 0);
            OweUp(vk);
            return true;
        }

        if (string.IsNullOrEmpty(_subLayerKeyDown) && IsSubLayerKey(settings, keyName))
        {
            _subLayerKeyDown = keyName;
            _swallowedDown.TryAdd(vk, 0);   // 记下 down：抬起时成对清理、重复时能被识别
            OweUp(vk);
            SubLayerChanged?.Invoke(keyName);
            return true;
        }

        var mods = KeyModifiers.None;
        if (_laltDown) mods |= KeyModifiers.Alt;
        if (_lwinDown || _rwinDown) mods |= KeyModifiers.Win;
        if (_ctrlDown) mods |= KeyModifiers.Ctrl;
        if (_shiftDown) mods |= KeyModifiers.Shift;

        /* 有非修饰键子层按住时，只查"子层前缀 + 键"的绑定。
         *
         * **不再回退**到普通查法 —— 这一条和触发层保持同一套语义：
         * 按住子层键 = 键盘交给这个子层，子层里没绑就是空操作。
         * 以前会在子层查不到时退回 caps_q，于是"子层里没绑的键"
         * 会去执行基础层的功能，用户看到的就是"子层根本没生效"。
         *
         * ⚠ 修饰键子层（ctrl / ctrl_shift）本来就在 combo 里带上修饰前缀，
         * 走的是下面的 comboId，不受这条影响。 */
        string comboId = KeyName.BuildComboEx(mods, keyName);
        KeyBinding? binding = string.IsNullOrEmpty(_subLayerKeyDown)
            ? FindInLayer(settings, _activeLayer, comboId)
            : FindInLayer(settings, _activeLayer, ComboForSubLayer(_subLayerKeyDown, mods, keyName));

        if (binding == null || !binding.IsBound)
        {
            /* 层内没有绑定的键 → **吞掉**，恢复成"按了什么都不发生"。
             *
             * v4.5.4 按用户要求改了语义：按住触发键 = 键盘完全交给这一层。
             *   · 绑了功能 → 执行功能；
             *   · 没绑    → 空操作（既不执行功能，也不恢复原生输入）。
             *
             * 以前这里是 return false（放行），于是"按住触发键按个没绑的键"
             * 会打出原字符 —— 用户的原话是"只会恢复成按键原有的功能"，
             * 他认为这是错的：层的语义应该是接管，而不是"没绑就漏过去"。
             *
             * up 事件也要一起吞：只吞 down 会让前台程序收到"没有按下过
             * 的抬起"，个别程序（游戏、远程桌面）会因此判定输入异常。 */
            _usedOtherKey = true;
            _swallowedDown.TryAdd(vk, 0);
            OweUp(vk);
            return true;
        }

        string action = binding.Action;
        string arg = binding.Arg;

        bool canRepeat = settings.Global.RepeatWhenHeld && IsRepeatable(action);
        bool isRepeat = _swallowedDown.ContainsKey(vk);
        if (isRepeat)
        {
            /* 连续量动作由引擎自己的计时器驱动，系统的自动重复一律吞掉。
             * 两套节奏叠在一起会变成"按住后突然加速"，而且速率随
             * 用户的键盘设置变化，完全不可控。 */
            if (IsContinuous(action)) return true;
            if (!canRepeat) return true;
            if (vk == _lastRepeatVk && (DateTime.Now - _lastRepeatTime).TotalMilliseconds < 28) return true;
            _lastRepeatTime = DateTime.Now;
        }

        _lastRepeatVk = vk;
        _swallowedDown.TryAdd(vk, 0);
        OweUp(vk);
        _usedCombo = true;

        /* 同一个动作在 150ms 内重复请求 → 丢弃。
         *
         * 防御的是"同一时刻有两个钩子/两套引擎"这类事故：一个按键被两边各执行一次，
         * 用户看到的就是"按一下冒出来两个 QQ"。
         * 连续量动作（音量/亮度）和可重复动作（方向键）不参与去重，它们本来就是高频的。 */
        if (!IsContinuous(action) && !IsRepeatable(action) && IsDuplicateAction(action, arg))
        {
            CoreLog.Info($"动作去重：{action}（150ms 内重复请求，已忽略）");
            return true;
        }

        _queue.Post(() =>
        {
            try
            {
                _invoke(action, arg);
                ActionTriggered?.Invoke(action);
            }
            catch (Exception ex) { CoreLog.Error($"动作 {action} 执行失败", ex); }
        });

        /* 连续量动作：记下这一笔，之后由计时器按固定节奏一直执行，
         * 直到**这个键**松开。用户看到的就是"按住音量键，音量一条线地涨"。
         *
         * 每个键各存一份，而不是共用一个槽位：按住亮度+ 的时候再按音量+，
         * 两个键都应该继续各自生效（旧实现里后者会把前者顶掉，
         * 松开其中一个就把另一条也停了）。 */
        if (IsContinuous(action))
        {
            var state = new RepeatState
            {
                Action = action,
                /* 连续重复明确传 1：动作自己就知道"这一步要细调"。
                 *
                 * 以前靠"距上次调用的时间差"去猜是不是连发，结果调度一抖
                 * 就会猜错 —— 长按中途突然跳一大格，看起来一格一格地顿。 */
                Arg = "1",
                StartedAt = DateTime.Now,
                NextAt = DateTime.Now.AddMilliseconds(RepeatDelayMs),
            };
            lock (_repeatGate) _repeats[vk] = state;
        }

        return true;
    }

    /// <summary>层模式下松开某个键。返回 true 表示吞掉。</summary>
    private bool OnKeyUpInLayer(int vk)
    {
        bool swallow = _swallowedUp.TryRemove(vk, out _);
        _swallowedDown.TryRemove(vk, out _);

        // 松开就停：只停这个键，其它还按着的连续键照旧
        StopContinuousRepeat(vk);

        /* 非修饰键的子层键松开 → 清掉前缀状态。
         *
         * 不清的话，用户按了 Tab 子层再松开，之后按任何键都会被拼成
         * caps_tab_xxx，看起来就是"Tab 子层粘住了"。 */
        if (!string.IsNullOrEmpty(_subLayerKeyDown) &&
            string.Equals(KeyName.NameOf(vk), _subLayerKeyDown, StringComparison.OrdinalIgnoreCase))
        {
            _subLayerKeyDown = "";
            SubLayerChanged?.Invoke("");
        }

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
    /// 把"执行了什么动作"记进日志。
    ///
    /// 用户报"启动后莫名打出一个字母"时，日志里能直接看到是不是某个动作被触发；
    /// 干跑（自检/渲染）不记，免得刷屏。
    /// </summary>
    private static void LogAction(string action, string arg)
    {
        if (InputSender.DryRun) return;
        try
        {
            CoreLog.Info(arg.Length == 0 ? $"[动作] {action}" : $"[动作] {action} {arg}");
        }
        catch { }
    }

    private Dictionary<string, KeyBinding>? LayerBindingMap(AppSettings s, int layerId)
    {
        var layer = s.Layers.FirstOrDefault(l => l.Id == layerId);
        List<KeyBinding>? keys = layer?.Keys;
        if (keys == null || keys.Count == 0)
            keys = layerId == 1 ? s.Keys : null;      // 老设置没有层结构时，Keys 即第 1 层
        if (keys == null) return null;

        int version = s.Global?.BindingsVersion ?? 0;

        if (_layerMapCache.TryGetValue(layerId, out var cached)
            && ReferenceEquals(cached.Src, keys)
            && cached.Map.Count == keys.Count
            && cached.Version == version)
            return cached.Map;

        var map = new Dictionary<string, KeyBinding>(keys.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
        {
            if (string.IsNullOrEmpty(k.Combo)) continue;
            map[k.Combo] = k;      // 重复时保留最后一条，与 SettingsStore.Normalize 的行为一致
        }

        _layerMapCache[layerId] = (map, keys, version);
        return map;
    }

    private void OnWheel(int direction, bool shift, bool ctrl)
    {
        if (!_triggerHeld || !_layerActive || !Enabled || Suspended) return;

        // 滚轮事件不走 OnKeyEvent，修饰键状态同样要用物理状态校正
        SyncModifierFlagsWithPhysical();
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
        /* 先松改键目标键，再卸钩子 —— 顺序反了就没机会补发了（钩子没了，
         * 但系统里那个目标键还按着）。 */
        try { ReleaseRemappedKeys(); } catch (Exception ex) { CoreLog.Swallow(ex, "退出时松开改键目标键"); }

        if (_holdTimer != null)
        {
            _holdTimer.Stop();
            _holdTimer = null;
        }
        _hook.Dispose();
        _physDown.Clear();
    }
}
