using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;
using KeyBinding = CapsLockPlus.Core.Settings.KeyBinding;

namespace CapsLockPlus.UI.Views;

public partial class KeysView : UserControl
{
    private AppHost? _host;
    private KeyModifiers _layer = KeyModifiers.None;
    private string _selectedKey = "q";
    private bool _suppressEvents;

    /// <summary>键盘行定义：每个键是 (显示文字, 键名ID, 宽度权重)。</summary>
    private static readonly (string Label, string? KeyId, double Width)[][] Rows =
    {
        new[]
        {
            ("`", "backquote", 1.0), ("1", "1", 1.0), ("2", "2", 1.0), ("3", "3", 1.0), ("4", "4", 1.0),
            ("5", "5", 1.0), ("6", "6", 1.0), ("7", "7", 1.0), ("8", "8", 1.0), ("9", "9", 1.0),
            ("0", "0", 1.0), ("-", "minus", 1.0), ("=", "equal", 1.0), ("⌫", "backspace", 1.9),
        },
        new[]
        {
            ("Tab", "tab", 1.6), ("Q", "q", 1.0), ("W", "w", 1.0), ("E", "e", 1.0), ("R", "r", 1.0),
            ("T", "t", 1.0), ("Y", "y", 1.0), ("U", "u", 1.0), ("I", "i", 1.0), ("O", "o", 1.0),
            ("P", "p", 1.0), ("[", "leftSquareBracket", 1.0), ("]", "rightSquareBracket", 1.0),
            ("\\", "backslash", 1.3),
        },
        new[]
        {
            ("Caps", null, 1.8), ("A", "a", 1.0), ("S", "s", 1.0), ("D", "d", 1.0), ("F", "f", 1.0),
            ("G", "g", 1.0), ("H", "h", 1.0), ("J", "j", 1.0), ("K", "k", 1.0), ("L", "l", 1.0),
            (";", "semicolon", 1.0), ("'", "quote", 1.0), ("Enter", "enter", 2.1),
        },
        new[]
        {
            ("RAlt", "ralt", 1.2), ("Z", "z", 1.0), ("X", "x", 1.0), ("C", "c", 1.0), ("V", "v", 1.0),
            ("B", "b", 1.0), ("N", "n", 1.0), ("M", "m", 1.0), (",", "comma", 1.0), (".", "dot", 1.0),
            ("/", "slash", 1.0), ("Space", "space", 5.6),
        },
        new[]
        {
            ("F1", "f1", 1.0), ("F2", "f2", 1.0), ("F3", "f3", 1.0), ("F4", "f4", 1.0), ("F5", "f5", 1.0),
            ("F6", "f6", 1.0), ("F7", "f7", 1.0), ("F8", "f8", 1.0), ("F9", "f9", 1.0), ("F10", "f10", 1.0),
            ("F11", "f11", 1.0), ("F12", "f12", 1.0),
        },
    };

    public KeysView() => InitializeComponent();

    /// <summary>
    /// 切到指定层并刷新界面。
    /// 层 ID 1 = 基础层；2 及以上用 Layer{id} 命名保存
    /// （这里沿用现有的"修饰键层"概念：2 = LAlt 层，3 = Win 层）。
    /// </summary>
    public void SelectLayerById(int layerId)
    {
        if (_host == null) return;
        var layer = _host.Settings.Layers.FirstOrDefault(l => l.Id == layerId);
        if (layer == null) return;

        // 前 3 层沿用既有 UI（基础 / LAlt / Win）；更高层暂用基础层 UI 打开
        _layer = layerId switch
        {
            2 => KeyModifiers.Alt,
            3 => KeyModifiers.Win,
            _ => KeyModifiers.None,
        };
        UpdateLayerButtons();
        SelectKey(_selectedKey);
    }

    public void Initialize(AppHost host)
    {
        _host = host;
        BuildKeyboard();
        Refresh();
        // 从层管理页跳过来时，AppHost 会记住目标层，这里应用它
        host.ApplyPendingKeysLayer(this);
    }

    public void Refresh()
    {
        if (_host == null) return;
        _suppressEvents = true;
        SchemeBox.SelectedIndex = _host.Settings.Global.HotkeyScheme == "capslock_plus" ? 1 : 0;

        ActionBox.Items.Clear();
        foreach (var group in _host.Actions.Items.GroupBy(i => i.Category))
        {
            foreach (var item in group)
            {
                ActionBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{group.Key} · {item.Title}",
                    Tag = item.Id,
                });
            }
        }
        _suppressEvents = false;
        BuildKeyboard();
        SelectKey(_selectedKey);
    }

    // ---------------- 键盘绘制 ----------------
    private void BuildKeyboard()
    {
        if (_host == null) return;
        KeyboardPanel.Children.Clear();
        string filter = SearchBox.Text?.Trim() ?? "";
        int bound = 0;

        foreach (var row in Rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            double total = row.Sum(k => k.Width);
            foreach (var (_, _, width) in row)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });

            for (int i = 0; i < row.Length; i++)
            {
                var (label, keyId, _) = row[i];
                if (keyId == null)
                {
                    var caps = MakeCapsKey();
                    Grid.SetColumn(caps, i);
                    grid.Children.Add(caps);
                    continue;
                }

                string combo = KeyName.BuildCombo(_layer, keyId);
                var binding = _host.Settings.FindBinding(combo);
                bool isBound = binding is { IsBound: true };
                if (isBound) bound++;

                string actionTitle = "";
                if (isBound && binding != null)
                    actionTitle = _host.Actions.Get(binding.Action)?.Title ?? binding.Action;

                bool match = filter.Length == 0 ||
                             label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                             keyId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                             actionTitle.Contains(filter, StringComparison.OrdinalIgnoreCase);
                if (!match && filter.Length > 0) continue;

                var cap = MakeKeycap(label, keyId, isBound, actionTitle);
                Grid.SetColumn(cap, i);
                grid.Children.Add(cap);
            }
            KeyboardPanel.Children.Add(grid);
        }

        var tap = _host.Settings.FindBinding("press_caps");
        string tapTitle = tap != null ? _host.Actions.Get(tap.Action)?.Title ?? tap.Action : "无";
        LayerHint.Text = _layer switch
        {
            KeyModifiers.Alt => $"当前：Caps + LAlt 层（{bound} 个绑定）",
            KeyModifiers.Win => $"当前：Caps + Win 层（{bound} 个绑定）",
            _ => $"当前：基础层（{bound} 个绑定）",
        };
        CountText.Text = $"短按 Caps：{tapTitle}　·　点键盘上任意键即可修改它的功能，修改立刻生效。";
        UpdateLayerButtons();
        RefreshBoundList();
    }

    private Border MakeCapsKey()
    {
        var border = new Border
        {
            Height = 52,
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("Brush.Accent"),
            BorderBrush = (Brush)FindResource("Brush.AccentHover"),
            BorderThickness = new Thickness(1),
        };
        border.Child = new TextBlock
        {
            Text = "Caps",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Brush.Surface"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return border;
    }

    private Border MakeKeycap(string label, string keyId, bool isBound, string actionTitle)
    {
        var border = new Border
        {
            Height = 52,
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = isBound ? $"{label} → {actionTitle}" : $"{label}（未绑定）",
            Background = (Brush)FindResource(isBound ? "Brush.AccentSoft" : "Brush.Keycap"),
            BorderBrush = (Brush)FindResource(isBound ? "Brush.AccentSoftBorder" : "Brush.KeycapBorder"),
        };
        if (keyId == _selectedKey)
        {
            border.BorderThickness = new Thickness(2);
            border.BorderBrush = (Brush)FindResource("Brush.Accent");
        }

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource(isBound ? "Brush.Accent" : "Brush.TextSecondary"),
        });
        border.Child = stack;

        string captured = keyId;
        border.MouseLeftButtonUp += (_, _) => SelectKey(captured);
        border.MouseEnter += (_, _) => border.Opacity = 0.8;
        border.MouseLeave += (_, _) => border.Opacity = 1.0;
        return border;
    }

    private static string Shorten(string text) => text.Length <= 6 ? text : text[..6];

    private void UpdateLayerButtons()
    {
        void Apply(Button b, bool active)
        {
            b.Background = (Brush)FindResource(active ? "Brush.AccentSoft" : "Brush.Surface");
            b.BorderBrush = (Brush)FindResource(active ? "Brush.AccentSoftBorder" : "Brush.BorderStrong");
            b.Foreground = (Brush)FindResource(active ? "Brush.Accent" : "Brush.TextSecondary");
            b.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
        Apply(LayerBase, _layer == KeyModifiers.None);
        Apply(LayerAlt, _layer == KeyModifiers.Alt);
        Apply(LayerWin, _layer == KeyModifiers.Win);
    }

    private void RefreshBoundList()
    {
        if (_host == null) return;
        var items = new List<BoundVm>();
        foreach (var binding in _host.Settings.Keys.Where(k => k.IsBound))
        {
            var parsed = KeyName.ParseCombo(binding.Combo);
            if (parsed == null) continue;
            var (mods, key) = parsed.Value;
            if (mods != _layer || key == "capslock") continue;
            items.Add(new BoundVm(key, KeyName.Display(key), _host.Actions.Get(binding.Action)?.Title ?? binding.Action));
        }
        BoundList.ItemsSource = items
            .OrderBy(i => KeyOrder(i.Key))
            .ThenBy(i => i.Key)
            .ToList();
    }

    /// <summary>按键盘上的自然顺序排序，读起来更顺。</summary>
    private static int KeyOrder(string key)
    {
        int idx = KeySequence.IndexOf(key);
        return idx < 0 ? 999 : idx;
    }

    private static readonly string[] KeySequence =
    {
        "q", "w", "e", "r", "t", "y", "u", "i", "o", "p",
        "a", "s", "d", "f", "g", "h", "j", "k", "l",
        "z", "x", "c", "v", "b", "n", "m",
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "0",
        "backquote", "minus", "equal", "leftSquareBracket", "rightSquareBracket", "backslash",
        "semicolon", "quote", "comma", "dot", "slash",
        "backspace", "tab", "enter", "space", "ralt",
        "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12",
        "wheelUp", "wheelDown",
    };

    private void SelectKey(string keyId)
    {
        if (_host == null) return;
        _selectedKey = keyId;
        string combo = KeyName.BuildCombo(_layer, keyId);
        var binding = _host.Settings.FindBinding(combo);

        EditKeyLabel.Text = KeyName.Display(keyId);
        _suppressEvents = true;
        string actionId = binding?.Action ?? "doNothing";
        foreach (ComboBoxItem item in ActionBox.Items)
        {
            if (item.Tag is string tag && tag.Equals(actionId, StringComparison.OrdinalIgnoreCase))
            {
                ActionBox.SelectedItem = item;
                break;
            }
        }
        ArgBox.Text = binding?.Arg ?? "";
        _suppressEvents = false;

        var info = _host.Actions.Get(actionId);
        ActionDesc.Text = info?.Description ?? "";
        bool needsArg = info?.NeedsArg == true || actionId is "moveLeft" or "moveRight" or "moveUp" or "moveDown"
            or "selectLeft" or "selectRight" or "selectUp" or "selectDown" or "moveWordLeft" or "moveWordRight"
            or "selectWordLeft" or "selectWordRight" or "pageMoveLineUp" or "pageMoveLineDown";
        ArgPanel.Visibility = needsArg ? Visibility.Visible : Visibility.Collapsed;
        ArgLabel.Text = actionId switch
        {
            "openUrl" => "网址",
            "runProgram" => "程序路径或命令",
            "wrapSelection" => "起始符号",
            _ => "次数 / 参数",
        };
        ArgHint.Text = actionId switch
        {
            "openUrl" => "例如 https://github.com",
            "runProgram" => "例如 notepad.exe",
            "wrapSelection" => "例如 ( 会把选中文字变成 (文字)",
            _ => "不填默认 1，填 5 表示一次移动 5 个字符",
        };
        BuildKeyboard();
    }

    // ---------------- 事件 ----------------
    private void OnLayerBase(object sender, RoutedEventArgs e) { _layer = KeyModifiers.None; SelectKey(_selectedKey); }
    private void OnLayerAlt(object sender, RoutedEventArgs e) { _layer = KeyModifiers.Alt; SelectKey(_selectedKey); }
    private void OnLayerWin(object sender, RoutedEventArgs e) { _layer = KeyModifiers.Win; SelectKey(_selectedKey); }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => BuildKeyboard();

    private void OnBoundSelected(object sender, SelectionChangedEventArgs e)
    {
        if (BoundList.SelectedItem is BoundVm vm) SelectKey(vm.Key);
    }

    private void OnSchemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        if (SchemeBox.SelectedItem is not ComboBoxItem item || item.Tag is not string scheme) return;
        if (_host.Settings.Global.HotkeyScheme == scheme) return;
        var result = MessageBox.Show(Window.GetWindow(this),
            "切换布局会把当前按键映射替换成该方案的默认值。\n你自己新增的组合键会保留。\n\n继续吗？",
            "切换按键方案", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            _suppressEvents = true;
            SchemeBox.SelectedIndex = _host.Settings.Global.HotkeyScheme == "capslock_plus" ? 1 : 0;
            _suppressEvents = false;
            return;
        }
        _host.ApplyHotkeyScheme(scheme);
        _host.Notify("已切换按键方案");
        Refresh();
    }

    private void OnActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        if (ActionBox.SelectedItem is not ComboBoxItem item || item.Tag is not string actionId) return;
        string combo = CurrentCombo();
        var binding = _host.Settings.FindBinding(combo);
        if (binding == null)
        {
            binding = new KeyBinding { Combo = combo };
            _host.Settings.Keys.Add(binding);
        }
        binding.Action = actionId;
        if (actionId == "doNothing") binding.Arg = "";
        _host.SaveSettings();
        var info = _host.Actions.Get(actionId);
        ActionDesc.Text = info?.Description ?? "";
        SelectKey(_selectedKey);
    }

    private void OnArgChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var binding = _host.Settings.FindBinding(CurrentCombo());
        if (binding == null) return;
        binding.Arg = ArgBox.Text.Trim();
        _host.SaveSettings();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var binding = _host.Settings.FindBinding(CurrentCombo());
        if (binding != null)
        {
            binding.Action = "doNothing";
            binding.Arg = "";
            _host.SaveSettings();
        }
        SelectKey(_selectedKey);
        _host.Notify($"{KeyName.Display(_selectedKey)} 已清除绑定");
    }

    private void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        string combo = KeyName.BuildCombo(_layer, _selectedKey);
        var binding = _host.Settings.FindBinding(combo);
        if (binding == null || !binding.IsBound) { _host.Notify("这个按键还没有绑定功能"); return; }
        _host.InvokeAction(binding.Action, binding.Arg);
    }

    /// <summary>
    /// 自定义按键：弹出捕获窗口，用户按下的键即成为新的绑定目标。
    ///
    /// 键盘上画出来的键之外，也支持小键盘、媒体键、浏览器键等
    /// （这些键的 VK 在 WPF 键盘事件里拿不到，必须走低级键盘钩子）。
    /// </summary>
    /// <summary>
    /// 当前编辑目标对应的 combo ID。
    /// 捕获过自定义组合键时用捕获到的修饰键，否则用当前层的修饰键。
    /// </summary>
    private string CurrentCombo() =>
        _capturedMods == KeyModifiers.None
            ? KeyName.BuildCombo(_layer, _selectedKey)
            : BuildComboFromCapture(_capturedMods, _selectedKey);

    private void OnCustomKeyClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var dlg = new UI.Windows.KeyCaptureWindow
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.CapturedCombo)) return;

        // 组合键要转成引擎认识的 combo ID：
        //   Ctrl+Shift+K + 当前层  ->  caps_ctrl_shift_k
        //   K                      ->  caps_k
        string key = dlg.CapturedKey ?? dlg.CapturedCombo;
        _capturedMods = dlg.CapturedMods;

        SelectKey(key);
        string combo = BuildComboFromCapture(_capturedMods, key);
        ActionDesc.Text = $"已选中自定义组合键「{combo}」，在下面选一个功能即可绑定。";
    }

    /// <summary>当前捕获到的修饰键（配合 SelectKey 使用）。</summary>
    private KeyModifiers _capturedMods;

    /// <summary>
    /// 把捕获窗口给出的 (修饰键, 主键) 转成引擎的组合键 ID。
    ///
    /// 引擎里修饰键原本只有 LAlt / Win 两种（caps_lalt_x / caps_win_x），
    /// 而用户可能捕获到 Ctrl/Shift。这里按顺序拼进 combo ID：
    ///   Caps + Ctrl+Shift+K  ->  caps_ctrl_shift_k
    /// 引擎侧原样查表，所以只要能存进去、查得到就行。
    /// </summary>
    private string BuildComboFromCapture(KeyModifiers mods, string key)
    {
        var parts = new List<string>();
        if (_layer == KeyModifiers.Alt) parts.Add("lalt");
        else if (_layer == KeyModifiers.Win) parts.Add("win");
        if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("ctrl");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("shift");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("lalt");
        if (mods.HasFlag(KeyModifiers.Win)) parts.Add("win");
        parts.Add(key);
        return "caps_" + string.Join("_", parts);
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var result = MessageBox.Show(Window.GetWindow(this),
            "把全部按键恢复成所选方案的默认值？你自定义的组合键会被保留。",
            "恢复默认", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;
        _host.ApplyHotkeyScheme(_host.Settings.Global.HotkeyScheme);
        Refresh();
        _host.Notify("按键映射已恢复默认");
    }

    private sealed record BoundVm(string Key, string KeyLabel, string Action);
}
