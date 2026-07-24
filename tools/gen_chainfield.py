"""
The Chainfield: a sealed orb of devastated rock floating half-out of
the deep sea, crusted with devastation growth, its shell cracked and
leaking green light; colossal rusted chains run taut from its hide
straight down through the water and into the rock, anchored at the
mantle. It was chained to the bottom, and rose anyway. Struct pass,
kind=chains.

    python tools/gen_chainfield.py > shapes/chainfield.txt

Suggested: /genisland shape=chainfield diameter=200 height=6 water=60 stone=rock-basalt sand=sand-basalt
"""
W = H = 100


def main():
    grid = [['.'] * W for _ in range(H)]

    grid[50][50] = 'C'

    print("# chainfield - a sealed orb of devastated rock floating half-out of the")
    print("# sea, thorned and cracked, green light leaking; taut colossal chains")
    print("# run from its hide down into the mantle rock (struct pass, kind=chains).")
    print("# Regenerate: python tools/gen_chainfield.py > shapes/chainfield.txt")
    print("# Suggested: /genisland shape=chainfield diameter=200 height=6 water=60 stone=rock-basalt sand=sand-basalt")
    print()
    print("ocean plunge=20 basin=75 depth=55")
    print("struct C kind=chains size=60 seed=11")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
