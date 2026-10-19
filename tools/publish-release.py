"""创建 GitHub Release 并上传 CapsLockPlus.exe。

凭据来自 Windows 凭据管理器（git push 时已在用），无需额外授权。
"""
import ctypes
import ctypes.wintypes as w
import json
import os
import sys
import time
import urllib.error
import urllib.request
import uuid

API = "https://api.github.com/repos/QQQOrange/capslock-plus-QQQOrange-"
TAG = os.environ.get("CLP_TAG", "v1.0.1")
NAME = os.environ.get("CLP_NAME", "CapsLock+ 图形版 " + TAG)


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


def main():
    exe = sys.argv[1]
    notes_path = sys.argv[2]
    token = read_token()
    hdr = {
        "Authorization": "Bearer " + token,
        "User-Agent": "CapsLockPlus-Release",
        "Accept": "application/vnd.github+json",
    }

    def call(url, method="GET", data=None, extra=None, raw=None, ctype=None):
        h = dict(hdr)
        if extra:
            h.update(extra)
        body = raw if raw is not None else (json.dumps(data).encode() if data is not None else None)
        req = urllib.request.Request(url, data=body, headers=h, method=method)
        try:
            with urllib.request.urlopen(req, timeout=180) as r:
                content = r.read()
                return r.status, (json.loads(content or b"{}") if ctype is None else content)
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode("utf-8", "ignore")[:500]

    # 1) 已存在就直接用，否则创建
    st, rel = call(API + "/releases/tags/" + TAG)
    if st == 200 and isinstance(rel, dict):
        print("release already exists:", rel["id"])
    else:
        notes = open(notes_path, encoding="utf-8").read()
        st, rel = call(API + "/releases", method="POST", data={
            "tag_name": TAG,
            "name": NAME,
            "body": notes,
            "draft": False,
            "prerelease": False,
        })
        print("create release:", st)
        if st not in (200, 201):
            print("FAILED:", rel)
            return 1

    rid = rel["id"]
    upload_url = rel["upload_url"].split("{")[0]

    # 2) 删除同名旧资产（便于重复发布）
    st, assets = call(API + f"/releases/{rid}/assets")
    if isinstance(assets, list):
        for a in assets:
            if a["name"] == "CapsLockPlus.exe":
                call(API + f"/releases/assets/{a['id']}", method="DELETE")
                print("removed old asset", a["id"])

    # 3) 上传
    data = open(exe, "rb").read()
    name = os.path.basename(exe)
    st, out = call(
        upload_url + "?name=" + name,
        method="POST",
        raw=data,
        extra={"Content-Type": "application/octet-stream",
               "Content-Length": str(len(data))},
    )
    print("upload:", st)
    if st in (200, 201):
        print("asset url:", out["browser_download_url"])
        print("asset size:", out["size"])
        return 0
    print("FAILED:", out)
    return 1


if __name__ == "__main__":
    sys.exit(main())
