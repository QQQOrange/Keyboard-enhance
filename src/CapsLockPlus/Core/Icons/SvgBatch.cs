// ⚠️ 本文件是**源码**，不是缓存 —— 把 Assets/engine-icons 里的 svg 批量转成 png 的命令行入口。
// .gitignore 里的 icons/ 曾经把这里一起忽略掉，2026-10-25 误删过一次，
// 后来从 v4.5.23 发布版反编译（ilspycmd）才找回来。因此原始注释全部丢失，
// 只剩代码本身；要改逻辑的话先对照发布版确认行为，补注释时按上面这句写。

using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace CapsLockPlus.Core.Icons;

public static class SvgBatch
{
    public static int Convert(string srcDir, string dstDir, int size = 64)
    {
        if (!Directory.Exists(srcDir))
        {
            CoreLog.Error("源目录不存在：" + srcDir);
            return 2;
        }
        Directory.CreateDirectory(dstDir);
        int num = 0;
        int num2 = 0;
        foreach (string item in Directory.EnumerateFiles(srcDir, "*.svg"))
        {
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(item);
            try
            {
                if (!(SvgRenderer.Render(item, size) is BitmapSource source))
                {
                    num2++;
                    CoreLog.Warn("渲染返回非位图：" + fileNameWithoutExtension);
                    continue;
                }
                string path = Path.Combine(dstDir, fileNameWithoutExtension + ".png");
                PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder
                {
                    Frames = { BitmapFrame.Create(source) }
                };
                using (FileStream stream = File.Create(path))
                {
                    pngBitmapEncoder.Save(stream);
                }
                num++;
            }
            catch (Exception ex)
            {
                num2++;
                CoreLog.Warn("渲染 SVG 失败（" + fileNameWithoutExtension + "）：" + ex.Message);
            }
        }
        CoreLog.Info($"SVG 批量转换完成：成功 {num}，失败 {num2}，输出 {dstDir}");
        return (num2 != 0) ? 1 : 0;
    }
}
