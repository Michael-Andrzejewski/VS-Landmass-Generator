"""Where a deep shiver can walk by the game's own pathfinding rules (VS 1.22
AStar.traversable), from a dump:
  - its box is 4.8 x 4 (5 x 5 x 4 cells with the node's centre offset 0.5);
  - a move to a neighbour (8 directions) at the same height needs the box
    clear there; then it falls while the block under the node centre has no
    collision, up to maxFallHeight;
  - if the box collides, it may step up one block only if the box raised by
    stepHeight (2.1251 for a deep giant shiver since Oversized Horrors 1.3.1)
    is clear: cells y+2 .. y+6;
Reports, for each spawner given, the walkable area round it and what blocks
its 11 x 11 x 7 spawn box, and writes a top view PNG of the walkable area.
    python astarcheck.py <file.lmd> <outdir> <x,y,z> ...  (spawner HUD positions)
The dump must come from /gendumparea CX CZ CR (env vars, default the tin
mine: 1915 -1605 85), which fixes where HUD 0,0 sits in it."""
import gzip, json, struct, sys, os, collections
import numpy as np
from PIL import Image

raw = gzip.open(sys.argv[1]).read(); hl = struct.unpack("<i", raw[4:8])[0]; h = json.loads(raw[8:8 + hl]); pal = h["palette"]
runs = np.frombuffer(raw[8 + hl:], dtype=np.dtype([("c", "<u4"), ("i", "<u2")]))
vol = np.repeat(runs["i"], runs["c"]).reshape(h["sy"], h["sz"], h["sx"])
out = sys.argv[2]; os.makedirs(out, exist_ok=True)
CX, CZ, CR = int(os.environ.get("CX", 1915)), int(os.environ.get("CZ", -1605)), int(os.environ.get("CR", 85))
OX, OZ, OY = CX - CR - 16, CZ - CR - 16, h["oy"]   # HUD of the dump's first column (it adds a 16-block margin)
nocoll = np.array([c == "air" or any(k in c for k in ("tallgrass", "flower", "fern", "bush", "plant", "torchholder", "loosestones", "looseores", "oxidation", "water")) for c in pal])
openm = nocoll[vol]
sy, sz, sx = vol.shape

def box_clear(y0, y1):
    """cells y0..y1 (relative to node y) all open in the 5 x 5 footprint, for every node"""
    t = np.ones_like(openm)
    for d in range(y0, y1 + 1):
        sh = np.zeros_like(openm)
        if d >= 0: sh[:sy - d] = openm[d:]
        else: sh[-d:] = openm[:sy + d]
        t &= sh
    u = t.copy()
    for d in range(1, 3): u[:, d:] &= t[:, :-d]; u[:, :-d] &= t[:, d:]
    u[:, :2] = False; u[:, -2:] = False
    v = u.copy()
    for d in range(1, 3): v[:, :, d:] &= u[:, :, :-d]; v[:, :, :-d] &= u[:, :, d:]
    v[:, :, :2] = False; v[:, :, -2:] = False
    return v
fit = box_clear(0, 3)          # the box at the node
up = box_clear(2, 6)           # the box raised by the step height
support = np.zeros_like(openm); support[1:] = ~openm[:-1]   # something with collision under the node centre
MAXFALL = 4
DIRS = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)]

def walk(start):
    seen = {start}; q = collections.deque([start])
    while q:
        y, z, x = q.popleft()
        for dx, dz in DIRS:
            nx, nz, ny = x + dx, z + dz, y
            if not (2 <= nx < sx - 2 and 2 <= nz < sz - 2): continue
            if fit[ny, nz, nx]:
                f = 0
                while not support[ny, nz, nx]:
                    if not fit[ny - 1, nz, nx]: break
                    ny -= 1; f += 1
                    if f > MAXFALL: break
                if f > MAXFALL or not support[ny, nz, nx]: continue
            elif up[ny, nz, nx] and ny + 1 < sy:
                ny += 1
            else:
                continue
            n = (ny, nz, nx)
            if n not in seen: seen.add(n); q.append(n)
    return seen

img = np.zeros((sz, sx, 3), dtype=np.uint8)
cave = (openm & np.flip(np.maximum.accumulate(np.flip(~openm, 0), 0), 0)).any(0)
img[cave] = (70, 70, 70)
for s in sys.argv[3:]:
    hx, hy, hz = (int(v) for v in s.split(","))
    x, y, z = hx - OX, hy - OY, hz - OZ
    # the spawn box: hitbox + 3 each side and above = 10.8 x 7 at spawner +1, +2, +3
    blocked = None
    for dy in (1, 2, 3):
        yy = y + dy
        sub = vol[yy:yy + 7, z - 5:z + 6, x - 5:x + 6]
        bad = ~nocoll[sub]
        if not bad.any(): blocked = None; spawn_y = yy; break
        ys, zs, xs = np.where(bad)
        blocked = collections.Counter(pal[sub[a, b, c]].split(":")[-1] for a, b, c in zip(ys, zs, xs)).most_common(4)
    # walking: start at the spawn node (on the floor over the spawner)
    start = None
    for dy in (1, 2, 3):
        if fit[y + dy, z, x] and support[y + dy, z, x]: start = (y + dy, z, x); break
    reach = walk(start) if start else set()
    xs_ = [c[2] for c in reach]; zs_ = [c[1] for c in reach]
    for c in reach: img[c[1], c[2]] = (40, 150, 60)
    img[z - 1:z + 2, x - 1:x + 2] = (255, 0, 0)
    ext = (max(xs_) - min(xs_) + 1, max(zs_) - min(zs_) + 1) if reach else (0, 0)
    print(f"spawner HUD {hx},{hy},{hz}: spawn box {'clear' if blocked is None else 'BLOCKED by ' + str(blocked)}; "
          f"walkable {len(reach)} nodes, spanning {ext[0]} x {ext[1]} blocks, y {min(c[0] for c in reach) + OY if reach else '-'}..{max(c[0] for c in reach) + OY if reach else '-'}")
Image.fromarray(img).resize((sx * 4, sz * 4), Image.NEAREST).save(os.path.join(out, "walk.png"))
