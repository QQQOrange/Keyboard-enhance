"""探测：真实按键 vs Unicode 注入，在什么节奏下长文本不丢字/不错字符。

用修好的手法测（前台守卫 + 不在按住 Caps 的状态下打字 + 读回前充分等待），
避免把测试自身的问题当成程序的 bug。
"""
import ctypes
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from e2e_ime_typing import (  # noqa: E402
    u32, KEYBDINPUT, INPUT, INPUT_KEYBOARD, KEYEVENTF_KEYUP,
    hkl_with_lang, use_layout, ensure_focus,
    clip_get, clip_set, read_editor, clear_editor, start_notepad,
)

KEYEVENTF_UNICODE = 0x0004
TEXT = ("hello world 0123456789 abcdefghijklmnopqrstuvwxyz "
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ!@#$%^&*()")
TAG = 0x50524F42


def mk(vk, up, scan=0, flags=0):
    inp = INPUT(type=INPUT_KEYBOARD)
    inp.u.ki = KEYBDINPUT(wVk=0 if flags & KEYEVENTF_UNICODE else vk,
                          wScan=scan if flags & KEYEVENTF_UNICODE else 0,
                          dwFlags=flags | (KEYEVENTF_KEYUP if up else 0),
                          time=0, dwExtraInfo=TAG)
    return inp


def send(inputs, gap_after_ms=0):
    buf = (INPUT * len(inputs))(*inputs)
    u32.SendInput(len(inputs), buf, ctypes.sizeof(INPUT))
    if gap_after_ms:
        time.sleep(gap_after_ms / 1000.0)


def plan_of(text):
    out = []
    for ch in text:
        s = u32.VkKeyScanW(ord(ch))
        out.append((s & 0xFF, bool((s >> 8) & 0x01)))
    return out


def keys_batched(text, batch, gap):
    plan = plan_of(text)
    for start in range(0, len(plan), batch):
        arr = []
        for vk, shift in plan[start:start + batch]:
            if shift:
                arr.append(mk(0x10, False))
            arr += [mk(vk, False), mk(vk, True)]
            if shift:
                arr.append(mk(0x10, True))
        send(arr, gap)


def keys_paced(text, tap_ms, mod_ms):
    """逐字符，修饰键单独发并留间隔（避免 Shift 状态在翻译时已经变了）。"""
    for vk, shift in plan_of(text):
        if shift:
            send([mk(0x10, False)], mod_ms)
        send([mk(vk, False)])
        time.sleep(tap_ms / 1000.0)
        send([mk(vk, True)])
        if shift:
            send([mk(0x10, True)], mod_ms)


def unicode_batched(text, batch, gap):
    for start in range(0, len(text), batch):
        arr = []
        for ch in text[start:start + batch]:
            arr += [mk(0, False, ord(ch), KEYEVENTF_UNICODE), mk(0, True, ord(ch), KEYEVENTF_UNICODE)]
        send(arr, gap)


def unicode_paced(text, ch_ms):
    for ch in text:
        send([mk(0, False, ord(ch), KEYEVENTF_UNICODE), mk(0, True, ord(ch), KEYEVENTF_UNICODE)], ch_ms)


def main():
    np_proc, hwnd = start_notepad()
    if not hwnd:
        print("没找到记事本")
        return 1
    en = hkl_with_lang(0x0409)
    ensure_focus(hwnd)
    use_layout(hwnd, en)
    print(f"布局 0x{hkl_with_lang(0x0409) or 0:08X}（英文，无输入法参与）  文本 {len(TEXT)} 字\n")
    old = clip_get()
    try:
        cases = [
            ("按键-分批", lambda: keys_batched(TEXT, 32, 6)),
            ("按键-逐字 4ms", lambda: keys_paced(TEXT, 4, 3)),
            ("按键-逐字 10ms", lambda: keys_paced(TEXT, 10, 5)),
            ("Unicode-分批32", lambda: unicode_batched(TEXT, 32, 6)),
            ("Unicode-批8", lambda: unicode_batched(TEXT, 8, 8)),
            ("Unicode-逐字 2ms", lambda: unicode_paced(TEXT, 2)),
            ("Unicode-逐字 6ms", lambda: unicode_paced(TEXT, 6)),
        ]
        for label, fn in cases:
            oks, detail, last = 0, "", ""
            t0 = time.time()
            for _ in range(3):
                ensure_focus(hwnd)
                clear_editor(hwnd)
                time.sleep(0.2)
                t1 = time.time()
                fn()
                cost = (time.time() - t1) * 1000
                time.sleep(1.2)                  # 读回前充分等待
                last = read_editor(hwnd) or ""
                if last == TEXT:
                    oks += 1
                else:
                    detail = f"长度 {len(last)}/{len(TEXT)}"
            mark = "可靠" if oks == 3 else f"{oks}/3"
            print(f"  {label:<16} 发送 {cost:5.0f}ms  {mark:<5} {detail}")
            if oks < 3 and len(last) == len(TEXT):
                d = [(i, TEXT[i], last[i]) for i in range(len(TEXT)) if TEXT[i] != last[i]]
                print(f"     错字 {len(d)} 处：" + " ".join(f"[{i}]{a!r}→{b!r}" for i, a, b in d[:6]))
    finally:
        if old is not None:
            clip_set(old)
        np_proc.kill()
    return 0


if __name__ == "__main__":
    sys.exit(main())
