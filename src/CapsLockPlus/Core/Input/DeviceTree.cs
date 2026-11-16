using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CapsLockPlus.Core.Input;

/// <summary>
/// 设备树查询：从一条 HID 设备往上找到"物理设备"，并取它的产品名。
///
/// ## 为什么需要它
///
/// Raw Input 报上来的是 **HID 集合**，不是物理设备。一只键盘在系统里通常是：
///
///     USB\VID_1532&amp;PID_00A4\5&amp;1a2b3c&amp;0&amp;01        ← 物理设备（有产品名）
///       USB\VID_1532&amp;PID_00A4&amp;MI_00\7&amp;...&amp;0&amp;0000    ← 接口
///         HID\VID_1532&amp;PID_00A4&amp;MI_00\8&amp;...&amp;0&amp;0000   ← 集合（Raw Input 看到的就是这层）
///
/// 只查最后一层的 `DeviceDesc`，拿到的是驱动给的通用名（「HID Keyboard Device」），
/// 五只键盘四只同名，用户根本分不清。产品名挂在上面的 USB 节点上。
///
/// 复合设备还会把一只键盘拆成 2~3 个集合（键盘、多媒体键、系统控制），
/// 按物理设备归并才能让列表中"一只键盘只占一行"。
/// </summary>
internal static class DeviceTree
{
    private const int MaxDepth = 6;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0;

    /// <summary>实例 ID（`USB\VID_x&amp;PID_y\serial`）的父节点；没有父节点返回空串。</summary>
    public static string Parent(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return "";

        try
        {
            if (CM_Locate_DevNodeW(out uint node, instanceId, CM_LOCATE_DEVNODE_NORMAL) != 0) return "";
            if (CM_Get_Parent(out uint parent, node, 0) != 0 || parent == 0) return "";

            var sb = new StringBuilder(512);
            if (CM_Get_Device_IDW(parent, sb, sb.Capacity, 0) != 0) return "";
            return sb.ToString();
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "查询设备父节点");
            return "";
        }
    }

    /// <summary>
    /// 往上走到物理设备那一层：`HID\…` → `USB\VID_x&amp;PID_y&amp;MI_00\…`（接口）→ `USB\VID_x&amp;PID_y\…`。
    /// 找不到父节点（虚拟设备、ACPI 端点）就返回自己。
    /// </summary>
    public static string Physical(string? instanceId)
    {
        string id = instanceId ?? "";
        if (id.Length == 0) return "";

        for (int depth = 0; depth < MaxDepth; depth++)
        {
            if (!IsCollectionOrInterface(id)) return id;

            string parent = Parent(id);
            if (parent.Length == 0) return id;
            id = parent;
        }

        return id;
    }

    /// <summary>HID 集合本身或 USB 接口节点 —— 这两层不是"物理设备"。</summary>
    private static bool IsCollectionOrInterface(string id)
    {
        if (id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.StartsWith("RZVIRTUAL\\", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.StartsWith("VIRTUAL_HID\\", StringComparison.OrdinalIgnoreCase)) return true;
        return id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)
            || id.Contains("&COL", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 产品名：从物理设备往上一层层找"不像驱动默认名"的那个。
    /// 找不到返回空串，由调用方接着用 HID 层的名字兜底。
    /// </summary>
    public static string ProductName(string? instanceId)
    {
        string id = Physical(instanceId);
        if (id.Length == 0) return "";

        /* USB 设备自己报上来的产品名（iProduct 字符串）：
         * 「Razer Mouse Dock Pro」「LDN 2.4G-S64」都在这，注册表里反而只有
         * 「USB Composite Device」这种驱动默认名。 */
        string bus = BusReportedName(id);
        if (bus.Length > 0 && !IsGeneric(bus)) return bus;

        string own = NameAt(id);
        if (own.Length > 0 && !IsGeneric(own)) return own;

        /* 接口节点（USB\…&MI_01\…）有时才有名字（Razer 的 Mic 口就是这样），看一眼。 */
        string parent = Parent(id);
        if (parent.Length > 0 && SharesVidPid(id, parent))
        {
            string pbus = BusReportedName(parent);
            if (pbus.Length > 0 && !IsGeneric(pbus)) return pbus;

            string pname = NameAt(parent);
            if (pname.Length > 0 && !IsGeneric(pname)) return pname;
        }

        /* 都没有就报厂商 —— 「Apple 设备 05AC:0265」也比「HID Keyboard Device」有用。
         * 注意：**绝不**再往上爬，上一层是 USB 集线器，爬上去满屏都是「Generic USB Hub」。 */
        return "";
    }

    private static bool SharesVidPid(string a, string b)
    {
        string va = KeyboardDevices.VidPidOf(a), vb = KeyboardDevices.VidPidOf(b);
        return va.Length > 0 && va.Equals(vb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>USB 设备报上来的产品名（DEVPKEY_Device_BusReportedDeviceDesc）。</summary>
    public static string BusReportedName(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return "";

        try
        {
            if (CM_Locate_DevNodeW(out uint node, instanceId, CM_LOCATE_DEVNODE_NORMAL) != 0) return "";

            var key = BusReportedKey;
            uint size = 0;

            /* 第一次只问大小：返回 CR_BUFFER_SMALL 是正常流程，不能当失败。 */
            CM_Get_DevNode_PropertyW(node, ref key, out _, null, ref size, 0);
            if (size == 0 || size > 4096) return "";

            var buffer = new byte[size];
            int hr = CM_Get_DevNode_PropertyW(node, ref key, out _, buffer, ref size, 0);
            if (hr != 0 || size < 2) return "";

            string text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length));
            int nul = text.IndexOf('\0');
            if (nul >= 0) text = text[..nul];
            return text.Trim();
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "读取设备产品名");
            return "";
        }
    }

    private static readonly DEVPROPKEY BusReportedKey = new()
    {
        FormatId = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"),
        PropertyId = 4,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>
    /// Raw Input 的设备路径 → 实例 ID：
    /// `\\?\HID#VID_046D&PID_C31C&MI_00#8&2f…&0&0000#{guid}` → `HID\VID_046D&PID_C31C&MI_00\8&2f…&0&0000`。
    /// 已经是实例 ID 的原样返回。
    /// </summary>
    public static string InstanceOf(string? rawPath)
    {
        string p = (rawPath ?? "").Trim();
        if (p.Length == 0) return "";

        if (!p.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return p.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("RZVIRTUAL\\", StringComparison.OrdinalIgnoreCase)
                ? p
                : "";
        }

        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];

        int brace = p.IndexOf("#{", StringComparison.Ordinal);
        if (brace > 0) p = p[..brace];

        string[] parts = p.Split('#', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return "";

        var sb = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append('\\');
            sb.Append(parts[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 注册表里这条设备的 FriendlyName / DeviceDesc（已解资源引用）。
    /// 读不到返回空串。
    /// </summary>
    public static string NameAt(string? instanceId)
    {
        string reg = RegistryPath(instanceId);
        if (reg.Length == 0) return "";

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(reg);
            if (key == null) return "";

            foreach (string valueName in FriendlyFirst)
            {
                if (key.GetValue(valueName) is not string raw || raw.Length == 0) continue;

                string resolved = ResolveIndirect(raw);
                if (resolved.Length > 0) return resolved;
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "读取设备名"); }

        return "";
    }

    private static readonly string[] FriendlyFirst = { "FriendlyName", "DeviceDesc" };

    /// <summary>
    /// 实例 ID → 注册表 Enum 子键。`USB\VID_x&amp;PID_y\serial`
    /// → `SYSTEM\CurrentControlSet\Enum\USB\VID_x&amp;PID_y\serial`。
    /// </summary>
    public static string RegistryPath(string? instanceId)
    {
        string id = (instanceId ?? "").Trim();
        if (id.Length == 0) return "";

        // 也接受 Raw Input 的 `\\?\HID#VID_x&PID_y#8&…#{guid}` 形式
        if (id.StartsWith(@"\\?\", StringComparison.Ordinal)) return KeyboardDevices.RegistryPathOf(id);

        int brace = id.IndexOf("#{", StringComparison.Ordinal);
        if (brace > 0) id = id[..brace];

        string[] parts = id.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "";

        var sb = new StringBuilder("SYSTEM\\CurrentControlSet\\Enum\\");
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append('\\');
            sb.Append(parts[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 驱动默认名 —— 见到这些说明"这不是产品名"，还要继续往上找。
    /// 中英文系统都可能出现，所以两边都列。
    /// </summary>
    public static bool IsGeneric(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0) return true;

        foreach (string prefix in GenericPrefixes)
        {
            if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (string exact in GenericExact)
        {
            if (n.Equals(exact, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return n.Contains("VID_", StringComparison.OrdinalIgnoreCase)
            && n.Contains("PID_", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] GenericPrefixes =
    {
        "HID-compliant", "HID 符合", "符合 HID", "USB Input Device", "USB 输入设备",
        "USB Composite Device", "USB 复合设备", "Microsoft Input Configuration",
        "Microsoft 输入配置", "HID Keyboard Device", "HID 键盘设备",
    };

    private static readonly string[] GenericExact =
    {
        "HID-compliant device", "HID-compliant mouse", "HID-compliant touch pad",
        "HID-compliant consumer control device", "HID-compliant system controller",
    };

    /// <summary>解析 `@keyboard.inf,%hid.keyboarddevice%;HID Keyboard Device` 这类资源引用。</summary>
    public static string ResolveIndirect(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        string text = raw.Trim();
        if (!text.StartsWith('@')) return text;

        int semi = text.LastIndexOf(';');
        if (semi > 0 && semi < text.Length - 1)
        {
            string tail = text[(semi + 1)..].Trim();
            if (tail.Length > 0 && !tail.StartsWith('@')) return tail;
        }

        try
        {
            var buffer = new StringBuilder(512);
            if (SHLoadIndirectString(text, buffer, buffer.Capacity, IntPtr.Zero) != 0) return "";

            string resolved = buffer.ToString().Trim();
            return resolved.Length > 0 && !resolved.StartsWith('@') ? resolved : "";
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "解析设备资源名");
            return "";
        }
    }

    /// <summary>物理设备的枚举器（USB / ACPI / ROOT / BTHENUM …），用于分组展示。</summary>
    public static string EnumeratorOf(string? instanceId)
    {
        string id = instanceId ?? "";
        int slash = id.IndexOf('\\');
        return slash > 0 ? id[..slash].ToUpperInvariant() : "";
    }

    /// <summary>
    /// 常见外设厂商（按 USB 厂商号）。只用来在完全没有产品名时给个能认的标签，
    /// 比如 Apple 触控板不报 iProduct，至少能显示「Apple 设备」而不是「HID Keyboard Device」。
    /// </summary>
    public static string VendorOf(string? key)
    {
        string k = (key ?? "").ToUpperInvariant();
        int vid = k.IndexOf("VID_", StringComparison.Ordinal);
        if (vid < 0 || vid + 8 > k.Length) return "";

        string id = k.Substring(vid + 4, 4);
        return Vendors.TryGetValue(id, out string? name) ? name : "";
    }

    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["05AC"] = "Apple",
        ["1532"] = "Razer",
        ["046D"] = "罗技",
        ["0483"] = "STMicro",
        ["0B05"] = "华硕",
        ["045E"] = "微软",
        ["04F2"] = "群光",
        ["04D9"] = "Holtek",
        ["258A"] = "SINO WEALTH",
        ["0C45"] = "松翰",
        ["1B1C"] = "海盗船",
        ["0951"] = "金士顿",
        ["413C"] = "戴尔",
        ["17EF"] = "联想",
        ["03F0"] = "惠普",
        ["1050"] = "Yubico",
        ["291A"] = "Anker",
        ["05E3"] = "创惟",
        ["174C"] = "祥硕",
        ["20DD"] = "Sony",
        ["3434"] = "Keychron",
        ["320F"] = "AKKO",
    };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[]? propertyBuffer, ref uint propertyBufferSize, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int length, uint flags);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SHLoadIndirectString")]
    private static extern int SHLoadIndirectString(string source, StringBuilder buffer, int length, IntPtr reserved);
}
