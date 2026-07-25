"""
The Diving Bell Mine: an unremarkable hilly island standing over a very
remarkable hole. Its southern shore looks out across a blue hole in the sea
floor, and out over that hole reach the corroded derrick beams of a drowned
mine, each one dropping a vast chain into the water. The chains end in
house-sized diving bells that still hold their pocket of air and their
ghostlight; the shaft below them opens into a flooded chamber whose walls
are veined with nickel, fluorite, lapis, cinnabar and stranger things, with
older bells lying collapsed on the floor among the wreckage.

The island itself is deliberately plain: rolling grass hills, a few rock
knolls, a rocky south shore for the derrick towers to stand on and a sand
beach on its open north side. Everything that matters is underwater.

    python tools/gen_divingbell_mine.py > shapes/divingbell_mine.txt

Suggested: /genisland shape=divingbell_mine diameter=460 height=26 water=30
           stone=rock-limestone sand=sand-limestone
"""
import math

W = H = 100
CX = CY = 50.0


def coast_r(ang):
    """Outer coast radius (cells) of the northern landmass."""
    return 45.0 * (1.0
                   + 0.060 * math.sin(2 * ang + 0.9)
                   + 0.045 * math.sin(3 * ang + 2.4)
                   + 0.025 * math.sin(5 * ang + 4.1))


def bay_edge(dx):
    """How far north of centre the southern shore runs, at this dx (cells)."""
    return 25.0 + 3.0 * math.sin(dx * 0.18) + 2.0 * math.sin(dx * 0.07 + 1.3)


def blob(dx, dz, bx, bz, rx, rz):
    return ((dx - bx) / rx) ** 2 + ((dz - bz) / rz) ** 2 <= 1.0


def region(c, r):
    dx, dz = c + 0.5 - CX, r + 0.5 - CY
    rho = math.hypot(dx, dz)
    ang = math.atan2(dz, dx)
    if rho > coast_r(ang):
        return '.'
    edge = bay_edge(dx)
    if dz > -edge:
        return '.'                       # the water, and the hole in it

    inland = -dz - edge                  # cells north of the southern shore
    far = coast_r(ang) - rho             # cells south of the northern coast

    # the working shore: a low rock apron facing the hole, where the
    # derrick towers stand
    if inland < 3.0:
        return 'c'
    # open north beach
    if far < 3.0:
        return 'b'
    # two rock knolls, the island's only landmarks
    if blob(dx, dz, -14.0, -36.0, 6.0, 5.0) or blob(dx, dz, 17.0, -30.0, 5.0, 4.5):
        return 'K'
    # a shallow bowl of meadow behind the shore
    if blob(dx, dz, 2.0, -33.0, 15.0, 6.5):
        return 'm'
    return 'h'


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]

    # the struct marker sits at the grid centre, in open water: the ocean
    # basin is centred there too, so the chamber gets its deep bowl
    grid[50][50] = 'D'

    print("# divingbell_mine - a plain hilly island over a drowned mine.")
    print("# Regenerate: python tools/gen_divingbell_mine.py > shapes/divingbell_mine.txt")
    print("# Suggested: /genisland shape=divingbell_mine diameter=460 height=26 water=30 stone=rock-limestone sand=sand-limestone")
    print("#")
    print("# The island is ordinary on purpose. South of it the sea floor opens")
    print("# into a blue hole: corroded derrick beams reach out over it from the")
    print("# shore, vast chains drop from their tips, and each chain ends in a")
    print("# giant diving bell holding air and a ghostlight. The shaft below")
    print("# widens into a flooded chamber veined with nickel, fluorite, lapis,")
    print("# cinnabar, corundum and rarer things, with collapsed bells and the")
    print("# wreckage of the works lying on its floor (struct pass, kind=divingbell).")
    print()
    print("region h rock=limestone rock2=phyllite fertility=medium surface=grass forest=0.010 trees=oak,birch litter=0.7 sticks=0.03 bushes=raspberry:0.006,blackcurrant:0.004 scatter=cornflower:0.008,wilddaisy:0.008,cowparsley:0.006,eaglefern:0.012 orebits=copper:0.0012 stones=0.02 height=0.72 shore=20 rough=0.40")
    print("region m rock=limestone rock2=phyllite fertility=high   surface=grass bushes=raspberry:0.008 scatter=cornflower:0.012,forgetmenot:0.010,redtopgrass:0.010 wildgrass=0.45 height=0.50 shore=22 rough=0.22")
    print("region K rock=limestone rock2=phyllite surface=rock stones=0.06 boulders=0.014 orebits=copper:0.0020 height=1.0 shore=9 rough=0.55")
    print("region c rock=limestone sand=sand-limestone surface=rocksand stones=0.05 boulders=0.010 height=0.52 shore=4 rough=0.30")
    print("region b rock=limestone sand=sand-limestone surface=sand shells=0.02 bushes=birch:0.004 height=0.16 shore=30 rough=0.04 cattails=0.10")
    print("ocean basin=150 depth=33 plunge=5")
    print("struct D kind=divingbell size=130 seed=7")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
