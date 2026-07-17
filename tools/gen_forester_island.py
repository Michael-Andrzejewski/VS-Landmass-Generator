"""
Forester island: a mountainous maple island, ~300 blocks across.

The outline is the landmass Michael liked from the first tin-island draft
(rounded coast with a deep west bay), re-rendered on a finer 120-cell grid.
The new design turns it into maple mountain country:

  - A crescent RIDGE of bare granite crags (R) wraps the bay from north
    around east to south, with three peak knots. High maple forest (H) hangs
    below the crags, mid slopes (M) below that, and the valley floor (P)
    drains west into the bay. All of it is dense maple: sugar and norway
    maple low, mountain maple high, each band with its own mushroom guild.
  - Inside the ridge crescent sits a hidden alpine dell (I): a high meadow
    with japanese maples, wild vegetable crops gone feral, and a small tarn
    (w) with cattails and waterlilies at its centre.
  - The bay head has a sand cove (B) with reeds, a logging-camp clearing (G)
    on the south shelf (wild rye and roots growing around the old site), and
    a steep rocksand apron (C) on the north wall where the DEEP MINE (cave
    marker V) bores in just above the waterline and dives under the ridge.
  - A dark hollow (D) of crimson king maples crowds the mine entrance:
    jack-o-lantern mushrooms glowing at dusk, deathcaps and devil boletes
    under the litter.

Geology is granite with claystone blended through the underground, then
`deposits natural`: the game's own ore pass runs over the island, so quartz
(with gold/silver pockets), coal seams in the claystone, copper and whatever
else the local ore maps carry appear exactly as vanilla worldgen would have
placed them. No hand-placed ore anywhere.

    python tools/gen_forester_island.py > shapes/forester_island.txt
"""
import math

W, H = 120, 120
CX, CY = 60.0, 60.0


def adist(a, b):
    """Angular distance in degrees, wrap-safe."""
    d = (a - b) % 360.0
    return d if d <= 180.0 else 360.0 - d


def coast_r(ang):
    """Coast radius (cells) at angle `ang` (deg; 0=east, 90=south)."""
    t = math.radians(ang)
    r = 51.5 * (1.0
                + 0.045 * math.sin(2 * t + 1.7)
                + 0.035 * math.sin(3 * t + 0.4)
                + 0.020 * math.sin(5 * t + 3.1))
    # The deep bay carved out of the west side.
    a = adist(ang, 185.0)
    r *= 1.0 - 0.68 * math.exp(-(a / 46.0) ** 2)
    return r


# The ridge is a circular arc (a crescent) wrapping the bay: centre east of
# the island's middle, opening toward the west so the bay stays low.
RIDGE_CX, RIDGE_CZ = 5.0, 0.0
RIDGE_R = 26.0
RIDGE_SPAN = 122.0            # degrees each side of due east


def ridge_dist(dx, dz):
    """Distance (cells) to the ridge crest arc."""
    ax, az = dx - RIDGE_CX, dz - RIDGE_CZ
    phi = math.degrees(math.atan2(az, ax))
    rho = math.hypot(ax, az)
    if abs(phi) <= RIDGE_SPAN:
        return abs(rho - RIDGE_R)
    e = math.radians(RIDGE_SPAN if phi > 0 else -RIDGE_SPAN)
    ex, ez = RIDGE_R * math.cos(e), RIDGE_R * math.sin(e)
    return math.hypot(ax - ex, az - ez)


# Peak knots widen the crag band into summits at three points on the arc.
PEAKS = [math.radians(a) for a in (-75.0, 10.0, 85.0)]


def peak_dist(dx, dz):
    best = 999.0
    for a in PEAKS:
        px = RIDGE_CX + RIDGE_R * math.cos(a)
        pz = RIDGE_CZ + RIDGE_R * math.sin(a)
        best = min(best, math.hypot(dx - px, dz - pz))
    return best


def region(c, r):
    dx, dz = c + 0.5 - CX, r + 0.5 - CY
    rho = math.hypot(dx, dz)
    ang = math.degrees(math.atan2(dz, dx))
    rc = coast_r(ang)
    if rho > rc:
        return '.'
    t = rho / rc                       # 0 at centre, 1 at the coast

    rd = ridge_dist(dx, dz)
    pd = peak_dist(dx, dz)
    inner = math.hypot(dx - RIDGE_CX, dz - RIDGE_CZ)

    # The tarn and its dell fill the bowl inside the ridge crescent.
    if inner < 3.2:
        return 'w'
    if inner < 8.0:
        return 'I'

    # Bay head, west: sand cove at the deepest point, then the mine apron on
    # the north wall (order matters: the cove claims its arc first).
    if adist(ang, 181.0) < 7.0 and t > 0.84:
        return 'B'
    if adist(ang, -166.0) < 10.0 and t > 0.78:
        return 'C'

    # Dark hollow inland of the mine apron.
    if adist(ang, -164.0) < 16.0 and 0.52 < t <= 0.78:
        return 'D'

    # Logging camp clearing on the bay's south shelf.
    if ((dx + 13.0) / 6.5) ** 2 + ((dz - 7.0) / 4.5) ** 2 <= 1.0:
        return 'G'

    # The mountain bands, crest outward: summit knots poke above a narrow
    # broken crag band, then the maple bands descend.
    if pd < 5.5:
        return 'S'
    if rd < 3.2:
        return 'R'
    if rd < 9.5:
        return 'H'
    if rd < 17.0:
        return 'M'

    return 'P'


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]

    # Deep-mine entrance: on the rocksand apron of the bay's north wall, just
    # above the waterline, boring east (heading auto -> island centre) and
    # diving under the ridge toward the dell.
    ang = math.radians(-166.0)
    rho = coast_r(-166.0) * 0.90
    mx = int(CX + rho * math.cos(ang))
    mz = int(CY + rho * math.sin(ang))
    if grid[mz][mx] == 'C':
        grid[mz][mx] = 'V'
    else:
        raise SystemExit(f"cave marker landed on '{grid[mz][mx]}' at {mx},{mz}, expected C")

    # A giant landmark sugar maple on the camp clearing's edge.
    tx, tz = int(CX - 13.0), int(CY + 7.0)
    if grid[tz][tx] in ('G', 'P'):
        grid[tz][tx] = 'A'
    else:
        raise SystemExit(f"landmark tree landed on '{grid[tz][tx]}' at {tx},{tz}")

    print("# forester_island - mountainous maple island with a west bay, ~300 blocks.")
    print("# Regenerate: python tools/gen_forester_island.py > shapes/forester_island.txt")
    print("# Suggested: /genisland shape=forester_island diameter=300 height=34")
    print("#")
    print("# Granite crag crescent (R) with three summit knots (S) wraps the bay;")
    print("# forest bands H/M/P descend from it, a hidden dell (I) with a tarn (w)")
    print("# sits inside the crescent. Bay head: sand cove B, logging clearing G,")
    print("# rocksand apron C with the deep mine (V) diving under the ridge, dark")
    print("# hollow D (crimson king maples, glowing jack-o-lanterns) at its door.")
    print("# Ore is all `deposits natural`: the game's own quartz/coal/copper/etc,")
    print("# mirroring what prospecting reads in this part of the world. Claystone")
    print("# blended into the granite hosts the coal seams.")
    print("# Deferred (add later): logging camp structures, mine timbers/lighting,")
    print("# teleporter.")
    print()
    print("region P rock=granite rock2=claystone fertility=medium surface=grass forest=0.050 trees=sugarmaple,norwaymaple litter=0.9 sticks=0.05 bushes=raspberry:0.006,blueberry:0.006 scatter=chanterelle:0.006,kingbolete:0.005,commonmorel:0.004,fieldmushroom:0.004,eaglefern:0.020 height=0.40 shore=16 rough=0.09")
    print("region M rock=granite rock2=claystone fertility=medium surface=grass forest=0.060 trees=sugarmaple,mountainmaple litter=1.0 sticks=0.06 scatter=flyagaric:0.005,saffronmilkcap:0.005,indigomilkcap:0.003,violetwebcap:0.003,deerfern:0.015 height=0.64 shore=16 rough=0.12")
    print("region H rock=granite rock2=claystone fertility=low    surface=grass forest=0.040 trees=mountainmaple,sugarmaplesmall litter=0.8 sticks=0.04 boulders=0.006 scatter=redwinecap:0.004,puffball:0.004,witchhat:0.003,horsetail:0.008 height=0.82 shore=16 rough=0.15")
    print("region S rock=granite rock2=claystone fertility=low    surface=rock boulders=0.020 height=1.0  shore=14 rough=0.22")
    print("region R rock=granite rock2=claystone fertility=low    surface=rock boulders=0.015 height=0.90 shore=14 rough=0.20")
    print("region I rock=granite rock2=claystone fertility=high   surface=grass forest=0.008 trees=japanesemaple bushes=redcurrant:0.008,blackcurrant:0.008 scatter=crop-carrot-7:0.006,crop-turnip-5:0.006,crop-parsnip-8:0.005,crop-onion-7:0.005,crop-cabbage-10:0.004,cornflower:0.012,forgetmenot:0.010 height=0.55 shore=16 rough=0.06")
    print("region w rock=granite rock2=claystone fertility=medium surface=grass height=0.55 shore=16 pond=3 cattails=0.5 lilies=0.08 clay=0.4")
    print("region G rock=granite rock2=claystone fertility=high   surface=grass forest=0.004 trees=sugarmaple sticks=0.06 scatter=crop-rye-9:0.010,crop-carrot-7:0.005,crop-turnip-5:0.005,catmint:0.008,cowparsley:0.006,fieldmushroom:0.004 height=0.40 shore=16 rough=0.06")
    print("region D rock=granite rock2=claystone fertility=low    surface=grass forest=0.075 trees=crimsonkingmaple,mountainmaple litter=1.0 wildgrass=0.10 sticks=0.07 scatter=jackolantern:0.008,deathcap:0.004,devilbolete:0.003,elfinsaddle:0.004,blacktrumpet:0.004,earthball:0.003 height=0.50 shore=16 rough=0.10")
    print("region C rock=granite surface=rocksand boulders=0.015 height=0.50 shore=3 rough=0.12")
    print("region B rock=granite surface=sand shells=0.015 cattails=0.35 bushes=birch:0.005 height=0.16 shore=30 rough=0.03")
    print("tree A sugarmaple 2.6")
    # Deep mine: generous bore with real chambers (scale), three branch arms,
    # diving at 20 over a long main gallery. seed=26 picked in the previewer
    # sweep of 70 seeds: 865 steps, ONE step lost to the water guard, bottom
    # galleries 58 below sea.
    print("cave V heading=auto dip=20 length=170 radius=2.8 squash=0.8 weave=0.5 scale=1.35 branches=3 branchdepth=2 branchlen=0.6 depth=70 mouth=2 entry=6 seed=26")
    print("deposits natural")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
