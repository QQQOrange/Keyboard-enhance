using System.Windows;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Localization;
using CapsLockPlus.Core.Qbar;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Update;
using CapsLockPlus.Core.Windows;
using CapsLockPlus.UI.Theme;
using CapsLockPlus.UI.Windows;

namespace CapsLockPlus.UI;

/// <summary>
/// 应用装配中心：把核心引擎、动作目录、托盘、各个窗口串起来。
/// 界面层只通过这里访问功能，方便日后扩展或替换 UI。
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ActionQueue _queue = new();
    private readonly ClipboardWatcher _clipboard;
    private readonly HookWatchdog _watchdog;
    private string _lastClipboardText = "";
    private TrayIcon? _tray;
    private MainWindow? _main;
    private QbarWindow? _qbar;
    private Core.Text.SelectionWatcher? _selectionWatcher;
    private MathBoardWindow? _math;
    private ClipboardHistoryWindow? _clipHistory;
    private bool _disposed;

    public AppSettings Settings { get; set; }
    public HotkeyEngine Engine { get; }
    public ActionCatalog Actions { get; private set; }
    public ActionContext Context { get; }
    public ClipboardService Clipboard { get; }
    public WindowBindingService Bindings { get; }
    public QbarService Qbar { get; }
    public bool Enabled => Engine.Enabled;
    public bool HookAlive => Engine.IsHookAlive;

    /// <summary>正在退出（关闭窗口不再拦截到托盘）。</summary>
    public bool Exiting { get; private set; }

    public event Action? SettingsChanged;
    public event Action<bool>? EnabledChanged;
    public event Action<bool>? CapsHeldChanged;

    public AppHost(AppSettings settings, Dispatcher dispatcher)
    {
        Settings = settings;
        _dispatcher = dispatcher;

        /* 设置序列化必须回到界面线程做（对象图只在这条线程被修改）。
         * Core 层不引用 WPF，所以由这里把 marshal 注入进去。 */
        SettingsStore.Marshal = action =>
        {
            if (_dispatcher.CheckAccess()) action();
            else _dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Background);
        };

        /* 同步服务的设置读取走委托，保证恢复配置后拿到的是最新引用。
         * 第二个参数是"界面线程序列化快照"：备份与指纹会遍历整个对象图，
         * 必须避开用户正在编辑的集合。 */
        Sync = new Core.Sync.SyncService(() => Settings, SettingsJsonSnapshot);

        Clipboard = new ClipboardService
        {
            Enabled = true,
            ClipboardFeatureOn = settings.Global.AllowClipboard,
        };
        Clipboard.Notified += Notify;

        // 用委托而不是直接传 settings：重新加载配置会整体替换 Settings 对象，
// 抓着旧引用会导致"界面显示新绑定、实际按旧配置切窗口"。
        Bindings = new WindowBindingService();
        Bindings.Notified += Notify;

        var mouse = new MouseService
        {
            Speed = settings.Global.MouseSpeed,
            Step = settings.Global.MouseMoveStep,
        };

        Context = new ActionContext
        {
            Settings = () => Settings,
            Clipboard = Clipboard,
            Windows = new WindowActions(),
            WindowBindings = Bindings,
            Mouse = mouse,
            Notify = Notify,
        };
        Actions = ActionCatalog.Create(Context);

        Qbar = new QbarService(() => Settings, () => Clipboard.History, () => Actions.Items);

        Engine = new HotkeyEngine(_queue, () => Settings, InvokeAction);
        // 让引擎知道宏播放器：播放中按 Esc 需要能取消
        Engine.MacroPlayer = Context.MacroPlayer;

        /* 宏播放是异步的：`PlayMacro` 早就返回了，失败只能事后弹。
         * 复用动作失败那条出口（同一个 Toast、同一个 ✗ 前缀），
         * 否则用户看到的是"按了快捷键，一点动静都没有"。 */
        Context.MacroPlayer.Failed += reason =>
            NotifyTranslated(UiText.FailLine(UiText.FailedAt(Loc.T("播放宏"), reason, DateTime.Now)));

        /* 宏里的「执行动作」步骤：转发到同一个 InvokeAction，
         * 于是失败通知、使用统计、按应用层这些既有语义全部自动生效。 */
        Context.MacroPlayer.ActionInvoker = (id, arg) => InvokeAction(id, arg);
        Context.MacroPlayer.ActionTitle = id => ActionCatalog.TitleOf(id) ?? id;

        /* 规则体检：启动时记一条日志。
         * 用户报"某个键时灵时不灵"时，这条日志能立刻把范围缩小到配置冲突。 */
        try
        {
            int findings = Core.Input.RuleConflicts.Analyze(Settings).Count;
            if (findings > 0)
                CoreLog.Info($"规则体检：{findings} 条提示（设置页「规则体检」可查看）");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "规则体检"); }

        /* 按设备区分改键：Raw Input 只用来回答"这次按键来自哪只键盘"。
         * 注册失败不影响任何其它功能（DeviceResolver 保持为空 = 不区分设备）。 */
        _deviceTracker = new KeyboardDeviceTracker();
        if (_deviceTracker.Start())
        {
            Engine.DeviceResolver = (vk, down) => _deviceTracker?.ResolveDevice(vk, down);

            /* 宏录制的第二路来源：远程桌面 / 向日葵这类工具的按键可能进不了低级钩子，
             * 但 Raw Input 看得到。去重在录制器里做（同一个物理按键两路都会到）。 */
            _deviceTracker.KeyObserved += (vk, down, physical) =>
            {
                /* 看护统计**所有**按键：远程会话里每个键都是注入的，如果只看物理键，
                 * 钩子在那类环境下死了也永远不会被重装 —— 表现就是"热键全靠兜底、
                 * 按键还吞不掉"。误报风险由"一场会话最多重装两次"兜住。 */
                Interlocked.Increment(ref _rawKeyCount);
                if (physical) Interlocked.Increment(ref _rawPhysicalCount);

                if (Engine.MacroRecorder is { IsRecording: true } rec) rec.RecordRaw(vk, down);

                /* 选区监听也靠这条路：不再让 SelectionWatcher 自己装第二个低级钩子
                 * （那条钩子会把整条输入链拖住，见 SelectionWatcher.Start 的注释）。 */
                _selectionWatcher?.NotifyKey(vk, down);

                /* 正在等用户按键的窗口（自定义按键捕获）也接这条路：
                 * 它原来只靠临时装的低级钩子，而钩子被系统摘掉时一个键都收不到 ——
                 * 表现就是「点开『按下要发送的键』按什么都没反应」。Raw Input 不受影响。 */
                KeyCaptureSink.Push(vk, down);

                /* 兜底：钩子长时间收不到按键时（远程桌面 / 注入输入），
                 * 由 Raw Input 把按键喂给引擎，否则所有热键一起失灵。
                 * 钩子正常工作时这条路自动关闭（见 ShouldUseRawFallback）。
                 *
                 * ⚠ 宿主模式下本进程没有钩子，`_hook.KeyEventCount` 恒为 0，
                 * 兜底的"静默 10 秒"判据必然成立 —— 一旦喂进来，本进程就会用
                 * 自己那份层状态再执行一遍动作，和宿主转发的动作撞车
                 * （同一个键开出两个不同程序）。所以宿主模式下这条路必须关掉。 */
                if (!_hookHostReady) Engine.FeedFromRaw(vk, down);
            };
            StartHookTrafficWatch();
            StartHookProbe();

            /* 诊断：完整启动完成后再跑捕获窗自检。 */
            if (Core.CaptureSelfTest.Requested)
            {
                Core.CaptureSelfTest.Requested = false;
                _dispatcher.BeginInvoke(new Action(Core.CaptureSelfTest.RunInApp));
            }
        }
        else
            _deviceTracker.Dispose();

        /* 按应用自动启用层：监听前台窗口，命中规则就把对应层设为叠加层。
         * 只记进程名（隐私），命中失败不影响任何既有功能。 */
        _foreground = new ForegroundWatcher();
        _foreground.Changed += OnForegroundChanged;
        if (!_foreground.Start())
        {
            _foreground.Dispose();
            _foreground = null;
        }
        Engine.CapsHeldChanged += held => _dispatcher.BeginInvoke(() => CapsHeldChanged?.Invoke(held));
        // 反馈模式：每次组合键触发都在屏幕上闪一下动作名，
        // 便于确认“按键到底有没有被识别”。
        //
        // 这里**不再**套一层 BeginInvoke：ToastWindow 内部会自己调度到界面线程，
        // 并且只保留最新一条。外面再套一层的话，连按时会排出一长串回调，
        // 每个都去改同一条窗口 —— 等于把"覆盖"退化成"排队覆盖"，
        // 界面线程忙时看起来就是提示慢半拍。
        Engine.ActionTriggered += action =>
        {
            if (!Settings.Global.ShowActionFeedback) return;
            string title = Actions.Get(action)?.Title ?? action;
            ToastWindow.Show(title, "feedback");
        };

        WireEvents();

        // 剪贴板：用 AddClipboardFormatListener 的消息驱动，取代上游 900ms 轮询
        _clipboard = new ClipboardWatcher(OnClipboardChanged);
        _watchdog = new HookWatchdog(Engine, _dispatcher, message => Notify(message));
    }

    private void WireEvents()
    {
        // 注意：这里必须用 lambda 包一层（原因见下）。
        // 而且要把引擎关键字传下去：qbar:<引擎> 动作绑定到按键后，
        // 按下去弹出的 Qbar 要**默认用那个引擎**搜索。
        /* 兜底接管后按键吞不掉：提示一次，避免用户以为"长按 Tab 一边切层一边跳焦点"是 bug。 */
        /* 别名展开时记一次使用次数：只请求防抖保存，不打断当前输入。 */
        Engine.SnippetUsed += () =>
        {
            try { SettingsStore.RequestSave(Settings); } catch { }
        };

        Engine.RawFallbackDegraded += () => _dispatcher.BeginInvoke(() =>
        {
            if (_degradedNotified) return;
            _degradedNotified = true;
            Notify(Loc.T("键盘钩子收不到按键，已改用 Raw Input 触发热键：热键能用，但被吞的键仍会传给当前程序"));
        });

        Context.QbarRequested += engineKey =>
            _dispatcher.BeginInvoke(() => ShowQbar(EnginePrefill(engineKey)));
        Context.MathBoardRequested += () => _dispatcher.BeginInvoke(() => ShowMathBoard());
        Context.SettingsRequested += () => _dispatcher.BeginInvoke(() => ShowMainWindow());
        Context.HelpRequested += () => _dispatcher.BeginInvoke(() => ShowMainWindow("help"));
        Context.ClipboardHistoryRequested += () => _dispatcher.BeginInvoke(() => ShowClipboardHistory());
        Context.ReloadRequested += () => _dispatcher.BeginInvoke(() => ReloadAll());
        Context.ToggleEnabledRequested += () => _dispatcher.BeginInvoke(() => ToggleEnabled());
        Context.ScreenshotRequested += () => _dispatcher.BeginInvoke(() => Context.Screenshot());
        Context.MacrosRequested += () => _dispatcher.BeginInvoke(() => ShowMainWindow("macros"));
    }

    /// <summary>宏录制器（长驻 AppHost，页面重建也不会丢）。</summary>
    public Core.Macro.MacroRecorder MacroRecorder { get; } = new();

    /// <summary>
    /// 开启 / 关闭宏录制。
    /// 开启后引擎会把所有真实按键透传给录制器（不影响正常输入）。
    /// </summary>
    [Obsolete("请直接使用 MacroRecorder 属性")]
    public void EnableMacroRecording(Core.Macro.MacroRecorder recorder)
    {
        Engine.MacroRecorder = recorder;
    }

    /// <summary>
    /// 让「按键映射」页切到指定层（从层管理页跳转时用）。
    /// 页面尚未创建时会记住目标层，等页面初始化时再应用。
    /// </summary>
    public void SelectKeysLayer(int layerId)
    {
        _pendingKeysLayer = layerId;
        // 按键映射页已经创建过就直接切；否则等它 Initialize 时再应用
        if (Windows.MainWindow.Instance?.GetPage("keys") is Views.KeysView kv)
            kv.SelectLayerById(layerId);
    }

    /// <summary>
    /// 待应用的按键映射层。0 = 没有待处理项。
    ///
    /// 用 0 当"无"的哨兵而不是 1：层 ID 从 1 开始，
    /// 早先用 1 当默认值导致"跳转到第 1 层"这个请求被当成空操作吞掉，
    /// 用户从层管理页点第 1 层的"编辑按键"时不会切层。
    /// </summary>
    private int _pendingKeysLayer;

    /// <summary>把 AppHost 记住的目标层应用到 KeysView（页面刚创建时调用）。</summary>
    public void ApplyPendingKeysLayer(Views.KeysView view)
    {
        if (_pendingKeysLayer <= 0) return;
        view.SelectLayerById(_pendingKeysLayer);
        _pendingKeysLayer = 0;
    }

    public void Start()
    {
        /* 启动就先清理"空壳动作"（选了『自定义按键』『输入文本』等分类，
         * 但参数一个都没填的绑定）。
         *
         * ⚠ 这一句以前只挂在 ReloadAll()（托盘「重新加载配置」）里，
         * 而启动根本不走 ReloadAll —— 于是用户那条 caps_2→customKey 空壳记录
         * 在我加了清理逻辑之后**依然**每次重启都还在。必须在启动路径上也调一次。 */
        /* 先看上次有没有正常退出。
         * 崩溃 / 被任务管理器结束后，用户看到的只是"热键好像不管用了"，
         * 而日志里到底有没有线索全靠运气 —— 这个标记能直接告诉他"上次是异常退出"。 */
        /* 诊断模式（--selftest / --render / --dump-bindings …）都是跑完就 Environment.Exit，
         * 它们写下的标记没人去清 —— 那样下次正常启动就会虚假地提示"上次没有正常退出"。
         * 所以诊断模式下不参与这套标记。 */
        bool unclean = !Core.CliFlags.IsDiagnostic(Environment.GetCommandLineArgs().Skip(1))
                       && Core.Health.CrashGuard.MarkRunning(AppPaths.DataDir);

        /* 上一次没正常退出：把上一轮日志的尾巴另存一份，方便直接发给开发者。 */
        if (unclean) Core.Health.CrashGuard.SaveContext(AppPaths.DataDir, CoreLog.LogPath);

        ResetIncompleteCustomKeys();
        if (ClearStaleNotes(Settings) > 0) SettingsStore.RequestSave(Settings);

        /* 前缀符号改成"只认一个"之后，老配置（;mail 之类）要自动迁就一下。 */
        if (InferSnippetPrefix(Settings) is char inferred)
        {
            SettingsStore.RequestSave(Settings);
            CoreLog.Info($"文本片段：按已有触发词把前缀符号设为「{inferred}」");
        }

        /* 先起"钩子宿主"：它是个最小进程（不建界面、不做同步），装钩子的可靠性
         * 远高于本进程 —— 完整进程的 GC 停顿会把钩子回调拖过系统的 300ms 限制，
         * 导致钩子被静默摘掉（实测根因）。拿不到 READY 就退回自己装钩子。 */
        bool hookHostReady = StartHookHost();

        Engine.Start(installHook: !hookHostReady);
        if (Settings.Global.ShowTrayIcon) CreateTray();

        /* 启动善后：上一次如果被强杀/崩溃，系统里可能还留着"按下去没抬起来"的
         * 修饰键或鼠标键（表现是 Win 粘住、点击变成多选、鼠标像一直按着）。
         * 等钩子系统就绪后再补发抬起 —— 太早会和我们自己的注入撞在一起。 */
        /* 渲染/自检/诊断这些无人值守的模式不启用巡检：
         * 它们会在没有真实按键的情况下长时间空转，补发抬起反而会
         * 和截图用的布局泵互相卡住（实测 --render 会卡在 keys 页）。 */
        bool headless = Environment.GetCommandLineArgs().Any(a => a is
            "--render" or "--render-diag" or "--selftest" or "--diag" or "--osd" or "--hooktest");

        var healTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        healTimer.Tick += (_, _) =>
        {
            healTimer.Stop();
            try
            {
                string held = Core.Input.InputSafety.DescribeHeld();
                Core.Input.InputSafety.ReleaseAll();
                if (held.Length > 0) CoreLog.Warn($"启动善后：补发抬起 {held}");
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "启动善后"); }
        };
        if (!headless) healTimer.Start();

        /* 粘键巡检：20 秒没有任何按键、但系统仍认为修饰键/鼠标键按着 → 补发抬起。 */
        var stuckWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        stuckWatch.Tick += (_, _) =>
        {
            long last = Engine.LastKeyActivityMs;
            bool held = Engine.PhysicallyDownCount > 0;

            /* 放到后台线程：补发抬起要进 user32，绝不能占着界面线程。 */
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { Core.Input.InputSafety.HealIfStuck(last, held, out _); }
                catch (Exception ex) { CoreLog.Swallow(ex, "粘键巡检"); }
            });
        };
        if (!headless) stuckWatch.Start();

        /* 上次异常退出：等托盘/OSD 就绪后再提示一句。
         * 不放进启动关键路径（通知失败也不影响功能），也只在真的异常时出现。 */
        /* 启动 60 秒后回收一次工作集：这时启动期的临时对象都已释放，
         * 回收才有意义（刚启动就收会把还要用的页面也赶出去）。 */
        var trimTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        trimTimer.Tick += (_, _) =>
        {
            trimTimer.Stop();

            /* 只在"用户看不见界面"时回收：窗口开着的时候做压缩式 GC 会让输入卡一下，
             * 省下来的那几十 MB 不值得拿手感去换。 */
            if (_main is { IsVisible: true } && _main.WindowState != WindowState.Minimized) return;
            TrimWorkingSet();
        };
        trimTimer.Start();

        if (unclean)
        {
            var tip = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            tip.Tick += (_, _) =>
            {
                tip.Stop();
                Notify(Loc.T("上次没有正常退出。如果热键或剪贴板异常，去「测试」页看诊断。"));
            };
            tip.Start();
        }
        _clipboard.Start();
        _watchdog.Start();
        EnsureWakeWindow();

        /* 「选中即捕获」：监听用户的 Ctrl+C。
         *
         * 这是**主力取词通道**。主动取词（UIA / WM_COPY / SendInput）
         * 在 Electron / Chromium 上全部不可靠 —— 实测确认：
         * Edge 拒绝 UIA 访问、Chromium 过滤合成输入。
         * 但用户**真实按下**的 Ctrl+C 谁都拦不住，
         * 我们只要在旁边看着剪贴板序号变化就能拿到选区。 */
        try
        {
            _selectionWatcher ??= new Core.Text.SelectionWatcher();
            Context.SelectionWatcher = _selectionWatcher;
            if (Environment.GetEnvironmentVariable("CLP_NO_SELECTION") != "1") _selectionWatcher.Start();
        }
        catch (Exception ex) { CoreLog.Warn("选区监听启动失败：" + ex.Message); }

        ApplyStartupSetting();

        /* 预热亮度横条：把窗口和 HWND 提前建好。
         *
         * 冷启动时 new Window() + 建 HWND 要 50~120ms，第一次按亮度键
         * 会明显慢半拍（横条比手慢）。这里放到界面线程空闲时做，
         * 开机那一下的热路径不受影响。 */
        _dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => UI.Windows.OsdWindow.Warmup()));

        /* 顺便把亮度通道也探一次（WMI / DDC 探测要几百毫秒）。
         *
         * 不预热的话，"本次启动后第一次按亮度键"会先卡在探测上，
         * 横条要等半秒才出来。放到后台线程做，开机那一下完全无感。 */
        Task.Run(() =>
        {
            try { Core.Windows.BrightnessService.IsSupported(); }
            catch (Exception ex) { CoreLog.Warn("亮度通道预热失败：" + ex.Message); }
        });

        /* 音频接口必须在**动作队列这条 STA 线程**上建。
         *
         * COM 指针是 apartment 绑定的，让界面线程（设置页要显示"系统步进"）
         * 抢先创建的话，动作队列之后就在跨线程用别人的指针 —— 未定义行为。
         * 这里开机就在队列上问一次，把归属线程和步进缓存定下来，
         * 顺便让第一次按音量键少一次建枚举器的开销。 */
        _queue.Post(() =>
        {
            try { Core.Windows.VolumeService.StepPercent(); }
            catch (Exception ex) { CoreLog.Warn("音频接口预热失败：" + ex.Message); }
        });

        CoreLog.Info($"{AppInfo.ProductName} 已启动（管理员：{ElevationHelper.IsElevated}）");

        /* 设置文件读不出来时，用户看到的是"所有配置都没了"。
         * 日志里有 ERROR，但他不会去看 —— 必须主动说一句，
         * 而且要说清"原文件已另存"，否则他只会以为被删了。 */
        if (SettingsStore.LoadFailure is { } loadFailure)
            Notify(loadFailure);
    }

    private ForegroundWatcher? _foreground;
    private KeyboardDeviceTracker? _deviceTracker;

    /// <summary>降级提示只弹一次。</summary>
    private bool _degradedNotified;


    /// <summary>钩子自检连续失败次数。</summary>
    private int _probeFailures;

    /// <summary>本次运行是否已经尝试过"重启自己救钩子"。</summary>
    private bool _selfRestartTried;

    /// <summary>发给宿主的管理命令（CAPTURE 1/0 之类）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _hookHostCommands = new();

    /// <summary>钩子宿主进程（最小进程，只装钩子跑热键）。</summary>
    private System.Diagnostics.Process? _hookHost;

    /// <summary>宿主是否已就绪（就绪后本进程不再装钩子）。</summary>
    private bool _hookHostReady;


    /// <summary>钩子自检的计时器（首轮失败时要临时提前复查）。</summary>
    private System.Windows.Threading.DispatcherTimer? _probeTimer;

    /// <summary>Raw Input 收到的按键总数（和钩子的事件数对比，用来发现"钩子被摘"）。</summary>
    private long _rawKeyCount;

    /// <summary>其中能识别出物理设备的那些（日志用）。</summary>
    private long _rawPhysicalCount;

    private System.Windows.Threading.DispatcherTimer? _hookWatch;
    private long _lastHookCount, _lastRawCount;
    /// <summary>钩子没收到、但 Raw Input 收到了的按键数（累计到一定量就判定摘钩）。</summary>
    private long _rawSinceHook;

    /// <summary>从什么时候开始"钩子一个都没收到"。</summary>
    private long _hookSilentSince;

    /// <summary>已经重装过几次（一场会话最多两次，避免无限重装刷日志）。</summary>
    private int _hookHeals;

    /// <summary>
    /// 按流量比对，发现"钩子已经被系统摘掉"就重装。
    ///
    /// 低级钩子的老坑：回调一旦超时（默认 300ms），系统会**静默**把它摘掉 ——
    /// 句柄还在、`IsHookAlive` 还是 true、钩子线程还活着（探活也正常），
    /// 但从这一刻起一个按键都收不到。表现就是"宏录制没反应""组合键全失灵"，
    /// 而界面上一切正常。
    ///
    /// Raw Input 不受影响，所以拿两边的增量对比就能判定：
    /// 连续两轮（约 6 秒）Raw Input 收到了键、钩子却零，就重装钩子。
    /// </summary>
    private void StartHookTrafficWatch()
    {
        _hookWatch = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _hookWatch.Tick += (_, _) => CheckHookTraffic();
        _hookWatch.Start();
    }

    /// <summary>
    /// 钩子自检的定时器：启动 3 秒后探一次，之后每 60 秒一次。
    ///
    /// 探针 = 自己敲一个不带标记的 F24，再数钩子有没有收到。
    /// 这是唯一不依赖"用户按键"的判据：钩子被系统摘掉时，
    /// 句柄在、线程活、探活 ping 也正常，只看状态永远发现不了。
    /// 探不到就重装 —— 装回来后热键才真正"能吞键"。
    /// </summary>
    private void StartHookProbe()
    {
        /* 排查用：CLP_PROBE_LOG=1 时每 2 秒探一次，并且**通过也记**。 */
        bool verbose = Environment.GetEnvironmentVariable("CLP_PROBE_LOG") == "1";

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            /* 首次延迟 10 秒：启动那几秒窗口/页面在构建，注入按键不一定能立刻落地，
             * 早探会把"还没准备好"误判成"钩子坏了"—— 以前就是这么误触发重装的。 */
            Interval = TimeSpan.FromSeconds(verbose ? 2 : 10),
        };
        _probeTimer = timer;
        int round = 0;
        timer.Tick += (_, _) =>
        {
            round++;
            if (round == 1 && !verbose)
            {
                timer.Interval = TimeSpan.FromSeconds(60);
            }
            ProbeHookOnce(round == 1 ? "启动自检" : "定期自检", verbose);
        };
        timer.Start();
    }

    /// <summary>
    /// 钩子在这个进程里救不回来时，重启自己。
    ///
    /// 背景（实测）：同一个时刻，跑了几分钟的主进程里新装的钩子一个事件都收不到，
    /// 而新起一个进程装的钩子立刻就能收到 —— 这是进程级的故障，进程内怎么重装都没用。
    /// 用户看到的现象是"长按 Tab 一直跳焦点"（兜底能触发但吞不掉按键）。
    ///
    /// 约束：
    ///   · 只在普通运行时做（诊断/自检模式绝不重启）；
    ///   · 每次运行最多一次，且 15 分钟内不重复（用数据目录里的时间戳文件防抖），
    ///     否则一旦环境持续异常就会无限重启。
    /// </summary>
    private void RestartSelfForHookRecovery(bool force = false)
    {
        try
        {
            if (Core.CliFlags.IsDiagnostic(Environment.GetCommandLineArgs().Skip(1)))
            {
                CoreLog.Warn("诊断模式下不自动重启（避免打断诊断）");
                return;
            }

            string stamp = System.IO.Path.Combine(Core.AppPaths.DataDir, "hook-self-restart.txt");
            if (!force) try
            {
                if (System.IO.File.Exists(stamp) &&
                    DateTime.Now - System.IO.File.GetLastWriteTime(stamp) < TimeSpan.FromMinutes(15))
                {
                    CoreLog.Warn("15 分钟内已经自动重启过一次，这次不再重启（避免循环）");
                    return;
                }
                System.IO.File.WriteAllText(stamp, DateTime.Now.ToString("O"));
            }
            catch { /* 写不了时间戳就不做防抖，按"本次运行最多一次"限制 */ }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            var args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1));
            CoreLog.Warn($"键盘钩子已失效且无法在进程内恢复，自动重启程序以恢复（{exe} {args}）");

            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };

            /* 管理员权限下钩子反复装不上：改成**普通权限**重启。
             * 实测普通权限下钩子收发正常，而提权进程里经常一个事件都收不到
             * （安全软件 / UIPI 拦截提权进程的全局钩子）。 */
            bool wasElevated = Core.ElevationHelper.IsElevated;
            if (wasElevated)
            {
                psi.Environment["CLP_SKIP_ELEVATE"] = "1";
                CoreLog.Warn("当前是管理员权限：改用普通权限重启（管理员模式下键盘钩子收不到按键）");
            }
            foreach (var a in Environment.GetCommandLineArgs().Skip(1)) psi.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(psi);

            _dispatcher.BeginInvoke(() => ExitApp());
        }
        catch (Exception ex) { CoreLog.Warn("自动重启失败：" + ex.Message); }
    }

    /// <summary>
    /// 启动钩子宿主进程并等它 READY（最多 2.5 秒）。
    /// 详见 <see cref="Core.HookHost"/>：宿主是最小进程，不受本进程 GC 停顿影响。
    /// </summary>
    /// <summary>给宿主发一条管理命令（宿主连上来后按序写入）。</summary>
    private void SendHookHostCommand(string command) => _hookHostCommands.Enqueue(command);

    /// <summary>
    /// 宿主的控制通道（本进程是服务端，宿主是客户端）。
    /// 目前只用来通知"开始/结束录键"：录键期间宿主不能吞键，否则触发键录不到。
    /// </summary>
    private void StartHookHostControlPipe(string pipeName)
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new System.IO.Pipes.NamedPipeServerStream(
                        pipeName + "-cmd", System.IO.Pipes.PipeDirection.Out, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte,
                        System.IO.Pipes.PipeOptions.None);
                    server.WaitForConnection();
                    using var w = new System.IO.StreamWriter(server, System.Text.Encoding.UTF8) { AutoFlush = true };
                    while (server.IsConnected)
                    {
                        if (_hookHostCommands.TryDequeue(out string? cmd)) w.WriteLine(cmd);
                        else Thread.Sleep(30);
                    }
                }
                catch { Thread.Sleep(500); }
            }
        });
    }

    private bool StartHookHost()
    {
        /* 自检 / 渲染 / 诊断模式不拉宿主：这些模式跑完就退，
         * 多一个常驻子进程只会在日志里制造噪音，也没有热键需求。 */
        if (Core.CliFlags.IsDiagnostic(Environment.GetCommandLineArgs().Skip(1)))
        {
            CoreLog.Info("诊断模式：不启动钩子宿主");
            return false;
        }

        string pipeName = $"clp-hook-{Environment.ProcessId}";
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--hookhost");
            psi.ArgumentList.Add(pipeName);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());

            _hookHost = System.Diagnostics.Process.Start(psi);
            if (_hookHost == null) return false;

            var ready = new ManualResetEventSlim(false);
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var server = new System.IO.Pipes.NamedPipeServerStream(
                        pipeName, System.IO.Pipes.PipeDirection.In, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte,
                        System.IO.Pipes.PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new System.IO.StreamReader(server, System.Text.Encoding.UTF8);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line == "READY") { ready.Set(); continue; }
                        if (line == "BYE") break;
                        if (Environment.GetEnvironmentVariable("CLP_MIRROR_LOG") == "1")
                            CoreLog.Info($"[镜像] {line}");

                        if (line.StartsWith("KEY\t", StringComparison.Ordinal))
                        {
                            var p3 = line.Split('\t');
                            if (p3.Length >= 3 && int.TryParse(p3[1], out int k) && p3[2] == "1")
                            {
                                KeyCaptureSink.Push(k, true);
                                _dispatcher.BeginInvoke(() => Engine.ApplyRemoteKey(k, true));
                            }
                            else if (p3.Length >= 3 && int.TryParse(p3[1], out int k2))
                            {
                                KeyCaptureSink.Push(k2, false);
                                _dispatcher.BeginInvoke(() => Engine.ApplyRemoteKey(k2, false));
                            }
                            continue;
                        }
                        if (line.StartsWith("TRIG\t", StringComparison.Ordinal))
                        {
                            var p4 = line.Split('\t');
                            bool held = p4.Length > 1 && p4[1] == "1";
                            int layer = p4.Length > 2 && int.TryParse(p4[2], out int l0) ? l0 : -1;
                            string key = p4.Length > 3 ? p4[3] : "";
                            _dispatcher.BeginInvoke(() => Engine.ApplyRemoteTrigger(held, key, layer));
                            continue;
                        }
                        if (line.StartsWith("LAYER\t", StringComparison.Ordinal))
                        {
                            if (int.TryParse(line[6..], out int l1))
                                _dispatcher.BeginInvoke(() => Engine.ApplyRemoteLayer(l1));
                            continue;
                        }

                        if (line == "HOOKFAIL")
                        {
                            CoreLog.Warn("钩子宿主报告：钩子没有生效");
                            if (Core.ElevationHelper.IsElevated && !_selfRestartTried)
                            {
                                _selfRestartTried = true;
                                _dispatcher.BeginInvoke(() => RestartSelfForHookRecovery());
                            }
                            continue;
                        }
                        if (!line.StartsWith("ACT\t", StringComparison.Ordinal)) continue;

                        var parts = line.Split('\t');
                        if (parts.Length < 2) continue;
                        string action = parts[1];
                        string arg = parts.Length > 2 ? parts[2] : "";
                        _dispatcher.BeginInvoke(() =>
                        {
                            try { InvokeAction(action, arg); }
                            catch (Exception ex) { CoreLog.Swallow(ex, "宿主动作"); }
                        });
                    }
                }
                catch (Exception ex) { CoreLog.Warn("钩子宿主管道异常：" + ex.Message); }

                if (_hookHostReady)
                {
                    _hookHostReady = false;
                    Engine.HookDelegatedToHost = false;
                    CoreLog.Warn("钩子宿主已退出，改由本进程自己装钩子");
                    _dispatcher.BeginInvoke(() =>
                    {
                        try { Engine.RestartHook(); } catch (Exception ex) { CoreLog.Swallow(ex, "接管钩子"); }
                    });
                }
            });

            StartHookHostControlPipe(pipeName);

            /* 录键期间：本进程引擎与宿主都停止吞键，并把宿主的按键喂进捕获窗。 */
            KeyCaptureSink.Changed += open =>
            {
                Engine.CaptureMode = open;
                SendHookHostCommand(open ? "CAPTURE 1" : "CAPTURE 0");
                CoreLog.Info(open ? "开始录键：已通知宿主暂停吞键" : "结束录键：宿主恢复吞键");
            };

            if (ready.Wait(2500))
            {
                _hookHostReady = true;
                Engine.HookDelegatedToHost = true;
                CoreLog.Info($"钩子宿主已就绪（PID={_hookHost.Id}）：钩子由它负责");
                return true;
            }

            CoreLog.Warn("钩子宿主 2.5 秒内没有就绪，改由本进程自己装钩子");
            try { _hookHost.Kill(); } catch { }
            _hookHost = null;
            return false;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("启动钩子宿主失败，改由本进程自己装钩子：" + ex.Message);
            try { _hookHost?.Kill(); } catch { }
            _hookHost = null;
            return false;
        }
    }

    private void ProbeHookOnce(string why, bool verbose = false)
    {
        /* 宿主模式下本进程没有钩子，探针无意义。 */
        if (_hookHostReady) return;

        try
        {
            /* 单次失败不算数：启动期注入可能只是慢了一拍。
             * 连续三次都收不到才认为钩子真的坏了 —— 误判一次就要重装一次，
             * 而重装是有代价的（旧线程卸新钩子的竞态就是这么触发的）。 */
            bool ok = false;
            bool inconclusive = false;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var r = Engine.ProbeHook(attempt == 1 ? 600 : 900);
                if (r == true) { ok = true; break; }
                if (r == null) { inconclusive = true; break; }
                Thread.Sleep(150);
            }

            /* 注入本身失败（前台是提权窗口时 UIPI 会拒绝 SendInput）：
             * 这跟钩子坏没坏无关，什么都不能下结论。 */
            if (!ok && inconclusive)
            {
                CoreLog.Warn($"钩子自检无法进行（{why}）：注入按键被系统拒绝，错误码 {Core.Input.InputSender.LastInjectError}" +
                             "（前台是管理员窗口时是正常的），本次不重装");
                return;
            }
            if (ok)
            {
                _probeFailures = 0;
                if (_probeTimer != null && !verbose && _probeTimer.Interval != TimeSpan.FromSeconds(60))
                    _probeTimer.Interval = TimeSpan.FromSeconds(60);
            }
            if (ok && verbose)
            {
                CoreLog.Info($"钩子自检通过（{why}）, 累计事件={Engine.KeyEventCount}");
            }
            if (ok)
            {
                /* 通过时不写日志（每 60 秒一条太吵），失败才记。 */
                _probeFailures = 0;
                return;
            }

            _probeFailures++;

            /* 第一次失败只记一笔、不动手：启动期注入慢一拍很常见，
             * 而重装本身是有风险的（旧线程卸新钩子的竞态）。 */
            if (_probeFailures < 2)
            {
                CoreLog.Info($"钩子自检第一轮没过（{why}）：可能是启动期注入还没落地，5 秒后复查");
                if (_probeTimer != null) _probeTimer.Interval = TimeSpan.FromSeconds(5);
                return;
            }

            /* 管理员权限下连钩子都收不到键，而同一台机器上**普通权限**的 --hooktest 是 6/6 正常
             * （用户实测）—— 这种情况不该再折腾重装，直接换成普通权限重启一次。
             * 这是唯一"零操作"的自救路径：用户不用去设置里关管理员模式。 */
            if (Core.ElevationHelper.IsElevated && !_selfRestartTried)
            {
                _selfRestartTried = true;
                CoreLog.Warn("管理员权限下钩子收不到按键（普通权限下同一台机器是正常的），改用普通权限重启");
                RestartSelfForHookRecovery();
                return;
            }

            CoreLog.Warn($"钩子自检失败（{why}，连续第 {_probeFailures} 次）：收不到自己注入的按键" +
                         $"（句柄非零={Engine.HookInstalled}，线程={Engine.HookThreadState}，" +
                         $"探活={(Engine.PingHook(200) ? "有响应" : "无响应")}，" +
                         $"最长回调={Engine.HookMaxCallbackMs:F0}ms，" +
                         $"回调卡住={Engine.HookCallbackStuckMs}ms(vk=0x{Engine.HookStuckVk:X2})），正在重装");
            Engine.RestartHook();

            /* 重装后立刻复验一次：成功就当场恢复（用户不用等下一个 60 秒），
             * 仍然失败就说明不是"偶发摘钩"，继续计数。 */
            bool recovered = false;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                Thread.Sleep(200);
                if (Engine.ProbeHook(900) == true) { recovered = true; break; }
            }
            if (recovered)
            {
                CoreLog.Info("重装后自检通过，钩子已恢复");
                return;
            }
            CoreLog.Warn("重装后自检仍然失败（这次重装没救回来）");

            /* 连续多次救不回来：这个进程里的钩子已经废了（实测同一时刻新进程的钩子完全正常）。
             * 与其让用户面对"热键时灵时不灵、触发键还漏键"，不如自己重启一次 ——
             * 新进程的钩子是好的，这是唯一被证据支持的恢复手段。 */
            /* ⚠ 以前这里会自动重启程序。用户明确反对："没事就重启，影响正常使用"。
             *
             * 现在改成**只提示、不重启**：钩子救不回来时，热键确实用不了，
             * 但重启与否应该由用户决定 —— 托盘给出提示，用户自己在空闲时重启，
             * 而不是打字打到一半窗口突然没了。
             */
            if (_probeFailures >= 4 && !_degradedNotified)
            {
                _degradedNotified = true;
                CoreLog.Warn("钩子多次重装仍失效：不再自动重启（改为提示用户手动处理）");
                Notify(Loc.T("键盘钩子失效了，热键暂时用不了：可以继续用，也可以稍后重启程序恢复"));
                return;
            }

            /* 连续三次都装不回来：这不是"偶发摘钩"，是环境不允许（安全软件拦截提权进程的钩子
             * 之类）。热键此时靠 Raw Input 还能触发，但**吞不掉**按键，必须让用户知道。 */
            if (_probeFailures >= 3 && !_degradedNotified)
            {
                _degradedNotified = true;
                Notify(Loc.T("键盘钩子一直装不上（可能被安全软件拦截）：热键仍能用，但按下的键也会传给当前程序。建议重启程序或把本程序加入白名单"));
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "钩子自检"); }
    }

    private void CheckHookTraffic()
    {
        long raw = Interlocked.Read(ref _rawKeyCount);
        /* 宿主模式下钩子在**宿主进程**里，本进程的 KeyEventCount 恒为 0 ——
         * 必须用宿主回传的计数，否则会误判"钩子被摘"，把吞键整个关掉。 */
        long hook = _hookHostReady ? Engine.RemoteKeyCount : Engine.KeyEventCount;
        long rawDelta = raw - _lastRawCount;
        long hookDelta = hook - _lastHookCount;
        _lastRawCount = raw;
        _lastHookCount = hook;

        if (hookDelta > 0)
        {
            /* 钩子在工作，全部清零。 */
            /* 钩子确实在工作：清掉一切"怀疑"状态。
             * 也顺手让宿主恢复吞键 —— 老版本可能给它发过 SWALLOW 0。 */
            if (Engine.SwallowUnavailable) Engine.SwallowUnavailable = false;
            if (_hookHostReady) SendHookHostCommand("SWALLOW 1");
            _rawSinceHook = 0;
            _hookSilentSince = 0;
            _hookHeals = 0;
            return;
        }


        /* 钩子一个都没收到：把 Raw Input 期间收到的键累加起来。
         *
         * 不能"本轮没有按键就清零" —— 摘钩之后人往往是断续打字的，
         * 每轮之间空一拍就永远凑不够阈值。 */
        _rawSinceHook += rawDelta;
        if (_rawSinceHook == 0) return;

        long now = Environment.TickCount64;
        if (_hookSilentSince == 0) _hookSilentSince = now;
        if (_rawSinceHook < 6) return;
        if (now - _hookSilentSince < 3000) return;

        /* 只在"真实硬件按键被漏"时重装，而且一场会话最多两次：
         * 远程桌面 / 注入工具的按键根本进不了钩子，但它们不算进这里（不是物理设备）。 */
        if (_hookHeals >= 2)
        {
            _rawSinceHook = 0;
            _hookSilentSince = 0;
            return;
        }
        _hookHeals++;

        CoreLog.Warn($"键盘钩子漏掉了 {_rawSinceHook} 个**物理**按键（Raw Input 收到了）—— " +
                     $"判定钩子被系统摘掉，正在重装（第 {_hookHeals} 次）");
        _rawSinceHook = 0;
        _hookSilentSince = 0;

        /* ⚠ 这里**绝不能**把吞键关掉。
         *
         * 4.5.92 曾在这里发 SWALLOW 0 + SwallowUnavailable=true。而宿主模式的
         * 计数以前恒为 0，健康检查必然误判 → 一打字就"钩子死了" → 主动关掉吞键：
         * Tab 立刻恢复成原生 Tab、层永远进不去。用户感受到的就是
         * "怎么改都还是 Tab 一直跳"，而且日志里连原因都看不到。
         *
         * 现在：只让持有钩子的一侧重装（REHOOK），吞键始终保持开启。
         * 真的救不回来时，下面的探测/回退会把钩子搬到本进程，而不是"自废武功"。 */
        if (_hookHostReady)
        {
            /* 钩子在宿主进程里 —— 让宿主自己重装。 */
            SendHookHostCommand("REHOOK");
        }
        else
        {
            Engine.RestartHook();
        }

        /* 连着两次都救不回来：干脆放弃宿主，由本进程接管（能吞一个是一个）。 */
        if (_hookHeals >= 2 && _hookHostReady)
        {
            /* ⚠ 必须先杀掉宿主再自己装钩子。
             *
             * 否则同一时刻会有**两个**低层钩子、两套引擎在跑：
             * 同一个按键被两边各执行一次（打开两个 QQ、两个 WorkBuddy），
             * 吞键也各吞一半 —— 用户看到的就是"按一下触发一堆、然后键盘全废"。
             * 这是 4.5.9x 一个真实事故的直接原因。 */
            CoreLog.Warn("宿主重装两次仍漏键，改由本进程装钩子（先停掉宿主，避免两个钩子）");
            try { _hookHost?.Kill(); } catch { }
            _hookHost = null;
            _hookHostReady = false;
            Engine.HookDelegatedToHost = false;
            Engine.Start(installHook: true);
            Engine.SwallowUnavailable = false;
        }

        _lastHookCount = Engine.KeyEventCount;
        _lastRawCount = Interlocked.Read(ref _rawKeyCount);
    }

    /// <summary>设备追踪是否可用（Raw Input 注册成功）。界面据此提示"按设备改键不可用"。</summary>
    public bool DeviceTrackingAvailable => _deviceTracker?.IsRunning == true;

    /// <summary>前台变化：更新引擎的叠加层。</summary>
    private void OnForegroundChanged(string process)
    {
        /* 片段要按应用限定，必须每换一次前台就更新 ——
         * 所以放在下面的"层没变就返回"之前。 */
        Engine.ForegroundProcess = process;

        int layer = AppLayerRules.Match(Settings.Global.AppLayers, process);
        if (layer == Engine.AppLayerId) return;

        Engine.AppLayerId = layer;

        /* 切层要有明确反馈：用户按下的键突然变成另一套功能，
         * 没有提示会以为是键盘坏了。 */
        if (layer > 0)
        {
            var layerName = Settings.Layers.FirstOrDefault(l => l.Id == layer)?.Name;
            Notify($"已切到 {LayerName.Display(layer, layerName)}");
        }
        else
        {
            Notify("已回到基础层");
        }

        CoreLog.Info(layer > 0
            ? $"按应用启用叠加层：{process} → 第 {layer} 层（只接管该层绑过的组合）"
            : $"离开按应用叠加层：{process}");
    }

    /// <summary>设置里改了规则后立刻重算，不用等下次切窗口。</summary>
    public void RefreshAppLayer() => _foreground?.Refresh();

    private void ApplyStartupSetting()
    {
        /* 先清理改名前的旧自启项。
         *
         * 软件从 CapsLock+ 改名后，老用户的机器上可能还留着一条指向
         * CapsLockPlus.exe 的自启项 —— 那个文件已经不存在了，开机会弹错误。
         * 这一步必须在"判断是否需要同步自启"之前做。 */
        StartupManager.MigrateLegacyValue();

        /* 自启有两种载体：注册表（普通权限）与计划任务（管理员模式，登录静默提权）。
         * 由"是否管理员模式"决定，两者互斥。
         *
         * 只在**不一致**时才同步：避免每次启动都重建计划任务；
         * 但 exe 被挪过位置也算不一致（任务里记的是绝对路径）。 */
        if (StartupManager.NeedsRepair(Settings.Global.StartWithWindows, Settings.Global.RunAsAdmin))
            StartupManager.Sync(Settings.Global.StartWithWindows, Settings.Global.RunAsAdmin);
    }

    // ---------------- 动作 ----------------
    /// <summary>
    /// 本次动作调用内是否已经弹过提示。
    /// 动作自己通常会解释原因（"没能打开文件夹：xxx"），
    /// 这时不该再补一条，否则同一个失败会弹两次。
    /// </summary>
    private bool _notifiedInCall;

    public bool InvokeAction(string action, string arg)
    {
        _notifiedInCall = false;

        bool ok;
        try
        {
            ok = Actions.Invoke(action, arg);
        }
        catch (Exception ex)
        {
            /* 动作里抛异常以前会一路冒到动作队列线程（最坏情况把队列打死），
             * 现在在这里收口：记日志 + 按失败通知，主流程继续。 */
            CoreLog.Error($"动作执行异常：{action}（{ex.GetType().Name}）", ex);
            NotifyActionFailed(action, ex.Message);
            return false;
        }

        if (ok)
        {
            UsageStats.Record(action);
            CoreLog.Info($"动作：{action}{(string.IsNullOrEmpty(arg) ? "" : " " + arg)}");
            return true;
        }

        // 未注册（未知 id）或执行了但没完成，都算失败
        CoreLog.Warn($"动作未执行：{action}（未注册，或执行了但没完成）");
        NotifyActionFailed(action, null);
        return false;
    }

    /// <summary>
    /// 动作失败通知。
    ///
    /// 三条原则：
    ///   ① **与成功提示同一条出口**（同一个 Toast、同一个开关 `ShowToast`），
    ///      否则用户会觉得"失败时界面没反应"；
    ///   ② 文案统一为 `没能<动作名>：<原因> · <时间>`，一眼能看出是失败；
    ///   ③ **不重复**：动作自己通常已经解释过原因（如"没能打开文件夹：不存在"），
    ///      本次调用内已经弹过就不再补一条，避免同一个失败弹两次。
    /// </summary>
    private void NotifyActionFailed(string action, string? reason)
    {
        if (_notifiedInCall) return;

        string title = ActionCatalog.TitleOf(action) ?? action;

        /* 前缀沿用产品里既有的失败标记「✗ 」（与设置页状态行同一套约定），
         * 这样不用改 Toast 样式也能一眼看出"这条是失败"。 */
        NotifyTranslated(UiText.FailLine(UiText.FailedAt(title, reason, DateTime.Now)));
    }

    /// <summary>诊断开关：把钩子收到的每个按键写进日志。</summary>
    public void EnableKeyTrace() => Engine.EnableKeyTrace();

    /// <summary>
    /// 弹一条 Toast 提示。
    ///
    /// 这里是**显示出口**，翻译放在这一层：全仓几十处调用点
    /// （`Notify("已恢复默认设置")`）都照旧写中文，不必逐处包 Loc.T。
    /// 查不到译文就原样显示，所以带参数的拼串（"已同步 N 项"）最坏也只是保持中文。
    /// </summary>
    public void Notify(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || !Settings.Global.ShowToast) return;
        _notifiedInCall = true;
        /* 直接交给 ToastWindow：它内部会调度到界面线程，且只显示最新一条。
         * 连按音量键时，前一条还没画出来就被后一条顶掉，不会积压。 */
        ToastWindow.Show(Loc.T(message));
    }

    /// <summary>
    /// 已经翻译好的提示文本直接上屏。
    ///
    /// 失败通知是"格式化后的整句"（含动作名、原因、时间），
    /// 再过一遍 <see cref="Loc.T"/> 当键查表没有意义，也会让译文对不上。
    /// </summary>
    private void NotifyTranslated(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || !Settings.Global.ShowToast) return;
        _notifiedInCall = true;
        ToastWindow.Show(message);
    }

    public void ToggleEnabled() => SetEnabled(!Engine.Enabled);

    public void SetEnabled(bool enabled)
    {
        if (Engine.Enabled == enabled) return;
        Engine.Enabled = enabled;

        /* 暂停前把可能"按着没抬起来"的键补一次抬起：
         * 暂停后引擎不再管按键，粘住就没人修了。 */
        if (!enabled) Core.Input.InputSafety.ReleaseAll();

        _tray?.SetEnabled(enabled);
        EnabledChanged?.Invoke(enabled);
        Notify(enabled ? AppInfo.ShortName + " 已启用" : AppInfo.ShortName + " 已暂停");
    }

    // ---------------- 设置 ----------------
    /// <summary>
    /// 保存设置并**广播变更**。
    ///
    /// 之前只写文件、不通知任何人，导致"按键映射页改完绑定，
    /// 已经打开的快捷键速查页、触发键页还显示旧内容"。
    ///
    /// 现在每次保存都触发 <see cref="SettingsChanged"/>，
    /// 各页面自行决定要不要刷新 —— 速查表因此能即时更新。
    /// </summary>
    /// <summary>
    /// 全局搜索选中某条别名时，把触发词放这儿；
    /// 别名页加载后取走并选中 —— 跨页面选中最省事的做法。
    /// </summary>
    public string? PendingAliasTrigger { get; set; }

    /// <summary>取走"待选中的别名"（取一次就清空）。</summary>
    public string? TakePendingAlias()
    {
        string? trigger = PendingAliasTrigger;
        PendingAliasTrigger = null;
        return trigger;
    }

    public void SaveSettings()
    {
        /* 防抖 400ms：参数框是逐字符触发，以前每敲一个字就序列化 + 加密 + 三次磁盘操作。
         * 现在界面线程只记一笔，真正落盘由后台写线程在静默后完成。 */
        SettingsStore.RequestSave(Settings);


        /* 防抖动：打字时每次按键都会调 SaveSettings（参数框的 TextChanged），
         * 直接同步通知会让所有页面跟着重绘，卡顿明显。
         * 这里延迟到下一轮消息循环的空闲时刻再通知，
         * 一轮连续编辑只会刷新一次。 */
        _settingsNotifyPending = true;
        if (_settingsNotifyQueued) return;
        _settingsNotifyQueued = true;

        _dispatcher.BeginInvoke(new Action(() =>
        {
            _settingsNotifyQueued = false;
            if (!_settingsNotifyPending) return;
            _settingsNotifyPending = false;
            SettingsChanged?.Invoke();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool _settingsNotifyPending;
    private bool _settingsNotifyQueued;

    /// <summary>把待保存的设置立刻落盘（退出、备份、切换加密前调用）。</summary>
    public void FlushSettings()
    {
        SettingsStore.FlushPending();
    }

    /// <summary>
    /// 在界面线程上把当前设置序列化成 JSON，供后台线程（备份/指纹）安全使用。
    /// 后台线程自己序列化活对象时，碰到 UI 正在增删的集合会抛"集合已修改"。
    /// </summary>
    public string SettingsJsonSnapshot()
    {
        if (_dispatcher.CheckAccess())
            return System.Text.Json.JsonSerializer.Serialize(Settings, SettingsStore.JsonOptions);

        try
        {
            /* 带超时的 Invoke：界面线程若正卡在别处，宁可 2 秒后自己退化成
             * 直接序列化，也不要让后台备份线程无限期挂着（更不会死锁退出流程）。 */
            var op = _dispatcher.InvokeAsync(() =>
                System.Text.Json.JsonSerializer.Serialize(Settings, SettingsStore.JsonOptions));

            if (op.Task.Wait(TimeSpan.FromSeconds(2)) && op.Task.IsCompletedSuccessfully)
                return op.Task.Result;

            CoreLog.Warn("界面线程 2 秒内没有交出设置快照，退化为直接序列化");
        }
        catch (Exception ex)
        {
            CoreLog.Warn("获取设置快照失败，退化为直接序列化：" + ex.Message);
        }

        return System.Text.Json.JsonSerializer.Serialize(Settings, SettingsStore.JsonOptions);
    }

    /// <summary>
    /// 把第 1 层（基础层）的按键恢复成所选方案的默认值，其它层不动。
    ///
    /// 具体规则与"为什么必须就地改 List"见
    /// <see cref="DefaultSettings.ResetLayerOne"/> —— 抽成静态是为了让自检
    /// 能直接验，不必为了一个纯数据操作起一整套宿主。
    /// </summary>
    /// <summary>
    /// 以普通权限重启（**只在用户明确点击时**调用）。
    ///
    /// 为什么需要：管理员权限下，部分机器（安全软件 / UIPI）会让键盘钩子收不到任何按键，
    /// 热键整体失效。这是权限边界，程序内部修不了 —— 只能回到普通权限。
    ///
    /// 为什么必须走 explorer：从提权进程直接 Process.Start 出来的子进程会**继承**提权令牌，
    /// 只有让已经在普通权限下的 explorer 去拉起，才是真正的降权。
    /// </summary>
    public void RestartUnelevated()
    {
        try
        {
            Settings.Global.RunAsAdmin = false;   // 否则新实例又会自己提权回来
            SaveSettings();

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{exe}\"")
            {
                UseShellExecute = true,
            });

            CoreLog.Warn("用户选择以普通权限重启");
            _dispatcher.BeginInvoke(() => ExitApp());
        }
        catch (Exception ex)
        {
            Notify(Loc.T("重启失败：") + ex.Message);
        }
    }

    /// <summary>
    /// 把工作集还给系统（内存"瘦身"）。
    ///
    /// .NET 启动后保留着大量已经用不到的页面；托盘常驻程序动辄 150 MB 看着不体面。
    /// 这里只做**一次**主动回收（空闲 60 秒 + 最小化到托盘时），
    /// 不做定时回收 —— 那会造成反复缺页，反而更卡。
    /// </summary>
    public void TrimWorkingSet()
    {
        try
        {
            /* 先做一次"压缩 + 回收"。
             * 只调 SetProcessWorkingSetSize 是没用的：页面对象还活着，
             * 系统把页面换出去、下次访问立刻又全换回来，内存数字也不会降。
             * 这里是一次性动作（60 秒空闲 / 收进托盘），阻塞几十毫秒可以接受。 */
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            using var p = System.Diagnostics.Process.GetCurrentProcess();
            Native.TrimWorkingSet(p.Handle);
            CoreLog.Info($"已回收工作集，当前 {p.WorkingSet64 / 1024 / 1024} MB");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "回收工作集"); }
    }

    /// <summary>把指定层恢复成默认（按键映射页的「恢复本页默认设置」用它）。</summary>
    public void ResetLayer(int layerId)
    {
        DefaultSettings.ResetLayer(Settings, layerId, Settings.Global.HotkeyScheme);
        SaveSettings();
    }

    public void ApplyHotkeyScheme(string scheme)
    {
        DefaultSettings.ResetLayerOne(Settings, scheme);
        SaveSettings();
    }

    /// <summary>
    /// 清理"只选了『自定义按键』分类、但从没设置过"的绑定。
    ///
    /// 这类绑定的动作是 customKey，但既没有要发送的键、也没有目标程序 ——
    /// 按下去什么都不会发生。以前它会被原样保存，重启后那个键还显示「自定义按键」，
    /// 用户看到的就是"我没设置成功，它怎么记住了"。
    ///
    /// 现在开机统一改回「无操作」，并写一条日志（可追溯，不会静默改数据）。
    /// </summary>
    internal static int ResetIncompleteCustomKeys(Core.Settings.AppSettings s)
    {
        int fixedCount = 0;
        try
        {
            foreach (var layer in s.Layers)
            {
                foreach (var b in layer.Keys)
                {
                    if (!IsIncompleteAction(b)) continue;

                    b.Action = "doNothing";
                    b.Arg = "";
                    fixedCount++;
                }
            }

            if (fixedCount > 0)
                CoreLog.Warn($"清理了 {fixedCount} 个未完成的自定义按键绑定（没有设过要发送的键），已恢复为「无操作」");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "清理未完成的自定义按键"); }
        return fixedCount;
    }

    /// <summary>
    /// 按已有触发词**推断**前缀符号。
    ///
    /// 为什么需要：前缀从"任意符号都认"改成"只认一个"以后，默认值是 `/`，
    /// 而老用户的触发词多半是 `;mail` 这种 —— 升级后突然全都不展开了。
    /// 这里在启动时看一眼：现有触发词都以同一个符号开头、而当前前缀对不上，
    /// 就把前缀改成那个符号（并写日志），用户不用手动改。
    /// </summary>
    internal static char? InferSnippetPrefix(Core.Settings.AppSettings s)
    {
        var snippets = s.Global?.Snippets;
        if (snippets == null || snippets.Count == 0) return null;

        string prefix = s.Hotstring?.Prefix ?? "";
        if (prefix.Length > 0 && snippets.Any(x => x.Trigger?.StartsWith(prefix, StringComparison.Ordinal) == true))
            return null;                                  // 当前前缀能用，不动

        var common = snippets
            .Select(x => x.Trigger?.TrimStart() is { Length: > 0 } t ? t[0] : '\0')
            .Where(c => c != '\0' && !char.IsLetterOrDigit(c))
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        if (common == null) return null;
        if (common.Key.ToString() == prefix) return null;

        s.Hotstring.Prefix = common.Key.ToString();
        return common.Key;
    }

    /// <summary>
    /// 清除一个键的绑定：动作、参数、**备注**一起清。
    ///
    /// 备注（<see cref="KeyBinding.Note"/>）是"这个绑定叫什么"的说明 ——
    /// 绑定都清了还留着备注，用户看到的就是"文字没了，备注还在"。
    /// </summary>
    internal static void ClearBinding(Core.Settings.KeyBinding b)
    {
        b.Action = "doNothing";
        b.Arg = "";
        b.Note = null;
    }

    /// <summary>
    /// 清理"没有动作却还留着备注"的绑定（历史数据：旧版「清除绑定」只清动作和参数）。
    /// </summary>
    internal static int ClearStaleNotes(Core.Settings.AppSettings s)
    {
        int cleared = 0;
        try
        {
            foreach (var layer in s.Layers)
            {
                foreach (var b in layer.Keys) if (ClearOne(b)) cleared++;
                foreach (var sub in layer.EffectiveSubLayers)
                    foreach (var b in sub.Keys) if (ClearOne(b)) cleared++;
            }

            if (cleared > 0)
                CoreLog.Warn($"清理了 {cleared} 个「没有动作却还留着备注」的绑定");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "清理残留备注"); }
        return cleared;

        static bool ClearOne(Core.Settings.KeyBinding b)
        {
            if (string.IsNullOrWhiteSpace(b.Note)) return false;
            if (b.Action is not (null or "doNothing")) return false;
            if (!string.IsNullOrWhiteSpace(b.Arg)) return false;

            b.Note = null;
            return true;
        }
    }

    /// <summary>
    /// 这个绑定是不是"选了动作、但内容没填"的空壳。
    ///
    /// 这一类动作（输入文本 / 窗口绑定 / 自定义按键 / 直达网址·文件夹·文件）有个共同点：
    /// 光选分类不填参数，按下去什么都不会发生。而编辑器在"选分类"的那一刻就把动作写进配置了，
    /// 于是重启后那个键还显示着「自定义按键」「输入文本」……用户看到的就是
    /// "我根本没设置成功，它怎么记住了"。
    /// </summary>
    internal static bool IsIncompleteAction(Core.Settings.KeyBinding b)
    {
        switch (b.Action)
        {
            case "customKey":
                return string.IsNullOrWhiteSpace(Core.Actions.CustomKeySpec.FromArg(b.Arg).Key);
            case "typeText":
            case "winBind":
            case "webPage":
            case "openFolder":
            case "openFile":
                return string.IsNullOrWhiteSpace(b.Arg);
            default:
                return false;
        }
    }

    /// <summary>启动时清理，并在真的改动过时落盘。</summary>
    private void ResetIncompleteCustomKeys()
    {
        if (ResetIncompleteCustomKeys(Settings) > 0) SettingsStore.RequestSave(Settings);
    }

    public void ReloadAll()
    {
        Settings = SettingsStore.Load();
        ResetIncompleteCustomKeys();
        ThemeManager.Apply(ThemeManager.Parse(Settings.Global.Theme));
        // 主题一变就作废 Qbar 的样式缓存，否则已打开的面板还留着旧配色
        _qbar?.InvalidateStyle();
        Context.Clipboard.ClipboardFeatureOn = Settings.Global.AllowClipboard;
        Context.Mouse.Speed = Settings.Global.MouseSpeed;
        Context.Mouse.Step = Settings.Global.MouseMoveStep;
        Engine.Suspended = false;
        RebuildActions();
        ApplyStartupSetting();
        // 选区监听器在 Start() 里已启动；RebuildActions 会重建 Context，
        // 这里只需把引用补回去，否则取词会退回主动取词那条不可靠的路。
        if (_selectionWatcher != null) Context.SelectionWatcher = _selectionWatcher;

        _tray?.SetEnabled(Engine.Enabled);
        _qbar?.InvalidateStyle();
        _main?.ReloadFromSettings();
        SettingsChanged?.Invoke();
        Notify("配置已重新加载");
    }

    // ---------------- 窗口 ----------------
    private System.Windows.Interop.HwndSource? _wakeWindow;

    /// <summary>
    /// 建一个**隐藏的顶层窗口**，专门接收"第二次启动本程序"的唤醒消息。
    ///
    /// 为什么需要它：默认启动不显示主窗口（只驻留托盘），这时
    /// `Process.MainWindowHandle` 是 0（它只认可见窗口），于是
    /// <see cref="SingleInstance.ActivateExisting"/> 走"广播消息"那条路 ——
    /// 而早先**没有任何窗口处理这个消息**：用户双击第二次，
    /// 等 6 秒（互斥体超时），然后什么都没有发生。
    ///
    /// 注意不能用 message-only 窗口（HWND_MESSAGE）：广播消息只发给顶层窗口，
    /// 消息窗口收不到。这里建的是普通顶层窗口，只是从来不 Show。
    /// </summary>
    private void EnsureWakeWindow()
    {
        if (_wakeWindow != null) return;
        try
        {
            var p = new System.Windows.Interop.HwndSourceParameters("KeyboardEnhance.Wake")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,                 // 不要 WS_VISIBLE
                ExtendedWindowStyle = 0x80,      // WS_EX_TOOLWINDOW：不进 Alt+Tab
            };
            _wakeWindow = new System.Windows.Interop.HwndSource(p);
            _wakeWindow.AddHook(OnWakeMessage);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("创建唤醒窗口失败（再启动一次不会自动打开主窗口）：" + ex.Message);
        }
    }

    private IntPtr OnWakeMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != SingleInstance.WmShowWindow) return IntPtr.Zero;
        handled = true;
        CoreLog.Info("收到「再打开一次」的唤醒消息，显示主窗口");
        ShowMainWindow();
        return IntPtr.Zero;
    }

    public void ShowMainWindow(string? page = null)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_main == null)
            {
                _main = new MainWindow(this);
                /* 窗口被真的关掉之后（用户关掉「关闭时最小化到托盘」），
                 * 这个字段必须清空：WPF 的已关闭窗口不能再 Show()，
                 * 否则托盘图标、托盘菜单、「设置」动作从此全部弹
                 * "Cannot set Visibility or call Show after a Window has closed"，
                 * 只有重启进程才能恢复。 */
                _main.Closed += (_, _) => _main = null;
            }
            if (page != null) _main.Navigate(page);
            _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
            _main.Topmost = true;
            _main.Topmost = false;
            _main.Focus();
        });
    }

    /// <summary>
    /// 只在窗口隐藏 / 最小化之后回收内存。
    ///
    /// 注意：不要对“正在显示”的窗口调用 SetProcessWorkingSetSize ——
    /// WPF 随后会重新访问被换出的渲染页，触发大量缺页中断，
    /// 实测空闲 CPU 会从 0% 涨到 15% 左右，得不偿失。
    /// </summary>
    private void TrimWorkingSetWhenIdle()
    {
        if (_main is { IsVisible: true } && _main.WindowState != WindowState.Minimized) return;
        MemoryTrimmer.Trim();
    }

    public void ShowQbar(string? prefill = null)
    {
        /* 和计算器同一个坑：窗口被**关闭**过之后不能再 Show()，
         * 而 Qbar 的字段没人清空 —— 于是"关掉一次以后 Caps+Q 再也唤不出来"。
         * 现在关掉就把字段清空，并且整段兜住异常：宁可重建一次，也不能什么都不弹。 */
        try
        {
            if (_qbar == null)
            {
                _qbar = new QbarWindow(this);
                _qbar.Closed += (_, _) =>
                {
                    CoreLog.Info("Qbar：窗口已关闭，下次重新创建");
                    _qbar = null;
                };
            }
            _qbar.Open(prefill);
        }
        catch (Exception ex)
        {
            CoreLog.Error("Qbar 打开失败（将重建窗口再试一次）", ex);
            _qbar = null;
            try
            {
                _qbar = new QbarWindow(this);
                _qbar.Closed += (_, _) => _qbar = null;
                _qbar.Open(prefill);
            }
            catch (Exception ex2)
            {
                CoreLog.Error("Qbar 重建后仍然打不开", ex2);
                Notify(Loc.T("Qbar 打不开，详情见日志"));
            }
        }
    }

    /// <summary>
    /// 把动作带的引擎关键字转成 Qbar 的预填文本。
    ///
    /// 面板本身就是"关键字 + 空格 + 内容"的语法（bd 关键词 = 百度搜），
    /// 所以预填成 "bd " 让用户直接接着打字即可，不必新增一套输入协议。
    /// </summary>
    private string? EnginePrefill(string? engineKey)
    {
        if (string.IsNullOrWhiteSpace(engineKey)) return null;
        string key = engineKey.Trim();
        // 只为真实存在的引擎生成预填，别把乱码塞进输入框
        return Settings.Search.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                                        && !x.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
            ? key + " "
            : null;
    }

    public void ShowMathBoard()
    {
        /* 窗口被用户关掉（Esc / 标题栏 ×）之后不能再复用：Show() 一个已关闭的
         * 窗口会抛异常。而 `IsLoaded` 在关闭之后**仍然是 true**（WPF 不复位它），
         * 拿它当"还能用吗"的判据等于永远判"能用" ——
         * 实际表现是：计算器按 Esc 关掉一次，之后 Caps+F2 再也打不开，
         * 只会弹一个"出错了：…"。
         * 正确做法是让 Closed 把字段清空，下次自然新建。 */
        if (_math == null)
        {
            _math = new MathBoardWindow();
            _math.Closed += (_, _) => _math = null;
        }
        _math.Show();
        _math.Activate();
    }

    public void ShowClipboardHistory()
    {
        if (_clipHistory == null)
        {
            _clipHistory = new ClipboardHistoryWindow(this);
            _clipHistory.Closed += (_, _) => _clipHistory = null;
        }
        _clipHistory.RefreshItems();
        _clipHistory.Show();
        _clipHistory.Activate();
    }

    public void ShowOnboarding()
    {
        var w = new OnboardingWindow(this);
        w.ShowDialog();
    }

    /// <summary>启动时发现新版本：弹窗询问是否下载（由“自动更新”开关控制）。</summary>
    public void ShowUpdatePrompt(UpdateInfo info)
    {
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                var w = new UpdatePromptWindow(this, info);
                w.ShowDialog();
            }
            catch (Exception ex)
            {
                CoreLog.Error("显示更新提示失败", ex);
            }
        });
    }

    /// <summary>启动后延迟检查更新，避免和界面初始化抢资源。</summary>
    public void ScheduleUpdateCheck()
    {
        if (!Settings.Global.AutoUpdate) return;
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromSeconds(6),
        };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                var service = new UpdateService();
                var result = await service.CheckAsync(Settings.Global.GitHubToken);
                Settings.Global.LastUpdateCheck = DateTime.Now;
                SaveSettings();
                if (!result.Success || !result.IsNewer || result.Update == null) return;
                if (result.Update.Version == Settings.Global.SkippedVersion) return;
                ShowUpdatePrompt(result.Update);
            }
            catch (Exception ex)
            {
                CoreLog.Warn("自动检查更新失败：" + ex.Message);
            }
        };
        timer.Start();
    }

    /// <summary>供离屏渲染使用：创建主窗口但不走“显示并置前”的流程。</summary>
    public MainWindow CreateMainWindow() => new(this);

    // ---------------- WebDAV 同步 ----------------

    /// <summary>同步服务（界面与自动同步共用）。</summary>
    public Core.Sync.SyncService Sync { get; private set; } = null!;

    /// <summary>最近一次同步的提示，供界面显示。</summary>
    public string LastSyncMessage { get; private set; } = "";

    /// <summary>
    /// 启动同步：延后几秒再跑，等界面和钩子都就绪。
    /// 联网在后台线程，不阻塞界面。
    /// </summary>
    public void ScheduleStartupSync()
    {
        var s = Settings.Sync;
        if (!s.SyncOnStart)
        {
            CoreLog.Info("启动同步：未开启，跳过");
            return;
        }

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await RunStartupSyncAsync();
        };
        timer.Start();
    }

    private async Task RunStartupSyncAsync()
    {
        try
        {
            CoreLog.Info("启动同步：开始");
            var result = await Task.Run(() => Sync.SyncOnStartAsync()).ConfigureAwait(true);
            LastSyncMessage = result.Message;
            if (!string.IsNullOrEmpty(result.Message))
            {
                CoreLog.Info($"启动同步：{result.Message}");
                if (Settings.Global.ShowToast) Notify("同步：" + result.Message);
            }
            SaveSettings();
            SyncCompleted?.Invoke(result);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("启动同步异常：" + ex.Message);
        }
    }

    /// <summary>退出时同步（由 App.OnExit 调用）。</summary>
    public Core.Sync.SyncResult? RunExitSync()
    {
        try
        {
            if (!Settings.Sync.SyncOnExit) return null;
            CoreLog.Info("退出同步：开始");
            var r = Sync.SyncOnExit();
            CoreLog.Info($"退出同步：{r.Message}");
            SaveSettings();
            return r;
        }
        catch (Exception ex)
        {
            return Core.Sync.SyncResult.Fail(ex.Message);
        }
    }

    /// <summary>同步完成事件（界面刷新用）。</summary>
    public event Action<Core.Sync.SyncResult>? SyncCompleted;

    // ---------------- 自动备份 ----------------

    private System.Windows.Threading.DispatcherTimer? _autoBackupTimer;
    private bool _autoBackupBusy;

    /// <summary>
    /// 重建自动备份定时器。
    ///
    /// 由设置页在开关/间隔改动后调用，也在启动时调一次。
    /// 间隔很短时（最短 5 分钟）不会真的每 5 分钟写一份 ——
    /// 写之前会比对配置文件的写入时间，没变化就跳过，
    /// 否则用户开着软件不用，一天也能堆出几百份一模一样的备份。
    /// </summary>
    public void RestartAutoBackupTimer()
    {
        _autoBackupTimer?.Stop();
        _autoBackupTimer = null;

        var s = Settings.Sync;
        if (!s.AutoBackup)
        {
            CoreLog.Info("自动备份：未开启");
            return;
        }
        if (string.IsNullOrWhiteSpace(s.BackupPassword))
        {
            CoreLog.Warn("自动备份：没设备份密码，无法加密，已跳过");
            return;
        }

        int minutes = Math.Clamp(s.AutoBackupMinutes, 5, 1440);
        _autoBackupTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(minutes),
        };
        _autoBackupTimer.Tick += async (_, _) => await RunAutoBackupAsync().ConfigureAwait(true);
        _autoBackupTimer.Start();
        CoreLog.Info($"自动备份：每 {minutes} 分钟一次");
    }

    private async Task RunAutoBackupAsync()
    {
        if (_autoBackupBusy) return;
        _autoBackupBusy = true;
        try
        {
            var r = await Task.Run(() => Sync.AutoBackupAsync()).ConfigureAwait(true);
            LastSyncMessage = r.Message;
            if (r.Ok)
            {
                CoreLog.Info("自动备份：" + r.Message);
                SaveSettings();
                SyncCompleted?.Invoke(r);
            }
            else
            {
                CoreLog.Warn("自动备份：" + r.Message);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("自动备份异常：" + ex.Message);
        }
        finally
        {
            _autoBackupBusy = false;
        }
    }

    /// <summary>动作表重建后触发（界面需要刷新功能下拉）。</summary>
    public event Action? ActionsChanged;

    /// <summary>
    /// 重建动作表。
    ///
    /// 必须能重建：宏动作（macro:&lt;id&gt;）是在 <c>ActionCatalog.Create</c> 时
    /// 按当时的宏列表注册的。动作表只在启动时建一次的话，
    /// **用户新建的宏永远不会出现在"功能"下拉里** ——
    /// 这正是用户反馈的"按键绑不了宏"。
    ///
    /// 所以任何会改变动作集合的操作（增删宏、重新加载配置）之后都要调它。
    /// </summary>
    public void RebuildActions()
    {
        Actions = ActionCatalog.Create(Context);
        ActionsChanged?.Invoke();
    }

    /// <summary>手动触发一次同步并通知界面。</summary>
    public async Task<Core.Sync.SyncResult> RunSyncNowAsync(bool upload)
    {
        var r = upload
            ? await Sync.UploadAsync().ConfigureAwait(true)
            : await Sync.DownloadAsync().ConfigureAwait(true);
        LastSyncMessage = r.Message;
        SaveSettings();
        SyncCompleted?.Invoke(r);
        return r;
    }

    public void ExitApp(bool restart = false)
    {
        Exiting = true;
        SaveSettings();
        UsageStats.Flush();      // 统计攒着落盘，退出时补最后一次
        _tray?.Dispose();
        _tray = null;
        try { _selectionWatcher?.Dispose(); _selectionWatcher = null; } catch { }   /* 预期内：清理动作，失败无副作用 */
        if (restart)
        {
            /* 重启自己（权限切换必须重启才生效）。
             * 用 explorer 启动而不是当前进程 —— 否则子进程会继承
             * 当前进程的完整性级别，达不到"降权重启"的目的。 */
            try
            {
                var exe = Environment.ProcessPath ?? AppPaths.AppDir.TrimEnd('\\') + "\\KeyboardEnhance.exe";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                { UseShellExecute = true, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory });
            }
            catch (Exception ex) { CoreLog.Warn("重启失败：" + ex.Message); }
        }
        Core.Health.CrashGuard.MarkCleanExit(AppPaths.DataDir);
        Application.Current.Shutdown();
    }

    // ---------------- 托盘 ----------------
    private void CreateTray()
    {
        _tray = new TrayIcon(this);
        _tray.SetEnabled(Engine.Enabled);
    }

    public void RefreshTray() => _tray?.SetEnabled(Engine.Enabled);

    /// <summary>
    /// 界面语言切换后调用：把「文本在构造时就写死」的长生命周期界面全部重建一遍。
    ///
    /// 为什么需要这一步 —— 用 <c>{loc:T '…'}</c> 标出来的 XAML 文字是绑定，
    /// 换语言时自己会重画；但下面这几处不是绑定，是**造对象那一刻**就把文字塞进去的：
    ///   · 托盘右键菜单（WinForms ToolStripMenuItem，构造时定死 .Text）；
    ///   · 动作目录（<see cref="RebuildActions"/> 里的 ActionCatalog，Title/Description 是 record 属性）；
    ///   · 主窗口已缓存的分页（各 View 实例留着旧的文本）；
    ///   · 整条动作列表的订阅方（按键映射页、Qbar 的候选列表）。
    /// 不重建这些，用户切完语言会看到「设置页是英文、右键菜单还是中文」。
    /// </summary>
    public void NotifyLocalizedSurfaces()
    {
        try
        {
            RebuildActions();      // 动作目录 → 触发 ActionsChanged，映射页/Qbar 跟着重建
            _tray?.Relocalize();   // 托盘菜单 + 悬浮提示
            _main?.ReloadFromSettings();
            _qbar?.InvalidateStyle();
            SettingsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            CoreLog.Error("切换语言后重建界面失败（部分界面可能仍是旧语言）", ex);
        }
    }

    // ---------------- 剪贴板 ----------------
    private void OnClipboardChanged()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;
            string text = System.Windows.Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text == _lastClipboardText) return;

            /* 「输入文本」的长文本走剪贴板粘贴时会临时写两下（正文 + 还原）
             * 那份内容不是用户复制的，记进历史纯属噪声。 */
            if (ClipboardPaster.IsSelfWrite(text)) return;

            _lastClipboardText = text;
            Clipboard.RecordHistory(text);
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    public void Dispose()
    {
        try { _foreground?.Dispose(); _foreground = null; } catch { }
        try { _deviceTracker?.Dispose(); _deviceTracker = null; } catch { }
        try { _wakeWindow?.Dispose(); _wakeWindow = null; } catch { }
        try { Sync?.DisposeClient(); } catch { }
        if (_disposed) return;
        _disposed = true;
        _watchdog.Dispose();
        _clipboard.Dispose();
        Engine.Dispose();
        _tray?.Dispose();
        _queue.Dispose();
        // 隐藏/退出时把内存还给系统（此时不会再有渲染访问）
        MemoryTrimmer.Trim();
    }

}

