"""
The Kneeling Colossus: a ~110-block giant kneeling on the deep floor,
carved from the local basalt with rusted-iron highlights, ghostlight
eyes behind the visor and glowing crown studs. Shield tucked at the
chest, solid rusted greatsword raised point-up. Only the crown and the
blade break the surface; everything else is a dive. Struct pass,
kind=colossus. Open deep water, nothing else: no stubs, no pillars.

    python tools/gen_colossus_deep.py > shapes/colossus_deep.txt

Suggested: /genisland shape=colossus_deep diameter=160 height=6 water=95 maxdepth=120 stone=rock-basalt sand=sand-basalt
"""

W = H = 80


def main():
    grid = [['.'] * W for _ in range(H)]
    grid[40][40] = 'M'

    print("# colossus_deep - a kneeling giant carved from basalt with rusted-iron")
    print("# highlights, ghostlight eyes and crown studs, solid rusted greatsword.")
    print("# Only the crown and the blade break the surface (struct pass, kind=colossus).")
    print("# Regenerate: python tools/gen_colossus_deep.py > shapes/colossus_deep.txt")
    print("# Suggested: /genisland shape=colossus_deep diameter=160 height=6 water=95 maxdepth=120 stone=rock-basalt sand=sand-basalt")
    print()
    print("ocean plunge=30 basin=60 depth=95")
    print("struct M kind=colossus size=60 seed=7")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
