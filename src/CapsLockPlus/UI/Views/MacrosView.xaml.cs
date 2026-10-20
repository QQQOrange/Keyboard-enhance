using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CapsLockPlus.Core.Macro;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.UI.Windows;
using Macro = CapsLockPlus.Core.Settings.Macro;

namespace CapsLockPlus.UI.Views;

public partial class MacrosView : UserControl
{
    private AppHost? _host;
    private Macro? _editing;
    private readonly MacroRecorder _recorder = new();

    public MacrosView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        Reload();
    }

    private Macro? Selected => MacroList.SelectedItem as Macro;

    // ---------------- 列表 ----------------

    private void Reload()
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

    private void RefreshSteps()
    {
        var m = _editing;
        if (m == null)
        {
            StepList.ItemsSource = null;
            return;
        }

        var rows = new List<StepVm>();
        for (int i = 0; i < m.Steps.Count; i++)
        {
            var s = m.Steps[i];
            rows.Add(new StepVm(i + 1, i, Describe(s)));
        }
        StepList.ItemsSource = rows;
        StatusText.Text = m.Steps.Count == 0 ? "还没有步骤，先录制或插入" : $"共 {m.Steps.Count} 步";
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

        if (_recorder.IsRecording)
        {
            // 停止录制 -> 生成宏
            var macro = _recorder.Stop("新宏 " + DateTime.Now.ToString("HH:mm:ss"));
            _host.Settings.Macros.Add(macro);
            _editing = macro;
            _host.SaveSettings();
            Reload();
            RecordButton.Content = "开始录制";
            RecordStatus.Text = "已生成";
            _host.Notify($"宏已录制：{macro.Steps.Count} 步");
            return;
        }

        // 开始录制：钩子开始记录（引擎层透传，不影响正常输入）
        _recorder.Start();
        _host.EnableMacroRecording(_recorder);
        RecordButton.Content = "停止录制";
        RecordStatus.Text = "录制中…照常操作键盘";
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

    // ---------------- 插入步骤 ----------------

    private void OnInsertDelay(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        _editing.Steps.Add(new MacroStep { Kind = "delay", DelayMs = 200 });
        _host?.SaveSettings();
        RefreshSteps();
    }

    private void OnInsertRandom(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        _editing.Steps.Add(new MacroStep { Kind = "delayRandom", MinMs = 80, MaxMs = 220 });
        _host?.SaveSettings();
        RefreshSteps();
    }

    private void OnInsertText(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        var text = PromptText("要输入什么文本？", "插入文本步骤");
        if (string.IsNullOrEmpty(text)) return;
        _editing.Steps.Add(new MacroStep { Kind = "text", Text = text });
        _host?.SaveSettings();
        RefreshSteps();
    }

    private void OnInsertKeyDown(object sender, RoutedEventArgs e) => InsertKey("keyDown");

    private void OnInsertKeyUp(object sender, RoutedEventArgs e) => InsertKey("keyUp");

    private void InsertKey(string kind)
    {
        if (_editing == null) return;

        // 用捕获窗口拿一个键，Esc 取消
        var dlg = new KeyCaptureWindow { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.CapturedCombo)) return;

        string name = dlg.CapturedCombo;
        _editing.Steps.Add(new MacroStep { Kind = kind, Key = name });
        _host?.SaveSettings();
        RefreshSteps();
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
