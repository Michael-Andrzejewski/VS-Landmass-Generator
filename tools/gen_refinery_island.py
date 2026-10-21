"""
Rusted Refinery: a low, flat industrial island gone to ruin. Concrete pads,
a pipe rack the length of the site, two colossal penstocks sloping to the
south shore, storage tanks, distillation columns, scaffolding, a gantry
crane over a flooded sump, the control room. Marsh flats creep in from the
west; a line of power pylons marches off to the north-east.

- P: the site, barren gravelly ground (the refinery levels its own pads)
- F: weedy fringe, low fertility grass
- w: marsh flats, flooded one block, cattails
- B: grey sand shore

The structures are the `refinery` and `pylons` megastructures (MegaStructs.cs).

    python tools/gen_refinery_island.py > shapes/refinery_island.txt

Suggested: /genisland shape=refinery_island diameter=280 height=7 water=30 stone=rock-basalt sand=sand-basalt climate=arid
"""
import math

W = H = 140
CX = CZ = 70.0


def lim(th):
    return 58.0 * (1.0 + 0.07 * math.sin(2 * th + 0.4) + 0.05 * math.sin(5 * th + 1.9) + 0.03 * math.sin(11 * th))


def main():
    grid = [['.'] * W for _ in range(H)]
    for r in range(H):
        for c in range(W):
            x, z = c + 0.5, r + 0.5
            dx, dz = x - CX, z - CZ
            d, th = math.hypot(dx, dz), math.atan2(dz, dx)
            L = lim(th)
            if d > L:
                continue
            if d > L - 4:
                grid[r][c] = 'B'
            elif dx < -30 and math.sin(dz * 0.15) + math.sin(dx * 0.2) > -0.2:
                grid[r][c] = 'w'
            elif d > L - 14:
                grid[r][c] = 'F'
            else:
                grid[r][c] = 'P'
    grid[int(CZ)][int(CX)] = 'Q'
    grid[int(CZ) - 30][int(CX) + 30] = 'L'
    print("# refinery_island - the Rusted Refinery: pads, pipe rack, penstocks, tanks,")
    print("# distillation columns, scaffolding, a gantry crane over a flooded sump, the")
    print("# control room; marsh flats west, a pylon line marching off north-east.")
    print("# Regenerate: python tools/gen_refinery_island.py > shapes/refinery_island.txt")
    print("# Suggested: /genisland shape=refinery_island diameter=280 height=7 water=30 stone=rock-basalt sand=sand-basalt climate=arid")
    print()
    print("region P rock=basalt fertility=verylow surface=barren wildgrass=0.04 stones=0.02 scatter=metal-scraps:0.002,loosegears-1:0.002 height=0.55 shore=20 rough=0.03")
    print("region F rock=basalt fertility=low surface=grass wildgrass=0.25 stones=0.02 height=0.5 shore=16 rough=0.06")
    print("region w rock=basalt surface=grass flood=1 cattails=0.3 height=0.2 shore=4 rough=0.04")
    print("region B rock=basalt surface=sand shells=0.01 height=0.12 shore=20 rough=0.03")
    print("struct Q kind=refinery size=72 seed=21")
    print("struct L kind=pylons seed=8 count=5 spacing=42 heading=45 height=40 above=10 reach=70")
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
