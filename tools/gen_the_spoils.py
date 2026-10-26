"""
The Spoils: the Great Machine's dumping ground.

Jonas cut pieces out of reality for the Great Work and threw what he could not
use into the sea. The island under the heap is a low, ragged shelf of
devastated basalt with gravel and rubble shores and black-sand spits; the heap
itself (a quarter of a kilometre of cut-out pieces of everywhere, caverns
through it, the Great Machine's red fog over it) is the `spoils` structure on
Q (TheSpoils.cs).

- r: the devastated shelf the heap stands on
- s: rubble flats round it, scrap and stone
- B: black basalt sand
- g: gravel shoals
- k: wreck stacks offshore

    python tools/gen_the_spoils.py > shapes/the_spoils.txt

Suggested: /genisland shape=the_spoils diameter=560 height=20 water=36 climate=arid stone=rock-basalt sand=sand-basalt
"""
import math

W = H = 180
C = 90.0


def coast(th):
    return 80.0 * (1 + 0.10 * math.sin(3 * th + 1.7) + 0.07 * math.sin(5 * th + 0.3) + 0.05 * math.sin(7 * th + 2.9)
                   + 0.04 * math.sin(11 * th + 0.8) + 0.03 * math.sin(17 * th + 1.9))


SPITS = [(math.radians(a), l) for a, l in [(20, 16), (140, 12), (205, 18), (290, 14)]]
STACKS = [(math.radians(a), off, r) for a, off, r in [(60, 8, 2.4), (100, 6, 1.8), (175, 9, 2.6), (245, 7, 2.0), (320, 5, 1.6), (355, 10, 2.2)]]


def region(c, r):
    x, z = c + 0.5 - C, r + 0.5 - C
    d = math.hypot(x, z)
    th = math.atan2(z, x)
    L = coast(th)
    # spits of black sand running out from the shelf
    for (a, ln) in SPITS:
        ux, uz = math.cos(a), math.sin(a)
        along = x * ux + z * uz
        across = abs(-x * uz + z * ux)
        if coast(a) - 4 < along < coast(a) + ln and across < 3.5 * (1 - (along - coast(a)) / (ln + 4)):
            return 'B'
    if d > L:
        for (a, off, rad) in STACKS:
            sx, sz = (coast(a) + off) * math.cos(a), (coast(a) + off) * math.sin(a)
            if math.hypot(x - sx, z - sz) < rad:
                return 'k'
        return '.'
    if d > L - 3:
        return 'g' if math.sin(th * 9) > 0.3 else 'B'
    if d > L * 0.82:
        return 's'
    return 'r'


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]
    grid[int(C)][int(C)] = 'Q'
    print("# the_spoils - the Great Machine's dumping ground: a ragged devastated shelf")
    print("# under a heap of pieces cut out of everywhere (struct Q, kind=spoils), caverns")
    print("# through it, the Great Machine's red fog over all of it.")
    print("# Regenerate: python tools/gen_the_spoils.py > shapes/the_spoils.txt")
    print("# Suggested: /genisland shape=the_spoils diameter=560 height=20 water=36 climate=arid stone=rock-basalt sand=sand-basalt")
    print()
    print("region r rock=basalt rock2=andesite fertility=verylow surface=rocksand devastation=0.6 wildgrass=0 stones=0.03 boulders=0.01 orebits=copper:0.004 height=0.45 shore=26 rough=0.3")
    print("region s rock=basalt rock2=scoria fertility=verylow surface=rocksand devastation=0.4 wildgrass=0 stones=0.05 boulders=0.02 height=0.28 shore=18 rough=0.35")
    print("region B rock=basalt sand=sand-basalt surface=sand devastation=0.2 shells=0.004 height=0.08 shore=14 rough=0.05")
    print("region g rock=basalt surface=rocksand stones=0.04 height=0.06 shore=10 rough=0.1")
    print("region k rock=basalt surface=rock height=0.5 shore=3 rough=0.5")
    print("struct Q kind=spoils size=160 radius=215 seed=7 top=74 peaks=7 cell=8 caverns=14 entrances=9 shards=90 gears=9")
    print("deposits natural")
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
