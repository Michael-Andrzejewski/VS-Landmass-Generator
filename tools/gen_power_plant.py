"""
The Ruined Power Plant, the experimental warping site: a broad dark island
under three hyperboloid cooling towers (one broken off halfway), a finned
brutalist reactor hall with a burst containment dome, a transformer yard,
and 30 blocks down a vast pillared chamber holding the warp rig, a great
upright ring of rusted steel lit along its rim, the floor round it gone to
drock. Coolant tunnels run out under each tower. Power lines march away
north and west.

- P: the plant's ground, barren and dark (the structure levels its own pads)
- H: low heathy hills round it
- B: dark sand shore

    python tools/gen_power_plant.py > shapes/power_plant.txt

Suggested: /genisland shape=power_plant diameter=320 height=12 water=36 stone=rock-basalt sand=sand-basalt climate=cold
"""
import math

W = H = 160
C = 80.0


def lim(th):
    return 66.0 * (1.0 + 0.08 * math.sin(2 * th + 1.0) + 0.05 * math.sin(5 * th + 0.3) + 0.03 * math.sin(9 * th))


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
            if d > L - 4:
                grid[r][c] = 'B'
            elif d < 46:
                grid[r][c] = 'P'
            else:
                grid[r][c] = 'H'
    grid[80][80] = 'Q'
    grid[80 - 52][80 - 10] = 'K'
    grid[80 + 10][80 - 54] = 'L'
    print("# power_plant - the experimental warping site: three cooling towers (one")
    print("# broken), a finned reactor hall with a burst dome, a transformer yard,")
    print("# and underground the pillared warp chamber and its lit ring. Power lines")
    print("# march away north and west.")
    print("# Regenerate: python tools/gen_power_plant.py > shapes/power_plant.txt")
    print("# Suggested: /genisland shape=power_plant diameter=320 height=12 water=36 stone=rock-basalt sand=sand-basalt climate=cold")
    print()
    print("region P rock=basalt fertility=verylow surface=barren wildgrass=0.05 stones=0.02 devastation=0.004 height=0.45 shore=30 rough=0.05")
    print("region H rock=basalt rock2=andesite fertility=low surface=grass wildgrass=0.25 scatter=flower-heather-free:0.012 stones=0.02 height=0.8 shore=14 rough=0.35")
    print("region B rock=basalt surface=sand height=0.12 shore=20 rough=0.04")
    print("struct Q kind=powerplant size=84 seed=3")
    print("struct K kind=pylons seed=41 count=4 spacing=42 heading=350 height=44 above=10 reach=60")
    print("struct L kind=pylons seed=42 count=4 spacing=42 heading=265 height=44 above=10 reach=60")
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
