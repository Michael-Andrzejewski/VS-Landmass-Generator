"""
The Serpent's Coil: an island that IS a spiral. You realize the shape of
the island from the water too late.

The landmass is the spine trick run along an Archimedean spiral: land is
every point within half-thickness of the spiral curve. Sailing "into" the
island means following one narrowing water channel between coils, black
sand walls on both sides, until it opens into the EYE: a sheltered pool
around a tiny nest islet. The serpent's head (the thick inner end of the
coil) looms over the pool, and the gullet cave opens in its face.

  - Basalt body with OBSIDIAN blended through the underground: glassy
    black flesh under dark rock. Black basalt-sand fringes every shore.
  - A DORSAL FIN of crimson king maples runs the crest of the whole body:
    a line of dark red canopies tracing the spiral from above.
  - The head (s < 0.15 of the curve) is bare tall rock; the tail end
    tapers to a low sand spit, the obvious raft landing. Devastation
    touches the body lightly: the serpent corrupts what it coils around.
  - The GULLET (cave V) opens in the head's face over the eye pool and
    bores down through the head: quartz-flecked black galleries.
  - The nest islet N at dead centre is reserved for a later structure
    pass: an Underwater Horrors nest (serpent/kraken spawner) and the
    hoard.

Humid dark tint (18C rain 0.85) baked into the land regions.

    python tools/gen_serpentcoil_island.py > shapes/serpentcoil_island.txt
"""
import math

W, H = 140, 140
CX, CY = 70.0, 70.0

R0 = 15.0            # spiral centre-line radius at the inner terminus
B = 4.8              # radial growth per radian: pitch = 2*pi*B ~ 30 cells
THETA_MAX = 2.9 * math.pi   # ~1.45 turns
NEST_R = 2.8
POOL_R = 6.5


def half_thickness(s):
    """Body half-thickness (cells) at curve fraction s: 0 head, 1 tail tip."""
    return 3.0 + 5.5 * (1.0 - s) ** 1.1


# Dense sample of the spiral centre-line (about 0.5 cells apart).
POINTS = []
n = 900
for i in range(n + 1):
    th = THETA_MAX * i / n
    r = R0 + B * th
    POINTS.append((r * math.cos(th), r * math.sin(th), i / n))


def nearest(dx, dz):
    """Distance to the spiral centre-line and the curve fraction there."""
    best, bs = 1e9, 0.0
    for (px, pz, s) in POINTS:
        d = (dx - px) ** 2 + (dz - pz) ** 2
        if d < best:
            best, bs = d, s
    return math.sqrt(best), bs


def region(c, r):
    dx, dz = c + 0.5 - CX, r + 0.5 - CY
    rho = math.hypot(dx, dz)

    if rho < NEST_R:
        return 'N'
    if rho < POOL_R:
        return '.'          # the eye pool

    d, s = nearest(dx, dz)
    half = half_thickness(s)
    if d > half:
        return '.'

    if half - d < 1.7:
        return 'A'          # black sand fringe, both sides of every coil
    if s > 0.93:
        return 'T'          # tail spit: the landing
    if s < 0.15:
        return 'H'          # the head: bare tall rock
    if d < 1.3:
        return 'F'          # dorsal fin: crimson king maples on the crest
    return 'B'


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]

    # Gullet mouth: the head's inner face, over the eye pool.
    th = 0.5
    rr = R0 + B * th - (half_thickness(0.5 * th / THETA_MAX) - 1.5)
    vx = int(CX + rr * math.cos(th))
    vz = int(CY + rr * math.sin(th))
    if grid[vz][vx] in ('H', 'A', 'B', 'F'):
        grid[vz][vx] = 'V'
    else:
        raise SystemExit(f"cave marker landed on '{grid[vz][vx]}' at {vx},{vz}")

    # Bore outward, away from the eye, through the head coil.
    ux, uz = math.cos(th), math.sin(th)
    heading = math.degrees(math.atan2(ux, -uz)) % 360.0

    print("# serpentcoil_island - a spiral serpent: one water channel between black")
    print("# coils, narrowing to the eye pool, the head and its gullet cave at the end.")
    print("# Regenerate: python tools/gen_serpentcoil_island.py > shapes/serpentcoil_island.txt")
    print("# Suggested: /genisland shape=serpentcoil_island diameter=320 height=24")
    print("#")
    print("# Basalt body, obsidian blended underground, black sand fringes. Crimson")
    print("# king maples run the crest like a dorsal fin. Land at the tail spit T,")
    print("# sail or walk the coil inward; the head H looms over the eye pool and")
    print("# the gullet (V) opens in its face. Light devastation on the body.")
    print("# Nest islet N (centre) reserved for a later pass: Underwater Horrors")
    print("# nest spawner + hoard.")
    print()
    clim = "climate=18:0.85"
    print(f"region B rock=basalt rock2=obsidian fertility=low surface=grass devastation=0.008 scatter=devgrowth-thorns:0.008,devgrowth-shard:0.004 wildgrass=0.12 stones=0.02 sandy=0.06 height=0.78 shore=7 rough=0.14 {clim}")
    print(f"region F rock=basalt rock2=obsidian fertility=low surface=grass forest=0.12 trees=crimsonkingmaple litter=0.7 sticks=0.04 wildgrass=0.10 height=0.85 shore=7 rough=0.12 {clim}")
    print(f"region H rock=basalt rock2=obsidian surface=rocksand boulders=0.015 stones=0.03 height=1.0 shore=6 rough=0.22 {clim}")
    print(f"region A rock=basalt surface=sand shells=0.010 height=0.20 shore=8 rough=0.05 {clim}")
    print(f"region T rock=basalt surface=sand shells=0.020 cattails=0.08 height=0.22 shore=12 rough=0.04 {clim}")
    print(f"region N rock=basalt surface=sand shells=0.040 boulders=0.030 height=0.35 shore=6 rough=0.10 {clim}")
    # The gullet: bores outward through the head coil and dives under the
    # channel. Quartz flecks the black walls. seed=4 from the previewer
    # sweep: 460 steps, ZERO wet, bottom galleries 61 below sea.
    print(f"cave V heading={heading:.0f} dip=28 length=140 radius=2.4 squash=0.8 weave=0.5 scale=1.25 branches=2 branchdepth=2 branchlen=0.6 depth=55 mouth=4 entry=6 ores=quartz:0.05 seed=4")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
