"""
Lighthouse: a drowned lighthouse whose keeper's islet sank BENEATH it.
The tower stands centered on a submerged granite shoal (flood-capped a
few blocks under the surface), so it clearly rises from a landmass, not
from open water. The struct pass (kind=lighthouse) raises the tower:
interior spiral stair through planked room floors, hollow glass lamp
room with a baked ghostlight, broken sister stump on the same shoal.

    python tools/gen_lighthouse.py > shapes/lighthouse.txt

Suggested: /genisland shape=lighthouse diameter=120 height=6 water=40
"""
import math

W = H = 60


def main():
    grid = [['.'] * W for _ in range(H)]

    def blob(cx, cz, rx, rz, kind, jag=0.3, ph=0.0):
        for r in range(H):
            for c in range(W):
                x, z = c + 0.5, r + 0.5
                d = math.hypot((x - cx) / rx, (z - cz) / rz)
                a = math.atan2(z - cz, x - cx)
                lim = 1.0 + jag * (0.5 * math.sin(3 * a + ph) + 0.3 * math.sin(5 * a + ph * 2))
                if d <= lim:
                    grid[r][c] = kind

    blob(30, 30, 15, 14, 'u', jag=0.22, ph=1.0)  # the drowned shoal, centered under the tower
    blob(34, 30, 2.5, 2, 'r')          # the keeper's awash rock at the tower foot
    blob(25, 37, 1.8, 1.5, 'k')        # a skerry
    grid[30][30] = 'B'                 # the lighthouse, dead center on the shoal

    print("# lighthouse - a drowned lighthouse centered on its sunken keeper's shoal:")
    print("# interior spiral stair through planked room floors, hollow glass lamp room")
    print("# with a baked ghostlight, torn rusty waterline band, and a broken sister")
    print("# stump on the same shoal (struct pass, kind=lighthouse).")
    print("# Regenerate: python tools/gen_lighthouse.py > shapes/lighthouse.txt")
    print("# Suggested: /genisland shape=lighthouse diameter=120 height=6 water=40")
    print()
    print("region u rock=granite surface=rock climate=dry flood=3 height=0.20 shore=2 rough=0.30")
    print("region r rock=granite fertility=verylow surface=barren climate=dry wildgrass=0 stones=0.05 height=0.30 shore=1 rough=0.40")
    print("region k rock=granite fertility=verylow surface=barren climate=dry wildgrass=0 stones=0.04 height=0.15 shore=1 rough=0.40")
    print("ocean plunge=16")
    print("struct B kind=lighthouse size=68 seed=3")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
