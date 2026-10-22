namespace CapsLockPlus.Core.Settings;

/// <summary>
/// 内置图标库：常用的搜索/网站图标以 SVG 形式随程序分发（体积只有几百字节），
/// 首次使用时由 IconCache 渲染成位图并缓存，不依赖网络。
///
/// 用户也可以在 Qbar 设置里给自己的条目指定任意 png/ico/exe，
/// 有图标就显示图标，没有就显示"名字首字 + 品牌色"的圆形标签。
/// </summary>
public static class IconUrls
{
    /// <summary>把内置 SVG 写成文件并返回路径（首次调用时生成，之后直接复用）。</summary>
    public static string Materialize(string name, string svg)
    {
        try
        {
            string dir = Path.Combine(AppPaths.DataDir, "icons");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".svg");
            if (!File.Exists(path)) File.WriteAllText(path, svg);
            return path;
        }
        catch
        {
            return "";
        }
    }

    // ---- 各品牌图标（简化几何，够小、够清晰）----

    public static readonly string Baidu = Materialize("baidu", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#2932E1"/>
          <circle cx="17.4" cy="17" r="3.4" fill="#fff"/>
          <circle cx="30.6" cy="17" r="3.4" fill="#fff"/>
          <circle cx="24" cy="25.5" r="4" fill="#fff"/>
          <circle cx="14" cy="26.5" r="2.5" fill="#fff" opacity="0.9"/>
          <circle cx="34" cy="26.5" r="2.5" fill="#fff" opacity="0.9"/>
          <path d="M16 32.5 Q24 37.6 32 32.5" stroke="#fff" stroke-width="2.4"
                stroke-linecap="round" fill="none"/>
        </svg>
        """);

    public static readonly string Google = Materialize("google", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#fff"/>
          <path d="M38.5 24.3c0-.9-.1-1.7-.2-2.5H24v4.8h8.2c-.4 2-1.5 3.6-3.1 4.7v3.9h5c2.9-2.7 4.4-6.6 4.4-10.9z" fill="#4285F4"/>
          <path d="M24 39c3.9 0 7.2-1.3 9.6-3.5l-5-3.9c-1.4.9-3.2 1.5-4.6 1.5-3.7 0-6.8-2.5-7.9-5.9h-5.2v4.1C13.3 35.8 18.2 39 24 39z" fill="#34A853"/>
          <path d="M16.1 27.2c-.3-.8-.4-1.7-.4-2.6s.2-1.8.4-2.6v-4.1h-5.2C10.3 20 10 22 10 24s.3 4 .9 6.1l5.2-4.1z" fill="#FBBC05"/>
          <path d="M24 15.4c2.1 0 4 .7 5.5 2.1l4.4-4.4C31.2 10.8 27.9 9 24 9c-5.8 0-10.7 3.2-13.1 8.1l5.2 4.1c1.1-3.4 4.2-5.8 7.9-5.8z" fill="#EA4335"/>
        </svg>
        """);

    public static readonly string Bing = Materialize("bing", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <rect width="48" height="48" rx="24" fill="#008373"/>
          <path d="M17 11l7 2.6v16.8l7.4-4.3 5.6 3.2-13 7.7-7-3.9V11z" fill="#fff"/>
        </svg>
        """);

    public static readonly string Taobao = Materialize("taobao", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#FF5000"/>
          <path d="M14 20h20v3H14z" fill="#fff"/>
          <path d="M18 26c0 5 3 8 7 8 3 0 5-1.4 6.4-3.6l-2.7-1.6c-.9 1.4-2.1 2.2-3.7 2.2-2.2 0-3.9-1.9-3.9-4.9V26H18z" fill="#fff"/>
          <circle cx="24" cy="16" r="3.4" fill="#fff"/>
        </svg>
        """);

    public static readonly string Wikipedia = Materialize("wikipedia", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#F8F9FA"/>
          <circle cx="24" cy="24" r="19" fill="#fff" stroke="#54595D" stroke-width="1.5"/>
          <path d="M13 18h3.6l3.2 9.6 3-9.6h3.2l3 9.6 3.2-9.6H35l-4.6 13h-3.2l-3-9.4-3 9.4h-3.2L13 18z"
                fill="#111" transform="translate(0,1.5)"/>
        </svg>
        """);

    public static readonly string GitHub = Materialize("github", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#181717"/>
          <path d="M24 10c-7.7 0-14 6.3-14 14 0 6.2 4 11.4 9.6 13.3.7.1 1-.3 1-.7v-2.6c-3.9.8-4.7-1.7-4.7-1.7-.6-1.6-1.6-2-1.6-2-1.3-.9.1-.9.1-.9 1.4.1 2.2 1.5 2.2 1.5 1.3 2.2 3.4 1.6 4.2 1.2.1-.9.5-1.6.9-2-3.1-.4-6.4-1.6-6.4-7 0-1.5.5-2.8 1.4-3.8-.1-.4-.6-1.8.1-3.7 0 0 1.2-.4 3.8 1.4 1.1-.3 2.3-.5 3.5-.5s2.4.2 3.5.5c2.7-1.8 3.8-1.4 3.8-1.4.7 1.9.3 3.3.1 3.7.9 1 1.4 2.3 1.4 3.8 0 5.5-3.3 6.6-6.4 7 .5.4 1 1.3 1 2.6v3.9c0 .4.3.8 1 .7C34 35.4 38 30.2 38 24c0-7.7-6.3-14-14-14z" fill="#fff"/>
        </svg>
        """);

    public static readonly string Mdn = Materialize("mdn", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <rect width="48" height="48" rx="24" fill="#1B1B1B"/>
          <text x="24" y="29" font-family="Segoe UI, Arial" font-size="13" font-weight="700"
                fill="#fff" text-anchor="middle">MDN</text>
        </svg>
        """);

    public static readonly string Zhihu = Materialize("zhihu", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#0084FF"/>
          <text x="24" y="30" font-family="Segoe UI, Arial" font-size="15" font-weight="700"
                fill="#fff" text-anchor="middle">知</text>
        </svg>
        """);

    // ---------------- 国内引擎 ----------------

    public static readonly string Bilibili = Materialize("bilibili", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <rect width="48" height="48" rx="24" fill="#FB7299"/>
          <path d="M17 13l4 4h6l4-4 2 2-2 2h1a4 4 0 0 1 4 4v10a4 4 0 0 1-4 4H16a4 4 0 0 1-4-4V21a4 4 0 0 1 4-4h1l-2-2 2-2z"
                fill="none" stroke="#fff" stroke-width="2.4" stroke-linejoin="round"/>
          <path d="M19 24v5M29 24v5" stroke="#fff" stroke-width="2.4" stroke-linecap="round"/>
        </svg>
        """);

    public static readonly string Weixin = Materialize("weixin", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#07C160"/>
          <ellipse cx="19" cy="21" rx="9" ry="7.5" fill="#fff"/>
          <ellipse cx="30" cy="27" rx="7.5" ry="6" fill="#fff"/>
          <circle cx="16" cy="20" r="1.4" fill="#07C160"/>
          <circle cx="22" cy="20" r="1.4" fill="#07C160"/>
          <circle cx="27.5" cy="26" r="1.2" fill="#07C160"/>
          <circle cx="32.5" cy="26" r="1.2" fill="#07C160"/>
        </svg>
        """);

    public static readonly string Weibo = Materialize("weibo", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#E6162D"/>
          <ellipse cx="22" cy="27" rx="11" ry="8" fill="#fff"/>
          <ellipse cx="22" cy="27" rx="6" ry="4.5" fill="#E6162D"/>
          <path d="M30 14c4 0 7 3 7 7" stroke="#fff" stroke-width="2.2" fill="none" stroke-linecap="round"/>
          <path d="M30 19c1.8 0 3 1.3 3 3" stroke="#fff" stroke-width="2" fill="none" stroke-linecap="round"/>
        </svg>
        """);

    public static readonly string Jd = Materialize("jd", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#E1251B"/>
          <text x="24" y="30" font-family="Segoe UI, Arial" font-size="15" font-weight="700"
                fill="#fff" text-anchor="middle">JD</text>
        </svg>
        """);

    public static readonly string Xiaohongshu = Materialize("xiaohongshu", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#FF2442"/>
          <text x="24" y="30" font-family="Segoe UI, Arial" font-size="14" font-weight="700"
                fill="#fff" text-anchor="middle">小红书</text>
        </svg>
        """);

    public static readonly string Douyin = Materialize("douyin", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <rect width="48" height="48" rx="24" fill="#000"/>
          <path d="M28 12h4c.4 3.4 2.6 5.6 6 6v4c-2.4-.2-4.5-1-6-2.4V28a8 8 0 1 1-8-8c.7 0 1.4.1 2 .3v4.2A4 4 0 1 0 28 28V12z" fill="#fff"/>
          <path d="M28 12h4c.4 3.4 2.6 5.6 6 6v4c-2.4-.2-4.5-1-6-2.4V28a8 8 0 1 1-8-8c.7 0 1.4.1 2 .3v4.2A4 4 0 1 0 28 28V12z"
                fill="#25F4EE" opacity=".55" transform="translate(-1.6,-1.2)"/>
        </svg>
        """);

    public static readonly string Douban = Materialize("douban", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#2E963A"/>
          <text x="24" y="30" font-family="Segoe UI, Arial" font-size="14" font-weight="700"
                fill="#fff" text-anchor="middle">豆瓣</text>
        </svg>
        """);

    public static readonly string Csdn = Materialize("csdn", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#FC5531"/>
          <text x="24" y="29" font-family="Segoe UI, Arial" font-size="12" font-weight="700"
                fill="#fff" text-anchor="middle">CSDN</text>
        </svg>
        """);

    public static readonly string Juejin = Materialize("juejin", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#1E80FF"/>
          <path d="M24 14l12 8-12 8-12-8 12-8z" fill="#fff"/>
          <path d="M17 26l7 5 7-5 5 3.5-12 8-12-8L17 26z" fill="#fff" opacity=".75"/>
        </svg>
        """);

    public static readonly string Gitee = Materialize("gitee", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#C71D23"/>
          <path d="M32 18H19a4 4 0 0 0-4 4v1h13v4H15v-5a7 7 0 0 1 7-7h10v3z" fill="#fff"/>
          <path d="M24 25h9v4a7 7 0 0 1-7 7h-4v-3h4a4 4 0 0 0 4-4h-6v-4z" fill="#fff"/>
        </svg>
        """);

    public static readonly string Pinduoduo = Materialize("pinduoduo", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#E22E1F"/>
          <text x="24" y="29" font-family="Segoe UI, Arial" font-size="12" font-weight="700"
                fill="#fff" text-anchor="middle">拼多多</text>
        </svg>
        """);

    public static readonly string TencentVideo = Materialize("tencentvideo", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#0068FF"/>
          <path d="M20 17l14 7-14 7V17z" fill="#fff"/>
        </svg>
        """);

    public static readonly string Iqiyi = Materialize("iqiyi", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#00BE06"/>
          <text x="24" y="29" font-family="Segoe UI, Arial" font-size="12" font-weight="700"
                fill="#fff" text-anchor="middle">爱奇艺</text>
        </svg>
        """);

    // ---------------- 国外引擎 ----------------

    public static readonly string Youtube = Materialize("youtube", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#FF0000"/>
          <rect x="12" y="17" width="24" height="15" rx="4" fill="#fff"/>
          <path d="M21.5 21.5l7 3-7 3v-6z" fill="#FF0000"/>
        </svg>
        """);

    public static readonly string Reddit = Materialize("reddit", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#FF4500"/>
          <circle cx="24" cy="26" r="9" fill="#fff"/>
          <circle cx="20.5" cy="25" r="1.5" fill="#FF4500"/>
          <circle cx="27.5" cy="25" r="1.5" fill="#FF4500"/>
          <path d="M19.5 30c2.6 1.8 6.4 1.8 9 0" stroke="#FF4500" stroke-width="1.6" fill="none" stroke-linecap="round"/>
          <circle cx="31" cy="15" r="2.4" fill="#fff"/>
        </svg>
        """);

    public static readonly string StackOverflow = Materialize("stackoverflow", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#F48024"/>
          <path d="M18 31h12v3H18v-3z" fill="#fff"/>
          <path d="M19 26.5l10.5 2.2.6-3L19.6 23.5l-.6 3z" fill="#fff"/>
          <path d="M21 21l9.5 4.5 1.4-2.8L22.4 18 21 21z" fill="#fff"/>
          <path d="M25 16.5l7 7 2.2-2.2-7-7-2.2 2.2z" fill="#fff"/>
        </svg>
        """);

    public static readonly string Amazon = Materialize("amazon", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#FF9900"/>
          <text x="24" y="27" font-family="Segoe UI, Arial" font-size="11" font-weight="700"
                fill="#111" text-anchor="middle">amazon</text>
          <path d="M15 31c5 2.6 13 2.6 18 0" stroke="#111" stroke-width="2" fill="none" stroke-linecap="round"/>
        </svg>
        """);

    public static readonly string Twitter = Materialize("twitter", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#000"/>
          <path d="M16 15h5.5l5 6.6L32 15h3l-7 9 7.5 10h-5.5l-5.4-7.2L18 34h-3l7.4-9.5L16 15z" fill="#fff"/>
        </svg>
        """);

    public static readonly string Facebook = Materialize("facebook", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#1877F2"/>
          <path d="M27 26h4l.7-4.5H27V19c0-1.3.4-2.2 2.3-2.2H32v-4c-.4-.1-1.9-.2-3.5-.2-3.4 0-5.8 2-5.8 5.8v3.4H19V26h3.7v10h4.3V26z" fill="#fff"/>
        </svg>
        """);

    public static readonly string Npm = Materialize("npm", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <rect width="48" height="48" rx="24" fill="#CB3837"/>
          <path d="M13 16h22v16H24v-4h-6v4h-5V16zm4 4v8h2v-8h-2zm6 0v8h3v-8h-3z" fill="#fff"
                transform="translate(0,0)"/>
        </svg>
        """);

    public static readonly string Nuget = Materialize("nuget", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#004880"/>
          <circle cx="17" cy="20" r="3" fill="#fff"/>
          <rect x="21" y="17" width="12" height="12" rx="2.5" fill="#fff"/>
        </svg>
        """);

    // ---------------- 第二批补充引擎 ----------------
    //
    // 说明：这些是"品牌色圆底 + 几何标识"的简化图标，随程序内置、离线可用。
    // 之所以不直接抓官网 logo，是因为它们是各公司商标，重绘成简化几何既避免了
    // 直接复制商标素材，也能在同一套视觉规则下保持整齐。

    public static readonly string Yahoo = Materialize("yahoo", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#6001D2"/>
          <path d="M13 15h5l5 8 5-8h5l-7.5 12v7h-5v-7L13 15z" fill="#fff"/>
        </svg>
        """);

    public static readonly string DuckDuckGo = Materialize("duckduckgo", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#DE5833"/>
          <circle cx="24" cy="24" r="12" fill="#fff"/>
          <circle cx="24" cy="24" r="8" fill="none" stroke="#DE5833" stroke-width="2"/>
          <path d="M24 16v3M24 29v3M16 24h3M29 24h3" stroke="#DE5833" stroke-width="2"
                stroke-linecap="round"/>
        </svg>
        """);

    public static readonly string WolframAlpha = Materialize("wolframalpha", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#DD1100"/>
          <path d="M24 13l14 22H10l14-22z" fill="none" stroke="#fff" stroke-width="2.6"
                stroke-linejoin="round"/>
          <path d="M24 21v8" stroke="#fff" stroke-width="2.6" stroke-linecap="round"/>
        </svg>
        """);

    public static readonly string GoogleImages = Materialize("googleimages", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#4285F4"/>
          <rect x="13" y="15" width="22" height="18" rx="3" fill="#fff"/>
          <circle cx="19" cy="21" r="2.4" fill="#4285F4"/>
          <path d="M15 31l6-7 4 4.5 3-3 5 5.5H15z" fill="#4285F4"/>
        </svg>
        """);

    public static readonly string GoogleMaps = Materialize("googlemaps", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#34A853"/>
          <path d="M24 12c-4.4 0-8 3.5-8 7.9 0 5.9 8 16.1 8 16.1s8-10.2 8-16.1C32 15.5 28.4 12 24 12z"
                fill="#fff"/>
          <circle cx="24" cy="20" r="3.4" fill="#34A853"/>
        </svg>
        """);

    public static readonly string Netflix = Materialize("netflix", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#E50914"/>
          <path d="M18 14h5l7 13V14h4v20h-5l-7-13v13h-4V14z" fill="#fff"/>
        </svg>
        """);

    public static readonly string Gmail = Materialize("gmail", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#EA4335"/>
          <rect x="12" y="17" width="24" height="15" rx="2" fill="#fff"/>
          <path d="M12 19l12 8 12-8" stroke="#EA4335" stroke-width="2.4" fill="none"
                stroke-linejoin="round"/>
        </svg>
        """);

    public static readonly string GoogleDrive = Materialize("googledrive", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#0F9D58"/>
          <path d="M19 14h10l9 16H20l-1-16z" fill="#fff" opacity=".95"/>
          <path d="M14 30l5-16 9 16H14z" fill="#FFC107" opacity=".9"/>
        </svg>
        """);

    public static readonly string YoutubeMusic = Materialize("youtubemusic", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#FF0000"/>
          <circle cx="24" cy="24" r="11" fill="#fff"/>
          <path d="M21.5 19.5l8 4.5-8 4.5v-9z" fill="#FF0000"/>
        </svg>
        """);

    public static readonly string Gist = Materialize("gist", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#24292F"/>
          <rect x="14" y="15" width="20" height="18" rx="2" fill="none" stroke="#fff"
                stroke-width="2.2"/>
          <path d="M18 21h8M18 25h12M18 29h6" stroke="#fff" stroke-width="2" stroke-linecap="round"/>
        </svg>
        """);

    public static readonly string Sogou = Materialize("sogou", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#FF6900"/>
          <text x="24" y="30" font-family="Segoe UI, Arial" font-size="15" font-weight="700"
                fill="#fff" text-anchor="middle">S</text>
        </svg>
        """);

    public static readonly string Ecosia = Materialize("ecosia", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#0F7B4F"/>
          <path d="M24 12c6 4 9 9 9 14a9 9 0 1 1-18 0c0-5 3-10 9-14z" fill="#fff"/>
          <path d="M24 20v14" stroke="#0F7B4F" stroke-width="2.2" stroke-linecap="round"/>
        </svg>
        """);

    public static readonly string Brave = Materialize("brave", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#FB542B"/>
          <path d="M24 13l9 5v9l-9 8-9-8v-9l9-5z" fill="none" stroke="#fff" stroke-width="2.6"
                stroke-linejoin="round"/>
        </svg>
        """);

    public static readonly string Spotify = Materialize("spotify", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#1DB954"/>
          <path d="M16 20c6-2 13-1 17 2" stroke="#fff" stroke-width="2.6" fill="none"
                stroke-linecap="round"/>
          <path d="M17 25c5-1.6 11-.8 14 1.6" stroke="#fff" stroke-width="2.3" fill="none"
                stroke-linecap="round"/>
          <path d="M18 30c4-1.2 8-.6 11 1.2" stroke="#fff" stroke-width="2" fill="none"
                stroke-linecap="round"/>
        </svg>
        """);

    public static readonly string Dictionary = Materialize("dictionary", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#8B5E3C"/>
          <path d="M15 14h13a4 4 0 0 1 4 4v16H19a4 4 0 0 1-4-4V14z" fill="#fff"/>
          <path d="M19 20h9M19 24h7M19 28h5" stroke="#8B5E3C" stroke-width="2"
                stroke-linecap="round"/>
        </svg>
        """);

    public static readonly string ChatGpt = Materialize("chatgpt", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#10A37F"/>
          <circle cx="24" cy="24" r="9" fill="none" stroke="#fff" stroke-width="2.4"/>
          <path d="M24 15v6M24 27v6M15 24h6M27 24h6" stroke="#fff" stroke-width="2.4"
                stroke-linecap="round"/>
        </svg>
        """);
}
