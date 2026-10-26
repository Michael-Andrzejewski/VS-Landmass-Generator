"""
The Alchemist Lair (0.68.0): the drowned Collegium of the alchemists, south-east
of the Great Machine, gone over to the Devastation.

The alchemists built a college on this island: quad after quad of tall stone
halls, an observatory on the hill to the north, and on the east shore, in a
bowl of its own, the gateway they opened. Something came through it. The
Devastation spreads from the gateway across the whole campus, thickest at the
bowl and thinning towards the west, and the fine machinery of the college
still stands in it. The `alchemist` structure (AlchemistLair.cs) builds all of
that; this is the ground under it:

- P: the campus plateau in the middle, broad and nearly flat, where the quads go
- O: the observatory hill north of the campus, steep sided
- G: the gateway bowl on the east side, sunk low, heavily devastated
- D: devastated scrubland round the bowl, dead trees, drock breaking through
- w: woods on the lowland round the plateau, devastation creeping in
- B: the landing beach on the south shore below the college gate
- C: rock cliffs round the rest of the coast

    python tools/gen_alchemist_lair.py > shapes/alchemist_lair.txt

Suggested: /genisland shape=alchemist_lair diameter=600 height=40 water=34 climate=temperate stone=rock-andesite sand=sand-andesite
"""
import math

W = H = 200
C = W / 2.0


def coast(th):
    return 91.0 * (1 + 0.05 * math.sin(3 * th + 1.1) + 0.035 * math.sin(5 * th + 0.3) + 0.02 * math.sin(11 * th + 2.0))


def plateau(th):
    return 45.0 * (1 + 0.05 * math.sin(4 * th + 0.7) + 0.03 * math.sin(7 * th + 1.9))


def adist(a, b):
    d = (a - b) % (2 * math.pi)
    return min(d, 2 * math.pi - d)


HILL = (0.0, -64.0, 17.0)       # the observatory hill: x, z, radius in cells
BOWL = (66.0, 6.0, 15.0)        # the gateway bowl
SOUTH = math.pi / 2


def region(c, r):
    x, z = c + 0.5 - C, r + 0.5 - C
    d = math.hypot(x, z)
    th = math.atan2(z, x)
    L = coast(th)
    if d > L:
        return '.'
    if d > L - 6:
        return 'B' if adist(th, SOUTH) < 0.45 else 'C'
    hd = math.hypot(x - HILL[0], z - HILL[1])
    if hd < HILL[2]:
        return 'O'
    bd = math.hypot(x - BOWL[0], z - BOWL[1])
    if bd < BOWL[2]:
        return 'G'
    if d < plateau(th):
        return 'P'
    # the way down from the plateau to the bowl stays open ground
    if bd < BOWL[2] + 18 or (abs(z - BOWL[1]) < 5 and x > 30 and x < BOWL[0]):
        return 'D'
    return 'w'


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]
    grid[int(C)][int(C)] = 'Q'
    print("# alchemist_lair - the Alchemist Lair: the alchemists' college gone over to the")
    print("# Devastation (struct Q, kind=alchemist): a campus plateau of quads, the")
    print("# observatory hill to the north, the gateway bowl on the east shore.")
    print("# Suggested: /genisland shape=alchemist_lair diameter=600 height=40 water=34 climate=temperate stone=rock-andesite sand=sand-andesite")
    print("# Regenerate: python tools/gen_alchemist_lair.py > shapes/alchemist_lair.txt")
    print()
    print("region P rock=andesite rock2=granite fertility=low surface=grass wildgrass=0.15 stones=0.002 devastation=0.0015 height=0.55 shore=30 rough=0.015")
    print("region O rock=andesite rock2=granite fertility=low surface=grass wildgrass=0.2 stones=0.01 boulders=0.01 devastation=0.002 height=1.0 shore=13 rough=0.10")
    print("region G rock=andesite surface=rock devastation=0.03 boulders=0.004 height=0.16 shore=14 rough=0.06")
    print("region D rock=andesite fertility=verylow surface=barren devastation=0.012 forest=0.004 trees=deadacacia stones=0.01 height=0.3 shore=20 rough=0.08")
    print("region w rock=andesite fertility=low surface=grass wildgrass=0.3 litter=0.4 sticks=0.008 forest=0.03 trees=englishoak,silverbirch,scotspine,deadacacia devastation=0.003 height=0.42 shore=24 rough=0.07")
    print("region B rock=andesite surface=sand shells=0.01 height=0.1 shore=24 rough=0.03")
    print("region C rock=andesite surface=rock boulders=0.008 height=0.34 shore=5 rough=0.18")
    print("struct Q kind=alchemist radius=290")
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
