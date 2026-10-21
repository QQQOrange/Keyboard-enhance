using CapsLockPlus.Core.Input;
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
    /// 播放一个宏。
    /// </summary>
    /// <param name="macro">要播放的宏。</param>
    /// <param name="repeatOverride">覆盖宏自身的重复次数（null = 用宏设置）。</param>
    /// <param name="onCompleted">播放结束（正常 / 取消 / 异常皆算）后的回调。</param>
    public void Play(Core.Settings.Macro macro, int? repeatOverride = null,
        Action? onCompleted = null)
    {
        if (macro == null || macro.Steps.Count == 0) return;
        if (IsPlaying)
        {
            CoreLog.Info("宏播放请求被忽略：已有宏在播放");
            return;
        }

        // 复制一份，避免播放时用户在界面上改步骤导致索引错乱
        var snapshot = macro.Clone();
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
            try
            {
                for (int round = 0; round < repeat; round++)
                {
                    CurrentStep = -1;
                    foreach (var step in snapshot.Steps)
                    {
                        if (_cancelRequested) break;

                        CurrentStep++;
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
                        }
                    }

                    if (_cancelRequested) break;

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
                    try { InputSender.KeyUp(vk); } catch { }
                }
                held.Clear();

                CurrentStep = -1;
                try { _callback?.Invoke(); } catch { }
            }
        })
        {
            IsBackground = true,
            Name = "macro-player",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
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
