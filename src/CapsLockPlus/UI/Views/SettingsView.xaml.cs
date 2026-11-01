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
        RefreshConflicts();
        RefreshAppLayerEditor();
        RefreshGestureEditor();
        RefreshSnippetEditor();
        RefreshChordEditor();
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
        var findings = RuleConflicts.Analyze(_host.Settings);

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

        // 层内绑定不在这一页，交给主窗口换页
        if (target == ConflictTarget.Layer)
        {
            _host?.ShowMainWindow("layers");
            return;
        }

        FocusSection(target switch
        {
            ConflictTarget.BaseRemap => RemapSection,
            ConflictTarget.Gesture => GestureSection,
            ConflictTarget.Chord => ChordSection,
            ConflictTarget.Snippet => SnippetSection,
            ConflictTarget.AppLayer => AppLayerSection,
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

    // ---------------- 组合键 ----------------

    /// <summary>
    /// 组合键可用的键：字母/数字 + 常用功能键（同按才需要字母）。
    ///
    /// **必须是懒加载**：它要读 <see cref="GestureKeys"/>，而那个字段声明在文件更后面。
    /// 写成 `static readonly ... = BuildChordKeys()` 会在类型初始化阶段就执行，
    /// 那时 GestureKeys 还是 null，`AddRange(null)` 抛 ArgumentNullException，
    /// 整个 SettingsView 的静态构造失败 —— 表现就是**设置页直接打不开**。
    /// （v4.5.42 就这么翻过一次车，靠 `--render` 才抓出来。）
    /// </summary>
    private static string[]? _chordKeys;

    private static string[] ChordKeys => _chordKeys ??= BuildChordKeys();

    private static string[] BuildChordKeys()
    {
        var list = new List<string>();
        for (char c = 'a'; c <= 'z'; c++) list.Add(c.ToString());
        for (char c = '0'; c <= '9'; c++) list.Add(c.ToString());
        list.AddRange(GestureKeys);
        list.Add("semicolon");
        return list.ToArray();
    }

    private void RefreshChordEditor()
    {
        if (ChordKey1Box == null || _host == null) return;

        foreach (var box in new[] { ChordKey1Box, ChordKey2Box })
        {
            box.Items.Clear();
            foreach (string k in ChordKeys)
            {
                // "semicolon" 是占位名，KeyName 里对应的规范名是 ";" 的键名，兜底用分号键的 vk 名
                string name = k == "semicolon" ? ";" : k;
                if (KeyName.VkOf(name) == null) continue;
                box.Items.Add(new ComboBoxItem { Content = KeyName.Display(name), Tag = name });
            }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        ChordActionBox.Items.Clear();
        foreach (var a in _host.Actions.Items.Where(a => !a.Hidden))
            ChordActionBox.Items.Add(new ComboBoxItem { Content = a.Title, Tag = a.Id });
        if (ChordActionBox.Items.Count > 0) ChordActionBox.SelectedIndex = 0;

        ChordList.Items.Clear();
        foreach (var c in _host.Settings.Global.Chords.ToList())
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };

            var del = new Button
            {
                Content = Loc.T("移除"),
                Padding = new Thickness(10, 3, 10, 3),
                Style = (Style)FindResource("Button.Base"),
            };
            var captured = c;
            del.Click += (_, _) =>
            {
                _host?.Settings.Global.Chords.Remove(captured);
                Save();
                RefreshChordEditor();
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);

            string actionTitle = ActionCatalog.TitleOf(c.Action) ?? c.Action;
            row.Children.Add(new TextBlock
            {
                Text = $"{KeyName.Display(c.Key1)} + {KeyName.Display(c.Key2)} → {actionTitle}",
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("Text.Tertiary"),
            });
            ChordList.Items.Add(row);
        }
    }

    private void OnAddChordClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (ChordKey1Box.SelectedItem is not ComboBoxItem k1 || k1.Tag is not string key1) return;
        if (ChordKey2Box.SelectedItem is not ComboBoxItem k2 || k2.Tag is not string key2) return;
        if (ChordActionBox.SelectedItem is not ComboBoxItem a || a.Tag is not string action) return;

        if (string.Equals(key1, key2, StringComparison.OrdinalIgnoreCase))
        {
            _host.Notify(Loc.T("两个键不能相同"));
            return;
        }

        int conflictsBefore = ConflictCount();
        var list = _host.Settings.Global.Chords;
        var existing = list.FirstOrDefault(c =>
            (string.Equals(c.Key1, key1, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Key2, key2, StringComparison.OrdinalIgnoreCase)) ||
            (string.Equals(c.Key1, key2, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Key2, key1, StringComparison.OrdinalIgnoreCase)));
        if (existing != null) existing.Action = action;      // 同组合覆盖
        else list.Add(new ChordRule { Key1 = key1, Key2 = key2, Action = action });

        Save();
        RefreshChordEditor();
        _host.Notify(WithConflictHint(
            Loc.F("已设置组合键：{0} + {1}", KeyName.Display(key1), KeyName.Display(key2)),
            conflictsBefore));
    }

    // ---------------- 文本片段 ----------------

    private void RefreshSnippetEditor()
    {
        if (SnippetList == null || _host == null) return;

        SnippetList.Items.Clear();
        foreach (var sn in _host.Settings.Global.Snippets.ToList())
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };

            var del = new Button
            {
                Content = Loc.T("移除"),
                Padding = new Thickness(10, 3, 10, 3),
                Style = (Style)FindResource("Button.Base"),
            };
            var captured = sn;
            del.Click += (_, _) =>
            {
                _host?.Settings.Global.Snippets.Remove(captured);
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
                : $"　·　" + Loc.F("仅 {0}", sn.Apps.Trim());

            row.Children.Add(new TextBlock
            {
                Text = $"{sn.Trigger} → {preview}{scope}",
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("Text.Tertiary"),
            });
            SnippetList.Items.Add(row);
        }
    }

    /// <summary>输入框变化时的即时提示：说明这条片段会不会在当前程序里生效。</summary>
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

    private void OnAddSnippetClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;

        string trigger = (SnippetTriggerBox.Text ?? "").Trim();
        string text = SnippetTextBox.Text ?? "";
        string apps = (SnippetAppsBox.Text ?? "").Trim();

        if (!SnippetMatcher.IsValidTrigger(trigger))
        {
            _host.Notify(Loc.T("触发词要以 ; / . 这类符号开头，且不能含空格"));
            return;
        }
        if (text.Length == 0)
        {
            _host.Notify(Loc.T("展开内容不能为空"));
            return;
        }

        int conflictsBefore = ConflictCount();
        var list = _host.Settings.Global.Snippets;
        var existing = list.FirstOrDefault(x => x.Trigger == trigger);
        if (existing != null)
        {
            existing.Text = text;       // 同触发词覆盖
            existing.Apps = apps;
        }
        else
        {
            list.Add(new Snippet { Trigger = trigger, Text = text, Apps = apps });
        }

        SnippetTriggerBox.Text = "";
        SnippetTextBox.Text = "";
        SnippetAppsBox.Text = "";
        Save();
        RefreshSnippetEditor();
        _host.Notify(WithConflictHint(Loc.F("已添加片段：{0}", trigger), conflictsBefore));
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
        foreach (string k in GestureKeys)
            GestureKeyBox.Items.Add(new ComboBoxItem { Content = KeyName.Display(k), Tag = k });
        if (GestureKeyBox.Items.Count > 0) GestureKeyBox.SelectedIndex = 0;

        GestureActionBox.Items.Clear();
        foreach (var a in _host.Actions.Items.Where(a => !a.Hidden))
            GestureActionBox.Items.Add(new ComboBoxItem { Content = a.Title, Tag = a.Id });
        if (GestureActionBox.Items.Count > 0) GestureActionBox.SelectedIndex = 0;

        GestureList.Items.Clear();
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

    private void OnAddGestureClick(object sender, RoutedEventArgs e)
    {
        if (_host == null || _suppress) return;
        if (GestureKeyBox.SelectedItem is not ComboBoxItem kItem || kItem.Tag is not string key) return;
        if (GestureActionBox.SelectedItem is not ComboBoxItem aItem || aItem.Tag is not string action) return;

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
            string text = string.IsNullOrWhiteSpace(l.Name)
                ? Loc.F("第 {0} 层", l.Id)
                : Loc.F("第 {0} 层 · {1}", l.Id, l.Name);
            AppLayerLayerBox.Items.Add(new ComboBoxItem { Content = text, Tag = l.Id });
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
        bool on = KeyRemapMap.Toggle(_host.Settings.Global.BaseRemaps, preset);
        Save();
        RefreshRemapState();
        _host.Notify(WithConflictHint(on
            ? Loc.F("已启用改键：{0}", preset.Title)
            : Loc.F("已取消改键：{0}", preset.Title), conflictsBefore));
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
    private void RefreshRemapState()
    {
        if (RemapStateText == null) return;

        var list = _host?.Settings.Global.BaseRemaps;
        string desc = list == null || list.Count == 0 ? "" : KeyRemapMap.Describe(list);

        RemapStateText.Text = string.IsNullOrEmpty(desc)
            ? Loc.T("当前未启用任何基础改键")
            : Loc.F("当前生效：{0}", desc);
        RemapClearButton.Visibility = list == null || list.Count == 0
            ? Visibility.Collapsed : Visibility.Visible;
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
        PermissionText.Text = Core.ElevationHelper.PermissionSummary();
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
