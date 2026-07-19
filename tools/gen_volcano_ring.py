"""
Volcano Ring island: the remnant rim of an exploded volcano, 700 wide, with
a 400-wide crater lake inside it. From Michael's sketch:

- A distinct RING of land, but with wavy natural edges and little rocky
  outcrops, not a perfect circle.
- Huge redwoods, densely forested, across the north of the ring; a few
  landmark giants stand in their own glades.
- Lots of pine (and its resin) on the west.
- Many jungle warm-weather crops across the south: kapok jungle with wild
  pineapple, cassava, peanut and amaranth, plus patches of terra preta
  where the old crater farmers enriched the ground.
- The crater lake holds a few small islands by its inside edges (green
  kapok isles and black obsidian spires) and a very deep center where
  serpents can lurk: two Underwater Horrors serpent spawners sit on the
  deep floor, lifted so their trigger range reaches the surface.

Rock is basalt with obsidian blended through the rim crest, the caldera
wall and the lake spires (the glassy heart of the old volcano). Basalt ores
verified against 1.22 allowedVariants: nativecopper to bountiful, gold and
silver, bismuthinite, cassiterite, sphalerite, pentlandite, quartz and
cinnabar. deposits natural re-runs the game's own ore pass on top.

    python tools/gen_volcano_ring.py > shapes/volcano_ring.txt

Suggested: /genisland shape=volcano_ring diameter=700 height=46 water=60 climate=lush
"""
import math

W = H = 300
CX = CZ = 150.0

# The rim ring: wavy outer coast and wavy crater-lake shore. Amplitudes in
# cells (1 cell = ~2.33 blocks at diameter 700). The sin(2th) terms squash
# both circles independently so the ring's width varies and nothing reads
# as a compass-drawn circle.
def outer_r(th):
    return 130.0 + 5.0 * math.sin(2 * th + 1.0) + 6.5 * math.sin(3 * th + 0.7) \
        + 4.0 * math.sin(5 * th + 2.1) + 2.5 * math.sin(9 * th + 4.0)

def lake_r(th):
    return 82.0 + 4.0 * math.sin(2 * th + 2.6) + 5.0 * math.sin(3 * th + 3.9) \
        + 3.5 * math.sin(6 * th + 1.2) + 2.5 * math.sin(11 * th + 5.0)

# Little rocky outcrops on the outer coast: small jagged blobs centered
# just past the coastline at these compass bearings (deg from north, cw).
OUTCROPS = [(15.0, 6.0), (60.0, 5.0), (145.0, 7.0), (200.0, 5.5),
            (265.0, 5.0), (335.0, 6.5)]

# Lake islands by the inside edges: (bearing, radius, kind, gap to shore).
# Distance from center is derived from the actual lake shore at that
# bearing, so the isles always hug the inside edge with clear water around.
# 'g' = green kapok isle, 'w' = black obsidian spire.
LAKE_ISLES = [
    (95.0, 6.0, 'g', 7.0),
    (185.0, 5.5, 'g', 8.0),
    (25.0, 3.5, 'w', 6.0),
    (250.0, 4.2, 'w', 9.0),
    (320.0, 3.2, 'w', 6.0),
]


def bearing_th(bearing_deg):
    b = math.radians(bearing_deg)
    return math.atan2(-math.cos(b), math.sin(b))


def isle_center(bb, rr, gap):
    th = bearing_th(bb)
    return polar(bb, lake_r(th) - rr - gap)

# Black sand coves on the outer coast, one per province: bearing, half-arc.
COVES = [(80.0, 4.5), (210.0, 4.5), (300.0, 4.5)]

# Terra preta patches inside the jungle province: (bearing, offset from the
# middle of the forest band, radius). Anchored to the band like the giants.
TERRA_PRETA = [(115.0, -4.0, 5.5), (152.0, 3.0, 5.0),
               (178.0, -2.0, 6.0), (212.0, 5.0, 5.0)]

# Landmark giant redwoods in the north forest (each clears its own glade).
# Distance is computed per bearing: the midpoint of the forest band between
# the rim crest and the coastal cliffs, wherever the wavy edges put it.
GIANT_BEARINGS = [332.0, 356.0, 18.0, 48.0, 78.0]

# Serpent spawners on the deep lake floor.
SPAWNERS = [(0.0, 10.0), (140.0, 16.0)]

# Small mines around the ring. Six bore in from the outer coast (marker a
# few cells inside the coastline, heading inland); three open partway up
# the caldera wall, overlooking the lake, and dive under the rim (marker on
# the inner cliff band, heading outward). Each cave line has its own step
# budget since 0.43.0.
OUTER_CAVES = [('a', 10.0), ('b', 55.0), ('c', 135.0),
               ('d', 205.0), ('e', 250.0), ('f', 295.0)]
WALL_CAVES = [('h', 80.0), ('i', 170.0), ('j', 315.0)]

# Flooded diving caves: mouths on the deep lake floor near the center,
# tunnels full of water (flooded=1), for serpent country spelunking.
FLOODED_CAVES = [('x', 30.0, 12.0), ('y', 160.0, 20.0), ('z', 270.0, 16.0)]


def polar(bearing_deg, dist):
    b = math.radians(bearing_deg)
    return CX + dist * math.sin(b), CZ - dist * math.cos(b)


def band_mid(th):
    # Middle of the forest band between the rim crest and the coast.
    return 0.5 * ((lake_r(th) + 14.5) + (outer_r(th) - 6.0))


def in_blob(x, z, cx, cz, radius, jag):
    dx, dz = x - cx, z - cz
    th = math.atan2(dz, dx)
    lim = radius * (1.0 + jag * (0.6 * math.sin(3 * th + cx) + 0.4 * math.sin(5 * th + cz)))
    return math.hypot(dx, dz) <= lim


def main():
    # First pass: what is land.  kind: None water, 'ring', islet char.
    kind = [[None] * W for _ in range(H)]
    for r in range(H):
        for c in range(W):
            x, z = c + 0.5, r + 0.5
            dx, dz = x - CX, z - CZ
            ro = math.hypot(dx, dz)
            th = math.atan2(dz, dx)
            for (bb, rr, kk, gap) in LAKE_ISLES:
                icx, icz = isle_center(bb, rr, gap)
                if in_blob(x, z, icx, icz, rr, 0.25 if kk == 'w' else 0.14):
                    kind[r][c] = kk
                    break
            if kind[r][c] is not None:
                continue
            if lake_r(th) < ro <= outer_r(th):
                kind[r][c] = 'ring'
                continue
            for (bb, rr) in OUTCROPS:
                b = math.radians(bb)
                oth = math.atan2(-math.cos(b), math.sin(b))
                ocx, ocz = polar(bb, outer_r(oth) + 2.0)
                if in_blob(x, z, ocx, ocz, rr, 0.20):
                    kind[r][c] = 'ring'
                    break

    def is_land(c, r):
        return 0 <= c < W and 0 <= r < H and kind[r][c] is not None

    def near_water(c, r, rad):
        w = int(rad) + 1
        for dc in range(-w, w + 1):
            for dr in range(-w, w + 1):
                if dc * dc + dr * dr > rad * rad:
                    continue
                if not is_land(c + dc, r + dr):
                    return True
        return False

    def province(b, ro):
        # Wobble the sector borders so forest boundaries meander.
        b2 = (b + 7.0 * math.sin(0.13 * ro + 0.8) + 4.0 * math.sin(0.31 * ro)) % 360.0
        if 100.0 <= b2 < 235.0:
            return 'J'   # jungle south and southeast
        if 235.0 <= b2 < 305.0:
            return 'p'   # pine west
        return 'D'       # redwoods across the north

    grid = [['.'] * W for _ in range(H)]
    for r in range(H):
        for c in range(W):
            k = kind[r][c]
            x, z = c + 0.5, r + 0.5
            dx, dz = x - CX, z - CZ
            ro = math.hypot(dx, dz)
            th = math.atan2(dz, dx)
            b = math.degrees(math.atan2(dx, -dz)) % 360.0

            if k is None:
                # Flooded kelp skirt around the second green isle.
                (bb, rr, _, gap) = LAKE_ISLES[1]
                icx, icz = isle_center(bb, rr, gap)
                if math.hypot(x - icx, z - icz) <= rr + 2.5 and ro < lake_r(th):
                    grid[r][c] = 'k'
                continue
            if k in 'gw':
                grid[r][c] = k
                continue

            rel = ro - lake_r(th)
            # Black sand cove beaches, one per province.
            cove = any(min(abs(b - cb), 360.0 - abs(b - cb)) < ch and ro >= outer_r(th) - 9.0
                       for (cb, ch) in COVES)
            if cove:
                grid[r][c] = 'B'
            elif rel <= 5.5:
                grid[r][c] = 'I'   # caldera wall dropping to the lake
            elif rel <= 14.5:
                grid[r][c] = 'V'   # the volcano's rim crest
            elif near_water(c, r, 3.2):
                grid[r][c] = 'C'   # outer coastal cliffs
            elif any(math.hypot(x - polar(tb, band_mid(bearing_th(tb)) + toff)[0],
                                z - polar(tb, band_mid(bearing_th(tb)) + toff)[1]) <= tr
                     for (tb, toff, tr) in TERRA_PRETA):
                grid[r][c] = 'T' if province(b, ro) == 'J' else province(b, ro)
            else:
                grid[r][c] = province(b, ro)

    for gb in GIANT_BEARINGS:
        gx, gz = polar(gb, band_mid(bearing_th(gb)))
        gc, gr = int(gx), int(gz)
        if grid[gr][gc] != 'D':
            raise SystemExit(f"giant redwood at bearing {gb} landed on '{grid[gr][gc]}', not D")
        grid[gr][gc] = 'Q'

    for (sb, sd) in SPAWNERS:
        sx, sz = polar(sb, sd)
        sc, sr = int(sx), int(sz)
        if grid[sr][sc] != '.':
            raise SystemExit(f"serpent spawner at bearing {sb} is not in open water")
        grid[sr][sc] = 'S'

    for (ch, cb) in OUTER_CAVES:
        th = bearing_th(cb)
        cx, cz = polar(cb, outer_r(th) - 3.0)
        cc, cr = int(cx), int(cz)
        if grid[cr][cc] in '.kw g':
            raise SystemExit(f"outer cave {ch} at bearing {cb} landed on '{grid[cr][cc]}'")
        grid[cr][cc] = ch

    for (ch, cb) in WALL_CAVES:
        th = bearing_th(cb)
        cx, cz = polar(cb, lake_r(th) + 2.5)
        cc, cr = int(cx), int(cz)
        if grid[cr][cc] not in 'IV':
            raise SystemExit(f"wall cave {ch} at bearing {cb} landed on '{grid[cr][cc]}', not the caldera wall")
        grid[cr][cc] = ch

    for (ch, cb, cd) in FLOODED_CAVES:
        cx, cz = polar(cb, cd)
        cc, cr = int(cx), int(cz)
        if grid[cr][cc] != '.':
            raise SystemExit(f"flooded cave {ch} at bearing {cb} is not in open water")
        grid[cr][cc] = ch

    print("# volcano_ring - the remnant rim of an exploded volcano, 700 wide, from")
    print("# Michael's sketch: a wavy basalt ring around a 400-wide crater lake.")
    print("# Huge redwoods across the north (five landmark giants in their own")
    print("# glades), resin pine on the west, kapok jungle full of wild warm")
    print("# weather crops and terra preta patches on the south. Obsidian blends")
    print("# through the rim crest and caldera wall; black sand coves; small")
    print("# islands hug the lake's inside edges (two green, three obsidian")
    print("# spires, one kelp shallow) and two serpent spawners wait on the deep")
    print("# center floor with their trigger lifted to the surface.")
    print("# Regenerate: python tools/gen_volcano_ring.py > shapes/volcano_ring.txt")
    print("# Suggested: /genisland shape=volcano_ring diameter=700 height=46 water=60 climate=lush")
    print()
    print("region D rock=basalt fertility=high surface=grass forest=0.12 trees=redwoodpine litter=0.9 sticks=0.06 ores=copper:0.012,nickel:0.004,quartz:0.008 orebits=copper:0.002 wildgrass=0.15 scatter=eaglefern:0.020,deerfern:0.014,flyagaric:0.004,kingbolete:0.005 height=0.58 shore=12 rough=0.10")
    print("region p rock=basalt fertility=medium surface=grass forest=0.10 trees=scotspine litter=0.7 sticks=0.05 ores=copper:0.012,tin:0.005,zinc:0.005 wildgrass=0.22 bushes=blueberry:0.005,cranberry:0.003 scatter=horsetail:0.008,fieldmushroom:0.004 height=0.55 shore=12 rough=0.11")
    print("region J rock=basalt fertility=high surface=grass forest=0.11 trees=kapok,largekapok,vineykapok,purpleheart,ebony litter=0.9 sticks=0.06 ores=copper:0.012,silver:0.004,zinc:0.005 wildgrass=0.28 scatter=crop-pineapple-13:0.010,crop-cassava-7:0.010,crop-peanut-7:0.008,crop-amaranth-7:0.006,flower-croton-medium-crimson-green:0.008,flower-rafflesia-red:0.002,cinnamonfern:0.014,hartstongue:0.008 height=0.55 shore=12 rough=0.09")
    print("region T rock=basalt fertility=terrapreta surface=grass forest=0.07 trees=kapok,purpleheart litter=0.8 sticks=0.05 ores=copper:0.012 wildgrass=0.25 scatter=crop-pineapple-13:0.016,crop-cassava-7:0.016,crop-peanut-7:0.012,crop-amaranth-7:0.010,flower-croton-medium-lemongreen:0.008 height=0.55 shore=12 rough=0.09")
    print("region V rock=basalt rock2=obsidian surface=rock ores=copper:0.018,gold:0.005,quartz:0.012,cinnabar:0.006,bismuth:0.005 orebits=copper:0.003 boulders=0.018 stones=0.03 height=1.0 shore=10 rough=0.20")
    print("region I rock=basalt rock2=obsidian surface=rock ores=copper:0.012,quartz:0.010,cinnabar:0.004 boulders=0.010 stones=0.02 height=0.50 shore=3 rough=0.16")
    print("region C rock=basalt surface=rock ores=copper:0.010,quartz:0.008 boulders=0.012 stones=0.02 height=0.32 shore=3 rough=0.15")
    print("region B rock=basalt sand=sand-basalt surface=sand shells=0.02 height=0.15 shore=16 rough=0.03")
    print("region w rock=obsidian rock2=basalt surface=rock boulders=0.012 stones=0.03 height=0.55 shore=4 rough=0.26")
    print("region g rock=basalt fertility=high surface=grass forest=0.18 trees=kapok,vineykapok wildgrass=0.20 litter=0.6 height=0.30 shore=6 rough=0.12")
    print("region k rock=basalt sand=sand-basalt surface=sand flood=2 kelp=0.35 height=0.10 shore=4 rough=0.05")
    print("tree Q redwoodpine 2.2")
    print("block S underwaterhorrors:serpentspawner 60")
    # Six outer-coast digs, varied dips and sizes; heading runs inland
    # (bearing + 180), so the mouth opens in the coastal cliffs.
    for (ch, cb, dip, ln, rad, br, mo) in [
            ('a', 10.0, 18, 120, 2.4, 2, 6), ('b', 55.0, 30, 140, 2.2, 1, 8),
            ('c', 135.0, 14, 100, 2.7, 2, 5), ('d', 205.0, 26, 130, 2.0, 1, 7),
            ('e', 250.0, 22, 110, 2.5, 2, 6), ('f', 295.0, 34, 140, 2.3, 1, 9)]:
        hd = (cb + 180.0) % 360.0
        print(f"cave {ch} heading={hd:.0f} dip={dip} length={ln} radius={rad} squash=0.75 weave=0.5 scale=1 branches={br} branchdepth=1 branchlen=0.5 depth=40 mouth={mo} entry=10 ores=copper:0.05 seed=1")
    # Three caldera-wall mines: a door partway up the inner cliff over the
    # lake, diving deep under the rim.
    for (ch, cb, dip, ln, mo) in [('h', 80.0, 24, 150, 16), ('i', 170.0, 30, 160, 18), ('j', 315.0, 22, 140, 14)]:
        print(f"cave {ch} heading={cb:.0f} dip={dip} length={ln} radius=2.6 squash=0.75 weave=0.5 scale=1 branches=2 branchdepth=1 branchlen=0.5 depth=70 mouth={mo} entry=8 ores=copper:0.05 seed=1")
    # Three flooded diving caves from the deep lake floor.
    for (ch, cb, dip, ln, dp) in [('x', 30.0, 34, 110, 60), ('y', 160.0, 38, 120, 70), ('z', 270.0, 30, 100, 55)]:
        print(f"cave {ch} heading={cb:.0f} dip={dip} length={ln} radius=2.8 squash=0.8 weave=0.4 scale=1 branches=1 branchdepth=1 branchlen=0.5 depth={dp} entry=4 flooded=1 ores=copper:0.05 seed=1")
    print("deposits natural")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
