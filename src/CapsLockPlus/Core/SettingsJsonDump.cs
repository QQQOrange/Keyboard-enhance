using System;
using System.IO;
using System.Text.Json;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core;

/// <summary>`--dump-settings-json [文件]`：把解密后的设置写成可读 JSON，仅用于本地排查。</summary>
internal static class SettingsJsonDump
{
    public static int Run(string[] args)
    {
        string? path = ValueOf(args) ?? AppPaths.SettingsFile;
        if (!Path.IsPathRooted(path)) path = Path.Combine(AppPaths.DataDir, path);

        if (!SettingsStore.TryLoadFile(path, out var s, out string error))
        {
            Console.Out.WriteLine("读不出来：" + error);
            return 2;
        }

        string output = Path.Combine(Path.GetTempPath(), "clp-settings.json");
        File.WriteAllText(output, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        Console.Out.WriteLine(output);
        return 0;
    }

    private static string? ValueOf(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--dump-settings-json", StringComparison.OrdinalIgnoreCase)) continue;
            string next = args[i + 1];
            return next.StartsWith("--", StringComparison.Ordinal) ? null : next;
        }
        return null;
    }
}
