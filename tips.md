# Island-building tips

Working notes for agents building islands with this mod. Michael plans 20-30
hand-designed islands for a custom Vintage Story world, so read this before
starting one, and add what you learn. Gotchas that cost real debugging time go
in [papercuts.md](papercuts.md).

## Workflow

1. Write a generator script in `tools/gen_<name>_island.py` that prints a shape
   file (see `gen_starter_island.py` as a template). Radial-harmonic outlines
   plus angle/ellipse region tests work well and stay smooth.
2. `python tools/gen_<name>_island.py > shapes/<name>.txt`
3. Copy the shape to `%APPDATA%\VintagestoryData\LandmassGenerator\`. Shape
   files are read fresh at command time: **no rebuild, no world restart** for
   shape-only changes.
4. Code changes need `dotnet build -c Release` (auto-deploys the zip to the
   Mods folder) **and a world restart** to load the new DLL.
5. Test with `/genisland shape=<name> diameter=<d> height=<h>` from open ocean.
   Regenerating over an old island works, but old TREES are never cleared, so
   prefer a fresh spot.
6. You cannot drive the VS client. Ask Michael for screenshots, and tell him
   exactly which angles help (shoreline, region transitions, the pond).

## Design language that works

- **Smoothness is the default.** The generator rounds smooth terrain and noise
  separately, so a region with `rough` below ~0.12 comes out with clean flowing
  ground and zero speckle. There is no dithering; treat `rough` above ~0.3 as
  "rubble field" and use it rarely, if at all.
- **Heights are fractions of the command's `height`.** For a gentle island use
  height=8 with plains at ~0.7. The highest region defines the peak.
- **Cliff-then-highland needs two bands**: a thin coastal region with a small
  `shore` (3) and modest height for the cliff at the water, then the tall
  region behind it. One region cannot express both. See regions C and R in
  `starter_island.txt`.
- **Shores are swim-out flush.** The generator drops the whole island one block
  so the coast meets the water surface exactly. Do not fight this; beaches with
  height ~0.16 and shore ~36 taper beautifully into it.
- **Ponds must be interior.** The pond water level derives from the region's
  raw `height`, assuming the rise has saturated. A pond region touching a
  coast slope would float or sink. Give the pond region the same `height` as
  its surroundings.
- **Region borders blend automatically** (heights and shores are smoothed over
  a ~5 cell radius), so adjacent regions may differ in height by ~0.15 without
  a visible seam. Bigger differences read as a deliberate slope.
- Keep neighboring grass regions' `height` equal when you want an invisible
  border (fertility/flora changes only).
- **Non-blob outlines: build the landmass around a SPINE, not a radius.** A
  radial `coast_r(ang)` with a bay carved out always reads as a dented blob.
  For a crescent (tin_island), the land is a band around a circular spine:
  inside iff `|rho - spineR| < halfThickness(phi)`, thick at the back and
  tapering toward the horn tips, with an angular gap left for the harbor
  mouth. The same trick generalizes: an S-curve, an atoll ring, a hook are all
  "distance to a curve < thickness(param)".
- **Orientation is a command option, not a shape edit.** `rotate=<deg>` spins
  any drawn island clockwise; design shapes in their natural pose (tin
  island's harbor faces west) and aim them at generation time.
- **Mood comes from climate tint as much as blocks.** `climate=arid` rewrites
  the worldgen climate map over the island so grass and leaves render rusty
  desert-yellow; `lush` goes deep green. Pair `climate=arid` with verylow
  fertility, `surface=barren` bands, sparse `wildgrass`, and extra exposed
  rock for a properly desolate island.

## Making it look like the game made it

The authority on what natural VS terrain contains is
`%APPDATA%\Vintagestory\assets\survival\worldgen\blockpatches\*.json`: each
file lists the exact block codes vanilla scatters. Parity checklist for a
"natural" island, with the region keys that provide it:

| Vanilla feature | Region key | Notes |
| --- | --- | --- |
| Tall grass tufts | `wildgrass` | on by default (0.35), mixed heights |
| Loose stones | `stones` | on by default (0.012), granite |
| Flowers | `scatter=cornflower:0.01,...` | friendly names resolve via flower-/mushroom-/fern-/herb- prefixes |
| Ferns, mushrooms | `scatter=` | mushrooms belong under trees |
| Berry bushes | `bushes=raspberry:0.01,...` | any `fruitingbush-wild-*` type; `birch` = dwarf birch shrub |
| Fallen sticks | `sticks` | forest regions |
| Leaf litter | `litter` | stamps discs under each tree (leafy at trunk, grass at canopy edge), like vanilla; ~0.8 for a real forest floor |
| Reeds by water | `cattails` | pond rim ring, or sea waterline on shore regions |
| Waterlilies | `lilies` | pond regions |
| Seashells | `shells` | sand columns only |
| Boulders | `boulders` | uses the region's rock |
| Clay by water | `clay` | pond rim; players NEED clay early game |
| Surface ore hints | `copperbits` | loose ore stones, real pickupables |
| Underground ore | `ores=copper:medium,...` | this mod's terrain has NO ore unless asked |

Rules of thumb for densities: wildgrass 0.3-0.4, flowers 0.005-0.015 each,
bushes 0.002-0.012, mushrooms ~0.005, shells ~0.02, boulders under 0.015.
Below ~0.002 a feature is effectively invisible on a 150-block island.

## Verified block-code cheat sheet

All verified against 1.22.3 assets. When in doubt, grep
`%APPDATA%\Vintagestory\assets\survival\blocktypes\` for the json and read its
`variantgroups`.

- Soil: `soil-{verylow|low|medium|compost|high}-{none|normal|...}`.
  **`compost` displays as "High fertility soil"; `high` displays as "Terra
  preta".** The `fertility=` key translates high→compost for you.
- Sand exists for every rock type: `sand-{rock}` (e.g. `sand-chalk` is white).
- Water: fresh `water-still-7`, ocean `saltwater-still-7` (different tint!).
- Tallgrass: `tallgrass-{veryshort|short|mediumshort|medium|tall|verytall}-free`.
- Bushes: `fruitingbush-wild-{type}-free`, types: beautyberry, blueberry,
  cloudberry, cranberry, blackberry, blackcurrant, raspberry, redcurrant,
  whitecurrant, strawberry.
- Cattail: `tallplant-coopersreed-land-normal-free` (also tule, brownsedge,
  papyrus).
- Wild crops: `crop-{type}-{stage}` sit fine on plain soil (flax stages 1-9).
- Loose things: `loosestones-{rock}-free`, `looseboulders-{rock}-free`,
  `loosestick-free`, `looseores-{mineral}-{rock}-free` (NO grade segment, and
  combos are gated by allowedVariants: nativecopper occurs in most rocks
  including slate/peridotite, malachite only in limestone/marble).
- Seashells: `seashell-{scallop|sundial|turritella|clam|conch|seastar|volute}-{latte|plain|seafoam|darkpurple|cinnamon|turquoise}`.
- Clay: `rawclay-{blue|red|fire}-none`.
- Waterlily: `waterlily`.
- Forest floor (leaf litter): `forestfloor-{0..7}`, 0 = leafiest.
- Trees: generator names are the file names in
  `assets/survival/worldgen/treegen/` (englishoak, scotspine, silverbirch,
  dwarfbirch...). `trees=oak` style aliases resolve via FindTreeGenerator.

## Testing checklist before calling an island done

- Walk-out test: swim to every shore type and to the pond; you must be able to
  climb out anywhere.
- Meadow check: interior grass should have NO stray one-block bumps.
- Region seams: beach-to-grass and grass-to-rock transitions read as slopes,
  not walls.
- Flora sanity: trees grounded, reeds at water, mushrooms under trees, nothing
  floating.
- Run `/genisland shapes` and read the chat for region problem notes (missing
  blocks are reported, not silently dropped).
