"""生成 CapsLock+ 图形版的应用图标（多尺寸 ICO + PNG）。"""
from PIL import Image, ImageDraw, ImageFilter
import os

OUT_DIR = os.path.join(os.path.dirname(__file__), "..", "src", "CapsLockPlus", "Assets")
os.makedirs(OUT_DIR, exist_ok=True)

BASE = 1024


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def make_base():
    img = Image.new("RGBA", (BASE, BASE), (0, 0, 0, 0))
    # 渐变底
    grad = Image.new("RGB", (BASE, BASE))
    gd = ImageDraw.Draw(grad)
    c1 = (43, 108, 246)     # #2B6CF6
    c2 = (108, 76, 246)     # #6C4CF6
    c3 = (34, 211, 238)     # #22D3EE
    for y in range(BASE):
        t = y / (BASE - 1)
        if t < 0.55:
            col = lerp(c1, c2, t / 0.55)
        else:
            col = lerp(c2, c3, (t - 0.55) / 0.45)
        gd.line([(0, y), (BASE, y)], fill=col)

    mask = rounded_mask(BASE, int(BASE * 0.235))
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)

    # 顶部高光
    hl = Image.new("RGBA", (BASE, BASE), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hl)
    hd.ellipse([-BASE * 0.35, -BASE * 0.75, BASE * 1.35, BASE * 0.42], fill=(255, 255, 255, 42))
    hl = hl.filter(ImageFilter.GaussianBlur(BASE * 0.05))
    img = Image.alpha_composite(img, Image.composite(hl, Image.new("RGBA", (BASE, BASE), (0, 0, 0, 0)), mask))
    d = ImageDraw.Draw(img)

    # 键帽
    kx, ky = BASE * 0.17, BASE * 0.20
    kw, kh = BASE * 0.66, BASE * 0.60
    shadow = Image.new("RGBA", (BASE, BASE), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shadow)
    sd.rounded_rectangle([kx, ky + BASE * 0.035, kx + kw, ky + kh + BASE * 0.035],
                         radius=BASE * 0.12, fill=(10, 16, 40, 90))
    shadow = shadow.filter(ImageFilter.GaussianBlur(BASE * 0.022))
    img = Image.alpha_composite(img, shadow)
    d = ImageDraw.Draw(img)

    d.rounded_rectangle([kx, ky, kx + kw, ky + kh], radius=BASE * 0.12,
                        fill=(255, 255, 255, 246))
    # 键帽顶部一条淡色渐变
    top = Image.new("RGBA", (BASE, BASE), (0, 0, 0, 0))
    td = ImageDraw.Draw(top)
    td.rounded_rectangle([kx + BASE * 0.012, ky + BASE * 0.012, kx + kw - BASE * 0.012, ky + kh * 0.55],
                         radius=BASE * 0.11, fill=(255, 255, 255, 255))
    top = top.filter(ImageFilter.GaussianBlur(BASE * 0.006))
    img = Image.alpha_composite(img, top)
    d = ImageDraw.Draw(img)

    # CapsLock 图标：向上的箭头 + 底部横杠
    cx = BASE * 0.5
    arrow_top = ky + kh * 0.20
    arrow_bottom = ky + kh * 0.62
    half = kw * 0.19
    d.polygon([(cx, arrow_top), (cx + half, arrow_top + kh * 0.24), (cx - half, arrow_top + kh * 0.24)],
              fill=(43, 108, 246, 255))
    bar_w = kw * 0.13
    d.rounded_rectangle([cx - bar_w / 2, arrow_top + kh * 0.26, cx + bar_w / 2, arrow_bottom],
                        radius=bar_w * 0.28, fill=(43, 108, 246, 255))
    d.rounded_rectangle([cx - kw * 0.20, arrow_bottom + kh * 0.07, cx + kw * 0.20, arrow_bottom + kh * 0.15],
                        radius=kh * 0.04, fill=(43, 108, 246, 255))

    return img


def write_ico(path, images):
    """手写 ICO：每个尺寸用 PNG 编码（Vista 之后都支持，体积小、带 alpha）。"""
    import struct
    blobs = []
    for im in images:
        from io import BytesIO
        buf = BytesIO()
        im.save(buf, format="PNG")
        blobs.append((im.width, im.height, buf.getvalue()))
    header = struct.pack("<HHH", 0, 1, len(blobs))
    offset = 6 + 16 * len(blobs)
    entries = b""
    data = b""
    for w, h, blob in blobs:
        entries += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
        data += blob
    with open(path, "wb") as f:
        f.write(header + entries + data)


def main():
    base = make_base()
    png_path = os.path.join(OUT_DIR, "app.png")
    base.resize((512, 512), Image.LANCZOS).save(png_path, "PNG")

    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    frames = [base.resize((s, s), Image.LANCZOS) for s in sizes]
    ico_path = os.path.join(OUT_DIR, "app.ico")
    write_ico(ico_path, frames)
    print("written", ico_path, os.path.getsize(ico_path), "bytes")
    print("written", png_path)


if __name__ == "__main__":
    main()
