using Microsoft.Win32;

namespace CapsLockPlus.Core.Settings;

/// <summary>开机自启（HKCU\...\Run），不需要管理员权限，卸载时也容易清理。</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>注册表值名（新名称）。</summary>
    private const string ValueName = "KeyboardEnhance";

    /// <summary>
    /// 改名前的注册表值名。
    ///
    /// 软件从 CapsLock+ 改名为按键增强后，用户机器上可能还留着一条
    /// 指向旧 exe 路径的自启项。不清理的话，开机会弹"找不到文件"，
    /// 而设置页又显示"未启用"，用户会莫名其妙。
    /// 所以启动时顺手把旧值删掉（见 <see cref="MigrateLegacyValue"/>）。
    /// </summary>
    private const string LegacyValueName = "CapsLockPlus";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string s) return false;

            /* 不只看有没有值，还要看**指向的文件真的存在**。
             * 用户可能手工把 exe 挪走了，那种情况下"已启用"是假象。 */
            string? path = ExtractExePath(s);
            return path != null && File.Exists(path);
        }
        catch { return false; }
    }

    /// <summary>从命令行里取出 exe 路径（可能带引号、可能带参数）。</summary>
    private static string? ExtractExePath(string commandLine)
    {
        string s = commandLine.Trim();
        if (s.Length == 0) return null;

        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }

        // 无引号：取到第一个空格前的部分
        int sp = s.IndexOf(' ');
        return sp > 0 ? s[..sp] : s;
    }

    /// <summary>
    /// 清理改名前的自启项。
    ///
    /// 只在**新值还没设置**时才迁移，避免覆盖用户当前的选择。
    /// </summary>
    public static void MigrateLegacyValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (key.GetValue(ValueName) != null) return;      // 新值已在，不动

            if (key.GetValue(LegacyValueName) != null)
            {
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                CoreLog.Info("已清理旧的 CapsLockPlus 开机自启项");
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("清理旧自启项失败：" + ex.Message);
        }
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
                // 顺手清掉可能残留的旧值，避免"关了自启却还在启动"
                key.DeleteValue(LegacyValueName, false);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Error("设置开机自启失败", ex);
        }
    }
}
