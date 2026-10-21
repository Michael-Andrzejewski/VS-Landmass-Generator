"""
Dangerous Swamplands: a warm drowned delta between the Lazaret and Blackfen.

Blackfen is the cold peat fen. This is its opposite number: a big, humid,
low swamp where the SEA runs inland. Rafts can enter but the way through is
a maze, and the deep middle belongs to something else.

  - Four tidal creeks wind in from the north, east, south and west coasts.
    Three of them meet in a TIDAL LAGOON at the heart of the island: real
    sea, deep enough for an Underwater Horrors serpent (spawner Z), with a
    drowned sinkhole shaft (flooded cave Q) opening in its floor.
  - Every salt edge is drowned cypress forest standing in the water
    (flood=1), and two broad DROWNED GROVES (flood=2) block the creeks'
    lower reaches: wade or walk around, a raft will not pass.
  - The dry floor is real peat over shale and claystone: bog iron
    (limonite, via the iron alias in shale) and coal in the ground,
    blue clay at the meres.
  - The west half holds the FRESH water: black meres linked by bayous,
    cattails, lilies, swamp cypress in the rims.
  - Jungle hummocks of viney kapok and bald cypress rise out of the
    marsh: the only firm, dry ground. One carries a giant STILT SHIVER
    spawner that watches the eastern drowned grove.
  - The ROT HEART in the north-east lobe is devastated: crusted soil,
    dead kapok, a giant dead landmark tree, the killer-mushroom guild,
    and a giant TAINTED DRIFTER spawner at its centre.
  - An old LEVEE ridge in the north-west is the one high place, with the
    BOG-IRON MINE (cave V) boring in at its southern face.
  - Warm, sodden climate tint (20C, rain 0.98) on every region, so it reads
    rich swamp green against Blackfen's cold dark green.

    python tools/gen_dangerous_swamplands.py > shapes/dangerous_swamplands.txt
"""
import math

W, H = 160, 112
CX, CY = 80.0, 56.0
RX, RZ = 74.0, 50.0

# Tidal creeks: polylines from the coast inward, in cells relative to the
# centre (x east, z south). Width tapers from the mouth (first point) inward.
CREEKS = [
    # (points, mouth half-width, inner half-width, wobble phase)
    ([(-10.0, 56.0), (-5.0, 38.0), (0.0, 22.0), (4.0, 8.0), (8.0, 0.0)], 2.6, 1.3, 0.7),
    ([(80.0, 8.0), (60.0, 2.0), (42.0, -8.0), (24.0, -4.0)], 2.8, 1.4, 2.3),
    ([(22.0, -56.0), (18.0, -38.0), (14.0, -20.0), (12.0, -10.0)], 2.4, 1.3, 4.1),
    ([(-80.0, -6.0), (-62.0, -2.0), (-48.0, 6.0)], 2.6, 1.2, 5.5),
]
LAGOON = (10.0, -4.0, 15.0, 10.0)        # cx, cz, rx, rz

MERES = [
    (-40.0, -16.0, 7.0, 5.0),
    (-50.0, 18.0, 6.0, 4.5),
    (-24.0, 24.0, 5.5, 4.0),
    (-30.0, -32.0, 5.0, 3.5),
]
BAYOUS = [
    ((-40.0, -16.0), (-30.0, -32.0)),
    ((-40.0, -16.0), (-50.0, 18.0)),
    ((-50.0, 18.0), (-24.0, 24.0)),
]
BAYOU_HALF = 1.6

DROWNED_GROVES = [(34.0, 16.0, 12.0, 7.0), (-6.0, 30.0, 8.0, 6.0)]   # flood=2 blobs
HUMMOCKS = [(-18.0, 2.0, 8.0), (54.0, 30.0, 6.5), (24.0, 34.0, 5.5), (-64.0, -18.0, 5.0), (-2.0, -30.0, 6.5)]
ROT_HEART = (46.0, -26.0, 12.0)
LEVEE = ((-50.0, -38.0), (-22.0, -44.0), 4.2)
SALT_EDGE = 2.4        # cells of flood=1 drowned cypress along every salt edge


def seg_dist(px, pz, ax, az, bx, bz):
    vx, vz = bx - ax, bz - az
    L2 = vx * vx + vz * vz
    t = 0.0 if L2 == 0 else max(0.0, min(1.0, ((px - ax) * vx + (pz - az) * vz) / L2))
    return math.hypot(px - (ax + t * vx), pz - (az + t * vz)), t


def coast_m(ang):
    t = math.radians(ang)
    return (1.0 + 0.070 * math.sin(2 * t + 0.5) + 0.055 * math.sin(3 * t + 1.9)
            + 0.040 * math.sin(5 * t + 3.3) + 0.028 * math.sin(8 * t + 0.7))


def coast_t(dx, dz):
    """0 at the centre, 1 on the coast (elliptic, harmonically wobbled)."""
    e = math.hypot(dx / RX, dz / RZ)
    ang = math.degrees(math.atan2(dz, dx))
    return e / coast_m(ang)


def in_creek(dx, dz):
    for pts, w0, w1, ph in CREEKS:
        n = len(pts) - 1
        for i in range(n):
            (ax, az), (bx, bz) = pts[i], pts[i + 1]
            # Meander: shift the sample point across the reach.
            wob = 2.2 * math.sin((dx * 0.21 + dz * 0.17) + ph + i)
            d, t = seg_dist(dx + wob * 0.7, dz - wob * 0.7, ax, az, bx, bz)
            s = (i + t) / n
            if d < w0 + (w1 - w0) * s:
                return True
    return False


def in_lagoon(dx, dz):
    lx, lz, rx, rz = LAGOON
    a = math.atan2(dz - lz, dx - lx)
    wob = 1.0 + 0.10 * math.sin(3 * a + 1.1) + 0.07 * math.sin(5 * a + 2.6)
    return math.hypot((dx - lx) / rx, (dz - lz) / rz) < wob


def salt(dx, dz):
    return coast_t(dx, dz) >= 1.0 or in_creek(dx, dz) or in_lagoon(dx, dz)


def region(c, r):
    dx, dz = c + 0.5 - CX, r + 0.5 - CY
    t = coast_t(dx, dz)
    if t >= 1.0 or in_creek(dx, dz) or in_lagoon(dx, dz):
        return '.'

    # Distance (in cells) to the nearest salt water, by a small ring search.
    near_salt = 99.0
    for rad in (1.0, 1.7, SALT_EDGE):
        hit = False
        for k in range(16):
            a = k * math.pi / 8
            if salt(dx + rad * math.cos(a), dz + rad * math.sin(a)):
                hit = True
                break
        if hit:
            near_salt = rad
            break

    (lax, laz), (lbx, lbz), lhw = LEVEE
    if seg_dist(dx, dz, lax, laz, lbx, lbz)[0] < lhw + 0.8 * math.sin(dx * 0.4):
        return 'K'

    hx, hz, hr = ROT_HEART
    if math.hypot(dx - hx, dz - hz) < hr + 1.2 * math.sin(math.atan2(dz - hz, dx - hx) * 4 + 0.6) and near_salt > 1.0:
        return 'R'

    for (ux, uz, ur) in HUMMOCKS:
        if math.hypot(dx - ux, dz - uz) < ur + 0.8 * math.sin(math.atan2(dz - uz, dx - ux) * 3 + ux):
            return 'J'

    # Fresh water only well inside and well away from salt.
    if t < 0.78 and near_salt > SALT_EDGE:
        for (mx, mz, rx, rz) in MERES:
            if ((dx - mx) / rx) ** 2 + ((dz - mz) / rz) ** 2 <= 1.0:
                return 'M'
        for ((ax, az), (bx, bz)) in BAYOUS:
            bend = 1.5 * math.sin((dx - dz) * 0.33 + ax)
            if seg_dist(dx + bend, dz + bend, ax, az, bx, bz)[0] < BAYOU_HALF:
                return 'N'

    for (gx, gz, rx, rz) in DROWNED_GROVES:
        wob = 1.0 + 0.15 * math.sin(dx * 0.5 + gz) + 0.1 * math.sin(dz * 0.6 + gx)
        if ((dx - gx) / rx) ** 2 + ((dz - gz) / rz) ** 2 <= wob:
            return 'X'

    if near_salt <= SALT_EDGE and t < 0.93:
        return 'W'
    if t > 0.91:
        return 'A'
    return 'F'


def cell(dx, dz):
    return int(CX + dx), int(CY + dz)


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]

    def mark(ch, dx, dz, allowed):
        x, z = cell(dx, dz)
        if grid[z][x] not in allowed:
            raise SystemExit(f"marker {ch} landed on '{grid[z][x]}' at {x},{z}")
        grid[z][x] = ch

    lx, lz, _, _ = LAGOON
    mark('Z', lx + 3.0, lz - 1.0, '.')            # serpent spawner, lagoon centre
    mark('Q', lx - 8.0, lz + 3.0, '.')            # drowned sinkhole shaft in the lagoon floor
    hx, hz, _ = ROT_HEART
    mark('H', hx, hz, 'R')                        # giant tainted drifter spawner
    mark('T', hx - 4.0, hz + 3.0, 'R')            # giant dead kapok landmark
    mark('G', 54.0, 30.0, 'J')                    # giant stilt shiver on the east hummock
    mark('Y', -18.0, 2.0, 'J')                    # landmark bald cypress on the big hummock
    # Mine mouth on the levee's NORTH face, where the ground falls away to
    # the shore within a few blocks (a mouth on the inland face looks out
    # over flat levee top and reads as buried). heading=auto still drives
    # the bore south under the swamp.
    (ax, az), (bx, bz), hw = LEVEE
    mx, mz = (ax + bx) / 2 - 4.0, (az + bz) / 2 - hw + 1.0
    mark('V', mx, mz, 'K')

    clim = "climate=20:0.98"
    rock = "rock=shale rock2=claystone"
    print("# dangerous_swamplands - a warm drowned delta: tidal creeks meet in a deep")
    print("# serpent lagoon, drowned cypress groves, fresh meres and bayous, jungle")
    print("# hummocks, a devastated rot heart, and a bog-iron mine under the levee.")
    print("# Regenerate: python tools/gen_dangerous_swamplands.py > shapes/dangerous_swamplands.txt")
    print("# Suggested: /genisland shape=dangerous_swamplands diameter=480 height=6 water=30")
    print("#")
    print("# SALT: creeks and the lagoon are ocean cells ('.'), so the sea runs")
    print("# inland and the lagoon deepens toward its middle. Every salt edge is")
    print("# flood=1 cypress shallows W; drowned groves X (flood=2) choke two")
    print("# creeks. FRESH: meres M joined by bayous N in the west, interior only.")
    print("# Hummocks J (firm, dry, jungle) are checked before the flood bands so")
    print("# they stand as islands in the marsh. Rot heart R is devastated.")
    print("# Markers: Z serpent spawner, Q flooded sinkhole cave, H giant tainted")
    print("# drifter spawner, G giant stilt shiver spawner, T/Y landmark trees,")
    print("# V bog-iron mine under the levee K.")
    print()
    print(f"region F {rock} surface=peat ores=iron:0.010,coal:0.012 forest=0.022 trees=baldcypressswamp,baldcypress,vineykapok bushes=blackberry:0.006,beautyberry:0.006 scatter=horsetail:0.012,eaglefern:0.010,fieldmushroom:0.003 wildgrass=0.26 sticks=0.04 litter=0.5 stones=0.003 height=0.46 shore=8 rough=0.04 {clim}")
    print(f"region A {rock} surface=sand sand=sand-shale shells=0.006 stones=0.004 height=0.28 shore=6 rough=0.03 {clim}")
    print(f"region W {rock} surface=sand sand=sand-shale flood=1 forest=0.045 trees=baldcypressswamp kelp=0.06 height=0.30 shore=8 rough=0.03 {clim}")
    print(f"region X {rock} surface=sand sand=sand-shale flood=2 forest=0.040 trees=baldcypressswamp,baldcypress kelp=0.10 height=0.28 shore=8 rough=0.03 {clim}")
    print(f"region M {rock} surface=peat pond=2 forest=0.030 trees=baldcypressswamp cattails=0.50 lilies=0.15 clay=0.40 height=0.46 shore=8 {clim}")
    print(f"region N {rock} surface=peat pond=2 forest=0.030 trees=baldcypressswamp cattails=0.45 lilies=0.08 clay=0.25 height=0.46 shore=8 {clim}")
    print(f"region J {rock} fertility=medium surface=grass forest=0.085 trees=vineykapok,baldcypress,kapok bushes=blackberry:0.008,beautyberry:0.006 scatter=eaglefern:0.020,horsetail:0.006,flower-croton-medium-crimson-green:0.004 wildgrass=0.32 sticks=0.05 litter=0.8 height=0.78 shore=8 rough=0.08 {clim}")
    print(f"region R {rock} surface=barren devastation=0.004 forest=0.018 trees=deadkapok scatter=deathcap:0.007,devilbolete:0.005,elfinsaddle:0.004,jackolantern:0.006,ghostpipewhite:0.004 wildgrass=0.06 sticks=0.06 height=0.44 shore=8 rough=0.05 {clim}")
    print(f"region K {rock} surface=grass fertility=low forest=0.030 trees=baldcypress,riverbirch wildgrass=0.28 stones=0.02 boulders=0.006 height=1.0 shore=5 rough=0.10 {clim}")
    print("tree T deadkapok 2.4")
    print("tree Y baldcypress 2.2")
    print("block Z underwaterhorrors:serpentspawner 40")
    print("block H oversizedhorrors:giantspawner-drifter-tainted")
    print("block G oversizedhorrors:giantspawner-shiver-stilt")
    # seed=3 from the previewer sweep on the north-face mouth: dry end to end, 970 steps.
    print("cave V heading=auto dip=24 length=170 radius=2.4 squash=0.8 weave=0.7 scale=1.1 branches=3 branchdepth=2 branchlen=0.6 pinch=0.4 depth=50 mouth=4 entry=6 ores=iron:0.08,coal:0.04 timbers=12 holders=14 seed=3")
    print("cave Q flooded=1 heading=auto dip=45 length=70 radius=2.2 squash=0.8 weave=0.5 scale=1 branches=1 branchdepth=1 branchlen=0.5 depth=22 entry=4 ores=iron:0.06 seed=3")
    print("deposits natural")
    print()
    print("loot swamp 2 cave=V")
    print("loot sunken 1 near=Z radius=50 where=underwater")
    print("loot swamp 1 near=T radius=30 where=surface")
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
