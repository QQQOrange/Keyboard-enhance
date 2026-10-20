using System.Diagnostics;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Macro;

/// <summary>
/// 宏录制器。
///
/// 录制期间**照常透传按键**（不吞键，不影响用户正常操作），
/// 只把每次事件连同高精度时间戳记下来；
/// 停止时把相邻事件的时间差转成"延迟步骤"，得到可编辑的宏。
///
/// 两个关键取舍：
///  1. 修饰键**不压平成组合键**。Ctrl+C 录成
///     KeyDown(Ctrl) / Delay / KeyDown(C) / … / KeyUp(C) / KeyUp(Ctrl)，
///     这样用户事后可以单独改某一边的时长。压平成"组合键"就改不动了。
///  2. 时间戳用 Stopwatch（QueryPerformanceCounter），
///     不用 DateTime.Now —— 后者只有 15ms 分辨率，录出来的节奏全是乱的。
/// </summary>
public sealed class MacroRecorder
{
    private readonly object _gate = new();
    private readonly List<RawEvent> _events = new();
    private readonly Stopwatch _clock = new();
    private bool _recording;

    private sealed record RawEvent(int Vk, bool Down, long Ms);

    /// <summary>是否正在录制。</summary>
    public bool IsRecording
    {
        get { lock (_gate) { return _recording; } }
    }

    /// <summary>已录制的事件数（界面显示用）。</summary>
    public int EventCount
    {
        get { lock (_gate) { return _events.Count; } }
    }

    /// <summary>已录制的毫秒数。</summary>
    public long ElapsedMs
    {
        get { lock (_gate) { return _recording ? _clock.ElapsedMilliseconds : 0; } }
    }

    public void Start()
    {
        lock (_gate)
        {
            _events.Clear();
            _clock.Restart();
            _recording = true;
        }
    }

    /// <summary>喂一个真实按键给录制器（由钩子回调调用，必须极快）。</summary>
    public void Record(int vk, bool down)
    {
        bool shouldLog = false;
        lock (_gate)
        {
            if (!_recording) return;
            _events.Add(new RawEvent(vk, down, _clock.ElapsedMilliseconds));
            shouldLog = _events.Count <= 3;      // 前几个事件打日志，确认真的在录
        }
        if (shouldLog)
            CoreLog.Info($"宏录制：vk=0x{vk:X2} down={down}");
    }

    /// <summary>
    /// 停止录制并生成宏。
    /// </summary>
    /// <param name="name">宏名称</param>
    /// <param name="minDelayMs">小于这个间隔的延迟直接丢掉（过滤人手抖动）</param>
    public Core.Settings.Macro Stop(string name)
    {
        lock (_gate)
        {
            _recording = false;
            var sw = _clock.ElapsedMilliseconds;
            var events = _events.ToList();

            var macro = new Core.Settings.Macro { Name = name };

            // 第一个事件之前可能等了很久（用户在想），不留这段
            foreach (var (e, i) in events.Select((e, i) => (e, i)))
            {
                if (i > 0)
                {
                    int gap = (int)(e.Ms - events[i - 1].Ms);
                    if (gap >= minDelayMs)
                    {
                        macro.Steps.Add(new MacroStep { Kind = "delay", DelayMs = gap });
                    }
                }

                macro.Steps.Add(new MacroStep
                {
                    Kind = e.Down ? "keyDown" : "keyUp",
                    Key = KeyName.NameOf(e.Vk) ?? $"vk0x{e.Vk:X2}",
                });
            }

            // 收尾补一个短延迟，避免循环播放时按键贴在一起
            if (macro.Steps.Count > 0)
                macro.Steps.Add(new MacroStep { Kind = "delay", DelayMs = 100 });

            CoreLog.Info($"宏录制完成：{events.Count} 个事件，时长 {sw}ms，" +
                         $"生成 {macro.Steps.Count} 步");
            return macro;
        }
    }

    /// <summary>取消录制（不生成宏）。</summary>
    public void Abort()
    {
        lock (_gate)
        {
            _recording = false;
            _events.Clear();
        }
    }

    private const int minDelayMs = 15;
}
