using System.Windows;
using Microsoft.Win32;

namespace CapsLockPlus.UI.Theme;

/// <summary>主题切换：浅色 / 深色 / 跟随系统。</summary>
public static class ThemeManager
{
    public enum Mode { Light, Dark, System }

    public static Mode Current { get; private set; } = Mode.Light;

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
