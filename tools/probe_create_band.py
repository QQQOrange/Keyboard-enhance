"""实验二：CreateWindowInBand 的权限门槛。

SetWindowBand 对普通进程一律 ACCESS_DENIED（见 probe_band_set.py），
但 CreateWindowInBand 能不能绕过去？这里对 0..17 逐个试真实创建。

用法：
    python tools/probe_create_band.py
"""

import ctypes
import ctypes.wintypes as w
import sys

sys.path.insert(0, ".")
from probe_band import ZBID, user32  # noqa: E402

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

WNDPROC = ctypes.WINFUNCTYPE(
    ctypes.c_ssize_t, w.HWND, ctypes.c_uint, w.WPARAM, w.LPARAM)

ClassWndProc = WNDPROC(lambda h, m, wp, lp: user32.DefWindowProcW(h, m, wp, lp))


class WNDCLASSW(ctypes.Structure):
    _fields_ = [
        ("style", ctypes.c_uint),
        ("lpfnWndProc", WNDPROC),
        ("cbClsExtra", ctypes.c_int),
        ("cbWndExtra", ctypes.c_int),
        ("hInstance", w.HINSTANCE),
        ("hIcon", w.HICON),
        ("hCursor", w.HANDLE),
        ("hbrBackground", w.HBRUSH),
        ("lpszMenuName", w.LPCWSTR),
        ("lpszClassName", w.LPCWSTR),
    ]


try:
    CreateWindowInBand = user32.CreateWindowInBand
    CreateWindowInBand.argtypes = [
        w.DWORD, w.ATOM, w.LPCWSTR, w.DWORD, ctypes.c_int, ctypes.c_int,
        ctypes.c_int, ctypes.c_int, w.HWND, w.HMENU, w.HINSTANCE, w.LPVOID, w.DWORD]
    CreateWindowInBand.restype = w.HWND
    HAVE = True
except AttributeError:
    HAVE = False

try:
    CreateWindowInBandEx = user32.CreateWindowInBandEx
    CreateWindowInBandEx.argtypes = [
        w.DWORD, w.ATOM, w.LPCWSTR, w.DWORD, ctypes.c_int, ctypes.c_int,
        ctypes.c_int, ctypes.c_int, w.HWND, w.HMENU, w.HINSTANCE, w.LPVOID,
        w.DWORD, w.DWORD]
    CreateWindowInBandEx.restype = w.HWND
    HAVE_EX = True
except AttributeError:
    HAVE_EX = False

WS_POPUP = 0x80000000
WS_EX_TOOLWINDOW = 0x00000080


def main():
    print(f"CreateWindowInBand={'OK' if HAVE else 'NO'}  "
          f"CreateWindowInBandEx={'OK' if HAVE_EX else 'NO'}")

    hinst = kernel32.GetModuleHandleW(None)
    wc = WNDCLASSW()
    wc.lpfnWndProc = ClassWndProc
    wc.hInstance = hinst
    wc.lpszClassName = "ProbeBandClass"
    atom = user32.RegisterClassW(ctypes.byref(wc))
    print(f"RegisterClass atom={atom}\n")

    for band in (0, 1, 2, 3, 4, 6, 8, 10, 11, 12, 16, 17):
        ctypes.set_last_error(0)
        h = CreateWindowInBand(
            WS_EX_TOOLWINDOW, atom, f"probe{band}", WS_POPUP,
            0, 0, 200, 60, None, None, hinst, None, band)
        err = ctypes.get_last_error()
        name = ZBID.get(band, str(band))
        print(f"band {band:>2} {name:<28} hwnd={h or 0:<10} err={err}"
              f"  {'✔ 可以' if h else '✘ 拒绝'}")
        if h:
            user32.DestroyWindow(h)

    # DestroyWindow 需要原型
    user32.DestroyWindow.argtypes = [w.HWND]


if __name__ == "__main__":
    main()
