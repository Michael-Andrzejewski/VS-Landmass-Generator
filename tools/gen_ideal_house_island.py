"""
Ideal house island: the starter island grown to 600 blocks and given a site
for Michael's ideal house. Same bones as starter_island (radial harmonic
coast, white-sand west beach, oak forest north, slate headland with the
copper mine north-east, cattail pond with its rich meadow, flax meadow,
low-fertility south rim, giant oak) at four times the width, plus:

  - S  house site: a big flat grass terrace north-west of the centre,
       kept clean (no bushes, thin wild grass) so it can be built on.
  - G  terra preta garden strip along the south edge of the house site.
  - A  arboretum: a mixed wood of every temperate tree the game grows, so
       every tree seed can be gathered without leaving the island.
  - I  iron headland: a claystone/shale rise on the south-west coast with
       coal and iron in the rock, and a second mine (marker N) bored into
       it. Copper + iron + coal on one island is the road to steel.
  - a second, larger lake ('w') east of the house, ringed by rich meadow.

Grid is 2x the starter's (192 x 180), so at diameter=600 one cell is about
three blocks. Cell coordinates in this file are the starter's times two.

    python tools/gen_ideal_house_island.py > shapes/ideal_house_island.txt
"""
import math

S = 2.0                    # cell scale relative to the starter island
W, H = 192, 180
CX, CY = 96.0, 90.0


def adist(a, b):
    """Angular distance in degrees, wrap-safe."""
    d = (a - b) % 360.0
    return d if d <= 180.0 else 360.0 - d


def coast_r(ang):
    """Coast radius (cells) at angle `ang` (deg; 0=east, 90=south)."""
    t = math.radians(ang)
    r = 39.0 * S * (1.0
                    + 0.050 * math.sin(2 * t + 0.9)
                    + 0.040 * math.sin(3 * t + 2.1)
                    + 0.022 * math.sin(5 * t + 4.2)
                    + 0.012 * math.sin(7 * t + 1.3))
    # Gentle west bulge: the beach lobe.
    a = adist(ang, 168.0)
    r *= 1.0 + 0.06 * math.exp(-(a / 34.0) ** 2)
    # South-west shoulder: the iron headland pushes the coast out a little.
    a = adist(ang, 132.0)
    r *= 1.0 + 0.05 * math.exp(-(a / 16.0) ** 2)
    return r


def seg_dist(px, pz, ax, az, bx, bz):
    """Distance from point P to segment AB, in cells."""
    vx, vz = bx - ax, bz - az
    t = max(0.0, min(1.0, ((px - ax) * vx + (pz - az) * vz) / (vx * vx + vz * vz)))
    return math.hypot(px - (ax + t * vx), pz - (az + t * vz))


def ell(dx, dz, cx, cz, rx, rz):
    return ((dx - cx) / rx) ** 2 + ((dz - cz) / rz) ** 2 <= 1.0


def region(c, r):
    dx, dz = c + 0.5 - CX, r + 0.5 - CY
    rho = math.hypot(dx, dz)
    ang = math.degrees(math.atan2(dz, dx))
    rc = coast_r(ang)
    if rho > rc:
        return '.'
    t = rho / rc                       # 0 at centre, 1 at the coast

    # Cattail pond south-east of the giant oak (starter's pond, scaled).
    if ell(dx, dz, 7.0 * S, 14.0 * S, 5.2 * S, 3.6 * S):
        return 'w'
    # The lake: a bigger mere east of the house site, north of the flax.
    if ell(dx, dz, 26.0, -30.0, 12.0, 8.0):
        return 'w'

    # Big clay vein from the pond's east edge toward the south-east coast.
    if seg_dist(dx, dz, 12.0 * S, 14.0 * S, 28.0 * S, 27.0 * S) < 2.6 * S:
        return 'V'

    # Small hidden clay patch in the forest.
    if ell(dx, dz, -8.0 * S, -27.0 * S, 3.5 * S, 2.5 * S):
        return 'c'

    # North-east slate edge: waterline apron first, high slate behind it.
    if adist(ang, -15.0) < 50.0 and t > 0.86:
        return 'C'
    if adist(ang, -15.0) < 42.0 and t > 0.66:
        return 'R'

    # South-west iron headland: rocky apron at the water, claystone rise
    # behind it, both narrower than the slate headland.
    if adist(ang, 132.0) < 24.0 and t > 0.88:
        return 'D'
    if adist(ang, 132.0) < 18.0 and t > 0.72:
        return 'I'

    # West beach lobe, reedy at its north tip.
    a = adist(ang, 168.0)
    if a < 42.0 and t > 0.55 + 0.38 * (a / 42.0) ** 2:
        return 'T' if adist(ang, -153.0) < 13.0 else 'B'

    # Low-fertility ground along the south rim.
    if adist(ang, 95.0) < 40.0 and t > 0.72:
        return 'L'

    # House site: flat terrace north-west of the centre.
    if ell(dx, dz, -22.0, -14.0, 22.0, 15.0):
        return 'S'
    # Terra preta garden strip hugging the site's south edge.
    if ell(dx, dz, -22.0, 4.0, 18.0, 4.5):
        return 'G'

    # Arboretum: between the forest and the beach on the west.
    if adist(ang, -135.0) < 22.0 and 0.50 < t < 0.90:
        return 'A'

    # Oak forest across the north.
    if adist(ang, -95.0) < 50.0 and t > 0.40:
        return 'F'

    # Rich meadow wrapping the pond and the lake.
    if ell(dx, dz, 7.0 * S, 14.0 * S, 11.5 * S, 9.5 * S):
        return 'H'
    if ell(dx, dz, 26.0, -30.0, 19.0, 14.0):
        return 'H'

    # Wild flax meadow, east of centre.
    if ell(dx, dz, 20.0 * S, -2.0 * S, 8.5 * S, 7.0 * S):
        return 'X'

    return 'P'


def place(grid, x, z, mark, expect):
    got = grid[z][x]
    if got not in expect:
        raise SystemExit(f"marker {mark} landed on '{got}' at {x},{z}, expected one of {expect}")
    grid[z][x] = mark


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]

    # Giant oak at the centre, as on the starter.
    place(grid, 95, 90, 'O', 'PHS')

    # Copper mine: base of the slate headland's waterline apron (angle -15).
    ang = math.radians(-15.0)
    place(grid, int(CX + 36.6 * S * math.cos(ang)), int(CY + 36.6 * S * math.sin(ang)), 'M', 'C')

    # Iron mine: base of the iron headland's apron (angle 132).
    ang = math.radians(132.0)
    place(grid, int(CX + 37.4 * S * math.cos(ang)), int(CY + 37.4 * S * math.sin(ang)), 'N', 'D')

    print("# ideal_house_island - the starter island at 600 blocks with a site for the ideal house.")
    print("# Regenerate: python tools/gen_ideal_house_island.py > shapes/ideal_house_island.txt")
    print("# Suggested: /genisland shape=ideal_house_island diameter=600 height=18 stone=rock-peridotite sand=sand-peridotite")
    print("#")
    print("# Starter island bones (white-sand west beach, oak forest north, slate")
    print("# headland + copper mine north-east, cattail pond and rich meadow, flax")
    print("# meadow east, low-fertility south rim, giant oak) at four times the width.")
    print("# New: S flat house terrace north-west of centre, G terra preta garden on its")
    print("# south edge, A arboretum of every temperate tree, a lake east of the house,")
    print("# and an iron headland (D apron, I claystone rise) with a coal-and-iron mine (N)")
    print("# on the south-west coast.")
    print()
    print("region P rock=slate rock2=peridotite fertility=medium surface=grass ores=copper:0.02   bushes=raspberry:0.002,blueberry:0.002 scatter=cornflower:0.010,forgetmenot:0.010,cowparsley:0.005 height=0.62 shore=40 rough=0.08")
    print("region S rock=slate rock2=peridotite fertility=medium surface=grass ores=copper:0.02   wildgrass=0.12 stones=0.003 scatter=cornflower:0.004 height=0.62 shore=40 rough=0.02")
    print("region G rock=slate rock2=peridotite fertility=terrapreta surface=grass ores=copper:0.02 wildgrass=0.15 flax=0.02 scatter=cornflower:0.012,forgetmenot:0.012,catmint:0.008 height=0.62 shore=40 rough=0.03")
    print("region F rock=slate rock2=peridotite fertility=medium surface=grass ores=copper:0.02   forest=0.015 trees=oak orebits=copper:0.0012 bushes=raspberry:0.012 sticks=0.04 litter=0.8 scatter=fieldmushroom:0.006,flyagaric:0.003,eaglefern:0.025,deerfern:0.012,horsetail:0.010 height=0.68 shore=40 rough=0.08")
    print("region A rock=slate rock2=peridotite fertility=medium surface=grass ores=copper:0.02   forest=0.020 trees=englishoak,silverbirch,scotspine,sugarmaple,norwaymaple,walnut,larch,fir,riverbirch,himalayanbirch bushes=blackberry:0.006,blueberry:0.006 sticks=0.04 litter=0.8 scatter=fieldmushroom:0.006,eaglefern:0.020,deerfern:0.010 height=0.66 shore=40 rough=0.08")
    print("region H rock=slate rock2=peridotite fertility=high   surface=grass ores=copper:0.02   bushes=cranberry:0.01 scatter=cornflower:0.015,forgetmenot:0.015,horsetail:0.010 height=0.62 shore=40 rough=0.06")
    print("region X rock=slate rock2=peridotite fertility=medium surface=grass ores=copper:0.02   flax=0.05 bushes=blackcurrant:0.005,redcurrant:0.005 scatter=catmint:0.008,cowparsley:0.006 height=0.62 shore=40 rough=0.08")
    print("region L rock=slate                  fertility=low    surface=grass ores=copper:0.02   bushes=cranberry:0.008 scatter=cowparsley:0.004 height=0.50 shore=36 rough=0.12")
    print("region B rock=slate sand=sand-chalk  surface=sand     bushes=birch:0.006,strawberry:0.003 shells=0.02 height=0.14 shore=90 rough=0.03")
    print("region T rock=slate sand=sand-chalk  surface=sand     bushes=birch:0.006,strawberry:0.003 shells=0.02 height=0.14 shore=90 rough=0.03 cattails=1.0")
    print("region C rock=slate sand=sand-slate  surface=rocksand boulders=0.015 height=0.55 shore=6  rough=0.10")
    print("region R rock=slate rock2=peridotite surface=rock     ores=copper:0.02   orebits=copper:0.0025 boulders=0.010 height=1.0 shore=40 rough=0.12")
    print("region D rock=claystone sand=sand-claystone surface=rocksand boulders=0.015 ores=coal:0.02 height=0.50 shore=6  rough=0.10")
    print("region I rock=claystone rock2=shale  surface=rock     ores=iron:0.03,coal:0.03 orebits=iron:0.0025 boulders=0.010 height=0.90 shore=36 rough=0.14")
    print("region V rock=slate rock2=peridotite fertility=medium surface=grass clay=0.95 ores=copper:0.02   height=0.62 shore=40 rough=0.06")
    print("region c rock=slate rock2=peridotite fertility=medium surface=grass clay=0.95 ores=copper:0.02   height=0.68 shore=40 rough=0.08")
    print("region w rock=slate rock2=peridotite fertility=medium surface=grass height=0.62 shore=40 pond=4 cattails=0.45 lilies=0.10 clay=0.5")
    print("tree O oak 2.4")
    # Copper mine as on the starter, stretched for the bigger headland.
    print("cave M heading=auto dip=18 length=180 radius=2.7 squash=0.75 weave=0.45 scale=0.8 branches=5 branchdepth=2 branchlen=0.7 depth=45 mouth=3 entry=6 ores=copper:0.06 seed=12")
    # Iron mine: wider bore, coal and iron in the walls, twisting branches.
    print("cave N heading=auto dip=16 length=220 radius=3.0 squash=0.75 weave=0.5 scale=0.9 branches=5 branchdepth=2 branchlen=0.7 branchradius=0.6 depth=55 mouth=5 entry=6 ores=iron:0.06,coal:0.04 seed=7")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
