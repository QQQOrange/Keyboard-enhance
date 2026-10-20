using System.Windows;
using System.Windows.Controls;
using CapsLockPlus.Core.Input;

namespace CapsLockPlus.UI.Views;

public partial class HelpView : UserControl
{
    private AppHost? _host;

    public HelpView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        Refresh();
    }

    public void Refresh()
    {
        if (_host == null) return;
        Build(FilterBox.Text?.Trim() ?? "");
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => Build(FilterBox.Text?.Trim() ?? "");

    private void Build(string filter)
    {
        if (_host == null) return;
        var groups = new List<HelpGroup>();
        var byCategory = new Dictionary<string, List<HelpItem>>();

        // 遍历**所有层**的绑定，而不只是第 1 层。
        // 之前只读 Settings.Keys（即第 1 层），第 2~10 层绑的键在速查表里
        // 完全看不到 —— 用户以为"绑了没用"，其实是速查表漏了。
        foreach (var layer in _host.Settings.Layers.OrderBy(l => l.Id))
        {
            var keys = layer.Keys.Count > 0
                ? layer.Keys
                : (layer.Id == 1 ? _host.Settings.Keys : layer.Keys);

            // 这一层由哪个触发键进入（用于显示前缀）
            var trigger = _host.Settings.Triggers.FirstOrDefault(t => t.LayerId == layer.Id);
            string layerPrefix = layer.Id == 1
                ? "Caps"
                : trigger != null
                    ? $"[第{layer.Id}层] {KeyName.Display(trigger.Key)}"
                    : $"[第{layer.Id}层]";

            foreach (var binding in keys.Where(k => k.IsBound))
            {
                var parsed = KeyName.ParseCombo(binding.Combo);
                if (parsed == null) continue;
                var (mods, key) = parsed.Value;
                if (key == "capslock") continue;

                string modPart = mods switch
                {
                    KeyModifiers.None => "",
                    _ => "+" + string.Join("+", new[]
                    {
                        mods.HasFlag(KeyModifiers.Ctrl) ? "Ctrl" : null,
                        mods.HasFlag(KeyModifiers.Shift) ? "Shift" : null,
                        mods.HasFlag(KeyModifiers.Alt) ? "LAlt" : null,
                        mods.HasFlag(KeyModifiers.Win) ? "Win" : null,
                    }.Where(s => s != null)),
                };
                string combo = $"{layerPrefix}{modPart}+{KeyName.Display(key)}";

                var info = _host.Actions.Get(binding.Action);
                if (info == null) continue;
                string desc = binding.Arg.Length > 0
                    ? $"{info.Description}（参数 {binding.Arg}）"
                    : info.Description;
                if (filter.Length > 0 &&
                    !combo.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !info.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !desc.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!byCategory.TryGetValue(info.Category, out var list))
                {
                    list = new List<HelpItem>();
                    byCategory[info.Category] = list;
                }
                list.Add(new HelpItem(combo, info.Title, desc));
            }
        }

        // 内置的固定说明
        byCategory.TryAdd("面板", new List<HelpItem>());
        var panel = byCategory["面板"];
        if (filter.Length == 0)
        {
            panel.Add(new HelpItem("短按 Caps", "切换大小写", "不按其他键时松开 Caps 的默认行为"));
            panel.Add(new HelpItem("双击 Caps", "打开 Qbar", $"{Core.Input.HotkeyEngine.DoubleTapWindowMs} 毫秒内连按两下"));
            panel.Add(new HelpItem("Qbar 内", "↑ ↓ 选择", "Enter 执行，Esc 关闭，Tab 补全"));
            panel.Add(new HelpItem("Qbar 内", "=1+2*3", "直接计算并把结果复制到剪贴板"));
        }

        string[] order = { "光标移动", "选择", "编辑", "剪贴板", "面板", "窗口", "窗口绑定", "鼠标", "系统", "基础" };
        foreach (var cat in order)
        {
            if (!byCategory.TryGetValue(cat, out var items) || items.Count == 0) continue;
            groups.Add(new HelpGroup(cat, items.Count.ToString(), items));
        }
        foreach (var kv in byCategory)
        {
            if (order.Contains(kv.Key) || kv.Value.Count == 0) continue;
            groups.Add(new HelpGroup(kv.Key, kv.Value.Count.ToString(), kv.Value));
        }

        // 每组内部按“基础层 → LAlt 层 → Win 层”再按键盘顺序排列，查找更快
        foreach (var g in groups)
        {
            g.Items.Sort((a, b) =>
            {
                int la = LayerRank(a.Combo), lb = LayerRank(b.Combo);
                if (la != lb) return la.CompareTo(lb);
                return KeyRank(a.Combo).CompareTo(KeyRank(b.Combo));
            });
        }

        Groups.ItemsSource = groups;
    }

    private static int LayerRank(string combo) =>
        combo.Contains("LAlt", StringComparison.Ordinal) ? 1 :
        combo.Contains("Win", StringComparison.Ordinal) ? 2 : 0;

    private static int KeyRank(string combo)
    {
        int idx = combo.LastIndexOf('+');
        string key = idx >= 0 ? combo[(idx + 1)..] : combo;
        int rank = Array.IndexOf(KeyOrder, key);
        return rank < 0 ? 999 : rank;
    }

    /// <summary>按键盘上从左到右、从上到下的顺序（帮助页读起来更自然）。</summary>
    private static readonly string[] KeyOrder =
    {
        "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P",
        "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'",
        "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/",
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "0",
        "`", "-", "=", "[", "]", "\\",
        "Backspace", "Tab", "Enter", "Space", "RAlt",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "PgUp", "PgDn", "Home", "End", "Up", "Down", "Left", "Right",
    };

    private sealed record HelpItem(string Combo, string Title, string Desc);

    private sealed class HelpGroup
    {
        public HelpGroup(string title, string count, List<HelpItem> items)
        {
            Title = title;
            Count = count;
            Items = items;
        }

        public string Title { get; }
        public string Count { get; }
        public List<HelpItem> Items { get; }
    }
}
