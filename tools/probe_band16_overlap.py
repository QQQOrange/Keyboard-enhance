"""实验四：把测试窗口放到「开始菜单正中央」，确认 band16 真的压得住它。

上一版把窗口放在屏幕中部，而开始菜单只占底部一块，可能根本没重叠
（那种"通过"是假通过）。这里先量出开始菜单的真实矩形，再把
band16 普通窗口 / band16 分层窗口 / band1 topmost 对照窗口并排塞进去。

用法：
    python tools/probe_band16_overlap.py
"""

import ctypes
import ctypes.wintypes as w
import sys
import time

sys.path.insert(0, "tools")
from probe_band import band_of, user32  # noqa: E402

gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

user32.SetProcessDPIAware()
user32.DefWindowProcW.argtypes = [w.HWND, ctypes.c_uint, w.WPARAM, w.LPARAM]
user32.DefWindowProcW.restype = ctypes.c_ssize_t
WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, w.HWND, ctypes.c_uint, w.WPARAM, w.LPARAM)
CALLBACK = WNDPROC(lambda h, m, wp, lp: user32.DefWindowProcW(h, m, wp, lp))


class WNDCLASSW(ctypes.Structure):
    _fields_ = [
        ("style", ctypes.c_uint), ("lpfnWndProc", WNDPROC),
        ("cbClsExtra", ctypes.c_int), ("cbWndExtra", ctypes.c_int),
        ("hInstance", w.HINSTANCE), ("hIcon", w.HICON),
        ("hCursor", w.HANDLE), ("hbrBackground", w.HBRUSH),
        ("lpszMenuName", w.LPCWSTR), ("lpszClassName", w.LPCWSTR),
    ]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", w.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
        ("biPlanes", w.WORD), ("biBitCount", w.WORD), ("biCompression", w.DWORD),
        ("biSizeImage", w.DWORD), ("biXPelsPerMeter", ctypes.c_long),
        ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", w.DWORD),
        ("biClrImportant", w.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", w.DWORD * 3)]


CWIB = user32.CreateWindowInBand
CWIB.argtypes = [w.DWORD, w.ATOM, w.LPCWSTR, w.DWORD,
                 ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                 w.HWND, w.HMENU, w.HINSTANCE, w.LPVOID, w.DWORD]
CWIB.restype = w.HWND

user32.RegisterClassW.argtypes = [ctypes.POINTER(WNDCLASSW)]
user32.RegisterClassW.restype = w.ATOM
for fn, args in (
    ("GetDC", [w.HWND]), ("ReleaseDC", [w.HWND, w.HDC]),
    ("UpdateLayeredWindow", [w.HWND, w.HDC, ctypes.POINTER(w.POINT),
                             ctypes.POINTER(w.SIZE), w.HDC, ctypes.POINTER(w.POINT),
                             w.DWORD, ctypes.c_void_p, w.DWORD]),
    ("GetWindowRect", [w.HWND, ctypes.POINTER(w.RECT)]),
):
    getattr(user32, fn).argtypes = args

gdi32.CreateCompatibleDC.argtypes = [w.HDC]
gdi32.CreateCompatibleDC.restype = w.HDC
gdi32.CreateCompatibleBitmap.argtypes = [w.HDC, ctypes.c_int, ctypes.c_int]
gdi32.CreateCompatibleBitmap.restype = w.HBITMAP
gdi32.CreateDIBSection.argtypes = [w.HDC, ctypes.POINTER(BITMAPINFO), w.UINT,
                                   ctypes.POINTER(ctypes.c_void_p), w.HANDLE, w.DWORD]
gdi32.CreateDIBSection.restype = w.HBITMAP
gdi32.SelectObject.argtypes = [w.HDC, w.HGDIOBJ]
gdi32.SelectObject.restype = w.HGDIOBJ
gdi32.BitBlt.argtypes = [w.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                         w.HDC, ctypes.c_int, ctypes.c_int, w.DWORD]
gdi32.GetDIBits.argtypes = [w.HDC, w.HBITMAP, w.UINT, w.UINT, w.LPVOID,
                            ctypes.POINTER(BITMAPINFO), w.UINT]
gdi32.CreateSolidBrush.argtypes = [w.DWORD]
gdi32.CreateSolidBrush.restype = w.HBRUSH
gdi32.DeleteObject.argtypes = [w.HGDIOBJ]
gdi32.DeleteObject.restype = w.BOOL
gdi32.DeleteDC.argtypes = [w.HDC]

WS_POPUP, WS_VISIBLE = 0x80000000, 0x10000000
WS_EX_TOOLWINDOW, WS_EX_LAYERED, WS_EX_TOPMOST = 0x80, 0x80000, 0x08
ULW_ALPHA = 0x02
AC_SRC_OVER, AC_SRC_ALPHA = 0x00, 0x01
VK_LWIN, KEYEVENTF_KEYUP = 0x5B, 0x0002


def pixel(x, y):
    hdc = user32.GetDC(None)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, 1, 1)
    gdi32.SelectObject(mem, bmp)
    gdi32.BitBlt(mem, 0, 0, 1, 1, hdc, x, y, 0x00CC0020)
    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = 1
    bi.bmiHeader.biHeight = -1
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    buf = (ctypes.c_ubyte * 4)()
    gdi32.GetDIBits(mem, bmp, 0, 1, buf, ctypes.byref(bi), 0)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(None, hdc)
    return buf[2], buf[1], buf[0]


def tap_win():
    user32.keybd_event(VK_LWIN, 0, 0, None)
    time.sleep(0.04)
    user32.keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, None)


def rect_of(h):
    r = w.RECT()
    user32.GetWindowRect(h, ctypes.byref(r))
    return r


def make_plain(name, band, color, x, y, wide, tall, hinst):
    brush = gdi32.CreateSolidBrush(color[0] | (color[1] << 8) | (color[2] << 16))
    cls = WNDCLASSW()
    cls.lpfnWndProc = CALLBACK
    cls.hInstance = hinst
    cls.hbrBackground = brush
    cls.lpszClassName = name
    atom = user32.RegisterClassW(ctypes.byref(cls))
    h = CWIB(WS_EX_TOOLWINDOW, atom, name, WS_POPUP | WS_VISIBLE,
             x, y, wide, tall, None, None, hinst, None, band)
    print(f"  {name}: band={band} hwnd={h or 0} err={ctypes.get_last_error()} "
          f"实测band={band_of(h)}")
    return h, brush


def make_layered(name, band, color, x, y, wide, tall, hinst, alpha=230):
    cls = WNDCLASSW()
    cls.lpfnWndProc = CALLBACK
    cls.hInstance = hinst
    cls.lpszClassName = name
    atom = user32.RegisterClassW(ctypes.byref(cls))
    h = CWIB(WS_EX_TOOLWINDOW | WS_EX_LAYERED, atom, name, WS_POPUP | WS_VISIBLE,
             x, y, wide, tall, None, None, hinst, None, band)
    if not h:
        print(f"  {name}: band={band} 创建失败 err={ctypes.get_last_error()}")
        return 0

    screen = user32.GetDC(None)
    mem = gdi32.CreateCompatibleDC(screen)
    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = wide
    bi.bmiHeader.biHeight = -tall
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    bits = ctypes.c_void_p()
    bmp = gdi32.CreateDIBSection(mem, ctypes.byref(bi), 0, ctypes.byref(bits), None, 0)
    gdi32.SelectObject(mem, bmp)
    buf = (ctypes.c_ubyte * (wide * tall * 4)).from_address(bits.value)
    for i in range(0, len(buf), 4):
        buf[i + 0] = color[2]   # B
        buf[i + 1] = color[1]   # G
        buf[i + 2] = color[0]   # R
        buf[i + 3] = alpha      # A

    blend = ctypes.c_ubyte * 4
    bl = blend(AC_SRC_OVER, 0, alpha, AC_SRC_ALPHA)
    pt_dst = w.POINT(x, y)
    size = w.SIZE(wide, tall)
    pt_src = w.POINT(0, 0)
    ok = user32.UpdateLayeredWindow(h, screen, ctypes.byref(pt_dst), ctypes.byref(size),
                                    mem, ctypes.byref(pt_src), 0, ctypes.byref(bl), ULW_ALPHA)
    print(f"  {name}: band={band} hwnd={h} err={ctypes.get_last_error()} "
          f"实测band={band_of(h)} UpdateLayeredWindow={bool(ok)}")
    user32.ReleaseDC(None, screen)
    return h


def main():
    sw, sh = user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)
    print(f"屏幕 {sw}x{sh}")

    tap_win()
    time.sleep(1.5)
    fg = user32.GetForegroundWindow()
    r = rect_of(fg)
    print(f"开始菜单 hwnd={fg} rect={r.left},{r.top}-{r.right},{r.bottom} "
          f"band={band_of(fg)}")
    tap_win()
    time.sleep(0.6)

    if r.bottom <= r.top:
        print("没量到开始菜单，退出")
        return

    cx = (r.left + r.right) // 2
    cy = (r.top + r.bottom) // 2
    bw, bh = 300, 160
    y = cy - bh // 2

    hinst = kernel32.GetModuleHandleW(None)
    print("建窗口（都塞进开始菜单中心）：")
    w1, b1 = make_plain("P16Plain", 16, (255, 0, 0), cx - 480, y, bw, bh, hinst)
    w2 = make_layered("P16Layered", 16, (0, 255, 0), cx - 150, y, bw, bh, hinst)
    w3, b3 = make_plain("P1Topmost", 1, (0, 0, 255), cx + 180, y, bw, bh, hinst)
    user32.SetWindowPos(w3, -1, 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010)  # HWND_TOPMOST

    time.sleep(0.7)
    p1 = (cx - 480 + 150, y + 80)
    p2 = (cx - 150 + 150, y + 80)
    p3 = (cx + 180 + 150, y + 80)
    print("\n开始菜单未打开（R=band16 普通, G=band16 分层, B=band1 topmost）：")
    print(f"  {p1} = {pixel(*p1)}")
    print(f"  {p2} = {pixel(*p2)}")
    print(f"  {p3} = {pixel(*p3)}")

    tap_win()
    time.sleep(1.6)
    c1, c2, c3 = pixel(*p1), pixel(*p2), pixel(*p3)
    print("\n开始菜单已打开：")
    print(f"  band16 普通   = {c1}  {'✔ 在上面' if c1[0] > 180 and c1[1] < 70 else '✘ 被盖住'}")
    print(f"  band16 分层   = {c2}  {'✔ 在上面' if c2[1] > 180 and c2[0] < 70 else '✘ 被盖住'}")
    print(f"  band1 topmost = {c3}  {'✔ 在上面' if c3[2] > 180 and c3[0] < 70 else '✘ 被盖住（预期）'}")

    tap_win()
    time.sleep(0.5)
    for h in (w1, w2, w3):
        if h:
            user32.DestroyWindow(h)
    for b in (b1, b3):
        gdi32.DeleteObject(b)


if __name__ == "__main__":
    main()
