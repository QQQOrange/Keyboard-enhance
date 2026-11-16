using System.Runtime.InteropServices;
using CapsLockPlus.Core.Localization;

namespace CapsLockPlus.Core.Input;

/// <summary>归并后的鼠标（同一只鼠标的多个系统条目合成一条）。</summary>
public sealed record MouseDeviceGroup(string Name, string Kind, int Count, string Sample)
{
    /// <summary>组里至少有一个集合自称触控板（键盘那边通常只报"HID Keyboard Device"）。</summary>
    public bool TouchPad { get; init; }
}

/// <summary>一个鼠标设备（系统视角）。</summary>
public sealed record MouseDevice(
    string Path,
    string Name,
    string Kind,
    bool LikelyBuiltIn);

/// <summary>
/// 枚举系统里的鼠标设备。
///
/// ## 为什么要单独猜"这是哪只鼠标"
///
/// 键盘那套（<see cref="KeyboardDevices"/>）只枚举 RIM_TYPEKEYBOARD。
/// 但"左键 / 右键 / 中键 / 侧键改成别的键"这条规则同样需要设备维度：
/// 笔记本触摸板、游戏鼠标、远程桌面的虚拟鼠标在系统里是**不同设备**，
/// 用户想要的往往是"只改这只游戏鼠标的侧键，触摸板别动"。
///
/// ## 设备标识为什么要带前缀
///
/// <see cref="KeyboardDevices.KeyOf"/> 返回的是 `VID_046D&amp;PID_C31C` 这种形式。
/// 一只游戏键盘的键盘接口和鼠标接口**共用同一个 VID/PID** ——
/// 如果鼠标也直接用这个标识，"只对键盘生效"的规则会同时作用到同一只设备的鼠标接口上
/// （反之亦然）。所以鼠标标识统一加 <c>MS:</c> 前缀，键盘保持原样（兼容已存的配置）。
/// </summary>
public static class MouseDevices
{
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEMOUSE = 2;

    /// <summary>设备标识前缀：与键盘标识区分开，避免复合设备互相串配置。</summary>
    public const string Prefix = "MS:";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _names =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>列出鼠标设备。失败返回空列表（展示功能，绝不抛异常）。</summary>
    public static IReadOnlyList<MouseDevice> List()
    {
        var list = new List<MouseDevice>();

        try
        {
            uint count = 0;
            uint size = (uint)Marshal.SizeOf<RawInputDeviceList>();

            if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0)
                return list;

            var buffer = new RawInputDeviceList[count + 2];
            uint got = GetRawInputDeviceList(buffer, ref count, size);
            if (got == uint.MaxValue || got == 0) return list;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < got && i < buffer.Length; i++)
            {
                if (buffer[i].Type != RIM_TYPEMOUSE) continue;

                string path = NameOf(buffer[i].Device);
                if (path.Length == 0) continue;
                if (!seen.Add(path)) continue;

                list.Add(new MouseDevice(
                    path,
                    KeyboardDevices.FriendlyName(path, KindOf(path)),
                    KindOf(path),
                    IsBuiltIn(path)));
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "枚举鼠标设备");
        }

        return list;
    }

    /// <summary>
    /// 设备标识（用于"只在这只鼠标上生效"的配置）。
    /// 优先 VID/PID，没有才退回完整路径；统一加 <see cref="Prefix"/>。
    /// </summary>
    public static string KeyOf(string? path)
    {
        string p = (path ?? "").Trim();
        if (p.Length == 0) return "";

        string vidpid = KeyboardDevices.VidPidOf(p);
        string key = Prefix + (vidpid.Length > 0 ? vidpid : p.ToUpperInvariant());

        /* 同一只物理设备常常既报键盘集合、又报鼠标集合（触控板、带多媒体键的鼠标）。
         * 有键盘兄弟时统一用键盘那边的标识 —— 列表里一只触控板只占一行，
         * 勾"不接管"也只勾一次，配置里不会出现同名两份方案。 */
        return HasKeyboardTwin(vidpid) ? vidpid : key;
    }

    private static readonly object TwinGate = new();
    private static HashSet<string> _twinKeys = new(StringComparer.OrdinalIgnoreCase);
    private static long _twinAt;
    private const int TwinCacheMs = 3000;

    private static bool HasKeyboardTwin(string vidpid)
    {
        if (vidpid.Length == 0) return false;
        RefreshTwinCache();
        lock (TwinGate) return _twinKeys.Contains(vidpid);
    }

    /// <summary>刷新"哪些 VID/PID 同时是键盘"的缓存（最多 3 秒延迟，插拔后自适应）。</summary>
    internal static void RefreshTwinCache()
    {
        lock (TwinGate)
        {
            long now = Environment.TickCount64;
            if (_twinAt != 0 && now - _twinAt < TwinCacheMs) return;

            try
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in KeyboardDevices.Group(KeyboardDevices.List()))
                {
                    string v = KeyboardDevices.VidPidOf(g.Sample);
                    if (v.Length > 0) set.Add(v);
                }
                _twinKeys = set;
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "刷新键盘-鼠标归并表"); }

            _twinAt = now;
        }
    }

    /// <summary>把一个已带前缀的标识还原成裸标识（写配置前用）。</summary>
    public static string Strip(string? key)
    {
        string k = (key ?? "").Trim();
        return k.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? k[Prefix.Length..] : k;
    }

    /// <summary>鼠标类别标签。纯字符串判断，便于自检。</summary>
    public static string KindOf(string path)
    {
        string p = (path ?? "").ToUpperInvariant();

        if (p.Contains("RDP_") || p.Contains("RDP_MOUSE") || p.Contains("TERMINPUT")) return Loc.T("远程桌面虚拟鼠标");
        if (p.Contains("ROOT#") || p.Contains("ROOT\\")) return Loc.T("虚拟鼠标（ROOT）");
        if (p.Contains("HID")) return Loc.T("外接鼠标（HID）");
        if (p.Contains("PS2") || p.Contains("I8042")) return Loc.T("触摸板 / PS2 鼠标");
        if (p.Contains("USB")) return Loc.T("外接鼠标（USB）");
        return Loc.T("其它");
    }

    /// <summary>名字像是在说"我是触控板"。</summary>
    private static bool LooksLikeTouchPad(string? name)
    {
        string n = (name ?? "").ToLowerInvariant();
        return n.Contains("touch pad") || n.Contains("touchpad")
            || n.Contains("trackpad") || n.Contains("触摸板") || n.Contains("触控板");
    }

    private static bool IsBuiltIn(string path)
    {
        string p = (path ?? "").ToUpperInvariant();
        return p.Contains("ACPI") || p.Contains("I8042") || p.Contains("PS2");
    }

    /// <summary>
    /// 把系统条目按物理设备归并（一只鼠标常被拆成 HID 集合 + 消费者控制两条）。
    /// </summary>
    public static IReadOnlyList<MouseDeviceGroup> Group(IReadOnlyList<MouseDevice>? devices)
    {
        var result = new List<MouseDeviceGroup>();
        if (devices == null) return result;

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
        {
            if (d == null) continue;

            string vidpid = KeyboardDevices.VidPidOf(d.Path);
            string key = vidpid.Length > 0 ? "id:" + vidpid : "path:" + d.Path;

            bool pad = LooksLikeTouchPad(d.Name);

            if (index.TryGetValue(key, out int at))
            {
                var prev = result[at];
                string kind = prev.Kind == Loc.T("其它") ? d.Kind : prev.Kind;
                /* 名字取"更能说明这是什么"的那条：触控板集合叫「HID-compliant touch pad」，
                 * 同一只设备的另一个集合可能只叫「vendor-defined」。 */
                string name = pad && !prev.TouchPad ? d.Name : prev.Name;
                result[at] = prev with { Count = prev.Count + 1, Kind = kind, Name = name, TouchPad = prev.TouchPad || pad };
                continue;
            }

            _names[KeyOf(d.Path)] = d.Name;
            index[key] = result.Count;
            result.Add(new MouseDeviceGroup(d.Name, d.Kind, 1, d.Path) { TouchPad = pad });
        }

        return result;
    }

    /// <summary>标识 → 友好名（由 <see cref="Group"/> 顺带填充；查不到原样返回）。</summary>
    public static string DisplayNameOf(string? key)
    {
        string k = (key ?? "").Trim();
        if (k.Length == 0) return "";
        return _names.TryGetValue(k, out var name) ? name : Strip(k);
    }

    /// <summary>登记一个名字（枚举之外的地方也能补名字，例如 Raw Input 事件里见到的设备）。</summary>
    internal static void Remember(string key, string name)
    {
        if (key.Length == 0 || name.Length == 0) return;
        _names[key] = name;
    }

    private static string NameOf(IntPtr device)
    {
        uint size = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0) return "";

        IntPtr buf = Marshal.AllocHGlobal((int)size * 2 + 2);
        try
        {
            uint ok = GetRawInputDeviceInfo(device, RIDI_DEVICENAME, buf, ref size);
            if (ok == uint.MaxValue) return "";
            return Marshal.PtrToStringUni(buf) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList
    {
        public IntPtr Device;
        public uint Type;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(
        [Out] RawInputDeviceList[]? devices, ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(
        IntPtr device, uint command, IntPtr data, ref uint size);
}
