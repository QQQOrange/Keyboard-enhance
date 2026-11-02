# -*- coding: utf-8 -*-
"""用 curl 上传大资产（Python urllib 在这台机器上传大文件会被代理重置。

用法：
    python tools/upload-asset-curl.py <release_id> <文件> [资产名]

token 从 Windows 凭据管理器读，写进临时 header 文件给 curl 用，
不出现在命令行参数里；API 调用也全部走 curl（urllib 的 TLS 栈这里不可靠）。
"""
import ctypes
import ctypes.wintypes as w
import json
import os
import subprocess
import sys
import tempfile

REPO = "QQQOrange/Keyboard-enhance"


class CREDENTIAL(ctypes.Structure):
    _fields_ = [
        ("Flags", w.DWORD), ("Type", w.DWORD), ("TargetName", w.LPWSTR),
        ("Comment", w.LPWSTR), ("LastWritten", w.FILETIME),
        ("CredentialBlobSize", w.DWORD),
        ("CredentialBlob", ctypes.POINTER(ctypes.c_byte)),
        ("Persist", w.DWORD), ("AttributeCount", w.DWORD),
        ("Attributes", ctypes.c_void_p), ("TargetAlias", w.LPWSTR),
        ("UserName", w.LPWSTR),
    ]


def read_token():
    advapi = ctypes.WinDLL("advapi32", use_last_error=True)
    advapi.CredReadW.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, ctypes.POINTER(ctypes.POINTER(CREDENTIAL))]
    p = ctypes.POINTER(CREDENTIAL)()
    if not advapi.CredReadW("git:https://github.com", 1, 0, ctypes.byref(p)):
        raise RuntimeError("读不到 git 凭据")
    c = p.contents
    token = ctypes.string_at(c.CredentialBlob, c.CredentialBlobSize).decode("utf-16-le")
    advapi.CredFree(p)
    return token


def curl(args, hdr):
    cmd = ["curl.exe", "-sS", "-H", "@" + hdr] + args
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError(f"curl 失败 {r.returncode}: {r.stderr[:200]}")
    return r.stdout


def main():
    rid, path = sys.argv[1], sys.argv[2]
    name = sys.argv[3] if len(sys.argv) > 3 else os.path.basename(path)

    token = read_token()
    fd, hdr = tempfile.mkstemp(suffix=".hdr")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write("Authorization: Bearer " + token + "\n")
            f.write("Accept: application/vnd.github+json\n")
            f.write("User-Agent: CapsLockPlus-Release\n")

        # 同名旧资产先删掉（失败重传时留下的半截会挡住）
        listing = curl(["--max-time", "60",
                        f"https://api.github.com/repos/{REPO}/releases/{rid}/assets"], hdr)
        for a in json.loads(listing or "[]"):
            if a.get("name") == name:
                curl(["-X", "DELETE", "--max-time", "60",
                      f"https://api.github.com/repos/{REPO}/releases/assets/{a['id']}"], hdr)
                print("removed old asset", a["id"])

        url = (f"https://uploads.github.com/repos/{REPO}/releases/{rid}/assets"
               f"?name={name}")
        out = curl([
            "-X", "POST",
            "-H", "Content-Type: application/octet-stream",
            "--data-binary", "@" + path,
            "--retry", "6", "--retry-delay", "5", "--retry-all-errors",
            "--connect-timeout", "30",
            url,
        ], hdr)
        data = json.loads(out or "{}")
        if data.get("state") == "uploaded":
            print("uploaded:", data.get("name"), data.get("size"))
            print("url:", data.get("browser_download_url"))
            return 0
        print("FAILED:", out[:300])
        return 1
    finally:
        try:
            os.remove(hdr)
        except OSError:
            pass


if __name__ == "__main__":
    sys.exit(main())
