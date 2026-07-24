"""
Giant Serpent Skeleton: a wide ring of white chalk islets around a deep
lagoon; on the lagoon floor a 330-block serpent skeleton lying in an
omega pose (straight neck and tail, half-circle body), distinct spine
and ribcage along its whole length, horned skull with glowing eyes, and
at the heart of the half-circle a large destroyed ship torn in two.
Everything underwater; ghostlights stud spine and rib tips. Struct
pass, kind=serpent (size= is the half-circle radius).

    python tools/gen_giant_serpent_skeleton.py > shapes/giant_serpent_skeleton.txt

Suggested: /genisland shape=giant_serpent_skeleton diameter=300 height=8 water=38
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

    # ten chalk islets on a wide ring, with two open passes into the lagoon
    n = 10
    for i in range(n):
        a = i * 2 * math.pi / n + 0.3
        if i in (2, 7):
            continue                        # the passes
        cx = 55 + math.cos(a) * 44
        cz = 55 + math.sin(a) * 44
        blob(cx, cz, 4 + (i % 3), 3.5 + (i % 2), 'v', i * 1.7)

    grid[55][55] = 'S'

    print("# giant_serpent_skeleton - a chalk islet ring around a deep wide lagoon;")
    print("# on its floor a 330-block serpent skeleton in an omega pose: straight")
    print("# neck and tail, half-circle body, full spine + ribcage, horned skull,")
    print("# and a large ship torn in two at the half-circle's heart (kind=serpent).")
    print("# Regenerate: python tools/gen_giant_serpent_skeleton.py > shapes/giant_serpent_skeleton.txt")
    print("# Suggested: /genisland shape=giant_serpent_skeleton diameter=300 height=8 water=38")
    print()
    print("region v rock=chalk fertility=verylow surface=barren climate=dry wildgrass=0 stones=0.06 height=0.60 shore=2 rough=0.45")
    print("ocean plunge=8 basin=78 depth=38")
    print("struct S kind=serpent size=62 seed=5")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
