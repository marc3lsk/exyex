"""Generates src/Mavyex/Assets/mavyex.ico (same design as the Rust version's icon_draw.rs: a blue rounded square
with two white bars – "two panes", the right one dimmer). Run:  python -I tools/make_icon.py"""
import os, struct, zlib

BG = (76, 139, 245)
SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SS = 4


def inside(x, y, x0, y0, x1, y1, r):
    if x < x0 or x > x1 or y < y0 or y > y1:
        return False
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def render(size):
    out = bytearray()
    k = 0.7
    dim = tuple(BG[i] + (255 - BG[i]) * k for i in range(3))
    for py in range(size):
        out.append(0)
        for px in range(size):
            r = g = b = n = 0.0
            for sy in range(SS):
                for sx in range(SS):
                    x = (px + (sx + 0.5) / SS) / size
                    y = (py + (sy + 0.5) / SS) / size
                    if not inside(x, y, 0.03, 0.03, 0.97, 0.97, 0.22):
                        continue
                    c = BG
                    if inside(x, y, 0.20, 0.22, 0.46, 0.78, 0.06):
                        c = (255, 255, 255)
                    elif inside(x, y, 0.54, 0.22, 0.80, 0.78, 0.06):
                        c = dim
                    r += c[0]; g += c[1]; b += c[2]; n += 1
            out += bytes((round(r / n), round(g / n), round(b / n), round(255 * n / (SS * SS)))) if n else b'\0\0\0\0'
    return bytes(out)


def png(size, raw):
    def chunk(kind, data):
        body = kind + data
        return struct.pack('>I', len(data)) + body + struct.pack('>I', zlib.crc32(body) & 0xFFFFFFFF)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))


def ico(sizes):
    images = [png(s, render(s)) for s in sizes]
    head = struct.pack('<HHH', 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b''
    for s, img in zip(sizes, images):
        d = 0 if s >= 256 else s
        entries += struct.pack('<BBBBHHII', d, d, 0, 0, 1, 32, len(img), offset)
        offset += len(img)
    return head + entries + b''.join(images)


if __name__ == '__main__':
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    dest = os.path.join(root, 'src', 'Mavyex', 'Assets', 'mavyex.ico')
    data = ico(SIZES)
    open(dest, 'wb').write(data)
    print(f'wrote {dest} ({len(data)} bytes)')
