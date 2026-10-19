using System.Collections.Concurrent;

namespace CapsLockPlus.Core;

/// <summary>
/// 单线程动作队列。钩子回调里绝对不能做耗时操作（超过 ~300ms 会被系统静默摘掉钩子），
/// 所以所有功能动作都丢进这里按顺序执行。
/// </summary>
public sealed class ActionQueue : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;
    private volatile bool _disposed;

    public ActionQueue(string name = "CapsLockPlus.Actions")
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                CoreLog.Error("动作执行失败", ex);
            }
        }
    }

    public void Post(Action action)
    {
        if (_disposed) return;
        try { _queue.Add(action); } catch (InvalidOperationException) { /* 已关闭 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
    }
}

/// <summary>极简日志：写入 %LocalAppData%\CapsLockPlus\logs\app.log。</summary>
public static class CoreLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string LogPath => _path ??= Path.Combine(AppPaths.DataDir, "logs", "app.log");

    /// <summary>控制台输出开关：带 --render / --selftest 时关闭，避免污染命令行输出。</summary>
    public static bool EchoToConsole { get; set; }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024) fi.Delete();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (ex != null) line += Environment.NewLine + ex;
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志失败不能影响主流程
        }
    }
}

/// <summary>程序使用的目录约定。</summary>
public static class AppPaths
{
    /// <summary>
    /// 数据目录。可用环境变量 CAPSLOCKPLUS_DATA 覆盖（便携模式 / 测试用），
    /// 默认在 %LocalAppData%\CapsLockPlus。
    /// </summary>
    public static string DataDir { get; } = ResolveDataDir();

    private static string ResolveDataDir()
    {
        var custom = Environment.GetEnvironmentVariable("CAPSLOCKPLUS_DATA");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                Directory.CreateDirectory(custom);
                return custom;
            }
            catch { }
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CapsLockPlus");
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string BindingsFile => Path.Combine(DataDir, "winsbind.json");
    public static string AppDir => AppContext.BaseDirectory;
}
