"""Floor plans of an .lmd dump: what lies just under each given height,
seen from above, the way a plan of a building cuts through its walls.

    python tools/hslice.py <file.lmd> <outprefix> --y 10,20,45 [--depth 3] [--crop dx,dz,r] [--scale 1]

For each y, every column shows the first block found going down from y
through `depth` layers (darker the lower it is), black where all of them
are empty, dark grey for rock and soil so the rooms, pipes and machines
stand out. Writes <outprefix>_y<Y>.png.
"""
import argparse, gzip, json, os, struct
import numpy as np
from PIL import Image

ap = argparse.ArgumentParser()
ap.add_argument("lmd"); ap.add_argument("out")
ap.add_argument("--y", required=True)
ap.add_argument("--depth", type=int, default=3)
ap.add_argument("--crop", default="")
ap.add_argument("--scale", type=int, default=1)
a = ap.parse_args()

REPO = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
colors = json.load(open(os.path.join(REPO, "viewer", "blockcolors.json"), encoding="utf-8"))
raw = gzip.open(a.lmd).read()
hl = struct.unpack("<i", raw[4:8])[0]
h = json.loads(raw[8:8 + hl]); pal = h["palette"]
runs = np.frombuffer(raw[8 + hl:], dtype=np.dtype([("c", "<u4"), ("i", "<u2")]))
sy, sz, sx, oy = h["sy"], h["sz"], h["sx"], h["oy"]
vol = np.repeat(runs["i"], runs["c"]).reshape(sy, sz, sx)
TERRAIN = ("rock-", "soil", "sand", "gravel", "drock", "devastatedsoil", "crackedrock", "regolith", "ore-", "clay")
FIX = {"ghostlight": (90, 200, 255), "lava": (255, 90, 20), "water": (40, 90, 200), "saltwater": (30, 70, 160), "glass": (170, 220, 230),
       "metalblock-corroded": (150, 80, 45), "metalblock-new-riveted": (160, 165, 175), "metalblock-new-plain": (185, 190, 196),
       "copper": (60, 160, 120), "brass": (200, 170, 70), "tinbronze": (190, 120, 60), "gold": (240, 200, 60),
       "refractory": (170, 90, 60), "burnedbrick": (70, 40, 35), "burnedplanks": (50, 40, 35), "planks": (140, 105, 60),
       "agedstonebricks": (150, 140, 125), "stonebricks": (130, 130, 128), "crackedstonebricks": (115, 115, 112),
       "polishedrock": (170, 170, 175), "rockpolished": (175, 175, 180), "stonebrickslab": (150, 150, 150),
       "ironfence": (80, 80, 90), "carvedlog": (120, 80, 40), "packedglacierice": (180, 215, 240), "glacierice": (200, 230, 250),
       "clutter": (230, 60, 200), "jonas": (255, 240, 120), "chest": (255, 220, 0), "spiderweb": (220, 220, 220)}


def colour(code):
    c0 = code.split(":")[-1]
    if c0 == "air": return None
    if any(c0.startswith(t) for t in TERRAIN) and "polished" not in c0: return (52, 50, 50)
    for k, v in FIX.items():
        if k in c0: return v
    v = colors.get(code if ":" in code else "game:" + code)
    return tuple(v[:3]) if v else (200, 0, 200)


cols = [colour(p) for p in pal]
rgb = np.array([c if c is not None else (0, 0, 0) for c in cols], dtype=np.float32)
empty = np.array([c is None for c in cols])
cx, cz = sx // 2, sz // 2
x0, x1, z0, z1 = 0, sx, 0, sz
if a.crop:
    dx, dz, r = (int(v) for v in a.crop.split(","))
    x0, x1, z0, z1 = max(0, cx + dx - r), min(sx, cx + dx + r), max(0, cz + dz - r), min(sz, cz + dz + r)
for ys in a.y.split(","):
    Y = int(ys) - oy
    img = np.zeros((z1 - z0, x1 - x0, 3), dtype=np.float32)
    done = np.zeros((z1 - z0, x1 - x0), dtype=bool)
    for d in range(a.depth):
        y = Y - d
        if y < 0 or y >= sy: continue
        lay = vol[y, z0:z1, x0:x1]
        hit = ~empty[lay] & ~done
        img[hit] = rgb[lay][hit] * (1 - 0.22 * d)
        done |= hit
    im = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
    if a.scale > 1: im = im.resize((im.width * a.scale, im.height * a.scale), Image.NEAREST)
    out = f"{a.out}_y{ys}.png"
    im.save(out)
    print("wrote", out)
