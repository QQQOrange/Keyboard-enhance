using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CapsLockPlus.Core.Input;
using CapsLockPlus.Core.Settings;
using CapsLockPlus.Core.Text;
using CapsLockPlus.Core.Windows;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Actions;

/// <summary>
/// 打开网址 / 文件夹 / 文件 / 程序，以及截图。与 ActionContext 同属 partial 类。
/// </summary>
public sealed partial class ActionContext
{

    /// <summary>
    /// 仅供自检：拦截"打开网址"这一步，免得测试真的拉起浏览器。
    /// 置为 null 时走真实的 Process.Start。
    /// </summary>
    public static Action<string>? OpenUrlInterceptForTest { get; set; }

    public void OpenUrl(string url) => OpenUrlCore(url, normalize: true);

    /// <summary>
    /// 打开一个**已经构造好**的网址，不再做规范化。
    ///
    /// 「直接搜索」拼出来的 URL 里，关键词已经过 <see cref="Uri.EscapeDataString"/>
    /// 编码（<c>%E6%B5%8B</c> 这类）。如果再过一遍
    /// <see cref="UrlUtil"/> 的规范化，<c>Uri.ToString()</c> 会把百分号转义
    /// 重新解码成可读字符，长关键词还可能被截断 —— 搜索词就丢了。
    /// 所以这条路径要跳过规范化。
    /// </summary>
    public void OpenUrlRaw(string url) => OpenUrlCore(url, normalize: false);

    private void OpenUrlCore(string url, bool normalize)
    {
        string target = normalize ? (UrlUtil.Normalize(url) ?? url) : url;

        if (OpenUrlInterceptForTest != null)
        {
            OpenUrlInterceptForTest(target);
            return;
        }

        try
        {
            /* 不只是 Process.Start —— 那样浏览器会**在后台**打开
             * （前台锁定把新窗口压在任务栏），用户按下直达网址的键
             * 只看到任务栏闪一下。这里和"直达文件夹"走同一套置前逻辑。 */
            bool web = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                       || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            WindowService.LaunchInForeground(target,
                web ? WindowService.ShellTargetKind.Url : WindowService.ShellTargetKind.File);
        }
        catch (Exception ex) { Notify(UiText.Failed("打开网址", ex.Message)); }
    }

    /// <summary>
    /// 仅供自检：拦截"打开文件夹"，免得测试真的弹出资源管理器。
    /// 置为 null 时走真实的 Process.Start。
    /// </summary>
    public static Action<string>? OpenFolderInterceptForTest { get; set; }

    /// <summary>
    /// 在文件资源管理器里打开一个文件夹，并把它带到**最前面**。
    ///
    /// 为什么交给 shell（UseShellExecute）而不是 `explorer.exe "路径"`：
    ///   · 路径里有空格 / 中文 / 方括号时，命令行引号很容易拼错；
    ///   · 用 shell 打开目录是系统标准行为，OneDrive、库、快捷方式都能认；
    ///   · 顺带支持 `%USERPROFILE%`、`~`、`shell:Downloads` 这些写法。
    ///
    /// 为什么还要额外做"置前"：我们是从后台按下快捷键的，前台是别的程序，
    /// Windows 的前台锁定会把新开的资源管理器窗口压在任务栏 ——
    /// 用户看到的就是"文件夹在后台打开了"。具体做法见
    /// <see cref="Windows.WindowService.OpenFolderInForeground"/>。
    ///
    /// 路径不存在时给明确提示，而不是静默失败 ——
    /// 用户最怕的就是"按了没反应，也不知道为什么"。
    /// </summary>
    public void OpenFolder(string path)
    {
        string target = ExpandFolderPath(path);
        if (target.Length == 0) return;

        if (OpenFolderInterceptForTest != null)
        {
            OpenFolderInterceptForTest(target);
            return;
        }

        try
        {
            if (!Directory.Exists(target))
            {
                Notify(Loc.F("文件夹不存在：{0}", target));
                return;
            }
            CapsLockPlus.Core.Windows.WindowService.OpenFolderInForeground(target);
        }
        catch (Exception ex) { Notify(UiText.Failed("打开文件夹", ex.Message)); }
    }

    /// <summary>
    /// 仅供自检：拦截"打开文件"，免得测试真的拉起 Office / 播放器。
    /// 置为 null 时走真实的 Process.Start。
    /// </summary>
    public static Action<string>? OpenFileInterceptForTest { get; set; }

    /// <summary>
    /// 用系统默认程序打开一个**文件**（双击它时会发生什么，这里就做什么）。
    ///
    /// 用 shell 而不是手工查注册表：`.psd`、`.docx`、`.mp4` 该用什么程序打开
    /// 完全由系统关联决定，我们没必要也不应该自己判断。
    /// </summary>
    public void OpenFile(string path)
    {
        string target = ExpandPath(path);
        if (target.Length == 0) return;

        if (OpenFileInterceptForTest != null)
        {
            OpenFileInterceptForTest(target);
            return;
        }

        try
        {
            if (!File.Exists(target))
            {
                Notify(Loc.F("文件不存在：{0}", target));
                return;
            }

            /* 和网址/文件夹一样：打开之后把那个程序带到最前面。
             * 具体是谁打开由文件关联决定（.docx → Word、.mp4 → 播放器），
             * WindowService 会自己查关联再认窗口。 */
            WindowService.LaunchInForeground(target, WindowService.ShellTargetKind.File);
        }
        catch (Exception ex) { Notify(UiText.Failed("打开这个文件", ex.Message)); }
    }

    /// <summary>
    /// 把用户填的路径整理成可以直接交给 shell 的形式：
    /// 去引号 / `%ENV%` / `~` / `shell:` / 正斜杠归一 / 去尾部分隔符。
    /// 文件夹、文件、选区里来的路径都走这里，保证"同一个路径只有一种写法"。
    /// 抽成静态是为了让自检能直接验，不必真的打开资源管理器。
    /// </summary>
    public static string ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string p = path.Trim().Trim('"').Trim();
        if (p.Length == 0) return "";

        // shell: 开头的虚拟路径（shell:Downloads / shell:Startup…）原样交给系统
        if (p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return p;

        p = Environment.ExpandEnvironmentVariables(p);

        if (p == "~")
            p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (p.StartsWith("~/") || p.StartsWith("~\\"))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..]);

        // 统一分隔符，去掉末尾多余的 \（"C:\" 这种根目录要保留）
        p = p.Replace('/', '\\');
        if (p.Length > 3 && p.EndsWith('\\')) p = p.TrimEnd('\\');
        return p;
    }

    /// <summary>旧名字，保留给已有调用点；行为与 <see cref="ExpandPath"/> 一致。</summary>
    public static string ExpandFolderPath(string? path) => ExpandPath(path);

    /// <summary>
    /// 运行一个程序或命令。
    /// 注意不要用 "cmd /c start" 去绕——那样会闪出一个控制台窗口，
    /// 而且带空格路径的引号处理很容易出错。这里直接交给 shell 执行。
    /// </summary>
    public void RunProgram(string command)
    {
        try
        {
            string cmd = command.Trim();
            if (cmd.Length == 0) return;

            // 形如 "C:\path with space\x.exe" -arg 的写法：拆出程序和参数
            string exe = cmd;
            string args = "";
            if (cmd.StartsWith('"'))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0)
                {
                    exe = cmd[1..end];
                    args = cmd[(end + 1)..].Trim();
                }
            }
            else
            {
                int sp = cmd.IndexOf(' ');
                if (sp > 0 && !File.Exists(cmd))
                {
                    exe = cmd[..sp];
                    args = cmd[(sp + 1)..].Trim();
                }
            }

            var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
            if (args.Length > 0) psi.Arguments = args;
            Process.Start(psi);
        }
        catch (Exception ex) { Notify(UiText.Failed("运行这个程序", ex.Message)); }
    }

    public bool Screenshot()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
            return true;
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("SnippingTool.exe") { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Notify(UiText.Failed("启动截图工具", ex.Message));
                return false;
            }
        }
    }
}
