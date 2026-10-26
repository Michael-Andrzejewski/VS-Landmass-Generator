"""
The Hanging Gardens Temple: a mountainous island the monks made beautiful.

After the Devastation a handful of monks were stranded here. They spent their
lives making it as beautiful as they could, and now it trades. The terrain is
the base the `gardens` structure (HangingGardens.cs) builds on:

- T: the temple terrace, a broad flat plateau in the middle, its sides steep
- g: rolling lowland of grass and Japanese maples round it
- h: low wooded hills in the lowland
- M: a mountain ridge across the north, pines on its crags
- F: the monks' fields east of the plateau, flat meadow with wild flax
- W: a lily pond in the lowland south of the plateau, where the approach starts
- B: sand beaches on the south and east, where the boats come in
- C: rock cliffs on the north and west coasts
- k: sea stacks offshore

The spires (sheer karst pillars with gardens hanging from their ledges, a
stair cut up inside each, rope bridges between them), the temple, the pagoda,
the torii approach and the three markets are all structure: struct Q.

    python tools/gen_hanging_gardens.py > shapes/hanging_gardens.txt

Suggested: /genisland shape=hanging_gardens diameter=520 height=80 water=34 climate=temperate stone=rock-limestone sand=sand-limestone
"""
import math

W = H = 200
CX = CZ = 100.0


def coast(th):
    return 86.0 * (1.0 + 0.07 * math.sin(3 * th + 0.4) + 0.05 * math.sin(5 * th + 2.0)
                   + 0.03 * math.sin(8 * th + 1.1) + 0.02 * math.sin(13 * th + 0.5))


def plateau(th):
    return 27.0 * (1.0 + 0.08 * math.sin(4 * th + 1.3) + 0.05 * math.sin(7 * th + 0.2))


def adist(a, b):
    d = (a - b) % (2 * math.pi)
    return min(d, 2 * math.pi - d)


HILLS = [(-40, 38, 9), (52, 30, 8), (-58, -12, 10), (30, -52, 7), (60, -30, 8)]
STACKS = [(math.radians(a), d, r) for a, d, r in [(200, 9, 2.2), (235, 6, 1.6), (300, 8, 2.0), (330, 5, 1.4), (20, 7, 1.8), (110, 6, 1.5)]]


def region(c, r):
    x, z = c + 0.5 - CX, r + 0.5 - CZ
    d = math.hypot(x, z)
    th = math.atan2(z, x)          # 0 = east, pi/2 = south (map z grows south)
    L = coast(th)
    if d > L:
        for (a, off, rad) in STACKS:
            sx, sz = (coast(a) + off) * math.cos(a), (coast(a) + off) * math.sin(a)
            if math.hypot(x - sx, z - sz) < rad:
                return 'k'
        return '.'
    t = d / L
    north = adist(th, -math.pi / 2)         # 0 at due north
    # the lily pond beside the foot of the approach, south-west of the plateau
    if math.hypot(x + 22, z - 46) < 8 + 1.5 * math.sin(th * 3):
        return 'W'
    if d < plateau(th):
        return 'T'
    # the ridge across the north, a crescent hugging the coast
    if north < 1.05 and t > 0.52 and t < 0.93:
        k = 1 - north / 1.05
        if t > 0.93 - 0.32 * k:
            return 'M'
    if t > 0.9:
        south = adist(th, math.pi / 2)
        east = adist(th, 0)
        if south < 0.9 or east < 0.6:
            return 'B'
        return 'C'
    for (hx, hz, hr) in HILLS:
        if math.hypot(x - hx, z - hz) < hr:
            return 'h'
    # the fields east of the plateau
    if adist(th, 0.15) < 0.45 and 0.33 < t < 0.7:
        return 'F'
    return 'g'


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]
    grid[int(CZ)][int(CX)] = 'Q'
    print("# hanging_gardens - a mountainous island the stranded monks made beautiful:")
    print("# a temple terrace in the middle, maple lowland, a pine ridge in the north,")
    print("# the monks' fields, a lily pond on the approach. The spires with their")
    print("# hanging gardens, the rope bridges, the temple and the three markets are")
    print("# the `gardens` structure (Q).")
    print("# Regenerate: python tools/gen_hanging_gardens.py > shapes/hanging_gardens.txt")
    print("# Suggested: /genisland shape=hanging_gardens diameter=520 height=80 water=34 climate=temperate stone=rock-limestone sand=sand-limestone")
    print()
    flowers = "scatter=flower-cornflower-free:0.004,flower-wilddaisy-free:0.004,flower-catmint-free:0.003,fieldmushroom:0.002"
    print(f"region T rock=limestone rock2=granite fertility=high surface=grass wildgrass=0.3 stones=0.002 forest=0.004 trees=japanesemaple {flowers} height=0.36 shore=40 rough=0.02")
    print(f"region g rock=limestone rock2=granite fertility=high surface=grass wildgrass=0.38 stones=0.004 sticks=0.01 litter=0.5 forest=0.035 trees=japanesemaple,japanesemaple,scotspine bushes=raspberry:0.003,blueberry:0.002 {flowers} height=0.14 shore=36 rough=0.08")
    print(f"region h rock=limestone rock2=granite fertility=high surface=grass wildgrass=0.3 litter=0.8 forest=0.08 trees=japanesemaple,japanesemaple,mountainmaple height=0.22 shore=36 rough=0.12")
    print("region M rock=limestone rock2=chalk fertility=low surface=grass ores=quartz:0.02,copper:0.006 boulders=0.01 stones=0.02 forest=0.03 trees=mountainpine,scotspine scatter=flower-edelweiss-free:0.006 height=0.55 shore=60 rough=0.32")
    print("region F rock=limestone rock2=granite fertility=compost surface=grass wildgrass=0.25 flax=0.06 pumpkins=0.004 scatter=flower-catmint-free:0.004 height=0.16 shore=36 rough=0.02")
    print("region W rock=limestone fertility=high surface=grass pond=4 pondslope=5 lilies=0.25 cattails=0.3 clay=0.04 height=0.14 shore=36 rough=0.02")
    print("region B rock=limestone surface=sand shells=0.02 height=0.04 shore=26 rough=0.03")
    print("region C rock=limestone surface=rock boulders=0.01 height=0.3 shore=5 rough=0.18")
    print("region k rock=limestone surface=rock height=0.45 shore=3 rough=0.45")
    print("struct Q kind=gardens size=160 radius=230 seed=21 spires=12")
    print("deposits natural")
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
