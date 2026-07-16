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

    map
    ..........FFFFFFFF..........
    .......PPPPPPPPPPPPPBBBB....
    ...

`.` is ocean, every other character is a region. Region keys:

| Key | Meaning |
| --- | --- |
| `rock` | Rock type for this area. Its stone, sand, and ores follow it (`granite`, `slate`, `basalt`, ...). |
| `rock2` | A second rock, noise-blended into the first underground (e.g. `rock=slate rock2=peridotite`). |
| `sand` | Explicit beach block, so a slate island can still have a white `sand-chalk` beach. |
| `fertility` | Soil richness for grass areas: `verylow`, `low`, `medium`, `high`, `terrapreta`. |
| `surface` | `grass`, `sand`, `rock`, or `rocksand` (rocky outcrops speckled with sand). |
| `height` | Fraction of the island's peak `height` this area rises to. |
| `shore` | Blocks from the coast to full height. Small is a sheer cliff, large is a gentle beach. |
| `rough` | Surface roughness, for outcrops and broken ground. |
| `pond` | Makes the region a pond this many blocks deep: a level water bowl contained by a flat grass rim. |
| `cattails` | On a pond region: chance per rim column of a cattail, so reeds ring the water. On a shore region: chance per column within 3 blocks of the sea. |
| `flax` | Chance per grass column of a wild flax plant (mixed maturity). |
| `copperbits` | Chance per grass or rock column that a surface-copper CLUSTER starts there, reproducing the game's own `surfacecopper` deposits: a shallow poor/medium ore disc in the stone under the soil, with loose copper bits over about a third of it. Digging under any bit finds the ore. Typical values 0.001-0.003 (each cluster is ~10-20 bits). |
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
| `ores`, `forest`, `trees` | As above, but per region. |
| `tree <char> <type> <size>` | A landmark tree at that spot on the map. |

Height comes from a distance-to-coast field, so interiors rise and shores taper naturally, and the coastline is jittered with noise so the grid never shows as stair-steps. Two worked examples ship, each generated by a script in `tools/` from a hand-drawn map: `ideal_island` (a varied island with beach, plains, forest and a rocky slate arm) and `starter_island` (a smooth, idyllic player-start island, ~150 across: a large white-sand beach fills the west side, sparse oak forest covers the north, a rich meadow and pond sit by the central giant oak, and the one rugged edge is a slate headland on the north-east with a small cliff at the water):

    /genisland shape=starter_island diameter=150 height=8 stone=rock-peridotite sand=sand-peridotite

## Notes and current limits

Early standalone build, kept separate from Building Commands until it proves stable.

- Chunks are loaded with `KeepLoaded=false`, so for very large islands stay near the centre while it generates.
- No undo yet. Generate on a test world or somewhere you do not mind reshaping.

## Building

`dotnet build -c Release` compiles the mod and deploys `LandmassGenerator_<version>.zip` to `%APPDATA%\VintagestoryData\Mods`, replacing the previous build. Requires the Vintage Story API DLLs at `%APPDATA%\Vintagestory`.
