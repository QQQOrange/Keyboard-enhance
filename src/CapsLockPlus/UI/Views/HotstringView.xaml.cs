using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Views;

/// <summary>
/// 「文本片段」页面：三栏布局 —— 左分组、中间全部片段、右边编辑。
///
/// 数据源是 <see cref="GlobalOptions.Snippets"/>（就是真正参与展开的那一份），
/// 以前它和「文字替换」两套并存，用户根本分不清编辑哪个才会生效 —— 现在只有这一个入口。
/// 没选中片段时右侧编辑栏整块隐藏。
/// </summary>
public partial class HotstringView : UserControl
{
    private AppHost? _host;
    private Snippet? _editing;
    private bool _suppress;

    /// <summary>分组行的 Group：null = 全部；"" = 未分组；其它 = 组名。</summary>
    private sealed class GroupRow
    {
        public string? Group { get; init; }
        public string Label { get; init; } = "";
        public int Count { get; init; }
        public override string ToString() => Label;
    }

    public sealed class Row
    {
        public Snippet Snippet { get; init; } = new();
        public string KeyLabel { get; init; } = "";
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

    private string? CurrentGroup => (GroupList.SelectedItem as GroupRow)?.Group;

    private void Refresh()
    {
        if (_host == null) return;

        var opt = _host.Settings.Hotstring;
        string filter = FilterBox.Text?.Trim() ?? "";
        string? keepGroup = CurrentGroup;

        var all = Entries.ToList();

        var groups = all.Select(x => x.Group?.Trim())
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct(StringComparer.CurrentCulture)
            .OrderBy(g => g, StringComparer.CurrentCulture)
            .ToList();

        var groupRows = new List<GroupRow>
        {
            new() { Group = null, Label = Loc.T("全部"), Count = all.Count },
        };
        groupRows.AddRange(groups.Select(g => new GroupRow
        {
            Group = g,
            Label = g!,
            Count = all.Count(x => string.Equals(x.Group?.Trim(), g, StringComparison.CurrentCulture)),
        }));
        groupRows.Add(new GroupRow
        {
            Group = "",
            Label = Loc.T("未分组"),
            Count = all.Count(x => string.IsNullOrWhiteSpace(x.Group)),
        });

        _suppress = true;
        try
        {
            GroupList.ItemsSource = groupRows;
            GroupList.SelectedItem = groupRows.FirstOrDefault(g => g.Group == keepGroup) ?? groupRows[0];
        }
        finally { _suppress = false; }

        string? group = CurrentGroup;

        var rows = all
            .Where(x => group == null
                        || (group.Length == 0
                            ? string.IsNullOrWhiteSpace(x.Group)
                            : string.Equals(x.Group?.Trim(), group, StringComparison.CurrentCulture)))
            .Where(x => string.IsNullOrWhiteSpace(filter)
                        || (x.Trigger?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (x.Comment?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (x.Text?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(x => x.Trigger, StringComparer.OrdinalIgnoreCase)
            .Select(x => new Row
            {
                Snippet = x,
                KeyLabel = x.Trigger ?? "",
                Preview = Shorten(opt.ShowNoteFirst && !string.IsNullOrWhiteSpace(x.Comment) ? x.Comment : x.Text, 56),
            })
            .ToList();

        _suppress = true;
        try
        {
            EntryList.ItemsSource = rows;
            CountText.Text = Loc.F("{0} 条", rows.Count);
            EntryList.SelectedItem = rows.FirstOrDefault(r => ReferenceEquals(r.Snippet, _editing));
            BuildGroupOptions();
        }
        finally { _suppress = false; }

        if (EntryList.SelectedItem is Row row) LoadEditor(row.Snippet);
        else { _editing = null; ShowEditor(false); }
    }

    private static string Shorten(string? s, int max)
    {
        s = (s ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
        return s.Length <= max ? s : s[..max] + "…";
    }

    private void BuildGroupOptions()
    {
        string current = (GroupBox.Text ?? "").Trim();

        var groups = Entries.Select(x => x.Group?.Trim())
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct(StringComparer.CurrentCulture)
            .OrderBy(g => g, StringComparer.CurrentCulture)
            .ToList();

        _suppress = true;
        try
        {
            GroupBox.Items.Clear();
            foreach (var g in groups) GroupBox.Items.Add(g);
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
            KeyBox.Text = entry.Trigger ?? "";
            ValueBox.Text = entry.Text ?? "";
            CommentBox.Text = entry.Comment ?? "";
            AppsBox.Text = entry.Apps ?? "";
            GroupBox.Text = entry.Group ?? "";
            EnabledBox.IsChecked = entry.Enabled;
            StatusText.Text = "";
        }
        finally { _suppress = false; }
    }

    /* ---------------- 事件 ---------------- */

    private void OnOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        var opt = _host.Settings.Hotstring;

        opt.FullWidth = FullWidthBox.IsChecked == true;
        opt.ExpandOnSpace = SpaceBox.IsChecked == true;
        opt.SwallowSpace = SwallowBox.IsChecked == true;
        opt.ShowNoteFirst = NoteFirstBox.IsChecked == true;

        _host.SaveSettings();
        Refresh();
    }

    private void OnPrefixChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress) return;

        /* 前缀符号换了，已有触发词要不要跟着改？
         * 不改：用户可能本来就有多种前缀。这里只影响"哪些触发词会被认"，
         * 列表上会立刻按新前缀过滤，看得到效果。 */
        _host.Settings.Hotstring.Prefix = PrefixBox.Text ?? "";
        _host.SaveSettings();
        Refresh();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        Refresh();
    }

    private void OnGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        Refresh();
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

        _editing.Trigger = KeyBox.Text?.Trim() ?? "";
        _editing.Text = ValueBox.Text ?? "";
        _editing.Comment = CommentBox.Text ?? "";
        _editing.Apps = AppsBox.Text?.Trim() ?? "";
        _editing.Group = GroupBox.Text?.Trim() ?? "";
        _editing.Enabled = EnabledBox.IsChecked == true;

        _host.SaveSettings();
        Refresh();
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
            Comment = Loc.T("新片段"),
            Group = CurrentGroup ?? "",
            Enabled = true,
        };
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

        Entries.Remove(_editing);
        _host.SaveSettings();
        _editing = null;
        Refresh();
    }
}
