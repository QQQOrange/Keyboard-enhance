namespace CapsLockPlus.Core.Security;

/// <summary>
/// 造 <see cref="System.Security.Cryptography.AesGcm"/> 的统一入口。
///
/// ## 为什么要包一层
///
/// .NET 8 起 `new AesGcm(key)` 被标记为过时（SYSLIB0053）：旧重载**默认按 16 字节
/// 认证标签**处理，一旦以后默认值变化，老的加密数据就可能读不回来（或者反过来
/// 把新数据写成老格式）。官方要求显式写出标签长度。
///
/// 但本项目是双目标：`net10.0-windows` 与 `net7.0-windows`（Win7 那条线），
/// 后者**没有**带标签长度的构造函数 —— 直接改会在 Win7 线上编译失败。
/// 所以这里用条件编译收口，业务代码只管调 <see cref="Create"/>，
/// 两个目标框架的行为完全一致（都按 16 字节标签，正是现有数据用的格式）。
/// </summary>
internal static class AesGcmFactory
{
    /// <summary>认证标签长度（字节）。现有加密数据全部按 16 字节标签写入，不能改。</summary>
    internal const int TagSizeBytes = 16;

    /// <summary>用给定的 256 位密钥造一个 AesGcm 实例（调用方负责 Dispose）。</summary>
    internal static System.Security.Cryptography.AesGcm Create(ReadOnlySpan<byte> key)
    {
#if NET8_0_OR_GREATER
        return new System.Security.Cryptography.AesGcm(key, TagSizeBytes);
#else
        /* net7.0：只有不带标签长度的重载，它的默认值就是 16；
         * 上面那个常量在这里用不上，但保持同一份数字便于对照。 */
        return new System.Security.Cryptography.AesGcm(key);
#endif
    }
}
