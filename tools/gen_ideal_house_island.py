"""
Ideal house island: the LANDFORM TEMPLATE, second pass (2026-09-16).

This pass is deliberately about shape, not biome. Michael's note on the first
cut was "don't change the biomes yet, I just want a template, we'll add layers
later", so the region list is cut from fifteen to nine and each one carries
only what it needs to read as ground: grass, sand, rock, water.

What the drawing and his marked-up screenshots ask for:

  - A clean grassy prairie over most of the west, south-west and north.
  - A beach along the BOTTOM that is broad at its west end and thins away
    eastward into a strip, with a short ridge rising beside that thin end.
  - The ridge therefore FADES OUT toward the west: its inner edge climbs from
    0.76 to past the beach line, so the band closes to nothing. The first cut
    ran it at a constant depth all the way round to the south and left a grey
    slab across the bottom of the island, which is the "random stone patch" he
    crossed out.
  - The iron headland is gone for the same reason: it was the other grey patch.
  - Surface stone on the right reads as SLATE. Marble is the second rock, so it
    comes through deeper in the ridgeside, and the white is the beach sand.
  - Fewer trees everywhere except the dense block on the east, and fewest of
    all over the prairie at the bottom left.

Every region edge is a threshold plus a sum of sines, so nothing sits on a
clean arc. That is what "smoother and more organic lines" costs: the wobble
has to go on the boundary function itself, not on the outline.

Grid is 200 x 200, so at diameter=600 one cell is three blocks.

    python tools/gen_ideal_house_island.py > shapes/ideal_house_island.txt
"""
import math

W, H = 200, 200
CX, CY = 100.0, 100.0
SUGGEST_HEIGHT = 18


def adist(a, b):
    """Angular distance in degrees, wrap-safe."""
    d = (a - b) % 360.0
    return d if d <= 180.0 else 360.0 - d


def arc_has(ang, a0, a1):
    """True when `ang` lies in the arc a0..a1 (degrees, may be negative)."""
    return ((ang - a0) % 360.0) <= ((a1 - a0) % 360.0)


def wob(ang, *terms):
    """Sum of sines on an angle, in degrees. Each term is (amp, freq, phase).

    Every region threshold gets one of these. A bare threshold draws a perfect
    arc and reads as machinery from the air; three incommensurate harmonics
    read as a coastline.
    """
    return sum(a * math.sin(math.radians(ang) * f + p) for a, f, p in terms)


def clamp01(v):
    return 0.0 if v < 0.0 else (1.0 if v > 1.0 else v)


def coast_r(ang):
    """Coast radius (cells) at angle `ang` (deg; 0=east, 90=south)."""
    return 92.0 * (1.0 + wob(ang,
                             (0.040, 2, 0.9),
                             (0.030, 3, 2.1),
                             (0.018, 5, 4.2),
                             (0.011, 7, 1.3),
                             (0.007, 11, 5.0)))


def ell(dx, dz, cx, cz, rx, rz):
    return ((dx - cx) / rx) ** 2 + ((dz - cz) / rz) ** 2 <= 1.0


# ── the beach and the ridge beside its thin end ──────────────────────────────
# The beach runs from its EAST end round the bottom to its WEST end. It is a
# strip at the east, under the ridge, and opens into a broad strand by the time
# it reaches the south-west.
BEACH_E, BEACH_W = 48.0, 156.0
# The ridge covers the east coast and dies away before it reaches the south.
RIDGE_E, RIDGE_W = -26.0, 100.0


def beach_inner(ang):
    u = clamp01((ang - BEACH_E) / (BEACH_W - BEACH_E))
    return 0.945 - 0.085 * u + wob(ang, (0.012, 5, 1.1), (0.008, 9, 2.7))


def ridge_inner(ang):
    """Climbs westward so the rock band pinches out instead of wrapping the
    bottom of the island in grey."""
    u = clamp01((ang - RIDGE_E) / (RIDGE_W - RIDGE_E))
    return 0.755 + 0.235 * (u ** 1.5) + wob(ang, (0.022, 4, 0.5), (0.014, 7, 2.2))


def region(c, r):
    dx, dz = c + 0.5 - CX, r + 0.5 - CY
    rho = math.hypot(dx, dz)
    ang = math.degrees(math.atan2(dz, dx))
    rc = coast_r(ang)
    if rho > rc:
        return '.'
    t = rho / rc                       # 0 at centre, 1 at the coast

    # Willow Lake, east of centre: 25 x 21 cells reads as 71 x 61 of water.
    if ell(dx, dz, 25.0, 3.0, 12.5, 10.5):
        return 'w'

    # The beach, and the ridge behind its eastern half.
    if arc_has(ang, BEACH_E, BEACH_W) and t > beach_inner(ang):
        return 'B'
    if arc_has(ang, RIDGE_E, RIDGE_W) and t > ridge_inner(ang):
        return 'R'

    # A few small strands elsewhere on the coast, so the rest of the shore is
    # not uniformly grass to the waterline.
    if t > 0.955 + wob(ang, (0.02, 6, 0.7)) and wob(ang, (1.0, 5, 2.4)) > 0.45:
        return 'B'

    # House terrace and its garden, south of centre.
    if ell(dx, dz, -6.0, 44.0, 22.0, 15.0):
        return 'S'
    if ell(dx, dz, -6.0, 62.0, 17.0, 4.5):
        return 'G'

    # Rich rim around the lake.
    if ell(dx, dz, 25.0, 3.0, 21.0, 18.0):
        return 'H'

    # The dense block: east and north-east, behind the ridge.
    if arc_has(ang, -58.0, 86.0) and t > 0.30 + wob(ang, (0.06, 3, 1.4), (0.035, 6, 0.2)):
        return 'J'

    # Prairie over the west, south-west and north.
    if t > 0.40 + wob(ang, (0.09, 2, 2.9), (0.05, 5, 0.6), (0.03, 8, 4.1)):
        return 'P'

    # and moderate forest through the middle.
    return 'F'


def place(grid, x, z, mark, expect):
    got = grid[z][x]
    if got not in expect:
        raise SystemExit(f"marker {mark} landed on '{got}' at {x},{z}, expected one of {expect}")
    grid[z][x] = mark


def find_on(grid, deg, want, t_from=0.99, t_to=0.80):
    """Walk inward along a bearing and return the first cell of region `want`.

    Hunting for a marker spot by picking a radius by hand breaks every time a
    threshold moves, and the failure is a build error rather than something you
    notice in the render. Searching for the region is stable.
    """
    ang = math.radians(deg)
    rc = coast_r(deg)
    steps = int(abs(t_from - t_to) * 400) + 1
    for i in range(steps):
        t = t_from + (t_to - t_from) * i / (steps - 1)
        x = int(CX + t * rc * math.cos(ang))
        z = int(CY + t * rc * math.sin(ang))
        if 0 <= x < W and 0 <= z < H and grid[z][x] == want:
            return x, z
    raise SystemExit(f"no '{want}' cell on bearing {deg} between t {t_from} and {t_to}")


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]

    # Giant oak west of centre, out on the prairie edge.
    place(grid, 92, 100, 'O', 'FPH')

    # Both mines bore into the ridge FROM THE SAND. A mouth needs ground that
    # falls away in front of it within about 24 blocks: put one up on the ridge
    # and the generator reports the entrance as buried, because seaward of a
    # plateau column is more plateau.
    # Two cells in from the waterline: right on the coast a mouth can drown.
    x, z = find_on(grid, 56.0, 'B', 0.975, 0.90)
    place(grid, x, z, 'M', 'B')
    x, z = find_on(grid, 104.0, 'B', 0.975, 0.86)
    place(grid, x, z, 'N', 'B')

    print("# ideal_house_island - landform template, second pass.")
    print("# Regenerate: python tools/gen_ideal_house_island.py > shapes/ideal_house_island.txt")
    print(f"# Suggested: /genisland shape=ideal_house_island diameter=600 height={SUGGEST_HEIGHT}")
    print("#")
    print("# Shape only: nine regions, no biome detail yet. Clean grass prairie over the")
    print("# west and north, moderate forest through the middle, a dense block east, and")
    print("# a beach along the bottom that is broad in the south-west and thins eastward")
    print("# into a strip under a short slate ridge. The ridge's inner edge climbs")
    print("# westward so the rock band pinches out instead of wrapping the bottom.")
    print("# Surface stone reads slate; marble is the second rock, deeper in the")
    print("# ridgeside, and the white is the beach sand.")
    print()
    print("region P rock=shale rock2=peridotite fertility=medium surface=grass forest=0.0015 trees=englishoak scatter=cornflower:0.010,forgetmenot:0.008,wilddaisy:0.006 wildgrass=0.40 height=0.45 shore=30 rough=0.05")
    print("region F rock=shale rock2=whitemarble fertility=medium surface=grass forest=0.030 trees=englishoak,sugarmaple,silverbirch,scotspine sticks=0.04 litter=0.8 scatter=eaglefern:0.020,fieldmushroom:0.005 height=0.55 shore=30 rough=0.07")
    print("region J rock=slate rock2=whitemarble fertility=medium surface=grass forest=0.070 trees=kapok,vineykapok,largekapok,purpleheart,ebony sticks=0.05 litter=0.9 stones=0.018 scatter=eaglefern:0.030,deerfern:0.015 height=0.70 shore=24 rough=0.14")
    print("region R rock=slate rock2=whitemarble sand=sand-chalk surface=rock ores=copper:0.02 boulders=0.010 stones=0.020 height=0.88 shore=4  rough=0.13")
    print("region B rock=slate sand=sand-chalk surface=sand shells=0.025 height=0.18 shore=8  rough=0.03")
    print("region H rock=shale rock2=peridotite fertility=high surface=grass forest=0.008 trees=riverbirch clay=0.25 scatter=horsetail:0.012,cornflower:0.012 height=0.52 shore=30 rough=0.04")
    print("region S rock=shale rock2=peridotite fertility=medium surface=grass wildgrass=0.12 stones=0.002 height=0.48 shore=30 rough=0.02")
    print("region G rock=shale rock2=peridotite fertility=terrapreta surface=grass wildgrass=0.15 height=0.48 shore=30 rough=0.03")
    print("region w rock=shale rock2=peridotite fertility=high surface=grass pond=5 cattails=0.35 lilies=0.10 clay=0.4 height=0.52 shore=30")
    print("tree O oak 2.4")
    print("cave M heading=auto dip=18 length=190 radius=2.7 squash=0.75 weave=0.45 scale=0.8 branches=5 branchdepth=2 branchlen=0.7 depth=45 mouth=5 entry=6 ores=copper:0.06 seed=12")
    print("cave N heading=auto dip=16 length=220 radius=3.0 squash=0.75 weave=0.5 scale=0.9 branches=5 branchdepth=2 branchlen=0.7 branchradius=0.6 depth=55 mouth=5 entry=6 ores=iron:0.06,coal:0.04 seed=7")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
