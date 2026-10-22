"""PNG 体积分析与无损/近无损压缩（纯标准库，不依赖 Pillow）。

两种用法：
  python tools/png8.py stat  <目录>          # 统计尺寸/格式/颜色数/体积
  python tools/png8.py pack  <源目录> <目标目录>  # 转成 PNG8（颜色数 ≤256 时无损）

为什么自己做：这些引擎图标以 Resource 形式**原样嵌进程序集**，
exe 又是未压缩的单文件，所以图标的字节数 = exe 的体积。297 张 64×64 的
RGBA PNG 本来要 1.7 MB，而它们绝大多数是扁平图标 —— 用调色板存只要零头。
"""

import glob
import os
import struct
import sys
import zlib
from collections import Counter


def decode(path):
    data = open(path, "rb").read()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", path
    pos, idat, pal, trns = 8, b"", None, None
    w = h = bd = ct = None
    while pos < len(data):
        ln = struct.unpack(">I", data[pos:pos + 4])[0]
        typ = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + ln]
        pos += 12 + ln
        if typ == b"IHDR":
            w, h, bd, ct = struct.unpack(">IIBB", chunk[:10])
            interlace = chunk[12]
            assert interlace == 0, "不支持隔行 PNG"
        elif typ == b"PLTE":
            pal = chunk
        elif typ == b"tRNS":
            trns = chunk
        elif typ == b"IDAT":
            idat += chunk
        elif typ == b"IEND":
            break

    assert bd == 8, f"只支持 8 位，实际 {bd}"
    nch = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ct]
    raw = zlib.decompress(idat)
    stride = w * nch
    out = bytearray()
    prev = bytearray(stride)
    p = 0
    for _ in range(h):
        f = raw[p]
        p += 1
        line = bytearray(raw[p:p + stride])
        p += stride
        if f == 1:
            for i in range(nch, stride):
                line[i] = (line[i] + line[i - nch]) & 255
        elif f == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 255
        elif f == 3:
            for i in range(stride):
                a = line[i - nch] if i >= nch else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 255
        elif f == 4:
            for i in range(stride):
                a = line[i - nch] if i >= nch else 0
                b = prev[i]
                c = prev[i - nch] if i >= nch else 0
                pp = a + b - c
                pa, pb, pc = abs(pp - a), abs(pp - b), abs(pp - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 255
        out += line
        prev = line
    return w, h, ct, nch, bytes(out), pal, trns


def to_rgba(w, h, ct, nch, px, pal, trns):
    """展开成 RGBA，便于统一统计颜色与重编码。"""
    out = bytearray()
    if ct == 6:
        return bytearray(px)
    if ct == 2:
        for i in range(0, len(px), 3):
            out += px[i:i + 3] + b"\xff"
        return out
    if ct == 0:
        for i in range(len(px)):
            out += bytes((px[i],) * 3) + b"\xff"
        return out
    if ct == 4:
        for i in range(0, len(px), 2):
            out += bytes((px[i],) * 3) + px[i + 1:i + 2]
        return out
    # ct == 3：调色板
    alpha = trns or b""
    for i in range(len(px)):
        idx = px[i]
        r, g, b = pal[idx * 3:idx * 3 + 3]
        a = alpha[idx] if idx < len(alpha) else 255
        out += bytes((r, g, b, a))
    return out


def encode_png8(path, w, h, rgba):
    """RGBA → PNG8（color type 3，带 tRNS），256 色以内无损。"""
    colors = Counter()
    for i in range(0, len(rgba), 4):
        colors[bytes(rgba[i:i + 4])] += 1
    assert len(colors) <= 256, "颜色超过 256，需要量化"

    palette = list(colors.keys())
    index = {c: i for i, c in enumerate(palette)}
    pal_bytes = b"".join(c[:3] for c in palette)
    # tRNS 只需写到最后一个非不透明色的位置
    alpha_list = [c[3] for c in palette]
    last = max((i for i, a in enumerate(alpha_list) if a != 255), default=-1)
    trns = bytes(alpha_list[:last + 1]) if last >= 0 else b""

    raw = bytearray()
    for y in range(h):
        raw.append(0)                      # filter: None（调色板图用 None 最省）
        base = y * w * 4
        raw += bytes(index[bytes(rgba[base + x * 4:base + x * 4 + 4])] for x in range(w))

    def chunk(typ, payload):
        return (struct.pack(">I", len(payload)) + typ + payload
                + struct.pack(">I", zlib.crc32(typ + payload) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 3, 0, 0, 0)
    body = (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", ihdr)
            + chunk(b"PLTE", pal_bytes)
            + (chunk(b"tRNS", trns) if trns else b"")
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))
    open(path, "wb").write(body)
    return len(body)


def main():
    mode = sys.argv[1]
    src = sys.argv[2]

    if mode == "stat":
        files = sorted(glob.glob(os.path.join(src, "*.png")))
        total = 0
        hist = Counter()
        for f in files:
            w, h, ct, nch, px, pal, trns = decode(f)
            rgba = to_rgba(w, h, ct, nch, px, pal, trns)
            n = len({bytes(rgba[i:i + 4]) for i in range(0, len(rgba), 4)})
            size = os.path.getsize(f)
            total += size
            hist["<=256色" if n <= 256 else ">256色"] += 1
        print(f"{len(files)} 张，共 {total} 字节（{total/1024:.0f} KB）")
        for k, v in hist.items():
            print(f"  {k}: {v} 张")
        return

    dst = sys.argv[3]
    os.makedirs(dst, exist_ok=True)
    files = sorted(glob.glob(os.path.join(src, "*.png")))
    before = after = 0
    skipped = 0
    for f in files:
        w, h, ct, nch, px, pal, trns = decode(f)
        rgba = to_rgba(w, h, ct, nch, px, pal, trns)
        n = len({bytes(rgba[i:i + 4]) for i in range(0, len(rgba), 4)})
        out = os.path.join(dst, os.path.basename(f))
        b0 = os.path.getsize(f)
        before += b0
        if n > 256:
            open(out, "wb").write(open(f, "rb").read())   # 原样拷贝
            after += b0
            skipped += 1
            continue
        b1 = encode_png8(out, w, h, rgba)
        after += b1
    print(f"{len(files)} 张：{before} → {after} 字节 "
          f"（省 {before-after} = {(before-after)/max(before,1)*100:.0f}%），"
          f"跳过量化的 {skipped} 张")


if __name__ == "__main__":
    main()
