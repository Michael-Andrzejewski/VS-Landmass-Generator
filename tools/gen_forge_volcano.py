"""
Forge Volcano: a WIDE flat island barely above the waterline, sloping
gently at its beaches, with a genuine volcano cone rising from its
center. The cone is stepped concentric height rings (the terrain pass
blurs region heights over ~5 cells, so the steps read as one smooth
slope). Low-fertility soil and basalt sand on the flats, a terra preta
ring surrounding the cone, bare basalt on the volcano itself. The
struct pass (kind=forge, size=40) carves the doubled crater, floods the
throat with lava, and hangs the crucible forge over the melt.

    python tools/gen_forge_volcano.py > shapes/forge_volcano.txt

Suggested: /genisland shape=forge_volcano diameter=400 height=80 water=40 stone=rock-basalt sand=sand-basalt
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
            wob = 1.0 + 0.08 * math.sin(3 * a + 1.1) + 0.05 * math.sin(7 * a)
            if d > 42 * wob:
                continue                    # open sea
            dc = d / wob                    # de-wobbled distance for the rings
            if dc <= 6:
                grid[r][c] = 'z'            # summit
            elif dc <= 10:
                grid[r][c] = 'y'
            elif dc <= 14:
                grid[r][c] = 'x'
            elif dc <= 18:
                grid[r][c] = 'w'            # cone base
            elif dc <= 23:
                grid[r][c] = 't'            # terra preta ring around the volcano
            else:
                grid[r][c] = 'l'            # the wide low flats

    grid[45][45] = 'F'

    print("# forge_volcano - a wide flat low island with a genuine volcano cone")
    print("# rising from its center: low soil flats with basalt sand, a terra preta")
    print("# ring around the cone, stepped basalt slopes, and a doubled crater with")
    print("# a lava throat and the hanging crucible forge (struct pass, kind=forge).")
    print("# Regenerate: python tools/gen_forge_volcano.py > shapes/forge_volcano.txt")
    print("# Suggested: /genisland shape=forge_volcano diameter=400 height=80 water=40 stone=rock-basalt sand=sand-basalt")
    print()
    print("region l rock=basalt fertility=low surface=barren climate=arid sandy=0.35 wildgrass=0.06 stones=0.05 height=0.05 shore=8 rough=0.15")
    print("region t rock=basalt fertility=terrapreta surface=barren climate=arid wildgrass=0.10 stones=0.04 devastation=0.04 height=0.10 shore=6 rough=0.20")
    print("region w rock=basalt fertility=verylow surface=rock climate=arid stones=0.05 height=0.28 shore=6 rough=0.30")
    print("region x rock=basalt surface=rock climate=arid stones=0.04 height=0.52 shore=6 rough=0.30")
    print("region y rock=basalt surface=rock climate=arid stones=0.04 height=0.76 shore=6 rough=0.30")
    print("region z rock=basalt surface=rock climate=arid height=1.0 shore=6 rough=0.25")
    print("ocean plunge=6")
    print("struct F kind=forge size=40 seed=9")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
