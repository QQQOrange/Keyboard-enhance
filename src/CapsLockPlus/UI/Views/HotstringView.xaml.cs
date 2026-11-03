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

    /* ---------------- 拖拽状态 ---------------- */

    /// <summary>按下左键时的位置（判断"是点击还是拖拽"）。</summary>
    private Point _dragStartPoint;

    /// <summary>按下左键时命中的别名（拖拽源）。</summary>
    private Snippet? _dragSnippet;

    /// <summary>当前高亮的分组行（拖拽经过时）。</summary>
    private ListBoxItem? _dragHoverItem;

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
        public override string ToString() => KeyLabel;
    }

    public HotstringView() => InitializeComponent();

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
            NoteFirstBox.IsChecked = opt.ShowNoteFirst;
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

        var rows = new List<GroupRow> { new() { Group = null, Label = Loc.T("全部"), Count = all.Count } };
        rows.AddRange(groups.Select(g => new GroupRow
        {
            Group = g,
            Label = g,
            Count = all.Count(x => string.Equals(x.Group?.Trim(), g, StringComparison.CurrentCultureIgnoreCase)),
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

        var rows = all
            .Where(x => group == null
                        || (group.Length == 0
                            ? string.IsNullOrWhiteSpace(x.Group)
                            : string.Equals(x.Group?.Trim(), group, StringComparison.CurrentCultureIgnoreCase)))
            .Where(x => string.IsNullOrWhiteSpace(filter)
                        || (x.Trigger?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (x.Comment?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (x.Text?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(x => x.Trigger, StringComparer.OrdinalIgnoreCase)
            .Select(x => new Row
            {
                Snippet = x,
                KeyLabel = StripPrefix(x.Trigger, opt.EffectivePrefix),
                Initial = InitialOf(x.Trigger, opt.EffectivePrefix),
                GroupLabel = string.IsNullOrWhiteSpace(x.Group) ? "" : x.Group!.Trim(),
                Preview = Shorten(opt.ShowNoteFirst && !string.IsNullOrWhiteSpace(x.Comment) ? x.Comment : x.Text, 56),
            })
            .ToList();

        _suppress = true;
        try
        {
            EntryList.ItemsSource = rows;
            FilterCount.Text = Loc.F("共 {0} 条", rows.Count);
            EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EntryList.SelectedItem = rows.FirstOrDefault(r => ReferenceEquals(r.Snippet, _editing));
        }
        finally { _suppress = false; }
    }

    private void SyncEditorToSelection()
    {
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

        var opt = _host.Settings.Hotstring;
        string current = opt.EffectivePrefix ?? "";
        char? stale = null;
        int count = 0;

        foreach (var snippet in Entries)
        {
            string trigger = snippet.Trigger ?? "";
            if (trigger.Length < 2) continue;

            char first = trigger[0];
            if (!IsPrefixSymbol(first)) continue;
            if (current.Length > 0 && first == current[0]) continue;

            if (stale == null) stale = first;
            else if (stale != first) return;      // 新旧混着，不猜

            count++;
        }

        if (stale == null || count == 0) return;

        int moved = SnippetPrefixOps.Migrate(_host.Settings, stale.Value.ToString(), current);
        if (moved == 0) return;

        CoreLog.Info($"别名前缀自动对齐：{stale} → {current}，共 {moved} 条");
        _host.SaveSettings();
        _host.Notify(Loc.F("已把 {0} 条别名的旧前缀统一成「{1}」", moved, current));
    }

    /// <summary>这个字符像不像"前缀符号"（用来识别旧前缀，避免误改正文）。</summary>
    private static bool IsPrefixSymbol(char c)
        => c is ';' or '/' or '\\' or '.' or ',' or ':' or '\''
              or '\'' or '"' or '~' or '!' or '@' or '#' or '$' or '%' or '^'
              or '&' or '*' or '-' or '_' or '+' or '=' or '|' or '。' or '，'
              or '；' or '、' or '：';

    // ---------------- 小工具 ----------------

    private static string InitialOf(string? trigger, string? prefix)
    {
        string s = StripPrefix(trigger, prefix);
        if (s.Length == 0) return "?";
        char c = char.ToUpperInvariant(s[0]);
        return char.IsLetterOrDigit(c) ? c.ToString() : s[..1];
    }

    /// <summary>列表/编辑框里显示触发词时去掉前缀符号（;mail 只显示 mail）。</summary>
    private static string StripPrefix(string? trigger, string? prefix)
    {
        if (string.IsNullOrEmpty(trigger)) return "";
        if (!string.IsNullOrEmpty(prefix) && trigger.StartsWith(prefix!, StringComparison.Ordinal))
            return trigger[prefix!.Length..];
        return trigger;
    }

    /// <summary>保存时把前缀补回去（用户只输入了 mail）。</summary>
    private static string ApplyPrefix(string? typed, string? prefix)
    {
        string name = typed?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) return "";
        if (string.IsNullOrEmpty(prefix)) return name;
        return name.StartsWith(prefix!, StringComparison.Ordinal) ? name : prefix + name;
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
            KeyBox.Text = StripPrefix(entry.Trigger, _host?.Settings.Hotstring.EffectivePrefix);
            EditTitle.Text = Loc.F("正在编辑：{0}", KeyBox.Text.Length == 0 ? Loc.T("（新别名）") : KeyBox.Text);
            ValueBox.Text = entry.Text ?? "";
            CommentBox.Text = entry.Comment ?? "";
            AppsBox.Text = entry.Apps ?? "";
            EnabledBox.IsChecked = entry.Enabled;
            StatusText.Text = "";

            GroupBox.Text = entry.Group ?? "";
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
        string label = StripPrefix(snippet.Trigger, _host?.Settings.Hotstring.EffectivePrefix);
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
            ClearDragHover();
            Mouse.UpdateCursor();
        }
    }

    private void OnGroupDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("clp/snippet"))
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
        opt.ShowNoteFirst = NoteFirstBox.IsChecked == true;

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

        _editing.Trigger = ApplyPrefix(KeyBox.Text?.Trim(), _host.Settings.Hotstring.EffectivePrefix);
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
            StripPrefix(_editing.Trigger, _host.Settings.Hotstring.EffectivePrefix));
        StatusText.Text = Loc.T("已保存");
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
        if (_host == null || _editing == null) return;
        DeleteSnippet(_editing);
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

        menu.Items.Clear();

        var move = new MenuItem { Header = Loc.T("移到分组") };
        foreach (string g in AllGroups())
        {
            string target = g;
            var mi = new MenuItem
            {
                Header = target,
                IsCheckable = true,
                IsChecked = string.Equals(row.Snippet.Group?.Trim(), target,
                    StringComparison.CurrentCultureIgnoreCase),
            };
            mi.Click += (_, _) => MoveTo(row.Snippet, target);
            move.Items.Add(mi);
        }

        if (move.Items.Count > 0) move.Items.Add(new Separator());

        var ungrouped = new MenuItem
        {
            Header = Loc.T("未分组"),
            IsCheckable = true,
            IsChecked = string.IsNullOrWhiteSpace(row.Snippet.Group),
        };
        ungrouped.Click += (_, _) => MoveTo(row.Snippet, "");
        move.Items.Add(ungrouped);

        var create = new MenuItem { Header = Loc.T("新建分组…") };
        create.Click += (_, _) =>
        {
            string? name = AskNewGroup();
            if (name != null) MoveTo(row.Snippet, name);
        };
        move.Items.Add(create);

        menu.Items.Add(move);
        menu.Items.Add(new Separator());

        var del = new MenuItem { Header = Loc.T("删除这个别名") };
        del.Click += (_, _) => DeleteSnippet(row.Snippet);
        menu.Items.Add(del);

    }
}
