"""
Ideal house island, laid out from Michael's hand-drawn map (2026-09-16).

Reading of the drawing, west to east:

  - LEFT (west) is prairie: open grass with only the occasional tree, the
    university/barracks/arena side of the map.
  - The CENTRE thickens into real forest, and the EAST is thicker still,
    steeper and rockier, reading as the drawing's thick jungle forest.
  - The EAST COAST is a tall white ridge of marble and chalk. It meets the sea
    as a cliff at both ends, and between those ends it steps back to leave a
    thin level beach at its foot: the drawing's secluded beach, walled off from
    the island by the ridge, with one ascending path notched through.
  - NORTH is shoretown: flat, prairie-like, its coast broken by small scattered
    white-sand beaches rather than one continuous strand.
  - WILLOW LAKE sits east of centre, a 70 block mere ringed with rich meadow
    and swamp cypress standing in the shallows.
  - Michael's house keeps its flat terrace and terra preta garden, south of
    centre where the drawing puts them, and the iron headland keeps its mine on
    the south-west shoulder.

Rock is shale with marble through it, the white ridge is marble over chalk, and
peridotite blends underground as the deep rock.

Grid is 200 x 200, so at diameter=600 one cell is three blocks.

    python tools/gen_ideal_house_island.py > shapes/ideal_house_island.txt
"""
import math

W, H = 200, 200
CX, CY = 100.0, 100.0

# The command height these fractions were chosen against. The east ridge is
# meant to stand 12-15 blocks over its beach: 1.00 and 0.15 of 18 is 15.3.
SUGGEST_HEIGHT = 18


def adist(a, b):
    """Angular distance in degrees, wrap-safe."""
    d = (a - b) % 360.0
    return d if d <= 180.0 else 360.0 - d


def coast_r(ang):
    """Coast radius (cells) at angle `ang` (deg; 0=east, 90=south)."""
    t = math.radians(ang)
    r = 92.0 * (1.0
                + 0.038 * math.sin(2 * t + 0.9)
                + 0.030 * math.sin(3 * t + 2.1)
                + 0.018 * math.sin(5 * t + 4.2)
                + 0.010 * math.sin(7 * t + 1.3))
    # South-west shoulder: the iron headland pushes the coast out a little.
    r *= 1.0 + 0.045 * math.exp(-(adist(ang, 138.0) / 16.0) ** 2)
    return r


def seg_dist(px, pz, ax, az, bx, bz):
    """Distance from point P to segment AB, in cells."""
    vx, vz = bx - ax, bz - az
    t = max(0.0, min(1.0, ((px - ax) * vx + (pz - az) * vz) / (vx * vx + vz * vz)))
    return math.hypot(px - (ax + t * vx), pz - (az + t * vz))


def ell(dx, dz, cx, cz, rx, rz):
    return ((dx - cx) / rx) ** 2 + ((dz - cz) / rz) ** 2 <= 1.0


# ── the east ridge and its secluded beach ────────────────────────────────────
# The ridge spans a wider arc than the beach, so at both ends of the beach it
# runs right down to the water as a cliff and closes the pocket off.
RIDGE_A0, RIDGE_A1 = -22.0, 86.0
BEACH_A0, BEACH_A1 = 6.0, 64.0
PATH_A = 66.0                     # the one notch down through the ridge


def arc_has(ang, a0, a1):
    """True when `ang` lies in the arc a0..a1 (degrees, may be negative)."""
    return ((ang - a0) % 360.0) <= ((a1 - a0) % 360.0)


def region(c, r):
    dx, dz = c + 0.5 - CX, r + 0.5 - CY
    rho = math.hypot(dx, dz)
    ang = math.degrees(math.atan2(dz, dx))
    rc = coast_r(ang)
    if rho > rc:
        return '.'
    t = rho / rc                       # 0 at centre, 1 at the coast

    # ── Willow Lake, east of centre. 23 cells across is about 70 blocks. ──
    if ell(dx, dz, 25.0, 3.0, 12.5, 10.5):
        return 'w'

    # ── the ascending path: a saddle notched through the ridge ──
    if adist(ang, PATH_A) < 4.0 and t > 0.76:
        return 'p'

    # ── east coast: white ridge, with the beach tucked into its middle ──
    if arc_has(ang, RIDGE_A0, RIDGE_A1) and t > 0.80:
        if arc_has(ang, BEACH_A0, BEACH_A1) and t > 0.895:
            return 'B'
        return 'R'

    # ── south rim: a thin beach along the bottom of the drawing ──
    if adist(ang, 108.0) < 22.0 and t > 0.955:
        return 'B'

    # ── south-west iron headland, kept from the old island ──
    if adist(ang, 138.0) < 22.0 and t > 0.90:
        return 'D'
    if adist(ang, 138.0) < 16.0 and t > 0.76:
        return 'I'

    # ── north: shoretown's coast, small scattered white-sand beaches ──
    if adist(ang, -100.0) < 46.0 and t > 0.945:
        # pockets rather than one strand: a slow wave along the shore
        if math.sin(math.radians(ang) * 6.0 + 0.7) > 0.25:
            return 'B'

    # ── west coast: the prairie runs almost to the water ──
    if adist(ang, 178.0) < 30.0 and t > 0.965:
        return 'B'

    # ── house terrace and its garden, south of centre ──
    if ell(dx, dz, -2.0, 46.0, 21.0, 14.0):
        return 'S'
    if ell(dx, dz, -2.0, 63.0, 17.0, 4.5):
        return 'G'

    # ── rich meadow ringing the lake, and the flax meadow past it ──
    if ell(dx, dz, 25.0, 3.0, 21.0, 18.0):
        return 'H'
    if ell(dx, dz, 46.0, 30.0, 13.0, 10.0):
        return 'X'

    # ── clay vein running out of the lake's south shore ──
    if seg_dist(dx, dz, 26.0, 13.0, 44.0, 44.0) < 4.0:
        return 'V'

    # ── east: thick, steep, rocky forest behind the ridge ──
    if arc_has(ang, RIDGE_A0 - 6.0, RIDGE_A1 + 6.0) and t > 0.30 + 0.05 * math.sin(math.radians(ang) * 5.0):
        return 'J'

    # ── north: shoretown proper, flat and prairie-like ──
    if adist(ang, -100.0) < 42.0 and t > 0.50 + 0.06 * math.sin(math.radians(ang) * 7.0 + 1.1):
        return 'K'

    # ── west: prairie with only the occasional tree ──
    if adist(ang, 175.0) < 44.0 and t > 0.46 + 0.06 * math.sin(math.radians(ang) * 6.0 + 3.4):
        return 'P'

    # ── everything else is the thick forest through the middle ──
    return 'F'


def place(grid, x, z, mark, expect):
    got = grid[z][x]
    if got not in expect:
        raise SystemExit(f"marker {mark} landed on '{got}' at {x},{z}, expected one of {expect}")
    grid[z][x] = mark


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]

    # Giant oak just west of the centre, on the forest edge.
    place(grid, 92, 100, 'O', 'FPH')

    # Copper mine bored into the ridge from the SECLUDED BEACH, so the hidden
    # strand is worth the walk down the path. The mouth has to stand on the
    # sand: put it up on the ridge plateau instead and the generator reports
    # "no open air within 24 blocks seaward of the mouth, entrance may be
    # buried", because seaward of a plateau column is more plateau.
    ang = math.radians(35.0)
    place(grid, int(CX + 0.915 * coast_r(35.0) * math.cos(ang)),
          int(CY + 0.915 * coast_r(35.0) * math.sin(ang)), 'M', 'B')

    # Iron mine at the foot of the iron headland, as before.
    ang = math.radians(138.0)
    place(grid, int(CX + 0.93 * coast_r(138.0) * math.cos(ang)),
          int(CY + 0.93 * coast_r(138.0) * math.sin(ang)), 'N', 'D')

    print("# ideal_house_island - laid out from Michael's hand-drawn map.")
    print("# Regenerate: python tools/gen_ideal_house_island.py > shapes/ideal_house_island.txt")
    print(f"# Suggested: /genisland shape=ideal_house_island diameter=600 height={SUGGEST_HEIGHT}")
    print("#")
    print("# West is open prairie, the centre thickens into forest and the east is")
    print("# thicker, steeper and rockier. The east coast is a tall white marble ridge")
    print("# that meets the sea as a cliff at both ends and steps back in the middle to")
    print("# leave a thin level beach at its foot (B), walled off from the island, with")
    print("# one ascending path (p) notched through. North is flat shoretown prairie")
    print("# with small scattered white-sand beaches. Willow Lake (w) is a 70 block mere")
    print("# east of centre. Rock is shale with marble through it, the ridge is marble")
    print("# over chalk, peridotite blends underground as the deep rock.")
    print()
    # Ground. Heights are fractions of the command height; at height=18 the
    # ridge stands 18 blocks and its beach 2.7, so the wall over the sand is
    # about 15 blocks, which is what the drawing asks for.
    print("region P rock=shale rock2=peridotite fertility=medium surface=grass forest=0.004 trees=englishoak,silverbirch bushes=raspberry:0.004,blueberry:0.003 scatter=cornflower:0.012,forgetmenot:0.012,cowparsley:0.008,wilddaisy:0.006 wildgrass=0.40 height=0.45 shore=30 rough=0.07")
    print("region K rock=shale rock2=peridotite fertility=medium surface=grass forest=0.003 trees=englishoak bushes=blueberry:0.003 scatter=cornflower:0.014,forgetmenot:0.010,wilddaisy:0.008,cowparsley:0.006 wildgrass=0.42 height=0.40 shore=30 rough=0.04")
    print("region F rock=shale rock2=whitemarble fertility=medium surface=grass forest=0.055 trees=englishoak,sugarmaple,norwaymaple,silverbirch,walnut,scotspine bushes=raspberry:0.012,blackberry:0.008 sticks=0.05 litter=0.85 scatter=fieldmushroom:0.007,chanterelle:0.005,eaglefern:0.028,deerfern:0.014,horsetail:0.008 height=0.58 shore=30 rough=0.09")
    print("region J rock=shale rock2=whitemarble fertility=medium surface=grass forest=0.075 trees=kapok,vineykapok,largekapok,purpleheart,ebony,acacia bushes=blackberry:0.010,blackcurrant:0.006 sticks=0.06 litter=0.9 stones=0.020 boulders=0.004 scatter=eaglefern:0.035,deerfern:0.018,fieldmushroom:0.006,horsetail:0.010 height=0.75 shore=22 rough=0.16 climate=lush")
    print("region R rock=whitemarble rock2=chalk sand=sand-chalk surface=rock ores=copper:0.02 orebits=copper:0.0020 boulders=0.012 stones=0.020 height=0.88 shore=4  rough=0.14")
    print("region p rock=whitemarble rock2=chalk sand=sand-chalk fertility=low surface=grass wildgrass=0.25 stones=0.015 scatter=heather:0.008 height=0.45 shore=12 rough=0.06")
    print("region B rock=whitemarble sand=sand-chalk surface=sand bushes=birch:0.005,strawberry:0.003 shells=0.030 height=0.18 shore=8  rough=0.03")
    print("region S rock=shale rock2=peridotite fertility=medium surface=grass wildgrass=0.12 stones=0.002 scatter=cornflower:0.005 height=0.50 shore=30 rough=0.02")
    print("region G rock=shale rock2=peridotite fertility=terrapreta surface=grass wildgrass=0.15 flax=0.02 scatter=cornflower:0.012,forgetmenot:0.012,catmint:0.008 height=0.50 shore=30 rough=0.03")
    print("region H rock=shale rock2=peridotite fertility=high surface=grass forest=0.010 trees=riverbirch bushes=cranberry:0.010 scatter=cornflower:0.016,forgetmenot:0.016,horsetail:0.014 height=0.55 shore=30 rough=0.05")
    print("region X rock=shale rock2=peridotite fertility=medium surface=grass flax=0.05 bushes=blackcurrant:0.005,redcurrant:0.005 scatter=catmint:0.008,cowparsley:0.006 height=0.50 shore=30 rough=0.07")
    print("region V rock=shale rock2=peridotite fertility=medium surface=grass clay=0.95 height=0.56 shore=30 rough=0.05")
    print("region D rock=claystone sand=sand-claystone surface=rocksand boulders=0.015 ores=coal:0.02 height=0.50 shore=6  rough=0.10")
    print("region I rock=claystone rock2=shale surface=rock ores=iron:0.03,coal:0.03 orebits=iron:0.0025 boulders=0.010 height=0.85 shore=30 rough=0.14")
    print("region w rock=shale rock2=peridotite fertility=high surface=grass pond=5 forest=0.030 trees=baldcypressswamp cattails=0.40 lilies=0.10 clay=0.4 height=0.55 shore=30")
    print("tree O oak 2.4")
    print("cave M heading=auto dip=18 length=190 radius=2.7 squash=0.75 weave=0.45 scale=0.8 branches=5 branchdepth=2 branchlen=0.7 depth=45 mouth=5 entry=6 ores=copper:0.06 seed=12")
    print("cave N heading=auto dip=16 length=220 radius=3.0 squash=0.75 weave=0.5 scale=0.9 branches=5 branchdepth=2 branchlen=0.7 branchradius=0.6 depth=55 mouth=5 entry=6 ores=iron:0.06,coal:0.04 seed=7")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
