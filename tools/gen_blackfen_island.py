"""
Blackfen: a low, flat, waterlogged bog island. The anti-forester.

Nowhere to climb: the fen sits ~3 blocks over the sea, the heath hummocks a
couple higher, and the single rocky knoll (the mine mouth) is the only
"summit". When a temporal storm catches you here, there is no high ground.

  - The whole floor is real minable PEAT (surface=peat, fuel for the iron
    push), over shale with claystone blended through it: coal country.
    `ores=coal` seams everywhere plus `deposits natural`, so prospecting
    reads honestly and the coal is genuinely there.
  - Black meres (shallow ponds) dot the fen: cattails, waterlilies, blue
    clay in their rims. The largest mere is reserved for a later structure
    pass (a drowned ruin under the lilies).
  - Swamp cypress and river birch stand in the wet; scots pine and dwarf
    birch shrubs hold the drier hummocks with gorse and heather.
  - Gourmand foraging with stakes: cranberry, cloudberry and blueberry
    bushes through the fen, edible morels and field mushrooms in the open,
    and a DARK HOLLOW guild around the deep pools: deathcap, elfin saddle,
    devil bolete, ghost pipes, jack-o-lanterns glowing at dusk. Learn your
    mushrooms or die to dinner.
  - A cool sodden climate tint (6C, rain 0.95) baked into every region:
    the island reads dark damp green against Rustfall's rusty sea.
  - The BOG MINE (cave V) bores in at the knoll's face and winds down
    through the shale, walls seamed thick with coal.

Structure sites reserved for a later pass: drowned ruin in the great mere,
peat-cutter's abandoned camp on a hummock, mine props in the bog mine.

    python tools/gen_blackfen_island.py > shapes/blackfen_island.txt
"""
import math

W, H = 120, 120
CX, CY = 60.0, 60.0

MERES = [
    # (cx, cz, rx, rz) in cells relative to centre; first is the great mere.
    (16.0, 10.0, 8.0, 5.5),
    (-14.0, -6.0, 6.5, 4.5),
    (10.0, -16.0, 5.5, 4.0),
    (-4.0, 14.0, 5.0, 4.0),
    (-22.0, 12.0, 4.0, 3.0),
    (2.0, -2.0, 3.5, 3.0),
    (24.0, -4.0, 4.0, 3.0),
    (-5.0, -21.0, 4.5, 3.0),
]

# Flooded marsh wedges: the two coast inlets widen into shallow drowned
# flats (flood=1) that cut deep into the fen. Swamp cypress stands in the
# water; a raft cannot cross, a player wades. Boundaries are noise-wobbled
# so the marsh edge wanders instead of running straight.
MARSHES = [(105.0, 20.0, 0.50), (255.0, 17.0, 0.55)]   # (bearing, half-angle, t inner edge)

# River channels: winding pond=2 waterways linking the meres into one
# freshwater system (same height as the meres, so one continuous level).
CHANNELS = [
    ((16.0, 10.0), (2.0, -2.0)),
    ((2.0, -2.0), (-14.0, -6.0)),
    ((-4.0, 14.0), (2.0, -2.0)),
    ((10.0, -16.0), (2.0, -2.0)),
    ((24.0, -4.0), (16.0, 10.0)),
]
CHANNEL_HALF = 1.7


def seg_dist(px, pz, ax, az, bx, bz):
    vx, vz = bx - ax, bz - az
    L2 = vx * vx + vz * vz
    t = 0.0 if L2 == 0 else max(0.0, min(1.0, ((px - ax) * vx + (pz - az) * vz) / L2))
    return math.hypot(px - (ax + t * vx), pz - (az + t * vz))

HOLLOWS = [(-16.0, 8.0, 9.0), (12.0, -10.0, 8.0), (22.0, 2.0, 6.0), (-6.0, -20.0, 6.5)]
HUMMOCKS = [(-8.0, -30.0, 6.0), (24.0, -20.0, 5.0), (-28.0, -8.0, 5.0), (8.0, 24.0, 6.0), (-14.0, 24.0, 4.5)]

KNOLL_ANG = -48.0     # bearing of the mine knoll: north-east coast
KNOLL_T = 0.80        # how far out along that bearing (fraction of coast)
KNOLL_R = 4.5


def adist(a, b):
    d = (a - b) % 360.0
    return d if d <= 180.0 else 360.0 - d


def coast_r(ang):
    """Ragged bog coastline: strong harmonics, two soft inlets."""
    t = math.radians(ang)
    r = 52.0 * (1.0
                + 0.060 * math.sin(2 * t + 0.8)
                + 0.050 * math.sin(3 * t + 2.1)
                + 0.035 * math.sin(5 * t + 4.0)
                + 0.020 * math.sin(7 * t + 1.3))
    r *= 1.0 - 0.22 * math.exp(-(adist(ang, 105.0) / 16.0) ** 2)
    r *= 1.0 - 0.18 * math.exp(-(adist(ang, 255.0) / 14.0) ** 2)
    return r


def knoll_center():
    rho = coast_r(KNOLL_ANG) * KNOLL_T
    a = math.radians(KNOLL_ANG)
    return rho * math.cos(a), rho * math.sin(a)


def region(c, r):
    dx, dz = c + 0.5 - CX, r + 0.5 - CY
    rho = math.hypot(dx, dz)
    ang = math.degrees(math.atan2(dz, dx))
    rc = coast_r(ang)
    if rho > rc:
        return '.'
    t = rho / rc

    kx, kz = knoll_center()
    if math.hypot(dx - kx, dz - kz) < KNOLL_R:
        return 'R'

    # Meres and their features stay off the coast slope (interior only).
    if t < 0.74:
        for (mx, mz, rx, rz) in MERES:
            if ((dx - mx) / rx) ** 2 + ((dz - mz) / rz) ** 2 <= 1.0:
                return 'M'
        # Winding channels between the meres: the wobble bends each reach.
        for ((ax, az), (bx, bz)) in CHANNELS:
            bend = 1.6 * math.sin((dx + dz) * 0.35 + ax + bz)
            if seg_dist(dx + bend, dz - bend, ax, az, bx, bz) < CHANNEL_HALF:
                return 'N'
    if t < 0.82:
        for (hx, hz, hr) in HOLLOWS:
            if math.hypot(dx - hx, dz - hz) < hr:
                return 'D'
        for (ux, uz, ur) in HUMMOCKS:
            if math.hypot(dx - ux, dz - uz) < ur:
                return 'H'

    for (mb, mh, mt) in MARSHES:
        wob = 1.0 + 0.28 * math.sin(rho * 0.42 + mb * 1.7) + 0.18 * math.sin(math.radians(ang) * 3.1 + 1.2)
        if adist(ang, mb) < mh * wob and t > mt + 0.05 * math.sin(rho * 0.55 + mb):
            return 'W'

    if t > 0.90:
        return 'A'
    return 'F'


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]

    # Mine mouth: the seaward face of the knoll.
    kx, kz = knoll_center()
    rho = math.hypot(kx, kz) + KNOLL_R * 0.55
    a = math.radians(KNOLL_ANG)
    mx = int(CX + rho * math.cos(a))
    mz = int(CY + rho * math.sin(a))
    if grid[mz][mx] in ('R', 'A', 'F'):
        grid[mz][mx] = 'V'
    else:
        raise SystemExit(f"cave marker landed on '{grid[mz][mx]}' at {mx},{mz}")

    print("# blackfen_island - low waterlogged peat bog: coal, clay, bog berries,")
    print("# deadly mushrooms, black meres, and NO high ground during a storm.")
    print("# Regenerate: python tools/gen_blackfen_island.py > shapes/blackfen_island.txt")
    print("# Suggested: /genisland shape=blackfen_island diameter=260 height=7")
    print("#")
    print("# The whole floor is minable peat over shale+claystone coal country")
    print("# (ores=coal seams AND deposits natural, so propick reads honestly).")
    print("# FRESH water: meres M linked by winding river channels N into one")
    print("# waterway, swamp cypress standing in the knee-deep rims, clumped")
    print("# reeds, lilies, blue clay. SALT water: both coast inlets widen into")
    print("# flooded marsh W over dark basalt-sand beds with kelp stalks and")
    print("# cypress, ringed by dark sand beaches A (no reeds in the salt).")
    print("# Hollows D grow the killer mushroom guild; hummocks H are the only")
    print("# dry-ish ground. Knoll R hosts the BOG MINE (V), walls seamed with")
    print("# coal. Cool sodden tint everywhere. Marsh edges are noise-wobbled")
    print("# and flood depth ramps from dry land (v0.28), so no square steps.")
    print("# Structures reserved (later pass): drowned ruin in the great mere,")
    print("# peat-cutter's camp on a hummock, props in the bog mine.")
    print()
    clim = "climate=6:0.95"
    print(f"region F rock=shale rock2=claystone surface=peat ores=coal:0.02 forest=0.012 trees=baldcypressswamp,riverbirch bushes=cranberry:0.010,cloudberry:0.008,blueberry:0.005 scatter=heather:0.012,horsetail:0.010,fieldmushroom:0.004,commonmorel:0.003 wildgrass=0.22 sticks=0.03 stones=0.004 height=0.48 shore=8 rough=0.04 {clim}")
    print(f"region A rock=shale rock2=claystone surface=sand sand=sand-basalt shells=0.008 stones=0.004 height=0.30 shore=6 rough=0.03 {clim}")
    print(f"region M rock=shale rock2=claystone surface=peat pond=2 forest=0.030 trees=baldcypressswamp cattails=0.45 lilies=0.10 clay=0.35 height=0.48 shore=8 {clim}")
    print(f"region N rock=shale rock2=claystone surface=peat pond=2 forest=0.030 trees=baldcypressswamp cattails=0.45 lilies=0.06 clay=0.25 height=0.48 shore=8 {clim}")
    print(f"region W rock=shale rock2=claystone surface=sand sand=sand-basalt flood=1 forest=0.020 trees=baldcypressswamp kelp=0.07 height=0.30 shore=8 rough=0.03 {clim}")
    print(f"region D rock=shale rock2=claystone surface=peat ores=coal:0.02 forest=0.050 trees=baldcypressswamp scatter=jackolantern:0.007,deathcap:0.006,elfinsaddle:0.005,devilbolete:0.004,ghostpipewhite:0.006,ghostpipered:0.003,blacktrumpet:0.004 wildgrass=0.10 sticks=0.05 height=0.44 shore=8 rough=0.05 {clim}")
    print(f"region H rock=shale rock2=claystone fertility=low surface=grass forest=0.030 trees=scotspine,dwarfbirch bushes=birch:0.010,blueberry:0.006 scatter=westerngorse:0.008,heather:0.010,wilddaisy:0.004 wildgrass=0.30 sticks=0.03 height=0.85 shore=8 rough=0.10 {clim}")
    print(f"region R rock=shale rock2=claystone surface=rocksand boulders=0.012 stones=0.03 height=1.0 shore=4 rough=0.16 {clim}")
    # The bog mine: modest bore (the island is low, so the mouth sits just a
    # few blocks up), winding and coal-lined. mouth=4 per the flat-shore rule.
    # seed=2 from the previewer sweep: 831 steps, ZERO wet, bottom galleries
    # 63 below sea.
    print("cave V heading=auto dip=26 length=150 radius=2.4 squash=0.8 weave=0.6 scale=1.15 branches=3 branchdepth=2 branchlen=0.6 depth=55 mouth=4 entry=6 ores=coal:0.10 seed=2")
    print("deposits natural")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
