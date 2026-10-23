"""实测系统音量横条的位置（物理像素），用来校准亮度横条的下沿。

做法：
  1. 截一张底图；
  2. 发一次 VK_VOLUME_UP，让 Windows 弹出自己的音量横条；
  3. 立刻再截一张，和底图求差，找出变化区域的外接矩形；
  4. 打印矩形（物理像素 + DIP @该屏缩放）、屏幕/工作区/任务栏信息。

横条是 GPU 渲染的，截图能看到；亮度背光不在截图里，所以这个办法
只适合量"系统音量条"，而它正是我们要对齐的目标。
"""
import ctypes
import time
from ctypes import wintypes

from PIL import ImageGrab

user32 = ctypes.WinDLL("user32", use_last_error=True)
shell32 = ctypes.WinDLL("shell32", use_last_error=True)

VK_VOLUME_UP = 0xAF
KEYEVENTF_KEYUP = 0x0002


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]


class INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("ki", KEYBDINPUT), ("pad", ctypes.c_byte * 32)]
    _anonymous_ = ("u",)
    _fields_ = [("type", wintypes.DWORD), ("u", _U)]


def send_volume_up():
    inp = INPUT()
    inp.type = 1
    inp.ki = KEYBDINPUT(VK_VOLUME_UP, 0, 0, 0, None)
    user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))
    time.sleep(0.02)
    inp.ki = KEYBDINPUT(VK_VOLUME_UP, 0, KEYEVENTF_KEYUP, 0, None)
    user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


# ---- 屏幕 / 工作区 / 任务栏（物理像素） ----
user32.SetProcessDPIAware()

SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN = 76, 77
SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN = 78, 79
vx = user32.GetSystemMetrics(SM_XVIRTUALSCREEN)
vy = user32.GetSystemMetrics(SM_YVIRTUALSCREEN)
vw = user32.GetSystemMetrics(SM_CXVIRTUALSCREEN)
vh = user32.GetSystemMetrics(SM_CYVIRTUALSCREEN)

SPI_GETWORKAREA = 0x0030
rect = wintypes.RECT()
user32.SystemParametersInfoW(SPI_GETWORKAREA, 0, ctypes.byref(rect), 0)


class APPBARDATA(ctypes.Structure):
    _fields_ = [("cbSize", wintypes.DWORD), ("hWnd", wintypes.HWND),
                ("uCallbackMessage", wintypes.UINT), ("uEdge", wintypes.UINT),
                ("rc", wintypes.RECT), ("lParam", wintypes.LPARAM)]


ABM_GETTASKBARPOS = 0x00000005
abd = APPBARDATA()
abd.cbSize = ctypes.sizeof(APPBARDATA)
ok_bar = shell32.SHAppBarMessage(ABM_GETTASKBARPOS, ctypes.byref(abd))

print(f"virtual screen: {vx},{vy} {vw}x{vh}")
print(f"work area:      {rect.left},{rect.top} {rect.right - rect.left}x{rect.bottom - rect.top}")
if ok_bar:
    r = abd.rc
    print(f"taskbar rect:   {r.left},{r.top} {r.right - r.left}x{r.bottom - r.top} (uEdge={abd.uEdge})")
else:
    print("taskbar rect:   (query failed)")

# ---- 找系统音量横条 ----
base = ImageGrab.grab(all_screens=True).convert("RGB")
print("base screenshot:", base.size)

send_volume_up()
# 音量条大约 200ms 淡入、2s 后消失；这里连拍几帧，排除动画中的状态
shots = []
for _ in range(6):
    time.sleep(0.12)
    shots.append(ImageGrab.grab(all_screens=True).convert("RGB"))

best = None
for idx, shot in enumerate(shots):
    if shot.size != base.size:
        print("size changed, skip")
        continue
    # 只在画面下 1/3 找差异，避免鼠标/时钟干扰
    y0 = int(base.size[1] * 2 / 3)
    bbox = None
    px_b = base.load()
    px_s = shot.load()
    minx, miny, maxx, maxy = 10 ** 9, 10 ** 9, -1, -1
    step = 2
    for y in range(y0, base.size[1], step):
        for x in range(0, base.size[0], step):
            a = px_b[x, y]
            b = px_s[x, y]
            if abs(a[0] - b[0]) + abs(a[1] - b[1]) + abs(a[2] - b[2]) > 40:
                if x < minx: minx = x
                if y < miny: miny = y
                if x > maxx: maxx = x
                if y > maxy: maxy = y
    if maxx < 0:
        print(f"frame {idx}: no diff")
        continue
    w, h = maxx - minx + 1, maxy - miny + 1
    print(f"frame {idx}: diff bbox x={minx}..{maxx} y={miny}..{maxy}  ({w}x{h})")
    if w > 100 and h > 40 and (best is None or h > best[1]):
        best = (w, h, minx, miny, maxx, maxy)

if best:
    w, h, minx, miny, maxx, maxy = best
    print(f"\nOSD bbox (physical): x={minx}..{maxx} y={miny}..{maxy} => {w}x{h}")
    print(f"OSD bottom gap to screen bottom: {vh - 1 - maxy} px")
    print(f"OSD bottom gap to work-area bottom: {rect.bottom - 1 - maxy} px")
    if ok_bar:
        print(f"OSD bottom gap to taskbar top: {abd.rc.top - 1 - maxy} px")
    print(f"OSD height: {h} px (DIP @2x: {h/2:.1f})")
else:
    print("no OSD found")
