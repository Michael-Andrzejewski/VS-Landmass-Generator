"""
The Great Machine: the heart of the Rot. A walled brutalist complex (the
`greatmachine` megastructure, MegaStructs.cs) on a level plaza in the
middle of a devastated wasteland: 40-block enclosure walls, a stepped
pyramid with its centre taken out, the altar and the Machine's core at the
foot of the shaft, a double helix of rusted steel rising out of it to the
top of the world, and below it the gear hall and its ring of empty halls.
Ruined fortresses stand on the four diagonals and lines of power pylons
march out from the walls on the four axes.

- P: the plaza, flat (the structure levels it), barren
- D: the devastated wasteland round it, devastation patches and growths
- R: rocky ruin fringe toward the coast
- B: grey sand shore

    python tools/gen_great_machine.py > shapes/great_machine.txt

Suggested: /genisland shape=great_machine diameter=600 height=30 water=40 stone=rock-basalt sand=sand-basalt climate=arid
"""
import math

W = H = 150
C = 75.0


def lim(th):
    return 70.0 * (1.0 + 0.05 * math.sin(3 * th + 0.4) + 0.04 * math.sin(7 * th + 1.1) + 0.02 * math.sin(13 * th))


def main():
    grid = [['.'] * W for _ in range(H)]
    for r in range(H):
        for c in range(W):
            x, z = c + 0.5, r + 0.5
            dx, dz = x - C, z - C
            d, th = math.hypot(dx, dz), math.atan2(dz, dx)
            L = lim(th)
            if d > L:
                continue
            if max(abs(dx), abs(dz)) <= 42:
                grid[r][c] = 'P'
            elif d > L - 3:
                grid[r][c] = 'B'
            elif d > L - 10:
                grid[r][c] = 'R'
            else:
                grid[r][c] = 'D'
    grid[75][75] = 'Q'
    for i, (fx, fz) in enumerate([(-1, -1), (1, -1), (1, 1), (-1, 1)]):
        grid[int(C + fz * 45)][int(C + fx * 45)] = 'FGHI'[i]
    for i, (lx, lz) in enumerate([(0, -1), (1, 0), (0, 1), (-1, 0)]):
        grid[int(C + lz * 52)][int(C + lx * 52)] = 'KLMN'[i]

    print("# great_machine - the heart of the Rot: walled brutalist pyramid with its")
    print("# centre taken out, the altar and core at the foot, a double helix to the")
    print("# top of the world, the gear hall and empty halls below; ruined forts on")
    print("# the diagonals, pylon lines on the axes, devastated wasteland all round.")
    print("# Regenerate: python tools/gen_great_machine.py > shapes/great_machine.txt")
    print("# Suggested: /genisland shape=great_machine diameter=600 height=30 water=40 stone=rock-basalt sand=sand-basalt climate=arid")
    print()
    print("region P rock=basalt fertility=verylow surface=barren wildgrass=0.02 height=0.35 shore=40 rough=0.02")
    print("region D rock=basalt rock2=andesite fertility=verylow surface=barren devastation=0.03 scatter=devgrowth-thorns:0.030,devgrowth-bush:0.020,devgrowth-shard:0.012,loosegears-1:0.003,metalpartpile-tiny:0.003 wildgrass=0.03 stones=0.03 height=0.40 shore=30 rough=0.10")
    print("region R rock=basalt surface=rocksand devastation=0.01 boulders=0.01 stones=0.03 height=0.45 shore=8 rough=0.25")
    print("region B rock=basalt surface=sand height=0.12 shore=20 rough=0.04")
    print("struct Q kind=greatmachine size=150 seed=1 turns=3")
    for i, ch in enumerate('FGHI'):
        print(f"bastion {ch} size=26 dungeony=-6 seed={11 + i}")
    for i, (ch, hd) in enumerate(zip('KLMN', (0, 90, 180, 270))):
        print(f"struct {ch} kind=pylons seed={30 + i} count=3 spacing=40 heading={hd} height=42 reach=40")
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
