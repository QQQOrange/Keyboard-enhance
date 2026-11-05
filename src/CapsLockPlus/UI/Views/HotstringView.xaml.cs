using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Views;

/// <summary>
/// 「别名替换」页：三栏 —— 左分组、中列表、右编辑。
///
/// 数据源是 <see cref="GlobalOptions.Snippets"/>（真正参与展开的那一份）。
/// 分组既可以从设置里的 <see cref="GlobalOptions.SnippetGroups"/> 手动新建，
/// 也会自动收进别名身上已经用到的组名 —— 以前只能"先有别名才有组"，
/// 用户没法先建一个空分组再往里放东西。
/// </summary>
public partial class HotstringView : UserControl
{
    private AppHost? _host;
    private Snippet? _editing;
    private bool _suppress;

    /// <summary>刷新分组列表后要选中的组名（改名/新建后用它把选中状态带过去）。</summary>
    private string? _selectGroupAfterRefresh;

    /// <summary>自动对齐前缀只做一次，避免迁移后再次触发。</summary>
    private bool _prefixAlignedOnce;

    /// <summary>当前排序方式（0 触发词 / 1 最近使用 / 2 使用次数）。</summary>
    private int _sortMode;

    /// <summary>只显示从未使用过的别名。</summary>
    private bool _unusedOnly;

    /// <summary>列表里是否显示展开后的内容（眼睛按钮控制）。</summary>

    /* ---------------- 拖拽状态 ---------------- */

    /// <summary>按下左键时的位置（判断"是点击还是拖拽"）。</summary>
    private Point _dragStartPoint;

    /// <summary>按下左键时命中的别名（拖拽源）。</summary>
    private Snippet? _dragSnippet;

    /// <summary>当前高亮的分组行（拖拽经过时）。</summary>
    private ListBoxItem? _dragHoverItem;

    /// <summary>自定义顺序拖拽时的插入位置指示线。</summary>
    private UI.InsertLineAdorner? _insertLine;

    /// <summary>跟着鼠标走的拖拽小卡片。</summary>
    private UI.DragChipAdorner? _dragAdorner;

    /// <summary>分组行的 Group：null = 全部；"" = 未分组；其它 = 组名。</summary>
    private sealed class GroupRow
    {
        public string? Group { get; init; }
        public string Label { get; init; } = "";
        public int Count { get; init; }
        public bool IsReal => Group is { Length: > 0 };
        public override string ToString() => Label;
    }

    public sealed class Row
    {
        public Snippet Snippet { get; init; } = new();
        public string KeyLabel { get; init; } = "";

        /// <summary>列表左侧小方块里的首字母。</summary>
        public string Initial { get; init; } = "";

        /// <summary>右侧显示的分组名（没分组就是空）。</summary>
        public string GroupLabel { get; init; } = "";
        public string Preview { get; init; } = "";

        /// <summary>
        /// 左侧小方块里显示的**当前前缀符号**（例如 `;`）。
        ///
        /// 它跟着设置里的「前缀符号」走：改前缀会刷新列表，这里自然一起变。
        /// 前缀被清空（不需要前缀）时显示一个中点，避免出现空白方块。
        /// </summary>
        public string PrefixLabel { get; init; } = ";";

        /// <summary>停用的别名整行压暗，一眼能看出来（不用点进去看勾选）。</summary>
        public double Opacity => Snippet.Enabled ? 1.0 : 0.45;

        /// <summary>列表右侧的"用过几次"；从没用过就留空，避免一列全是 0。</summary>
        public string UsageLabel => Snippet.UseCount > 0 ? Snippet.UseCount.ToString() : "";

        public override string ToString() => KeyLabel;
    }

    public HotstringView()
    {
        InitializeComponent();

        /* 排序下拉：文字在构造时填，切语言重开页面即可（和其他页面一致）。 */
        SortBox.Items.Add(Loc.T("按触发词"));
        SortBox.Items.Add(Loc.T("按最近使用"));
        SortBox.Items.Add(Loc.T("按使用次数"));
        SortBox.Items.Add(Loc.T("自定义顺序"));
        SortBox.SelectedIndex = 0;

    }

    public void Initialize(AppHost host)
    {
        _host = host;
        var opt = host.Settings.Hotstring;

        _suppress = true;
        try
        {
            PrefixBox.Text = opt.Prefix ?? "";
            FullWidthBox.IsChecked = opt.FullWidth;
            SpaceBox.IsChecked = opt.ExpandOnSpace;
            SwallowBox.IsChecked = opt.SwallowSpace;
            opt.ShowTextInList = false;      // 列表只显示备注，永远不显示替换内容
        }
        finally { _suppress = false; }

        Refresh();
    }

    private List<Snippet> Entries => _host?.Settings.Global.Snippets ?? new List<Snippet>();

    private List<string> CustomGroups => _host?.Settings.Global.SnippetGroups ?? new List<string>();

    private string? CurrentGroup => (GroupList.SelectedItem as GroupRow)?.Group;

    /// <summary>所有分组：手动建的 ∪ 别名里用到的，去重排序。</summary>
    private List<string> AllGroups()
        => _host == null ? new List<string>() : SnippetGroupOps.All(_host.Settings);

    // ---------------- 刷新 ----------------

    private void Refresh()
    {
        AlignStalePrefixOnce();
        FocusPendingAlias();

        if (_host == null) return;
        RefreshGroups();
        RefreshList();
        SyncEditorToSelection();
    }

    private void RefreshGroups()
    {
        if (_host == null) return;

        string? keepGroup = _selectGroupAfterRefresh ?? CurrentGroup;
        _selectGroupAfterRefresh = null;
        var all = Entries.ToList();
        var groups = AllGroups();

        var savedOrder = _host.Settings.Global.SnippetGroupOrder ?? new List<string>();

        int CountOf(List<Snippet> all, string g)
            => all.Count(x => string.Equals(x.Group?.Trim(), g, StringComparison.CurrentCultureIgnoreCase));

        int OrderIndex(string g)
        {
            int i = savedOrder.FindIndex(o => string.Equals(o?.Trim(), g, StringComparison.CurrentCultureIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }

        /* 分组只用"自定义顺序"：没拖过的组按名称排在后面，拖过的按保存的顺序。 */
        var ordered = groups.OrderBy(OrderIndex).ThenBy(g => g, StringComparer.CurrentCulture);

        var rows = new List<GroupRow> { new() { Group = null, Label = Loc.T("全部"), Count = all.Count } };
        rows.AddRange(ordered.Select(g => new GroupRow
        {
            Group = g,
            Label = g,
            Count = CountOf(all, g),
        }));
        rows.Add(new GroupRow
        {
            Group = "",
            Label = Loc.T("未分组"),
            Count = all.Count(x => string.IsNullOrWhiteSpace(x.Group)),
        });

        _suppress = true;
        try
        {
            GroupList.ItemsSource = rows;
            GroupList.SelectedItem = rows.FirstOrDefault(g => g.Group == keepGroup) ?? rows[0];

            /* 「全部 / 未分组」不是真分组，改名和删除对它们没有意义。 */
            bool real = (GroupList.SelectedItem as GroupRow)?.IsReal == true;
            RenameGroupButton.IsEnabled = real;
            DeleteGroupButton.IsEnabled = real;
        }
        finally { _suppress = false; }
    }

    private void RefreshList()
    {
        if (_host == null) return;

        var opt = _host.Settings.Hotstring;
        string filter = FilterBox.Text?.Trim() ?? "";
        string? group = CurrentGroup;
        var all = Entries.ToList();

        var filtered = all
            .Where(x => group == null
                        || (group.Length == 0
                            ? string.IsNullOrWhiteSpace(x.Group)
                            : string.Equals(x.Group?.Trim(), group, StringComparison.CurrentCultureIgnoreCase)))
            .Where(x => string.IsNullOrWhiteSpace(filter)
                        || (x.Trigger?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (x.Comment?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (x.Text?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(x => !_unusedOnly || x.UseCount <= 0);

        var rows = SortSnippets(filtered, _sortMode)
            .Select(x => new Row
            {
                Snippet = x,
                KeyLabel = SnippetPrefixOps.Strip(x.Trigger, opt.EffectivePrefix),
                Initial = InitialOf(x.Trigger, opt.EffectivePrefix),
                GroupLabel = string.IsNullOrWhiteSpace(x.Group) ? "" : x.Group!.Trim(),
                PrefixLabel = string.IsNullOrEmpty(opt.EffectivePrefix) ? "·" : opt.EffectivePrefix,
                Preview = BuildPreview(x),
            })
            .ToList();

        _suppress = true;
        try
        {
            EntryList.ItemsSource = rows;
            FilterCount.Text = Loc.F("共 {0} 条", rows.Count);
            CleanupButton.Visibility = _unusedOnly && rows.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            /* 空状态要说清"为什么空"：是没建过，还是筛选没命中，还是这个分组空着。
             * 统一一句"还没有别名"会让用户以为数据丢了。 */
            EmptyHint.Text = rows.Count != 0 ? "" :
                filter.Length > 0
                    ? Loc.F("没有匹配「{0}」的别名。", filter)
                    : (group is { Length: > 0 } g
                        ? Loc.F("「{0}」这个分组还空着。可以把别名拖进来，或右键「移到分组」。", g)
                        : Loc.T("还没有别名。点下面的「新增」建一条。"));
            EntryList.SelectedItem = rows.FirstOrDefault(r => ReferenceEquals(r.Snippet, _editing));
        }
        finally { _suppress = false; }
    }

    private void SyncEditorToSelection()
    {
        int selected = EntryList.SelectedItems.Count;
        MoveButton.IsEnabled = selected > 0;
        DeleteButton.IsEnabled = selected > 0;
        MoveButton.Content = selected > 1 ? Loc.F("移到分组 ({0})", selected) : Loc.T("移到分组");
        DeleteButton.Content = selected > 1 ? Loc.F("删除 ({0})", selected) : Loc.T("删除");

        if (EntryList.SelectedItem is Row row)
        {
            if (!ReferenceEquals(row.Snippet, _editing)) LoadEditor(row.Snippet);
        }
        else
        {
            MoveButton.IsEnabled = false;
            _editing = null;
            ShowEditor(false);
        }
    }

    /// <summary>
    /// 打开页面时自动对齐一次旧前缀。
    ///
    /// 场景：老版本改前缀只改设置、不改条目，于是配置里所有触发词都还带着旧符号
    /// （;mail），而当前前缀已经是 / —— 列表按 / 去剥，剥不掉，每条前面都多出一个 ;。
    ///
    /// 只在**完全无歧义**时自动做：所有带符号前缀的条目用的都是同一个符号，
    /// 且它和当前前缀不同。混合情况交给用户改前缀（改的时候会自动迁移）。
    /// </summary>
    private void AlignStalePrefixOnce()
    {
        if (_prefixAlignedOnce || _host == null) return;
        _prefixAlignedOnce = true;

        string current = _host.Settings.Hotstring.EffectivePrefix ?? "";

        /* 新旧符号混着的时候 DetectStale 会返回 null —— 那种情况不猜，
         * 交给用户改一次前缀（改前缀一定会整批迁移）。 */
        char? stale = SnippetPrefixOps.DetectStale(Entries, current, out int count);
        if (stale == null || count == 0) return;

        int moved = SnippetPrefixOps.Migrate(_host.Settings, stale.Value.ToString(), current);
        if (moved == 0) return;

        CoreLog.Info($"别名前缀自动对齐：{stale} → {current}，共 {moved} 条");
        _host.SaveSettings();
        _host.Notify(Loc.F("已把 {0} 条别名的旧前缀统一成「{1}」", moved, current));
    }

    // ---------------- 小工具 ----------------

    private static string InitialOf(string? trigger, string? prefix)
    {
        string s = SnippetPrefixOps.Strip(trigger, prefix);
        if (s.Length == 0) return "?";
        char c = char.ToUpperInvariant(s[0]);
        return char.IsLetterOrDigit(c) ? c.ToString() : s[..1];
    }

    private static string Shorten(string? s, int max)
    {
        s = (s ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
        return s.Length <= max ? s : s[..max] + "…";
    }

    private Window? Owner => Window.GetWindow(this);

    private void ShowEditor(bool show)
    {
        EditorCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) return;

        _suppress = true;
        try
        {
            KeyBox.Text = "";
            ValueBox.Text = "";
            CommentBox.Text = "";
            AppsBox.Text = "";
            GroupBox.Text = "";
            EnabledBox.IsChecked = false;
            StatusText.Text = "";
        }
        finally { _suppress = false; }
    }

    private void LoadEditor(Snippet entry)
    {
        _editing = entry;
        ShowEditor(true);

        _suppress = true;
        try
        {
            KeyBox.Text = SnippetPrefixOps.Strip(entry.Trigger, _host?.Settings.Hotstring.EffectivePrefix);
            EditTitle.Text = Loc.F("正在编辑：{0}", KeyBox.Text.Length == 0 ? Loc.T("（新别名）") : KeyBox.Text);
            ValueBox.Text = entry.Text ?? "";
            CommentBox.Text = entry.Comment ?? "";
            AppsBox.Text = entry.Apps ?? "";
            EnabledBox.IsChecked = entry.Enabled;
            StatusText.Text = "";
            StatusText.Foreground = (Brush)FindResource("Brush.TextTertiary");

            GroupBox.Text = entry.Group ?? "";

            /* 使用情况：帮用户判断这条还留着有没有意义。 */
            UsageText.Text = entry.UseCount <= 0
                ? Loc.T("还没用过")
                : (entry.LastUsed is { } at
                    ? Loc.F("用过 {0} 次 · 最近 {1:MM-dd HH:mm}", entry.UseCount, at)
                    : Loc.F("用过 {0} 次", entry.UseCount));
        }
        finally { _suppress = false; }
    }

    // ---------------- 分组管理 ----------------

    /// <summary>新建分组，返回组名；用户取消返回 null。</summary>
    private string? AskNewGroup()
    {
        string? name = UI.Windows.TextPromptWindow.Ask(Owner,
            Loc.T("新建分组"),
            Loc.T("起一个名字，之后右键列表里的别名即可「移到分组」。"));

        if (string.IsNullOrWhiteSpace(name)) return null;

        name = name.Trim();
        if (!SnippetGroupOps.Add(_host!.Settings, name))
        {
            _host.Notify(Loc.F("分组「{0}」已经存在", name));
            return name;
        }

        _host.SaveSettings();
        RefreshGroups();
        return name;
    }

    /* ---------------- 分组选择弹层 ---------------- */

    private void OnPickGroupClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var items = new List<string> { "" };
        items.AddRange(AllGroups().Where(g => g.Length > 0));

        GroupPickerList.ItemsSource = items;
        GroupPickerList.SelectedItem = items.FirstOrDefault(g =>
            string.Equals(g, GroupBox.Text?.Trim() ?? "", StringComparison.CurrentCultureIgnoreCase));

        GroupPicker.IsOpen = true;
    }

    private void OnGroupPickerPicked(object sender, MouseButtonEventArgs e)
    {
        if (GroupPickerList.SelectedItem is not string picked) return;

        GroupPicker.IsOpen = false;
        GroupBox.Text = picked;
        GroupBox.CaretIndex = GroupBox.Text.Length;
        SaveEditorFields();
    }

    /* ---------------- 拖拽：把别名拖到分组上 ---------------- */

    private void OnEntryMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(EntryList);
        _dragSnippet = null;

        var item = ItemsControl.ContainerFromElement(EntryList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item?.DataContext is Row row) _dragSnippet = row.Snippet;
    }

    private void OnEntryMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSnippet == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        Point now = e.GetPosition(EntryList);
        if (Math.Abs(now.X - _dragStartPoint.X) < 8 && Math.Abs(now.Y - _dragStartPoint.Y) < 8) return;

        var payload = new DataObject("clp/snippet", _dragSnippet);
        var snippet = _dragSnippet;
        _dragSnippet = null;

        /* 拖拽卡片：
         *   · 挂在**窗口**的装饰层上（挂在列表上会被列表边界裁掉，
         *     鼠标一离开列表就"卡在边缘"，正是用户看到的卡顿感）；
         *   · 位置由 60fps 的定时器从 Mouse.GetPosition 直接读取，
         *     不再依赖 DragOver 事件 —— 那样只有在拖过目标控件时才会更新。
         */
        string label = SnippetPrefixOps.Strip(snippet.Trigger, _host?.Settings.Hotstring.EffectivePrefix);
        if (string.IsNullOrEmpty(label))
            label = Shorten(snippet.Comment, 24);

        /* 装饰层挂在**页面自己**身上：Window 本身没有 AdornerLayer，
         * GetAdornerLayer(window) 返回 null —— 之前卡片压根没画出来就是这个原因。
         *
         * 位置更新用 GiveFeedback：DoDragDrop 期间是 OLE 自己的消息循环，
         * WPF 的 DispatcherTimer 不会走，只有 GiveFeedback 会被持续触发。
         * （这也就是用户看到的"卡片不动 / 到边界卡住"。） */
        _dragAdorner = UI.DragChipAdorner.Show(this, label, UI.DragChipAdorner.CursorIn(this));

        GiveFeedbackEventHandler follow = (_, args) =>
        {
            if (_dragAdorner != null)
                _dragAdorner.MoveTo(UI.DragChipAdorner.CursorIn(this));
        };

        EntryList.GiveFeedback += follow;
        try
        {
            DragDrop.DoDragDrop(EntryList, payload, DragDropEffects.Move);
        }
        finally
        {
            EntryList.GiveFeedback -= follow;
            _dragAdorner?.Remove();
            _dragAdorner = null;
            RemoveInsertLine();
            ClearDragHover();
            Mouse.UpdateCursor();
        }
    }

    /* ---------------- 分组拖拽排序 ---------------- */

    private Point _groupDragStart;
    private GroupRow? _groupDragRow;

    private void OnGroupMouseDown(object sender, MouseButtonEventArgs e)
    {
        _groupDragStart = e.GetPosition(GroupList);
        _groupDragRow = (sender as FrameworkElement)?.DataContext as GroupRow;
    }

    private void OnGroupMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _groupDragRow is not { IsReal: true } row) return;

        var now = e.GetPosition(GroupList);
        if (Math.Abs(now.X - _groupDragStart.X) < 6 && Math.Abs(now.Y - _groupDragStart.Y) < 6) return;

        var data = new DataObject("clp/group", row.Group!);
        DragDrop.DoDragDrop(GroupList, data, DragDropEffects.Move);
        _groupDragRow = null;
    }

    private void OnGroupDragOver(object sender, DragEventArgs e)
    {
        bool isGroupDrag = e.Data.GetDataPresent("clp/group");

        if (!isGroupDrag && !e.Data.GetDataPresent("clp/snippet"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var target = FindGroupRowAt(e.GetPosition(GroupList));
        if (target is not { IsReal: true } && target is not { Group: "" })
        {
            /* 「全部」只是个视图，不接受放下。 */
            ClearDragHover();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        HighlightDragHover(target);

        e.Handled = true;
    }

    private void OnGroupDragLeave(object sender, DragEventArgs e)
    {
        ClearDragHover();
    }

    private void OnGroupDrop(object sender, DragEventArgs e)
    {
        ClearDragHover();

        /* 分组拖拽排序：把拖动的组插到目标组的位置。 */
        if (e.Data.GetData("clp/group") is string dragged)
        {
            var dropTarget = FindGroupRowAt(e.GetPosition(GroupList));
            if (dropTarget is not { IsReal: true } || _host == null) return;

            SnippetGroupOps.Reorder(_host.Settings, dragged, dropTarget.Group);
            _host.SaveSettings();

            RefreshGroups();
            _host.Notify(Loc.F("已调整「{0}」的位置", dragged));
            e.Handled = true;
            return;
        }

        if (e.Data.GetData("clp/snippet") is not Snippet snippet) return;

        var target = FindGroupRowAt(e.GetPosition(GroupList));
        if (target == null) return;

        if (target.Group is null)
        {
            /* 全部：不当作分组，只提示一句。 */
            _host?.Notify(Loc.T("「全部」只是查看全部别名，不能作为分组；要取消分组请拖到「未分组」"));
            return;
        }

        MoveTo(snippet, target.Group);
        FlashGroupRow(target);
        e.Handled = true;
    }

    /// <summary>找出某个坐标下的分组行（没有就是 null）。</summary>
    private GroupRow? FindGroupRowAt(Point point)
    {
        var hit = GroupList.InputHitTest(point) as DependencyObject;
        var item = ItemsControl.ContainerFromElement(GroupList, hit) as ListBoxItem;
        return item?.DataContext as GroupRow;
    }

    /// <summary>拖拽经过时把目标分组"抬起来"一点 + 加底色。</summary>
    private void HighlightDragHover(GroupRow row)
    {
        if (GroupList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem item) return;
        if (ReferenceEquals(item, _dragHoverItem)) return;

        ClearDragHover();
        _dragHoverItem = item;

        item.Background = (Brush)FindResource("Brush.AccentSoft");
        item.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        item.RenderTransform = scale;

        var grow = new DoubleAnimation(1, 1.03, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void ClearDragHover()
    {
        if (_dragHoverItem == null) return;

        var item = _dragHoverItem;
        _dragHoverItem = null;

        var fade = new DoubleAnimation(1.03, 1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        if (item.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, fade);
        }

        var brush = new SolidColorBrush(Colors.Transparent);
        item.Background = brush;
    }

    /// <summary>放下之后给目标分组一个"闪一下"的反馈。</summary>
    private void FlashGroupRow(GroupRow row)
    {
        if (GroupList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem item) return;

        var brush = new SolidColorBrush(((SolidColorBrush)FindResource("Brush.Accent")).Color)
        {
            Opacity = 0.55,
        };
        item.Background = brush;

        var fade = new DoubleAnimation(0.55, 0, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        fade.Completed += (_, _) => item.Background = Brushes.Transparent;
        brush.BeginAnimation(SolidColorBrush.OpacityProperty, fade);
    }

    /// <summary>保存编辑区里的字段（供弹层选择后调用）。</summary>
    private void SaveEditorFields()
    {
        if (_host == null || _editing == null) return;

        _editing.Group = GroupBox.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(_editing.Group))
            SnippetGroupOps.Add(_host.Settings, _editing.Group);

        _host.SaveSettings();
        RefreshList();
        RefreshGroups();
    }

    private void OnNewGroupClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        string? name = AskNewGroup();
        if (name == null) return;

        // 建完直接选中它，用户马上就能看到"空分组"
        if (GroupList.ItemsSource is List<GroupRow> rows)
        {
            var target = rows.FirstOrDefault(g =>
                string.Equals(g.Group, name, StringComparison.CurrentCultureIgnoreCase));
            if (target != null) GroupList.SelectedItem = target;
        }
    }

    /// <summary>
    /// 分组右键菜单（Shift+F10 / 菜单键也走这里 —— 所以用 ContextMenuOpening，
    /// 而不是只绑鼠标事件）。
    /// </summary>
    private void OnGroupContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_host == null
            || e.Source is not FrameworkElement source
            || source.ContextMenu is not ContextMenu menu)
        {
                e.Handled = true;
            return;
        }

        GroupRow? row = null;

        /* 事件源就是被右键的那个列表项（键盘下是当前项）。 */
        row = source.DataContext as GroupRow;

        if (row is not { IsReal: true })
        {
            e.Handled = true;
            return;
        }

        GroupList.SelectedItem = row;

        menu.Items.Clear();

        var rename = new MenuItem { Header = Loc.T("重命名分组…") };
        rename.Click += (_, _) => RenameGroup(row.Group!);
        menu.Items.Add(rename);

        var del = new MenuItem { Header = Loc.T("删除分组") };
        del.Click += (_, _) => DeleteGroup(row.Group!, row.Count);
        menu.Items.Add(del);

        /* 菜单已经挂在控件上（XAML 里那个空 ContextMenu），
         * WPF 会在本方法返回后自己打开 —— 这里不要再手动 IsOpen。 */
    }

    private void OnRenameGroupClick(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is GroupRow { IsReal: true } row) RenameGroup(row.Group!);
    }

    private void OnDeleteGroupClick(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is GroupRow { IsReal: true } row) DeleteGroup(row.Group!, row.Count);
    }

    private void RenameGroup(string oldName)
    {
        if (_host == null) return;

        string? name = UI.Windows.TextPromptWindow.Ask(Owner,
            Loc.T("重命名分组"),
            Loc.T("改名之后，这个组里的别名会一起跟过去。"), oldName);

        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (string.Equals(name, oldName, StringComparison.Ordinal)) return;

        if (SnippetGroupOps.Rename(_host.Settings, oldName, name) < 0)
        {
            _host.Notify(Loc.F("分组「{0}」已经存在", name));
            return;
        }

        _host.SaveSettings();
        _selectGroupAfterRefresh = name;
        Refresh();
        _host.Notify(Loc.F("分组已改名为「{0}」", name));
    }

    private void DeleteGroup(string name, int count)
    {
        if (_host == null) return;

        string question = count > 0
            ? Loc.F("删除分组「{0}」？\n\n这个分组里有 {1} 个别名，它们会回到「未分组」，不会被删除。", name, count)
            : Loc.F("删除分组「{0}」？", name);

        if (!UI.Windows.AppDialog.ConfirmDanger(Owner, question, Loc.T("删除分组"),
                okText: Loc.T("删除"))) return;

        SnippetGroupOps.Delete(_host.Settings, name);

        _host.SaveSettings();
        Refresh();
        _host.Notify(Loc.F("已删除分组「{0}」", name));
    }

    private void MoveTo(Snippet snippet, string group)
    {
        if (_host == null) return;

        SnippetGroupOps.Move(_host.Settings, snippet, group);

        _host.SaveSettings();
        Refresh();
        _host.Notify(string.IsNullOrWhiteSpace(group)
            ? Loc.T("已移到「未分组」")
            : Loc.F("已移到「{0}」", group));
    }

    /// <summary>全局搜索跳过来时，选中指定的那条别名。</summary>
    private void FocusPendingAlias()
    {
        string? trigger = _host?.TakePendingAlias();
        if (string.IsNullOrEmpty(trigger)) return;

        var snippet = Entries.FirstOrDefault(x =>
            string.Equals((x.Trigger ?? "").Trim(), trigger, StringComparison.CurrentCultureIgnoreCase));
        if (snippet == null) return;

        _editing = snippet;
        RefreshList();
        LoadEditor(snippet);

        if (EntryList.SelectedItem is Row row) EntryList.ScrollIntoView(row);
    }

    /// <summary>当前选中的别名（多选时是全部；没选中时退回编辑区那一条）。</summary>
    private List<Snippet> SelectedSnippets()
    {
        var list = new List<Snippet>();
        foreach (var item in EntryList.SelectedItems)
            if (item is Row row) list.Add(row.Snippet);

        if (list.Count == 0 && _editing != null) list.Add(_editing);
        return list;
    }

    // ---------------- 事件 ----------------

    private void OnOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        var opt = _host.Settings.Hotstring;

        /* 全角开关会改变"实际生效的前缀"（; ↔ ；），触发词里存的还是旧的那个 ——
         * 和改前缀符号是同一个坑，这里一并迁移。 */
        string oldPrefix = opt.EffectivePrefix ?? "";

        opt.FullWidth = FullWidthBox.IsChecked == true;
        opt.ExpandOnSpace = SpaceBox.IsChecked == true;
        opt.SwallowSpace = SwallowBox.IsChecked == true;

        string newPrefix = opt.EffectivePrefix ?? "";
        if (oldPrefix != newPrefix)
        {
            int moved = SnippetPrefixOps.Migrate(_host.Settings, oldPrefix, newPrefix);
            if (moved > 0) CoreLog.Info($"切换全角符号：{oldPrefix} → {newPrefix}，已迁移 {moved} 条别名");
        }

        _host.SaveSettings();
        RefreshList();
        if (_editing != null) LoadEditor(_editing);
    }

    private void OnPrefixChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress) return;

        string oldPrefix = _host.Settings.Hotstring.EffectivePrefix ?? "";
        string newPrefix = PrefixBox.Text ?? "";

        _host.Settings.Hotstring.Prefix = newPrefix;

        /* 前缀是**存在每条触发词里**的（;mail）。改前缀时必须把已有条目一起搬，
         * 否则列表按新前缀剥不掉 → 每条别名前面都多出一个旧前缀。 */
        int moved = SnippetPrefixOps.Migrate(_host.Settings, oldPrefix, newPrefix);
        if (moved > 0) CoreLog.Info($"改前缀符号：{oldPrefix} → {newPrefix}，已迁移 {moved} 条别名");

        _host.SaveSettings();
        Refresh();

        /* 编辑区里的触发词也要跟着刷新（它显示的是剥掉前缀后的值）。 */
        if (_editing != null) LoadEditor(_editing);
    }

    /// <summary>搜索框：Esc 清空（再按一次回到列表），Ctrl+F 全选关键词。</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (FilterBox.Text.Length > 0) FilterBox.Text = "";
            else EntryList.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterBox.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>排序：触发词（默认）/ 最近使用（新的在前）/ 使用次数（多的在前）。</summary>
    private static IEnumerable<Snippet> SortSnippets(IEnumerable<Snippet> source, int mode) => mode switch
    {
        1 => source.OrderByDescending(x => x.LastUsed ?? DateTime.MinValue)
                   .ThenBy(x => x.Trigger, StringComparer.OrdinalIgnoreCase),
        2 => source.OrderByDescending(x => x.UseCount)
                   .ThenBy(x => x.Trigger, StringComparer.OrdinalIgnoreCase),
        3 => source.OrderBy(x => x.Order)
                   .ThenBy(x => x.Trigger, StringComparer.OrdinalIgnoreCase),
        _ => source.OrderBy(x => x.Trigger, StringComparer.OrdinalIgnoreCase),
    };

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _sortMode = Math.Max(0, SortBox.SelectedIndex);
        RefreshList();
    }

    private void OnUnusedOnlyChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _unusedOnly = UnusedOnlyBox.IsChecked == true;
        RefreshList();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        RefreshList();
    }

    private void OnGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null) return;

        if (GroupList.SelectedItem is GroupRow row)
        {
            RenameGroupButton.IsEnabled = row.IsReal;
            DeleteGroupButton.IsEnabled = row.IsReal;
        }

        if (_suppress) return;
        RefreshList();
        SyncEditorToSelection();
    }

    /// <summary>页面级快捷键：Ctrl+F 搜索；列表有焦点时 Ctrl+A 全选。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && EntryList.IsKeyboardFocusWithin)
        {
            EntryList.SelectAll();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;

        if (EntryList.SelectedItem is Row row)
        {
            if (!ReferenceEquals(row.Snippet, _editing)) LoadEditor(row.Snippet);
        }
        else
        {
            _editing = null;
            ShowEditor(false);
        }
    }

    private void OnEditorChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;

        /* 编辑区可能指向一条"已经不在配置里"的条目：
         * 同步恢复覆盖了配置、或者这条在别处被删掉了。
         * 继续往里写只会改到一个游离对象，存进去也会在下一次刷新时消失 ——
         * 用户感受就是"改了没反应"。这里先核对一次，找不到就收起编辑区。 */
        if (!Entries.Any(x => ReferenceEquals(x, _editing)))
        {
            var again = Entries.FirstOrDefault(x =>
                string.Equals(x.Trigger, _editing.Trigger, StringComparison.OrdinalIgnoreCase));

            if (again == null)
            {
                _editing = null;
                ShowEditor(false);
                RefreshList();
                return;
            }

            LoadEditor(again);
        }

        _editing.Trigger = SnippetPrefixOps.Apply(KeyBox.Text, _host.Settings.Hotstring.EffectivePrefix);
        _editing.Text = ValueBox.Text ?? "";
        _editing.Comment = CommentBox.Text ?? "";
        _editing.Apps = AppsBox.Text?.Trim() ?? "";
        _editing.Group = GroupBox.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(_editing.Group))
            SnippetGroupOps.Add(_host.Settings, _editing.Group);   // 手打的新组名立刻登记，侧栏马上出现
        _editing.Enabled = EnabledBox.IsChecked == true;

        _host.SaveSettings();

        /* 只刷新列表，**不要**重建编辑区。
         * 以前这里调 Refresh()：它会重建分组下拉的选项、再把文本赋回输入框，
         * 于是用户在下拉框里打字打到一半就被清掉 —— 表现就是"分组改不了"。 */
        RefreshList();
        RefreshGroups();
        EditTitle.Text = Loc.F("正在编辑：{0}",
            SnippetPrefixOps.Strip(_editing.Trigger, _host.Settings.Hotstring.EffectivePrefix));

        /* 触发词撞车：两条同触发词时只有其中一条会命中，用户很难自己发现。 */
        bool duplicate = _editing.Trigger.Length > 0 && Entries.Any(x =>
            !ReferenceEquals(x, _editing) &&
            string.Equals((x.Trigger ?? "").Trim(), _editing.Trigger.Trim(), StringComparison.CurrentCultureIgnoreCase));

        if (duplicate)
        {
            StatusText.Text = Loc.T("⚠ 这个触发词和另一条别名重复，只有其中一条会生效");
            StatusText.Foreground = (Brush)FindResource("Brush.Warning");
        }
        else
        {
            StatusText.Text = Loc.T("已保存");
            StatusText.Foreground = (Brush)FindResource("Brush.TextTertiary");
        }
    }

    /// <summary>
    /// 底部的「移到分组」按钮。
    ///
    /// 右键菜单在部分环境里不好按出来（用户反馈过），所以给一个**显式按钮**，
    /// 走的是同一份分组列表，不会两处逻辑各写一遍。
    /// </summary>
    private void OnMoveClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var targets = SelectedSnippets();
        if (targets.Count == 0) return;

        var menu = new ContextMenu
        {
            PlacementTarget = MoveButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
        };

        FillMoveMenu(menu, targets);
        menu.IsOpen = true;
    }

    /// <summary>把"移到分组"的菜单项填进给定菜单（右键与底部按钮共用）。</summary>
    private void FillMoveMenu(ContextMenu menu, Snippet snippet)
        => FillMoveMenu(menu, new List<Snippet> { snippet });

    /// <summary>把"移到分组"的菜单项填进给定菜单；支持一次移动多条。</summary>
    private void FillMoveMenu(ContextMenu menu, List<Snippet> snippets)
    {
        menu.Items.Clear();
        if (snippets.Count == 0) return;

        bool allSameGroup = snippets
            .Select(x => (x.Group ?? "").Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Count() == 1;
        string current = allSameGroup ? (snippets[0].Group ?? "").Trim() : "\u0000";

        var move = new MenuItem { Header = snippets.Count > 1
            ? Loc.F("把这 {0} 条移到分组", snippets.Count)
            : Loc.T("移到分组") };

        foreach (string g in AllGroups())
        {
            string target = g;
            var mi = new MenuItem
            {
                Header = target,
                IsCheckable = true,
                IsChecked = string.Equals(current, target, StringComparison.CurrentCultureIgnoreCase),
            };
            mi.Click += (_, _) => MoveMany(snippets, target);
            move.Items.Add(mi);
        }

        if (move.Items.Count > 0) move.Items.Add(new Separator());

        var ungrouped = new MenuItem
        {
            Header = Loc.T("未分组"),
            IsCheckable = true,
            IsChecked = current.Length == 0,
        };
        ungrouped.Click += (_, _) => MoveMany(snippets, "");
        move.Items.Add(ungrouped);

        var create = new MenuItem { Header = Loc.T("新建分组…") };
        create.Click += (_, _) =>
        {
            string? name = AskNewGroup();
            if (name != null) MoveMany(snippets, name);
        };
        move.Items.Add(create);

        menu.Items.Add(move);
    }

    /* ---------------- 自定义顺序：列表内拖放重排 ---------------- */

    private Row? FindRowAt(Point point)
    {
        var hit = EntryList.InputHitTest(point) as DependencyObject;
        var item = ItemsControl.ContainerFromElement(EntryList, hit) as ListBoxItem;
        return item?.DataContext as Row;
    }

    private void OnEntryListDragOver(object sender, DragEventArgs e)
    {
        /* 只有「自定义顺序」时才把"拖进列表"当作重排；
         * 其他排序下拖动是"移到分组"，由左侧分组列表处理。 */
        if (_sortMode != 3 || !e.Data.GetDataPresent("clp/snippet"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;

        /* 指示线画在"将要插入的位置"：落在行上半 → 行上沿，下半 → 行下沿；
         * 空白处 → 列表末尾。 */
        _insertLine ??= UI.InsertLineAdorner.Show(EntryList);

        if (_insertLine != null)
        {
            var target = FindRowAt(e.GetPosition(EntryList));
            double y;

            if (target == null)
            {
                y = EntryList.ActualHeight;
            }
            else if (EntryList.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem container)
            {
                var top = container.TranslatePoint(new Point(0, 0), EntryList).Y;
                double half = container.ActualHeight / 2;
                y = e.GetPosition(EntryList).Y > top + half
                    ? top + container.ActualHeight
                    : top;
            }
            else
            {
                y = EntryList.ActualHeight;
            }

            _insertLine.MoveTo(y);
        }

        e.Handled = true;
    }

    private void OnEntryListDrop(object sender, DragEventArgs e)
    {
        RemoveInsertLine();

        if (_host == null || _sortMode != 3) return;
        if (e.Data.GetData("clp/snippet") is not Snippet dragged) return;

        var target = FindRowAt(e.GetPosition(EntryList));

        /* 以"当前显示顺序"为基准重排，再按顺序重新编号。 */
        var ordered = SortSnippets(_host.Settings.Global.Snippets, 3).ToList();
        ordered.Remove(dragged);

        int index = target == null ? ordered.Count : ordered.IndexOf(target.Snippet);
        if (index < 0) index = ordered.Count;

        /* 落在目标行的下半部分 → 插到它后面。 */
        if (target != null &&
            EntryList.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem container &&
            e.GetPosition(container).Y > container.ActualHeight / 2)
        {
            index++;
        }

        ordered.Insert(Math.Clamp(index, 0, ordered.Count), dragged);

        for (int i = 0; i < ordered.Count; i++) ordered[i].Order = i;

        _host.SaveSettings();
        Refresh();
        e.Handled = true;
    }

    /// <summary>移除插入位置指示线（拖拽结束、离开列表时调）。</summary>
    private void RemoveInsertLine()
    {
        _insertLine?.Remove();
        _insertLine = null;
    }

    /// <summary>把一批别名移到同一个分组。</summary>
    private void MoveMany(List<Snippet> snippets, string group)
    {
        if (_host == null || snippets.Count == 0) return;

        if (snippets.Count == 1)
        {
            MoveTo(snippets[0], group);
            return;
        }

        foreach (var snippet in snippets) SnippetGroupOps.Move(_host.Settings, snippet, group);

        _host.SaveSettings();
        Refresh();

        _host.Notify(string.IsNullOrWhiteSpace(group)
            ? Loc.F("已把 {0} 条别名移到「未分组」", snippets.Count)
            : Loc.F("已把 {0} 条别名移到「{1}」", snippets.Count, group));
    }


    /// <summary>
    /// 列表正文那一列：**只显示备注**。
    ///
    /// 替换内容永远不在这里出现（避免一眼就看到隐私文本）；没写备注就给一句
    /// 可操作的提示，而不是留空。
    /// </summary>
    private static string BuildPreview(Snippet snippet)
    {
        string comment = (snippet.Comment ?? "").Trim();
        return comment.Length > 0 ? Shorten(comment, 56) : Loc.T("（无备注）");
    }

    /* ---------------- 只在指定程序里展开：两种选择方式 ---------------- */

    /// <summary>方式一：从文件管理器里挑 exe（可多选）。</summary>
    private void OnPickAppFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择要限定的程序"),
            Filter = Loc.T("程序") + " (*.exe)|*.exe|" + Loc.T("所有文件") + " (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        /* 只取文件名：进程名匹配用的是 exe 名字，不是完整路径。 */
        AppendApps(dialog.FileNames.Select(System.IO.Path.GetFileName).Where(n => !string.IsNullOrEmpty(n))!);
    }

    /// <summary>方式二：十字拾取 —— 在那个程序的窗口上点一下。</summary>
    private void OnPickAppWindow(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.WindowPickerWindow { Owner = Window.GetWindow(this) };
            picker.ShowDialog();

            if (picker.PickedWindow == IntPtr.Zero) return;

            var info = Core.Windows.WindowService.Describe(picker.PickedWindow);
            string exe = (info?.ExeName ?? "").Trim();
            if (exe.Length == 0)
            {
                _host?.Notify(Loc.T("没认出这个窗口属于哪个程序"));
                return;
            }

            AppendApps(new[] { exe });
        }
        catch (Exception ex)
        {
            CoreLog.Error("十字拾取程序失败", ex);
            _host?.Notify(Loc.T("拾取失败：") + ex.Message);
        }
    }

    /// <summary>把程序名追加进输入框（去重、统一用逗号分隔）。</summary>
    private void AppendApps(IEnumerable<string> names)
    {
        var list = (AppsBox.Text ?? "")
            .Split(new[] { ',', '，', ';', '；', '、' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

        foreach (string name in names)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) continue;
            if (list.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(n);
        }

        AppsBox.Text = string.Join(", ", list);
        _host?.Notify(Loc.T("已加入限定程序"));
    }

    /// <summary>把文本放进剪贴板（失败给提示，不抛）。</summary>
    private void CopyToClipboard(string? text, string doneMessage)
    {
        string value = text ?? "";
        if (value.Length == 0)
        {
            _host?.Notify(Loc.T("这条别名的内容为空"));
            return;
        }

        try
        {
            Clipboard.SetText(value);
            _host?.Notify(doneMessage);
        }
        catch (Exception ex)
        {
            CoreLog.Error("复制到剪贴板失败", ex);
            _host?.Notify(Loc.T("复制失败：") + ex.Message);
        }
    }

    /// <summary>复制一条别名（触发词自动加序号，避免撞车）。</summary>
    private void DuplicateSnippet(Snippet source)
    {
        if (_host == null) return;

        string prefix = _host.Settings.Hotstring.EffectivePrefix ?? "";
        string baseName = SnippetPrefixOps.Strip(source.Trigger, prefix);
        if (baseName.Length == 0) baseName = Loc.T("副本");

        string candidate = baseName + "2";
        int n = 2;
        while (Entries.Any(x => string.Equals(SnippetPrefixOps.Strip(x.Trigger, prefix), candidate,
                                               StringComparison.CurrentCultureIgnoreCase)))
        {
            n++;
            candidate = baseName + n;
        }

        var copy = new Snippet
        {
            Trigger = SnippetPrefixOps.Apply(candidate, prefix),
            Text = source.Text,
            Comment = source.Comment,
            Group = source.Group,
            Apps = source.Apps,
            Enabled = source.Enabled,
        };

        Entries.Add(copy);
        _host.SaveSettings();
        _editing = copy;
        Refresh();
        LoadEditor(copy);
        KeyBox.Focus();
        KeyBox.CaretIndex = KeyBox.Text.Length;
        _host.Notify(Loc.F("已复制为「{0}」", candidate));
    }

    /// <summary>一键删除所有"从未展开过"的别名（先确认）。</summary>
    private void OnCleanupClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var unused = _host.Settings.Global.Snippets.Where(x => x.UseCount <= 0).ToList();
        if (unused.Count == 0)
        {
            _host.Notify(Loc.T("没有从未使用的别名"));
            return;
        }

        if (!UI.Windows.AppDialog.Confirm(Owner,
                Loc.F("删除 {0} 条从未使用过的别名？\n\n删除后不可撤销（可以在「同步与备份」里找回旧备份）。", unused.Count),
                Loc.T("清理未使用"),
                Windows.DialogKind.Warn,
                okText: Loc.T("删除")))
        {
            return;
        }

        foreach (var snippet in unused) _host.Settings.Global.Snippets.Remove(snippet);
        _editing = null;
        _host.SaveSettings();
        Refresh();

        _host.Notify(Loc.F("已删除 {0} 条从未使用的别名", unused.Count));
    }

    /// <summary>导出"当前分组"的别名（全部视图 = 导出全部）。</summary>
    private void OnExportGroupClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        string? group = CurrentGroup;
        var list = group == null
            ? _host.Settings.Global.Snippets.ToList()
            : _host.Settings.Global.Snippets
                .Where(x => string.Equals((x.Group ?? "").Trim(), group, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

        if (list.Count == 0)
        {
            _host.Notify(Loc.T("这个分组里还没有别名"));
            return;
        }

        string name = group == null
            ? "keyboard-enhance-aliases.json"
            : $"keyboard-enhance-aliases-{SanitizeFileName(group.Length == 0 ? Loc.T("未分组") : group)}.json";

        ExportToFile(list, name);
    }

    /// <summary>把文件名里 Windows 不接受的字符换掉。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        string cleaned = new string(chars).Trim();
        return cleaned.Length == 0 ? "aliases" : cleaned;
    }

    /// <summary>导出指定别名到文件（两个导出入口共用）。</summary>
    private void ExportToFile(List<Snippet> snippets, string suggestedName)
    {
        if (_host == null || snippets.Count == 0) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("导出别名"),
            Filter = Loc.T("别名文件 (*.json)|*.json"),
            FileName = suggestedName,
            DefaultExt = ".json",
        };

        if (dlg.ShowDialog(Owner) != true) return;

        try
        {
            System.IO.File.WriteAllText(dlg.FileName,
                SnippetPortable.Export(snippets),
                new System.Text.UTF8Encoding(false));

            _host.Notify(Loc.F("已导出 {0} 条别名", snippets.Count));
        }
        catch (Exception ex)
        {
            CoreLog.Error("导出别名失败", ex);
            _host.Notify(Loc.T("导出失败：") + ex.Message);
        }
    }

    /// <summary>别名体检：重复触发词 / 空内容 / 前缀不对 / 分组未登记。</summary>
    private void OnAuditClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var issues = SnippetAudit.Run(_host.Settings);

        /* 没用过的条目不算"问题"，但对清理很有用 —— 附一句，并告诉用户去哪儿看。 */
        int unused = _host.Settings.Global.Snippets.Count(x => x.UseCount <= 0);
        string unusedHint = unused > 0
            ? "\n\n" + Loc.F("另有 {0} 条别名还没用过，勾上右上角「从未使用」就能只看它们。", unused)
            : "";

        if (issues.Count == 0)
        {
            UI.Windows.AppDialog.Success(Owner,
                Loc.T("没有发现问题：触发词不重复、内容都不为空、分组也都登记过了。") + unusedHint,
                Loc.T("别名体检"));
            return;
        }

        UI.Windows.AppDialog.Warn(Owner,
            Loc.F("发现 {0} 个问题：", issues.Count) + "\n\n" + SnippetAudit.Describe(issues) + unusedHint,
            Loc.T("别名体检"));

        /* 能自动修的那几类，问一句就修 —— 否则用户看完列表还得自己一条条改。 */
        int fixable = issues.Count(x => x.Title is "触发词重复" or "前缀不对" or "分组未登记");
        if (fixable == 0) return;

        if (!UI.Windows.AppDialog.Confirm(Owner,
                Loc.F("其中 {0} 个可以自动修（前缀换成当前的、分组补登记、重复的加序号）。要修吗？", fixable),
                Loc.T("自动修复"),
                Windows.DialogKind.Question,
                okText: Loc.T("修复")))
        {
            return;
        }

        var fixedResult = SnippetAudit.Fix(_host.Settings);
        _host.SaveSettings();
        Refresh();

        _host.Notify(Loc.F("已修复 {0} 项：前缀 {1}、分组 {2}、触发词 {3}",
            fixedResult.Total, fixedResult.PrefixFixed, fixedResult.GroupsRegistered, fixedResult.TriggersRenamed));
    }

    /// <summary>导出全部别名到一个 JSON 文件。</summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        ExportToFile(_host.Settings.Global.Snippets.ToList(), "keyboard-enhance-aliases.json");
    }

    /// <summary>从 JSON 文件导入别名（合并，不删除本地已有条目）。</summary>
    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("导入别名"),
            Filter = Loc.T("别名文件 (*.json)|*.json|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
        };

        if (dlg.ShowDialog(Owner) != true) return;

        List<SnippetPortable.Entry>? parsed;
        try
        {
            parsed = SnippetPortable.Parse(System.IO.File.ReadAllText(dlg.FileName));
        }
        catch (Exception ex)
        {
            CoreLog.Error("读取别名文件失败", ex);
            _host.Notify(Loc.T("读取失败：") + ex.Message);
            return;
        }

        if (parsed == null)
        {
            _host.Notify(Loc.T("这个文件不是有效的别名文件（需要 JSON）"));
            return;
        }

        /* 有现存数据时才问"合并还是替换"；空表直接导入，少一次打扰。 */
        bool replace = false;
        int existing = _host.Settings.Global.Snippets.Count;
        if (existing > 0)
        {
            bool confirmed = UI.Windows.AppDialog.Confirm(Owner,
                Loc.F("导入会和现有 {0} 条别名合并。\n\n要改成「替换全部」吗？替换会先清空现有别名，且不可撤销。", existing),
                Loc.T("导入别名"),
                Windows.DialogKind.Question,
                okText: Loc.T("替换全部"),
                cancelText: Loc.T("合并导入"));

            replace = confirmed;
        }

        var result = SnippetPortable.Import(parsed, _host.Settings.Global.Snippets, replace);
        _host.SaveSettings();
        _editing = null;
        Refresh();

        _host.Notify(replace
            ? Loc.F("已替换为 {0} 条别名", result.Added)
            : Loc.F("导入完成：新增 {0} 条，更新 {1} 条", result.Added, result.Updated));
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        string prefix = _host.Settings.Hotstring.EffectivePrefix ?? "";
        var entry = new Snippet
        {
            Trigger = prefix,
            Text = "",
            Comment = Loc.T("新别名"),
            Group = CurrentGroup ?? "",
            Enabled = true,
        };

        if (!string.IsNullOrWhiteSpace(entry.Group)) SnippetGroupOps.Add(_host.Settings, entry.Group);

        Entries.Add(entry);
        _host.SaveSettings();

        _editing = entry;
        Refresh();

        /* ⚠ Refresh() 里的 SyncEditorToSelection 只在"选中的不是当前编辑项"时才 LoadEditor，
         * 而这里 _editing 已经等于新条目 —— 于是编辑区永远不显示，
         * 用户点了「新增」只看列表多一行、右边空空。必须显式打开一次。 */
        LoadEditor(entry);

        KeyBox.Focus();
        KeyBox.CaretIndex = KeyBox.Text.Length;
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var targets = SelectedSnippets();
        if (targets.Count == 0) return;

        if (targets.Count == 1)
        {
            DeleteSnippet(targets[0]);
            return;
        }

        /* 多条一起删：必须确认。别名是用户攒出来的东西，误删很难恢复。 */
        if (!UI.Windows.AppDialog.Confirm(Owner,
                Loc.F("删除选中的 {0} 条别名？\n\n删除后不可撤销。", targets.Count),
                Loc.T("删除别名"), Windows.DialogKind.Warn, okText: Loc.T("删除")))
        {
            return;
        }

        foreach (var snippet in targets) Entries.Remove(snippet);
        _editing = null;
        _host.SaveSettings();
        Refresh();
        _host.Notify(Loc.F("已删除 {0} 条别名", targets.Count));
    }

    private void DeleteSnippet(Snippet snippet)
    {
        if (_host == null) return;

        Entries.Remove(snippet);
        _host.SaveSettings();
        if (ReferenceEquals(_editing, snippet)) _editing = null;
        Refresh();
    }

    /// <summary>列表里别名的右键菜单：移到分组 / 删除（Shift+F10 同样可用）。</summary>
    private void OnEntryContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_host == null
            || e.Source is not FrameworkElement source
            || source.ContextMenu is not ContextMenu menu)
        {
                e.Handled = true;
            return;
        }

        Row? row = null;

        row = source.DataContext as Row;

        if (row == null)
        {
            e.Handled = true;
            return;
        }

        EntryList.SelectedItem = row;

        FillMoveMenu(menu, row.Snippet);

        string name = SnippetPrefixOps.Strip(row.Snippet.Trigger, _host.Settings.Hotstring.EffectivePrefix);

        var copyText = new MenuItem { Header = Loc.T("复制内容") };
        copyText.Click += (_, _) => CopyToClipboard(row.Snippet.Text, Loc.T("内容已复制"));
        menu.Items.Add(copyText);

        var copyTrigger = new MenuItem { Header = Loc.T("复制触发词") };
        copyTrigger.Click += (_, _) => CopyToClipboard(name, Loc.T("触发词已复制"));
        menu.Items.Add(copyTrigger);

        menu.Items.Add(new Separator());

        var dup = new MenuItem { Header = Loc.T("复制一个副本") };
        dup.Click += (_, _) => DuplicateSnippet(row.Snippet);
        menu.Items.Add(dup);

        menu.Items.Add(new Separator());

        var del = new MenuItem { Header = Loc.T("删除这个别名") };
        del.Click += (_, _) => DeleteSnippet(row.Snippet);
        menu.Items.Add(del);

    }
}
