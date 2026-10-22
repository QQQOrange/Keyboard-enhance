using System;
using System.Runtime.InteropServices;

namespace CapsLockPlus.Core.Windows;

/// <summary>
/// 让 Windows 弹出**系统默认的 OSD**（屏幕中下方那条音量横条）。
///
/// 为什么需要它：直接调 Core Audio 改音量时，系统**不会**弹 OSD ——
/// 那条横条是 explorer 在收到 <c>WM_APPCOMMAND</c>（音量键对应的应用命令）时
/// 才画出来的，和我们调 API 没有任何关系。这也解释了"音量能调，但看不到横条"。
///
/// 为什么不用 <c>SendInput</c> 发媒体键：
/// 媒体键走的是"投递给**前台窗口**"这条路，前台程序可以吃掉
/// <c>WM_APPCOMMAND</c> 不往下传，键盘过滤驱动/远程会话也可能吞掉注入的键 ——
/// 这正是上一版"音量键按了没反应"的原因。
///
/// 这里的做法：**直接把 <c>WM_APPCOMMAND</c> 发给任务栏窗口**（<c>Shell_TrayWnd</c>），
/// 绕开前台程序，由 explorer 自己完成"调音量 + 画 OSD"。
///
/// 为了让 OSD 上的数字和真实音量完全一致，做法是：
///   1. 先把音量悄悄设到"目标值 ∓ 系统一步"（这一步不弹 OSD）；
///   2. 再让 shell 走一步 —— 正好落在目标值上，OSD 显示的就是目标值。
/// 系统一步通常是 2%（50 档），用 <c>GetVolumeStepInfo</c> 现查，不写死。
/// </summary>
public static class ShellOsd
{
    private const int WM_APPCOMMAND = 0x0319;

    private const int APPCOMMAND_VOLUME_MUTE = 0x08;
    private const int APPCOMMAND_VOLUME_DOWN = 0x09;
    private const int APPCOMMAND_VOLUME_UP = 0x0A;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>仅供自检：把"发消息"换掉，避免真的去动系统音量。</summary>
    internal static Func<IntPtr, int, IntPtr, IntPtr, IntPtr>? SendForTest { get; set; }

    private static IntPtr Send(IntPtr hwnd, int msg, IntPtr w, IntPtr l)
        => SendForTest != null ? SendForTest(hwnd, msg, w, l) : SendMessage(hwnd, msg, w, l);

    /// <summary>
    /// 把音量调到 <paramref name="targetPercent"/>，并让系统弹出 OSD。
    ///
    /// 返回 true = explorer 真的响应了（OSD 已显示、音量已到位）；
    /// false = 任务栏没响应（精简系统 / 第三方外壳），调用方应自行兜底。
    /// </summary>
    public static bool SetVolume(int targetPercent, int direction)
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;

        int? current = VolumeService.Get();
        if (current == null) return false;

        targetPercent = Math.Clamp(targetPercent, 0, 100);
        int step = VolumeService.StepPercent();

        /* 先把音量退到"目标值的前一档"，再让 shell 走一步。
         *
         * 为什么不用"直接发命令、再补一次 Set"：那样 OSD 上显示的是
         * shell 那一步的值（目标 ± 2%），和真实音量对不上 ——
         * 用户会看到横条写着 52%，实际是 50%。
         * 先退一档则 shell 一步正好落在目标上，横条和实际完全一致。
         *
         * 退档这一步本身是静默的（不弹 OSD），发生在几毫秒内，
         * 用户只会看到最后那一条正确的横条。 */
        int pre = direction >= 0 ? targetPercent - step : targetPercent + step;
        pre = Math.Clamp(pre, 0, 100);
        VolumeService.Set(pre);

        int command = direction >= 0 ? APPCOMMAND_VOLUME_UP : APPCOMMAND_VOLUME_DOWN;
        Send(tray, WM_APPCOMMAND, IntPtr.Zero, (IntPtr)(command << 16));

        /* shell 是**异步**处理的：SendMessage 返回时音量未必已经落地
         * （explorer 把活丢给自己的另一个线程/COM 调用）。
         * 立刻回读会读到"没变"，从而误判成"任务栏没响应" ——
         * 这个误判会让本该弹系统横条的场景退回自绘横条。
         * 所以给最多 3 次、每次 70ms 的等待。 */
        int? after = null;
        for (int i = 0; i < 3; i++)
        {
            System.Threading.Thread.Sleep(70);
            after = VolumeService.Get();
            if (after != null && after.Value != pre) break;
        }

        if (after == null) return false;

        // shell 没动（还是退档后的值）→ 说明它没处理这条命令
        bool responded = after.Value != pre;
        if (!responded)
        {
            // 至少把音量落到目标值，别让用户按了等于没按
            VolumeService.Set(targetPercent);
            return false;
        }

        // shell 动了但没落在目标上（步进不一致）→ 纠正，OSD 已经弹出来了
        if (after.Value != targetPercent) VolumeService.Set(targetPercent);
        return true;
    }

    /// <summary>
    /// 切换静音，同样交给 shell 做（这样才有系统的静音 OSD）。
    /// 返回 null = 任务栏没响应，调用方改用 Core Audio。
    /// </summary>
    public static bool? ToggleMute()
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return null;

        bool? before = VolumeService.GetMute();
        if (before == null) return null;

        Send(tray, WM_APPCOMMAND, IntPtr.Zero, (IntPtr)(APPCOMMAND_VOLUME_MUTE << 16));

        // 同 SetVolume：shell 异步处理，等一小会儿再回读
        bool? after = null;
        for (int i = 0; i < 3; i++)
        {
            System.Threading.Thread.Sleep(70);
            after = VolumeService.GetMute();
            if (after != null && after != before) break;
        }

        if (after == null || after == before)
        {
            VolumeService.SetMute(!before.Value);   // 兜底：自己切
            return false;
        }
        return after.Value;
    }

    /// <summary>仅供自检：这条消息该带什么 lParam（高位是 APPCOMMAND 编号）。</summary>
    internal static int VolumeLParam(bool up) => (up ? APPCOMMAND_VOLUME_UP : APPCOMMAND_VOLUME_DOWN) << 16;

    // ---------------- 亮度：系统自带的 OSD ----------------

    /* 亮度横条是 Windows 自己画的（那个太阳图标 + 青色滑条）。
     *
     * 它不在 explorer 的 WM_APPCOMMAND 里，而是由 shell 的**浮层宿主窗口**
     * （类名 NativeHWNDHost）在收到 SHELLHOOK 注册消息时画出来。
     * 这个窗口平时不存在，要用 shell 的一个未公开 COM 接口把它"叫醒"。
     *
     * 链路（参考 qwerty12 / YashMaster 的逆向结论，Win8+ 有效）：
     *   1. CoCreateInstance(CLSID_ShellProvider) → QueryInterface(IFlyoutDispatcher)
     *      → 调 vtable 第 3 个槽位 (type, 0)，type=3 是亮度浮层、0 是音量浮层；
     *   2. FindWindow("NativeHWNDHost") 拿到浮层宿主；
     *   3. PostMessage(host, RegisterWindowMessage("SHELLHOOK"), 0x38, 0)。
     *
     * wParam 在不同版本上不一样：Win10 用 0x37，Win11 用 0x38
     * （0x37 在 Win11 上是音量浮层）。所以两个都发一次 ——
     * 发错的那个不会画横条，也不会报错。
     */

    private static readonly Guid ClsidShellProvider = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidFlyoutDispatcher = new("41f9d2fb-7834-4ab6-8b1b-73e74064b465");

    /// <summary>浮层宿主窗口的类名（Win8/10/11 都是这个）。</summary>
    private const string OsdHostClass = "NativeHWNDHost";

    private const uint CLSCTX_ALL = 0x17;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter,
        uint dwClsContext, ref Guid riid, out IntPtr ppv);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FlyoutDispatch(IntPtr self, int type, uint flags);

    /// <summary>
    /// 让 Windows 画出**系统自带的亮度横条**。
    /// 返回 false = 这台系统上叫不出浮层（例如精简版 / 更老的系统），调用方自己兜底。
    /// </summary>
    public static bool ShowBrightnessOsd()
    {
        try
        {
            IntPtr host = FindWindow(OsdHostClass, null);
            if (!IsWindow(host)) host = EnsureOsdHost();
            if (!IsWindow(host)) return false;

            uint shellHook = RegisterWindowMessage("SHELLHOOK");
            if (shellHook == 0) return false;

            /* 两个 wParam 都发：Win10 认 0x37、Win11 认 0x38，
             * 发错的那个不会有任何副作用。 */
            PostMessage(host, shellHook, (IntPtr)0x38, IntPtr.Zero);
            PostMessage(host, shellHook, (IntPtr)0x37, IntPtr.Zero);

            // 等它把横条画出来（浮层是异步渲染的）
            for (int i = 0; i < 6; i++)
            {
                System.Threading.Thread.Sleep(30);
                if (IsWindowVisible(host)) return true;
            }
            return true;      // 消息已发出；可见性拿不到也别判失败
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[OSD] 调用系统亮度浮层失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 把浮层宿主窗口"叫醒"。
    ///
    /// 它平时不存在，必须先通过 shell 的 IFlyoutDispatcher 触发一次
    /// （type=3 亮度 / 0 音量），系统才会创建 NativeHWNDHost。
    /// </summary>
    private static IntPtr EnsureOsdHost()
    {
        IntPtr unknown = IntPtr.Zero;
        IntPtr dispatcher = IntPtr.Zero;
        try
        {
            Guid clsid = ClsidShellProvider;
            Guid iid = IidIUnknown;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out unknown);
            if (hr != 0 || unknown == IntPtr.Zero)
            {
                CoreLog.Warn($"[OSD] CoCreateInstance(ShellProvider) 失败：0x{hr:X8}");
                return IntPtr.Zero;
            }

            Guid dispIid = IidFlyoutDispatcher;
            if (Marshal.QueryInterface(unknown, ref dispIid, out dispatcher) != 0 || dispatcher == IntPtr.Zero)
            {
                CoreLog.Warn("[OSD] QueryInterface(IFlyoutDispatcher) 失败");
                return IntPtr.Zero;
            }

            /* vtable 第 3 槽 = IUnknown 三个方法之后的第一个自定义方法。
             * 参数 (type, flags)：3 = 亮度浮层，0 = 音量浮层。 */
            IntPtr vtable = Marshal.ReadIntPtr(dispatcher);
            IntPtr fn = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
            if (fn == IntPtr.Zero) return IntPtr.Zero;

            var call = Marshal.GetDelegateForFunctionPointer<FlyoutDispatch>(fn);
            try { call(dispatcher, 3, 0); } catch { }
            try { call(dispatcher, 0, 0); } catch { }

            // 给它一点时间创建窗口
            for (int i = 0; i < 8; i++)
            {
                IntPtr h = FindWindow(OsdHostClass, null);
                if (IsWindow(h)) return h;
                System.Threading.Thread.Sleep(40);
            }
            return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[OSD] 叫醒浮层失败：" + ex.Message);
            return IntPtr.Zero;
        }
        finally
        {
            if (dispatcher != IntPtr.Zero) Marshal.Release(dispatcher);
            if (unknown != IntPtr.Zero) Marshal.Release(unknown);
        }
    }

    /// <summary>仅供诊断：浮层宿主此刻是否存在 / 是否可见。</summary>
    internal static (bool Exists, bool Visible) OsdHostState()
    {
        IntPtr h = FindWindow(OsdHostClass, null);
        return (IsWindow(h), IsWindowVisible(h));
    }
}
