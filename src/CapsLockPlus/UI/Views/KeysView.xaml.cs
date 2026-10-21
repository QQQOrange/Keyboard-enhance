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

    /// <summary>当前编辑的层 ID（1~10）。</summary>
    private int _layerId = 1;

    /// <summary>层内的修饰键子层：直接按 / LAlt / Win。</summary>
    private KeyModifiers _layer = KeyModifiers.None;

    private string _selectedKey = "q";
    private bool _suppressEvents;

    /// <summary>
    /// 当前层的绑定列表。
    ///
    /// 绑定是按层分开存的：第 1 层就是历史遗留的 <c>Settings.Keys</c>，
    /// 其余层各自有 <c>Layers[i].Keys</c>。所有查找都必须走这里，
    /// 否则在第 4~10 层编辑时会误改第 1 层。
    /// </summary>
    private List<KeyBinding> CurrentLayerKeys
    {
        get
        {
            if (_host == null) return new List<KeyBinding>();
            var layer = _host.Settings.Layers.FirstOrDefault(l => l.Id == _layerId);
            if (layer != null) return layer.Keys;
            // 老设置没有层结构时，Keys 即第 1 层
            return _layerId == 1 ? _host.Settings.Keys : new List<KeyBinding>();
        }
    }

    /// <summary>在当前层里找绑定（大小写不敏感）。</summary>
    private KeyBinding? FindInCurrentLayer(string combo) =>
        CurrentLayerKeys.FirstOrDefault(k =>
            string.Equals(k.Combo, combo, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 标准 ANSI 108 键布局。
    ///
    /// 每个键 = (显示文字, 键名ID, 宽度权重)；宽度 1.0 = 一个标准单位。
    /// 跨行的大键（Enter / Shift / Space / 小键盘回车 / 小键盘 0）按
    /// 真实物理宽度给权重，其余严格按 108 键 ANSI 标准排布：
    ///
    ///   ┌ Esc ─┬ F1 F2 F3 F4 ┬ F5 F6 F7 F8 ┬ F9 F10 F11 F12 ┐ PrtSc ScrLk Pause
    ///   │ ` 1…0 - = ⌫ ×2 ─ Ins Home PgUp ─ Num / * -
    ///   │ Tab ×1.5 Q…P [ ] \ ×1.5 ─ Del End PgDn ─ 7 8 9 (+)
    ///   │ Caps ×1.75 A…L ; ' Enter ×2.25 ─┐       4 5 6 ┐
    ///   │ Shift ×2.25 Z…M , . / ─ Shift ×2.75 │ ↑ │   1 2 3 ┐Enter×2
    ///   │ Ctrl Win Alt Space×6.25 Alt Win ☰ Ctrl │ ← ↓ → │ 0×2.25 .
    ///
    /// 用 <c>Pad</c> 辅助把每行补齐到相同总宽度，不靠手写占位键 ——
    /// 占位键之前把小键盘列数搞乱，是上一版布局看起来乱的直接原因。
    /// </summary>
    private static readonly (string Label, string? KeyId, double Width)[][] Main =
    {
        // ① 功能键行（ANSI：Esc 单独一排，F1~F12 四组，Printer 键三个）
        new[]
        {
            ("Esc", "esc", 3.0), ("F1", "f1", 1.0), ("F2", "f2", 1.0), ("F3", "f3", 1.0), ("F4", "f4", 1.0),
            ("F5", "f5", 1.0), ("F6", "f6", 1.0), ("F7", "f7", 1.0), ("F8", "f8", 1.0),
            ("F9", "f9", 1.0), ("F10", "f10", 1.0), ("F11", "f11", 1.0), ("F12", "f12", 1.0),
            ("PrtSc", "printScreen", 1.0), ("ScrLk", "scrollLock", 1.0), ("Pause", "pause", 1.0),
        },
        // ② 数字行
        new[]
        {
            ("`", "backquote", 1.0), ("1", "1", 1.0), ("2", "2", 1.0), ("3", "3", 1.0), ("4", "4", 1.0),
            ("5", "5", 1.0), ("6", "6", 1.0), ("7", "7", 1.0), ("8", "8", 1.0), ("9", "9", 1.0),
            ("0", "0", 1.0), ("-", "minus", 1.0), ("=", "equal", 1.0), ("⌫", "backspace", 2.0),
        },
        // ③ Tab 行
        new[]
        {
            ("Tab", "tab", 1.5), ("Q", "q", 1.0), ("W", "w", 1.0), ("E", "e", 1.0), ("R", "r", 1.0),
            ("T", "t", 1.0), ("Y", "y", 1.0), ("U", "u", 1.0), ("I", "i", 1.0), ("O", "o", 1.0),
            ("P", "p", 1.0), ("[", "leftSquareBracket", 1.0), ("]", "rightSquareBracket", 1.0),
            ("\\", "backslash", 1.5),
        },
        // ④ Caps 行
        new[]
        {
            ("Caps", "capslock", 1.75), ("A", "a", 1.0), ("S", "s", 1.0), ("D", "d", 1.0), ("F", "f", 1.0),
            ("G", "g", 1.0), ("H", "h", 1.0), ("J", "j", 1.0), ("K", "k", 1.0), ("L", "l", 1.0),
            (";", "semicolon", 1.0), ("'", "quote", 1.0), ("Enter", "enter", 2.25),
        },
        // ⑤ Shift 行
        new[]
        {
            ("LShift", "lshift", 2.25), ("Z", "z", 1.0), ("X", "x", 1.0), ("C", "c", 1.0), ("V", "v", 1.0),
            ("B", "b", 1.0), ("N", "n", 1.0), ("M", "m", 1.0), (",", "comma", 1.0), (".", "dot", 1.0),
            ("/", "slash", 1.0), ("RShift", "rshift", 2.75),
        },
        // ⑥ 底行
        new[]
        {
            ("LCtrl", "lctrl", 1.25), ("LWin", "lwin", 1.25), ("LAlt", "lalt", 1.25), ("", "space", 6.25),
            ("RAlt", "ralt", 1.25), ("RWin", "rwin", 1.25), ("☰", "apps", 1.25), ("RCtrl", "rctrl", 1.25),
        },
    };

    /// <summary>编辑键簇（垂直排，与主键区右侧对齐；null 行是空隙）。</summary>
    private static readonly (string Label, string? KeyId)[][] Edit =
    {
        new[] { ("", (string?)null) },
        new[] { ("Ins", "insert"), ("Home", "home"), ("PgUp", "pageUp") },
        new[] { ("Del", "delete"), ("End", "end"), ("PgDn", "pageDown") },
        new[] { ("", (string?)null) },
        new[] { ("", (string?)null), ("↑", "up"), ("", (string?)null) },
        new[] { ("←", "left"), ("↓", "down"), ("→", "right") },
    };

    /// <summary>主键区 + 编辑键簇的标准总宽度（14 单位 + 3 单位）。</summary>
    private const double MainRowWidth = 14.0;
    private const double EditClusterWidth = 3.0;


    public KeysView() => InitializeComponent();

    /// <summary>
    /// 切到指定层并刷新界面（从「触发键与层」页跳过来时用）。
    /// </summary>
    public void SelectLayerById(int layerId)
    {
        if (_host == null) return;
        if (_host.Settings.Layers.All(l => l.Id != layerId)) return;

        _layerId = layerId;
        _layer = KeyModifiers.None;      // 切层时回到"直接按"子层
        BuildLayerBox();
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

        BuildCategoryBox();
        _suppressEvents = false;
        BuildLayerBox();
        BuildKeyboard();
        SelectKey(_selectedKey);
    }

    /// <summary>
    /// 填"分类"下拉。分类来自动作表本身，所以加了宏就会多出「宏」这一类。
    /// 顺序是按动作表里的出现顺序，宏排在最后（它是用户自己的东西）。
    /// </summary>
    private void BuildCategoryBox()
    {
        if (_host == null) return;
        string? keep = (CategoryBox.SelectedItem as ComboBoxItem)?.Tag as string;

        CategoryBox.Items.Clear();
        foreach (var cat in _host.Actions.Items.Select(i => i.Category).Distinct())
            CategoryBox.Items.Add(new ComboBoxItem { Content = cat, Tag = cat });

        // 尽量保留用户原来选的分类
        foreach (ComboBoxItem it in CategoryBox.Items)
        {
            if (it.Tag as string == keep) { CategoryBox.SelectedItem = it; break; }
        }
        if (CategoryBox.SelectedIndex < 0 && CategoryBox.Items.Count > 0)
            CategoryBox.SelectedIndex = 0;

        BuildActionBox();
    }

    /// <summary>填"功能"下拉，只列当前分类下的动作。</summary>
    private void BuildActionBox()
    {
        if (_host == null) return;
        string? cat = (CategoryBox.SelectedItem as ComboBoxItem)?.Tag as string;

        ActionBox.Items.Clear();
        foreach (var item in _host.Actions.Items.Where(i => i.Category == cat))
            ActionBox.Items.Add(new ComboBoxItem { Content = item.Title, Tag = item.Id });

        if (ActionBox.Items.Count > 0) ActionBox.SelectedIndex = 0;
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        _suppressEvents = true;
        BuildActionBox();
        _suppressEvents = false;
    }

    // ---------------- 键盘绘制 ----------------

    /// <summary>当前层的触发键名（用于在键盘真实位置高亮它）。</summary>
    private string? CurrentTriggerKey =>
        _host?.Settings.Triggers.FirstOrDefault(t => t.LayerId == _layerId && t.Enabled)?.Key;

    private void BuildKeyboard()
    {
        if (_host == null) return;
        KeyboardPanel.Children.Clear();
        string filter = SearchBox.Text?.Trim() ?? "";
        int bound = 0;

        /* 触发键在键盘上的**真实位置**高亮。
         *
         * 之前写死显示在左上角（Caps 位），用户把 F13 绑到第 3 层时，
         * 切过去亮的还是 Caps 位 —— 符号换了但位置没换，跟配置对不上。
         * 现在：当前层的触发键是哪个键，就高亮键盘上那个键本身。 */
        string? triggerKey = CurrentTriggerKey;

        // 外层：主键区 + 右侧竖排（编辑键簇 / 小键盘）
        var outer = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        outer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(MainRowWidth + EditClusterWidth, GridUnitType.Star),
        });
        outer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(4.0, GridUnitType.Star),
        });

        // ---- 左：主键区 + 编辑键簇 ----
        var left = new Grid();
        left.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(MainRowWidth, GridUnitType.Star),
        });
        left.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(EditClusterWidth, GridUnitType.Star),
        });

        for (int r = 0; r < Main.Length; r++)
        {
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = new Grid();
            Grid.SetRow(row, r);
            Grid.SetColumn(row, 0);
            foreach (var (_, _, w) in Main[r])
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });

            for (int i = 0; i < Main[r].Length; i++)
            {
                var (label, keyId, w) = Main[r][i];
                if (keyId == null) continue;

                var cap = BuildKeycapEntry(label, keyId, filter, ref bound, triggerKey);
                if (cap == null) continue;                       // 被搜索过滤掉
                Grid.SetColumn(cap, i);
                row.Children.Add(cap);
            }
            left.Children.Add(row);
        }
        outer.Children.Add(left);

        // 编辑键簇（垂直排在与主键区同一栅格的第 1 列）
        var editPanel = new Grid();
        Grid.SetColumn(editPanel, 1);
        for (int r = 0; r < Edit.Length; r++)
        {
            editPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = Edit[r];
            // 单格占位行：第一行很短（与功能键行错开），其余行是正常键高
            if (keys.Length == 1 && keys[0].Item2 == null)
            {
                editPanel.RowDefinitions[^1] = new RowDefinition
                { Height = new GridLength(r == 0 ? 20 : 52) };
                continue;
            }
            for (int c = 0; c < keys.Length; c++)
            {
                var (label, keyId) = keys[c];
                if (keyId == null) continue;
                var cap = BuildKeycapEntry(label, keyId, filter, ref bound, triggerKey);
                if (cap == null) continue;
                Grid.SetRow(cap, r);
                Grid.SetColumn(cap, c);
                editPanel.Children.Add(cap);
            }
        }
        left.Children.Add(editPanel);

        // ---- 右：小键盘（4 列宽，含 + 键与跨两格的回车） ----
        // 标准小键盘：第 1 行 Num/÷/×/−，2~4 行数字，第 5 行 0(跨两格) 与 .
        string[][] fixedRows =
        {
            new[] { "numLock", "numDivide", "numMultiply", "numSubtract" },
            new[] { "num7", "num8", "num9", "numAdd" },
            new[] { "num4", "num5", "num6", "" },                 // 第 4 列由回转车占（跨两行）
            new[] { "num1", "num2", "num3", "" },
            new[] { "num0", "numDecimal" },
        };

        var num = new Grid();
        Grid.SetColumn(num, 1);
        num.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });  // 与功能键行错开
        foreach (var _ in fixedRows)
            num.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int r = 0; r < fixedRows.Length; r++)
        {
            var keys = fixedRows[r];
            for (int c = 0; c < keys.Length; c++)
            {
                string keyId = keys[c];
                if (keyId.Length == 0) continue;

                string label = keyId switch
                {
                    "numLock" => "Num",
                    "numDivide" => "/",
                    "numMultiply" => "*",
                    "numSubtract" => "-",
                    "numAdd" => "+",
                    "numDecimal" => ".",
                    "num0" => "0",
                    _ => keyId[^1].ToString(),
                };

                var cap = BuildKeycapEntry(label, keyId, filter, ref bound, triggerKey);
                if (cap == null) continue;
                Grid.SetRow(cap, r + 1);
                Grid.SetColumn(cap, c);
                if (keyId == "num0")
                {
                    Grid.SetColumnSpan(cap, 2);      // 小键盘 0 跨两格
                }
                num.Children.Add(cap);
            }
        }

        // 小键盘回车（跨两行）+ 上下键（把方向键 ↑ ↓ 也放进来更完整）
        var numEnter = BuildKeycapEntry("Enter", "enter", filter, ref bound, triggerKey);
        if (numEnter != null)
        {
            Grid.SetRow(numEnter, 3);
            Grid.SetColumn(numEnter, 3);
            Grid.SetRowSpan(numEnter, 2);
            num.Children.Add(numEnter);
        }
        outer.Children.Add(num);

        KeyboardPanel.Children.Add(outer);

        // ---- 状态文字 ----
        var tap = FindInCurrentLayer("press_caps");
        string tapTitle = tap != null ? _host.Actions.Get(tap.Action)?.Title ?? tap.Action : "无";
        var layerObj = _host.Settings.Layers.FirstOrDefault(l => l.Id == _layerId);
        string layerName = layerObj?.Name ?? $"第 {_layerId} 层";
        var trigger = _host.Settings.Triggers.FirstOrDefault(t => t.LayerId == _layerId);
        string triggerText = trigger != null
            ? $"触发键 {KeyName.Display(trigger.Key)}（键盘上已高亮）"
            : "（没有触发键指向这一层）";

        string modText = _layer switch
        {
            KeyModifiers.Ctrl => " + Ctrl",
            KeyModifiers.Shift => " + Shift",
            KeyModifiers.Alt => " + LAlt",
            KeyModifiers.Win => " + Win",
            _ => "",
        };
        LayerHint.Text = $"当前：第 {_layerId} 层「{layerName}」{modText}（{bound} 个绑定）　·　{triggerText}";

        CountText.Text = _layerId == 1
            ? $"短按触发键：{tapTitle}　·　点键盘上任意键即可修改它的功能，修改立刻生效。"
            : $"这一层的按键绑定独立于其他层。要改短按行为，去「触发键与层」页。";
        UpdateLayerButtons();
        RefreshBoundList();
    }

    /// <summary>
    /// 建一个键帽；被搜索过滤掉时返回 null。
    /// </summary>
    private Border? BuildKeycapEntry(string label, string keyId, string filter,
        ref int bound, string? triggerKey)
    {
        string combo = KeyName.BuildComboEx(_layer, keyId);
        var binding = FindInCurrentLayer(combo);
        bool isBound = binding is { IsBound: true };
        if (isBound) bound++;

        string actionTitle = "";
        if (isBound && binding != null)
        {
            actionTitle = _host?.Actions.Get(binding.Action)?.Title ?? binding.Action;
            if (actionTitle.StartsWith("播放宏：", StringComparison.OrdinalIgnoreCase))
                actionTitle = "▶ " + actionTitle["播放宏：".Length..];
        }

        bool match = filter.Length == 0 ||
                     label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                     keyId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                     actionTitle.Contains(filter, StringComparison.OrdinalIgnoreCase);
        if (!match && filter.Length > 0) return null;

        return MakeKeycap(label, keyId, isBound, actionTitle, triggerKey);
    }

    /// <summary>把层下拉框填上（10 个层，标注各自的触发键）。</summary>
    private void BuildLayerBox()
    {
        if (_host == null) return;
        _suppressEvents = true;
        LayerBox.Items.Clear();
        foreach (var layer in _host.Settings.Layers.OrderBy(l => l.Id))
        {
            var trigger = _host.Settings.Triggers.FirstOrDefault(t => t.LayerId == layer.Id);
            string suffix = trigger != null
                ? $"　← 长按 {KeyName.Display(trigger.Key)}"
                : "　（未分配触发键）";
            LayerBox.Items.Add(new ComboBoxItem
            {
                Content = $"{layer.Id}. {layer.Name}{suffix}",
                Tag = layer.Id,
            });
            if (layer.Id == _layerId) LayerBox.SelectedIndex = LayerBox.Items.Count - 1;
        }
        if (LayerBox.SelectedIndex < 0 && LayerBox.Items.Count > 0) LayerBox.SelectedIndex = 0;
        _suppressEvents = false;
    }

    private void OnLayerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        if (LayerBox.SelectedItem is not ComboBoxItem { Tag: int id }) return;
        _layerId = id;
        SelectKey(_selectedKey);
    }

    /// <summary>
    /// 建一个键帽。
    /// </summary>
    /// <param name="triggerKey">当前层的触发键名（非 null 时该键会额外高亮 +
    /// 加"触发键"角标，用户一眼看到"我按哪个键进这一层"）。</param>
    private Border MakeKeycap(string label, string keyId, bool isBound,
        string actionTitle, string? triggerKey)
    {
        // 触发键在键盘**真实位置**高亮（不再写死在左上角）
        bool isTrigger = triggerKey != null
                         && string.Equals(triggerKey, keyId, StringComparison.OrdinalIgnoreCase);

        var border = new Border
        {
            Height = 52,
            Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(isTrigger ? 2 : 1),
            Cursor = Cursors.Hand,
            ToolTip = isTrigger
                ? $"★ {label} = 本层触发键\n长按它进入这一层；短按执行短按动作"
                : isBound ? $"{label} → {actionTitle}" : $"{label}（未绑定）",
            Background = (Brush)FindResource(isTrigger ? "Brush.Accent"
                : isBound ? "Brush.AccentSoft" : "Brush.Keycap"),
            BorderBrush = (Brush)FindResource(isTrigger ? "Brush.AccentHover"
                : isBound ? "Brush.AccentSoftBorder" : "Brush.KeycapBorder"),
        };
        if (keyId == _selectedKey && !isTrigger)
        {
            border.BorderThickness = new Thickness(2);
            border.BorderBrush = (Brush)FindResource("Brush.Accent");
        }

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = isTrigger ? "★ " + label : label,
            FontSize = label.Length > 4 ? 10 : 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource(isTrigger ? "Brush.Surface"
                : isBound ? "Brush.Accent" : "Brush.TextSecondary"),
        });
        // 已绑定时把功能名缩略显示在键帽下方，一眼看到"这个键当前干什么"
        if (isBound && !string.IsNullOrEmpty(actionTitle) && !isTrigger)
        {
            stack.Children.Add(new TextBlock
            {
                Text = Shorten(actionTitle),
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("Brush.TextTertiary"),
            });
        }
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
        Apply(LayerCtrl, _layer == KeyModifiers.Ctrl);
        Apply(LayerShift, _layer == KeyModifiers.Shift);
        Apply(LayerAlt, _layer == KeyModifiers.Alt);
        Apply(LayerWin, _layer == KeyModifiers.Win);
    }

    private void RefreshBoundList()
    {
        if (_host == null) return;
        var items = new List<BoundVm>();
        foreach (var binding in CurrentLayerKeys.Where(k => k.IsBound))
        {
            var parsed = KeyName.ParseCombo(binding.Combo);
            if (parsed == null) continue;
            var (mods, key) = parsed.Value;
            if (key == "capslock") continue;
            // 不再按修饰键过滤：这一层里所有绑定都要列出来，
            // 否则用自定义按键绑的 Ctrl+Shift+K 之类就"看不见"了。
            string label = mods == KeyModifiers.None
                ? KeyName.Display(key)
                : $"{ModifierText(mods)} {KeyName.Display(key)}";
            items.Add(new BoundVm(mods, key, label,
                _host.Actions.Get(binding.Action)?.Title ?? binding.Action));
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
        string combo = CurrentCombo();
        var binding = FindInCurrentLayer(combo);

        EditKeyLabel.Text = KeyName.Display(keyId);
        _suppressEvents = true;

        string actionId = binding?.Action ?? "doNothing";

        // 两级下拉：先按动作找到它所属分类，切过去，再在分类里选中它
        var info = _host.Actions.Get(actionId);
        if (info != null)
        {
            foreach (ComboBoxItem it in CategoryBox.Items)
            {
                if (it.Tag as string == info.Category)
                {
                    if (!ReferenceEquals(CategoryBox.SelectedItem, it))
                    {
                        CategoryBox.SelectedItem = it;
                        BuildActionBox();
                    }
                    break;
                }
            }
        }

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
    private void OnLayerBase(object sender, RoutedEventArgs e) => SelectModifierLayer(KeyModifiers.None);
    private void OnLayerCtrl(object sender, RoutedEventArgs e) => SelectModifierLayer(KeyModifiers.Ctrl);
    private void OnLayerShift(object sender, RoutedEventArgs e) => SelectModifierLayer(KeyModifiers.Shift);
    private void OnLayerAlt(object sender, RoutedEventArgs e) => SelectModifierLayer(KeyModifiers.Alt);
    private void OnLayerWin(object sender, RoutedEventArgs e) => SelectModifierLayer(KeyModifiers.Win);

    /// <summary>层内修饰键切换后要刷新（键盘上的绑定标记会变）。</summary>
    private void SelectModifierLayer(KeyModifiers mods)
    {
        _layer = mods;
        SelectKey(_selectedKey);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => BuildKeyboard();

    private void OnBoundSelected(object sender, SelectionChangedEventArgs e)
    {
        // 点列表里的条目要把**修饰键一起**还原，否则点了 Ctrl+Shift+K
        // 只会选中 K，编辑的其实是另一个绑定。
        if (BoundList.SelectedItem is BoundVm vm)
        {
            _layer = vm.Mods;
            SelectKey(vm.Key);
        }
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
        var binding = FindInCurrentLayer(combo);
        if (binding == null)
        {
            // 必须加进**当前层**的列表。写进 Settings.Keys 会让
            // 第 2~10 层新建的绑定跑到第 1 层去（这个 bug 之前就是这样）。
            binding = new KeyBinding { Combo = combo };
            CurrentLayerKeys.Add(binding);
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
        var binding = FindInCurrentLayer(CurrentCombo());
        if (binding == null) return;
        binding.Arg = ArgBox.Text.Trim();
        _host.SaveSettings();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var binding = FindInCurrentLayer(CurrentCombo());
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
        string combo = CurrentCombo();
        var binding = FindInCurrentLayer(combo);
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
    ///
    /// 层内修饰键（<see cref="_layer"/>）就是组合键的修饰部分 ——
    /// 自定义按键捕获到 Ctrl+Shift+K 时，直接把 `_layer` 设成 Ctrl|Shift、
    /// 把 `_selectedKey` 设成 "k"，于是这里一律用同一个公式，
    /// 不存在"捕获到的修饰键被丢掉"的可能。
    /// </summary>
    private string CurrentCombo() => KeyName.BuildComboEx(_layer, _selectedKey);

    /// <summary>层内修饰键的中文说明（界面提示用）。</summary>
    private static string ModifierText(KeyModifiers m)
    {
        if (m == KeyModifiers.None) return "直接按";
        var parts = new List<string>();
        if (m.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (m.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (m.HasFlag(KeyModifiers.Alt)) parts.Add("LAlt");
        if (m.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        return "+ " + string.Join(" + ", parts);
    }

    private void OnCustomKeyClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var dlg = new UI.Windows.KeyCaptureWindow
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.CapturedCombo)) return;

        // 捕获结果直接把"层内修饰键 + 主键"设进当前编辑状态，
        // 于是组合键与单键走的是同一条路径，不会再出现"录了却没绑上"。
        string key = dlg.CapturedKey ?? dlg.CapturedCombo;
        _layer = dlg.CapturedMods;

        SelectKey(key);
        ActionDesc.Text = $"已选中「{KeyName.Display(key)}」{ModifierText(_layer)}" +
                          $"（{CurrentCombo()}），在右边选一个功能即可绑定。";
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

    private sealed record BoundVm(KeyModifiers Mods, string Key, string KeyLabel, string Action);
}
