"""
Eerie Island: wide, smooth, rolling green downs given over to the dead.
Odd black obsidian monoliths stand far apart across the hills, daubed with
red ochre crosses, a stone circle of obsidian rings one great monolith, and
a graveyard spreads among them: rows of grave mounds and leaning
headstones, a rotten coffin under each, here and there a chest of grave
goods instead. A few weathered stone pillars, not a forest of them: the
monoliths are what the island is about.

- a..e: the downs, lush green grass, one region per height band. A smooth
  low-frequency field picks the band, and the generator's border smoothing
  rounds the steps into rolling hills.
- B: a dark basalt sand beach all round
- k: a few rock skerries offshore

The structure itself is the `graveyard` megastructure (EerieIsland.cs) on Q:
the Burying Isle, where the wardens laid the dead of the Devastation, with a
mass grave under every marker, catacombs under the island's heart, buried
vaults, trenches, wells, a chapel and the robbers' tunnels (lore/eerie_*.txt).

    python tools/gen_eerie_island.py > shapes/eerie_island.txt

Suggested: /genisland shape=eerie_island diameter=420 height=22 water=36 climate=lush
"""
import math

W = H = 168
CX = CZ = 84.0
BANDS = "abcde"
BAND_H = [0.55, 0.66, 0.77, 0.88, 1.0]


def lim(th):
    return 68.0 * (1.0 + 0.08 * math.sin(3 * th + 0.7) + 0.05 * math.sin(5 * th + 2.1) + 0.03 * math.sin(9 * th + 0.3))


def hills(x, z):
    # long, soft swells: wavelengths of 40-90 cells (100-225 blocks)
    return (math.sin(x * 0.075 + 1.3) * math.cos(z * 0.062 - 0.4)
            + 0.7 * math.sin((x + z) * 0.048 + 2.2)
            + 0.5 * math.cos((x - 0.6 * z) * 0.091 + 0.9))


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
            if d > L - 6:
                grid[r][c] = 'B'
                continue
            n = (hills(dx, dz) + 2.2) / 4.4          # about 0..1
            b = max(0, min(len(BANDS) - 1, int(n * len(BANDS))))
            grid[r][c] = BANDS[b]
    for i in range(7):
        th = i * 2 * math.pi / 7 + 0.4
        rho = lim(th) + 7 + 3 * math.sin(i * 1.7)
        bx, bz = CX + rho * math.cos(th), CZ + rho * math.sin(th)
        for r in range(H):
            for c in range(W):
                if grid[r][c] == '.' and math.hypot(c + 0.5 - bx, r + 0.5 - bz) <= 1.6 + (i % 3) * 0.6:
                    grid[r][c] = 'k'
    grid[int(CZ)][int(CX)] = 'Q'

    print("# eerie_island - wide, smooth, rolling green downs given over to the dead:")
    print("# odd obsidian monoliths standing far apart with red ochre crosses, a stone")
    print("# circle round one great monolith, a few weathered pillars, and a graveyard")
    print("# of mounds and headstones with a coffin (or a chest) buried under each.")
    print("# Regenerate: python tools/gen_eerie_island.py > shapes/eerie_island.txt")
    print("# Suggested: /genisland shape=eerie_island diameter=420 height=22 water=36 climate=lush")
    print()
    for ch, h in zip(BANDS, BAND_H):
        print(f"region {ch} rock=andesite fertility=high surface=grass climate=lush wildgrass=0.42 stones=0.004 "
              f"scatter=flower-heather-free:0.004,fieldmushroom:0.002 height={h} shore=40 rough=0.08")
    print("region B rock=andesite sand=sand-basalt surface=sand stones=0.01 height=0.12 shore=30 rough=0.04")
    print("region k rock=basalt surface=rock height=0.45 shore=3 rough=0.5")
    print("struct Q kind=graveyard size=150 seed=13 monoliths=44 pillars=40 graves=700 plots=12 mausoleums=4 great=34 ring=9 catacombs=4 vaults=14 trenches=6 wells=4 robbers=3")
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
