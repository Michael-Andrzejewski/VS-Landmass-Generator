"""
Builds viewer/blockcolors.json: an average RGB color for every block code
that appears in any .lmd block dump, sampled from the real game textures,
so the previewer's exact-dump mode paints blocks in true colors.

For each palette code the script scores every PNG under the game's block
texture folders by how many code tokens appear in its path, and averages
the winner's pixels. Hand overrides cover liquids, glow blocks and clutter
shape types, where a texture lookup would mislead.

    python tools/gen_blockcolors.py

Run it after new dumps exist (tools/dumpgen.mjs does this automatically).
"""
import gzip
import json
import os
import re
import struct
import sys
from pathlib import Path

from PIL import Image

REPO = Path(__file__).resolve().parent.parent
DUMPS = REPO / "export" / "data" / "LandmassGenerator" / "dumps"
OUT = REPO / "viewer" / "blockcolors.json"
ASSETS = Path(os.environ["APPDATA"]) / "Vintagestory" / "assets"
TEXROOTS = [
    ASSETS / "survival" / "textures" / "block",
    ASSETS / "game" / "textures" / "block",
]

# (regex on the domainless code) -> fixed [r, g, b] or [r, g, b, a].
# Checked before any texture lookup.
OVERRIDES = [
    (r"^air$", [0, 0, 0, 0]),
    (r"^(salt)?water", [30, 80, 140, 110]),
    (r"^lakeice", [170, 205, 230, 200]),
    (r"^glacierice", [160, 200, 230]),
    (r"^lava", [255, 120, 20]),
    (r"^ghostlight-green", [80, 255, 150]),
    (r"^ghostlight-blue", [90, 170, 255]),
    (r"^glass", [200, 220, 230, 90]),
    (r"^ember", [200, 90, 30]),
    # Clutter shape types: colored by what the shape IS, not the block code.
    (r"\|.*(junkpipe|pipelong|pipe)", [122, 74, 52]),
    (r"\|.*(junkbeam|beam)", [92, 88, 86]),
    (r"\|.*(junkchain|chain)", [104, 96, 90]),
    (r"\|.*(junktank|tank|boiler)", [110, 82, 60]),
    (r"\|.*(junksheet|sheet)", [120, 100, 84]),
    (r"\|.*(gearhugemetal|gear|mech|machine)", [98, 90, 80]),
    (r"\|.*(valve|gauge)", [140, 110, 70]),
    (r"\|", [115, 100, 88]),  # any other clutter shape: scrap brown
    (r"^spiderweb", [220, 220, 220, 120]),
    (r"^glowworms", [180, 255, 170]),
    (r"^soil-.*-normal", [86, 118, 50]),      # grass top
    (r"^soil-.*-sparse", [104, 112, 58]),
    (r"^soil", [96, 72, 50]),
    (r"^bonysoil", [140, 128, 100]),
    (r"^flotsam", [110, 92, 66]),
    (r"^drycarcass", [150, 135, 110]),
    (r"^clutteredbookshelf$", [96, 70, 45]),
    (r"^groundstorage$|^microblock$|^multiblock", [120, 120, 120, 60]),
    (r"^loosegears", [130, 105, 60]),
    (r"^lootvessel", [130, 90, 60]),
    (r"^metalpartpile", [95, 85, 75]),
    (r"^flower-", [170, 160, 90]),
    (r"^devgrowth", [96, 62, 88]),
    (r"^metalblock-.*corroded", [96, 78, 62]),
    (r"^metalblock-new", [152, 156, 162]),   # bright steel plate
    (r"^metalblock", [108, 112, 120]),
    (r"quartz_nativegold", [196, 156, 62]),   # gold trim should READ as gold
    (r"^ironfence|^metalfence", [70, 68, 66]),
    (r"^devastatedsoil", [78, 60, 74]),
    (r"^coalpile", [40, 38, 36]),
    (r"^stationarybasket", [150, 118, 62]),
    (r"^aquaticplant|^seaweed", [46, 110, 70]),
    (r"^forestfloor", [72, 58, 40]),
    (r"^loosestick", [112, 88, 58]),
    (r"^stonepath", [128, 124, 118]),
]

WORD = re.compile(r"[a-z]+")


def load_palettes():
    codes = set()
    if not DUMPS.is_dir():
        sys.exit(f"no dumps folder at {DUMPS}; run tools/dumpgen.mjs first")
    for f in DUMPS.glob("*.lmd"):
        d = gzip.open(f, "rb").read(1 << 22)
        if d[:4] != b"LMD1":
            print(f"skipping {f.name}: bad magic")
            continue
        hlen = struct.unpack("<i", d[4:8])[0]
        codes.update(json.loads(d[8:8 + hlen])["palette"])
    return sorted(codes)


def texture_index():
    idx = []
    for root in TEXROOTS:
        if not root.is_dir():
            continue
        for p in root.rglob("*.png"):
            rel = p.relative_to(root).as_posix().lower()
            idx.append((set(WORD.findall(rel)), rel, p))
    return idx


def avg_color(path):
    img = Image.open(path).convert("RGBA")
    img = img.resize((16, 16))  # cheap and plenty for an average
    px = list(img.getdata())
    solid = [(r, g, b) for r, g, b, a in px if a > 64]
    if not solid:
        return None
    n = len(solid)
    return [sum(c[0] for c in solid) // n, sum(c[1] for c in solid) // n, sum(c[2] for c in solid) // n]


def hash_color(code):
    h = 0
    for ch in code:
        h = (h * 31 + ord(ch)) & 0xFFFFFFFF
    return [90 + h % 90, 90 + (h >> 8) % 90, 90 + (h >> 16) % 90]


def main():
    codes = load_palettes()
    idx = texture_index()
    out = {}
    misses = []

    for full in codes:
        code = full.split(":", 1)[-1]
        for pat, rgb in OVERRIDES:
            if re.search(pat, code):
                out[full] = rgb
                break
        else:
            tokens = set(WORD.findall(code.lower()))
            # Grade words appear in ore codes but almost never in paths.
            tokens -= {"poor", "medium", "rich", "bountiful", "grown", "normal", "free", "very", "north", "south", "east", "west", "up", "down"}
            best, best_score = None, 0
            for words, rel, p in idx:
                score = len(tokens & words)
                if score > best_score or (score == best_score and best and score > 0 and len(rel) < len(best[1])):
                    best, best_score = (words, rel, p), score
            col = avg_color(best[2]) if best and best_score >= max(1, len(tokens) - 1) else None
            if col is None:
                col = avg_color(best[2]) if best and best_score >= 1 else None
            if col is None:
                col = hash_color(code)
                misses.append(full)
            out[full] = col

    OUT.write_text(json.dumps(out, indent=0))
    print(f"wrote {OUT} with {len(out)} colors from {len(codes)} palette codes")
    if misses:
        print(f"{len(misses)} code(s) fell back to hash colors:")
        for m in misses[:40]:
            print("   ", m)


if __name__ == "__main__":
    main()
