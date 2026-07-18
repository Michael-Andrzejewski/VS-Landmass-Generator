"""
The Salt Teeth: blinding white chalk karst bristling with rock blades, and
the salt that makes long sea voyages survivable.

One karst island IMPALED on a ridge of white spires: the row of blade
islets runs in from the north-east, punches THROUGH the island as a line
of jagged spires taller than anything else on it, and emerges again on
the south-west side as more teeth. All around the coast, smaller shards
jut from the water at all angles, and between them lurk REEFS: shards
whose tips sit one block under the surface (flood=1), waiting for hulls.

  - All chalk (white rock, white sand-chalk beaches, shells everywhere),
    limestone blended through the underground. `deposits natural` over
    that sedimentary pair adds honest malachite copper, and can roll the
    game's own giant quartz discs (radius up to ~60, vanilla's numbers).
  - Under a barren saddle in the ridge sits a SALT DOME: rock2=halite
    blended through the saddle region's underground, the way vanilla's
    own salt domes sit in sedimentary rock.
  - Guaranteed quartz besides the natural rolls: the spine, spires and
    salt saddle carry ores=quartz:0.05, and the SALT CAVE's walls run
    at ores=quartz:0.12: white galleries glittering hard, then solid
    halite where the bore crosses the dome.
  - Dry faded-gold grassland tint (climate=dry) against the white rock.

Structure sites reserved for a later pass: salt-evaporation ruin on the
beach, a beacon cairn on the outermost tooth.

    python tools/gen_saltteeth_island.py > shapes/saltteeth_island.txt
"""
import math

W, H = 130, 130
MX, MZ = 50.0, 80.0          # main island centre (grid coords)
AXIS_DEG = -42.0             # spine/teeth axis, pointing north-east
MAIN_R = 29.0

# Teeth along the axis, BOTH directions: the ridge runs through the island.
# Most are SHORT sharp snags now; only the interior spine towers. Their
# regions use shore close to their width, so each rises as a POINT, not a
# flat-topped pillar. (distance from centre, half-len along, half-wid across, tall?)
TEETH = [
    (38.0, 6.0, 3.0, True), (55.0, 5.5, 2.7, False), (70.0, 4.5, 2.3, True), (82.0, 3.5, 1.8, False),
    (-38.0, 5.0, 2.6, False), (-50.0, 4.0, 2.0, True),
]

# Spires: the ridge punching up THROUGH the island interior. These stay the
# tallest things on the island (the "larger spine").
# (u along axis, v across, radius)
SPIRES = [(-16.0, 2.0, 2.6), (-7.0, -3.0, 2.2), (3.0, 3.5, 2.4), (18.0, -2.5, 2.2), (24.0, 3.0, 2.6)]

SALT_U, SALT_V = 10.0, -2.0  # salt-dome saddle centre in (along, across) coords
SALT_RU, SALT_RV = 8.0, 6.0
SALT_SPIRE = (6.0, -6.5, 1.9)  # a pillar of solid halite emerging from the dome


def adist(a, b):
    d = (a - b) % 360.0
    return d if d <= 180.0 else 360.0 - d


def coast_r(ang):
    t = math.radians(ang)
    return MAIN_R * (1.0
                     + 0.055 * math.sin(2 * t + 1.1)
                     + 0.040 * math.sin(3 * t + 3.6)
                     + 0.025 * math.sin(5 * t + 0.7))


AX = math.cos(math.radians(AXIS_DEG))
AZ = math.sin(math.radians(AXIS_DEG))

# Shard ring: small sharp islets scattered around the coast on the golden
# angle. Every third one is a LURKER: a reef one block under the surface.
SHARDS = []
for k in range(22):
    ang = (k * 137.508 + 9.0) % 360.0
    a = math.radians(ang)
    dist = coast_r(ang) + 5.0 + (k * 7 % 9)
    sx = MX + dist * math.cos(a)
    sz = MZ + dist * math.sin(a)
    size = 1.1 + (k * 5 % 3) * 0.55
    kind = 'U' if k % 3 == 2 else 'J'
    # Keep clear of the main tooth row (it owns the axis).
    du = (sx - MX) * AX + (sz - MZ) * AZ
    dv = -(sx - MX) * AZ + (sz - MZ) * AX
    near_tooth = any(abs(du - d) < hl + size + 2.5 and abs(dv) < hw + size + 2.5
                     for (d, hl, hw, _h) in TEETH)
    if not near_tooth:
        SHARDS.append((sx, sz, size, kind))


def region(c, r):
    dx, dz = c + 0.5 - MX, r + 0.5 - MZ
    u = dx * AX + dz * AZ          # along the axis (positive = toward the NE teeth)
    v = -dx * AZ + dz * AX         # across the axis
    rho = math.hypot(dx, dz)
    ang = math.degrees(math.atan2(dz, dx))
    rc = coast_r(ang)

    if rho <= rc:
        t = rho / rc
        # The salt spire: solid halite standing proud of the saddle.
        (qu, qv, qr) = SALT_SPIRE
        if math.hypot(u - qu, v - qv) < qr:
            return 'Q'
        # The salt saddle: a barren notch in the ridge over the halite dome.
        if ((u - SALT_U) / SALT_RU) ** 2 + ((v - SALT_V) / SALT_RV) ** 2 <= 1.0:
            return 'T'
        # Spires punching through the island interior.
        for (su, sv, sr) in SPIRES:
            if math.hypot(u - su, v - sv) < sr:
                return 'P'
        # Crag spine along the axis, through the whole island.
        if abs(v) < 3.8:
            return 'S'
        # Sheer white cliff band on the north-west arc.
        if t > 0.80 and adist(ang, -115.0) < 52.0:
            return 'C'
        if t > 0.86:
            return 'B'
        return 'K'

    # The teeth: sheer blade islets continuing the ridge into the sea, with
    # hard jagged edges (two wobble frequencies).
    for (d, hl, hw, tall) in TEETH:
        wob = 1.0 + 0.22 * math.sin(u * 1.7 + d) + 0.14 * math.sin(u * 4.3 + v * 3.1 + d * 2.0)
        if ((u - d) / hl) ** 2 + (v / (hw * wob)) ** 2 <= 1.0:
            return 'E' if tall else 'F'

    # Scattered shards and lurking reefs around the coast.
    for (sx, sz, size, kind) in SHARDS:
        ddx, ddz = c + 0.5 - sx, r + 0.5 - sz
        wob = 1.0 + 0.3 * math.sin(ddx * 2.3 + ddz * 1.9 + sx)
        if math.hypot(ddx, ddz) < size * wob:
            return kind
    return '.'


def main():
    grid = [[region(c, r) for c in range(W)] for r in range(H)]

    # Salt-cave mouth: north-west cliff face, near the waterline.
    m_ang = math.radians(-120.0)
    rho = coast_r(-120.0) * 0.88
    vx = int(MX + rho * math.cos(m_ang))
    vz = int(MZ + rho * math.sin(m_ang))
    if grid[vz][vx] in ('C', 'B', 'K'):
        grid[vz][vx] = 'V'
    else:
        raise SystemExit(f"cave marker landed on '{grid[vz][vx]}' at {vx},{vz}")

    # Aim the bore from the mouth through the salt dome's centre.
    tx = MX + SALT_U * AX - SALT_V * AZ
    tz = MZ + SALT_U * AZ + SALT_V * AX
    heading = math.degrees(math.atan2(tx - vx, -(tz - vz))) % 360.0

    print("# saltteeth_island - white chalk karst impaled on a ridge: short sharp")
    print("# teeth taper to POINTS (shore ~ blade width makes each rise a cone),")
    print("# the interior spires alone tower, a solid halite spire stands over the")
    print("# salt dome, and lurker reefs sit one block under the surface offshore.")
    print("# Regenerate: python tools/gen_saltteeth_island.py > shapes/saltteeth_island.txt")
    print("# Suggested: /genisland shape=saltteeth_island diameter=300 height=26")
    print("#")
    print("# All chalk + limestone underground (deposits natural: malachite copper")
    print("# and the game's own giant quartz discs can roll here). Region T is the")
    print("# salt saddle: barren ground over a rock2=halite dome. Spine S, spires P")
    print("# and saddle T carry guaranteed quartz seams; the salt cave (V) enters")
    print("# the NW cliff and bores through the dome, walls at quartz 0.12. Teeth")
    print("# E/F are sheer; J shards jut around the coast; U reefs lurk one block")
    print("# under the surface. climate=dry bakes a faded-gold grass tint.")
    print("# Structures reserved (later pass): salt-evaporation ruin on beach B,")
    print("# beacon cairn on the outermost tooth.")
    print()
    clim = "climate=dry"
    print(f"region K rock=chalk rock2=limestone fertility=low surface=grass ores=copper:0.010,lead:0.006,iron:0.0015 forest=0.008 trees=scotspine,dwarfbirch bushes=birch:0.006 scatter=edelweiss:0.008,westerngorse:0.008,wilddaisy:0.005,mugwort:0.004 wildgrass=0.15 stones=0.02 height=0.55 shore=10 rough=0.10 {clim}")
    print(f"region S rock=chalk rock2=limestone surface=rocksand ores=quartz:0.05,zinc:0.008,copper:0.008 boulders=0.014 stones=0.03 height=0.92 shore=6 rough=0.24 {clim}")
    print(f"region P rock=chalk rock2=limestone surface=rock ores=quartz:0.05,silver:0.006 boulders=0.010 height=1.28 shore=5 rough=0.35 {clim}")
    print(f"region Q rock=halite surface=rock height=0.95 shore=3 rough=0.18 {clim}")
    print(f"region T rock=chalk rock2=halite fertility=verylow surface=barren ores=quartz:0.05,copper:0.008 wildgrass=0 stones=0.03 boulders=0.008 height=0.65 shore=6 rough=0.14 {clim}")
    print(f"region C rock=chalk rock2=limestone surface=rock ores=copper:0.008,lead:0.005 boulders=0.010 height=0.55 shore=6 rough=0.16 {clim}")
    print(f"region B rock=chalk surface=sand shells=0.030 height=0.14 shore=14 rough=0.04 {clim}")
    print(f"region E rock=chalk surface=rock boulders=0.020 stones=0.02 height=0.62 shore=5 rough=0.40 {clim}")
    print(f"region F rock=chalk surface=rock boulders=0.015 stones=0.02 height=0.45 shore=5 rough=0.40 {clim}")
    print(f"region J rock=chalk surface=rock boulders=0.02 height=0.35 shore=3 rough=0.45 {clim}")
    print(f"region U rock=chalk surface=rock flood=1 height=0.30 shore=1 rough=0.20 {clim}")
    # The salt cave: generous white galleries with real chambers, diving
    # through the halite dome. Even against the cliff apron, scale 1.4
    # needed a raised mouth: at mouth=2 every seed's entry clipped the sea
    # (the flat-shore lesson applies to waterline cliffs too). Seed from
    # the previewer sweep; see the baked value's comment history in git.
    print(f"cave V heading={heading:.0f} dip=24 length=180 radius=2.6 squash=0.8 weave=0.55 scale=1.4 branches=3 branchdepth=2 branchlen=0.6 depth=65 mouth=5 entry=6 ores=quartz:0.12 seed=23")
    print("deposits natural")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
