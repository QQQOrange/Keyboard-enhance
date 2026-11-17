using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Gestures;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.UI.Views;

/// <summary>
/// 鼠标手势页：左边列手势，右边编一条。
///
/// 这一页只做"编辑"，真正的识别在 <see cref="GestureService"/> 里 ——
/// 界面不参与任何钩子逻辑，录制时也只是让服务把下一笔回传过来。
/// </summary>
public partial class GesturesView
{
    private AppHost? _host;
    private readonly List<GestureRow> _rows = new();
    private readonly List<TargetRow> _targetRows = new();
    private MouseGesture _current = new();
    private string _targetApp = "";
    private bool _loading;

    public GesturesView()
    {
        InitializeComponent();
        PreviewCanvas.SizeChanged += (_, _) => DrawPreview();
    }

    public void Initialize(AppHost host)
    {
        _host = host;
        BuildChoices();
        Refresh();
    }

    public void Refresh()
    {
        if (_host == null) return;
        _loading = true;
        try
        {
            var opts = _host.Settings.GestureOptions;
            EnabledBox.IsChecked = opts.Enabled;
            TrailBox.IsChecked = opts.ShowTrail;
            ClickBox.IsChecked = opts.ClickGestures;
            MinMoveBox.Text = opts.MinMove.ToString();
            ThresholdBox.Text = opts.MatchThreshold.ToString("0.00");
            ActivationBox.Text = opts.ActivationDistance.ToString();
            TurnBox.Text = opts.TurnTolerance.ToString("0.00");
            BuildTargets();
            LoadList(selectId: _current.Id);
        }
        finally { _loading = false; }
    }

    // ───────────────────────── 列表 ─────────────────────────

    // ───────────────────── 作用目标 ─────────────────────

    /// <summary>
    /// 重建左侧「作用目标」：全局 + 每个配过手势的应用 + 手动加过但还没配的空目标。
    /// 手势里存的是 exe 全路径，这里只显示程序名，完整路径放在 ToolTip。
    /// </summary>
    private void BuildTargets()
    {
        var settings = _host!.Settings;
        var options = settings.GestureOptions;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in settings.Gestures)
        {
            string key = g.AppPath ?? "";
            counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        var scopePaths = new List<string>();
        foreach (string path in counts.Keys)
            if (!string.IsNullOrWhiteSpace(path)) scopePaths.Add(path);
        foreach (string path in options.Targets)
            if (!string.IsNullOrWhiteSpace(path)
                && !scopePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                scopePaths.Add(path);
        scopePaths.Sort(StringComparer.OrdinalIgnoreCase);

        _targetRows.Clear();
        counts.TryGetValue("", out int globalCount);
        _targetRows.Add(new TargetRow("", Loc.T("全局"), globalCount > 0 ? globalCount.ToString() : "",
            "\uE774", Loc.T("所有程序里都生效、且优先级低于应用手势"),
            string.Equals(_targetApp, "", StringComparison.OrdinalIgnoreCase)));

        foreach (string path in scopePaths)
        {
            counts.TryGetValue(path, out int count);
            _targetRows.Add(new TargetRow(path, AppName(path), count > 0 ? count.ToString() : "",
                "\uE71D", path, string.Equals(_targetApp, path, StringComparison.OrdinalIgnoreCase)));
        }

        // 选中的目标被删光后，退回全局，避免列表莫名其妙停在空页上。
        if (_targetRows.All(r => !r.Selected))
            _targetApp = "";

        TargetList.ItemsSource = null;
        TargetList.ItemsSource = _targetRows;
    }

    private static string AppName(string path)
    {
        try
        {
            /* 优先用文件里的显示名：exe 名常常是 msedge / explorer / cmd，
             * 摆在一列里像日志而不像"应用程序"。FileDescription 是
             * "Microsoft Edge" / "Windows 资源管理器" / "Windows 命令处理程序"，
             * 读不到（绿色版、被删掉）再退回文件名。 */
            string? desc = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();

            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch { return path; }
    }

    private void OnPickTarget(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TargetRow row) return;
        if (string.Equals(row.AppPath, _targetApp, StringComparison.OrdinalIgnoreCase)) return;
        _targetApp = row.AppPath;
        BuildTargets();
        LoadList(selectId: "");
    }

    /// <summary>入口一：从资源管理器挑 exe（和「按应用自动启用层」同一个交互）。</summary>
    private void OnPickTargetFile(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("手势对哪个程序生效"),
            Filter = Loc.T("程序 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true) AddTarget(dlg.FileName);
    }

    /// <summary>入口二：十字准星点选窗口，取它的 exe 全路径。</summary>
    private void OnPickTargetWindow(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var picker = new UI.Windows.WindowPickerWindow();
        if (Window.GetWindow(this) is { } owner) picker.Owner = owner;
        picker.ShowDialog();

        var h = picker.PickedWindow;
        if (h == IntPtr.Zero) return;       // 用户按了 Esc

        var info = Core.Windows.WindowService.Describe(h);
        if (info == null || string.IsNullOrEmpty(info.ExePath))
        {
            _host.Notify(Loc.T("取不到这个窗口的程序，改用「选择程序…」挑 exe"));
            return;
        }
        AddTarget(info.ExePath);
    }

    private void AddTarget(string path)
    {
        if (_host == null || string.IsNullOrWhiteSpace(path)) return;
        var targets = _host.Settings.GestureOptions.Targets;
        if (!targets.Contains(path, StringComparer.OrdinalIgnoreCase)) targets.Add(path);
        _targetApp = path;
        _host.SaveSettings();
        BuildTargets();
        LoadList(selectId: "");
    }

    // ───────────────────────── 列表 ─────────────────────────

    private void LoadList(string selectId)
    {
        string filter = FilterBox.Text.Trim();
        var settings = _host!.Settings;

        TargetTitle.Text = string.IsNullOrEmpty(_targetApp)
            ? Loc.T("全局的手势")
            : Loc.F("{0} 的手势", AppName(_targetApp));

        _rows.Clear();
        foreach (var g in settings.Gestures)
        {
            if (!string.Equals(g.AppPath ?? "", _targetApp, StringComparison.OrdinalIgnoreCase)) continue;
            string title = g.Title(ActionCatalog.TitleOf(g.Action));
            string subtitle = Describe(g);
            string hay = title + " " + subtitle + " " + g.AppPath + " " + g.Note;
            if (filter.Length > 0 && hay.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            /* 以前这里显示「WG」——用户看不出那是"从 WGestures 导入"的标记，
             反而会以为手势只能在 WGestures 里用。现在只在"限定了程序"时
             显示一颗小标签，导入来源只在详情里用一句话说明。 */
            string badge = string.IsNullOrWhiteSpace(g.AppPath)
                ? ""
                : Loc.T("仅此应用");
            _rows.Add(new GestureRow(g, title, subtitle, badge, GlyphFor(g)));
        }

        GestureList.ItemsSource = null;
        GestureList.ItemsSource = _rows;
        EmptyHint.Text = string.IsNullOrEmpty(_targetApp)
            ? Loc.T("全局还没有手势。点右上角「录制新手势」，会记在「全局」下面。")
            : Loc.F("「{0}」还没有手势。点右上角「录制新手势」，会记在这个应用下面。", AppName(_targetApp));
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = _rows.Count == 0
            ? Loc.T("这个目标下还没有手势")
            : Loc.F("这个目标 {0} 条 · 全部 {1} 条", _rows.Count, settings.Gestures.Count);

        var pick = _rows.FirstOrDefault(r => r.Gesture.Id == selectId) ?? _rows.FirstOrDefault();
        if (pick != null)
        {
            _current = pick.Gesture.Clone();
        }
        else
        {
            // 一条都没有时也要把右侧填成"一条合法的默认手势"，
            // 否则下拉框是空的，用户点「保存」才发现什么都没选。
            _current = new MouseGesture
            {
                Id = MouseGesture.NewId(),
                Kind = "stroke",
                Button = GestureButtons.Right,
                AppPath = _targetApp,
            };
        }
        LoadEditor();

        // 列表重建后 WPF 有时会保留上一次的滚动位置，导致第一行只露出半截。
        // 列表是一次性重建的（没有虚拟化），直接回到顶部最稳。
        ListScroll.ScrollToTop();
    }

    private void LoadEditor()
    {
        _loading = true;
        try
        {
            NameBox.Text = _current.Name;
            SelectTag(ButtonBox, _current.Button);
            SelectTag(ChordBox, _current.Chord);
            SelectTag(KindBox, _current.Kind);
            SelectValue(WheelBox, _current.Wheel);
            SelectValue(CornerBox, _current.Corner);
            SelectTag(ActionBox, _current.Action);
            ArgBox.Text = _current.Arg;
            EditorNote.Text = _current.Note;
            EditorNote.Visibility = string.IsNullOrWhiteSpace(_current.Note) ? Visibility.Collapsed : Visibility.Visible;
            UpdateKindRows();
            UpdateArgVisibility();
            DrawPreview();
        }
        finally { _loading = false; }
    }

    private void UpdateKindRows()
    {
        string kind = SelectedTag(KindBox) ?? "stroke";
        StrokeRow.Visibility = kind == "stroke" ? Visibility.Visible : Visibility.Collapsed;
        CornerRow.Visibility = kind == "corner" ? Visibility.Visible : Visibility.Collapsed;
        WheelRow.Visibility = kind == "wheel" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateArgVisibility()
    {
        var info = _host?.Actions.Get(_current.Action);
        bool needs = info?.NeedsArg == true;
        ArgBox.Visibility = needs ? Visibility.Visible : Visibility.Collapsed;
        if (needs && ArgBox.Tag as string != _current.Action)
        {
            ArgBox.Tag = _current.Action;
            ArgBox.Text = _current.Arg;
        }
    }

    private static string Describe(MouseGesture g)
    {
        string button = Loc.T(GestureButtons.Display(g.Button));
        if (!string.IsNullOrWhiteSpace(g.Chord))
            button = Loc.F("{0} + {1}", button, Loc.T(GestureButtons.Display(g.Chord)));
        if (g.Kind == "stroke") g.EnsurePattern();
        return g.Kind switch
        {
            "corner" => Loc.F("{0} · 屏幕{1}", button, CornerName(g.Corner)),
            "wheel" => Loc.F("{0} · 滚轮{1}", button, WheelName(g.Wheel)),
            "click" => Loc.F("{0} · 单击", button),
            "stroke" => Loc.F("{0} · {1}", button,
                !string.IsNullOrEmpty(g.Pattern)
                    ? string.Join(" ", GesturePattern.Arrows(g.Pattern))
                    : DescribeShape(g.Path)),
            _ => Loc.F("{0} · {1}", button, DescribeShape(g.Path))
        };
    }

    private static string CornerName(int corner) => corner switch
    {
        1 => Loc.T("左上角"),
        2 => Loc.T("右上角"),
        3 => Loc.T("左下角"),
        4 => Loc.T("右下角"),
        _ => Loc.T("任意角落")
    };

    private static string WheelName(int wheel) => wheel switch
    {
        -1 => Loc.T("向上"),
        1 => Loc.T("向下"),
        -2 => Loc.T("向左"),
        2 => Loc.T("向右"),
        _ => Loc.T("向上")
    };

    /// <summary>
    /// 用箭头把形状"读"出来：把轨迹压成 8 个采样点，相邻点的方向取 8 邻域，
    /// 连续同向合并，最多显示 4 段。列表里一眼能看出是哪一笔，不用点进去看图。
    /// </summary>
    public static string DescribeShape(IReadOnlyList<int> flat)
    {
        if (flat.Count < 4) return Loc.T("未录制");
        var pts = new List<GestureMath.Pt>(flat.Count / 2);
        for (int i = 0; i + 1 < flat.Count; i += 2) pts.Add(new GestureMath.Pt(flat[i], flat[i + 1]));
        var dense = GestureMath.Resample(GestureMath.Normalize(pts), 9);

        var arrows = new List<string>();
        for (int i = 1; i < dense.Count; i++)
        {
            double dx = dense[i].X - dense[i - 1].X;
            double dy = dense[i].Y - dense[i - 1].Y;
            if (Math.Abs(dx) < 45 && Math.Abs(dy) < 45) continue;
            string a = Arrow(dx, dy);
            if (arrows.Count == 0 || arrows[^1] != a) arrows.Add(a);
        }

        if (arrows.Count == 0) return Loc.T("单击");
        if (arrows.Count > 4) arrows = arrows.Take(4).ToList();
        return string.Join(" ", arrows);
    }

    private static string Arrow(double dx, double dy)
    {
        bool east = dx > 0, west = dx < 0;
        bool south = dy > 0, north = dy < 0;
        double ax = Math.Abs(dx), ay = Math.Abs(dy);
        bool horizontal = ax > ay * 1.8;
        bool vertical = ay > ax * 1.8;
        if (horizontal) return east ? "→" : "←";
        if (vertical) return south ? "↓" : "↑";
        if (east && south) return "↘";
        if (east && north) return "↗";
        if (west && south) return "↙";
        return "↖";
    }

    private static string GlyphFor(MouseGesture g) => g.Kind switch
    {
        "corner" => "\uE7FC",   // Corner
        "wheel" => "\uE962",
        _ => "\uE962"           // Mouse
    };

    // ───────────────────────── 选项 ─────────────────────────

    private void BuildChoices()
    {
        ButtonBox.ItemsSource = GestureButtons.All
            .Select(b => new Choice(b, Loc.T(GestureButtons.Display(b)))).ToList();
        ButtonBox.DisplayMemberPath = "Text";
        ButtonBox.SelectedValuePath = "Value";

        ChordBox.ItemsSource = new List<Choice> { new("", Loc.T("不组合")) }
            .Concat(GestureButtons.All.Select(b => new Choice(b, Loc.T(GestureButtons.Display(b)))))
            .ToList();
        ChordBox.DisplayMemberPath = "Text";
        ChordBox.SelectedValuePath = "Value";

        KindBox.ItemsSource = new List<Choice>
        {
            new("stroke", Loc.T("画一笔")),
            new("corner", Loc.T("甩到角落单击")),
            new("wheel", Loc.T("按住 + 滚轮")),
            new("click", Loc.T("单击这个键"))
        };
        KindBox.DisplayMemberPath = "Text";
        KindBox.SelectedValuePath = "Value";

        WheelBox.ItemsSource = new List<Choice>
        {
            new("-1", Loc.T("向上")),
            new("1", Loc.T("向下")),
            new("-2", Loc.T("向左")),
            new("2", Loc.T("向右"))
        };
        WheelBox.DisplayMemberPath = "Text";
        WheelBox.SelectedValuePath = "Value";

        CornerBox.ItemsSource = new List<Choice>
        {
            new("0", Loc.T("任意角落")),
            new("1", Loc.T("左上角")),
            new("2", Loc.T("右上角")),
            new("3", Loc.T("左下角")),
            new("4", Loc.T("右下角"))
        };
        CornerBox.DisplayMemberPath = "Text";
        CornerBox.SelectedValuePath = "Value";

        var actions = _host!.Actions.Items
            .Where(i => !i.Hidden && i.Id != "doNothing")
            .Select(i => new Choice(i.Id, i.DisplayCategory + " · " + i.Title))
            .ToList();
        ActionBox.ItemsSource = actions;
        ActionBox.DisplayMemberPath = "Text";
        ActionBox.SelectedValuePath = "Value";
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        LoadList(selectId: _current.Id);
    }

    private void OnPickGesture(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GestureRow row) return;
        _current = row.Gesture.Clone();
        LoadEditor();
    }

    private void OnEditorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateKindRows();
    }

    private void OnActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _current.Action = SelectedValue(ActionBox) ?? "";
        UpdateArgVisibility();
    }

    private void OnOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _host == null) return;
        var o = _host.Settings.GestureOptions;
        o.Enabled = EnabledBox.IsChecked == true;
        o.ShowTrail = TrailBox.IsChecked == true;
        o.ClickGestures = ClickBox.IsChecked == true;
        _host.SaveSettings();
    }

    private void OnOptionsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _host == null) return;
        var o = _host.Settings.GestureOptions;
        if (int.TryParse(MinMoveBox.Text, out int min) && min is >= 4 and <= 400) o.MinMove = min;
        if (double.TryParse(ThresholdBox.Text, out double th) && th is >= 0.05 and <= 0.9) o.MatchThreshold = th;
        if (int.TryParse(ActivationBox.Text, out int act) && act is >= 4 and <= 200) o.ActivationDistance = act;
        if (double.TryParse(TurnBox.Text, out double turn) && turn is >= 0 and <= 1) o.TurnTolerance = turn;
        _host.SaveSettings();
    }

    // ───────────────────────── 编辑 ─────────────────────────

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        /* 新建时给个好用的默认值：否则动作下拉是空的，用户画完一笔点保存
         * 只会收到「先给手势选一个动作」，看起来就像"加不进去"。 */
        _current = new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Name = Loc.T("新手势"),
            Button = "right",
            Kind = "stroke",
            Action = "sendKeys",
            AppPath = _targetApp,
            Enabled = true,
        };
        LoadEditor();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var g = _current;
        g.Name = NameBox.Text.Trim();
        g.Button = SelectedValue(ButtonBox) ?? "right";
        g.Chord = SelectedTag(ChordBox) ?? "";
        if (string.Equals(g.Chord, g.Button, StringComparison.OrdinalIgnoreCase)) g.Chord = "";
        g.Kind = SelectedTag(KindBox) ?? "stroke";
        g.Wheel = int.TryParse(SelectedValue(WheelBox), out int w) ? w : -1;
        g.Corner = int.TryParse(SelectedValue(CornerBox), out int c) ? c : 0;
        g.Action = SelectedValue(ActionBox) ?? "";
        g.Arg = ArgBox.Text;
        /* 限定程序不再在手势里选：由左边选中的「作用目标」决定，
         * 编辑时原样保留，避免把应用手势改成全局手势。 */
        g.AppPath = _current.AppPath ?? "";

        if (string.IsNullOrWhiteSpace(g.Action))
        {
            _host.Notify(Loc.T("先给手势选一个动作"));
            return;
        }
        if (g.Kind == "stroke" && !g.HasPath)
        {
            _host.Notify(Loc.T("还没录制笔画 —— 点「按住鼠标键画一笔」"));
            return;
        }

        var existing = _host.Settings.Gestures.FirstOrDefault(x => x.Id == g.Id);
        if (existing == null)
        {
            _host.Settings.Gestures.Add(g.Clone());
        }
        else
        {
            existing.CopyFrom(g);
        }

        _host.SaveSettings();
        _targetApp = g.AppPath ?? "";
        BuildTargets();
        LoadList(selectId: g.Id);
        _host.Notify(Loc.T("手势已保存"));
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var existing = _host.Settings.Gestures.FirstOrDefault(x => x.Id == _current.Id);
        if (existing == null) return;
        _host.Settings.Gestures.Remove(existing);
        _host.SaveSettings();
        _current = new MouseGesture
        {
            Id = MouseGesture.NewId(),
            Kind = "stroke",
            Button = GestureButtons.Right,
            AppPath = _targetApp,
        };
        BuildTargets();
        LoadList(selectId: "");
        _host.Notify(Loc.T("手势已删除"));
    }


    /// <summary>
    /// 录制：弹一个专门的窗口让用户在里面画，窗口自己收鼠标事件。
    ///
    /// 不用全局钩子录，是因为"设置页里点一下按钮 → 去别的窗口上画"这条路
    /// 一遇到焦点变化就断；在自家窗口里画，按的是哪颗键、笔画什么样，全都确定。
    /// </summary>
    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new UI.Windows.GestureRecordWindow
        {
            Owner = Window.GetWindow(this),
            Options = _host.Settings.GestureOptions,
        };

        bool accepted;
        _host.Gestures.Suspended = true;
        try { accepted = dlg.ShowDialog() == true; }
        finally { _host.Gestures.Suspended = false; }

        if (!accepted || dlg.Result == null) return;

        var recorded = dlg.Result;
        _current.Path = recorded.Path;
        _current.Pattern = recorded.Pattern;
        if (recorded.Button.Length > 0) _current.Button = recorded.Button;
        _current.Kind = "stroke";
        if (string.IsNullOrWhiteSpace(_current.AppPath)) _current.AppPath = _targetApp;
        LoadEditor();
        _host.Notify(Loc.T("笔画已录制，记得点保存"));
    }

    private void OnRecordNewClick(object sender, RoutedEventArgs e)
    {
        OnNewClick(sender, e);
        OnRecordClick(sender, e);
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择 WGestures 配置"),
            Filter = "WGestures 配置 (gestures.json)|gestures.json|JSON (*.json)|*.json"
        };
        string guess = @"C:\Users\" + Environment.UserName + @"\AppData\Roaming\WGestures2";
        try
        {
            var latest = System.IO.Directory.Exists(guess)
                ? System.IO.Directory.GetDirectories(guess).OrderByDescending(d => d).FirstOrDefault()
                : null;
            if (latest != null && System.IO.File.Exists(System.IO.Path.Combine(latest, "gestures.json")))
                dlg.FileName = System.IO.Path.Combine(latest, "gestures.json");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "定位 WGestures 配置"); }

        if (dlg.ShowDialog() != true) return;

        try
        {
            var result = WGesturesImporter.ImportFile(dlg.FileName, _host.Settings);
            _host.SaveSettings();
            BuildTargets();
            LoadList(selectId: "");
            _host.Notify(Loc.F("已导入 {0} 条，跳过 {1} 条", result.Imported, result.Skipped));
        }
        catch (Exception ex)
        {
            CoreLog.Error("导入 WGestures 配置失败", ex);
            _host.Notify(Loc.F("导入失败：{0}", ex.Message));
        }
    }

    // ───────────────────────── 预览 ─────────────────────────

    private void DrawPreview()
    {
        PreviewLine.Points.Clear();
        PatternBadge.Text = "";
        if (!_current.HasPath)
        {
            if (!string.IsNullOrEmpty(_current.Pattern))
                PatternBadge.Text = string.Join(" ", GesturePattern.Arrows(_current.Pattern));
            return;
        }

        _current.EnsurePattern();
        PatternBadge.Text = string.Join(" ", GesturePattern.Arrows(_current.Pattern));

        double w = PreviewCanvas.ActualWidth > 10 ? PreviewCanvas.ActualWidth : 340;
        double h = PreviewCanvas.ActualHeight > 10 ? PreviewCanvas.ActualHeight : 118;
        const double pad = 14;

        var pts = GestureMath.Normalize(_current.Points());
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double spanX = Math.Max(1, maxX - minX), spanY = Math.Max(1, maxY - minY);
        double scale = Math.Min((w - pad * 2) / spanX, (h - pad * 2) / spanY);
        // 居中：直线笔画也不会贴着左边画
        double offX = (w - spanX * scale) / 2, offY = (h - spanY * scale) / 2;

        foreach (var p in pts)
            PreviewLine.Points.Add(new Point(offX + (p.X - minX) * scale, offY + (p.Y - minY) * scale));
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private static void SelectTag(ComboBox box, string value)
    {
        foreach (var item in box.Items.OfType<Choice>())
            if (string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static void SelectValue(ComboBox box, int value)
        => SelectTag(box, value.ToString());

    private static string? SelectedValue(ComboBox box) => (box.SelectedItem as Choice)?.Value;

    private static string? SelectedTag(ComboBox box) => (box.SelectedItem as Choice)?.Value;

    private sealed record Choice(string Value, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record GestureRow(MouseGesture Gesture, string Title, string Subtitle, string Badge, string Glyph);

    private sealed record TargetRow(string AppPath, string Title, string Count, string Glyph, string Tooltip, bool Selected);
}
