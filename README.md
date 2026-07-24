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
| `block <char> <blockcode> [lift]` | One block placed resting on the actual ground at that spot, on the sea floor when the spot is underwater. Meant for spawners and props from other mods (e.g. `block X underwaterhorrors:serpentspawner 15`), so the code is resolved at placement time: a block the game does not know is reported as a note, never an error. The optional lift raises it that many blocks off the ground (underwater it never rises past 3 below the surface), for spawners whose trigger range must reach swimmers at the surface. Unlike tree and cave markers, a block marker may stand in open ocean; it only joins a region if one directly touches it. |

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

### Ocean plunge (`ocean plunge=N`)

By default the reshaped sea floor starts 2 blocks below sea right at a
drawn coastline, which makes a wading shelf and a visible sand ring around
every land mass. The standalone shape line `ocean plunge=20` starts the
floor 20 below sea instead: coasts drop sheer into deep water with no
shelf and no ring. Pair with `stone=`/`sand=` command options matching the
island's rock so the reshaped ocean floor is not default granite.

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
`storyloc <code> <mapX> <mapZ>` lines. Pins all locations, rebuilds the
worldgen maps (dropping the auto-rolled story spots), then pregenerates each
story area one by one with progress in chat.

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
