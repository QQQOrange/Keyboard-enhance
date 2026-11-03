using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private void BuildGroupOptions()
    {
        string current = (GroupBox.Text ?? "").Trim();

        _suppress = true;
        try
        {
            GroupBox.Items.Clear();
            foreach (var g in AllGroups()) GroupBox.Items.Add(g);
            GroupBox.Text = current;
        }
        finally { _suppress = false; }
    }

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

            BuildGroupOptions();
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

        opt.FullWidth = FullWidthBox.IsChecked == true;
        opt.ExpandOnSpace = SpaceBox.IsChecked == true;
        opt.SwallowSpace = SwallowBox.IsChecked == true;
        opt.ShowNoteFirst = NoteFirstBox.IsChecked == true;

        _host.SaveSettings();
        RefreshList();
        if (_editing != null) LoadEditor(_editing);
    }

    private void OnPrefixChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress) return;

        _host.Settings.Hotstring.Prefix = PrefixBox.Text ?? "";
        _host.SaveSettings();
        Refresh();
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
        _editing.Enabled = EnabledBox.IsChecked == true;

        _host.SaveSettings();

        /* 只刷新列表，**不要**重建编辑区。
         * 以前这里调 Refresh()：它会重建分组下拉的选项、再把文本赋回输入框，
         * 于是用户在下拉框里打字打到一半就被清掉 —— 表现就是"分组改不了"。 */
        RefreshList();
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
