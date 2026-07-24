"""
Forge Volcano: a genuine volcano cone, not a crater lake. The struct
pass (0.49.0, kind=forge) carves a real crater into the summit, floods
its throat with actual lava, and hangs a crucible forge over the melt on
four colossal chains, reached by a railed catwalk from the rim. Three
frozen lava runs spill down the outer slopes.

    python tools/gen_forge_volcano.py > shapes/forge_volcano.txt

Suggested: /genisland shape=forge_volcano diameter=180 height=60 water=40 stone=rock-basalt sand=sand-basalt
"""
import math

W = H = 90


def main():
    grid = [['.'] * W for _ in range(H)]

    for r in range(H):
        for c in range(W):
            x, z = c + 0.5, r + 0.5
            d = math.hypot(x - 45, z - 45)
            a = math.atan2(z - 45, x - 45)
            lim = 38 * (1.0 + 0.10 * math.sin(3 * a + 1.1) + 0.06 * math.sin(7 * a))
            if d <= lim:
                grid[r][c] = 'v'

    grid[45][45] = 'F'

    print("# forge_volcano - a real volcano cone with a carved crater, a lava")
    print("# throat, and a crucible forge hung over the melt on four colossal")
    print("# chains with a railed catwalk from the rim (struct pass, kind=forge).")
    print("# Regenerate: python tools/gen_forge_volcano.py > shapes/forge_volcano.txt")
    print("# Suggested: /genisland shape=forge_volcano diameter=180 height=60 water=40 stone=rock-basalt sand=sand-basalt")
    print()
    print("region v rock=basalt fertility=verylow surface=barren climate=arid devastation=0.05 wildgrass=0 stones=0.04 height=1.0 shore=2 rough=0.30")
    print("ocean plunge=6")
    print("struct F kind=forge size=20 seed=9")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
