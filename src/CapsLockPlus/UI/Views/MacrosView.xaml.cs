using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Macro;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.UI.Windows;
using Macro = CapsLockPlus.Core.Settings.Macro;

namespace CapsLockPlus.UI.Views;

public partial class MacrosView : UserControl
{
    private AppHost? _host;
    private Macro? _editing;

    /// <summary>程序化填充控件时置 true，避免把回填当成用户输入再存一遍。</summary>
    private bool _suppress;

    /// <summary>正在重设宏列表（此时的选中变化不是用户操作）。</summary>
    private bool _reloading;

    // 录制器放在 AppHost 上而不是这里：页面会被缓存复用，
    // 但万一被重建，本地字段就丢了，录制会"点了没反应"。

    public MacrosView()
    {
        InitializeComponent();
        LiveList.ItemsSource = _live;
        // 让控件本身可以拿焦点：录制开始后焦点不能留在按钮上，
        // 否则用户打个空格就把录制停了（Button 被聚焦时空格 = 点击）
        Focusable = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Initialize(AppHost host)
    {
        _host = host;
        Reload();
    }

    /// <summary>
    /// 每次进入页面都会触发（页面是缓存的，Initialize 只跑一次）。
    ///
    /// 订阅放在这里而不是构造里：切到别的页面时 Unloaded 会退订，
    /// 录制还在后台继续，回来时必须重新接上并补回已经录下的部分。
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var rec = _host.MacroRecorder;
        rec.Recorded -= OnRecorderEvent;
        rec.Recorded += OnRecorderEvent;

        if (rec.IsRecording) ResumeLive();
        else ApplyIdleUi();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_host?.MacroRecorder is { } rec) rec.Recorded -= OnRecorderEvent;
        // 注意：定时器不停 —— 录制还在继续，悬浮条要照常刷新
    }

    private Macro? Selected => MacroList.SelectedItem as Macro;

    /// <summary>录制器（由 AppHost 持有，页面重建也不会丢）。</summary>
    private MacroRecorder Recorder => _host?.MacroRecorder ?? throw new InvalidOperationException(Loc.T("未初始化"));

    // ---------------- 列表 ----------------

    /// <summary>重新载入列表（页面再次显示时也会调用）。</summary>
    public void Reload()
    {
        if (_host == null) return;

        /* 重设 ItemsSource 会让 ListBox 先清空选择，进而触发一次
         * SelectionChanged(null) —— 早先没挡住这一下，_editing 被清成 null，
         * 于是"切走再切回宏页"右侧就空了，之前正在编辑的宏也丢了。
         * 这里用 _reloading 把重载期间的选中变化挡掉，重载完再按 ID 选回来。 */
        string? keepId = _editing?.Id;

        _reloading = true;
        MacroList.ItemsSource = null;
        MacroList.ItemsSource = _host.Settings.Macros.ToList();
        _reloading = false;

        var macros = _host.Settings.Macros;
        var target = (keepId != null ? macros.FirstOrDefault(m => m.Id == keepId) : null)
                     ?? macros.FirstOrDefault();

        EmptyHint.Visibility = macros.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (target != null)
        {
            // 走 OnMacroSelected 把名称/重复/步骤一起回填
            MacroList.SelectedItem = target;
        }
        else
        {
            _editing = null;
            _selectedStep = -1;
            NameBox.Text = "";
            RepeatBox.Text = "1";
            RefreshSteps();
            StatusText.Text = Loc.T("选一个宏，或新建一个");
        }
    }

    private void OnMacroSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_reloading) return;          // 列表重载引起的空选择，不是用户在点

        var m = Selected;
        _editing = m;

        NameBox.Text = m?.Name ?? "";
        RepeatBox.Text = (m?.Repeat ?? 1).ToString();
        _selectedStep = -1;
        RefreshSteps();
        StatusText.Text = m == null ? Loc.T("选一个宏，或新建一个") : Loc.F("{0} 步 · 时长约 {1}", m.Steps.Count, m.DurationText);
    }

    private void RefreshSteps(bool keepSelection = false)
    {
        var m = _editing;
        if (m == null)
        {
            _stepRows = new List<StepVm>();
            StepList.ItemsSource = null;
            StepEditor.Visibility = Visibility.Collapsed;
            StepCount.Text = "";
            StepEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        int want = _selectedStep;
        var rows = new List<StepVm>();
        for (int i = 0; i < m.Steps.Count; i++)
        {
            rows.Add(new StepVm(i + 1, i, Describe(m.Steps[i])));
        }

        _stepRows = rows;
        StepList.ItemsSource = rows;
        StepCount.Text = m.Steps.Count == 0 ? "" : Loc.F("共 {0} 步 · 约 {1}", m.Steps.Count, m.DurationText);
        StepEmpty.Visibility = m.Steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = m.Steps.Count == 0 ? Loc.T("还没有步骤，先录制或插入") : Loc.F("共 {0} 步", m.Steps.Count);

        if (keepSelection && want >= 0 && want < rows.Count)
        {
            StepList.SelectedIndex = want;
        }
        else if (keepSelection)
        {
            _selectedStep = -1;
            StepEditor.Visibility = Visibility.Collapsed;
        }
    }

    private static string Describe(MacroStep s) => s.Kind switch
    {
        "keyDown" => Loc.F("按下 {0}", KeyName.Display(s.Key)),
        "keyUp" => Loc.F("抬起 {0}", KeyName.Display(s.Key)),
        "delay" => Loc.F("等待 {0} ms", s.DelayMs),
        "delayRandom" => Loc.F("等待 {0}~{1} ms（随机）", s.MinMs, s.MaxMs),
        "text" => Loc.F("输入「{0}」", Truncate(s.Text, 24)),
        _ => s.Kind,
    };

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    // ---------------- 录制 ----------------

    /// <summary>实时列表的数据源（录制时每个按键一行）。</summary>
    private readonly ObservableCollection<LiveRow> _live = new();

    /// <summary>上一条事件的时间戳，用来判断中间要不要插"等待"行（-1 = 还没有）。</summary>
    private long _lastEventMs = -1;

    /// <summary>最近按下的键（悬浮条和统计行显示）。</summary>
    private string _lastKeyLabel = "";

    private DispatcherTimer? _tick;
    private DispatcherTimer? _playTick;

    // 播放中标志。**不能靠 StatusText.Text.StartsWith("播放中") 判断** ——
    // 英文界面下状态行是 "Playing…"，拿中文去比永远不成立，
    // 于是播放结束后状态行停在"播放中"，而且这个 bug 只在切到英文时才出现。
    private bool _playing;

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var rec = Recorder;

        if (rec.IsRecording)
        {
            // 一个键都没按就停止：不生成空宏（列表里堆一串"新宏 HH:mm:ss"毫无意义）
            if (rec.EventCount == 0)
            {
                rec.Abort();
                _host.Engine.MacroRecorder = null;
                StopTick();
                RecordingHudWindow.HideHud();
                ApplyIdleUi();
                RecordStatus.Text = Loc.T("这次没有录到按键，没有生成宏");
                Focus();
                return;
            }

            // 停止录制 -> 生成宏
            var macro = rec.Stop(Loc.T("新宏 ") + Loc.FormatTime(DateTime.Now));
            _host.Engine.MacroRecorder = null;          // 告诉引擎：录制结束
            _host.Settings.Macros.Add(macro);
            _editing = macro;
            SaveAndRebuild();
            Reload();
            StopTick();
            RecordingHudWindow.HideHud();
            ApplyIdleUi();
            RecordStatus.Text = Loc.F("已生成「{0}」· {1} 步 · {2}", macro.Name, macro.Steps.Count, macro.DurationText);
            Focus();                       // 焦点从"停止并保存"按钮上挪开，别再被下一次敲键触发
            _host.Notify(Loc.F("宏已录制：{0} 步", macro.Steps.Count));
            return;
        }

        // 开始录制：钩子继续透传，只记录事件
        rec.Start();
        _host.Engine.MacroRecorder = rec;
        _live.Clear();
        _lastEventMs = -1;
        _lastKeyLabel = "";
        ApplyRecordingUi();
        StartTick();
        Focus();                       // 见 ctor 注释：焦点绝不能留在按钮上
        CoreLog.Info("宏录制开始");
    }

    private void OnCancelRecordClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || !Recorder.IsRecording) return;
        Recorder.Abort();
        _host.Engine.MacroRecorder = null;
        StopTick();
        RecordingHudWindow.HideHud();
        ApplyIdleUi();
        RecordStatus.Text = Loc.T("已取消这次录制");
        _host.Notify(Loc.T("已取消录制"));
    }

    /// <summary>
    /// 钩子线程回调：立刻把事件转交界面线程。
    /// 这里**绝不能**碰控件、也不能同步等待 —— 拖慢钩子会被系统摘钩。
    /// </summary>
    private void OnRecorderEvent(int vk, bool down, long ms)
    {
        Dispatcher.BeginInvoke(new Action(() => AppendLive(vk, down, ms)));
    }

    private void AppendLive(int vk, bool down, long ms)
    {
        // 排队期间可能已经点了停止，这种"尾巴"直接丢掉
        if (_host?.MacroRecorder is not { IsRecording: true }) return;
        AddLiveRow(vk, down, ms);
        LiveScroll.ScrollToEnd();
    }

    /// <summary>把一条事件变成实时行（必要时先补一行"等待 N ms"，和最终宏完全一致）。</summary>
    private void AddLiveRow(int vk, bool down, long ms)
    {
        if (_lastEventMs >= 0)
        {
            int gap = (int)(ms - _lastEventMs);
            if (gap >= MacroRecorder.MinDelayMs) _live.Add(LiveRow.Delay(gap));
        }
        _lastEventMs = ms;

        string name = KeyName.NameOf(vk) ?? $"vk0x{vk:X2}";
        _lastKeyLabel = KeyName.Display(name) + (down ? " ↓" : " ↑");
        _live.Add(LiveRow.Key(ms, KeyName.Display(name), down));

        // 长时间录制时限制行数：界面不需要 5000 行，内存和滚动都会受影响
        if (_live.Count > 400) _live.RemoveAt(0);
    }

    /// <summary>页面重新进入 / 被重建时，用录制器的快照把实时列表补回来。</summary>
    private void ResumeLive()
    {
        _live.Clear();
        _lastEventMs = -1;
        _lastKeyLabel = "";
        foreach (var e in Recorder.Snapshot()) AddLiveRow(e.Vk, e.Down, e.Ms);
        ApplyRecordingUi();
        StartTick();
        LiveScroll.ScrollToEnd();
    }

    private void StartTick()
    {
        if (_tick == null)
        {
            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _tick.Tick += OnTick;
        }
        _tick.Start();
    }

    private void StopTick() => _tick?.Stop();

    /// <summary>每 100ms 刷新一次时长/事件数，并同步悬浮条。</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        var rec = _host?.MacroRecorder;
        if (rec == null || !rec.IsRecording) { StopTick(); return; }

        long ms = rec.ElapsedMs;
        int count = rec.EventCount;
        string clock = RecordingHudWindow.Format(ms);

        RecordStatus.Text = Loc.F("录制中… {0} · {1} 个事件", clock, count);
        LiveStats.Text = count == 0
            ? Loc.T("等待第一个按键…")
            : Loc.F("{0} 个事件 · {1} · 停止后约 {2} 步", count, clock, _live.Count);

        RecordingHudWindow.UpdateHud(ms, count, _lastKeyLabel);

        // 主窗口在前台时没必要再浮一条（屏幕上已经有实时列表了）
        if (Window.GetWindow(this)?.IsActive == true) RecordingHudWindow.HideHud();
        else RecordingHudWindow.ShowHud();
    }

    private void ApplyRecordingUi()
    {
        RecordButton.Content = Loc.T("■ 停止并保存");
        if (TryFindResource("Button.Danger") is Style danger) RecordButton.Style = danger;
        CancelRecordButton.Visibility = Visibility.Visible;
        RecDotIdle.Visibility = Visibility.Collapsed;
        RecDotRec.Visibility = Visibility.Visible;
        RecBadgeIdle.Visibility = Visibility.Collapsed;
        RecBadgeRec.Visibility = Visibility.Visible;
        RecTitle.Text = Loc.T("正在录制");
        RecHint.Text = Loc.T("照常操作键盘即可，每按一个键会立刻出现在下面；停止后自动生成可编辑的步骤。");
        StartPulse();

        // 整块右侧换成"录制监视器"：录制时看步骤列表没有意义，
        // 而且这时候改步骤只会让人分不清"屏幕上的"和"正在录的"
        StepsCard.Visibility = Visibility.Collapsed;
        LivePanel.Visibility = Visibility.Visible;

        // 左侧也锁住：录制中"新建/删除/测试播放"都会把状态搅乱
        // （测试播放还会把自己的按键录进去）。
        // 列表用 IsHitTestVisible 而不是 IsEnabled —— 禁用态会把列表刷成系统灰白底，
        // 在深色主题里非常刺眼。
        ListActions.IsEnabled = false;
        MacroList.IsHitTestVisible = false;
        MacroList.Opacity = 0.55;
    }

    private void ApplyIdleUi()
    {
        RecordButton.Content = Loc.T("● 开始录制");
        if (TryFindResource("Button.Primary") is Style primary) RecordButton.Style = primary;
        CancelRecordButton.Visibility = Visibility.Collapsed;
        RecDotIdle.Visibility = Visibility.Visible;
        RecDotRec.Visibility = Visibility.Collapsed;
        RecBadgeIdle.Visibility = Visibility.Visible;
        RecBadgeRec.Visibility = Visibility.Collapsed;
        RecTitle.Text = Loc.T("录制宏");
        RecHint.Text = Loc.T("点「开始录制」后照常操作键盘，按键会立刻出现在下面。录制期间不影响正常输入。");
        StopPulse();
        LivePanel.Visibility = Visibility.Collapsed;
        StepsCard.Visibility = Visibility.Visible;
        ListActions.IsEnabled = true;
        MacroList.IsHitTestVisible = true;
        MacroList.Opacity = 1;
    }

    private void StartPulse()
    {
        var pulse = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(620))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        RecDotRec.BeginAnimation(OpacityProperty, pulse);
    }

    private void StopPulse()
    {
        RecDotRec.BeginAnimation(OpacityProperty, null);
        RecDotRec.Opacity = 1;
    }

    // ---------------- 增删 ----------------

    /// <summary>
    /// 保存设置并重建动作表。
    ///
    /// 宏在动作表里是以「播放宏：xxx」的形式注册的，动作表不重建的话
    /// 新建的宏不会出现在「按键映射」页的功能下拉里 —— 用户就绑不上它。
    /// 这就是"通过触发键进层后按键绑不了宏"的原因。
    /// </summary>
    private void SaveAndRebuild()
    {
        if (_host == null) return;
        _host.SaveSettings();
        _host.RebuildActions();
    }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var m = new Macro { Name = Loc.T("新宏"), Repeat = 1 };
        _host.Settings.Macros.Add(m);
        _editing = m;
        SaveAndRebuild();
        Reload();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _editing == null) return;
        string name = _editing.Name;
        _host.Settings.Macros.RemoveAll(m => m.Id == _editing.Id);
        _editing = null;
        SaveAndRebuild();
        Reload();
        _host.Notify(Loc.F("已删除宏「{0}」", name));
    }

    private void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _editing == null)
        {
            StatusText.Text = Loc.T("先选一个宏");
            return;
        }
        if (_editing.Steps.Count == 0)
        {
            StatusText.Text = Loc.T("这个宏还没有步骤");
            return;
        }
        _playing = true;
        _host.Context.MacroPlayer.Play(_editing, onCompleted: () =>
            Dispatcher.BeginInvoke(OnPlayFinished));
        StatusText.Text = Loc.T("播放中…按 Esc 或点「停止」可中断");
        StopPlayButton.Visibility = Visibility.Visible;
        ClearCurrentMarker();
        StartPlayTick();
    }

    /// <summary>停止正在播放的宏（Esc 之外的第二入口 —— 引擎被暂停时
    /// Esc 转发会失效，这里始终可用）。</summary>
    private void OnStopPlayClick(object sender, RoutedEventArgs e)
    {
        _host?.Context.MacroPlayer.Cancel();
    }

    private void StartPlayTick()
    {
        if (_playTick == null)
        {
            _playTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _playTick.Tick += OnPlayTick;
        }
        _playTick.Start();
    }

    /// <summary>播放时高亮当前步骤 —— 光有"播放中"三个字，看不出播到哪了。</summary>
    private void OnPlayTick(object? sender, EventArgs e)
    {
        var player = _host?.Context.MacroPlayer;
        if (player == null || !player.IsPlaying || _editing == null)
        {
            _playTick?.Stop();
            ClearCurrentMarker();
            return;
        }

        int idx = player.CurrentStep;
        for (int i = 0; i < _stepRows.Count; i++) _stepRows[i].IsCurrent = i == idx;
        if (idx >= 0 && idx < _stepRows.Count)
        {
            StatusText.Text = Loc.F("播放中… 第 {0} / {1} 步（Esc 停止）", idx + 1, _editing.Steps.Count);
            StepList.ScrollIntoView(_stepRows[idx]);
        }
    }

    private void ClearCurrentMarker()
    {
        foreach (var r in _stepRows) r.IsCurrent = false;
    }

    private void OnPlayFinished()
    {
        StopPlayButton.Visibility = Visibility.Collapsed;
        _playTick?.Stop();
        ClearCurrentMarker();
        if (_playing)
        {
            _playing = false;
            StatusText.Text = Loc.T("播放结束");
        }
    }

    // ---------------- 编辑 ----------------

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_editing == null || NameBox.Text == _editing.Name) return;
        _editing.Name = NameBox.Text;
        // 改名要重建动作表：功能下拉里显示的就是宏名
        SaveAndRebuild();
        MacroList.Items.Refresh();
    }

    private void OnRepeatChanged(object sender, TextChangedEventArgs e)
    {
        if (_editing == null) return;
        if (int.TryParse(RepeatBox.Text, out int n))
        {
            _editing.Repeat = Math.Max(0, n);
            _host?.SaveSettings();
            MacroList.Items.Refresh();
        }
    }

    private void OnRemoveStep(object sender, RoutedEventArgs e)
    {
        if (_editing == null || sender is not Button { Tag: int index }) return;
        if (index < 0 || index >= _editing.Steps.Count) return;
        _editing.Steps.RemoveAt(index);
        if (_selectedStep >= index) _selectedStep = Math.Min(_selectedStep - 1, _editing.Steps.Count - 1);
        _host?.SaveSettings();
        RefreshSteps();
        if (_selectedStep >= 0) StepList.SelectedIndex = _selectedStep;
    }

    // ---------------- 选中步骤的编辑 ----------------

    /// <summary>当前选中步骤的下标（-1 = 没选）。</summary>
    private int _selectedStep = -1;

    /// <summary>步骤列表当前的行（播放高亮要按行改 IsCurrent）。</summary>
    private List<StepVm> _stepRows = new();

    private void OnStepSelected(object sender, SelectionChangedEventArgs e)
    {
        _selectedStep = StepList.SelectedItem is StepVm vm ? vm.Index : -1;
        InsertHint.Text = _selectedStep >= 0
            ? Loc.F("插入（插到第 {0} 步之后）", _selectedStep + 1)
            : Loc.T("插入（追加到末尾）");
        BindStepEditor();
    }

    /// <summary>把选中步骤的内容填进编辑区。</summary>
    private void BindStepEditor()
    {
        if (_editing == null || _selectedStep < 0 || _selectedStep >= _editing.Steps.Count)
        {
            StepEditor.Visibility = Visibility.Collapsed;
            return;
        }

        var step = _editing.Steps[_selectedStep];
        StepEditor.Visibility = Visibility.Visible;
        StepEditorTitle.Text = Loc.F("第 {0} 步", _selectedStep + 1);
        _suppress = true;

        DelayEditPanel.Visibility = step.Kind == "delay" ? Visibility.Visible : Visibility.Collapsed;
        RandomEditPanel.Visibility = step.Kind == "delayRandom" ? Visibility.Visible : Visibility.Collapsed;
        TextEditPanel.Visibility = step.Kind == "text" ? Visibility.Visible : Visibility.Collapsed;
        KeyEditPanel.Visibility = step.Kind is "keyDown" or "keyUp" ? Visibility.Visible : Visibility.Collapsed;

        switch (step.Kind)
        {
            case "delay":
                DelayEditBox.Text = step.DelayMs.ToString();
                break;
            case "delayRandom":
                MinEditBox.Text = step.MinMs.ToString();
                MaxEditBox.Text = step.MaxMs.ToString();
                break;
            case "text":
                TextEditBox.Text = step.Text;
                break;
            case "keyDown":
            case "keyUp":
                KeyEditLabel.Text = KeyName.Display(step.Key);
                break;
        }

        _suppress = false;
    }

    private void OnDelayEditChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;
        if (_selectedStep < 0 || _selectedStep >= _editing.Steps.Count) return;
        var step = _editing.Steps[_selectedStep];
        if (step.Kind != "delay") return;
        if (!int.TryParse(DelayEditBox.Text, out int ms)) return;
        step.DelayMs = Math.Clamp(ms, 0, 600_000);
        _host.SaveSettings();
        RefreshSteps(keepSelection: true);
    }

    private void OnRandomEditChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;
        if (_selectedStep < 0 || _selectedStep >= _editing.Steps.Count) return;
        var step = _editing.Steps[_selectedStep];
        if (step.Kind != "delayRandom") return;
        if (int.TryParse(MinEditBox.Text, out int lo)) step.MinMs = Math.Clamp(lo, 0, 600_000);
        if (int.TryParse(MaxEditBox.Text, out int hi)) step.MaxMs = Math.Clamp(hi, 0, 600_000);
        _host.SaveSettings();
        RefreshSteps(keepSelection: true);
    }

    private void OnTextEditChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;
        if (_selectedStep < 0 || _selectedStep >= _editing.Steps.Count) return;
        var step = _editing.Steps[_selectedStep];
        if (step.Kind != "text") return;
        step.Text = TextEditBox.Text;
        _host.SaveSettings();
        RefreshSteps(keepSelection: true);
    }

    private void OnChangeStepKey(object sender, RoutedEventArgs e)
    {
        if (_host == null || _editing == null) return;
        if (_selectedStep < 0 || _selectedStep >= _editing.Steps.Count) return;

        var dlg = new KeyCaptureWindow { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.CapturedCombo)) return;

        _editing.Steps[_selectedStep].Key = dlg.CapturedCombo;
        _host.SaveSettings();
        BindStepEditor();
        RefreshSteps(keepSelection: true);
    }

    // ---------------- 调整顺序 ----------------

    private void OnMoveStepUp(object sender, RoutedEventArgs e) => MoveStep(-1);

    private void OnMoveStepDown(object sender, RoutedEventArgs e) => MoveStep(+1);

    /// <summary>和相邻步骤交换位置（录错顺序时不用删掉重录）。</summary>
    private void MoveStep(int delta)
    {
        if (_editing == null || _selectedStep < 0) return;
        int to = _selectedStep + delta;
        if (to < 0 || to >= _editing.Steps.Count) return;

        var step = _editing.Steps[_selectedStep];
        _editing.Steps.RemoveAt(_selectedStep);
        _editing.Steps.Insert(to, step);
        _selectedStep = to;

        _host?.SaveSettings();
        RefreshSteps(keepSelection: true);
        BindStepEditor();
        if (_selectedStep >= 0 && _selectedStep < _stepRows.Count)
            StepList.ScrollIntoView(_stepRows[_selectedStep]);
    }

    // ---------------- 插入步骤 ----------------

    /// <summary>
    /// 新步骤要插到哪。选中了某一步就插到它**后面**，
    /// 否则追加到末尾 —— 这样用户能在任意两个字之间插入延迟。
    /// </summary>
    private int InsertIndex()
    {
        if (_editing == null) return 0;
        if (_selectedStep < 0 || _selectedStep >= _editing.Steps.Count)
            return _editing.Steps.Count;      // 末尾
        return _selectedStep + 1;
    }

    private void AddStep(MacroStep step)
    {
        if (_editing == null) return;
        int at = InsertIndex();
        _editing.Steps.Insert(at, step);
        _selectedStep = at;                    // 选中新插入的那一步，方便接着改
        _host?.SaveSettings();
        RefreshSteps(keepSelection: true);
        BindStepEditor();
    }

    private void OnInsertDelay(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        AddStep(new MacroStep { Kind = "delay", DelayMs = 200 });
    }

    private void OnInsertRandom(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        AddStep(new MacroStep { Kind = "delayRandom", MinMs = 80, MaxMs = 220 });
    }

    private void OnInsertText(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        var text = PromptText(Loc.T("要输入什么文本？"), Loc.T("插入文本步骤"));
        if (string.IsNullOrEmpty(text)) return;
        AddStep(new MacroStep { Kind = "text", Text = text });
    }

    private void OnInsertKeyDown(object sender, RoutedEventArgs e) => InsertKey("keyDown");

    private void OnInsertKeyUp(object sender, RoutedEventArgs e) => InsertKey("keyUp");

    private void InsertKey(string kind)
    {
        if (_editing == null) return;

        // 用捕获窗口拿一个键（支持组合键），Esc 取消
        var dlg = new KeyCaptureWindow { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.CapturedCombo)) return;

        AddStep(new MacroStep { Kind = kind, Key = dlg.CapturedCombo });
    }

    // ---------------- 简单输入框 ----------------

    private static string? PromptText(string message, string title)
    {
        var win = new Window
        {
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.ToolWindow,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 8) });
        var box = new TextBox { MinHeight = 28 };
        panel.Children.Add(box);
        var ok = new Button { Content = Loc.T("确定"), IsDefault = true, Margin = new Thickness(0, 10, 0, 0),
                              HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(16, 5, 16, 5) };
        ok.Click += (_, _) => win.DialogResult = true;
        panel.Children.Add(ok);
        win.Content = panel;
        return win.ShowDialog() == true ? box.Text : null;
    }

    // ---------------- 视图模型 ----------------

    /// <summary>
    /// 仅供离屏渲染：把页面摆成"正在录制、已经录到几个键"的样子。
    /// 和 RenderTools 里窗口绑定面板的做法一致 —— 平时不常驻的状态也得能被截图确认。
    /// </summary>
    internal bool ShowRecordingForRender()
    {
        if (_host == null) return false;

        _live.Clear();
        _lastEventMs = -1;
        _lastKeyLabel = "";

        // 一串"按下 A、Ctrl+C、再打字"的典型轨迹（含两次停顿）
        (int Vk, bool Down, long Ms)[] seq =
        {
            (0xA2, true, 0), (0x41, true, 130), (0x41, false, 190), (0xA2, false, 240),
            (0xA2, true, 700), (0x43, true, 860), (0x43, false, 930), (0xA2, false, 980),
            (0x48, true, 1240), (0x48, false, 1320), (0x49, true, 1400), (0x49, false, 1480),
        };
        foreach (var (vk, down, ms) in seq) AddLiveRow(vk, down, ms);

        ApplyRecordingUi();
        LiveStats.Text = Loc.T("12 个事件 · 0:01.4 · 停止后约 22 步");
        RecordStatus.Text = Loc.T("录制中… 0:01.4 · 12 个事件");
        return true;
    }

    internal void ResetRenderState()
    {
        StopTick();
        RecordingHudWindow.HideHud();
        ApplyIdleUi();
        RecordStatus.Text = "";
    }

    /// <summary>步骤列表当前的行数（自检用）。</summary>
    internal int StepRowCountForTest => _stepRows.Count;

    /// <summary>实时列表当前的行数（自检用）。</summary>
    internal int LiveRowCountForTest => _live.Count;

    /// <summary>界面是否已经切到"录制态"（步骤卡片让位给实时监视器）。</summary>
    internal bool IsRecordingUiForTest =>
        LivePanel.Visibility == Visibility.Visible && StepsCard.Visibility == Visibility.Collapsed;

    /// <summary>自检用：直接在两种界面状态间切换（不依赖 AppHost）。</summary>
    internal void ToggleRecordingUiForTest(bool recording)
    {
        if (recording) ApplyRecordingUi();
        else ApplyIdleUi();
    }

    /// <summary>把一条按键喂进实时列表 —— 自检里代替真实钩子事件。</summary>
    internal void FeedLiveForTest(int vk, bool down, long ms) => AddLiveRow(vk, down, ms);

    /// <summary>步骤列表的一行。<c>IsCurrent</c> 会变，所以要实现 INotifyPropertyChanged。</summary>
    private sealed class StepVm : INotifyPropertyChanged
    {
        private bool _isCurrent;

        public StepVm(int order, int index, string label)
        {
            Order = order;
            Index = index;
            Label = label;
        }

        public int Order { get; }
        public int Index { get; }
        public string Label { get; }

        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value) return;
                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>实时列表的一行：一个按键，或一段等待。</summary>
    private sealed class LiveRow
    {
        public string Time { get; private init; } = "";
        public string Text { get; private init; } = "";
        public string Tag { get; private init; } = "";
        public bool IsUp { get; private init; }
        public bool IsDelay { get; private init; }

        public static LiveRow Key(long ms, string name, bool down) => new()
        {
            Time = $"{ms / 1000.0:0.00}s",
            Text = name,
            Tag = down ? Loc.T("按下") : Loc.T("抬起"),
            IsUp = !down,
        };

        public static LiveRow Delay(int gapMs) => new()
        {
            Time = "",
            Text = Loc.F("等待 {0} ms", gapMs),
            IsDelay = true,
        };
    }
}
