using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.UI.Theme;

namespace CapsLockPlus.UI.Windows;

/// <summary>
/// 动作选择器：一级分类 → 二级分类 → 具体动作，顶部可全局搜索。
///
/// 动作目录有一百多项，原来的 ComboBox 只能靠滚轮翻，找一个「窗口置顶」
/// 要翻很久。这里把「找」拆成两步：先选大类，再选子类，最后只在几十项里挑；
/// 知道名字的人直接搜，搜索结果不再受分类限制。
/// </summary>
public partial class ActionPickerWindow : Window
{
    /// <summary>一级分类 → 它包含的动作分类（ActionInfo.Category 的原文）。</summary>
    private static readonly (string Name, string[] Categories)[] Groups =
    {
        ("文本与光标", new[] { "文本编辑", "光标移动", "输入文本" }),
        ("窗口与系统", new[] { "窗口功能", "窗口绑定", "系统功能", "系统", "智能直达" }),
        ("鼠标", new[] { "鼠标功能" }),
        ("剪贴板", new[] { "剪贴板" }),
        ("按键与宏", new[] { "基础", "自定义按键", "宏" }),
        ("链接与直达", new[] { "直达链接" }),
    };

    private readonly List<ActionInfo> _all;
    private readonly Dictionary<string, Button> _groupButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _subButtons = new(StringComparer.Ordinal);
    private string _group = "";
    private string _category = "";

    public ActionPickerWindow(IEnumerable<ActionInfo> actions, string? selectedId)
    {
        InitializeComponent();

        _all = actions
            .Where(a => !a.Hidden)
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.DisplayCategory, StringComparer.CurrentCulture)
            .ThenBy(a => a.Title, StringComparer.CurrentCulture)
            .ToList();

        SelectedId = selectedId ?? "";

        SourceInitialized += (_, _) =>
        {
            try
            {
                bool dark = ThemeManager.Current == ThemeManager.Mode.Dark
                            || (ThemeManager.Current == ThemeManager.Mode.System && ThemeManager.IsDarkSystem);
                WindowChrome.Apply(this, dark, WindowChrome.BackdropType.None);
            }
            catch
            {
                // 只是观感，失败不影响选择
            }
        };

        Loaded += (_, _) =>
        {
            /* 主窗口可以贴在屏幕右边缘，CenterOwner 会把对话框推到屏幕外。
             * 统一夹到主屏工作区，保证标题栏和“选择”按钮都看得见。 */
            var work = SystemParameters.WorkArea;
            Width = Math.Min(Width, Math.Max(MinWidth, work.Width - 24));
            Height = Math.Min(Height, Math.Max(MinHeight, work.Height - 24));
            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
            Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
            BuildGroupButtons();
            SearchBox.Focus();
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { DialogResult = false; e.Handled = true; }
        };
    }

    /// <summary>用户选中的动作 ID；空串表示「关闭双击」。</summary>
    public string SelectedId { get; private set; }

    private void BuildGroupButtons()
    {
        GroupPanel.Children.Clear();
        _groupButtons.Clear();

        foreach (var (name, cats) in Groups)
        {
            if (!_all.Any(a => cats.Contains(a.Category, StringComparer.OrdinalIgnoreCase))) continue;
            AddChip(GroupPanel, name, () => SelectGroup(name), _groupButtons);
        }

        // 没被上面任何一组收进去的分类，统一进「其他」，避免动作“消失”。
        var known = Groups.SelectMany(g => g.Categories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_all.Any(a => !known.Contains(a.Category)))
            AddChip(GroupPanel, "其他", () => SelectGroup("其他"), _groupButtons);
    }

    private void SelectGroup(string name)
    {
        _group = name;
        _category = "";
        foreach (var (key, button) in _groupButtons)
            SetChipState(button, key == name);

        SubPanel.Children.Clear();
        _subButtons.Clear();

        var cats = CategoriesOf(name);
        foreach (var cat in cats)
        {
            int count = _all.Count(a => string.Equals(a.Category, cat, StringComparison.OrdinalIgnoreCase));
            if (count == 0) continue;
            string label = $"{Loc.T(cat)} ({count})";
            string captured = cat;
            var button = AddChip(SubPanel, label, () => SelectCategory(captured), _subButtons, cat);
            if (cat == _category) SetChipState(button, true);
        }

        ActionList.ItemsSource = null;
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private IEnumerable<string> CategoriesOf(string group)
    {
        if (group == "其他")
        {
            var known = Groups.SelectMany(g => g.Categories).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _all.Select(a => a.Category).Where(c => !known.Contains(c)).Distinct();
        }

        var def = Groups.FirstOrDefault(g => g.Name == group);
        return def.Categories ?? Array.Empty<string>();
    }

    private void SelectCategory(string category)
    {
        _category = category;
        foreach (var (key, button) in _subButtons)
            SetChipState(button, string.Equals(key, category, StringComparison.OrdinalIgnoreCase));

        ShowActions(_all.Where(a => string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase)));
        SearchBox.Focus();
    }

    private void ShowActions(IEnumerable<ActionInfo> actions)
    {
        var list = actions.ToList();
        ActionList.ItemsSource = list;
        if (list.Count == 0) return;

        var current = list.FirstOrDefault(a => string.Equals(a.Id, SelectedId, StringComparison.OrdinalIgnoreCase));
        ActionList.SelectedItem = current ?? list[0];
        ActionList.ScrollIntoView(ActionList.SelectedItem);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        string text = SearchBox.Text.Trim();
        if (text.Length == 0)
        {
            if (_category.Length > 0)
                ShowActions(_all.Where(a => string.Equals(a.Category, _category, StringComparison.OrdinalIgnoreCase)));
            else
                ActionList.ItemsSource = null;
            return;
        }

        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = _all.Where(a => words.All(w =>
            a.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
            a.Description.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
            a.DisplayCategory.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
            a.Id.Contains(w, StringComparison.OrdinalIgnoreCase)));

        ShowActions(hits);
    }

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ActionList.SelectedItem is ActionInfo) Accept();
    }

    private void OnOk(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (ActionList.SelectedItem is not ActionInfo info) return;
        SelectedId = info.Id;
        DialogResult = true;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        SelectedId = "";
        DialogResult = true;
    }

    private static Button AddChip(Panel panel, string label, Action action,
        Dictionary<string, Button> map, string? key = null)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 8),
            Style = (Style)Application.Current.FindResource("Button.Base"),
        };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
        map[key ?? label] = button;
        return button;
    }

    private static void SetChipState(Button button, bool active)
    {
        button.Background = active
            ? (Brush)Application.Current.FindResource("Brush.Accent")
            : (Brush)Application.Current.FindResource("Brush.SurfaceAlt");
        button.Foreground = active
            ? (Brush)Application.Current.FindResource("Brush.Surface")
            : (Brush)Application.Current.FindResource("Brush.TextPrimary");
    }
}
