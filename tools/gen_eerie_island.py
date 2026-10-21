"""
Eerie Island: rolling grassy hills given over to the dead. A field of
weathered stone pillars and black obsidian monoliths daubed with red ochre
crosses, a stone circle of obsidian around one great monolith, and a vast
graveyard among them: rows of grave mounds and leaning headstones, a rotten
coffin under each, here and there a chest of grave goods instead.

- G: the graveyard downs, low fertility grass on rolling hills
- M: the moor, rougher, stonier, darker grass and boulders
- B: a dark basalt sand beach all round
- k: a few rock skerries offshore

The structure itself is the `graveyard` megastructure (MegaStructs.cs) on Q.

    python tools/gen_eerie_island.py > shapes/eerie_island.txt

Suggested: /genisland shape=eerie_island diameter=260 height=26 water=36
"""
import math

W = H = 130
CX = CZ = 65.0


def lim(th):
    return 52.0 * (1.0 + 0.10 * math.sin(3 * th + 0.7) + 0.06 * math.sin(5 * th + 2.1) + 0.04 * math.sin(9 * th + 0.3))


def main():
    grid = [['.'] * W for _ in range(H)]
    for r in range(H):
        for c in range(W):
            x, z = c + 0.5, r + 0.5
            dx, dz = x - CX, z - CZ
            d = math.hypot(dx, dz)
            th = math.atan2(dz, dx)
            L = lim(th)
            if d > L:
                continue
            if d > L - 5:
                grid[r][c] = 'B'
            elif (math.sin(dx * 0.11 + 1.3) + math.cos(dz * 0.09 - 0.4) + 0.6 * math.sin((dx + dz) * 0.05)) > 1.1:
                grid[r][c] = 'M'
            else:
                grid[r][c] = 'G'
    for i in range(7):
        th = i * 2 * math.pi / 7 + 0.4
        rho = lim(th) + 6 + 3 * math.sin(i * 1.7)
        bx, bz = CX + rho * math.cos(th), CZ + rho * math.sin(th)
        for r in range(H):
            for c in range(W):
                if grid[r][c] == '.' and math.hypot(c + 0.5 - bx, r + 0.5 - bz) <= 1.6 + (i % 3) * 0.6:
                    grid[r][c] = 'k'
    grid[int(CZ)][int(CX)] = 'Q'

    print("# eerie_island - rolling grassy hills given over to the dead: weathered")
    print("# stone pillars, black obsidian monoliths with red ochre crosses, a stone")
    print("# circle round one great monolith, and a vast graveyard of mounds and")
    print("# headstones with a coffin (or a chest) buried under each.")
    print("# Regenerate: python tools/gen_eerie_island.py > shapes/eerie_island.txt")
    print("# Suggested: /genisland shape=eerie_island diameter=260 height=26 water=36")
    print()
    print("region G rock=andesite fertility=low surface=grass climate=cold wildgrass=0.30 stones=0.012 scatter=flower-heather-free:0.010,fieldmushroom:0.002 height=1.0 shore=14 rough=0.55")
    print("region M rock=andesite rock2=basalt fertility=low surface=grass climate=cold wildgrass=0.18 stones=0.03 boulders=0.012 height=1.0 shore=12 rough=0.7")
    print("region B rock=andesite sand=sand-basalt surface=sand stones=0.02 height=0.14 shore=26 rough=0.05")
    print("region k rock=basalt surface=rock height=0.45 shore=3 rough=0.6")
    print("struct Q kind=graveyard size=96 seed=13 monoliths=34 pillars=150 graves=200 mausoleums=4 great=32 ring=9")
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
