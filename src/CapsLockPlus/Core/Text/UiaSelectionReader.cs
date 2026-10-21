using System.Runtime.InteropServices;

namespace CapsLockPlus.Core.Text;

/// <summary>
/// 用 UI Automation 直接读取当前选区。
///
/// 这是**不依赖剪贴板、不依赖模拟按键**的取词方式，因此：
///   · 不会改动用户剪贴板
///   · **不受 UIPI 限制** —— 以管理员身份运行时，
///     SendInput 往非管理员的浏览器 / 编辑器投递按键会被系统拦掉，
///     而 UIA 是跨进程 COM 调用，不经过输入队列，照样能读到选区。
///   · 不需要目标窗口在前台获得焦点（读的是"当前有选区"的控件）
///
/// 覆盖范围：实现了 UIA TextPattern 的程序 —— Chromium 系浏览器、
/// Electron 应用、Office、大部分现代编辑器。
/// 自绘 UI（部分国产客户端）没有实现，那边由 WM_COPY / SendInput 兜底。
/// </summary>
public static class UiaSelectionReader
{
    // ---- COM 接口 GUID ----
    private static readonly Guid CLSID_CUIAutomation =
        new("FF48DBA4-60EF-4201-AA87-54103EEF594E");

    private static readonly int UIA_TextPatternId = 10014;

    // ---- UIA 结构 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct SAFEARRAY
    {
        public ushort cDims;
        public ushort fFeatures;
        public uint cbElements;
        public uint cLocks;
        public IntPtr pvData;
        public IntPtr rgsabound;
    }

    /// <summary>
    /// 读当前焦点元素的选区文本。
    ///
    /// 必须在 **STA** 线程调用（COM 要求）。取不到就返回空串，
    /// 绝不抛异常 —— 取词失败不该影响用户操作。
    /// </summary>
    public static string GetSelectedText()
    {
        object? automation = null;
        object? focused = null;
        object? pattern = null;
        object? ranges = null;

        try
        {
            automation = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_CUIAutomation)!);
            if (automation == null) return "";

            dynamic ui = automation;

            // 拿"当前有键盘焦点的元素"
            focused = ui.GetFocusedElement();
            if (focused == null) return "";

            dynamic el = focused;

            // 取 TextPattern
            try
            {
                pattern = el.GetCurrentPattern(UIA_TextPatternId);
            }
            catch { return ""; }

            if (pattern == null) return "";

            dynamic tp = pattern;

            // 取当前所有选区
            try
            {
                ranges = tp.GetSelection();
            }
            catch { return ""; }

            if (ranges == null) return "";

            // SAFEARRAY 里存的是 IUIAutomationTextRange* 数组
            int count = GetArrayLength(ranges);
            if (count <= 0) return "";

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                object? range = GetArrayItem(ranges, i);
                if (range == null) continue;
                try
                {
                    dynamic r = range;
                    string piece = r.GetText(4096) as string ?? "";
                    if (piece.Length > 0) sb.Append(piece);
                }
                catch { }
                finally { Release(range); }
            }

            return sb.ToString();
        }
        catch
        {
            return "";
        }
        finally
        {
            Release(ranges);
            Release(pattern);
            Release(focused);
            Release(automation);
        }
    }

    // ---- SAFEARRAY 辅助 ----
    //
    // UIA 返回的选区集合是 SAFEARRAY(IUnknown*)。C# 没有内建读取器，
    // 这里手工解析头部 + 取元素。

    private static int GetArrayLength(object array)
    {
        try
        {
            IntPtr psa = Marshal.GetIUnknownForObject(array);
            try
            {
                // SAFEARRAY 布局：cDims(2) fFeatures(2) cbElements(4) cLocks(4)
                //                pvData(ptr) rgsabound(ptr)
                // 一维数组的元素个数在 rgsabound[0].cElements
                IntPtr rgsabound = Marshal.ReadIntPtr(psa, 16);
                if (rgsabound == IntPtr.Zero) return 0;
                // SAFEARRAYBOUND: cElements(4) lLbound(4)
                return Marshal.ReadInt32(rgsabound);
            }
            finally { Marshal.Release(psa); }
        }
        catch { return 0; }
    }

    private static object? GetArrayItem(object array, int index)
    {
        try
        {
            IntPtr psa = Marshal.GetIUnknownForObject(array);
            try
            {
                IntPtr pvData = Marshal.ReadIntPtr(psa, 12);
                if (pvData == IntPtr.Zero) return null;

                // 元素是指针（IUnknown*），按 IntPtr 大小步进
                IntPtr elemPtr = Marshal.ReadIntPtr(pvData, index * IntPtr.Size);
                if (elemPtr == IntPtr.Zero) return null;

                return Marshal.GetObjectForIUnknown(elemPtr);
            }
            finally { Marshal.Release(psa); }
        }
        catch { return null; }
    }

    private static void Release(object? com)
    {
        if (com == null) return;
        try
        {
            if (Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
        catch { }
    }
}
