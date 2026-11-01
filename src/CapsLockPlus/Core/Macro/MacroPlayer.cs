using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Localization;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Macro;

/// <summary>
/// 宏播放器。
///
/// 设计要点（参照键盘驱动的成熟方案）：
///   * **播放跑在专用后台线程**，绝不占用钩子回调 —— 钩子回调有 300ms 超时限制，
///     超时系统会静默移除钩子，整套功能就失效了；
///   * 支持固定延迟 / 随机延迟（每次播放在区间内取）；
///   * 文本输入逐字符发送（KEYEVENTF_UNICODE），不碰剪贴板、能过 IME；
///   * 按下过的键全部记录，**无论正常结束、被取消还是抛异常，都补齐抬起** ——
///     否则 Ctrl / Shift 会粘住不放，用户之后打字全是 Ctrl+字母；
///   * 播放中按 Esc 可取消（由引擎的键盘钩子转发 <see cref="Cancel"/>）。
/// </summary>
public sealed class MacroPlayer : IDisposable
{
    private Thread? _thread;
    private volatile bool _cancelRequested;

    /// <summary>播放结束后的回调（在播放线程上触发；UI 用需自行切线程）。</summary>
    private Action? _callback;

    /// <summary>当前是否正在播放。</summary>
    public bool IsPlaying => _thread is { IsAlive: true };

    /// <summary>当前播放到第几步（0 起；-1 = 未播放）。供界面高亮。</summary>
    public int CurrentStep { get; private set; } = -1;

    /// <summary>请求取消当前播放。</summary>
    public void Cancel()
    {
        if (!IsPlaying) return;
        _cancelRequested = true;
        CoreLog.Info("宏播放已请求取消");
    }

    /// <summary>
    /// 一个宏单次播放最多执行多少步。
    ///
    /// 存在的唯一理由是"无限循环 + 极短循环体"：没有上限时，用户配错一个循环
    /// 就会把 UI 线程之外的一切拖死，而且表现为"程序卡住"，极难归因。
    /// 20 万步对正常宏远远够用（一次播放通常几十步）。
    /// </summary>
    public const int StepBudget = 200_000;

    /// <summary>最近一次播放失败的原因（null = 没有失败）。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 播放中途失败（`waitFor` 超时且没有重试可兜）。
    ///
    /// 为什么要有这个事件：播放是异步的，`PlayMacro` 早就返回 true 了，
    /// 失败只能事后通知 —— 否则用户看到的是"按了一下，什么都没发生"。
    /// </summary>
    public event Action<string>? Failed;

    /// <summary>
    /// 动作执行器（由 AppHost 注入，转发给 <c>InvokeAction</c>）。
    ///
    /// 为什么用委托而不是直接引用动作目录：MacroPlayer 在 Core 层，
    /// 让它依赖 UI 层的 AppHost 会把整条链路绑死，自检也没法替换。
    /// </summary>
    public Func<string, string, bool>? ActionInvoker { get; set; }

    /// <summary>动作 id → 可读名称（失败提示用）。没注入就退回显示 id。</summary>
    public Func<string, string>? ActionTitle { get; set; }

    /// <summary>自检用：替换条件求值，避免依赖真实的前台窗口/剪贴板。</summary>
    internal static Func<Core.Settings.MacroStep, MacroContext, bool>? ConditionForTest { get; set; }

    /// <summary>
    /// 播放一个宏（兼容旧调用：忽略结果）。
    /// 需要知道"有没有播起来"时用 <see cref="TryPlay"/>。
    /// </summary>
    public void Play(Core.Settings.Macro macro, int? repeatOverride = null,
        Action? onCompleted = null)
        => TryPlay(macro, repeatOverride, onCompleted);

    /// <summary>
    /// 播放一个宏。返回 false 表示没播（空宏 / 已在播放 / 流程控制配对错误）。
    /// </summary>
    /// <param name="macro">要播放的宏。</param>
    /// <param name="repeatOverride">覆盖宏自身的重复次数（null = 用宏设置）。</param>
    /// <param name="onCompleted">播放结束（正常 / 取消 / 异常皆算）后的回调。</param>
    public bool TryPlay(Core.Settings.Macro macro, int? repeatOverride = null,
        Action? onCompleted = null)
    {
        LastError = null;

        if (macro == null || macro.Steps.Count == 0)
        {
            LastError = Loc.T("宏里还没有步骤");
            return false;
        }
        if (IsPlaying)
        {
            CoreLog.Info("宏播放请求被忽略：已有宏在播放");
            LastError = Loc.T("已有宏在播放");
            return false;
        }

        // 复制一份，避免播放时用户在界面上改步骤导致索引错乱
        var snapshot = macro.Clone();
        var flow = MacroFlow.Compile(snapshot.Steps);

        /* 配对错误就**不播**：半播出去的按键（已经按下 Ctrl 但没抬起）比不播更糟。
         * 把原因留在 LastError 上，由调用方决定怎么提示。 */
        if (!flow.Ok)
        {
            LastError = flow.Error ?? Loc.T("宏的流程控制配对有误");
            CoreLog.Error($"宏「{snapshot.Name}」无法播放：{LastError}");
            return false;
        }

        int repeat = repeatOverride ?? (snapshot.Repeat <= 0 ? int.MaxValue : snapshot.Repeat);

        _cancelRequested = false;
        _callback = onCompleted;
        CurrentStep = -1;

        _thread = new Thread(() =>
        {
            /* held 必须在 try **之外**声明：
             * 早先放在 try 里，播放中途抛异常时 catch 直接跳过收尾，
             * 已按下未抬起的修饰键就永远粘住了。 */
            var held = new HashSet<int>();
            bool abort = false;
            string? failReason = null;
            var retries = new Dictionary<int, int>();   // retry 下标 → 已尝试次数

            try
            {
                for (int round = 0; round < repeat; round++)
                {
                    CurrentStep = -1;

                    /* 程序计数器：没有流程控制标记的宏，走的还是"逐条往下"的老路径，
                     * 只是把 foreach 换成了 pc 自增 —— 行为完全一致。
                     * 有标记时靠 flow 的配对表跳转。 */
                    var loops = new Dictionary<int, int>();     // loop 下标 → 已执行轮数
                    int pc = 0;
                    int budget = StepBudget;

                    while (pc < snapshot.Steps.Count)
                    {
                        if (_cancelRequested) break;

                        if (--budget <= 0)
                        {
                            CoreLog.Warn($"宏「{snapshot.Name}」执行步数超过上限（{StepBudget}），已中止");
                            break;
                        }

                        CurrentStep = pc;
                        var step = snapshot.Steps[pc];

                        switch (step.Kind)
                        {
                            case "keyDown":
                            {
                                int vk = KeyName.VkOf(step.Key) ?? 0;
                                if (vk != 0)
                                {
                                    InputSender.KeyDown(vk);
                                    held.Add(vk);
                                }
                                break;
                            }
                            case "keyUp":
                            {
                                int vk = KeyName.VkOf(step.Key) ?? 0;
                                if (vk != 0)
                                {
                                    InputSender.KeyUp(vk);
                                    held.Remove(vk);
                                }
                                break;
                            }

                            case "text":
                                SendText(step.Text);
                                break;

                            case "delay":
                                Wait(step.DelayMs);
                                break;

                            case "delayRandom":
                            {
                                int lo = Math.Min(step.MinMs, step.MaxMs);
                                int hi = Math.Max(step.MinMs, step.MaxMs);
                                Wait(Random.Shared.Next(lo, hi + 1));
                                break;
                            }

                            /* ---- 流程控制 ----
                             * 每次求值都重新读一次外部状态（前台窗口/剪贴板），
                             * 而不是整段宏共用一份快照：循环里等待窗口变化是正当用法。 */
                            case "if":
                            {
                                bool hit = EvaluateCondition(step);
                                if (!hit)
                                {
                                    pc = flow.SkipTo(pc);
                                    continue;
                                }
                                break;
                            }

                            case "else":
                                // 走到这里说明 then 分支正常走完了 → 跳过 else 分支
                                pc = flow.AfterElse(pc);
                                continue;

                            case "endIf":
                                break;

                            /* 等待条件成立：轮询到成立或超时。
                             * 超时是宏里**唯一会失败**的一步 —— 重试块只兜它。 */
                            case "waitFor":
                            {
                                if (!WaitForCondition(step))
                                {
                                    if (_cancelRequested) { abort = true; break; }

                                    int target = ResolveRetry(flow, snapshot.Steps, retries, pc);
                                    if (target < 0)
                                    {
                                        abort = true;
                                        failReason = Loc.F("「{0}」等待超时（{1} ms）",
                                            MacroCondition.Describe(step.Cond, step.CondValue, step.Not),
                                            step.TimeoutMs);
                                        break;
                                    }

                                    pc = target;
                                    continue;
                                }
                                break;
                            }

                            /* 执行一个内置动作：把 124 个动作变成宏里的一步。
                             * 动作返回 false 也算失败，同样交给重试块 ——
                             * 否则"打开窗口没成功"这类情况在宏里就被静默吞掉了。 */
                            case "action":
                            {
                                if (!RunActionStep(step))
                                {
                                    if (_cancelRequested) { abort = true; break; }

                                    int target = ResolveRetry(flow, snapshot.Steps, retries, pc);
                                    if (target < 0)
                                    {
                                        abort = true;
                                        string name = ActionTitle?.Invoke(step.ActionId) ?? step.ActionId;
                                        failReason = Loc.F("动作「{0}」没有完成", name);
                                        break;
                                    }

                                    pc = target;
                                    continue;
                                }
                                break;
                            }

                            case "retry":
                                retries[pc] = 1;      // 第 1 次尝试
                                break;

                            case "endRetry":
                            {
                                // 正常走完：清掉计数，免得下次进这个块还带着旧账
                                int r = flow.RetryOf(pc);
                                if (r >= 0) retries.Remove(r);
                                break;
                            }

                            case "loop":
                                loops[pc] = 1;
                                break;

                            case "endLoop":
                            {
                                int loopAt = flow.LoopOf(pc);
                                if (loopAt >= 0)
                                {
                                    int total = snapshot.Steps[loopAt].Count;
                                    loops.TryGetValue(loopAt, out int done);

                                    if (flow.ShouldRepeat(loopAt, done, total))
                                    {
                                        loops[loopAt] = done + 1;
                                        pc = loopAt + 1;
                                        continue;
                                    }
                                    loops.Remove(loopAt);
                                }
                                break;
                            }
                        }

                        if (abort) break;

                        pc++;
                    }

                    if (_cancelRequested || abort) break;

                    // 循环之间留一点间隔，避免"无限循环"把 CPU 吃满
                    if (round + 1 < repeat) Wait(120);
                }
            }
            catch (Exception ex)
            {
                CoreLog.Warn("宏播放出错：" + ex.Message);
            }
            finally
            {
                // 收尾必须在 finally：正常 / 取消 / 异常三条路都要补齐抬起
                foreach (var vk in held)
                {
                    try { InputSender.KeyUp(vk); } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
                }
                held.Clear();

                CurrentStep = -1;

                if (abort && failReason != null)
                {
                    LastError = failReason;
                    CoreLog.Warn($"宏「{snapshot.Name}」中止：{failReason}");
                    try { Failed?.Invoke(failReason); }
                    catch (Exception ex) { CoreLog.Swallow(ex, "宏失败通知"); }
                }

                try { _callback?.Invoke(); }
                catch (Exception ex) { CoreLog.Swallow(ex, "宏播放结束回调"); }
            }
        })
        {
            IsBackground = true,
            Name = "macro-player",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return true;
    }

    /// <summary>
    /// 执行宏里的一个动作步骤。返回 false = 没完成（会被重试块接手）。
    ///
    /// 三种"不算失败"的情况要区分清楚，否则宏会因为无关原因中止：
    ///   · 没注入执行器（自检、或命令面板等不该跑动作的场合）→ 记日志跳过；
    ///   · 动作 id 为空（用户刚插入还没选）→ 记日志跳过；
    ///   · 动作抛异常 → **算失败**（这是真的执行出错，和返回 false 同类）。
    /// </summary>
    private bool RunActionStep(Core.Settings.MacroStep step)
    {
        var invoke = ActionInvoker;

        if (invoke == null || string.IsNullOrWhiteSpace(step.ActionId))
        {
            CoreLog.Warn("宏里的动作步骤被跳过：没有执行器或动作 id 为空");
            return true;
        }

        try
        {
            return invoke(step.ActionId, step.ActionArg ?? "");
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"宏执行动作 {step.ActionId} 出错：{ex.Message}");
            return false;
        }
    }

    /// <summary>条件求值。自检可替换（<see cref="ConditionForTest"/>），生产走真实状态。</summary>
    private static bool EvaluateCondition(Core.Settings.MacroStep step)
    {
        var ctx = MacroContext.Capture();
        var hook = ConditionForTest;
        return hook != null ? hook(step, ctx) : MacroCondition.Evaluate(step, ctx);
    }

    /// <summary>
    /// 轮询等待条件成立。返回 false = 超时或被取消。
    ///
    /// 间隔固定 100ms：比这更密没有意义（前台切换本身就有延迟），
    /// 比这更疏会让"等窗口出现"平均慢半拍才能继续。
    /// </summary>
    private bool WaitForCondition(Core.Settings.MacroStep step)
    {
        int timeout = Math.Clamp(step.TimeoutMs, 0, 600_000);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            if (_cancelRequested) return false;

            // 先判一次再判超时：timeout = 0 时也允许"此刻已经成立"直接通过
            if (EvaluateCondition(step)) return true;

            if (clock.ElapsedMilliseconds >= timeout) return false;

            Wait(100);
        }
    }

    /// <summary>
    /// 某一步失败时，找该由哪个重试块接手。返回重跑起点（块内第一条），-1 = 没人兜。
    ///
    /// 内层用尽后交给外层：这样"内层重试 3 次、外层再整体重试 2 次"能表达出来。
    /// 重入外层时必须**清掉内层的计数**，否则内层一进块就是"已用尽"状态。
    /// </summary>
    private static int ResolveRetry(MacroFlow flow, IReadOnlyList<Core.Settings.MacroStep> steps,
        Dictionary<int, int> used, int atIndex)
    {
        int r = flow.RetryOf(atIndex);
        while (r >= 0)
        {
            int total = steps[r].Count <= 0 ? 1 : steps[r].Count;
            used.TryGetValue(r, out int tried);

            if (tried < total)
            {
                used[r] = tried + 1;

                foreach (int inner in used.Keys.Where(k => k > r).ToList())
                    used.Remove(inner);

                CoreLog.Info($"宏重试：第 {r + 1} 步的重试块第 {tried} 次失败，开始第 {tried + 1} 次尝试");
                return r + 1;
            }

            r = flow.ParentRetry(r);
        }
        return -1;
    }

    /// <summary>等待一段时间，期间可被取消。</summary>
    private void Wait(int ms)
    {
        if (ms <= 0) return;
        int slept = 0;
        const int slice = 20;
        while (slept < ms && !_cancelRequested)
        {
            int step = Math.Min(slice, ms - slept);
            Thread.Sleep(step);
            slept += step;
        }
    }

    /// <summary>
    /// 逐字符发送文本。
    ///
    /// 为什么不走剪贴板粘贴：
    ///   1. 会覆盖用户剪贴板内容；
    ///   2. 很多程序（尤其游戏 / 远程桌面 / 只响应 WM_CHAR 的自研控件）不认粘贴；
    ///   3. 逐字符才能过 IME，中文也能正常输入。
    /// </summary>
    private static void SendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        InputSender.Text(text);
    }

    public void Dispose()
    {
        _cancelRequested = true;
    }
}
