"""
Tin island, second design: an unmistakable crescent. The island is a curved
arm bent around a sheltered harbor, with a clearly defined entrance between
the two horn tips. The mouth opens WEST at rotate=0; use the command's
rotate= option to aim it at the starter island.

Geometry: the landmass is a band around a circular spine (radius A cells),
fat at the back of the crescent and tapering toward the horn tips, with a
gap (the harbor mouth) left where the spine would cross the west.

Character (per Michael's brief): taller, a lot of exposed rock, verylow
fertility everywhere with barren-dirt bands by the sand, sparse faded grass
(pair with climate=arid for the rusty desert tint), heavy devastation,
little vegetation.

  - Rocky ridge (R) runs along the whole crescent's crest.
  - Devastation briars (D) own the north arm and its tip.
  - Scattered devastation sections + rusty gear piles (E) on the outer
    east rim.
  - Surface tin patches north (T, future ruined laboratory) and south (S,
    by the future tin mine); rocky tin-mine knoll (O) on the south arm.
  - Barren dirt (B) rings the inner harbor slope and part of the south
    outer coast.
  - Small maple pockets (M) on the harbor-facing east slope and the SW
    lobe; small pumpkin patch (K) centre-north; rusty debris (J)
    centre-south. 2% cassiterite through the core.

Structure sites reserved for a later pass (NOT built here): ruined
laboratory half underground (north arm, by T), teleporter beside it,
tin mine (knoll O).

    python tools/gen_tin_island.py > shapes/tin_island.txt
"""
import math

W, H = 100, 100
CX, CY = 50.0, 50.0

SPINE_R = 33.0        # spine circle radius, cells
MOUTH_HALF = 30.0     # harbor mouth half-angle, degrees (mouth faces west)
ARM_END = 150.0       # |phi| at the horn tips


def wrap(a):
    """Wrap to (-180, 180]."""
    a = (a + 180.0) % 360.0 - 180.0
    return 180.0 if a == -180.0 else a


def spine(ang):
    """Effective spine radius and half-thickness at angle `ang` (0=east)."""
    t = math.radians(ang)
    a_eff = SPINE_R * (1.0
                       + 0.030 * math.sin(2 * t + 1.7)
                       + 0.020 * math.sin(5 * t + 0.4))
    phi = wrap(ang)
    frac = min(1.0, abs(phi) / ARM_END)          # 0 back, 1 at the tips
    half = (3.0 + 11.0 * math.cos(frac * math.pi / 2) ** 0.9) \
        * (1.0 + 0.10 * math.sin(3 * t + 2.6))
    return a_eff, half


def region(c, r):
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
    if 100.0 <= phi <= 118.0 and abs(u) < 0.55:
        return 'O'

    # Surface tin patches: north arm (future laboratory) and south arm.
    if -108.0 <= phi <= -88.0 and -0.7 <= u <= 0.5:
        return 'T'
    if 78.0 <= phi <= 95.0 and -0.5 <= u <= 0.7:
        return 'S'

    # Devastation briars: the whole north arm crest and its horn tip.
    if phi < -125.0:
        return 'D'
    if phi < -55.0 and u > -0.35:
        return 'D'

    # Outer east rim: scattered devastation sections and rusty gear piles.
    if abs(phi) < 55.0 and u > 0.5:
        return 'E'

    # Small maple pockets: harbor-facing east slope, and the SW lobe.
    if abs(phi - 8.0) < 12.0 and -0.7 <= u <= -0.3:
        return 'M'
    if 124.0 <= phi <= 143.0 and abs(u) < 0.55:
        return 'M'

    # Pumpkin patch centre-north; rusty debris and machinery centre-south.
    if -50.0 <= phi <= -40.0 and -0.5 <= u <= -0.05:
        return 'K'
    if 35.0 <= phi <= 55.0 and -0.5 <= u <= 0.3:
        return 'J'

    # Rocky ridge along the crescent's crest.
    if abs(u) < 0.28:
        return 'R'

    # Barren dirt: the inner harbor slope, and part of the south outer coast.
    if u < -0.68:
        return 'B'
    if u > 0.62 and 75.0 <= phi <= 110.0:
        return 'B'

    return 'P'


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]

    print("# tin_island - crescent tin island around a sheltered harbor, semi-devastated.")
    print("# Regenerate: python tools/gen_tin_island.py > shapes/tin_island.txt")
    print("# Suggested: /genisland shape=tin_island diameter=200 height=14 stone=rock-andesite sand=sand-basalt climate=arid")
    print("# The harbor mouth opens WEST at rotate=0; add rotate=<deg clockwise> to aim")
    print("# it at the starter island (rotate=90: mouth opens north... west->north).")
    print("#")
    print("# Tall rocky andesite+basalt crescent, rusty arid tint, verylow fertility,")
    print("# barren dirt by the sand, sparse faded grass, heavy devastation. Rock ridge")
    print("# along the crest, briars on the north arm, gears + devastation on the east")
    print("# rim, tin surface patches north and south, tin-mine knoll on the south arm.")
    print("# Structure sites reserved (later pass): ruined laboratory half underground")
    print("# (north arm, region T), teleporter beside it, tin mine (knoll O).")
    print()
    print("region P rock=andesite rock2=basalt fertility=verylow surface=grass ores=tin:0.02 orebits=tin:0.0008 wildgrass=0.10 stones=0.02 scatter=fieldmushroom:0.0015 height=0.66 shore=10 rough=0.14")
    print("region R rock=andesite rock2=basalt surface=rocksand ores=tin:0.02 orebits=tin:0.0015 boulders=0.010 stones=0.035 height=1.0 shore=7 rough=0.32")
    print("region D rock=andesite rock2=basalt fertility=verylow surface=grass ores=tin:0.02 devastation=0.012 scatter=devgrowth-thorns:0.030,devgrowth-bush:0.018,devgrowth-shard:0.010 wildgrass=0.05 stones=0.02 height=0.88 shore=8 rough=0.24")
    print("region E rock=andesite rock2=basalt fertility=verylow surface=grass ores=tin:0.02 devastation=0.014 scatter=loosegears-2:0.006,loosegears-4:0.004,devgrowth-thorns:0.010,devgrowth-shard:0.006 wildgrass=0.05 stones=0.02 height=0.78 shore=9 rough=0.20")
    print("region B rock=andesite rock2=basalt fertility=verylow surface=barren ores=tin:0.02 wildgrass=0 stones=0.025 height=0.45 shore=8 rough=0.10")
    print("region M rock=andesite rock2=basalt fertility=low    surface=grass ores=tin:0.02 forest=0.010 trees=maple,sugarmaplesmall sticks=0.02 litter=0.6 wildgrass=0.15 scatter=fieldmushroom:0.003 height=0.74 shore=10 rough=0.12")
    print("region K rock=andesite rock2=basalt fertility=low    surface=grass ores=tin:0.02 scatter=pumpkin-fruit-3:0.010,pumpkin-fruit-4:0.006,pumpkin-fruit-2:0.006 wildgrass=0.12 height=0.70 shore=10 rough=0.10")
    print("region J rock=andesite rock2=basalt fertility=verylow surface=grass ores=tin:0.02 devastation=0.006 scatter=metalpartpile-tiny:0.008,metalpartpile-small:0.005,metal-scraps:0.003,loosegears-1:0.004,loosegears-3:0.002 wildgrass=0.08 height=0.70 shore=10 rough=0.14")
    print("region T rock=andesite rock2=basalt fertility=verylow surface=grass ores=tin:0.02 orebits=tin:0.007 wildgrass=0.08 stones=0.02 height=0.82 shore=9 rough=0.18")
    print("region S rock=andesite rock2=basalt fertility=verylow surface=grass ores=tin:0.02 orebits=tin:0.007 wildgrass=0.08 stones=0.02 height=0.74 shore=9 rough=0.16")
    print("region O rock=andesite rock2=basalt surface=rocksand ores=tin:0.035 orebits=tin:0.012 boulders=0.015 height=1.0 shore=6 rough=0.35")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
