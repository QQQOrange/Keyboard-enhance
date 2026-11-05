using CapsLockPlus.Core.Security;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CapsLockPlus.Core;

/// <summary>
/// 诊断命令的输出通道。
///
/// 为什么需要它：本程序是**窗口型 exe**（WinExe），没有控制台。
/// 用户在 PowerShell 里直接 `<c>&amp; KeyboardEnhance.exe --hooktest</c> 时：
///   · PowerShell 不等 GUI 程序，命令"立刻返回"，看起来像没反应；
///   · 即使等到进程结束，<c>Console.WriteLine</c> 也没有控制台可写，输出直接丢掉。
///
/// 于是诊断结果谁都看不到 —— 这正是"发了命令一点反应都没有"的原因。
///
/// 现在统一走这里：
///   ① 尝试挂上父进程的控制台（从命令行启动时能看到文字）；
///   ② 一定写一份到数据目录 <c>logs\*.txt</c>；
///   ③ 还弹一个消息框 —— 双击运行也能看到结论。
/// </summary>
internal static class CliOutput
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    private const int AttachParentProcess = -1;

    /// <summary>报告写到哪里（数据目录下）。</summary>
    public static string ReportPath(string name)
        => Path.Combine(AppPaths.DataDir, "logs", name);

    /// <summary>
    /// 输出一段诊断结果：能挂上控制台就打印，同时写文件，并弹窗显示。
    /// </summary>
    /// <param name="title">窗口标题</param>
    /// <param name="fileName">报告文件名（logs 目录下）</param>
    /// <param name="text">报告正文</param>
    public static void Report(string title, string fileName, string text)
    {
        bool hasConsole = false;

        // ① 从命令行启动时，挂到父进程的控制台上
        try
        {
            if (AttachConsole(AttachParentProcess))
            {
                hasConsole = true;
                var stdout = Console.OpenStandardOutput();
                Console.SetOut(new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });
                Console.WriteLine();
                Console.WriteLine(text);
            }
        }
        catch { /* 没有控制台是常态 */ }

        // ② 一定落盘，方便用户把文件发出来
        string path = ReportPath(fileName);
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            SecureFile.WriteAllText(path, text + Environment.NewLine);
        }
        catch { /* 写不了就只弹窗 */ }

        // ③ 弹窗：双击运行 / PowerShell 不等 GUI 时，这是唯一能看见的出口
        try
        {
            string body = text;
            try { if (File.Exists(path)) body += Environment.NewLine + "报告已保存：" + path; } catch { }
            System.Windows.MessageBox.Show(body, title,
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        catch { /* 无界面环境（如服务）时忽略 */ }

        if (hasConsole)
        {
            try { FreeConsole(); } catch { }
        }
    }
}
