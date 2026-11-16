// ⚠️ 本文件是**源码**，不是缓存 —— 引擎图标缓存：内置 306 个图标 + 用户自选图标。
// .gitignore 里的 icons/ 曾经把这里一起忽略掉，2026-10-25 误删过一次，
// 后来从 v4.5.23 发布版反编译（ilspycmd）才找回来。因此原始注释全部丢失，
// 只剩代码本身；要改逻辑的话先对照发布版确认行为，补注释时按上面这句写。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CapsLockPlus.Core.Settings;
using static CapsLockPlus.Core.Interop.NativeMethods;

namespace CapsLockPlus.Core.Icons;

public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new object();

    private static readonly Dictionary<string, ImageSource?> _resourceCache = new Dictionary<string, ImageSource?>();

    private static readonly ConcurrentDictionary<string, ImageSource?> WebCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> WebPending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, List<Action<ImageSource?>>> WebWaiters = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient WebHttp = new() { Timeout = TimeSpan.FromSeconds(4) };

    /* 下载并发闸门。启动预热（WarmWebIcons）会一次排进几十个站，
     * 不限制的话每个站还有 4 个候选地址，短时间能发出上百个请求，
     * 既拖慢 Qbar 自己的图标，也容易被对方当成爬虫。 */
    private static readonly SemaphoreSlim WebDownloadGate = new(4, 4);

    /* favicon 失败后多久再试。之前"失败也写进 WebCache"只在进程内生效，
     * 重启后又把每个站重试一遍；落盘的 .miss 让"这个站没有图标"这件事
     * 也跨进程记住，但保留 TTL —— 站点以后补上 favicon 还能自动恢复。 */
    private static readonly TimeSpan WebMissTtl = TimeSpan.FromMinutes(30);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, nint[] phiconLarge, nint[] phiconSmall, uint nIcons);

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
            ImageSource? imageSource3 = FromExecutable(entry.Value);
            if (imageSource3 != null)
            {
                return imageSource3;
            }
        }
        return null;
    }

    /// <summary>
    /// 从 PATH / 开始菜单快捷方式解析程序图标。
    /// Qbar 的启动程序表里很多值只写了 notepad.exe、eudic.exe，
    /// 直接 File.Exists 找不到，但用户要的是“这个软件自己的图标”。
    /// </summary>
    public static ImageSource? FromExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string text = value.Trim().Trim('"');
        if (text.Contains("://", StringComparison.Ordinal)) return null;

        if (File.Exists(text)) return FromFile(text);

        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), text);
                if (File.Exists(candidate)) return FromFile(candidate);
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// 同步取**已经拿到过**的网站图标：内存 → 磁盘 favicon 缓存。
    ///
    /// 「常用网址」页要求点进去的第一帧就是真实图标，不能先画首字、
    /// 等一次 Dispatcher/网络回调再换图。列表项在构造时同步问这里，
    /// 有就直接用；没有才退回首字并异步补。
    /// </summary>
    public static ImageSource? GetWebIcon(string? url)
    {
        string host = HostOf(url);
        if (host.Length == 0) return null;

        if (WebCache.TryGetValue(host, out ImageSource? cached)) return cached;

        try
        {
            string path = WebIconPath(host);
            if (!File.Exists(path)) return null;
            ImageSource? image = FromImageFile(path);
            if (image == null) return null;
            WebCache[host] = image;
            return image;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "读取网页图标缓存");
            return null;
        }
    }

    /// <summary>
    /// 后台预热一批网址图标（启动时调用）。
    ///
    /// 目的只有一个：等用户点进「常用网址」时，磁盘缓存已经写好了，
    /// 第一帧就是图标。已经缓存过的站直接跳过，不产生任何网络请求。
    /// </summary>
    public static void WarmWebIcons(IEnumerable<string>? urls)
    {
        if (urls == null) return;
        foreach (string url in urls.Where(u => !string.IsNullOrWhiteSpace(u))
                                   .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string host = HostOf(url);
            if (host.Length == 0) continue;
            if (WebCache.ContainsKey(host)) continue;
            if (WebHasFreshMiss(host)) continue;
            try { LoadWebIconAsync(url, _ => { }); } catch { }
        }
    }

    /// <summary>
    /// 异步取网站 favicon。先试站点自己的 /favicon.ico，再试常用图标服务；
    /// 结果按 host 缓存（内存 + 磁盘），失败也只请求一次，不阻塞 Qbar 的查询线程。
    /// </summary>
    public static void LoadWebIconAsync(string? url, Action<ImageSource?> done)
    {
        string host = HostOf(url);
        if (host.Length == 0)
        {
            done(null);
            return;
        }

        if (WebCache.TryGetValue(host, out ImageSource? cached))
        {
            done(cached);
            return;
        }

        /* 磁盘缓存命中：直接回调，不切线程。这样"上次已经拿到过的图标"
         * 与列表数据在同一帧就位，不会出现先文字后刷图。 */
        ImageSource? onDisk = GetWebIcon(url);
        if (onDisk != null)
        {
            done(onDisk);
            return;
        }

        /* 最近刚失败过：TTL 内不再联网，也不假装有图标。 */
        if (WebHasFreshMiss(host))
        {
            done(null);
            return;
        }

        if (!WebPending.TryAdd(host, true))
        {
            /* 同一 host 已有请求在飞：把这次回调挂到等待队列。
             * 不挂的话，先建好的 chip 触发下载，随后建的结果行
             * 只会看到 Pending 然后永远停在首字图标。 */
            var list = WebWaiters.GetOrAdd(host, _ => new List<Action<ImageSource?>>());
            lock (list) list.Add(done);

            /* 竞态：请求可能刚好在这两行之间完成，并已经把等待表整份摘走。
             * 这时新挂进去的 list 没人会来 drain，回调永远不回来 ——
             * 图标停在首字，而且这个 host 还会一直被当成"已缓存"。
             * 把回调摘出来，自己补一次结果。 */
            if (WebPending.ContainsKey(host)) return;
            if (WebWaiters.TryRemove(host, out var orphan))
            {
                lock (orphan) orphan.Remove(done);
            }
            try { done(GetWebIcon(url)); } catch { }
            return;
        }

        Task.Run(async () =>
        {
            ImageSource? image = null;
            try
            {
                await WebDownloadGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    foreach (string candidate in WebIconUrls(host))
                    {
                        try
                        {
                            byte[] bytes = await WebHttp.GetByteArrayAsync(candidate).ConfigureAwait(false);
                            image = DecodeIcon(bytes);
                            if (image != null) break;
                        }
                        catch { }
                    }
                }
                finally
                {
                    WebDownloadGate.Release();
                }

                // 落盘要在写内存缓存之前：下次进页面（甚至重启后）同步读到的就是它。
                SaveWebIcon(host, image);

                /* ConcurrentDictionary 不收 null 值：没抓到就只留 .miss 标记。
                 * 早先这里无条件 WebCache[host] = image，没图标时会抛
                 * ArgumentNullException，后面的收尾全被跳过 —— 这个 host 就永远
                 * 停在"下载中"，等 .miss 过期后再也不会重试。 */
                if (image != null) WebCache[host] = image;
            }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "下载网页图标");
            }
            finally
            {
                WebPending.TryRemove(host, out _);
                if (WebWaiters.TryRemove(host, out var waiters))
                {
                    lock (waiters)
                    {
                        foreach (var waiter in waiters)
                        {
                            try { waiter(image); } catch { }
                        }
                    }
                }
            }

            try { done(image); } catch { }
        });
    }

    private static ImageSource? DecodeIcon(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 1024 * 1024) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.DecodePixelWidth = 64;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static string HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : "";
    }

    /// <summary>favicon 磁盘缓存目录。放在数据目录里，跟着软件一起搬走。</summary>
    private static string WebIconDir()
    {
        string dir = Path.Combine(AppPaths.DataDir, "icons", "web");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 一个 host 对应的缓存文件名：可读的 host + 短哈希。
    ///
    /// 只用 host 当文件名会被 Windows 的非法字符拦下（端口、IPv6 冒号等），
    /// 只留哈希又没法人工排查，所以两者各取一半。
    /// </summary>
    private static string WebIconPath(string host)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host)))
                             .ToLowerInvariant()[..10];
        var sb = new StringBuilder(host.Length);
        foreach (char c in host)
            sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_');
        string name = sb.ToString();
        if (name.Length > 48) name = name[^48..];
        return Path.Combine(WebIconDir(), name + "-" + hash + ".png");
    }

    /// <summary>最近一次抓取失败、且还在 TTL 内 → 这段时间别再联网。</summary>
    private static bool WebHasFreshMiss(string host)
    {
        try
        {
            string miss = WebIconPath(host) + ".miss";
            if (!File.Exists(miss)) return false;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < WebMissTtl) return true;
            File.Delete(miss);
            return false;
        }
        catch { return false; }
    }

    /// <summary>把抓到的图标原子落盘；失败写 .miss 标记。</summary>
    private static void SaveWebIcon(string host, ImageSource? image)
    {
        try
        {
            string path = WebIconPath(host);
            string miss = path + ".miss";
            if (image is not BitmapSource bmp)
            {
                File.WriteAllText(miss, DateTime.UtcNow.Ticks.ToString());
                return;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            string tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                encoder.Save(fs);
            File.Move(tmp, path, true);

            if (File.Exists(miss)) File.Delete(miss);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "写入网页图标缓存");
        }
    }

    private static IEnumerable<string> WebIconUrls(string host)
    {
        string bare = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        yield return "https://" + host + "/favicon.ico";
        if (bare != host) yield return "https://" + bare + "/favicon.ico";
        yield return "https://www.google.com/s2/favicons?sz=64&domain=" + Uri.EscapeDataString(host);
        yield return "https://icons.duckduckgo.com/ip3/" + Uri.EscapeDataString(host) + ".ico";
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
                imageSource = text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                              text.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? FromShellIcon(text)
                    : IsShellDocument(text) ? FromShellFile(text) : FromImageFile(text);
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

    private static bool IsShellDocument(string path)
    {
        return path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".appref-ms", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".msc", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 快捷方式/控制面板项没有可直接 ExtractIconEx 的图标资源，
    /// 交给 shell 解析，拿到的就是目标程序/设置页的图标。
    /// </summary>
    private static ImageSource? FromShellFile(string path)
    {
        var info = new SHFILEINFO();
        IntPtr hIcon = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
        if (hIcon == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            using Icon icon = Icon.FromHandle(info.hIcon);
            using Bitmap bitmap = icon.ToBitmap();
            using MemoryStream stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(info.hIcon);
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
        nint num = IntPtr.Zero;
        nint small = IntPtr.Zero;
        try
        {
            /* ExtractIconEx 一次会同时填**大图标和小图标**两个数组。
             * 早先只取了 array[0] 并只 DestroyIcon 它 ——
             * 小图标那一份句柄每个路径泄漏一个（缓存清理后还会再漏一次），
             * 长时间运行后 USER 句柄数一直涨。 */
            if (ExtractIconEx(path, 0, array, phiconSmall, 1u) == 0) return null;
            num = array[0];
            small = phiconSmall[0];
            if (num == IntPtr.Zero) return null;

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
            if (num != IntPtr.Zero) DestroyIcon(num);
            if (small != IntPtr.Zero && small != num) DestroyIcon(small);
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
