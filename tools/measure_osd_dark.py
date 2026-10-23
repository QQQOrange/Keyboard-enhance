import ctypes, time
from ctypes import wintypes
from PIL import ImageGrab

user32 = ctypes.WinDLL("user32")
user32.SetProcessDPIAware()
VK = 0xAF


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD),
                ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]


class INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("ki", KEYBDINPUT), ("pad", ctypes.c_byte * 32)]
    _anonymous_ = ("u",)
    _fields_ = [("type", wintypes.DWORD), ("u", _U)]


def tap(vk=VK):
    i = INPUT()
    i.type = 1
    i.ki = KEYBDINPUT(vk, 0, 0, 0, None)
    user32.SendInput(1, ctypes.byref(i), ctypes.sizeof(INPUT))
    time.sleep(0.02)
    i.ki = KEYBDINPUT(vk, 0, 2, 0, None)
    user32.SendInput(1, ctypes.byref(i), ctypes.sizeof(INPUT))


time.sleep(1.6)          # 等黑底窗口盖住桌面
tap()
time.sleep(0.55)

img = ImageGrab.grab(all_screens=True).convert("RGB")
img.save(r"D:\Local AI\git\Keyboard enhance源代码\tools\osd_black.png")
print("size", img.size)

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

print("bright bbox", (minx, miny, maxx, maxy),
      "size", (maxx - minx + 1, maxy - miny + 1))
print("gap to screen bottom", H - 1 - maxy, "px")
