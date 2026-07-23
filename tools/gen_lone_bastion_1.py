"""
Lone Bastion 1: the real bastion, about a quarter of the flatcave study's
footprint (that first cut is preserved as flatcave_ruin_base). A small rough
square of pale granite with the same sheer 27-block walls, and this time a
GENUINE castle on top, built by the mod's bastion structure pass (0.45.0):

- Four sheared corner towers (two ruined floors, window slits, ragged
  diagonal tops), a crumbled curtain wall with merlons and breaches, two
  roofless outbuildings.
- Spiral stairs in the NW and SE towers bore down through the rock to a
  FLAT DUNGEON LEVEL at 8 below sea: rectangular hallways on a lattice,
  barred cells (iron fences, some long broken), part-dressed brick walls,
  rubble. Square passages bored into the earth, not caves.
- One cave on the east cliff face dips to the same level and meets the
  halls: the dungeon's back entrance.
- Rusty grass, one quiet green lawn, gear scatter, a skerry ring with
  lurker shards and kelp beds: the Lone Bastion mood kit at 1/4 scale.

    python tools/gen_lone_bastion_1.py > shapes/lone_bastion_1.txt

Suggested: /genisland shape=lone_bastion_1 diameter=100 height=27 water=40
"""
import math

W = H = 100
CX = CZ = 50.0
AX, AZ = 33.0, 31.0
PP = 4.0  # squarer than the study: the castle wants its corners


def body_lim(th):
    return 1.0 + 0.045 * math.sin(2 * th + 1.1) + 0.03 * math.sin(3 * th + 0.4) \
        + 0.02 * math.sin(5 * th + 2.6)


def in_body(x, z, scale=1.0):
    dx, dz = x - CX, z - CZ
    if dx == 0 and dz == 0:
        return True
    th = math.atan2(dz, dx)
    e = (abs(dx / (AX * scale)) ** PP + abs(dz / (AZ * scale)) ** PP) ** (1.0 / PP)
    return e <= body_lim(th)


def edge_rho(th):
    lo, hi = 5.0, 60.0
    for _ in range(40):
        mid = (lo + hi) / 2
        if in_body(CX + mid * math.cos(th), CZ + mid * math.sin(th)):
            lo = mid
        else:
            hi = mid
    return lo


GREEN = (38.0, 62.0, 9.0)   # one quiet lawn, SW of the keep
SKERRY_TYPES = ['k', 'U', 'j', 's', 'k', 'U', 'k', 'j', 's', 'k']


def main():
    grid = [['.'] * W for _ in range(H)]

    for r in range(H):
        for c in range(W):
            x, z = c + 0.5, r + 0.5
            if not in_body(x, z):
                continue
            if math.hypot(x - GREEN[0], z - GREEN[1]) <= GREEN[2]:
                grid[r][c] = 'g'
            elif in_body(x, z, 0.72):
                grid[r][c] = 'C'
            else:
                grid[r][c] = 'P'

    band = []
    for r in range(H):
        for c in range(W):
            if grid[r][c] == '.':
                continue
            close = False
            for dc in range(-3, 4):
                for dr in range(-3, 4):
                    if dc * dc + dr * dr > 9.5:
                        continue
                    cc, rr = c + dc, r + dr
                    if not (0 <= cc < W and 0 <= rr < H) or grid[rr][cc] == '.':
                        close = True
                        break
                if close:
                    break
            if close:
                band.append((c, r))
    for (c, r) in band:
        grid[r][c] = 'N'

    for i in range(10):
        th = i / 10.0 * 2 * math.pi + 0.2 * math.sin(i * 2.7)
        gap = 4.0 + 6.0 * (0.5 + 0.5 * math.sin(i * 1.9 + 0.7))
        kind = SKERRY_TYPES[i % len(SKERRY_TYPES)]
        rad = (3.5 if kind == 's' else 1.6 + 1.2 * (0.5 + 0.5 * math.sin(i * 3.3 + 1.4)))
        rho = edge_rho(th) + gap + rad
        bx, bz = CX + rho * math.cos(th), CZ + rho * math.sin(th)
        jag = 0.25 if kind in 'jk' else 0.10
        for r in range(H):
            for c in range(W):
                if grid[r][c] != '.':
                    continue
                x, z = c + 0.5, r + 0.5
                dx, dz = x - bx, z - bz
                d = math.hypot(dx, dz)
                a = math.atan2(dz, dx)
                lim = rad * (1.0 + jag * (0.6 * math.sin(3 * a + i) + 0.4 * math.sin(5 * a + i * 2)))
                if d <= lim:
                    grid[r][c] = kind

    # The bastion itself, centered on the plateau.
    grid[50][50] = 'Q'

    # Back-entrance cave: mouth on the east cliff of the BODY (skip any
    # skerry or kelp bed standing further out on this row), dives to the
    # dungeon level and meets the halls.
    # Two cells inland: the outermost coast cell can round into water under
    # the coastline jitter, and a drowned marker is silently skipped.
    kc = next(c for c in range(W - 1, -1, -1) if grid[50][c] in 'NPCg')
    grid[50][kc - 2] = 'K'

    print("# lone_bastion_1 - the Lone Bastion proper: a quarter-scale sheer granite")
    print("# square with a REAL ruined castle (bastion structure pass, mod 0.45.0):")
    print("# four sheared towers, crumbled curtain, spiral stairs in the NW and SE")
    print("# towers boring down to a flat lattice dungeon of square hallways and")
    print("# barred cells at 8 below sea. The east cliff cave dips to the same")
    print("# level and meets the halls as a back entrance. The full-size study")
    print("# this grew from is preserved as flatcave_ruin_base.")
    print("# Regenerate: python tools/gen_lone_bastion_1.py > shapes/lone_bastion_1.txt")
    print("# Suggested: /genisland shape=lone_bastion_1 diameter=100 height=27 water=40")
    print()
    print("region P rock=granite fertility=verylow surface=grass climate=arid ores=hematite:0.020,quartz:0.010,copper:0.008 orebits=iron:0.002 wildgrass=0.12 stones=0.03 scatter=loosegears-1:0.005,loosegears-2:0.003,metalpartpile-tiny:0.003 height=1.0 shore=8 rough=0.08")
    print("region C rock=granite fertility=verylow surface=grass climate=arid ores=hematite:0.020,quartz:0.010,copper:0.008 orebits=iron:0.002 wildgrass=0.10 stones=0.05 boulders=0.018 scatter=loosegears-1:0.007,loosegears-3:0.004,metalpartpile-tiny:0.005,metal-scraps:0.003 height=1.0 shore=8 rough=0.08")
    print("region g rock=granite fertility=medium surface=grass ores=hematite:0.020,quartz:0.010 wildgrass=0.38 stones=0.015 scatter=loosegears-1:0.008,cornflower:0.005 height=1.0 shore=8 rough=0.06")
    print("region N rock=granite surface=rock climate=arid ores=hematite:0.045,quartz:0.012 orebits=iron:0.003 boulders=0.012 stones=0.025 height=0.90 shore=3 rough=0.14")
    print("region j rock=granite surface=rock climate=arid ores=hematite:0.025,quartz:0.010 boulders=0.010 stones=0.03 height=0.78 shore=5 rough=0.55")
    print("region k rock=granite surface=rock climate=arid ores=hematite:0.020 stones=0.03 height=0.42 shore=4 rough=0.60")
    print("region U rock=granite surface=rock climate=arid flood=1 height=0.30 shore=1 rough=0.40")
    print("region s rock=granite sand=sand-granite surface=sand flood=2 kelp=0.40 height=0.15 shore=4 rough=0.05")
    print("bastion Q size=34 dungeony=-8 seed=5")
    print("cave K heading=auto dip=22 length=130 radius=2.4 squash=0.8 weave=0.55 scale=1.2 branches=2 branchdepth=1 branchlen=0.5 pinch=0.35 depth=17 mouth=8 entry=10 ores=iron:0.05 seed=2")
    print("deposits natural")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
