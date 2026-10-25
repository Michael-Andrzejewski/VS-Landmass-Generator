"""Checks on a Great Machine dump (.lmd), with its server log.

    python gmcheck.py <file.lmd> --log server-main.log [--hud cx,cz]

1. Reach: every known way in is plugged where it meets the air (each way
   down's shaft top, each pipe mouth near its wall, each plateau hall's
   stairwell top). Then all open cells are labelled. The works' pocket (at
   the Axle Well) must not reach the sky; every way down's foot, every pipe
   mouth's inner end and a sample of maze chambers must be in it.
2. Every way down's shaft open from its foot to its floor.
3. Floating clutter: clutter-like cells with nothing solid under, beside or
   over them.
4. Fluid that would run: water or lava with air beside or under it, and
   whether it is in the works' pocket.
"""
import argparse, gzip, json, re, struct
import numpy as np
from scipy import ndimage

ap = argparse.ArgumentParser()
ap.add_argument("lmd")
ap.add_argument("--log", required=True)
ap.add_argument("--hud", default="2,-1398")
a = ap.parse_args()
hx, hz = (int(v) for v in a.hud.split(","))

raw = gzip.open(a.lmd).read()
hl = struct.unpack("<i", raw[4:8])[0]
h = json.loads(raw[8:8 + hl]); pal = h["palette"]
runs = np.frombuffer(raw[8 + hl:], dtype=np.dtype([("c", "<u4"), ("i", "<u2")]))
vol = np.repeat(runs["i"], runs["c"]).reshape(h["sy"], h["sz"], h["sx"])
oy = h["oy"]; sy, sz, sx = vol.shape
cx, cz = sx // 2, sz // 2
names = [c.split(":")[-1] for c in pal]
PASS = ("air", "water", "lava", "clutter", "jonas", "ironfence", "slab", "spiderweb", "chest", "loose", "metalpartpile", "door",
        "ladder", "torch", "devgrowth", "tallgrass", "flower", "fern", "mushroom", "glowworm", "stalagsection", "leaves", "rail",
        "groundstorage", "riftward", "translocator", "teleporter", "resonator", "locustnest")
openp = np.array([any(k in c for k in PASS) and "stairs" not in c for c in names])
O = openp[vol]
txt = open(a.log, encoding="utf-8", errors="replace").read()

downs = [(m.group(1), int(m.group(2)) - hx, int(m.group(3)), int(m.group(4)) - hz, int(m.group(5)) - hx, int(m.group(6)), int(m.group(7)) - hz, int(m.group(8)))
         for m in re.finditer(r"great machine way down \((\w+)\) at HUD (-?\d+), (-?\d+), (-?\d+) probe (-?\d+), (-?\d+), (-?\d+) shaft (-?\d+)", txt)]
mouths = [(m.group(1), int(m.group(2)) - hx, int(m.group(3)), int(m.group(4)) - hz, int(m.group(5)) - hx, int(m.group(6)), int(m.group(7)) - hz)
          for m in re.finditer(r"great machine pipe mouth \(([^)]*)\) at HUD (-?\d+), (-?\d+), (-?\d+) probe (-?\d+), (-?\d+), (-?\d+)", txt)]
halls = [(int(m.group(1)) - hx, int(m.group(2)), int(m.group(3)) - hz)
         for m in re.finditer(r"great machine plateau hall \([^)]*\) at HUD (-?\d+), (-?\d+), (-?\d+)", txt)]
chambers = [(int(m.group(1)) - hx, int(m.group(2)), int(m.group(3)) - hz)
            for m in re.finditer(r"great machine chamber probe at HUD (-?\d+), (-?\d+), (-?\d+)", txt)]
print(f"from the log: {len(downs)} ways down, {len(mouths)} pipe mouths, {len(halls)} plateau halls, {len(chambers)} chamber probes")

# 2. the shafts, before any plugging
good = broken = 0; bl = []
for kind, dx, F, dz, px, py, pz, yb in downs:
    x, z = cx + dx, cz + dz
    if kind == "ladder":
        shut = int((~O[yb - oy:F - oy, z, x]).sum())
    else:
        rin = 3.5 if kind == "standpipe" else 2.5
        zz, xx = np.ogrid[-4:5, -4:5]
        ring = ((xx * xx + zz * zz) <= rin * rin) & ((xx * xx + zz * zz) > 1.5)
        shut = sum(1 for y in range(yb + 3, F - 1) if O[y - oy, z - 4:z + 5, x - 4:x + 5][ring].mean() < 0.4)
    if shut == 0: good += 1
    else: broken += 1; bl.append(f"{kind} at {dx},{F},{dz} from {yb}: {shut} shut level(s)")
print(f"way-down shafts open top to bottom: {good} of {good + broken}")
for b in bl[:10]: print("  SHUT:", b)

# 1. plug the ways in, then label
P = O.copy()
zz, xx = np.ogrid[:sz, :sx]
def plug_col(dx, dz, r, y0, y1):
    disk = (xx - (cx + dx)) ** 2 + (zz - (cz + dz)) ** 2 <= r * r
    P[max(0, y0 - oy):max(0, y1 - oy), disk] = False
for kind, dx, F, dz, *_ in downs:
    r = {"ladder": 1.6, "spiral": 3.6, "standpipe": 8.5}[kind]
    plug_col(dx, dz, r, F - 4, sy + oy)
for kind, mx, my, mz, ix, iy, iz in mouths:
    # a ball near the wall: 70% of the way from the inner end to the mouth
    bx, bz, by = cx + ix + (mx - ix) * 0.7, cz + iz + (mz - iz) * 0.7, iy + 1 - oy
    x0, x1, z0, z1, y0, y1 = int(bx) - 9, int(bx) + 10, int(bz) - 9, int(bz) + 10, max(0, int(by) - 9), int(by) + 10
    gy, gz, gx = np.ogrid[y0:y1, z0:z1, x0:x1]
    ball = (gx - bx) ** 2 + (gz - bz) ** 2 + (gy - by) ** 2 <= 8.5 ** 2
    P[y0:y1, z0:z1, x0:x1][ball] = False
for dx, F, dz in halls:
    plug_col(dx, dz, 6.5, F - 6, sy + oy)
lab, n = ndimage.label(P)
sizes = np.bincount(lab.ravel())
sky = set(np.unique(lab[-1][P[-1]]).tolist()) - {0}

def L(dx, y, dz, r=3):
    x, z, yi = cx + dx, cz + dz, y - oy
    for rr in range(r + 1):
        sub = lab[max(0, yi - rr):yi + rr + 1, max(0, z - rr):z + rr + 1, max(0, x - rr):x + rr + 1]
        nz = sub[sub > 0]
        if nz.size: return int(np.bincount(nz).argmax())
    return 0

works = L(20, 9, 0)
print(f"works pocket: {sizes[works] if works else 0} open cells; {'JOINS THE SKY (an opening nobody planned)' if works in sky else 'sealed once the known ways in are plugged'}")
ok = bad = 0; badl = []
for kind, dx, F, dz, px, py, pz, yb in downs:
    got = L(px, py, pz)
    if got == works: ok += 1
    else: bad += 1; badl.append(f"way down ({kind}) {dx},{F},{dz} foot probe -> pocket {got} ({sizes[got] if got else 0})")
for kind, mx, my, mz, ix, iy, iz in mouths:
    got = L(ix, iy, iz)
    if got == works: ok += 1
    else: bad += 1; badl.append(f"mouth ({kind}) {mx},{my},{mz} inner probe -> pocket {got} ({sizes[got] if got else 0})")
print(f"ways in reaching the works: {ok} of {ok + bad}")
for b in badl[:15]: print("  NOT:", b)
ok = bad = 0; badl = []
for dx, y, dz in chambers:
    got = L(dx, y, dz)
    if got == works: ok += 1
    else: bad += 1; badl.append(f"chamber {dx},{y},{dz} -> pocket {got} ({sizes[got] if got else 0})")
print(f"chambers (1 in 4) in the works' pocket: {ok} of {ok + bad}")
for b in badl[:15]: print("  NOT:", b)

# 3. floating clutter
cl = np.array([("clutter" in c or "jonas-lamp" in c or "clutteredbookshelf" in c) for c in names])
C = cl[vol]
# what holds clutter up: anything solid, a top-half slab, a shelf (shelves stack)
holds = np.array([("slab" in c and "-up" in c) or "clutteredbookshelf" in c for c in names])
S_ = ~O | holds[vol]
sup = np.zeros_like(C)
sup[1:] |= S_[:-1]; sup[:-1] |= S_[1:]
sup[:, 1:] |= S_[:, :-1]; sup[:, :-1] |= S_[:, 1:]
sup[:, :, 1:] |= S_[:, :, :-1]; sup[:, :, :-1] |= S_[:, :, 1:]
fl = C & ~sup
r2 = (np.arange(sx)[None, :] - cx) ** 2 + (np.arange(sz)[:, None] - cz) ** 2
inside = r2 <= 300 ** 2
print(f"clutter cells: {int(C.sum())}; touching nothing solid: {int(fl.sum())}, of them inside the walls: {int((fl & inside[None]).sum())}")
ys, zs, xs = np.nonzero(fl & inside[None])
for y, z, x in list(zip(ys, zs, xs))[:8]: print(f"  floating at dx {x - cx}, y {y + oy}, dz {z - cz}: {names[vol[y, z, x]]}")

# 4. running fluid
fluid = np.array([("water" in c or "lava" in c) for c in names])[vol]
A = np.array([c == "air" for c in names])[vol]
nb = np.zeros_like(A)
nb[1:] |= A[:-1]
nb[:, 1:] |= A[:, :-1]; nb[:, :-1] |= A[:, 1:]
nb[:, :, 1:] |= A[:, :, :-1]; nb[:, :, :-1] |= A[:, :, 1:]
run = fluid & nb
inw = run & (lab == works) if works else run & False
print(f"fluid cells that would run: {int(run.sum())}; of them in the works' pocket: {int(inw.sum())}")
ys, zs, xs = np.nonzero(inw)
for y, z, x in list(zip(ys, zs, xs))[:6]: print(f"  running in the works at dx {x - cx}, y {y + oy}, dz {z - cz}: {names[vol[y, z, x]]}")

cnt = {}
for key in ("chest", "groundstorage", "riftward", "statictranslocator", "basereturnteleporter", "corpsereturnteleporter", "resonator", "ladder", "locustnest-cage"):
    ids = [i for i, c in enumerate(names) if key in c]
    cnt[key] = int(np.isin(vol, ids).sum()) if ids else 0
print("blocks:", cnt)
