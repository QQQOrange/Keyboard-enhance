using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 层内子层编辑器。
///
/// 每一行是一个子层：
///   · 左边的按钮点一下可以**捕获任意键**（Ctrl、Tab、CapsLock、F13…）
///   · 中间的输入框可以给这个子层起**自定义名字**
///   · 右边可以删除
///
/// 每层最多 10 个子层。
/// </summary>
public partial class SubLayerEditorWindow : Window
{
    /// <summary>一行的视图模型。</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private string _key = "";
        private string? _name;

        public string Key
        {
            get => _key;
            set
            {
                _key = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(KeyLabel));
            }
        }

        public string? Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        /// <summary>按钮上显示的文字（键名或"点击设置"）。</summary>
        public string KeyLabel =>
            string.IsNullOrWhiteSpace(Key) ? "点击设置…" : KeyName.Display(Key);

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly ObservableCollection<Row> _rows = new();

    public SubLayerEditorWindow() : this("", new List<SubLayerDef>()) { }

    public SubLayerEditorWindow(string layerName, List<SubLayerDef> current)
    {
        InitializeComponent();

        TitleText.Text = string.IsNullOrWhiteSpace(layerName)
            ? "层内子层设置"
            : $"「{layerName}」的层内子层";

        foreach (var s in current)
            _rows.Add(new Row { Key = s.Key, Name = s.Name });

        if (_rows.Count == 0)
            foreach (var s in LayerOptions.DefaultSubLayers())
                _rows.Add(new Row { Key = s.Key, Name = s.Name });

        SubList.ItemsSource = _rows;
        UpdateCount();

        Loaded += (_, _) => FitToWorkArea();
    }

    /// <summary>
    /// 把窗口压进当前屏幕的工作区。
    ///
    /// 写死 Height=620 在小屏笔记本上会出问题：1366×768 开 125% 缩放时
    /// 工作区只有约 600 DIP，620 高的窗口会**超出屏幕下沿**，
    /// 底部的按钮和最后一行被切掉 —— 看起来就像"下半部分没显示全"。
    /// 这里按工作区把高度收一收（列表本身可滚动，收小不丢内容）。
    /// </summary>
    private void FitToWorkArea()
    {
        try
        {
            var wa = SystemParameters.WorkArea;

            // 留出一点边距，别贴着任务栏和屏幕边缘
            double maxH = Math.Max(MinHeight, wa.Height - 60);
            double maxW = Math.Max(MinWidth, wa.Width - 60);

            MaxHeight = maxH;
            MaxWidth = maxW;

            if (Height > maxH) Height = maxH;
            if (Width > maxW) Width = maxW;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("子层窗口适配屏幕失败：" + ex.Message);
        }
    }

    private void UpdateCount()
    {
        int max = LayerOptions.MaxSubLayers;
        CountText.Text = $"{_rows.Count} / {max} 个子层";
        CountText.Foreground = _rows.Count > max
            ? (System.Windows.Media.Brush)FindResource("Brush.Accent")
            : (System.Windows.Media.Brush)FindResource("Brush.TextTertiary");
    }

    /// <summary>用户确认后的子层定义。</summary>
    public List<SubLayerDef> SelectedSubLayers =>
        _rows.Where(r => !string.IsNullOrWhiteSpace(r.Key))
             .Select(r => new SubLayerDef { Key = r.Key, Name = r.Name })
             .Take(LayerOptions.MaxSubLayers)
             .ToList();

    /// <summary>点"键"按钮：打开按键捕获，支持任意键。</summary>
    private void OnPickKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Row row }) return;

        var capture = new KeyCaptureWindow
        {
            Owner = this,
            Title = "按下要作为子层触发键的按键",
        };
        if (capture.ShowDialog() != true) return;

        string combo = capture.CapturedCombo;
        if (string.IsNullOrWhiteSpace(combo)) return;

        // 捕获到的是 combo ID（caps_ctrl_k），取其中的键名部分
        var parsed = KeyName.ParseCombo(combo);
        if (parsed is { } p)
        {
            var mods = p.Mods;
            var parts = new List<string>();
            if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("ctrl");
            if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("shift");
            if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("alt");
            if (mods.HasFlag(KeyModifiers.Win)) parts.Add("win");
            parts.Add(p.Key);
            row.Key = string.Join("_", parts);
        }
        else
        {
            row.Key = combo;
        }
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (_rows.Count >= LayerOptions.MaxSubLayers)
        {
            MessageBox.Show(this, $"最多 {LayerOptions.MaxSubLayers} 个子层。",
                "已达到上限", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _rows.Add(new Row());
        UpdateCount();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Row row }) _rows.Remove(row);
        UpdateCount();
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        foreach (var s in LayerOptions.DefaultSubLayers())
            _rows.Add(new Row { Key = s.Key, Name = s.Name });
        UpdateCount();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_rows.Count > LayerOptions.MaxSubLayers)
        {
            MessageBox.Show(this,
                $"最多 {LayerOptions.MaxSubLayers} 个子层，请先删掉一些。",
                "子层太多", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// 自检用：量一行输入框的**内容区高度**和一行字的高度。
    ///
    /// 为什么要测：输入框写死 Height 时，内容区 = 高度 − 上下 Padding − 上下边框。
    /// 字号稍大或 DPI 不是 100% 时，内容区会小于一行字，
    /// 表现为"只看到字符的上半部分"—— 而界面上看起来只是"字有点贴边"，
    /// 很容易被当成字体问题放过去。
    /// </summary>
    internal (double Box, double Content, double Text) MeasureRowTextBox()
    {
        UpdateLayout();

        var tb = FindFirstTextBox(this);
        if (tb == null) return (0, 0, 0);

        double content = tb.ActualHeight
                         - tb.Padding.Top - tb.Padding.Bottom
                         - tb.BorderThickness.Top - tb.BorderThickness.Bottom;

        var typeface = new System.Windows.Media.Typeface(
            tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        var ft = new System.Windows.Media.FormattedText(
            "汉字Agqy", System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight, typeface, tb.FontSize,
            System.Windows.Media.Brushes.Black,
            System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip);

        return (tb.ActualHeight, content, ft.Height);
    }

    private static System.Windows.Controls.TextBox? FindFirstTextBox(DependencyObject root)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.TextBox hit) return hit;
            if (FindFirstTextBox(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>
    /// 自检用：把窗口里**所有**显示文字的控件量一遍，列出"需要的高度 &gt; 实际高度"
    /// 的那些 —— 也就是字被裁掉一半的地方。
    ///
    /// 只量输入框不够：用户说"字符只显示上半部分"时，出问题的可能是按钮、
    /// 标签，也可能是整行被容器压扁。宁可全量扫一遍，让数据指出位置。
    /// </summary>
    internal string AuditTextClipping()
    {
        UpdateLayout();

        var bad = new List<string>();
        Walk(this);

        return bad.Count == 0 ? "" : string.Join(" / ", bad);

        void Walk(DependencyObject root)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is System.Windows.FrameworkElement fe and not System.Windows.Controls.Primitives.ScrollBar)
                    Inspect(fe);
                Walk(child);
            }
        }

        void Inspect(System.Windows.FrameworkElement fe)
        {
            if (!fe.IsVisible || fe.ActualHeight <= 0) return;

            string text = fe switch
            {
                System.Windows.Controls.TextBlock t => t.Text,
                System.Windows.Controls.TextBox t => t.Text,
                System.Windows.Controls.Button b => b.Content as string ?? "",
                _ => "",
            };
            if (string.IsNullOrWhiteSpace(text)) return;

            double padV = fe switch
            {
                System.Windows.Controls.Control c => c.Padding.Top + c.Padding.Bottom
                                                    + c.BorderThickness.Top + c.BorderThickness.Bottom,
                _ => 0,
            };

            /* FontFamily 这类属性在 TextBlock 和 Control 上是各自声明的，
             * FrameworkElement 上并没有 —— 所以要按具体类型取。 */
            System.Windows.Media.FontFamily family;
            System.Windows.FontStyle style;
            System.Windows.FontWeight weight;
            System.Windows.FontStretch stretch;
            double size;

            switch (fe)
            {
                case System.Windows.Controls.TextBlock tb:
                    (family, style, weight, stretch, size) =
                        (tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch, tb.FontSize);
                    break;
                case System.Windows.Controls.Control c:
                    (family, style, weight, stretch, size) =
                        (c.FontFamily, c.FontStyle, c.FontWeight, c.FontStretch, c.FontSize);
                    break;
                default:
                    return;
            }

            var typeface = new System.Windows.Media.Typeface(family, style, weight, stretch);
            var ft = new System.Windows.Media.FormattedText(
                text, System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight, typeface, size,
                System.Windows.Media.Brushes.Black,
                System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip);

            double need = ft.Height + padV;
            if (need > fe.ActualHeight + 0.5)
                bad.Add($"{fe.GetType().Name}「{text}」需 {need:F1} 实 {fe.ActualHeight:F1}");
        }
    }
}
