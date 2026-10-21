using System.IO;
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
    /// 标准 ANSI 108 键布局 —— **统一坐标系**。
    ///
    /// ⚠ 上一版是错的：每行各自用 Star 分配宽度，各行总权重不同
    /// （18 / 15 / 15 / 15 / 15 / 13.75），于是同一"1 单位"在不同行
    /// 的物理宽度不一样，键全对不齐，看起来就是"完全混乱"。
    ///
    /// 现在改成**整个键盘用同一个 92 列坐标系**：
    ///   * 1 个标准键宽 = 4 列；
    ///   * 每个键记录它的**绝对起始列**与跨列数；
    ///   * 所有行共用同一套 ColumnDefinition，按键绝对落位。
    ///
    /// 92 列 = 主键区 15 单位(60 列) + 间距 1 列 + 编辑簇 3 单位(12 列)
    ///        + 间距 1 列 + 小键盘 4 单位(16 列) + 右侧留白 2 列。
    ///
    /// 布局（列号）：
    ///   主键区  0..59    编辑簇 61..72    小键盘 74..89
    /// </summary>
    private static readonly (string Label, string? KeyId, int Col, int Span)[][] Layout =
    {
        // ① 功能键行 y=0
        new[]
        {
            ("Esc","esc",0,4),
            ("F1","f1",8,4),("F2","f2",12,4),("F3","f3",16,4),("F4","f4",20,4),
            ("F5","f5",28,4),("F6","f6",32,4),("F7","f7",36,4),("F8","f8",40,4),
            ("F9","f9",48,4),("F10","f10",52,4),("F11","f11",56,4),("F12","f12",60,4),
            ("PrtSc","printScreen",68,7),("ScrLk","scrollLock",75,7),("Pause","pause",82,7),
        },
        // ② 数字行 y=1
        new[]
        {
            ("`","backquote",0,4),
            ("1","1",4,4),("2","2",8,4),("3","3",12,4),("4","4",16,4),
            ("5","5",20,4),("6","6",24,4),("7","7",28,4),("8","8",32,4),
            ("9","9",36,4),("0","0",40,4),("-","minus",44,4),("=","equal",48,4),
            ("⌫","backspace",52,8),
            ("Ins","insert",61,4),("Home","home",65,4),("PgUp","pageUp",69,4),
            ("Num","numLock",74,4),("/","numDivide",78,4),("*","numMultiply",82,4),("-","numSubtract",86,4),
        },
        // ③ Tab 行 y=2
        new[]
        {
            ("Tab","tab",0,6),
            ("Q","q",6,4),("W","w",10,4),("E","e",14,4),("R","r",18,4),("T","t",22,4),
            ("Y","y",26,4),("U","u",30,4),("I","i",34,4),("O","o",38,4),("P","p",42,4),
            ("[","leftSquareBracket",46,4),("]","rightSquareBracket",50,4),
            ("\\","backslash",54,6),
            ("Del","delete",61,4),("End","end",65,4),("PgDn","pageDown",69,4),
            ("7","num7",74,4),("8","num8",78,4),("9","num9",82,4),
            ("+","numAdd",86,4),
        },
        // ④ Caps 行 y=3
        new[]
        {
            ("Caps","capslock",0,7),
            ("A","a",7,4),("S","s",11,4),("D","d",15,4),("F","f",19,4),("G","g",23,4),
            ("H","h",27,4),("J","j",31,4),("K","k",35,4),("L","l",39,4),
            (";","semicolon",43,4),("'","quote",47,4),
            ("Enter","enter",51,9),
            ("4","num4",74,4),("5","num5",78,4),("6","num6",82,4),
            ("+","numAdd",86,4),           // 与上一行的 + 合并成跨两行：见下方 MergeRows
        },
        // ⑤ Shift 行 y=4
        new[]
        {
            ("LShift","lshift",0,9),
            ("Z","z",9,4),("X","x",13,4),("C","c",17,4),("V","v",21,4),("B","b",25,4),
            ("N","n",29,4),("M","m",33,4),(",","comma",37,4),(".","dot",41,4),("/","slash",45,4),
            ("RShift","rshift",49,11),
            ("↑","up",65,4),
            ("1","num1",74,4),("2","num2",78,4),("3","num3",82,4),
            ("⏎","enter",86,4),            // 小键盘回车（跨两行：见下方 MergeRows）
        },
        // ⑥ 底行 y=5
        new[]
        {
            ("LCtrl","lctrl",0,5),("LWin","lwin",5,4),("LAlt","lalt",9,4),
            ("Space","space",13,26),
            ("RAlt","ralt",39,4),("RWin","rwin",43,4),("☰","apps",47,4),("RCtrl","rctrl",51,9),
            ("←","left",61,4),("↓","down",65,4),("→","right",69,4),
            ("0","num0",74,8),(".","numDecimal",82,4),("⏎","enter",86,4),
        },
    };

    /// <summary>整个键盘的列数（4 列 = 1 个标准键宽）。</summary>
    private const int TotalCols = 92;

    /// <summary>
    /// 需要**纵向合并**的键：小键盘的 + 与回车各跨两行。
    /// (行号, 键名, 跨行数)
    /// </summary>
    private static readonly (int Row, string KeyId, int Span)[] RowMerges =
    {
        (3, "numAdd", 2),
        (4, "enter", 2),
    };


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
         * 之前写死显示在左上角（Caps 位）：把 F13 绑到第 3 层时
         * 切过去亮的还是 Caps 位，符号换了位置没换。
         * 现在：当前层的触发键是哪个键，就高亮键盘上那个键本身。 */
        string? triggerKey = CurrentTriggerKey;

        /* 整个键盘用**唯一一个 92 列 Grid**。
         *
         * 上一版每行各自 new Grid + 各自的 Star 宽度 —— 各行总权重不同，
         * 同一"1 单位"在每行的实际像素宽度都不一样，键全对不齐。
         * 现在所有行共用同一套 ColumnDefinition，键按绝对列号落位。 */
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (int c = 0; c < TotalCols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        for (int r = 0; r < Layout.Length; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 找出需要纵向合并的键：这些键从 Layout 里剔除，单独按 RowSpan 摆放
        var mergeLookup = RowMerges.ToDictionary(m => (m.Row, m.KeyId), m => m.Span);

        for (int r = 0; r < Layout.Length; r++)
        {
            foreach (var (label, keyId, col, span) in Layout[r])
            {
                if (keyId == null) continue;
                // 跨行键只在它起始行放一次
                if (mergeLookup.ContainsKey((r, keyId))) continue;

                var cap = BuildKeycapEntry(label, keyId, filter, ref bound, triggerKey);
                if (cap == null) continue;
                Grid.SetRow(cap, r);
                Grid.SetColumn(cap, col);
                Grid.SetColumnSpan(cap, span);
                grid.Children.Add(cap);
            }
        }

        // 跨行大键（小键盘 + 与回车）
        foreach (var (row, keyId, rowSpan) in RowMerges)
        {
            var def = Layout[row].FirstOrDefault(k => k.KeyId == keyId);
            if (def.KeyId == null) continue;      // 元组默认值的 KeyId 是 null

            var cap = BuildKeycapEntry(def.Label, keyId, filter, ref bound, triggerKey);
            if (cap == null) continue;
            Grid.SetRow(cap, row);
            Grid.SetRowSpan(cap, rowSpan);
            Grid.SetColumn(cap, def.Col);
            Grid.SetColumnSpan(cap, def.Span);
            grid.Children.Add(cap);
        }

        KeyboardPanel.Children.Add(grid);

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

            /* 窗口绑定：键帽上显示**程序名**而不是"切换窗口"。
             * 用户要的是"一眼看到这个键对应哪个软件"。 */
            if (binding.Action == "winBind" && !string.IsNullOrWhiteSpace(binding.Arg))
            {
                try { actionTitle = Path.GetFileNameWithoutExtension(binding.Arg); }
                catch { actionTitle = "切换窗口"; }
            }
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
            // 92 列坐标系下一个单位键只有 4 列，键帽要相应缩小才排得下
            Height = 44,
            Margin = new Thickness(1, 1, 1, 1),
            CornerRadius = new CornerRadius(7),
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
            Text = isTrigger ? "★" + label : label,
            FontSize = label.Length > 4 ? 8.5 : label.Length > 2 ? 10 : 11.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource(isTrigger ? "Brush.Surface"
                : isBound ? "Brush.Accent" : "Brush.TextSecondary"),
        });
        // 已绑定时把功能名缩略显示在键帽下方（键帽只有 44px 高，字体要小）
        if (isBound && !string.IsNullOrEmpty(actionTitle) && !isTrigger)
        {
            stack.Children.Add(new TextBlock
            {
                Text = Shorten(actionTitle),
                FontSize = 7.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
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

        /* 「窗口绑定」不走普通参数框，而是换成专用面板。
         *
         * 用户原话：「我在分类里面选了窗口绑定，那旁边的功能图标就会自动变成
         * 一个选择程序的图标」。所以这里直接给出"选程序"这件事本身，
         * 而不是让他在"参数框"和"另一个动作"之间琢磨。
         */
        bool isWinBind = actionId == "winBind";
        WinBindPanel.Visibility = isWinBind ? Visibility.Visible : Visibility.Collapsed;
        ArgPanel.Visibility = isWinBind
            ? Visibility.Collapsed
            : (needsArg ? Visibility.Visible : Visibility.Collapsed);
        if (isWinBind) RefreshWinBindPanel();

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

    // ---------------- 窗口绑定面板 ----------------

    /// <summary>
    /// 仅供渲染：把界面切到「窗口绑定」动作，让面板显示出来，好截一张图确认。
    /// 返回 true 表示成功切过去了。
    /// </summary>
    public bool ShowWinBindPanelForRender()
    {
        if (_host == null) return false;

        // 分类切到「窗口绑定」
        foreach (ComboBoxItem it in CategoryBox.Items)
        {
            if (it.Tag as string == "窗口绑定")
            {
                CategoryBox.SelectedItem = it;
                BuildActionBox();
                break;
            }
        }
        // 功能切到 winBind
        foreach (ComboBoxItem it in ActionBox.Items)
        {
            if (it.Tag as string == "winBind")
            {
                ActionBox.SelectedItem = it;
                return WinBindPanel.Visibility == Visibility.Visible;
            }
        }
        return false;
    }

    /// <summary>仅供渲染：截图后把界面恢复到"当前按键真实状态"。</summary>
    public void ResetRenderState() => SelectKey(_selectedKey);

    /// <summary>当前按键已绑定的程序路径（取自绑定的 Arg）。</summary>
    private string CurrentWinBindExe()
    {
        var b = FindInCurrentLayer(CurrentCombo());
        return b?.Action == "winBind" ? (b.Arg ?? "") : "";
    }

    /// <summary>把"已选程序"区域刷新成当前绑定的样子。</summary>
    private void RefreshWinBindPanel()
    {
        string exe = CurrentWinBindExe();
        bool has = !string.IsNullOrWhiteSpace(exe) && File.Exists(exe);

        if (has)
        {
            WinBindName.Text = Path.GetFileNameWithoutExtension(exe);
            WinBindPath.Text = exe;
            WinBindClearButton.Visibility = Visibility.Visible;
            WinBindHint.Text = "按这个键：程序没开就自动启动；已开着就切过去；"
                             + "已经在前台时再按一次会最小化。";
        }
        else if (!string.IsNullOrWhiteSpace(exe))
        {
            // 路径记着但文件没了（程序被卸载/移动）
            WinBindName.Text = Path.GetFileNameWithoutExtension(exe);
            WinBindPath.Text = exe + "（文件不存在）";
            WinBindClearButton.Visibility = Visibility.Visible;
            WinBindHint.Text = "这个程序已经不在了，重新选一个吧。";
        }
        else
        {
            WinBindName.Text = "还没选择";
            WinBindPath.Text = "点下面任一按钮选择要绑定的程序";
            WinBindClearButton.Visibility = Visibility.Collapsed;
            WinBindHint.Text = "";
        }

        // 程序图标（取不到就不显示，不留空框）
        WinBindIcon.Source = has ? Core.Icons.IconCache.FromFile(exe) : null;
    }

    /// <summary>把选好的程序写进当前按键的绑定。</summary>
    private void ApplyWinBindExe(string exePath)
    {
        if (_host == null || string.IsNullOrWhiteSpace(exePath)) return;

        string combo = CurrentCombo();
        var binding = FindInCurrentLayer(combo);
        if (binding == null)
        {
            binding = new KeyBinding { Combo = combo };
            CurrentLayerKeys.Add(binding);
        }
        binding.Action = "winBind";
        binding.Arg = exePath;
        _host.SaveSettings();

        RefreshWinBindPanel();
        BuildKeyboard();
        _host.Notify($"已绑定：{Path.GetFileNameWithoutExtension(exePath)}");
    }

    /// <summary>入口一：选程序文件（适合还没运行的程序）。</summary>
    private void OnPickProgramFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要绑定的程序",
            Filter = "程序 (*.exe)|*.exe|所有文件|*.*",
            CheckFileExists = true,
        };
        // 从已选程序所在目录打开，连着改几个程序时更省事
        string cur = CurrentWinBindExe();
        if (File.Exists(cur))
        {
            try { dlg.InitialDirectory = Path.GetDirectoryName(cur); } catch { }
        }
        if (dlg.ShowDialog() != true) return;
        ApplyWinBindExe(dlg.FileName);
    }

    /// <summary>入口二：十字准星点选已打开的窗口，取其 exe 路径。</summary>
    private void OnPickWindow(object sender, RoutedEventArgs e)
    {
        var picker = new UI.Windows.WindowPickerWindow();
        if (Window.GetWindow(this) is { } owner) picker.Owner = owner;
        picker.ShowDialog();

        var h = picker.PickedWindow;
        if (h == IntPtr.Zero) return;

        var info = Core.Windows.WindowService.Describe(h);
        if (info == null || string.IsNullOrEmpty(info.ExePath))
        {
            _host?.Notify("取不到这个窗口的程序路径，改用「选择程序文件」试试");
            return;
        }
        ApplyWinBindExe(info.ExePath);
    }

    /// <summary>清除这个键的程序绑定。</summary>
    private void OnWinBindClear(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null) return;
        b.Arg = "";
        b.Action = "doNothing";
        _host.SaveSettings();
        RefreshWinBindPanel();
        BuildKeyboard();
        SelectKey(_selectedKey);      // 让功能下拉也回到"不绑定"
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
