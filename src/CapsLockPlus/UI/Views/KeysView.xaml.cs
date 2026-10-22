using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CapsLockPlus.Core.Actions;
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

    /// <summary>
    /// 当前选中的**子层键名**（空字符串 = "直接按"子层）。
    ///
    /// 与 <see cref="_layer"/> 的区别：_layer 是修饰键位（Ctrl/Shift/…），
    /// 用于拼 combo 前缀；子层键还可以是任意物理键（Tab、CapsLock），
    /// 那种情况下 _layer 为 None、_subLayerKey 有值。
    /// </summary>
    private string _subLayerKey = "";

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
    /// 键帽统一高度。
    ///
    /// 所有键必须等高，否则同行内已绑定键会把行撑高、
    /// 未绑定键上下留白，看起来"按键间距不一致"。
    /// 52px 是能放下"分类 + 功能名（两行）+ 键名"三行内容的最小值。
    /// </summary>
    private const double KeycapHeight = 52;

    /// <summary>键帽之间的水平/垂直间距（单侧），总间距是它的两倍。</summary>
    private const double KeycapGap = 3;

    /// <summary>
    /// 需要**纵向合并**的键：小键盘的 + 与回车各跨两行。
    ///
    /// ⚠ 这些键**不能**同时出现在 <see cref="Layout"/> 里 ——
    /// 那样会被画两次（上一版小键盘就出现了两个加号）。
    ///
    /// 尺寸按标准 108 键：+ 是**宽键**（高 2 单位、宽 1 单位），
    /// 回车是高 2 单位、宽 1 单位。它们比普通键高，视觉上自然有分量。
    /// (行号, 键名, 跨行数, 起始列, 跨列数, 标签)
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
        string? keepType = (CategoryBoxType.SelectedItem as ComboBoxItem)?.Tag as string;

        /* 隐藏动作（openUrl / runProgram）不参与分类列表：
         * 它们只为老配置的绑定保留执行能力，不该再出现在界面上。 */
        var cats = _host.Actions.Items.Where(i => !i.Hidden)
            .Select(i => i.Category).Distinct().ToList();

        /* 三个分类下拉填同样的内容：
         *   · CategoryBox        —— 普通模式（分类 + 功能两级）
         *   · CategoryBoxWinBind —— 窗口绑定模式（分类 + 两个按钮）
         *   · CategoryBoxWeb     —— 直达网页模式（分类 + 网址 + 备注）
         * 三者必须同步，否则用户在哪个里面选都看不到另一个。 */
        CategoryBox.Items.Clear();
        CategoryBoxWinBind.Items.Clear();
        CategoryBoxWeb.Items.Clear();
        CategoryBoxCustom.Items.Clear();
        foreach (var cat in cats)
        {
            CategoryBox.Items.Add(new ComboBoxItem { Content = cat, Tag = cat });
            CategoryBoxWinBind.Items.Add(new ComboBoxItem { Content = cat, Tag = cat });
            CategoryBoxWeb.Items.Add(new ComboBoxItem { Content = cat, Tag = cat });
            CategoryBoxCustom.Items.Add(new ComboBoxItem { Content = cat, Tag = cat });
            CategoryBoxType.Items.Add(new ComboBoxItem { Content = cat, Tag = cat });
        }

        Restore(CategoryBox, keep);
        Restore(CategoryBoxWinBind, keepWin);
        Restore(CategoryBoxWeb, keepWeb);
        Restore(CategoryBoxType, keepType);

        static void Restore(ComboBox box, string? tag) => SelectTag(box, tag, fallbackFirst: true);

        BuildActionBox();
    }

    /// <summary>「窗口绑定」分类的名字（这个分类不走功能下拉）。</summary>
    private const string WinBindCategory = "窗口绑定";

    /// <summary>
    /// 「直达链接」分类的名字（同样不走功能下拉）。
    ///
    /// 这一类下面有三个动作：直达网址 / 直达文件夹 / 直达文件。
    /// 面板里先用三个按钮选类型，再填目标 + 备注 ——
    /// 比"在功能下拉里翻 200 个动作"直观得多。
    /// </summary>
    private const string WebPageCategory = "直达链接";

    /// <summary>「输入文本」分类（只要一个多行文本框）。</summary>
    private const string TypeTextCategory = "输入文本";

    /// <summary>
    /// 「自定义按键」分类：把某个键发给某个常驻程序。
    ///
    /// 这个分类只有一个动作（customKey），参数是 CustomKeySpec 的 JSON。
    /// 面板里能设"发什么键 / 发给哪个应用 / 是否管理员启动 / 是否切前台"。
    /// </summary>
    private const string CustomKeyCategory = "自定义按键";

    /// <summary>「直达链接」下的三个动作 ID。</summary>
    private const string LinkUrlAction = "webPage";
    private const string LinkFolderAction = "openFolder";
    private const string LinkFileAction = "openFile";

    /// <summary>当前分类是不是「窗口绑定」。</summary>
    private bool IsWinBindCategory =>
        (CategoryBox.SelectedItem as ComboBoxItem)?.Tag as string == WinBindCategory;

    /// <summary>填"功能"下拉。有搜索词时跨分类搜索，否则只列当前分类。</summary>
        /// <summary>填"功能"下拉：只列当前分类下的动作。</summary>
    private void BuildActionBox()
    {
        if (_host == null) return;
        string? cat = (CategoryBox.SelectedItem as ComboBoxItem)?.Tag as string;

        ActionBox.Items.Clear();
        foreach (var item in _host.Actions.Items.Where(i => !i.Hidden && i.Category == cat))
            ActionBox.Items.Add(new ComboBoxItem { Content = item.Title, Tag = item.Id });

        if (ActionBox.Items.Count > 0) ActionBox.SelectedIndex = 0;
    }

    /// <summary>
    /// 一个功能是否匹配搜索词。
    ///
    /// 匹配标题、短名、分类、描述、宏名 —— 用户可能记得
    /// "我绑过一个 github 的网址"或"那个宏叫什么"。
    /// </summary>
    private bool MatchesAction(CapsLockPlus.Core.Actions.ActionInfo item, string filter)
    {
        if (item.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.ShortTitle.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.Category.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;
        if (item.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;

        // 宏：再用宏名兜一次，防止名字只存在于宏对象里
        if (item.Id.StartsWith("macro:", StringComparison.OrdinalIgnoreCase) && _host != null)
        {
            foreach (var macro in _host.Settings.Macros)
            {
                if (item.Id.EndsWith(macro.Id, StringComparison.OrdinalIgnoreCase) &&
                    macro.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>搜索框内容变化：重建功能下拉。</summary>
        // ---------------- 搜索结果 ----------------

    /// <summary>一条搜索结果：点它会跳到对应的分类 + 功能。</summary>
    private sealed record SearchHit(string ActionId, string Category, string Title)
    {
        public string Subtitle => Category;
    }

    /// <summary>
    /// 搜索框内容变化：构建结果列表并弹出浮层。
    ///
    /// 搜索是**独立的结果下拉**，不干扰下面的分类/功能下拉 ——
    /// 用户点中一条之后，才把三项一起切过去。
    /// </summary>
    private void OnActionSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;

        string filter = ActionSearchBox.Text?.Trim() ?? "";
        if (filter.Length == 0)
        {
            SearchPopup.IsOpen = false;
            SearchResultList.ItemsSource = null;
            return;
        }

        var hits = _host.Actions.Items
            .Where(i => !i.Hidden && MatchesAction(i, filter))
            .OrderBy(i => i.Category)
            .ThenBy(i => i.Title)
            .Take(60)
            .Select(i => new SearchHit(i.Id, i.Category, i.Title))
            .ToList();

        SearchResultList.ItemsSource = hits;
        SearchPopup.IsOpen = hits.Count > 0;
    }

    /// <summary>
    /// 点了某条搜索结果 → 分类、功能、参数面板一起跳过去。
    /// </summary>
    private void OnSearchResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        if (SearchResultList.SelectedItem is not SearchHit hit) return;

        _suppressEvents = true;
        try
        {
            // ① 切分类（四个分类下拉都要同步）
            var info = _host.Actions.Get(hit.ActionId);
            if (info != null)
            {
                foreach (var box in new[] { CategoryBox, CategoryBoxWinBind, CategoryBoxWeb, CategoryBoxType })
                    SelectTag(box, info.Category);
            }

            // ② 按分类决定用哪种编辑模式
            var mode = ModeOf(info?.Category);
            ApplyEditorMode(mode);

            // ③ 普通模式下把功能下拉选中到目标项
            if (mode == EditorMode.Normal)
            {
                BuildActionBox();
                foreach (ComboBoxItem it in ActionBox.Items)
                {
                    if (it.Tag is string tag && tag.Equals(hit.ActionId, StringComparison.OrdinalIgnoreCase))
                    {
                        ActionBox.SelectedItem = it;
                        break;
                    }
                }
                ApplySelectedAction();
            }
            else
            {
                // 窗口绑定 / 直达网页 / 输入文本：把当前键的动作设过去
                EnsureActionForCategory(hit.ActionId);
            }

            // ④ 收起搜索
            ActionSearchBox.Text = "";
            SearchPopup.IsOpen = false;
            SearchResultList.ItemsSource = null;
        }
        finally
        {
            _suppressEvents = false;
        }
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
        /// <summary>输入文本：分类 + 多行文本框。</summary>
        TypeText,
        /// <summary>自定义按键：分类 + 键 / 应用 / 两个开关。</summary>
        CustomKey,
    }

    /// <summary>根据分类名判断该用哪种模式。</summary>
    private static EditorMode ModeOf(string? category) => category switch
    {
        WinBindCategory => EditorMode.WinBind,
        WebPageCategory => EditorMode.WebPage,
        TypeTextCategory => EditorMode.TypeText,
        CustomKeyCategory => EditorMode.CustomKey,
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
        TypeTextPicker.Visibility = mode == EditorMode.TypeText ? Visibility.Visible : Visibility.Collapsed;
        CustomKeyPicker.Visibility = mode == EditorMode.CustomKey ? Visibility.Visible : Visibility.Collapsed;

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
                ActionDesc.Text = "选「直达网址」就用默认浏览器打开网址；选「直达文件夹」就在资源管理器里打开那个目录；选「直达文件」就用系统默认程序打开它。备注会显示在键帽上。";
                RefreshWebPageFields();
                break;
            case EditorMode.TypeText:
                ActionDesc.Text = "填一段文字：按这个键就把它直接输入到当前光标处。";
                RefreshTypeTextField();
                break;
            case EditorMode.CustomKey:
                ActionDesc.Text = "先把要发送的键和应用绑好。按这个键时会先看应用在不在运行："
                                + "在运行就直接把键发过去，没运行就先把它拉起来再发。";
                RefreshCustomKeyPanel();
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

            /* 用户**手动**换了分类 → 清掉搜索框。
             *
             * 否则会出现"我搜了「删除」，再选「面板」分类，
             * 功能下拉里还是搜索的结果"这种矛盾状态 ——
             * 手动选分类是更强的意图，应该覆盖之前的搜索。 */
            if (ActionSearchBox != null && !string.IsNullOrEmpty(ActionSearchBox.Text))
            {
                ActionSearchBox.Text = "";
            }

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
                EnsureLinkAction();
            }
            else if (mode == EditorMode.TypeText)
            {
                EnsureActionForCategory("typeText");
            }
            else if (mode == EditorMode.CustomKey)
            {
                EnsureCustomKeyAction();
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

        /* 换成音量 / 亮度时，上一个动作的参数（比如"移动 5 个字符"的 5）
         * 不能被继承成"步进 5%" —— 用户没设过却生效了，非常费解。 */
        if (IsStepArgAction(actionId) && !IsValidStep(binding.Arg)) binding.Arg = "";

        binding.Action = actionId;
        _host.SaveSettings();
        BuildKeyboard();
    }

    /// <summary>
    /// 选了「直达链接」分类时的默认动作。
    ///
    /// 与其它分类不同，这一类里已经有三个动作（网址 / 文件夹 / 文件）——
    /// 用户在这个键上原本绑的是「直达文件」时，重新点一下分类
    /// **不能**把它悄悄改成「直达网址」，那会把他填的路径当成网址打开。
    /// </summary>
    private void EnsureLinkAction()
    {
        if (_host == null) return;
        var binding = FindInCurrentLayer(CurrentCombo());
        if (binding != null && IsLinkAction(binding.Action)) return;
        EnsureActionForCategory(LinkUrlAction);
    }

    /// <summary>是不是「直达链接」分类下的三种动作之一。</summary>
    private static bool IsLinkAction(string? actionId) =>
        actionId is LinkUrlAction or LinkFolderAction or LinkFileAction;

    /// <summary>当前键绑的是「直达链接」里的哪一种（没绑则返回 null）。</summary>
    private string? CurrentLinkKind()
    {
        var b = FindInCurrentLayer(CurrentCombo());
        return IsLinkAction(b?.Action) ? b!.Action : null;
    }

    /// <summary>切换「直达网址 / 直达文件夹 / 直达文件」。</summary>
    private void SetLinkKind(string actionId)
    {
        if (_host == null || _suppressEvents) return;

        string combo = CurrentCombo();
        var binding = FindInCurrentLayer(combo);
        if (binding == null)
        {
            binding = new KeyBinding { Combo = combo };
            CurrentLayerKeys.Add(binding);
        }

        if (binding.Action == actionId) return;

        /* 网址、文件夹、文件的 Arg 是三种东西，互相不能复用：
         * 把 "https://github.com" 当文件夹路径去打开只会弹"文件夹不存在"，
         * 反过来把 "D:\Work" 当网址更糟（会拼成 http://d:\Work）。
         * 所以切换类型时清空目标，备注保留（用户起的名字通常还能用）。 */
        binding.Action = actionId;
        binding.Arg = "";
        _host.SaveSettings();

        _suppressEvents = true;
        WebUrlBox.Text = "";
        _suppressEvents = false;

        RefreshWebPageFields();
        BuildKeyboard();
        ShowBindStatus(actionId switch
        {
            LinkUrlAction => "已切换成「直达网址」—— 填一个网址即可",
            LinkFolderAction => "已切换成「直达文件夹」—— 填路径或点「浏览…」选一个",
            _ => "已切换成「直达文件」—— 点「浏览…」挑一个文件，或直接填路径",
        });
    }

    private void OnLinkKindUrl(object sender, RoutedEventArgs e) => SetLinkKind(LinkUrlAction);
    private void OnLinkKindFolder(object sender, RoutedEventArgs e) => SetLinkKind(LinkFolderAction);
    private void OnLinkKindFile(object sender, RoutedEventArgs e) => SetLinkKind(LinkFileAction);

    /// <summary>
    /// 挑一个文件夹填进目标框。
    ///
    /// 用 .NET 自带的 OpenFolderDialog（WPF 版，Win10 1809+ 都是系统原生对话框），
    /// 不引 WinForms，也不自己造轮子。
    /// </summary>
    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要直达的文件夹",
            Multiselect = false,
        };

        // 已经有合法路径时从那里开始浏览，省得每次从"此电脑"点起
        string current = Core.Actions.ActionContext.ExpandFolderPath(WebUrlBox.Text);
        if (Directory.Exists(current)) dlg.InitialDirectory = current;

        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FolderName)) return;

        WebUrlBox.Text = dlg.FolderName;          // TextChanged 会负责落盘
        ShowBindStatus($"文件夹已设置：{dlg.FolderName}");
    }

    /// <summary>
    /// 挑一个文件填进目标框（「直达文件」用）。
    ///
    /// 和文件夹那边一样用 .NET 自带的 OpenFileDialog（WPF 版），不引 WinForms。
    /// 过滤器只留「所有文件」而不按类型筛：要直达的文件可能是 .docx、.psd、
    /// .mp4 甚至没有扩展名的数据文件，替用户预设类型只会让他找不到自己的文件。
    /// </summary>
    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要直达的文件",
            Multiselect = false,
            CheckFileExists = true,
            Filter = "所有文件|*.*",
        };

        // 已经填过路径时从它所在的目录开始浏览
        string current = Core.Actions.ActionContext.ExpandPath(WebUrlBox.Text);
        try
        {
            string? dir = Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        catch { }

        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FileName)) return;

        WebUrlBox.Text = dlg.FileName;            // TextChanged 会负责落盘
        ShowBindStatus($"文件已设置：{dlg.SafeFileName}");
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
            var s when ReferenceEquals(s, CategoryBoxType) => (CategoryBoxType.SelectedItem as ComboBoxItem)?.Tag as string,
            var s when ReferenceEquals(s, CategoryBoxCustom) => (CategoryBoxCustom.SelectedItem as ComboBoxItem)?.Tag as string,
            _ => SelectedCategory,
        };
        if (tag == null) return;

        foreach (var box in new[]
                 { CategoryBox, CategoryBoxWinBind, CategoryBoxWeb, CategoryBoxType, CategoryBoxCustom })
        {
            if (ReferenceEquals(box, source)) continue;
            SelectTag(box, tag);
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
        else if (IsStepArgAction(actionId) && !IsValidStep(binding.Arg)) binding.Arg = "";
        _host.SaveSettings();

        var info = _host.Actions.Get(actionId);
        ActionDesc.Text = info?.Description ?? "";
        ShowArgPanelFor(actionId, info?.NeedsArg == true || IsStepArgAction(actionId));
        BuildKeyboard();
    }

    // ---------------- 键盘绘制 ----------------

    /// <summary>
    /// 在分类下拉里选中某个 Tag。
    ///
    /// 页面上有 5 个分类下拉（普通 / 窗口绑定 / 网页 / 直链 / 自定义），
    /// 它们永远显示同一个分类 —— 以前每个调用点都写一遍同样的 foreach，
    /// 改一处漏一处的风险很高，这里收成一个函数。
    /// </summary>
    private static void SelectTag(ComboBox box, string? tag, bool fallbackFirst = false)
    {
        foreach (ComboBoxItem it in box.Items)
        {
            if (it.Tag as string == tag) { box.SelectedItem = it; return; }
        }
        if (fallbackFirst && box.Items.Count > 0) box.SelectedIndex = 0;
    }

    /// <summary>当前层的触发键名（用于在键盘真实位置高亮它）。</summary>
    private string? CurrentTriggerKey =>
        _host?.Settings.Triggers.FirstOrDefault(t => t.LayerId == _layerId && t.Enabled)?.Key;

    private void BuildKeyboard()
    {
        if (_host == null) return;
        KeyboardPanel.Children.Clear();
        // 搜索框已移除：不再按关键字过滤，整把键盘始终完整显示
        string filter = "";
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

            /* 跨行键必须**拉伸填满**两行的高度。
             * 它的 Height 被 MakeKeycap 固定成 44/52，直接放进 RowSpan 会在
             * 两行之间留白，看起来"加号只有普通键那么大"、
             * 而不是标准 108 键那种高键。 */
            cap.Height = double.NaN;
            cap.VerticalAlignment = VerticalAlignment.Stretch;
            cap.Margin = new Thickness(1, 1, 1, 1);

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

        /* 修饰子层的提示必须能显示**组合**修饰键。
         *
         * 原来是个 switch，只认 Ctrl / Shift / Alt / Win 四个单值，
         * 录了 Ctrl+Shift 就落到 `_ => ""`，界面完全看不出"正在看一个修饰子层"，
         * 只看到"0 个绑定"—— 用户会以为绑定被删了。
         * 顺便把"怎么回到普通键"写在同一行。 */
        string modText = _layer == KeyModifiers.None ? "" : ModifierText(_layer);
        string backHint = _layer == KeyModifiers.None
            ? ""
            : "　·　点上方的「直接按」回到普通键";
        LayerHint.Text = $"当前：第 {_layerId} 层「{layerName}」{modText}（{bound} 个绑定）　·　{triggerText}{backHint}";

        /* 顶部提示里的触发键必须**跟着当前层变**。
         *
         * 之前这里写死"按住 CapsLock"，即便第 2 层配的是 F13，
         * 切过去仍提示按住大写键 —— 与实际触发方式完全对不上。 */
        UpdateTriggerBadge(trigger);

        CountText.Text = _layerId == 1
            ? $"短按触发键：{tapTitle}　·　点键盘上任意键即可修改它的功能，修改立刻生效。"
            : $"这一层的按键绑定独立于其他层。要改短按行为，去「触发键与层」页。";
        UpdateLayerButtons();
        RefreshBoundList();
    }

    /// <summary>
    /// 键帽上到底显示什么文字。
    ///
    /// 默认用 ShortTitle —— 长标题在 26px 宽的键帽里显示不全。
    /// 但有一批动作**自己就算好了键帽文字**，它们的 ShortTitle 只是分类名之类的
    /// 兜底，绝不能盖上去：
    ///   · 直达链接三类 / 输入文本 / 窗口绑定 → 各自的 LabelOf（域名 / 文件夹名 / 文字开头）；
    ///   · 自定义按键 → 备注 → 应用名 →「自定义」。
    ///
    /// ⚠ customKey 之前漏在这个名单外，键帽永远显示 ShortTitle「自定义」，
    /// 用户填的备注根本没机会显示 —— 这就是"备注保存了但键帽上没生效"的根因。
    /// 抽成纯函数是为了让自检能直接断言这个名单，不必去驱动界面。
    /// </summary>
    internal static string KeycapTitleOf(string actionId, string computedLabel, string? shortTitle)
    {
        if (string.IsNullOrWhiteSpace(shortTitle)) return computedLabel;
        if (actionId is "winBind" or "webPage" or "openFolder"
            or "openFile" or "typeText" or "customKey")
            return computedLabel;
        if (actionId.StartsWith("macro:", StringComparison.OrdinalIgnoreCase)) return computedLabel;
        return shortTitle!;
    }

    /// <summary>
    /// 建一个键帽；被搜索过滤掉时返回 null。
    /// </summary>
    private Border? BuildKeycapEntry(string label, string keyId, string filter,
        ref int bound, string? triggerKey)
    {
        string combo = KeyName.BuildComboEx(_layer, keyId);
        var binding = FindInCurrentLayer(combo);

        /* 只有"动作真实存在"才算已绑定。
         *
         * 这一条很关键：老配置文件里可能残留已删除的动作
         * （如 winbindActivate1、旧版的覆盖动作），
         * 如果只判断 Action != "doNothing"，这些键会被显示成"已绑定"、
         * 键帽上还会冒出一个原始动作 ID，用户看到的就是"明明没绑却显示绑了"。
         *
         * 现在的判定：动作非空 + 不是 doNothing + **能在动作表里找到**。 */
        bool isBound = binding is { IsBound: true }
                       && _host?.Actions.Get(binding.Action) != null;
        if (isBound) bound++;

        string actionTitle = "";
        string actionCategory = "";
        if (isBound && binding != null)
        {
            // 统一走 DescribeActionOf，键帽与"本层已绑定"列表的文字保证一致
            actionTitle = DescribeActionOf(binding);
            // 分类标识：用户要求"单看按键就知道绑的功能属于哪个分类"
            actionCategory = _host?.Actions.Get(binding.Action)?.Category ?? "";
        }

        string keycapTitle = isBound && binding != null
            ? KeycapTitleOf(binding.Action, actionTitle, _host?.Actions.Get(binding.Action)?.ShortTitle)
            : actionTitle;

        bool match = filter.Length == 0 ||
                     label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                     keyId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                     actionTitle.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                     actionCategory.Contains(filter, StringComparison.OrdinalIgnoreCase);
        if (!match && filter.Length > 0) return null;

        return MakeKeycap(label, keyId, isBound, keycapTitle, actionCategory, triggerKey);
    }

    /// <summary>
    /// 更新顶部「按住 XX + 下面的键」里的触发键名。
    ///
    /// 触发键可以是**复杂按键**（组合键），例如 Ctrl+Shift+K。
    /// 这种时候提示要写成「按住 Ctrl+Shift+K」，而不是只显示最后一个键。
    /// </summary>
    private void UpdateTriggerBadge(TriggerKey? trigger)
    {
        if (trigger == null)
        {
            TriggerKeyBadge.Text = "没有触发键";
            TriggerKeySuffix.Text = "这一层还没分配触发键，去「触发键与层」页设置";
            return;
        }

        string display = DescribeTriggerKey(trigger.Key);
        TriggerKeyBadge.Text = "按住 " + display;
        TriggerKeySuffix.Text = "+ 下面的键";
    }

    /// <summary>
    /// 把触发键的键名转成人类可读的形式。
    ///
    /// 设置里存的是组合键 ID（如 <c>caps_ctrl_shift_k</c>），
    /// 直接显示会是一串看不懂的编码 —— 这里还原成 "Ctrl+Shift+K"。
    /// </summary>
    private static string DescribeTriggerKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "（未设置）";

        // 单键（capslock / f13 / ralt…）直接查键名表
        var parsed = KeyName.ParseCombo(key);
        if (parsed != null)
        {
            var (mods, k) = parsed.Value;
            string name = KeyName.Display(k);
            return mods == KeyModifiers.None ? name : $"{ModifierText(mods)}+{name}";
        }

        // 不是组合键 ID，可能是纯键名
        int vk = KeyName.VkOf(key) ?? 0;
        return vk != 0 ? KeyName.Display(key) : key;
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

        /* 切层必须把"层内修饰键"复位。
         *
         * 不复位的话：在基础层点了「自定义按键…」录了个 Ctrl+Shift+K，
         * `_layer` 就停在 Ctrl|Shift；接着切到第 2、3 层，
         * 键盘上每个键算出来的 combo 都是 `ctrl_shift_xx`，自然一个都查不到，
         * 界面显示"0 个绑定"—— 用户看到的就是"所有层的东西都被清空了"。
         * 实际上绑定都在，只是视图卡在一个空的修饰子层上。 */
        _layer = KeyModifiers.None;
        _subLayerKey = "";
        SelectKey(_selectedKey);
    }

    /// <summary>
    /// 建一个键帽。
    /// </summary>
    /// <param name="triggerKey">当前层的触发键名（非 null 时该键会额外高亮 +
    /// 加"触发键"角标，用户一眼看到"我按哪个键进这一层"）。</param>
    private Border MakeKeycap(string label, string keyId, bool isBound,
        string actionTitle, string actionCategory, string? triggerKey)
    {
        // 触发键在键盘**真实位置**高亮（不再写死在左上角）
        bool isTrigger = triggerKey != null
                         && string.Equals(triggerKey, keyId, StringComparison.OrdinalIgnoreCase);
        bool isSelected = string.Equals(keyId, _selectedKey, StringComparison.OrdinalIgnoreCase);

        /* 三种视觉状态，一眼可分但**对比不过分**：
         *
         *   触发键（本层）  → 深蓝实心 + 白字 + ★
         *   已绑定功能      → 深蓝实心 + 白字
         *   未绑定          → 浅蓝底 + 灰蓝字
         *
         * 未绑定用浅蓝而不是纯白：纯白键帽和深蓝键帽并排时对比太硬，
         * 满屏键帽看着刺眼。浅蓝能"退到背景里"，深蓝才自然跳出来。 */
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
            background = (Brush)FindResource("Brush.AccentSoft");      // 浅蓝（柔和）
            borderBrush = (Brush)FindResource("Brush.AccentSoftBorder");
            textBrush = (Brush)FindResource("Brush.TextSecondary");
            subTextBrush = (Brush)FindResource("Brush.TextTertiary");
        }

        var border = new Border
        {
            /* **所有键帽等高**，间距才一致。
             *
             * 之前已绑定的键 52px、未绑定的 44px：同一行里只要有一个已绑定的键，
             * 行高就被撑到 52，行内未绑定的键顶部/底部留出 8px 空隙 ——
             * 看起来就是"有的键间距大、有的小"。
             *
             * 现在统一 52px：即使这一行全是未绑定键，多出来的空间也被
             * VerticalAlignment=Center 均分到上下，视觉间距仍然均匀。
             * 高度一致也让整个键盘的行距天然对齐。 */
            Height = KeycapHeight,
            Margin = new Thickness(KeycapGap / 2),
            VerticalAlignment = VerticalAlignment.Center,
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

        if (isBound)
        {
            /* ===== 已绑定：功能名当主角，键名退成小字 =====
             *
             * 用户要求：「原来的功能要缩小，而它附上的功能就要把字体
             * 显示尽可能的全，字体的大小也要比按键本身功能要大一点，
             * 占的位置也要比现在的要大」。
             *
             * 所以顺序反过来：先功能名（大字、占两行），再键名（小字）。
             * 键帽加高到 52px 才装得下。 */

            // ① 分类标识（小字，最上面，告诉用户"这是哪一类功能"）
            stack.Children.Add(new TextBlock
            {
                Text = Shorten(actionCategory, 4),
                FontSize = 6.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = subTextBrush,
                Opacity = 0.62,
                Margin = new Thickness(0, 0, 0, 1),
            });

            // ② 功能名：允许折两行，尽量显示全名
            stack.Children.Add(new TextBlock
            {
                Text = actionTitle,
                FontSize = 8.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 26,                    // 最多两行
                LineHeight = 9.5,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = textBrush,
            });

            // ③ 键名：缩小放在最下面
            stack.Children.Add(new TextBlock
            {
                Text = isTrigger ? "★" + label : label,
                FontSize = 6.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = subTextBrush,
                Opacity = 0.7,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }
        else
        {
            /* ===== 未绑定：只显示键名，居中大字 ===== */
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

    /// <summary>
    /// 重建"层内修饰键"按钮组。
    ///
    /// 每层可以配 0~10 个修饰键组合，所以按钮是**动态生成**的：
    /// 第一个永远是"直接按"（无修饰键），后面按配置逐个加。
    ///
    /// 性能与内存：只在内容真的变化时重建。这个方法是每次 BuildKeyboard
    /// 都会调的（层切换、按键编辑都触发），无脑重建会反复分配 Button 对象
    /// 并悬挂事件处理器。这里用一个"结构指纹"判断是否需要重建，
    /// 只需要更新选中态时走轻量路径。
    /// </summary>
    private void UpdateLayerButtons()
    {
        if (_host == null) return;

        var layer = _host.Settings.Layers.FirstOrDefault(l => l.Id == _layerId);
        var subs = layer?.EffectiveSubLayers ?? LayerOptions.DefaultSubLayers();

        // 结构指纹：层 ID + 每个子层的键与名字。相同则只刷新选中态。
        string signature = _layerId + "|" +
            string.Join(",", subs.Select(s => s.Key + ":" + s.Display));
        if (signature == _subLayerSignature && SubLayerPanel.Children.Count > 0)
        {
            RefreshSubLayerSelection();
            return;
        }
        _subLayerSignature = signature;

        SubLayerPanel.Children.Clear();

        void AddButton(string text, string subKey, string tip)
        {
            var b = new Button
            {
                Content = text,
                Style = (Style)FindResource("Button.Base"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 5, 0),
                ToolTip = tip,
                // Tag 记住这个按钮对应的子层键名，选中态刷新时读它
                Tag = subKey ?? "",
            };
            b.Click += OnSubLayerButtonClick;
            SubLayerPanel.Children.Add(b);
        }

        // 第一项：直接按（无子层），始终存在
        AddButton("直接按", "", "进入层后直接按的键");

        /* 后面按该层配置的子层逐个生成。
         *
         * 子层键可以是任意物理键（ctrl / tab / capslock…），
         * 也可以起自定义名字 —— 按钮上优先显示名字。 */
        foreach (var sub in subs)
        {
            if (string.IsNullOrWhiteSpace(sub.Key)) continue;
            AddButton("+ " + sub.Display, sub.Key,
                $"进入层后再按住 {sub.Display} 按的键");
        }

        RefreshSubLayerSelection();
    }

    /// <summary>只刷新子层按钮的选中态（不重建控件，开销极小）。</summary>
    private void RefreshSubLayerSelection()
    {
        foreach (var child in SubLayerPanel.Children)
        {
            if (child is not Button b || b.Tag is not string key) continue;
            bool active = string.Equals(_subLayerKey, key, StringComparison.OrdinalIgnoreCase);

            b.Background = (Brush)FindResource(active ? "Brush.AccentSoft" : "Brush.Surface");
            b.BorderBrush = (Brush)FindResource(active ? "Brush.AccentSoftBorder" : "Brush.BorderStrong");
            b.Foreground = (Brush)FindResource(active ? "Brush.Accent" : "Brush.TextSecondary");
            b.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>
    /// 子层按钮点击。
    ///
    /// 用共享处理器 + Tag 取修饰键，而不是每个按钮一个闭包 ——
    /// 闭包会捕获 this 并在按钮被移除后仍然存活一段时间，属于隐性泄漏。
    /// </summary>
    private void OnSubLayerButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key }) SelectSubLayer(key);
    }

    /// <summary>切到指定子层（键名，空字符串 = 直接按）。</summary>
    private void SelectSubLayer(string subKey)
    {
        _subLayerKey = subKey ?? "";
        _layer = KeyModifiersOf(subKey);
        SelectKey(_selectedKey);
    }

    /// <summary>
    /// 子层键名 → 修饰位标记。
    ///
    /// ctrl/shift/alt/win 这类修饰键返回对应位；其它键（tab、capslock）
    /// 返回 None —— 它们的 combo 用不带修饰前缀的形式。
    /// </summary>
    private static KeyModifiers KeyModifiersOf(string? subKey)
    {
        if (string.IsNullOrWhiteSpace(subKey)) return KeyModifiers.None;
        var m = KeyModifiers.None;
        foreach (string part in subKey.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            m |= part.ToLowerInvariant() switch
            {
                "ctrl" => KeyModifiers.Ctrl,
                "shift" => KeyModifiers.Shift,
                "alt" => KeyModifiers.Alt,
                "win" => KeyModifiers.Win,
                _ => KeyModifiers.None,
            };
        }
        return m;
    }

    /// <summary>修饰位标记 → 子层键名（从旧接口切换时用）。</summary>
    private static string LayerKeyOf(KeyModifiers mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("ctrl");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("shift");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("alt");
        if (mods.HasFlag(KeyModifiers.Win)) parts.Add("win");
        return string.Join("_", parts);
    }

    private string? _subLayerSignature;

    /// <summary>配置当前层的子层（最多 10 个，可用任意键 + 自定义名字）。</summary>
    private void OnEditSubLayers(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var layer = _host.Settings.Layers.FirstOrDefault(l => l.Id == _layerId);
        if (layer == null) return;

        // 传当前子层定义（深拷贝，取消时不污染原配置）
        var current = layer.EffectiveSubLayers
            .Select(s => new SubLayerDef { Key = s.Key, Name = s.Name })
            .ToList();

        var dlg = new UI.Windows.SubLayerEditorWindow(layer.Name, current)
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true) return;

        layer.SubLayers = dlg.SelectedSubLayers;
        // 新字段生效后清掉旧字段，避免"改过又用回旧值"
        layer.SubLayerModifiers.Clear();
        _host.SaveSettings();

        // 如果当前停留的子层已被删除，回到"直接按"
        if (!string.IsNullOrEmpty(_subLayerKey) &&
            !layer.EffectiveSubLayers.Any(s =>
                string.Equals(s.Key, _subLayerKey, StringComparison.OrdinalIgnoreCase)))
        {
            _subLayerKey = "";
            _layer = KeyModifiers.None;
        }

        UpdateLayerButtons();
        SelectKey(_selectedKey);
        ShowBindStatus($"「{layer.Name}」现有 {layer.EffectiveSubLayers.Count} 个子层");
    }

    private void RefreshBoundList()
    {
        if (_host == null) return;
        var items = new List<BoundVm>();
        foreach (var binding in CurrentLayerKeys.Where(k => k.IsBound))
        {
            var parsed = KeyName.ParseCombo(binding.Combo);
            if (parsed == null) continue;

            // 动作已不在动作表里（旧配置残留）→ 不列出来，避免误导
            if (_host?.Actions.Get(binding.Action) == null) continue;

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
                // 可编辑下拉要手动同步显示文本，否则显示区是空白
                ActionBox.Text = item.Content as string ?? "";
                break;
            }
        }
        ArgBox.Text = binding?.Arg ?? "";

        // 同步另外三个"专用模式"的分类下拉
        foreach (ComboBoxItem wi in CategoryBoxWinBind.Items)
        {
            if (wi.Tag as string == info?.Category) { CategoryBoxWinBind.SelectedItem = wi; break; }
        }
        foreach (ComboBoxItem wb2 in CategoryBoxWeb.Items)
        {
            if (wb2.Tag as string == info?.Category) { CategoryBoxWeb.SelectedItem = wb2; break; }
        }
        foreach (ComboBoxItem tb in CategoryBoxType.Items)
        {
            if (tb.Tag as string == info?.Category) { CategoryBoxType.SelectedItem = tb; break; }
        }

        _suppressEvents = false;

        /* 按动作所属分类决定用哪个模式 ——
         *   winBind   → 两个按钮 + 当前绑定卡片
         *   webPage   → 网址 + 备注
         *   其它      → 功能下拉 + 参数框 */
        var mode = actionId switch
        {
            "winBind" => EditorMode.WinBind,
            "webPage" or "openFolder" or "openFile" => EditorMode.WebPage,   // 直达链接的三种类型
            "typeText" => EditorMode.TypeText,
            "customKey" => EditorMode.CustomKey,
            _ => EditorMode.Normal,
        };
        ApplyEditorMode(mode);

        if (mode == EditorMode.Normal)
        {
            ActionDesc.Text = info?.Description ?? "";
            ShowArgPanelFor(actionId,
                info?.NeedsArg == true || IsCountArgAction(actionId) || IsStepArgAction(actionId));
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

    /// <summary>
    /// 音量 / 亮度：参数是"按一次调多少个百分点"。
    ///
    /// 以前这两个动作只能整体用设置页里的幅度，想让"音量 +"和"音量 -"
    /// 走不同的步子就没办法。参数本来就支持覆盖（<c>StepOr</c>），
    /// 但界面上没有入口 —— 等于藏起来了。现在给它们一个明确的输入框。
    /// </summary>
    private static bool IsStepArgAction(string actionId) => actionId is
        "volumeUp" or "volumeDown" or "brightnessUp" or "brightnessDown";

    private static bool IsVolumeStepAction(string actionId)
        => actionId is "volumeUp" or "volumeDown";

    /// <summary>参数是不是一个有效的步进（1~100 的整数）。</summary>
    private static bool IsValidStep(string? arg)
        => int.TryParse(arg, out int v) && v is >= 1 and <= 100;

    /// <summary>这个步进动作"留空"时实际用多少（设置页里的幅度）。</summary>
    private int DefaultStepFor(string actionId)
    {
        var g = _host?.Settings.Global;
        if (g == null) return VolDefaultStep;
        return IsVolumeStepAction(actionId)
            ? Math.Clamp(g.VolumeStep, 1, 50)
            : Math.Clamp(g.BrightnessStep, 1, 50);
    }

    /// <summary>系统音量每步固定 2%，设置里不允许低于它。</summary>
    private const int VolDefaultStep = 5;

    /// <summary>按动作类型决定参数面板的样子。</summary>
    private void ShowArgPanelFor(string actionId, bool needsArg, bool isWinBind = false)
    {
        bool step = IsStepArgAction(actionId);

        ArgPanel.Visibility = (needsArg || step) && !isWinBind
            ? Visibility.Visible
            : Visibility.Collapsed;

        ArgDefaultButton.Visibility = step ? Visibility.Visible : Visibility.Collapsed;

        ArgLabel.Text = actionId switch
        {
            "openUrl" => "网址",
            "runProgram" => "程序路径或命令",
            "wrapSelection" => "起始符号",
            "volumeUp" or "volumeDown" => "音量步进（%）",
            "brightnessUp" or "brightnessDown" => "亮度步进（%）",
            _ => "次数 / 参数",
        };

        int def = DefaultStepFor(actionId);

        ArgHint.Text = actionId switch
        {
            "openUrl" => "例如 https://github.com",
            "runProgram" => "例如 notepad.exe",
            "wrapSelection" => "例如 ( 会把选中文字变成 (文字)",
            "volumeUp" or "volumeDown" =>
                $"按一次调多少音量：留空 = 用设置里的 {def}%。"
                + "按住不放时会自动切成 1% 细调，一路平滑地加上去",
            "brightnessUp" or "brightnessDown" =>
                $"按一次调多少亮度：留空 = 用设置里的 {def}%。"
                + "按住不放时会自动切成 1% 细调，一路平滑地加上去",
            _ => "不填默认 1，填 5 表示一次移动 5 个字符",
        };

        // 步进最多三位（1~100），次数最多三位（1~200）
        ArgBox.MaxLength = step || IsCountArgAction(actionId) ? 3 : 0;
    }

    /// <summary>「用默认」：清空参数，回到设置页里的调节幅度。</summary>
    private void OnArgUseDefault(object sender, RoutedEventArgs e)
    {
        // 走 TextChanged 那条路，绑定与保存都会自动跟上
        ArgBox.Text = "";
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
    /// 仅供渲染：切到「系统 → 直达链接」，确认智能直达这个动作
    /// 真的出现在功能下拉里、说明文字也写对了。
    ///
    /// 刻意**不调用** <c>ApplySelectedAction</c> —— 那会真的改写当前按键的绑定，
    /// 截图是"观察"不是"操作"，不能顺手改配置。
    /// </summary>
    public bool ShowSmartOpenForRender()
    {
        if (_host == null) return false;

        _suppressEvents = true;
        try
        {
            foreach (ComboBoxItem it in CategoryBox.Items)
            {
                if (it.Tag as string == "智能直达")
                {
                    CategoryBox.SelectedItem = it;
                    break;
                }
            }

            ApplyEditorMode(EditorMode.Normal);
            BuildActionBox();

            foreach (ComboBoxItem it in ActionBox.Items)
            {
                if (it.Tag as string == "smartOpen")
                {
                    ActionBox.SelectedItem = it;
                    break;
                }
            }
        }
        finally
        {
            _suppressEvents = false;
        }

        var info = _host.Actions.Get("smartOpen");
        ActionDesc.Text = info?.Description ?? "";
        ShowArgPanelFor("smartOpen", needsArg: false);

        return ActionBox.SelectedItem is ComboBoxItem { Tag: "smartOpen" };
    }

    /// <summary>
    /// 自检用：把视图切到「自定义按键」面板（内容最高），强制布局后
    /// 报出底部两个按钮的位置 —— 用来验证它们不会被挤出可视区。
    ///
    /// 为什么这条要专门测：以前中间几行是 Auto 高度，面板一展开就把
    /// 「清除绑定」「测试一下」推到卡片外面，怎么滚都看不到
    /// （溢出的是 Grid 行，不是 ScrollViewer 的内容，滚动条根本不出现）。
    /// </summary>
    public (bool Visible, double Bottom, double ViewHeight, double ScrollHeight) FooterForTest(
        double width, double height)
    {
        ApplyEditorMode(EditorMode.CustomKey);

        Measure(new Size(width, height));
        Arrange(new Rect(0, 0, width, height));
        UpdateLayout();

        double bottom = 0;
        try
        {
            var p = ClearBindButton.TransformToAncestor(this).Transform(new Point(0, 0));
            bottom = p.Y + ClearBindButton.ActualHeight;
        }
        catch
        {
            // 没进可视树时 TransformToAncestor 会抛，按"不可见"处理
        }

        /* 注意用 ActualWidth/Height 而不是 IsVisible：
         * IsVisible 要求控件挂在一个真实窗口的渲染树上，
         * 自检里的视图是"离线"布局的，IsVisible 恒为 false，
         * 用它判断会得到假失败。尺寸不为 0 才说明它真的被排版出来了。 */
        bool laidOut = ClearBindButton.ActualWidth > 0 && ClearBindButton.ActualHeight > 0;
        return (laidOut, bottom, ActualHeight, EditorScroll.ActualHeight);
    }

    /// <summary>
    /// 仅供渲染：把界面切到「自定义按键」面板，好截一张图确认各个控件都画出来了。
    /// </summary>
    public bool ShowCustomKeyForRender()
    {
        if (_host == null) return false;

        _suppressEvents = true;
        try
        {
            foreach (ComboBoxItem it in CategoryBox.Items)
            {
                if (it.Tag as string == CustomKeyCategory)
                {
                    CategoryBox.SelectedItem = it;
                    break;
                }
            }
        }
        finally
        {
            _suppressEvents = false;
        }

        ApplyEditorMode(EditorMode.CustomKey);
        SaveCustomSpec(new CustomKeySpec
        {
            Key = "ctrl+shift+k",
            AppPath = @"C://Program Files//Tencent//Weixin//Weixin.exe",
            RunAsAdmin = false,
            BringFront = false,
            WaitAfterLaunchMs = 900,
        });

        // 备注也填一个，截图里能看到键帽上显示的是"微信"而不是进程名
        var b = FindInCurrentLayer(CurrentCombo());
        if (b != null)
        {
            b.Note = "微信";
            _host.SaveSettings();
            BuildKeyboard();
        }

        return CustomKeyPicker.Visibility == Visibility.Visible;
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

            /* 用户自定义名优先。
             *
             * exe 名经常不是软件名（idea64.exe 是 IDEA、Code.exe 是 VS Code），
             * 只显示文件名会让人认不出绑的是哪个软件。 */
            if (!string.IsNullOrWhiteSpace(binding.Note)) return binding.Note!.Trim();

            try { return Path.GetFileNameWithoutExtension(binding.Arg); }
            catch { return "切换窗口"; }
        }

        if (id == "webPage")
            return Core.Actions.UrlUtil.LabelOf(binding.Note, binding.Arg);

        // 直达文件夹：备注优先，否则用文件夹名（"项目"比 "D:\Work\项目" 好认）
        if (id == "openFolder")
            return FolderLabelOf(binding.Note, binding.Arg);

        // 直达文件：备注优先，否则用文件名
        if (id == "openFile")
            return FileLabelOf(binding.Note, binding.Arg);

        // 输入文本：备注优先，否则显示文字开头几个字
        if (id == "typeText")
            return TypeTextLabelOf(binding.Note, binding.Arg);

        // 自定义按键：备注优先，否则用绑定的应用名（"微信"比"发送按键到应用"好认）
        if (id == "customKey")
            return CustomKeyLabelOf(binding.Note, CustomKeySpec.FromArg(binding.Arg));

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
    /// 直达文件夹在键帽上的标签：备注优先，否则用文件夹名。
    ///
    /// 路径通常比名字长得多（"D:\Work\2026\项目文档"），
    /// 直接放键帽会挤爆；而"直达文件夹"又完全看不出绑的是哪个目录。
    /// 静态是为了让自检能直接验。
    /// </summary>
    internal static string FolderLabelOf(string? note, string? path)
    {
        if (!string.IsNullOrWhiteSpace(note)) return note.Trim();
        if (string.IsNullOrWhiteSpace(path)) return "文件夹";

        string p = Core.Actions.ActionContext.ExpandFolderPath(path);
        if (p.Length == 0) return "文件夹";

        // shell:Downloads 这类虚拟路径没有"最后一段"，直接显示原值
        if (p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return p["shell:".Length..];

        string name;
        try { name = Path.GetFileName(p.TrimEnd('\\')); }
        catch { return "文件夹"; }

        // "D:\" 这种根目录：GetFileName 返回空，退回盘符
        if (string.IsNullOrWhiteSpace(name)) name = p.TrimEnd('\\');
        return name.Length <= 8 ? name : name[..8] + "…";
    }

    /// <summary>
    /// 直达文件在键帽上的标签：备注优先，否则用文件名（去掉扩展名）。
    ///
    /// 和文件夹同一个道理：完整路径放不进键帽，而"直达文件"四个字
    /// 完全看不出绑的是哪个文件。扩展名去掉是因为 `.docx` 不带信息量，
    /// 保留名字反而更容易一眼认出（"方案" vs "方案.docx"）。
    /// </summary>
    internal static string FileLabelOf(string? note, string? path)
    {
        if (!string.IsNullOrWhiteSpace(note)) return note.Trim();
        if (string.IsNullOrWhiteSpace(path)) return "文件";

        string p = Core.Actions.ActionContext.ExpandPath(path);
        if (p.Length == 0) return "文件";

        string name;
        try { name = Path.GetFileNameWithoutExtension(p); }
        catch { return "文件"; }

        if (string.IsNullOrWhiteSpace(name))
        {
            try { name = Path.GetFileName(p); } catch { return "文件"; }
        }
        if (string.IsNullOrWhiteSpace(name)) return "文件";

        return name.Length <= 8 ? name : name[..8] + "…";
    }

    /// <summary>
    /// 输入文本在界面上的标签：有备注用备注，否则取第一行开头几个字。
    ///
    /// 为什么要截断：一段文字可能有几百字，直接放进键帽会把整个键盘撑变形；
    /// 而"输入文本"这四个字又完全看不出绑的是什么内容。
    ///
    /// 静态是为了让自检能直接验，不必驱动界面。
    /// </summary>
    internal static string TypeTextLabelOf(string? note, string? text)
    {
        if (!string.IsNullOrWhiteSpace(note)) return note.Trim();
        if (string.IsNullOrWhiteSpace(text)) return "输入文本";

        // 只取第一行，把换行/制表压成空格 —— 键帽里不能出现多行
        string first = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        // 连续空格压成一个，避免显示成 "a    b"
        while (first.Contains("  ", StringComparison.Ordinal)) first = first.Replace("  ", " ");

        if (first.Length == 0) return "输入文本";
        return first.Length <= 6 ? first : first[..6] + "…";
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

    // ---------------- 输入文本 ----------------

    /// <summary>把多行文本框与备注框刷成当前绑定的内容。</summary>
    private void RefreshTypeTextField()
    {
        var b = FindInCurrentLayer(CurrentCombo());
        bool isType = b?.Action == "typeText";

        _suppressEvents = true;
        TypeTextBox.Text = isType ? (b!.Arg ?? "") : "";
        TypeNoteBox.Text = isType ? (b.Note ?? "") : "";
        _suppressEvents = false;
    }

    /// <summary>文本框内容变化：写进当前按键的绑定。</summary>
    private void OnTypeTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;

        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || b.Action != "typeText") return;

        b.Arg = TypeTextBox.Text ?? "";
        _host.SaveSettings();
        BuildKeyboard();

        if (!string.IsNullOrEmpty(b.Arg))
        {
            string preview = b.Arg.Length > 12 ? b.Arg[..12] + "…" : b.Arg;
            ShowBindStatus($"已保存要输入的文字：「{preview.Replace("\r", " ").Replace("\n", " ")}」");
        }
    }

    /// <summary>
    /// 备注变化：写进 <see cref="KeyBinding.Note"/>。
    ///
    /// 备注只影响**显示**（键帽 + 已绑定列表），不参与输入 ——
    /// 用户要把一长串邮箱/地址起个短名字，键帽上才看得清。
    /// 留空即删除备注，键帽回退显示文字开头。
    /// </summary>
    private void OnTypeNoteChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;

        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || b.Action != "typeText") return;

        string note = (TypeNoteBox.Text ?? "").Trim();
        b.Note = string.IsNullOrEmpty(note) ? null : note;
        _host.SaveSettings();
        BuildKeyboard();

        ShowBindStatus(string.IsNullOrEmpty(note)
            ? "备注已清空，键帽改显示文字开头"
            : $"备注已保存：「{note}」—— 键帽上会显示这个名字");
    }

    /// <summary>
    /// 把「直达链接」面板刷成当前绑定的内容：
    /// 类型按钮的高亮、目标框的标签、浏览/取标题按钮的显隐，全部跟着走。
    /// </summary>
    private void RefreshWebPageFields()
    {
        var b = FindInCurrentLayer(CurrentCombo());
        string kind = IsLinkAction(b?.Action) ? b!.Action! : LinkUrlAction;
        bool isUrl = kind == LinkUrlAction;
        bool isFolder = kind == LinkFolderAction;
        bool isFile = kind == LinkFileAction;

        _suppressEvents = true;
        WebUrlBox.Text = IsLinkAction(b?.Action) ? (b!.Arg ?? "") : "";
        WebNoteBox.Text = b?.Note ?? "";
        _suppressEvents = false;

        // 类型按钮：选中的那个用主按钮样式，另外两个用普通样式
        LinkKindUrlButton.Style = (Style)FindResource(isUrl ? "Button.Primary" : "Button.Base");
        LinkKindFolderButton.Style = (Style)FindResource(isFolder ? "Button.Primary" : "Button.Base");
        LinkKindFileButton.Style = (Style)FindResource(isFile ? "Button.Primary" : "Button.Base");

        LinkTargetLabel.Text = isUrl ? "网址" : isFolder ? "文件夹路径" : "文件路径";
        WebUrlBox.ToolTip = isUrl
            ? "例如 https://github.com；不带 http 也会自动补全"
            : isFolder
                ? @"例如 D:\Work\项目；支持 %USERPROFILE%、~ 和 shell:Downloads"
                : @"例如 D:\文档\方案.docx；支持 %USERPROFILE%、~ 和正斜杠";

        BrowseFolderButton.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
        BrowseFileButton.Visibility = isFile ? Visibility.Visible : Visibility.Collapsed;
        FetchTitleButton.Visibility = isUrl ? Visibility.Visible : Visibility.Collapsed;

        WebFetchStatus.Text = string.IsNullOrWhiteSpace(b?.Arg) || isUrl
            ? ""
            : isFolder
                ? (Directory.Exists(Core.Actions.ActionContext.ExpandPath(b!.Arg))
                    ? "文件夹存在，按这个键就会在资源管理器里打开它。"
                    : "这个路径现在不存在 —— 按下去会提示「文件夹不存在」。")
                : (File.Exists(Core.Actions.ActionContext.ExpandPath(b!.Arg))
                    ? "文件存在，按这个键就会用默认程序打开它。"
                    : "这个路径现在不存在 —— 按下去会提示「文件不存在」。");

        LinkHint.Text = isUrl
            ? "填网址时会自动抓网页标题当备注（不满意可以改）。备注会显示在键帽上。"
            : isFolder
                ? @"不填备注时，键帽上显示文件夹名。路径支持 %USERPROFILE%\Desktop、~\Downloads 和 shell:Downloads 这几种写法。"
                : "不填备注时，键帽上显示文件名。会按系统文件关联打开（和双击一样），不用选程序。";
    }

    private void OnWebUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || !IsLinkAction(b.Action)) return;

        b.Arg = WebUrlBox.Text.Trim();
        _host.SaveSettings();
        BuildKeyboard();          // 键帽上要跟着变

        switch (b.Action)
        {
            case LinkUrlAction:
                // 网址填完就去抓标题当备注（用户要求"一定要先拉一下"）
                MaybeFetchTitle();
                break;

            case LinkFolderAction:
                // 文件夹：立刻反馈"这条路径到底存不存在"，别等用户按了才发现
                WebFetchStatus.Text = string.IsNullOrWhiteSpace(b.Arg)
                    ? ""
                    : (Directory.Exists(Core.Actions.ActionContext.ExpandPath(b.Arg))
                        ? "文件夹存在，按这个键就会在资源管理器里打开它。"
                        : "这个路径现在不存在 —— 按下去会提示「文件夹不存在」。");
                break;

            default:
                // 文件：同样立刻告诉用户"这个文件在不在"
                WebFetchStatus.Text = string.IsNullOrWhiteSpace(b.Arg)
                    ? ""
                    : (File.Exists(Core.Actions.ActionContext.ExpandPath(b.Arg))
                        ? "文件存在，按这个键就会用默认程序打开它。"
                        : "这个路径现在不存在 —— 按下去会提示「文件不存在」。");
                break;
        }
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
        if (b == null || !IsLinkAction(b.Action)) return;

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

        // 自定义名称（备注）回填到输入框，不覆盖用户已写的内容
        var cur = FindInCurrentLayer(CurrentCombo());
        _suppressEvents = true;
        WinBindNoteBox.Text = cur?.Action == "winBind" ? (cur.Note ?? "") : "";
        _suppressEvents = false;

        if (has)
        {
            // 显示名优先用自定义名，否则用 exe 文件名
            WinBindName.Text = string.IsNullOrWhiteSpace(cur?.Note)
                ? Path.GetFileNameWithoutExtension(exe)
                : cur!.Note!.Trim();
            WinBindPath.Text = exe;
            WinBindClearButton.Visibility = Visibility.Visible;
            WinBindHint.Text = "按这个键：程序没开就自动启动；已开着就切过去；"
                             + "已经在前台时再按一次会最小化。";
        }
        else if (!string.IsNullOrWhiteSpace(exe))
        {
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

    /// <summary>窗口绑定的自定义显示名（exe 名不一定是软件名）。</summary>
    private void OnWinBindNoteChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var b = FindInCurrentLayer(CurrentCombo());
        if (b == null || b.Action != "winBind") return;

        string note = WinBindNoteBox.Text.Trim();
        b.Note = string.IsNullOrWhiteSpace(note) ? null : note;
        _host.SaveSettings();
        BuildKeyboard();

        if (!string.IsNullOrWhiteSpace(note))
            ShowBindStatus($"显示名称已保存：「{note}」—— 键帽上会显示这个名字");
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

        /* 换程序时**保留**用户写的显示名 —— 他可能只是换了个安装路径。
         * 但如果新程序的 exe 名和旧备注配对不上（比如从 notepad 换成 chrome），
         * 保留旧名字反而误导，所以只在完全没备注时不动。 */

        _host.SaveSettings();

        RefreshWinBindPanel();
        BuildKeyboard();

        string name = string.IsNullOrWhiteSpace(binding.Note)
            ? Path.GetFileNameWithoutExtension(exePath)
            : binding.Note!.Trim();
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


    /// <summary>层内修饰键切换后要刷新（键盘上的绑定标记会变）。</summary>
    private void SelectModifierLayer(KeyModifiers mods)
    {
        _subLayerKey = LayerKeyOf(mods);
        _layer = mods;
        SelectKey(_selectedKey);
    }



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

        /* 从**搜索结果**里选了一个功能 → 要把分类切到它所属的那一类。
         *
         * 否则会出现：搜"删除"选中「删除整行」，但左边的分类还停在
         * 「面板」，功能下拉里显示的又是搜索结果 —— 用户看不出
         * 这个功能到底属于哪个分类。
         *
         * 切分类之前先清空搜索框（否则 BuildActionBox 仍按搜索过滤）。 */
        if (info != null && !string.IsNullOrEmpty(ActionSearchBox?.Text))
        {
            _suppressEvents = true;
            try
            {
                ActionSearchBox.Text = "";

                foreach (ComboBoxItem it in CategoryBox.Items)
                {
                    if (it.Tag as string == info.Category)
                    {
                        CategoryBox.SelectedItem = it;
                        CategoryBoxWinBind.SelectedItem = it;
                        CategoryBoxWeb.SelectedItem = it;
                        CategoryBoxType.SelectedItem = it;
                        break;
                    }
                }
                BuildActionBox();

                // 把刚选的动作重新选上（BuildActionBox 会重置到第一项）
                foreach (ComboBoxItem it in ActionBox.Items)
                {
                    if (it.Tag is string tag && tag.Equals(actionId, StringComparison.OrdinalIgnoreCase))
                    {
                        ActionBox.SelectedItem = it;
                        break;
                    }
                }
            }
            finally
            {
                _suppressEvents = false;
            }

            // 不要走 SelectKey（它会重新按分类定位），这里已经同步好了
            BuildKeyboard();
            return;
        }

        SelectKey(_selectedKey);
    }

    private void OnArgChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var binding = FindInCurrentLayer(CurrentCombo());
        if (binding == null) return;

        string text = ArgBox.Text.Trim();

        /* 步进类参数只收数字。
         *
         * 用户很自然会写「5%」或「5个点」，而 <c>int.TryParse</c> 认不出来 ——
         * 结果就是"我明明填了 5，按一下还是跳 10%"，看着像没保存。
         * 这里直接把非数字滤掉，顺手夹到 1~100，填什么都是有效值。 */
        if (IsStepArgAction(binding.Action) && text.Length > 0)
        {
            string digits = new(text.Where(char.IsDigit).Take(3).ToArray());

            if (digits.Length > 0 && int.TryParse(digits, out int n))
            {
                if (n > 100) digits = "100";
                else if (n == 0) digits = "1";      // 0 等于"按了不动"，没有意义
            }

            if (digits != ArgBox.Text)
            {
                _suppressEvents = true;
                try
                {
                    ArgBox.Text = digits;
                    ArgBox.CaretIndex = digits.Length;
                }
                finally { _suppressEvents = false; }
            }

            text = digits;
            UpdateStepHint(binding.Action, text);
        }

        binding.Arg = text;
        _host.SaveSettings();
    }

    /// <summary>步进框下面那行提示跟着输入变，让用户立刻看到"实际会用多少"。</summary>
    private void UpdateStepHint(string actionId, string text)
    {
        int def = DefaultStepFor(actionId);
        string unit = IsVolumeStepAction(actionId) ? "音量" : "亮度";

        if (string.IsNullOrEmpty(text))
        {
            ArgHint.Text = $"留空 = 用设置里的 {def}%：按一次{unit}变 {def}%";
            return;
        }

        if (!int.TryParse(text, out int v)) return;

        string extra = string.Empty;
        if (IsVolumeStepAction(actionId))
        {
            /* 系统音量是按档走的（通常 2%），设定值不是整数倍时要累计补偿。
             *
             * 这里**不能**去问 VolumeService.StepPercent()：那是 COM 调用，
             * 会在界面线程上把音频端点建起来，而动作队列是另一条 STA 线程 ——
             * 跨单元拿同一个接口指针会出问题。所以只做算术提示。 */
            if (v % 2 != 0)
                extra = $"（系统每档 2%，会按次数平均凑成 {v}%）";
        }

        ArgHint.Text = $"按一次{unit}变 {v}%{extra}；按住不放仍按 1% 细调";
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

    // ---------------- 自定义按键面板 ----------------

    /// <summary>
    /// 选了「自定义按键」分类时，把当前键的动作切成 customKey。
    ///
    /// ⚠ 只在"不是 customKey"时才改，绝不动已有的参数 ——
    /// 用户在这个分类里反复点来点去，不能把他填好的键和应用冲掉。
    /// </summary>
    private void EnsureCustomKeyAction()
    {
        if (_host == null) return;
        var binding = FindInCurrentLayer(CurrentCombo());
        if (binding != null && binding.Action == "customKey") return;
        EnsureActionForCategory("customKey");
    }

    /// <summary>当前键的「自定义按键」参数（没绑这一类就返回空参数）。</summary>
    private CustomKeySpec CurrentCustomSpec()
    {
        var b = FindInCurrentLayer(CurrentCombo());
        return b != null && b.Action == "customKey"
            ? CustomKeySpec.FromArg(b.Arg)
            : new CustomKeySpec();
    }

    /// <summary>
    /// 保存参数到当前键。
    ///
    /// 注意这里**只动这一个绑定**，不碰 _layer / _selectedKey / 其它层 ——
    /// 早先那版捕获完会顺手改掉当前层内修饰键和选中键，
    /// 结果整个视图跳到一个空的修饰子层，看起来像"所有绑定都被清空了"。
    /// </summary>
    private void SaveCustomSpec(CustomKeySpec spec)
    {
        if (_host == null) return;

        string combo = CurrentCombo();
        var binding = FindInCurrentLayer(combo);
        if (binding == null)
        {
            binding = new KeyBinding { Combo = combo };
            CurrentLayerKeys.Add(binding);
        }

        binding.Action = "customKey";
        binding.Arg = spec.ToArg();
        _host.SaveSettings();

        BuildKeyboard();          // 键帽上的名字要跟着变
        RefreshCustomKeyPanel();
    }

    /// <summary>把面板刷成当前绑定的内容。</summary>
    private void RefreshCustomKeyPanel()
    {
        if (_host == null || CustomKeyPicker == null) return;

        var spec = CurrentCustomSpec();

        _suppressEvents = true;
        CustomAppBox.Text = spec.AppPath;
        CustomAdminBox.IsChecked = spec.RunAsAdmin;
        CustomMainWindowBox.IsChecked = spec.BringFront;
        CustomWaitBox.Text = spec.WaitAfterLaunchMs.ToString();
        CustomNoteBox.Text = FindInCurrentLayer(CurrentCombo())?.Note ?? "";
        _suppressEvents = false;

        // 捕获到的键：没设时把按钮文案留着，设了就显示成键帽
        bool hasKey = !string.IsNullOrWhiteSpace(spec.Key);
        CustomKeyBadge.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
        CustomKeyBadgeText.Text = hasKey ? CustomKeyRunner.ComboDisplay(spec.Key) : "";
        CustomKeyCaptureButton.Content = hasKey ? "重新设置…" : "按下要发送的键…";

        // 应用状态：路径存不存在直接说，别等按下去才报错
        if (string.IsNullOrWhiteSpace(spec.AppPath))
        {
            CustomAppStatus.Text = "";
        }
        else if (File.Exists(spec.AppPath))
        {
            CustomAppStatus.Text = $"已找到：{Path.GetFileName(spec.AppPath)}（进程名 {spec.ProcessName}）";
        }
        else if (!spec.AppPath.Contains('\\') && !spec.AppPath.Contains('/'))
        {
            // 只填了 exe 名字：靠进程名匹配，也能用
            CustomAppStatus.Text = $"按进程名「{spec.ProcessName}」匹配（没有填完整路径）";
        }
        else
        {
            CustomAppStatus.Text = "这个路径现在不存在 —— 按下去会启动失败";
        }

        CustomKeyHint.Text = spec.IsReady
            ? $"按这个键时：先看「{spec.ProcessName}」在不在运行 —— "
              + "在运行就直接把键发过去；没运行就先启动它（"
              + (spec.RunAsAdmin ? "以管理员身份" : "普通权限")
              + "），等窗口出来再发键。"
            : "还差：" + (hasKey ? "" : "「要发送的键」")
              + (!hasKey && string.IsNullOrWhiteSpace(spec.AppPath) ? " 和 " : "")
              + (string.IsNullOrWhiteSpace(spec.AppPath) ? "「绑定应用」" : "");
    }

    /// <summary>捕获"要发送的键"。</summary>
    private void OnCustomKeyCapture(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var spec = CurrentCustomSpec();
        var dlg = new UI.Windows.KeyCaptureWindow
        {
            Owner = Window.GetWindow(this),
            PrefixKeys = TriggerPrefixKeys(),
        };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.CapturedCombo)) return;

        /* 存**完整组合键**（含修饰键），发送时原样重放。
         * 这里刻意不碰 _layer / _selectedKey：捕获的是"要发出去的键"，
         * 跟"当前在编辑键盘上的哪个键"是两件事。 */
        spec.Key = dlg.CapturedCombo;
        SaveCustomSpec(spec);
        ShowBindStatus($"已设置要发送的键：{CustomKeyRunner.ComboDisplay(spec.Key)}");
    }

    /// <summary>当前配置的触发键（捕获时要告诉捕获窗口，免得把前缀键录成目标键）。</summary>
    private HashSet<int> TriggerPrefixKeys()
    {
        var prefixes = new HashSet<int>();
        if (_host == null) return prefixes;

        foreach (var t in _host.Settings.Triggers)
        {
            if (string.IsNullOrWhiteSpace(t.Key)) continue;
            if (KeyName.VkOf(t.Key) is int vk && vk != 0) prefixes.Add(vk);
        }
        if (KeyName.VkOf("capslock") is int caps && caps != 0) prefixes.Add(caps);
        return prefixes;
    }

    private void OnCustomAppChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var spec = CurrentCustomSpec();
        spec.AppPath = CustomAppBox.Text.Trim();
        SaveCustomSpec(spec);
    }

    private void OnBrowseCustomApp(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要绑定的程序",
            Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        var spec = CurrentCustomSpec();
        spec.AppPath = dlg.FileName;
        SaveCustomSpec(spec);
        ShowBindStatus($"已绑定应用：{Path.GetFileName(dlg.FileName)}");
    }

    private void OnCustomAdminChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var spec = CurrentCustomSpec();
        spec.RunAsAdmin = CustomAdminBox.IsChecked == true;
        SaveCustomSpec(spec);
    }

    private void OnCustomMainWindowChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        var spec = CurrentCustomSpec();
        spec.BringFront = CustomMainWindowBox.IsChecked == true;
        SaveCustomSpec(spec);
    }

    /// <summary>
    /// 「启动后等多久再发键」。
    ///
    /// 边打字边落盘，但**不合法就不落盘** —— 清空输入框时 TextChanged 会先来一个
    /// 空串，这时候把 0 写进去会把用户原来的 900 冲掉；等用户真的输入数字再写。
    /// </summary>
    private void OnCustomWaitChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        if (!int.TryParse(CustomWaitBox.Text.Trim(), out int ms)) return;

        var spec = CurrentCustomSpec();
        spec.WaitAfterLaunchMs = Math.Clamp(ms, 0, 10000);
        SaveCustomSpec(spec);
    }

    /// <summary>
    /// 备注。存在 <see cref="KeyBinding.Note"/> 上（不是 spec 里）——
    /// 备注是"这个键叫什么"，属于绑定本身，和"发什么键给哪个程序"无关。
    /// 键帽与已绑定列表都优先显示它。
    /// </summary>
    private void OnCustomNoteChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;

        string combo = CurrentCombo();
        var binding = FindInCurrentLayer(combo);
        if (binding == null)
        {
            binding = new KeyBinding { Combo = combo, Action = "customKey" };
            CurrentLayerKeys.Add(binding);
        }

        string note = CustomNoteBox.Text.Trim();
        // 空备注存 null，设置文件里干脆不出现这个字段（和直达链接保持一致）
        binding.Note = note.Length == 0 ? null : note;
        _host.SaveSettings();
        BuildKeyboard();          // 键帽上要立刻显示新名字
    }

    /// <summary>键帽上显示什么：备注 → 应用名 → "自定义"。</summary>
    internal static string CustomKeyLabelOf(string? note, CustomKeySpec spec)
    {
        if (!string.IsNullOrWhiteSpace(note)) return note.Trim();
        string proc = spec.ProcessName;
        if (proc.Length > 0) return proc.Length <= 8 ? proc : proc[..8] + "…";
        return "自定义";
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
