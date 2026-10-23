"""实测横条圆角半径（物理像素），用来把我们的亮度条和系统音量条对齐。

为什么不能靠肉眼估：192×48 的卡片在 200% 缩放下是 384×96 像素，
圆角差 2 DIP 只有 4 个像素，截图缩到屏幕上看几乎一样，
但并排时"我们的更方"一眼就能看出来 —— 用户就是这么发现的。

做法：
  1. 本进程直接开一块纯黑全屏置顶窗体（Tk），把背景压成 0；
     （不调 tools/blackwin.ps1：.ps1 会被执行策略拦）
  2. 触发横条：系统 = 发 VK_VOLUME_UP，我们 = 起 --osd 50；
  3. 截屏，在底部找亮块的外接矩形；
  4. 沿左上角逐行求"左边界"，用圆角矩形方程做最小二乘拟合半径。

用法：
  python tools/measure_osd_radius.py system
  python tools/measure_osd_radius.py ours  <exe 路径>
"""
import ctypes
import os
import subprocess
import sys
import time
import tkinter as tk
from ctypes import wintypes

from PIL import ImageGrab

user32 = ctypes.WinDLL("user32")
user32.SetProcessDPIAware()
VK_VOLUME_UP = 0xAF


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]


class INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("ki", KEYBDINPUT), ("pad", ctypes.c_byte * 32)]
    _anonymous_ = ("u",)
    _fields_ = [("type", wintypes.DWORD), ("u", _U)]


def tap(vk=VK_VOLUME_UP):
    i = INPUT()
    i.type = 1
    i.ki = KEYBDINPUT(vk, 0, 0, 0, None)
    user32.SendInput(1, ctypes.byref(i), ctypes.sizeof(INPUT))
    time.sleep(0.02)
    i.ki = KEYBDINPUT(vk, 0, 2, 0, None)
    user32.SendInput(1, ctypes.byref(i), ctypes.sizeof(INPUT))


def shot():
    return ImageGrab.grab(all_screens=True).convert("RGB")


def find_card(img):
    """底部区域的亮块外接矩形。阈值取"黑底(0) 与卡片(~129)"的中点附近。

    x 只在**屏幕中间一条**找：横条是水平居中的，而右下角随时可能弹出
    通知气泡/专注助手提示 —— 一旦入镜，外接矩形会被它拉到整块屏幕，
    拟合出来的半径就没有意义（踩过：量出 2080×474 的"卡片"）。
    """
    px = img.load()
    W, H = img.size
    x_lo = max(0, W // 2 - 500)
    x_hi = min(W, W // 2 + 500)
    minx, miny, maxx, maxy = W, H, -1, -1
    for y in range(H - 500, H):
        for x in range(x_lo, x_hi):
            r, g, b = px[x, y]
            if r + g + b > 70:          # 排除阴影（约 32）
                if x < minx: minx = x
                if y < miny: miny = y
                if x > maxx: maxx = x
                if y > maxy: maxy = y
    if maxx < 0:
        return None
    return (minx, miny, maxx, maxy)


def left_edge(px, y, x_from, x_to, thr):
    """第 y 行从左往右找第一个亮度跨过 thr 的位置（线性插值，亚像素）。"""
    prev = None
    for x in range(x_from, x_to):
        r, g, b = px[x, y]
        v = r + g + b
        if prev is not None and prev < thr <= v:
            t = (thr - prev) / (v - prev)
            return (x - 1) + t
        prev = v
    return None


def fit_radius(px, x0, y0, card_sum, max_r=40, from_bottom=None):
    """用 d(t) = R - sqrt(R^2 - (R - t)^2) 拟合半径 R（物理像素）。

    t 是从卡片上（或下）边往里数的行号，d 是当行左边界相对卡片左边的偏移。
    t >= R 之后是直边，理论偏移 0。

    为什么还要 from_bottom：两条横条的**底边**必然对齐（都贴工作区底 14 DIP），
    而顶边会差 1~2 px（WPF 与 DWM 的抗锯齿起点不同）。从顶边量等于把
    这 1~2 px 的偏差塞进了半径里；从底边量就是同一起跑线。
    """
    thr = card_sum * 0.5
    prof = []
    for t in range(0, max_r + 6):
        y = (from_bottom - t) if from_bottom is not None else (y0 + t)
        e = left_edge(px, y, x0 - 6, x0 + max_r + 8, thr)
        if e is not None:
            prof.append((t, e - x0))
    if not prof:
        return None, []

    best = None
    for q in range(4, max_r * 4 + 1):        # 0.25 px 步长
        R = q * 0.25
        err = 0.0
        for t, d in prof:
            if t >= R:
                model = 0.0
            else:
                dy = R - t
                model = R - (R * R - dy * dy) ** 0.5
            err += (model - d) ** 2
        mse = err / len(prof)
        if best is None or mse < best[1]:
            best = (R, mse)
    return best, prof


def analyze(tag, img):
    box = find_card(img)
    if not box:
        print(f"[{tag}] 没找到横条")
        return
    x0, y0, x1, y1 = box
    w, h = x1 - x0 + 1, y1 - y0 + 1
    px = img.load()

    mid = (y0 + y1) // 2
    samples = [sum(px[x, mid]) for x in range(x0 + 2, x0 + 12)]
    card_sum = sum(samples) / len(samples)

    best, prof = fit_radius(px, x0, y0, card_sum)
    print(f"[{tag}] 卡片 bbox=({x0},{y0})-({x1},{y1})  {w}x{h} px  净亮度={card_sum:.0f}")
    if best:
        R, mse = best
        print(f"[{tag}] 左上角拟合 R={R:.2f} px  → {R / 2:.2f} DIP @200%  (mse={mse:.2f})")

    bestB, profB = fit_radius(px, x0, y0, card_sum, from_bottom=y1)
    if bestB:
        R, mse = bestB
        print(f"[{tag}] 左下角拟合 R={R:.2f} px  → {R / 2:.2f} DIP @200%  (mse={mse:.2f})"
              f"   ← 底边对齐，最可靠")
    if profB:
        print(f"[{tag}] 左下剖面: " + " ".join(f"{t}:{d:.1f}" for t, d in profB[:12]))
    if prof:
        print(f"[{tag}] 左上剖面: " + " ".join(f"{t}:{d:.1f}" for t, d in prof[:12]))
    img.crop((x0 - 8, y0 - 8, x0 + 60, y0 + 60)).resize((272, 272), 0) \
        .save(f"tools/osd_corner_{tag}.png")


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "system"
    exe = sys.argv[2] if len(sys.argv) > 2 else None

    vx = user32.GetSystemMetrics(76)
    vy = user32.GetSystemMetrics(77)
    vw = user32.GetSystemMetrics(78)
    vh = user32.GetSystemMetrics(79)

    root = tk.Tk()
    root.overrideredirect(True)
    root.attributes("-topmost", True)
    root.geometry(f"{vw}x{vh}+{vx}+{vy}")
    root.configure(bg="black")
    root.update()

    result = {}

    def trigger():
        if mode == "system":
            tap()
        else:
            result["proc"] = subprocess.Popen(
                [exe, "--osd", "50"],
                env={**os.environ,
                     "CAPSLOCKPLUS_DATA": os.path.abspath("tools/.rad-data"),
                     "CAPSLOCKPLUS_MUTEX_SUFFIX": "_rad"})
        root.after(700 if mode == "system" else 1100, capture)

    def capture():
        img = shot()
        img.save(f"tools/osd_full_{mode}.png")
        analyze(mode, img)
        root.after(60, root.destroy)

    root.after(1100, trigger)
    root.mainloop()


if __name__ == "__main__":
    main()
