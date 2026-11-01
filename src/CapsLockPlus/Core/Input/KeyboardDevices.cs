using System.Runtime.InteropServices;
using System.Text;
using CapsLockPlus.Core.Localization;

namespace CapsLockPlus.Core.Input;

/// <summary>归并后的键盘（同一物理设备的多个系统条目合成一条）。</summary>
public sealed record KeyboardDeviceGroup(string Name, string Kind, int Count, string Sample);

/// <summary>一个键盘设备（系统视角）。</summary>
public sealed record KeyboardDevice(
    string Path,
    string Name,
    string Kind,
    bool LikelyBuiltIn);

/// <summary>
/// 枚举系统里的键盘设备。
///
/// ## 这一版只做"看见"，不做"区分"
///
/// 按设备区分改键需要把设备标识带进按键热路径（Raw Input + 每个事件附带 hDevice），
/// 一旦出问题就是"整个键盘失灵"级别的事故。所以先只做**只读枚举**：
/// 让用户与开发者都能确认"系统到底认几个键盘、分别是什么"，
/// 再决定要不要继续往下做。这一步不改任何按键行为，随时可以整体删掉。
///
/// ## 为什么键盘会"多出来"
///
/// 常见会多出好几条：笔记本内置键盘（ACPI）、外接 HID 键盘（可能按 Colection 拆成两条）、
/// 远程桌面虚拟键盘（Root\RDP_*）、以及某些驱动注册的虚拟设备。
/// 这就是"按设备配置"必须谨慎的原因 —— 用户以为只有一个键盘，实际系统认四五个。
/// </summary>
public static class KeyboardDevices
{
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDI_DEVICEINFO = 0x2000000b;
    private const uint RIM_TYPEKEYBOARD = 1;

    /// <summary>
    /// 列出键盘设备。失败返回空列表（不抛异常：这是展示功能，绝不能影响启动）。
    /// </summary>
    public static IReadOnlyList<KeyboardDevice> List()
    {
        var list = new List<KeyboardDevice>();

        try
        {
            uint count = 0;
            uint size = (uint)Marshal.SizeOf<RawInputDeviceList>();

            if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0)
                return list;

            // 多留两条余量：两次调用之间设备可能插拔
            var buffer = new RawInputDeviceList[count + 2];
            uint got = GetRawInputDeviceList(buffer, ref count, size);
            if (got == uint.MaxValue || got == 0) return list;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < got && i < buffer.Length; i++)
            {
                if (buffer[i].Type != RIM_TYPEKEYBOARD) continue;

                string path = NameOf(buffer[i].Device);
                if (path.Length == 0) continue;
                if (!seen.Add(path)) continue;          // 同一设备可能被重复报告

                list.Add(new KeyboardDevice(
                    path,
                    FriendlyName(path),
                    KindOf(path),
                    IsBuiltIn(path)));
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "枚举键盘设备");
        }

        return list;
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

    /// <summary>
    /// 设备路径 → 类别标签。纯字符串判断，便于自检。
    /// </summary>
    public static string KindOf(string path)
    {
        string p = (path ?? "").ToUpperInvariant();

        if (p.Contains("RDP_")) return Loc.T("远程桌面虚拟键盘");

        /* "Microsoft Keyboard RID" 是终端服务/远程会话的 RID 设备，几乎每台机器都有，
         * 但它不是实体键盘。名字就在路径里，直接按名字认。 */
        if (p.Contains("KEYBOARD RID")) return Loc.T("系统虚拟键盘（RID）");

        if (p.Contains("ACPI")) return Loc.T("内置键盘（ACPI）");

        /* 设备路径用 `#` 分隔，不会有反斜杠 —— 这里两种都认，
         * 因为拿到的可能是原始路径，也可能是已经转换过的枚举路径。 */
        if (p.Contains("ROOT#") || p.Contains("ROOT\\")) return Loc.T("虚拟键盘（ROOT）");

        if (p.Contains("HID")) return Loc.T("外接键盘（HID）");
        if (p.Contains("USB")) return Loc.T("外接键盘（USB）");
        return Loc.T("其它");
    }

    /// <summary>
    /// 把系统条目按"物理设备"归并。
    ///
    /// 为什么必须归并：一只复合键盘常被系统拆成 2~3 条（键盘集合 / 消费者控制集合 / 厂商集合），
    /// 直接列出来用户会以为"我有五只键盘"。归并键用 VID/PID，没有的退回完整路径。
    /// </summary>
    public static IReadOnlyList<KeyboardDeviceGroup> Group(IReadOnlyList<KeyboardDevice>? devices)
    {
        var result = new List<KeyboardDeviceGroup>();
        if (devices == null) return result;

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
        {
            if (d == null) continue;

            string vidpid = VidPidOf(d.Path);
            string key = vidpid.Length > 0 ? "id:" + vidpid : "path:" + d.Path;

            if (index.TryGetValue(key, out int at))
            {
                // 同类条目里优先显示不是"其它"的类别
                var prev = result[at];
                result[at] = prev with
                {
                    Count = prev.Count + 1,
                    Kind = prev.Kind == Loc.T("其它") ? d.Kind : prev.Kind,
                };
                continue;
            }

            index[key] = result.Count;
            result.Add(new KeyboardDeviceGroup(d.Name, d.Kind, 1, d.Path));
        }

        return result;
    }

    /// <summary>是否很可能就是笔记本自带键盘。</summary>
    public static bool IsBuiltIn(string path)
        => (path ?? "").ToUpperInvariant().Contains("ACPI");

    /// <summary>
    /// 从设备路径里抠出 VID/PID，用于在拿不到友好名字时也能区分两只外接键盘。
    /// </summary>
    public static string VidPidOf(string path)
    {
        string p = (path ?? "").ToUpperInvariant();

        int vid = p.IndexOf("VID_", StringComparison.Ordinal);
        int pid = p.IndexOf("PID_", StringComparison.Ordinal);
        if (vid < 0 || pid < 0) return "";

        string v = vid + 4 + 4 <= p.Length ? p.Substring(vid + 4, 4) : "";
        string d = pid + 4 + 4 <= p.Length ? p.Substring(pid + 4, 4) : "";
        return v.Length == 4 && d.Length == 4 ? $"VID_{v}&PID_{d}" : "";
    }

    /// <summary>
    /// 友好名字：优先读注册表里的 DeviceDesc；读不到就退回 VID/PID 或类别。
    ///
    /// 注册表路径由设备路径转换而来：
    ///   `\\?\HID#VID_046D&PID_C31C&MI_00#8&2f...&0&0000#{guid}` 
    ///   → `HKLM\SYSTEM\CurrentControlSet\Enum\HID\VID_046D&PID_C31C&MI_00\8&2f...&0&0000`
    /// </summary>
    public static string FriendlyName(string path)
    {
        string reg = RegistryPathOf(path);
        if (reg.Length > 0)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(reg);
                if (key?.GetValue("DeviceDesc") is string desc && desc.Length > 0)
                {
                    // 形如 "@%SystemRoot%\System32\xxx.dll,-123" 的是本地化资源引用，
                    // 直接解析要 LoadString，代价不值当 —— 退回用 VID/PID。
                    if (!desc.StartsWith('@')) return desc;

                    if (key.GetValue("FriendlyName") is string friendly && friendly.Length > 0)
                        return friendly;
                }
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "读取键盘设备名"); }
        }

        string vidpid = VidPidOf(path);
        if (vidpid.Length > 0) return vidpid;
        return KindOf(path);
    }

    /// <summary>设备路径 → 注册表 Enum 子键路径（不含 HKLM\SYSTEM\CurrentControlSet\Enum\ 前缀）。</summary>
    public static string RegistryPathOf(string path)
    {
        string p = path ?? "";
        if (p.Length == 0) return "";

        // 去掉 \\?\ 与 #{guid} 尾巴
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        int brace = p.IndexOf("#{", StringComparison.Ordinal);
        if (brace > 0) p = p[..brace];

        string[] parts = p.Split('#', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return "";

        // 前两段是 枚举器\硬件ID，其余是实例 ID（可能含 #）
        return "SYSTEM\\CurrentControlSet\\Enum\\" + parts[0] + "\\" + parts[1] +
               "\\" + string.Join('\\', parts.Skip(2));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList
    {
        public IntPtr Device;
        public uint Type;
    }

    /* 只保留数组重载：`null` 与 IntPtr 同时存在时会有重载歧义，
     * 第一次"问数量"的调用传 null 走同一份声明。 */
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(
        [Out] RawInputDeviceList[]? devices, ref uint count, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        IntPtr device, uint command, IntPtr data, ref uint size);
}
