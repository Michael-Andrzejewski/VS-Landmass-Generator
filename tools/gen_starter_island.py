"""
Starter island for players: a mostly-grassy round island ~150 blocks across,
white-sand beach on the west, a small central pond, slate + peridotite below,
and a giant oak landmark.

    python tools/gen_starter_island.py > shapes/starter_island.txt
"""
import math

W, H = 96, 90
C = (48, 44)                      # island centre in cells

# Blobby round outline, clockwise, in grid-cell coordinates. The little jut on
# the west (around row 44) is the beach spit.
OUTLINE = [
    (48, 4), (60, 6), (70, 11), (79, 18), (85, 27), (88, 37),
    (88, 47), (85, 57), (79, 67), (71, 75), (60, 81), (49, 84),
    (39, 83), (29, 79), (21, 72), (16, 64), (13, 55),
    (12, 49), (6, 45), (12, 41), (14, 33),
    (19, 25), (27, 17), (36, 10), (43, 6),
]

POND_C, POND_R = (55, 58), 3.6    # ~7 cells -> ~12 blocks across
RICH_C, RICH_R = (30, 62), 9.0    # rich-soil patch, south-west
OAK = (46, 43)                    # the giant oak


def inside(px, py, poly):
    hit = False
    n = len(poly)
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        if (y1 > py) != (y2 > py):
            xint = x1 + (py - y1) * (x2 - x1) / (y2 - y1)
            if px < xint:
                hit = not hit
    return hit


def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def region(c, r):
    # Interior features first.
    if dist((c, r), POND_C) <= POND_R:
        return 'w'                      # pond
    if r < 20:
        return 'F'                      # sparse oak forest, north
    if r > 73:
        return 'L'                      # low-fertility slate, south edge
    if c < 16 and 34 < r < 56:
        return 'B'                      # white-sand beach, west spit
    if dist((c, r), RICH_C) <= RICH_R:
        return 'H'                      # rich soil, south-west
    return 'P'                          # grassy plains


def main():
    grid = []
    for r in range(H):
        row = []
        for c in range(W):
            row.append(region(c, r) if inside(c + 0.5, r + 0.5, OUTLINE) else '.')
        grid.append(row)

    if grid[OAK[1]][OAK[0]] != '.':
        grid[OAK[1]][OAK[0]] = 'O'

    print("# starter_island - player starting island, ~150 blocks across.")
    print("# Regenerate: python tools/gen_starter_island.py > shapes/starter_island.txt")
    print("# Suggested: /genisland shape=starter_island diameter=150 height=22")
    print("#")
    print("# Grassy, slate+peridotite below, white-sand west beach, central pond, giant oak.")
    print("# Deferred (add later): cattails/wild flax flora, surface copper, ruined chest,")
    print("# teleporter, devastated mine, shoreline boulders.")
    print()
    print("region P rock=slate rock2=peridotite fertility=medium surface=grass ores=copper:medium height=0.8 shore=11 rough=0.16")
    print("region F rock=slate rock2=peridotite fertility=medium surface=grass ores=copper:sparse forest=0.03 trees=oak height=0.85 shore=11 rough=0.2")
    print("region H rock=slate rock2=peridotite fertility=high   surface=grass ores=copper:medium height=0.8 shore=11 rough=0.15")
    print("region L rock=slate                  fertility=low    surface=grass ores=copper:medium height=0.62 shore=6 rough=0.42")
    print("region B rock=slate sand=sand-chalk  surface=sand     height=0.12 shore=30 rough=0.06")
    print("region w rock=slate rock2=peridotite fertility=medium surface=grass height=0.8 shore=11 pond=4")
    print("tree O oak 2.4")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
