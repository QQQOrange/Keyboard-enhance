"""验证图标"无损重压"：直接比较 git 里的原图与工作区里的新图，逐像素。

比走 WPF 渲染更可靠 —— 渲染本身带 ClearType/缩放的不确定性。
用法：python tools/verify_icons.py
"""

import io
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from png8 import decode, to_rgba

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def pixels(data):
    """从字节流解出 (w,h,rgba)。"""
    import tempfile
    with tempfile.NamedTemporaryFile(suffix=".png", delete=False) as f:
        f.write(data)
        tmp = f.name
    try:
        w, h, ct, nch, px, pal, trns = decode(tmp)
        return w, h, to_rgba(w, h, ct, nch, px, pal, trns)
    finally:
        os.unlink(tmp)


def main():
    changed = subprocess.run(
        ["git", "diff", "--name-only", "HEAD", "--", "src/CapsLockPlus/Assets/engine-icons"],
        cwd=ROOT, capture_output=True, text=True).stdout.split()

    print(f"比较 {len(changed)} 张改动过的图标")
    bad = 0
    for rel in changed:
        old = subprocess.run(["git", "show", f"HEAD:{rel}"], cwd=ROOT, capture_output=True).stdout
        new = open(os.path.join(ROOT, rel), "rb").read()
        wa, ha, da = pixels(old)
        wb, hb, db = pixels(new)
        if (wa, ha) != (wb, hb):
            print(f"  {os.path.basename(rel)}: 尺寸变了")
            bad += 1
            continue
        if da != db:
            diff = sum(1 for i in range(0, len(da), 4) if da[i:i + 4] != db[i:i + 4])
            maxd = max(abs(da[i + k] - db[i + k])
                       for i in range(0, len(da), 4) if da[i:i + 4] != db[i:i + 4]
                       for k in range(4))
            print(f"  {os.path.basename(rel)}: {diff} 像素不同，最大差 {maxd}")
            bad += 1
        o, n = len(old), len(new)
        if o > n:
            pass

    if bad == 0:
        print("全部像素完全一致（无损）")
    else:
        print(f"有 {bad} 张不一致")


if __name__ == "__main__":
    main()
