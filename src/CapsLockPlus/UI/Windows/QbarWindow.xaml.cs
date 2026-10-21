using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Icons;
using CapsLockPlus.Core.Qbar;
using CapsLockPlus.Core.Windows;
using CapsLockPlus.UI.Theme;

namespace CapsLockPlus.UI.Windows;

public partial class QbarWindow : Window
{
    private readonly AppHost _host;
    private List<QbarItem> _items = new();
    private QbarChip? _chip;
    private bool _closing;
    private bool _suppressTextChanged;

    public QbarWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Deactivated += (_, _) => HideQbar();
        PreviewKeyDown += OnWindowKeyDown;
        Results.SelectionChanged += (_, _) => UpdateHint();
        ApplyStyle();
    }

    private SolidColorBrush? _panelBrush;
    private SolidColorBrush? _borderBrush;
    private string _styleStamp = "";

    private void ApplyStyle()
    {
        var q = _host.Settings.Qbar;
        try
        {
            // 只有设置真的变了才重建画笔：Qbar 是高频开关的面板，
            // 每次 Open 都 new 一次 SolidColorBrush 属于无谓开销。
            string stamp = $"{q.AccentColor}|{q.PanelColor}|{q.PanelOpacity}|{q.TextFontSize}|{q.LineHeight}|{q.ListCount}";
            if (stamp == _styleStamp) return;
            _styleStamp = stamp;

            var accent = (Color)ColorConverter.ConvertFromString(q.AccentColor);
            _borderBrush = new SolidColorBrush(accent);
            _borderBrush.Freeze();
            Panel.BorderBrush = _borderBrush;

            var panelColor = (Color)ColorConverter.ConvertFromString(q.PanelColor);
            byte alpha = (byte)(double.TryParse(q.PanelOpacity, out double op) ? Math.Clamp(op, 0.3, 1) * 255 : 245);
            _panelBrush = new SolidColorBrush(Color.FromArgb(alpha, panelColor.R, panelColor.G, panelColor.B));
            _panelBrush.Freeze();
            Panel.Background = _panelBrush;

            Input.FontSize = q.TextFontSize;
            // 字号变大时必须同步抬高输入框，否则上下缘会被裁掉。
            // 用真实字形高度来算（不是估系数），再留出安全余量。
            Input.MinHeight = Core.Text.TextMetrics.MinBoxHeight(q.TextFontSize, "汉字Agqy");
            Results.MaxHeight = q.LineHeight * q.ListCount + 12;
            SizeToContent = SizeToContent.Height;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("Qbar 样式应用失败：" + ex.Message);
        }
    }

    /// <summary>设置页改了外观后调用，强制下次打开重建画笔。</summary>
    public void InvalidateStyle() => _styleStamp = "";

    public void Open(string? prefill = null)
    {
        ApplyStyle();
        ClearChip();
        if (!IsVisible)
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + wa.Height * 0.22;
            Show();
        }
        Activate();
        Topmost = true;
        Focus();

        /* prefill 有两种形态：
         *   ① "bd "           —— 引擎关键字（由 qbar:<key> 动作传入）
         *   ② "要搜索的内容"   —— 普通预填文本
         *
         * 形态 ① 必须**立刻变成标签**，否则它只是输入框里的一段文字：
         * 用户按 Caps+G 打开却看到输入框写着 "go "，还得自己再敲个空格
         * 才变标签 —— 等于"绑定没生效"。这里直接判定并转换。 */
        string pre = prefill ?? "";
        SetInputText(pre);
        if (pre.Length > 0 && TryMakeChip())
        {
            // 已变成引擎标签：清空输入，让用户直接打关键词
            SetInputText("");
            RunQuery();
        }
        else if (pre.Length == 0)
        {
            RunQuery();
        }
        else
        {
            RunQuery();       // 普通预填：按内容正常搜
        }

        Input.Focus();
        Keyboard.Focus(Input);
        PlayShowAnimation();
    }

    /// <summary>
    /// 离屏渲染用：打开并把某个关键字直接变成标签（相当于用户输入 "bd " 之后的状态）。
    /// </summary>
    public void OpenForRender(string keyword, string? tail = null)
    {
        Open("");
        SetInputText(keyword);
        if (!TryMakeChip()) RunQuery();
        if (!string.IsNullOrEmpty(tail)) SetInputText(tail);
        RunQuery();
    }

    private void PlayShowAnimation()
    {
        Panel.BeginAnimation(OpacityProperty, null);
        Panel.Opacity = 1;
        var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Slide.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    public void HideQbar()
    {
        if (!IsVisible || _closing) return;
        _closing = true;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90));
        fade.Completed += (_, _) =>
        {
            Hide();
            Panel.BeginAnimation(OpacityProperty, null);
            Panel.Opacity = 1;
            _closing = false;
        };
        Panel.BeginAnimation(OpacityProperty, fade);
    }

    // ---------------- 标签（Chip） ----------------

    /// <summary>
    /// 把 "bd" 变成标签。触发时机：输入空格 / Tab 时，
    /// 且当前整段文本恰好是一个已定义的关键字。
    /// </summary>
    private bool TryMakeChip()
    {
        if (_chip != null) return false;
        string text = Input.Text.Trim();
        if (text.Length == 0) return false;
        var chip = _host.Qbar.ResolveChip(text);
        if (chip == null) return false;
        SetChip(chip);
        SetInputText("");
        return true;
    }

    private void SetChip(QbarChip chip)
    {
        _chip = chip;
        ChipLabel.Text = chip.Label;

        if (chip.HasIcon)
        {
            ChipIcon.Source = chip.Icon;
            ChipIconBox.Visibility = Visibility.Visible;
            ChipInitialBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            ChipIcon.Source = null;
            ChipIconBox.Visibility = Visibility.Collapsed;
            ChipInitialBox.Visibility = Visibility.Visible;
            ChipInitial.Text = chip.Initial;
            ChipInitialBox.Background = new SolidColorBrush(chip.Accent);
        }

        ChipBox.Visibility = Visibility.Visible;
        ModeIcon.Text = chip.Kind switch
        {
            ChipKind.Search => "\uE721",
            ChipKind.Web => "\uE774",
            _ => "\uE756",
        };
        UpdateMode();
    }

    private void ClearChip()
    {
        _chip = null;
        ChipBox.Visibility = Visibility.Collapsed;
        ChipIcon.Source = null;
        ModeIcon.Text = "\uE721";
    }

    /// <summary>移除标签，并把关键字放回输入框，方便直接改。</summary>
    private void RemoveChip(bool restoreText = true)
    {
        if (_chip == null) return;
        string key = _chip.Key;
        ClearChip();
        if (restoreText) SetInputText(key);
        else RunQuery();
        Input.Focus();
        Input.CaretIndex = Input.Text.Length;
    }

    private void OnChipClose(object sender, RoutedEventArgs e) => RemoveChip();

    private void SetInputText(string text)
    {
        _suppressTextChanged = true;
        Input.Text = text;
        Input.CaretIndex = text.Length;
        _suppressTextChanged = false;
    }

    // ---------------- 输入与检索 ----------------

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideQbar(); e.Handled = true; }
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideQbar();
                e.Handled = true;
                break;
            case Key.Space:
                // 空格：如果整段正好是关键字，就转成标签（之后空格用于分词）
                if (TryMakeChip()) { e.Handled = true; break; }
                break;
            case Key.Tab:
                if (TryMakeChip()) { e.Handled = true; break; }
                CompleteSelected();
                e.Handled = true;
                break;
            case Key.Back:
                // 输入框已空时按退格 → 把标签还原成文字
                if (_chip != null && Input.Text.Length == 0)
                {
                    RemoveChip();
                    e.Handled = true;
                }
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveSelection(5);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveSelection(-5);
                e.Handled = true;
                break;
            case Key.Enter:
                ExecuteSelected();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_items.Count == 0) return;
        int idx = Math.Clamp(Results.SelectedIndex + delta, 0, _items.Count - 1);
        Results.SelectedIndex = idx;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void CompleteSelected()
    {
        if (Results.SelectedItem is not QbarItem item) return;
        // 补全成“关键字 + 空格”，下一次按键即可变成标签
        string completion = item.Result.Kind switch
        {
            QbarKind.Run or QbarKind.Web or QbarKind.Search when item.Result.Badge.Length > 0
                => item.Result.Badge + " ",
            _ => item.Result.Title,
        };
        SetInputText(completion);
        if (TryMakeChip()) RunQuery();
        else RunQuery();
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;
        RunQuery();
    }

    private void RunQuery()
    {
        var results = _host.Qbar.Query(Input.Text, _chip, Math.Max(6, _host.Settings.Qbar.ListCount + 2));
        _items = results.Select(r => new QbarItem(r, _host.Settings.Qbar.ShowIcons)).ToList();
        Results.ItemsSource = _items;
        if (_items.Count > 0) Results.SelectedIndex = 0;
        UpdateMode();
        UpdateHint();
    }

    private void UpdateMode()
    {
        if (_chip != null)
        {
            HintText.Text = _chip.Kind switch
            {
                ChipKind.Search => "Enter 搜索",
                ChipKind.Web => "Enter 打开",
                _ => "Enter 启动",
            };
            return;
        }
        string text = Input.Text;
        string hint = "Enter 执行";
        if (text.StartsWith('=')) hint = "Enter 复制结果";
        else if (text.StartsWith('/')) hint = "内置命令";
        else if (text.Contains(' ') && _host.Settings.Search.Any(s =>
                     s.Key.Equals(text.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
            hint = "搜索";
        else if (text.Length > 0 && QbarService.LooksLikeMath(text)) hint = "计算";
        HintText.Text = hint;
    }

    private void UpdateHint()
    {
        if (Results.SelectedItem is not QbarItem item) return;
        HintText.Text = item.Result.Kind switch
        {
            QbarKind.Math => "Enter 复制结果",
            QbarKind.Clipboard => "Enter 粘贴",
            QbarKind.Window => "Enter 切换窗口",
            QbarKind.Command => "Enter 执行",
            QbarKind.Search => _chip != null ? "Enter 搜索" : "Enter 打开搜索",
            QbarKind.Web => "Enter 打开网址",
            QbarKind.Run => "Enter 启动",
            _ => "Enter 执行",
        };
    }

    private void ExecuteSelected()
    {
        if (Results.SelectedItem is not QbarItem item) return;
        var result = item.Result;
        HideQbar();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var cb = new QbarService.QbarCallbacks(
                    Notify: _host.Notify,
                    CopyToClipboard: text =>
                    {
                        try { System.Windows.Clipboard.SetText(text); } catch { }
                    },
                    Paste: text => _host.Clipboard.PasteText(text),
                    OpenTarget: OpenTarget,
                    RunTarget: RunTarget,
                    RunCommand: RunCommand);
                _host.Qbar.Execute(result, cb);
            }
            catch (Exception ex)
            {
                CoreLog.Error("Qbar 执行失败", ex);
                _host.Notify("执行失败：" + ex.Message);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OpenTarget(string target)
    {
        if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            _host.Context.OpenUrl(target);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.Notify("打开失败：" + ex.Message);
        }
    }

    private void RunTarget(string target, bool asAdmin, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                Arguments = args,
            };
            if (asAdmin) psi.Verb = "runas";
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _host.Notify("启动失败：" + ex.Message);
        }
    }

    private void RunCommand(string command)
    {
        switch (command)
        {
            case "openSettings": _host.ShowMainWindow("settings"); break;
            case "openBindings": _host.ShowMainWindow("bindings"); break;
            case "openHelp": _host.ShowMainWindow("help"); break;
            case "openClipboard": _host.ShowClipboardHistory(); break;
            case "openMath": _host.ShowMathBoard(); break;
            case "reload": _host.ReloadAll(); break;
            case "toggleEnabled": _host.ToggleEnabled(); break;
            case "exit": _host.ExitApp(); break;
        }
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<ListBoxItem>(d) != null)
            ExecuteSelected();
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => ExecuteSelected();

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private sealed class QbarItem
    {
        public QbarItem(QbarResult result, bool showIcons = true)
        {
            Result = result;
            var chip = result.Chip;

            /* 「显示图标」这个设置项之前**没人读** ——
             * 界面上能勾掉，但列表照样显示图标，属于典型表面实现。
             * 现在关掉时只显示文字，不再画图标。 */
            if (!showIcons)
            {
                // 什么都不画，靠文本区分
            }
            else if (chip is { HasIcon: true })
            {
                IconSource = chip.Icon;
                HasIcon = true;
            }
            else if (chip != null)
            {
                Letter = chip.Initial;
                LetterBrush = new SolidColorBrush(chip.Accent);
                HasLetter = true;
            }

            IconGlyph = result.Kind switch
            {
                QbarKind.Search => "\uE721",
                QbarKind.Web => "\uE774",
                QbarKind.Run => "\uE756",
                QbarKind.Window => "\uE737",
                QbarKind.Math => "\uE8EF",
                QbarKind.Clipboard => "\uE77F",
                QbarKind.File => "\uE8B7",
                _ => "\uE756",
            };
            Badge = result.Kind == QbarKind.Command ? result.Payload : result.KindLabel;
        }

        public QbarResult Result { get; }
        public string IconGlyph { get; }
        public string Badge { get; }
        public ImageSource? IconSource { get; }
        public bool HasIcon { get; }
        public bool HasLetter { get; }
        public string Letter { get; } = "";
        public Brush LetterBrush { get; } = Brushes.Transparent;
        public string Title => Result.Title;
        public string Subtitle => Result.Subtitle;
    }
}
