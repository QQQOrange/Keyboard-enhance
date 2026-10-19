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

    public static readonly string Translate = Materialize("translate", """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="24" fill="#0F9D58"/>
          <path d="M12 16h11v2.4h-4.2c.4 2.6 1.5 5 3.2 7-1.2 1-2.6 1.8-4.1 2.4l-1-1.9c1.2-.5 2.3-1.2 3.2-2-1.3-1.6-2.2-3.5-2.6-5.5H12V16z" fill="#fff"/>
          <path d="M22 32h2.4l1-2.6h5.2l1 2.6H34l-5-13h-2l-5 13zm4.1-4.6l1.8-4.8 1.8 4.8h-3.6z" fill="#fff"/>
          <path d="M26 16h10v2h-10z" fill="#fff" opacity=".9"/>
        </svg>
        """);
}
