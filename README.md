# VS Landmass Generator

A Vintage Story code mod that generates whole procedural landmasses from a single chat command. It began as an experiment inside [Building Commands](https://github.com/Michael-Andrzejewski/VS-Building-Commands) and was split out so it can stabilize on its own.

## `/genisland [key=value ...]`

Generates a procedural ocean island centred on where you stand. Because a landmass is far more blocks than a normal fill may touch at once, it does not run in one burst. It force-loads the island's chunks and then builds column by column across game ticks, committing one batch per tick, so even a 500-block island never freezes the server.

The shape is a radial dome with a simplex-noise coastline. The height falloff is biased by compass direction, so one side eases down into a sandy beach while the opposite side drops as a stone cliff. The island is rooted on the **real sea floor** (probed per column), its flank slopes down to meet it, and it blends back into the natural seabed at the work boundary, so nothing floats and there is no visible rim.

Requires the `controlserver` privilege (you have it in single player). Only one island generates at a time.

### Options

Options are `key=value` tokens in any order. A lone leading number is read as `diameter`.

| Key | Default | Meaning |
| --- | --- | --- |
| `diameter` | 120 | Island width in blocks. |
| `height` | 40 | Peak height above the water line at the centre. |
| `water` | 30 | How far the sea floor is carved down just outside the coast. |
| `sealevel` | world's | The water line. Water fills up to one block below it, matching the surrounding ocean. |
| `oceanwater` | `saltwater-still-7` | The block the ocean ring is filled with. Salt water matches the sea's tint; set to `water-still-7` if you are placing the island in a freshwater lake. |
| `maxdepth` | 80 | Safety cap on how far below the water line a column will fill. |
| `beachdir` | `s` | Compass side (`n e s w`) that eases into a sand beach. |
| `cliffdir` | `n` | Compass side that drops as a stone cliff. |
| `seed` | random | Fixes the shape so a run is repeatable. |
| `rotate` | 0 | Degrees to spin a drawn (`shape=`) island clockwise on the map, so its harbour or beach can face a neighbouring island. `rotate=90`: what pointed north now points east. |
| `climate` | none | Rewrites the worldgen climate over the island, which is what tints grass and leaves: `arid` fades them rusty desert-yellow, `lush` deepens them tropical green. Also `dry`, `temperate`, `cold`, or `<tempC>:<rain 0..1>` (e.g. `climate=32:0.1`). Fades back to the natural climate outside the island, persists with the world, and also affects real temperature there (crops, snow). The game caches plant tint client-side, so the mod's client half refreshes it live; a client WITHOUT the mod installed sees the new tint after reconnecting. |
| `ores` | none | Ore veins to seed. See below. |
| `forest` | 0 | Chance per land column of a tree, e.g. `0.02`. 0 means bare. |
| `trees` | `oak` | Comma-separated tree types for the forest. |
| `stone` | `rock-granite` | Core block. Ore must occur in this rock. |
| `soil` | `soil-medium-none` | Sub-surface dirt. |
| `grass` | `soil-medium-normal` | Grass-topped surface. |
| `sand` | `sand-granite` | Beach block. |

### Ore

Vintage Story's ore pass only runs during natural worldgen, so blocks this mod places contain **no ore at all** unless you ask for it. That means the island's geology is entirely yours to choose.

    ores=copper:rich,iron:medium,tin:sparse

Each ore gets its own 3D noise field: above a threshold is a vein, and the deeper into the vein a block sits, the richer the grade (poor to bountiful). Veins run through the whole core, from just under the soil down to the seabed.

Richness is a target DENSITY, the fraction of the island's stone that is ore, and the threshold is calibrated against the noise so the fraction is real: `rare` 0.2%, `sparse` 0.5%, `medium` 1%, `rich` 2%, `abundant` 3.5%. A number gives the fraction directly: `copper:0.02` means 2 ore blocks per 100 stone, about the ceiling a prospecting pick reads in rich natural terrain.

Friendly names (`copper`, `iron`, `tin`, `zinc`, `lead`, `nickel`, `chromium`, `titanium`, `tungsten`, `bismuth`, `manganese`) map to the underlying minerals, or you can name a mineral directly: `nativecopper`, `limonite`, `galena`, `cassiterite`, `chromite`, `ilmenite`, `sphalerite`, `bismuthinite`, `magnetite`, `hematite`, `malachite`, `pentlandite`, `uranium`, `wolframite`, `rhodochrosite`.

Ore blocks are rock-specific, so an ore that does not occur in your `stone` rock is reported and skipped rather than silently dropped.

### Natural ore (`deposits=natural`)

Instead of (or on top of) hand-placed veins, the island can carry the ore the game itself would have generated there. Add `deposits=natural` to the command, or the line `deposits natural` to a shape file (`deposits=off` on the command overrides the shape line).

After terrain, caves and climate are done, the mod re-runs the game's own deposit generator over the island's chunk columns: same world seed, same per-chunk randomness, same regional ore maps the prospecting pick reads. If the sea floor nearby propicks "high native copper", the island gets that same high copper, exactly where vanilla worldgen would have put it. Quartz with its gold and silver pockets, coal seams, gems, clay and gravel lenses, loose surface ore bits: everything from the game's deposit tables, at natural depths below the island's actual surface.

Two things to know:

- An ore only appears where its host rock exists. Coal seams need a sedimentary rock (claystone, sandstone, shale, chalk, limestone, chert, conglomerate), quartz lives in nearly everything, copper in most igneous and sedimentary rock. Pick `rock=`/`rock2=` per region with that in mind; the second rock blends through the underground in blobs, so `rock=granite rock2=claystone` hosts both quartz veins and coal.
- The pass also writes the island's real surface into the engine's per-column heightmaps (this happens for every island now, deposits or not). Prospecting readings, rain and snow previously still believed the surface was the old sea floor; now they see the island.

### Forest

    forest=0.02 trees=oak,pine,birch

After the terrain lands, a second pass walks the grass and rolls `forest` per column, growing a random tree from your list using the game's own tree generators (so they look native). Trees are only placed on grass, never on the beach or the cliff faces. A single tall oak is always planted at the summit as a landmark.

### Examples

    /genisland diameter=120
    /genisland diameter=200 height=45 beachdir=s cliffdir=n seed=1234
    /genisland diameter=300 height=60 ores=copper:rich,iron:medium forest=0.02 trees=oak,pine

Run it from open ocean for the cleanest result. On existing land it clears the terrain above the new island.

## Drawn islands (`shape=`)

Instead of a radial dome you can build a specific island you drew.

    /genisland shape=ideal_island diameter=400
    /genisland shape=ideal_house_island diameter=600 height=18
    /genisland shapes                      # list installed shapes

Shape files live in `%APPDATA%\VintagestoryData\LandmassGenerator\` (the mod prints the path on load). A shape is an ASCII map plus a legend of regions, and each region gets its own rock, surface, ore, forest and shore steepness:

    region F rock=granite surface=grass    ores=iron:medium   forest=0.055 trees=oak,pine height=1.0  shore=9  rough=0.35
    region P rock=granite surface=grass    ores=copper:rich   forest=0.004 trees=oak      height=0.78 shore=9  rough=0.22
    region B rock=granite surface=sand     ores=              forest=0     height=0.10 shore=34 rough=0.08
    region R rock=slate   surface=rocksand ores=copper:medium forest=0     height=0.66 shore=4  rough=0.7
    tree O oak 2.2
    deposits natural

    map
    ..........FFFFFFFF..........
    .......PPPPPPPPPPPPPBBBB....
    ...

`.` is ocean, every other character is a region. The standalone line `deposits natural` turns on the vanilla ore pass for the whole island (see "Natural ore" above). Region keys:

| Key | Meaning |
| --- | --- |
| `rock` | Rock type for this area. Its stone, sand, and ores follow it (`granite`, `slate`, `basalt`, ...). |
| `rock2` | A second rock, noise-blended into the first underground (e.g. `rock=slate rock2=peridotite`). |
| `sand` | Explicit beach block, so a slate island can still have a white `sand-chalk` beach. |
| `fertility` | Soil richness for grass areas: `verylow`, `low`, `medium`, `high`, `terrapreta`. |
| `surface` | `grass`, `sand`, `rock`, `rocksand` (rocky outcrops speckled with sand), `barren` (worn ground: soil with only patchy sparse grass), or `peat` (a bog floor: real minable peat a few blocks deep, part-covered in sparse grass). |
| `sandy` | Wind-blown sand drifts: contiguous noise blobs of the region's sand across grass or barren ground, e.g. `sandy=0.15`. |
| `pumpkins` | Chance per column of a wild pumpkin patch centre: a mother plant surrounded by real pumpkin vines in mixed stages (adopted by the mother so they survive), fruits beside them, rusty debris between them. Typical 0.01-0.03 on a small patch region. |
| `height` | Fraction of the island's peak `height` this area rises to. |
| `shore` | Blocks from the coast to full height. Small is a sheer cliff, large is a gentle beach. |
| `rough` | Surface roughness, for outcrops and broken ground. |
| `pond` | Makes the region a pond this many blocks deep: a level water bowl contained by a flat grass rim. Two touching pond regions with the same `height` share one water level, so a snaking pond region is a river. A pond region with `forest=` grows its trees out of the knee-deep rim water (swamp cypress in the mere). |
| `pondslope` | How far in from the shore a pond's bed takes to reach full depth, in blocks. Default 4, which suits a farm pond. A mere tens of blocks across needs tens of blocks of slope or its floor is a flat box with a lip: `pond=30 pondslope=16` gives a basin that drops steadily from the reeds to 30 deep in the middle. The bed may sink below sea level into the island's own rock, stopping short of the bottom of the fill. |
| `flood` | The region's ground sinks up to this many blocks (1-3) BELOW sea level and the sea flows over it: marsh flats, mangrove shallows, lurking reefs. The depth ramps smoothly from zero at the nearest dry land, so the meadow descends into the water without a step. Too shallow to raft, wadeable on foot. The region's trees rise straight out of the water, and its `cattails` grow IN the water where it is exactly one deep (the game's water reed). |
| `kelp` | On a flood region: chance per water column of a seaweed stalk rooted on the flooded bed, up to 3 tall. This is the plant that belongs in SALT shallows (cattails are for fresh water). |
| `cattails` | On a pond region: chance per rim column of a cattail, so reeds ring the water. On a shore region: chance per column within 3 blocks of the sea, gathered into clumped reed beds (20-40 block patches, dense inside, bare between) rather than an even hem. The beds also run out INTO the sea: wherever the water just off that coast is exactly one block deep, the game's water reed stands in it, same clumps, so a bed crosses the waterline instead of stopping at it. |
| `flax` | Chance per grass column of a wild flax plant (mixed maturity). |
| `orebits` | Surface ore clusters, e.g. `orebits=tin:0.002,copper:0.001`: the chance per grass or rock column that a cluster starts there, reproducing the game's own surface deposits: a shallow poor/medium ore disc in the stone under the soil, with loose ore bits over about a third of it. Digging under any bit finds the ore. Friendly ore names resolve like `ores=`. Typical values 0.001-0.003 (each cluster is ~10-20 bits). `copperbits=x` is a legacy alias for `orebits=copper:x`. |
| `devastation` | Chance per column that a devastated-ground patch starts there: a ragged disc of unmineable devastated soil, heaviest crust at the centre, drock pushed into the ground, devastation growths sprouting from it. |
| `bushes` | Bush kinds and chances, e.g. `bushes=raspberry:0.01,birch:0.006`. Any wild fruiting bush type works (`raspberry`, `cranberry`, `blueberry`, `blackcurrant`, ...); `birch` grows a dwarf birch shrub. |
| `wildgrass` | Chance per grass column of a tallgrass tuft. Defaults to 0.35 everywhere; set 0 to turn off. |
| `stones` | Chance per column of a loose stone. Defaults to 0.012 everywhere; the stone matches the rock actually under that column (both region rocks, picked by the underground blend). Set 0 to turn off. |
| `sticks` | Chance per grass column of a fallen stick (forest floors). |
| `litter` | Density of leaf litter stamped in a disc under each tree, leafy at the trunk and grading back to grass at the canopy edge. |
| `scatter` | Generic decor, e.g. `scatter=cornflower:0.01,fieldmushroom:0.006,eaglefern:0.02`. Friendly names resolve through the flower, mushroom, fern and herb block families; full block codes also work. |
| `lilies` | On a pond region: chance of a waterlily per water column. |
| `shells` | Chance per sand column of a seashell (random type and color). |
| `boulders` | Chance per rock-surface column of a loose boulder in the region's rock. Boulders never appear on grass. |
| `clay` | On a pond region: chance a rim column becomes a blue clay deposit. On a normal region: chance per column that the soil becomes clay down to the rock, capped with sparse-grass clay so the deposit hides in the meadow. |
| `climate` | This region's own plant-tint climate, same values as the command's `climate=` option. Lets one island carry several tints (a lush valley under arid crags). Regions without one keep the command's island-wide climate, or the natural climate if none was given. Climate pixels are ~30 blocks wide, so bands narrower than that blur together. |
| `ores`, `forest`, `trees` | As above, but per region. `ores=` also accepts the ungraded minerals (`coal`, `quartz`, `sulfur`, `olivine`, `lapislazuli`, ...): they place their single ungraded block at every richness roll. |
| `tree <char> <type> <size>` | A landmark tree at that spot on the map. |
| `cave <char> [key=value ...]` | A cave system entering at that spot on the map. See below. |
| `block <char> <blockcode> [lift] [beside=<char> side=<east\|west\|north\|south>]` | One block placed resting on the actual ground at that spot (with `beside=`, not drawn on the map: right against that landmark tree's trunk on the given side, e.g. `block Y game:statictranslocator-broken-north beside=O side=west`), on the sea floor when the spot is underwater. Meant for spawners and props from other mods (e.g. `block X underwaterhorrors:serpentspawner 15`), so the code is resolved at placement time: a block the game does not know is reported as a note, never an error. The optional lift raises it that many blocks off the ground (underwater it never rises past 3 below the surface), for spawners whose trigger range must reach swimmers at the surface. Unlike tree and cave markers, a block marker may stand in open ocean; it only joins a region if one directly touches it. |

### Caves (`cave <char> ...`)

A `cave` line declares a hand-placed cave system; every map cell holding its
char becomes an entrance. Carving works the way the game's own cave generator
does: a tunnel is a one-block-per-step walk whose direction drifts with
momentum-smoothed noise, hollowing a tapered ellipsoid at each step, and
random widening impulses occasionally blow the tunnel out into large
chambers, bigger the deeper the tunnel is (vanilla's big underground rooms,
reproduced). Two of vanilla's safety rules are kept: a step that would touch
any water is skipped whole (a cave beside the ocean can never breach it),
and the tunnel always stays at least 5 blocks below the surface, which both
prevents skylights and keeps every cave ceiling in ROCK, below the 3-block
soil skin. The mouth is the exception: the generator stamps a small HEADWALL there, a
solid outcrop of the local rock a couple of blocks taller than the tunnel
(replacing any soil in its footprint), and bores the adit into it. The
entrance is therefore always a horizontal doorway in a rock face with a
stone ceiling, even where the natural ground rises too gently to offer a
wall.

    cave M heading=auto dip=13 length=85 radius=2.7 weave=0.45 scale=0.8 branches=4 branchdepth=2 branchlen=0.7 depth=30 mouth=2 entry=12 ores=copper:0.06 seed=30

| Key | Default | Meaning |
| --- | --- | --- |
| `heading` | `auto` | Initial direction in MAP degrees (0 = up on the map, 90 = right), so the design survives `rotate=`. `auto` aims at the island's centre. |
| `dip` | 12 | Descent angle in degrees while the tunnel is diving. |
| `length` | 80 | Main tunnel length in blocks. Branches are fractions of it. |
| `radius` | 2.6 | Horizontal carve radius. The tunnel tapers toward both ends and swells and narrows along the way. |
| `squash` | 0.72 | Vertical radius as a fraction of `radius` (flatter than wide, like real tunnels). |
| `weave` | 0.5 | How much the tunnel wanders, 0 dead straight to 1 very windy. |
| `scale` | 1 | Overall size multiplier (0.5 to 4): scales the tunnel radius AND the chamber events. `scale=2` makes a grand cavern system out of the same layout. |
| `branches` | 2 | Side tunnels forked off the main run. |
| `branchdepth` | 2 | How many levels deep branches may branch again. |
| `branchlen` | 0.5 | Branch length as a fraction of the parent tunnel (each branch varies about 30% around it). Raise toward 1 for long wandering galleries, with a small `scale` for a cramped spidery mine. |
| `branchradius` | 0.85 | Branch carve radius as a fraction of the parent's, applied per branch level (0.3 to 1.2). At 0.45 a vast main bore forks into half-width side passages whose own branches run a quarter width: long narrow twisting galleries off one grand artery. Chamber events still scale with `scale`, so narrow passages keep opening into the occasional large cavern. |
| `pinch` | 0 | Periodic squeezes along every tunnel (0 to 0.8): the passage necks down by this fraction and opens out again roughly every 85 blocks, so wide halls connect through narrow throats. Squeezes scale the tunnel body but not chamber events, and they are RNG-free, so adding pinch never changes a saved seed's path. |
| `depth` | 60 | The tunnel levels out this many blocks below its mouth. A hard floor since 0.44.1: no step may sink more than 2 blocks past it (high `weave` used to let branches drill 40+ blocks deeper). |
| `mouth` | 2 | Mouth floor height above sea level. |
| `entry` | 10 | Blocks of dead-level adit before the dive starts, so the entrance is a horizontal doorway in the hill face, never a hole in the floor. |
| `ores` | none | Wall lining, e.g. `ores=copper:0.06`: every stone block exposed in the tunnel wall has that chance to become ore (poor/medium/rich mix), matched to whatever rock the wall actually is. |
| `seed` | stable | The cave's path is deterministic per design, so it survives regeneration and matches the previewer. Set `seed=` to reroll a layout you dislike. |

Height comes from a distance-to-coast field, so interiors rise and shores taper naturally, and the coastline is jittered with noise so the grid never shows as stair-steps. The worked examples ship as scripts in `tools/`, each generated from a hand-drawn map: `ideal_island` (a varied island with beach, plains, forest and a rocky slate arm), `starter_island` (a smooth, idyllic player-start island, ~150 across, with a big west beach, sparse oak forest, a meadow pond, and one slate headland), `tin_island` (a tall rocky crescent bent around a sheltered harbor, rusty-arid, heavily devastated, tin through the core), and `forester_island` (a rounded island with a west bay, regions still placeholder):

    /genisland shape=starter_island diameter=150 height=8 stone=rock-peridotite sand=sand-peridotite
    /genisland shape=tin_island diameter=220 height=14 stone=rock-basalt sand=sand-basalt climate=arid

### Megastructures (0.61.0, `src/MegaStructs.cs`)

Nine structure kinds share one toolkit (voxel lines, tubes with flanges,
gears in any plane, hyperboloid shells, swept balls, and an `Air()` that
leaves sea water where the sea would be). They take their own keys on the
`struct` line; anything not listed is an error for the older kinds only.

| kind | what | keys |
| --- | --- | --- |
| `pylons` | A line of lattice power pylons: open zigzag bracing, two crossarm levels, chain insulators, sagging cables between neighbours; some lean hard, some snapped with the top lying beside them, all sunk into the ground or sea floor. | `count`, `spacing`, `heading` (map degrees, turns with `rotate=`), `height`, `above=N` (grow so the arms clear the sea by N), `reach=` (map cells kept loaded, for lines over open sea) |
| `refinery` | Concrete pads, a four-line pipe rack on trestles (a fallen section hanging), two colossal penstocks sloping to the shore, storage tanks (one torn open), distillation columns with ring platforms (one toppled), scaffolding towers, a gantry crane with its hook over a flooded sump, the control room with two chests. | `size` (half-width) |
| `graveyard` | The Eerie Island, the Burying Isle (`EerieIsland.cs`, lore in `lore/eerie_*.txt`): black obsidian monoliths with red ochre X marks (decor) far apart over the downs, each over a mass grave (an earth pit shored with timber: coffins along its walls, the dead in cloth, skeletons and bones between, chests of their belongings), and a great monolith in an obsidian circle over the Great Pit; catacombs on two levels under the island's heart (galleries lined with burial niches, a rotunda with an oculus down to the bone heap of the pit floor, ossuaries walled in skull cobble, chapels, sarcophagus halls, the wardens' rooms, sealed vaults, the last warden's cell), joined by a spiral stair, entered by the Warden's Gate stair from the downs, with bone chutes falling in from the pits above; buried family vaults, charnel trenches, ossuary wells, a buried chapel, the grave robbers' tunnels from pit to pit with their camp; hundreds of single graves in rows near the markers (a coffin, a chest, a skeleton or an open grave under each), walled family plots, weathered pillars and mausoleums with crypts. Six lore books (the decree, the marking of the pits, the barge ledger, a worker's letter, a robber's note, the last warden's entry) lie in its chests. | `size`, `pillars`, `monoliths`, `graves`, `plots`, `mausoleums`, `great`, `ring`, `catacombs` (node reach each way, 0 = none), `pits` (0 = no mass graves), `vaults`, `trenches`, `wells`, `chapels`, `robbers` |
| `powerplant` | Three hyperboloid cooling towers (one broken off, its shell heaped round it), a brutalist reactor hall with a finned facade and a burst dome, a transformer yard; under the hall a ramp down 30 blocks to a pillared chamber with the warp rig (an upright lit ring, drock floor) and coolant tunnels out under each tower. | `size` |
| `greatmachine` | The Great Machine (rebuilt in 0.63.0, `GreatMachine.cs`): on the `great_machine` island's impact crater, a scorched crater surface; an abyss cut into the crater floor 70 below the sea (dry, scree walls, lava pools); six chasms from the abyss out to the sea (dry and deep inland, a rubble sill near the coast, sea water only in the last stretch), each with a branch crack, and thin fractures across the plateau; a 30-block steel sphere hanging in the crater just under the plateau's level, on chains from five floating islands (stalactites under them, a stalagmite rising to each, a loot chest on one), propped by three stalagmites out of the abyss; inside it a double iron door, a deck, the control area round the Spreading Devastation core, machinery under the deck, and a rupture torn outward at the top right as seen from the walkway; a double helix from the sphere's top to a point with the beacon; two crystal blades shaped like a Halo energy sword's either side of the helix, twice its height, the sphere at the hilt; a walkway with rusted iron railings on a truss and stalagmite piers from the crater wall to the doors, and a stone stair from its landing to the rim; an 18-sided ring of ruined walls round the crater (five sides gone, five partial, blast holes, a south gate) that the chasms cut through; a red fog zone over the island. Since 0.65.0 (`GreatMachineSurface.cs`) a riveted iron collar round the crater's lip and a ring round the abyss mouth, huge fallen metal (collar sections, girders, gears, plates, pipe) and fried buildings filling the crater, and machinery and wrecked halls between the crater and the walls; and below it all (`GreatMachineWorks.cs`, `GreatMachineVaults*.cs`) ten buried works down to the mantle (the Boiler Cathedral, the Engine Gallery, the Archive, the Laboratories, the Foundry, the Axle Well, the Cistern, the Resonator, the Workers' Quarters, the Deep Bore) with wings of workrooms, labs, offices and stores, joined by walkable pipes lit by Jonas lamps and glowing seams, buried machinery packed into the rock round them, and three stairwells up to halls on the plateau. Since 0.66.0 (`GreatMachineWarrens.cs`, `GreatMachineLoot.cs`) the warrens tie it all together: a maze of tunnels on seven levels (masonry halls, riveted iron passages, timbered mine drifts, small pipes) between chambers on a 24-block grid, a random spanning tree with a third of the other links put back so it branches, dead-ends and loops, ladders and spiral stair towers between the levels, furnished rooms and locust fabrication bays; tunnels driven into every dry work and many pipes; a ladder or spiral stair down through the floor of almost every building, and standpipes (huge broken pipes with a stair inside) standing out of the crater floor; huge pipes jutting out of the chasm walls (some at the floor, to walk into) and the abyss walls; and the finds: chests (Jonas parts, temporal gears, ingots, tools), armor stands, ingot piles and Jonas devices (static translocators, rift wards, base and corpse return teleporters, resonators). Its clutter is settled onto real support when it is placed. | `rim` (crater rim radius, default 248), `abyss` (84), `wall` (300), `sphere` (radius, 15), `turns`, `fog` (fog radius, 640; 0 = none), `fogfade` (200), `works` (0 = none of the 0.65.0 ruins or works), `buried` (buried machines, 900), `entrances` (0 = no stairwells up to the plateau: the works are sealed in rock, reached only by digging). `warrens` (0 = none of the 0.66.0 maze, ways down and pipe mouths), `loot` (0 = no finds). Radii are for a 4-blocks-a-cell island and scale with it. |
| `ruin` | One Better Ruins megastructure (0.67.0, `RuinIsles.cs`) on a levelled footing: the median ground of its footprint, blended back into the slope; the trees over it felled whole; the schematic placed the way the game places it (its own y offset, filler blocks to air, the block-layer marker as grass, block entities, loot and creatures after the blocks are in). Which one: `structure=` on the struct line, else the island's `ruin=` option (a `ruins` plan line writes that). `cardinals_island` carries the cathedral this way; the `ruin_isle`, `ruin_isle_cold` and `ruin_isle_arid` shapes carry the rest. Since 0.68.0 a story structure's schematic can be named outright with `path=` and `storyoffset=` (its `schematicYOffsets` entry): `mansion_island` carries the Better Ruins mansion this way. | `structure` (a megastructures-group code, e.g. `megastructures-giantcathedral-dampus`), `schematic` (which of its schematics, by index), `rot` (0, 90, 180, 270), `blend` (8), `path` (a schematic asset, e.g. `betterruins:worldgen/schematics/story/brmansion.json`), `storyoffset` (placed as a SurfaceRuin story structure: bottom at ground minus its height plus this) |
| `spoils` | The Spoils (0.67.0, `TheSpoils.cs`, lore in `lore/spoils_scavenger.txt`), the Great Machine's dumping ground: a heap of pieces cut out of everywhere, each a cell of a weighted Voronoi grid with its own material and tilted layering (rock with its ores and a few rich seams, strata of three rocks, the Devastation's own drock, slabs of meadow, sand, gravel, clay, glacier ice under snow, stacked timber, a corner of somebody's house, stretches of wall, the Machine's riveted plate, coal and peat, geodes of quartz in marble, obsidian, halite), meeting along flat cut faces; shards of the same jutting out of its top; half-buried rusted gears; devastated soil, drock and devastation growths over its skin; caverns joined by tunnels and opened to the flanks, all above the sea, each with salvage chests, junk, a cold light and one piece of somewhere else (a tree hanging from the roof, a corner of a room, a frozen pond, a patch of meadow, a seam of rich ore); the Great Machine's red fog over it. | `radius` (heap radius; `size` stops at 160), `top` (highest peak above the sea, 72), `peaks` (6), `cell` (piece size, 8), `caverns` (9), `entrances` (6), `shards`, `gears`, `fade` (fog fade, 160) |
| `gardens` | The Hanging Gardens Temple (0.67.0, `HangingGardens.cs`, lore in `lore/hanging_gardens_chronicle.txt`): sheer karst spires, each with a spiral stair cut up its middle from a door at its foot to a pavilion on its crown, ledges round its sides planted as gardens with vines down the faces and old man's beard under the ledges; rope bridges with bamboo rails between the spires and out from the terrace, each entering a spire through a passage onto its stair; on the terrace the main hall (vermilion posts, white plaster, grey tiled roof, the altar and the monks' chronicle), a five-tier pagoda, an open pavilion, the monks' quarters, a raked gravel garden and a koi pond with a red bridge; a broad stone stair down from the terrace with three torii and stone lanterns; a pier and a torii standing in the sea off the landing beach; four markets in the lowland, each with three walled stalls and a trader in every stall (furniture, clothing, agriculture with its watered fields, survival goods), and six more traders at spots of their own (the main hall, the west quarters, the pagoda, booths at the pier, the foot of the approach and the koi pond): eighteen; bamboo groves and gravel paths between it all. Since 0.68.0 every passage through a spire (its door, the bridges' ways in, the doorways onto the ledges) is a real corridor on a diagonal too and runs on through the island's own rock until it comes out, and the bridge decks have no holes. | `radius` (how far it spreads; `size` stops at 160), `spires` (12) |
| `alchemist` | The Alchemist Lair (0.68.0, `AlchemistLair.cs`, lore in `lore/alchemist_*.txt`): the alchemists' college the Devastation came out of. On the `alchemist_lair` island's plateau a three by three grid of quads, each a courtyard closed by six stone halls of two to four storeys (slate gables, buttresses, tall windows, corner towers with pyramid roofs, chimneys; a flight of stairs to every storey): libraries, archives, laboratories, optics and drafting rooms, lecture halls, dormitories, refectories and kitchens, workshops, engine halls with standing gears, cabinets of specimens, a chapel, an infirmary; in the courtyards the great clock tower, orreries in fountains, walk-in engines of meshing gears with their boilers, armillary spheres, sundial gardens, and where the Devastation is worst a blighted crater. Paved streets with Jonas lamps on posts; the observatory on the north hill (a round tower, a copper dome split for the great telescope, the orrery and charts inside, a stair down the hillside); the portal gateway in the east bowl (a ring of obsidian and brass on a stepped dais, ghostlights round its rim, drock shards growing in from it, coil towers and their cables, the gatekeeper's control desk, a road and a pipe run from the campus); the college gate and a stair down to the landing quay. After the build the Devastation is laid over the whole island from the gateway out: devastated soil, growths on lawns and slate, drock eating into the masonry; the halls nearest the gate are holed and breached. | `radius` (how far the island reaches, 290) |

Islands for them: `alchemist_lair`, `mansion_island` (the `ruin` kind with the mansion), `eerie_island`, `refinery_island`, `power_plant`,
`pylon_line` (open sea) and `great_machine` (with four bastion ruins and four
pylon lines of its own); all are in `worldplan.example.txt`. In Rustfall
worlds a conditional patch (`assets/landmassgenerator/patches/rustfall-greatmachine.json`)
makes Spreading Devastation place an empty schematic at its pinned
`greatmachine` location and force no land there, so its maps and
`/devastate machine` still point at the new machine. The Better Ruins
mansion is handled the same way since 0.68.0
(`assets/landmassgenerator/patches/rustfall-mansion.json`): its story
structure places an empty schematic and forces no land, its location is
pinned to the Mansion island, and the island places the house.

### Bastion ruins (`bastion <char> ...`)

A ruined fortress structure pass (0.45.0), placed like a cave: a `bastion Q
size=34 dungeony=-8 seed=5` line plus a `Q` on the map at the castle center.
Builds in the masonry of the marker region's rock (stone bricks, cracked
bricks, cobble):

- Four 9x9 corner towers with two ruined floors, window slits, courtyard
  doorways, and tops sheared off diagonally toward the sea.
- A crumbled curtain wall with merlons, breaches and fallen stones, plus two
  roofless outbuildings.
- Spiral stairs in the NW and SE towers boring down to a flat dungeon level:
  a lattice of square 2-wide hallways and 8x6 cells with iron-bar doors
  (some long broken), part-dressed walls and rubble, cut straight out of the
  island's rock. `dungeony=` sets the dungeon floor relative to sea level.
- The dungeon spreads under the whole fortress and beyond, clipped to
  columns at least 6 blocks inside the island, so halls never breach a
  cliff or the sea. A cave line whose `depth=` puts its floor at the
  dungeon level becomes a back entrance where it crosses the halls.

The bastion is axis-aligned regardless of `rotate=`. The previewer draws it
as schematic boxes (towers, curtain, dungeon slab) for placement checks; the
dungeon slab box ignores the island clipping the real pass applies.

### Wreck fields (`wreck <char> ...`)

A floating metallic wreckage field structure pass (reworked in 0.47.0,
densified in 0.48.0): `wreck W radius=55 whirlpool=0 seed=7` plus a `W`
at the field center.
Builds from corroded rusty-iron metal blocks, REAL rusted pipework and
machinery junk (the devastation clutter shapes: junkpipe junctions, broken
pipe ends, pipelong runs, junk beams, hanging chains, tanks, valves),
locust-nest metal spikes (they really hurt), iron fences, part piles and
devastation rock. Meant for a shape of sheer rock spires over deep water
(`ocean plunge=20`, see below):

- One TITANIC hull (up to 64 blocks) rolled onto its side, half-submerged
  and AFLOAT over the deep, plating torn open by coherent noise with the
  rib cage surviving, spiky metal along the torn edges, interior flooded
  below the local waterline.
- 16-21 shattered segments (bow cones, open hull rings); two thirds float
  at the waterline, every third sank and rests on whatever is below it,
  the deep floor or the top of a submerged spire.
- Recognizable ship parts (0.48.0): masts with yard crossbeams and chain
  rigging (the titan's two masts skim sideways just above the water,
  since the ship lies on its side), two sinking prows climbing steeply
  out of the sea, two capsized keel-up hulls with air pockets inside,
  and giant corroded gears as propellers at the sterns.
- Devastated soil silts into some hull tear holes above the waterline and
  thorny devastation growth (devgrowth-thorns/bush/shrike) climbs out.
- The tangle: every floating ruin and every rock spire summit is a node,
  and sagging trusses of hull metal, pipework, junk beams and fence stubs
  run between neighbouring nodes at the waterline, with chains hanging
  below and spikes on top. The ruins visibly hold each other up; the deep
  water underneath stays empty and dark. Truss paths are axis-aligned
  staircases (0.48.0), so long pipes only ever run straight along an
  axis, with junction pieces (junkpipe shapes) at every bend: rectilinear
  wreck plumbing, never diagonal strings of pipe.
- Junk knots at every node, plus rust crust and shoreline litter on spire
  flanks near the waterline (`drock`, piles, spikes, jutting beams).
- Clutter shapes live on a block entity, so the pass records them during
  the bulk build and stamps them through the live accessor after commit
  (type + rotateY on `BEBehaviorShapeFromAttributes`).
- `whirlpool=1` (the maelstrom): a VAST divot pressed into the open sea
  itself, radius ~85% of the field and 26 deep at the eye (0.48.0). No
  rim, no drained pit: each column inside the funnel keeps the full
  ocean below and loses only the water above the local cone surface, and
  that surface block is REAL directional flowing water
  (`saltwater-{n,ne,e,...}-{level}`) spiraling inward, so the whole bowl
  visibly runs downhill into a 2x2 down-flow throat at the eye. The cone's
  slope stays under one block per block, so the flowing staircase covers
  it with no exposed walls. Wrecks inside ride the lowered surface, so the
  streams pour into their torn hulls, and rock tall enough to break the
  local surface stands proud with the swirl wrapping around it. Liquids
  only recompute on block updates, so the sculpted sea holds its shape
  until a player disturbs it.

### Ocean plunge and basin (`ocean plunge=N basin=R depth=D`)

By default the reshaped sea floor starts 2 blocks below sea right at a
drawn coastline, which makes a wading shelf and a visible sand ring around
every land mass. The standalone shape line `ocean plunge=20` starts the
floor 20 below sea instead: coasts drop sheer into deep water with no
shelf and no ring. Pair with `stone=`/`sand=` command options matching the
island's rock so the reshaped ocean floor is not default granite.

`basin=R depth=D` guarantees a bowl of D-deep water within R blocks of
the ISLAND CENTER, no matter how far the nearest land is. The normal
coast carve only reaches `OceanRing` blocks off shore (about 22% of the
diameter), so a megastructure placed in open water would otherwise stand
on whatever shallow natural seabed happens to be there. The basin fades
back to natural over 18 blocks past R.

### Megastructures (`struct <char> kind=... size=... seed=...`)

One directive, seven set pieces (0.49.0, extended in 0.53.0). The marker
can stand in open water; pair with `ocean basin=` when the piece needs
guaranteed depth.

- `kind=lighthouse` (size = tower height): the Drowned Lighthouse
  (`beacon` still accepted as an alias). A tapered granite lighthouse
  standing centered on its drowned shoal: an interior spiral stair
  climbs through planked room floors (flooded below the sea, furnished
  above it, window slits per room), torn rusty band at the waterline,
  and a hollow glass lamp room with a baked ghostlight on its pedestal,
  gallery walkway, rail and cone roof. A broken sister stump stands on
  the same shoal, its fallen lantern cage still glowing.
- `kind=chains` (size = field radius): the Sealed Orb. A hollow shell of
  devastated rock and soil floating half-out of the sea, crusted with
  devastation growth (thorns, shards, shrikes), its upper shell cracked
  and leaking the green light of whatever is sealed inside. Colossal
  rusted chains (hollow alternating links, wide enough to walk) run taut
  from its hide down through the water and into the seabed rock.
- `kind=colossus`: the Kneeling Colossus. A ~110-block giant kneeling
  on the deep floor, built from high-exponent boxes so every part is
  rectangular, Minecraft-statue style. Carved from the island's stone
  with rusted-iron highlights only (iron part rims, corroded plate
  courses, no ore and no gold), ghostlight eyes burning behind the
  carved eye slit, glowing studs around the iron crown band, shield
  tucked at the chest, and a SOLID straight rusted greatsword raised
  point-up. Only the crown and the blade break the surface.
- `kind=serpent` (size = half-circle radius): the Giant Serpent
  Skeleton. A 330-block chalk skeleton lying in an omega pose on the
  lagoon floor: straight neck, half-circle body, straight tapering tail,
  distinct vertebrae and a full ribcage, horned skull with glowing eye
  sockets. At the heart of the half-circle lies a large ship torn in
  two, masts fallen, debris strewn between the halves. Underwater
  Horrors ghostlights stud the spine and rib tips (skipped gracefully if
  that mod is absent).
- `kind=forge` (size = crater radius): the Crater Forge. Carves a real
  crater into a volcano cone, floods the throat with actual lava
  (`lava-still-7`), and hangs a crucible full of melt over it on four
  colossal chains, with a railed catwalk from the rim and three frozen
  lava runs spilling down the outer slopes. The forge_volcano shape
  pairs it with a wide low island: soil flats a few blocks above the
  waterline, a terra preta ring, and a stepped basalt cone.

- `kind=divingbell` (size = the works; the ridge field is about 0.74x
  its radius): the Diving Bell Mine. Raises a solid sea mount of
  rippling rock ridges from the real sea floor upward, then splits it
  with long narrow chasms that fall most of the way to the mantle,
  whose walls carry ledges and overhangs, with flooded caves running
  off them. Ore is seeded through the entire body of rock and is
  thickest in the chasm faces, where each deposit is offset so the slot
  cuts it open: titanium/ilmenite, nickel/pentlandite, chromite,
  uranium, fluorite, lapis lazuli, cinnabar, malachite, corundum,
  graphite, olivine, sylvite, borax, kernite, silver, tin, diamond and
  more, every pair checked against the game's allowedVariants AND its
  ore type property list. Deposits are written only into cells the
  builder knows are stone, with a noise-chewed edge that dissolves into
  the host, so none of them can hang in the water as a ball. Ghostlights
  dot both rims and the worked faces. On the shore the derrick stands:
  braced towers carrying corroded box girders with walkways and railings
  out over the rift, each dropping a chain into a widened bay where a
  house-sized cast bell hangs at its own depth, holding a real pocket of
  AIR so a diver can surface inside it. Older bells lie collapsed on the
  ridges and the rift floor and sit driven into the chasm walls with
  their mouths cut open, every one of them another air pocket, among
  torn hull sections, draped chains and scrap.
  If no land is in reach the towers stand on rock piers instead.
- `kind=granary` (size = compound radius): the Hill Granary. A ruined
  farm compound in the island's own rock: a round silo whose upper
  courses have fallen away toward one side, grain still heaped inside
  and spilled out of the doorway; a barn with footing walls, standing
  timber posts, a few surviving roof beams and part of its plank roof,
  holding hay, loot vessels, a basket and a quern; a cobbled threshing
  floor; and a tumbled yard wall with grass growing through it.

Shapes: `lighthouse`, `chainfield`, `colossus_deep`,
`giant_serpent_skeleton`, `forge_volcano`, `divingbell_mine`,
`granary_temperate` / `granary_arid` / `granary_lush` / `granary_cold`
(see their gen scripts for the suggested commands).

## Localhost previewer

    node viewer/serve.js        ->  http://localhost:5184

Renders any shape file in the browser (three.js) so an island can be
critiqued without launching Vintage Story: terrain with region colors, water,
the pond, tree markers, forest sprinkle, and every declared cave. Terrain
coastline detail is approximate (the jitter noise is a stand-in), but region
layout, heights and shores are the same math the mod runs.

Caves are exact, not approximate: the previewer ports the mod's cave walk
verbatim (same RNG, same constants), so the tunnel on screen is the tunnel
the game carves. Steps that would touch water render dark: the in-game fluid
guard SKIPS those, so a dark section means that part of the cave will not
exist. The info panel counts them; pick a different `seed=` on the cave line
until the layout stays clean.

## Exact previews (block dumps)

The shape-file preview approximates terrain and only sketches structures.
For a preview that CANNOT differ from the game, generate a block dump:

    node tools/dumpgen.mjs lighthouse chainfield ...
    node tools/dumpgen.mjs all               (every shape with a Suggested line)
    node tools/dumpgen.mjs --full ...        (keep the natural terrain around it)
    node tools/dumpgen.mjs --reset ...       (recreate the throwaway world)
    node tools/dumpgen.mjs --stop            (shut the background server down)

The script drives a headless dedicated server (data folder `export/data`,
its own port, pure-ocean world; your real game data is never touched),
builds each island there with the current DLL and shape files using each
shape's `# Suggested:` command line, and writes the blocks to
`export/data/LandmassGenerator/dumps/<shape>.lmd`. The previewer's dropdown
lists dumps under "exact dumps": every voxel on screen is a block the real
generator placed. Colors are averaged from the actual game textures
(`tools/gen_blockcolors.py`, run automatically). The water checkbox and the
cutaway slider expose underwater and interior work; ghostlight, lava and
other glow blocks render full bright.

Fast loop: the server is left RUNNING between runs, so the first run pays
the ~35s boot and every later run hands its jobs straight to the live
server: an iteration costs one island build. Each run builds at fresh
coordinates (a persistent job counter), so runs never collide. When the
mod zip is newer than the running server, dumpgen restarts it
automatically, so after `dotnet build -c Release` you just rerun dumpgen.
Shape-file edits need no rebuild or restart at all.

Dumps are BARE by default: columns the generator never touched are written
as air, so files load and mesh fast and the viewer shows exactly what the
mod will place (landmass, structures, their carved ocean ring). Pass
`--full` to keep the natural seabed and vanilla worldgen around the island,
for checking how a piece meets real terrain.

Under the hood: `/genisland ... dump=1` (or `dump=<name>`, plus
`dumpfull=1` for full dumps) writes the dump when the island finishes,
from any world. The headless server runs commands from
`LandmassGenerator/dumpjobs.txt` (the server console ignores piped stdin):
consumed at boot, and in the dump world also watched for on a 2s tick,
which is what lets dumpgen feed a running server. `dumpwatch.flag` keeps
the server alive after the last job; without it the server stops itself.

## Story world commands

For custom worlds that place the vanilla story locations deliberately
(details and workflow in tips.md):

    /genstoryloc <code> [chunkRange]

Pins a story location (resonancearchive, lazaret, village, devastationarea,
tobiascave, treasurehunter) at your position mid-session and regenerates the
area so it appears now, no restart. For the devastation it also re-points the
layer painter, timeswitch and effects, which vanilla only reads at world load.
Everything in the regen square is deleted.

    /genworldsetup [plan]

One-shot setup for a fresh world from a plan file
(`%APPDATA%\VintagestoryData\LandmassGenerator\worldplan.txt`, created with
defaults on first run). Plan directives: `pureocean` (landcover and upheavel
to 0, applied immediately; land then only exists where forced), `clearspawn N`
(wipe the vanilla spawn land patch at map center), and
`storyloc <code> <mapX> <mapZ>` lines. Pins all locations and rebuilds the
worldgen maps (dropping the auto-rolled story spots). Story areas then
generate the first time a player comes near, like any vanilla story
structure; add a `pregenstory` line to generate them all during setup instead
(slow: the devastation island alone is ~2700 chunk columns).

`ruins [margin=500] [gap=1400] [reach=2200] [spawn=1500] [count=N] [skip=<family>,...] [vanilla=keep]`
(0.67.0) gives every Better Ruins megastructure an island of its own. Better
Ruins places them as surface structures, which the game only keeps where the
ground under the centre and four corners is exactly level and dry; in a
Rustfall world the only land it sees while placing them is the odd hilly
natural island, so they practically never appear. The line picks one site per
megastructure family in open sea, seeded by the world, at least `margin` blocks
from the edge of every plan island and pinned story location (300 from the
game's other story locations), `gap` from each other, within `reach` of the
plan's area, and builds a `ruin_isle` sized to the ruin (2.3 times its longer
side plus 80, 180 to 460 wide; cold ruins get `ruin_isle_cold`, hot or dry ones
`ruin_isle_arid`). The sites are worked out once and kept in the save. The
game's own rolls for the megastructures group are switched off in such a world
(`vanilla=keep` leaves them on). `skip=` leaves families out by schematic folder
or structure code (the Rustfall plan skips `megastructure-10`, the cathedral,
which stands on Cardinals). Only worlds set up from 0.67.0 on, or keeping their
plan in the save, expand the line (an older world reading the live plan has had
its setup and would only get the islands' bare land).

In every Rustfall world (0.67.2), wherever the game's own megastructure rolls
are on, a guard refuses any megastructure the game tries to place within 500
blocks of a plan island's or pinned story location's edge (plus 120 for its
own size), so they still turn up around the world, never next to a lore
island. The log reports how many attempts it refused.

Natural islands keep clear of islands (0.67.0): with "Natural world-gen
islands" on, no natural island breaches within 100 blocks of the edge of a plan
island (half its plan diameter) or a story location's forced land. The
underwater landforms there are flattened as each map region generates, out to
another 128 blocks for the landform map's blending. The edge is where the
island's land really ends (a drawn shape can reach past half its diameter in its
corners). Every Rustfall world (0.67.2); in a world set up earlier it reaches
the regions not yet generated, and natural islands already there stay.

### World creation screen (0.56.0+)

The mod adds a **Rustfall** preset to the New World screen, next to
Standard and Exploration. Picking it sets up the Rustfall ocean world with
no further steps: pure ocean, spawn radius 0, the "Rustfall world" flag,
natural islands on. Press Customize for the **Rustfall** tab:

| Setting | Default | Meaning |
|---|---|---|
| Rustfall world | on with the preset | Runs the world plan on first load (pure ocean, story locations pinned, starter island generated under your feet). |
| Natural world-gen islands | on | Let underwater terrain occasionally breach as small natural islands. |
| Starter island diameter | 150 blocks | Width of the plan's island at 0,0. Replaces the plan file's `diameter=` for that island, at worldgen and in the decoration pass. Every other plan island slides straight away from spawn by half the change, so the water gap the plan drew stays the same (the default plan's cattail isles sit 85 blocks off the starter's shore at any size). Slider 100 to 600 in steps of 10. |

Everything else about the world (which islands, where, story coordinates)
still comes from `worldplan.txt`. The Rustfall tab also appears on the other
presets, so a Standard world with "Rustfall world" ticked behaves the same.

The full Rustfall layout (23 drawn islands, seven story locations including
Spreading Devastation's `greatmachine`) ships as `shapes/worldplan.example.txt`.
Copy it to `worldplan.txt` in the LandmassGenerator data folder.

### Chests and lore (0.58.0+)

A shape line `chest <char> <type> <contents> [cave=<char>] [near=<char> [side=<dir>]] [at=gx,gz] [rust=N] [trunk=1]`
places a filled chest. `<type>` is a vanilla chest type (`normal-aged`,
`collapsed1` to `collapsed4`, `golden-aged`, `owl-aged`, `normal-generic`).
`<contents>` is a comma list of `lore:<id>` (a read-only written book from
`lore/<id>.txt` in the LandmassGenerator folder) and item or block codes with an
optional `*count` (`game:gear-rusty*6`). Place it by drawing `<char>` on the map,
or with `at=` (grid cell), `near=` (two cells inland of a marker such as a landmark
tree; add `side=east|west|north|south` to put it right against the tree's trunk,
found from the world's own logs, so any trunk width works) or `cave=` (on the
floor of that cave's deepest open step). `rust=N` stains the ground around it
with devastated soil. `trail=rust` (cave chests) paints oxidation rust along the
cave floor, by the shortest path through the carved air, from the mouth down to
the chest, with rust weeping down the walls here and there.

Island maps (0.60.0): `landmassgenerator:islandmap-tinmine`, `-forest` and
`-ideal` are charts a chest can hold. Using one puts a red X on the world map at
that plan island (`tin_island`, `forester_island`, `ideal_house_island`), the way
a Better Ruins locator map marks its find.

Loot pass (0.60.0): two shape lines place loot after every structure is built,
finding real floor cells with two blocks of room near a marker:

    loot <table> <count> [near=<char>] [radius=N] [where=inside|surface|underwater|any] [cave=<char>] [type=<chest type>] [above=N] [below=N]
    armorstand <set> <count> [near=<char>] [radius=N] [where=...] [above=N] [below=N]

`inside` = a roof within 10 blocks and walls on two sides; `cave=` spreads them
along that cave; `above=`/`below=` keep them N blocks over/under sea level
(castle floors vs. its dungeon). Too few spots of the asked kind: the rest go to
any floor nearby. Tables (LootPass.cs): fortress, treasury, lighthouse, forge,
mine, ironmine, swamp, wreck, sunken, granary, ruin; every item is checked
against the game and skipped (logged) if missing. Armor stand sets: guard,
knight, bronze, forlorn (aged armor stands, armor written straight into their
slots). Done once per island (save flag), so /genchests never doubles it.

With the Rustfall Lore mod installed (it turns every `lore/*.txt` into a one-page
journal, code `rf-<id>`), a `lore:<id>` chest holds a vanilla lore book carrying
that journal's code instead of a written book: reading it files the text under
Lore in the journal (J). Without the mod it stays a plain written book.

In Rustfall worlds a runaway herd guard refuses a natural spawn when 12 or more
creatures of the same kind already live within 48 blocks (see HerdCap.cs).

Lore files (`lore/*.txt` in the repo, copied to the data folder):

    title: To Seraph
    by: Jonas Falx
    book: aged-darkbeige
    ---
    The text. The game splits it into pages.

`book: paper` or `book: scroll` makes it a loose page or a scroll (0.69.0).

Lore pass (0.69.0). Three more kinds of chest contents:

- `randomlore:<category>[:<item code>]` is a vanilla lore book that reveals the
  next unread page of a random journal in that category when read. Rustfall's
  pools chain categories, so a book still finds something once one is used up:
  `rfpool-jonas` (Jonas, then research, Tobias, any vanilla, anything),
  `rfpool-tobias`, `rfpool-sea` (diaries first), `rfpool-village` (villager
  stories), `rfpool-lab` (research), `rfpool-war` (Better Ruins' Forlorn Hope
  and Last Wall records), `rfpool-trade`. "Anything" leaves out the Rustfall
  records and the vanilla notes bound to their own sites (LorePools.cs).
- `journal:<code>:<page>[:<item code>]` is a lore book holding that one page of
  a journal: `journal:falx:5` is The Bells, the sixth of Michael's scenes
  (0 The Lens, 1 The Charge, 2 The Golem, 3 The Flux, 4 The Mines, 5 The Bells).
- `game:letter-faded` is vanilla's faded letter, which marks Nadiya's village.

A `loot` line takes `with=<contents>`: those go into the first chest it places,
ahead of the roll (the lighthouse keeper's log, the lore in a sunken hull). A
`storychest` plan line takes `near=<block>`: the chest nearest a block whose code
starts with that (`near=anvil` is the smithy in Nadiya's village), among the
chests outside the structure's locked land claims. More island maps:
`islandmap-ironmine`, `-serpent` (the giant serpent skeleton) and `-fortress`
(the Ruined Fortress, `lone_bastion_1`). A new loot table, `serpentnest`, is
heavy on gold and silver.

Mine props: a cave line may add `timbers=N` (a log frame, two posts and a cap
beam, every N steps where the tunnel is 3 to 7 wide and 3 to 6 tall) and
`holders=N` (an empty aged torch holder on the wall at head height every N
steps, for players to light the mine with torches). Halls and flooded caves get
nothing.

Structure kinds `lab` (Tin Island's half-underground flux lab: sunken brick
room, stair, fallen roof on rusted girders, benches, the glowing lens rig, Rust
bleeding out of the door) and `hut` (a timber trader's hut on a levelled footing;
it fells the trees around it first). `gearhill` (0.59.0) is the Starter Island's
buried Machine: anchor it on a cell beside a landmark tree and it finds the trunk
itself, then seals a rusted-plate chamber (13 x 11 x 7) under it, at least 3 blocks
of soil below the lowest ground over its footprint, holding a great gear half sunk
in the floor with a smaller one in mesh, a drive shaft wall to wall, pipes and
chains on the ceiling and parts on the floor. Nothing shows at the surface; it is
found by digging down under the tree. Since 0.60.0 its roof sits just under the
grass (2 blocks of soil, stepping down with the knoll) and two chests of machine
parts stand in its corners.

`lab` takes `huts=N` (0.60.0): N wrecked outbuildings within 20 to 34 blocks, each
with a loot chest. The lab itself now has a ruined second storey that stands 8
courses over the grass, so it reads from the sea. `treasurehunter` places the
game's own treasure hunter homestead (vanilla story schematic, with its trader
spawner and the same land claim); `chest=<contents>` puts a chest 2 blocks
outside the claim, beside the gate.

Mine dressing (0.60.0): a cave line may add `machinery=N` (every N steps a
stretch of wall turns to rusted plate with a gearbox or pipe run against it and
parts on the floor) and `giants=<spawner block>*K` (K spawner blocks in the
cave's widest, tallest halls, 30+ blocks apart; made for Oversized Horrors'
`oversizedhorrors:giantspawner-*`).
`giantrange=N` gives the `giants=` and `mouthgiant=` spawners their own trigger
radius (Oversized Horrors 1.3.0+). A `block` line takes `attr:<key>=<value>`
settings for the placed block's entity, e.g. a serpent spawner's
`attr:triggerRange=80 attr:requireWater=true attr:lingerSeconds=120
attr:lingerRange=120` (Underwater Horrors 0.21.0+); numbers go in as floats,
true/false as booleans.

World plan lines (0.60.0): `devastation off` pauses Spreading Devastation in the
new world (saved with it; `/devastate start` resumes). `storychest <code>
<contents>` adds items to a vanilla story structure's top entrance chest once
the game has built it (the highest chest near a heavy door in its footprint);
`/genstorychests` runs that check at once without waiting for a player.

World plan line (0.63.3): `rain <x> <z> <rainfall 0..1> [radius] [fade]` sets
how often it rains around a spot, nothing else (temperature untouched, grass
tint follows rainfall a little). Rain is decided at the player's own block from
the climate map's rainfall there, plus a bonus of up to 0.16 near sea level.
The character screen calls 0.3-0.45 "uncommon"; 0.4 rains roughly 10-13% of
the time on land, against ~50% at 1.0. The Rustfall plan has `rain 0 0 0.4 400
150` (spawn was "very common"). Applied to each map region as it generates, so
a new world has it from the first chunk; each region remembers the zones it got.

    /genrain                      read the rainfall where you stand
    /genrain 0.4 [radius] [fade]  set it around you (default 400, fading over 150)
    /genrain plan                 apply the plan's rain lines to land that already exists

`/genrain plan` is for a world made before its plan had the line. Zones set with
`/genrain <value>` are kept in the save and reach later land too.

Each world keeps its own plan (0.64.0). The world setup stores the plan's text
and every shape it names in the save; from then on the worldgen renderer, the
setup's island builds, the rain zones and `/genscan` read those, so editing
`worldplan.txt` or a shape never changes a world that already exists, and a
world copied from another data folder draws only its own islands. A hand-run
`/genisland` still uses today's files.

    /genplan                     which plan this world draws from
    /genplan adopt [name]        keep a plan in this world's save

`adopt` is for worlds set up before 0.64.0, which still read the live files at
every load. `name` is `LandmassGenerator/<name>.txt`, or a folder
`LandmassGenerator/<name>/` holding a `worldplan.txt` plus any shapes that
differ from the live ones (the shapes the world was really built with);
nothing given adopts the live `worldplan.txt`. It takes effect at the next
world load and builds nothing.

    /genchests

Brings an existing world up to date with its plan islands: builds missing `lab`
and `hut` structures (never any other kind), places the chests, any missing
marker blocks (the anchors) and the mine props. Re-running is safe: built
structures, spots with a chest nearby, marker blocks already standing near their
spot, and islands whose props are in (a flag in the save) are skipped.

    /genscan [step] [margin]

Finds the natural islands of the current world. With natural islands on, tall
landforms under the sea breach wherever the seed puts them, so the only way to
know where is to generate terrain and look. Generates one chunk column every
`step` blocks (default 128) over the plan's whole area plus `margin` (default
1500), keeps every sample with land above sea level that no plan island or
story location explains, then generates the full neighbourhood of each find
to measure it: HUD centre, size, peak height above sea, nearest planned site.
Results go to chat, the log, and `natural_islands.txt` in the LandmassGenerator
folder. The sampled chunks stay generated in the save. Same seed and settings
give the same natural islands, so one scan describes every world made with
that seed.

    /timereset

Rewinds the calendar to 8am on the 1st of May, year 0, the date a brand new
world starts on. Vanilla `/time` can set the hour and the month but never the
year. Run it as the last step before handing the finished save file to a new
player, so their story begins on day one. Uses the world's own calendar
settings, so custom days-per-month worlds land on the right date too.

## Notes and current limits

Early standalone build, kept separate from Building Commands until it proves stable.

- Chunks are loaded with `KeepLoaded=false`, so for very large islands stay near the centre while it generates.
- No undo yet. Generate on a test world or somewhere you do not mind reshaping.

## Building

`dotnet build -c Release` compiles the mod and deploys `LandmassGenerator_<version>.zip` to `%APPDATA%\VintagestoryData\Mods`, replacing the previous build. Requires the Vintage Story API DLLs at `%APPDATA%\Vintagestory`.

### Red fog: `/genfog list | here [radius] [fade] | clear | dome|haze|embers on|off` (0.63.0)

The Great Machine's island is shrouded in red fog, the way the vanilla
Devastation area wears its brown one: an ambient fog close up, a red dome
seen from out at sea, and embers and ash rising from the ground. The
`greatmachine` structure adds its zone when it is built; `/genfog here 640
200` adds one where you stand (for a world whose machine predates 0.63.0),
`/genfog list` shows them and `/genfog clear` removes them. Zones are kept in
the save and sent to players as they join. Clients without the mod simply
see no fog.

Each part can be switched off for a world (0.63.1): `/genfog dome off` drops
the red sphere seen from outside (it reads very red and shows through the
water), `/genfog haze off` the fog inside, `/genfog embers off` the rising
particles. `sphere` and `particles` work as names too; `on` brings a part
back, and `/genfog list` shows which are on. Since 0.63.2 a world starts
with the dome off and the haze and embers on; `/genfog dome on` brings the
dome back.

### Inspecting a built world: `/gendumparea x z [radius] [name]` (0.59.0)

Writes the world around HUD x z (a square, default radius 64) to
`LandmassGenerator/dumps/<name>.lmd`, natural terrain included, for the
previewer. Works headless, so a test world can be checked spot by spot
without walking it (through the Pilot bridge: `/cmd?c=gendumparea -3618 -6737 90 nat26`).

### Checking structure lights: `/genlightcheck x z [radius]` and `/genrelight x z [radius]` (0.65.0)

`/genlightcheck` loads and holds the area, finds every Jonas lamp and
ghostlight in it, and logs how many shine at the level their type says
(the game's own stored block light at the lamp or beside it). `/genrelight`
relights them all now, the same way a structure's lights are relit when a
player first comes near. On a headless server with no player, far chunks
drop their pending light updates when they unload, so lights placed there
read dark until relit with the area held loaded; give the light queue a
minute or two after a relight before checking. Use `tools/sealcheck.py`
on a dump to see which open spaces join the sky, and `tools/fluidcheck.py`
for water or lava that would run.

### Checking the keep-out: `/genkeepout check [rule]` (0.67.3)

Walks the ring from every plan island's edge out to `rule` blocks (default
100), generating chunks as needed, and reports natural land in it per island.
Island and story land are left out. On the installed Rustfall 0.67 all 47
islands are clear at 100.

### Drawing the whole world map: `/genmapexport [x1 z1 x2 z2 | core | list]` (0.67.4)

The world map (M) is drawn by the game client from the chunks the server has
sent it, one 32 x 32 piece per chunk column, and kept in
`VintagestoryData/Maps/<world id>.db`. This command draws the same pieces on
the server: it loads every chunk column in the area (generating the ones not
generated yet), paints each piece the way the client does (the parchment
palette, slope shading, water edges), and writes them into
`Maps/<world id>.db` in the server's data folder.

The default area is every plan island (the ruin isles among them) and every
story location within 2200 blocks of one, plus 640 blocks, and a square of its
own round each story location farther out. `core` leaves those squares out,
`list` shows the areas without drawing, and four numbers give one area in HUD
coordinates. A rerun skips tiles already drawn, so a stopped export picks up
where it left off.

To give a player the whole map:

1. Copy the save to a throwaway server data folder (the export generates and
   saves every chunk in the area, so the copy grows by gigabytes).
2. Run `/genmapexport` there. On Rustfall 0.67 the core alone is 259,000
   chunk columns, about 55 minutes and 3.2 GB.
3. With the game closed, copy `Maps/<world id>.db` into the player's
   `VintagestoryData/Maps`. The copy of the save keeps the world id, so the
   file name matches the player's own save.

Chunks the player visits later are redrawn by the client as usual.

### Rain: `/genrain cap [percent]` and `/genrain survey` (0.68.0)

The climate map's rainfall sets both the colour of grass and leaves and how
often it rains, so a lush island was also a wet one: on Rustfall 0.67 it
rained 66% of the time on Eerie Island and 44% on the Ideal Home Island. In a
Rustfall world the weather now reads a softened rainfall: unchanged up to
0.15 below the cap, squeezed above it so that rainfall 1.0 becomes the cap
(default 0.40). Grass and leaves keep their real colour. `/genrain cap 55`
sets the cap for a world (100 turns it off; it lives in the world config, so
the client agrees), `/genrain cap` shows it, and `/genrain survey` logs every
plan island's and story location's rainfall and the share of the time it
rains there, counted with the game's own weather noise.

### Dynamic Villages: `/genvillages [check x z]` and the plan's `village` line (0.68.0)

Michael's Dynamic Villages mod founds a village wherever a player's
surroundings offer level soil, which in a Rustfall world meant the lore
islands themselves (the gardens, the Ideal Home Island, the Forge Volcano)
and the story locations. In a Rustfall world it now refuses any site within
60 blocks of a plan island's edge (the ruin isles too) or of a story
location's land. A plan line `village <mapX> <mapZ> [hamlet|farmvillage|stockade]`
founds one on purpose, once, when a player first comes within 600 blocks
after the setup has decorated every island; the Rustfall plan puts a farm
village on the Ideal Home Island. `/genvillages` lists the plan's villages,
`/genvillages check x z` asks Dynamic Villages for its verdict on a site.

### Clearing ground without straight treelines (0.68.0)

Every structure that clears ground for itself (the gardens, the ruins, the
Alchemist Lair) now fells whole trees: a tree with a log on the ground being
cleared comes down entire, and every other tree keeps every leaf. Emptying a
box left the forest edge cut off along a grid line.
