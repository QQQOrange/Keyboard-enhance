"""最终生成：从 QuickSearch 的 engines.json 生成引擎表 + 同步它用到的所有图标。

这是唯一需要执行的脚本（前几版被拆成好几步，互相覆盖踩过坑）。它做三件事：
  1. 从源图标目录算出 slug，并把 SVG 转换结果同步进 Assets/engine-icons；
  2. 生成引擎表（只保留"图标确实存在"的引擎）；
  3. 删除没被任何引擎引用的图标（用户要求：用不到的图标不要）。
"""
import json
import os
import re
import shutil
from pypinyin import lazy_pinyin

ROOT = r"D:\Local AI\git\Quick search 源代码"
ENG = os.path.join(ROOT, "ui", "data", "engines.json")
SRC_ICONS = os.path.join(ROOT, "ui", "data", "icons")

WS = r"C:\Users\64381\Documents\deepseek-harness\default-workspace"
DIR = os.path.join(WS, "CapsLockPlus", "src", "CapsLockPlus", "Assets", "engine-icons")
CONVERTED = os.path.join(WS, "_tools", "all-icons-new")   # 已由 --svg2png 转好的 SVG 结果
OUT = os.path.join(WS, "_tools", "qs_engine_lines.txt")


def slug_of(stem: str) -> str:
    if re.fullmatch(r"[\x00-\x7F]+", stem):
        s = re.sub(r"[^A-Za-z0-9]+", "_", stem).strip("_").lower()
    else:
        s = "_".join(lazy_pinyin(stem))
        s = re.sub(r"[^A-Za-z0-9]+", "_", s).strip("_").lower()
    return (s or "icon")[:48]


# ---------- 1. 同步 SVG 转换结果 ----------
synced = 0
if os.path.isdir(CONVERTED):
    for fn in os.listdir(CONVERTED):
        if not fn.endswith(".png"):
            continue
        stem = os.path.splitext(fn)[0]
        dst = os.path.join(DIR, slug_of(stem) + ".png")
        s = os.path.join(CONVERTED, fn)
        # 已经存在且更大的就不覆盖（避免把 PNG/ICO 转的清晰图覆盖成 SVG 小图）
        if os.path.exists(dst) and os.path.getsize(dst) > os.path.getsize(s):
            continue
        shutil.copy2(s, dst)
        synced += 1
print(f"同步 SVG 图标 {synced} 个")

# ---------- 2. 生成引擎表 ----------
data = json.load(open(ENG, encoding="utf-8"))
have = {os.path.splitext(f)[0] for f in os.listdir(DIR) if f.endswith(".png")}

MANUAL = {
    "百度": "bd", "必应": "by", "搜狗": "sogou", "360": "so",
    "哔哩哔哩": "bili", "百度翻译": "bf", "百度知道": "bzd", "百度百科": "bb",
    "百度文库": "bwk", "百度经验": "bjy", "微信(搜狗)": "wx", "知乎": "zh",
    "知乎直答": "zd", "腾讯元宝": "yb", "智谱清言": "zp", "文心一言": "wy",
    "海螺AI": "hailuo", "秘塔AI搜索": "mt", "神马搜索": "sm", "夸克搜索": "kk",
    "头条搜索": "tt", "无追搜索": "wz", "豆瓣读书": "dushu", "豆瓣": "db",
    "萌娘百科": "meng", "少数派": "sspai", "果壳": "guokr", "简书": "jianshu",
    "脉脉": "maimai", "博客园": "cnblogs", "开源中国": "oschina",
    "菜鸟教程": "runoob", "腾讯云社区": "txcloud", "阿里云开发者": "aliyun",
    "维基词典": "wcd", "维基文库": "wks", "维基百科": "wiki",
    "淘宝": "tb", "京东": "jd", "微博": "wb", "小红书": "xhs", "抖音": "dy",
    "爱奇艺": "iqy", "腾讯视频": "tx", "拼多多": "pdd", "豆包": "doubao",
    "通义": "tongyi", "Kimi": "kimi", "DeepSeek": "deepseek",
    "掘金": "juejin", "CSDN": "csdn", "Gitee": "gitee", "LeetCode": "leetcode",
}

MANUAL_EN = {
    "google": "go", "bing": "bi", "github": "gh", "gitlab": "gl",
    "wikipedia": "wi", "youtube": "yo", "youtube music": "yom",
    "reddit": "re", "stackoverflow": "so", "stackoverflow(谷歌)": "sog",
    "mdn": "md", "amazon": "am", "twitter": "tw", "x (twitter)": "x",
    "facebook": "fa", "duckduckgo": "dd", "yahoo": "ya",
    "qwant": "qw", "mojeek": "mj", "startpage": "st", "yandex": "yx",
    "brave 搜索": "br", "ecosia": "ec", "npm": "np", "nuget": "nu",
    "pypi": "pypi", "maven": "maven", "docker hub": "docker",
    "chatgpt": "gpt", "claude": "claude", "gemini": "gemini", "grok": "grok",
    "copilot": "copilot", "perplexity ai": "perplexity",
    "hugging face": "hf", "mistral": "mistral", "poe": "poe",
    "quora": "quora", "wikihow": "wikihow", "hacker news": "hn",
    "stack exchange": "se", "crates.io": "crates", "go packages": "gopkg",
    "rubygems": "gems", "packagist": "packagist", "bitbucket": "bitbucket",
    "sourcegraph": "sourcegraph", "conda": "conda", "arch wiki": "arch",
    "lobsters": "lobsters", "dev": "dev", "leetcode": "leetcode",
    "infoq": "infoq", "apache issues": "apache", "segmentfault": "segmentfault",
    "qwen chat": "qwen", "codeberg": "codeberg",
}

EXTRA = [
    ("bi", "https://cn.bing.com/search?q={q}", "Bing", "bi_ying"),
    ("wi", "https://en.wikipedia.org/w/index.php?search={q}", "Wikipedia（英文）", "wei_ji_bai_ke"),
    ("am", "https://www.amazon.com/s?k={q}", "Amazon", "ya_ma_xun"),
]


def key_for(name: str):
    raw, lower = name.strip(), name.strip().lower()
    if raw in MANUAL:
        return MANUAL[raw]
    if lower in MANUAL_EN:
        return MANUAL_EN[lower]
    letters = re.sub(r"[^a-zA-Z]", "", lower)
    if letters:
        return letters[:2]
    try:
        ini = "".join(lazy_pinyin(raw, style=1))
        ini = re.sub(r"[^a-z]", "", ini.lower())
        return ini[:4] if ini else None
    except Exception:
        return None


rows, seen = [], set()
for cat in data.get("categories", []):
    for e in cat.get("engines", []):
        if e.get("kind") != "search":
            continue
        name = e["name"]
        if name in seen:
            continue
        f = e.get("iconFile")
        if not f:
            continue
        slug = slug_of(os.path.splitext(f)[0])
        if slug not in have:
            continue                      # 图标没有就跳过这个引擎
        seen.add(name)
        rows.append({"name": name, "url": e["url"].replace("%s", "{q}"), "icon": slug})

used = {}
for r in rows:
    k = key_for(r["name"]) or ("e" + str(len(used)))
    base, n = k, 2
    while k in used:
        k = f"{base}{n}"
        n += 1
    used[k] = r["name"]
    r["key"] = k

for key, url, name, icon in EXTRA:
    if key in used or icon not in have:
        continue
    used[key] = name
    rows.append({"key": key, "name": name, "url": url, "icon": icon})

# 去掉真正的重复项：QuickSearch 原表里同一个站点的同一个搜索入口被列了两次。
#   py  与 pypi 都是 PyPI 搜索  -> 去掉 py，保留 pypi
#   by  与 bi   都是必应搜索    -> 去掉 by，保留 bi（bi 是此前已发布的关键字，必须留）
# 注意：同一站点的"图片/地图/新闻/词典"是不同的引擎，要保留，所以只按
# "域名 + 路径" 判重，不按域名判重。
DEDUPE = {"py", "by"}


def dedupe_key(url: str) -> str:
    s = url
    i = s.find("://")
    if i >= 0:
        s = s[i + 3:]
    # 去掉查询串再比，路径相同即视为同一入口
    for ch in ("?", "#"):
        j = s.find(ch)
        if j >= 0:
            s = s[:j]
    s = s.lower()
    if s.startswith("www."):
        s = s[4:]
    return s


kept, seen_entries = [], set()
for r in rows:
    if r["key"] in DEDUPE:
        continue
    dk = dedupe_key(r["url"])
    if dk in seen_entries:
        continue
    seen_entries.add(dk)
    kept.append(r)
rows = kept

PRIORITY = ["google", "百度", "bing", "必应", "github", "知乎", "哔哩哔哩",
            "淘宝", "youtube", "wikipedia", "维基百科", "DeepSeek", "ChatGPT"]
rows.sort(key=lambda r: (PRIORITY.index(r["name"]) if r["name"] in PRIORITY else 999, r["name"]))

lines = []
for r in rows:
    nm = r["name"].replace('"', '\\"')
    url = r["url"].replace('"', '\\"')
    lines.append(f'            new() {{ Key = "{r["key"]}", Value = "{url}", '
                 f'Comment = "{nm}", EngineIcon = "{r["icon"]}" }},')
open(OUT, "w", encoding="utf-8").write("\n".join(lines))

# ---------- 3. 删除未引用的图标 ----------
needed = {r["icon"] for r in rows}
unused = sorted(have - needed)
removed = 0
for u in unused:
    try:
        os.remove(os.path.join(DIR, u + ".png"))
        removed += 1
    except Exception:
        pass

left = [f for f in os.listdir(DIR) if f.endswith(".png")]
total = sum(os.path.getsize(os.path.join(DIR, f)) for f in left)

print(f"引擎数: {len(rows)}")
print(f"所用图标: {len(needed)}")
print(f"删除未引用图标: {removed}")
print(f"最终图标: {len(left)} 个, {total/1024:.0f} KB")

from collections import Counter
dup = [k for k, v in Counter(r["key"] for r in rows).items() if v > 1]
print("关键字冲突:", dup or "无")
