"""
Giant Serpent Skeleton: a ring of white chalk islets around a deep
lagoon, and on the lagoon floor a colossal sea-serpent skeleton curled
around a sunken ship, ribs arching 20+ blocks, entirely underwater.
Ghostlight studs its edges and its eye sockets, so at night the whole
coil glows up through the water. Struct pass, kind=serpent.

    python tools/gen_giant_serpent_skeleton.py > shapes/giant_serpent_skeleton.txt

Suggested: /genisland shape=giant_serpent_skeleton diameter=220 height=8 water=38
"""
import math

W = H = 110


def main():
    grid = [['.'] * W for _ in range(H)]

    def blob(cx, cz, rx, rz, kind, ph):
        for r in range(H):
            for c in range(W):
                x, z = c + 0.5, r + 0.5
                d = math.hypot((x - cx) / rx, (z - cz) / rz)
                a = math.atan2(z - cz, x - cx)
                lim = 1.0 + 0.3 * (0.5 * math.sin(3 * a + ph) + 0.3 * math.sin(5 * a + ph * 2))
                if d <= lim:
                    grid[r][c] = kind

    # nine chalk vertebra-islets, with two open passes into the lagoon
    n = 9
    for i in range(n):
        a = i * 2 * math.pi / n + 0.3
        if i in (2, 6):
            continue                        # the passes
        cx = 55 + math.cos(a) * 27
        cz = 55 + math.sin(a) * 27
        blob(cx, cz, 3.5 + (i % 3), 3 + (i % 2), 'v', i * 1.7)

    grid[55][55] = 'S'

    print("# giant_serpent_skeleton - a chalk islet ring around a deep lagoon; on")
    print("# its floor a colossal serpent skeleton curled around a sunken ship,")
    print("# ribs arching 20+, ghostlights along every edge (struct pass, kind=serpent).")
    print("# Regenerate: python tools/gen_giant_serpent_skeleton.py > shapes/giant_serpent_skeleton.txt")
    print("# Suggested: /genisland shape=giant_serpent_skeleton diameter=220 height=8 water=38")
    print()
    print("region v rock=chalk fertility=verylow surface=barren climate=dry wildgrass=0 stones=0.06 height=0.60 shore=2 rough=0.45")
    print("ocean plunge=8 basin=56 depth=38")
    print("struct S kind=serpent size=34 seed=5")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
