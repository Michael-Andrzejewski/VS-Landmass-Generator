"""Is a buried place sealed? Which open spaces of an .lmd dump join the sky.

    python tools/sealcheck.py <file.lmd> --points "name:dx,y,dz;..." [--plug "dx,dz,r,ymin;..."]

Open space is anything a player or a draught could pass: air, fluids,
clutter, lamps, fences, slabs, webs, chests and the like. Every open cell
is labelled by the pocket it belongs to (6-connected), and each point
(relative to the dump centre, y absolute) reports whether its pocket
reaches the top of the dump, i.e. the open sky. --plug fills columns
(radius r round dx, dz, from ymin up) before labelling, to test whether a
place stays sealed once its known ways in are closed.
"""
import argparse, gzip, json, struct
import numpy as np
from scipy import ndimage

ap = argparse.ArgumentParser()
ap.add_argument("lmd")
ap.add_argument("--points", required=True)
ap.add_argument("--plug", default="")
a = ap.parse_args()

raw = gzip.open(a.lmd).read()
hl = struct.unpack("<i", raw[4:8])[0]
h = json.loads(raw[8:8 + hl]); pal = h["palette"]
runs = np.frombuffer(raw[8 + hl:], dtype=np.dtype([("c", "<u4"), ("i", "<u2")]))
vol = np.repeat(runs["i"], runs["c"]).reshape(h["sy"], h["sz"], h["sx"])
oy = h["oy"]
PASS = ("air", "water", "lava", "clutter", "jonas", "ironfence", "slab", "spiderweb", "chest", "loose", "metalpartpile", "door",
        "ladder", "torch", "devgrowth", "tallgrass", "flower", "fern", "mushroom", "glowworm", "stalagsection", "leaves", "rail")
openp = np.array([any(k in c.split(":")[-1] for k in PASS) for c in pal])
O = openp[vol]
cx, cz = h["sx"] // 2, h["sz"] // 2
for spec in [s for s in a.plug.split(";") if s]:
    dx, dz, r, ymin = (float(v) for v in spec.split(","))
    zz, xx = np.ogrid[:h["sz"], :h["sx"]]
    disk = (xx - (cx + dx)) ** 2 + (zz - (cz + dz)) ** 2 <= r * r
    O[int(ymin) - oy:, disk] = False
lab, n = ndimage.label(O)
sky = set(np.unique(lab[-1][O[-1]]).tolist()) - {0}
print(f"{n} open pockets; {len(sky)} of them reach the sky")
for spec in a.points.split(";"):
    name, pt = spec.split(":")
    dx, y, dz = (int(v) for v in pt.split(","))
    x, z, yy = cx + dx, cz + dz, y - oy
    # the nearest open cell to the point (a fitting may stand on it)
    best = None
    for r in range(0, 4):
        sub = O[max(0, yy - r):yy + r + 1, z - r:z + r + 1, x - r:x + r + 1]
        if sub.any():
            k = np.argwhere(sub)[0]
            best = (max(0, yy - r) + k[0], z - r + k[1], x - r + k[2]); break
    if best is None:
        print(f"  {name:24s} no open cell near the point"); continue
    L = lab[best]
    size = int((lab == L).sum()) if L else 0
    print(f"  {name:24s} pocket {L:6d} ({size} open cells): {'JOINS THE SKY' if L in sky else 'sealed'}")
