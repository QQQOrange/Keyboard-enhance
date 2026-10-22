using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace KeyboardEnhance.Launcher
{
    /// <summary>
    /// 启动器：在 .NET 之前拦一道。
    ///
    /// 背景：主程序是依赖 .NET 10 桌面运行时的单文件应用。机器上没装运行时时，
    /// 系统的 apphost 只会静默失败（GUI 应用没有控制台可写）或者弹一个英文框，
    /// 用户看到的就是"双击没反应"。
    ///
    /// 本程序用 .NET Framework 4.8 编译（Windows 10/11 自带），所以**任何机器都能跑**：
    ///   · 找到 .NET 10 桌面运行时 → 直接启动主程序，把自己退出；
    ///   · 找不到 → 中文提示，点「确定」打开官方下载页，点「取消」退出。
    ///
    /// 它不做别的事：不写配置、不改注册表、不检查更新 —— 逻辑越少越不容易坏。
    /// </summary>
    internal static class Program
    {
        /// <summary>主程序文件名（与本启动器同目录）。</summary>
        private const string AppFileName = "KeyboardEnhance.App.exe";

        /// <summary>需要的 .NET 主版本。</summary>
        private const int RequiredMajor = 10;

        /// <summary>官方下载页（中文）。页面上「.NET Desktop Runtime 10.x」就是需要装的东西。</summary>
        private const string DownloadPage = "https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0";

        private const string Caption = "按键增强";

        // MessageBox 的按钮/图标常量
        private const uint MB_OKCANCEL = 0x00000001;
        private const uint MB_ICONERROR = 0x00000010;
        private const uint MB_ICONWARNING = 0x00000030;
        private const uint MB_SETFOREGROUND = 0x00010000;
        private const uint MB_TOPMOST = 0x00040000;
        private const int IDOK = 1;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ShellExecuteW(IntPtr hwnd, string op, string file,
            string parameters, string directory, int showCmd);

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string dir = AppDirectory();
                string app = Path.Combine(dir, AppFileName);

                if (!HasDesktopRuntime(RequiredMajorVersion()))
                {
                    AskToInstallRuntime();
                    return 2;
                }

                if (!File.Exists(app))
                {
                    MessageBox("找不到主程序：\n\n" + app + "\n\n"
                        + "请把压缩包里的「KeyboardEnhance.exe」和「KeyboardEnhance.App.exe」\n"
                        + "放在同一个文件夹里再运行。", MB_ICONERROR);
                    return 3;
                }

                var psi = new ProcessStartInfo(app)
                {
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                };
                if (args != null && args.Length > 0) psi.Arguments = JoinArgs(args);

                Process.Start(psi);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox("启动失败：" + ex.Message, MB_ICONERROR);
                return 1;
            }
        }

        /// <summary>本 exe 所在目录（主程序与它同目录）。</summary>
        private static string AppDirectory()
        {
            try
            {
                string loc = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc)) return Path.GetDirectoryName(loc);
            }
            catch { }
            return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        }

        /// <summary>
        /// 需要的 .NET 主版本。
        ///
        /// 允许用环境变量临时改成别的大版本，唯一用途是**验证缺运行时的那条提示**
        /// （正常情况下机器上装着 .NET 10，永远走不到提示分支）。
        /// 没设这个变量时就是 <see cref="RequiredMajor"/>。
        /// </summary>
        private static int RequiredMajorVersion()
        {
            string env = Environment.GetEnvironmentVariable("KE_LAUNCHER_TEST_MAJOR");
            int major;
            if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env.Trim(), out major) && major > 0)
                return major;
            return RequiredMajor;
        }

        // ---------------- .NET 运行时检测 ----------------

        /// <summary>
        /// 有没有装指定主版本的 **桌面** 运行时。
        ///
        /// 为什么要查 Microsoft.WindowsDesktop.App 而不是 Microsoft.NETCore.App：
        /// WPF 依赖桌面运行时；只装了基础运行时（比如某些服务器环境）依然起不来。
        /// </summary>
        private static bool HasDesktopRuntime(int major)
        {
            foreach (string root in DotnetRoots())
            {
                try
                {
                    string shared = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
                    if (!Directory.Exists(shared)) continue;

                    foreach (string v in Directory.GetDirectories(shared))
                        if (MajorOf(Path.GetFileName(v)) == major) return true;
                }
                catch { }
            }

            /* 注册表兜底：安装器一定会写这里。
             * 目录被挪到别处、或者 DOTNET_ROOT 没设时，靠它也能查出来。 */
            string[] keys =
            {
                @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App",
                @"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App",
            };
            foreach (string path in keys)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path))
                    {
                        if (key == null) continue;
                        foreach (string name in key.GetValueNames())
                            if (MajorOf(name) == major) return true;
                    }
                }
                catch { }
            }

            return false;
        }

        /// <summary>可能的 dotnet 安装根目录。</summary>
        private static string[] DotnetRoots()
        {
            var list = new System.Collections.Generic.List<string>();

            void Add(string p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                string t = p.Trim().TrimEnd('\\');
                foreach (string exist in list)
                    if (string.Equals(exist, t, StringComparison.OrdinalIgnoreCase)) return;
                list.Add(t);
            }

            Add(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
            Add(Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"));
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"));

            return list.ToArray();
        }

        /// <summary>从 "10.0.13" 这种目录名/值名里取出主版本；取不到返回 -1。</summary>
        private static int MajorOf(string version)
        {
            if (string.IsNullOrEmpty(version)) return -1;

            int i = 0;
            while (i < version.Length && version[i] >= '0' && version[i] <= '9') i++;
            if (i == 0) return -1;

            int major;
            return int.TryParse(version.Substring(0, i), out major) ? major : -1;
        }

        // ---------------- 提示 ----------------

        /// <summary>缺运行时的中文提示：点确定打开下载页，点取消退出。</summary>
        private static void AskToInstallRuntime()
        {
            string text =
                "「按键增强」需要 .NET 10 桌面运行时（.NET Desktop Runtime 10.x，64 位）。\n\n"
                + "这台电脑上没找到它，所以程序无法启动。\n\n"
                + "点「确定」会自动打开下载页面：\n"
                + "　　在页面里选「.NET Desktop Runtime 10.x」→ Windows x64 安装包\n"
                + "　　装完后再打开「按键增强」即可。\n\n"
                + "点「取消」退出。";

            int r = MessageBoxW(IntPtr.Zero, text, Caption,
                MB_OKCANCEL | MB_ICONWARNING | MB_SETFOREGROUND | MB_TOPMOST);

            if (r == IDOK) OpenUrl(DownloadPage);
        }

        private static void MessageBox(string text, uint icon)
        {
            MessageBoxW(IntPtr.Zero, text, Caption, MB_OK | icon | MB_SETFOREGROUND | MB_TOPMOST);
        }

        private const uint MB_OK = 0x00000000;

        /// <summary>用默认浏览器打开链接。</summary>
        private static void OpenUrl(string url)
        {
            try
            {
                ShellExecuteW(IntPtr.Zero, "open", url, null, null, 1);
            }
            catch (Exception ex)
            {
                MessageBox("打开浏览器失败，请手动访问：\n" + url + "\n\n" + ex.Message, MB_ICONERROR);
            }
        }

        // ---------------- 参数透传 ----------------

        /// <summary>把命令行参数原样交给主程序（--tray / --selftest / --render …）。</summary>
        private static string JoinArgs(string[] args)
        {
            var sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a));
            }
            return sb.ToString();
        }

        /// <summary>按 Windows 命令行规则加引号（net48 没有 ArgumentList）。</summary>
        private static string Quote(string s)
        {
            if (s == null) return "\"\"";
            if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;

            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"') sb.Append('\\');
                sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
