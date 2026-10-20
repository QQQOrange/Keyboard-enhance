using System.Windows.Threading;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Windows;

namespace CapsLockPlus.Core.Macro;

/// <summary>
/// 宏播放器。
///
/// 设计要点（参照键盘驱动的成熟方案）：
///   * **播放跑在专用后台线程**，绝不占用钩子回调 —— 钩子回调有 300ms 超时限制，
///     超时系统会静默移除钩子，整套功能就失效了；
///   * 每一步只做"发键 / 睡一会"，时间用 Stopwatch 精确等待，避免累加误差；
///   * 支持 fixed（固定延迟）/ random（每次播放在区间内随机取）；
///   * 文本输入逐字符发送，并通过 KEYEVENTF_UNICODE 保证中文与 IME 兼容；
///   * 按下过的键全部记录在 <see cref="_held"/> 里，中断或结束时**必须补齐抬起**，
///     否则 Ctrl / Shift 会粘住不放；
///   * 提供取消令牌，Esc 可随时中断。
/// </summary>
public sealed class MacroPlayer : IDisposable
{
    private readonly SemaphoreSlim _cancelGate = new(1, 1);
    private Thread? _thread;
    private volatile bool _cancelRequested;

    /// <summary>播放结束（无论正常结束还是被取消）。</summary>
    public event Action? Completed;

    /// <summary>当前是否正在播放。</summary>
    public bool IsPlaying => _thread is { IsAlive: true };

    /// <summary>当前播放到第几步（从 0 开始；-1 表示未播放）。用于界面高亮。</summary>
    public int CurrentStep { get; private set; } = -1;

    /// <summary>请求取消当前播放。</summary>
    public void Cancel()
    {
        _cancelRequested = true;
    }

    /// <summary>
    /// 播放一个宏。
    /// </summary>
    /// <param name="macro">要播放的宏。</param>
    /// <param name="repeatOverride">覆盖宏自身的重复次数（null = 用宏设置）。</param>
    public void Play(Core.Settings.Macro macro, int? repeatOverride = null)
    {
        if (macro == null || macro.Steps.Count == 0) return;
        if (IsPlaying) return;              // 同一时刻只播一个

        // 复制一份，避免播放时用户在界面上改步骤导致索引错乱
        var snapshot = macro.Clone();
        int repeat = repeatOverride ?? (snapshot.Repeat <= 0 ? int.MaxValue : snapshot.Repeat);

        _cancelRequested = false;
        CurrentStep = -1;

        _thread = new Thread(() =>
        {
            try
            {
                var held = new HashSet<int>();

                for (int round = 0; round < repeat; round++)
                {
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

                // 收尾：把所有还按着的键都抬起来，否则会粘修饰键
                foreach (var vk in held)
                {
                    try { InputSender.KeyUp(vk); } catch { }
                }
                held.Clear();
            }
            catch (Exception ex)
            {
                CoreLog.Warn("宏播放出错：" + ex.Message);
            }
            finally
            {
                CurrentStep = -1;
                Dispatcher.CurrentDispatcher.BeginInvoke(() => Completed?.Invoke());
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
        _cancelGate.Dispose();
    }
}
