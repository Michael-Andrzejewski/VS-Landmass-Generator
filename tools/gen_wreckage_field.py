"""
Wreckage Field: a floating metallic graveyard over deep dead water. Sheer
devastated basalt spires jut straight out of the ocean (ocean plunge=20:
no sandy shelf ring, the rock drops sheer into ~70-deep water), and the
wreck structure pass (0.47.0) fills the water between them: one titanic
hull rolled onto its side and half-submerged, shattered bow and hull
segments AFLOAT at the waterline, all lashed to each other and to the
spires by a sagging tangle of hull metal, real rusted pipework (the
devastation clutter junkpipe/pipelong shapes), junk beams, hanging chains
and metal spikes. The ruins hold each other up; under them the water is
empty and dark except for the two hulls that sank all the way down.

Two variants from this one script, identical except for the maelstrom:

    python tools/gen_wreckage_field.py > shapes/wreckage_field.txt
    python tools/gen_wreckage_field.py whirlpool > shapes/wreckage_maelstrom.txt

The maelstrom variant marks the wreck line whirlpool=1: a divot pressed
into the open sea itself. No rim, no drained pit: the cone's surface is
real directional flowing water spiraling inward and down into a 2x2
down-flow throat at the eye, and the wrecks inside ride the lowered
surface so the streams pour into their torn hulls.

Suggested: /genisland shape=wreckage_field diameter=240 height=30 water=70 stone=rock-basalt sand=sand-basalt
           /genisland shape=wreckage_maelstrom diameter=240 height=30 water=70 stone=rock-basalt sand=sand-basalt
"""
import math
import sys

WHIRLPOOL = len(sys.argv) > 1 and sys.argv[1].lower().startswith('whirl')

W = H = 120
CX = CZ = 60.0

# Spires: (cx, cz, rx, rz, kind). 's' = tall spike (full height), 'm' = mid
# spire, 't' = low stub barely clearing the water. The center 16 cells stay
# open so the maelstrom's funnel spins in clear water. Cells are ~2 blocks.
SPIRES = [
    (60, 38, 3, 4, 's'),
    (76, 44, 3, 3, 'm'),
    (84, 60, 4, 3, 's'),
    (78, 76, 3, 3, 't'),
    (64, 84, 3, 4, 's'),
    (46, 80, 3, 3, 'm'),
    (38, 64, 4, 3, 's'),
    (42, 46, 3, 3, 't'),
    (52, 30, 2, 3, 'm'),
    (88, 36, 3, 3, 't'),
    (94, 74, 3, 4, 'm'),
    (70, 94, 3, 3, 't'),
    (34, 88, 3, 4, 's'),
    (26, 52, 3, 3, 't'),
    (72, 26, 3, 3, 's'),
]


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

    for i, (cx, cz, rx, rz, kind) in enumerate(SPIRES):
        for (c, r) in blob(cx, cz, rx, rz, 0.35, i * 2.3):
            grid[r][c] = kind

    grid[60][60] = 'W'

    name = 'wreckage_maelstrom' if WHIRLPOOL else 'wreckage_field'
    print(f"# {name} - a floating metallic graveyard: devastated basalt spires")
    print("# drop sheer into deep dead water; between them a titanic capsized hull")
    print("# and shattered segments float at the waterline, tangled to the rock and")
    print("# to each other by hull metal, rusted pipework, junk beams and chains")
    print("# (wreck structure pass).")
    if WHIRLPOOL:
        print("# MAELSTROM variant: the wreck line carries whirlpool=1, a divot")
        print("# pressed into the open sea whose surface is real flowing water")
        print("# spiraling down into a down-flow throat, wrecks riding the cone.")
    print("# Regenerate: python tools/gen_wreckage_field.py" + (" whirlpool" if WHIRLPOOL else "") + f" > shapes/{name}.txt")
    print(f"# Suggested: /genisland shape={name} diameter=240 height=30 water=70 stone=rock-basalt sand=sand-basalt")
    print()
    print("region s rock=basalt fertility=verylow surface=barren climate=arid devastation=0.10 wildgrass=0 stones=0.05 height=1.0 shore=1 rough=0.55")
    print("region m rock=basalt fertility=verylow surface=barren climate=arid devastation=0.08 wildgrass=0 stones=0.04 height=0.55 shore=1 rough=0.50")
    print("region t rock=basalt fertility=verylow surface=barren climate=arid devastation=0.06 wildgrass=0 stones=0.03 height=0.26 shore=1 rough=0.45")
    print("ocean plunge=20")
    print(f"wreck W radius=55 whirlpool={1 if WHIRLPOOL else 0} seed=7")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
