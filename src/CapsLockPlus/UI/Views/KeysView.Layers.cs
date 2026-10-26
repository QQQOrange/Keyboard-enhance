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
// 层与子层：层下拉、子层按钮、选中键、动作参数面板。

    private void BuildLayerBox()
    {
        if (_host == null) return;
        _suppressEvents = true;
        LayerBox.Items.Clear();
        foreach (var layer in _host.Settings.Layers.OrderBy(l => l.Id))
        {
            var trigger = _host.Settings.Triggers.FirstOrDefault(t => t.LayerId == layer.Id);
            string suffix = trigger != null
                ? Loc.F("　← 长按 {0}", KeyName.Display(trigger.Key))
                : Loc.T("　（未分配触发键）");
            LayerBox.Items.Add(new ComboBoxItem
            {
                /* 序号 + 层名 + 触发键提示：序号是纯数字，层名走 LayerName（默认名跟着语言，
                 * 用户改过的名字原样），提示已经是译好的整句。 */
                Content = $"{layer.Id}. {LayerName.Display(layer.Id, layer.Name)}{suffix}",
                Tag = layer.Id,
            });
            if (layer.Id == _layerId) LayerBox.SelectedIndex = LayerBox.Items.Count - 1;
        }
        if (LayerBox.SelectedIndex < 0 && LayerBox.Items.Count > 0) LayerBox.SelectedIndex = 0;
        _suppressEvents = false;
    }

    private void OnLayerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppressEvents) return;
        if (LayerBox.SelectedItem is not ComboBoxItem { Tag: int id }) return;
        _layerId = id;

        /* 切层必须把"层内修饰键"复位。
         *
         * 不复位的话：在基础层点了「自定义按键…」录了个 Ctrl+Shift+K，
         * `_layer` 就停在 Ctrl|Shift；接着切到第 2、3 层，
         * 键盘上每个键算出来的 combo 都是 `ctrl_shift_xx`，自然一个都查不到，
         * 界面显示"0 个绑定"—— 用户看到的就是"所有层的东西都被清空了"。
         * 实际上绑定都在，只是视图卡在一个空的修饰子层上。 */
        _layer = KeyModifiers.None;
        _subLayerKey = "";
        SelectKey(_selectedKey);
    }

    /// <summary>
    /// 建一个键帽。
    /// </summary>
    /// <param name="triggerKey">当前层的触发键名（非 null 时该键会额外高亮 +
    /// 加"触发键"角标，用户一眼看到"我按哪个键进这一层"）。</param>
    private Border MakeKeycap(string label, string keyId, bool isBound,
        string actionTitle, string actionCategory, string? triggerKey)
    {
        // 触发键在键盘**真实位置**高亮（不再写死在左上角）
        bool isTrigger = triggerKey != null
                         && string.Equals(triggerKey, keyId, StringComparison.OrdinalIgnoreCase);
        bool isSelected = string.Equals(keyId, _selectedKey, StringComparison.OrdinalIgnoreCase);

        /* 三种视觉状态，一眼可分但**对比不过分**：
         *
         *   触发键（本层）  → 深蓝实心 + 白字 + ★
         *   已绑定功能      → 深蓝实心 + 白字
         *   未绑定          → 浅蓝底 + 灰蓝字
         *
         * 未绑定用浅蓝而不是纯白：纯白键帽和深蓝键帽并排时对比太硬，
         * 满屏键帽看着刺眼。浅蓝能"退到背景里"，深蓝才自然跳出来。 */
        Brush background, borderBrush, textBrush, subTextBrush;
        if (isTrigger || isBound)
        {
            background = (Brush)FindResource("Brush.Accent");
            borderBrush = (Brush)FindResource("Brush.AccentHover");
            textBrush = (Brush)FindResource("Brush.Surface");         // 白字
            subTextBrush = (Brush)FindResource("Brush.Surface");
        }
        else
        {
            background = (Brush)FindResource("Brush.AccentSoft");      // 浅蓝（柔和）
            borderBrush = (Brush)FindResource("Brush.AccentSoftBorder");
            textBrush = (Brush)FindResource("Brush.TextSecondary");
            subTextBrush = (Brush)FindResource("Brush.TextTertiary");
        }

        var border = new Border
        {
            /* **所有键帽等高**，间距才一致。
             *
             * 之前已绑定的键 52px、未绑定的 44px：同一行里只要有一个已绑定的键，
             * 行高就被撑到 52，行内未绑定的键顶部/底部留出 8px 空隙 ——
             * 看起来就是"有的键间距大、有的小"。
             *
             * 现在统一 52px：即使这一行全是未绑定键，多出来的空间也被
             * VerticalAlignment=Center 均分到上下，视觉间距仍然均匀。
             * 高度一致也让整个键盘的行距天然对齐。 */
            Height = KeycapHeight,
            Margin = new Thickness(KeycapGap / 2),
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Background = background,
            BorderBrush = borderBrush,
            ToolTip = isTrigger
                ? Loc.F("★ {0} = 本层触发键\n长按它进入这一层；短按执行短按动作", label)
                : isBound ? $"{label} → {actionTitle}" : Loc.F("{0}（未绑定）", label),
        };

        /* 当前选中的键用更粗的描边圈出来。
         * 已绑定的键本身就是深蓝底，再用蓝描边会看不见 ——
         * 所以选中态改用白色描边（在深蓝底上对比明显）。 */
        if (isSelected)
        {
            border.BorderThickness = new Thickness(2);
            border.BorderBrush = isBound || isTrigger
                ? (Brush)FindResource("Brush.Surface")
                : (Brush)FindResource("Brush.Accent");
        }

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 1, 0),
        };

        if (isBound)
        {
            /* ===== 已绑定：功能名当主角，键名退成小字 =====
             *
             * 用户要求：「原来的功能要缩小，而它附上的功能就要把字体
             * 显示尽可能的全，字体的大小也要比按键本身功能要大一点，
             * 占的位置也要比现在的要大」。
             *
             * 所以顺序反过来：先功能名（大字、占两行），再键名（小字）。
             * 键帽加高到 52px 才装得下。 */

            // ① 分类标识（小字，最上面，告诉用户"这是哪一类功能"）
            stack.Children.Add(new TextBlock
            {
                Text = Shorten(actionCategory, 4),
                FontSize = 6.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = subTextBrush,
                Opacity = 0.62,
                Margin = new Thickness(0, 0, 0, 1),
            });

            // ② 功能名：允许折两行，尽量显示全名
            stack.Children.Add(new TextBlock
            {
                Text = actionTitle,
                FontSize = 8.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 26,                    // 最多两行
                LineHeight = 9.5,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = textBrush,
            });

            // ③ 键名：缩小放在最下面
            stack.Children.Add(new TextBlock
            {
                Text = isTrigger ? "★" + label : label,
                FontSize = 6.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = subTextBrush,
                Opacity = 0.7,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }
        else
        {
            /* ===== 未绑定：只显示键名，居中大字 ===== */
            stack.Children.Add(new TextBlock
            {
                Text = isTrigger ? "★" + label : label,
                FontSize = label.Length switch
                {
                    <= 1 => 12,
                    2 => 10.5,
                    3 => 9,
                    _ => 8,
                },
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = textBrush,
            });
        }
        border.Child = stack;

        string captured = keyId;
        border.MouseLeftButtonUp += (_, _) => SelectKey(captured);
        border.MouseEnter += (_, _) => border.Opacity = 0.82;
        border.MouseLeave += (_, _) => border.Opacity = 1.0;
        return border;
    }

    /// <summary>
    /// 宽度足够放下第二行文字的键（≥ 6 列）。
    /// 这些键上多显示一个功能名不会显得挤。
    /// </summary>
    private static bool IsWideKey(string keyId) => keyId is
        "space" or "enter" or "backspace" or "tab"
        or "lshift" or "rshift" or "lctrl" or "rctrl"
        or "capslock";

    /// <summary>截断到指定字数（超长部分直接省掉，键帽上放不下省略号）。</summary>
    private static string Shorten(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0) return "";
        text = text.Trim();
        return text.Length <= max ? text : text[..max];
    }

    /// <summary>
    /// 重建"层内修饰键"按钮组。
    ///
    /// 每层可以配 0~10 个修饰键组合，所以按钮是**动态生成**的：
    /// 第一个永远是"直接按"（无修饰键），后面按配置逐个加。
    ///
    /// 性能与内存：只在内容真的变化时重建。这个方法是每次 BuildKeyboard
    /// 都会调的（层切换、按键编辑都触发），无脑重建会反复分配 Button 对象
    /// 并悬挂事件处理器。这里用一个"结构指纹"判断是否需要重建，
    /// 只需要更新选中态时走轻量路径。
    /// </summary>
    private void UpdateLayerButtons()
    {
        if (_host == null) return;

        var layer = _host.Settings.Layers.FirstOrDefault(l => l.Id == _layerId);
        var subs = layer?.EffectiveSubLayers ?? LayerOptions.DefaultSubLayers();

        // 结构指纹：层 ID + 每个子层的键与名字。相同则只刷新选中态。
        string signature = _layerId + "|" +
            string.Join(",", subs.Select(s => s.Key + ":" + s.Display));
        if (signature == _subLayerSignature && SubLayerPanel.Children.Count > 0)
        {
            RefreshSubLayerSelection();
            return;
        }
        _subLayerSignature = signature;

        SubLayerPanel.Children.Clear();

        void AddButton(string text, string subKey, string tip)
        {
            var b = new Button
            {
                Content = text,
                Style = (Style)FindResource("Button.Base"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 5, 0),
                ToolTip = tip,
                // Tag 记住这个按钮对应的子层键名，选中态刷新时读它
                Tag = subKey ?? "",
            };
            b.Click += OnSubLayerButtonClick;
            SubLayerPanel.Children.Add(b);
        }

        // 第一项：直接按（无子层），始终存在
        AddButton(Loc.T("直接按"), "", Loc.T("进入层后直接按的键"));

        /* 后面按该层配置的子层逐个生成。
         *
         * 子层键可以是任意物理键（ctrl / tab / capslock…），
         * 也可以起自定义名字 —— 按钮上优先显示名字。 */
        foreach (var sub in subs)
        {
            if (string.IsNullOrWhiteSpace(sub.Key)) continue;
            AddButton("+ " + sub.Display, sub.Key,
                Loc.F("进入层后再按住 {0} 按的键", sub.Display));
        }

        RefreshSubLayerSelection();
    }

    /// <summary>只刷新子层按钮的选中态（不重建控件，开销极小）。</summary>
    private void RefreshSubLayerSelection()
    {
        foreach (var child in SubLayerPanel.Children)
        {
            if (child is not Button b || b.Tag is not string key) continue;
            bool active = string.Equals(_subLayerKey, key, StringComparison.OrdinalIgnoreCase);

            b.Background = (Brush)FindResource(active ? "Brush.AccentSoft" : "Brush.Surface");
            b.BorderBrush = (Brush)FindResource(active ? "Brush.AccentSoftBorder" : "Brush.BorderStrong");
            b.Foreground = (Brush)FindResource(active ? "Brush.Accent" : "Brush.TextSecondary");
            b.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>
    /// 子层按钮点击。
    ///
    /// 用共享处理器 + Tag 取修饰键，而不是每个按钮一个闭包 ——
    /// 闭包会捕获 this 并在按钮被移除后仍然存活一段时间，属于隐性泄漏。
    /// </summary>
    private void OnSubLayerButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key }) SelectSubLayer(key);
    }

    /// <summary>切到指定子层（键名，空字符串 = 直接按）。</summary>
    private void SelectSubLayer(string subKey)
    {
        _subLayerKey = subKey ?? "";
        _layer = KeyModifiersOf(subKey);
        SelectKey(_selectedKey);
    }

    /// <summary>
    /// 子层键名 → 修饰位标记。
    ///
    /// ctrl/shift/alt/win 这类修饰键返回对应位；其它键（tab、capslock）
    /// 返回 None —— 它们的 combo 用不带修饰前缀的形式。
    /// </summary>
    private static KeyModifiers KeyModifiersOf(string? subKey)
    {
        if (string.IsNullOrWhiteSpace(subKey)) return KeyModifiers.None;
        var m = KeyModifiers.None;
        foreach (string part in subKey.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            m |= part.ToLowerInvariant() switch
            {
                "ctrl" => KeyModifiers.Ctrl,
                "shift" => KeyModifiers.Shift,
                "alt" => KeyModifiers.Alt,
                "win" => KeyModifiers.Win,
                _ => KeyModifiers.None,
            };
        }
        return m;
    }

    /// <summary>修饰位标记 → 子层键名（从旧接口切换时用）。</summary>
    private static string LayerKeyOf(KeyModifiers mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("ctrl");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("shift");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("alt");
        if (mods.HasFlag(KeyModifiers.Win)) parts.Add("win");
        return string.Join("_", parts);
    }

    private string? _subLayerSignature;

    /// <summary>配置当前层的子层（最多 10 个，可用任意键 + 自定义名字）。</summary>
    private void OnEditSubLayers(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var layer = _host.Settings.Layers.FirstOrDefault(l => l.Id == _layerId);
        if (layer == null) return;

        // 传当前子层定义（深拷贝，取消时不污染原配置）
        var current = layer.EffectiveSubLayers
            .Select(s => new SubLayerDef { Key = s.Key, Name = s.Name })
            .ToList();

        var dlg = new UI.Windows.SubLayerEditorWindow(layer.Name, current)
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true) return;

        layer.SubLayers = dlg.SelectedSubLayers;
        // 新字段生效后清掉旧字段，避免"改过又用回旧值"
        layer.SubLayerModifiers.Clear();
        _host.SaveSettings();

        // 如果当前停留的子层已被删除，回到"直接按"
        if (!string.IsNullOrEmpty(_subLayerKey) &&
            !layer.EffectiveSubLayers.Any(s =>
                string.Equals(s.Key, _subLayerKey, StringComparison.OrdinalIgnoreCase)))
        {
            _subLayerKey = "";
            _layer = KeyModifiers.None;
        }

        UpdateLayerButtons();
        SelectKey(_selectedKey);
        ShowBindStatus(Loc.F("「{0}」现有 {1} 个子层", layer.Name, layer.EffectiveSubLayers.Count));
    }

    private void SelectKey(string keyId)
    {
        if (_host == null) return;
        _selectedKey = keyId;
        string combo = CurrentCombo();
        var binding = FindInCurrentLayer(combo);

        _suppressEvents = true;

        string actionId = binding?.Action ?? "doNothing";

        // 两级下拉：先按动作找到它所属分类，切过去，再在分类里选中它
        var info = _host.Actions.Get(actionId);
        if (info != null)
        {
            foreach (ComboBoxItem it in CategoryBox.Items)
            {
                if (it.Tag as string == info.Category)
                {
                    if (!ReferenceEquals(CategoryBox.SelectedItem, it))
                    {
                        CategoryBox.SelectedItem = it;
                        BuildActionBox();
                    }
                    break;
                }
            }
        }

        foreach (ComboBoxItem item in ActionBox.Items)
        {
            if (item.Tag is string tag && tag.Equals(actionId, StringComparison.OrdinalIgnoreCase))
            {
                ActionBox.SelectedItem = item;
                // 可编辑下拉要手动同步显示文本，否则显示区是空白
                ActionBox.Text = item.Content as string ?? "";
                break;
            }
        }
        ArgBox.Text = binding?.Arg ?? "";

        // 同步另外三个"专用模式"的分类下拉
        foreach (ComboBoxItem wi in CategoryBoxWinBind.Items)
        {
            if (wi.Tag as string == info?.Category) { CategoryBoxWinBind.SelectedItem = wi; break; }
        }
        foreach (ComboBoxItem wb2 in CategoryBoxWeb.Items)
        {
            if (wb2.Tag as string == info?.Category) { CategoryBoxWeb.SelectedItem = wb2; break; }
        }
        foreach (ComboBoxItem tb in CategoryBoxType.Items)
        {
            if (tb.Tag as string == info?.Category) { CategoryBoxType.SelectedItem = tb; break; }
        }

        _suppressEvents = false;

        /* 按动作所属分类决定用哪个模式 ——
         *   winBind   → 两个按钮 + 当前绑定卡片
         *   webPage   → 网址 + 备注
         *   其它      → 功能下拉 + 参数框 */
        var mode = actionId switch
        {
            "winBind" => EditorMode.WinBind,
            "webPage" or "openFolder" or "openFile" => EditorMode.WebPage,   // 直达链接的三种类型
            "typeText" => EditorMode.TypeText,
            "customKey" => EditorMode.CustomKey,
            _ => EditorMode.Normal,
        };
        ApplyEditorMode(mode);

        if (mode == EditorMode.Normal)
        {
            ActionDesc.Text = info?.Description ?? "";
            ShowArgPanelFor(actionId,
                info?.NeedsArg == true || IsCountArgAction(actionId) || IsStepArgAction(actionId));
        }

        BuildKeyboard();

        /* 编辑区现在在键盘**下方**，两者离得远了：标题上必须写清"正在改哪个键"，
         * 否则用户往下滚之后会忘了自己在改谁（左右分栏时靠位置就能看出来）。 */
        EditorKeyText.Text = SelectedKeyLabel();
    }

    /// <summary>当前编辑的键在标题上的写法（含子层与修饰键）。</summary>
    private string SelectedKeyLabel()
    {
        string label = KeyName.Display(_selectedKey);
        if (!string.IsNullOrEmpty(_subLayerKey) && !IsModifierOnlySubLayer(_subLayerKey))
            label = $"{KeyName.Display(_subLayerKey)} {label}";
        if (_layer != KeyModifiers.None) label = $"{ModifierText(_layer)} {label}";
        return label;
    }

    /// <summary>这些动作的"参数"是次数，界面要提示填数字。</summary>
    private static bool IsCountArgAction(string actionId) => actionId is
        "moveLeft" or "moveRight" or "moveUp" or "moveDown"
        or "selectLeft" or "selectRight" or "selectUp" or "selectDown"
        or "moveWordLeft" or "moveWordRight"
        or "selectWordLeft" or "selectWordRight"
        or "pageMoveLineUp" or "pageMoveLineDown";

    /// <summary>
    /// 音量 / 亮度：参数是"按一次调多少个百分点"。
    ///
    /// 以前这两个动作只能整体用设置页里的幅度，想让"音量 +"和"音量 -"
    /// 走不同的步子就没办法。参数本来就支持覆盖（<c>StepOr</c>），
    /// 但界面上没有入口 —— 等于藏起来了。现在给它们一个明确的输入框。
    /// </summary>
    private static bool IsStepArgAction(string actionId) => actionId is
        "volumeUp" or "volumeDown" or "brightnessUp" or "brightnessDown";

    private static bool IsVolumeStepAction(string actionId)
        => actionId is "volumeUp" or "volumeDown";

    /// <summary>参数是不是一个有效的步进（1~100 的整数）。</summary>
    private static bool IsValidStep(string? arg)
        => int.TryParse(arg, out int v) && v is >= 1 and <= 100;

    /// <summary>这个步进动作"留空"时实际用多少（设置页里的幅度）。</summary>
    private int DefaultStepFor(string actionId)
    {
        var g = _host?.Settings.Global;
        if (g == null) return VolDefaultStep;
        return IsVolumeStepAction(actionId)
            ? Math.Clamp(g.VolumeStep, 1, 50)
            : Math.Clamp(g.BrightnessStep, 1, 50);
    }

    /// <summary>系统音量每步固定 2%，设置里不允许低于它。</summary>
    private const int VolDefaultStep = 5;

    /// <summary>按动作类型决定参数面板的样子。</summary>
    private void ShowArgPanelFor(string actionId, bool needsArg, bool isWinBind = false)
    {
        bool step = IsStepArgAction(actionId);

        ArgPanel.Visibility = (needsArg || step) && !isWinBind
            ? Visibility.Visible
            : Visibility.Collapsed;

        ArgDefaultButton.Visibility = step ? Visibility.Visible : Visibility.Collapsed;

        ArgLabel.Text = actionId switch
        {
            "openUrl" => Loc.T("网址"),
            "runProgram" => Loc.T("程序路径或命令"),
            "wrapSelection" => Loc.T("起始符号"),
            "volumeUp" or "volumeDown" => Loc.T("音量步进（%）"),
            "brightnessUp" or "brightnessDown" => Loc.T("亮度步进（%）"),
            _ => Loc.T("次数 / 参数"),
        };

        int def = DefaultStepFor(actionId);

        ArgHint.Text = actionId switch
        {
            "openUrl" => Loc.T("例如 https://github.com"),
            "runProgram" => Loc.T("例如 notepad.exe"),
            "wrapSelection" => Loc.T("例如 ( 会把选中文字变成 (文字)"),
            "volumeUp" or "volumeDown" =>
                Loc.F("按一次调多少音量：留空 = 用设置里的 {0}%。", def)
                + Loc.T("按住不放时会自动切成 1% 细调，一路平滑地加上去"),
            "brightnessUp" or "brightnessDown" =>
                Loc.F("按一次调多少亮度：留空 = 用设置里的 {0}%。", def)
                + Loc.T("按住不放时会自动切成 1% 细调，一路平滑地加上去"),
            _ => Loc.T("不填默认 1，填 5 表示一次移动 5 个字符"),
        };

        // 步进最多三位（1~100），次数最多三位（1~200）
        ArgBox.MaxLength = step || IsCountArgAction(actionId) ? 3 : 0;
    }

    /// <summary>「用默认」：清空参数，回到设置页里的调节幅度。</summary>
    private void OnArgUseDefault(object sender, RoutedEventArgs e)
    {
        // 走 TextChanged 那条路，绑定与保存都会自动跟上
        ArgBox.Text = "";
    }
}
