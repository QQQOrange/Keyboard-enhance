using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CapsLockPlus.Core.Input;

namespace CapsLockPlus.UI.Views;

/// <summary>
/// 「测试」页：把键盘钩子收到的真实按键原样显示出来。
///
/// 解决什么问题：用户反馈"按键没反应"时，过去只能靠猜 ——
/// 是钩子没装上？触发键没识别？层没切过去？还是动作本身失败？
/// 这一页把每一步都摆出来：按了什么键、当时在第几层、会执行哪个动作。
/// </summary>
public partial class KeyTestView : UserControl
{
    private AppHost? _host;
    private readonly ObservableCollection<EventRow> _rows = new();
    private bool _subscribed;

    private const int MaxRows = 80;

    public KeyTestView()
    {
        InitializeComponent();
        EventList.ItemsSource = _rows;

        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    public void Initialize(AppHost host)
    {
        _host = host;
        Subscribe();
        RefreshStatus();
    }

    public void Refresh() => RefreshStatus();

    private void Subscribe()
    {
        if (_host == null || _subscribed) return;
        _host.Engine.KeySeen += OnKeySeen;
        _host.Engine.TriggerHeldChanged += OnTriggerHeldChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (_host == null || !_subscribed) return;
        _host.Engine.KeySeen -= OnKeySeen;
        _host.Engine.TriggerHeldChanged -= OnTriggerHeldChanged;
        _subscribed = false;
    }

    // ---------------- 状态区 ----------------

    private void RefreshStatus()
    {
        if (_host == null) return;

        HookText.Text = _host.Engine.IsHookAlive ? "正常" : "未启动";
        HookText.Foreground = (System.Windows.Media.Brush)FindResource(
            _host.Engine.IsHookAlive ? "Brush.Success" : "Brush.Danger");

        TriggerText.Text = _host.Engine.IsTriggerHeld
            ? (KeyName.Display(_host.Engine.ActiveTriggerKey ?? "") is { Length: > 0 } t ? t : "按住中")
            : "未按住";
        TriggerText.Foreground = (System.Windows.Media.Brush)FindResource(
            _host.Engine.IsTriggerHeld ? "Brush.Accent" : "Brush.TextSecondary");

        int layer = _host.Engine.ActiveLayer;
        LayerText.Text = layer > 0 ? $"第 {layer} 层" : "未进层";
        LayerText.Foreground = (System.Windows.Media.Brush)FindResource(
            layer > 0 ? "Brush.Accent" : "Brush.TextSecondary");
    }

    private void OnTriggerHeldChanged(bool held)
    {
        Dispatcher.BeginInvoke((Action)(() =>
        {
            RefreshStatus();
            if (_host == null) return;

            _rows.Insert(0, new EventRow
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Key = held ? "▼ 触发键按下" : "▲ 触发键松开",
                Layer = held ? "进层中" : "回基础层",
                Action = held ? "等待长按 / 组合键" : "层已退出",
            });
            TrimRows();
        }));
    }

    // ---------------- 按键流水 ----------------

    private void OnKeySeen(int vk, bool down, int layer)
    {
        // 只记按下：抬起事件会让列表瞬间翻倍，看不过来
        if (!down) return;

        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (_host == null) return;

            string? name = KeyName.NameOf(vk);
            string display = name != null ? KeyName.Display(name) : $"VK 0x{vk:X2}";

            string layerText = layer > 0 ? $"第 {layer} 层" : "基础层";
            string action = name != null ? DescribeBinding(layer, name) : "（未识别的键）";

            _rows.Insert(0, new EventRow
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Key = display,
                Layer = layerText,
                Action = action,
            });
            TrimRows();

            LastKeyText.Text = display;
            LastKeyDetail.Text = $"{layerText}　·　{action}";
            RefreshStatus();
        }));
    }

    /// <summary>
    /// 这个键在指定层里会执行什么。
    ///
    /// 先找"无修饰键"的那条，再退而求其次找任意带修饰的 ——
    /// 测试页只拿到了 vk，拿不到当时按住了哪些修饰键，
    /// 所以做不到百分百精确；但足以回答"这个键到底有没有绑东西"。
    /// </summary>
    private string DescribeBinding(int layerId, string keyName)
    {
        if (_host == null) return "—";

        var keys = _host.Settings.Layers.FirstOrDefault(l => l.Id == layerId)?.Keys
                   ?? (layerId <= 1 ? _host.Settings.Keys : null);
        if (keys == null) return "这一层没有绑定表";

        Core.Settings.KeyBinding? fallback = null;
        foreach (var b in keys)
        {
            if (!b.IsBound) continue;
            var parsed = KeyName.ParseCombo(b.Combo);
            if (parsed == null) continue;
            if (!parsed.Value.Key.Equals(keyName, StringComparison.OrdinalIgnoreCase)) continue;

            if (parsed.Value.Mods == KeyModifiers.None) return TitleOf(b);
            fallback ??= b;
        }

        if (fallback != null) return TitleOf(fallback) + "（带修饰键）";
        return "（未绑定）";
    }

    private string TitleOf(Core.Settings.KeyBinding binding)
    {
        var info = _host?.Actions.Get(binding.Action);
        if (info == null) return binding.Action;

        // 备注/参数比动作名有用："输入文本"看不出输入的是什么
        if (!string.IsNullOrWhiteSpace(binding.Note)) return $"{info.Title}：{binding.Note}";
        if (binding.Action == "typeText" && !string.IsNullOrWhiteSpace(binding.Arg))
        {
            string preview = binding.Arg.Length > 10 ? binding.Arg[..10] + "…" : binding.Arg;
            return $"{info.Title}：{preview.Replace("\n", " ")}";
        }
        return info.Title;
    }

    private void TrimRows()
    {
        while (_rows.Count > MaxRows) _rows.RemoveAt(_rows.Count - 1);
    }

    // ---------------- 按钮 ----------------

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        LastKeyText.Text = "—";
        LastKeyDetail.Text = "按任意键开始测试";
        StatusText.Text = "已清空";
    }

    private void OnCopyDiag(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 按键测试诊断 ===");
            sb.AppendLine($"时间        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"键盘钩子    : {(_host?.Engine.IsHookAlive == true ? "正常" : "未启动")}");
            sb.AppendLine($"引擎启用    : {(_host?.Engine.Enabled == true ? "是" : "否（已暂停）")}");
            sb.AppendLine($"当前层      : {(_host?.Engine.ActiveLayer ?? -1)}");
            sb.AppendLine($"触发键      : {_host?.Engine.ActiveTriggerKey ?? "(无)"}");
            sb.AppendLine();

            var triggers = _host?.Settings.Triggers ?? new List<Core.Settings.TriggerKey>();
            sb.AppendLine($"已配置触发键（{triggers.Count} 个）：");
            foreach (var t in triggers)
                sb.AppendLine($"  · {KeyName.Display(t.Key)} → 第 {t.LayerId} 层，长按阈值 {t.HoldMs}ms");
            if (triggers.Count == 0) sb.AppendLine("  （没有 —— 按住任何键都不会进层）");
            sb.AppendLine();

            sb.AppendLine("最近按键：");
            foreach (var r in _rows.Take(30))
                sb.AppendLine($"  {r.Time}  {r.Key}\t{r.Layer}\t{r.Action}");

            Clipboard.SetText(sb.ToString());
            StatusText.Text = "诊断信息已复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    /// <summary>流水里的一行。</summary>
    private sealed class EventRow
    {
        public string Time { get; init; } = "";
        public string Key { get; init; } = "";
        public string Layer { get; init; } = "";
        public string Action { get; init; } = "";
    }
}
