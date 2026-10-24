"""Low-memory views of a big .lmd dump: a shaded top-down map, and vertical
slices through the dump centre. Streams the volume one y layer at a time.

    python tools/lmdview.py <file.lmd> <outprefix> [--scale 1] [--slices x0,z0,x-40,z25]
                      [--count code1,code2] [--noplants]
Slice offsets are relative to the dump centre (x = a north-south slice at
that x offset, z = an east-west slice at that z offset).
"""
import argparse, gzip, json, os, struct
import numpy as np
from PIL import Image

ap = argparse.ArgumentParser()
ap.add_argument("lmd"); ap.add_argument("out")
ap.add_argument("--scale", type=int, default=1)
ap.add_argument("--slices", default="")
ap.add_argument("--count", default="")
ap.add_argument("--noplants", action="store_true")
a = ap.parse_args()

REPO = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
colors = json.load(open(os.path.join(REPO, "viewer", "blockcolors.json"), encoding="utf-8"))
extra = {"rockpolished-andesite": (168, 170, 172), "metalblock-new-plain-iron": (120, 124, 132), "crackedstonebricks-andesite": (128, 132, 136),
         "rockpolished-basalt": (62, 62, 66), "rockpolished-obsidian": (28, 24, 34), "rock-obsidian": (36, 32, 42), "drock": (92, 46, 40),
         "packedglacierice": (180, 215, 240), "glacierice": (200, 230, 250), "glass-blue": (40, 110, 180), "lava": (255, 90, 20),
         "devastatedsoil": (110, 60, 50), "ironfence": (60, 60, 64), "irondoor": (90, 90, 96), "stonebrickslab": (150, 150, 150),
         "metalblock-new-plain-steel": (175, 180, 188), "metalblock-new-riveted-steel": (150, 155, 165),
         "metalblock-corroded": (120, 72, 46), "rock-andesite": (120, 120, 122), "rock-basalt": (70, 70, 74),
         "ghostlight": (90, 170, 255), "beaconlight": (255, 255, 120), "machinecore": (255, 40, 40), "devgrowth": (140, 40, 60)}
PLANTS = ("leaves", "tallgrass", "flower", "fern", "mushroom", "bush", "cattail", "reed", "loose", "litter", "devgrowth")

raw = gzip.open(a.lmd).read()
hl = struct.unpack("<i", raw[4:8])[0]
h = json.loads(raw[8:8 + hl]); pal = h["palette"]
runs = np.frombuffer(raw[8 + hl:], dtype=np.dtype([("c", "<u4"), ("i", "<u2")]))
sy, sz, sx, oy = h["sy"], h["sz"], h["sx"], h["oy"]
sea = h.get("sea", 110)
cxo, czo = h.get("cx", 0) - h.get("ox", 0), h.get("cz", 0) - h.get("oz", 0)
if cxo <= 0 or cxo >= sx: cxo, czo = sx // 2, sz // 2
print("dump", sx, sz, sy, "oy", oy, "sea", sea, "centre", cxo, czo, "palette", len(pal))


def colour(code):
    c = code.split("|")[0]
    c0 = c.split(":")[-1]
    if c0 == "air": return None
    if a.noplants and any(k in c0 for k in PLANTS): return None
    if "water" in c0: return (40, 80, 170)
    for k, v in extra.items():
        if c0.startswith(k): return v
    v = colors.get(c if ":" in c else "game:" + c)
    base = "game:" + c0
    while v is None and "-" in base:
        base = base.rsplit("-", 1)[0]
        v = next((colors[x] for x in colors if x.startswith(base + "-")), None)
    if v is None:
        if "clutter" in c0: return (150, 60, 40)
        if "chest" in c0: return (240, 210, 40)
        return (200, 0, 200)
    return tuple(v[:3])


cols = [colour(p) for p in pal]
solid = np.array([c is not None for c in cols])
water = np.array(["water" in p for p in pal])
rgb = np.array([c if c is not None else (0, 0, 0) for c in cols], dtype=np.float32)
counts = {}
cwant = [w for w in a.count.split(",") if w]

L = sz * sx
ends = np.cumsum(runs["c"].astype(np.int64))
starts = ends - runs["c"]
topY = np.full(L, -1, dtype=np.int32)
topI = np.zeros(L, dtype=np.uint16)
slices = []
for s in [s for s in a.slices.split(",") if s]:
    ax, off = s[0], int(s[1:])
    slices.append((ax, off, np.zeros((sy, sz if ax == "x" else sx), dtype=np.uint16)))
for y in range(sy):
    s0, s1 = y * L, (y + 1) * L
    i0 = np.searchsorted(ends, s0, side="right")
    i1 = np.searchsorted(starts, s1, side="left")
    c = runs["c"][i0:i1].astype(np.int64).copy()
    c[0] -= s0 - starts[i0]
    c[-1] -= ends[i1 - 1] - s1
    layer = np.repeat(runs["i"][i0:i1], c)
    m = solid[layer]
    topY[m] = y
    topI[m] = layer[m]
    for w in cwant:
        idx = [k for k, p in enumerate(pal) if w in p]
        if idx: counts[w] = counts.get(w, 0) + int(np.isin(layer, idx).sum())
    l2 = layer.reshape(sz, sx)
    for ax, off, img in slices:
        if ax == "x": img[y] = l2[:, cxo + off]
        else: img[y] = l2[czo + off, :]

topY2 = topY.reshape(sz, sx).astype(np.float32)
base = rgb[topI].reshape(sz, sx, 3)
gx = np.gradient(np.where(topY2 < 0, 0, topY2), axis=1)
gz = np.gradient(np.where(topY2 < 0, 0, topY2), axis=0)
shade = np.clip(1.0 - 0.10 * gx - 0.10 * gz, 0.45, 1.4)
hb = np.clip(0.55 + (topY2 + oy - sea) / 160.0, 0.4, 1.2)
img = np.clip(base * shade[..., None] * hb[..., None], 0, 255).astype(np.uint8)
img[topY2 < 0] = (10, 20, 40)
im = Image.fromarray(img)
if a.scale != 1: im = im.resize((sx * a.scale, sz * a.scale), Image.NEAREST)
im.save(a.out + "_top.png")
print("wrote", a.out + "_top.png", im.size)
for ax, off, sl in slices:
    simg = rgb[sl][::-1].astype(np.uint8)
    simg[(~solid[sl])[::-1]] = (235, 238, 242)
    seal = sy - 1 - (sea - oy)
    if 0 <= seal < sy: simg[seal, ::4] = (255, 0, 0)
    sim = Image.fromarray(simg)
    sim.save(f"{a.out}_slice_{ax}{off}.png")
    print("wrote", f"{a.out}_slice_{ax}{off}.png", sim.size)
if counts: print("counts", counts)
np.save(a.out + "_topY.npy", (topY2 + oy).astype(np.int16))
