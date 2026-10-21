using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
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
    ///        + 间距 1 列 + 小键盘 4 单位(16 列)。
    ///
    /// 布局（列号）：
    ///   主键区  0..59    编辑簇 61..72    小键盘 74..89
    ///
    /// ⚠ 小键盘的 **+** 与 **回车** 是跨行的，只在 <see cref="RowMerges"/>
    /// 里声明一次，**不要**在下面的 Layout 里重复写 —— 上一版两处都写了，
    /// 结果键盘上出现了两个加号。
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
            ("Num","numLock",74,4),("/","numDivide",78,4),("*","numMultiply",82,4),("−","numSubtract",86,4),
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
            // 第 4 列是小键盘 +，跨 y=2..3 两行 → 在 RowMerges 里声明
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
            // 第 4 列仍是被 + 占据（跨行），这里不写
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
            // 第 4 列是小键盘回车，跨 y=4..5 两行 → 在 RowMerges 里声明
        },
        // ⑥ 底行 y=5
        new[]
        {
            // 修饰键标签故意用 2~3 个字符：4 列宽（约 26px）放不下 "LCtrl"
            ("Ctrl","lctrl",0,5),("Win","lwin",5,4),("Alt","lalt",9,4),
            ("Space","space",13,26),
            ("Alt","ralt",39,4),("Win","rwin",43,4),("☰","apps",47,4),("Ctrl","rctrl",51,9),
            ("←","left",61,4),("↓","down",65,4),("→","right",69,4),
            ("0","num0",74,8),(".","numDecimal",82,4),
            // 第 4 列仍是被回车占据（跨行）
        },
    };

    /// <summary>整个键盘的列数（4 列 = 1 个标准键宽）。</summary>
    private const int TotalCols = 92;

    /// <summary>
    /// 需要**纵向合并**的键：小键盘的 + 与回车各跨两行。
    ///
    /// ⚠ 这些键**不能**同时出现在 <see cref="Layout"/> 里 ——
    /// 那样会被画两次（上一版小键盘就出现了两个加号）。
    /// (行号, 键名, 跨行数, 起始列, 跨列数)
    /// </summary>
    private static readonly (int Row, string KeyId, int RowSpan, int Col, int ColSpan, string Label)[] RowMerges =
    {
        (2, "numAdd", 2, 86, 4, "+"),
        (4, "enter", 2, 86, 4, "⏎"),

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
        string? keep = SelectedCategory;
        string? keepWin = (CategoryBoxWinBind.SelectedItem as ComboBoxItem)?.Tag as string;
        string? keepWeb = (CategoryBoxWeb.SelectedItem as ComboBoxItem)?.Tag as string;

        var cats = _host.Actions.Items.Select(i => i.Category).Distinct().ToList();

        /* 三个分类下拉填同样的内容：
         *   · CategoryBox        —— 普通模式（分类 + 功能两级）
         *   · CategoryBoxWinBind —— 窗口绑定模式（分类 + 两个按钮）
         *   · CategoryBoxWeb     —— 直达网页模式（分类 + 网址 + 备注）
         * 三者必须同步，否则用户在哪个里面选都看不到另一个。 */
        CategoryBox.Items.Clear();
        CategoryBoxWinBind.Items.Clear();
        CategoryBoxWeb.Items.Clear();
        foreach (var cat in cats)
        {
            CategoryBox.Items.Add(new ComboBoxItem { Content = cat, Tag = cat });
            CategoryBoxWinBind.Items.Add(new ComboBoxItem { Content = cat, Tag = cat });
            CategoryBoxWeb.Items.Add(new ComboBoxItem { Content = cat, Tag = cat });
        }

        Restore(CategoryBox, keep);
        Restore(CategoryBoxWinBind, keepWin);
        Restore(CategoryBoxWeb, keepWeb);

        static void Restore(ComboBox box, string? tag)
        {
            foreach (ComboBoxItem it in box.Items)
            {
                if (it.Tag as string == tag) { box.SelectedItem = it; return; }
            }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        BuildActionBox();
    }

    /// <summary>「窗口绑定」分类的名字（这个分类不走功能下拉）。</summary>
    private const string WinBindCategory = "窗口绑定";

    /// <summary>「直达网页」分类的名字（同样不走功能下拉，只要网址 + 备注）。</summary>
    private const string WebPageCategory = "直达网页";

    /// <summary>当前分类是不是「窗口绑定」。</summary>
    private bool IsWinBindCategory =>
        (CategoryBox.SelectedItem as ComboBoxItem)?.Tag as string == WinBindCategory;

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

    /// <summary>界面当前处于哪种编辑模式。</summary>
    private enum EditorMode
    {
        /// <summary>普通：分类 + 功能两级下拉 + 参数框。</summary>
        Normal,
        /// <summary>窗口绑定：分类 + 两个按钮（点选窗口 / 选程序文件）。</summary>
        WinBind,
        /// <summary>直达网页：分类 + 网址 + 备注。</summary>
        WebPage,
    }

    /// <summary>根据分类名判断该用哪种模式。</summary>
    private static EditorMode ModeOf(string? category) => category switch
    {
        WinBindCategory => EditorMode.WinBind,
        WebPageCategory => EditorMode.WebPage,
        _ => EditorMode.Normal,
    };

    /// <summary>当前选中的分类（普通模式那个下拉）。</summary>
    private string? SelectedCategory =>
        (CategoryBox.SelectedItem as ComboBoxItem)?.Tag as string;

    /// <summary>
    /// 切换编辑模式：三个面板互斥显示。
    ///
    /// 这就是"选了分类，下面就换成对应的东西"的实现 ——
    ///   · 窗口绑定 → 两个按钮
    ///   · 直达网页 → 网址 + 备注
    ///   · 其它     → 功能下拉 + 参数框
    /// </summary>
    private void ApplyEditorMode(EditorMode mode)
    {
        NormalActionPicker.Visibility = mode == EditorMode.Normal ? Visibility.Visible : Visibility.Collapsed;
        WinBindPicker.Visibility = mode == EditorMode.WinBind ? Visibility.Visible : Visibility.Collapsed;
        WebPagePicker.Visibility = mode == EditorMode.WebPage ? Visibility.Visible : Visibility.Collapsed;

        WinBindPanel.Visibility = mode == EditorMode.WinBind ? Visibility.Visible : Visibility.Collapsed;

        // 非普通模式不显示参数框（普通模式的显隐由 ShowArgPanelFor 决定）
        if (mode != EditorMode.Normal) ArgPanel.Visibility = Visibility.Collapsed;

        switch (mode)
        {
            case EditorMode.WinBind:
                ActionDesc.Text = "选定一个程序后：按一下切过去，再按一次最小化；程序没开就自动启动。";
                RefreshWinBindPanel();
                break;
            case EditorMode.WebPage:
                ActionDesc.Text = "填一个网址：按这个键就用默认浏览器打开它。备注会显示在键帽上。";
                RefreshWebPageFields();
                break;
        }
    }

    /// <summary>
    /// 分类切换。
    /// </summary>
    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;

        _suppressEvents = true;
        try
        {
            SyncCategoryBoxes(sender);

            var mode = ModeOf(SelectedCategory);
            ApplyEditorMode(mode);

            if (mode == EditorMode.Normal)
            {
                BuildActionBox();
                ApplySelectedAction();       // 让下方参数面板跟着刷新
            }
            else if (mode == EditorMode.WinBind)
            {
                // 选了窗口绑定分类：若当前键还没绑窗口，先把动作切成 winBind，
                // 这样用户接下来点"选择程序"就能直接落盘。
                EnsureActionForCategory("winBind");
            }
            else if (mode == EditorMode.WebPage)
            {
                EnsureActionForCategory("webPage");
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    /// <summary>
    /// 把当前按键的动作设成指定 ID（若还没设过）。
    /// 用于窗口绑定 / 直达网页这类"选了分类就等于选了唯一动作"的场景。
    /// </summary>
    private void EnsureActionForCategory(string actionId)
    {
        if (_host == null) return;
        string combo = CurrentCombo();
        var binding = FindInCurrentLayer(combo);
        if (binding == null)
        {
            binding = new KeyBinding { Combo = combo };
            CurrentLayerKeys.Add(binding);
        }
        if (binding.Action == actionId) return;      // 已经是了，别覆盖 Arg

        binding.Action = actionId;
        _host.SaveSettings();
        BuildKeyboard();
    }

    /// <summary>
    /// 三个分类下拉（普通 / 窗口绑定 / 直达网页）互相同步 ——
    /// 用户从哪个里面选了分类，另外两个也跟着切过去。
    /// </summary>
    private void SyncCategoryBoxes(object? source)
    {
        string? tag = source switch
        {
            var s when ReferenceEquals(s, CategoryBox) => (CategoryBox.SelectedItem as ComboBoxItem)?.Tag as string,
            var s when ReferenceEquals(s, CategoryBoxWinBind) => (CategoryBoxWinBind.SelectedItem as ComboBoxItem)?.Tag as string,
            var s when ReferenceEquals(s, CategoryBoxWeb) => (CategoryBoxWeb.SelectedItem as ComboBoxItem)?.Tag as string,
            _ => SelectedCategory,
        };
        if (tag == null) return;

        foreach (var box in new[] { CategoryBox, CategoryBoxWinBind, CategoryBoxWeb })
        {
            if (ReferenceEquals(box, source)) continue;
            foreach (ComboBoxItem it in box.Items)
            {
                if (it.Tag as string == tag) { box.SelectedItem = it; break; }
            }
        }
    }

    /// <summary>显示/隐藏窗口绑定的"当前绑定"卡片。</summary>
    private void ShowWinBindArea(bool show)
    {
        if (!show) { WinBindPanel.Visibility = Visibility.Collapsed; return; }
        WinBindPanel.Visibility = Visibility.Visible;
        RefreshWinBindPanel();
    }

    /// <summary>
    /// 普通模式下：把 ActionBox 里选中的动作应用到当前按键。
    /// 与 <see cref="OnActionChanged"/> 同样的写入逻辑，但不受 _suppressEvents 影响。
    /// </summary>
    private void ApplySelectedAction()
    {
        if (_host == null) return;
        if (ActionBox.SelectedItem is not ComboBoxItem { Tag: string actionId }) return;

        string combo = CurrentCombo();
        var binding = FindInCurrentLayer(combo);
        if (binding == null)
        {
            binding = new KeyBinding { Combo = combo };
            CurrentLayerKeys.Add(binding);
        }
        binding.Action = actionId;
        if (actionId == "doNothing") binding.Arg = "";
        _host.SaveSettings();

        var info = _host.Actions.Get(actionId);
        ActionDesc.Text = info?.Description ?? "";
        ShowArgPanelFor(actionId, info?.NeedsArg == true);
        BuildKeyboard();
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

        /* 画普通键（单行）。
         *
         * 跨行键（小键盘 + 与回车）**不在 Layout 里出现**，只由 RowMerges 声明，
         * 所以这里不需要"跳过"逻辑 —— 上一版两处都写了，导致 + 被画两次。 */
        for (int r = 0; r < Layout.Length; r++)
        {
            foreach (var (label, keyId, col, span) in Layout[r])
            {
                if (keyId == null) continue;

                var cap = BuildKeycapEntry(label, keyId, filter, ref bound, triggerKey);
                if (cap == null) continue;
                Grid.SetRow(cap, r);
                Grid.SetColumn(cap, col);
                Grid.SetColumnSpan(cap, span);
                grid.Children.Add(cap);
            }
        }

        // 跨行大键（小键盘 + 与回车）：位置与标签都在 RowMerges 里
        foreach (var (row, keyId, rowSpan, col, colSpan, label) in RowMerges)
        {
            var cap = BuildKeycapEntry(label, keyId, filter, ref bound, triggerKey);
            if (cap == null) continue;
            Grid.SetRow(cap, row);
            Grid.SetRowSpan(cap, rowSpan);
            Grid.SetColumn(cap, col);
            Grid.SetColumnSpan(cap, colSpan);
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
            // 统一走 DescribeActionOf，键帽与"本层已绑定"列表的文字保证一致
            actionTitle = DescribeActionOf(binding);
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
        bool isSelected = string.Equals(keyId, _selectedKey, StringComparison.OrdinalIgnoreCase);

        /* 三种视觉状态，一眼可分：
         *
         *   触发键（本层）  → 深蓝实心 + 白字 + ★     （它是"入口"，最该显眼）
         *   已绑定功能      → 深蓝实心 + 白字          （用户要的：深蓝色一眼看出有东西）
         *   未绑定          → 浅色键帽 + 次要色文字    （安静，不抢注意力）
         *
         * 之前"已绑定"只是浅蓝底 + 蓝字，对比度低，扫一眼看不出哪些键有功能。
         */
        Brush background, borderBrush, textBrush, subTextBrush;
        if (isTrigger || isBound)
        {
            background = (Brush)FindResource("Brush.Accent");
            borderBrush = (Brush)FindResource("Brush.AccentHover");
            textBrush = (Brush)FindResource("Brush.Surface");         // 白字
            subTextBrush = (Brush)FindResource("Brush.Surface");
        }
        else
        {
            background = (Brush)FindResource("Brush.Keycap");
            borderBrush = (Brush)FindResource("Brush.KeycapBorder");
            textBrush = (Brush)FindResource("Brush.TextSecondary");
            subTextBrush = (Brush)FindResource("Brush.TextTertiary");
        }

        var border = new Border
        {
            // 92 列坐标系下一个单位键只有 4 列；高度要放下"键名 + 功能名"两行
            Height = isBound ? 46 : 44,
            Margin = new Thickness(1, 1, 1, 1),
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Background = background,
            BorderBrush = borderBrush,
            ToolTip = isTrigger
                ? $"★ {label} = 本层触发键\n长按它进入这一层；短按执行短按动作"
                : isBound ? $"{label} → {actionTitle}" : $"{label}（未绑定）",
        };

        /* 当前选中的键用更粗的描边圈出来。
         * 已绑定的键本身就是深蓝底，再用蓝描边会看不见 ——
         * 所以选中态改用白色描边（在深蓝底上对比明显）。 */
        if (isSelected)
        {
            border.BorderThickness = new Thickness(2);
            border.BorderBrush = isBound || isTrigger
                ? (Brush)FindResource("Brush.Surface")
                : (Brush)FindResource("Brush.Accent");
        }

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 1, 0),
        };

        /* 第一行永远是**按键名**（F5 / Q / Space…）。
         *
         * 这一行不能省：深蓝底色只说明"有绑定"，但用户还得知道
         * "这是哪个键"，否则一眼扫过去全是功能名，容易搞混。 */
        stack.Children.Add(new TextBlock
        {
            Text = isTrigger ? "★" + label : label,
            FontSize = label.Length switch
            {
                <= 1 => 12,
                2 => 10.5,
                3 => 9,
                _ => 8,
            },
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = textBrush,
        });

        /* 第二行是**功能名**（已绑定时才显示）。
         *
         * 键帽实际可用宽度很窄：92 列铺满这张卡片，单位键只有约 26px。
         * 7px 字号下中文一个字约 7px，所以窄键只能放 3 个字
         * （"下一行""粘贴""github" 这样），宽键放到 8 个。
         *
         * 完整名字在悬停 tooltip 和右侧「本层已绑定」列表里都有，
         * 键帽上只需要"认得出是哪个功能"就够了。 */
        if (isBound && !string.IsNullOrWhiteSpace(actionTitle))
        {
            bool wide = IsWideKey(keyId);
            stack.Children.Add(new TextBlock
            {
                Text = Shorten(actionTitle, wide ? 8 : 3),
                FontSize = wide ? 8 : 6.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 0, 0, 0),
                Foreground = subTextBrush,
                Opacity = 0.85,
            });
        }
        border.Child = stack;

        string captured = keyId;
        border.MouseLeftButtonUp += (_, _) => SelectKey(captured);
        border.MouseEnter += (_, _) => border.Opacity = 0.82;
        border.MouseLeave += (_, _) => border.Opacity = 1.0;
        return border;
    }

    /// <summary>
    /// 宽度足够放下第二行文字的键（≥ 6 列）。
    /// 这些键上多显示一个功能名不会显得挤。
    /// </summary>
    private static bool IsWideKey(string keyId) => keyId is
        "space" or "enter" or "backspace" or "tab"
        or "lshift" or "rshift" or "lctrl" or "rctrl"
        or "capslock";

    /// <summary>截断到指定字数（超长部分直接省掉，键帽上放不下省略号）。</summary>
    private static string Shorten(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0) return "";
        text = text.Trim();
        return text.Length <= max ? text : text[..max];
    }

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

            /* 列表里的"功能"列要和键帽一致：
             * 窗口绑定显示程序名，直达网页显示备注/域名 ——
             * 否则用户看到的是"覆盖""打开网页"，对不上自己绑的东西。 */
            string action = DescribeActionOf(binding);
            items.Add(new BoundVm(mods, key, label, action));
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

        // 同步"窗口绑定模式"的分类下拉
        foreach (ComboBoxItem it in CategoryBoxWinBind.Items)
        // 同步另外两个"专用模式"的分类下拉
        foreach (ComboBoxItem wi in CategoryBoxWinBind.Items)
        {
            if (wi.Tag as string == info?.Category) { CategoryBoxWinBind.SelectedItem = wi; break; }
        }
        foreach (ComboBoxItem wb2 in CategoryBoxWeb.Items)
        {
            if (wb2.Tag as string == info?.Category) { CategoryBoxWeb.SelectedItem = wb2; break; }
        }

        _suppressEvents = false;

        /* 按动作所属分类决定用哪个模式 ——
         *   winBind   → 两个按钮 + 当前绑定卡片
         *   webPage   → 网址 + 备注
         *   其它      → 功能下拉 + 参数框 */
        var mode = actionId switch
        {
            "winBind" => EditorMode.WinBind,
            "webPage" => EditorMode.WebPage,
            _ => EditorMode.Normal,
        };
        ApplyEditorMode(mode);

        if (mode == EditorMode.Normal)
        {
            ActionDesc.Text = info?.Description ?? "";
            ShowArgPanelFor(actionId, info?.NeedsArg == true || IsCountArgAction(actionId));
        }

        BuildKeyboard();
    }

    /// <summary>这些动作的"参数"是次数，界面要提示填数字。</summary>
    private static bool IsCountArgAction(string actionId) => actionId is
        "moveLeft" or "moveRight" or "moveUp" or "moveDown"
        or "selectLeft" or "selectRight" or "selectUp" or "selectDown"
        or "moveWordLeft" or "moveWordRight"
        or "selectWordLeft" or "selectWordRight"
        or "pageMoveLineUp" or "pageMoveLineDown";

    /// <summary>按动作类型决定参数面板的样子。</summary>
    private void ShowArgPanelFor(string actionId, bool needsArg, bool isWinBind = false)
    {
        ArgPanel.Visibility = needsArg && !isWinBind ? Visibility.Visible : Visibility.Collapsed;

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
    }

    // ---------------- 窗口绑定面板 ----------------

    /// <summary>
    /// 仅供渲染：切到「直达网页」模式，好截一张图确认网址/备注框都画出来了。
    /// </summary>
    public bool ShowWebPagePanelForRender()
    {
        if (_host == null) return false;

        foreach (ComboBoxItem it in CategoryBox.Items)
        {
            if (it.Tag as string == WebPageCategory)
            {
                CategoryBox.SelectedItem = it;
                return WebPagePicker.Visibility == Visibility.Visible;
            }
        }
        return false;
    }

    /// <summary>
    /// 仅供渲染：把界面切到「窗口绑定」模式，好截一张图确认按钮和卡片都画出来了。
    /// </summary>
    public bool ShowWinBindPanelForRender()
    {
        if (_host == null) return false;

        foreach (ComboBoxItem it in CategoryBox.Items)
        {
            if (it.Tag as string == WinBindCategory)
            {
                CategoryBox.SelectedItem = it;
                return WinBindPicker.Visibility == Visibility.Visible;
            }
        }
        return false;
    }

    /// <summary>仅供渲染：截图后把界面恢复到"当前按键真实状态"。</summary>
    public void ResetRenderState() => SelectKey(_selectedKey);

    /// <summary>
    /// 一个绑定在界面上该显示成什么。
    ///
    /// 键帽和"本层已绑定"列表都用它，保证两处文字一致 ——
    /// 早先键帽显示程序名、列表显示"覆盖"，用户对不上号。
    /// </summary>
    private string DescribeActionOf(KeyBinding binding)
    {
        string id = binding.Action ?? "";

        if (id == "winBind")
        {
            if (string.IsNullOrWhiteSpace(binding.Arg)) return "切换窗口（未选程序）";
            try { return Path.GetFileNameWithoutExtension(binding.Arg); }
            catch { return "切换窗口"; }
        }

        if (id == "webPage")
            return Core.Actions.UrlUtil.LabelOf(binding.Note, binding.Arg);

        if (id.StartsWith("macro:", StringComparison.OrdinalIgnoreCase))
        {
            string t = _host?.Actions.Get(id)?.Title ?? id;
            return t.StartsWith("播放宏：", StringComparison.OrdinalIgnoreCase)
                ? "▶ " + t["播放宏：".Length..]
                : t;
        }

        return _host?.Actions.Get(id)?.Title ?? id;
    }

    /// <summary>
    /// 从网址里取出域名做键帽标签（写不出备注时的退化显示）。
    /// 解析失败就原样返回，绝不抛异常 —— 键帽渲染不能因为一个坏网址崩掉。
    /// </summary>
    private static string WebHostOf(string? url) => Core.Actions.UrlUtil.HostOf(url);

    // ---------------- 就地状态提示 ----------------

    /// <summary>
    /// 在编辑面板里显示一行结果（绿色=成功，红色=失败）。
    ///
    /// 为什么不只用 <see cref="AppHost.Notify"/>：那是屏幕右下角的 toast，
    /// 用户正在主窗口里点按钮，视线根本不在那儿 —— 会以为"没反应"。
    /// </summary>
    private void ShowBindStatus(string message, bool ok = true)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            BindStatusBar.Visibility = Visibility.Collapsed;
            return;
        }

        BindStatusText.Text = message;
        BindStatusBar.Background = (Brush)FindResource(ok ? "Brush.AccentSoft" : "Brush.SurfaceAlt");
        BindStatusText.Foreground = (Brush)FindResource(ok ? "Brush.Accent" : "Brush.TextSecondary");
        BindStatusBar.BorderBrush = (Brush)FindResource(ok ? "Brush.AccentSoftBorder" : "Brush.Border");
        BindStatusBar.BorderThickness = new Thickness(1);
        BindStatusBar.Visibility = Visibility.Visible;

        // 3 秒后自动淡出，避免一直占着版面
        _statusTimer?.Stop();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer?.Stop();
            _statusTimer = null;
            BindStatusBar.Visibility = Visibility.Collapsed;
        };
        _statusTimer.Start();
    }

    private DispatcherTimer? _statusTimer;

    // ---------------- 直达网页面板 ----------------

    /// <summary>把"网址 / 备注"两个输入框刷成当前绑定的内容。</summary>
    private void RefreshWebPageFields()
    {
        var b = FindInCurrentLayer(CurrentCombo());
        bool isWeb = b?.Action == "webPage";
        WebUrlBox.Text = isWeb ? (b!.Arg ?? "") : "";
        WebNoteBox.Text = isWeb ? (b.Note ?? "") : "";
    }

    private void OnWebUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || b.Action != "webPage") return;

        b.Arg = WebUrlBox.Text.Trim();
        _host.SaveSettings();
        BuildKeyboard();          // 键帽上要跟着变

        // 网址填完就去抓标题当备注（用户要求"一定要先拉一下"）
        MaybeFetchTitle();
    }

    /// <summary>
    /// 抓网页标题填进备注。
    ///
    /// 时机：网址变化后**防抖 700ms** —— 用户还在敲字时不要一路请求。
    /// 只在备注为空时自动填：如果用户已经自己写过备注，绝不覆盖。
    /// </summary>
    private void MaybeFetchTitle(bool force = false)
    {
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || b.Action != "webPage") return;
        if (string.IsNullOrWhiteSpace(b.Arg)) return;

        // 已有备注且不是手动点"重新取标题" → 不覆盖用户写的东西
        if (!force && !string.IsNullOrWhiteSpace(b.Note)) return;

        _titleTimer?.Stop();
        _titleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _titleTimer.Tick += (_, _) =>
        {
            _titleTimer?.Stop();
            _titleTimer = null;
            StartTitleFetch(force);
        };
        _titleTimer.Start();
    }

    private DispatcherTimer? _titleTimer;

    /// <summary>真正发起抓取（后台线程，不卡界面）。</summary>
    private void StartTitleFetch(bool overwrite)
    {
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || b.Action != "webPage") return;
        string url = b.Arg ?? "";
        if (string.IsNullOrWhiteSpace(url)) return;

        WebFetchStatus.Text = "正在获取网页标题…";
        FetchTitleButton.IsEnabled = false;

        // 用 Task.Run 而不是同步调用：HttpClient 最长等 3 秒，同步会冻结界面
        Task.Run(() => Core.Actions.WebTitleFetcher.Fetch(url))
            .ContinueWith(t =>
            {
                string? title = t.IsCompletedSuccessfully ? t.Result : null;

                Dispatcher.Invoke(() =>
                {
                    FetchTitleButton.IsEnabled = true;

                    // 用户可能在等待期间切走了别的键，重新取一次当前绑定
                    var cur = FindInCurrentLayer(CurrentCombo());
                    if (cur == null || cur.Action != "webPage") return;

                    if (string.IsNullOrWhiteSpace(title))
                    {
                        WebFetchStatus.Text = "没能取到标题（网站在线吗？），可以直接手写备注。";
                        return;
                    }

                    if (!overwrite && !string.IsNullOrWhiteSpace(cur.Note))
                    {
                        WebFetchStatus.Text = $"取到标题「{title}」，但你已有备注，保留你的。";
                        return;
                    }

                    cur.Note = title;
                    _host?.SaveSettings();
                    _suppressEvents = true;
                    WebNoteBox.Text = title;
                    _suppressEvents = false;

                    WebFetchStatus.Text = $"已取到标题：{title}（可以改）";
                    BuildKeyboard();
                });
            });
    }

    private void OnFetchTitleClick(object sender, RoutedEventArgs e)
    {
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || b.Action != "webPage" || string.IsNullOrWhiteSpace(b.Arg))
        {
            ShowBindStatus("先填一个网址，再取标题。", ok: false);
            return;
        }
        StartTitleFetch(overwrite: true);
    }

    private void OnWebNoteChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || b.Action != "webPage") return;

        b.Note = WebNoteBox.Text.Trim();
        _host.SaveSettings();
        BuildKeyboard();

        // 有备注了就给个确认（用户反馈"没有提示"）
        if (!string.IsNullOrWhiteSpace(b.Note))
            ShowBindStatus($"备注已保存：「{b.Note}」—— 键帽上会显示这个名字");
    }

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

        // 换程序时旧备注（如果有）就不适用了，清掉免得显示错的
        binding.Note = null;

        _host.SaveSettings();

        RefreshWinBindPanel();
        BuildKeyboard();

        string name = Path.GetFileNameWithoutExtension(exePath);
        string keyLabel = KeyName.Display(_selectedKey);
        ShowBindStatus($"{keyLabel} 已绑定「{name}」—— 按一次切过去，再按一次最小化");
        _host.Notify($"{keyLabel} → {name}");
    }

    /// <summary>入口一：选程序文件（适合还没运行的程序）。</summary>
    private void OnPickExeClick(object sender, RoutedEventArgs e)
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
    private void OnPickWindowClick(object sender, RoutedEventArgs e)
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
