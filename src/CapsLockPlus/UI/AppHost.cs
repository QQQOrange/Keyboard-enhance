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
public sealed partial class AppHost : IDisposable
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
    /// 设置/清除引擎上的录制器，并同步宿主的 Suspended 状态。
    /// 只改主进程会让宿主继续执行热键，录制时按 Caps+Q 仍会弹面板。
    /// </summary>
    public void SetMacroRecorder(Core.Macro.MacroRecorder? recorder)
    {
        Engine.MacroRecorder = recorder;
        if (_hookHostReady)
            SendHookHostCommand(recorder != null ? "SUSPEND 1" : "SUSPEND 0");
    }

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
        /* 老版本留下的明文数据（usage.json / 日志 / 绑定快照）在启动时统一加密，
         * 幂等：已经是密文的文件直接跳过。 */
        Core.Security.LocalDataProtection.MigrateAll();

        bool unclean = !Core.CliFlags.IsDiagnostic(Environment.GetCommandLineArgs().Skip(1))
                       && Core.Health.CrashGuard.MarkRunning(AppPaths.DataDir);

        /* 上一次没正常退出：把上一轮日志的尾巴另存一份，方便直接发给开发者。 */
        /* 启动体检：如果配置里有"把修饰键本身换掉"的改键（Ctrl→Alt 这类），
         * 用户很容易根本没意识到 —— 表现是"Ctrl+C 变成 Alt+C""快捷键全不对"。
         * 只在第一次问，选"保留"就不再打扰。 */
        if (unclean) Core.Health.CrashGuard.SaveContext(AppPaths.DataDir, CoreLog.LogPath);

        ResetIncompleteCustomKeys();
        if (ClearStaleNotes(Settings) > 0) SettingsStore.RequestSave(Settings);

        /* 前缀符号改成"只认一个"之后，老配置（;mail 之类）要自动迁就一下。 */
        if (InferSnippetPrefix(Settings) is char inferred)
        {
            /* 绑定内容变了：让引擎的层绑定缓存失效（它按 List 实例 + 条数判断，
         * "恢复默认"是清空后原地重填，可能沿用旧缓存）。 */
        Settings.Global.BindingsVersion++;

        SettingsStore.RequestSave(Settings);
            CoreLog.Info($"文本片段：按已有触发词把前缀符号设为「{inferred}」");
        }

        /* 先起"钩子宿主"：它是个最小进程（不建界面、不做同步），装钩子的可靠性
         * 远高于本进程 —— 完整进程的 GC 停顿会把钩子回调拖过系统的 300ms 限制，
         * 导致钩子被静默摘掉（实测根因）。拿不到 READY 就退回自己装钩子。 */
        /* 宿主进程通过环境变量继承"是否追踪按键"（--trace 只设了主进程的开关，
         * 而钩子在宿主里）。 */
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--trace", StringComparison.OrdinalIgnoreCase)))
            Environment.SetEnvironmentVariable("CLP_TRACE", "1");

        bool hookHostReady = StartHookHost();

        /* 启动头两秒不许注入：这是"启动后莫名打出一个字母"的物理保险。
         * 钩子探针走 TapUntagged（直连 SendInput），不受影响。 */
        Core.Input.InputSender.QuietUntil = DateTime.Now.AddSeconds(2);

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
                if (Core.Input.InputSender.InjectCount > 0)
                    CoreLog.Warn($"[注入] 启动 2 秒内共注入 {Core.Input.InputSender.InjectCount} 次，最后一次：{Core.Input.InputSender.LastInjectSummary}");

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

            /* 放到后台线程：补发抬起要进 user32，绝不能占着界面线程。 */
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    /* 先看改键账本：源键早就松开、目标键还按着 → 立刻松开。
                     * 这一步和下面的粘键巡检互补：改键的抬起丢失时，
                     * 系统里"按着"的其实是目标键（比如 Alt），而不是用户按的源键。 */
                    Engine.ReleaseStaleRemaps();

                    /* 修饰键账本兜底：只在我们没有正在播宏的时候做 ——
                     * 宏可以合法地按住修饰键很久。 */
                    if (Engine.MacroPlayer?.IsPlaying != true)
                        Core.Input.InputSender.ReleaseStaleModifiers();

                    Core.Input.InputSafety.HealIfStuck(last, out _);
                }
                catch (Exception ex) { CoreLog.Swallow(ex, "粘键巡检"); }
            });
        };
        if (!headless) stuckWatch.Start();

        /* 快速纠偏：只在"刚刚注入过"的 3 秒窗口里工作。
         *
         * 判据：系统认为某个修饰键按着，但引擎**没收到过它的真实按下**
         * → 那它只可能是我们注入的组合键漏了抬起。
         * 立刻补发抬起，而不是等 20 秒巡检 —— 用户对"Ctrl 卡住"的体感是即时的。 */
        var leakWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        leakWatch.Tick += (_, _) =>
        {
            try
            {
                if ((DateTime.Now - Core.Input.InputSender.LastInjectAt).TotalSeconds > 3) return;

                foreach (int vk in Core.Input.InputSafety.WatchedModifiers)
                {
                    if (!Core.Input.InputSafety.IsDown(vk)) continue;

                    /* 只松"我们自己按下、却没抬起"的键。
                     * 用户自己按住的 Ctrl/Alt 不能被误判 —— 那会把正在进行
                     * 的 Ctrl+C 直接打断（手势工具复制失败的典型原因）。 */
                    if (!Core.Input.InputSender.ShouldAutoRelease(vk, Engine.IsPhysicallyDown(vk))) continue;

                    Core.Input.InputSafety.ReleaseKey(vk);
                    CoreLog.Warn($"[注入] 发现漏抬起的修饰键 {Core.Input.KeyName.NameOf(vk)}，已补发抬起");
                }
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "修饰键纠偏"); }
        };
        if (!headless) leakWatch.Start();

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

}
