// ⚠️ 本文件是**源码**，不是缓存 —— 引擎图标缓存：内置 306 个图标 + 用户自选图标。
// .gitignore 里的 icons/ 曾经把这里一起忽略掉，2026-10-25 误删过一次，
// 后来从 v4.5.23 发布版反编译（ilspycmd）才找回来。因此原始注释全部丢失，
// 只剩代码本身；要改逻辑的话先对照发布版确认行为，补注释时按上面这句写。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CapsLockPlus.Core.Settings;

namespace CapsLockPlus.Core.Icons;

public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new object();

    private static readonly Dictionary<string, ImageSource?> _resourceCache = new Dictionary<string, ImageSource?>();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, nint[] phiconLarge, nint[] phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    public static ImageSource? ForEntry(QEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.EngineIcon))
        {
            ImageSource? imageSource = FromResource(entry.EngineIcon);
            if (imageSource != null)
            {
                return imageSource;
            }
        }
        if (!string.IsNullOrWhiteSpace(entry.IconPath))
        {
            ImageSource? imageSource2 = FromFile(entry.IconPath);
            if (imageSource2 != null)
            {
                return imageSource2;
            }
        }
        if (IsExecutable(entry.Value))
        {
            ImageSource? imageSource3 = FromFile(entry.Value);
            if (imageSource3 != null)
            {
                return imageSource3;
            }
        }
        return null;
    }

    public static ImageSource? FromResource(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        string text = name.Trim();
        if (_resourceCache.TryGetValue(text, out ImageSource? value))
        {
            return value;
        }
        ImageSource? imageSource = null;
        try
        {
            PngBitmapDecoder pngBitmapDecoder = new PngBitmapDecoder(new Uri("pack://application:,,,/Assets/engine-icons/" + text + ".png", UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (pngBitmapDecoder.Frames.Count > 0)
            {
                imageSource = pngBitmapDecoder.Frames[0];
            }
            if (imageSource != null)
            {
                ((Freezable)imageSource).Freeze();
            }
        }
        catch
        {
            imageSource = null;
        }
        _resourceCache[text] = imageSource;
        return imageSource;
    }

    private static bool IsExecutable(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        string text = value.Trim().Trim('"');
        if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !text.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return text.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    public static ImageSource? FromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        string text = path.Trim().Trim('"');
        lock (Gate)
        {
            if (Cache.TryGetValue(text, out ImageSource? value))
            {
                return value;
            }
            if (Cache.Count > 256)
            {
                Cache.Clear();
            }
        }
        ImageSource? imageSource = null;
        try
        {
            if (File.Exists(text))
            {
                imageSource = ((text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || text.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ? FromShellIcon(text) : FromImageFile(text));
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("读取图标失败（" + text + "）：" + ex.Message);
        }
        lock (Gate)
        {
            Cache[text] = imageSource;
            return imageSource;
        }
    }

    private static ImageSource? FromImageFile(string path)
    {
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return FromSvg(path);
        }
        using MemoryStream streamSource = new MemoryStream(File.ReadAllBytes(path));
        BitmapImage bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.StreamSource = streamSource;
        bitmapImage.DecodePixelWidth = 64;
        bitmapImage.EndInit();
        ((Freezable)bitmapImage).Freeze();
        return bitmapImage;
    }

    private static ImageSource? FromSvg(string path)
    {
        try
        {
            ImageSource? imageSource = SvgRenderer.Render(path);
            if (imageSource == null)
            {
                return null;
            }
            ((Freezable)imageSource).Freeze();
            return imageSource;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("渲染 SVG 失败（" + path + "）：" + ex.Message);
            return null;
        }
    }

    private static ImageSource? FromShellIcon(string path)
    {
        nint[] array = new nint[1];
        nint[] phiconSmall = new nint[1];
        nint num = ((ExtractIconEx(path, 0, array, phiconSmall, 1u) != 0) ? array[0] : IntPtr.Zero);
        if (num == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            using Icon icon = Icon.FromHandle(num);
            using Bitmap bitmap = icon.ToBitmap();
            using MemoryStream memoryStream = new MemoryStream();
            bitmap.Save(memoryStream, ImageFormat.Png);
            memoryStream.Position = 0L;
            BitmapImage bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.StreamSource = memoryStream;
            bitmapImage.EndInit();
            ((Freezable)bitmapImage).Freeze();
            return bitmapImage;
        }
        finally
        {
            DestroyIcon(num);
        }
    }

    public static System.Windows.Media.Color ColorFor(string text)
    {
        string[] array = new string[10] { "#2B6CF6", "#6C4CF6", "#22C1E8", "#1F9D62", "#D9822B", "#E5484D", "#0EA5A5", "#8B5CF6", "#DB2777", "#475569" };
        int num = 0;
        foreach (char c in text)
        {
            num = (num * 31 + c) & 0x7FFFFFFF;
        }
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(array[num % array.Length]);
    }

    public static string Initial(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "?";
        }
        text = text.Trim();
        return char.ToUpperInvariant(text[0]).ToString();
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Cache.Clear();
        }
    }
}
