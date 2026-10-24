"""
The Great Machine: the heart of the Rot, on a shattered island twice the old
size. The machine (the `greatmachine` megastructure, GreatMachine.cs) hangs
in a vast impact crater at the centre: a 30-block steel sphere on chains
between floating islands, propped by stalagmites rising out of an abyss,
a double helix rising from its top and two crystal blades like a Halo
energy sword standing either side of it. Ruined walls ring the crater, and
chasms run out from the abyss to the sea. This file only draws the land:
the plateau, the crater bowl, the coast and the three landing ramps. The
structure pass carves the abyss and the chasms and builds everything else.

- P: the devastated plateau, about 50 above the sea
- C: sheer basalt cliffs at the coast
- R, B: three ramps from a sand landing up to the plateau (south, west, north-east)
- m, n: the crater's raised rim and its outer slope
- a..l: the crater bowl, falling in rings to the floor
- o: the crater floor, just above sea level (the abyss is cut into it)

    python tools/gen_great_machine.py > shapes/great_machine.txt

Suggested: /genisland shape=great_machine diameter=1200 height=50 water=30 stone=rock-basalt sand=sand-basalt climate=arid
"""
import math

W = H = 300
C = 150.0
BPC = 4.0          # blocks per cell at the suggested diameter

RINGS = "abcdefghijkl"
FLOOR_R, RIM_IN, RIM_OUT, OUTER = 108.0, 246.0, 262.0, 292.0   # blocks
RAMPS = (90.0, 222.0, 340.0)   # map angle of each landing (atan2(dz, dx): 90 = south)


def lim(th):
    return 140.0 * (1.0 + 0.05 * math.sin(3 * th + 0.4) + 0.035 * math.sin(7 * th + 1.1)
                    + 0.02 * math.sin(13 * th) + 0.012 * math.sin(23 * th + 2.3))


def bowl(rb):
    """Crater height fraction at a radius in blocks (None outside the crater)."""
    if rb < FLOOR_R:
        return 0.06
    if rb < RIM_IN:
        s = (rb - FLOOR_R) / (RIM_IN - FLOOR_R)
        return 0.06 + 1.10 * s ** 1.6
    return None


def angdiff(a, b):
    return abs((a - b + 180.0) % 360.0 - 180.0)


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
            rb = d * BPC
            deg = math.degrees(th) % 360.0
            ramp = min(angdiff(deg, a) for a in RAMPS) < 5.5
            if rb < FLOOR_R:
                ch = 'o'
            elif rb < RIM_IN:
                ch = RINGS[min(len(RINGS) - 1, int((rb - FLOOR_R) / ((RIM_IN - FLOOR_R) / len(RINGS))))]
            elif rb < RIM_OUT:
                ch = 'm'
            elif rb < OUTER:
                ch = 'n'
            elif ramp and d > L - 2.2:
                ch = 'B'
            elif ramp and rb > L * BPC - 190:
                ch = 'R'
            elif d > L - 2.5:
                ch = 'C'
            else:
                ch = 'P'
            grid[r][c] = ch
    grid[150][150] = 'Q'
    for i, (fx, fz) in enumerate([(-1, -1), (1, -1), (1, 1), (-1, 1)]):
        grid[int(C + fz * 77.8)][int(C + fx * 77.8)] = 'FGHI'[i]
    for i, (lx, lz) in enumerate([(0, -1), (1, 0), (0, 1), (-1, 0)]):
        grid[int(C + lz * 90)][int(C + lx * 90)] = 'KLMN'[i]

    print("# great_machine - the heart of the Rot: a 30-block steel sphere hung on chains")
    print("# between floating islands over an abyss in a vast impact crater, a double helix")
    print("# on top, two crystal blades either side; ruined walls round the crater, chasms")
    print("# from the abyss to the sea, ruined forts on the diagonals, pylon lines on the axes.")
    print("# Regenerate: python tools/gen_great_machine.py > shapes/great_machine.txt")
    print("# Suggested: /genisland shape=great_machine diameter=1200 height=50 water=30 stone=rock-basalt sand=sand-basalt climate=arid")
    print()
    dev = "scatter=devgrowth-thorns:0.020,devgrowth-bush:0.012,devgrowth-shard:0.008,loosegears-1:0.002,metalpartpile-tiny:0.002"
    crater = "scatter=devgrowth-shard:0.014,devgrowth-thorns:0.016,metalpartpile-tiny:0.004,metalpartpile-small:0.002,loosegears-2:0.003"
    print(f"region P rock=basalt rock2=andesite fertility=verylow surface=barren devastation=0.10 {dev} wildgrass=0.03 stones=0.03 height=1.0 shore=12 rough=0.14")
    print("region C rock=basalt surface=rock stones=0.02 boulders=0.008 height=0.92 shore=3 rough=0.3")
    print("region R rock=basalt surface=rocksand devastation=0.04 stones=0.04 boulders=0.006 wildgrass=0.02 height=1.0 shore=150 rough=0.1")
    print("region B rock=basalt surface=sand height=0.08 shore=20 rough=0.03")
    print("region m rock=basalt rock2=andesite surface=rocksand devastation=0.12 boulders=0.02 stones=0.05 height=1.16 shore=40 rough=0.2")
    print("region n rock=basalt rock2=andesite surface=rocksand devastation=0.12 boulders=0.01 stones=0.04 height=1.08 shore=40 rough=0.16")
    step = (RIM_IN - FLOOR_R) / len(RINGS)
    for i, ch in enumerate(RINGS):
        h = bowl(FLOOR_R + (i + 0.5) * step)
        print(f"region {ch} rock=basalt rock2=andesite surface=rocksand devastation=0.35 {crater} stones=0.04 boulders=0.006 height={h:.3f} shore=40 rough=0.06")
    print(f"region o rock=basalt rock2=andesite surface=rocksand devastation=0.5 {crater} stones=0.05 height=0.06 shore=40 rough=0.05")
    print("ocean plunge=20")
    print("struct Q kind=greatmachine seed=1 turns=3 fog=640 fogfade=200")
    for i, ch in enumerate('FGHI'):
        print(f"bastion {ch} size=26 dungeony=-6 seed={11 + i}")
    for i, (ch, hd) in enumerate(zip('KLMN', (0, 90, 180, 270))):
        print(f"struct {ch} kind=pylons seed={30 + i} count=3 spacing=45 heading={hd} height=42 reach=40")
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
