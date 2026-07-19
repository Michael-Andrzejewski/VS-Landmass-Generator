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
6. Check the design in the localhost previewer BEFORE asking Michael to test:
   `node viewer/serve.js`, open http://localhost:5184, pick the shape. It
   renders terrain, regions, water, markers, and the EXACT cave paths (the
   viewer ports the mod's cave walk verbatim). Iterate cave `seed=` there
   until the info panel reports zero steps touching water.
7. You cannot drive the VS client. Ask Michael for screenshots, and tell him
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
- **Caves are declared per entrance, not scattered.** A `cave <char>` line
  plus a map marker gives a hand-placed cave system: `heading=auto` aims it
  at the island centre, `dip`/`depth` shape the descent, `weave` and
  `branches` make it a mine. Wall ore (`ores=copper:0.06`) turns the tunnel
  into a real deposit players can pick at. The path is deterministic per
  design (same layout every regeneration); reroll with `seed=` and pick the
  layout in the previewer.
- **Mountains are the spine trick in another suit.** The forester island's
  crag crescent is "distance to a circular arc < band width", stacked: summit
  knots (pd) above a narrow crag band above high/mid forest bands, heights
  1.0 / 0.9 / 0.82 / 0.64 stepping down. The bowl INSIDE the arc becomes a
  hidden dell for free, and a tarn pond fits its centre. Suggested height
  ~34 on a 300 island reads properly mountainous; 26 read as a big hill.
- **Wide caves need a raised mouth on flat shores.** At `scale` 1.5+ the
  carve ellipsoid plus the fluid guard's padding reaches 4-5 blocks below
  the adit floor. A waterline mouth (`mouth=2`) works against a steep apron
  (starter island), but on a flat beach the first gallery steps keep
  clipping the water sideways and the guard walls the passage off, and no
  seed sweep fixes it (the wet run sits in the LEVEL entry, so `dip` does
  nothing either). `mouth=6` lifts the adit clear. The previewer's "wet on
  the main tunnel, worst run N" line is the tell: a run above 2 on the main
  tunnel means a sealed mine, wet branch tips just truncate. Salt Teeth
  extended the lesson: even against a steep waterline CLIFF, scale 1.4
  clipped the sea on every seed at `mouth=2`; `mouth=5` cleared it, and a
  cave door partway up a cliff face looks right anyway. Sweep-diagnose
  first: EVERY seed wet at the mouth = structural, raise the mouth; only
  SOME seeds wet with long runs = the path crossing shallow water, pick a
  dry seed or steepen `dip`.
- **A spiral (or any curve) is still the spine trick.** Serpent's Coil is
  "distance to an Archimedean spiral < half-thickness(s)", with the curve
  densely sampled into ~900 points and nearest-point distance per cell
  (fast enough in Python at 140x140). The same code path draws S-curves,
  hooks, rings. Details that made it read as a creature from above: taper
  the half-thickness from a thick head to a thin tail tip, run a 1-2 cell
  band of dark-canopy forest along the crest (a dorsal fin line), fringe
  every shore with the rock's own sand, and end the tail in a low landing
  spit. For an eye lagoon inside the coil, the spiral's inner terminus
  radius must exceed head half-thickness + pool radius or the head eats
  the pool.
- **Tint can vary WITHIN an island (v0.26+).** Regions accept their own
  `climate=` (same presets as the command option): a lush dell under arid
  crags, or a whole test island of strips. Climate pixels are ~30 blocks
  wide and the client interpolates between them, so make each climate zone
  at least ~60 blocks across or bands blur together. `climate_isle` is the
  reference/test shape (8 identical strips, one per climate).
- **Bogs are surface=peat (v0.26+).** Real minable peat a few blocks deep,
  part-covered in sparse grass; flora treats peat as grass-like ground, so
  cattails, berry bushes, mushrooms and tallgrass sit on it naturally.
  Pair with `rock=shale rock2=claystone` and `ores=coal:0.02` for honest
  coal country (`coal`, `quartz`, `sulfur` and the other ungraded minerals
  work in `ores=` and cave `ores=` since v0.26: one block, all grades).
- **Pointed peaks come from shore, not height.** A region whose `shore` is
  about equal to its own radius rises as a CONE (the coast-distance ramp
  never saturates), while a tiny `shore` makes a flat-topped tower with
  cliff walls. Michael specifically rejected the towers ("narrow towards
  the top and sharpen to points"): sharp islets and spires want
  `shore ~ half-width`, heavy `rough`, and most of them SHORT, with only
  one spine left tall. Abrupt cliffs everywhere read as unnatural; save
  shore=2-3 for one deliberate cliff face.
- **Rivers are just snaking ponds.** Two touching pond regions with the
  same `height` share one water level, so a winding pond=2 region drawn as
  a thick polyline between meres becomes a continuous waterway. Give pond
  regions `forest=` and (v0.28) the trees stand in the knee-deep rim
  water: cypress-lined rivers for free.
- **Salt water and fresh water want different plants.** Cattails and
  waterlilies are freshwater: meres, rivers, pond rims. The sea and
  sea-connected flood flats want `kelp=` (seaweed stalks, v0.28) over a
  dark `sand=` bed instead. Michael called this out on the fen: reeds
  hemming the SALT shore looked wrong.
- **flood= puts the sea ON a region (v0.27+).** The region's ground caps
  1-3 blocks below sea level and salt water flows over it: marsh flats a
  raft cannot cross (Blackfen's drowned inlets), mangrove-style shallows
  where the region's trees stand IN the water, and lurker reefs (a small
  rock islet with `flood=1` sits one block under the surface, invisible
  from a raft: Salt Teeth's 'U' shards). Waterline `cattails` everywhere
  now clump into 20-40 block reed beds instead of an even hem, and a
  flood region's cattails use the game's water reed inside 1-deep water.
  A dry region checked BEFORE the flood wedge (a hummock, a knoll) becomes
  an island inside the marsh for free.
- **Salt is a ROCK, not an ore.** Vanilla halite comes as solid `rock-halite`
  domes inside sedimentary rock (claystone/sandstone/shale/chalk/limestone/
  chert/conglomerate). For a salt island, give the dome region
  `rock=chalk rock2=halite`: the underground blend becomes a proper
  minable salt body, and a cave through it exposes the salt in the walls.
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
| Vanilla-true ore | `deposits natural` line | re-runs the game's own deposit pass over the island: same seed, same ore maps propick reads. Needs host rock: coal wants a sedimentary `rock2` (claystone/shale), quartz+copper live in granite. No `ores=` needed at all |
| Wild pumpkin patches | `pumpkins` | mother plant + parented living vines + fruits + debris |
| Sand drifts inland | `sandy` | noise blobs of the region's sand across grass |

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

## Vanilla story locations in the ocean world (verified 1.22)

- `/wgen story setpos <code> <x 1 z> true` pins a story location; codes:
  resonancearchive, lazaret, village, devastationarea, tobiascave,
  treasurehunter. Plain coordinates are HUD/map-center relative, so with the
  starter island at 0,0 the planned map coords work directly. Y is ignored.
- setpos also forces LAND into the ocean map (patch radius = landformRadius
  + 32, NaturalShape-grown to roughly double area, so ~650 blocks wide at
  the vanilla radius 200), plus the required landform and, for the village,
  climate. Structures generate only during CHUNK GENERATION; regen with
  `/wgen delr N` if the area already generated as ocean.
- Do all setpos calls immediately on first join (treasurehunter auto-rolls
  within ~1100 of spawn and spawn chunks generate instantly), then restart
  once so the auto-rolled forced-land entries are dropped; the forcing list
  is rebuilt from saved locations at every world load.
- Island size per location is `landformRadius` in
  `worldgen/storystructures.json`; shrunk for Rustfall by the
  extras/rustfall-story-tweaks patch mod (floors: must cover the schematic,
  village is 195x225). Radius changes only affect newly generated chunks.
- Never /genisland over a story site: the stamp would shred the schematic.
  Treasure hunter therefore sits ~450 blocks off Forester Island as its own
  islet.
- `.mapzoom 0.1` (client command, standalone Map Zoom Out mod at
  Desktop\vs-map-zoom) zooms the map past vanilla's
  0.25 floor to see the whole 12k world; map must be open. Very low values
  load a lot of map chunks at once, don't go below ~0.05.
- Devastation ordering trap: GenDevastationLayer snapshots the devastationarea
  location ONCE at world load (InitWorldGen), while the tower schematic reads
  the live registry at chunkgen. Generate the area after a setpos WITHOUT
  restarting and you get tower-but-no-devastation (and the fog/effects/past
  dimension point at the old spot). Recovery: restart, then
  `/wgen story rmsc devastationarea`, then `/wgen delr 22` at the site.
- `/genstoryloc <code> [chunkRange]` (mod 0.30.0+) places a story location at
  your feet mid-session: chains vanilla setpos + delr (delr also deletes map
  regions, so the forced landform re-applies even in already-generated areas)
  and, for devastationarea, re-points the three systems that snapshot the
  location at world load (GenDevastationLayer, Timeswitch, devastation
  effects) plus broadcasts the new location to clients. Default range covers
  landformRadius + 32; everything in that square is DELETED and regenerates
  as you fly around.
- `/genworldsetup` (0.31.0+) is the fresh-world one-shot: plan file
  worldplan.txt (pureocean / clearspawn / storyloc lines, defaults = the
  Rustfall coordinates). Verified mechanics: NoiseOcean rolls each ~1km cell
  against landcover, so ANY landcover above 0 yields kilometer-scale
  continents and exactly 0 yields none; upheavals (upheavelCommonness,
  default 0.3) create big raised landmasses independently, so pure ocean
  needs BOTH at 0. GenMaps.initWorldGen() is public and re-running it
  mid-session is the restart-equivalent (rebuilds oceanGen from current
  config, clears forced-land lists); GenStoryStructures.SetupForceLandform
  (private, reflection) re-adds forcing for saved locations. The vanilla
  spawn land patch is hardcoded (ForceRandomLandArea, radius 128 at map
  center) and must be cleared from GenMaps.requireLandAt plus delr'd to
  make 0,0 open ocean.
- Pure ocean has THREE land sources, not one (all verified in 1.22 code):
  landcover rolls ~1km continent cells (0 = none); upheavelCommonness
  raises seafloor independently (0 = none); and the LANDFORM map still
  rolls normal landforms under the ocean, which GenTerra merely shifts
  down by oceanicity * MapSizeY/256 * 0.333 (~85 blocks), so tall
  landforms (largeislands, mountains...) breach the surface as random
  small islands. 0.33.0 fixes the third source when the plan says
  pureocean: every new map region's LandformMap is set to veryflat
  outside story-location circles and its UpheavelMap zeroed
  (MapRegionGeneration handler, runs after GenMaps by mod load order).
  Regions generated BEFORE the flag was set keep their islands; delr
  them or make the world fresh.
- Natural islands are a Rustfall-tab OPTION (0.35.0+): the
  rustfallNaturalIslands checkbox (default on) gates the pure-ocean landform
  flattening; unchecked = only story + hand-built land exists. Square island
  artifacts come from mixed-settings seams: chunks generated before the
  ocean config changed sitting next to chunks generated after (clearspawn
  square edges, old-vs-new map regions). 0.35.0 prevents the class for
  checkbox worlds by forcing the ocean config at SaveGameLoaded, BEFORE the
  first chunk generates; InitWorldGenerator reads it after that. Existing
  seams: stand nearby and /wgen delr 8 (also deletes map regions, so the
  regen is consistent).
- Plan files can BUILD islands (0.36.0+): `island <mapX> <mapZ> <genisland
  options>` lines run right after clearspawn and before the story pregen,
  via /genisland's new x=/z= map-coordinate options (no player needed).
  Default plan builds starter_island at 0,0, so a fresh Rustfall world puts
  ground under the player within the first minute; the kept spawn core is
  3x3 chunks so a 150-wide starter fully covers it. Islands run one at a
  time (_islandBusy polled every 2s); the shape file must already be in
  the LandmassGenerator data folder.
- Deleted chunks do NOT regenerate on their own and clients keep rendering
  stale terrain they already downloaded: after clearspawn the wiped square
  looked like "chunks not generating" until relog. 0.37.0 regenerates the
  wiped square in paced bands right after deletion and pushes every chunk
  (BroadcastChunk + ResendMapChunk) to connected clients. Setup also now
  starts at ServerRunPhase.RunGame, before the singleplayer client finishes
  joining, and the spawn clear zone is 640 blocks of flattened landform in
  BOTH natural-island modes, so spawn is always open ocean past the starter.
- Islands can render DURING chunk generation (0.38.0+): on Rustfall /
  pure-ocean plan worlds a ChunkColumnGeneration pass (Vegetation, after
  vanilla vegetation 0.5, before GenLightSurvival 0.95 so sunlight is
  computed with the island in place) writes each plan island's terrain
  into chunks as they are born, updating WorldGenTerrainHeightMap +
  RainHeightMap per column. Vanilla holds a joining player at "Loading
  spawn chunk..." until the spawn chunk column is generated, and spawn Y
  is WorldGenTerrainHeightMap+1 at map middle, so the player materializes
  STANDING on the starter island on frame one of a brand-new world. Three
  things make it deterministic: spawnRadius forced to 0 at SaveGameLoaded
  (vanilla default scatters first spawns ~50 blocks, here into ocean),
  GenMaps' private requireLandAt list cleared at InitWorldGenerator so no
  vanilla spawn continent is ever born, and per-island seeds derived from
  world seed + plan coordinates (PlanIslandSeed) so the worldgen renderer
  and the live decorator (/genisland skipterrain=1 on the auto setup)
  agree on every block. The auto clearspawn keeps chunks that touch a
  worldgen island rect: they were born correct, deleting them would make
  the island vanish and rebuild in view.
- Island CHAINS are one shape file, not many islands (0.39.0, cattail_isles):
  draw each islet as an ellipse strung along a spine curve, alternating
  small across-spine offsets so they read scattered, not threaded. Two
  numbers decide whether the chain reads as separate spires or one drowned
  bank: the ocean carve hits full `water=` depth about OceanRing*0.45
  (~0.1 * diameter) blocks from the nearest LAND, and flood= skirts COUNT
  as land for that distance. So keep skirts thin (~2 blocks) and give
  islets 20+ block edge gaps or the sea floor between them stays shallow.
  In-game the columns root on the real sea floor when it is deeper than
  the carve, so the previewer's flat -8 ring is the WORST case. For the
  reed look: cattails on the dry islets (waterline beds), cattails on the
  flood=1 skirts (the game's water reed standing IN the 1-deep water),
  kelp only on the deeper flood heads. Since 0.39.1 every cattails= shore
  also hems itself in the SEA: wherever the water just off that coast is
  exactly one deep, water reeds continue the same clumped beds, so no
  skirt region is needed just to get reeds into the water.
- `block <char> <blockcode>` (0.39.0) places one block resting on the real
  ground at that map cell, sea floor included: made for OTHER MODS' spawners
  and props (block X underwaterhorrors:serpentspawner). Resolved at
  placement time, so a missing mod is a chat note, not an error, and the
  marker may stand in open ocean (it only joins a region that directly
  touches it). It counts toward the decoration chunk rect, so a far-out
  marker still gets its chunk preloaded for the pre-open pass.
- Decoration can beat the loading screen too (0.38.2+): ServerMain launch
  order is TriggerWorldgenStartup (worldgen init + BLOCKING spawn chunk
  generation, 7x7 chunks = 224 blocks around map middle) then the RunGame
  phase event (synchronous, main thread) and only then thread start / tick
  loop / join processing. So the auto Rustfall setup now runs the whole
  world setup synchronously inside its RunGame handler, and decorates
  worldgen-rendered islands in one blocking burst (plant loop + finish
  passes) while the player is still loading: the world opens with trees,
  flora, ore bits and caves in place. Guards: only with zero connected
  players, and only if the island's land chunks (+24 block tree margin)
  are all loaded, else it falls back to the paced live pass. The offshore
  ring may poke past the spawn area; it needs no decoration, so the check
  ignores it.
