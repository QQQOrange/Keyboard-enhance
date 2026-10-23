"""量一次我们自己画的亮度横条，和系统音量条逐像素对比。

前提：另开一个纯黑全屏窗口把桌面盖住（tools/blackwin.ps1），
这样卡片边缘是干净的亮/暗边界，量出来的 bbox 才有意义。
"""
import os
import subprocess
import time

from PIL import ImageGrab

EXE = r"D:\Local AI\git\Keyboard enhance源代码\src\CapsLockPlus\bin\Release\net10.0-windows\win-x64\KeyboardEnhance.exe"

env = dict(os.environ)
env["CAPSLOCKPLUS_DATA"] = r"D:\Local AI\git\Keyboard enhance源代码\.osdtest-data"
env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = "osdtest"

time.sleep(1.4)          # 等黑底窗口就位
proc = subprocess.Popen([EXE, "--osd", "50"], env=env)
time.sleep(1.1)          # 等它 Show 出来

img = ImageGrab.grab(all_screens=True).convert("RGB")
img.save(r"D:\Local AI\git\Keyboard enhance源代码\tools\osd_ours.png")

px = img.load()
W, H = img.size
minx, miny, maxx, maxy = W, H, -1, -1
for y in range(H - 400, H):
    for x in range(0, W):
        r, g, b = px[x, y]
        if r + g + b > 120:
            minx = min(minx, x)
            miny = min(miny, y)
            maxx = max(maxx, x)
            maxy = max(maxy, y)

print("size", img.size)
print("ours bbox", (minx, miny, maxx, maxy),
      "size", (maxx - minx + 1, maxy - miny + 1))
print("gap to screen bottom", H - 1 - maxy, "px")
print("system bbox was (1728, 2038, 2111, 2131) size (384, 94), gap 28 px")

proc.wait(timeout=10)
