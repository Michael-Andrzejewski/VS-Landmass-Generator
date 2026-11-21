"""Find clutter props that float: nothing holds any face their model touches.

    python -I tools/floatcheck.py <dump.lmd> [--near x,z,r] [--list N]

Reads the dump and its .clutter.tsv (each prop's type, turn and push, written
by the dump since 0.73.0), and the clutter catalog's model bounds. A prop is
held when a face its turned and pushed model touches (within one pixel) meets
a solid block, or another prop. Props the catalog does not know are skipped.
Positions are printed as dump coordinates and as offsets from the dump centre.
"""
import argparse, collections, gzip, json, math, os, struct
import numpy as np

REPO = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
TOL = 0.07

ap = argparse.ArgumentParser()
ap.add_argument("lmd")
ap.add_argument("--near", default="")
ap.add_argument("--list", type=int, default=25)
a = ap.parse_args()

raw = gzip.open(a.lmd).read()
hl = struct.unpack("<i", raw[4:8])[0]
h = json.loads(raw[8:8 + hl]); pal = h["palette"]
runs = np.frombuffer(raw[8 + hl:], dtype=np.dtype([("c", "<u4"), ("i", "<u2")]))
sx, sy, sz = h["sx"], h["sy"], h["sz"]
vol = np.repeat(runs["i"], runs["c"].astype(np.int64)).reshape(sy, sz, sx)
cxo, czo = h.get("cx", 0) - h.get("ox", 0), h.get("cz", 0) - h.get("oz", 0)

SOFT = ("air", "water", "clutter", "jonas-lamp", "tallgrass", "flower", "fern", "leaves", "loose", "mushroom",
        "bush", "reed", "cattail", "devgrowth", "snowlayer", "litter", "torch", "chain", "rope")
solid_idx = np.array([not any(s in c.split(":")[-1].split("|")[0] for s in SOFT) and c != "air" for c in pal])
clut_idx = np.array([("clutter" in c or "jonas-lamp" in c) for c in pal])

cat = {}
for r in json.load(open(os.path.join(REPO, "tools", "clutter-catalog", "clutter-catalog-raw.json"), encoding="utf-8"))["rows"]:
    if r.get("_bmin") is not None:
        cat[(r["block"] + "|" + r["type"]).lower()] = (r["_bmin"], r["_bmax"])

tsv = a.lmd[:-4] + ".clutter.tsv"
rows = [l.rstrip("\n").split("\t") for l in open(tsv, encoding="utf-8")][1:]

def at(y, z, x):
    if not (0 <= y < sy and 0 <= z < sz and 0 <= x < sx): return 0
    return vol[y, z, x]

near = [float(v) for v in a.near.split(",")] if a.near else None
floating, unknown, total = [], 0, 0
for x, y, z, blk, typ, rot, ox, oz in rows:
    x, y, z = int(x), int(y), int(z)
    if near and math.hypot(x - cxo - near[0], z - czo - near[1]) > near[2]: continue
    key = (blk + "|" + typ).lower()
    if key not in cat: unknown += 1; continue
    total += 1
    (x1, y1, z1), (x2, y2, z2) = cat[key]
    th, c, s = float(rot), math.cos(float(rot)), math.sin(float(rot))
    xs, zs = [], []
    for px in (x1, x2):
        for pz in (z1, z2):
            dx, dz = px - 0.5, pz - 0.5
            xs.append(0.5 + dx * c + dz * s + float(ox)); zs.append(0.5 - dx * s + dz * c + float(oz))
    bx1, bx2, bz1, bz2 = min(xs), max(xs), min(zs), max(zs)
    faces = []
    if y1 <= TOL: faces.append((0, -1, 0))
    if y2 >= 1 - TOL: faces.append((0, 1, 0))
    if bx1 <= TOL: faces.append((-1, 0, 0))
    if bx2 >= 1 - TOL: faces.append((1, 0, 0))
    if bz1 <= TOL: faces.append((0, 0, -1))
    if bz2 >= 1 - TOL: faces.append((0, 0, 1))
    held = any(solid_idx[at(y + fy, z + fz, x + fx)] or clut_idx[at(y + fy, z + fz, x + fx)] for fx, fy, fz in faces)
    if not held: floating.append((typ, x, y, z, x - cxo, z - czo, len(faces)))

print("props checked: %d (catalog does not know %d)" % (total, unknown))
print("floating: %d" % len(floating))
by = collections.Counter(f[0] for f in floating)
for t, n in by.most_common(20): print("  %4d  %s" % (n, t))
for f in floating[:a.list]:
    print("  %-30s dump %d,%d,%d  from centre %+d,%+d  faces touched %d" % f)
