using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Input;
using CapsLockPlus.UI.Windows;
using CapsLockPlus.Core.Settings;
using KeyBinding = CapsLockPlus.Core.Settings.KeyBinding;

namespace CapsLockPlus.UI.Views;

public partial class KeysView : UserControl
{
// 键盘绘制：键帽网格、触发键徽标、键名解析。partial 拆自 KeysView.xaml.cs。

    // ---------------- 键盘绘制 ----------------

    /// <summary>
    /// 在分类下拉里选中某个 Tag。
    ///
    /// 页面上有 5 个分类下拉（普通 / 窗口绑定 / 网页 / 直链 / 自定义），
    /// 它们永远显示同一个分类 —— 以前每个调用点都写一遍同样的 foreach，
    /// 改一处漏一处的风险很高，这里收成一个函数。
    /// </summary>
    private static void SelectTag(ComboBox box, string? tag, bool fallbackFirst = false)
    {
        foreach (ComboBoxItem it in box.Items)
        {
            if (it.Tag as string == tag) { box.SelectedItem = it; return; }
        }
        if (fallbackFirst && box.Items.Count > 0) box.SelectedIndex = 0;
    }

    /// <summary>当前层的触发键名（用于在键盘真实位置高亮它）。</summary>
    private string? CurrentTriggerKey =>
        _host?.Settings.Triggers.FirstOrDefault(t => t.LayerId == _layerId && t.Enabled)?.Key;

    private void BuildKeyboard()
    {
        if (_host == null) return;
        KeyboardPanel.Child = null;
        // 搜索框已移除：不再按关键字过滤，整把键盘始终完整显示
        string filter = "";
        int bound = 0;

        /* 触发键在键盘上的**真实位置**高亮。
         *
         * 之前写死显示在左上角（Caps 位）：把 F13 绑到第 3 层时
         * 切过去亮的还是 Caps 位，符号换了位置没换。
         * 现在：当前层的触发键是哪个键，就高亮键盘上那个键本身。 */
        string? triggerKey = CurrentTriggerKey;

        /* 整个键盘用**唯一一个 92 列 Grid**。
         *
         * 上一版每行各自 new Grid + 各自的 Star 宽度 —— 各行总权重不同，
         * 同一"1 单位"在每行的实际像素宽度都不一样，键全对不齐。
         * 现在所有行共用同一套 ColumnDefinition，键按绝对列号落位。 */
        var grid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 6),
            /* 显式给自然尺寸（不再是"铺满父容器"）。
             * 父级 Viewbox 就是拿这个尺寸当基准做等比例缩放：
             * 窗口宽 → 整把键盘按同一比例放大；窗口窄 → 按同一比例缩小。
             * 不给尺寸的话，Viewbox 会用无限宽度量星列，
             * 星列退化成 Auto，每行宽度全由文字撑出来，反而更乱。 */
            Width = NaturalWidth,
            Height = NaturalHeight,
        };
        for (int c = 0; c < TotalCols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        for (int r = 0; r < Layout.Length; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        /* 画普通键（单行）。
         *
         * 跨行键（小键盘 + 与回车）**不在 Layout 里出现**，只由 RowMerges 声明，
         * 所以这里不需要"跳过"逻辑 —— 上一版两处都写了，导致 + 被画两次。 */
        for (int r = 0; r < Layout.Length; r++)
        {
            foreach (var (label, keyId, col, span) in Layout[r])
            {
                if (keyId == null) continue;

                var cap = BuildKeycapEntry(label, keyId, filter, ref bound, triggerKey);
                if (cap == null) continue;
                Grid.SetRow(cap, r);
                Grid.SetColumn(cap, col);
                Grid.SetColumnSpan(cap, span);
                grid.Children.Add(cap);
            }
        }

        // 跨行大键（小键盘 + 与回车）：位置与标签都在 RowMerges 里
        foreach (var (row, keyId, rowSpan, col, colSpan, label) in RowMerges)
        {
            var cap = BuildKeycapEntry(label, keyId, filter, ref bound, triggerKey);
            if (cap == null) continue;

            /* 跨行键必须**拉伸填满**两行的高度。
             * 它的 Height 被 MakeKeycap 固定成 44/52，直接放进 RowSpan 会在
             * 两行之间留白，看起来"加号只有普通键那么大"、
             * 而不是标准 108 键那种高键。 */
            cap.Height = double.NaN;
            cap.VerticalAlignment = VerticalAlignment.Stretch;
            cap.Margin = new Thickness(1, 1, 1, 1);

            Grid.SetRow(cap, row);
            Grid.SetRowSpan(cap, rowSpan);
            Grid.SetColumn(cap, col);
            Grid.SetColumnSpan(cap, colSpan);
            grid.Children.Add(cap);
        }

        KeyboardPanel.Child = grid;

        // ---- 状态文字 ----
        var tap = FindInCurrentLayer("press_caps");
        string tapTitle = tap != null ? _host.Actions.Get(tap.Action)?.Title ?? tap.Action : Loc.T("无");
        var layerObj = _host.Settings.Layers.FirstOrDefault(l => l.Id == _layerId);
        string layerName = LayerName.Display(_layerId, layerObj?.Name);
        var trigger = _host.Settings.Triggers.FirstOrDefault(t => t.LayerId == _layerId);
        string triggerText = trigger != null
            ? Loc.F("触发键 {0}（键盘上已高亮）", KeyName.Display(trigger.Key))
            : Loc.T("（没有触发键指向这一层）");

        /* 修饰子层的提示必须能显示**组合**修饰键。
         *
         * 原来是个 switch，只认 Ctrl / Shift / Alt / Win 四个单值，
         * 录了 Ctrl+Shift 就落到 `_ => ""`，界面完全看不出"正在看一个修饰子层"，
         * 只看到"0 个绑定"—— 用户会以为绑定被删了。
         * 顺便把"怎么回到普通键"写在同一行。 */
        string modText = _layer != KeyModifiers.None
            ? ModifierText(_layer)
            : string.IsNullOrEmpty(_subLayerKey) ? "" : " + " + KeyName.Display(_subLayerKey);
        string backHint = modText.Length == 0
            ? ""
            : Loc.T("　·　点上方的「直接按」回到普通键");
        /* 顶部信息条只放一句短的，触发键和"怎么回到普通键"进 ToolTip。
         *
         * 以前整句（含触发键、回到上层的提示）挤在一行，窗口一窄就被截断，
         * 而且这条信息每切一次层都变长变短，旁边的「键盘大小」跟着左右跳。
         * 现在主句固定为"第几层 + 有没有子层 + 几个绑定"，长度稳定。 */
        LayerHint.Text = Loc.F("当前：第 {0} 层「{1}」{2}（{3} 个绑定）", _layerId, layerName, modText, bound);
        LayerHint.ToolTip = $"{triggerText}{backHint}";

        /* 顶部提示里的触发键必须**跟着当前层变**。
         *
         * 之前这里写死"按住 CapsLock"，即便第 2 层配的是 F13，
         * 切过去仍提示按住大写键 —— 与实际触发方式完全对不上。 */
        UpdateTriggerBadge(trigger);

        CountText.Text = _layerId == 1
            ? Loc.F("短按触发键：{0}　·　点键盘上任意键即可修改它的功能，修改立刻生效。", tapTitle)
            : Loc.T("这一层的按键绑定独立于其他层。要改短按行为，去「触发键与层」页。");
        UpdateLayerButtons();
    }

    /// <summary>
    /// 键帽上到底显示什么文字。
    ///
    /// 默认用 ShortTitle —— 长标题在 26px 宽的键帽里显示不全。
    /// 但有一批动作**自己就算好了键帽文字**，它们的 ShortTitle 只是分类名之类的
    /// 兜底，绝不能盖上去：
    ///   · 直达链接三类 / 输入文本 / 窗口绑定 → 各自的 LabelOf（域名 / 文件夹名 / 文字开头）；
    ///   · 自定义按键 → 备注 → 应用名 →「自定义」。
    ///
    /// ⚠ customKey 之前漏在这个名单外，键帽永远显示 ShortTitle「自定义」，
    /// 用户填的备注根本没机会显示 —— 这就是"备注保存了但键帽上没生效"的根因。
    /// 抽成纯函数是为了让自检能直接断言这个名单，不必去驱动界面。
    /// </summary>
    internal static string KeycapTitleOf(string actionId, string computedLabel, string? shortTitle)
    {
        if (string.IsNullOrWhiteSpace(shortTitle)) return computedLabel;
        if (actionId is "winBind" or "webPage" or "openFolder"
            or "openFile" or "typeText" or "customKey")
            return computedLabel;
        if (actionId.StartsWith("macro:", StringComparison.OrdinalIgnoreCase)) return computedLabel;
        return shortTitle!;
    }

    /// <summary>
    /// 建一个键帽；被搜索过滤掉时返回 null。
    /// </summary>
    private Border? BuildKeycapEntry(string label, string keyId, string filter,
        ref int bound, string? triggerKey)
    {
        string combo = ComboFor(keyId);
        var binding = FindInCurrentLayer(combo);

        /* 只有"动作真实存在"才算已绑定。
         *
         * 这一条很关键：老配置文件里可能残留已删除的动作
         * （如 winbindActivate1、旧版的覆盖动作），
         * 如果只判断 Action != "doNothing"，这些键会被显示成"已绑定"、
         * 键帽上还会冒出一个原始动作 ID，用户看到的就是"明明没绑却显示绑了"。
         *
         * 现在的判定：动作非空 + 不是 doNothing + **能在动作表里找到**。 */
        bool isBound = binding is { IsBound: true }
                       && _host?.Actions.Get(binding.Action) != null;
        if (isBound) bound++;

        string actionTitle = "";
        string actionCategory = "";
        if (isBound && binding != null)
        {
            // 统一走 DescribeActionOf，键帽与编辑面板里的说明文字保证一致
            actionTitle = DescribeActionOf(binding);
            // 分类标识：用户要求"单看按键就知道绑的功能属于哪个分类"
            actionCategory = _host?.Actions.Get(binding.Action)?.DisplayCategory ?? "";
        }

        string keycapTitle = isBound && binding != null
            ? KeycapTitleOf(binding.Action, actionTitle, _host?.Actions.Get(binding.Action)?.ShortTitle)
            : actionTitle;

        bool match = filter.Length == 0 ||
                     label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                     keyId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                     actionTitle.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                     actionCategory.Contains(filter, StringComparison.OrdinalIgnoreCase);
        if (!match && filter.Length > 0) return null;

        return MakeKeycap(label, keyId, isBound, keycapTitle, actionCategory, triggerKey);
    }

    /// <summary>
    /// 更新顶部「按住 XX + 下面的键」里的触发键名。
    ///
    /// 触发键可以是**复杂按键**（组合键），例如 Ctrl+Shift+K。
    /// 这种时候提示要写成「按住 Ctrl+Shift+K」，而不是只显示最后一个键。
    /// </summary>
    private void UpdateTriggerBadge(TriggerKey? trigger)
    {
        if (trigger == null)
        {
            TriggerKeyBadge.Text = Loc.T("没有触发键");
            TriggerKeySuffix.Text = Loc.T("这一层还没分配触发键，去「触发键与层」页设置");
            return;
        }

        string display = DescribeTriggerKey(trigger.Key);
        TriggerKeyBadge.Text = Loc.T("按住 ") + display;
        TriggerKeySuffix.Text = Loc.T("+ 下面的键");
    }

    /// <summary>
    /// 把触发键的键名转成人类可读的形式。
    ///
    /// 设置里存的是组合键 ID（如 <c>caps_ctrl_shift_k</c>），
    /// 直接显示会是一串看不懂的编码 —— 这里还原成 "Ctrl+Shift+K"。
    /// </summary>
    private static string DescribeTriggerKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Loc.T("（未设置）");

        // 单键（capslock / f13 / ralt…）直接查键名表
        var parsed = KeyName.ParseCombo(key);
        if (parsed != null)
        {
            var (mods, k) = parsed.Value;
            string name = KeyName.Display(k);
            return mods == KeyModifiers.None ? name : $"{ModifierText(mods)}+{name}";
        }

        // 不是组合键 ID，可能是纯键名
        int vk = KeyName.VkOf(key) ?? 0;
        return vk != 0 ? KeyName.Display(key) : key;
    }
}
