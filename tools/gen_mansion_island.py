"""
The Mansion: the island under the Better Ruins mansion (0.68.0).

Michael's map draws a Mansion circle south of the Great Machine. The Better
Ruins mansion is a story structure the game rolls thousands of blocks away; in
a Rustfall world it is emptied (assets/landmassgenerator/patches/
rustfall-mansion.json) and its story location pinned here, and this island's
`ruin` structure (Q) places the mansion's own schematic on a levelled lawn the
way the game would (story offset 32: twenty layers of cellar under the lawn).

The estate round it, from the house outwards:

- L: the lawn the house stands on, level and close-cut
- g: the old gardens, gone wild: long grass, flowers, the odd yew and maple
- o: an orchard on the east side
- f: the park woods, oak and birch and beech, deep litter
- W: the mere, an ornamental lake west of the house with reeds
- d: the drive, a long open strip from the landing up to the front lawn
- B: the landing beach on the south shore, where the drive starts
- C: low rocky shores elsewhere

    python tools/gen_mansion_island.py > shapes/mansion_island.txt

Suggested: /genisland shape=mansion_island diameter=380 height=18 water=32 climate=temperate stone=rock-granite sand=sand-granite
"""
import math

W = H = 152
C = W / 2.0


def coast(th):
    return 66.0 * (1 + 0.06 * math.sin(3 * th + 0.5) + 0.04 * math.sin(5 * th + 2.2) + 0.025 * math.sin(9 * th + 1.3))


def adist(a, b):
    d = (a - b) % (2 * math.pi)
    return min(d, 2 * math.pi - d)


SOUTH = math.pi / 2


def region(c, r):
    x, z = c + 0.5 - C, r + 0.5 - C
    d = math.hypot(x, z)
    th = math.atan2(z, x)
    L = coast(th)
    if d > L:
        return '.'
    # the landing beach: the south shore either side of the drive
    if d > L - 6:
        return 'B' if adist(th, SOUTH) < 0.55 else 'C'
    # the drive: a straight strip from the landing to the lawn, wide enough
    # that the park trees either side do not close over it
    if abs(x) <= 4.5 and z > 14 and z < L - 6:
        return 'd'
    # the lawn round the house (the house is about 30 x 19 cells)
    if abs(x) < 24 and z > -18 and z < 16:
        return 'L'
    # the mere, west of the house
    if math.hypot((x + 40) / 13.0, (z + 4) / 9.0) < 1:
        return 'W'
    # the old gardens: a band round the lawn
    if abs(x) < 34 and z > -28 and z < 26:
        return 'g'
    # the orchard, east
    if x > 30 and abs(z) < 26 and d < L - 10:
        return 'o'
    return 'f'


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]
    grid[int(C)][int(C)] = 'Q'
    print("# mansion_island - the estate under the Better Ruins mansion (struct Q, kind=ruin):")
    print("# a levelled lawn for the house, wild gardens, an orchard, park woods, a mere,")
    print("# and a drive up from the landing beach on the south shore.")
    print("# Suggested: /genisland shape=mansion_island diameter=380 height=18 water=32 climate=temperate stone=rock-granite sand=sand-granite")
    print("# Regenerate: python tools/gen_mansion_island.py > shapes/mansion_island.txt")
    print()
    print("region L rock=granite fertility=medium surface=grass wildgrass=0.05 stones=0 height=0.62 shore=40 rough=0.01")
    print("region d rock=granite fertility=low surface=grass wildgrass=0.02 stones=0 height=0.6 shore=40 rough=0.01")
    print("region g rock=granite fertility=high surface=grass wildgrass=0.55 stones=0.002 scatter=flower-cornflower-free:0.01,flower-wilddaisy-free:0.012,flower-lupine-purple-free:0.006,flower-forgetmenot-free:0.006,flower-orangemallow-free:0.004 forest=0.006 trees=englishoak,mountainmaple bushes=raspberry:0.004,blackcurrant:0.003 height=0.62 shore=40 rough=0.03")
    print("region o rock=granite fertility=high surface=grass wildgrass=0.4 forest=0.03 trees=englishoak,silverbirch bushes=raspberry:0.006,redcurrant:0.004 scatter=fieldmushroom:0.003 height=0.64 shore=34 rough=0.04")
    print("region f rock=granite fertility=medium surface=grass wildgrass=0.3 litter=0.7 sticks=0.01 forest=0.07 trees=oldenglishoak,englishoak,silverbirch,walnut,mountainmaple bushes=blueberry:0.003 scatter=fieldmushroom:0.004,eaglefern:0.02 height=0.72 shore=26 rough=0.08")
    print("region W rock=granite fertility=medium surface=grass pond=6 pondslope=10 cattails=0.35 lilies=0.05 clay=0.02 height=0.6 shore=40 rough=0.02")
    print("region B rock=granite surface=sand shells=0.02 height=0.12 shore=26 rough=0.03")
    print("region C rock=granite surface=rocksand boulders=0.01 height=0.3 shore=8 rough=0.12")
    print("struct Q kind=ruin size=60 structure=mansion path=betterruins:worldgen/schematics/story/brmansion.json storyoffset=32 rot=180 blend=10")
    # a worn chart to somewhere else and villagers' papers (0.70.0)
    print("loot ruin 2 near=Q radius=40 where=inside with=landmassgenerator:islandmap-random,randomlore:rfpool-village")
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
