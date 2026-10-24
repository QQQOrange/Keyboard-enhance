"""离线看截图：比较两张 PNG 的差异区域，并统计该区域里的强调色。

为什么需要：聊天里没法"用眼睛看"截图时（换模型、批量回归、CI），
至少要用可复现的数字确认"界面真的变了、而且变对了地方"。

典型用法（验证「直达链接」的自动识别徽标）：

    # 1) 找出"填网址"与"填文件夹"两张图的差异范围
    python tools/probe_png_color.py diff docs/screenshots/keys-webpage.png \
                                      docs/screenshots/keys-link-folder.png

    # 2) 在差异框里统计各张图的蓝/绿/橙像素，确认徽标颜色跟着类型变
    python tools/probe_png_color.py colors docs/screenshots/keys-webpage.png \
                                             docs/screenshots/keys-link-folder.png \
                                             docs/screenshots/keys-link-file.png

颜色取自 Theme/Dark.xaml：Accent #5B93FF、Success #3DD68C、Warning #F5A524。
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from png8 import decode, to_rgba   # 复用已有解码器，不引第三方库

# (名字, R, G, B)
PALETTE = [
    ("蓝 Accent", 0x5B, 0x93, 0xFF),
    ("绿 Success", 0x3D, 0xD6, 0x8C),
    ("橙 Warning", 0xF5, 0xA5, 0x24),
]


def load(path):
    w, h, ct, nch, px, pal, trns = decode(path)
    return w, h, to_rgba(w, h, ct, nch, px, pal, trns)


def pixel(buf, w, x, y):
    i = (y * w + x) * 4
    return buf[i], buf[i + 1], buf[i + 2]


def diff_box(path_a, path_b, step=2, tol=6):
    """两张同尺寸图的差异包围盒：(x0, y0, x1, y1)，无差异返回 None。"""
    wa, ha, da = load(path_a)
    wb, hb, db = load(path_b)
    if (wa, ha) != (wb, hb):
        raise SystemExit(f"尺寸不同：{wa}x{ha} vs {wb}x{hb}")

    x0, y0, x1, y1 = wa, ha, -1, -1
    for y in range(0, ha, step):
        for x in range(0, wa, step):
            pa = pixel(da, wa, x, y)
            pb = pixel(db, wb, x, y)
            if max(abs(pa[i] - pb[i]) for i in range(3)) > tol:
                if x < x0: x0 = x
                if y < y0: y0 = y
                if x > x1: x1 = x
                if y > y1: y1 = y
    if x1 < 0:
        return None
    return x0, y0, x1 + step, y1 + step


def count_in_box(path, box, tol=14, step=1):
    """在给定框里统计各强调色的像素数。"""
    w, h, buf = load(path)
    x0, y0, x1, y1 = box
    counts = [0] * len(PALETTE)
    for y in range(max(0, y0), min(h, y1), step):
        for x in range(max(0, x0), min(w, x1), step):
            r, g, b = pixel(buf, w, x, y)
            for i, (_, tr, tg, tb) in enumerate(PALETTE):
                if abs(r - tr) <= tol and abs(g - tg) <= tol and abs(b - tb) <= tol:
                    counts[i] += 1
    return counts


def ascii_view(path, cols=120, rows=54, box=None, step=4):
    """把图缩成字符画（暗→亮：' ' . : - = + * # % @）。

    不是给人看美观，是给"没法直接看图"的场合一个廉价的版面轮廓：
    能看出卡片、输入框、按钮、大片空白落在哪，定位完再用 diff/colors 定量。
    """
    w, h, buf = load(path)
    x0, y0, x1, y1 = box if box else (0, 0, w, h)
    bw = max(1, (x1 - x0) // cols)
    bh = max(1, (y1 - y0) // rows)
    ramp = " .:-=+*#%@"

    lines = []
    for ry in range(rows):
        line = []
        for rx in range(cols):
            sx, sy = x0 + rx * bw, y0 + ry * bh
            total = 0
            n = 0
            for y in range(sy, min(sy + bh, y1), step):
                for x in range(sx, min(sx + bw, x1), step):
                    r, g, b = pixel(buf, w, x, y)
                    total += (r * 299 + g * 587 + b * 114) // 1000   # 亮度
                    n += 1
            v = total // n if n else 0
            line.append(ramp[min(len(ramp) - 1, v * len(ramp) // 256)])
        lines.append("".join(line))
    return "\n".join(lines)


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    mode = sys.argv[1]
    files = sys.argv[2:]

    if mode == "ascii":
        path = files[0]
        box = None
        if len(files) >= 5:
            box = tuple(int(v) for v in files[1:5])
        print(ascii_view(path, box=box))
        return 0

    if mode == "diff":
        if len(files) != 2:
            print("diff 需要正好两个文件")
            return 2
        box = diff_box(files[0], files[1])
        if box is None:
            print("两张图完全相同")
            return 0
        w, h, _ = load(files[0])
        print(f"差异范围：x {box[0]}..{box[2]}  y {box[1]}..{box[3]}"
              f"（图 {w}x{h}，占宽 {(box[2]-box[0])/w*100:.0f}%、"
              f"占高 {(box[3]-box[1])/h*100:.0f}%）")
        return 0

    if mode == "colors":
        if len(files) < 2:
            print("colors 至少需要两个文件（第一个用来定差异框）")
            return 2
        box = diff_box(files[0], files[1])
        if box is None:
            print("前两张图没有差异，无法定位区域")
            return 2
        print(f"差异框：x {box[0]}..{box[2]}  y {box[1]}..{box[3]}")
        for p in files:
            counts = count_in_box(p, box)
            summary = "  ".join(f"{name}={c}" for (name, _, _, _), c in zip(PALETTE, counts))
            print(f"{os.path.basename(p):28s} {summary}")
        return 0

    print(f"未知模式：{mode}")
    return 2


if __name__ == "__main__":
    sys.exit(main())
