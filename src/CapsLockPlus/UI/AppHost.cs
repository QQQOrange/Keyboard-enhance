using System.Windows;
using System.Windows.Threading;
using CapsLockPlus.Core;
using CapsLockPlus.Core.Actions;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Qbar;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Translate;
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
    private MathBoardWindow? _math;
    private TranslateWindow? _translate;
    private ClipboardHistoryWindow? _clipHistory;
    private bool _disposed;

    public AppSettings Settings { get; set; }
    public HotkeyEngine Engine { get; }
    public ActionCatalog Actions { get; private set; }
    public ActionContext Context { get; }
    public ClipboardService Clipboard { get; }
    public WindowBindingService Bindings { get; }
    public QbarService Qbar { get; }
    public TranslateService Translator { get; } = new();
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

        // 同步服务的设置读取走委托，保证恢复配置后拿到的是最新引用
        Sync = new Core.Sync.SyncService(() => Settings);

        Clipboard = new ClipboardService
        {
            Enabled = true,
            ClipboardFeatureOn = settings.Global.AllowClipboard,
        };
        Clipboard.Notified += Notify;

        // 用委托而不是直接传 settings：重新加载配置会整体替换 Settings 对象，
// 抓着旧引用会导致"界面显示新绑定、实际按旧配置切窗口"。
        Bindings = new WindowBindingService(() => Settings);
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

        Qbar = new QbarService(() => Settings, () => Clipboard.History);

        Engine = new HotkeyEngine(_queue, () => Settings, InvokeAction);
        // 让引擎知道宏播放器：播放中按 Esc 需要能取消
        Engine.MacroPlayer = Context.MacroPlayer;
        Engine.CapsHeldChanged += held => _dispatcher.BeginInvoke(() => CapsHeldChanged?.Invoke(held));
        // 反馈模式：每次组合键触发都在屏幕上闪一下动作名，
        // 便于确认“按键到底有没有被识别”。
        Engine.ActionTriggered += action => _dispatcher.BeginInvoke(() =>
        {
            if (!Settings.Global.ShowActionFeedback) return;
            string title = Actions.Get(action)?.Title ?? action;
            ToastWindow.Show(title, "feedback");
        });

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
        Context.QbarRequested += engineKey =>
            _dispatcher.BeginInvoke(() => ShowQbar(EnginePrefill(engineKey)));
        Context.MathBoardRequested += () => _dispatcher.BeginInvoke(() => ShowMathBoard());
        Context.SettingsRequested += () => _dispatcher.BeginInvoke(() => ShowMainWindow());
        Context.HelpRequested += () => _dispatcher.BeginInvoke(() => ShowMainWindow("help"));
        Context.ClipboardHistoryRequested += () => _dispatcher.BeginInvoke(() => ShowClipboardHistory());
        Context.TranslateRequested += () => _dispatcher.BeginInvoke(() => ShowTranslate());
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
        Engine.Start();
        if (Settings.Global.ShowTrayIcon) CreateTray();
        _clipboard.Start();
        _watchdog.Start();
        ApplyStartupSetting();
        CoreLog.Info($"{AppInfo.ProductName} 已启动（管理员：{ElevationHelper.IsElevated}）");
    }

    private void ApplyStartupSetting()
    {
        /* 先清理改名前的旧自启项。
         *
         * 软件从 CapsLock+ 改名后，老用户的机器上可能还留着一条指向
         * CapsLockPlus.exe 的自启项 —— 那个文件已经不存在了，开机会弹错误。
         * 这一步必须在"判断是否需要同步自启"之前做。 */
        StartupManager.MigrateLegacyValue();

        if (Settings.Global.StartWithWindows != StartupManager.IsEnabled())
            StartupManager.SetEnabled(Settings.Global.StartWithWindows);
    }

    // ---------------- 动作 ----------------
    public bool InvokeAction(string action, string arg)
    {
        bool ok = Actions.Invoke(action, arg);
        if (!ok) CoreLog.Warn($"动作未执行：{action}（可能未在动作目录中注册）");
        else CoreLog.Info($"动作：{action}{(string.IsNullOrEmpty(arg) ? "" : " " + arg)}");
        return ok;
    }

    /// <summary>诊断开关：把钩子收到的每个按键写进日志。</summary>
    public void EnableKeyTrace() => Engine.EnableKeyTrace();

    public void Notify(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || !Settings.Global.ShowToast) return;
        _dispatcher.BeginInvoke(() => ToastWindow.Show(message));
    }

    public void ToggleEnabled() => SetEnabled(!Engine.Enabled);

    public void SetEnabled(bool enabled)
    {
        if (Engine.Enabled == enabled) return;
        Engine.Enabled = enabled;
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
    public void SaveSettings()
    {
        SettingsStore.Save(Settings);

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

    public void ApplyHotkeyScheme(string scheme)
    {
        Settings.Global.HotkeyScheme = scheme;
        var preset = scheme == "capslock_plus" ? DefaultSettings.ClassicScheme() : DefaultSettings.CapsloxScheme();
        // 保留用户自己加过、预设里没有的组合键
        foreach (var custom in Settings.Keys.Where(k => preset.All(p =>
                     !p.Combo.Equals(k.Combo, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            preset.Add(custom);
        }
        Settings.Keys = preset;
        SaveSettings();
    }

    public void ReloadAll()
    {
        Settings = SettingsStore.Load();
        ThemeManager.Apply(ThemeManager.Parse(Settings.Global.Theme));
        // 主题一变就作废 Qbar 的样式缓存，否则已打开的面板还留着旧配色
        _qbar?.InvalidateStyle();
        Context.Clipboard.ClipboardFeatureOn = Settings.Global.AllowClipboard;
        Context.Mouse.Speed = Settings.Global.MouseSpeed;
        Context.Mouse.Step = Settings.Global.MouseMoveStep;
        Engine.Suspended = false;
        RebuildActions();
        ApplyStartupSetting();
        _tray?.SetEnabled(Engine.Enabled);
        _qbar?.InvalidateStyle();
        _main?.ReloadFromSettings();
        SettingsChanged?.Invoke();
        Notify("配置已重新加载");
    }

    // ---------------- 窗口 ----------------
    public void ShowMainWindow(string? page = null)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _main ??= new MainWindow(this);
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
        _qbar ??= new QbarWindow(this);
        _qbar.Open(prefill);
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
        // 窗口被用户关掉（Esc / 关闭按钮）后不能再复用：Show() 一个已关闭的窗口会抛异常。
        if (_math is not { IsLoaded: true }) _math = new MathBoardWindow();
        _math.Show();
        _math.Activate();
    }

    public void ShowTranslate()
    {
        if (_translate is not { IsLoaded: true }) _translate = new TranslateWindow(this);
        _translate.Show();
        _translate.Activate();
        _translate.StartWithSelection();
    }

    public void ShowClipboardHistory()
    {
        if (_clipHistory is not { IsLoaded: true }) _clipHistory = new ClipboardHistoryWindow(this);
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
                var result = await service.CheckAsync();
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

    public void ExitApp()
    {
        Exiting = true;
        SaveSettings();
        _tray?.Dispose();
        _tray = null;
        Application.Current.Shutdown();
    }

    // ---------------- 托盘 ----------------
    private void CreateTray()
    {
        _tray = new TrayIcon(this);
        _tray.SetEnabled(Engine.Enabled);
    }

    public void RefreshTray() => _tray?.SetEnabled(Engine.Enabled);

    // ---------------- 剪贴板 ----------------
    private void OnClipboardChanged()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;
            string text = System.Windows.Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text == _lastClipboardText) return;
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

