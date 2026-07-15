"""
Rasterize the hand-drawn 'ideal island' into a shape grid for /genisland.

The drawing is traced as one closed outline in image pixel space, then each
land cell is tagged with the region it falls in (forest / plains / beach /
rocky). Re-run this to tweak the shape:

    python tools/gen_ideal_island.py > shapes/ideal_island.txt
"""

W, H = 96, 90                      # grid cells
X0, X1 = 150.0, 1060.0             # image px window mapped onto the grid
Y0, Y1 = 30.0, 1020.0

# Closed outline of the island, traced clockwise from the top of the crown.
# The big eastern wedge (channel) is part of this outline: it runs west along
# y~618 to the apex at x~388, then back out east and down to the arm's tip.
OUTLINE = [
    (430, 45), (560, 38), (660, 42), (740, 50),
    (752, 150), (750, 230), (738, 290), (700, 330),
    # beach spit, jutting east
    (697, 352), (860, 352), (1000, 352), (1035, 360),
    (1042, 420), (1038, 462),
    (930, 530), (840, 585), (760, 530), (700, 610), (680, 618),
    # channel: west to the apex, then back east along its southern shore
    (500, 618), (388, 618),
    (600, 672), (800, 725), (950, 762),
    # southern rocky arm: east tip, then back west along the bottom
    (958, 790), (900, 845), (800, 900), (700, 955),
    (600, 985), (500, 1005), (430, 1012), (350, 1000), (280, 965), (232, 928),
    # west coast, heading north
    (195, 860), (178, 780), (172, 700), (168, 600), (170, 520),
    (182, 450), (200, 382), (250, 345), (300, 325), (330, 312),
    # north-west shoulder back up to the crown
    (360, 270), (385, 215), (400, 150), (415, 90),
]

OAK = (270, 617)  # the circled "Tall oak tree"


def inside(px, py, poly):
    """Even-odd point-in-polygon."""
    hit = False
    n = len(poly)
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        if (y1 > py) != (y2 > py):
            xint = x1 + (py - y1) * (x2 - x1) / (y2 - y1)
            if px < xint:
                hit = not hit
    return hit


def region(px, py):
    """Which labelled area of the drawing this land pixel belongs to."""
    # Beach: the eastern spit only (not the main island's east shoulder above it).
    if px > 692 and 345 <= py < 630:
        return 'B'
    # Forested area: the northern lobe.
    if py < 330:
        return 'F'
    # Plains/grass: down to "grass ends here" in the west, and above the channel.
    grass_ends = 720 if px < 390 else 625
    if py < grass_ends:
        return 'P'
    # Everything left is the rocky/slate south.
    return 'R'


def main():
    grid = []
    for r in range(H):
        py = Y0 + (r + 0.5) * (Y1 - Y0) / H
        row = []
        for c in range(W):
            px = X0 + (c + 0.5) * (X1 - X0) / W
            row.append(region(px, py) if inside(px, py, OUTLINE) else '.')
        grid.append(row)

    # Stamp the oak marker.
    oc = int((OAK[0] - X0) / (X1 - X0) * W)
    orow = int((OAK[1] - Y0) / (Y1 - Y0) * H)
    if grid[orow][oc] != '.':
        grid[orow][oc] = 'O'

    print("# ideal_island - traced from the hand-drawn map.")
    print("# Regenerate with: python tools/gen_ideal_island.py > shapes/ideal_island.txt")
    print("#")
    print("# F forested north, granite, iron below")
    print("# P plains/grass, granite, copper below and in the cliffsides")
    print("# B eastern beach spit, granite sand")
    print("# R southern rocky arm: slate, no grass, outcrops and sheer cliffs")
    print("# O the tall oak")
    print()
    print("region F rock=granite surface=grass  ores=iron:rare      forest=0.05  trees=oak,pine  height=1.0  shore=9  rough=0.35")
    print("region P rock=granite surface=grass  ores=copper:rich    forest=0.004 trees=oak       height=0.78 shore=9  rough=0.22")
    print("region B rock=granite surface=sand   ores=               forest=0     height=0.10 shore=34 rough=0.08")
    print("region R rock=slate   surface=rocksand ores=copper:medium forest=0     height=0.66 shore=4  rough=0.7")
    print("tree O oak 2.2")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
