"""
Ruin isles: the small islands a world plan's `ruins` line gives each Better
Ruins megastructure (RuinIsles.cs), and Cardinals, the big flat island under
the Better Ruins cathedral.

A ruin isle is a gentle island with a broad flat meadow in the middle, where
the `ruin` structure (Q) levels a footing and places its megastructure, a
ring of trees and low rises round it, and a beach. Three climates:

    python tools/gen_ruin_isle.py temperate > shapes/ruin_isle.txt
    python tools/gen_ruin_isle.py cold > shapes/ruin_isle_cold.txt
    python tools/gen_ruin_isle.py arid > shapes/ruin_isle_arid.txt
    python tools/gen_ruin_isle.py cardinals > shapes/cardinals_island.txt

The ruins line sizes each isle to its ruin (diameter 2.3 x the ruin's longer
side + 80) and picks the structure; the flat middle is 0.30 of the diameter
across each way from the centre, room for the ruin's footprint and its blend.

Cardinals: /genisland shape=cardinals_island diameter=460 height=22 water=34 climate=cold stone=rock-andesite sand=sand-andesite
"""
import math
import sys

VARIANTS = {
    "temperate": dict(
        meadow="fertility=medium surface=grass wildgrass=0.35 stones=0.004 scatter=flower-cornflower-free:0.004,flower-wilddaisy-free:0.004,fieldmushroom:0.002",
        ring="fertility=medium surface=grass wildgrass=0.3 litter=0.6 forest=0.05 trees=englishoak,silverbirch,scotspine bushes=raspberry:0.004",
        beach="surface=sand shells=0.02", rock="granite"),
    "cold": dict(
        meadow="fertility=low surface=grass wildgrass=0.3 stones=0.008 scatter=flower-heather-free:0.006,flower-edelweiss-free:0.003",
        ring="fertility=low surface=grass wildgrass=0.25 litter=0.5 forest=0.06 trees=fir,larch,scotspine bushes=cloudberry:0.004,cranberry:0.003",
        beach="surface=sand shells=0.01", rock="andesite"),
    "arid": dict(
        meadow="fertility=verylow surface=sand sandy=0.6 wildgrass=0.08 stones=0.01 scatter=flower-goldenpoppy-free:0.002",
        ring="fertility=verylow surface=grass sandy=0.4 wildgrass=0.1 forest=0.01 trees=acacia boulders=0.01",
        beach="surface=sand shells=0.01", rock="sandstone"),
}
VARIANTS["cardinals"] = VARIANTS["cold"]


def main():
    v = sys.argv[1] if len(sys.argv) > 1 else "temperate"
    p = VARIANTS[v]
    big = v == "cardinals"
    W = H = 140 if big else 100
    C = W / 2.0
    R = W * 0.46

    def coast(th):
        return R * (1 + 0.05 * math.sin(3 * th + 0.8) + 0.035 * math.sin(5 * th + 2.4) + 0.02 * math.sin(9 * th + 1.0))

    flat = W * 0.30
    rises = [(math.radians(a), d, r) for a, d, r in ([(30, 0.38, 6), (150, 0.36, 7), (250, 0.37, 6), (330, 0.4, 5), (95, 0.39, 5)] if big
                                                      else [(40, 0.37, 4), (200, 0.36, 5), (300, 0.38, 4)])]
    lake = (math.radians(210), 0.22 * W, 7.5) if big else None
    grid = [['.'] * W for _ in range(H)]
    for r in range(H):
        for c in range(W):
            x, z = c + 0.5 - C, r + 0.5 - C
            d = math.hypot(x, z)
            th = math.atan2(z, x)
            L = coast(th)
            if d > L:
                continue
            if d > L - (5 if big else 4):
                grid[r][c] = 'B'
                continue
            ch = 'a'
            if d > flat:
                ch = 'f'
                for (a, dd, rr) in rises:
                    if math.hypot(x - math.cos(a) * dd * W, z - math.sin(a) * dd * W) < rr:
                        ch = 'h'
            if lake and math.hypot(x - math.cos(lake[0]) * lake[1], z - math.sin(lake[0]) * lake[1]) < lake[2]:
                ch = 'W'
            grid[r][c] = ch
    grid[int(C)][int(C)] = 'Q'

    rock = p["rock"]
    name = "cardinals_island" if big else ("ruin_isle" if v == "temperate" else "ruin_isle_" + v)
    if big:
        print("# cardinals_island - a broad, nearly flat cold island under the Better Ruins")
        print("# cathedral (struct Q, kind=ruin), a ring of fir and larch, low rises, a lake.")
        print("# Suggested: /genisland shape=cardinals_island diameter=460 height=22 water=34 climate=cold stone=rock-andesite sand=sand-andesite")
    else:
        print(f"# {name} - a small flat island for one Better Ruins megastructure (struct Q,")
        print("# kind=ruin; the plan's ruins line names the structure and sizes the island).")
    print(f"# Regenerate: python tools/gen_ruin_isle.py {v} > shapes/{name}.txt")
    print()
    print(f"region a rock={rock} {p['meadow']} height=0.62 shore=40 rough=0.03")
    print(f"region f rock={rock} {p['ring']} height=0.66 shore=36 rough=0.08")
    print(f"region h rock={rock} {p['ring']} height=0.85 shore=30 rough=0.14")
    print(f"region B rock={rock} {p['beach']} height=0.12 shore=22 rough=0.03")
    if big:
        print(f"region W rock={rock} fertility=low surface=grass pond=5 pondslope=8 cattails=0.2 clay=0.03 height=0.62 shore=40 rough=0.02")
        print("struct Q kind=ruin size=60 structure=megastructures-giantcathedral-dampus blend=10")
    else:
        print("struct Q kind=ruin size=40")
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
