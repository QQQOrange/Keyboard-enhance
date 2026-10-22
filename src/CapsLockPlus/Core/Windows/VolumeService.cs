using System;
using System.Runtime.InteropServices;

namespace CapsLockPlus.Core.Windows;

/// <summary>
/// 系统主音量控制（Core Audio API 的 IAudioEndpointVolume）。
///
/// 为什么不再「模拟媒体键」：
/// 旧实现是 SendInput 发 VK_VOLUME_UP / VK_VOLUME_DOWN（0xAF / 0xAE）。
/// 这条路依赖系统 shell 对媒体键的分发，丢失环节很多：
///   · 键盘过滤驱动、安全软件、远程桌面会话可能吞掉注入的媒体键；
///   · 前台程序抢先处理 WM_APPCOMMAND 且不往下传；
///   · 注入时 wScan 为 0，个别机器上 shell 不认（它靠扫描码分发媒体键）。
/// 任何一环断掉都表现为"按下没反应"，而且拿不到当前音量，
/// 也就没法显示百分比、没法按固定步进调。
///
/// 直接调 IAudioEndpointVolume：
///   · 一次 COM 调用直达音频引擎，成功/失败立刻可知；
///   · 能读当前值 → 可以按 5% 步进，可以提示"音量 65%"；
///   · 静音状态可读写，静音键不再依赖系统行为。
///
/// 线程模型：动作队列是单条 STA 线程，COM 对象只在那条线程创建与使用。
/// 接口本身是 free-threaded 的，即便将来换线程也不会踩单元问题。
/// </summary>
public static class VolumeService
{
    private static readonly object Gate = new();
    private static IAudioEndpointVolume? _endpoint;
    private static bool _warned;

    /* ---- COM 对象是" apartment 绑定"的，只能在家里用 ----
     *
     * `_endpoint` 是在**第一次调用它的那条 STA 线程**上创建的（正常情况下
     * 就是动作队列线程）。自定义 COM 接口没有注册代理/存根，
     * 换一条线程直接用它 = 未定义行为：轻则报错、重则崩进程。
     *
     * 实际情况：设置页要显示"系统音量步进"，那是界面线程；
     * 如果它先碰到 COM，动作队列之后就在用别人家的接口指针。
     * 所以这里记下宿主线程，别家线程只允许读缓存值。 */
    private static int _comThreadId;

    /// <summary>系统音量"一步"是多少（缓存，0 = 还没问过）。</summary>
    private static int _stepCache;

    /// <summary>逐次加/减的默认步进（百分点）。和常见音量脚本的 ±5 对齐。</summary>
    public const int DefaultStep = 5;

    /// <summary>问不到系统步进时的保守值（绝大多数机器的音量是 50 档）。</summary>
    public const int FallbackSysStep = 2;

    /// <summary>这台机器能不能读写主音量（没有输出设备时为 false）。</summary>
    public static bool IsSupported() => Endpoint() != null;

    /// <summary>
    /// 当前主音量（0~100）；没有可用输出设备时返回 null。
    /// 静音状态下依然返回音量值（Windows 也是这么显示的）。
    /// </summary>
    public static int? Get()
    {
        var ep = Endpoint();
        if (ep == null) return null;

        try
        {
            if (ep.GetMasterVolumeLevelScalar(out float level) != 0) return null;
            return ToPercent(level);
        }
        catch (Exception ex)
        {
            WarnOnce("读取音量失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>设置绝对音量（0~100）。成功返回 true。</summary>
    public static bool Set(int percent)
    {
        var ep = Endpoint();
        if (ep == null) return false;

        try
        {
            percent = Math.Clamp(percent, 0, 100);
            /* SetMasterVolumeLevelScalar 第二个参数是事件上下文 Guid：
             * 传 Guid.Empty 表示"不是某个具体应用发起的"。 */
            Guid ctx = Guid.Empty;
            if (ep.SetMasterVolumeLevelScalar(percent / 100f, ref ctx) != 0) return false;
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce("写入音量失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 系统音量"一步"是多少个百分点（通常是 2%，即 50 档）。
    ///
    /// 用来让系统 OSD 显示的数字和真实音量完全一致：先把音量退到
    /// 目标值前一档，再让 shell 走一步，正好落在目标值上。
    /// </summary>
    /// <summary>
    /// 系统音量"一步"是多少个百分点（通常 2%，即 50 档）。
    ///
    /// 用来让设置页的下限和"补偿发几次键"都与系统一致。
    ///
    /// **可以在界面线程调用**：缓存命中直接返回；缓存没命中且当前不是
    /// COM 的宿主线程时，返回保守值 <see cref="FallbackSysStep"/>，
    /// 绝不在别人的线程上碰那个接口指针（见 <see cref="_comThreadId"/>）。
    /// 真正的刷新发生在动作队列线程上（启动时预热一次）。
    /// </summary>
    public static int StepPercent()
    {
        int cached = Volatile.Read(ref _stepCache);
        if (cached > 0) return cached;

        int owner = Volatile.Read(ref _comThreadId);
        if (owner != 0 && owner != Environment.CurrentManagedThreadId)
            return FallbackSysStep;

        var ep = Endpoint();
        if (ep == null) return FallbackSysStep;

        int step = FallbackSysStep;
        try
        {
            if (ep.GetVolumeStepInfo(out _, out uint stepCount) == 0 && stepCount > 0)
                step = Math.Max(1, (int)Math.Round(100.0 / stepCount));
        }
        catch
        {
            step = FallbackSysStep;
        }

        Volatile.Write(ref _stepCache, step);
        return step;
    }

    /// <summary>
    /// 在当前音量上加减 <paramref name="deltaPercent"/> 个百分点。
    /// 返回值：null = 没有可用输出设备；否则是**回读到的**真实音量。
    ///
    /// 为什么回读而不是直接返回目标值：不同声卡会把 0.65 量化成
    /// 0.6499999 或整档（比如只认 2% 的倍数），回读才能跟系统实际状态一致。
    /// </summary>
    public static int? Step(int deltaPercent)
    {
        int? cur = Get();
        if (cur == null) return null;

        int target = TargetPercent(cur.Value, deltaPercent);
        if (!Set(target)) return null;
        return Get() ?? target;
    }

    /// <summary>切换静音，返回切换后的状态；没有设备时返回 null。</summary>
    public static bool? ToggleMute()
    {
        bool? now = GetMute();
        if (now == null) return null;

        bool next = !now.Value;
        return SetMute(next) ? next : null;
    }

    /// <summary>当前是否静音；没有设备时返回 null。</summary>
    public static bool? GetMute()
    {
        var ep = Endpoint();
        if (ep == null) return null;

        try
        {
            if (ep.GetMute(out bool muted) != 0) return null;
            return muted;
        }
        catch (Exception ex)
        {
            WarnOnce("读取静音状态失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>设置静音状态。成功返回 true。</summary>
    public static bool SetMute(bool muted)
    {
        var ep = Endpoint();
        if (ep == null) return false;

        try
        {
            Guid ctx = Guid.Empty;
            return ep.SetMute(muted, ref ctx) == 0;
        }
        catch (Exception ex)
        {
            WarnOnce("写入静音状态失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 音量夹取到 0~100。抽成纯函数是为了让自检能直接断言边界，
    /// 不必真的去改机器的音量。
    /// </summary>
    public static int TargetPercent(int current, int deltaPercent)
        => Math.Clamp(current + deltaPercent, 0, 100);

    /// <summary>0~1 的浮点音量 → 整数百分比（四舍五入，避免 0.6499999 显示成 64）。</summary>
    public static int ToPercent(float level)
        => Math.Clamp((int)Math.Round(level * 100f), 0, 100);

    /// <summary>
    /// 丢弃缓存的 COM 对象，下次调用重新获取默认输出设备。
    /// 用户切换默认播放设备（插耳机、切 HDMI）后需要这个 ——
    /// 旧对象指向的是**上一台**设备，继续用会改错目标的音量。
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            if (_endpoint != null)
            {
                try { Marshal.ReleaseComObject(_endpoint); } catch { }
                _endpoint = null;
            }
            // 归属线程与步进缓存一起作废：下次在新线程上重建时重新问
            Volatile.Write(ref _comThreadId, 0);
            Volatile.Write(ref _stepCache, 0);
        }
    }

    private static IAudioEndpointVolume? Endpoint()
    {
        /* 已经在别的线程上建好了：**别把指针交给调用方**。
         *
         * 自定义 COM 接口没有跨 apartment 的代理，换线程使用是未定义行为
         * （轻则调用失败，重则整个进程挂掉）。返回 null 让上层走降级路径
         * （音量照旧由系统音量键处理，只是少了回读校验），
         * 比"偶尔崩一次"好得多。 */
        if (_endpoint != null)
        {
            return Volatile.Read(ref _comThreadId) == Environment.CurrentManagedThreadId
                ? _endpoint
                : null;
        }

        lock (Gate)
        {
            // 并发下被别人抢先建好：同样只在宿主线程上交付
            if (_endpoint != null)
            {
                return Volatile.Read(ref _comThreadId) == Environment.CurrentManagedThreadId
                    ? _endpoint
                    : null;
            }

            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

                // eRender + eMultimedia 就是"当前默认播放设备"（和音量图标一致）
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device) != 0)
                    return null;

                Guid iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out IAudioEndpointVolume? volume) != 0
                    || volume == null)
                    return null;

                _endpoint = volume;
                // 记下宿主线程：只有这条线程能再用这个指针
                Volatile.Write(ref _comThreadId, Environment.CurrentManagedThreadId);
                Volatile.Write(ref _stepCache, 0);
                return _endpoint;
            }
            catch (Exception ex)
            {
                WarnOnce("初始化音频接口失败：" + ex.Message);
                return null;
            }
            finally
            {
                if (device != null) { try { Marshal.ReleaseComObject(device); } catch { } }
                if (enumerator != null) { try { Marshal.ReleaseComObject(enumerator); } catch { } }
            }
        }
    }

    /// <summary>同一个错误只记一次日志：长按调音量时每秒会调好几次，刷日志没意义。</summary>
    private static void WarnOnce(string message)
    {
        if (_warned) return;
        _warned = true;
        CoreLog.Warn("[音量] " + message);
    }

    // ================= COM 互操作 =================
    //
    // ⚠ 这些接口的方法**顺序**必须与原生 vtable 完全一致，少一个、
    // 换一个位置都会导致调用错位（典型表现：一调就 AccessViolation，
    // 或者把音量设成乱七八糟的值）。全部按 mmdeviceapi.h / endpointvolume.h 抄。

    private enum EDataFlow
    {
        eRender = 0,
        eCapture = 1,
        eAll = 2,
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2,
    }

    private static class ClsCtx
    {
        public const uint All = 0x17; // INPROC_SERVER|INPROC_HANDLER|LOCAL_SERVER|REMOTE_SERVER
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.Interface)] out IAudioEndpointVolume? ppInterface);

        [PreserveSig]
        int OpenPropertyStore(uint stgmAccess, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out uint count);

        [PreserveSig]
        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float levelDb);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float levelDb);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

        [PreserveSig]
        int GetVolumeStepInfo(out uint step, out uint stepCount);

        [PreserveSig]
        int VolumeStepUp(ref Guid eventContext);

        [PreserveSig]
        int VolumeStepDown(ref Guid eventContext);

        [PreserveSig]
        int QueryHardwareSupport(out uint hardwareSupportMask);

        [PreserveSig]
        int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }
}
