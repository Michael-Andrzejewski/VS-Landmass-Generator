"""
Tin island, from Michael's reference drawing: a crescent with a deep bay
opening west, dark andesite + basalt geology, semi-devastated.

  - Devastation briars along the north rim and the sharp north-west tip.
  - Scattered devastation sections and rusty gear piles along the east rim.
  - Surface tin deposit patches north (near the future ruined laboratory)
    and south (near the future tin mine), plus strays island-wide.
  - Rocky tin-mine knoll in the south, tin-rich, boulders.
  - Sparse maple trees east interior and on the south-west lobe.
  - Pumpkin patch centre-north; rusty debris and machinery centre-south.
  - Whole island: 2% cassiterite through the core, low-fertility grass.

Structure sites reserved for a later pass (NOT built here): ruined
laboratory half underground (north interior), teleporter (near the lab),
tin mine (the south knoll).

    python tools/gen_tin_island.py > shapes/tin_island.txt
"""
import math

W, H = 100, 100
CX, CY = 50.0, 50.0


def adist(a, b):
    """Angular distance in degrees, wrap-safe."""
    d = (a - b) % 360.0
    return d if d <= 180.0 else 360.0 - d


def coast_r(ang):
    """Coast radius (cells) at angle `ang` (deg; 0=east, 90=south)."""
    t = math.radians(ang)
    r = 43.0 * (1.0
                + 0.045 * math.sin(2 * t + 1.7)
                + 0.035 * math.sin(3 * t + 0.4)
                + 0.020 * math.sin(5 * t + 3.1))
    # The crescent: a deep bay carved out of the west side.
    a = adist(ang, 185.0)
    r *= 1.0 - 0.68 * math.exp(-(a / 46.0) ** 2)
    return r


def region(c, r):
    dx, dz = c + 0.5 - CX, r + 0.5 - CY
    rho = math.hypot(dx, dz)
    ang = math.degrees(math.atan2(dz, dx))
    rc = coast_r(ang)
    if rho > rc:
        return '.'
    t = rho / rc                       # 0 at centre, 1 at the coast

    # Rocky tin-mine knoll, south of centre (future mine structure site).
    if ((dx + 4.0) / 5.0) ** 2 + ((dz - 25.0) / 4.0) ** 2 <= 1.0:
        return 'O'

    # Surface tin patch north, by the future laboratory site.
    if ((dx + 10.0) / 8.0) ** 2 + ((dz + 24.0) / 6.0) ** 2 <= 1.0:
        return 'T'

    # Devastation-briar rim: the north edge and the sharp north-west tip.
    if adist(ang, -120.0) < 55.0 and t > 0.72:
        return 'D'

    # East rim: scattered devastation sections and rusty gear piles.
    if adist(ang, -10.0) < 50.0 and t > 0.78:
        return 'E'

    # Sparse maples: east interior, and the south-west lobe.
    if ((dx - 22.0) / 10.0) ** 2 + ((dz - 8.0) / 8.0) ** 2 <= 1.0:
        return 'M'
    if ((dx + 18.0) / 11.0) ** 2 + ((dz - 21.0) / 8.0) ** 2 <= 1.0:
        return 'M'

    # Pumpkin patch, centre-north.
    if ((dx - 2.0) / 7.0) ** 2 + ((dz + 16.0) / 5.0) ** 2 <= 1.0:
        return 'K'

    # Rusty debris and machinery, centre-south.
    if ((dx - 4.0) / 12.0) ** 2 + ((dz - 15.0) / 9.0) ** 2 <= 1.0:
        return 'J'

    # Surface tin patch south, by the future mine.
    if ((dx + 10.0) / 8.0) ** 2 + ((dz - 32.0) / 5.0) ** 2 <= 1.0:
        return 'S'

    return 'P'


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]

    print("# tin_island - crescent tin island, ~200 blocks across, semi-devastated.")
    print("# Regenerate: python tools/gen_tin_island.py > shapes/tin_island.txt")
    print("# Suggested: /genisland shape=tin_island diameter=200 height=10 stone=rock-andesite sand=sand-basalt")
    print("#")
    print("# Dark andesite+basalt crescent, bay opening west. Devastation briars north")
    print("# and at the NW tip, devastated sections + rusty gears east, maples east and")
    print("# south-west, pumpkins centre-north, rusty debris centre-south, surface tin")
    print("# north and south, tin-mine knoll south. 2% cassiterite through the core.")
    print("# Structure sites reserved (later pass): ruined laboratory half underground")
    print("# (north interior, region T area), teleporter beside it, tin mine (knoll O).")
    print()
    print("region P rock=andesite rock2=basalt fertility=low    surface=grass ores=tin:0.02 orebits=tin:0.0008 bushes=blueberry:0.002 scatter=cowparsley:0.004,fieldmushroom:0.002 height=0.70 shore=15 rough=0.09")
    print("region D rock=andesite rock2=basalt fertility=verylow surface=grass ores=tin:0.02 devastation=0.004 scatter=devgrowth-thorns:0.020,devgrowth-bush:0.012,devgrowth-shard:0.006 wildgrass=0.15 height=0.75 shore=13 rough=0.12")
    print("region E rock=andesite rock2=basalt fertility=verylow surface=grass ores=tin:0.02 devastation=0.006 scatter=loosegears-2:0.004,loosegears-4:0.0025,devgrowth-thorns:0.006,devgrowth-shard:0.004 wildgrass=0.12 height=0.72 shore=13 rough=0.12")
    print("region M rock=andesite rock2=basalt fertility=medium surface=grass ores=tin:0.02 forest=0.012 trees=maple,sugarmaplesmall sticks=0.03 litter=0.7 scatter=fieldmushroom:0.005,eaglefern:0.015 height=0.72 shore=15 rough=0.08")
    print("region K rock=andesite rock2=basalt fertility=medium surface=grass ores=tin:0.02 scatter=pumpkin-fruit-3:0.012,pumpkin-fruit-4:0.008,pumpkin-fruit-2:0.008 height=0.70 shore=15 rough=0.07")
    print("region J rock=andesite rock2=basalt fertility=low    surface=grass ores=tin:0.02 devastation=0.0025 scatter=metalpartpile-tiny:0.006,metalpartpile-small:0.0035,metal-scraps:0.002,loosegears-1:0.003,loosegears-3:0.0015 height=0.70 shore=15 rough=0.09")
    print("region T rock=andesite rock2=basalt fertility=low    surface=grass ores=tin:0.02 orebits=tin:0.006 height=0.72 shore=15 rough=0.09")
    print("region S rock=andesite rock2=basalt fertility=low    surface=grass ores=tin:0.02 orebits=tin:0.006 height=0.70 shore=15 rough=0.09")
    print("region O rock=andesite rock2=basalt surface=rocksand ores=tin:0.035 orebits=tin:0.010 boulders=0.012 height=0.90 shore=8 rough=0.30")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
