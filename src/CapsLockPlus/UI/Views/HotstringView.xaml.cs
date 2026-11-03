using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Views;

/// <summary>
/// 「文本片段」独立页面（之前挤在 Qbar 面板的「文字替换」标签里）。
///
/// 页面职责：
///   · 触发设置 —— 前缀符号（只有一个）、全/半角、空格触发、吞空格、列表优先显示备注；
///   · 列表 —— 按分组排，显示"备注优先"的名字 + 内容预览 + 分组；
///   · 编辑 —— 触发词 / 内容 / 备注 / 分组 / 启用。
/// </summary>
public partial class HotstringView : UserControl
{
    private AppHost? _host;
    private QEntry? _editing;
    private bool _suppress;

    /// <summary>列表行（重建式刷新：改一个字就整表重建，简单且不会漏更新）。</summary>
    public sealed class Row
    {
        public QEntry Entry { get; init; } = new();
        public string KeyLabel { get; init; } = "";
        public string Preview { get; init; } = "";
        public string GroupLabel { get; init; } = "";
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

    private IEnumerable<QEntry> Entries => _host?.Settings.HotStrings ?? new List<QEntry>();

    /// <summary>重建列表。</summary>
    private void Refresh()
    {
        if (_host == null) return;

        string filter = FilterBox.Text?.Trim() ?? "";
        var opt = _host.Settings.Hotstring;

        var rows = QEntry.Filter(Entries, filter)
            .OrderBy(e => string.IsNullOrWhiteSpace(e.Group) ? "\uFFFF" : e.Group, StringComparer.CurrentCulture)
            .ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .Select(e => new Row
            {
                Entry = e,
                KeyLabel = opt.EffectivePrefix + e.Key,
                Preview = Shorten(e.Value, 48),
                GroupLabel = string.IsNullOrWhiteSpace(e.Group) ? "" : e.Group,
            })
            .ToList();

        string? keep = _editing?.Key;
        _suppress = true;
        try
        {
            EntryList.ItemsSource = rows;
            CountText.Text = Loc.F("{0} 条", rows.Count);

            if (keep != null)
            {
                var again = rows.FirstOrDefault(r => ReferenceEquals(r.Entry, _editing));
                EntryList.SelectedItem = again;
            }
            if (EntryList.SelectedItem == null && rows.Count > 0)
                EntryList.SelectedIndex = 0;

            BuildGroupOptions();
        }
        finally { _suppress = false; }

        if (EntryList.SelectedItem is Row row) LoadEditor(row.Entry);
        else { _editing = null; ClearEditor(); }
    }

    private static string Shorten(string s, int max)
    {
        s = (s ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
        return s.Length <= max ? s : s[..max] + "…";
    }

    private void BuildGroupOptions()
    {
        string current = (GroupBox.Text ?? "").Trim();
        var groups = Entries.Select(e => e.Group?.Trim())
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

    private void LoadEditor(QEntry entry)
    {
        _editing = entry;
        _suppress = true;
        try
        {
            KeyBox.Text = entry.Key ?? "";
            ValueBox.Text = entry.Value ?? "";
            CommentBox.Text = entry.Comment ?? "";
            GroupBox.Text = entry.Group ?? "";
            EnabledBox.IsChecked = entry.Enabled;
            StatusText.Text = "";
        }
        finally { _suppress = false; }
    }

    private void ClearEditor()
    {
        _suppress = true;
        try
        {
            KeyBox.Text = "";
            ValueBox.Text = "";
            CommentBox.Text = "";
            GroupBox.Text = "";
            EnabledBox.IsChecked = false;
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
        _host.Settings.Hotstring.Prefix = PrefixBox.Text ?? "";
        _host.SaveSettings();
        Refresh();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        Refresh();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (EntryList.SelectedItem is Row row) LoadEditor(row.Entry);
    }

    private void OnEditorChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;

        _editing.Key = KeyBox.Text?.Trim() ?? "";
        _editing.Value = ValueBox.Text ?? "";
        _editing.Comment = CommentBox.Text ?? "";
        _editing.Group = GroupBox.Text?.Trim() ?? "";
        _editing.Enabled = EnabledBox.IsChecked == true;

        _host.SaveSettings();
        Refresh();
        StatusText.Text = Loc.T("已保存");
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var entry = new QEntry
        {
            Key = "",
            Value = "",
            Comment = Loc.T("新片段"),
            Group = (GroupBox.Text ?? "").Trim(),
            Enabled = true,
        };
        _host.Settings.HotStrings.Add(entry);
        _host.SaveSettings();

        _editing = entry;
        Refresh();
        KeyBox.Focus();
        KeyBox.SelectAll();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _editing == null) return;

        _host.Settings.HotStrings.Remove(_editing);
        _host.SaveSettings();
        _editing = null;
        Refresh();
    }
}
