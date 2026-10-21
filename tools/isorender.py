"""Isometric render of an .lmd block dump (from /gendumparea or dumpgen).

    python iso.py <file.lmd> <out.png> [--cut x|z|none] [--from ne|nw|se|sw]
                  [--ymin N] [--ymax N] [--scale S] [--noplants] [--nowater]
                  [--crop dx,dz,r] [--only metal,chain]

--cut x keeps only the half with x <= centre (a cutaway that shows inside);
--ymin/--ymax keep a height band (absolute y); --crop is relative to the
dump centre; --only keeps blocks whose code contains one of the words. Colours come from the
repo's viewer/blockcolors.json, shaded per face (top bright, sides darker).
"""
import argparse, gzip, json, os, struct
import numpy as np
from PIL import Image

ap = argparse.ArgumentParser()
ap.add_argument("lmd"); ap.add_argument("out")
ap.add_argument("--cut", default="none"); ap.add_argument("--from", dest="view", default="se")
ap.add_argument("--ymin", type=int, default=-1); ap.add_argument("--ymax", type=int, default=9999)
ap.add_argument("--scale", type=float, default=2.0)
ap.add_argument("--noplants", action="store_true"); ap.add_argument("--nowater", action="store_true")
ap.add_argument("--crop", default="")
ap.add_argument("--only", default="")
a = ap.parse_args()

HERE = os.path.dirname(os.path.abspath(__file__))
colors = json.load(open(os.path.join(HERE, "..", "viewer", "blockcolors.json"), encoding="utf-8"))
colors.update({"game:rockpolished-andesite": [168, 170, 172], "game:metalblock-new-plain-iron": [120, 124, 132],
    "game:crackedstonebricks-andesite": [128, 132, 136], "game:rockpolished-basalt": [62, 62, 66],
    "game:rockpolished-obsidian": [28, 24, 34], "game:rock-obsidian": [36, 32, 42], "game:drock": [70, 40, 40]})

raw = gzip.open(a.lmd).read()
hl = struct.unpack("<i", raw[4:8])[0]
h = json.loads(raw[8:8 + hl]); pal = h["palette"]
runs = np.frombuffer(raw[8 + hl:], dtype=np.dtype([("c", "<u4"), ("i", "<u2")]))
vol = np.repeat(runs["i"], runs["c"]).reshape(h["sy"], h["sz"], h["sx"])   # y z x
oy = h["oy"]

PLANTS = ("leaves", "tallgrass", "flower", "fern", "mushroom", "bush", "cattail", "horsetail", "lily", "reed",
          "flax", "loose", "litter", "cowparsley", "forgetmenot", "cornflower", "catmint", "wildvine", "devgrowth", "crop-", "sapling")


def col(code):
    c = code.split("|")[0]
    if c == "air" or c.endswith(":air"): return None
    if a.only and not any(o in c for o in a.only.split(",")): return None
    if a.noplants and any(k in c for k in PLANTS): return None
    if "water" in c:
        return None if a.nowater else (50, 80, 170, 110)
    k = c if ":" in c else "game:" + c
    v = colors.get(k)
    if v is None:
        # nearest by prefix
        base = k
        while "-" in base and v is None:
            base = base.rsplit("-", 1)[0]
            v = next((colors[x] for x in colors if x.startswith(base + "-")), None)
    if v is None:
        if "clutter" in c: v = (150, 60, 40)
        elif "chest" in c: v = (240, 210, 40)
        elif "spawner" in c: v = (255, 0, 255)
        else: v = (200, 0, 200)
    v = list(v)[:3]
    return (v[0], v[1], v[2], 255)


cols = [col(p) for p in pal]
solid = np.array([c is not None for c in cols])
rgba = np.array([c if c is not None else (0, 0, 0, 0) for c in cols], dtype=np.float32)

s = solid[vol]
sy, sz, sx = s.shape
ys = np.arange(sy)[:, None, None] + oy
if a.ymin >= 0: s &= ys >= a.ymin
s &= ys <= a.ymax
if a.crop:
    cx, cz, r = [int(v) for v in a.crop.split(",")]
    lx, lz = h["cx"] - h["ox"] + cx, h["cz"] - h["oz"] + cz   # offsets from the dump centre
    xs = np.arange(sx)[None, None, :]; zs = np.arange(sz)[None, :, None]
    s &= (abs(xs - lx) <= r) & (abs(zs - lz) <= r)
if a.cut == "x": s[:, :, sx // 2 + 1:] = False
if a.cut == "z": s[:, sz // 2 + 1:, :] = False

# view orientation: rotate so the camera always looks from +x,+z (se)
rot = {"se": 0, "sw": 1, "nw": 2, "ne": 3}[a.view]
s = np.rot90(s, k=rot, axes=(1, 2)); v2 = np.rot90(vol, k=rot, axes=(1, 2))
sy, sz, sx = s.shape

# exposed faces: top (+y), +x side, +z side
pad = np.pad(s, 1)
top = s & ~pad[2:, 1:-1, 1:-1]
fx = s & ~pad[1:-1, 1:-1, 2:]
fz = s & ~pad[1:-1, 2:, 1:-1]
vis = top | fx | fz
y, z, x = np.nonzero(vis)
ids = v2[y, z, x]
base = rgba[ids]
shade = np.where(top[y, z, x], 1.0, np.where(fx[y, z, x], 0.78, 0.6))
c = base[:, :3] * shade[:, None]

S = a.scale
px = ((x - z) * S + sz * S).astype(np.int32)
py = ((x + z) * S * 0.5 - y * S * 1.15 + sy * S * 1.15).astype(np.int32)
depth = x + z + y * 1.0
order = np.argsort(depth)
W = int((sx + sz) * S) + 4; H = int((sx + sz) * S * 0.5 + sy * S * 1.15) + 4
img = np.full((H, W, 3), (235, 238, 242), dtype=np.uint8)
blk = int(np.ceil(S)) + 1
for dy in range(blk + 1):
    for dx in range(blk):
        xx = np.clip(px[order] + dx, 0, W - 1); yy = np.clip(py[order] + dy, 0, H - 1)
        img[yy, xx] = c[order].astype(np.uint8)
im = Image.fromarray(img)
bb = Image.fromarray((img != np.array([235, 238, 242], dtype=np.uint8)).any(axis=2).astype(np.uint8) * 255).getbbox()
if bb: im = im.crop(bb)
im.save(a.out)
print(a.out, im.size, "voxels", len(order))
