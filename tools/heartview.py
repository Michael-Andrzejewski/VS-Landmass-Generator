"""Cutaway picture of the Great Machine's heart from a dump.

    python -I tools/heartview.py <great_machine.lmd> <out.png> [--housing <shape>] [--mod <assets>] [--radius 12]
                                 [--cam 1,0.7,1.2] [--size 900]

Finds the heart (two polished obsidian blocks stacked, polished basalt three
blocks east and west), then draws what is inside the shell round it: blocks
as cubes in their viewer colours, clutter props as their real models with the
turn and push the dump's .clutter.tsv records, and, with --housing, a Lens
Housing model on the plinth (the mod places it; the headless dump has none).
"""
import argparse, gzip, json, math, os, struct, sys
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "clutter-catalog"))
import build_clutter_catalog as B  # noqa: E402
import render_clutter as R  # noqa: E402
import render_shape as RS  # noqa: E402
import vsjson  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument("lmd"); ap.add_argument("out")
ap.add_argument("--housing", default="")
ap.add_argument("--mod", action="append", default=[])
ap.add_argument("--radius", type=int, default=12)
ap.add_argument("--up", type=int, default=7)
ap.add_argument("--cam", default="1,0.75,1.25")
ap.add_argument("--size", type=int, default=900)
a = ap.parse_args()

raw = gzip.open(a.lmd).read()
hl = struct.unpack("<i", raw[4:8])[0]
h = json.loads(raw[8:8 + hl]); pal = h["palette"]
runs = np.frombuffer(raw[8 + hl:], dtype=np.dtype([("c", "<u4"), ("i", "<u2")]))
sx, sy, sz = h["sx"], h["sy"], h["sz"]
vol = np.repeat(runs["i"], runs["c"].astype(np.int64)).reshape(sy, sz, sx)

idx = {c: i for i, c in enumerate(pal)}
obs = idx.get("game:rockpolished-obsidian", idx.get("game:rock-obsidian"))
bas = idx.get("game:rockpolished-basalt", idx.get("game:rock-basalt"))
ys, zs, xs = np.nonzero((vol[:-1] == obs) & (vol[1:] == obs))
heart = None
for y, z, x in zip(ys, zs, xs):
    if 3 <= x < sx - 3 and vol[y, z, x - 3] == bas and vol[y, z, x + 3] == bas:
        heart = (int(x), int(y), int(z)); break
if heart is None:
    sys.exit("no heart found")
hx, yc, hz = heart
print("heart at dump", hx, yc, hz)

colors = json.load(open(os.path.join(HERE, "..", "viewer", "blockcolors.json"), encoding="utf-8"))
def col(code):
    k = code.split(":")[-1]
    for key in (k, code):
        if key in colors: return colors[key]
    best = None
    for c in colors:
        if k.startswith(c) and (best is None or len(c) > len(best)): best = c
    return colors[best] if best else [150, 150, 150]

dom = RS.Domains(B.DEFAULT_ASSETS, a.mod)
assets = B.Assets(B.DEFAULT_ASSETS)
imgs = R.ImgStore(assets)
entries = {(e["block"], e["type"]): e for e in R.load_entries(assets)}

rr = a.radius
def inside(x, y, z):
    return (x - hx) ** 2 + (z - hz) ** 2 <= rr * rr and yc - 1 <= y <= yc + a.up

quads = []
solid = lambda c: c != "air" and "clutter" not in c and "jonas-lamp" not in c and "ghostlight" not in c
tex_cache = {}
for y in range(yc - 1, yc + a.up + 1):
    for z in range(hz - rr, hz + rr + 1):
        for x in range(hx - rr, hx + rr + 1):
            if not inside(x, y, z): continue
            c = pal[vol[y, z, x]]
            if not solid(c): continue
            rgb = col(c)
            if c not in tex_cache:
                t = np.ones((1, 1, 4), np.float32); t[0, 0, :3] = np.array(rgb[:3]) / 255.0; tex_cache[c] = t
            for f, (dx, dy, dz) in (("u", (0, 1, 0)), ("n", (0, 0, -1)), ("s", (0, 0, 1)), ("e", (1, 0, 0)), ("w", (-1, 0, 0)), ("d", (0, -1, 0))):
                nx, ny, nz = x + dx, y + dy, z + dz
                if inside(nx, ny, nz) and solid(pal[vol[ny, nz, nx]]): continue
                P = np.array(B.face_corners(f, 1, 1, 1), float) + (x - hx, y - yc, z - hz)
                quads.append((P, np.zeros((4, 2)), tex_cache[c], f))

tsv = a.lmd[:-4] + ".clutter.tsv"
n = 0
for row in [l.rstrip("\n").split("\t") for l in open(tsv, encoding="utf-8")][1:]:
    x, y, z = int(row[0]), int(row[1]), int(row[2])
    if not inside(x, y, z): continue
    e = entries.get((row[3], row[4]))
    if e is None: continue
    th, ox, oz = float(row[5]), float(row[6]), float(row[7])
    c, s = math.cos(th), math.sin(th)
    rot = np.array([[c, 0, s, 0], [0, 1, 0, 0], [-s, 0, c, 0], [0, 0, 0, 1.0]])
    place = B.m_translate(x - hx + ox, y - yc, z - hz + oz) @ B.m_about_center(rot)
    quads += R.collect_quads(e, assets, imgs, place)
    n += 1
print("props drawn:", n)

if a.housing:
    p = a.housing
    if not os.path.isfile(p):
        d, rel = B.split_domain(p)
        p = dom.find(d, "shapes/" + rel + ".json")
    quads += RS.shape_quads(dom, vsjson.load(p), {}, place=B.m_translate(0, 2, 0))

V = R.view_matrix(tuple(float(v) for v in a.cam.split(",")))
pts = np.concatenate([q[0] for q in quads])
img = R.raster(quads, [], size=a.size, V=V, bounds_pts=pts, bg=(30, 32, 36, 255), exposure=1.25)
img.convert("RGB").save(a.out)
print("wrote", a.out)
