"""
Granary Terraces: a small farmed hill island gone feral, in four climates.

A pre-devastation farm: stepped crop terraces wrapping an off-centre summit,
each step held by a low rock retaining lip, a stone-lined cistern on the
second terrace, a couple of thickets where the scrub took a field back, two
devastated patches where something went wrong, and the ruined granary itself
on the summit yard (struct pass, kind=granary: silo, barn, threshing floor,
yard wall). Overall it is a NICE island: fed soil, wild crops everywhere,
flowers, a landmark tree.

Four variants share the terrain and swap rock, trees, flowers and crops:

    python tools/gen_granary_terraces.py temperate > shapes/granary_temperate.txt
    python tools/gen_granary_terraces.py arid      > shapes/granary_arid.txt
    python tools/gen_granary_terraces.py lush      > shapes/granary_lush.txt
    python tools/gen_granary_terraces.py cold      > shapes/granary_cold.txt

Suggested: /genisland shape=granary_<climate> diameter=150 height=26
           stone=rock-<rock> sand=<sand>
(each generated file prints its own suggested line)
"""
import math
import sys

# The terrain pass smooths region heights over a ~5 CELL radius, so the grid
# resolution is what decides whether a terrace reads as a step or as a ramp.
# At 96 cells across 150 blocks a step blurred over 8 blocks and the whole
# hill came out as one smooth dome (measured in the exact dump); at 140 the
# same step blurs over ~5 blocks and stays a step.
W, H = 140, 132
CX, CY = 70.0, 66.0
SX, SZ = -6.0, -4.4          # the summit sits off centre, so the terraces are eccentric

# Every palette below uses block codes verified against the 1.22 assets:
# crop-{type}-{stage} stages come from blocktypes/plant/crop/*.json, flower
# names from worldproperties/block/flower.json, tree generators from
# worldgen/treegen/*.json.
PALETTES = {
    "temperate": dict(
        climate="temperate", rock="granite", rock2="andesite", sand="sand-granite",
        trees="oak,birch", thicket="oak,birch", orchard="oak",
        bushes="raspberry:0.012,blackcurrant:0.007",
        thicketbushes="raspberry:0.030,blackberry:0.020,blueberry:0.012",
        flowers="cornflower:0.010,forgetmenot:0.010,wilddaisy:0.008,cowparsley:0.006",
        ferns="eaglefern:0.020,deerfern:0.012",
        cropA="crop-spelt-9:0.10,crop-spelt-7:0.08,crop-rye-8:0.05",
        cropB="crop-flax-9:0.09,crop-flax-6:0.06,crop-cabbage-9:0.05",
        cropC="crop-turnip-5:0.08,crop-onion-6:0.06,crop-carrot-6:0.05",
        cropD="crop-rye-6:0.06,crop-parsnip-7:0.05,crop-turnip-3:0.04",
        pondplants="cattails=0.45 lilies=0.10",
        landmark="oak 2.6",
    ),
    "arid": dict(
        climate="arid", rock="sandstone", rock2="limestone", sand="sand-sandstone",
        trees="acacia", thicket="acacia,truemulga", orchard="acacia",
        bushes="blackcurrant:0.005",
        thicketbushes="blackcurrant:0.014",
        flowers="goldenpoppy:0.012,mugwort:0.008,orangemallow:0.008,westerngorse:0.005",
        ferns="barrelcactus-normal:0.010",
        cropA="crop-sunflower-12:0.09,crop-sunflower-8:0.07,crop-amaranth-9:0.06",
        cropB="crop-amaranth-7:0.09,crop-fennel-9:0.06,crop-licorice-9:0.05",
        cropC="crop-licorice-6:0.07,crop-fennel-5:0.06,crop-onion-7:0.04",
        cropD="crop-amaranth-5:0.06,crop-fennel-7:0.04",
        pondplants="cattails=0.30 lilies=0.06",
        landmark="acacia 2.4",
    ),
    "lush": dict(
        climate="lush", rock="claystone", rock2="limestone", sand="sand-limestone",
        trees="kapok,purpleheart", thicket="kapok,ebony", orchard="purpleheart",
        bushes="raspberry:0.008,beautyberry:0.008",
        thicketbushes="beautyberry:0.026,raspberry:0.016",
        flowers="orangemallow:0.012,goldenpoppy:0.008,flower-croton-medium-lemongreen:0.006,flower-rafflesia-red:0.0015",
        ferns="cinnamonfern:0.020,eaglefern:0.014",
        cropA="crop-rice-10:0.10,crop-rice-7:0.08,crop-soybean-11:0.05",
        cropB="crop-cassava-9:0.09,crop-peanut-9:0.07,crop-soybean-8:0.05",
        cropC="crop-pineapple-13:0.06,crop-cassava-6:0.07,crop-peanut-6:0.05",
        cropD="crop-rice-5:0.06,crop-peanut-4:0.05",
        pondplants="cattails=0.50 lilies=0.16",
        landmark="largekapok 2.2",
    ),
    "cold": dict(
        climate="cold", rock="slate", rock2="andesite", sand="sand-slate",
        trees="scotspine,silverbirch", thicket="fir,scotspine", orchard="silverbirch",
        bushes="cranberry:0.012,cloudberry:0.008,birch:0.006",
        thicketbushes="cranberry:0.028,cloudberry:0.018,birch:0.014",
        flowers="edelweiss:0.010,heather:0.012,bluebell:0.008,lilyofthevalley:0.006",
        ferns="hartstongue:0.014,horsetail:0.012",
        cropA="crop-rye-9:0.10,crop-rye-6:0.07,crop-cabbage-10:0.05",
        cropB="crop-turnip-5:0.09,crop-parsnip-8:0.06,crop-cabbage-7:0.05",
        cropC="crop-carrot-7:0.07,crop-parsnip-5:0.06,crop-turnip-3:0.05",
        cropD="crop-rye-5:0.06,crop-carrot-4:0.04",
        pondplants="cattails=0.35 lilies=0.08",
        landmark="scotspine 2.6",
    ),
}

# Terrace bands as a FRACTION of the summit-to-coast distance on each ray,
# not as absolute radii: the summit sits off centre, so a fixed radius would
# run the lowest terraces straight into the beach on the near side and leave
# a bare apron on the far side.
BANDS = [(0.28, 'Y'), (0.46, 'A'), (0.50, 'p'), (0.66, 'B'), (0.70, 'q'),
         (0.86, 'C'), (0.90, 's'), (0.96, 'o')]

# (band fraction, bearing degrees, radius in cells, region letter)
FEATURES = [(0.58, 40, 4.7, 'w'),       # the cistern, on terrace B
            (0.58, 150, 6.6, 'T'),      # thicket on terrace B
            (0.78, 250, 6.6, 'U'),      # thicket on terrace C
            (0.78, 320, 6.6, 'x'),      # devastated patch on terrace C
            (0.37, 80, 5.5, 'X')]       # devastated scar on terrace A


def coast_r(ang):
    return 57.0 * (1.0
                   + 0.055 * math.sin(2 * ang + 0.7)
                   + 0.040 * math.sin(3 * ang + 2.6)
                   + 0.022 * math.sin(5 * ang + 4.4))


def wobble(a):
    """The terrace rings breathe, so they never read as compass circles."""
    return 1.0 + 0.070 * math.sin(2 * a + 1.1) + 0.045 * math.sin(3 * a + 3.3)


_rcs_cache = {}


def summit_coast(a):
    """Distance from the summit to the coast along bearing `a` (radians)."""
    key = round(a * 240)
    if key in _rcs_cache:
        return _rcs_cache[key]
    ca, sa = math.cos(a), math.sin(a)
    t = 5.0
    while t < 100.0:
        x, z = SX + ca * t, SZ + sa * t
        if math.hypot(x, z) > coast_r(math.atan2(z, x)):
            break
        t += 0.25
    _rcs_cache[key] = t
    return t


def feature_centre(frac, deg):
    a = math.radians(deg)
    d = frac * summit_coast(a) * wobble(a)
    return SX + math.cos(a) * d, SZ + math.sin(a) * d


def region(c, r, features):
    dx, dz = c + 0.5 - CX, r + 0.5 - CY
    if math.hypot(dx, dz) > coast_r(math.atan2(dz, dx)):
        return '.'
    du, dw = dx - SX, dz - SZ
    a = math.atan2(dw, du)
    u = math.hypot(du, dw) / (summit_coast(a) * wobble(a))

    # features first: each sits wholly inside one terrace band, so it
    # inherits that band's height and the step around it stays clean
    for fx, fz, rad, key in features:
        if math.hypot(dx - fx, dz - fz) <= rad:
            return key
    for outer, key in BANDS:
        if u <= outer:
            return key
    return 'e'                          # the beach


def main():
    which = (sys.argv[1] if len(sys.argv) > 1 else "temperate").lower()
    if which not in PALETTES:
        raise SystemExit(f"climate must be one of {', '.join(PALETTES)}")
    p = PALETTES[which]

    features = [feature_centre(frac, deg) + (rad, key) for frac, deg, rad, key in FEATURES]
    grid = [[region(c, r, features) for c in range(W)] for r in range(H)]

    # the granary compound on the summit yard, and a landmark tree out on
    # the first terrace
    gx, gz = int(CX + SX), int(CY + SZ)
    if grid[gz][gx] != 'Y':
        raise SystemExit(f"granary marker landed on '{grid[gz][gx]}', expected Y")
    grid[gz][gx] = 'G'
    ang = math.radians(200)
    tx, tz = int(CX + SX + math.cos(ang) * 18.0), int(CY + SZ + math.sin(ang) * 18.0)
    if grid[tz][tx] == 'A':
        grid[tz][tx] = 'O'

    rock, rock2, sand, clim = p["rock"], p["rock2"], p["sand"], p["climate"]
    base = f"rock={rock} rock2={rock2} climate={clim}"
    stone_line = f"stone=rock-{rock} sand={sand}"

    print(f"# granary_{which} - a small terraced farm island gone feral ({clim}).")
    print(f"# Regenerate: python tools/gen_granary_terraces.py {which} > shapes/granary_{which}.txt")
    print(f"# Suggested: /genisland shape=granary_{which} diameter=150 height=26 {stone_line}")
    print("#")
    print("# Stepped crop terraces around an off-centre summit, each step held by")
    print("# a rock retaining lip; a cistern on the second terrace, two thickets")
    print("# where the scrub took a field back, two devastated patches, and the")
    print("# ruined granary on the summit yard (struct pass, kind=granary).")
    print("# One of four climate variants; the others are arid, lush, cold and")
    print("# temperate, all from the same generator.")
    print()
    # Each terrace and the rock lip at its edge share ONE height, so the
    # terrace is a genuinely flat platform and the whole drop happens
    # between the lip and the next terrace down. Splitting the difference
    # (a lip halfway between two terraces) just made one long ramp: region
    # heights are smoothed over ~5 cells, so a step needs the flats wide
    # and the drops concentrated.
    print(f"region Y {base} fertility=terrapreta surface=grass wildgrass=0.30 scatter={p['flowers']} stones=0.03 height=1.00 shore=14 rough=0.05")
    print(f"region A {base} fertility=terrapreta surface=grass wildgrass=0.22 flax=0.03 scatter={p['cropA']},{p['flowers']} height=0.84 shore=14 rough=0.04")
    print(f"region p {base} surface=rock stones=0.06 boulders=0.012 height=0.84 shore=6 rough=0.08")
    print(f"region B {base} fertility=high surface=grass wildgrass=0.26 scatter={p['cropB']},{p['flowers']} bushes={p['bushes']} height=0.62 shore=14 rough=0.04")
    print(f"region q {base} surface=rock stones=0.06 boulders=0.012 height=0.62 shore=6 rough=0.08")
    print(f"region C {base} fertility=high surface=grass wildgrass=0.32 scatter={p['cropC']},{p['flowers']} bushes={p['bushes']} height=0.40 shore=14 rough=0.05")
    print(f"region s {base} surface=rock stones=0.06 boulders=0.012 height=0.40 shore=6 rough=0.08")
    # the lower apron: feral pasture and the old orchard
    print(f"region o {base} fertility=medium surface=grass forest=0.014 trees={p['orchard']} litter=0.6 sticks=0.03 wildgrass=0.42 bushes={p['bushes']} scatter={p['cropD']},{p['flowers']},{p['ferns']} height=0.18 shore=18 rough=0.09")
    print(f"region e {base} sand={sand} surface=sand shells=0.02 height=0.08 shore=30 rough=0.03 cattails=0.20")
    # thickets: the fields the scrub took back
    print(f"region T {base} fertility=high surface=grass forest=0.060 trees={p['thicket']} litter=0.9 sticks=0.06 wildgrass=0.55 bushes={p['thicketbushes']} scatter={p['ferns']},{p['flowers']} height=0.62 shore=14 rough=0.07")
    print(f"region U {base} fertility=high surface=grass forest=0.055 trees={p['thicket']} litter=0.9 sticks=0.06 wildgrass=0.55 bushes={p['thicketbushes']} scatter={p['ferns']},{p['flowers']} height=0.40 shore=14 rough=0.07")
    # devastated patches: bare crust, dead crops, growths
    print(f"region x {base} fertility=verylow surface=barren devastation=0.10 wildgrass=0.05 stones=0.05 scatter=deadcrop:0.15 height=0.40 shore=14 rough=0.11")
    print(f"region X {base} fertility=verylow surface=barren devastation=0.10 wildgrass=0.05 stones=0.05 scatter=deadcrop:0.15 height=0.84 shore=14 rough=0.11")
    # the cistern
    print(f"region w {base} fertility=high surface=grass height=0.62 shore=14 pond=3 {p['pondplants']} clay=0.5")
    print(f"tree O {p['landmark']}")
    print("struct G kind=granary size=20 seed=4")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
