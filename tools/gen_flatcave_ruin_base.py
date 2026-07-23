"""
Lone Bastion: a ruined medieval fortress island from Michael's freecam
session (sculpted with Opus in voice mode). The vision, block by block:

- A large rough UNEVEN SQUARE of pale granite, rising ~27 blocks sheer out
  of deep cold water. No beach anywhere: the walls drop straight down,
  slightly tucked inward, multi-faced. Rusty iron lines streak the dry rock
  above the waves (hematite seams in the cliff band).
- The castle ruin occupies ~70% of the plateau, centered, with ~15% grass
  clearance to each edge. The castle itself (four sheared towers, curtain
  walls, outbuildings, the hidden carved cliff staircase, the dungeon cells,
  bars, chains, half-bone half-metal figures, chests, pulsing blue moss) is
  a LATER STRUCTURE PASS; the C region records the footprint and carries the
  tumbled-masonry ground (granite boulders + stones + gear piles).
- Grass is rusty almost everywhere (climate=arid + verylow fertility), with
  a few quiet hopeful GREEN patches away from the wreckage: medium
  fertility, thick wildgrass, hand-sized gears with grass through the teeth.
- The spider-machine lies broken in the NE corner, mixed with other
  machinery until you cannot tell where the spider ends: metal part piles,
  loose gears, scrap.
- The island is ringed by SHARP ROCKS: granite spires that pierce the
  surface (j tall, k short, heavy rough, shore ~ half-width so they rise as
  points) and flood=1 lurker shards sitting just beneath it (U). Seaweed
  sways around them on dark granite-sand flood beds (s, kelp).
- Below the castle a dungeon winds down: wide hall, narrow squeeze, wide
  hall (pinch), weaving, to ~50 below sea, just under the seabed, its widest
  rooms at the bottom. Walls carry iron (hematite via the iron alias in
  granite). Mouth on the west cliff face, partway up, easy to miss: the
  nearest terrain gets to the invisible carved staircase.
- One drowned shaft (flooded=1) out among the SE skerries: the 90% buried
  machine country, minable with a breath timer.

    python tools/gen_flatcave_ruin_base.py > shapes/flatcave_ruin_base.txt

Suggested: /genisland shape=flatcave_ruin_base diameter=340 height=27 water=40
"""
import math

W = H = 200
CX = CZ = 100.0
AX, AZ = 64.0, 60.0     # half-extents; slight asymmetry keeps the square uneven
PP = 3.4                # superellipse exponent: rounded-corner square


def body_lim(th):
    return 1.0 + 0.055 * math.sin(2 * th + 1.1) + 0.04 * math.sin(3 * th + 0.4) \
        + 0.028 * math.sin(5 * th + 2.6)


def in_body(x, z, scale=1.0):
    dx, dz = x - CX, z - CZ
    if dx == 0 and dz == 0:
        return True
    th = math.atan2(dz, dx)
    e = (abs(dx / (AX * scale)) ** PP + abs(dz / (AZ * scale)) ** PP) ** (1.0 / PP)
    return e <= body_lim(th)


def edge_rho(th):
    """Distance from center to the body coast along bearing th."""
    lo, hi = 10.0, 110.0
    for _ in range(40):
        mid = (lo + hi) / 2
        if in_body(CX + mid * math.cos(th), CZ + mid * math.sin(th)):
            lo = mid
        else:
            hi = mid
    return lo


# Green patches: away from the NE wreck corner. Big enough (~60 blocks) that
# the climate tint interpolation actually reads green against the arid rust.
GREENS = [(66.0, 126.0, 16.0), (116.0, 142.0, 15.0)]
# Spider wreck corner (NE): fragments, part piles, gears, scrap.
WRECK = (138.0, 62.0, 14.0)

# Skerry ring: sharp rocks all around, type pattern cycles tall spire /
# short tooth / lurker shard / kelp bed. Deterministic jitter via sines.
SKERRY_TYPES = ['k', 'U', 'j', 'k', 's', 'U', 'k', 'j', 'U', 'k', 's', 'k']


def main():
    grid = [['.'] * W for _ in range(H)]

    def is_land(c, r):
        return 0 <= c < W and 0 <= r < H and grid[r][c] != '.'

    # Body + provinces first.
    for r in range(H):
        for c in range(W):
            x, z = c + 0.5, r + 0.5
            if not in_body(x, z):
                continue
            if math.hypot(x - WRECK[0], z - WRECK[1]) <= WRECK[2]:
                grid[r][c] = 'X'
            elif any(math.hypot(x - gx, z - gz) <= gr for (gx, gz, gr) in GREENS):
                grid[r][c] = 'g'
            elif in_body(x, z, 0.70):
                grid[r][c] = 'C'
            else:
                grid[r][c] = 'P'

    # Sheer cliff band: any body cell within 3 cells of water.
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

    # Skerries: 24 stations around the coast, offset 5-14 cells past the
    # edge, radius 2-4 cells. Types cycle; kelp beds are wider and flatter.
    for i in range(24):
        th = i / 24.0 * 2 * math.pi + 0.13 * math.sin(i * 2.7)
        gap = 5.0 + 9.0 * (0.5 + 0.5 * math.sin(i * 1.9 + 0.7))
        kind = SKERRY_TYPES[i % len(SKERRY_TYPES)]
        rad = (4.5 if kind == 's' else 2.0 + 1.6 * (0.5 + 0.5 * math.sin(i * 3.3 + 1.4)))
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

    # Main dungeon mouth: westmost cliff cell on the center row, partway up
    # the west face; heading=auto bores toward the island heart.
    kc = next(c for c in range(W) if grid[100][c] != '.')
    grid[100][kc] = 'K'

    # Drowned shaft: open water among the SE skerries (flooded caves may
    # stand in open water; the mouth is a hole in the actual sea floor).
    th = math.radians(52)
    rho = edge_rho(th) + 22.0
    fc, fr = int(CX + rho * math.cos(th)), int(CZ + rho * math.sin(th))
    while grid[fr][fc] != '.':
        fc += 1
    grid[fr][fc] = 'F'

    print("# flatcave_ruin_base - the Lone Bastion: a ruined fortress on a rough")
    print("# uneven square of pale granite, ~27 blocks sheer above deep cold water.")
    print("# Rusty iron seams in the cliffs, rusty grass with a few hopeful green")
    print("# patches, gear piles and machine scrap thickening toward the collapsed")
    print("# spider-machine in the NE corner. Sharp granite spires and just-under-")
    print("# the-surface lurker shards ring the whole island; seaweed sways on dark")
    print("# sand beds among them. A winding pinched dungeon-cave drops from a door")
    print("# partway up the west cliff to just under the seabed; a drowned shaft")
    print("# waits among the SE skerries. The castle itself (towers, walls, cells,")
    print("# blue moss, chests) is a later structure pass; region C is its footprint.")
    print("# Regenerate: python tools/gen_flatcave_ruin_base.py > shapes/flatcave_ruin_base.txt")
    print("# Suggested: /genisland shape=flatcave_ruin_base diameter=340 height=27 water=40")
    print()
    print("region P rock=granite fertility=verylow surface=grass climate=arid ores=hematite:0.020,quartz:0.010,copper:0.008,tin:0.004,bismuth:0.003 orebits=iron:0.002 wildgrass=0.12 stones=0.03 scatter=loosegears-1:0.004,loosegears-2:0.002,metalpartpile-tiny:0.002 height=1.0 shore=8 rough=0.08")
    print("region C rock=granite fertility=verylow surface=grass climate=arid ores=hematite:0.020,quartz:0.010,copper:0.008,tin:0.004,bismuth:0.003 orebits=iron:0.002 wildgrass=0.10 stones=0.05 boulders=0.020 scatter=loosegears-1:0.006,loosegears-3:0.003,metalpartpile-tiny:0.004,metal-scraps:0.002 height=1.0 shore=8 rough=0.08")
    print("region X rock=granite fertility=verylow surface=grass climate=arid ores=hematite:0.020,quartz:0.010 wildgrass=0.05 stones=0.04 boulders=0.012 scatter=metalpartpile-small:0.014,metalpartpile-tiny:0.010,loosegears-4:0.008,loosegears-2:0.006,metal-scraps:0.006 height=1.0 shore=8 rough=0.16")
    print("region g rock=granite fertility=medium surface=grass ores=hematite:0.020,quartz:0.010 wildgrass=0.38 stones=0.015 scatter=loosegears-1:0.008,cornflower:0.005 height=1.0 shore=8 rough=0.06")
    print("region N rock=granite surface=rock climate=arid ores=hematite:0.045,quartz:0.012,copper:0.006 orebits=iron:0.003 boulders=0.012 stones=0.025 height=0.90 shore=3 rough=0.14")
    print("region j rock=granite surface=rock climate=arid ores=hematite:0.025,quartz:0.010 boulders=0.010 stones=0.03 height=0.78 shore=5 rough=0.55")
    print("region k rock=granite surface=rock climate=arid ores=hematite:0.020 stones=0.03 height=0.42 shore=4 rough=0.60")
    print("region U rock=granite surface=rock climate=arid flood=1 height=0.30 shore=1 rough=0.40")
    print("region s rock=granite sand=sand-granite surface=sand flood=2 kelp=0.40 height=0.15 shore=4 rough=0.05")
    print("cave K heading=auto dip=26 length=330 radius=3.0 squash=0.8 weave=0.7 scale=1.7 branches=5 branchdepth=2 branchlen=0.6 branchradius=0.7 pinch=0.45 depth=52 mouth=10 entry=12 ores=iron:0.05 seed=1")
    print("cave F flooded=1 heading=auto dip=40 length=80 radius=2.2 squash=0.8 weave=0.5 scale=1 branches=1 branchdepth=1 branchlen=0.5 depth=18 entry=4 ores=iron:0.05 seed=3")
    print("deposits natural")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
