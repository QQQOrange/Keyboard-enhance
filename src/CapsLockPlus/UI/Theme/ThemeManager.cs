using System.Windows;
using CapsLockPlus.Core;
using Microsoft.Win32;

namespace CapsLockPlus.UI.Theme;

/// <summary>主题切换：浅色 / 深色 / 跟随系统。</summary>
public static class ThemeManager
{
    public enum Mode { Light, Dark, System }

    public static Mode Current { get; private set; } = Mode.Light;

    /// <summary>
    /// 监听系统主题变化的计时器。
    ///
    /// 为什么需要它：Windows 切换深浅色时**不会**主动通知普通应用，
    /// 只有重新读取注册表才知道。以前只在启动时读一次，所以"跟随系统"
    /// 要等下次打开软件才生效，看起来就是"切换不灵敏"。
    /// </summary>
    private static System.Windows.Threading.DispatcherTimer? _watch;
    private static bool _lastSystemDark;

    public static bool IsDarkSystem
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }
    }

    public static void Apply(Mode mode)
    {
        Current = mode;
        bool dark = mode == Mode.Dark || (mode == Mode.System && IsDarkSystem);
        _lastSystemDark = IsDarkSystem;
        var uri = new Uri(dark ? "Theme/Theme.Dark.xaml" : "Theme/Theme.Light.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };
        var app = Application.Current;
        if (app == null) return;
        var existing = app.Resources.MergedDictionaries
            .Where(d => d.Source != null && d.Source.OriginalString.Contains("Theme."))
            .ToList();
        foreach (var d in existing) app.Resources.MergedDictionaries.Remove(d);
        app.Resources.MergedDictionaries.Insert(0, dict);
        ThemeChanged?.Invoke(dark);

        // 跟随系统模式下开始监听，其它模式没必要
        if (mode == Mode.System) StartWatching();
        else StopWatching();
    }

    /// <summary>
    /// 开始监听系统主题变化（1 秒轮询一次，开销可忽略）。
    /// 只在"跟随系统"模式下运行；切到固定主题时自动停止。
    /// </summary>
    public static void StartWatching()
    {
        if (_watch != null) return;
        _lastSystemDark = IsDarkSystem;
        _watch = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _watch.Tick += (_, _) =>
        {
            if (Current != Mode.System) return;
            bool now = IsDarkSystem;
            if (now == _lastSystemDark) return;
            _lastSystemDark = now;
            CoreLog.Info($"系统主题变为{(now ? "深色" : "浅色")}，应用新主题");
            Apply(Mode.System);
        };
        _watch.Start();
    }

    public static void StopWatching()
    {
        _watch?.Stop();
        _watch = null;
    }

    public static event Action<bool>? ThemeChanged;

    public static Mode Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "dark" => Mode.Dark,
        "system" => Mode.System,
        _ => Mode.Light,
    };

    public static string ToSetting(Mode mode) => mode switch
    {
        Mode.Dark => "dark",
        Mode.System => "system",
        _ => "light",
    };
}
