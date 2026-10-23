"""把主窗口最大化后截图（验证"快速上手页最大化后右边/下边是空的"）。

用 PrintWindow(PW_RENDERFULLCONTENT) 抓窗口内容，即使窗口被别的窗口盖住也能拿到。
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

EXE = r"D:\Local AI\git\Keyboard enhance源代码\src\CapsLockPlus\bin\Release\net10.0-windows\win-x64\KeyboardEnhance.exe"
OUT = r"D:\Local AI\git\Keyboard enhance源代码\tools\main_max.png"

env = dict(os.environ)
env["CAPSLOCKPLUS_DATA"] = r"D:\Local AI\git\Keyboard enhance源代码\.uitest-data"
env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = "uitest"

proc = subprocess.Popen([EXE], env=env)

EnumWindowsProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def find_window(pid):
    found = []

    def cb(h, l):
        p = wintypes.DWORD()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(h):
            n = user32.GetWindowTextLengthW(h)
            if n > 0:
                buf = ctypes.create_unicode_buffer(512)
                user32.GetWindowTextW(h, buf, 512)
                found.append((h, buf.value))
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return found


hwnd = None
for _ in range(60):
    time.sleep(0.25)
    wins = find_window(proc.pid)
    for h, title in wins:
        if "按键增强" in title or "Keyboard" in title:
            hwnd = h
            break
    if hwnd:
        break

if not hwnd:
    print("main window not found")
    proc.kill()
    raise SystemExit(1)

print("hwnd", hex(hwnd))
user32.ShowWindow(hwnd, 3)          # SW_MAXIMIZE
time.sleep(1.2)

rect = wintypes.RECT()
user32.GetWindowRect(hwnd, ctypes.byref(rect))
w, h = rect.right - rect.left, rect.bottom - rect.top
print("window rect", rect.left, rect.top, w, h)

hdc = user32.GetWindowDC(hwnd)
mem = gdi32.CreateCompatibleDC(hdc)
bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
gdi32.SelectObject(mem, bmp)

ok = user32.PrintWindow(hwnd, mem, 2)   # PW_RENDERFULLCONTENT
print("PrintWindow", ok)


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG),
                ("biHeight", wintypes.LONG), ("biPlanes", wintypes.WORD),
                ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
                ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD),
                ("biClrImportant", wintypes.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


bi = BITMAPINFO()
bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
bi.bmiHeader.biWidth = w
bi.bmiHeader.biHeight = -h        # top-down
bi.bmiHeader.biPlanes = 1
bi.bmiHeader.biBitCount = 32
bi.bmiHeader.biCompression = 0

buf = ctypes.create_string_buffer(w * h * 4)
gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)

img = Image.frombuffer("RGB", (w, h), buf, "raw", "BGRX", 0, 1)
img.save(OUT)
print("saved", OUT, img.size)

gdi32.DeleteObject(bmp)
gdi32.DeleteDC(mem)
user32.ReleaseDC(hwnd, hdc)

proc.kill()
print("killed")
