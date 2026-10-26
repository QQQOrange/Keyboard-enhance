using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Input;
using CapsLockPlus.UI.Windows;
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
        new (string, string?, int, int)[]
        {
            ("Esc","esc",0,4),
            ("F1","f1",8,4),("F2","f2",12,4),("F3","f3",16,4),("F4","f4",20,4),
            ("F5","f5",28,4),("F6","f6",32,4),("F7","f7",36,4),("F8","f8",40,4),
            ("F9","f9",48,4),("F10","f10",52,4),("F11","f11",56,4),("F12","f12",60,4),
            ("PrtSc","printScreen",68,7),("ScrLk","scrollLock",75,7),("Pause","pause",82,7),
        },
        // ② 数字行 y=1
        new (string, string?, int, int)[]
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
        new (string, string?, int, int)[]
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
        new (string, string?, int, int)[]
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
        new (string, string?, int, int)[]
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
        new (string, string?, int, int)[]
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
    /// 一个标准键（4 列）的宽度。
    ///
    /// 这是**自然尺寸**下的值：整把键盘按它算出一个固定宽高比，
    /// 再交给 Viewbox 等比缩放到窗口大小（见 <see cref="NaturalWidth"/>）。
    /// 以前没有自然尺寸，键盘宽度完全由父容器决定 —— 窗口一窄，
    /// 92 个星列被压到每个十几像素，键帽高度却还是 52，
    /// 于是整把键盘"横向压扁、纵向不变"，字体挤成一团。
    /// </summary>
    private const double KeyWidth = 46;

    /// <summary>
    /// 键盘的自然宽度（未缩放）。92 列 = 23 个标准键宽。
    /// </summary>
    internal static double NaturalWidth => TotalCols / 4.0 * KeyWidth;

    /// <summary>
    /// 键盘的自然高度（未缩放）。每行一个 <see cref="KeycapHeight"/> 加间距。
    /// </summary>
    internal static double NaturalHeight => Layout.Length * (KeycapHeight + KeycapGap);

    /* ---------------- 尺寸：由档位决定，不跟窗口走 ---------------- */

    /// <summary>
    /// 键盘卡片左右两侧的固定宽度 —— 算"页面最小宽度"用。
    ///
    ///   侧栏 228 + 页面左边距 24 + 右边距 20 + 卡片内边距 20+20 + 卡片边框 1+1
    ///   + 页面纵向滚动条（编辑区在下面，几乎总是有）
    ///
    /// 编辑区搬到键盘下方之后，这里**不再包含编辑面板的 330 和它旁边的 14 间距**——
    /// 那 344 DIP 正是键盘以前被挤小的直接原因（约占"大"档键盘宽度的三分之一）。
    ///
    /// 数字必须和 MainWindow.xaml / KeysView.xaml 里的实际值一一对应；
    /// 改任何一处布局都要回来改这里，否则算出来的最小宽度会差几十像素，
    /// 表现为"窗口到了所谓的最小尺寸，键盘却还在出横向滚动条"。
    ///
    /// 滚动条那 17 DIP 是实测踩出来的：整页滚动条画在 PageScroll **内部**，
    /// 它一出现就从可用宽度里扣掉 17，键盘于是"刚好差一点"而要横向滚。
    /// 用 <see cref="SystemParameters.VerticalScrollBarWidth"/> 而不是写死 17，
    /// 免得不同 DPI 下又对不上。
    ///
    /// 末尾的 +4 是取整余量：主窗口开了 UseLayoutRounding，
    /// 把窗口设成"刚好装下"的宽度时各层取整会抹掉零点几像素，
    /// 判定就成了"装不下"，键盘明明刚好放下却冒出一条横向滚动条。
    /// </summary>
    internal static double WindowFixedWidth =>
        228 + 24 + 20 + 20 + 20 + 2
        + SystemParameters.VerticalScrollBarWidth
        + 4;

    /// <summary>
    /// 键盘卡片上下那些固定高度加起来（**已含标题栏 56**）：
    ///
    ///   页面上下边距 4+20、工具栏 ~46 + 下间距 14、卡片内边距 16+16、
    ///   信息条 ~39、底部控制条 ~67、卡片边框 2 —— 共约 280，留 10 余量。
    ///
    /// 编辑区在卡片之下、由整页滚动承接，所以**不计入**最小高度：
    /// 窗口再矮也只是要往下滚一下，键盘本身不会被压缩（那才是真正的病）。
    /// </summary>
    internal const double WindowFixedHeight = 290;

    /// <summary>渲染期强制档位（只在 --render 里用，不写用户设置）。</summary>
    internal static string? RenderPresetOverrideForTest;

    /// <summary>当前生效的档位。</summary>
    internal KeyboardSizePreset EffectivePreset => KeyboardSizePreset.Resolve(
        RenderPresetOverrideForTest ?? _host?.Settings.Global.KeyboardSize);

    /// <summary>档位下键盘的实际宽度（DIP）。</summary>
    internal static double WidthOf(KeyboardSizePreset preset) => NaturalWidth * preset.Scale;

    /// <summary>档位下键盘的实际高度（DIP）。</summary>
    internal static double HeightOf(KeyboardSizePreset preset) => NaturalHeight * preset.Scale;

    /// <summary>档位下标准键（4 列）的宽度。</summary>
    internal static double StandardKeyWidthOf(KeyboardSizePreset preset) => KeyWidth * preset.Scale;

    /// <summary>档位下键帽高度。</summary>
    internal static double StandardKeyHeightOf(KeyboardSizePreset preset) => KeycapHeight * preset.Scale;

    /// <summary>
    /// 装下当前档位键盘所需的**窗口**尺寸。
    ///
    /// 这就是"这个页面的最小大小"的来源：主窗口切到按键映射页时
    /// 拿它去设 MinWidth / MinHeight（见 MainWindow.ApplyPageMinimum），
    /// 于是窗口最多只能缩到刚好看全整把键盘。
    /// </summary>
    internal (double Width, double Height) RequiredWindowSize()
    {
        var preset = EffectivePreset;
        return (WindowFixedWidth + WidthOf(preset),
                Math.Max(620, WindowFixedHeight + HeightOf(preset)));
    }

    /// <summary>
    /// 把键盘设成当前档位的大小。
    ///
    /// 这里**不再读可用空间**：档位是多少就是多少。窗口比它宽，多出来的
    /// 空间是键盘两侧的居中留白；窗口比它窄（屏幕本身装不下），外层
    /// ScrollViewer 出横向滚动条，键帽一个像素都不缩。
    /// </summary>
    internal void ApplyKeyboardSize()
    {
        var preset = EffectivePreset;
        double w = WidthOf(preset);
        double h = HeightOf(preset);

        KeyboardPanel.Width = w;
        KeyboardPanel.Height = h;
        SizeText.Text = $"{w:0} × {h:0}";
    }

    /// <summary>
    /// 填「键盘大小」下拉并选中当前档位。
    ///
    /// 标签里带上实际像素尺寸：只写"大 / 小"用户没法判断到底多大，
    /// 而"大 · 1058 × 330"能直接和窗口宽度对起来。
    /// </summary>
    private void BuildSizeBox()
    {
        if (_host == null) return;
        /* 用 EffectivePreset 而不是直接读设置：渲染时 override 可能压过设置，
         * 下拉框必须显示**当前真正生效**的那一档，否则出图时会出现
         * Loc.T("键盘是 794 宽、下拉写着 1058") 的自相矛盾。 */
        var current = EffectivePreset;

        _suppressEvents = true;
        try
        {
            SizeBox.Items.Clear();
            foreach (var preset in KeyboardSizePreset.All)
            {
                SizeBox.Items.Add(new ComboBoxItem
                {
                    Content = preset.Label(NaturalWidth),
                    Tag = preset.Id,
                    ToolTip = preset.DisplayHint,
                });
            }
            foreach (ComboBoxItem item in SizeBox.Items)
            {
                if (item.Tag as string == current.Id) { SizeBox.SelectedItem = item; break; }
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void OnKeyboardSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        if (SizeBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;

        var preset = KeyboardSizePreset.Resolve(id);
        if (string.Equals(_host.Settings.Global.KeyboardSize, preset.Id, StringComparison.Ordinal))
            return;

        /* 存的就是"默认档"本身：下次打开这个页面（哪怕重启程序）
         * 都直接按这一档渲染，不需要用户再选一次。 */
        _host.Settings.Global.KeyboardSize = preset.Id;
        _host.SaveSettings();

        ApplyKeyboardSize();

        /* 页面最小尺寸跟着档位走 —— 由主窗口负责改 MinWidth/MinHeight。
         * 换大档时窗口会被撑到装得下，换小档时窗口可以缩得更小。 */
        (Window.GetWindow(this) as MainWindow)?.ApplyPageMinimum(this);
    }

    /// <summary>仅供自检：按指定窗口尺寸完整排一次版，返回键盘最终的大小。</summary>
    public (double Scale, double KeyWidth, double KeyHeight, bool NeedsScrollX)
        KeyboardScaleForTest(double width, double height)
    {
        Measure(new Size(width, height));
        Arrange(new Rect(0, 0, width, height));
        UpdateLayout();
        ApplyKeyboardSize();
        UpdateLayout();

        double panelW = KeyboardPanel.Width;
        if (double.IsNaN(panelW) || panelW <= 0) return (0, 0, 0, false);

        return (panelW / NaturalWidth,
                panelW / (TotalCols / 4.0),
                KeyboardPanel.Height / Layout.Length,
                panelW > KeyboardScroll.ActualWidth + 0.5);
    }

    /// <summary>
    /// 仅供 --render：临时按某个档位出图（**不写用户设置**）。
    ///
    /// 渲染是独立进程，改设置也不会污染用户的真实配置；但为了保险，
    /// 这里只动内存里的 override，落盘的事一概不做。
    /// </summary>
    public void SetSizePresetForRender(string presetId)
    {
        RenderPresetOverrideForTest = KeyboardSizePreset.Resolve(presetId).Id;
        ApplyKeyboardSize();
        BuildSizeBox();
        UpdateLayout();
    }

    /// <summary>仅供 --render：清掉档位 override，回到用户真实设置。</summary>
    public void ClearSizePresetForRender()
    {
        RenderPresetOverrideForTest = null;
        ApplyKeyboardSize();
        BuildSizeBox();
        UpdateLayout();
    }

    /// <summary>仅供自检：键盘的直接宿主（应当就是那个 ScrollViewer）。</summary>
    internal DependencyObject? KeyboardHostForTest => KeyboardPanel.Parent;

    /// <summary>仅供自检：键盘可视区当前有多宽（排完版后才有值）。</summary>
    internal double KeyboardViewportWidthForTest => KeyboardScroll.ActualWidth;

    /// <summary>仅供自检：键盘（Viewbox）自己声明的宽度。</summary>
    internal double KeyboardPanelWidthForTest => KeyboardPanel.Width;

    /// <summary>
    /// 仅供自检：键盘底边与编辑卡片顶边在页面里的纵坐标。
    ///
    /// 用来钉住「编辑区在键盘**下方**」这条布局约定 —— 它是"把键盘空间
    /// 抢回来"的核心：左右分栏时编辑面板固定占 330 宽，键盘只能缩。
    /// </summary>
    internal (double KeyboardBottom, double EditorTop, double PageWidth, double KeyboardWidth)
        VerticalOrderForTest(double width, double height)
    {
        Measure(new Size(width, height));
        Arrange(new Rect(0, 0, width, height));
        UpdateLayout();

        try
        {
            var kb = KeyboardScroll.TransformToAncestor(this).Transform(new Point(0, 0));
            var ed = EditorCard.TransformToAncestor(this).Transform(new Point(0, 0));
            return (kb.Y + KeyboardScroll.ActualHeight, ed.Y, ActualWidth, KeyboardScroll.ActualWidth);
        }
        catch
        {
            return (0, 0, 0, 0);
        }
    }

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


    public KeysView()
    {
        InitializeComponent();

        /* 搜索结果的浮层是**独立窗口**，页面一滚它不会跟着搜索框走，
         * 会钉在原来的屏幕位置、和输入框错位。滚一下就收起最省事 ——
         * 用户把页面滚走时本来也不是在看那几条结果。 */
        PageScroll.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0 && SearchPopup.IsOpen) SearchPopup.IsOpen = false;
        };
    }

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
        /* 每次进这个页面都重新读档位 —— 用户选过"特大"之后，
         * 下次打开（甚至重启程序）就该直接是特大，不用再选一次。 */
        BuildSizeBox();
        ApplyKeyboardSize();
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
            /* Content 显示译文、Tag 存原文 —— Tag 是身份（跟 WinBindCategory
             * 这些常量对得上、跟 info.Category 对得上），Content 才是给人看的。
             * 这一条区分不清，英文界面下分类下拉就会"选不中"。 */
            string shown = Loc.T(cat);
            CategoryBox.Items.Add(new ComboBoxItem { Content = shown, Tag = cat });
            CategoryBoxWinBind.Items.Add(new ComboBoxItem { Content = shown, Tag = cat });
            CategoryBoxWeb.Items.Add(new ComboBoxItem { Content = shown, Tag = cat });
            CategoryBoxCustom.Items.Add(new ComboBoxItem { Content = shown, Tag = cat });
            CategoryBoxType.Items.Add(new ComboBoxItem { Content = shown, Tag = cat });
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
            .Select(i => new SearchHit(i.Id, i.DisplayCategory, i.Title))
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
                ActionDesc.Text = Loc.T("选定一个程序后：按一下切过去，再按一次最小化；程序没开就自动启动。");
                RefreshWinBindPanel();
                break;
            case EditorMode.WebPage:
                ActionDesc.Text = Loc.T("选「直达网址」就用默认浏览器打开网址；选「直达文件夹」就在资源管理器里打开那个目录；选「直达文件」就用系统默认程序打开它。备注会显示在键帽上。");
                RefreshWebPageFields();
                break;
            case EditorMode.TypeText:
                ActionDesc.Text = Loc.T("填一段文字：按这个键就把它直接输入到当前光标处。");
                RefreshTypeTextField();
                break;
            case EditorMode.CustomKey:
                ActionDesc.Text = Loc.T("先把要发送的键和应用绑好。按这个键时会先看应用在不在运行：")
                                + Loc.T("在运行就直接把键发过去，没运行就先把它拉起来再发。");
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

    /* 这里原本有「直达网址 / 直达文件夹 / 直达文件」三个按钮和 CurrentLinkKind()。
     * v4.5.19 起类型不再让用户选：输入框边填边识别，
     * 由 KeysView.Inputs.ApplyLinkDetection 决定绑哪个动作（见 LinkActionFor）。
     *
     * 之所以能安全去掉"手动选类型"：三个动作只决定**怎么打开**，
     * 目标字符串是同一份 Arg，识别结果的 Target 也会在打开时再归一，
     * 所以认错了改一下文字就自动纠回来，不会像以前那样把 Arg 清空重填。 */

    /// <summary>
    /// 挑一个文件夹填进目标框。
    ///
    /// 用 .NET 自带的 OpenFolderDialog（WPF 版，Win10 1809+ 都是系统原生对话框），
    /// 不引 WinForms，也不自己造轮子。
    /// </summary>
    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        // 用 WinForms 的 FolderBrowserDialog，而不是 .NET 8 起才有的
        // Microsoft.Win32.OpenFolderDialog —— Win7 线（net7.0-windows）没有后者。
        // 代价是 Win7 上是经典样式对话框；换来的是两条线同一份代码。
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = Loc.T("选择要直达的文件夹"),
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true,
        };

        // 已经有合法路径时从那里开始浏览，省得每次从"此电脑"点起
        string current = Core.Actions.ActionContext.ExpandFolderPath(WebUrlBox.Text);
        if (Directory.Exists(current)) dlg.SelectedPath = current;

        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        string folder = dlg.SelectedPath;
        if (string.IsNullOrWhiteSpace(folder)) return;

        WebUrlBox.Text = folder;               // TextChanged 会负责落盘
        ShowBindStatus(Loc.F("文件夹已设置：{0}", folder));
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
            Title = Loc.T("选择要直达的文件"),
            Multiselect = false,
            CheckFileExists = true,
            Filter = Loc.T("所有文件|*.*"),
        };

        // 已经填过路径时从它所在的目录开始浏览
        string current = Core.Actions.ActionContext.ExpandPath(WebUrlBox.Text);
        try
        {
            string? dir = Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FileName)) return;

        WebUrlBox.Text = dlg.FileName;            // TextChanged 会负责落盘
        ShowBindStatus(Loc.F("文件已设置：{0}", dlg.SafeFileName));
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
}
