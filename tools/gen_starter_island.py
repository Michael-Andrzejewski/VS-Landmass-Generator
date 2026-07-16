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

    # North-east slate edge: waterline apron first, high slate behind it.
    if adist(ang, -15.0) < 50.0 and t > 0.86:
        return 'C'
    if adist(ang, -15.0) < 42.0 and t > 0.66:
        return 'R'

    # West beach lobe: deepest mid-lobe, tapering to nothing at its ends.
    a = adist(ang, 168.0)
    if a < 42.0 and t > 0.55 + 0.38 * (a / 42.0) ** 2:
        return 'B'

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

    print("# starter_island - player starting island, ~150 blocks across.")
    print("# Regenerate: python tools/gen_starter_island.py > shapes/starter_island.txt")
    print("# Suggested: /genisland shape=starter_island diameter=150 height=8")
    print("#")
    print("# Smooth and idyllic: big white-sand west beach, sparse oak forest north,")
    print("# rich meadow around the cattail pond, wild flax meadow east, low-fertility")
    print("# south rim. The one rough edge is the north-east slate headland (C apron +")
    print("# R high slate) where the devastated mine will go.")
    print("# Deferred (add later): surface copper, ruined chest, teleporter,")
    print("# devastated mine structure, shoreline boulders.")
    print()
    print("region P rock=slate rock2=peridotite fertility=medium surface=grass ores=copper:medium height=0.70 shore=16 rough=0.08")
    print("region F rock=slate rock2=peridotite fertility=medium surface=grass ores=copper:sparse forest=0.015 trees=oak height=0.75 shore=16 rough=0.08")
    print("region H rock=slate rock2=peridotite fertility=high   surface=grass ores=copper:medium height=0.70 shore=16 rough=0.06")
    print("region X rock=slate rock2=peridotite fertility=medium surface=grass ores=copper:medium flax=0.05 height=0.70 shore=16 rough=0.08")
    print("region L rock=slate                  fertility=low    surface=grass ores=copper:medium height=0.55 shore=14 rough=0.12")
    print("region B rock=slate sand=sand-chalk  surface=sand     height=0.10 shore=36 rough=0.03")
    print("region C rock=slate sand=sand-slate  surface=rocksand height=0.60 shore=3  rough=0.30")
    print("region R rock=slate rock2=peridotite surface=rock     ores=copper:rich height=1.0 shore=16 rough=0.40")
    print("region w rock=slate rock2=peridotite fertility=medium surface=grass height=0.70 shore=16 pond=4 cattails=0.45")
    print("tree O oak 2.4")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
