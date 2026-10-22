"""Check the starter knoll and buried machine in an .lmd dump.
    python knollcheck.py <file.lmd>"""
import gzip, json, struct, sys
import numpy as np
raw = gzip.open(sys.argv[1]).read(); hl = struct.unpack("<i", raw[4:8])[0]; h = json.loads(raw[8:8 + hl]); pal = h["palette"]
runs = np.frombuffer(raw[8 + hl:], dtype=np.dtype([("c", "<u4"), ("i", "<u2")]))
vol = np.repeat(runs["i"], runs["c"]).reshape(h["sy"], h["sz"], h["sx"]); sea = h["sea"] - h["oy"]
terrain = np.array([any(k in c for k in ("soil", "rock", "sand", "gravel", "clay", "forestfloor")) and "metal" not in c for c in pal])
metal = np.array(["metalblock" in c for c in pal])
clut = np.array([("clutter" in c or "loosegears" in c or "metalpart" in c or "metal-" in c) for c in pal])
chest = np.array(["chest" in c for c in pal]); trans = np.array(["translocator" in c for c in pal])
ys, zs, xs = np.where(trans[vol]); k = int(np.argmax(ys)); tx, tz = xs[k] + 1, zs[k]   # translocator sits west of the trunk
def ground(x, z):
    ys = np.where(terrain[vol[:, z, x]])[0]; return int(ys.max()) - sea
print("trunk column:", [(y - sea, pal[vol[y, tz, tx]].split(':')[-1]) for y in range(sea + ground(tx, tz) - 1, sea + ground(tx, tz) + 3)])
print("W->E ground:", [ground(tx + d, tz) for d in range(-16, 17, 2)])
print("N->S ground:", [ground(tx, tz + d) for d in range(-16, 17, 2)])
R = 14
mach = metal | clut
exposed = 0; top_gap = 99
for dz in range(-R, R + 1):
    for dx in range(-R, R + 1):
        col = vol[:, tz + dz, tx + dx]; g = ground(tx + dx, tz + dz) + sea
        my = np.where(mach[col])[0]
        if len(my):
            top_gap = min(top_gap, g - int(my.max()))
            exposed += int((my > g).sum())
print("machine blocks above the ground:", exposed, " fewest soil blocks over the machine:", top_gap - 1)
sub = vol[:, tz - R:tz + R + 1, tx - R:tx + R + 1]
print("metal", int(metal[sub].sum()), "clutter/parts", int(clut[sub].sum()))
for nm, m in (("chest", chest), ("translocator", trans)):
    y, z, x = np.where(m[vol]); k = int(np.argmax(y))
    print(nm, "dx", x[k] - tx, "dz", z[k] - tz, "y", y[k] - sea, "on", pal[vol[y[k] - 1, z[k], x[k]]].split(':')[-1])
