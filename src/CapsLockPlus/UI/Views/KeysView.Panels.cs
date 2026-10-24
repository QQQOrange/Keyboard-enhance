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
// 窗口绑定面板、各模式的就地字段、渲染用的开关，以及供自检调用的几何查询。

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
    /// 仅供渲染：往「直达链接」的目标框里填一段文字，让截图能拍到识别徽标
    /// 到底是"网址 / 文件夹 / 文件"里的哪一种。
    ///
    /// 会真的走一遍 TextChanged（识别 + 落盘），所以只该在隔离数据目录下用。
    /// </summary>
    public void FillLinkTargetForRender(string text)
    {
        _suppressEvents = false;
        WebUrlBox.Text = text;
        UpdateLayout();
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
    /// 报出底部两个按钮在**整页内容**里的位置 —— 用来验证它们不会被裁掉。
    ///
    /// 为什么这条要专门测：以前中间几行是 Auto 高度，面板一展开就把
    /// 「清除绑定」「测试一下」推到卡片外面，怎么滚都看不到
    /// （溢出的是 Grid 行，不是 ScrollViewer 的内容，滚动条根本不出现）。
    ///
    /// 现在编辑区在键盘下方、整页只有一个纵向 ScrollViewer：
    /// 按钮只要落在内容范围内（Bottom ≤ ContentHeight）就一定滚得到，
    /// 不会再出现"内容被祖先裁掉、滚也滚不出来"的情况。
    /// </summary>
    public (bool Visible, double Bottom, double ContentHeight, double ViewportHeight) FooterForTest(
        double width, double height)
    {
        ApplyEditorMode(EditorMode.CustomKey);

        Measure(new Size(width, height));
        Arrange(new Rect(0, 0, width, height));
        UpdateLayout();

        double bottom = 0;
        try
        {
            /* 坐标是相对**视口**的，加上当前滚动偏移才是它在内容里的位置。
             * 这里没有主动滚动，偏移通常是 0，但写全了才不会在别处复用时算错。 */
            var p = ClearBindButton.TransformToAncestor(PageScroll).Transform(new Point(0, 0));
            bottom = p.Y + ClearBindButton.ActualHeight + PageScroll.VerticalOffset;
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
        return (laidOut, bottom, PageScroll.ExtentHeight, PageScroll.ViewportHeight);
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

            /* 五个分类下拉必须同步。
             * 事件被抑制时不会走 OnCategoryChanged，所以这里手动同步，
             * 否则截图里会出现"面板是自定义按键、上面的分类却写着直达链接"
             * 这种自相矛盾的画面。 */
            foreach (var box in new[] { CategoryBoxWinBind, CategoryBoxWeb, CategoryBoxType, CategoryBoxCustom })
                SelectTag(box, CustomKeyCategory);
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
    public void ResetRenderState()
    {
        SelectKey(_selectedKey);
        PageScroll.ScrollToTop();
    }

    /// <summary>
    /// 仅供渲染：把页面滚到编辑区，好截一张"键盘下方的编辑面板"完整样子。
    ///
    /// 出图时窗口高度通常只有 800，编辑区在折叠线以下 —— 不滚下去的话，
    /// 后面所有面板（自定义按键 / 窗口绑定…）的截图拍到的都是键盘。
    /// </summary>
    public void ScrollToEditorForRender()
    {
        UpdateLayout();
        double top = 0;
        try { top = EditorCard.TransformToAncestor(PageScroll).Transform(new Point(0, 0)).Y + PageScroll.VerticalOffset; }
        catch { /* 预期内：还没参与布局时直接滚到顶 */ }

        PageScroll.ScrollToVerticalOffset(Math.Max(0, top - 12));
        UpdateLayout();
    }

    /// <summary>
    /// 一个绑定在界面上该显示成什么。
    ///
    /// 键帽和编辑面板里的说明都用它，保证两处文字一致 ——
    /// 早先键帽显示程序名、面板显示「覆盖」，用户对不上号。
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

    // ---------------- 窄窗口：编辑区降级换行 ----------------

    /// <summary>
    /// 编辑区主行的"降级换行"阈值（DIP，ScrollViewer 的可视宽度）。
    ///
    /// 880 是照三组并排的舒适度定的：普通分类那一行 = 搜索 240 + 分类 150 +
    /// 功能 *，功能下拉至少要 300 才看得全"直达链接 · 直达文件"这种值，
    /// 再加两处 12 的组间距 ≈ 726；而「直达链接」那一行还多一个 200 的备注，
    /// 目标框会被压到 400 以下 —— 网址长得快，400 根本不够看。
    ///
    /// 实际效果：键盘档「大 / 中」时编辑区 920+，保持四组并排；
    /// 换成「小」档、窗口又拖到最小（编辑区约 817）时自动降级 —— 备注下沉，
    /// 目标框多拿 200 的宽度。
    /// </summary>
    private const double CompactBreakpoint = 880;

    /// <summary>
    /// 上一次算出来的形态。null = 还没算过（第一次布局必须应用一次）。
    /// 存下来是为了避免每次 SizeChanged 都重排 —— 重排会触发布局，
    /// 布局又会触发 SizeChanged，来回抖。
    /// </summary>
    private bool? _editorCompact;

    /// <summary>
    /// 窗口变窄时把次要字段（搜索、备注）从主行挪到第二行。
    ///
    /// 为什么不干脆让它们一起挤窄：备注从 200 挤到 120 还能用，
    /// 但搜索框挤到 150 就只剩"搜索"两个字宽，输入的关键词根本看不见；
    /// 功能下拉被压到 200 以下，长值会截成"直达链接 · 直…"。
    /// 与其所有字段一起受罪，不如让次要的整块下移 —— 横向省下的宽度
    /// 全部留给主字段。
    ///
    /// 只改 Grid 的行/列（不复制控件、不重建可视树），所以输入内容、
    /// 焦点、事件绑定全都不受影响。
    /// </summary>
    private void OnPageScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = e.NewSize.Width;
        bool compact = width > 0 && width < CompactBreakpoint;
        if (_editorCompact == compact) return;
        _editorCompact = compact;

        LayoutEditorGroups(compact);
    }

    /// <summary>按"宽/窄"两种形态摆放编辑区的主行分组。</summary>
    private void LayoutEditorGroups(bool compact)
    {
        if (SearchGroup == null) return;   // 设计器/极早期布局

        // 搜索：宽时与分类、功能同一行；窄时自己占第二行（横跨整行）
        Grid.SetRow(SearchGroup, compact ? 1 : 0);
        Grid.SetColumn(SearchGroup, 0);
        Grid.SetColumnSpan(SearchGroup, compact ? 3 : 1);
        SearchGroup.Margin = compact
            ? new Thickness(0, 12, 0, 0)
            : new Thickness(0, 0, 12, 0);

        /* 备注：宽时留在主行最右（紧邻分类那一组），窄时下沉整行。
         * 三个面板共用同一套规则，只是宽屏时各自的列位置不同。 */
        PlaceNote(NoteGroupWeb, wideColumn: 1, compact);
        PlaceNote(NoteGroupType, wideColumn: 1, compact);
        PlaceNote(NoteGroupCustom, wideColumn: 2, compact);

        /* 说明 + 参数：宽时并排（参数固定 340 靠右），窄时参数整行换到下面。
         *
         * 参数输入框本身就是一根"横条"，宽的时候和说明共享一行完全不挤 ——
         * 以前它自己占一整行，右边全空。可窄的时候不能让它们并排：
         * 说明会被压成两三个字一换行，比多占一行还难读。 */
        Grid.SetRow(ArgPanel, compact ? 1 : 0);
        Grid.SetColumn(ArgPanel, compact ? 0 : 1);
        Grid.SetColumnSpan(ArgPanel, compact ? 2 : 1);
        ArgPanel.Width = compact ? double.NaN : 340;   // NaN = 由父容器决定（铺满）
        ArgPanel.Margin = compact ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        ActionDesc.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 16, 0);
    }

    private static void PlaceNote(FrameworkElement? note, int wideColumn, bool compact)
    {
        if (note == null) return;

        Grid.SetRow(note, compact ? 1 : 0);
        Grid.SetColumn(note, compact ? 0 : wideColumn);
        Grid.SetColumnSpan(note, compact ? 3 : 1);
        note.Margin = compact
            ? new Thickness(0, 12, 0, 0)
            : new Thickness(0, 0, 12, 0);
    }

    /// <summary>
    /// 仅供自检：把编辑区摆成宽/窄两种形态并回读关键分组的位置。
    /// </summary>
    internal EditorLayout EditorLayoutForTest(bool compact)
    {
        LayoutEditorGroups(compact);
        return new EditorLayout(
            SearchRow: Grid.GetRow(SearchGroup),
            SearchSpan: Grid.GetColumnSpan(SearchGroup),
            NoteRow: Grid.GetRow(NoteGroupWeb),
            NoteSpan: Grid.GetColumnSpan(NoteGroupWeb),
            NoteMarginTop: NoteGroupWeb.Margin.Top,
            ArgRow: Grid.GetRow(ArgPanel),
            ArgColumn: Grid.GetColumn(ArgPanel),
            ArgSpan: Grid.GetColumnSpan(ArgPanel),
            ArgWidth: ArgPanel.Width);
    }
}

/// <summary>编辑区一次布局的形态快照（自检用）。</summary>
internal sealed record EditorLayout(
    int SearchRow, int SearchSpan,
    int NoteRow, int NoteSpan, double NoteMarginTop,
    int ArgRow, int ArgColumn, int ArgSpan, double ArgWidth);
