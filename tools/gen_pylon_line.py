"""
Sunken power line: a march of lattice transmission towers across open sea,
standing in 30 blocks of water. Their crossarms break the surface; some lean
hard, some have snapped and lie on the floor, cables sag from the survivors.
Struct pass, kind=pylons (MegaStructs.cs). Open water, nothing else.

    python tools/gen_pylon_line.py > shapes/pylon_line.txt

Suggested: /genisland shape=pylon_line diameter=280 height=6 water=30 stone=rock-basalt sand=sand-basalt
"""
W = H = 140


def main():
    grid = [['.'] * W for _ in range(H)]
    grid[70][70] = 'P'
    print("# pylon_line - a line of sunken lattice power pylons across open sea,")
    print("# crossarms above the waves, some leaning, some snapped (kind=pylons).")
    print("# Regenerate: python tools/gen_pylon_line.py > shapes/pylon_line.txt")
    print("# Suggested: /genisland shape=pylon_line diameter=280 height=6 water=30 stone=rock-basalt sand=sand-basalt")
    print()
    print("struct P kind=pylons seed=4 count=6 spacing=44 heading=60 height=46 above=12 reach=70")
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
