"""
Tin island, third design: still a clear crescent bent around a sheltered
harbor with a defined west entrance, but thicker through the arms, a little
wider, and with a more natural coastline (extra harmonics and a fatter south
arm). All-basalt geology. The mouth opens WEST at rotate=0; use the
command's rotate= option to aim it at the starter island.

Geometry: the landmass is a band around a circular spine (radius SPINE_R
cells), fat at the back of the crescent and tapering toward the horn tips,
with a gap (the harbor mouth) left where the spine would cross the west.

Character (per Michael's brief): tall and rocky, verylow fertility, worn
sparse-grass ground (surface=barren) near the sand, basalt-sand beach ring
along every waterline plus wind-blown sand drifts inland (sandy=), sparse
faded grass (pair with climate=arid for the rusty tint), heavy devastation,
little vegetation.

  - Basalt-sand beach ring (A) along the whole waterline, harbor included.
  - Rocky ridge (R) runs along the crescent's crest.
  - Devastation briars (D) own the north arm and its tip.
  - Scattered devastation sections + rusty gear piles (E) on the outer
    east rim.
  - Surface tin patches north (T, future ruined laboratory) and south (S,
    by the future tin mine); rocky tin-mine knoll (O) on the south arm.
  - Worn sparse-grass ground (B) on the inner harbor slope and part of the
    south outer coast.
  - Small maple pockets (M); wild pumpkin patch with real vines and debris
    (K, pumpkins=) centre-north; rusty debris (J) centre-south.
  - 2% cassiterite through the core.

Structure sites reserved for a later pass (NOT built here): ruined
laboratory half underground (north arm, by T), teleporter beside it,
tin mine (knoll O).

    python tools/gen_tin_island.py > shapes/tin_island.txt
"""
import math

W, H = 110, 110
CX, CY = 55.0, 55.0

SPINE_R = 33.0        # spine circle radius, cells
MOUTH_HALF = 34.0     # harbor mouth half-angle, degrees (mouth faces west)
ARM_END = 140.0       # |phi| at the horn tips


def wrap(a):
    """Wrap to (-180, 180]."""
    a = (a + 180.0) % 360.0 - 180.0
    return 180.0 if a == -180.0 else a


def spine(ang):
    """Effective spine radius and half-thickness at angle `ang` (0=east)."""
    t = math.radians(ang)
    a_eff = SPINE_R * (1.0
                       + 0.030 * math.sin(2 * t + 1.7)
                       + 0.020 * math.sin(5 * t + 0.4)
                       + 0.015 * math.sin(7 * t + 3.9))
    phi = wrap(ang)
    frac = min(1.0, abs(phi) / ARM_END)          # 0 back, 1 at the tips
    half = (4.2 + 11.3 * math.cos(frac * math.pi / 2) ** 0.9) \
        * (1.0 + 0.10 * math.sin(3 * t + 2.6)) \
        * (1.0 + 0.12 * math.sin(t))             # south arm runs fatter
    return a_eff, half


def base_region(c, r):
    dx, dz = c + 0.5 - CX, r + 0.5 - CY
    rho = math.hypot(dx, dz)
    ang = math.degrees(math.atan2(dz, dx))       # 0=east, +90=south
    phi = wrap(ang)
    if abs(phi) > ARM_END:                       # the harbor mouth gap
        return '.'

    a_eff, half = spine(ang)
    u = (rho - a_eff) / half                     # -1 harbor side, +1 ocean side
    if abs(u) > 1.0:
        return '.'

    # Rocky tin-mine knoll, south arm (future mine structure site).
    if 100.0 <= phi <= 116.0 and abs(u) < 0.55:
        return 'O'

    # Surface tin patches: north arm (future laboratory) and south arm.
    if -104.0 <= phi <= -85.0 and -0.7 <= u <= 0.5:
        return 'T'
    if 76.0 <= phi <= 92.0 and -0.5 <= u <= 0.7:
        return 'S'

    # Devastation briars: the whole north arm crest and its horn tip.
    if phi < -118.0:
        return 'D'
    if phi < -55.0 and u > -0.35:
        return 'D'

    # Outer east rim: scattered devastation sections and rusty gear piles.
    if abs(phi) < 55.0 and u > 0.5:
        return 'E'

    # Small maple pockets: harbor-facing east slope, and the SW lobe.
    if abs(phi - 8.0) < 12.0 and -0.7 <= u <= -0.3:
        return 'M'
    if 118.0 <= phi <= 136.0 and abs(u) < 0.55:
        return 'M'

    # Pumpkin patch centre-north; rusty debris and machinery centre-south.
    if -50.0 <= phi <= -38.0 and -0.55 <= u <= -0.05:
        return 'K'
    if 35.0 <= phi <= 55.0 and -0.5 <= u <= 0.3:
        return 'J'

    # Rocky ridge along the crescent's crest.
    if abs(u) < 0.28:
        return 'R'

    # Worn ground: the inner harbor slope, and part of the south outer coast.
    if u < -0.68:
        return 'B'
    if u > 0.62 and 75.0 <= phi <= 110.0:
        return 'B'

    return 'P'


def main():
    grid = [[base_region(c, r) for c in range(W)] for r in range(H)]

    # Basalt-sand beach ring: any land cell within ~2 cells of open water,
    # harbor shore included, becomes region A.
    ring = [row[:] for row in grid]
    for r in range(H):
        for c in range(W):
            if grid[r][c] == '.':
                continue
            near_water = False
            for dz in range(-2, 3):
                for dx in range(-2, 3):
                    if dx * dx + dz * dz > 4:
                        continue
                    nc, nr = c + dx, r + dz
                    if nc < 0 or nr < 0 or nc >= W or nr >= H or grid[nr][nc] == '.':
                        near_water = True
                        break
                if near_water:
                    break
            if near_water:
                ring[r][c] = 'A'
    grid = ring

    # Tin mine adit: at the harbor-side foot of the knoll (O), so the mouth
    # opens onto the sheltered water. heading is MANUAL: auto would aim at
    # the grid centre, which for a crescent is the middle of the harbor.
    # 198 map degrees bores south-south-west, straight into the arm under
    # the knoll.
    phi_m = 108.0
    a_eff, half = spine(phi_m)
    rho = a_eff - 0.75 * half
    ang = math.radians(phi_m)
    mx = int(CX + rho * math.cos(ang))
    mz = int(CY + rho * math.sin(ang))
    if grid[mz][mx] in ('A', 'B', 'O'):
        grid[mz][mx] = 'V'
    else:
        raise SystemExit(f"cave marker landed on '{grid[mz][mx]}' at {mx},{mz}, expected A/B/O")

    print("# tin_island - thick basalt crescent around a sheltered harbor, semi-devastated.")
    print("# Regenerate: python tools/gen_tin_island.py > shapes/tin_island.txt")
    print("# Suggested: /genisland shape=tin_island diameter=220 height=14 stone=rock-basalt sand=sand-basalt climate=arid")
    print("# The harbor mouth opens WEST at rotate=0; add rotate=<deg clockwise> to aim")
    print("# it at the starter island (rotate=90: the mouth opens north).")
    print("#")
    print("# All-basalt, rusty arid tint, verylow fertility, sparse faded grass. Basalt")
    print("# sand rings the whole waterline and drifts across the island (sandy=). Rock")
    print("# ridge along the crest, briars on the north arm, gears + devastation on the")
    print("# east rim, tin surface patches north and south, tin-mine knoll south, wild")
    print("# pumpkin patch with vines centre-north. 2% cassiterite through the core.")
    print("# Structure sites reserved (later pass): ruined laboratory half underground")
    print("# (north arm, region T), teleporter beside it, tin mine (knoll O).")
    print()
    print("region A rock=basalt surface=sand    ores=tin:0.02 stones=0.015 shells=0.006 height=0.22 shore=7 rough=0.06")
    print("region P rock=basalt fertility=verylow surface=grass ores=tin:0.02 orebits=tin:0.0008 wildgrass=0.10 stones=0.02 sandy=0.14 scatter=fieldmushroom:0.0015 height=0.66 shore=10 rough=0.14")
    print("region R rock=basalt surface=rocksand ores=tin:0.02 orebits=tin:0.0015 boulders=0.010 stones=0.035 height=1.0 shore=7 rough=0.32")
    print("region D rock=basalt fertility=verylow surface=grass ores=tin:0.02 devastation=0.012 scatter=devgrowth-thorns:0.030,devgrowth-bush:0.018,devgrowth-shard:0.010 wildgrass=0.05 stones=0.02 sandy=0.10 height=0.88 shore=8 rough=0.24")
    print("region E rock=basalt fertility=verylow surface=grass ores=tin:0.02 devastation=0.014 scatter=loosegears-2:0.006,loosegears-4:0.004,devgrowth-thorns:0.010,devgrowth-shard:0.006 wildgrass=0.05 stones=0.02 sandy=0.10 height=0.78 shore=9 rough=0.20")
    print("region B rock=basalt fertility=verylow surface=barren ores=tin:0.02 wildgrass=0 stones=0.025 sandy=0.20 height=0.45 shore=8 rough=0.10")
    print("region M rock=basalt fertility=low    surface=grass ores=tin:0.02 forest=0.010 trees=maple,sugarmaplesmall sticks=0.02 litter=0.6 wildgrass=0.15 sandy=0.05 scatter=fieldmushroom:0.003 height=0.74 shore=10 rough=0.12")
    print("region K rock=basalt fertility=low    surface=grass ores=tin:0.02 pumpkins=0.02 wildgrass=0.12 sandy=0.06 scatter=metalpartpile-tiny:0.004,loosegears-1:0.003 height=0.70 shore=10 rough=0.10")
    print("region J rock=basalt fertility=verylow surface=grass ores=tin:0.02 devastation=0.006 scatter=metalpartpile-tiny:0.008,metalpartpile-small:0.005,metal-scraps:0.003,loosegears-1:0.004,loosegears-3:0.002 wildgrass=0.08 sandy=0.10 height=0.70 shore=10 rough=0.14")
    print("region T rock=basalt fertility=verylow surface=grass ores=tin:0.02 orebits=tin:0.007 wildgrass=0.08 stones=0.02 sandy=0.12 height=0.82 shore=9 rough=0.18")
    print("region S rock=basalt fertility=verylow surface=grass ores=tin:0.02 orebits=tin:0.007 wildgrass=0.08 stones=0.02 sandy=0.12 height=0.74 shore=9 rough=0.16")
    print("region O rock=basalt surface=rocksand ores=tin:0.035 orebits=tin:0.012 boulders=0.015 height=1.0 shore=6 rough=0.35")
    # The tin mine proper: the big-chamber recipe Michael approved on the
    # starter island (radius=2.7 scale=1.6 branches=3 branchdepth=2), run
    # long, windy and DEEP. Walls lined rich with cassiterite.
    # mouth=6 lifts the adit above the flat harbor beach: at scale 1.6 the
    # carve ellipsoid is fat enough that a waterline mouth kept clipping
    # harbor water, and the fluid guard walled off the first gallery steps.
    # dip=30 gets under the arm's ocean side before the seabed drops.
    # seed=17 from the previewer sweep: 1336 steps, ZERO steps lost to the
    # water guard, bottom galleries 85 below sea.
    print("cave V heading=198 dip=30 length=220 radius=2.7 squash=0.8 weave=0.6 scale=1.6 branches=3 branchdepth=2 branchlen=0.6 depth=80 mouth=6 entry=6 ores=tin:0.08 seed=17")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
