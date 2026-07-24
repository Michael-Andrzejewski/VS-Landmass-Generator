"""
The Chainfield: no island to speak of. Colossal rusted anchor chains rise
taut out of the deep at angles and vanish into the sky-fog or arc over
the water between two seabed anchors (those dips are walkable). A few
chains still carry wrecks hooked mid-air. Struct pass 0.49.0,
kind=chains.

    python tools/gen_chainfield.py > shapes/chainfield.txt

Suggested: /genisland shape=chainfield diameter=200 height=6 water=60 stone=rock-basalt sand=sand-basalt
"""
import math

W = H = 100


def main():
    grid = [['.'] * W for _ in range(H)]

    def blob(cx, cz, rx, rz, kind):
        for r in range(H):
            for c in range(W):
                if math.hypot((c + 0.5 - cx) / rx, (r + 0.5 - cz) / rz) <= 1.0:
                    grid[r][c] = kind

    blob(30, 40, 2.5, 2, 't')
    blob(70, 64, 2, 2.5, 't')
    grid[50][50] = 'C'

    print("# chainfield - colossal anchor chains rising taut from the deep,")
    print("# some sagging walkably over the water between two anchors, some")
    print("# carrying hooked wrecks mid-air (struct pass, kind=chains).")
    print("# Regenerate: python tools/gen_chainfield.py > shapes/chainfield.txt")
    print("# Suggested: /genisland shape=chainfield diameter=200 height=6 water=60 stone=rock-basalt sand=sand-basalt")
    print()
    print("region t rock=basalt fertility=verylow surface=barren climate=arid devastation=0.06 wildgrass=0 stones=0.03 height=0.25 shore=1 rough=0.45")
    print("ocean plunge=20 basin=75 depth=55")
    print("struct C kind=chains size=60 seed=11")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
