"""在几种窗口尺寸下截「按键映射」页，用来看键盘有没有按比例缩放。

为什么需要这个脚本：`--render` 固定从默认窗口尺寸出图，看不出"缩小窗口后
键盘被压扁"的回归。这里直接起一个隔离实例，用 SetWindowPos 摆成几个常见
尺寸（默认 1240x800 / 半屏 / 最大化），再 PrintWindow 抓图。

DIP → 物理像素按 200% 算（这台机器是 3840x2160 + 200%）。
用法：python tools/capture_keys_sizes.py
"""
import ctypes
import os
import subprocess
import time
from ctypes import wintypes

from PIL import Image

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
user32.SetProcessDPIAware()

ROOT = r"D:\Local AI\git\Keyboard enhance源代码"
EXE = os.path.join(ROOT, r"src\CapsLockPlus\bin\Release\net10.0-windows\win-x64\KeyboardEnhance.exe")
OUT_DIR = os.path.join(ROOT, "tools")

DPI = 2.0
# (名字, DIP 宽, DIP 高)；height=None 表示最大化
SIZES = [
    ("keys_default", 1240, 800),
    ("keys_1500", 1500, 900),
    ("keys_max", None, None),
]

env = dict(os.environ)
env["CAPSLOCKPLUS_DATA"] = os.path.join(ROOT, ".uitest-data")
env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = "uitest"

EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def find_window(pid):
    found = []

    def cb(h, l):
        p = wintypes.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(h):
            if user32.GetWindowTextLengthW(h) > 0:
                buf = ctypes.create_unicode_buffer(512)
                user32.GetWindowTextW(h, buf, 512)
                found.append((h, buf.value))
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return found


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG),
                ("biHeight", wintypes.LONG), ("biPlanes", wintypes.WORD),
                ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
                ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD),
                ("biClrImportant", wintypes.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


def capture(hwnd, out_path):
    rect = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    w, h = rect.right - rect.left, rect.bottom - rect.top

    hdc = user32.GetWindowDC(hwnd)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    user32.PrintWindow(hwnd, mem, 2)          # PW_RENDERFULLCONTENT

    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -h
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    bi.bmiHeader.biCompression = 0

    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    img = Image.frombuffer("RGB", (w, h), buf, "raw", "BGRX", 0, 1)
    img.save(out_path)

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(hwnd, hdc)
    return img.size


proc = subprocess.Popen([EXE], env=env)

hwnd = None
for _ in range(80):
    time.sleep(0.25)
    for h, title in find_window(proc.pid):
        if "按键增强" in title or "Keyboard" in title:
            hwnd = h
            break
    if hwnd:
        break

if not hwnd:
    print("main window not found")
    proc.kill()
    raise SystemExit(1)

time.sleep(1.0)

# 切到「按键映射」页：点侧边栏第二项（默认打开的是快速上手）。
# 用 PostMessage 发鼠标消息，不动真实光标。
def click(hwnd, x_dip, y_dip):
    x, y = int(x_dip * DPI), int(y_dip * DPI)
    lp = (y << 16) | (x & 0xFFFF)
    user32.PostMessageW(hwnd, 0x0200, 0, lp)          # WM_MOUSEMOVE
    user32.PostMessageW(hwnd, 0x0201, 1, lp)          # WM_LBUTTONDOWN
    time.sleep(0.05)
    user32.PostMessageW(hwnd, 0x0202, 0, lp)          # WM_LBUTTONUP
    time.sleep(0.8)


click(hwnd, 76, 118)

for name, dw, dh in SIZES:
    if dw is None:
        user32.ShowWindow(hwnd, 3)            # SW_MAXIMIZE
    else:
        user32.ShowWindow(hwnd, 9)            # SW_RESTORE
        user32.SetWindowPos(hwnd, 0, 60, 60,
                            int(dw * DPI), int(dh * DPI), 0x0004 | 0x0010)  # NOZORDER|NOACTIVATE
    time.sleep(1.2)
    out = os.path.join(OUT_DIR, name + ".png")
    size = capture(hwnd, out)
    print(name, size, "->", out)

proc.kill()
print("done")
