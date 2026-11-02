using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace CapsLockPlus.Core;

/// <summary>
/// 极简日志：写入数据目录下的 logs\app.log。
///
/// 性能要求（踩过两次）：
///   · 旧实现每写一行都 `Directory.CreateDirectory` + `new FileInfo` + `File.AppendAllText`，
///     等于一次日志 = 三次文件系统调用，而且全程持一把进程级锁。
///   · 第二个坑（v4.5.65 修）：虽然改成了长开的 StreamWriter，但**每行仍然 flush**，
///     而低级键盘钩子的回调里也会写日志（切层、执行动作、trace）。
///     只要磁盘慢一拍（同时开着云同步 / 自动备份 / 安全软件在扫），
///     回调就会卡在 flush 上 —— 超过 LowLevelHooksTimeout(300ms) 后系统会
///     **把钩子摘掉并丢弃这次输入**，症状是"热键全失灵、长按触发键一直输出原键"，
///     而钩子句柄还在、线程还活着、状态显示一切正常。
///
/// 现在的模型：**调用方永不碰文件**。
///   · Write 只做字符串拼装 + 入队（无锁），微秒级返回；
///   · 后台泵线程负责真正写盘，按批 flush；
///   · 队列有上限，写不过来时丢弃并计数，绝不反压按键路径；
///   · Flush / Shutdown 会同步排空，保证退出前不丢日志。
/// </summary>
public static class CoreLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string? _path;
    private static long _bytes;

    /// <summary>单文件上限，超过就换新文件（旧行为：直接删）。</summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>待写队列上限：超过就丢弃（日志永远不能拖慢按键）。</summary>
    private const int MaxPending = 4000;

    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly AutoResetEvent Signal = new(false);
    private static readonly object PumpGate = new();
    private static Thread? _pump;
    private static long _dropped;

    /// <summary>因为队列满而丢掉的日志条数（诊断用）。</summary>
    public static long DroppedLines => Interlocked.Read(ref _dropped);

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
    private static readonly ConcurrentDictionary<string, long> SwallowTicks = new();
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

        if (EchoToConsole)
        {
            try { Console.WriteLine(line); } catch { }
        }

        /* 入队即返回：**绝不在调用方线程上做文件 IO**。
         * 钩子回调、动作队列、界面线程都可能走到这里。 */
        if (Pending.Count >= MaxPending)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        Pending.Enqueue(line);
        EnsurePump();
        try { Signal.Set(); } catch { }
    }

    private static void EnsurePump()
    {
        if (_pump != null) return;
        lock (PumpGate)
        {
            if (_pump != null) return;
            _pump = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "CapsLockPlus.LogPump",
                Priority = ThreadPriority.BelowNormal,
            };
            _pump.Start();
        }
    }

    private static void PumpLoop()
    {
        var batch = new List<string>(128);
        while (true)
        {
            try
            {
                Signal.WaitOne(250);
                DrainOnce(batch);
            }
            catch
            {
                // 泵线程绝不能死：日志失败就丢掉这一批
                try { Thread.Sleep(200); } catch { }
            }
        }
    }

    /// <summary>把队列里现有的行写出去（一批最多 256 行，写完 flush 一次）。</summary>
    private static void DrainOnce(List<string> batch)
    {
        while (true)
        {
            batch.Clear();
            while (batch.Count < 256 && Pending.TryDequeue(out string? line)) batch.Add(line);
            if (batch.Count == 0) return;

            lock (Gate)
            {
                try
                {
                    EnsureOpenLocked();
                    if (_bytes > MaxBytes) RotateLocked();
                    foreach (string line in batch)
                    {
                        _writer!.WriteLine(line);
                        _bytes += line.Length + Environment.NewLine.Length;
                    }
                    FlushLocked();
                }
                catch
                {
                    // 日志失败不能影响主流程；顺手关掉句柄，下次写再重开
                    ResetLocked();
                }
            }
        }
    }

    /// <summary>把缓冲刷到磁盘（退出、崩溃前、用户点"打开日志"时调用）。</summary>
    public static void Flush()
    {
        var batch = new List<string>(256);
        DrainOnce(batch);          // 先排空队列
        lock (Gate) FlushLocked();
    }

    /// <summary>退出前收尾：排空、flush 并释放句柄，避免文件被占用。</summary>
    public static void Shutdown()
    {
        var batch = new List<string>(256);
        DrainOnce(batch);

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
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
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
