"""逐像素比较两个目录下的同名 PNG（用于验证"无损优化"真的无损）。

用法：python tools/cmp_png.py <目录A> <目录B>
"""

import glob
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from png8 import decode, to_rgba   # 复用同一套解码器


def load(path):
    w, h, ct, nch, px, pal, trns = decode(path)
    return w, h, to_rgba(w, h, ct, nch, px, pal, trns)


def main():
    a_dir, b_dir = sys.argv[1], sys.argv[2]
    files = sorted(os.path.basename(p) for p in glob.glob(os.path.join(a_dir, "*.png")))
    worse = 0
    for name in files:
        pa, pb = os.path.join(a_dir, name), os.path.join(b_dir, name)
        if not os.path.exists(pb):
            print(f"{name}: 缺少 B")
            continue
        wa, ha, da = load(pa)
        wb, hb, db = load(pb)
        if (wa, ha) != (wb, hb):
            print(f"{name}: 尺寸不同 {wa}x{ha} vs {wb}x{hb}")
            worse += 1
            continue
        diff = 0
        maxd = 0
        for i in range(0, len(da), 4):
            if da[i:i + 4] != db[i:i + 4]:
                diff += 1
                for k in range(4):
                    maxd = max(maxd, abs(da[i + k] - db[i + k]))
        if diff:
            total = len(da) // 4
            print(f"{name}: {diff}/{total} 像素不同（{diff/total*100:.2f}%），最大通道差 {maxd}")
            worse += 1
    print(f"\n比较 {len(files)} 张，有差异 {worse} 张")


if __name__ == "__main__":
    main()
