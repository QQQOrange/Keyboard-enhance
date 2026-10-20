using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CapsLockPlus.Core;
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

    // 录制器放在 AppHost 上而不是这里：页面会被缓存复用，
    // 但万一被重建，本地字段就丢了，录制会"点了没反应"。

    public MacrosView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        Reload();
        // 恢复录制按钮状态（页面被重建时）
        if (Recorder.IsRecording)
        {
            RecordButton.Content = "停止录制";
            RecordStatus.Text = "录制中…照常操作键盘";
        }
    }

    private Macro? Selected => MacroList.SelectedItem as Macro;

    /// <summary>录制器（由 AppHost 持有，页面重建也不会丢）。</summary>
    private MacroRecorder Recorder => _host?.MacroRecorder ?? throw new InvalidOperationException("未初始化");

    /// <summary>重新载入列表（页面再次显示时也会调用）。</summary>
    public void Reload()
    {
        if (_host == null) return;
        MacroList.ItemsSource = null;
        MacroList.ItemsSource = _host.Settings.Macros.ToList();
        if (_editing != null)
        {
            MacroList.SelectedItem = _host.Settings.Macros.FirstOrDefault(m => m.Id == _editing.Id);
        }
        RefreshSteps();
    }

    private void OnMacroSelected(object sender, SelectionChangedEventArgs e)
    {
        var m = Selected;
        _editing = m;

        NameBox.Text = m?.Name ?? "";
        RepeatBox.Text = (m?.Repeat ?? 1).ToString();
        RefreshSteps();
        StatusText.Text = m == null ? "选一个宏，或新建" : $"共 {m.Steps.Count} 步";
    }

    private void RefreshSteps(bool keepSelection = false)
    {
        var m = _editing;
        if (m == null)
        {
            StepList.ItemsSource = null;
            StepEditor.Visibility = Visibility.Collapsed;
            return;
        }

        int want = _selectedStep;
        var rows = new List<StepVm>();
        for (int i = 0; i < m.Steps.Count; i++)
        {
            var s = m.Steps[i];
            rows.Add(new StepVm(i + 1, i, Describe(s)));
        }

        StepList.ItemsSource = rows;
        StatusText.Text = m.Steps.Count == 0 ? "还没有步骤，先录制或插入" : $"共 {m.Steps.Count} 步";

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
        "keyDown" => $"按下 {s.Key}",
        "keyUp" => $"抬起 {s.Key}",
        "delay" => $"等待 {s.DelayMs} ms",
        "delayRandom" => $"等待 {s.MinMs}~{s.MaxMs} ms（随机）",
        "text" => $"输入「{Truncate(s.Text, 24)}」",
        _ => s.Kind,
    };

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    // ---------------- 录制 ----------------

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var rec = Recorder;

        if (rec.IsRecording)
        {
            // 停止录制 -> 生成宏
            var macro = rec.Stop("新宏 " + DateTime.Now.ToString("HH:mm:ss"));
            _host.Engine.MacroRecorder = null;          // 告诉引擎：录制结束
            _host.Settings.Macros.Add(macro);
            _editing = macro;
            _host.SaveSettings();
            Reload();
            RecordButton.Content = "开始录制";
            RecordStatus.Text = $"已生成 {macro.Steps.Count} 步";
            _host.Notify($"宏已录制：{macro.Steps.Count} 步");
            return;
        }

        // 开始录制：钩子继续透传，只记录事件
        rec.Start();
        _host.Engine.MacroRecorder = rec;
        RecordButton.Content = "停止录制";
        RecordStatus.Text = "录制中…照常操作键盘";
        CoreLog.Info("宏录制开始");
    }

    // ---------------- 增删 ----------------

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var m = new Macro { Name = "新宏", Repeat = 1 };
        _host.Settings.Macros.Add(m);
        _editing = m;
        _host.SaveSettings();
        Reload();
        NameBox.Focus();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _editing == null) return;
        string name = _editing.Name;
        _host.Settings.Macros.RemoveAll(m => m.Id == _editing.Id);
        _editing = null;
        _host.SaveSettings();
        Reload();
        _host.Notify($"已删除宏「{name}」");
    }

    private void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _editing == null)
        {
            StatusText.Text = "先选一个宏";
            return;
        }
        _host.Context.MacroPlayer.Play(_editing);
        StatusText.Text = "播放中…按 Esc 可中断";
    }

    // ---------------- 编辑 ----------------

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_editing == null || NameBox.Text == _editing.Name) return;
        _editing.Name = NameBox.Text;
        _host?.SaveSettings();
        MacroList.Items.Refresh();
    }

    private void OnRepeatChanged(object sender, TextChangedEventArgs e)
    {
        if (_editing == null) return;
        if (int.TryParse(RepeatBox.Text, out int n))
        {
            _editing.Repeat = Math.Max(0, n);
            _host?.SaveSettings();
        }
    }

    private void OnRemoveStep(object sender, RoutedEventArgs e)
    {
        if (_editing == null || sender is not Button { Tag: int index }) return;
        if (index < 0 || index >= _editing.Steps.Count) return;
        _editing.Steps.RemoveAt(index);
        _host?.SaveSettings();
        RefreshSteps();
    }

    // ---------------- 选中步骤的编辑 ----------------

    /// <summary>当前选中步骤的下标（-1 = 没选）。</summary>
    private int _selectedStep = -1;

    private void OnStepSelected(object sender, SelectionChangedEventArgs e)
    {
        _selectedStep = StepList.SelectedItem is StepVm vm ? vm.Index : -1;
        InsertHint.Text = _selectedStep >= 0
            ? $"插入（插到第 {_selectedStep + 1} 步之后）"
            : "插入（追加到末尾）";
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
        StepEditorTitle.Text = $"第 {_selectedStep + 1} 步";
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
                KeyEditLabel.Text = Core.Input.KeyName.Display(step.Key);
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
        var text = PromptText("要输入什么文本？", "插入文本步骤");
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
        var ok = new Button { Content = "确定", IsDefault = true, Margin = new Thickness(0, 10, 0, 0),
                              HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(16, 5, 16, 5) };
        ok.Click += (_, _) => win.DialogResult = true;
        panel.Children.Add(ok);
        win.Content = panel;
        return win.ShowDialog() == true ? box.Text : null;
    }

    private sealed class StepVm
    {
        public StepVm(int order, int index, string label)
        {
            Order = order;
            Index = index;
            Label = label;
        }
        public int Order { get; }
        public int Index { get; }
        public string Label { get; }
    }
}
