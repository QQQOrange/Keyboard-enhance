"""直接读显示器的 DDC/CI 亮度（0~100），不依赖本程序。

为什么需要它：判断"调亮度到底生效没有"，只能读**硬件**的值。
截图是不行的 —— 截图捕获的是 GPU 渲染结果，背光亮度根本不进画面
（我一开始就是用截图的平均亮度去判断，结果明明写进去了却看着"没变化"）。

用法：python tools/ddc.py
"""

import ctypes
from ctypes import wintypes as wt

u32 = ctypes.WinDLL("user32", use_last_error=True)
dxva2 = ctypes.WinDLL("dxva2", use_last_error=True)

VCP_BRIGHTNESS = 0x10


class PHYSICAL_MONITOR(ctypes.Structure):
    _fields_ = [("hPhysicalMonitor", ctypes.c_void_p),
                ("szPhysicalMonitorDescription", ctypes.c_wchar * 128)]


def _setup():
    u32.EnumDisplayMonitors.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                        ctypes.c_void_p, wt.LPARAM]
    u32.EnumDisplayMonitors.restype = wt.BOOL
    dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR.argtypes = [ctypes.c_void_p,
                                                              ctypes.POINTER(wt.DWORD)]
    dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR.restype = wt.BOOL
    dxva2.GetPhysicalMonitorsFromHMONITOR.argtypes = [ctypes.c_void_p, wt.DWORD,
                                                      ctypes.POINTER(PHYSICAL_MONITOR)]
    dxva2.GetPhysicalMonitorsFromHMONITOR.restype = wt.BOOL
    dxva2.GetVCPFeatureAndVCPFeatureReply.argtypes = [
        ctypes.c_void_p, wt.BYTE, ctypes.POINTER(wt.DWORD),
        ctypes.POINTER(wt.DWORD), ctypes.POINTER(wt.DWORD)]
    dxva2.GetVCPFeatureAndVCPFeatureReply.restype = wt.BOOL
    dxva2.SetVCPFeature.argtypes = [ctypes.c_void_p, wt.BYTE, wt.DWORD]
    dxva2.SetVCPFeature.restype = wt.BOOL
    dxva2.DestroyPhysicalMonitor.argtypes = [ctypes.c_void_p]
    dxva2.DestroyPhysicalMonitor.restype = wt.BOOL


_setup()

_MonitorProc = ctypes.WINFUNCTYPE(wt.BOOL, ctypes.c_void_p, ctypes.c_void_p,
                                  ctypes.c_void_p, wt.LPARAM)


def read():
    """返回 [(描述, 百分比)]；读不到时返回空列表。"""
    out = []

    def cb(hmon, hdc, rect, data):
        count = wt.DWORD()
        if not dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, ctypes.byref(count)):
            return True
        if count.value == 0:
            return True

        arr = (PHYSICAL_MONITOR * count.value)()
        if not dxva2.GetPhysicalMonitorsFromHMONITOR(hmon, count.value, arr):
            return True

        for i in range(count.value):
            m = arr[i]
            code = wt.DWORD()
            cur = wt.DWORD()
            mx = wt.DWORD()
            if dxva2.GetVCPFeatureAndVCPFeatureReply(
                    m.hPhysicalMonitor, VCP_BRIGHTNESS,
                    ctypes.byref(code), ctypes.byref(cur), ctypes.byref(mx)):
                if mx.value:
                    out.append((m.szPhysicalMonitorDescription,
                                round(cur.value * 100 / mx.value)))
            dxva2.DestroyPhysicalMonitor(m.hPhysicalMonitor)
        return True

    u32.EnumDisplayMonitors(None, None, _MonitorProc(cb), 0)
    return out


def set_percent(percent):
    """把第一台支持 DDC 的屏设为指定百分比；返回是否成功。"""
    done = []

    def cb(hmon, hdc, rect, data):
        count = wt.DWORD()
        if not dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, ctypes.byref(count)):
            return True
        if count.value == 0:
            return True
        arr = (PHYSICAL_MONITOR * count.value)()
        if not dxva2.GetPhysicalMonitorsFromHMONITOR(hmon, count.value, arr):
            return True
        for i in range(count.value):
            m = arr[i]
            code = wt.DWORD()
            cur = wt.DWORD()
            mx = wt.DWORD()
            if dxva2.GetVCPFeatureAndVCPFeatureReply(
                    m.hPhysicalMonitor, VCP_BRIGHTNESS,
                    ctypes.byref(code), ctypes.byref(cur), ctypes.byref(mx)) and mx.value:
                raw = round(max(0, min(100, percent)) * mx.value / 100)
                if dxva2.SetVCPFeature(m.hPhysicalMonitor, VCP_BRIGHTNESS, raw):
                    done.append(percent)
            dxva2.DestroyPhysicalMonitor(m.hPhysicalMonitor)
        return True

    u32.EnumDisplayMonitors(None, None, _MonitorProc(cb), 0)
    return bool(done)


if __name__ == "__main__":
    import sys

    if len(sys.argv) > 1:
        target = int(sys.argv[1])
        ok = set_percent(target)
        print(f"设为 {target}%：{'成功' if ok else '失败'}")
        for desc, pct in read():
            print(f"{desc}: {pct}%")
    else:
        res = read()
        if not res:
            print("读不到 DDC 亮度（可能没开 DDC/CI）")
        for desc, pct in res:
            print(f"{desc}: {pct}%")
