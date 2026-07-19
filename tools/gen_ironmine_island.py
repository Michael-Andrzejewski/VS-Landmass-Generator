"""
Iron Mine island: a very large (560 wide) iron-country island, ~30 tall,
ringed by steep cliffs and deep water. From Michael's sketch:

- The main mass sits east, its west coast torn into deep fjord-like lobes
  ("jagged outcrops"), with a scatter of small slate spire islets standing
  off it in the western water.
- The north interior is bare mountainous granite, rough and hard to walk.
- The south is a scots pine forest on red-grey rock.
- The east face holds a VAST cavern entrance ringed in reddish rock,
  boring horizontally like the starter island's mine but far bigger:
  it branches many times and levels out at y=8, right above the mantle.

Iron everywhere, split by rock province (verified 1.22 allowedVariants):
  chert/shale (red-grey main body, south)  -> limonite  (+coal, quartz, lead, zinc)
  granite     (north mountains)            -> hematite  (+quartz, copper, tin, bismuth)
  slate       (west lobes + spire islets)  -> magnetite (+ilmenite, quartz, copper)
The cave's ores=iron lines its walls per actual wall rock, so the deep
galleries read limonite under the south, hematite under the mountains.
`deposits natural` re-runs the game's own ore pass on top, so the propick
reads honest vanilla ore maps in every province.

    python tools/gen_ironmine_island.py > shapes/ironmine_island.txt

Suggested: /genisland shape=ironmine_island diameter=560 height=30 water=45
"""
import math

W, H = 240, 240

BODY_CX, BODY_CZ = 152.0, 118.0
BODY_RZ = 100.0

# West coast lobes: (cx, cz, rx, rz). They overlap the body's receded west
# edge, so the gaps between them become deep inlets.
LOBES = [
    (86.0,  34.0, 30.0, 17.0),
    (78.0,  84.0, 34.0, 19.0),
    (82.0, 132.0, 30.0, 17.0),
    (90.0, 182.0, 34.0, 20.0),
]

# Jagged outcrop islets standing in the western water: slate spire cones.
ISLETS = [
    (34.0,  28.0, 5.0, 4.0),
    (20.0,  74.0, 4.0, 3.5),
    (30.0, 118.0, 5.5, 4.5),
    (22.0, 162.0, 4.0, 3.5),
    (14.0, 206.0, 3.5, 3.0),
]

# The vast cavern's mouth on the east face; the reddish rock ring radius.
MOUTH_C, MOUTH_R = 221, 105
RED_RING = 15.0

# Two small sand coves at inlet heads, the island's only landings: the
# rest of the coast is cliff by design.
COVES = [(98.0, 58.0, 7.5), (100.0, 158.0, 7.5)]


def in_body(x, z):
    dx, dz = x - BODY_CX, z - BODY_CZ
    # East half reaches further out than the west, so the west edge sits
    # deep and the lobes + inlets carve real fjords into it.
    rx = 65.0 + 9.0 * (dx / (abs(dx) + 30.0))
    th = math.atan2(dz, dx)
    e = math.hypot(dx / rx, dz / BODY_RZ)
    lim = 1.0 + 0.05 * math.sin(3 * th + 1.7) + 0.035 * math.sin(5 * th + 0.4) \
        + 0.025 * math.sin(7 * th + 3.1)
    return e <= lim


def in_blob(x, z, cx, cz, rx, rz, jag):
    dx, dz = x - cx, z - cz
    th = math.atan2(dz, dx)
    e = math.hypot(dx / rx, dz / rz)
    lim = 1.0 + jag * (0.6 * math.sin(3 * th + cx) + 0.4 * math.sin(5 * th + cz))
    return e <= lim


def land_at(x, z):
    """None = water, 'body' = main mass or lobe, 'islet' = outcrop spire."""
    for (cx, cz, rx, rz) in ISLETS:
        if in_blob(x, z, cx, cz, rx, rz, 0.22):
            return 'islet'
    if in_body(x, z):
        return 'body'
    for (cx, cz, rx, rz) in LOBES:
        if in_blob(x, z, cx, cz, rx, rz, 0.10):
            return 'body'
    return None


def main():
    kind = [[land_at(c + 0.5, r + 0.5) for c in range(W)] for r in range(H)]

    def is_land(c, r):
        return 0 <= c < W and 0 <= r < H and kind[r][c] is not None

    def near_water(c, r, rad):
        w = int(rad) + 1
        for dc in range(-w, w + 1):
            for dr in range(-w, w + 1):
                if dc * dc + dr * dr > rad * rad:
                    continue
                cc, rr = c + dc, r + dr
                if not (0 <= cc < W and 0 <= rr < H) or kind[rr][cc] is None:
                    return True
        return False

    def province(c, r):
        # Wobbled zone edges so rock and forest borders meander instead of
        # running ruler-straight across the island.
        ce = c + 7.0 * math.sin(r * 0.09 + 1.3) + 4.0 * math.sin(r * 0.23)
        re = r + 7.0 * math.sin(c * 0.08 + 0.5) + 4.0 * math.sin(c * 0.19 + 2.2)
        if ce < 100:
            return 'W'
        if re < 92:
            return 'M'
        if re < 112:
            return 'm'
        if re < 148:
            return 'G'
        return 'P'

    grid = [['.'] * W for _ in range(H)]
    for r in range(H):
        for c in range(W):
            k = kind[r][c]
            if k is None:
                continue
            x, z = c + 0.5, r + 0.5
            if k == 'islet':
                grid[r][c] = 'w'
                continue
            if any(math.hypot(x - cx, z - cz) <= cr for (cx, cz, cr) in COVES):
                grid[r][c] = 'B'
                continue
            if math.hypot(x - MOUTH_C, z - MOUTH_R) <= RED_RING:
                grid[r][c] = 'R'
                continue
            p = province(c, r)
            # Coastal cliff band in the province's own rock; the slate west
            # carries its cliffs in the W region's own small shore instead.
            if p != 'W' and near_water(c, r, 3.2):
                grid[r][c] = 'N' if p in 'Mm' else 'C'
                continue
            grid[r][c] = p

    if grid[MOUTH_R][MOUTH_C] == '.':
        raise SystemExit(f"cave marker at {MOUTH_C},{MOUTH_R} is not on land")
    grid[MOUTH_R][MOUTH_C] = 'K'

    print("# ironmine_island - a 560-wide iron island from Michael's sketch:")
    print("# steep cliffs ringed by deep water, a mountainous granite north that")
    print("# is hard to walk, a scots pine south on red-grey chert/shale, a torn")
    print("# fjord west coast with slate spire outcrops standing offshore, and a")
    print("# VAST cavern on the east face ringed in red chert. The cave branches")
    print("# many times and levels out at y=8, right above the mantle; its walls")
    print("# carry iron matched to whatever rock they pass (limonite in chert,")
    print("# hematite in granite, magnetite in slate). deposits natural adds the")
    print("# game's own ore maps on top so the propick reads true.")
    print("# Regenerate: python tools/gen_ironmine_island.py > shapes/ironmine_island.txt")
    print("# Suggested: /genisland shape=ironmine_island diameter=560 height=30 water=45")
    print()
    print("region M rock=granite surface=rock ores=hematite:0.034,quartz:0.018,copper:0.012,tin:0.005,bismuth:0.004 orebits=iron:0.002 boulders=0.020 stones=0.035 height=1.0 shore=12 rough=0.28")
    print("region m rock=granite fertility=low surface=grass forest=0.015 trees=scotspine ores=hematite:0.024,quartz:0.012,copper:0.010 wildgrass=0.18 stones=0.025 boulders=0.008 scatter=edelweiss:0.006,mugwort:0.004 height=0.80 shore=14 rough=0.18")
    print("region G rock=chert rock2=shale fertility=low surface=grass forest=0.030 trees=scotspine litter=0.5 sticks=0.03 ores=limonite:0.032,coal:0.012,quartz:0.010,copper:0.006 wildgrass=0.25 bushes=blueberry:0.004,cranberry:0.004 scatter=horsetail:0.008,eaglefern:0.010,fieldmushroom:0.004 height=0.68 shore=14 rough=0.10")
    print("region P rock=chert rock2=shale fertility=medium surface=grass forest=0.070 trees=scotspine litter=0.9 sticks=0.06 ores=limonite:0.042,coal:0.014,quartz:0.010,lead:0.007,zinc:0.006 orebits=iron:0.002 wildgrass=0.20 bushes=blueberry:0.006,cloudberry:0.003 scatter=flyagaric:0.004,kingbolete:0.005,deerfern:0.012,horsetail:0.008 height=0.62 shore=12 rough=0.09")
    print("region C rock=chert rock2=shale surface=rock ores=limonite:0.028,quartz:0.012 boulders=0.012 stones=0.02 height=0.55 shore=3 rough=0.16")
    print("region N rock=granite surface=rock ores=hematite:0.028,quartz:0.012 boulders=0.012 stones=0.02 height=0.55 shore=3 rough=0.16")
    print("region R rock=chert surface=rock ores=limonite:0.055,quartz:0.015,sulfur:0.008 orebits=iron:0.003 boulders=0.022 stones=0.03 height=1.10 shore=6 rough=0.20")
    print("region W rock=slate surface=rock ores=magnetite:0.032,titanium:0.010,quartz:0.014,copper:0.008 orebits=iron:0.002 boulders=0.015 stones=0.03 height=0.70 shore=7 rough=0.22")
    print("region w rock=slate surface=rock ores=magnetite:0.032,quartz:0.014 boulders=0.015 stones=0.03 height=0.55 shore=9 rough=0.30")
    print("region B rock=chert sand=sand-chert surface=sand shells=0.02 height=0.15 shore=16 rough=0.03")
    # One cave, the whole island's 8000-step budget: a vast main bore
    # (radius x scale = 13.2, the carve cap) diving at 24 degrees to depth
    # 115, which clamps at the y=8 floor just above the mantle. mouth=14
    # keeps the huge entry ellipsoid clear of the sea per the wide-mouth
    # papercut. branchradius=0.45 halves the width per branch level, so
    # side passages run ~half the main bore and their branches ~a quarter:
    # long narrow twisting galleries off one vast artery, with room events
    # still blowing out the occasional large cavern at any depth.
    print("cave K heading=270 dip=24 length=430 radius=5.5 squash=0.8 weave=0.6 scale=2.4 branches=5 branchdepth=3 branchlen=0.6 branchradius=0.45 pinch=0.6 depth=115 mouth=14 entry=16 ores=iron:0.06 seed=1")
    print("deposits natural")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
