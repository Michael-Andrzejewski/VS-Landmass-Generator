"""
Starter island for players, redesigned from Michael's reference drawing:
a smooth, idyllic, mostly-grassy round island ~150 blocks across.

  - Large white-sand beach filling the whole west lobe.
  - Sparse small oak forest across the north.
  - One slate edge on the north-east: a slate-sand apron at the water with a
    small ~5 block slate cliff, then higher bare slate behind it. This is
    where the devastated mine will go, so its copper is rich.
  - Low-fertility slate ground along the south rim (kept smooth).
  - Giant oak at the centre; cattail pond just south-east of it, wrapped
    in a rich-soil meadow; wild flax meadow east of centre.

The outline is a radial harmonic curve rather than a polygon, so the coast
has no corners. All the non-slate regions use low rough values and wide
shores so the terrain rolls instead of juddering.

    python tools/gen_starter_island.py > shapes/starter_island.txt
"""
import math

W, H = 96, 90
CX, CY = 48.0, 45.0


def adist(a, b):
    """Angular distance in degrees, wrap-safe."""
    d = (a - b) % 360.0
    return d if d <= 180.0 else 360.0 - d


def coast_r(ang):
    """Coast radius (cells) at angle `ang` (deg; 0=east, 90=south)."""
    t = math.radians(ang)
    r = 39.0 * (1.0
                + 0.050 * math.sin(2 * t + 0.9)
                + 0.040 * math.sin(3 * t + 2.1)
                + 0.022 * math.sin(5 * t + 4.2))
    # Gentle west bulge: the beach lobe.
    a = adist(ang, 168.0)
    r *= 1.0 + 0.06 * math.exp(-(a / 34.0) ** 2)
    return r


def seg_dist(px, pz, ax, az, bx, bz):
    """Distance from point P to segment AB, in cells."""
    vx, vz = bx - ax, bz - az
    t = max(0.0, min(1.0, ((px - ax) * vx + (pz - az) * vz) / (vx * vx + vz * vz)))
    return math.hypot(px - (ax + t * vx), pz - (az + t * vz))


def region(c, r):
    dx, dz = c + 0.5 - CX, r + 0.5 - CY
    rho = math.hypot(dx, dz)
    ang = math.degrees(math.atan2(dz, dx))
    rc = coast_r(ang)
    if rho > rc:
        return '.'
    t = rho / rc                       # 0 at centre, 1 at the coast

    # Little pond, south-east of the giant oak.
    if ((dx - 7.0) / 5.2) ** 2 + ((dz - 14.0) / 3.6) ** 2 <= 1.0:
        return 'w'

    # Big clay vein: from the pond's east edge running toward the ocean on
    # the south-east, hidden under sparse-grass clay (region V).
    if seg_dist(dx, dz, 12.0, 14.0, 28.0, 27.0) < 2.6:
        return 'V'

    # Small hidden clay patch in the forest (region c).
    if ((dx + 8.0) / 3.5) ** 2 + ((dz + 27.0) / 2.5) ** 2 <= 1.0:
        return 'c'

    # North-east slate edge: waterline apron first, high slate behind it.
    if adist(ang, -15.0) < 50.0 and t > 0.86:
        return 'C'
    if adist(ang, -15.0) < 42.0 and t > 0.66:
        return 'R'

    # West beach lobe: deepest mid-lobe, tapering to nothing at its ends.
    # The north tip, where the beach meets the forest, is reedy (cattails).
    a = adist(ang, 168.0)
    if a < 42.0 and t > 0.55 + 0.38 * (a / 42.0) ** 2:
        return 'T' if adist(ang, -153.0) < 13.0 else 'B'

    # Low-fertility slate ground along the south rim.
    if adist(ang, 95.0) < 40.0 and t > 0.72:
        return 'L'

    # Sparse small oak forest across the north.
    if adist(ang, -95.0) < 50.0 and t > 0.40:
        return 'F'

    # Rich-soil meadow wrapping the pond.
    if ((dx - 7.0) / 11.5) ** 2 + ((dz - 14.0) / 9.5) ** 2 <= 1.0:
        return 'H'

    # Wild flax meadow, east of centre.
    if ((dx - 20.0) / 8.5) ** 2 + ((dz + 2.0) / 7.0) ** 2 <= 1.0:
        return 'X'

    return 'P'


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]

    # Giant oak at the centre.
    oak = (47, 45)
    if grid[oak[1]][oak[0]] != '.':
        grid[oak[1]][oak[0]] = 'O'

    # Haunted copper mine entrance: in the slate headland's ocean face
    # (region R, just inland of the C apron at angle -15), heading auto
    # (toward the island centre), so the adit dives down and diagonally
    # into the island.
    ang = math.radians(-15.0)
    mx = int(CX + 34.8 * math.cos(ang))
    mz = int(CY + 34.8 * math.sin(ang))
    if grid[mz][mx] == 'R':
        grid[mz][mx] = 'M'
    else:
        raise SystemExit(f"cave marker landed on '{grid[mz][mx]}' at {mx},{mz}, expected R")

    print("# starter_island - player starting island, ~150 blocks across.")
    print("# Regenerate: python tools/gen_starter_island.py > shapes/starter_island.txt")
    print("# Suggested: /genisland shape=starter_island diameter=150 height=8 stone=rock-peridotite sand=sand-peridotite")
    print("#")
    print("# Smooth and idyllic: big white-sand west beach, sparse oak forest north,")
    print("# rich meadow around the cattail pond, wild flax meadow east, low-fertility")
    print("# south rim. The one rough edge is the north-east slate headland (C apron +")
    print("# R high slate) where the devastated mine will go.")
    print("# The haunted copper mine's CAVE is carved (marker M in the slate face,")
    print("# descending diagonal adit, weaving and branching, copper-lined walls).")
    print("# Deferred (add later): ruined chest, teleporter, mine structure work")
    print("# (timbers, cobwebs, lighting) inside the cave, shoreline boulders.")
    print()
    print("region P rock=slate rock2=peridotite fertility=medium surface=grass ores=copper:0.02   bushes=raspberry:0.002,blueberry:0.002 scatter=cornflower:0.010,forgetmenot:0.010,cowparsley:0.005 height=0.70 shore=16 rough=0.08")
    print("region F rock=slate rock2=peridotite fertility=medium surface=grass ores=copper:0.02   forest=0.015 trees=oak copperbits=0.0012 bushes=raspberry:0.012 sticks=0.04 litter=0.8 scatter=fieldmushroom:0.006,flyagaric:0.003,eaglefern:0.025,deerfern:0.012,horsetail:0.010 height=0.75 shore=16 rough=0.08")
    print("region H rock=slate rock2=peridotite fertility=high   surface=grass ores=copper:0.02   bushes=cranberry:0.01 scatter=cornflower:0.015,forgetmenot:0.015,horsetail:0.010 height=0.70 shore=16 rough=0.06")
    print("region X rock=slate rock2=peridotite fertility=medium surface=grass ores=copper:0.02   flax=0.05 bushes=blackcurrant:0.005,redcurrant:0.005 scatter=catmint:0.008,cowparsley:0.006 height=0.70 shore=16 rough=0.08")
    print("region L rock=slate                  fertility=low    surface=grass ores=copper:0.02   bushes=cranberry:0.008 scatter=cowparsley:0.004 height=0.55 shore=14 rough=0.12")
    print("region B rock=slate sand=sand-chalk  surface=sand     bushes=birch:0.006,strawberry:0.003 shells=0.02 height=0.16 shore=36 rough=0.03")
    print("region T rock=slate sand=sand-chalk  surface=sand     bushes=birch:0.006,strawberry:0.003 shells=0.02 height=0.16 shore=36 rough=0.03 cattails=1.0")
    print("region C rock=slate sand=sand-slate  surface=rocksand boulders=0.015 height=0.60 shore=3  rough=0.10")
    print("region R rock=slate rock2=peridotite surface=rock     ores=copper:0.02   copperbits=0.0025 boulders=0.010 height=1.0 shore=16 rough=0.12")
    print("region V rock=slate rock2=peridotite fertility=medium surface=grass clay=0.95 ores=copper:0.02   height=0.70 shore=16 rough=0.06")
    print("region c rock=slate rock2=peridotite fertility=medium surface=grass clay=0.95 ores=copper:0.02   height=0.75 shore=16 rough=0.08")
    print("region w rock=slate rock2=peridotite fertility=medium surface=grass height=0.70 shore=16 pond=4 cattails=0.45 lilies=0.10 clay=0.5")
    print("tree O oak 2.4")
    print("cave M heading=auto dip=13 length=110 radius=2.7 squash=0.75 weave=0.55 branches=3 branchdepth=2 depth=34 mouth=3 ores=copper:0.06")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
