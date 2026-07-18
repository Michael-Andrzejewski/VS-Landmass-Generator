"""
Isle of Many Climates: a TEST island for the per-region climate= key.

A wide, gentle oval split into 8 north-south strips, walked west to east.
Every strip has IDENTICAL terrain and flora (same height, fertility, forest,
grass); the ONLY difference is the strip's climate, so any color change on
the ground, tallgrass, or tree canopies is pure climate tint.

West to east:
  1  frozen      climate=-20:0.9   (custom syntax, coldest the parser allows)
  2  cold        climate=cold      (-2C, rain 0.55)
  3  temperate   climate=temperate (12C, rain 0.60)
  4  CONTROL     no climate key    (whatever this part of the world is)
  5  lush        climate=lush      (24C, rain 0.90)
  6  dry         climate=dry       (26C, rain 0.28)
  7  arid        climate=arid      (32C, rain 0.08)
  8  scorched    climate=40:0.0    (custom syntax, hottest the parser allows)

Climate pixels are ~30 blocks wide, so each ~62-block strip has a clean core
with a short blend at its borders. Walk the island west to east along the
middle and screenshot each strip.

NOTE: real temperature changes too, not just tint. The frozen strip may
gather snow, and the cold strips will feel cold to crops.

    python tools/gen_climate_isle.py > shapes/climate_isle.txt
"""
import math

W, H = 150, 100
CX, CY = 75.0, 50.0

STRIPS = "12345678"
RX, RZ = 72.0, 44.0     # ellipse radii, cells


def region(c, r):
    dx, dz = c + 0.5 - CX, r + 0.5 - CY
    ang = math.atan2(dz, dx)
    # A touch of harmonic wobble so the coast is not a perfect ellipse.
    w = 1.0 + 0.035 * math.sin(3 * ang + 1.2) + 0.025 * math.sin(5 * ang + 0.3)
    if (dx / (RX * w)) ** 2 + (dz / (RZ * w)) ** 2 > 1.0:
        return '.'
    idx = int((dx + RX) / (2 * RX / 8))
    return STRIPS[max(0, min(7, idx))]


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]

    print("# climate_isle - Isle of Many Climates: per-region climate= TEST island.")
    print("# Regenerate: python tools/gen_climate_isle.py > shapes/climate_isle.txt")
    print("# Suggested: /genisland shape=climate_isle diameter=500 height=12")
    print("#")
    print("# 8 identical strips, west to east: frozen (-20:0.9), cold, temperate,")
    print("# CONTROL (no climate key, natural tint), lush, dry, arid, scorched (40:0.0).")
    print("# Terrain and flora are the same everywhere; only the tint should change.")
    print()
    base = ("fertility=medium surface=grass forest=0.020 trees=englishoak,silverbirch "
            "height=0.5 shore=12 rough=0.06")
    climates = [None, "-20:0.9", "cold", "temperate", None, "lush", "dry", "arid", "40:0.0"]
    for i, ch in enumerate(STRIPS, start=1):
        clim = climates[i]
        if clim is None:
            print("# strip 4 is the CONTROL: no climate key, natural tint.")
        suffix = f" climate={clim}" if clim else ""
        print(f"region {ch} rock=granite {base}{suffix}")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
