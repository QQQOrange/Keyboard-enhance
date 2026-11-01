using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 把"这次按键来自哪只键盘"提供给键盘钩子。
///
/// ## 为什么必须用 Raw Input
///
/// 低级键盘钩子（WH_KEYBOARD_LL）**不带设备信息** —— KBDLLHOOKSTRUCT 里没有设备句柄。
/// 想知道来源，只能另外注册 Raw Input（它的事件里有 hDevice）。
///
/// ## 关联是"尽力而为"，不是精确协议
///
/// 两条通路的时序不保证：Raw Input 的 WM_INPUT 是投递到消息队列的，
/// 而钩子是在输入处理链里同步调用的，**钩子常常先到**。
/// 所以这里用"短窗口轮询"：钩子最多等 <see cref="ResolveTimeoutMs"/> 毫秒，
/// 等到同键同方向的 Raw Input 事件就取它的设备，等不到就返回 null（按"所有键盘"处理）。
///
/// 三条硬约束：
///   ① 只在**有设备专属规则**的键上才等 —— 否则每个按键都多十几毫秒，打字会发涩；
///   ② 等待有上限（12ms），最坏情况只是"这次按得慢一点"，不会卡住输入；
///   ③ 拿到的事件立刻消费掉，避免同一个 Raw 事件被两次按键复用（连按时会串设备）。
///
/// ## 拿不到时的行为
///
/// 返回 null 让上层退回"所有键盘"的规则。这不是完美语义（设备专属规则会不生效），
/// 但比"猜错设备把按键改成别的"安全得多。
/// </summary>
public sealed class KeyboardDeviceTracker : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_KEYBOARD = 0x06;
    private const int RIM_TYPEKEYBOARD = 1;

    /// <summary>钩子里最多等这么久（毫秒）。</summary>
    public const int ResolveTimeoutMs = 12;

    /// <summary>Raw 事件与钩子事件算"同一次按键"的时间窗。</summary>
    public const int MatchWindowMs = 120;

    /// <summary>环形缓冲保留多少条（够覆盖几十毫秒的连打）。</summary>
    private const int MaxEntries = 32;

    private readonly object _gate = new();
    private readonly List<Entry> _recent = new(MaxEntries);

    private HwndSource? _source;
    private bool _registered;

    /// <summary>内部：一条 Raw 键盘事件。</summary>
    internal readonly record struct Entry(string Device, int Vk, bool Down, long Tick);

    /// <summary>是否可用（注册成功）。不可用时 <see cref="ResolveDevice"/> 一律返回 null。</summary>
    public bool IsRunning => _registered;

    /// <summary>
    /// 在**有消息泵的 UI 线程**上创建接收窗口并注册 Raw Input。
    /// 必须在 STA 线程调用。
    /// </summary>
    public bool Start()
    {
        if (_registered) return true;

        try
        {
            var parameters = new HwndSourceParameters("KeyboardEnhance.RawInput")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                ParentWindow = new IntPtr(-3),      // HWND_MESSAGE：消息专用窗口
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);

            var device = new RawInputDevice
            {
                UsagePage = HID_USAGE_PAGE_GENERIC,
                Usage = HID_USAGE_KEYBOARD,
                Flags = RIDEV_INPUTSINK,            // 没焦点也要收
                Target = _source.Handle,
            };

            if (!RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            {
                CoreLog.Warn($"注册 Raw Input 失败（Win32 错误 {Marshal.GetLastWin32Error()}）：" +
                             "按设备区分改键将不可用，其余功能不受影响");
                Dispose();
                return false;
            }

            _registered = true;
            CoreLog.Info("Raw Input 已注册：可以识别按键来自哪只键盘");
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("初始化键盘设备追踪失败：" + ex.Message);
            Dispose();
            return false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;

        try { ReadRawInput(lParam); }
        catch (Exception ex) { CoreLog.Swallow(ex, "读取 Raw Input"); }

        return IntPtr.Zero;
    }

    private void ReadRawInput(IntPtr lParam)
    {
        uint size = 0;
        int headerSize = Marshal.SizeOf<RawInputHeader>();

        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, (uint)headerSize);
        if (size == 0 || size > 512) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint got = GetRawInputData(lParam, RID_INPUT, buffer, ref size, (uint)headerSize);
            if (got == uint.MaxValue || got < headerSize) return;

            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != RIM_TYPEKEYBOARD) return;      // 鼠标等直接忽略

            var kb = Marshal.PtrToStructure<RawKeyboard>(buffer + headerSize);
            if (kb.VKey == 0) return;                          // 部分设备用扫描码，忽略

            bool down = kb.Message is 0x0100 or 0x0104;         // WM_KEYDOWN / WM_SYSKEYDOWN
            bool up = kb.Message is 0x0101 or 0x0105;
            if (!down && !up) return;

            /* 事件里只有设备句柄，路径要再问一次系统 —— 这是每按键一次的开销，
             * 但只有注册了 Raw Input 才有，且只有一次 P/Invoke（不到 0.1ms）。 */
            string device = DeviceKeyOf(KeyboardDevices.PathOf(header.Device));
            if (device.Length == 0) return;

            lock (_gate)
            {
                _recent.Add(new Entry(device, kb.VKey, down, Environment.TickCount64));
                if (_recent.Count > MaxEntries) _recent.RemoveRange(0, _recent.Count - MaxEntries);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// 钩子侧：这次按键来自哪只键盘（拿不到返回 null）。
    ///
    /// <paramref name="waitMs"/> 为 0 表示只查一次（不等待）。钩子传
    /// <see cref="ResolveTimeoutMs"/>，让"钩子比 WM_INPUT 早到"也能等到。
    /// </summary>
    public string? ResolveDevice(int vk, bool down, int waitMs = ResolveTimeoutMs)
    {
        if (!_registered) return null;

        if (TryTake(vk, down, out string? device)) return device;

        for (int waited = 0; waited < waitMs; waited++)
        {
            Thread.Sleep(1);                      // 让 UI 线程有机会处理 WM_INPUT
            if (TryTake(vk, down, out device)) return device;
        }
        return null;
    }

    private bool TryTake(int vk, bool down, out string? device)
    {
        device = null;
        long now = Environment.TickCount64;

        lock (_gate)
        {
            int index = MatchRecent(_recent, vk, down, now);
            if (index < 0) return false;

            device = _recent[index].Device;
            _recent.RemoveAt(index);              // 消费掉，防止同一条被复用
            return true;
        }
    }

    /// <summary>
    /// 纯函数：在缓冲里找"同键、同方向、在时间窗内"的最近一条。
    /// 从后往前找 —— 连打时最后一条才是这次按的。
    /// </summary>
    internal static int MatchRecent(IReadOnlyList<Entry> entries, int vk, bool down, long now,
        int windowMs = MatchWindowMs)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (e.Vk != vk || e.Down != down) continue;
            if (now - e.Tick > windowMs) continue;
            return i;
        }
        return -1;
    }

    /// <summary>设备标识：与界面、配置共用同一套规则（`KeyboardDevices.KeyOf`）。</summary>
    internal static string DeviceKeyOf(string path) => KeyboardDevices.KeyOf(path);

    public void Dispose()
    {
        _registered = false;

        try
        {
            if (_source != null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "释放 Raw Input"); }

        lock (_gate) _recent.Clear();
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public IntPtr ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
}
