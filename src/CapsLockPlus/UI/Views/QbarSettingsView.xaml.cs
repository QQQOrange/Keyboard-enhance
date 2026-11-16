using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CapsLockPlus.Core.Icons;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Views;

public partial class QbarSettingsView : UserControl
{
    private AppHost? _host;
    private string _tab = "search";
    private QEntry? _editing;
    private bool _suppress;

    private readonly DispatcherTimer _titleTimer;
    private int _titleToken;

    private static readonly HttpClient TitleHttp = new() { Timeout = TimeSpan.FromSeconds(4) };

    static QbarSettingsView()
    {
        try
        {
            TitleHttp.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) KeyboardEnhance/1.0");
        }
        catch { }
    }

    private static readonly string[] Palette =
    {
        "#2B6CF6", "#6C4CF6", "#22C1E8", "#1F9D62", "#D9822B", "#E5484D", "#111827", "#8A93A6",
    };

    public QbarSettingsView()
    {
        InitializeComponent();
        _titleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _titleTimer.Tick += async (_, _) =>
        {
            _titleTimer.Stop();
            await FillWebTitleAsync();
        };
    }

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
        ImeOnOpenBox.IsChecked = _host.Settings.Qbar.SwitchToEnglishOnOpen;
        ImeBackBox.IsChecked = _host.Settings.Qbar.SwitchBackOnEngine;
        _suppress = false;
        MarkSelectedSwatch();
        LoadEntries();
    }

    private List<QEntry> CurrentList() => _tab switch
    {
        "run" => _host!.Settings.Run,
        "web" => _host!.Settings.Web,
        "pinned" => _host!.Settings.Qbar.Pinned,
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
        var items = QEntry.Filter(all, filter).Select(e => new EntryVm(e, _tab != "run")).ToList();
        EntryList.ItemsSource = items;
        if (items.Count > 0) EntryList.SelectedIndex = 0;
        _suppress = false;
        if (items.Count > 0) BindEditor(items[0].Entry);
        else ClearEditor();

        if (FilterCount != null)
        {
            FilterCount.Text = filter.Length > 0
                ? Loc.F("匹配 {0} / {1}", items.Count, total)
                : Loc.F("共 {0} 项", total);
        }

        ValueHelp.Text = _tab switch
        {
            "search" => Loc.T("用 {q} 代表关键词。可以填网址，例如 https://www.bing.com/search?q={q}；也可以填程序路径并在“参数”里写 -w {q}，直接调用欧路词典这类本地查词程序。"),
            "run" => Loc.T("填程序路径或命令，例如 C:\\Windows\\notepad.exe 或 notepad.exe；参数写在下方“参数”里，{q} 代表 Qbar 中输入的内容。"),
            "web" => Loc.T("填完整网址，例如 https://github.com。"),
            "pinned" => Loc.T("填文件、文件夹、网址或程序路径，例如 D:\\\\项目 或 C:\\\\Windows\\\\notepad.exe；输入名称就能在 Qbar 里直接打开。"),
            _ => "",
        };
        ValueLabel.Text = _tab switch
        {
            "search" => Loc.T("搜索链接"),
            "run" => Loc.T("程序路径"),
            "web" => Loc.T("网址"),
            "pinned" => Loc.T("目标路径 / 网址"),
            _ => Loc.T("内容"),
        };
        EntryHint.Text = _tab switch
        {
            "search" => Loc.T("Qbar 里输入「关键字 内容」即可用对应引擎搜索"),
            "run" => Loc.T("Qbar 里输入关键字回车即可启动"),
            "web" => Loc.T("Qbar 里输入关键字回车即可打开"),
            "pinned" => Loc.T("Qbar 空面板会优先列出固定项，也可以直接搜索名称"),
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
        ArgumentsBox.Text = entry.Arguments;
        AdminBox.IsChecked = entry.RunAsAdmin;
        ArgumentsRow.Visibility = _tab == "web" ? Visibility.Collapsed : Visibility.Visible;
        EditTitle.Text = string.IsNullOrEmpty(entry.Key) ? Loc.T("新条目") : Loc.F("正在编辑：{0}", entry.Key);
        _suppress = false;
        UpdateIconPreview(entry);
    }

    /// <summary>图标预览：有图标显示图标，没有就显示首字（和 Qbar 里的表现一致）。</summary>
    private void UpdateIconPreview(QEntry entry)
    {
        /* 先看内存，再看磁盘上的 favicon 缓存。
           只看 ForEntry 的话，网址永远拿不到图标（它的图标是下载的），
           每次点开条目都要先闪一下首字、等下载回来才换成真图标。 */
        var icon = IconCache.ForEntry(entry);
        if (icon == null) icon = IconCache.GetWebIcon(entry.Value);

        /* 真的没有缓存才去下载：这一条只会在第一次见到这个网址时走到。
         * 用户此时可能已经切到别的条目，所以回调里要再核对一次。 */
        if (icon == null && _tab == "web" && IsWebUrl(entry.Value))
        {
            IconCache.LoadWebIconAsync(entry.Value, image =>
            {
                if (image == null) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(_editing, entry)) return;
                    IconPreview.Source = image;
                    IconPreview.Visibility = Visibility.Visible;
                    IconFallback.Visibility = Visibility.Collapsed;
                });
            });
        }

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
            Title = Loc.T("选择图标（图片或程序）"),
            Filter = Loc.T("图标或程序|*.png;*.ico;*.jpg;*.jpeg;*.exe;*.dll|所有文件|*.*"),
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
        ArgumentsBox.Text = "";
        AdminBox.IsChecked = false;
        ArgumentsRow.Visibility = _tab == "web" ? Visibility.Collapsed : Visibility.Visible;
        EditTitle.Text = Loc.T("（选择左边的条目，或点新增）");
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
        var items = CurrentList().Select(x => new EntryVm(x, _tab != "run")).ToList();
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
            _host.Notify(Loc.T("默认搜索不能删除，只能修改"));
            return;
        }
        CurrentList().Remove(vm.Entry);
        _host.SaveSettings();
        LoadEntries();
        _host.Notify(Loc.T("已删除"));
    }

    private void OnFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;
        _editing.Key = KeyBox.Text.Trim();
        _editing.Value = ValueBox.Text.Trim();
        _editing.Comment = CommentBox.Text.Trim();
        _editing.IconPath = IconBox.Text.Trim();
        _editing.Arguments = ArgumentsBox.Text.Trim();
        _editing.RunAsAdmin = AdminBox.IsChecked == true;
        _host.SaveSettings();
        // 同步刷新左侧列表的显示（保持当前选择与编辑焦点）
        EntryList.Items.Refresh();
        UpdateIconPreview(_editing);

        /* 新建网址：用户往往只粘贴链接，不想再手动写标题。
         * 停手 700ms 后抓一次网页 <title>，只填空备注，绝不覆盖用户已写的备注。 */
        if (_tab == "web" && string.IsNullOrWhiteSpace(_editing.Comment))
        {
            _titleToken++;
            _titleTimer.Stop();
            _titleTimer.Start();
        }
    }

    private static bool IsWebUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private async Task FillWebTitleAsync()
    {
        if (_host == null || _tab != "web" || _editing == null) return;
        var entry = _editing;
        string url = entry.Value?.Trim() ?? "";
        if (!IsWebUrl(url) || !string.IsNullOrWhiteSpace(entry.Comment)) return;

        int token = ++_titleToken;
        string? title = null;
        try
        {
            string html = await TitleHttp.GetStringAsync(url).ConfigureAwait(false);
            var match = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success) title = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
        }
        catch { }

        if (string.IsNullOrWhiteSpace(title))
        {
            try { title = new Uri(url).Host; } catch { title = null; }
        }
        if (string.IsNullOrWhiteSpace(title)) return;

        await Dispatcher.InvokeAsync(() =>
        {
            // 用户可能已经切走，或自己补了备注；两种情况下都不覆盖。
            if (token != _titleToken || !ReferenceEquals(_editing, entry)
                || !string.IsNullOrWhiteSpace(entry.Comment)) return;
            entry.Comment = title!;
            _suppress = true;
            CommentBox.Text = title!;
            _suppress = false;
            _host.SaveSettings();
            EntryList.Items.Refresh();
        });
    }

    private void OnFlagsChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress || _editing == null) return;
        _editing.RunAsAdmin = AdminBox.IsChecked == true;
        _editing.Arguments = ArgumentsBox.Text.Trim();
        _host.SaveSettings();
    }

    private void OnAppearanceChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Qbar.ListCount = (int)ListCountSlider.Value;
        _host.Settings.Qbar.TextFontSize = FontSizeSlider.Value;
        _host.Settings.Qbar.ShowIcons = ShowIconsBox.IsChecked == true;
        _host.Settings.Qbar.SwitchToEnglishOnOpen = ImeOnOpenBox.IsChecked == true;
        _host.Settings.Qbar.SwitchBackOnEngine = ImeBackBox.IsChecked == true;
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
        FontPreview.MinHeight = Core.Text.TextMetrics.MeasureHeight(size, Loc.T("预览 Preview 文字测试 qgp"));
    }

    private void OnResetAppearance(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _host.Settings.Qbar = new QbarOptions();
        _host.SaveSettings();
        Refresh();
        _host.Notify(Loc.T("Qbar 外观已恢复默认"));
    }

    private void OnTryClick(object sender, RoutedEventArgs e) => _host?.ShowQbar();

    private sealed class EntryVm : System.ComponentModel.INotifyPropertyChanged
    {
        public EntryVm(QEntry entry, bool loadIcon)
        {
            Entry = entry;

            /* 网站/搜索引擎显示品牌图标；启动程序列表不预取图标——
             * 程序图标只在 Qbar 搜到 exe/lnk 时按需提取，
             * 避免打开设置页时逐个读 90 多个 exe 拖慢界面。 */
            if (loadIcon)
            {
                try
                {
                    Icon = IconCache.ForEntry(entry);

                    /* 网址条目：先同步问一次磁盘 favicon 缓存。
                     * 有就直接作为第一帧显示 —— 之前这里只有异步回调，
                     * 于是每次进「常用网址」都先画首字、再闪成真图标。 */
                    if (Icon == null) Icon = IconCache.GetWebIcon(entry.Value);
                }
                catch { Icon = null; }
            }
            else
            {
                /* 启动程序列表：先显示首字母，图标在后台解析完再替换。
                 * 这样既有“本地有图标就用图标”的效果，又不会卡住设置页。 */
                Task.Run(() =>
                {
                    ImageSource? image = null;
                    try { image = IconCache.ForEntry(entry); } catch { }
                    if (image == null) return;
                    Application.Current?.Dispatcher.BeginInvoke(() => ApplyIcon(image));
                });
            }

            LetterBrush = new SolidColorBrush(ColorFor(entry.Key));
            Initial = string.IsNullOrWhiteSpace(entry.Key) ? "?"
                : entry.Key[..1].ToUpperInvariant();

            if (Icon == null && (entry.Value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                 || entry.Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                IconCache.LoadWebIconAsync(entry.Value, image => Application.Current?.Dispatcher.BeginInvoke(() => ApplyIcon(image)));
            }
        }

        private void ApplyIcon(ImageSource? image)
        {
            if (image == null) return;
            Icon = image;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasIconVisibility)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(NoIconVisibility)));
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public QEntry Entry { get; }
        public string Key => Entry.Key;
        public string Value => Entry.Value;
        public string Comment => Entry.DisplayName;   // 默认引擎那条的备注要跟着语言走

        /// <summary>图标（可能为 null）。</summary>
        public System.Windows.Media.ImageSource? Icon { get; private set; }

        /// <summary>没有图标时显示的首字母。</summary>
        public string Initial { get; }

        /// <summary>首字母色块的背景色（按关键字取稳定颜色）。</summary>
        public System.Windows.Media.Brush LetterBrush { get; }

        /// <summary>有图标时显示图片；没有图标时隐藏，避免和首字母色块叠在一起。</summary>
        public System.Windows.Visibility HasIconVisibility =>
            Icon == null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

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
