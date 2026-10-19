using Microsoft.Win32;

namespace CapsLockPlus.Core.Settings;

/// <summary>开机自启（HKCU\...\Run），不需要管理员权限，卸载时也容易清理。</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CapsLockPlus";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Contains("CapsLockPlus", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled, string? exePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;
            if (enabled)
            {
                string path = exePath ?? Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(path)) key.SetValue(ValueName, $"\"{path}\" --tray");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Error("设置开机自启失败", ex);
        }
    }
}
