namespace CapsLockPlus.Core;

/// <summary>
/// 命令行开关的集中定义。
///
/// 为什么要有这个类：同一批"诊断 / 测试"开关原先在三个地方各写一遍
/// （App 的干跑判定、App 的单实例判定、StartupPassword 的免密码门判定），
/// 三份清单迟早会不一致 —— 实际已经出过一次：
/// `--render` 在"免密码门"清单里、却不在"免单实例"清单里，
/// 于是主程序开着的时候跑 `--render` 会被单实例判掉，**一张图都不出**，
/// 退出码还是 0，看起来像"渲染器坏了"。
/// </summary>
public static class CliFlags
{
    /// <summary>
    /// 诊断 / 测试类开关：跑完即退，不装常驻托盘，也不代表用户想再开一个实例。
    ///
    /// 这些模式：
    ///   · 不弹启动密码（弹了会卡死在无人值守的脚本里）；
    ///   · 不参与单实例互斥（否则主程序一开着，诊断命令就静默失效）。
    /// </summary>
    private static readonly string[] DiagnosticFlags =
    {
        "--selftest", "--render", "--render-diag", "--osd", "--diag",
        "--hwtest", "--webdav-test", "--svg2png", "--reveal", "--pick-test",
        "--help", "--hooktest", "--dump-triggers",
    };

    /// <summary>
    /// `--help` 打印的用法说明。
    ///
    /// 为什么要有：诊断开关已经十来个（`--pick-test`、`--osd-hold` 这类
    /// 连 README 里都没写），用户报问题时我们常说"跑一下 --selftest"，
    /// 但他不知道该跑哪个、参数是什么。用法表跟着代码走，还能被自检盯住
    /// （见 SelfTest：每个诊断开关都必须出现在这张表里）。
    /// </summary>
    public const string Usage =
        "按键增强 Keyboard Enhance —— 命令行用法\n" +
        "\n" +
        "  不带参数                        正常启动（驻留托盘）\n" +
        "  --help                          显示这份说明后退出\n" +
        "\n" +
        "  自检与诊断（跑完即退，不装托盘、不弹启动密码）\n" +
        "  --selftest                      跑全部自检；退出码 0 = 全过（日志在数据目录 selftest.log）\n" +
         "  --diag                          环境诊断（钩子、前台会话、注入链路）；发现问题时退出码非 0\n" +
         "  --hooktest                      只装键盘钩子并自己敲两下，检查钩子是否真的能收到按键\n" +
         "  --dump-triggers                 打印解密后的触发键/层配置（排查触发键不生效时用）\n" +
         "  --capture-test                 打开自定义按键捕获窗并自检三路输入来源\n" +
        "  --hwtest                        硬件测试：真的调一次音量与亮度（会出声/变亮）\n" +
        "  --pick-test <结果文件>           验证「选中文字直接搜索」的取词链路，结果写进文件\n" +
        "  --webdav-test <地址> [用户名] [密码]   实测一次 WebDAV 往返\n" +
        "  --svg2png <源目录> <目标目录>     把 SVG 图标批量转成 PNG（维护图标表时用）\n" +
        "\n" +
        "  界面与出图\n" +
        "  --render <目录>                 把每个页面/浮层渲染成 PNG（用于文档与回归）\n" +
        "  --render-diag <目录>            同上，并输出布局度量与未翻译文案审计\n" +
        "  --osd [百分比]                   显示一次亮度横条（默认 50），配合 --osd-hold 控制停留\n" +
        "  --osd-hold <毫秒>               横条停留时长（默认 1900）\n" +
        "  --qbar                          直接打开 Qbar\n" +
        "  --reveal <路径>                  用资源管理器打开并选中该路径\n" +
        "  --tray                          启动但不显示主窗口（只驻留托盘）\n" +
        "  --trace                         启动并记录每个按键（写进 logs/app.log）\n" +
        "  --updated                       上一次是自动更新拉起的（用于提示「已更新到 x.y.z」）\n" +
        "\n" +
        "数据目录：默认在程序旁边的 KeyboardEnhance Data，可用环境变量 CAPSLOCKPLUS_DATA 改。\n";

    /// <summary>
    /// 这些模式会经过"输入文本 / 播放宏 / 复制"等真实动作，
    /// 必须切成干跑 —— 否则等于往用户当时的前台窗口里打字。
    ///
    /// 注意**不含** `--hwtest`：它就是要真的调一次音量/亮度。
    /// </summary>
    private static readonly string[] DryRunFlags =
    {
        "--selftest", "--render", "--render-diag", "--diag", "--webdav-test",
    };

    /// <summary>参数里有任意一个诊断开关。</summary>
    public static bool IsDiagnostic(IEnumerable<string>? args) => AnyOf(args, DiagnosticFlags);

    /// <summary>仅供自检：诊断开关清单（用来盯住"用法说明漏了某个开关"）。</summary>
    internal static IReadOnlyList<string> DiagnosticFlagsForTest => DiagnosticFlags;

    /// <summary>仅供自检：干跑开关清单。</summary>
    internal static IReadOnlyList<string> DryRunFlagsForTest => DryRunFlags;

    /// <summary>参数里有任意一个"必须干跑"的开关。</summary>
    public static bool NeedsDryRun(IEnumerable<string>? args) => AnyOf(args, DryRunFlags);

    /// <summary>参数里是否含指定开关（不区分大小写）。</summary>
    public static bool Has(IEnumerable<string>? args, string flag)
        => args != null && args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static bool AnyOf(IEnumerable<string>? args, string[] flags)
        => args != null && args.Any(a => flags.Contains(a, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 打印用法。
    ///
    /// 本程序是 WinExe（没有自己的控制台），`Console.WriteLine` 默认**什么也看不到** ——
    /// 从 cmd 里跑 `--help` 的人会以为命令没执行。所以先 AttachConsole 到父进程的控制台，
    /// 实在没有（双击运行）就把用法写进数据目录的 usage.txt，并在日志里留一行路径。
    /// </summary>
    public static void PrintUsage()
    {
        try { AttachConsole(-1); } catch { /* 没有父控制台：正常情况（双击启动） */ }

        try { Console.WriteLine(Usage); } catch { }

        try
        {
            string path = System.IO.Path.Combine(AppPaths.DataDir, "usage.txt");
            System.IO.File.WriteAllText(path, Usage, System.Text.Encoding.UTF8);
            CoreLog.Info("用法已写入：" + path);
        }
        catch (Exception e)
        {
            CoreLog.Warn("写 usage.txt 失败：" + e.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
