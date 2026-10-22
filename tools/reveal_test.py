"""验证「打开文件夹后窗口是不是在前台」。

做法：调程序的 --reveal 诊断入口（它内部用真实的 OpenFolderInForeground），
然后看系统的前台窗口是不是资源管理器。

为了不打扰用户：
  · 测试前记录前台窗口与所有资源管理器窗口的标题；
  · 测完把前台还给原来的窗口；
  · 如果有资源管理器窗口被"复用导航"走了，用原路径打开一次把它导航回来。

用法：python tools/reveal_test.py <exe> <要打开的文件夹>
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

u32 = ctypes.WinDLL("user32", use_last_error=True)
u32.SetProcessDPIAware()

EXE = os.path.abspath(sys.argv[1])
TARGET = sys.argv[2]

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def win_list():
    """[(hwnd, class, title)] —— 只要可见且有标题的顶层窗口。"""
    out = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(h, _):
        if not u32.IsWindowVisible(h):
            return True
        n = u32.GetWindowTextLengthW(h)
        if n:
            t = ctypes.create_unicode_buffer(n + 1)
            u32.GetWindowTextW(h, t, n + 1)
            c = ctypes.create_unicode_buffer(256)
            u32.GetClassNameW(h, c, 256)
            out.append((h, c.value, t.value))
        return True

    u32.EnumWindows(cb, 0)
    return out


def title_of(hwnd):
    n = u32.GetWindowTextLengthW(hwnd)
    b = ctypes.create_unicode_buffer(n + 1)
    u32.GetWindowTextW(hwnd, b, n + 1)
    return b.value


def focus(hwnd):
    """用 AttachThreadInput 抢前台（和程序里 ForceForeground 同思路）。"""
    if not hwnd:
        return False
    u32.ShowWindow(hwnd, 9)          # SW_RESTORE
    if u32.SetForegroundWindow(hwnd):
        return True
    fg = u32.GetForegroundWindow()
    fg_tid = u32.GetWindowThreadProcessId(fg, None)
    cur = ctypes.windll.kernel32.GetCurrentThreadId()
    attached = False
    try:
        if fg_tid and fg_tid != cur:
            attached = u32.AttachThreadInput(cur, fg_tid, True)
        u32.BringWindowToTop(hwnd)
        ok = bool(u32.SetForegroundWindow(hwnd))
        return ok
    finally:
        if attached:
            u32.AttachThreadInput(cur, fg_tid, False)


def main():
    before_wins = win_list()
    old_fg = u32.GetForegroundWindow()
    old_fg_title = title_of(old_fg)
    explorer_before = {h: t for h, c, t in before_wins if c == "CabinetWClass"}
    print(f"测试前前台：{old_fg_title[:50]}")
    print(f"测试前资源管理器窗口：{list(explorer_before.values())}")

    data = os.path.join(ROOT, "out-reveal")
    os.makedirs(data, exist_ok=True)
    env = dict(os.environ)
    env["CAPSLOCKPLUS_DATA"] = data
    env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = "_reveal"

    p = subprocess.Popen([EXE, "--reveal", TARGET], env=env)
    p.wait(timeout=30)
    time.sleep(0.6)

    report = os.path.join(data, "reveal.txt")
    print("\n--- 程序自己的报告 ---")
    if os.path.exists(report):
        print(open(report, encoding="utf-8", errors="replace").read().strip())
    else:
        print("(没有生成 reveal.txt)")

    # 独立再确认一次：此刻前台是谁
    fg_hwnd = u32.GetForegroundWindow()
    fg_title = title_of(fg_hwnd)
    cls = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(fg_hwnd, cls, 256)
    print(f"\n此刻前台：{fg_title[:50]} [{cls.value}]")

    leaf = os.path.basename(TARGET.rstrip("\\/"))
    ok = cls.value == "CabinetWClass" and leaf.lower() in fg_title.lower()
    print("判定：", "窗口已在前台 ✓" if ok else "没有在前台 ✗")

    # ---- 还原 ----
    time.sleep(0.3)
    if old_fg:
        focus(old_fg)
        time.sleep(0.4)
        print(f"已把前台还给：{title_of(old_fg)[:50]}")

    # 被复用导航走的窗口：用原路径打开一次导航回去
    for h, old_title in explorer_before.items():
        if not u32.IsWindow(h):
            continue
        now = title_of(h)
        if now == old_title:
            continue
        # 标题形如 "C:\xxx - 文件资源管理器"，取回路径部分
        path = old_title.split(" - ")[0]
        if path and os.path.exists(path):
            print(f"还原被导航的窗口：{path}")
            subprocess.Popen(["explorer.exe", path])
            time.sleep(1.0)

    return 0 if ok else 2


if __name__ == "__main__":
    sys.exit(main())
