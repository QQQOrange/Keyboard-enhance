"""Qbar 点击外部是否消失 —— 真机验证。

测几种"点别处"：
  ① 点窗口内的**透明边距**（面板视觉边界之外，但仍属于窗口）—— 最容易漏；
  ② 点另一个窗口（记事本）—— 最典型的操作；
  ③ 点面板自己 —— 不该关（否则点不了结果）；
  ④ 点任务栏 —— 任务栏不换前台，Deactivated 不触发，只能靠全局鼠标钩子兜底；
  ⑤ 点"窗口内、面板外"的投影区；
  ⑥ 双击结果行 —— 同一个结果只允许执行一次（曾经的 bug：双击=执行两遍）。

用法：python tools/e2e_qbar_dismiss.py [exe]
"""

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time
from datetime import datetime

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.join(ROOT, "dist", "KeyboardEnhance.exe")

u32 = ctypes.WinDLL("user32", use_last_error=True)
u32.SetProcessDPIAware()

VK_CAPITAL, VK_Q = 0x14, 0x51
VK_CONTROL, VK_V = 0x11, 0x56
KEYEVENTF_KEYUP = 0x0002
INPUT_KEYBOARD, INPUT_MOUSE = 1, 0


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD),
                ("dwFlags", wt.DWORD), ("time", wt.DWORD),
                ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]


class _U(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("mi", MOUSEINPUT), ("pad", ctypes.c_byte * 32)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _U)]


def key(vk, up=False):
    inp = INPUT(type=INPUT_KEYBOARD)
    inp.u.ki = KEYBDINPUT(wVk=vk, wScan=0, dwFlags=KEYEVENTF_KEYUP if up else 0,
                          time=0, dwExtraInfo=None)
    u32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


def click(x, y):
    u32.SetCursorPos(int(x), int(y))
    time.sleep(0.12)
    # 左键按下 + 抬起
    down = INPUT(type=INPUT_MOUSE)
    down.u.mi = MOUSEINPUT(0, 0, 0, 0x0002, 0, None)
    up = INPUT(type=INPUT_MOUSE)
    up.u.mi = MOUSEINPUT(0, 0, 0, 0x0004, 0, None)
    u32.SendInput(1, ctypes.byref(down), ctypes.sizeof(INPUT))
    time.sleep(0.05)
    u32.SendInput(1, ctypes.byref(up), ctypes.sizeof(INPUT))


def double_click(x, y, gap=0.04):
    """原地快速点两下。gap 控制两击间隔：第二击的"抬起"必须落在
    面板 90ms 淡出还没结束的时间里，才能验到"淡出期间不再执行"的闸门。"""
    u32.SetCursorPos(int(x), int(y))
    time.sleep(0.15)
    for _ in range(2):
        down = INPUT(type=INPUT_MOUSE)
        down.u.mi = MOUSEINPUT(0, 0, 0, 0x0002, 0, None)
        up = INPUT(type=INPUT_MOUSE)
        up.u.mi = MOUSEINPUT(0, 0, 0, 0x0004, 0, None)
        u32.SendInput(1, ctypes.byref(down), ctypes.sizeof(INPUT))
        time.sleep(0.03)
        u32.SendInput(1, ctypes.byref(up), ctypes.sizeof(INPUT))
        time.sleep(gap)


def ctrl_v():
    key(VK_CONTROL); time.sleep(0.04)
    key(VK_V); time.sleep(0.04); key(VK_V, up=True); time.sleep(0.04)
    key(VK_CONTROL, up=True)


def set_clipboard_text(text):
    """把一段文本放到剪贴板（CF_UNICODETEXT）。"""
    k32 = ctypes.windll.kernel32
    k32.GlobalAlloc.restype = ctypes.c_void_p
    k32.GlobalLock.restype = ctypes.c_void_p
    k32.GlobalLock.argtypes = [ctypes.c_void_p]
    u32.SetClipboardData.restype = ctypes.c_void_p
    u32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]
    u32.OpenClipboard.argtypes = [wt.HWND]
    if not u32.OpenClipboard(None):
        return False
    try:
        u32.EmptyClipboard()
        data = text.encode("utf-16-le") + b"\x00\x00"
        h = k32.GlobalAlloc(0x0002, len(data))     # GMEM_MOVEABLE
        if not h:
            return False
        p = k32.GlobalLock(h)
        if not p:
            return False
        ctypes.memmove(p, data, len(data))
        k32.GlobalUnlock(h)
        return bool(u32.SetClipboardData(13, h))   # CF_UNICODETEXT
    finally:
        u32.CloseClipboard()


def notepad_pids():
    """当前所有记事本进程的 PID 集合。"""
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq notepad.exe", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True, errors="replace").stdout
    pids = set()
    for line in out.splitlines():
        parts = line.strip().strip('"').split('","')
        if parts and parts[0].lower() == "notepad.exe" and len(parts) >= 2:
            try:
                pids.add(int(parts[1]))
            except ValueError:
                pass
    return pids


def find_window(title):
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        n = u32.GetWindowTextLengthW(hwnd)
        if n:
            buf = ctypes.create_unicode_buffer(n + 1)
            u32.GetWindowTextW(hwnd, buf, n + 1)
            if buf.value == title and u32.IsWindowVisible(hwnd):
                found.append(hwnd)
        return True

    u32.EnumWindows(cb, 0)
    return found[0] if found else 0


def wait_window(title, timeout=6.0):
    end = time.time() + timeout
    while time.time() < end:
        h = find_window(title)
        if h:
            return h
        time.sleep(0.15)
    return 0


def rect(hwnd):
    r = wt.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(r))
    return r


def activate(hwnd):
    u32.ShowWindow(hwnd, 9)
    u32.SetForegroundWindow(hwnd)
    time.sleep(0.35)


def open_qbar(hnote):
    activate(hnote)
    key(VK_CAPITAL)
    time.sleep(0.30)
    key(VK_Q)
    key(VK_Q, up=True)
    time.sleep(0.15)
    key(VK_CAPITAL, up=True)
    return wait_window("Qbar", timeout=6.0)


def gone_after(hqbar, wait=3.0):
    end = time.time() + wait
    while time.time() < end:
        if not u32.IsWindowVisible(hqbar):
            return True
        time.sleep(0.15)
    return False


def main():
    stamp = datetime.now().strftime("%H%M%S")
    data = os.path.join(ROOT, f"out-qbar-{stamp}")
    os.makedirs(data, exist_ok=True)
    env = dict(os.environ)
    env["CAPSLOCKPLUS_DATA"] = data
    env["CAPSLOCKPLUS_MUTEX_SUFFIX"] = f"_qbar_{stamp}"

    note = subprocess.Popen(["notepad.exe"])
    app = subprocess.Popen([EXE, "--tray"], env=env)
    ok = True
    results = []
    try:
        hnote = wait_window("无标题 - 记事本") or wait_window("Untitled - Notepad") or u32.FindWindowW("Notepad", None)
        if not hnote:
            print("✗ 记事本没起来")
            return 1
        time.sleep(1.0)

        # ① 点窗口内的透明边距（离窗口左上角 6px，面板视觉边界在 18px 之外）
        h = open_qbar(hnote)
        if not h:
            print("✗ Qbar 没弹出，无法测试")
            return 1
        time.sleep(0.9)                       # 过掉抢焦点宽限期
        r = rect(h)
        print(f"① 点窗口透明边距 ({r.left + 6}, {r.top + 6})")
        click(r.left + 6, r.top + 6)
        first = gone_after(h)
        results.append(("点透明边距", first))
        print("   ✔ 已消失" if first else "   ✗ 仍在（透明边距拦住了点击）")

        # ② 点另一个窗口（记事本中部）
        if not first:
            key(0x1B); key(0x1B, up=True); time.sleep(0.6)
        h = open_qbar(hnote)
        if not h:
            print("✗ 第二次 Qbar 没弹出")
            return 1
        time.sleep(0.9)
        rn = rect(hnote)
        cx, cy = (rn.left + rn.right) // 2, (rn.top + rn.bottom) // 2
        print(f"② 点记事本中部 ({cx}, {cy})")
        click(cx, cy)
        second = gone_after(h)
        results.append(("点别的窗口", second))
        print("   ✔ 已消失" if second else "   ✗ 仍在")

        # ③ 点面板自己（不该关）
        h = open_qbar(hnote)
        if not h:
            print("✗ 第三次 Qbar 没弹出")
            return 1
        time.sleep(0.9)
        r = rect(h)
        # 点面板的**实心内部**（不是列表项：点结果本来就会执行并关闭）。
        # 注意左边 44px 只等于 22 DIP，正好落在面板 18 DIP 圆角之外的透明角 ——
        # 那一击会穿透到下层窗口，关掉面板是**正确**的。
        cx, cy = r.left + 200, r.top + 120
        print(f"③ 点面板自己的空白处 ({cx}, {cy})")
        click(cx, cy)
        time.sleep(1.2)
        third_visible = bool(u32.IsWindowVisible(h))
        results.append(("点面板自己（不该关）", third_visible))
        print("   ✔ 仍然显示" if third_visible else "   ✗ 被误关")

        # ④ 点任务栏 —— 任务栏通常不会把前台"换走"，最容易漏的一种
        if third_visible:
            key(0x1B); key(0x1B, up=True); time.sleep(0.6)
        h = open_qbar(hnote)
        if not h:
            print("✗ 第四次 Qbar 没弹出")
            return 1
        time.sleep(0.9)
        tray = u32.FindWindowW("Shell_TrayWnd", None)
        fourth = True
        if tray:
            rt = rect(tray)
            tx, ty = (rt.left + rt.right) // 2, (rt.top + rt.bottom) // 2
            print(f"④ 点任务栏 ({tx}, {ty})")
            click(tx, ty)
            fourth = gone_after(h)
            results.append(("点任务栏", fourth))
            print("   ✔ 已消失" if fourth else "   ✗ 仍在（任务栏不换前台）")
            if not fourth:
                key(0x1B); key(0x1B, up=True); time.sleep(0.5)
        else:
            print("④ 找不到任务栏，跳过")

        # ⑤ 点"窗口内、面板外"的投影区（左侧，避开 20 DIP 圆角）
        if not fourth:
            key(0x1B); key(0x1B, up=True); time.sleep(0.5)
        h = open_qbar(hnote)
        fifth = True
        if h:
            time.sleep(0.9)
            r = rect(h)
            px, py = r.left + 30, r.top + 200      # 约 15 DIP 处，投影的实心部分
            print(f"⑤ 点投影区（窗口内、面板外）({px}, {py})")
            click(px, py)
            fifth = gone_after(h)
            results.append(("点投影区（窗口内面板外）", fifth))
            print("   ✔ 已消失" if fifth else "   ✗ 仍在（投影拦住了点击）")
            if not fifth:
                key(0x1B); key(0x1B, up=True); time.sleep(0.5)

        # ⑥ 双击结果行只执行一次
        #    曾经的 bug：单击已经执行并收起面板，双击的第二击落在还没
        #    淡出完的面板上，会把同一个结果再执行一遍 —— 这里就会多起
        #    一个记事本。用进程数来观测，不依赖焦点和剪贴板内容。
        if not fifth:
            key(0x1B); key(0x1B, up=True); time.sleep(0.5)
        sixth = True
        if not set_clipboard_text("notepad"):
            print("⑥ 剪贴板设置失败，跳过")
        else:
            time.sleep(1.2)                     # 等面板的剪贴板监听收进去
            h = open_qbar(hnote)
            if not h:
                print("✗ 第六次 Qbar 没弹出")
                sixth = False
            else:
                time.sleep(0.9)
                ctrl_v()                        # 把关键字粘进输入框（默认绑定没占 Ctrl+V）
                time.sleep(1.2)                 # 等结果出来
                r = rect(h)
                before = notepad_pids()
                print(f"⑥ 双击第一行结果 ({r.left + 200}, {r.top + 250})")
                double_click(r.left + 200, r.top + 250)
                time.sleep(2.5)
                new = notepad_pids() - before
                sixth = len(new) == 1
                results.append(("双击结果行只执行一次", sixth))
                print(f"   新起记事本 {len(new)} 个：" +
                      ("✔ 正好一个" if sixth else "✗ 执行了多次或没执行"))
                for pid in new:                 # 清理，只留脚本自己起的那个
                    subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True)

        ok = first and second and third_visible and fourth and fifth and sixth
        if not third_visible:
            key(0x1B); key(0x1B, up=True)

    finally:
        for p in (app, note):
            try:
                p.terminate(); p.wait(timeout=6)
            except Exception:
                p.kill()

    print()
    for name, good in results:
        print(f"  {'✔' if good else '✗'} {name}")
    print()
    print("结论:", "点击外部会关闭、点面板自己不会、双击只执行一次" if ok else "有问题，见上面 ✗")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
