using System;
using System.IO;

namespace CapsLockPlus.Core.Security;

/// <summary>
/// 把数据目录里"可能泄露用户配置"的文件统一转成密文。
///
/// 启动时跑一次（幂等：已经是密文的直接跳过）。覆盖：
///   · usage.json                         —— 用过哪些动作、每天用几次
///   · logs/*.log（app.log / hookhost.log / diagnose.log）—— 窗口标题、动作名、路径
///   · logs/*.txt（bindings.txt 等导出快照）—— 整份按键绑定
///   · crash-context-*.log                —— 崩溃时的日志尾巴
///   · migrated.txt                       —— 旧数据目录路径
///
/// 不动的文件：
///   · settings.json / vault.* —— 已经由 DataVault 加密；
///   · icons/*.svg             —— 程序自带图标，不含用户数据；
///   · running.flag            —— 只有一个时间戳。
/// </summary>
public static class LocalDataProtection
{
    public static int MigrateAll()
    {
        int converted = 0;
        string data = AppPaths.DataDir;

        /* 设置备份：老版本可能是明文 JSON，一并转密文（已有的 clpVault 密文会跳过）。 */
        string settingsBak = Path.Combine(data, "settings.json.bak");
        if (SecureFile.EncryptInPlace(settingsBak)) converted++;

        /* 崩溃时可能留下的明文临时文件：直接删，不留痕迹。 */
        foreach (string junk in new[] { "settings.json.tmp", "settings.json.broken" })
        {
            try
            {
                string path = Path.Combine(data, junk);
                if (File.Exists(path) && !SecureFile.IsEncryptedFile(path)) File.Delete(path);
            }
            catch { }
        }

        foreach (string name in new[] { "usage.json", "migrated.txt", "logs/bindings.txt", "logs/hooktest.txt" })
        {
            string path = Path.Combine(data, name.Replace('/', Path.DirectorySeparatorChar));
            if (SecureFile.EncryptInPlace(path)) converted++;
        }

        try
        {
            string logs = Path.Combine(data, "logs");
            if (Directory.Exists(logs))
            {
                foreach (string file in Directory.GetFiles(logs, "*.log"))
                    if (SecureFile.EncryptLogInPlace(file)) converted++;
                foreach (string file in Directory.GetFiles(logs, "*.txt"))
                    if (SecureFile.EncryptInPlace(file)) converted++;
            }

            foreach (string file in Directory.GetFiles(data, "crash-context-*.log"))
                if (SecureFile.EncryptInPlace(file)) converted++;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("加密本地数据文件时出错：" + ex.Message);
        }

        if (converted > 0) CoreLog.Info($"本地数据加密：已把 {converted} 个旧文件转成密文");
        return converted;
    }
}
