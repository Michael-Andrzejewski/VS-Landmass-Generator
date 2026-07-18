"""
The Salt Teeth: blinding white chalk karst, and the salt that makes long
sea voyages survivable.

One main karst island with a crag spine running north-east; the spine
CONTINUES into the sea as a row of four sheer white blade islets (the
drowned ridge), each shorter than the last. The channels between the
teeth are just wide enough for a raft, with monster water on both sides.

  - All chalk (white rock, white sand-chalk beaches, shells everywhere),
    limestone blended through the underground. `deposits natural` over
    that sedimentary pair adds honest malachite copper and whatever else
    the local ore maps carry.
  - Under a barren saddle in the ridge sits a SALT DOME: rock2=halite
    blended through the saddle region's underground, the way vanilla's
    own salt domes sit in sedimentary rock. Salt is preservation, and
    preservation is how you keep food on a raft.
  - The SALT CAVE (V) enters at the north-west cliff face near the
    waterline and bores straight through the dome: white galleries
    glittering with quartz, then walls of solid halite.
  - Dry faded-gold grassland tint (climate=dry) against the white rock;
    sparse pines, gorse, edelweiss on the chalk upland.

Structure sites reserved for a later pass: salt-evaporation ruin on the
beach, a beacon cairn on the outermost tooth.

    python tools/gen_saltteeth_island.py > shapes/saltteeth_island.txt
"""
import math

W, H = 130, 130
MX, MZ = 50.0, 80.0          # main island centre (grid coords)
AXIS_DEG = -42.0             # spine/teeth axis, pointing north-east
MAIN_R = 29.0

# Teeth along the axis: (distance from main centre, half-len along, half-wid across)
TEETH = [(38.0, 6.0, 3.0), (55.0, 5.5, 2.7), (70.0, 4.5, 2.3), (82.0, 3.5, 1.8)]

SALT_U, SALT_V = 10.0, -2.0  # salt-dome saddle centre in (along, across) coords
SALT_RU, SALT_RV = 8.0, 6.0


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


def region(c, r):
    dx, dz = c + 0.5 - MX, r + 0.5 - MZ
    u = dx * AX + dz * AZ          # along the axis (positive = toward the teeth)
    v = -dx * AZ + dz * AX         # across the axis
    rho = math.hypot(dx, dz)
    ang = math.degrees(math.atan2(dz, dx))
    rc = coast_r(ang)

    if rho <= rc:
        t = rho / rc
        # The salt saddle: a barren notch in the ridge over the halite dome.
        if ((u - SALT_U) / SALT_RU) ** 2 + ((v - SALT_V) / SALT_RV) ** 2 <= 1.0:
            return 'T'
        # Crag spine along the axis, through the whole island.
        if abs(v) < 3.8:
            return 'S'
        # Sheer white cliff band on the north-west arc.
        if t > 0.80 and adist(ang, -115.0) < 52.0:
            return 'C'
        if t > 0.86:
            return 'B'
        return 'K'

    # The teeth: blade islets continuing the spine into the sea.
    for (d, hl, hw) in TEETH:
        wob = 1.0 + 0.15 * math.sin(u * 1.7 + d)
        if ((u - d) / hl) ** 2 + (v / (hw * wob)) ** 2 <= 1.0:
            return 'E'
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

    print("# saltteeth_island - white chalk karst + a row of sheer blade islets, the")
    print("# drowned ridge. Salt dome under the saddle; the salt cave bores through it.")
    print("# Regenerate: python tools/gen_saltteeth_island.py > shapes/saltteeth_island.txt")
    print("# Suggested: /genisland shape=saltteeth_island diameter=300 height=26")
    print("#")
    print("# All chalk + limestone underground (deposits natural reads the real ore")
    print("# maps: malachite copper country). Region T is the salt saddle: barren")
    print("# ground over a rock2=halite dome, vanilla-style. The cave (V) enters the")
    print("# NW cliff near the waterline and runs through the dome: quartz-flecked")
    print("# white galleries, then solid salt. Teeth E are sheer; shelter the raft in")
    print("# the channels between them. climate=dry bakes a faded-gold grass tint.")
    print("# Structures reserved (later pass): salt-evaporation ruin on beach B,")
    print("# beacon cairn on the outermost tooth.")
    print()
    clim = "climate=dry"
    print(f"region K rock=chalk rock2=limestone fertility=low surface=grass forest=0.008 trees=scotspine,dwarfbirch bushes=birch:0.006 scatter=edelweiss:0.008,westerngorse:0.008,wilddaisy:0.005,mugwort:0.004 wildgrass=0.15 stones=0.02 height=0.55 shore=10 rough=0.10 {clim}")
    print(f"region S rock=chalk rock2=limestone surface=rocksand boulders=0.014 stones=0.03 height=0.92 shore=5 rough=0.24 {clim}")
    print(f"region T rock=chalk rock2=halite fertility=verylow surface=barren wildgrass=0 stones=0.03 boulders=0.008 height=0.65 shore=6 rough=0.14 {clim}")
    print(f"region C rock=chalk rock2=limestone surface=rock boulders=0.010 height=0.55 shore=3 rough=0.16 {clim}")
    print(f"region B rock=chalk surface=sand shells=0.030 cattails=0.10 height=0.14 shore=14 rough=0.04 {clim}")
    print(f"region E rock=chalk surface=rock boulders=0.020 stones=0.02 height=1.0 shore=2 rough=0.30 {clim}")
    # The salt cave: generous white galleries with real chambers, diving
    # through the halite dome. Quartz flecks the chalk walls (ore-quartz-chalk
    # is a valid vanilla combo). Even against the cliff apron, scale 1.4
    # needed mouth=5: at mouth=2 every seed's entry clipped the sea (the
    # flat-shore lesson applies to waterline cliffs too). The door partway
    # up the white cliff face looks right anyway. seed=23 from the sweep:
    # 946 steps, ZERO wet, bottom galleries 71 below sea.
    print(f"cave V heading={heading:.0f} dip=24 length=180 radius=2.6 squash=0.8 weave=0.55 scale=1.4 branches=3 branchdepth=2 branchlen=0.6 depth=65 mouth=5 entry=6 ores=quartz:0.04 seed=23")
    print("deposits natural")
    print()
    print("map")
    for row in grid:
        print("".join(row))


if __name__ == "__main__":
    main()
