"""
Cattail Isles: a chain of small islets 100 blocks off the starter island,
the world's first taste of open water and what lives under it.

The chain is really a row of TALL UNDERWATER SPIRES rising off the carved
sea floor. Most of them break the surface as tiny sand banks and
low-fertility dirt islets, 5 to 20 blocks long and only 3-4 blocks high,
every one hemmed with cattail beds. Each islet wears a flood=1 sand skirt,
a one-deep wadeable apron where the game's water reeds grow straight out
of the shallows, so the sand under the water is dotted with cattails too.

A few spires do NOT surface: flood=1 lurker tops one block under the
waterline (a reed or two marks them if you look), and two deeper flood=3
heads wrapped in kelp. And off the chain's seaward side, on the deep sea
floor, sits a serpent spawner from Underwater Horrors: the pretty reed
chain is the introduction to the water's danger. The marker is a `block`
line, so worlds without that mod installed just get a note, not an error.

    python tools/gen_cattail_isles.py > shapes/cattail_isles.txt
"""
import math

W, H = 150, 150
CX, CZ = 75.0, 75.0
BOW = 16.0          # how far the chain bows west (negative x)

def spine(t):
    """Chain spine: runs south to north, bowing west. Returns (x, z)."""
    z = 10.0 + t * 130.0
    x = CX - BOW * math.sin(math.pi * t)
    return x, z

def tangent(t):
    x1, z1 = spine(max(0.0, t - 0.01))
    x2, z2 = spine(min(1.0, t + 0.01))
    d = math.hypot(x2 - x1, z2 - z1)
    return (x2 - x1) / d, (z2 - z1) / d

# The islets. (t along spine, across-offset, kind, half-len, half-wid)
# Kinds: D low-fertility dirt, B barren dirt, S sand bank,
#        U lurker one under the surface, V deep kelp head.
# Surfaced islets: 9 (user asked for 6-12), lengths 5-20 blocks at 1.4
# blocks per cell. Alternating across-offsets keep it off a straight wire.
ISLETS = [
    (0.00,  1.0, 'S', 2.2, 1.6),
    (0.10, -2.0, 'D', 5.0, 3.2),
    (0.185, 4.0, 'U', 1.8, 1.4),
    (0.27,  2.5, 'S', 3.0, 2.0),
    (0.37, -2.5, 'B', 4.2, 2.8),
    (0.455, -5.0, 'V', 2.0, 1.6),
    (0.55,  1.0, 'D', 7.0, 4.2),
    (0.55,  9.0, 'U', 1.6, 1.3),
    (0.645, 5.0, 'U', 1.6, 1.3),
    (0.73, -2.0, 'S', 2.6, 1.8),
    (0.815, 2.0, 'D', 4.6, 3.0),
    (0.885, -4.0, 'V', 2.2, 1.7),
    (0.95,  1.5, 'B', 3.4, 2.2),
    (1.00, -1.0, 'S', 2.0, 1.5),
]

SKIRT = 1.6   # flood=1 sand apron width in cells around every surfaced islet

def islet_geom():
    out = []
    for (t, off, kind, hl, hw) in ISLETS:
        sx, sz = spine(t)
        tx, tz = tangent(t)
        nx, nz = -tz, tx          # across-spine normal
        out.append((sx + nx * off, sz + nz * off, tx, tz, kind, hl, hw))
    return out

GEOM = islet_geom()

def cell(c, r):
    x, z = c + 0.5, r + 0.5
    best, skirt = None, False
    for (ix, iz, tx, tz, kind, hl, hw) in GEOM:
        dx, dz = x - ix, z - iz
        u = dx * tx + dz * tz
        v = -dx * tz + dz * tx
        wob = 1.0 + 0.18 * math.sin(u * 1.9 + v * 1.3 + ix * 2.7)
        e = math.sqrt((u / (hl * wob)) ** 2 + (v / (hw * wob)) ** 2)
        if e <= 1.0:
            return kind
        # Absolute-width skirt: how many cells past the outline this is.
        if kind in 'DBS' and (e - 1.0) * ((hl + hw) / 2.0) <= SKIRT:
            skirt = True
    return 'F' if skirt else '.'

def main():
    grid = [[cell(c, r) for c in range(W)] for r in range(H)]

    # Serpent spawner on the deep sea floor, seaward (east) of the chain's
    # middle, well clear of every skirt.
    sx, sz = spine(0.5)
    mx, mz = int(sx + 24.0), int(sz)
    if grid[mz][mx] != '.':
        raise SystemExit(f"spawner marker landed on '{grid[mz][mx]}' at {mx},{mz}")
    grid[mz][mx] = 'X'

    print("# cattail_isles - a chain of tall underwater spires off the starter")
    print("# island; nine surface as tiny sand banks and low-fertility dirt islets")
    print("# (5-20 blocks long, 3-4 high), all hemmed in cattail beds. flood=1")
    print("# sand skirts put the game's water reeds IN the one-deep shallows, so")
    print("# the underwater sand is dotted too. Three lurker tops sit one block")
    print("# under the surface and two deep kelp heads never surface at all.")
    print("# The X block marker rests a serpent spawner (Underwater Horrors) on")
    print("# the deep sea floor east of the chain: this pretty place bites.")
    print("# Regenerate: python tools/gen_cattail_isles.py > shapes/cattail_isles.txt")
    print("# Suggested: /genisland shape=cattail_isles diameter=210 height=4 water=34")
    print()
    print("region D rock=granite fertility=low surface=grass cattails=0.20 wildgrass=0.25 scatter=wilddaisy:0.006 stones=0.02 shells=0.02 height=1.0 shore=3 rough=0.10")
    print("region B rock=granite fertility=verylow surface=barren cattails=0.18 wildgrass=0 stones=0.03 height=0.85 shore=3 rough=0.14")
    print("region S rock=granite surface=sand cattails=0.16 shells=0.04 height=0.55 shore=4 rough=0.06")
    print("region F rock=granite surface=sand flood=1 cattails=0.10 shells=0.02 height=0.10 shore=2 rough=0.05")
    print("region U rock=granite surface=sand flood=1 cattails=0.08 shells=0.02 height=0.30 shore=1 rough=0.15")
    print("region V rock=granite surface=sand flood=3 kelp=0.35 height=0.20 shore=1 rough=0.10")
    # Lifted 15 off the floor: the spawner triggers on players in water
    # within 40 blocks in 3D, so on the deep floor (~30 down) a surface
    # swimmer never armed it. At ~15 deep its reach covers the surface.
    print("block X underwaterhorrors:serpentspawner 15")
    print()
    print("map")
    for row in grid:
        print("".join(row))

if __name__ == "__main__":
    main()
