"""Render a Vintage Story world map database (Maps/<world id>.db) to a PNG.

    python mapdb2png.py <map.db> <out.png> [--scale 4] [--mid 512000]

Each row is one chunk column: position = the chunk index, data = MapPieceDB
(protobuf field 1, int32 written unpacked: 32x32 pixels, ABGR). Scale is how many
blocks one output pixel covers (a power of two up to 32). Prints the HUD
bounds it found.
"""
import argparse, sqlite3
import numpy as np
from PIL import Image

ap = argparse.ArgumentParser()
ap.add_argument("db"); ap.add_argument("out")
ap.add_argument("--scale", type=int, default=4)
ap.add_argument("--mid", type=int, default=512000)
a = ap.parse_args()


def pixels(data):
    # protobuf-net writes the int[] unpacked: tag 0x08 then a varint, per pixel
    out = []
    i, n = 0, len(data)
    while i < n:
        key = data[i]; i += 1
        if key != 0x08:
            return None
        v = 0; shift = 0
        while True:
            b = data[i]; i += 1
            v |= (b & 0x7F) << shift
            if not b & 0x80: break
            shift += 7
        out.append(v & 0xFFFFFFFF)
    return out


db = sqlite3.connect("file:" + a.db.replace("\\", "/") + "?mode=ro", uri=True)
rows = db.execute("select position, data from mappiece").fetchall()
# the chunk index: FastVec2i.ToChunkIndex = (z << 27) | x
pos = np.array([r[0] for r in rows], dtype=np.int64)
cxs, czs = pos & ((1 << 27) - 1), pos >> 27
cx0, cz0 = int(cxs.min()), int(czs.min())
W, H = int(cxs.max()) - cx0 + 1, int(czs.max()) - cz0 + 1
s = a.scale
img = np.zeros((H * 32 // s, W * 32 // s, 3), np.uint8)
for (p, data), cx, cz in zip(rows, cxs, czs):
    px = pixels(bytes(data))
    if not px or len(px) != 1024: continue
    arr = np.array(px, dtype=np.uint32).reshape(32, 32)
    rgb = np.stack([arr & 0xFF, (arr >> 8) & 0xFF, (arr >> 16) & 0xFF], -1).astype(np.uint8)
    rgb = rgb[::s, ::s]
    y, x = (int(cz) - cz0) * 32 // s, (int(cx) - cx0) * 32 // s
    img[y:y + rgb.shape[0], x:x + rgb.shape[1]] = rgb
Image.fromarray(img).save(a.out)
print(f"{len(rows)} pieces, {W} x {H} chunks, HUD x {cx0 * 32 - a.mid}..{(cx0 + W) * 32 - a.mid}, z {cz0 * 32 - a.mid}..{(cz0 + H) * 32 - a.mid}, image {img.shape[1]} x {img.shape[0]}")
