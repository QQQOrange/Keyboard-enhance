using CapsLockPlus.Core.Macro;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 通用"触发键 + 键盘层"组合键引擎 —— 整个产品的核心。
///
/// 模型（参照键盘驱动）：
///   * 用户可配置多个**触发键**，每个触发键绑定一个**层**；
///   * 短按（按住未超过该键的 HoldMs）→ 执行原键盘功能 / 自定义短按动作；
///   * 长按超过 HoldMs → 进入"层模式"，此时触发键本身不再输出原功能；
///   * 层模式下按任意键 → 查该层的绑定表，命中则执行（并吞键），
///     未命中则放行，让按键正常输入；
///   * 一个层可以有 10 个（默认），层内还支持 LAlt / Win 修饰键，
///     组合键 ID 形如 <c>caps_q</c> / <c>caps_lalt_q</c> / <c>caps_win_1</c>。
///
/// 默认只有一个触发键 = CapsLock 绑第 1 层，行为与上一版完全一致。
///
/// 相比上游 AutoHotkey 版的改进：
///   * 钩子回调里不做耗时操作，全部排队到工作线程执行，不会被系统摘钩；
///   * 组合键支持长按自动重复（移动 / 选择光标时手感更顺）；
///   * 状态机可被界面实时暂停 / 恢复。
/// </summary>
public sealed partial class HotkeyEngine : IDisposable
{
    private readonly KeyboardHook _hook = new();
    private readonly ActionQueue _queue;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string, string, bool> _invoke;

    /* ---- 两个线程同时摸的状态：必须用线程安全的容器 ----
     *
     * 键盘钩子跑在**它自己的线程**上（带消息循环），而"长按判定 / 连续重复"
     * 跑在界面线程的计时器上。这两条线程都会读写下面这两个集合：
     *   · 钩子线程：按下时 Add、抬起时 Remove（还决定要不要吞掉这个键）；
     *   · 界面线程：每 20ms 检查"这个键还按着吗"（Contains）。
     *
     * 早先用 HashSet：并发读写可能让内部结构错乱（运气不好会死循环或漏键），
     * 表现就是"偶尔有个键一直不响应"这种极难复现的问题。
     * ConcurrentDictionary 的 Add / TryRemove / ContainsKey 都是原子的，
     * 不需要额外加锁，也不会在钩子回调里制造争用。 */
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _swallowedDown = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _swallowedUp = new();

    /* ---- 基础改键：不按住触发键也生效的全局重映射 ----
     * 表按内容指纹缓存，避免每次按键都去解析键名。 */
    private RemapTable? _remapTable;
    private string _remapStamp = "";

    /// <summary>
    /// "这次按键来自哪只键盘"。由 AppHost 注入（Raw Input 实现）。
    /// 为空 = 不区分设备，行为与旧版完全一致。
    /// 只在**有设备专属规则的键**上调用，所以普通按键不会多花时间。
    /// </summary>
    public Func<int, bool, string?>? DeviceResolver { get; set; }

    /// <summary>当前按住的"源键 → 目标键"，用于成对松开（否则会粘键）。</summary>

    /* 改键账本 / 双击时间戳都会被**钩子线程**和**界面线程**同时碰
     * （暂停、挂起、钩子重装都会从界面线程调 ResetTriggerState）。
     * 字典没有线程安全保证，一边遍历一边增删会抛"集合已修改"，
     * 而抛出点在钩子回调里 —— 结果可能是某个目标键永远松不开。
     * 一把只在极短临界区内持有的锁，代价远小于这个风险。 */

    /// <summary>叠加层 id，由前台窗口监听写入（钩子线程读，必须 volatile）。</summary>
    private volatile int _appLayerId;

    /// <summary>
    /// 当前前台进程名（小写，含 .exe），由前台监听写入，供片段做按应用限定。
    /// 空串 = 还不知道 → 片段不做限定（fail open）。
    /// </summary>
    public string ForegroundProcess { get; set; } = "";

    /* ---- 双击动作：任意键连按两次触发一个动作 ---- */
    private Dictionary<int, string>? _gestureMap;
    private string _gestureStamp = "";

    /// <summary>每个键上一次按下的时刻（TickCount64），用于判定"连按两次"。</summary>
    private readonly Dictionary<int, long> _gestureLastTick = new();

    /// <summary>已经触发过双击、需要吞掉抬起事件的键。</summary>

    /* ---- 文本片段：累计纯文本输入，命中触发词就展开 ---- */
    private readonly System.Text.StringBuilder _snippetBuffer = new();


    /// <summary>自检用：替换时钟，避免依赖真实等待。</summary>
    internal Func<long> ClockForTest { get; set; } = () => Environment.TickCount64;

    /// <summary>
    /// 物理按键状态：钩子看到的每个 down 加入、每个 up 移除（**包括被吞掉的触发键**）。
    ///
    /// 只用来可靠地区分"系统自动重复"和"用户重新按下"。
    /// 旧实现看"两次 down 间隔 &lt; 100ms"，把键盘重复速率调慢
    /// （2~3 次/秒，间隔 300~500ms）的用户会踩到：重复 down 被当成
    /// 重新按下 → <see cref="ResetTriggerState"/> → 重新进层，
    /// 表现就是"层被反复踢掉、停不在目标层"。
    ///
    /// 这张表由钩子线程写、界面线程读，用并发字典保证原子性。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _physDown = new();

    // 当前被按住的触发键（同一时刻只认定一个，避免冲突）
    private int _activeTriggerVk = -1;
    private TriggerKey? _activeTrigger;
    private int _activeLayer = -1;
    private bool _triggerHeld;
    /* 由界面线程的计时器写、钩子线程读，必须 volatile：
     * 否则切层那一刻钩子可能仍读到旧值，把这个键按"未进层"放行。 */
    private volatile bool _layerActive;    // 已进入层模式（超过 HoldMs）
    private bool _usedCombo;               // 层内触发过绑定
    private bool _usedOtherKey;            // 层内按过未绑定的键
    private bool _tapHandled;              // 短按动作是否已触发（超时那一刻触发）

    private DateTime _triggerDownTime = DateTime.MinValue;

    /// <summary>
    /// 上一次收到触发键 down 的时刻（Environment.TickCount64）。
    ///
    /// 用来区分"系统自动重复"和"用户重新按下" —— 见 OnTriggerDown 里的说明。
    /// 不能用 GetAsyncKeyState：触发键的按下被我们吞掉了，系统层面它从未按下。
    /// </summary>
    private long _lastTriggerDownTick;
    private DateTime _lastTapTime = DateTime.MinValue;
    private bool _hasLastTap;

    private bool _laltDown;

    /// <summary>当前按住的"非修饰键子层键"的键名（空 = 没有）。</summary>
    private bool _lwinDown;
    private bool _rwinDown;
    private bool _ctrlDown;
    private bool _shiftDown;

    private int _lastRepeatVk = -1;
    private DateTime _lastRepeatTime = DateTime.MinValue;



    /// <summary>按住期间的判定周期（毫秒）。长按阈值精度依赖它。</summary>
    private const int HoldTickActiveMs = 20;

    /// <summary>
    /// "按下被吞、抬起还没来"的记账上限。
    ///
    /// 正常情况下每个 up 到达时就会销账，表里只有极少数同时在按的键。
    /// 只有 up 事件真的丢了才会堆积 —— 超过这个数说明账本已经不可信，
    /// 整体清掉比继续记着更安全（否则会把后面无关的 up 也吞掉）。
    /// </summary>
    private const int MaxPendingUps = 64;

    /// <summary>
    /// 空闲时的判定周期（毫秒）。
    ///
    /// 以前固定 20ms 常跑：一个托盘工具 99% 时间没人按键，却在界面线程上
    /// 每秒醒 50 次，白耗电。空闲时降到 4 次/秒就够 —— 那时既没有长按判定
    /// 也没有连续调节，唯一还需要的只是"万一触发键状态卡住"的自愈，
    /// 而那只会在按住期间发生。
    /// </summary>
    private const int HoldTickIdleMs = 250;

    /// <summary>
    /// 连续调节最长持续多少秒。
    ///
    /// 这是"抬起事件丢失"的最后一道保险：钩子没收到 up 时，
    /// 连续调节会一直跑下去。正常人按住不会超过十秒，
    /// 到点强制收手，既不会无限涨，也不会误伤长按。
    /// </summary>
    public const int MaxContinuousSeconds = 10;

    /// <summary>
    /// 音量用的间隔：比亮度疏一点。
    ///
    /// 音量走系统音量键，每一步固定是**系统的**步进（通常 2%），
    /// 45ms 一步就是 44%/秒，按住两秒就到底了，太冲。
    /// 70ms 一步约 28%/秒，和直接按住键盘音量键的手感接近。
    /// </summary>
    public const int VolumeRepeatIntervalMs = 70;

    /* v4.5.4 起不再保留"连续多少轮读到已松开"的计数：
     * 那条自愈逻辑依赖 GetAsyncKeyState，而触发键的按下/抬起都被我们
     * 自己的钩子吞掉了，系统层面它永远是"未按下"，于是按住一停顿就被
     * 误判成抬起。自愈改由 Suspended/Enabled 切换和"下次按下"处理，
     * 这个字段只剩两处赋值、没有任何读取，属于纯遗留，已删除。 */

    /// <summary>
    /// 最近一次真实按键事件的时间。
    ///
    /// 自愈判定要参考它：用户正在连按层内按键时，绝对不能因为
    /// `GetAsyncKeyState` 的一次读数就去复位状态 —— 那会把层直接踢掉，
    /// 表现是"第一次按键有效，第二次变回原生功能"。
    /// </summary>
    private DateTime _lastKeyActivity = DateTime.MinValue;



    private bool _enabled = true;
    private bool _suspended;

    /// <summary>
    /// 是否启用。切到 false（托盘"暂停"）时**必须复位触发键状态**：
    /// 按住触发键的期间被暂停的话，up 事件到不了状态机（OnKeyEvent 早退），
    /// <c>_triggerHeld</c> 会卡 true，恢复后计时器立刻把层激活，
    /// 整个键盘被层逻辑劫持 —— 只能再完整按一次触发键才能解锁。
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            ResetTriggerState("Enabled 切换");
        }
    }

    /// <summary>
    /// 挂起（录制宏时置 true，引擎完全放行、只记录不拦截）。
    /// 切换时同样要复位触发键状态，理由同 <see cref="Enabled"/>。
    /// </summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            ResetTriggerState("Suspended 切换");
        }
    }

    /// <summary>
    /// 复位触发键状态机（不补发任何按键）。
    ///
    /// 用于暂停 / 恢复 / 挂起切换、以及钩子自愈重装之后 ——
    /// 这些时刻我们可能错过了触发键的抬起事件，状态若不复位，
    /// 计时器会在下一次 tick 时把一个"幽灵按住"升级成层模式。
    /// </summary>
    private void ResetTriggerState(string reason)
    {
        /* 没有触发键会话时也要往下走 —— 只要还有"改键欠账"。
         *
         * 场景：用户按住被改键的 Ctrl（引擎已经把目标键 Alt 按下去了），
         * 这时点托盘「暂停」。早先这里因为"没有触发键/层"直接返回，
         * ReleaseRemappedKeys 不执行，于是 Alt 一直停在被按下的状态 ——
         * 复现后每个按键都会变成 Alt+键，而且在下次按 Ctrl 抬起之前无法解除。 */
        int pendingRemaps;
        lock (_remapGate) pendingRemaps = _remappedDown.Count;
        bool hasModifier = _laltDown || _lwinDown || _rwinDown || _ctrlDown || _shiftDown;
        if (!_triggerHeld && !_layerActive && pendingRemaps == 0 && !hasModifier) return;
        CoreLog.Info($"引擎状态复位（{reason}）：丢弃未完成的触发键会话");
        _triggerHeld = false;
        _layerActive = false;
        _activeTriggerVk = -1;
        _activeTrigger = null;
        _activeLayer = -1;
        _swallowedDown.Clear();
        if (_swallowedUp.Count > MaxPendingUps) _swallowedUp.Clear();
        _physDown.Clear();
        _laltDown = _lwinDown = _rwinDown = _ctrlDown = _shiftDown = false;
        _lastTriggerDownTick = 0;
        _lastKeyActivity = DateTime.Now;
        LastKeyActivityMs = Environment.TickCount64;
        if (!string.IsNullOrEmpty(_subLayerKeyDown))
        {
            _subLayerKeyDown = "";
            SubLayerChanged?.Invoke("");
        }
        ClearContinuousRepeat();
        ReleaseRemappedKeys();      // 复位时把改键的目标键松开，避免粘键
        lock (_remapGate) _gestureLastTick.Clear();   // 双击判定作废，避免跨会话误触发
    }

    /// <summary>是否正处于某个触发键的按住状态。</summary>
    public bool IsTriggerHeld => _triggerHeld || _remoteTriggerHeld;

    /// <summary>
    /// 钩子已经吞不住键了（被系统摘掉 / 装不上）。
    ///
    /// 这时**不能再演"层"**：按键本来就漏给前台了，我们还切层的话，
    /// 用户按 Tab 会一边跳焦点一边被当成"按住触发键"，松开后层还卡着。
    /// 置位期间引擎对触发键完全不介入，按键全部原样放行。
    /// </summary>
    public volatile bool SwallowUnavailable;

    /// <summary>外部强制丢弃当前触发键会话（钩子失效、恢复后调用）。</summary>
    public void ResetForRecovery(string reason) => ResetTriggerState(reason);

    /* ---- 宿主模式的显示镜像 ----
     *
     * 钩子在宿主进程里时，本进程看不到按键，界面（测试页）会一片空白。
     * 宿主把这些状态回传过来，这里只做**显示**，绝不参与热键决策
     * （否则等于两个进程都在处理按键）。 */

    /// <summary>
    /// 捕获模式：正在录「要发送的键」或「绑定哪个键」时置 true。
    ///
    /// 这时**任何键都不许吞** —— 否则用户想录 Caps/Tab 这类触发键时，
    /// 键在宿主那儿就被吞掉了，捕获窗永远等不到它。按键仍然照常上报（KeySeen）。
    /// 进入/退出捕获都会复位状态，避免录制期间漏掉的 up 让修饰键或触发键卡住。
    /// </summary>
    public bool CaptureMode
    {
        get => _captureMode;
        set
        {
            if (_captureMode == value) return;
            _captureMode = value;
            ResetTriggerState(value ? "进入录键模式" : "退出录键模式");
        }
    }
    private bool _captureMode;

    /// <summary>宿主回传的按键累计数（宿主模式下用它判断钩子健康度）。</summary>
    public long RemoteKeyCount => Interlocked.Read(ref _remoteKeyCount);







    /// <summary>当前激活的层（未激活时为 -1）。</summary>
    public int ActiveLayer => _layerActive ? _activeLayer : _remoteLayer;

    /// <summary>
    /// 当前按住的非修饰键子层键（空 = 没有子层）。
    ///
    /// 以前这个状态只写在私有字段 <c>_subLayerKeyDown</c> 里，界面 / OSD
    /// 完全看不到"已经切到 Tab 子层"，用户感受就是"层没切换"。
    /// 现在对外开放，并配合 <see cref="SubLayerChanged"/> 通知。
    /// </summary>


    /// <summary>最近一次执行的动作签名 + 时间，用于去重（防"两个钩子各执行一次"）。</summary>
    private readonly Dictionary<string, DateTime> _lastActionAt = new();

    private bool IsDuplicateAction(string action, string arg)
    {
        string key = action + "\u0000" + arg;
        lock (_lastActionAt)
        {
            if (_lastActionAt.TryGetValue(key, out var at) &&
                (DateTime.Now - at).TotalMilliseconds < 150)
            {
                return true;
            }

            _lastActionAt[key] = DateTime.Now;

            if (_lastActionAt.Count > 64)
            {
                var stale = new List<string>();
                foreach (var (k, v) in _lastActionAt)
                    if ((DateTime.Now - v).TotalSeconds > 5) stale.Add(k);
                foreach (string k in stale) _lastActionAt.Remove(k);
            }

            return false;
        }
    }

    /// <summary>
    /// 「按应用自动启用」的叠加层 id（0 = 没有）。
    ///
    /// 由外部（前台窗口监听）设置；只做一件事：在基础状态下，
    /// 如果某个组合在叠加层里绑过，就执行它并吞掉按键；没绑过则原样放行。
    /// </summary>
    public int AppLayerId
    {
        get => _appLayerId;
        set => _appLayerId = value;
    }

    /// <summary>进入 / 离开子层时触发；参数是子层键名，离开时是空串。</summary>
    public event Action<string>? SubLayerChanged;

    /// <summary>当前激活的触发键名（未激活时为 null）。</summary>
    public string? ActiveTriggerKey => _triggerHeld ? _activeTrigger?.Key : _remoteTriggerKey;

    /// <summary>需要通知界面的提示（动作名称）。</summary>
    public event Action<string>? ActionTriggered;

    /// <summary>状态变化（按住/松开触发键）。</summary>
    public event Action<bool>? TriggerHeldChanged;

    /// <summary>
    /// 每一个**真实**按键事件：(虚拟键码, 是否按下, 当时激活的层 -1 = 未进层)。
    ///
    /// 专供「测试」页：用户想确认"我按的键到底有没有被收到""切层成不成功"。
    /// 注入的按键不抛（否则测试页会被自己的动作刷屏）。
    /// </summary>
    public event Action? SnippetUsed;

    /// <summary>
    /// 按键拦截器（可选）。返回 true = "这个键我处理了，别再往下走"。
    ///
    /// 用途：Qbar 面板没拿到键盘焦点时也要能用 Esc 关掉 —— 面板显示期间挂上，
    /// 隐藏时摘掉。回调在钩子线程，只能做判断 + 派发，不能碰界面。
    /// </summary>
    public Func<int, bool>? KeyInterceptor { get; set; }

    /// <summary>最近一次收到物理按键的时刻（Environment.TickCount64 毫秒）。</summary>
    public long LastKeyActivityMs { get; private set; } = Environment.TickCount64;

    /// <summary>引擎认为当前按住的键数（含修饰键）。</summary>
    public int PhysicallyDownCount => _physDown.Count;

    /// <summary>这个键现在是不是**用户真的按着**（不含我们注入的）。</summary>
    public bool IsPhysicallyDown(int vk) => _physDown.ContainsKey(vk);

    public event Action<int, bool, int>? KeySeen;

    /// <summary>兼容旧名称：按住/松开 CapsLock。</summary>
    public event Action<bool>? CapsHeldChanged
    {
        add => TriggerHeldChanged += value;
        remove => TriggerHeldChanged -= value;
    }

    /// <summary>CapsLock 大写锁定当前是否开启。</summary>
    public static bool CapsLockOn => (GetKeyState(VK.CapsLock) & 1) != 0;

    /// <summary>切换 CapsLock 大写锁定状态。</summary>
    public static void ToggleCapsLock() => InputSender.Tap(0x14);

    public HotkeyEngine(ActionQueue queue, Func<AppSettings> settings, Func<string, string, bool> invoke)
    {
        _queue = queue;
        _settings = settings;
        _invoke = invoke;
        _hook.KeyEvent += OnKeyEvent;
        _hook.WheelEvent += OnWheel;
        /* 回调异常时的兜底决策：处于层语义就吞掉，别把按键漏给前台。
         * 见 KeyboardHook.SwallowOnError 的注释。 */
        _hook.SwallowOnError = ShouldSwallowOnError;
    }

    /// <param name="installHook">
    /// 是否由本进程安装键盘钩子。为 false 时只起计时器 —— 钩子由
    /// <see cref="HookHost"/> 那个最小进程负责（见它的注释：完整进程里
    /// GC 停顿会把钩子回调拖过 300ms，系统直接摘钩）。
    /// </param>
    public void Start(bool installHook = true)
    {
        if (installHook) _hook.Start();
        StartHoldTimer();

        if (!installHook)
        {
            CoreLog.Info("键盘钩子由独立宿主进程负责（本进程不再装钩子）");
            MarkHookAlive();
            return;
        }


        /* 日志必须**如实**：以前这里无条件写"键盘钩子已启动"，
         * 而 Start() 里 SetWindowsHookEx 失败时只会多一条 ERROR，
         * 之后整条日志看起来一切正常 —— 用户报"按键全都不灵 / 宏录制没反应"，
         * 从日志里根本看不出钩子其实没装上。 */
        if (IsHookAlive)
        {
            CoreLog.Info("键盘钩子已启动");
        }
        else
        {
            CoreLog.Error("⚠ 键盘钩子没有启动成功：所有组合键与宏录制都不会收到按键。" +
                          "运行 KeyboardEnhance.exe --diag 查看原因。");
        }

        MarkHookAlive();
    }

    /// <summary>
    /// 供宿主进程调用：驱动一次「长按阈值判定」。
    ///
    /// 主程序里这活由 DispatcherTimer 干，但宿主进程**没有 WPF 消息循环**，
    /// 那个计时器根本不会滴答 —— 结果是「按住触发键不放、不按别的键」永远进不了层
    /// （按别的键能进，因为那条路在钩子回调里）。所以宿主用普通计时器每秒调它 50 次。
    /// </summary>
    public void TickHold()
    {
        CheckHoldThreshold();
        FlushPendingSnippet();
    }

    /// <summary>
    /// 启动长按判定计时器。
    ///
    /// 钩子回调里不能做定时，所以阈值判定放在界面线程的计时器上：
    /// 每 20ms 检查一次"当前触发的键是否已按住超过阈值"，超过就切到层模式。
    ///
    /// 单独抽出来是为了让自检能**只启计时器、不装钩子**地验证真实长按路径
    /// （之前的自检用 EnterLayerImmediatelyForTest 直接跳过了计时器，
    ///  等于这条最关键的链路从来没被测过）。
    /// </summary>
    public void StartHoldTimer()
    {
        if (_holdTimer != null) return;
        _holdTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(HoldTickIdleMs),
        };
        /* 同一个计时器顺带处理"等待更长的触发词"：FlushPendingSnippet 没有挂起项时
         * 立刻返回，开销可以忽略。 */
        _holdTimer.Tick += (_, _) =>
        {
            CheckHoldThreshold();
            FlushPendingSnippet();
        };
        _holdTimer.Start();
    }

    /// <summary>
    /// 要求计时器切到"活跃周期"（20ms）。
    ///
    /// 调用点可能在钩子线程上，而 DispatcherTimer 只能在界面线程改 ——
    /// 所以这里用 Send 优先级投递，下一轮立刻生效（比长按阈值 200ms 快得多）。
    /// </summary>
    private void RequestFastHoldTick()
    {
        var timer = _holdTimer;
        if (timer == null) return;

        if (timer.Dispatcher.CheckAccess())
        {
            ApplyFastHoldTick();
            return;
        }

        try
        {
            timer.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send,
                new Action(ApplyFastHoldTick));
        }
        catch
        {
            // 退出过程中 Dispatcher 可能已关闭：忽略即可，反正要退了
        }
    }

    private void ApplyFastHoldTick()
    {
        var timer = _holdTimer;
        if (timer == null) return;
        if (timer.Interval != TimeSpan.FromMilliseconds(HoldTickActiveMs))
            timer.Interval = TimeSpan.FromMilliseconds(HoldTickActiveMs);
        if (!timer.IsEnabled) timer.Start();
    }

    /// <summary>阈值检查（活跃期每 20ms / 空闲期每 250ms 调一次；自检也可直接调）。</summary>
    private void CheckHoldThreshold()
    {
        // 连续量重复与触发键无关，先跑它
        CheckContinuousRepeat();

        /* ---- 层卡住看门狗 ----
         *
         * 万一松手事件丢了（钩子被摘、切桌面、另一个程序抢焦点），层会一直挂着。
         * 而层里的语义是"没绑定的键也吞掉"，用户感受到的就是**整个键盘失灵**，
         * 只能退出程序。这里兜底：长时间完全没有按键活动就把触发键会话清掉。
         *
         * 3 分钟是刻意选的：正常人不会按住触发键空等 3 分钟，
         * 中途思考、读屏幕都不会触发（一有按键就顺延）。 */

        // 没有任何"按住"迹象时退回空闲周期，避免空转耗电
        RelaxHoldTickIfIdle();

        if ((_triggerHeld || _layerActive) &&
            (DateTime.Now - _lastKeyActivity).TotalSeconds > 30)
        {
            CoreLog.Warn("层已经 30 秒没有任何按键活动，判定为抬起事件丢失，自动复位");
            ResetTriggerState("层长时间无操作");
            return;
        }

        if (!_triggerHeld) return;

        /* ---- 这里**不再**用 GetAsyncKeyState 判断"触发键还按着没有" ----
         *
         * v4.5.4 删掉了原来每 20ms 一次的"自愈"，因为它从根上就是错的：
         * 触发键的按下和抬起都被我们自己的钩子吞掉了（不吞的话 CapsLock
         * 会切换大小写、右 Alt 会激活菜单栏），所以**系统层面这个键
         * 从来没有被按下过** —— GetAsyncKeyState 对它读出来永远是"未按下"。
         *
         * 于是只要用户按住触发键、中途停顿超过 1 秒没按别的键，就会被判成
         * "抬起事件丢失"，层被踢掉。用户那边的体感是：
         * 按住触发键 → 触发一个功能 → 停下来看一眼 → 再按第二个键 →
         * 第二个键变回了原生功能（字母又变成了字母，而不是层里的绑定）。
         *
         * 真正需要自愈的场合（钩子被系统静默摘掉、UAC / 会话切换）另有
         * 更可靠的信号，不需要猜物理状态：
         *   · Suspended / Enabled 切换会显式 ResetTriggerState；
         *   · 用户下一次按触发键时，OnTriggerDown 用"两次 down 的间隔"
         *     区分"系统自动重复"和"重新按下"，后者会复位旧会话。
         *
         * 代价：万一抬起事件真的丢了，层会一直挂着，直到用户再按一次触发键。
         * 这期间层内**没有绑定**的键是放行的（照常输入），只有绑了功能的
         * 键会执行功能 —— 用户很容易察觉，按一下触发键就恢复。
         * 相比之下，"按住好好的层突然消失、所有绑定一次性失效"要糟得多。
         */
        if (_layerActive) return;

        /* 触发键配置读不到（按住期间被用户禁用 / 列表被重建）时，
         * 用默认阈值继续，而不是把这次按下丢掉 —— 用户不会知道
         * "因为你在按的时候改了设置，所以这次不算"。 */
        int holdMs = _activeTrigger?.HoldMs ?? 200;
        if ((DateTime.Now - _triggerDownTime).TotalMilliseconds >= holdMs)
            OnHoldThresholdReached();
    }

    /// <summary>
    /// 没有任何"正在按住"的迹象时，把计时器退回空闲周期（4 次/秒）。
    /// 由 <see cref="CheckHoldThreshold"/> 的自愈分支与抬起处理调用。
    /// </summary>
    private void RelaxHoldTickIfIdle()
    {
        var timer = _holdTimer;
        if (timer == null) return;
        if (_triggerHeld) return;
        lock (_repeatGate)
        {
            if (_repeats.Count > 0) return;
        }
        if (timer.Interval != TimeSpan.FromMilliseconds(HoldTickIdleMs))
            timer.Interval = TimeSpan.FromMilliseconds(HoldTickIdleMs);
    }

    /// <summary>
    /// 物理按键状态查询（可注入，供自检模拟）。
    ///
    /// 不注入时读真实的 <c>GetAsyncKeyState</c>；但**钩子没跑**时一律返回
    /// true（"当作按着"），这样自检里的 SimulateKey 不会被物理状态校验误伤。
    /// </summary>
    internal Func<int, bool>? PhysicalDownProbe { get; set; }

    private bool PhysicalChecksActive => PhysicalDownProbe != null || _hook.IsRunning;



    /// <summary>松开某个键 → 只停它自己的连续调节。</summary>
    private void StopContinuousRepeat(int vk)
    {
        lock (_repeatGate) _repeats.Remove(vk);
    }



    /// <summary>
    /// 是否"按住应该连续变化"的动作（音量 / 亮度）。
    ///
    /// 这类动作重复执行的语义是**线性累加**（一直加下去），
    /// 和"移动光标"那种重复不一样 —— 后者只是按住连发。
    /// </summary>
    /// <summary>这类动作按住时的步进间隔（毫秒）。</summary>
    public static int RepeatIntervalFor(string action) => action switch
    {
        "volumeUp" or "volumeDown" => VolumeRepeatIntervalMs,
        _ => RepeatIntervalMs,
    };

    public static bool IsContinuous(string action) => action switch
    {
        "volumeUp" or "volumeDown" or "brightnessUp" or "brightnessDown" => true,
        _ => false,
    };

    private bool PhysDown(int vk)
    {
        if (PhysicalDownProbe != null) return PhysicalDownProbe(vk);
        if (!_hook.IsRunning) return true;      // 自检：没有真实硬件，不做校验
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    /// <summary>同一个物理键的所有可能编码（左右专用码 + 通用码）。</summary>
    private static int[] FamilyCodes(int vk) => vk switch
    {
        VK.LControl or VK.RControl or 0x11 => new[] { 0xA2, 0xA3, 0x11 },
        VK.LShift or VK.RShift or 0x10 => new[] { 0xA0, 0xA1, 0x10 },
        VK.LMenu or VK.RMenu or 0x12 => new[] { 0xA4, 0xA5, 0x12 },
        _ => new[] { vk },
    };

    /// <summary>这个键（含同族编码）此刻是否被物理按住。</summary>
    private bool PhysDownFamily(int vk)
    {
        foreach (int code in FamilyCodes(vk))
            if (PhysDown(code)) return true;
        return false;
    }

    /// <summary>两个键码是否属于同一个修饰键家族（左/右/通用都算）。</summary>
    private static bool SameFamily(int a, int b)
    {
        if (a == -1 || b == -1) return false;
        return FamilyCodes(a).Intersect(FamilyCodes(b)).Any();
    }

    /// <summary>
    /// 用物理状态校正修饰键标记 —— **只校正没被我们吞掉的那些**。
    ///
    /// 校正本身是需要的：`_ctrlDown` 这类标记只要漏掉一次抬起（钩子重装、
    /// 切桌面、另一个程序抢焦点导致 up 被吞），就会**一直**是 true，
    /// 之后层内每个键都被拼成 `caps_ctrl_xxx`，用户绑的 `caps_q` 永远查不到。
    ///
    /// ⚠ 但**层内按下的修饰键一个都不能用物理状态校正**，这是 v4.5.5 修的
    /// 真实 bug（用户报"按下修饰键之后不能正确触发相关功能"）：
    ///
    ///   为了让 Ctrl/Shift/Alt/Win 不在系统里弹出 Alt 菜单、开始菜单，
    ///   层内按下它们时钩子会把事件**吞掉**。系统层面这些键从未被按下，
    ///   `GetAsyncKeyState` 读出来永远是 false。旧实现每次按键都无条件
    ///   按物理状态覆盖 —— 于是"Ctrl 按下（记为 true）→ 按 Q 前先校正
    ///   （被抹成 false）→ 查 caps_q"，Ctrl 子层整层失效，
    ///   Alt+滚轮同理。自检里因为探测函数被人为设成"按着"才没暴露。
    ///
    /// 现在的分工：
    ///   · 我们自己吞过的修饰键（在 <c>_swallowedUp</c> 里）→ 只信按键事件流；
    ///   · 没吞过的（进层**之前**就按着的）→ 物理状态可信，可以纠偏。
    ///
    /// ⚠ 触发键本身若是修饰键（右 Alt 当触发键），物理上它当然是按着的，
    /// 但绝不能算进 combo，否则又回到 caps_lalt_q 的老问题。
    /// </summary>
    private void SyncModifierFlagsWithPhysical()
    {
        if (!PhysicalChecksActive) return;

        int skip = _triggerHeld ? _activeTriggerVk : -1;

        /* 右 Alt 在不少键盘上是 **AltGr**：按下时系统会同时合成 Ctrl+Alt。
         * 触发键是它的时候，物理读到的那个 Ctrl 不能算进 combo ——
         * 否则层里每个键都被拼成 caps_ctrl_xxx，用户绑的 caps_up 永远查不到，
         * 表现就是"进层了，但层里的键全变成原生功能"。 */
        /* 判定 AltGr 要**同时**看到 Alt 和 Ctrl 都是物理按下 ——
         * 只看 Alt 会把"Ctrl 标记卡住、物理已松开"的正常校正也跳过，
         * 那层里的键又会全部变成 caps_ctrl_xxx。 */
        bool altGr = SameFamily(skip, VK.RMenu)
                     && PhysDownFamily(VK.RMenu)
                     && PhysDownFamily(VK.LControl);

        if (!SameFamily(skip, VK.LControl) && !altGr && !SwallowedInFamily(VK.LControl))
            _ctrlDown = PhysDownFamily(VK.LControl);
        if (!SameFamily(skip, VK.LShift) && !SwallowedInFamily(VK.LShift))
            _shiftDown = PhysDownFamily(VK.LShift);
        if (!SameFamily(skip, VK.LMenu) && !SwallowedInFamily(VK.LMenu))
            _laltDown = PhysDownFamily(VK.LMenu);
        if (!SameFamily(skip, VK.LWin) && !SwallowedInFamily(VK.LWin))
            _lwinDown = PhysDownFamily(VK.LWin);
        if (!SameFamily(skip, VK.RWin) && !SwallowedInFamily(VK.RWin))
            _rwinDown = PhysDownFamily(VK.RWin);
    }

    /// <summary>
    /// 这个修饰键（含左右/通用码）是不是**正被我们吞着**。
    ///
    /// <c>_swallowedUp</c> 里存的是"按下被吞掉、还没等到抬起"的键码。
    /// 这类键在系统看来根本没按下，物理状态不可信 —— 见上面的说明。
    /// </summary>
    private bool SwallowedInFamily(int vk)
    {
        foreach (int code in FamilyCodes(vk))
            if (_swallowedUp.ContainsKey(code)) return true;
        return false;
    }

    /// <summary>
    /// 仅供自检：查询某个修饰键当前被记为"按住"（Ctrl/Shift/Alt/Win）。
    /// </summary>
    internal bool ModifierHeldForTest(KeyModifiers which) => which switch    {
        KeyModifiers.Ctrl => _ctrlDown,
        KeyModifiers.Shift => _shiftDown,
        KeyModifiers.Alt => _laltDown,
        KeyModifiers.Win => _lwinDown || _rwinDown,
        _ => false,
    };

    /// <summary>仅供自检：手动跑一次阈值检查，不依赖计时器。</summary>
    public void CheckHoldThresholdForTest() => CheckHoldThreshold();

    /// <summary>
    /// 仅供自检：已经命中的文本片段数（命中后不一定真的注入文字 ——
    /// 焦点不可编辑时会跳过，所以这里只统计"匹配到"这件事）。
    /// </summary>
    internal int SnippetMatchesForTest { get; private set; }

    /// <summary>仅供自检：当前还有几个改键目标键处于"按下未松"的记账里。</summary>
    internal int PendingRemapsForTest
    {
        get { lock (_remapGate) return _remappedDown.Count; }
    }

    /// <summary>
    /// 仅供自检：把"最后一次按键活动"往前拨，用来复现
    /// "按住触发键、触发一个功能后停下来看了几秒"这个场景。
    ///
    /// 这个场景正是 v4.5.4 修的 bug：原来的自愈逻辑会在这时误判
    /// "抬起事件丢失"并把层踢掉，用户再按任何键就都变回原生功能了。
    /// </summary>
    public void SimulateIdleForTest(int milliseconds)
        => _lastKeyActivity = DateTime.Now.AddMilliseconds(-milliseconds);

    private System.Windows.Threading.DispatcherTimer? _holdTimer;

    public bool IsHookAlive => HookDelegatedToHost || _hook.IsRunning;

    /* ================= Raw Input 兜底 =================
     *
     * 低级钩子并不是万能的：远程桌面 / 向日葵 / ToDesk 的按键是注入事件，
     * 排在别的钩子后面时会被吞掉，于是**所有热键一起失灵**（用户看到的是
     * "Caps+Q 唤不出 Qbar"）。Raw Input 不受这个影响。
     *
     * 触发条件刻意收得很紧：只有钩子**连续 5 秒**一个事件都没收到、而 Raw Input
     * 还在报按键时，才把 Raw Input 当输入源喂给引擎；钩子一恢复就立刻交还，
     * 因此正常使用不会走这条路，也就不会出现"一个键触发两次"。
     */

    /// <summary>钩子静默多久后启用兜底（毫秒）。</summary>
    public const int RawFallbackQuietMs = 10000;

    private long _lastHookSample;
    private long _hookSeenAtMs;
    private long _rawFedCount;

    /// <summary>
    /// 兜底已启用、而且**吞不掉**按键时的通知（只抛一次）。
    ///
    /// 界面用它提示"热键能用，但按键还会传给当前程序"，
    /// 否则用户看到的是"长按 Tab 一边切层一边狂跳焦点"，比不生效更迷惑。
    /// </summary>
    public event Action? RawFallbackDegraded;

    /// <summary>现在是不是靠 Raw Input 在喂热键（诊断用）。</summary>
    public bool RawFallbackActive { get; private set; }

    /// <summary>连续漏掉的按键数（钩子没看到、Raw Input 看到了）。</summary>
    private int _missedStreak;

    /// <summary>兜底一共喂了多少个按键（诊断用）。</summary>
    public long RawFallbackCount => Interlocked.Read(ref _rawFedCount);

    /// <summary>引擎启动时记一下"钩子刚才是活的"，避免一开始就误判。</summary>
    internal void MarkHookAlive() => _hookSeenAtMs = Environment.TickCount64;

    /// <summary>自检用：直接指定"上次看到钩子事件"的时刻。</summary>
    internal void SeedHookSeenAtForTest(long ms) => _hookSeenAtMs = ms;

    /// <summary>
    /// Raw Input 这一路收到按键时的入口。
    ///
    /// 纯决策部分抽成 <see cref="ShouldUseRawFallback"/>，便于自检。
    /// </summary>
    /// <returns>真的喂给引擎了吗</returns>
    public bool FeedFromRaw(int vk, bool down)
        => FeedFromRaw(vk, down, _hook.KeyEventCount, Environment.TickCount64);

    internal bool FeedFromRaw(int vk, bool down, long hookCount, long nowMs)
    {
        /* ---- 宿主模式：本进程**一个键都不许喂** ----
         *
         * 钩子、吞键判定、层状态全在宿主进程里，动作由宿主通过管道转发过来。
         * 本进程只负责"执行动作"和"显示状态"。
         *
         * 这里以前没有拦截，后果是必然的：宿主模式下本进程自己的钩子计数恒为 0，
         * 于是 `_missedStreak` 一路累加、10 秒后 `ShouldUseRawFallback` 必然成立
         * —— 本进程开始用 Raw Input 喂自己的引擎，并且顺手 `RestartHook()` 又装了
         * **第二个低级钩子**。此时同一次按键会走两条独立的链：
         *   · 宿主：按键 → 宿主的层状态 → 转发动作 → 本进程执行；
         *   · 本进程：Raw Input → 本进程的层状态（另一份，且已被 ApplyRemote* 
         *     和这条自己的输入流搅乱）→ 直接执行动作。
         * 两份层状态一旦不同步，同一个键就会解析成**两个不同的绑定** ——
         * 用户看到的正是"按 Tab+T，QQ 和 WorkBuddy 一起打开"。
         *
         * 所以宿主模式下必须先退出，重装钩子的事交给 REHOOK 命令。 */
        if (HookDelegatedToHost) return false;

        /* 先数「钩子漏掉了几个键」：偶尔漏一个键是正常的（钩子被摘的那一刻、
         * 注入事件被上游钩子吃掉一次），连续漏掉 3 个才说明它真的不在链上了。
         * 以前单个键就接管，启动期一次误判就会把热键切到「吞不掉」的模式。 */
        if (hookCount == _lastHookSample) Interlocked.Increment(ref _missedStreak);
        else _missedStreak = 0;

        bool usable = ShouldUseRawFallback(hookCount, _lastHookSample, _hookSeenAtMs, nowMs,
                                           out bool hookJustMoved);

        /* 「钩子又动了」必须**最先**处理：
         * ShouldUseRawFallback 在 hookJustMoved 时返回 false，若沿用下面的
         * 早退分支，这段恢复逻辑永远走不到 —— RawFallbackActive 一直为 true、
         * 静默锚点也停在旧时间，钩子恢复后可能两边同时喂键。 */
        if (hookJustMoved)
        {
            _hookSeenAtMs = nowMs;
            _lastHookSample = hookCount;
            _missedStreak = 0;
            if (RawFallbackActive)
            {
                CoreLog.Info("键盘钩子恢复，热键交还给它");
                RawFallbackActive = false;
            }
            return false;
        }

        if (!usable || _missedStreak < 3)
        {
            _lastHookSample = hookCount;
            return false;
        }

        _lastHookSample = hookCount;

        if (!RawFallbackActive)
        {
            RawFallbackActive = true;
            CoreLog.Warn($"键盘钩子已经 {RawFallbackQuietMs / 1000} 秒没收到任何按键，改由 Raw Input 触发热键" +
                         "（远程桌面 / 注入输入时是正常的）");

            /* 光靠 Raw Input 只能"触发"，**吞不掉**按键 —— 用户按住触发键时，
             * 前台会继续收到这个键（长按 Tab 就一直跳焦点）。
             * 所以这里先把钩子重装一次：装回来了就恢复"能吞"，吞不掉才降级。 */
            try
            {
                RestartHook();
                CoreLog.Info("已尝试重装键盘钩子（恢复后会自动接管，不用重启程序）");
            }
            catch (Exception ex) { CoreLog.Warn("重装键盘钩子失败：" + ex.Message); }

            RawFallbackDegraded?.Invoke();
        }

        Interlocked.Increment(ref _rawFedCount);
        OnKeyEvent(vk, down, injected: false);
        return true;
    }

    /// <summary>
    /// 该不该用 Raw Input 兜底（纯函数）。
    ///
    /// 两条判据：
    ///   ① 钩子的事件计数这期间**一动没动**；
    ///   ② 距上次看到钩子事件已经超过 <see cref="RawFallbackQuietMs"/>。
    /// </summary>
    internal static bool ShouldUseRawFallback(
        long hookCount, long lastSample, long hookSeenAtMs, long nowMs, out bool hookJustMoved)
    {
        hookJustMoved = hookCount != lastSample;
        if (hookJustMoved) return false;
        if (hookSeenAtMs == 0) return false;
        return nowMs - hookSeenAtMs >= RawFallbackQuietMs;
    }

    /// <summary>
    /// 停掉本进程的钩子，把输入交给宿主进程（宿主崩溃后重启接管时用）。
    ///
    /// 与 <see cref="RestartHook"/> 的区别：这里**不**再安装钩子，而是先把
    /// <see cref="HookDelegatedToHost"/> 置真再停，避免任何一侧顺手又装回去。
    /// 调用方负责：宿主没起来时把它置回 false 并 <see cref="RestartHook"/>。
    /// </summary>
    public void DetachHookForHost()
    {
        try { ReleaseRemappedKeys(); } catch (Exception ex) { CoreLog.Swallow(ex, "切换宿主前松开改键"); }
        try { ResetTriggerState("切换钩子宿主"); } catch (Exception ex) { CoreLog.Swallow(ex, "切换宿主前复位状态"); }

        HookDelegatedToHost = true;
        try { _hook.Stop(); } catch (Exception ex) { CoreLog.Swallow(ex, "停用本进程钩子"); }
    }

    /// <summary>重装钩子（自愈用）。</summary>
    public void RestartHook()
    {
        /* 宿主模式下钩子**不在本进程**：在这里 Restart 只会装出第二个钩子，
         * 于是同一按键被两个引擎各执行一次（历史上"按一下打开两个程序"的事故）。
         * 要在宿主里重装，主程序发 REHOOK 命令给宿主。 */
        if (HookDelegatedToHost) return;

        /* 重装前先松开所有改键目标键：旧钩子如果漏收了抬起，
         * 账本里的目标键会永远按着 —— 重装之后更没人去松它。 */
        try { ReleaseRemappedKeys(); } catch (Exception ex) { CoreLog.Swallow(ex, "重装钩子前松开改键"); }

        /* 状态复位：旧钩子可能漏收了修饰键的抬起，账本里会一直留着"按着"。
         * 不复位的话，重装之后第一次组合键就会被拼成多余的前缀。 */
        try { ResetTriggerState("钩子重装"); } catch (Exception ex) { CoreLog.Swallow(ex, "重装钩子前复位状态"); }

        _hook.Restart();

        /* ⚠ 这里**不能**刷新「钩子活着」的时间锚。
         *
         * 重装成功只代表 SetWindowsHookEx 返回了句柄，不代表它真的会回调。
         * 一旦刷新，兜底的"静默超过 10 秒"判据就要从此刻重新计满 10 秒，
         * 而这段时间里热键是彻底不响应的 —— 等于把兜底自己废掉。
         * 连漏计数也**不清**：清了以后紧跟着的每个键都要重新攒 3 次，
         * 用户会看到"重装之后头两个键又丢了"。 */
        RawFallbackActive = RawFallbackActive;
    }

    /// <summary>
    /// 钩子线程还活着吗。
    ///
    /// 句柄在、<see cref="IsHookAlive"/> 为 true，并不等于真的还在收键 ——
    /// 被系统摘钩或线程卡死时两者都还是 true。录制前 / 诊断时用它探一下。
    /// </summary>
    public bool PingHook(int timeoutMs = 400) => _hook.Ping(timeoutMs);

    /// <summary>钩子回调的最长耗时（毫秒），长期超过 100ms 就要查是谁卡住了。</summary>
    public double HookMaxCallbackMs => _hook.MaxCallbackCostMs;

    /// <summary>钩子句柄是否非零（诊断用）。</summary>
    public bool HookInstalled => _hook.HookInstalled;

    /// <summary>本进程当前有没有在装钩子（宿主模式下为 false）。</summary>
    public bool HookOwned => _hook.IsRunning;

    /// <summary>
    /// 钩子是否由独立宿主进程负责。
    ///
    /// 为 true 时：<see cref="IsHookAlive"/> 一律报"正常"，
    /// 各处的看门狗/自愈逻辑都不会再去装第二个钩子（两个钩子会互相打架）。
    /// </summary>
    public bool HookDelegatedToHost { get; set; }

    /// <summary>钩子线程状态（诊断用）。</summary>
    public string HookThreadState => _hook.ThreadState;

    /// <summary>钩子回调是不是正卡着（毫秒，0 = 没卡）。</summary>
    public long HookCallbackStuckMs => _hook.CallbackStuckMs;

    /// <summary>卡住时正在处理的键（诊断用）。</summary>
    public int HookStuckVk => _hook.StuckVk;

    /// <summary>
    /// 钩子自检：自己敲一个不带标记的 F24，看钩子收不收得到。
    /// false = 钩子已经不在链上了（或线程卡死），应当重装。
    /// </summary>
    public bool? ProbeHook(int timeoutMs = 400) => _hook.SelfProbe(timeoutMs);

    /// <summary>
    /// 全局鼠标按键事件透传（屏幕物理坐标 + 鼠标消息）。
    /// Qbar 面板打开期间订阅，实现"点面板外面就收起" ——
    /// 任务栏不换前台、Deactivated 不触发，只能靠这个兜底。
    /// 回调运行在钩子线程：只做判断，别碰界面。
    /// </summary>
    public event Action<int, int, int>? MouseDownEvent
    {
        add => _hook.MouseDownEvent += value;
        remove => _hook.MouseDownEvent -= value;
    }

    /// <summary>诊断：让钩子把收到的按键写进日志。</summary>
    public void EnableKeyTrace() => _hook.TraceKeys = true;

    /// <summary>诊断：钩子累计收到的按键数。</summary>
    public long KeyEventCount => _hook.KeyEventCount;

    /// <summary>
    /// 挂起 / 恢复的真正入口之一：宏录制器被设置时自动挂起，
    /// 被清空（null）时自动恢复。录制期间层逻辑完全不拦截按键 ——
    /// 否则用户按 Caps+X 想录"打开 Qbar"，屏幕上却真弹出了 Qbar，
    /// 录到的原始键序与回放时的行为对不上。
    /// </summary>
    public MacroRecorder? MacroRecorder
    {
        get => _recorder;
        set
        {
            _recorder = value;
            // 挂起/恢复走属性 setter，复用状态复位逻辑
            Suspended = value != null;
        }
    }

    private MacroRecorder? _recorder;

    /// <summary>
    /// 宏播放器。引擎用它做两件事：播放中按 Esc 取消、以及判断"当前是否在播放宏"。
    /// 由 AppHost 在启动时接上。
    /// </summary>
    public MacroPlayer? MacroPlayer { get; set; }

    /// <summary>直接喂一个按键给状态机（不经过真实钩子），用于自检。</summary>
    public bool SimulateKey(int vk, bool down) => OnKeyEvent(vk, down, injected: false);

    /// <summary>
    /// 仅供自检 / 探针：把注入的按键也当作真实按键处理。
    ///
    /// 正常运行时必须保持 false —— 否则自己发出的合成按键会被自己再次拦截，
    /// 形成自我触发。测试无法产生真实硬件按键，只能靠合成，故测试时打开。
    /// </summary>
    public bool AcceptInjectedForTest { get; set; }

    /// <summary>
    /// 仅供自检：跳过长按阈值，触发键一按下就立即进入层模式。
    ///
    /// 真实运行靠 <see cref="_holdTimer"/> 在阈值到点时切层；
    /// 自检没有真实时间流逝，无法等 200ms，所以用这个开关直接进层。
    /// </summary>
    public bool EnterLayerImmediatelyForTest { get; set; }

    /// <summary>
    /// 仅供自检：模拟一次"短按"（不进入层模式）。
    ///
    /// <see cref="EnterLayerImmediatelyForTest"/> 会让每次按下都进层，
    /// 那样就没法测短按 / 双击路径了。自检在需要测短按时打开这个开关，
    /// 让本次按下按"未超时"处理。
    /// </summary>
    public bool SimulateShortPressForTest { get; set; }

    /// <summary>
    /// 迄今为止"补发原键（短按回放）"执行过多少次。仅供自检观察：
    /// 长按绝不该产生补发，否则就是"松手后又跳一次焦点"。
    /// </summary>
    public int TapReplayCountForTest { get; private set; }

    /// <summary>
    /// 钩子回调抛异常时的兜底决策（fail-closed）。
    ///
    /// 语义必须与正常路径一致，否则会凭空造出"层里按什么都没反应"：
    ///   · 层已激活   → 这个键本来就归本层接管 → 吞；
    ///   · 触发键按住 → 只吞触发键自己（其余键还没进层，放行不影响日常输入）；
    ///   · 其余情况   → 放行，与异常发生前的行为保持一致。
    /// </summary>
    private int _errorStreak;
    private long _lastErrorAtMs;

    internal bool ShouldSwallowOnError(int vk)
    {
        CountEngineError();

        if (!Enabled) return false;
        if (_layerActive) return true;
        if (!_triggerHeld) return false;
        try { return IsTrigger(_settings(), vk); }
        catch { return true; }   // 连判定都抛了：按触发键处理，宁可吞掉
    }

    /// <summary>
    /// 统计回调异常；短时间内连续多次 → 状态机可能已经不一致，做一次恢复。
    ///
    /// 为什么必须做：异常多半发生在"按下被吞、状态只更新了一半"的时候；
    /// 接着后续按键就会带着错误的修饰键前缀、或者层永远退不出去。
    /// 恢复动作走后台线程：钩子线程里只计数，不做输入操作。
    /// </summary>
    private void CountEngineError()
    {
        long now = Environment.TickCount64;

        /* 超过 10 秒清零：偶发的单次异常不需要恢复。 */
        if (now - _lastErrorAtMs > 10_000) _errorStreak = 0;
        _lastErrorAtMs = now;

        if (++_errorStreak < 3) return;
        _errorStreak = 0;

        CoreLog.Warn("按键回调连续异常 3 次，执行一次状态恢复（松开改键目标键与修饰键，复位层）");

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                ResetTriggerState("回调连续异常");
                ReleaseRemappedKeys();
                InputSender.ReleaseTrackedModifiers();
                InputSafety.ReleaseAll();
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "异常后恢复"); }
        });
    }
}
