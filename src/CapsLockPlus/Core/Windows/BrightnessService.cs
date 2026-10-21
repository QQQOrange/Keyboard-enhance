using System;
using System.Management;

namespace CapsLockPlus.Core.Windows;

/// <summary>
/// 屏幕亮度调节（走 WMI 的 WmiMonitorBrightnessMethods）。
///
/// 适用范围：笔记本内置屏、以及驱动正确暴露了亮度接口的外接屏。
/// 台式机 + 普通显示器的组合通常没有这条 WMI 接口（DDC/CI 另说），
/// 这时 <see cref="Set"/> 返回 false，由上层提示用户，而不是静默失败。
///
/// 为什么缓存上次亮度：WMI 查询一次要 200~500ms，长按连续调亮度时
/// 每次都查询会明显卡顿。写入成功后把值记下来，后续直接用缓存推算。
/// </summary>
public static class BrightnessService
{
    private const string Scope = @"root\wmi";
    private const string ClassMethods = "WmiMonitorBrightnessMethods";
    private const string ClassState = "WmiMonitorBrightness";

    /// <summary>上次成功读写的亮度（-1 = 还不知道）。</summary>
    private static int _last = -1;

    private static readonly object Gate = new();

    /// <summary>当前亮度（0~100）；这台设备不支持时返回 -1。</summary>
    public static int Get()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope,
                $"SELECT CurrentBrightness FROM {ClassState}");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    object? v = item["CurrentBrightness"];
                    if (v != null)
                    {
                        int val = Convert.ToInt32(v);
                        _last = val;
                        return val;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[亮度] 读取失败：" + ex.Message);
        }
        return -1;
    }

    /// <summary>设置绝对亮度（0~100）。成功返回 true。</summary>
    public static bool Set(int percent)
    {
        lock (Gate)
        {
            try
            {
                percent = Math.Clamp(percent, 0, 100);

                using var searcher = new ManagementObjectSearcher(Scope,
                    $"SELECT * FROM {ClassMethods}");

                bool any = false;
                foreach (ManagementObject item in searcher.Get())
                {
                    using (item)
                    {
                    try
                    {
                        // WmiSetBrightness(TimeoutSeconds, Brightness)
                        item.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)percent });
                        any = true;
                    }
                    catch (Exception ex)
                    {
                        CoreLog.Warn("[亮度] 写入失败：" + ex.Message);
                    }
                    }
                }

                if (any) _last = percent;
                return any;
            }
            catch (Exception ex)
            {
                CoreLog.Warn("[亮度] 不可用：" + ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// 在当前亮度上加减 <paramref name="delta"/> 个百分点。
    /// 返回值：null = 这台设备不支持；否则是调整后的亮度。
    /// </summary>
    public static int? Step(int delta)
    {
        int cur = _last >= 0 ? _last : Get();
        if (cur < 0) return null;

        int target = Math.Clamp(cur + delta, 0, 100);
        // 已经到边界就不必再走一次 WMI（0/100 再按只会白等 300ms）
        if (target == cur && Set(cur)) return cur;

        return Set(target) ? target : null;
    }

    /// <summary>这台机器是否支持软件调亮度（用于界面提示 / 自检）。</summary>
    public static bool IsSupported() => Get() >= 0;
}
