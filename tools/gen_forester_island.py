"""
Forester island. The outline started life as the first tin-island draft:
Michael liked the landmass itself but wanted the tin island to be a far
sharper crescent, so this shape was kept and re-purposed. The regions below
are PLACEHOLDERS carried over from that draft; they will be redesigned when
the forester island is actually built (expect dense mixed forest, a logging
camp, and friendlier soil).

    python tools/gen_forester_island.py > shapes/forester_island.txt
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
    # A deep bay carved out of the west side.
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

    if ((dx + 4.0) / 5.0) ** 2 + ((dz - 25.0) / 4.0) ** 2 <= 1.0:
        return 'O'
    if ((dx + 10.0) / 8.0) ** 2 + ((dz + 24.0) / 6.0) ** 2 <= 1.0:
        return 'T'
    if adist(ang, -120.0) < 55.0 and t > 0.72:
        return 'D'
    if adist(ang, -10.0) < 50.0 and t > 0.78:
        return 'E'
    if ((dx - 22.0) / 10.0) ** 2 + ((dz - 8.0) / 8.0) ** 2 <= 1.0:
        return 'M'
    if ((dx + 18.0) / 11.0) ** 2 + ((dz - 21.0) / 8.0) ** 2 <= 1.0:
        return 'M'
    if ((dx - 2.0) / 7.0) ** 2 + ((dz + 16.0) / 5.0) ** 2 <= 1.0:
        return 'K'
    if ((dx - 4.0) / 12.0) ** 2 + ((dz - 15.0) / 9.0) ** 2 <= 1.0:
        return 'J'
    if ((dx + 10.0) / 8.0) ** 2 + ((dz - 32.0) / 5.0) ** 2 <= 1.0:
        return 'S'
    return 'P'


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]

    print("# forester_island - rounded island with a west bay, ~200 blocks across.")
    print("# Regenerate: python tools/gen_forester_island.py > shapes/forester_island.txt")
    print("# Suggested: /genisland shape=forester_island diameter=200 height=10")
    print("#")
    print("# The outline is the first tin-island draft, kept because Michael liked the")
    print("# landmass. The REGIONS below are placeholders from that draft and will be")
    print("# redesigned when the forester island is built.")
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
