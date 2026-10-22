using System.IO;
using System.Text;

namespace CapsLockPlus.Core;

/// <summary>
/// 极简日志：写入数据目录下的 logs\app.log。
///
/// 性能要求（以前踩过）：
///   · 旧实现每写一行都 `Directory.CreateDirectory` + `new FileInfo` + `File.AppendAllText`，
///     等于一次日志 = 三次文件系统调用，而且全程持一把进程级锁。
///     按键路径每秒能写十几条 → 钩子线程被文件 IO 拖住，表现为"偶尔卡一下"。
///   · 现在改成**长开一个 StreamWriter** 并逐行 flush（见下方 Write 的说明），
///     体积检查用内存计数器（不再 stat）。
///
/// 线程模型：钩子线程 / 动作队列 / 界面线程都会调用，全部走同一把 <see cref="Gate"/>，
/// 但临界区里只剩一次内存写入（偶尔一次 flush），不会阻塞按键。
/// </summary>
public static class CoreLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string? _path;
    private static long _bytes;

    /// <summary>单文件上限，超过就换新文件（旧行为：直接删）。</summary>
    private const long MaxBytes = 2 * 1024 * 1024;



    public static string LogPath => _path ??= Path.Combine(AppPaths.DataDir, "logs", "app.log");

    /// <summary>控制台输出开关：带 --render / --selftest 时关闭，避免污染命令行输出。</summary>
    public static bool EchoToConsole { get; set; }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /* ---- 允许静默、但要留痕的异常 ----
     *
     * 全仓有一批 `catch { }`：清理由、探测型、回退型。它们**不该**弹窗打扰用户，
     * 但也不该彻底消失 —— 用户报"某个功能偶尔不灵"时，日志里没有任何线索，
     * 只能靠猜（这就是审视报告里 P1-1 的实质问题）。
     *
     * Swallow() 给这类地方一个统一的出口：
     *   · 写 WARN，带场景名与异常类型，能在 app.log 里搜到；
     *   · 同一场景 60 秒内只记一次，避免"每次按键都失败"把日志刷爆。
     */
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> SwallowTicks = new();
    private const int SwallowWindowMs = 60_000;

    /// <summary>
    /// 吞掉一个预期内的异常，但在日志里留一行（同一场景每分钟最多一次）。
    /// 用于"失败可接受、但要能事后定位"的地方。
    /// </summary>
    public static void Swallow(Exception? ex, string scene)
    {
        if (ex == null) return;

        long now = Environment.TickCount64;
        if (SwallowTicks.TryGetValue(scene, out long last) && now - last < SwallowWindowMs) return;
        SwallowTicks[scene] = now;

        Warn($"[已忽略] {scene}：{ex.GetType().Name} {ex.Message}");
    }

    private static void Write(string level, string message, Exception? ex)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (ex != null) line += Environment.NewLine + ex;

        lock (Gate)
        {
            try
            {
                EnsureOpenLocked();
                if (_bytes > MaxBytes) RotateLocked();
                _writer!.WriteLine(line);
                _bytes += line.Length + Environment.NewLine.Length;

                /* 每行都跟着 flush（AutoFlush）。
                 *
                 * 曾经想攒 20 行再刷，结果实测：进程被强制结束时，
                 * 那 20 行以内的日志全丢 —— 而"程序被杀了/崩了"恰恰是最需要日志的时刻。
                 * 现在只有一个常开的 StreamWriter + 一次 flush 系统调用（约 10µs），
                 * 相比旧实现（每次 open/close 文件）仍然是数量级的下降。 */
            }
            catch
            {
                // 日志失败不能影响主流程；顺手关掉句柄，下次写再重开
                ResetLocked();
            }
        }

        if (EchoToConsole)
        {
            try { Console.WriteLine(line); } catch { }
        }
    }

    /// <summary>把缓冲刷到磁盘（退出、崩溃前、用户点"打开日志"时调用）。</summary>
    public static void Flush()
    {
        lock (Gate) FlushLocked();
    }

    /// <summary>退出前收尾：flush 并释放句柄，避免文件被占用。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            FlushLocked();
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }

    private static void EnsureOpenLocked()
    {
        if (_writer != null) return;

        string path = LogPath;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // FileShare.ReadWrite：用户/工具可以在程序运行时查看日志
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _bytes = stream.Length;
    }

    private static void FlushLocked()
    {
        try { _writer?.Flush(); } catch { }
    }

    /// <summary>超过上限：关掉、删掉、重开（和旧行为一致：只留最近一段）。</summary>
    private static void RotateLocked()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        try { File.Delete(LogPath); } catch { }
        _bytes = 0;
        EnsureOpenLocked();
    }

    private static void ResetLocked()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        _bytes = 0;
    }
}
