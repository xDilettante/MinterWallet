# The wallet's icon, drawn from its shape (the logo of the interface: a white M on the accent-blue rounded square) with
# the standard library only: src/MinterWallet/Assets/app.ico (16-256 px, PNG entries) for the exe and the windows,
# packaging/minter-wallet.png (256 px) for the AppImage and the .deb. Run: py tools/make-icons.py
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ACCENT = (0x2F, 0x6F, 0xE0)
RADIUS = 0.22
M = [(0.24, 0.75), (0.24, 0.26), (0.34, 0.26), (0.5, 0.5), (0.66, 0.26), (0.76, 0.26), (0.76, 0.75), (0.66, 0.75),
     (0.66, 0.43), (0.535, 0.62), (0.465, 0.62), (0.34, 0.43), (0.34, 0.75)]


def in_square(u, v):
    cu = min(max(u, RADIUS), 1 - RADIUS)
    cv = min(max(v, RADIUS), 1 - RADIUS)
    return (u - cu) ** 2 + (v - cv) ** 2 <= RADIUS ** 2


def in_m(u, v):
    inside = False
    j = len(M) - 1
    for i in range(len(M)):
        (xi, yi), (xj, yj) = M[i], M[j]
        if (yi > v) != (yj > v) and u < (xj - xi) * (v - yi) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


def render(size, ss=4):
    rows = bytearray()
    for y in range(size):
        rows.append(0)
        for x in range(size):
            cover = white = 0
            for sy in range(ss):
                for sx in range(ss):
                    u = (x + (sx + 0.5) / ss) / size
                    v = (y + (sy + 0.5) / ss) / size
                    if in_square(u, v):
                        cover += 1
                        if in_m(u, v):
                            white += 1
            share = white / cover if cover else 0
            rows += bytes(round(c + (255 - c) * share) for c in ACCENT) + bytes([round(255 * cover / (ss * ss))])
    return png(size, rows)


def png(size, rows):
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xFFFFFFFF)
    header = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', header) + chunk(b'IDAT', zlib.compress(bytes(rows), 9)) + chunk(b'IEND', b'')


def ico(images):
    head = struct.pack('<HHH', 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = data = b''
    for size, image in images:
        entries += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(image), offset + len(data))
        data += image
    return head + entries + data


images = [(s, render(s)) for s in (16, 24, 32, 48, 64, 128, 256)]
os.makedirs(os.path.join(ROOT, 'src', 'MinterWallet', 'Assets'), exist_ok=True)
os.makedirs(os.path.join(ROOT, 'packaging'), exist_ok=True)
with open(os.path.join(ROOT, 'src', 'MinterWallet', 'Assets', 'app.ico'), 'wb') as f:
    f.write(ico(images))
with open(os.path.join(ROOT, 'packaging', 'minter-wallet.png'), 'wb') as f:
    f.write(images[-1][1])
print('icons written:', ', '.join(str(s) for s, _ in images))
