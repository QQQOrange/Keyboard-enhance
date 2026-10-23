"""实验：普通（非 UIAccess）进程能不能把自己的窗口放进外壳的 band。

结论先写在注释里：能，但只能放进 **band ≤ 2**。
band ≥ 4（开始菜单 / 搜索在 6）一律 ACCESS_DENIED，
除非进程带 UIAccess 令牌。

用法：
    python tools/probe_band_set.py
"""

import ctypes
import ctypes.wintypes as w
import os
import subprocess
import sys
import time

sys.path.insert(0, ".")
from probe_band import (  # noqa: E402
    ZBID, band_of, collect, text_of, class_of, user32,
)

SetWindowBand = user32.SetWindowBand
SetWindowBand.argtypes = [w.HWND, w.HWND, w.DWORD]
SetWindowBand.restype = w.BOOL

HWND_TOP = 0
HWND_TOPMOST = -1


def wait_window(pid, timeout=15.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        for r in collect():
            if r["pid"] == pid:
                return r
        time.sleep(0.2)
    return None


def try_band(hwnd, band, insert_after=HWND_TOP):
    ctypes.set_last_error(0)
    ok = SetWindowBand(hwnd, insert_after, band)
    err = ctypes.get_last_error()
    got = band_of(hwnd)
    return ok, err, got


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    exe = os.path.join(root, "dist", "KeyboardEnhance.exe")

    env = dict(os.environ)
    env["CAPSLOCKPLUS_DATA"] = os.path.join(root, ".probeband")
    env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = "-probe-band"

    print(f"启动 {exe} --osd 50 --osd-hold 120000")
    p = subprocess.Popen([exe, "--osd", "50", "--osd-hold", "120000"], env=env)
    try:
        win = wait_window(p.pid)
        if not win:
            print("没找到窗口")
            return
        hwnd = win["hwnd"]
        print(f"窗口 hwnd={hwnd} class={win['cls']!r} title={text_of(hwnd)!r}")
        print(f"初始 band = {band_of(hwnd)}  topmost={win['topmost']}\n")

        for band in (0, 1, 2, 3, 4, 6, 8, 11, 12, 16, 17):
            ok, err, got = try_band(hwnd, band)
            name = ZBID.get(band, str(band))
            print(f"SetWindowBand({band:>2} {name:<26}) -> ok={bool(ok)} "
                  f"err={err:<6} 实际band={got}")

        # 回到 0（默认）
        print("\n恢复 band 0：", try_band(hwnd, 0))

        # 试 HWND_TOPMOST 作为 insertAfter 的 band 2
        print("band 2 + HWND_TOPMOST：", try_band(hwnd, 2, HWND_TOPMOST))
    finally:
        p.terminate()
        try:
            p.wait(timeout=5)
        except Exception:
            p.kill()


if __name__ == "__main__":
    main()
