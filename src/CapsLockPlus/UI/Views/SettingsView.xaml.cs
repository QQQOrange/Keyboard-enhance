using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Localization;
using CapsLockPlus.UI.Windows;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.UI.Theme;
using Microsoft.Win32;

namespace CapsLockPlus.UI.Views;

public partial class SettingsView : UserControl
{
    private AppHost? _host;
    private bool _suppress;

    public SettingsView() => InitializeComponent();

    public void Initialize(AppHost host)
    {
        _host = host;
        Refresh();
    }

    public void Refresh()
    {
        if (_host == null) return;
        _suppress = true;
        var g = _host.Settings.Global;
        RefreshPermission();
        RefreshRemapState();
        RefreshUsage();

        /* 设备枚举是 P/Invoke：Refresh 会在每次保存时被调用（用户打字时每个键都保存），
         * 所以只在第一次进来时枚举，之后靠「刷新」按钮显式更新。
         *
         * 必须排在 RefreshConflicts 之前：体检结果和"当前生效"状态行里要把设备标识
         * （VID/PID）换成设备名，而名字来自这次枚举 —— 顺序反了第一次会显示标识。 */
        if (KeyboardList != null && KeyboardList.Items.Count == 0)
        {
            RefreshKeyboards();
            EnsureRemapDeviceItems();
            RefreshDeviceTrackingHint();
            RefreshRemapState();
        }
        RefreshConflicts();
        RefreshAppLayerEditor();
        RefreshGestureEditor();
        RefreshSnippetEditor();
        StartupBox.IsChecked = StartupManager.IsEnabled();
        TrayBox.IsChecked = g.CloseToTray;
        OpenOnStartBox.IsChecked = g.OpenWindowOnStartup;
        ToastBox.IsChecked = g.ShowToast;
        UpdateToastBox.IsChecked = g.ShowUpdateToast;
        FeedbackBox.IsChecked = g.ShowActionFeedback;
        DoubleTapBox.IsChecked = g.DoubleTapCaps;
        RepeatBox.IsChecked = g.RepeatWhenHeld;
        MouseSpeedSlider.Value = g.MouseSpeed;
        MouseSpeedText.Text = Loc.F("{0}（Caps+LAlt+滚轮 可快速调整）", g.MouseSpeed);
        /* 音量下限跟系统步进走：系统一步就是 2%，设 1% 也只能走 2%，
         * 与其让用户设一个"永远达不到"的值，不如把下限抬到 2%。 */
        int sysStep = Math.Max(1, Core.Windows.VolumeService.StepPercent());
        VolumeStepSlider.Minimum = sysStep;
        VolumeStepSlider.Value = Math.Max(g.VolumeStep, sysStep);
        BrightnessStepSlider.Value = g.BrightnessStep;
        UpdateVolumeStepText(g.VolumeStep);
        UpdateBrightnessStepText(g.BrightnessStep);
        TapCapsBox.SelectedIndex = g.TapCapsAction switch
        {
            "esc" => 1,
            "doNothing" => 2,
            _ => 0,
        };
        TextInputBox.SelectedIndex = (g.TextInputMode ?? "auto").ToLowerInvariant() switch
        {
            "keys" => 1,
            "unicode" => 2,
            _ => 0,
        };
        ThemeBox.SelectedIndex = g.Theme switch
        {
            "dark" => 1,
            "system" => 2,
            _ => 0,
        };
        BindLanguageBox(g.Language);
        DataPathText.Text = Loc.F("设置文件：{0}", AppPaths.SettingsFile)
                          + "\n" + Loc.F("日志：{0}", CoreLog.LogPath);
        RefreshStartupPwState();
        _suppress = false;
    }

    private void Save()
    {
        _host?.SaveSettings();
    }

    // ---------------- 规则分享 ----------------

    private void OnExportRulesClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        ShareBox.Text = RuleShare.Export(_host.Settings);
        _host.Notify(Loc.T("已生成分享码，复制上面的文本即可"));
    }

    private void OnImportRulesClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var (added, skipped) = RuleShare.Import(_host.Settings, ShareBox.Text, out string? error);
        if (error != null)
        {
            _host.Notify(error);
            return;
        }

        Save();
        Refresh();      // 各卡片跟着刷新，用户能立刻看到导入结果

        _host.Notify(added > 0
            ? Loc.F("已导入 {0} 项（跳过 {1} 项）", added, skipped)
            : Loc.F("没有新增内容：这 {0} 项都已存在", skipped));
    }

    // ---------------- 规则体检 ----------------

    /// <summary>
    /// 体检结果渲染。
    ///
    /// 设计取舍：**不自动修改任何配置**。自动"修好"冲突意味着替用户猜意图，
    /// 猜错就是静默改坏键盘。这里只负责说出来，怎么处理由用户决定。
    /// </summary>
    private void RefreshConflicts()
    {
        if (ConflictList == null || _host == null) return;

        ConflictList.Items.Clear();
        /* 用缓存下来的设备清单（RefreshKeyboards 时枚举一次）：
         * 这里每秒可能被调用好几次（用户打字时每个键都保存），不能每次 P/Invoke。 */
        var findings = RuleConflicts.Analyze(_host.Settings, _deviceKeys);

        ConflictEmptyText.Visibility = findings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConflictRecheckButton.Content = findings.Count == 0
            ? Loc.T("重新检查")
            : Loc.F("重新检查（{0} 条）", findings.Count);

        foreach (var f in findings)
        {
            bool warn = f.Level == ConflictLevel.Warn;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = (warn ? "⚠ " : "ⓘ ") + f.Message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource(warn ? "Brush.Danger" : "Brush.TextTertiary"),
            });

            /* "去修改"只出现在**能定位到具体卡片**的结论上。
             * 定位不到的宁可没有按钮，也不要给一个点了没反应的按钮。 */
            if (f.Target != ConflictTarget.None)
            {
                var go = new Button
                {
                    Content = Loc.T("去修改"),
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Style = (Style)FindResource("Button.Base"),
                    Tag = f.Target,
                };
                go.Click += OnConflictGoClick;
                Grid.SetColumn(go, 1);
                row.Children.Add(go);
            }

            ConflictList.Items.Add(row);
        }
    }

    /// <summary>
    /// 跳到对应的卡片并闪一下底色。
    ///
    /// 为什么闪底色而不是"选中那条规则"：改键/双击/组合键的列表项不是可选中控件，
    /// 硬做选中要改一堆模板；而"滚到那张卡片 + 高亮 1 秒"已经能把范围从
    /// "翻五张卡片"缩到"看一张卡片"，代价只有不到十行。
    /// </summary>
    private void OnConflictGoClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ConflictTarget target) return;

        /* 这几块内容已经不在「设置」页了（各自搬到了独立页面），
         * 点冲突项应该**跳到对应页面**，而不是在一段隐藏区域里滚动。 */
        string? page = target switch
        {
            ConflictTarget.Layer => "layers",
            ConflictTarget.Snippet => "hotstrings",
            ConflictTarget.AppLayer => "keys",
            _ => null,
        };
        if (page != null)
        {
            _host?.ShowMainWindow(page);
            return;
        }

        FocusSection(target switch
        {
            ConflictTarget.BaseRemap => RemapSection,
            ConflictTarget.Gesture => GestureSection,
            _ => null,
        });
    }

    private void FocusSection(System.Windows.Controls.Panel? section)
    {
        if (section == null) return;

        section.BringIntoView();

        var normal = section.Background;
        section.Background = (Brush)FindResource("Brush.AccentSoft");

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1100),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            section.Background = normal;
        };
        timer.Start();
    }

    /// <summary>复制成可粘贴的纯文本（含版本与时间，便于对账）。</summary>
    private void OnCopyConflictsClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        string version = "?";
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly().GetName();
            version = $"{asm.Version?.Major}.{asm.Version?.Minor}.{asm.Version?.Build}";
        }
        catch { }   /* 版本号取不到不影响复制 */

        string header = Loc.F("按键增强 v{0} 规则体检 · {1}",
            version, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        string report = RuleConflicts.BuildReport(RuleConflicts.Analyze(_host.Settings), header);

        if (ClipboardPaster.TrySetText(report))
            _host.Notify(Loc.T("已复制体检结果"));
        else
            _host.Notify(Loc.T("复制失败：剪贴板被其它程序占用"));
    }

    /// <summary>
    /// 新增规则后，如果这次改动带来了新冲突，就在同一条提示里补一句。
    ///
    /// 为什么不单独弹一条：Toast 只显示最新一条，弹两条等于前一条被吃掉。
    /// 也不做"每次保存都检查"：用户打字时每次按键都会保存，会刷成噪音。
    /// </summary>
    private string WithConflictHint(string message, int before)
    {
        if (_host == null) return message;

        int added = RuleConflicts.Analyze(_host.Settings).Count - before;
        return added <= 0
            ? message
            : message + Loc.F("　（新增 {0} 条冲突，可在「规则体检」查看）", added);
    }

    private int ConflictCount() => _host == null ? 0 : RuleConflicts.Analyze(_host.Settings).Count;

    private void OnRecheckConflictsClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        RefreshConflicts();
        int n = RuleConflicts.Analyze(_host.Settings).Count;
        _host.Notify(n == 0
            ? Loc.T("规则体检：没有发现问题")
            : Loc.F("规则体检：发现 {0} 条提示", n));
    }

    // ---------------- 键盘设备 ----------------

    /// <summary>
    /// 只读展示：系统认到几只键盘。
    ///
    /// 存在的意义是把"为什么另一只键盘不生效"从猜测变成可核对的事实 ——
    /// 目前改键对所有键盘一视同仁，所以这里只列设备，不做任何开关。
    /// </summary>
    /// <summary>上次枚举到的设备标识（供体检判断"设备是否在线"，避免重复枚举）。</summary>
    private List<string>? _deviceKeys;

    private void RefreshKeyboards()
    {
        if (KeyboardList == null) return;

        KeyboardList.Items.Clear();
        var groups = KeyboardDevices.Group(KeyboardDevices.List());

        _deviceKeys = groups
            .Select(g => KeyboardDevices.KeyOf(g.Sample))
            .Where(k => k.Length > 0)
            .ToList();

        KeyboardEmptyText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var g in groups)
        {
            var row = new TextBlock
            {
                Text = g.Count > 1
                    ? Loc.F("{0}　·　{1}　·　{2} 条系统条目", g.Name, g.Kind, g.Count)
                    : Loc.F("{0}　·　{1}", g.Name, g.Kind),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
                FontSize = 12,
                ToolTip = g.Sample,
                Style = (Style)FindResource("Text.Tertiary"),
            };
            KeyboardList.Items.Add(row);
        }
    }

    /// <summary>
    /// 设备追踪不可用时给出明确提示。
    ///
    /// 不说的话，用户配了"只对某只键盘生效"却毫无反应 —— 和"这功能是坏的"没区别。
    /// </summary>
    private void RefreshDeviceTrackingHint()
    {
        if (DeviceTrackingHint == null || _host == null) return;

        bool ok = _host.DeviceTrackingAvailable;
        DeviceTrackingHint.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        if (!ok)
            DeviceTrackingHint.Text = Loc.T("设备识别未启用：只对某只键盘生效的改键不会起作用（其它功能不受影响）。");
    }

    private void OnRefreshKeyboardsClick(object sender, RoutedEventArgs e)
    {
        RefreshKeyboards();

        /* 设备可能刚插上：把"生效键盘"下拉也重建一次，
         * 否则新键盘不会出现在预设的范围内，用户只能重启程序。 */
        if (RemapDeviceBox != null)
        {
            string keep = SelectedRemapDevice();
            RemapDeviceBox.Items.Clear();
            EnsureRemapDeviceItems();
            foreach (ComboBoxItem item in RemapDeviceBox.Items)
            {
                if (item.Tag is string tag && tag == keep) { RemapDeviceBox.SelectedItem = item; break; }
            }
        }

        RefreshConflicts();
        RefreshDeviceTrackingHint();
        int n = KeyboardDevices.Group(KeyboardDevices.List()).Count;
        _host?.Notify(n == 0
            ? Loc.T("没有枚举到键盘设备")
            : Loc.F("系统认到 {0} 个键盘", n));
    }

    // ---------------- 场景模板 ----------------

    /// <summary>
    /// 应用模板：合并进当前配置（只加缺失项）。
    /// 重复点同一个模板等于什么都不做，所以不需要二次确认。
    /// </summary>
    private void OnApplyPackClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;

        var pack = ScenarioPacks.Find(id);
        if (pack == null) return;

        int layerId = _host.Settings.Layers.FirstOrDefault()?.Id ?? 1;
        var (added, skipped) = ScenarioPacks.Apply(_host.Settings, pack, layerId);

        Save();
        Refresh();

        _host.Notify(added > 0
            ? Loc.F("已添加 {0} 项（跳过已存在的 {1} 项）", added, skipped)
            : Loc.F("没有新增内容：这 {0} 项都已存在", skipped));
    }

    // ---------------- 文本片段 ----------------

    /// <summary>正在编辑的片段（null = 新增模式）。</summary>
    private Snippet? _editingSnippet;

    private void RefreshSnippetEditor()
    {
        if (SnippetList == null || _host == null) return;

        SnippetList.Items.Clear();
        var items = _host.Settings.Global.Snippets.ToList();

        if (items.Count == 0)
        {
            SnippetList.Items.Add(new TextBlock
            {
                Text = Loc.T("还没有片段。上面填一个触发词（例如 ;mail）和内容，点「添加」。"),
                Style = (Style)FindResource("Text.Tertiary"),
                Margin = new Thickness(0, 4, 0, 4),
            });
            return;
        }

        foreach (var sn in items)
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };

            /* 暂停开关：片段经常一堆，临时不想让它展开时不该逼用户删掉。
             * 停用的仍然显示（灰一点），一眼看得出"有这条但没生效"。 */
            var enable = new CheckBox
            {
                IsChecked = sn.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = Loc.T("启用 / 停用这条片段"),
            };
            var captured = sn;
            enable.Click += (_, _) =>
            {
                captured.Enabled = enable.IsChecked == true;
                Save();
                RefreshSnippetEditor();
                _host?.Notify(captured.Enabled
                    ? Loc.F("已启用片段 {0}", captured.Trigger)
                    : Loc.F("已停用片段 {0}", captured.Trigger));
            };
            DockPanel.SetDock(enable, Dock.Left);
            row.Children.Add(enable);

            var del = new Button
            {
                Content = Loc.T("移除"),
                Padding = new Thickness(10, 3, 10, 3),
                Style = (Style)FindResource("Button.Base"),
            };
            del.Click += (_, _) =>
            {
                _host?.Settings.Global.Snippets.Remove(captured);
                if (ReferenceEquals(_editingSnippet, captured)) ResetSnippetForm();
                Save();
                RefreshSnippetEditor();
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);

            string preview = (sn.Text ?? "").Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 24) preview = preview[..24] + "…";

            // 配了"只在某些程序"时把范围显示出来，否则用户看不出为什么在别处不展开
            string scope = string.IsNullOrWhiteSpace(sn.Apps)
                ? ""
                : "　·　" + Loc.F("仅 {0}", sn.Apps.Trim());
            string note = string.IsNullOrWhiteSpace(sn.Comment) ? "" : "　·　" + sn.Comment.Trim();

            /* 点这一行 = 载入到上面的表单里编辑。
             * 以前只能"重新输一遍同样触发词"来覆盖，用户根本不知道能改。 */
            var body = new TextBlock
            {
                Text = $"{sn.Trigger} → {preview}{scope}{note}",
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("Text.Tertiary"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = Loc.T("点这一行：载入到上面编辑"),
                Opacity = sn.Enabled ? 1.0 : 0.5,
            };
            body.MouseLeftButtonUp += (_, _) => LoadSnippetIntoForm(captured);
            row.Children.Add(body);

            SnippetList.Items.Add(row);
        }
    }

    /// <summary>把一条片段载入表单（进入编辑模式）。</summary>
    private void LoadSnippetIntoForm(Snippet sn)
    {
        _editingSnippet = sn;
        SnippetTriggerBox.Text = sn.Trigger;
        SnippetTextBox.Text = sn.Text ?? "";
        SnippetAppsBox.Text = sn.Apps ?? "";
        SnippetCommentBox.Text = sn.Comment ?? "";
        SnippetSaveButton.Content = Loc.T("保存修改");
        SnippetCancelEditButton.Visibility = Visibility.Visible;
        SnippetTriggerBox.Focus();
        SnippetTriggerBox.CaretIndex = SnippetTriggerBox.Text.Length;
        UpdateSnippetTriggerHint();
    }

    private void OnCancelSnippetEdit(object sender, RoutedEventArgs e) => ResetSnippetForm();

    /// <summary>清空表单、退出编辑模式。</summary>
    private void ResetSnippetForm()
    {
        _editingSnippet = null;
        SnippetTriggerBox.Text = "";
        SnippetTextBox.Text = "";
        SnippetAppsBox.Text = "";
        SnippetCommentBox.Text = "";
        SnippetSaveButton.Content = Loc.T("添加");
        SnippetCancelEditButton.Visibility = Visibility.Collapsed;
        UpdateSnippetTriggerHint();
    }

    /// <summary>触发词输入的即时校验：别等到点「添加」才说哪里不对。</summary>
    private void OnSnippetTriggerChanged(object sender, TextChangedEventArgs e) => UpdateSnippetTriggerHint();

    private void UpdateSnippetTriggerHint()
    {
        if (SnippetTriggerHint == null) return;

        string trigger = (SnippetTriggerBox.Text ?? "").Trim();
        if (trigger.Length == 0)
        {
            SnippetTriggerHint.Text = Loc.T("触发词要以 ; / . 这类符号开头（例如 ;mail），不能含空格。");
            return;
        }
        if (trigger.Any(char.IsWhiteSpace))
        {
            SnippetTriggerHint.Text = Loc.T("✗ 触发词里不能有空格。");
            return;
        }
        if (SnippetMatcher.IsValidTrigger(trigger))
        {
            /* 提示"会不会被更长的触发词抢走"：这曾经是个坑 ——
             * 配了 ;m 之后再配 ;mail，;mail 永远触发不了。 */
            var others = _host?.Settings.Global.Snippets
                .Where(s => s != _editingSnippet && s.Enabled && s.Trigger.Length > trigger.Length
                            && s.Trigger.StartsWith(trigger, StringComparison.Ordinal))
                .Select(s => s.Trigger).ToList() ?? new List<string>();

            SnippetTriggerHint.Text = others.Count > 0
                ? Loc.F("✓ 触发词可用。注意：它同时是 {0} 的前缀，实际会等你打完再判断（约 120ms）。",
                        string.Join("、", others.Take(3)))
                : Loc.T("✓ 触发词可用。");
            return;
        }

        SnippetTriggerHint.Text = Loc.T("✗ 触发词必须以 ; / . 这类符号开头，不能以字母或数字开头。");
    }

    /// <summary>试算：不真的展开，只告诉用户会不会命中。</summary>
    private void OnSnippetTestChanged(object sender, TextChangedEventArgs e)
    {
        if (SnippetTestHint == null || _host == null) return;

        string buffer = (SnippetTestBox.Text ?? "").TrimStart();
        if (buffer.Length == 0)
        {
            SnippetTestHint.Text = Loc.T("试算：在上面随便打一段（例如 ;mail），这里会说命中哪一条、为什么没命中。");
            return;
        }

        string? process = _host.Engine.ForegroundProcess;
        var list = _host.Settings.Global.Snippets;
        var hit = SnippetMatcher.Match(list, buffer, process);

        if (hit != null)
        {
            string preview = (hit.Text ?? "").Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 40) preview = preview[..40] + "…";
            SnippetTestHint.Text = Loc.F("✓ 会展开成：{0}", preview);
            return;
        }

        // 没命中时把最常见的原因直接点出来，省得用户猜
        var blockedScope = list.FirstOrDefault(s => s.Enabled
            && SnippetMatcher.IsValidTrigger(s.Trigger)
            && buffer.EndsWith(s.Trigger, StringComparison.Ordinal)
            && !SnippetMatcher.AllowedIn(s, process));
        if (blockedScope != null)
        {
            SnippetTestHint.Text = Loc.F(
                "命中 {0}，但它限定只在 {1} 里展开，当前前台是「{2}」。",
                blockedScope.Trigger, blockedScope.Apps,
                string.IsNullOrWhiteSpace(process) ? Loc.T("（未知）") : process);
            return;
        }

        var disabled = list.FirstOrDefault(s => !s.Enabled
            && SnippetMatcher.IsValidTrigger(s.Trigger)
            && buffer.EndsWith(s.Trigger, StringComparison.Ordinal));
        if (disabled != null)
        {
            SnippetTestHint.Text = Loc.F("命中 {0}，但这条片段被停用了。", disabled.Trigger);
            return;
        }

        SnippetTestHint.Text = Loc.T("没有命中任何片段（触发词必须落在词首、以符号开头）。");
    }

    private void OnSnippetAppsHint(object sender, TextChangedEventArgs e)
    {
        if (SnippetAppsHint == null) return;

        string apps = (SnippetAppsBox.Text ?? "").Trim();
        if (apps.Length == 0)
        {
            SnippetAppsHint.Text = Loc.T("当前：在所有程序里都会展开。");
            return;
        }

        string current = _host?.Engine.ForegroundProcess ?? "";
        if (string.IsNullOrWhiteSpace(current))
        {
            SnippetAppsHint.Text = Loc.F("当前：只在 {0} 里展开（拿不到当前前台程序，无法预判）。", apps);
            return;
        }

        bool hit = SnippetMatcher.AllowedIn(new Snippet { Apps = apps }, current);
        SnippetAppsHint.Text = hit
            ? Loc.F("当前：只在 {0} 里展开 —— 现在的前台「{1}」在列表里。", apps, current)
            : Loc.F("当前：只在 {0} 里展开 —— 现在的前台「{1}」不在列表里，此处不会展开。", apps, current);
    }

    // ---- 片段的「只在哪些程序里展开」：两条录入途径 ----

    /// <summary>把程序名追加到 Apps 框（去重，保留用户已经填的内容）。</summary>
    private void AppendSnippetApp(string process)
    {
        if (SnippetAppsBox == null) return;

        string merged = SnippetMatcher.MergeAppList(SnippetAppsBox.Text, process, out bool added);
        if (!added)
        {
            _host?.Notify(Loc.F("{0} 已经在列表里了", SnippetMatcher.Normalize(process)));
            return;
        }

        SnippetAppsBox.Text = merged;
        SnippetAppsBox.CaretIndex = SnippetAppsBox.Text.Length;
        OnSnippetAppsHint(SnippetAppsBox, null!);
    }

    /// <summary>入口一：从文件管理器里选一个 exe。</summary>
    private void OnSnippetPickExeClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择程序"),
            Filter = Loc.T("程序 (*.exe)|*.exe|所有文件|*.*"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        AppendSnippetApp(System.IO.Path.GetFileName(dlg.FileName));
    }

    /// <summary>入口二：十字准星点选一个已打开的窗口，取它的程序名。</summary>
    private void OnSnippetPickWindowClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var picker = new UI.Windows.WindowPickerWindow();
        if (Window.GetWindow(this) is { } owner) picker.Owner = owner;
        picker.ShowDialog();

        var h = picker.PickedWindow;
        if (h == IntPtr.Zero) return;

        var info = Core.Windows.WindowService.Describe(h);
        if (info == null || string.IsNullOrEmpty(info.ExePath))
        {
            _host.Notify(Loc.T("取不到这个窗口的程序路径，改用「选择程序…」试试"));
            return;
        }
        AppendSnippetApp(System.IO.Path.GetFileName(info.ExePath));
    }

    /// <summary>
    /// 填入一条现成的范例。
    ///
    /// 片段的规则（符号开头、必须在词首、程序范围）不好用一句话讲清，
    /// 给一条能直接用的例子比写八行说明管用：用户改两个字就能当自己的用。
    /// </summary>
    private void OnSnippetExampleClick(object sender, RoutedEventArgs e)
    {
        SnippetTriggerBox.Text = ";mail";
        SnippetTextBox.Text = "someone@example.com";
        SnippetCommentBox.Text = Loc.T("示例：常用邮箱");
        SnippetAppsBox.Text = "";
        OnSnippetAppsHint(SnippetAppsBox, null!);
        UpdateSnippetTriggerHint();
        _host?.Notify(Loc.T("已填入示例：在任何程序里打 ;mail 就会变成邮箱地址"));
        SnippetTriggerBox.Focus();
        SnippetTriggerBox.CaretIndex = SnippetTriggerBox.Text.Length;
    }

    /// <summary>添加 / 保存修改。</summary>
    private void OnAddSnippetClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;

        string trigger = (SnippetTriggerBox.Text ?? "").Trim();
        string text = SnippetTextBox.Text ?? "";
        string apps = (SnippetAppsBox.Text ?? "").Trim();
        string comment = (SnippetCommentBox.Text ?? "").Trim();

        if (!SnippetMatcher.IsValidTrigger(trigger))
        {
            _host.Notify(Loc.T("触发词要以 ; / . 这类符号开头，且不能含空格"));
            SnippetTriggerBox.Focus();
            return;
        }
        if (text.Length == 0)
        {
            _host.Notify(Loc.T("展开内容不能为空"));
            SnippetTextBox.Focus();
            return;
        }

        int conflictsBefore = ConflictCount();
        var list = _host.Settings.Global.Snippets;

        if (_editingSnippet != null)
        {
            // 编辑模式：先查"改成的新触发词"有没有撞上别人的
            var clash = list.FirstOrDefault(x => !ReferenceEquals(x, _editingSnippet) && x.Trigger == trigger);
            if (clash != null)
            {
                _host.Notify(Loc.F("触发词 {0} 已经属于另一条片段，请换一个或取消编辑", trigger));
                return;
            }
            _editingSnippet.Trigger = trigger;
            _editingSnippet.Text = text;
            _editingSnippet.Apps = apps;
            _editingSnippet.Comment = comment;
            Save();
            ResetSnippetForm();
            RefreshSnippetEditor();
            _host.Notify(WithConflictHint(Loc.F("已保存片段 {0}", trigger), conflictsBefore));
            return;
        }

        var existing = list.FirstOrDefault(x => x.Trigger == trigger);
        if (existing != null)
        {
            /* 以前这里是静默覆盖：用户以为自己加了一条新片段，其实是把旧的改掉了。 */
            bool go = AppDialog.Confirm(Window.GetWindow(this),
                Loc.F("已经有触发词为 {0} 的片段了。\n\n要用现在的内容覆盖它吗？", trigger),
                Loc.T("覆盖片段"), DialogKind.Question, okText: Loc.T("覆盖"));
            if (!go) return;

            existing.Text = text;
            existing.Apps = apps;
            existing.Comment = comment;
            existing.Enabled = true;
            Save();
            ResetSnippetForm();
            RefreshSnippetEditor();
            _host.Notify(Loc.F("已覆盖片段 {0}", trigger));
            return;
        }

        list.Add(new Snippet { Trigger = trigger, Text = text, Apps = apps, Comment = comment, Enabled = true });
        Save();
        ResetSnippetForm();
        RefreshSnippetEditor();
        _host.Notify(WithConflictHint(Loc.F("已添加片段 {0}", trigger), conflictsBefore));
    }

    // ---------------- 双击动作 ----------------

    /// <summary>可选的键：只列常用键，避免下拉框几百项没法找。</summary>
    private static readonly string[] GestureKeys =
    {
        "capslock", "esc", "tab", "space", "enter", "backspace", "delete",
        "insert", "home", "end", "f1", "f2", "f3", "f4", "f5", "f6",
        "f7", "f8", "f9", "f10", "f11", "f12",
    };

    private void RefreshGestureEditor()
    {
        if (GestureKeyBox == null || _host == null) return;

        GestureKeyBox.Items.Clear();

        /* 第一项是占位，不是"默认值"。
         * 早先这里默认选中第一项（大写键）+ 第一个动作（不绑定），
         * 用户看到的像是"已经有一条：双击大写键 → 不绑定"，
         * 而上面又有一个"双击 Caps 打开搜索面板"的开关 —— 两处都提大写键，
         * 看起来就像冲突。占位项能把这个误解从根上去掉。 */
        GestureKeyBox.Items.Add(new ComboBoxItem { Content = Loc.T("选择按键…"), Tag = "" });
        foreach (string k in GestureKeys)
            GestureKeyBox.Items.Add(new ComboBoxItem { Content = KeyName.Display(k), Tag = k });
        GestureKeyBox.SelectedIndex = 0;

        GestureActionBox.Items.Clear();
        GestureActionBox.Items.Add(new ComboBoxItem { Content = Loc.T("选择动作…"), Tag = "" });
        foreach (var a in _host.Actions.Items.Where(a => !a.Hidden))
            GestureActionBox.Items.Add(new ComboBoxItem { Content = a.Title, Tag = a.Id });
        GestureActionBox.SelectedIndex = 0;

        GestureKeyBox.SelectionChanged -= OnGestureSelectionChanged;
        GestureKeyBox.SelectionChanged += OnGestureSelectionChanged;
        GestureActionBox.SelectionChanged -= OnGestureSelectionChanged;
        GestureActionBox.SelectionChanged += OnGestureSelectionChanged;
        UpdateGestureAddState();

        GestureList.Items.Clear();
        if (GestureEmptyHint != null)
            GestureEmptyHint.Visibility = _host.Settings.Global.KeyGestures.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        foreach (var g in _host.Settings.Global.KeyGestures.ToList())
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };

            var del = new Button
            {
                Content = Loc.T("移除"),
                Padding = new Thickness(10, 3, 10, 3),
                Style = (Style)FindResource("Button.Base"),
            };
            var captured = g;
            del.Click += (_, _) =>
            {
                _host?.Settings.Global.KeyGestures.Remove(captured);
                Save();
                RefreshGestureEditor();
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);

            string actionTitle = ActionCatalog.TitleOf(g.DoubleTapAction) ?? g.DoubleTapAction;
            row.Children.Add(new TextBlock
            {
                Text = Loc.F("双击 {0} → {1}", KeyName.Display(g.Key), actionTitle),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("Text.Tertiary"),
            });
            GestureList.Items.Add(row);
        }
    }

    /// <summary>两个下拉都选好了才让点「添加」。</summary>
    private void OnGestureSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateGestureAddState();

    private void UpdateGestureAddState()
    {
        if (GestureAddButton == null) return;

        bool keyOk = GestureKeyBox?.SelectedItem is ComboBoxItem k && k.Tag is string ks && ks.Length > 0;
        bool actionOk = GestureActionBox?.SelectedItem is ComboBoxItem a && a.Tag is string ac && ac.Length > 0;
        GestureAddButton.IsEnabled = keyOk && actionOk;
    }

    private void OnAddGestureClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (GestureKeyBox.SelectedItem is not ComboBoxItem kItem || kItem.Tag is not string key) return;
        if (GestureActionBox.SelectedItem is not ComboBoxItem aItem || aItem.Tag is not string action) return;
        if (key.Length == 0 || action.Length == 0) return;

        /* 触发键的双击归触发键自己管（OnTriggerUp 里处理 DoubleTapAction），
         * 这里再加一条永远不会触发 —— 与其让用户白配，不如当场说明。 */
        if (_host.Settings.Triggers.Any(t => t.Enabled &&
                string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            _host.Notify(Loc.F("「{0}」是触发键，它的双击在「触发键与层」页设置，这里配了也不会触发",
                KeyName.Display(key)));
            return;
        }

        int conflictsBefore = ConflictCount();
        var list = _host.Settings.Global.KeyGestures;
        var existing = list.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing != null) existing.DoubleTapAction = action;     // 同键覆盖
        else list.Add(new KeyGesture { Key = key, DoubleTapAction = action });

        Save();
        RefreshGestureEditor();
        _host.Notify(WithConflictHint(
            Loc.F("已设置：双击 {0} 执行 {1}", KeyName.Display(key), ActionCatalog.TitleOf(action) ?? action),
            conflictsBefore));
    }

    // ---------------- 按应用自动启用层 ----------------

    /// <summary>刷新"层"下拉框与已有规则列表。</summary>
    private void RefreshAppLayerEditor()
    {
        if (AppLayerLayerBox == null || _host == null) return;

        var layers = _host.Settings.Layers.Where(l => l.Enabled).ToList();
        AppLayerLayerBox.Items.Clear();
        foreach (var l in layers)
        {
            /* 名字要走 LayerName.Display：层的名字是**配置里的数据**，
             * 程序生成的默认名（"基础层" / "第 3 层"）要跟着界面语言走，
             * 用户自己改过的名字才原样显示。
             * 以前这里直接用 l.Name —— 英文界面下会看到 "Layer 1 · 基础层" 这种半中半英。 */
            string name = LayerName.Display(l.Id, l.Name);
            string text = Loc.F("第 {0} 层 · {1}", l.Id, name);
            AppLayerLayerBox.Items.Add(new ComboBoxItem { Content = text, Tag = l.Id, ToolTip = text });
        }
        if (AppLayerLayerBox.Items.Count > 0) AppLayerLayerBox.SelectedIndex = 0;

        AppLayerList.Items.Clear();
        foreach (var rule in _host.Settings.Global.AppLayers.ToList())
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };

            var del = new Button
            {
                Content = Loc.T("移除"),
                Padding = new Thickness(10, 3, 10, 3),
                Style = (Style)FindResource("Button.Base"),
            };
            var captured = rule;
            del.Click += (_, _) =>
            {
                _host?.Settings.Global.AppLayers.Remove(captured);
                Save();
                RefreshAppLayerEditor();
                _host?.RefreshAppLayer();
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);

            row.Children.Add(new TextBlock
            {
                Text = Loc.F("{0} → 第 {1} 层", rule.Process, rule.LayerId),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("Text.Tertiary"),
            });
            AppLayerList.Items.Add(row);
        }
    }

    private void OnAddAppLayerClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;

        string process = (AppLayerProcessBox.Text ?? "").Trim();
        if (AppLayerLayerBox.SelectedItem is not ComboBoxItem item || item.Tag is not int layerId)
            return;

        string normalized = AppLayerRules.Normalize(process);
        if (normalized.Length == 0)
        {
            _host.Notify(Loc.T("请先填进程名，例如 Code.exe"));
            return;
        }

        int conflictsBefore = ConflictCount();
        var list = _host.Settings.Global.AppLayers;
        var existing = list.FirstOrDefault(r => AppLayerRules.Normalize(r.Process) == normalized);
        if (existing != null)
        {
            existing.LayerId = layerId;        // 同一个程序重复添加 = 改绑定
        }
        else
        {
            list.Add(new AppLayerRule { Process = normalized, LayerId = layerId });
        }

        AppLayerProcessBox.Text = "";
        Save();
        RefreshAppLayerEditor();
        _host.RefreshAppLayer();
        _host.Notify(WithConflictHint(
            Loc.F("已设置：{0} 使用第 {1} 层", normalized, layerId), conflictsBefore));
    }

    // ---------------- 使用统计 ----------------

    private void OnUsageResetClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;

        UsageStats.Reset();
        RefreshUsage();
        _host.Notify(Loc.T("已清空使用统计"));
    }

    /// <summary>统计行：总数 + 今日 + 最常用动作。没有数据时明说，别显示 0 像个 bug。</summary>
    private void RefreshUsage()
    {
        if (UsageText == null) return;

        var (total, top, topCount, today) = UsageStats.Summary();
        if (total <= 0)
        {
            UsageText.Text = Loc.T("还没有统计数据：执行动作后会自动累计。");
            UsageResetButton.Visibility = Visibility.Collapsed;
            return;
        }

        string topName = string.IsNullOrEmpty(top) ? Loc.T("无") : (ActionCatalog.TitleOf(top) ?? top);
        UsageText.Text = Loc.F("已执行 {0} 次动作 · 今日 {1} 次 · 最常用：{2}（{3} 次）",
            total, today, topName, topCount);
        UsageResetButton.Visibility = Visibility.Visible;
    }

    // ---------------- 基础改键 ----------------

    /// <summary>
    /// 点预设按钮：已启用就取消，否则补齐。
    /// 只有"成对"的预设（如 Caps↔Esc）才需要这样处理；单向映射也走同一条路。
    /// </summary>
    private void OnRemapPresetClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;

        var preset = KeyRemapMap.FindPreset(id);
        if (preset == null) return;

        int conflictsBefore = ConflictCount();
        string device = SelectedRemapDevice();

        /* 涉及修饰键的预览要**先确认**。
         *
         * 这类预设一开，整个系统的按键语义就变了（Ctrl 变 Alt 之后，Ctrl+C、
         * Ctrl+S、Ctrl+V 全部变成别的意思），而按钮就在设置页上排成一排，
         * 很容易点着玩。用户反馈"我从没设置过 Ctrl 和 Alt 互换"就是这种情况。 */
        bool swapsModifier = preset.Pairs.Any(pair => IsModifierKey(pair.From) || IsModifierKey(pair.To));
        bool alreadyOn = KeyRemapMap.IsActive(_host.Settings.Global.BaseRemaps, preset, device);

        if (!alreadyOn && swapsModifier &&
            !UI.Windows.AppDialog.Confirm(Window.GetWindow(this),
                Loc.F("「{0}」会把修饰键本身换掉：开启后按 Ctrl 发出的就是 Alt，\n"
                      + "所有 Ctrl 快捷键（Ctrl+C / Ctrl+V / Ctrl+S…）都会变成 Alt 组合。\n\n"
                      + "确定要开启吗？", preset.Title),
                Loc.T("确认启用改键"),
                Windows.DialogKind.Warn,
                okText: Loc.T("启用")))
        {
            return;
        }

        bool on = KeyRemapMap.Toggle(_host.Settings.Global.BaseRemaps, preset, device);
        Save();
        RefreshRemapState();
        string scope = device.Length == 0 ? "" : Loc.F("（仅 {0}）", device);
        _host.Notify(WithConflictHint(on
            ? Loc.F("已启用改键：{0}{1}", preset.Title, scope)
            : Loc.F("已取消改键：{0}{1}", preset.Title, scope), conflictsBefore));
    }

    private void OnRemapClearClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (_host.Settings.Global.BaseRemaps.Count == 0) return;

        _host.Settings.Global.BaseRemaps.Clear();
        Save();
        RefreshRemapState();
        _host.Notify(Loc.T("已清除全部基础改键"));
    }

    /// <summary>状态行：列出当前生效的改键，空则明说"未启用"。</summary>
    /// <summary>当前选中的"生效键盘"；空串 = 所有键盘。</summary>
    private string SelectedRemapDevice()
        => RemapDeviceBox?.SelectedItem is ComboBoxItem item && item.Tag is string s ? s : "";

    /// <summary>
    /// 填充"生效键盘"下拉。枚举失败时只剩"所有键盘"，功能退化为旧行为。
    /// </summary>
    private void EnsureRemapDeviceItems()
    {
        if (RemapDeviceBox == null || RemapDeviceBox.Items.Count > 0) return;

        RemapDeviceBox.Items.Add(new ComboBoxItem { Content = Loc.T("所有键盘"), Tag = "" });

        foreach (var g in KeyboardDevices.Group(KeyboardDevices.List()))
        {
            string key = KeyboardDevices.KeyOf(g.Sample);
            if (key.Length == 0) continue;
            RemapDeviceBox.Items.Add(new ComboBoxItem
            {
                Content = Loc.F("{0}（{1}）", g.Name, g.Kind),
                Tag = key,
            });
        }

        RemapDeviceBox.SelectedIndex = 0;
    }

    private void RefreshRemapState()
    {
        if (RemapStateText == null) return;

        var list = _host?.Settings.Global.BaseRemaps;
        string desc = list == null || list.Count == 0 ? "" : KeyRemapMap.Describe(list);

        /* 涉及修饰键的改键要额外警告：用户会把"Ctrl+C 变成 Alt+C"当成 bug，
         * 而它其实是这条规则的必然结果。写在设置页，比事后排查便宜得多。 */
        bool swapsModifier = list != null && list.Any(r => IsModifierKey(r.From) || IsModifierKey(r.To));

        string text = string.IsNullOrEmpty(desc)
            ? Loc.T("当前未启用任何基础改键")
            : Loc.F("当前生效：{0}", desc);

        if (swapsModifier)
            text += "\n" + Loc.T("注意：这些规则会换掉修饰键本身 —— 开了 Ctrl→Alt 之后，Ctrl+C 在前台程序看来就是 Alt+C。不需要就点「清除」。");

        RemapStateText.Text = text;

        try
        {
            RemapStateText.Foreground = swapsModifier
                ? (System.Windows.Media.Brush)FindResource("Brush.Warning")
                : (System.Windows.Media.Brush)FindResource("Brush.TextTertiary");
        }
        catch { }
        RemapClearButton.Visibility = list == null || list.Count == 0
            ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>是不是修饰键（Ctrl / Shift / Alt / Win 的任意左右码）。</summary>
    private static bool IsModifierKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        return key!.Trim().ToLowerInvariant() switch
        {
            "ctrl" or "lctrl" or "rctrl" or "control" => true,
            "shift" or "lshift" or "rshift" => true,
            "alt" or "lalt" or "ralt" or "menu" => true,
            "win" or "lwin" or "rwin" => true,
            _ => false,
        };
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        bool enabled = StartupBox.IsChecked == true;
        _host.Settings.Global.StartWithWindows = enabled;
        Save();
        /* 自启载体要跟着"是否管理员模式"走：
         * 管理员模式用计划任务（登录静默提权，不弹 UAC），否则用注册表。 */
        var mode = StartupManager.Sync(enabled, _host.Settings.Global.RunAsAdmin);
        RefreshAdminState();      // 自启方式变了，权限提示要跟着变
        _host.Notify(enabled
            ? (mode == StartupMode.Task
                ? Loc.T("已设置开机自启（管理员模式，不再弹 UAC）")
                : Loc.T("已设置开机自启"))
            : Loc.T("已取消开机自启"));
    }

    private void OnTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.CloseToTray = TrayBox.IsChecked == true;
        Save();
    }

    private void OnOpenOnStartChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.OpenWindowOnStartup = OpenOnStartBox.IsChecked == true;
        Save();
    }

    private void OnToastChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.ShowToast = ToastBox.IsChecked == true;
        Save();
    }

    private void OnUpdateToastChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.ShowUpdateToast = UpdateToastBox.IsChecked == true;
        Save();
    }

    /// <summary>刷新权限显示。</summary>
    private void RefreshPermission()
    {
        bool admin = Core.ElevationHelper.IsElevated;
        AdminBadge.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        AdminBadgeText.Text = admin ? Loc.T("管理员") : Loc.T("普通权限");
        if (RunAsAdminBox != null) RunAsAdminBox.IsChecked = _host?.Settings.Global.RunAsAdmin ?? false;
        if (RestartUnelevatedButton != null)
            RestartUnelevatedButton.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;

        PermissionText.Text = admin
            ? Core.ElevationHelper.PermissionSummary()
              + "\n" + Loc.T("⚠ 如果你发现热键没反应（按 Tab 只会切换焦点），先把「以管理员身份启动」关掉再重启 —— 部分机器上管理员权限会让键盘钩子收不到按键。")
            : Core.ElevationHelper.PermissionSummary();
        RefreshAdminState();
    }

    /// <summary>
    /// 状态行：「设置成什么」和「现在实际是什么」分开写。
    ///
    /// 两者不一致时把「立即重启以生效」露出来 ——
    /// 否则用户改完开关以为已经生效，实际上要等下次启动，
    /// 中间会一直以为功能坏了（这个软件之前就吃过这个亏）。
    /// </summary>
    private void RefreshAdminState()
    {
        if (AdminStateText == null) return;

        bool running = Core.ElevationHelper.IsElevated;
        bool wanted = _host?.Settings.Global.RunAsAdmin ?? false;

        /* 整行当成一条原文去翻 —— 中文用「　·　」分隔、英文该用「 · 」，
         * 拆成多段拼接的话分隔符会跟着中文硬编码留在英文界面里。 */
        string adminNow = running ? Loc.T("管理员权限") : Loc.T("普通权限");
        string adminNext = wanted ? Loc.T("管理员") : Loc.T("普通");
        AdminStateText.Text = Loc.F("当前：{0}运行　·　设置：下次启动以{1}权限运行", adminNow, adminNext);

        /* 开机自启 + 管理员：只有用**注册表 Run 项**时才会每次登录弹 UAC。
         * 改用计划任务（HighestAvailable）后，登录时由任务计划程序直接提权，
         * 不再弹 —— 所以提示要看实际载体，不能只看两个开关。 */
        if (wanted && _host?.Settings.Global.StartWithWindows == true)
            AdminStateText.Text += StartupManager.CurrentMode() == StartupMode.Task
                ? Loc.T("\n开机自启已改用计划任务：登录时静默提权，不会再弹 UAC。")
                : Loc.T("\n开机自启时会弹一次 UAC 确认 —— 关掉「开机自动启动」就不会弹。");

        RestartElevatedButton.Visibility = running == wanted ? Visibility.Collapsed : Visibility.Visible;
        // 已经是管理员时不能再"提权重启"，按钮只用于"降回普通权限"
        RestartElevatedButton.Content = wanted ? Loc.T("立即重启以生效") : Loc.T("立即重启（回到普通权限）");
    }

    /// <summary>
    /// 「以管理员身份启动」开关。
    ///
    /// ⚠ 保存后需要**重启**才生效（启动时才决定是否提权）。
    /// 并在开启时明确告知后果：取词功能会失效。
    ///
    /// 不再强制重启：以前保存完直接 `ExitApp(restart: true)`，
    /// 用户可能正在做别的事，程序突然消失又弹 UAC 很吓人。
    /// 现在只保存，然后问一句要不要现在重启。
    /// </summary>
    private void OnRunAsAdminChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        bool enable = RunAsAdminBox.IsChecked == true;

        if (enable)
        {
            bool ok = AppDialog.Confirm(Window.GetWindow(this),
                Loc.T("以管理员身份运行后，可以对着「以管理员身份运行」的程序按快捷键。\n\n") +
                Loc.T("取词（选中文字 → 直接搜索）会自动改用普通权限代理执行，\n") +
                Loc.T("管理员模式下通常仍可正常使用。\n") +
                Loc.T("例外：目标程序本身也是管理员权限时，仍然取不到它的选区\n") +
                Loc.T("（这是 Windows 的隔离规则，任何用户态程序都绕不过）。\n\n") +
                Loc.T("代价：开机自启会改用计划任务（登录不再弹 UAC）；\n") +
                Loc.T("把文件拖进本程序窗口会失效。\n\n") +
                Loc.T("确定要开启吗？"),
                Loc.T("开启管理员模式"), DialogKind.Warn, okText: Loc.T("开启"));
            if (!ok)
            {
                _suppress = true;
                RunAsAdminBox.IsChecked = false;
                _suppress = false;
                return;
            }
        }

        _host.Settings.Global.RunAsAdmin = enable;
        Save();

        /* 提权方式变了，自启载体也要跟着换：
         *   · 开管理员 → 改成计划任务（登录静默提权，不再每次弹 UAC）；
         *   · 关管理员 → 改回注册表，并把遗留的计划任务删掉
         *     （否则下次登录仍会被那个任务以管理员拉起，开关等于没关）。
         * 此项失败不影响主流程：Sync 内部会退回 Run 键并记日志。 */
        StartupManager.Sync(_host.Settings.Global.StartWithWindows, enable);

        RefreshAdminState();

        bool ask = AppDialog.Confirm(Window.GetWindow(this),
            enable
                ? Loc.T("已保存。下次启动会以管理员身份运行（会弹一次 UAC 确认）。\n\n现在就重启吗？")
                : Loc.T("已保存。下次启动会以普通权限运行。\n\n现在就重启吗？"),
            Loc.T("需要重启才生效"), DialogKind.Question, okText: Loc.T("现在重启"), cancelText: Loc.T("稍后"));
        if (ask) RestartApp();
    }

    /// <summary>设置页里的「立即重启以生效」按钮。</summary>
    private void OnRestartForElevation(object sender, RoutedEventArgs e) => RestartApp();

    /// <summary>
    /// 重启程序。
    ///
    /// 目标权限是管理员、当前不是时，先显式拉起提权实例再退出 ——
    /// 否则普通权限重启一次还是普通权限，用户会以为开关没生效。
    /// </summary>
    private void RestartApp()
    {
        if (_host == null) return;
        bool wanted = _host.Settings.Global.RunAsAdmin;
        if (wanted && !Core.ElevationHelper.IsElevated)
        {
            if (Core.ElevationHelper.RelaunchElevated(Environment.GetCommandLineArgs().Skip(1).ToArray(), out string? err))
            {
                _host.ExitApp(restart: false);
                return;
            }
            AppDialog.Warn(Window.GetWindow(this),
                Loc.T("提权失败：") + (err ?? Loc.T("未知原因")) + Loc.T("\n\n（多半是在 UAC 对话框里点了「否」）"),
                Loc.T("没能以管理员身份重启"));
            return;
        }
        _host.ExitApp(restart: true);
    }

    /* ---- 启动密码（与备份密码完全独立的一套） ----------------------------
     *
     * 开启：先弹"设置新密码"（两遍确认），落库后才算开；
     * 关闭：先验当前密码，证明了才让关 —— 否则路过的人顺手一拨就解开了；
     * 修改：先验旧密码，再设新密码。
     *
     * 密码的存取全在 Core.Settings.StartupPassword（文件是真相），
     * 这里只做流程；设置里的 Security 区段是镜像，供界面显示与随设置持久化。
     */

    /// <summary>刷新开关、状态行与「修改密码」按钮（真相以 startup.json 为准）。</summary>
    private void RefreshStartupPwState()
    {
        if (_host == null) return;

        StartupPassword.MirrorTo(_host.Settings.Security);
        bool on = _host.Settings.Security.StartupPasswordEnabled;

        _suppress = true;
        StartupPwBox.IsChecked = on;
        _suppress = false;

        StartupPwStateText.Text = on
            ? Loc.T("已开启：下次启动软件时先要输入密码。忘记密码可删除数据目录里的 startup.json 解除保护（其他设置不受影响）。")
            : Loc.T("未开启：启动软件不需要密码。");

        ChangeStartupPwButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStartupPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;

        bool enable = StartupPwBox.IsChecked == true;
        var sec = _host.Settings.Security;

        if (enable)
        {
            var dlg = new NewPasswordWindow(
                Loc.T("设置启动密码"),
                Loc.T("设好后，每次启动本软件都要先输入这个密码才能进入主界面。"));
            dlg.ShowDialog();
            if (dlg.Password == null)
            {
                RevertStartupPwBox(false);
                return;
            }

            if (!StartupPassword.TrySet(dlg.Password, out string err))
            {
                AppDialog.Error(Window.GetWindow(this), err, Loc.T("设置启动密码"));
                RevertStartupPwBox(false);
                return;
            }

            StartupPassword.MirrorTo(sec);
            Save();
            RefreshStartupPwState();
            _host.Notify(Loc.T("已设置启动密码，下次启动软件时生效"));
        }
        else
        {
            // 关保护要先证明是机主：路过的人不能顺手一拨就解开
            var verify = new StartupPasswordWindow(
                p => StartupPassword.Verify(p),
                Loc.T("关闭启动密码"),
                Loc.T("请输入当前的启动密码。验证通过后才能关闭保护。"),
                Loc.T("关闭保护"), Loc.T("取消"));
            verify.ShowDialog();
            if (!verify.Passed)
            {
                RevertStartupPwBox(true);
                return;
            }

            StartupPassword.Clear();
            StartupPassword.MirrorTo(sec);
            Save();
            RefreshStartupPwState();
            _host.Notify(Loc.T("已关闭启动密码"));
        }
    }

    /// <summary>修改启动密码：验旧 → 设新。</summary>
    private void OnChangeStartupPassword(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        var verify = new StartupPasswordWindow(
            p => StartupPassword.Verify(p),
            Loc.T("修改启动密码"),
            Loc.T("请输入当前的启动密码。"),
            Loc.T("下一步"), Loc.T("取消"));
        verify.ShowDialog();
        if (!verify.Passed) return;

        var dlg = new NewPasswordWindow(
            Loc.T("修改启动密码"),
            Loc.T("设个好记的新密码。下次启动就要用它了。"));
        dlg.ShowDialog();
        if (dlg.Password == null) return;

        if (!StartupPassword.TrySet(dlg.Password, out string err))
        {
            AppDialog.Error(Window.GetWindow(this), err, Loc.T("修改启动密码"));
            return;
        }

        StartupPassword.MirrorTo(_host.Settings.Security);
        Save();
        RefreshStartupPwState();
        _host.Notify(Loc.T("已修改启动密码"));
    }

    /// <summary>把开关拨回真实状态（用户取消流程时用，避免界面撒谎）。</summary>
    private void RevertStartupPwBox(bool on)
    {
        _suppress = true;
        StartupPwBox.IsChecked = on;
        _suppress = false;
    }

    private void OnFeedbackChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.ShowActionFeedback = FeedbackBox.IsChecked == true;
        Save();
        if (FeedbackBox.IsChecked == true)
            _host.Notify(Loc.T("已开启按键反馈：按 Caps+S 试试，角落会显示「左移」"));
    }

    private void OnDoubleTapChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        bool on = DoubleTapBox.IsChecked == true;
        _host.Settings.Global.DoubleTapCaps = on;

        /* ⚠ 必须同步到每个触发键上。
         *
         * Global.DoubleTapCaps / TapCapsAction 只在"首次生成触发键"时被读一次
         * （见 SettingsStore.NormalizeLayers）。老配置里 Triggers 早就存在，
         * 所以光改全局字段的话，这个开关**永远不生效** ——
         * 属于典型的"界面上能改，实际没用"。
         *
         * 真正的开关在「触发键与层」页，这里做同步，保证两处一致。 */
        foreach (var t in _host.Settings.Triggers)
        {
            t.DoubleTapAction = on
                ? (_host.Settings.Global.DoubleTapCapsAction.Length > 0
                    ? _host.Settings.Global.DoubleTapCapsAction
                    : "qbar")
                : "";
        }

        Save();
    }

    private void OnRepeatChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        _host.Settings.Global.RepeatWhenHeld = RepeatBox.IsChecked == true;
        Save();
    }

    /// <summary>
    /// 音量一次调多少。
    ///
    /// 音量走系统音量键，而系统步进是固定的（通常 2%），所以设 5% 时
    /// 实际是"这次 4%、下次 6%"交替 —— 长期平均正好 5%。
    /// 这一点必须在界面上说清楚，否则用户会觉得"设了 5 却总是差 1"。
    /// </summary>
    private void OnVolumeStepChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_host == null || _suppress) return;
        int v = (int)Math.Round(VolumeStepSlider.Value);
        _host.Settings.Global.VolumeStep = v;
        UpdateVolumeStepText(v);
        Save();
    }

    private void OnBrightnessStepChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_host == null || _suppress) return;
        int v = (int)Math.Round(BrightnessStepSlider.Value);
        _host.Settings.Global.BrightnessStep = v;
        UpdateBrightnessStepText(v);
        Save();
    }

    private void UpdateVolumeStepText(int v)
    {
        if (VolumeStepText == null) return;
        int sys = Core.Windows.VolumeService.StepPercent();
        VolumeStepText.Text = (v % sys == 0
            ? Loc.F("{0}%（按住时按系统 {1}% 细调）", v, sys)
            : Loc.F("{0}%（系统步进 {1}%，按整数倍补偿，长期平均正好 {2}%）", v, sys, v))
            + Loc.T(" · 单个键可在「按键映射 → 编辑绑定」里单独设步进");
    }

    private void UpdateBrightnessStepText(int v)
    {
        if (BrightnessStepText == null) return;
        BrightnessStepText.Text = Loc.F("{0}%（按住时按 1% 细调）", v)
            + Loc.T(" · 单个键可在「按键映射 → 编辑绑定」里单独设步进");
    }

    private void OnMouseSpeedChanged(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        int v = (int)MouseSpeedSlider.Value;
        _host.Settings.Global.MouseSpeed = v;
        _host.Context.Mouse.Speed = v;
        MouseSpeedText.Text = Loc.F("{0}（Caps+LAlt+滚轮 可快速调整）", v);
        Save();
    }

    /// <summary>
    /// 「输入文本」用哪种方式打字。
    ///
    /// 之所以做成设置项而不是每个绑定单独选：<c>typeText</c> 的参数就是文字本身，
    /// 改成带模式的结构会破坏所有老配置。全局设置既能解决密码框，也不动存量数据。
    /// </summary>
    private void OnTextInputModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (TextInputBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _host.Settings.Global.TextInputMode = tag;
            Save();
        }
    }

    private void OnTapCapsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (TapCapsBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _host.Settings.Global.TapCapsAction = tag;

            // 同步到所有触发键 —— 否则老配置里改这里完全不生效
            // （全局字段只在首次生成触发键时被读取一次）。
            foreach (var t in _host.Settings.Triggers) t.TapAction = tag;

            Save();
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (ThemeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _host.Settings.Global.Theme = tag;
            ThemeManager.Apply(ThemeManager.Parse(tag));
            Save();
            if (Window.GetWindow(this) is { } w)
            {
                bool dark = tag == "dark" || (tag == "system" && ThemeManager.IsDarkSystem);
                WindowChrome.Apply(w, dark);
            }
        }
    }

    /// <summary>
    /// 出图用：滚到「外观」区。
    ///
    /// 界面语言这一行在页面下部、默认窗口高度下正好落在折叠线以下 ——
    /// 不滚过去，截图回归就永远看不到"语言下拉里到底显示了什么"
    /// （这个下拉曾经显示成 <c>LocLanguage { … }</c>，就是因为没人看见过它）。
    /// </summary>
    internal void ScrollToAppearanceForRender()
    {
        // 按"语言行在内容里的绝对纵坐标"直接定位，比 BringIntoView 可靠：
        // BringIntoView 只保证"露出一部分"，可能把卡片标题顶出可视区。
        if (RootScroll.Content is not System.Windows.FrameworkElement content) return;
        RootScroll.UpdateLayout();
        double y = LanguageBox.TransformToAncestor(content).Transform(default).Y;
        RootScroll.ScrollToVerticalOffset(Math.Max(0, y - 130));
        RootScroll.UpdateLayout();
    }

    /// <summary>出图用：滚回顶部，别把滚动位置留给后面的诊断与下一张图。</summary>
    internal void ResetRenderStateForRender()
    {
        RootScroll.ScrollToTop();
        RootScroll.UpdateLayout();
    }

    /// <summary>
    /// 把内置语言包填进下拉框。条目直接来自 <see cref="Loc.Available"/> ——
    /// 往 Assets/Lang 丢一个新 json，这里就自动多一项，不用改代码。
    /// </summary>
    private void BindLanguageBox(string? current)
    {
        if (!ReferenceEquals(LanguageBox.ItemsSource, Loc.Available))
            LanguageBox.ItemsSource = Loc.Available;

        string code = Loc.Normalize(current);
        LanguageBox.SelectedItem = Loc.Available.FirstOrDefault(l => l.Code == code)
                                   ?? Loc.Available.FirstOrDefault();
    }

    /// <summary>
    /// 切界面语言：**不重启**。
    ///   ① <see cref="Loc.Apply"/> 换字典并通知所有 {loc:T} 绑定 → XAML 自己重画；
    ///   ② <c>Load()</c> 重跑一遍 —— 代码里 set 的文本（数字/步长/路径这些
    ///      带单位的串）不会自己变，得重算；
    ///   ③ 存盘，下次启动直接是这门语言。
    /// </summary>
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (LanguageBox.SelectedItem is not LocLanguage lang) return;
        if (string.Equals(_host.Settings.Global.Language, lang.Code, StringComparison.OrdinalIgnoreCase))
            return;

        _host.Settings.Global.Language = lang.Code;
        Save();

        /* 顺序有讲究：
         * 1) 先 Loc.Apply —— 换字典并把所有 {loc:T} 令牌作废，XAML 上的文字**自己**重画；
         * 2) 再 Refresh —— 代码里拼出来的文本（Loc.T("设置文件：…")这类）刷新时才会重算；
         * 3) 最后重建常驻界面 —— 托盘菜单、动作目录、提示气泡是长生命周期对象，
         *    它们的文本在构造时就定死了，只能靠事件重建。
         * Refresh() 内部自己管 _suppress，这里不要在外层再动它。 */
        Loc.Apply(lang.Code);
        Refresh();
        _host.NotifyLocalizedSurfaces();
    }

    private void OnImportLegacy(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("选择旧版 CapsLock+settings.ini"),
            Filter = Loc.T("CapsLock+ 设置文件|*.ini|所有文件|*.*"),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var result = LegacyIniImporter.Import(dlg.FileName);

            /* 导入 = 整份替换当前配置（按键 / 搜索 / 程序 / 网址 / 热字串）。
             * 这个动作不可撤销，所以先在确认框里把"要导入多少、会覆盖什么"摆清楚 ——
             * 以前是直接替换、然后弹一个"导入完成"，用户点错文件就再也回不去了。 */
            int total = result.Keys + result.Search + result.Run + result.Web + result.HotStrings;
            if (total == 0)
            {
                AppDialog.Info(Window.GetWindow(this),
                    Loc.T("这个文件里没有可导入的设置（按键 / 搜索 / 程序 / 网址 / 热字串都是 0 项），当前配置没有改动。"),
                    Loc.T("导入旧版设置"));
                return;
            }

            string counts = Loc.F("即将导入：按键 {0} 项 / 搜索 {1} 条 / 程序 {2} 个 / 网址 {3} 个 / 文字替换 {4} 条。",
                result.Keys, result.Search, result.Run, result.Web, result.HotStrings);
            string warn = Loc.T("注意：导入会整份替换当前的按键、搜索、程序、网址与热字串（宏、层和触发键不受影响），且不能撤销。确定继续吗？");
            if (!AppDialog.Confirm(Window.GetWindow(this), counts + warn, Loc.T("导入旧版设置"),
                    okText: Loc.T("导入")))
                return;

            _host.Settings = result.Settings;
            _host.SaveSettings();
            _host.ReloadAll();
            Refresh();
            string msg = Loc.F("导入完成：\n· 按键 {0} 项\n· 搜索指令 {1} 条\n· 程序 {2} 个\n", result.Keys, result.Search, result.Run) +
                         Loc.F("· 网址 {0} 个\n· 文字替换 {1} 条", result.Web, result.HotStrings);
            if (result.Warnings.Count > 0)
                msg += Loc.T("\n\n以下项目需要手动确认：\n") + string.Join("\n", result.Warnings.Take(8));
            AppDialog.Info(Window.GetWindow(this), msg, Loc.T("导入旧版设置"), DialogKind.Success);
        }
        catch (Exception ex)
        {
            AppDialog.Error(Window.GetWindow(this), Loc.T("导入失败：") + ex.Message, Loc.T("导入旧版设置"));
        }
    }

    /// <summary>
    /// 「数据与维护 → 导入」：直接复用「同步与备份」页那套导入流程
    /// （选文件 → 解密/密码 → 确认 → 应用 → 结果提示）。
    ///
    /// 为什么不在这里另写一套：导入涉及密码重试、分组提示、悬空宏检查，
    /// 两份实现迟早会走偏。统一到一处，用户在哪个页面点都是同一个行为。
    /// </summary>
    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        _host.ShowMainWindow("backup");
        if (Windows.MainWindow.Instance?.GetPage("backup") is BackupView backup)
            backup.BeginImport();
    }

    private void OnRestartUnelevatedClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        if (!Core.ElevationHelper.IsElevated)
        {
            _host.Notify(Loc.T("当前已经是普通权限"));
            return;
        }

        if (!UI.Windows.AppDialog.Confirm(Window.GetWindow(this),
                Loc.T("以普通权限重启？\n\n· 会关闭「以管理员身份启动」；\n· 有些机器的管理员模式下键盘钩子收不到按键（热键全失效），普通权限才正常；\n· 重启后正在跑的任务会中断。"),
                Loc.T("以普通权限重启"), Windows.DialogKind.Question, okText: Loc.T("重启")))
        {
            return;
        }

        _host.RestartUnelevated();
    }

    private void OnOpenSettingsFile(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            Process.Start(new ProcessStartInfo(AppPaths.SettingsFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host?.Notify(Loc.T("打开失败：") + ex.Message);
        }
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CoreLog.LogPath)!);
            if (!File.Exists(CoreLog.LogPath)) File.WriteAllText(CoreLog.LogPath, "");
            Process.Start(new ProcessStartInfo(CoreLog.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host?.Notify(Loc.T("打开日志失败：") + ex.Message);
        }
    }

    private void OnReload(object sender, RoutedEventArgs e) => _host?.ReloadAll();

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if (!AppDialog.ConfirmDanger(Window.GetWindow(this),
                Loc.T("把全部设置恢复成默认值？\n（按键映射、Qbar 条目、窗口绑定、外观都会被重置）\n\n") +
                Loc.T("启动密码不受影响 —— 要关掉它请用上面的开关（需要验证当前密码）。"),
                Loc.T("重置全部设置"), okText: Loc.T("恢复默认"))) return;
        _host.Settings = DefaultSettings.Create();
        _host.SaveSettings();
        _host.ReloadAll();
        Refresh();
        _host.Notify(Loc.T("已恢复默认设置"));
    }
}
