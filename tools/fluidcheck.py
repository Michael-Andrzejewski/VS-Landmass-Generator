"""Find fluid that would run: water or lava with open air beside it or under it.

    python tools/fluidcheck.py <file.lmd> [--below Y] [--radius R]

A still fluid with air on top is fine (a pool); air at its side or below it
means it flows when its chunk ticks: a leak into a dry cut, or a lava pool
that spills. Counts by fluid, a few positions (relative to the dump centre,
y absolute), and the radius they are found at. --below keeps cells under a
height (default: the dump's sea level); --radius keeps cells within that
many blocks of the centre.
"""
import argparse, gzip, json, struct
import numpy as np

ap = argparse.ArgumentParser()
ap.add_argument("lmd")
ap.add_argument("--below", type=int, default=None)
ap.add_argument("--radius", type=float, default=1e9)
a = ap.parse_args()

raw = gzip.open(a.lmd).read()
hl = struct.unpack("<i", raw[4:8])[0]
h = json.loads(raw[8:8 + hl])
pal = h["palette"]
runs = np.frombuffer(raw[8 + hl:], dtype=np.dtype([("c", "<u4"), ("i", "<u2")]))
vol = np.repeat(runs["i"], runs["c"]).reshape(h["sy"], h["sz"], h["sx"])
oy = h.get("oy", 1)
below = (a.below if a.below is not None else h["sea"]) - oy
fluid = np.array([("water" in c and "lily" not in c) or "lava" in c for c in pal])
air = np.zeros(len(pal), bool); air[0] = True
F = fluid[vol]; A = air[vol]
open_side = np.zeros_like(F)
open_side[:, :, 1:] |= A[:, :, :-1]; open_side[:, :, :-1] |= A[:, :, 1:]
open_side[:, 1:, :] |= A[:, :-1, :]; open_side[:, :-1, :] |= A[:, 1:, :]
open_side[1:, :, :] |= A[:-1, :, :]
bad = F & open_side
bad[below:, :, :] = False
cz, cx = h["sz"] // 2, h["sx"] // 2
ys, zs, xs = np.where(bad)
r = np.sqrt((xs - cx) ** 2 + (zs - cz) ** 2)
keep = r <= a.radius
ys, zs, xs, r = ys[keep], zs[keep], xs[keep], r[keep]
print(f"{len(ys)} fluid cell(s) with air beside or below, under y {below + oy}, within {a.radius:g} of the centre")
kinds = {}
for y, z, x in zip(ys, zs, xs):
    c = pal[vol[y, z, x]].split(":")[-1]
    kinds[c] = kinds.get(c, 0) + 1
for c, n in sorted(kinds.items(), key=lambda kv: -kv[1]):
    print(f"  {n:6d} {c}")
for i in np.argsort(r)[:20]:
    print(f"  at dx {xs[i] - cx:5d} dz {zs[i] - cz:5d} y {ys[i] + oy:4d} (r {r[i]:.0f}): {pal[vol[ys[i], zs[i], xs[i]]].split(':')[-1]}")
