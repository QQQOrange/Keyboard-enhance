using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CapsLockPlus.Core.Icons;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Views;

public partial class QbarSettingsView : UserControl
{
    private AppHost? _host;
    private string _tab = "search";
    private QEntry? _editing;
    private bool _suppress;

    private static readonly string[] Palette =
    {
        "#2B6CF6", "#6C4CF6", "#22C1E8", "#1F9D62", "#D9822B", "#E5484D", "#111827", "#8A93A6",
    };

    public QbarSettingsView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        BuildSwatches();
        Tabs.SelectedIndex = 0;
        Refresh();
    }

    private void BuildSwatches()
    {
        ColorSwatches.Items.Clear();
        foreach (var color in Palette)
        {
            var btn = new Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 7, 7),
                Background = (Brush)new BrushConverter().ConvertFromString(color)!,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = color,
                ToolTip = color,
            };
            btn.Template = (ControlTemplate)FindResource("SwatchTemplate");
            btn.Click += (_, _) =>
            {
                if (_host == null) return;
                _host.Settings.Qbar.BorderColor = color;
                _host.Settings.Qbar.AccentColor = color;
                _host.SaveSettings();
                MarkSelectedSwatch();
            };
            ColorSwatches.Items.Add(btn);
        }
    }

    private void MarkSelectedSwatch()
    {
        if (_host == null) return;
        string current = _host.Settings.Qbar.AccentColor;
        foreach (Button b in ColorSwatches.Items)
        {
            bool sel = string.Equals(b.Tag as string, current, StringComparison.OrdinalIgnoreCase);
            b.BorderBrush = sel ? (Brush)FindResource("Brush.TextPrimary") : Brushes.Transparent;
        }
    }

    public void Refresh()
    {
        if (_host == null) return;
        _suppress = true;
        ListCountSlider.Value = _host.Settings.Qbar.ListCount;
        FontSizeSlider.Value = _host.Settings.Qbar.TextFontSize;
        UpdateFontPreview();
        ShowIconsBox.IsChecked = _host.Settings.Qbar.ShowIcons;
        _suppress = false;
        MarkSelectedSwatch();
        LoadEntries();
    }

    private List<QEntry> CurrentList() => _tab switch
    {
        "run" => _host!.Settings.Run,
        "web" => _host!.Settings.Web,
        "hotstring" => _host!.Settings.HotStrings,
        _ => _host!.Settings.Search,
    };

    private void LoadEntries()
    {
        if (_host == null) return;
        _editing = null;
        _suppress = true;
        EntryList.ItemsSource = null;

        // 过滤：按关键字 / 备注 / 内容模糊匹配，大小写不敏感。
        // 这样内置 300 多个引擎也能很快找到想要的那个。
        // 匹配规则抽在 QEntry.Matches 里，自检能直接验。
        string filter = FilterBox?.Text?.Trim() ?? "";
        var all = CurrentList().ToList();
        int total = all.Count;
        var items = QEntry.Filter(all, filter).Select(e => new EntryVm(e)).ToList();
        EntryList.ItemsSource = items;
        if (items.Count > 0) EntryList.SelectedIndex = 0;
        _suppress = false;
        if (items.Count > 0) BindEditor(items[0].Entry);
        else ClearEditor();

        if (FilterCount != null)
        {
            FilterCount.Text = filter.Length > 0
                ? $"匹配 {items.Count} / {total}"
                : $"共 {total} 项";
        }

        ValueHelp.Text = _tab switch
        {
            "search" => "用 {q} 代表关键词。例如 https://www.bing.com/search?q={q}。关键字填 default 表示默认搜索。",
            "run" => "填程序路径或命令，例如 C:\\Windows\\notepad.exe 或 notepad.exe。",
            "web" => "填完整网址，例如 https://github.com。",
            "hotstring" => "在任意地方输入关键字后按 Caps+Tab，会被替换成这里的内容。",
            _ => "",
        };
        ValueLabel.Text = _tab switch
        {
            "search" => "搜索链接",
            "run" => "程序路径",
            "web" => "网址",
            "hotstring" => "替换成",
            _ => "内容",
        };
        EntryHint.Text = _tab switch
        {
            "search" => "Qbar 里输入「关键字 内容」即可用对应引擎搜索",
            "run" => "Qbar 里输入关键字回车即可启动",
            "web" => "Qbar 里输入关键字回车即可打开",
            "hotstring" => "输入关键字后按 Caps+Tab 展开",
            _ => "",
        };
    }

    /// <summary>把某条记录回填到右侧编辑框。</summary>
    private void BindEditor(QEntry entry)
    {
        _editing = entry;
        _suppress = true;
        KeyBox.Text = entry.Key;
        ValueBox.Text = entry.Value;
        CommentBox.Text = entry.Comment;
        IconBox.Text = entry.IconPath;
        EditTitle.Text = string.IsNullOrEmpty(entry.Key) ? "新条目" : $"正在编辑：{entry.Key}";
        _suppress = false;
        UpdateIconPreview(entry);
    }

    /// <summary>图标预览：有图标显示图标，没有就显示首字（和 Qbar 里的表现一致）。</summary>
    private void UpdateIconPreview(QEntry entry)
    {
        var icon = IconCache.ForEntry(entry);
        if (icon != null)
        {
            IconPreview.Source = icon;
            IconPreview.Visibility = Visibility.Visible;
            IconFallback.Visibility = Visibility.Collapsed;
        }
        else
        {
            IconPreview.Source = null;
            IconPreview.Visibility = Visibility.Collapsed;
            IconFallback.Visibility = Visibility.Visible;
            IconFallback.Text = IconCache.Initial(entry.DisplayName);
            IconFallback.Foreground = new SolidColorBrush(IconCache.ColorFor(entry.DisplayName));
        }
    }

    private void OnPickIcon(object sender, RoutedEventArgs e)
    {
        if (_host == null || _editing == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图标（图片或程序）",
            Filter = "图标或程序|*.png;*.ico;*.jpg;*.jpeg;*.exe;*.dll|所有文件|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        IconBox.Text = dlg.FileName;
        _editing.IconPath = dlg.FileName;
        _host.SaveSettings();
        UpdateIconPreview(_editing);
        EntryList.Items.Refresh();
    }

    private void ClearEditor()
    {
        _editing = null;
        _suppress = true;
        KeyBox.Text = "";
        ValueBox.Text = "";
        CommentBox.Text = "";
        IconBox.Text = "";
        EditTitle.Text = "（选择左边的条目，或点新增）";
        _suppress = false;
        IconPreview.Source = null;
        IconPreview.Visibility = Visibility.Collapsed;
        IconFallback.Visibility = Visibility.Visible;
        IconFallback.Text = "?";
        IconFallback.Foreground = (Brush)FindResource("Brush.TextTertiary");
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => LoadEntries();

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || !IsLoaded) return;
        if (Tabs.SelectedItem is TabItem item && item.Tag is string tag)
        {
            _tab = tag;
            LoadEntries();
        }
    }

    private void OnEntrySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (EntryList.SelectedItem is not EntryVm vm) return;
        BindEditor(vm.Entry);
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var entry = new QEntry { Key = "new", Comment = "" };
        CurrentList().Add(entry);
        _host.SaveSettings();
        _suppress = true;
        EntryList.ItemsSource = null;
        var items = CurrentList().Select(x => new EntryVm(x)).ToList();
        EntryList.ItemsSource = items;
        EntryList.SelectedIndex = items.Count - 1;
        _suppress = false;
        BindEditor(entry);
        KeyBox.Focus();
        KeyBox.SelectAll();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || EntryList.SelectedItem is not EntryVm vm) return;
        if (vm.Entry.Key == "default")
        {
            _host.Notify("默认搜索不能删除，只能修改");
            return;
        }
        CurrentList().Remove(vm.Entry);
        _host.SaveSettings();
        LoadEntries();
        _host.Notify("已删除");
    }

    private void OnFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;
        _editing.Key = KeyBox.Text.Trim();
        _editing.Value = ValueBox.Text.Trim();
        _editing.Comment = CommentBox.Text.Trim();
        _editing.IconPath = IconBox.Text.Trim();
        _host.SaveSettings();
        // 同步刷新左侧列表的显示（保持当前选择与编辑焦点）
        EntryList.Items.Refresh();
        UpdateIconPreview(_editing);
    }

    private void OnAppearanceChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Qbar.ListCount = (int)ListCountSlider.Value;
        _host.Settings.Qbar.TextFontSize = FontSizeSlider.Value;
        _host.Settings.Qbar.ShowIcons = ShowIconsBox.IsChecked == true;
        UpdateFontPreview();
        _host.SaveSettings();
    }

    /// <summary>字号预览：让用户直接看到调大字号后的实际效果。</summary>
    private void UpdateFontPreview()
    {
        if (FontPreview == null) return;
        double size = FontSizeSlider.Value;
        FontPreview.FontSize = size;
        // 高度按真实字形高度算，和 Qbar 输入框保持一致的行为
        FontPreview.MinHeight = Core.Text.TextMetrics.MeasureHeight(size, "预览 Preview 文字测试 qgp");
    }

    private void OnResetAppearance(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _host.Settings.Qbar = new QbarOptions();
        _host.SaveSettings();
        Refresh();
        _host.Notify("Qbar 外观已恢复默认");
    }

    private void OnTryClick(object sender, RoutedEventArgs e) => _host?.ShowQbar();

    private sealed class EntryVm
    {
        public EntryVm(QEntry entry)
        {
            Entry = entry;

            /* 图标：内置引擎有品牌图标（随程序打包），
             * 程序条目会取 exe 图标，都没有则退回首字母色块。
             * IconCache.ForEntry 已封装这套优先级，直接用。 */
            try { Icon = IconCache.ForEntry(entry); } catch { Icon = null; }

            LetterBrush = new SolidColorBrush(ColorFor(entry.Key));
            Initial = string.IsNullOrWhiteSpace(entry.Key) ? "?"
                : entry.Key[..1].ToUpperInvariant();
        }

        public QEntry Entry { get; }
        public string Key => Entry.Key;
        public string Value => Entry.Value;
        public string Comment => Entry.Comment;

        /// <summary>图标（可能为 null）。</summary>
        public System.Windows.Media.ImageSource? Icon { get; }

        /// <summary>没有图标时显示的首字母。</summary>
        public string Initial { get; }

        /// <summary>首字母色块的背景色（按关键字取稳定颜色）。</summary>
        public System.Windows.Media.Brush LetterBrush { get; }

        /// <summary>有图标时隐藏色块。</summary>
        public System.Windows.Visibility NoIconVisibility =>
            Icon == null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        private static Color ColorFor(string text)
        {
            // 稳定配色：同一关键字永远同一个颜色，不随列表顺序变
            unchecked
            {
                int h = 17;
                foreach (char c in text) h = h * 31 + c;
                var palette = new[]
                {
                    Color.FromRgb(0x2B, 0x6C, 0xF6), Color.FromRgb(0x7C, 0x5C, 0xE0),
                    Color.FromRgb(0x1F, 0xA8, 0x9C), Color.FromRgb(0x2E, 0xA0, 0x4F),
                    Color.FromRgb(0xD9, 0x8A, 0x1F), Color.FromRgb(0xD4, 0x4A, 0x5B),
                };
                return palette[Math.Abs(h) % palette.Length];
            }
        }
    }
}
