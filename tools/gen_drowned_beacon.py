"""
Drowned Beacon: a lighthouse whose keeper's islet sank. The struct pass
(0.49.0, kind=beacon) raises a 68-block tapered granite tower from the
sea floor: bottom third underwater with a flooded spiral stair, torn
rusted band at the waterline, glowing ghostlight lamp room at the top
with a gallery rail and cone roof. A broken sister stump stands 22
blocks away, its fallen lantern cage still glowing on the seabed.

    python tools/gen_drowned_beacon.py > shapes/drowned_beacon.txt

Suggested: /genisland shape=drowned_beacon diameter=120 height=6 water=40
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

    blob(34, 30, 2.5, 2, 'r')          # the keeper's awash rock
    blob(25, 37, 1.8, 1.5, 'k')        # a skerry
    grid[30][30] = 'B'                 # the beacon, standing in open water

    print("# drowned_beacon - a lighthouse whose islet sank: bottom third in the")
    print("# sea with a flooded spiral stair, torn rusty waterline band, a lamp")
    print("# room that still glows, and a broken sister stump with its fallen")
    print("# lantern aglow on the seabed (struct pass, kind=beacon).")
    print("# Regenerate: python tools/gen_drowned_beacon.py > shapes/drowned_beacon.txt")
    print("# Suggested: /genisland shape=drowned_beacon diameter=120 height=6 water=40")
    print()
    print("region r rock=granite fertility=verylow surface=barren climate=dry wildgrass=0 stones=0.05 height=0.30 shore=1 rough=0.40")
    print("region k rock=granite fertility=verylow surface=barren climate=dry wildgrass=0 stones=0.04 height=0.15 shore=1 rough=0.40")
    print("ocean plunge=16")
    print("struct B kind=beacon size=68 seed=3")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
