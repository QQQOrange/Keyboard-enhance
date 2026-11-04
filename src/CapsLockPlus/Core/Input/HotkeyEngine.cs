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
        if (!_triggerHeld && !_layerActive && pendingRemaps == 0) return;
        CoreLog.Info($"引擎状态复位（{reason}）：丢弃未完成的触发键会话");
        _triggerHeld = false;
        _layerActive = false;
        _activeTriggerVk = -1;
        _activeTrigger = null;
        _activeLayer = -1;
        _swallowedDown.Clear();
        if (_swallowedUp.Count > MaxPendingUps) _swallowedUp.Clear();
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
    /// </summary>
    public bool CaptureMode { get; set; }

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
    public void TickHold() => CheckHoldThreshold();

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

        if (!ShouldUseRawFallback(hookCount, _lastHookSample, _hookSeenAtMs, nowMs, out bool hookJustMoved)
            || _missedStreak < 3)
        {
            if (hookJustMoved) _missedStreak = 0;
            _lastHookSample = hookCount;
            return false;
        }

        _lastHookSample = hookCount;
        if (hookJustMoved)
        {
            /* 钩子回来了：交还给它，别两边都喂。 */
            _hookSeenAtMs = nowMs;
            if (RawFallbackActive) CoreLog.Info("键盘钩子恢复，热键交还给它");
            RawFallbackActive = false;
            return false;
        }

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

    /// <summary>重装钩子（自愈用）。</summary>
    public void RestartHook()
    {
        /* 宿主模式下钩子**不在本进程**：在这里 Restart 只会装出第二个钩子，
         * 于是同一按键被两个引擎各执行一次（历史上"按一下打开两个程序"的事故）。
         * 要在宿主里重装，主程序发 REHOOK 命令给宿主。 */
        if (HookDelegatedToHost) return;

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
    internal bool ShouldSwallowOnError(int vk)
    {
        if (!Enabled) return false;
        if (_layerActive) return true;
        if (!_triggerHeld) return false;
        try { return IsTrigger(_settings(), vk); }
        catch { return true; }   // 连判定都抛了：按触发键处理，宁可吞掉
    }

    /// <summary>
    /// 钩子回调。返回值 = 是否吞掉该按键。
    /// 这里只做状态判断与入队，绝不做耗时操作（否则系统会摘钩）。
    ///
    /// 触发键的吞键策略（标准 tap-hold，与 QMK / kanata 一致）：
    ///   * 按下瞬间**吞掉**，不让原功能立刻发出（此时还不知是短按还是长按）；
    ///   * 若在阈值内松开 = **短按** → 用 SendInput 补发一次真实的按下+抬起，
    ///     原功能（切换大小写等）正常生效；
    ///   * 若超过阈值 = **长按** → 进入层模式，原功能永不发出。
    ///
    /// ⚠ 早先的实现是"按下放行、超阈值再补发 up"，看似更简单，但有致命问题：
    ///    按下时 CapsLock 已经被系统处理（大写已切换），而补发 up 又无法撤销
    ///    那次切换 —— 结果是"长按进层时大小写被莫名切了一下"，
    ///    而且系统认为 CapsLock 一直按着（因为真实 up 被吞了）。
    ///    现在的做法从根上避免了这两个问题。
    /// </summary>




    private bool OnKeyEvent(int vk, bool down, bool injected)
    {
        /* 捕获模式：只上报、不吞键。 */
        if (CaptureMode)
        {
            if (!injected && KeySeen != null) { try { KeySeen(vk, down, ActiveLayer); } catch { } }
            return false;
        }

        if (injected && !AcceptInjectedForTest) return false;   // 自己注入的按键放行

        _lastKeyActivity = DateTime.Now;

        /* 先取"本次事件到来之前这个键是否已经处于按下状态"，再把本次事件
         * 写进物理状态表。顺序不能反 —— 先写后读的话，本次 down 永远
         * 显示"已按下"，就再也分不出首次按下和自动重复了。
         *
         * 钩子对**所有**按键（含被吞掉的触发键/层内键）都会回调到
         * OnKeyEvent，所以这张表是可靠的物理按下视图；
         * 也正因为如此，<see cref="OnTriggerDown"/> 可以不再依赖
         * "两次 down 间隔"这种会被键盘重复速率影响的猜测。 */
        bool wasPhysicallyDown = _physDown.ContainsKey(vk);
        if (down) _physDown[vk] = 0;
        else _physDown.TryRemove(vk, out _);

        /* 测试页专用：把真实按键抛出去。
         *
         * 放在最前面（暂停判断、触发键判断之前）——
         * 用户要排查"按了没反应"时，第一条要确认的就是
         * "钩子到底收到这个键没有"。放后面的话，
         * 暂停状态或提前 return 的按键就看不见了。
         *
         * 界面异常绝不能影响键盘钩子，所以吞掉所有异常。 */
        if (!injected && KeySeen != null)
        {
            try { KeySeen(vk, down, ActiveLayer); } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        }

        /* 外部拦截器（Qbar 的 Esc 等）：在物理状态记账、上报之后调用，
         * 这样界面的"按键测试"仍然看得到这个键。 */
        if (KeyInterceptor is { } interceptor)
        {
            try { if (interceptor(vk)) return true; }
            catch (Exception ex) { CoreLog.Swallow(ex, "按键拦截器"); }
        }

        // 录制中：照常放行（不影响用户正常操作），只记录事件
        MacroRecorder?.Record(vk, down);

        /* ---- 0. 宏播放中按 Esc = 取消 ----
         *
         * 必须放在 Enabled/Suspended 判断**之前**：无限循环的宏唯一的退出
         * 通道就是这个，引擎一旦被暂停，Esc 再也转发不到 Cancel，
         * 用户只能杀进程 —— 而进程被杀时播放线程不执行 finally，
         * 已按下的修饰键会粘住。
         *
         * 不吞掉 Esc，让它照常传给前台程序（用户可能确实想按 Esc）。 */
        if (down && vk == VK.Escape && MacroPlayer?.IsPlaying == true)
        {
            MacroPlayer.Cancel();
        }

        if (!Enabled || Suspended) return false;                // 暂停状态

        var settings = _settings();

        /* ---- 0.2 钩子吞不住键：完全不介入 ----
         *
         * 这是"Tab 一直往后跳 + 松开后层卡住"的兜底：
         * 钩子一旦被系统摘掉，我们根本挡不住按键，就不该再假装能挡。
         * 把残留的触发键会话清掉，之后所有按键原样放行 —— 用户的键盘
         * 至少是"正常键盘"，而不是"随便按一下就触发层里功能"的坏键盘。 */
        if (SwallowUnavailable)
        {
            if (_triggerHeld || _layerActive) ResetTriggerState("钩子不可用");
            return false;
        }

        /* ---- 0.5 在判定触发键之前，先记录修饰键的按下状态 ----
         *
         * 组合触发键（Ctrl+Shift+K）需要知道"此刻按住了哪些修饰键"。
         * 之前 `_ctrlDown` 等字段只在**进层之后**（OnKeyDownInLayer）才维护，
         * 于是触发前按住 Ctrl 时状态仍是 false，组合触发键永远匹配不上。
         *
         * 这里在**每个按键事件**上同步修饰键状态，
         * 保证判定触发键时读到的是最新状态。
         * 抬起时也要清，否则松开 Ctrl 后状态会残留。
         *
         * ⚠ **触发键自己绝不能算"按住的修饰键"**。
         *
         * 把「右 Alt」设成触发键时，按住期间 Windows 会不断发来处理自动重复的
         * down 事件（长按 500ms 后开始，每 30~50ms 一次）。如果照样记进
         * `_laltDown`，层内每次按键都会被拼成 `caps_lalt_q` —— 而用户绑的是
         * `caps_q`，于是查不到绑定、按键原样放行。
         * 表现就是"右 Alt 当触发键，层里的绑定全都不生效，按键还是原功能"，
         * 而且**按住时间越长越必然出现**（自动重复一定已经开始了）。
         *
         * 同理，Ctrl / Shift / Win 做触发键时也会踩到这条。 */
        if (!(_triggerHeld && vk == _activeTriggerVk))
            UpdateModifierState(vk, down);

        /* ---- 0.9 阈值即时判定：不依赖界面线程的计时器 ----
         *
         * 长按判定原本只由界面线程的 DispatcherTimer 驱动，而它是
         * Background 优先级：界面一忙就顺延，200ms 的阈值实际可能
         * 300~400ms 才生效。这段"已按住、还没进层"的窗口是漏洞来源 ——
         * 子层键会漏给前台（Tab 连续跳焦点），松手还会被判成短按。
         *
         * 钩子每收到一个按键就先自己比一次时间戳：只要用户还在动键盘，
         * 切层就发生在下一个事件上，与界面线程调度无关。
         * 计时器仍然保留 —— 它负责"按住之后一个键都不按"的纯等待场景。 */
        if (down && _triggerHeld && !_layerActive)
        {
            int holdNow = _activeTrigger?.HoldMs ?? 200;
            if ((DateTime.Now - _triggerDownTime).TotalMilliseconds >= holdNow)
                OnHoldThresholdReached();
        }

        // ---- 1. 这个键是不是触发键？ ----
        /* ⚠ 片段缓冲区**不能**在这里无条件清空。
         *
         * 这一行原来是 `_snippetBuffer.Clear()`，写在"每个按键"上 ——
         * 而 ObserveSnippet 只追加当前这一个字符，于是缓冲区永远只有 1 个字，
         * `EndsWith(触发词)` 只可能匹配单字符触发词。
         * 用户配的 `;mail` / `;log` 这类长度大于 1 的片段**永远不展开**，
         * 而这个功能看起来"装好了"（配置界面、自检的纯函数都正常）。
         *
         * 现在只在"这个键被引擎吃掉"的地方清空 —— 被吞掉的键本来就不该
         * 参与拼接，放行的键才按顺序累加。 */
        if (IsTrigger(settings, vk))
        {
            _snippetBuffer.Clear();     // 被吞掉的键不参与片段拼接
            // 按下和抬起都吞掉：短按的原功能由 OnTriggerUp 里补发，
            // 长按则彻底不发出。这样不会污染系统的按键状态。
            if (down) OnTriggerDown(settings, vk, wasPhysicallyDown);
            else OnTriggerUp(settings, vk);
            return true;
        }

        // ---- 2. 触发键按住中，但还没到长按阈值 ----
        if (_triggerHeld && !_layerActive)
        {
            /* 按住触发键期间又按了别的键 → **立刻进层**，不等阈值。
             *
             * 为什么必须这样：如果非要等满 200ms 才进层，
             * 那用户每次想按组合键（Caps+Q、Caps+Ctrl+K）都得先"等一下"，
             * 手感非常别扭 —— 组合键会显得"时灵时不灵"。
             *
             * 判据也很自然：又按了别的键，说明用户明确是想用组合键，
             * 而不是单纯点一下触发键。纯点一下（没碰别的键）仍然走短按，
             * 保留切换大小写这类原功能。
             *
             * 这是 QMK / kanata 里 tap-hold 的标准做法（hold-on-other-key-press）。 */
            if (down)
            {
                /* 子层键即使没开 HoldOnOtherKey 也要立刻进层。
                 *
                 * 否则在"触发键已按下、还没到长按阈值"这段窗口里，子层键会
                 * 走到下面第 3 步被放行 —— 按住就触发它的原生功能
                 * （最典型的就是 Tab 连续跳焦点），而层始终进不去。
                 * 用户关掉 HoldOnOtherKey 本意是"组合键要等一等"，
                 * 不是"子层键漏给前台"。 */
                string? pressedName = KeyName.NameOf(vk);
                bool isSubLayerKey = pressedName != null && IsSubLayerKey(settings, pressedName);

                if (_activeTrigger?.HoldOnOtherKey == true || isSubLayerKey)
                {
                    _layerActive = true;
                    _tapHandled = true;      // 已经不算"干净短按"了
                    CoreLog.Info($"触发键 {_activeTrigger?.Key} 期间按了" +
                                 $"{(isSubLayerKey ? "子层键" : "其它键")}，立即进入第 {_activeLayer} 层");
                }
            }
        }

        // ---- 3. 不在层模式：放行，但先把"欠着的抬起"结清 ----
        if (!_triggerHeld || !_layerActive)
        {
            /* 下面每个 `return true` 都是"这个键被引擎吃掉了"，
             * 都要顺手清掉片段缓冲区（被吞掉的键不参与拼接）。 */

            /* 组合键：命中就把这个键扣住等第二个键，超时再放行。 */
            /* 双击动作：只对配置过的键生效，单击完全保持原样。 */
            if (down && TryRunDoubleTapGesture(settings, vk, wasPhysicallyDown))
            {
                _snippetBuffer.Clear();
                return true;
            }
            if (!down && _gestureSwallowUp.Remove(vk)) { _snippetBuffer.Clear(); return true; }

            /* 按应用的叠加层：只接管**绑过**的组合，未绑定的键原样放行。 */
            if (down && TryRunAppLayerBinding(settings, vk))
            {
                _snippetBuffer.Clear();
                return true;
            }

            /* 基础改键：只要不在层里就生效。
             * 放在"放行"之前 —— 命中就吞掉原键并改成目标键。 */
            if (TryApplyRemap(settings, vk, down)) { _snippetBuffer.Clear(); return true; }

            /* 按下被我们吞过、抬起还没结账的键，这里必须补吞。
             *
             * 典型路径：用户先松开触发键、再松开层内键（Tab）。
             * OnTriggerUp 已经把层状态清掉，于是这个 up 落到这里 ——
             * 直接放行的话，前台会收到一个"从未按下过的抬起"。
             * 记事本这类程序无感，但游戏 / 远程桌面 / WebView2 会判定
             * 输入异常，个别实现甚至认为该键一直按着、反复自重复。
             *
             * ⚠ 这里的 `_swallowedDown` 也**必须**一起清：
             * 它是"这个键的按下被吞过"的账本，同时被「按应用叠加层」
             * 拿来当自动重复的判据（`ContainsKey` 就当作重复 → 直接吞掉）。
             * 只在抬起时清 `_swallowedUp`，账本会永远留着这个键 ——
             * 表现是：按应用叠加层里绑的快捷键**只生效一次**，
             * 第二次按下被静默吃掉、动作不执行，抬起还漏给前台。 */
            if (!down)
            {
                _swallowedDown.TryRemove(vk, out _);
                if (_swallowedUp.TryRemove(vk, out _)) { _snippetBuffer.Clear(); return true; }
                return false;
            }

            /* 文本片段：只有"真的会打出去"的键才计入缓冲区。
             * 带 Ctrl/Alt/Win 的是命令，不是输入，直接断开。 */
            if (_ctrlDown || _laltDown || _lwinDown || _rwinDown)
            {
                _snippetBuffer.Clear();
            }
            else ObserveSnippet(settings, vk);

            return false;
        }

        // ---- 4. 层模式内的按键 ----
        _snippetBuffer.Clear();     // 层内按键一律被接管，不参与拼接
        return down ? OnKeyDownInLayer(vk, wasPhysicallyDown) : OnKeyUpInLayer(vk);
    }

    /// <summary>
    /// 触发键的虚拟键码集合（缓存）。
    ///
    /// <see cref="IsTrigger"/> 在**每一个按键**的钩子回调里都要跑一次。
    /// 原实现每次都遍历 Triggers 并对每个 Key 做一次
    /// `KeyName.VkOf()`（字符串 → 字典查找），也就是每次按键都做
    /// 1~N 次字符串比较 + 字典查找 —— 纯浪费，而钩子回调对延迟极其敏感
    /// （超过 LowLevelHooksTimeout 会被系统静默摘钩）。
    ///
    /// 失效判据用**内容指纹**而不是"列表实例 + 条数"：
    /// 用户在界面上改触发键的 Key 时列表实例和条数都不变，
    /// 只看那两项会导致"改了触发键不生效"。指纹里带上每个键名和启用状态。
    /// </summary>
    private List<TriggerMatch>? _triggerMatchCache;
    private string _triggerVkCacheStamp = "";

    private static string TriggerStamp(List<TriggerKey> list)
    {
        // 参数很少（通常 1~3 个），拼串开销远小于每次按键都查字典
        var sb = new System.Text.StringBuilder(32);
        foreach (var t in list)
        {
            sb.Append(t.Enabled ? '1' : '0').Append(t.Key).Append('|');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 一个触发键的匹配条件。
    ///
    /// 触发键**只允许单一按键**：一个物理键按下即进层。
    /// 组合键（Ctrl+K 之类）不能当触发键 —— 那会和"层内修饰键子层"
    /// 语义冲突：同样是 Ctrl+K，到底算触发还是算子层绑定？
    ///
    /// 组合键保留给**层内绑定**（进层后按住 Ctrl 再按 K），
    /// 两套机制职责清晰。
    /// </summary>
    private readonly record struct TriggerMatch(int Vk)
    {
        /// <summary>
        /// 用 <see cref="SameKey"/> 而不是直接比大小：
        /// 有些驱动发通用修饰码（0x12），有些发左右专用码（0xA5），
        /// 触发键配置里写的又是其中一种 —— 只比大小会"配置对了却按不出来"。
        /// </summary>
        public bool Matches(int vk) => SameKey(Vk, vk);
    }

    private List<TriggerMatch> TriggerMatchList(AppSettings s)
    {
        var list = s.Triggers;
        string stamp = TriggerStamp(list);
        if (_triggerMatchCache != null && _triggerVkCacheStamp == stamp)
            return _triggerMatchCache;

        var matches = new List<TriggerMatch>();
        foreach (var t in list)
        {
            if (!t.Enabled) continue;
            if (ParseTrigger(t.Key) is { } m) matches.Add(m);
        }

        _triggerMatchCache = matches;
        _triggerVkCacheStamp = stamp;
        return matches;
    }

    /// <summary>
    /// 把触发键字符串解析成匹配条件。
    ///
    /// **只接受单一按键**：如果配置里写了组合键（如 <c>caps_ctrl_k</c>），
    /// 这里会取出主键 K 并当作单键触发 —— 这样老配置不会彻底失效，
    /// 但也明确表达"组合触发已不再支持"。
    /// </summary>
    /// <summary>
    /// 诊断用：把外部配置里的键名解析结果描述出来（--dump-triggers）。
    /// 返回人类可读的结论，顺便回答"这个触发键会不会匹配 Tab(0x09)"。
    /// </summary>
    internal static string DescribeTriggerForDiag(string? key)
    {
        var m = ParseTrigger(key);
        if (m == null) return "解析失败（键名认不出来，永远不会触发）";
        return m.Value.Matches(0x09) ? "解析成功，且匹配 Tab(0x09)" : "解析成功";
    }

    private static TriggerMatch? ParseTrigger(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        // 组合键 ID 形式：取主键，忽略修饰部分
        var parsed = KeyName.ParseCombo(key);
        if (parsed != null)
        {
            var (_, k) = parsed.Value;
            if (KeyName.VkOf(k) is int vk && vk != 0) return new TriggerMatch(vk);
        }

        if (KeyName.VkOf(key) is int single && single != 0)
            return new TriggerMatch(single);

        return null;
    }

    /// <summary>
    /// 同步修饰键的按下/抬起状态。
    ///
    /// 必须对**左右两个键码和通用码**都认：有些键盘/驱动只发 0x11/0x10/0x12，
    /// 完全不发 LControl/RControl 这些具体码。
    /// </summary>
    private void UpdateModifierState(int vk, bool down)
    {
        switch (vk)
        {
            case VK.LControl or VK.RControl or 0x11:
                _ctrlDown = down;
                break;
            case VK.LShift or VK.RShift or 0x10:
                _shiftDown = down;
                break;
            case VK.LMenu or VK.RMenu or 0x12:
                _laltDown = down;
                break;
            case VK.LWin:
                _lwinDown = down;
                break;
            case VK.RWin:
                _rwinDown = down;
                break;
            default:
                return;      // 不是修饰键，什么都不做
        }

        // 抬起的修饰键也从吞掉集合里移除，避免残留
        if (!down) _swallowedUp.TryRemove(vk, out _);
    }

    /// <summary>
    /// 把某个键对应的修饰键标记清零（不管它当前是什么状态）。
    ///
    /// 专供"触发键本身就是修饰键"的场景：右 Alt 按住期间的自动重复
    /// 不能把自己算成"按住的 Alt"，否则层内绑定全部查不到。
    /// </summary>
    private void ClearModifierFlag(int vk)
    {
        switch (vk)
        {
            case VK.LControl or VK.RControl or 0x11:
                _ctrlDown = false;
                break;
            case VK.LShift or VK.RShift or 0x10:
                _shiftDown = false;
                break;
            case VK.LMenu or VK.RMenu or 0x12:
                _laltDown = false;
                break;
            case VK.LWin:
                _lwinDown = false;
                break;
            case VK.RWin:
                _rwinDown = false;
                break;
        }
    }

    /// <summary>
    /// 两个键码是否指同一个物理键。
    ///
    /// 修饰键有三种写法：左右专用码（0xA4 / 0xA5）、通用码（0x12）。
    /// 不同键盘 / 驱动 / 远程桌面会话发的并不一致 ——
    /// 触发键配的是 "ralt"（0xA5），某些环境却只发 0x12，
    /// 或者按下发专用码、抬发送通用码。
    ///
    /// 只做**同一修饰键内部**的等价：左 Alt 和右 Alt 仍然互不相等，
    /// 用户想分别绑两个键的需求不受影响。
    /// </summary>
    private static bool SameKey(int a, int b) => a == b || (a, b) switch
    {
        (0x12, 0xA4) or (0xA4, 0x12) => true,       // Alt   ↔ 左 Alt
        (0x12, 0xA5) or (0xA5, 0x12) => true,       // Alt   ↔ 右 Alt
        (0x11, 0xA2) or (0xA2, 0x11) => true,       // Ctrl  ↔ 左 Ctrl
        (0x11, 0xA3) or (0xA3, 0x11) => true,       // Ctrl  ↔ 右 Ctrl
        (0x10, 0xA0) or (0xA0, 0x10) => true,       // Shift ↔ 左 Shift
        (0x10, 0xA1) or (0xA1, 0x10) => true,       // Shift ↔ 右 Shift
        _ => false,
    };





    private bool IsTrigger(AppSettings s, int vk)
    {
        return TriggerMatchList(s).Any(m => m.Matches(vk));
    }

    /// <summary>找出这个 VK 对应的触发键配置（组合键也认）。</summary>
    private static TriggerKey? FindTrigger(AppSettings s, int vk)
        => s.Triggers.FirstOrDefault(t =>
            t.Enabled && ParseTrigger(t.Key) is { } m && m.Matches(vk));

    private void OnTriggerDown(AppSettings s, int vk, bool wasPhysicallyDown)
    {
        /* 自动重复：按住触发键时系统会反复发 down。
         *
         * 这里必须先把这个键的修饰键状态清掉 —— 触发键若是右 Alt / 右 Ctrl，
         * 重复的 down 会把 `_laltDown` 又置成 true（上面虽然拦了一道，
         * 但状态可能被其它路径写脏，这里再兜一次底），
         * 层内绑定就会被拼成 caps_lalt_xxx 而永远查不到。
         *
         * ⚠ 但"按住"这个判断本身可能是假的：上一次的抬起事件如果丢了，
         * `_triggerHeld` 会卡在 true，于是**这一次真实的按下被当成自动重复
         * 直接吞掉** —— 用户感受到的就是"触发键偶尔按一下没反应"。
         * 所以先拿物理状态核对：这个键现在其实没被按住 → 按全新按下处理。 */
        long nowTick = Environment.TickCount64;

        if (_triggerHeld)
        {
            /* 区分"系统自动重复"和"用户重新按下"。
             *
             * 主判据改为**物理按下状态**：钩子已经看到过这个键的 down、
             * 还没看到 up（wasPhysicallyDown == true），那它就是按住不放
             * 产生的重复 down，与键盘重复速率无关。
             *
             * 旧实现只看"两次 down 间隔 < 100ms"：Windows 的首次重复延迟
             * 默认 250~500ms 且可调，用户把重复速率调慢后，第二次起的 down
             * 全部落在 100ms 之外，被误判成"重新按下"→ 复位 → 重新进层，
             * 表现就是"层反复被踢掉、停不在目标层"。
             *
             * 兜底：极少数环境里 up 事件会丢，物理状态还残留；但 100ms 内
             * 不可能有人真的"松开再按下"，这种情况仍按重复处理。 */
            bool isAutoRepeat = wasPhysicallyDown
                                || (_lastTriggerDownTick != 0
                                    && nowTick - _lastTriggerDownTick < 100);

            if (isAutoRepeat)
            {
                _lastTriggerDownTick = nowTick;
                ClearModifierFlag(vk);
                return;      // 真的自动重复
            }

            CoreLog.Info($"触发键 0x{_activeTriggerVk:X2} 重新按下（上次会话没有正常结束），复位后重新开始");
            ResetTriggerState("触发键重新按下");
        }

        _lastTriggerDownTick = nowTick;

        /* ⚠ 先确定是哪个触发键，**再**清零修饰键状态。
         *
         * 组合键触发（Ctrl+Shift+K）依赖"当前按住哪些修饰键"来判断，
         * 一旦先清零就永远匹配不上 —— 组合触发键会彻底失效。 */
        var trigger = FindTrigger(s, vk);

        _triggerHeld = true;
        _triggerDownTime = DateTime.Now;
        RequestFastHoldTick();
        _usedCombo = false;
        _usedOtherKey = false;
        _tapHandled = false;

        /* 只清**自己**的修饰键标记，不能把五个全清。
         *
         * 早先这里是 `_laltDown = _lwinDown = ... = false` ——
         * 用户先按住 Shift 再按触发键时，Shift 明明还按着，
         * 却被这一行抹掉了，层内的 caps_shift_xxx 绑定全部查不到。
         * 而 Shift 的 down 早就放行给了系统，用户以为自己在按 Shift。 */
        ClearModifierFlag(vk);

        _swallowedDown.Clear();          // 上一次会话的"重复判定"作废
        /* _swallowedUp 不能整体清：它是"按下被吞、抬起还没来"的欠账。
         * 清掉之后，这些键的 up 会漏给前台（见 OnKeyEvent 第 3 步）。
         * 只在异常堆积（up 真的丢了）时兜底。 */
        if (_swallowedUp.Count > MaxPendingUps) _swallowedUp.Clear();

        _activeTriggerVk = vk;
        _activeTrigger = trigger;
        _activeLayer = trigger?.LayerId ?? 1;

        // 自检模式：立刻进层，不等真实时间流逝
        if (EnterLayerImmediatelyForTest && !SimulateShortPressForTest)
            OnHoldThresholdReached();
        else
            _layerActive = false;

        TriggerHeldChanged?.Invoke(true);
    }

    private void OnTriggerUp(AppSettings s, int vk)
    {
        /* 先清掉自己的修饰键标记：触发键是修饰键时（右 Alt / 右 Ctrl），
         * 抬起绝不能被当成"释放了一个层内修饰键"。 */
        ClearModifierFlag(vk);

        // 允许"按下 0xA5、抬起 0x12"这种混发（见 SameKey）
        if (!_triggerHeld || !SameKey(vk, _activeTriggerVk)) return;

        /* ---- 松手前补做最后一次长按判定 ----
         *
         * 长按判定有两路：钩子在每个按键事件上比对时间戳（主判据）、
         * 界面线程每 20ms 轮询（纯按住时的兜底）。但"按住之后一个键都不按"
         * 的时候只有计时器在跑，界面一忙它就晚到 —— 于是明明按满了阈值，
         * 松手这一刻 _layerActive 仍是 false，被当成短按补发一次原键
         * （用户看到"切完层松手，焦点又跳一下"）。
         *
         * 在清掉 _triggerHeld **之前**补判一次：按满阈值就一定算长按，
         * 该吞的键一个都不会漏出去；也不会出现"长按了却什么都没发生"。
         */
        int holdLimit = _activeTrigger?.HoldMs ?? 200;
        if (!_layerActive &&
            (DateTime.Now - _triggerDownTime).TotalMilliseconds >= holdLimit)
        {
            OnHoldThresholdReached();      // 置 _layerActive / _tapHandled
        }

        _triggerHeld = false;

        var trigger = _activeTrigger;

        /* 这一按算不算"干净短按"。
         *
         * 判据就是"有没有真的进层"：进过层（_layerActive）或已按长按处理过
         * （_tapHandled）就绝不补发；只有从头到尾没进层，才把原键补发出去。
         *
         * 于是"短按发 Tab、长按一个 Tab 都不发"是**同一条判定**推出来的结果，
         * 不需要再额外拿时长去猜 —— 时长只用来决定"进不进层"。 */
        bool cleanTap = !_usedCombo && !_usedOtherKey && !_layerActive;

        // 未进层的干净抬起 = 短按
        if (cleanTap && !_tapHandled && trigger != null)
        {
            /* 短按：**补发一次真实的按下+抬起**，让原功能生效。
             *
             * 因为整个按住过程都被我们吞掉了（见 OnKeyEvent 的注释），
             * 系统完全没收到这个键，所以这里必须自己补一次。
             *
             * 特殊处理「短按动作」：若用户把短按行为改成了别的动作
             * （比如改成 Esc、打开 Qbar），那就执行那个动作，不补发原键；
             * 若短按行为是"切换大小写"（默认），补发真实 CapsLock 即可 ——
             * 效果完全一致，但系统的大写状态是真实翻转的，
             * 不像自己合成按键那样可能与实际状态脱节。
             */
            string tapAction = trigger.TapAction ?? "";

            /* 双击判定必须在"补发原键"之前。
             *
             * 早先的写法把双击判定放在补发分支里面，导致默认配置
             * （TapAction = toggleCapsLock，会走补发原键那条路）时
             * **永远进不到双击分支** —— 双击打开 Qbar 因此失效。 */
            bool doubleTap = !string.IsNullOrEmpty(trigger.DoubleTapAction)
                             && _hasLastTap
                             && (DateTime.Now - _lastTapTime).TotalMilliseconds < DoubleTapWindowMs
                             && (DateTime.Now - _triggerDownTime).TotalMilliseconds < DoubleTapWindowMs;

            if (doubleTap)
            {
                _hasLastTap = false;            // 双击只算一次，避免三连击继续触发
                string dt = trigger.DoubleTapAction;
                _queue.Post(() => RunTapAction(dt));
            }
            else
            {
                _lastTapTime = DateTime.Now;
                _hasLastTap = true;

                /* 短按行为的判定。
                 *
                 * 新增 "keepKey" = **保留按键原功能**（显式选项）。
                 * 以前只有 toggleCapsLock / 空 / doNothing 才走补发原键那条路，
                 * 用户把 F5、问号这类键设成触发键时，短按就不知道该干嘛了
                 * （默认值还是 toggleCapsLock，跟那些键完全不搭）。
                 * 现在「触发键与层」页可以直接选"按键原功能"，
                 * 语义明确：短按 = 当这个键没被改造过一样用。 */
                if (tapAction is "toggleCapsLock" or "" or "doNothing" or "keepKey")
                {
                    /* 补发原键：对 CapsLock 是翻转大写，对 F5 是打开刷新，
                     * 对问号是正常输入 "?" —— 系统状态是真实产生的。 */
                    int vkToSend = vk;
                    _queue.Post(() =>
                    {
                        /* 只挡"层已经生效"这一种情况 —— 那是唯一会让原键
                         * 从层里漏出去的时刻。
                         *
                         * 不能挡 _triggerHeld：用户快速连按两下时，第二次按下
                         * 会把第一次的补发一起吞掉，表现为"按了没反应"。
                         * 补发属于**已经结束的那一次**按键，与新的按住无关。 */
                        if (_layerActive) return;
                        TapReplayCountForTest++;
                        InputSender.Tap(vkToSend);
                    });
                }
                else
                {
                    string ta = tapAction;
                    _queue.Post(() => RunTapAction(ta));
                }
            }
        }

        _layerActive = false;
        _activeTriggerVk = -1;
        _activeTrigger = null;
        _activeLayer = -1;
        /* 只清"重复判定"表。欠着的抬起继续留在 _swallowedUp 里，
         * 等它自己的 up 到达时由 OnKeyEvent 第 3 步吞掉 ——
         * 否则"先松触发键、后松层内键"就会漏一个孤儿 keyup 给前台。 */
        _swallowedDown.Clear();
        if (_swallowedUp.Count > MaxPendingUps) _swallowedUp.Clear();
        /* 子层状态跟着触发键一起退出：否则松开触发键后 ActiveSubLayer
         * 还停在 "tab"，界面会显示一个不存在的子层。 */
        if (!string.IsNullOrEmpty(_subLayerKeyDown))
        {
            _subLayerKeyDown = "";
            SubLayerChanged?.Invoke("");
        }
        ClearContinuousRepeat();      // 层都没了，连续调节当然要停
        TriggerHeldChanged?.Invoke(false);
    }

    /// <summary>
    /// 长按判定：由 UI 线程的计时器调用（钩子回调里不能做定时）。
    /// 超过阈值就进入层模式，并触发一次短按动作（因为用户没松手，
    /// 不会再有机会走"抬起时短按"分支）。
    /// </summary>
    private void OnHoldThresholdReached()
    {
        if (!_triggerHeld || _layerActive) return;
        _layerActive = true;
        _tapHandled = true;      // 已经算长按，抬起时不再补发原功能

        /* ⚠ 这里**不能**执行短按动作。
         *
         * 早先的实现除了切层，还顺手跑了一次 TapAction（切换大小写）——
         * 结果就是"每次长按进层，大写状态都被莫名翻一次"。
         * 长按的语义就是"这个键变成功能键前缀"，它本身什么都不该做。 */
    }

    /// <summary>执行短按动作。统一走动作表，便于在按键映射界面里改。</summary>
    private void RunTapAction(string action)
    {
        try
        {
            if (!string.IsNullOrEmpty(action) && action != "doNothing")
                _invoke(action, "");
        }
        catch (Exception ex) { CoreLog.Error("短按动作失败", ex); }
    }

    /// <summary>层模式下按下某个键。返回 true 表示吞掉。</summary>
    /// <summary>
    /// 叠加层：前台命中"按应用启用"的规则时，先查这一层有没有绑定。
    ///
    /// 与触发键的层的**根本区别**：这里不接管未绑定的键。
    /// 触发层的语义是"键盘交给这一层"，而叠加层是"在原有键盘上多几个快捷键"——
    /// 否则在 IDE 里打字会被整片吞掉。
    /// </summary>
    private bool TryRunAppLayerBinding(AppSettings s, int vk)
    {
        int layer = _appLayerId;
        if (layer <= 0) return false;

        // 系统自动重复：已经执行过，吞掉但不再触发
        if (_swallowedDown.ContainsKey(vk)) return true;

        string? keyName = KeyName.NameOf(vk);
        if (keyName == null) return false;

        var mods = KeyModifiers.None;
        if (_laltDown) mods |= KeyModifiers.Alt;
        if (_lwinDown || _rwinDown) mods |= KeyModifiers.Win;
        if (_ctrlDown) mods |= KeyModifiers.Ctrl;
        if (_shiftDown) mods |= KeyModifiers.Shift;

        var binding = FindInLayer(s, layer, KeyName.BuildComboEx(mods, keyName));
        if (binding == null || !binding.IsBound) return false;   // 没绑 → 直通

        string action = binding.Action;
        string arg = binding.Arg;

        _swallowedDown.TryAdd(vk, 0);
        _swallowedUp.TryAdd(vk, 0);

        _queue.Post(() =>
        {
            try
            {
                _invoke(action, arg);
                ActionTriggered?.Invoke(action);
            }
            catch (Exception ex) { CoreLog.Error($"动作 {action} 执行失败", ex); }
        });
        return true;
    }



    /// <summary>
    /// 文本片段：把这次按键折算成字符、累计到缓冲区，命中就展开。
    ///
    /// **保守实现**的两条底线：
    ///   · 认不出的键（方向键、功能键、非 US 布局字符）一律**清零**缓冲区，
    ///     宁可丢掉一次展开，也不要跨键拼出假触发词；
    ///   · 只在"不按任何修饰键"的纯文本输入时累计。
    /// </summary>
    private void ObserveSnippet(AppSettings s, int vk)
    {
        var all = s.Global.Snippets;
        if (all == null || all.Count == 0)
        {
            _snippetBuffer.Clear();
            return;
        }

        /* 只认一个前缀符号（用户可改、可清空、可切全角）。
         * 过滤放在匹配之前：既实现"只认一个符号"，又不影响匹配器本身的语义。 */
        var opt = s.Hotstring ?? new HotstringOptions();
        string prefix = opt.EffectivePrefix;
        var list = string.IsNullOrEmpty(prefix)
            ? all
            : all.Where(x => x.Trigger != null && x.Trigger.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        if (list.Count == 0)
        {
            _snippetBuffer.Clear();
            return;
        }

        /* 空格触发：打完触发词**再按空格**才替换（默认）。 */
        if (opt.ExpandOnSpace && vk == 0x20)
        {
            string typed = _snippetBuffer.ToString();
            var ready = SnippetMatcher.Match(list, typed, ForegroundProcess);
            if (ready == null)
            {
                _snippetBuffer.Clear();
                return;                     // 没命中 → 空格照常输入
            }

            ExpandSnippet(ready);
            _snippetSpaceSwallowed = true;  // 通知调用方把这次空格吞掉
            return;
        }

        char? ch = AsciiCharOf(vk, _shiftDown);
        if (ch == null)
        {
            _snippetBuffer.Clear();      // 非文本键：断开
            return;
        }

        _snippetBuffer.Append(ch.Value);
        if (_snippetBuffer.Length > SnippetMatcher.MaxBuffer)
        {
            string trimmed = SnippetMatcher.Trim(_snippetBuffer.ToString());
            _snippetBuffer.Clear();
            _snippetBuffer.Append(trimmed);
        }

        var hit = SnippetMatcher.Match(list, _snippetBuffer.ToString(), ForegroundProcess);
        if (hit == null) return;

        /* 有更长的候选（例如配了 ;m 和 ;mail，现在命中 ;m）就先别展开：
         * 等 120ms，用户接着打 'a' 就说明他要的是 ;mail。
         * 没有更长的候选时立刻展开 —— 常规场景保持"打完就变"的手感。 */
        /* 空格触发模式：命中先攒着，等空格；不在这里展开。 */
        if (opt.ExpandOnSpace) return;

        if (SnippetMatcher.HasLongerCandidate(list, _snippetBuffer.ToString(), ForegroundProcess))
        {
            _snippetPending = hit;
            _snippetPendingAt = DateTime.Now;
            _snippetPendingBuffer = _snippetBuffer.ToString();
            return;
        }

        ExpandSnippet(hit);
    }

    /// <summary>等长候选时的等待窗口。</summary>
    public const int SnippetWaitMs = 120;

    /// <summary>这次空格是否已经被"片段展开"消费掉（调用方据此吞键）。</summary>
    private bool _snippetSpaceSwallowed;

    private Snippet? _snippetPending;
    private DateTime _snippetPendingAt;
    private string _snippetPendingBuffer = "";

    /// <summary>
    /// 定时器每 tick 调一次：等待窗口过了、而且缓冲没再增长（说明用户没继续打），
    /// 就把刚才挂起的那条片段展开。
    /// </summary>
    internal void FlushPendingSnippet()
    {
        var pending = _snippetPending;
        if (pending == null) return;
        if ((DateTime.Now - _snippetPendingAt).TotalMilliseconds < SnippetWaitMs) return;

        _snippetPending = null;

        /* 缓冲变了 = 用户在继续打（或换了键），交给后面新的匹配。
         * 这里在界面线程读、钩子线程写，理论上可能撞上 —— StringBuilder 不保证线程安全，
         * 所以整段兜住：读不出来就当"没命中"，绝不能把异常抛到计时器里。 */
        string now;
        try { now = _snippetBuffer.ToString(); }
        catch { return; }

        if (!string.Equals(now, _snippetPendingBuffer, StringComparison.Ordinal)) return;
        ExpandSnippet(pending);
    }

    /// <summary>真正执行展开（删触发词 + 打内容）。</summary>
    private void ExpandSnippet(Snippet hit)
    {
        SnippetMatchesForTest++;
        _snippetBuffer.Clear();
        _snippetPending = null;

        /* 使用统计：只在内存里加，落盘交给调用方（AppHost 的防抖保存）。 */
        try
        {
            hit.UseCount++;
            hit.LastUsed = DateTime.Now;
            SnippetUsed?.Invoke();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "记录别名使用次数"); }

        int deleteCount = hit.Trigger.Length;
        string text = hit.Text ?? "";

        /* 触发词已经打进前台了，所以要先删掉再输入。
         * 投递到动作队列：钩子里做 IO/发送会拖慢每一次按键。 */
        _queue.Post(() =>
        {
            try
            {
                /* 焦点不在可编辑控件上就放弃展开：
                 * 否则 Backspace 会去删别的东西、内容会打到别处。
                 * 判断放在队列里（STA），不能放钩子里。 */
                if (!Text.EditableTarget.AllowsTextInput()) return;

                for (int i = 0; i < deleteCount; i++)
                {
                    InputSender.Tap(0x08);          // Backspace
                    System.Threading.Thread.Sleep(6);
                }
                InputSender.Text(text);
                CoreLog.Info($"片段展开：{hit.Trigger} → {text.Length} 字");
            }
            catch (Exception ex) { CoreLog.Warn("片段展开失败：" + ex.Message); }
        });
    }

    /// <summary>
    /// 虚拟键 → 字符（US 布局）。认不出返回 null。
    ///
    /// 不走 ToUnicodeEx：那个 API 会改变死键状态，可能干扰输入法。
    /// 这里只认最基本的 ASCII，认不出就让缓冲区断开 —— 安全优先。
    /// </summary>
    private static char? AsciiCharOf(int vk, bool shift)
    {
        if (vk >= 0x41 && vk <= 0x5A) return (char)((shift ? 'A' : 'a') + (vk - 0x41));
        if (vk >= 0x30 && vk <= 0x39)
        {
            if (!shift) return (char)vk;
            const string shiftedDigits = ")!@#$%^&*(";
            return shiftedDigits[vk - 0x30];
        }

        return vk switch
        {
            0x20 => ' ',
            0xBA => shift ? ':' : ';',
            0xBB => shift ? '+' : '=',
            0xBC => shift ? '<' : ',',
            0xBD => shift ? '_' : '-',
            0xBE => shift ? '>' : '.',
            0xBF => shift ? '?' : '/',
            0xC0 => shift ? '~' : '`',
            0xDB => shift ? '{' : '[',
            0xDC => shift ? '|' : '\\',
            0xDD => shift ? '}' : ']',
            0xDE => shift ? '"' : '\'',
            _ => null,
        };
    }

    private bool OnKeyDownInLayer(int vk, bool wasPhysicallyDown)
    {
        /* 「欠着的抬起」只在**本次按下的第一次**记账。
         *
         * 系统自动重复的 down 不能重复记账：用户可能先按住 Down 不放，
         * 再按住触发键进层 —— 这时 Down 的原始 down 早就放给前台了，
         * 若在层内把它的每个重复 down 都记成"按下被吞过"，
         * 松手时那次 up 就会被补吞，前台只收到 down、收不到 up
         * （游戏 / 远程桌面会一直认为这个键按着）。
         * wasPhysicallyDown 正是"这不可能是第一次按下"的判据。 */
        void OweUp(int code)
        {
            if (!wasPhysicallyDown) _swallowedUp.TryAdd(code, 0);
        }

        /* 修饰键：记录状态并吞掉（否则会触发系统级 Alt / Win 菜单）。
         *
         * ⚠ 左右与通用码都要认。早先只认 LControl/LShift/LMenu：
         *   · 按**右** Ctrl 组合会去查无修饰的 caps_k，绑了就错误执行；
         *   · 部分键盘/驱动只发 0x11/0x10/0x12 这三个通用码，
         *     完全没有分支处理，组合键永远拼不出来。 */
        if (vk is VK.LMenu or VK.RMenu or 0x12)
        {
            _laltDown = true;
            OweUp(vk);
            return true;
        }
        if (vk is VK.LWin or VK.RWin)
        {
            if (vk == VK.LWin) _lwinDown = true; else _rwinDown = true;
            OweUp(vk);
            return true;
        }
        if (vk is VK.LControl or VK.RControl or 0x11)
        {
            _ctrlDown = true;
            OweUp(vk);
            return true;
        }
        if (vk is VK.LShift or VK.RShift or 0x10)
        {
            _shiftDown = true;
            OweUp(vk);
            return true;
        }

        /* ---- 安全阀：Esc 永远退出层 ----
         *
         * 层里"没绑定的键也吞掉"，一旦状态卡住，用户会觉得整个键盘失灵，
         * 只能退出程序。给一个一定能按到的出口：Esc。
         * 吞掉它（不发给前台），避免 Esc 在游戏/对话框里触发别的行为。 */
        if (vk == VK.Escape)
        {
            CoreLog.Info("层里按了 Esc：退出层");
            ResetTriggerState("按 Esc 退出层");
            return true;
        }

        string? keyName = KeyName.NameOf(vk);

        /* 不认识的键（键名表里没有）→ 同样吞掉。
         *
         * 用户的要求是"按住触发键时，按的任何键都是层的按键；层里没绑定
         * 就等于空操作"。以前这里放行，结果是按了个没收录的键会漏到前台，
         * 和"层完全接管键盘"的预期不符。 */
        if (keyName == null)
        {
            _usedOtherKey = true;
            _swallowedDown.TryAdd(vk, 0);
            OweUp(vk);
            return true;
        }

        /* 用物理状态校正一遍修饰键标记再拼 combo。
         * 漏掉一次修饰键抬起事件就会让整层的组合键永久错位
         * （全部拼成 caps_ctrl_xxx），这里是唯一的纠偏机会。 */
        SyncModifierFlagsWithPhysical();

        var settings = _settings();

        /* ---- 子层键：可以是任意物理键（Tab / CapsLock / F13…） ----
         *
         * 早期的"层内修饰键"只认 Ctrl/Shift/Alt/Win 四个修饰键；
         * 现在子层键允许任意键，所以要先检查这个键是不是当前层配置的子层键。
         *
         * 命中就**吞掉并记住**，接下来的按键会带上这个前缀去查绑定
         * （例如 Tab 子层 + Q → caps_tab_q）。 */
        /* 已按住的子层键的**系统自动重复**：直接吞掉。
         *
         * 不能让它落进下面的 combo 查找 —— 那会把重复 down 拼成
         * caps_tab_tab 这种不存在的组合键；万一用户真的绑了这个 ID，
         * 按住子层键就会反复触发它。 */
        if (!string.IsNullOrEmpty(_subLayerKeyDown) &&
            string.Equals(keyName, _subLayerKeyDown, StringComparison.OrdinalIgnoreCase))
        {
            _swallowedDown.TryAdd(vk, 0);
            OweUp(vk);
            return true;
        }

        if (string.IsNullOrEmpty(_subLayerKeyDown) && IsSubLayerKey(settings, keyName))
        {
            _subLayerKeyDown = keyName;
            _swallowedDown.TryAdd(vk, 0);   // 记下 down：抬起时成对清理、重复时能被识别
            OweUp(vk);
            SubLayerChanged?.Invoke(keyName);
            return true;
        }

        var mods = KeyModifiers.None;
        if (_laltDown) mods |= KeyModifiers.Alt;
        if (_lwinDown || _rwinDown) mods |= KeyModifiers.Win;
        if (_ctrlDown) mods |= KeyModifiers.Ctrl;
        if (_shiftDown) mods |= KeyModifiers.Shift;

        /* 有非修饰键子层按住时，只查"子层前缀 + 键"的绑定。
         *
         * **不再回退**到普通查法 —— 这一条和触发层保持同一套语义：
         * 按住子层键 = 键盘交给这个子层，子层里没绑就是空操作。
         * 以前会在子层查不到时退回 caps_q，于是"子层里没绑的键"
         * 会去执行基础层的功能，用户看到的就是"子层根本没生效"。
         *
         * ⚠ 修饰键子层（ctrl / ctrl_shift）本来就在 combo 里带上修饰前缀，
         * 走的是下面的 comboId，不受这条影响。 */
        string comboId = KeyName.BuildComboEx(mods, keyName);
        KeyBinding? binding = string.IsNullOrEmpty(_subLayerKeyDown)
            ? FindInLayer(settings, _activeLayer, comboId)
            : FindInLayer(settings, _activeLayer, ComboForSubLayer(_subLayerKeyDown, mods, keyName));

        if (binding == null || !binding.IsBound)
        {
            /* 层内没有绑定的键 → **吞掉**，恢复成"按了什么都不发生"。
             *
             * v4.5.4 按用户要求改了语义：按住触发键 = 键盘完全交给这一层。
             *   · 绑了功能 → 执行功能；
             *   · 没绑    → 空操作（既不执行功能，也不恢复原生输入）。
             *
             * 以前这里是 return false（放行），于是"按住触发键按个没绑的键"
             * 会打出原字符 —— 用户的原话是"只会恢复成按键原有的功能"，
             * 他认为这是错的：层的语义应该是接管，而不是"没绑就漏过去"。
             *
             * up 事件也要一起吞：只吞 down 会让前台程序收到"没有按下过
             * 的抬起"，个别程序（游戏、远程桌面）会因此判定输入异常。 */
            _usedOtherKey = true;
            _swallowedDown.TryAdd(vk, 0);
            OweUp(vk);
            return true;
        }

        string action = binding.Action;
        string arg = binding.Arg;

        bool canRepeat = settings.Global.RepeatWhenHeld && IsRepeatable(action);
        bool isRepeat = _swallowedDown.ContainsKey(vk);
        if (isRepeat)
        {
            /* 连续量动作由引擎自己的计时器驱动，系统的自动重复一律吞掉。
             * 两套节奏叠在一起会变成"按住后突然加速"，而且速率随
             * 用户的键盘设置变化，完全不可控。 */
            if (IsContinuous(action)) return true;
            if (!canRepeat) return true;
            if (vk == _lastRepeatVk && (DateTime.Now - _lastRepeatTime).TotalMilliseconds < 28) return true;
            _lastRepeatTime = DateTime.Now;
        }

        _lastRepeatVk = vk;
        _swallowedDown.TryAdd(vk, 0);
        OweUp(vk);
        _usedCombo = true;

        /* 同一个动作在 150ms 内重复请求 → 丢弃。
         *
         * 防御的是"同一时刻有两个钩子/两套引擎"这类事故：一个按键被两边各执行一次，
         * 用户看到的就是"按一下冒出来两个 QQ"。
         * 连续量动作（音量/亮度）和可重复动作（方向键）不参与去重，它们本来就是高频的。 */
        if (!IsContinuous(action) && !IsRepeatable(action) && IsDuplicateAction(action, arg))
        {
            CoreLog.Info($"动作去重：{action}（150ms 内重复请求，已忽略）");
            return true;
        }

        _queue.Post(() =>
        {
            try
            {
                _invoke(action, arg);
                ActionTriggered?.Invoke(action);
            }
            catch (Exception ex) { CoreLog.Error($"动作 {action} 执行失败", ex); }
        });

        /* 连续量动作：记下这一笔，之后由计时器按固定节奏一直执行，
         * 直到**这个键**松开。用户看到的就是"按住音量键，音量一条线地涨"。
         *
         * 每个键各存一份，而不是共用一个槽位：按住亮度+ 的时候再按音量+，
         * 两个键都应该继续各自生效（旧实现里后者会把前者顶掉，
         * 松开其中一个就把另一条也停了）。 */
        if (IsContinuous(action))
        {
            var state = new RepeatState
            {
                Action = action,
                /* 连续重复明确传 1：动作自己就知道"这一步要细调"。
                 *
                 * 以前靠"距上次调用的时间差"去猜是不是连发，结果调度一抖
                 * 就会猜错 —— 长按中途突然跳一大格，看起来一格一格地顿。 */
                Arg = "1",
                StartedAt = DateTime.Now,
                NextAt = DateTime.Now.AddMilliseconds(RepeatDelayMs),
            };
            lock (_repeatGate) _repeats[vk] = state;
        }

        return true;
    }

    /// <summary>层模式下松开某个键。返回 true 表示吞掉。</summary>
    private bool OnKeyUpInLayer(int vk)
    {
        bool swallow = _swallowedUp.TryRemove(vk, out _);
        _swallowedDown.TryRemove(vk, out _);

        // 松开就停：只停这个键，其它还按着的连续键照旧
        StopContinuousRepeat(vk);

        /* 非修饰键的子层键松开 → 清掉前缀状态。
         *
         * 不清的话，用户按了 Tab 子层再松开，之后按任何键都会被拼成
         * caps_tab_xxx，看起来就是"Tab 子层粘住了"。 */
        if (!string.IsNullOrEmpty(_subLayerKeyDown) &&
            string.Equals(KeyName.NameOf(vk), _subLayerKeyDown, StringComparison.OrdinalIgnoreCase))
        {
            _subLayerKeyDown = "";
            SubLayerChanged?.Invoke("");
        }

        /* ⚠ 左右修饰键和通用码都要清。
         *
         * 早先只写 LControl/LShift/LMenu —— 用户按**右** Ctrl 组合后松手，
         * _ctrlDown 还是 true，之后按任何键都会被拼成 Ctrl 组合键
         * （caps_ctrl_q 之类），看起来就是"组合键时灵时不灵"。
         * 很多人习惯用右手小指按右 Ctrl，这条路径并不罕见。 */
        if (vk is VK.LMenu or VK.RMenu or 0x12) _laltDown = false;
        if (vk is VK.LWin or VK.RWin) { _lwinDown = false; _rwinDown = false; }
        if (vk is VK.LControl or VK.RControl or 0x11) _ctrlDown = false;
        if (vk is VK.LShift or VK.RShift or 0x10) _shiftDown = false;

        return swallow;
    }



    private Dictionary<string, KeyBinding>? LayerBindingMap(AppSettings s, int layerId)
    {
        var layer = s.Layers.FirstOrDefault(l => l.Id == layerId);
        List<KeyBinding>? keys = layer?.Keys;
        if (keys == null || keys.Count == 0)
            keys = layerId == 1 ? s.Keys : null;      // 老设置没有层结构时，Keys 即第 1 层
        if (keys == null) return null;

        if (_layerMapCache.TryGetValue(layerId, out var cached)
            && ReferenceEquals(cached.Src, keys)
            && cached.Map.Count == keys.Count)
            return cached.Map;

        var map = new Dictionary<string, KeyBinding>(keys.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
        {
            if (string.IsNullOrEmpty(k.Combo)) continue;
            map[k.Combo] = k;      // 重复时保留最后一条，与 SettingsStore.Normalize 的行为一致
        }

        _layerMapCache[layerId] = (map, keys);
        return map;
    }

    private void OnWheel(int direction, bool shift, bool ctrl)
    {
        if (!_triggerHeld || !_layerActive || !Enabled || Suspended) return;

        // 滚轮事件不走 OnKeyEvent，修饰键状态同样要用物理状态校正
        SyncModifierFlagsWithPhysical();
        if (!_laltDown) return;
        var settings = _settings();
        string combo = direction > 0 ? "caps_lalt_wheelUp" : "caps_lalt_wheelDown";
        var binding = FindInLayer(settings, _activeLayer, combo);
        if (binding == null || !binding.IsBound) return;
        _usedCombo = true;
        _queue.Post(() => _invoke(binding.Action, binding.Arg));
    }

    /// <summary>
    /// 该动作是否适合长按自动重复。
    /// </summary>
    public static bool IsRepeatable(string action) => action switch
    {
        "moveLeft" or "moveRight" or "moveUp" or "moveDown" => true,
        "moveWordLeft" or "moveWordRight" => true,
        "pageUp" or "pageDown" or "pageMoveLineUp" or "pageMoveLineDown" => true,
        "selectLeft" or "selectRight" or "selectUp" or "selectDown" => true,
        "selectWordLeft" or "selectWordRight" => true,
        "backspace" or "delete" or "deleteWord" or "forwardDeleteWord" => true,
        "mouseLeft" or "mouseRight" or "mouseUp" or "mouseDown" => true,
        "wheelUp" or "wheelDown" => true,
        _ => false,
    };

    public void Dispose()
    {
        if (_holdTimer != null)
        {
            _holdTimer.Stop();
            _holdTimer = null;
        }
        _hook.Dispose();
        _physDown.Clear();
    }
}
