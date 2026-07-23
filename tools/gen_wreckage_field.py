"""
Wreckage Field: a drowned metallic graveyard. Low dead basalt banks, most
of them just under or barely above the surface, littered by the mod's
wreck structure pass (0.46.0): one titanic hull rolled onto its side and
half-sunk, shattered bow and hull segments strewn as if a whirlpool
gathered them, a debris carpet of rusted pipes (chutes), jutting beams,
metal spikes, part piles and iron fence stubs, and a drock rust crust
creeping over every bank.

Two variants from this one script, identical except for the maelstrom:

    python tools/gen_wreckage_field.py > shapes/wreckage_field.txt
    python tools/gen_wreckage_field.py whirlpool > shapes/wreckage_maelstrom.txt

The maelstrom variant sinks every bank lower, marks the wreck line
whirlpool=1, and the structure pass then sculpts a sealed draining funnel
at the center: a jagged rust-and-metal rim just above sea, a cone of open
air descending to a pool 12 below the surface, three spiral streams of
real flowing water running down the walls, a 2x2 down-flow throat, and
wrecks lying in the pit. The rim is sealed on purpose: a live ocean
breach would let liquid physics slowly fill the pit back up.

Suggested: /genisland shape=wreckage_field diameter=240 height=6 water=35
           /genisland shape=wreckage_maelstrom diameter=240 height=6 water=35
"""
import math
import sys

WHIRLPOOL = len(sys.argv) > 1 and sys.argv[1].lower().startswith('whirl')

W = H = 120
CX = CZ = 60.0

# Banks: (cx, cz, rx, rz, kind). 'u' = drowned (flood=1, just under the
# surface), 'b' = barely above, 'r' = the few real islets. The center is one
# broad drowned shoal the titan grounds on.
BANKS = [
    (60, 60, 26, 22, 'u'),
    (60, 52, 10, 7, 'b'),
    (48, 68, 8, 6, 'b'),
    (30, 40, 9, 7, 'b'),
    (88, 42, 10, 7, 'u'),
    (92, 74, 8, 6, 'b'),
    (72, 92, 11, 8, 'u'),
    (38, 90, 8, 6, 'b'),
    (24, 66, 7, 5, 'u'),
    (80, 24, 8, 6, 'r'),
    (44, 22, 7, 5, 'b'),
    (20, 92, 6, 5, 'r'),
    (100, 58, 7, 5, 'u'),
]
# Deep dead flats between banks ('d', flood=3 basalt sand): drawn as wide
# skirts around every bank so the field reads as one drowned shoal system.


def main():
    grid = [['.'] * W for _ in range(H)]

    def blob(cx, cz, rx, rz, jag, seedphase):
        cells = []
        for r in range(H):
            for c in range(W):
                x, z = c + 0.5, r + 0.5
                dx, dz = x - cx, z - cz
                d = math.hypot(dx / rx, dz / rz)
                a = math.atan2(dz, dx)
                lim = 1.0 + jag * (0.5 * math.sin(3 * a + seedphase) + 0.3 * math.sin(5 * a + seedphase * 2))
                if d <= lim:
                    cells.append((c, r))
        return cells

    # skirts first, banks overwrite
    for i, (cx, cz, rx, rz, kind) in enumerate(BANKS):
        for (c, r) in blob(cx, cz, rx * 1.8, rz * 1.8, 0.15, i * 1.7):
            if grid[r][c] == '.':
                grid[r][c] = 'd'
    for i, (cx, cz, rx, rz, kind) in enumerate(BANKS):
        for (c, r) in blob(cx, cz, rx, rz, 0.22, i * 2.3):
            grid[r][c] = kind

    grid[60][60] = 'W'

    name = 'wreckage_maelstrom' if WHIRLPOOL else 'wreckage_field'
    hb = 0.20 if WHIRLPOOL else 0.55   # banks sink in the maelstrom variant
    hr = 0.55 if WHIRLPOOL else 1.0
    print(f"# {name} - a drowned metallic graveyard: dead basalt shoals under a")
    print("# titanic capsized hull, shattered ship segments, rusted pipes, jutting")
    print("# beams, metal spikes and a drock rust crust (wreck structure pass).")
    if WHIRLPOOL:
        print("# MAELSTROM variant: banks sunk lower, and the wreck line carries")
        print("# whirlpool=1: a sealed draining funnel with flowing spiral streams")
        print("# and a down-flow throat, wrecks lying in the pit.")
    print("# Regenerate: python tools/gen_wreckage_field.py" + (" whirlpool" if WHIRLPOOL else "") + f" > shapes/{name}.txt")
    print(f"# Suggested: /genisland shape={name} diameter=240 height=6 water=35")
    print()
    print(f"region b rock=basalt fertility=verylow surface=barren climate=arid devastation=0.05 wildgrass=0 stones=0.03 height={hb} shore=6 rough=0.20")
    print(f"region r rock=basalt fertility=verylow surface=barren climate=arid devastation=0.06 wildgrass=0 stones=0.04 height={hr} shore=5 rough=0.28")
    print("region u rock=basalt surface=rock climate=arid flood=1 height=0.30 shore=2 rough=0.25")
    print("region d rock=basalt sand=sand-basalt surface=sand climate=arid flood=3 height=0.12 shore=3 rough=0.10")
    print(f"wreck W radius=55 whirlpool={1 if WHIRLPOOL else 0} seed=7")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
