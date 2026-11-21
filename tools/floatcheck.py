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
        "bush", "reed", "cattail", "devgrowth", "snowlayer", "litter", "torch")
# props the game's own underground ruins place floating (their schematics have filler
# round them): counted apart, not as ours
NOT_OURS = {"pipe-veryrusted-v-topw-broken", "pipe-veryrusted-h-sw_broken"}
solid_idx = np.array([not any(s in c.split(":")[-1].split("|")[0] for s in SOFT) and c != "air" for c in pal])
water_idx = np.array(["water" in c for c in pal])
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
info = {}
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
    # every face of the turned, pushed model that meets a block boundary, over all
    # the cells the model spans (long pipes reach several blocks)
    def span(a, b): return range(int(math.floor(a + TOL)), max(int(math.floor(a + TOL)) + 1, int(math.ceil(b - TOL))))
    def onb(v): return abs(v - round(v)) <= TOL
    cells = []   # neighbour cells (dx, dy, dz) relative to the prop's block
    # the cell a low side rests against: the one beyond a boundary, or the one it reaches into
    def lo_cell(v): return int(round(v)) - 1 if onb(v) else int(math.floor(v))
    def hi_cell(v): return int(round(v)) if onb(v) else int(math.floor(v))
    if y1 <= TOL:
        cells += [(i, lo_cell(y1), k) for i in span(bx1, bx2) for k in span(bz1, bz2)]
    if y2 >= 1 - TOL:
        cells += [(i, hi_cell(y2), k) for i in span(bx1, bx2) for k in span(bz1, bz2)]
    if bx1 <= TOL:
        cells += [(lo_cell(bx1), j, k) for j in span(y1, y2) for k in span(bz1, bz2)]
    if bx2 >= 1 - TOL:
        cells += [(hi_cell(bx2), j, k) for j in span(y1, y2) for k in span(bz1, bz2)]
    if bz1 <= TOL:
        cells += [(i, j, lo_cell(bz1)) for i in span(bx1, bx2) for j in span(y1, y2)]
    if bz2 >= 1 - TOL:
        cells += [(i, j, hi_cell(bz2)) for i in span(bx1, bx2) for j in span(y1, y2)]
    flot = onb(y1) and round(y1) == 0 and water_idx[at(y - 1, z, x)] and not water_idx[at(y, z, x)]
    info[(x, y, z)] = (typ, cells, flot, (bx1, bx2, y1, y2, bz1, bz2))

# cells each prop's model reaches into beyond its own (coffins, stoves, beds)
cover = {}
for (x, y, z), (typ, cells, flot, bb) in info.items():
    bx1, bx2, by1, by2, bz1, bz2 = bb
    for cx in range(math.floor(bx1 + TOL), math.ceil(bx2 - TOL)):
        for cy in range(math.floor(by1 + TOL), math.ceil(by2 - TOL)):
            for cz in range(math.floor(bz1 + TOL), math.ceil(bz2 - TOL)):
                if (cx, cy, cz) != (0, 0, 0): cover.setdefault((x + cx, y + cy, z + cz), set()).add((x, y, z))
# held by a block (or as flotsam on water) first, then by a prop that is itself held
held = {p for p, v in info.items() if v[2]}
changed = True
while changed:
    changed = False
    for p, (typ, cells, flot, bb) in info.items():
        if p in held: continue
        x, y, z = p
        for fx, fy, fz in cells:
            n = (x + fx, y + fy, z + fz)
            if (solid_idx[at(n[1], n[2], n[0])] or n in held
                    or (clut_idx[at(n[1], n[2], n[0])] and n not in info)
                    or (fy < 0 and any(q in held for q in cover.get(n, ()) if q != p))):
                held.add(p); changed = True; break
not_ours = 0
for p, (typ, cells, flot, bb) in info.items():
    if p not in held and typ.lower() in NOT_OURS:
        not_ours += 1
    elif p not in held:
        floating.append((typ, p[0], p[1], p[2], p[0] - cxo, p[2] - czo, len(cells)))

print("props checked: %d (catalog does not know %d)" % (total, unknown))
print("floating: %d" % len(floating))
print("floating in the game's own ruins (not ours): %d" % not_ours)
by = collections.Counter(f[0] for f in floating)
for t, n in by.most_common(20): print("  %4d  %s" % (n, t))
for f in floating[:a.list]:
    print("  %-30s dump %d,%d,%d  from centre %+d,%+d  faces touched %d" % f)
