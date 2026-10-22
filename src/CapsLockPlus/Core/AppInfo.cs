namespace CapsLockPlus.Core;

/// <summary>产品标识：名称、版本、链接等信息集中在这里，避免各处硬编码。</summary>
public static class AppInfo
{
    /// <summary>产品全名（窗口标题、托盘提示、关于页共用）。</summary>
    public const string ProductName = "按键增强";

    /// <summary>英文名（关于页、发布说明用）。</summary>
    public const string EnglishName = "Keyboard Enhance";

    /// <summary>简短名称（空间紧张处使用，例如托盘气泡标题）。</summary>
    public const string ShortName = "按键增强";

    /// <summary>出品方标识。</summary>
    public const string Author = "QQQOrange";

    /// <summary>
    /// 主程序的可执行文件名。
    ///
    /// 为什么带 <c>.App</c>：同目录的 <c>KeyboardEnhance.exe</c> 是**启动器**
    /// （net48 编译，负责检查 .NET 10 运行时、缺了就提示并打开下载页）。
    /// 用户双击的永远是启动器；主程序是它拉起来的那个。
    /// </summary>
    public const string ExeName = "KeyboardEnhance.App.exe";

    /// <summary>启动器文件名（用户双击的那个）。</summary>
    public const string LauncherName = "KeyboardEnhance.exe";

    /// <summary>完整标题：名称 + 英文名，用于关于页首行。</summary>
    public const string TitleWithEnglish = ProductName + " · " + EnglishName;
}
