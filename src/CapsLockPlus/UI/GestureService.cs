using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Gestures;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Windows;
using CapsLockPlus.UI.Windows;

namespace CapsLockPlus.UI;

/// <summary>
/// 鼠标手势服务：从钩子里拿"真实的"鼠标按键与滚轮，攒出一条轨迹，
/// 识别成动作后交给和键盘完全相同的动作管线执行。
///
/// 为什么这件事只能在我们自己的钩子里做：WGestures 那类工具靠低级钩子
/// 观察全局鼠标；而我们**就是**钩子的主人。手势起点是物理按键，
/// 不需要把合成事件再喂回系统（那正是以前"侧键→中键→手势"永远失灵的原因：
/// 合成鼠标事件带着 LLMHF_INJECTED，手势软件一律不理）。
///
/// 线程模型：钩子回调在钩子线程，绘制/执行在界面线程。
/// 共享状态用 <see cref="_gate"/> 保护，钩子回调里只做"记账 + BeginInvoke"。
/// </summary>
public sealed class GestureService : IDisposable
{
    private readonly AppHost _host;
    private readonly object _gate = new();

    private GestureOverlayWindow? _overlay;
    private DispatcherTimer? _poll;
    private DispatcherTimer? _linger;

    private readonly List<GestureMath.Pt> _points = new();
    private GestureMath.Pt _start;
    private GestureMath.Pt _last;
    private int _vk;
    private bool _active;
    private string _appPath = "";
    private MouseGesture? _cornerCandidate;
    private GesturePattern.Tracker? _tracker;

    private Action<MouseGesture>? _recordDone;

    /* 手势专用鼠标钩子。
     *
     * 不能用 Engine.Hook：宿主模式（--hookhost）下真正的钩子在最小进程里，
     * 主进程这颗是没装过的空壳 —— 订阅它等于一条手势也收不到。
     * 这里自己装一颗**只装鼠标**的钩子，和宿主/引擎互不影响。 */
    private KeyboardHook? _gestureHook;

    /* 手势动作执行后的抑制窗：动作里可能再发鼠标键（“点击中键”之类），
     * 那些注入不能被当成新手势的开头。 */
    private long _gestureSuppressUntil;

    public GestureService(AppHost host)
    {
        _host = host;
    }

    /// <summary>是否正在等用户画一笔（设置页的「录制」按钮）。</summary>
    public bool IsRecording { get; private set; }

    /// <summary>正在绘制中（自检和界面用它判断状态，不参与逻辑）。</summary>
    public bool IsTracking
    {
        get { lock (_gate) return _active; }
    }

    public void Attach()
    {
        try
        {
            _gestureHook = new KeyboardHook();
            _gestureHook.GestureButtonEvent += OnButton;
            _gestureHook.GestureWheelEvent += OnWheel;
            _gestureHook.Start(mouseOnly: true);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "安装手势鼠标钩子"); }

        // 第一次启动时把 WGestures 的手势搬过来：用户升级上来就能直接用，
        // 不用先学会"导入"在哪。放到空闲优先级，等主窗口建好再改设置。
        try
        {
            Application.Current?.Dispatcher.BeginInvoke(
                new Action(AutoImportOnce),
                DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "安排手势自动导入"); }
    }

    /// <summary>只在"没导过 + 自己还没手势 + 找得到 WGestures 配置"时导入一次。</summary>
    private void AutoImportOnce()
    {
        try
        {
            var settings = _host.Settings;
            var options = settings.GestureOptions;
            if (options == null || !string.IsNullOrEmpty(options.AutoImportSource)) return;
            if (settings.Gestures.Count > 0) return;

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WGestures2");
            if (!Directory.Exists(root)) return;

            string? dir = Directory.GetDirectories(root)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (dir == null) return;

            string file = Path.Combine(dir, "gestures.json");
            if (!File.Exists(file)) return;

            var (imported, skipped) = WGesturesImporter.ImportFile(file, settings);
            options.AutoImportSource = file;
            _host.SaveSettings();

            if (imported > 0)
                _host.Notify(Loc.F("已从 WGestures 导入 {0} 条手势（{1} 条需手动处理）", imported, skipped));
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "自动导入手势"); }
    }

    public void Dispose()
    {
        try
        {
            if (_gestureHook != null)
            {
                _gestureHook.GestureButtonEvent -= OnButton;
                _gestureHook.GestureWheelEvent -= OnWheel;
                _gestureHook.Dispose();
                _gestureHook = null;
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "卸载手势钩子"); }

        _poll?.Stop();
        _linger?.Stop();
        try { _overlay?.Close(); } catch (Exception ex) { CoreLog.Swallow(ex, "关闭手势覆盖层"); }
    }

    // ───────────────────────── 录制 ─────────────────────────

    /// <summary>
    /// 进入录制：下一笔（任意按键）不执行动作，而是把轨迹回传给界面。
    /// 设置页用它实现「按住画一个」。
    /// </summary>
    public void BeginRecord(Action<MouseGesture> done)
    {
        lock (_gate)
        {
            _recordDone = done;
            IsRecording = true;
            _active = false;
            _points.Clear();
            _tracker = null;
        }
    }

    public void CancelRecord()
    {
        lock (_gate)
        {
            _recordDone = null;
            IsRecording = false;
            _tracker = null;
        }
        HideOverlay(0);
    }

    // ───────────────────────── 钩子入口 ─────────────────────────

    private bool OnButton(int vk, bool down, bool injected)
    {
        if (injected)
        {
            /* 注入的鼠标键默认不算手势开头 —— 否则“点击中键”这类动作会自己
             * 触发一轮新手势，越点越深。唯一的例外：改键把侧键换成中键时，
             * 那颗中键是我们自己发的，但用户的手指确实按了侧键。这种“改键
             * 回声”由 InputSender 发一张一次性通行证，允许它当手势起点。 */
            if (Environment.TickCount64 < Interlocked.Read(ref _gestureSuppressUntil)) return false;

            /* 通行证只在"按下"时用一次；抬起要去收尾已经开始的这一笔，
             * 不能再要一张新票 —— 否则侧键→中键这种一次按压只发一张票的
             * 场景，按下开始了、抬起却进不来，手势永远收不了尾。 */
            if (down)
            {
                if (!InputSender.TakeGesturePass(vk)) return false;
            }
            else
            {
                bool tracking;
                lock (_gate) tracking = _active && _vk == vk;
                if (!tracking) return false;
            }
        }

        var settings = _host.Settings;
        var opts = settings.GestureOptions;

        bool recording;
        lock (_gate) recording = IsRecording;
        if (recording) return HandleRecordButton(vk, down);

        if (opts is not { Enabled: true }) return false;
        if (GestureButtons.FromVk(vk).Length == 0) return false;   // 只处理 5 个鼠标键

        if (down)
        {
            lock (_gate)
            {
                if (_active) return false;      // 画到一半又按别的键：交给系统（例如右键菜单）
            }

            string app = ForegroundExe();
            var candidates = Candidates(settings, vk, app, includeWheel: true);
            if (candidates.Count == 0) return false;

            var (px, py) = CursorPos();
            lock (_gate)
            {
                _active = true;
                _vk = vk;
                _points.Clear();
                _start = new GestureMath.Pt(px, py);
                _last = _start;
                _points.Add(_start);
                _appPath = app;
                _cornerCandidate = CornerCandidate(candidates, px, py, opts.CornerSize);
                _tracker = new GesturePattern.Tracker(opts.ActivationDistance, opts.TurnTolerance, opts.MaxTurnDistance);
                _tracker.Start(px, py);
            }

            var o = opts;
            Ui(() =>
            {
                var overlay = EnsureOverlay();
                overlay.Begin(o.TrailColor, o.TrailWidth);
                overlay.ShowPassive();
                overlay.AddPoint(px, py);
                StartPoll(o);
            });
            return true;
        }

        lock (_gate)
        {
            if (!_active || vk != _vk) return false;
        }

        Finish(execute: true);
        return true;
    }

    private bool HandleRecordButton(int vk, bool down)
    {
        if (down)
        {
            var (px, py) = CursorPos();
            lock (_gate)
            {
                _active = true;
                _vk = vk;
                _points.Clear();
                _start = new GestureMath.Pt(px, py);
                _last = _start;
                _points.Add(_start);
                var recordOpts = _host.Settings.GestureOptions;
                _tracker = new GesturePattern.Tracker(recordOpts.ActivationDistance, recordOpts.TurnTolerance, recordOpts.MaxTurnDistance);
                _tracker.Start(px, py);
            }
            var opts = _host.Settings.GestureOptions;
            Ui(() =>
            {
                var overlay = EnsureOverlay();
                overlay.Begin(opts.TrailColor, opts.TrailWidth);
                overlay.ShowPassive();
                overlay.AddPoint(px, py);
                StartPoll(opts);
            });
            return true;
        }

        lock (_gate)
        {
            if (!_active || vk != _vk) return false;
        }
        Finish(execute: false);
        return true;
    }

    private bool OnWheel(int direction, bool injected, bool horizontal)
    {
        if (injected) return false;

        var settings = _host.Settings;
        if (settings.GestureOptions is not { Enabled: true }) return false;

        int wheel = horizontal ? (direction > 0 ? 2 : -2) : (direction > 0 ? -1 : 1);

        bool active;
        int vk;
        string app;
        lock (_gate)
        {
            active = _active;
            vk = _vk;
            app = _appPath;
        }

        if (!active)
        {
            /* "角落 + 滚轮"本来就不用按任何键：把手贴到屏幕角上拨一下滚轮。
               WGestures 里音量/亮度那几条就是这个形状，导入后 Button 留空，
               所以这里单独给"没按住键"的情况开一条路。 */
            var (cx, cy) = CursorPos();
            int corner = CornerAt(cx, cy, settings.GestureOptions.CornerSize);
            if (corner == 0) return false;

            var cornerHit = settings.Gestures.FirstOrDefault(g =>
                g.Enabled && g.Kind == "wheel" && g.Wheel == wheel &&
                string.IsNullOrWhiteSpace(g.Button) &&
                (g.Corner == 0 || g.Corner == corner) &&
                AppMatches(g, ForegroundExe()));

            if (cornerHit == null) return false;

            Execute(cornerHit, cx, cy);
            ShowLabelOnly(cornerHit, cx, cy);
            return true;
        }
        var hit = settings.Gestures.FirstOrDefault(g =>
            g.Enabled && g.Kind == "wheel" && g.Wheel == wheel &&
            GestureButtons.ToVk(g.Button) == vk && AppMatches(g, app));

        if (hit == null) return false;

        var (px, py) = CursorPos();
        Execute(hit, px, py);
        Finish(execute: false, keepOverlayFor: hit.Title(ActionCatalogTitle(hit)));
        return true;
    }

    // ───────────────────────── 采样与收尾 ─────────────────────────

    private void StartPoll(GestureOptions opts)
    {
        _poll ??= new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        _poll.Tick -= OnPoll;
        _poll.Tick += OnPoll;
        _poll.Start();
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        var (px, py) = CursorPos();
        bool add, grew;
        string pattern;
        lock (_gate)
        {
            if (!_active) return;
            double dx = px - _last.X, dy = py - _last.Y;
            add = dx * dx + dy * dy >= 4;   // 2px 一处，够画圆也够省内存
            if (add)
            {
                _last = new GestureMath.Pt(px, py);
                _points.Add(_last);
            }
            grew = add && (_tracker?.Move(px, py) ?? false);
            pattern = _tracker?.Pattern ?? "";
        }

        if (!add) return;
        if (Settings().GestureOptions.ShowTrail)
            _overlay?.AddPoint(px, py);

        /* 方向串一变就刷新提示。只画字，不重排轨迹，成本很低；
         * 但它决定了"画到一半知不知道自己在画什么"，值得每段都刷。 */
        if (!grew) return;
        int vk;
        string app;
        lock (_gate) { vk = _vk; app = _appPath; }
        _overlay?.SetPattern(pattern, px, py);
        _overlay?.SetSuggestions(SuggestionLines(Settings(), vk, app, pattern), px, py);
    }

    /// <summary>
    /// 收尾：先判定，再决定是执行动作、还是把这一下还原成普通单击。
    ///
    /// 「还原成单击」是右键手势能用的前提 —— 我们吞掉了按下与抬起，
    /// 如果没画出手势又不补发一次右键，用户的右键菜单就永远打不开了。
    /// </summary>
    private void Finish(bool execute, string? keepOverlayFor = null)
    {
        List<GestureMath.Pt> points;
        int vk;
        string app;
        MouseGesture? corner;
        string pattern;
        lock (_gate)
        {
            _active = false;
            points = new List<GestureMath.Pt>(_points);
            _points.Clear();
            vk = _vk;
            app = _appPath;
            corner = _cornerCandidate;
            pattern = _tracker?.Pattern ?? "";
            _tracker = null;
        }
        _poll?.Stop();

        var opts = Settings().GestureOptions;
        double extent = GestureMath.Extent(points);
        var (px, py) = CursorPos();

        MouseGesture? hit = null;
        double score = 1;
        if (execute)
        {
            if (pattern.Length > 0 && extent >= opts.MinMove)
            {
                (hit, score) = PickStroke(Settings(), vk, app, pattern, points, opts);
            }
            else if (corner != null)
            {
                hit = corner;
            }
            else
            {
                /* 没画形状，但配置里可能就是"单击这个键"（WGestures 里
                 * 中键单击=静音 那种）。先找 click，找不到才补发单击。 */
                var clicks = Candidates(Settings(), vk, app, includeWheel: false)
                    .Where(g => g.Kind == "click").ToList();
                hit = clicks.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g.AppPath))
                      ?? clicks.FirstOrDefault();
            }

            if (hit == null && extent < opts.MinMove)
            {
                /* 没画出形状 → 用户只是想点一下。补发一次被吞掉的按键。
                 * 带注入标记，所以不会再被自己的钩子接住。 */
                try { InputSender.Tap(vk); }
                catch (Exception ex) { CoreLog.Swallow(ex, "手势补发单击"); }
            }
        }
        else
        {
            // 录制模式：把轨迹交回界面，不执行
            Action<MouseGesture>? done;
            lock (_gate)
            {
                done = _recordDone;
                _recordDone = null;
                IsRecording = false;
            }
            var recorded = new MouseGesture
            {
                Id = MouseGesture.NewId(),
                Button = GestureButtons.FromVk(vk),
                Kind = "stroke",
                Path = GestureMath.ToFlat(GestureMath.Normalize(points)),
                Pattern = GesturePattern.FromPoints(
                    points, opts.ActivationDistance, opts.TurnTolerance, opts.MaxTurnDistance)
            };
            HideOverlay(0);
            try { done?.Invoke(recorded); }
            catch (Exception ex) { CoreLog.Swallow(ex, "手势录制回调"); }
            return;
        }

        if (hit != null)
        {
            Execute(hit, px, py);
            string title = hit.Title(ActionCatalogTitle(hit));
            ShowLabel(title, px, py);
            CoreLog.Info($"手势命中：{title}（偏差 {score:0.000}）");
        }
        else
        {
            HideOverlay(0);
            if (extent >= opts.MinMove)
                CoreLog.Info($"手势未命中（偏差 {score:0.000}）");
        }
    }

    private void Execute(MouseGesture g, double px, double py)
    {
        if (string.IsNullOrWhiteSpace(g.Action)) return;
        string action = g.Action;
        string arg = g.Arg;
        Interlocked.Exchange(ref _gestureSuppressUntil, Environment.TickCount64 + 700);
        Ui(() =>
        {
            try { _host.InvokeAction(action, arg); }
            catch (Exception ex) { CoreLog.Swallow(ex, "执行手势动作"); }
        });
    }

    private static string ActionCatalogTitle(MouseGesture g)
        => ActionCatalog.TitleOf(g.Action) ?? "";

    // ───────────────────────── 查询 ─────────────────────────

    private static List<MouseGesture> Candidates(AppSettings settings, int vk, string app, bool includeWheel)
    {
        var list = new List<MouseGesture>();
        foreach (var g in settings.Gestures)
        {
            if (!g.Enabled) continue;
            if (GestureButtons.ToVk(g.Button) != vk) continue;
            if (!includeWheel && g.Kind == "wheel") continue;
            if (g.Kind == "stroke")
            {
                g.EnsurePattern();
                if (!g.HasPath && string.IsNullOrEmpty(g.Pattern)) continue;
            }
            if (!AppMatches(g, app)) continue;
            list.Add(g);
        }
        return list;
    }

    /// <summary>
    /// 笔画匹配：应用手势优先。给某个程序单独配过的手势，先只在"这个程序的"里找；
    /// 找不到再退回全局的 —— 否则全局的"↑ 上滚"会抢在浏览器的"↑ 新标签页"前面。
    /// </summary>
    internal static (MouseGesture? Hit, double Score) PickStroke(
        AppSettings settings, int vk, string app, IReadOnlyList<GestureMath.Pt> points, GestureOptions opts)
    {
        string pattern = GesturePattern.FromPoints(
            points, opts.ActivationDistance, opts.TurnTolerance, opts.MaxTurnDistance);
        return PickStroke(settings, vk, app, pattern, points, opts);
    }

    /// <summary>
    /// 方向串优先，形状只在同一串里分胜负。
    ///
    /// 顺序是硬的：应用专属的精确串 → 全局精确串 → 应用专属通配 → 全局通配。
    /// 这样"浏览器里 ↑ 是新标签、桌面 ↑ 是上滚"能同时成立，而通配只是兜底，
    /// 永远不会抢走一条正好写了串的手势。
    /// </summary>
    internal static (MouseGesture? Hit, double Score) PickStroke(
        AppSettings settings, int vk, string app, string pattern,
        IReadOnlyList<GestureMath.Pt> points, GestureOptions opts)
    {
        var strokes = Candidates(settings, vk, app, includeWheel: false)
            .Where(g => g.Kind == "stroke").ToList();
        foreach (var g in strokes) g.EnsurePattern();

        var scoped = strokes.Where(g => !string.IsNullOrWhiteSpace(g.AppPath)).ToList();
        var global = strokes.Where(g => string.IsNullOrWhiteSpace(g.AppPath)).ToList();

        if (!string.IsNullOrEmpty(pattern))
        {
            var hit = Exact(scoped, pattern, points, opts);
            if (hit.Hit != null) return hit;
            hit = Exact(global, pattern, points, opts);
            if (hit.Hit != null) return hit;
        }

        foreach (var bucket in new[] { scoped, global })
        {
            var wild = bucket.Where(g => g.IsWildcard).ToList();
            if (wild.Count == 0) continue;
            var (hit, score) = GestureMath.Match(wild, points, opts.MatchThreshold);
            return (hit ?? wild[0], hit != null ? score : 0);
        }

        return (null, 1);
    }

    private static (MouseGesture? Hit, double Score) Exact(
        List<MouseGesture> bucket, string pattern, IReadOnlyList<GestureMath.Pt> points, GestureOptions opts)
    {
        var same = bucket.Where(g => string.Equals(g.Pattern, pattern, StringComparison.Ordinal)).ToList();
        if (same.Count == 0) return (null, 1);
        if (same.Count == 1) return (same[0], 0);

        var (hit, score) = GestureMath.Match(same, points, opts.MatchThreshold);
        return (hit ?? same[0], hit != null ? score : 0);
    }

    /// <summary>
    /// "画到这里还能往哪拐"：只列比当前串多一个方向的手势，按拐弯偏好排序。
    /// 偏好照 FlowMouse —— 横着画完先提示上/下，竖着画完先提示左/右。
    /// </summary>
    internal static List<string> SuggestionLines(AppSettings settings, int vk, string app, string pattern)
    {
        var list = new List<string>();
        var strokes = Candidates(settings, vk, app, includeWheel: false)
            .Where(g => g.Kind == "stroke").ToList();
        foreach (var g in strokes) g.EnsurePattern();

        var scoped = strokes.Where(g => !string.IsNullOrWhiteSpace(g.AppPath)).ToList();
        var bucket = scoped.Count > 0 ? scoped : strokes.Where(g => string.IsNullOrWhiteSpace(g.AppPath)).ToList();

        string last = pattern.Length == 0 ? "" : pattern[^1..];
        string[] order = last is "↑" or "↓"
            ? new[] { "←", "→", "↑", "↓" }
            : new[] { "↑", "↓", "←", "→" };

        foreach (var dir in order)
        {
            var next = pattern + dir;
            var g = bucket.FirstOrDefault(x => x.Pattern == next);
            if (g != null) list.Add($"{string.Join(" ", GesturePattern.Arrows(next))}  {g.Title(ActionCatalogTitle(g))}");
            if (list.Count >= 4) break;
        }
        return list;
    }

    private static bool AppMatches(MouseGesture g, string foreground)
    {
        if (string.IsNullOrWhiteSpace(g.AppPath)) return true;
        if (string.IsNullOrWhiteSpace(foreground)) return false;
        if (string.Equals(g.AppPath, foreground, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return string.Equals(
                System.IO.Path.GetFileNameWithoutExtension(g.AppPath),
                System.IO.Path.GetFileNameWithoutExtension(foreground),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static MouseGesture? CornerCandidate(List<MouseGesture> candidates, double px, double py, int size)
    {
        int corner = CornerAt(px, py, size);
        if (corner == 0) return null;
        return candidates.FirstOrDefault(g => g.Kind == "corner" && (g.Corner == 0 || g.Corner == corner));
    }

    /// <summary>指针在哪个角：1 左上、2 右上、3 左下、4 右下、0 不在角上。</summary>
    public static int CornerAt(double px, double py, int size)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int right = left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int bottom = top + GetSystemMetrics(SM_CYVIRTUALSCREEN);

        bool nearLeft = px <= left + size;
        bool nearRight = px >= right - size;
        bool nearTop = py <= top + size;
        bool nearBottom = py >= bottom - size;

        if (nearLeft && nearTop) return 1;
        if (nearRight && nearTop) return 2;
        if (nearLeft && nearBottom) return 3;
        if (nearRight && nearBottom) return 4;
        return 0;
    }

    private static string ForegroundExe()
    {
        try
        {
            IntPtr h = WindowService.Foreground;
            if (h == IntPtr.Zero) return "";
            GetWindowThreadProcessId(h, out uint pid);
            return pid == 0 ? "" : WindowService.GetProcessPath(pid);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "取前台程序路径");
            return "";
        }
    }

    private AppSettings Settings() => _host.Settings;

    // ───────────────────────── 覆盖层 ─────────────────────────

    private void Ui(Action action)
    {
        var app = Application.Current;
        if (app == null) return;
        try
        {
            if (app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.BeginInvoke(action);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "手势界面调度"); }
    }

    private GestureOverlayWindow EnsureOverlay()
    {
        if (_overlay != null) return _overlay;
        _overlay = new GestureOverlayWindow();
        _overlay.Closed += (_, _) => _overlay = null;
        return _overlay;
    }

    /// <summary>没有轨迹的一次性触发（角落手势、角落 + 滚轮）：只闪一下名字。</summary>
    private void ShowLabelOnly(MouseGesture g, double px, double py)
    {
        EnsureOverlay();
        ShowLabel(g.Title(ActionCatalogTitle(g)), px, py);
    }

    private void ShowLabel(string text, double px, double py)
    {
        Ui(() =>
        {
            if (_overlay == null) return;
            if (!_overlay.IsVisible) _overlay.ShowPassive();
            _overlay.ShowLabel(text, px, py);
        });
        HideOverlay(Settings().GestureOptions.LabelLingerMs);
    }

    private void HideOverlay(int delayMs)
    {
        _linger?.Stop();
        if (delayMs <= 0)
        {
            Ui(() =>
            {
                _overlay?.Clear();
                _overlay?.Hide();
            });
            return;
        }

        _linger ??= new DispatcherTimer(DispatcherPriority.Background);
        _linger.Stop();
        _linger.Interval = TimeSpan.FromMilliseconds(delayMs);
        _linger.Tick -= OnLinger;
        _linger.Tick += OnLinger;
        _linger.Start();
    }

    private void OnLinger(object? sender, EventArgs e)
    {
        _linger?.Stop();
        Ui(() =>
        {
            _overlay?.Clear();
            _overlay?.Hide();
        });
    }

    // ───────────────────────── Win32 ─────────────────────────

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private static (double X, double Y) CursorPos()
    {
        try
        {
            if (GetCursorPos(out var p)) return (p.X, p.Y);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "读取指针位置"); }
        return (0, 0);
    }
}
