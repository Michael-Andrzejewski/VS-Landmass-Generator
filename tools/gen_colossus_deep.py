"""
The Kneeling Colossus: a ~110-block armored giant kneeling on the deep
floor just above the mantle, in steel plate with gold trim, falx arm
outstretched and shield tucked at the chest. Only the crown of the great
helm and the falx blade break the surface; everything else is a dive.
Struct pass 0.49.0, kind=colossus. Three submerged rock stubs ring the
site so the deep water reads as a place, not a hole.

    python tools/gen_colossus_deep.py > shapes/colossus_deep.txt

Suggested: /genisland shape=colossus_deep diameter=160 height=6 water=95 maxdepth=120 stone=rock-basalt sand=sand-basalt
"""
import math

W = H = 80


def main():
    grid = [['.'] * W for _ in range(H)]

    def blob(cx, cz, rx, rz, kind):
        for r in range(H):
            for c in range(W):
                if math.hypot((c + 0.5 - cx) / rx, (r + 0.5 - cz) / rz) <= 1.0:
                    grid[r][c] = kind

    blob(40, 8, 2.5, 2, 'u')
    blob(68, 56, 2, 2.5, 'u')
    blob(12, 58, 2.5, 2, 'u')
    grid[40][40] = 'M'

    print("# colossus_deep - a kneeling armored giant on the deep floor, steel")
    print("# plate and gold trim, falx outstretched, shield tucked. Only the helm")
    print("# crown and the falx blade break the surface (struct pass, kind=colossus).")
    print("# Regenerate: python tools/gen_colossus_deep.py > shapes/colossus_deep.txt")
    print("# Suggested: /genisland shape=colossus_deep diameter=160 height=6 water=95 maxdepth=120 stone=rock-basalt sand=sand-basalt")
    print()
    print("region u rock=basalt surface=rock climate=arid flood=3 height=0.30 shore=1 rough=0.30")
    print("ocean plunge=30 basin=60 depth=95")
    print("struct M kind=colossus size=60 seed=7")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
