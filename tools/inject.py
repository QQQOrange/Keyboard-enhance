"""把生成的引擎表注入 SettingsStore.cs，替换 s.Search 列表。

只替换 "s.Search = new List<QEntry>" 到匹配的 "};" 之间的内容，
其余代码（含 BuiltinEnginesVersion 与迁移逻辑）原样保留。
"""
import io
import os

WS = r"C:\Users\64381\Documents\deepseek-harness\default-workspace"
CS = os.path.join(WS, "CapsLockPlus", "src", "CapsLockPlus", "Core", "Settings", "SettingsStore.cs")
LINES = os.path.join(WS, "_tools", "qs_engine_lines.txt")

eng = open(LINES, encoding="utf-8").read().rstrip("\n").split("\n")
src = io.open(CS, encoding="utf-8").read()
lines = src.split("\n")

try:
    start = next(i for i, l in enumerate(lines) if "s.Search = new List<QEntry>" in l)
    end = next(i for i in range(start + 1, len(lines)) if lines[i].strip() == "};")
except StopIteration:
    raise SystemExit("没找到 s.Search 列表，可能已被替换过")

header = [
    "        s.Search = new List<QEntry>",
    "        {",
    '            new() { Key = "default", Value = "https://www.bing.com/search?q={q}", Comment = "默认搜索" },',
    "",
    "            // ---- 引擎表由 QuickSearch 的 ui/data/engines.json 转换而来 ----",
    "            // 生成脚本：tools/qs_final.py ；图标已内置为 Assets/engine-icons/*.png。",
    "            // 关键字规则：国内引擎取拼音首字母，国外引擎取英文名前两个字母。",
]
footer = ["        };"]

new = lines[:start] + header + eng + footer + lines[end + 1:]
io.open(CS, "w", encoding="utf-8").write("\n".join(new))

print(f"原列表 {start+1}..{end+1} 行，替换为 {len(header)+len(eng)+len(footer)} 行")
print("引擎数（含 default）:", len(eng) + 1)
