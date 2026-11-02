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
    };

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

    /// <summary>参数里有任意一个"必须干跑"的开关。</summary>
    public static bool NeedsDryRun(IEnumerable<string>? args) => AnyOf(args, DryRunFlags);

    /// <summary>参数里是否含指定开关（不区分大小写）。</summary>
    public static bool Has(IEnumerable<string>? args, string flag)
        => args != null && args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static bool AnyOf(IEnumerable<string>? args, string[] flags)
        => args != null && args.Any(a => flags.Contains(a, StringComparer.OrdinalIgnoreCase));
}
