# Papercuts

Gotchas that cost real debugging time while building this mod. Read before
touching the generator; append when you hit a new one. General know-how goes
in [tips.md](tips.md).

The pattern across almost every entry below: **when a feature silently does
nothing, it has never once been the density or the tuning.** Five separate
times the cause was structural: a wrong block code (loose ore), a surface
gate (rock columns skipped by the plant pass), a pass overwriting its own
output, a client-side cache (climate tint), a block entity deleting itself
(pumpkin vines). Check codes, gates, caches, and lifecycles before touching
numbers.

## Vintage Story API traps

- **`ITreeGenerator.GrowTree` takes the GROUND block, not the air above it.**
  We passed `topY + 1` and every tree on the island floated one block up.
  Verified against the game's own TreeGenTool.cs, which passes the clicked
  ground block.
- **Soil block codes lie about fertility.** `soil-high` is Terra preta;
  "High fertility soil" is `soil-compost`. Michael noticed terra preta on a
  starter island where high-fertility soil was intended.
- **Ocean water is `saltwater`, not `water`.** Same climate tint map, different
  base texture, visibly different color. Fill ocean rings with
  `saltwater-still-7` or the seam shows.
- **The water surface block is at `SeaLevel - 1`.** Land clamped to `SeaLevel`
  leaves a one-block lip you cannot swim up. Shore land must end AT
  `SeaLevel - 1` (flush with the water surface) for walk-out beaches.
- **`GetTerrainMapheightAt` reflects earlier bulk edits** after Commit, so
  regenerating over an old island roots on the OLD island's surface. Fine for
  terrain; old trees are never cleared though.
- Blocks placed with SetBlock skip placement validation: crops stand on plain
  soil, reeds on sand. They stay until a neighbor update. Convenient, but do
  not rely on it for blocks with aggressive update checks.
- **Do not extrapolate a block-code shape from a sibling block.** Ore blocks
  are `ore-{grade}-{mineral}-{rock}`, so we assumed loose ore was
  `looseores-{grade}-...`. It is `looseores-{mineral}-{rock}-free`, no grade,
  and the mod's surface copper silently never spawned (soft-lock risk!). The
  blocktype's own `variantgroups` AND its `allowedVariants` list are the only
  truth: malachite looseores exist solely in limestone/marble rocks, for
  example. GetBlock returning null is reported in the chat problems note, so
  READ that note after every /genisland run.
- **Grass color is NOT in the block; it is the climate.** The client tints
  grass and leaves from the worldgen climate stored in each map region's
  `ClimateMap` (temp byte 16-23, rain byte 8-15, geologic activity byte 0-7;
  pixels span ~32 blocks). To fade an island rusty, rewrite those pixels
  (see StampClimate). Three traps inside: padding cells mirror data owned by
  NEIGHBOUR regions and are also read during interpolation, so write the whole
  padded grid from world-position math or borders show tint seams; regions
  must be marked `DirtyForSaving` and pushed with `BroadcastMapRegion` or the
  edit is invisible and lost; and chunks already sent were meshed with the old
  tint, so `ResendMapChunk` the footprint afterwards.
- **...and even all of that is not enough: the client caches the tint.**
  climate=arid looked like a no-op in testing despite correct server data,
  broadcast, and chunk resends. Decompiling VintagestoryLib showed why:
  `ClientWorldMap.LerpedClimateMaps` holds a pre-lerped per-region copy used
  by the chunk tesselator, and NOTHING invalidates it, not even the map
  region packet handler. The mod's client half (StartClientSide) clears it
  via reflection whenever a map region arrives; a vanilla client shows the
  new tint only after a relog. When a change "does nothing" despite verified
  data flow, hunt for a client-side cache before doubting the data.
- **pumpkin-vine blocks delete themselves when placed loose.** Their block
  entity (BlockEntityPumpkinVine) ticks every 2s and calls Die() unless its
  `parentPlantPos` points at a block whose code starts with `crop-pumpkin`
  or `pumpkin-vine`; distance is never checked. A `crop-pumpkin-N` mother on
  plain soil is static forever (only farmland ticks crops), so place one
  mother per patch and adopt every vine onto it by rewriting the BE's tree
  attributes (parentPlantPosX/Y/Z). Use the plain block accessor, not a bulk
  one, so the BE exists immediately after SetBlock.
- **Loaded chunks PACK their block data away in seconds; vanilla worldgen
  code assumes they never do.** Both v0.28 islands died in the finish pass
  with an NRE inside vanilla's DiscDepositGenerator: the PDB mapped the crash
  line to `chunks[y/32].Data.GetBlockIdUnsafe(...)`, and `Data` was null
  because the server had packed (compressed) the chunks during the ~2 minutes
  the island spent building. GenDeposits runs during chunk generation in
  vanilla, so it never meets a packed chunk; a replay minutes later does.
  `GetBlockIdUnsafe` is "unsafe" precisely because it skips the packed check.
  Fix (0.28.1): call `chunk.Unpack()` on every chunk before handing the
  column to GeneratePartial, which is exactly what vanilla's own /wgen regen
  command does before touching Data on loaded chunks. Corollary of the
  unloaded-chunks lesson below: a chunk being non-null does not mean its
  DATA is resident either. Diagnosis trick worth keeping: the game DLLs ship
  portable PDBs, so a crash line like "DiscGenerator.cs:318" can be mapped to
  the exact IL instruction with System.Reflection.Metadata sequence points +
  ilspycmd -il, turning "something in this method is null" into "THIS
  dereference is null" with certainty.
- **`initAssets(blockCallbacks: false)` does not just mute callbacks, it
  REWRITES the deposit configs** (`variant.WithBlockCallback &= flag`), and
  saltpeter cannot survive that. Its deposit targets cave AIR and relies on
  its callback (BlockFullCoating.TryPlaceBlockForWorldGen) to read which
  neighbour faces are solid and pick the matching coating variant
  (saltpeter-d/-n/-nd/...); the callback also enforces the y-window and the
  darkness check, and places nothing when no face is solid. With callbacks
  stripped, GenDeposit's raw-write branch stamps floor-variant saltpeter-d
  into every air cell of the disc, floating unattached, and each one pops
  into a ground item on its first neighbour update (OnNeighbourBlockChange
  drops a stack per lost face, breaks at zero). We had copied
  blockCallbacks: false from ProPickWorkSpace, which only READS deposit
  stats. Fixed in 0.28.2: pass true; writes go through our instance's
  blockAccessor, which setApi points at the plain world accessor (the
  worldgen thread's accessor is only attached by an event we never hook),
  and the replay runs on the main thread, so that is safe. Note vanilla's
  clay/peat use withLastLayerBlockCallback, a GENERATOR property the flag
  never touched, so those callbacks had been firing through the plain
  accessor all along: the proof it works.
- **A resolver failing quietly downgrades a feature to "missing".** Every
  optional feature here reports resolution failures via the problems list;
  keep that pattern for anything new, and treat any problem line in chat as a
  bug to fix, not a warning to ignore.

- **`TreeGenParams.otherBlockChance = 0` silently deletes wild pine resin.**
  The engine rolls resin logs as `otherBlockChance * treegen otherLogChance`
  (TreeGen.TriggerRandomOtherBlock), and the API default is 1.0: vanilla
  worldgen never sets the field, only player-grown saplings zero it so
  farmed trees never leak. Our GrowTree calls copied the sapling shape and
  passed 0, so every pine on every island came out resin-free while
  scotspine/fir/mountainpine/bristlecone all declare log-resin-pine-ud at
  otherLogChance 0.01 (acacia: log-resin-acacia-ud at 0.005), and no other
  treegen has an otherLog at all. Fixed in 0.42.0 by passing 1f (vanilla
  parity, roughly 1 leaking log per 100, rolled only on segments thick
  enough to be logs). Same silent-failure family as the rest of this file:
  the feature was structurally off, no density would have fixed it.

## Terrain-shaping lessons

- **A vanilla-style cave walk FORGETS its heading.** GenCaves' turn logic is
  a momentum random walk; fine for wilderness caves that go nowhere, fatal
  for a designed mine adit: at weave 0.55 the tunnel U-turned within 20 steps
  and bored out under the sea. Designed caves need a homing term pulling the
  heading back toward the design bearing every step (shortest arc, RNG-free).
  Same idea as the vertical dip target. Fork angles need capping too (40-86
  degrees) or side galleries run back out of the island.
- **Clamp caves against the DESIGNED surface, not the engine heightmap.**
  The cave roof clamp first used GetTerrainMapheightAt, which still held the
  pre-island ocean seabed after our bulk fills, so "keep 3 blocks under the
  surface" became "carve nothing above the old sea floor": in-game caves came
  out as tiny deep pockets with no visible mouth while the previewer showed
  a full system. Michael caught it by noclipping inside. The generator's own
  ColumnSurface is the ground truth for anything the generator itself built;
  it is also exactly what the previewer replays, so preview and game cannot
  disagree about the clamp again.
- **You cannot bore a horizontal doorway into ground that rises one block
  per step; BUILD the wall.** Coastal aprons climb gently, so a level adit
  sits under 1-4 blocks of cover for its whole entry. Clearing that thin
  cover to "open the mouth" slices a RAVINE along the entrance (Michael's
  screenshots), and not clearing it seals the mouth. The answer is neither:
  stamp a solid rock headwall around the mouth (StampHeadwall), replacing
  the soil in its footprint, and carve the tunnel through it. Michael
  described the fix himself: "manually place slate rock to cover the
  ceiling"; when a hand-fix is obvious, codify THAT instead of fighting the
  terrain. First stamp attempt only FILLED above ground and did nothing (5
  blocks placed) because the ground was already at height, just made of
  dirt: the stamp must convert, not just add.
- **The mouth sealed TWICE, for two different reasons; test the opening
  itself, in game.** Second cause: the skin-clear compared the ground against
  the carve loop's bound ceil(cy+vr), which is one block ABOVE the highest
  block the ellipsoid actually removes, so real 1-2 block skins looked like
  "no skin" and were never cleared. Any is-it-exposed test must use the top
  block actually carved in that column (cy + vr*sqrt(1 - horizontal frac),
  floored), not a bounding-box edge.
- **A cave mouth's open-carve window must end on BURIAL, not a step count.**
  With "doorway mode" fixed at the first 7 steps, entrances frequently sealed
  themselves: past step 7 the entry section preserved the surface block, so
  on the rising hill face the tube's top got clipped and the tunnel began a
  few blocks INSIDE the hill with no opening (Michael hit this on every
  island). Doorway mode now runs until the tube top is 5+ blocks under the
  designed ground. The 5 matters too: the island's soil skin is 3 blocks, so
  a roof clamp of ground-3 gave DIRT cave ceilings; ground-5 keeps every
  ceiling in rock with a spare stone layer above it. Any carve step whose
  padded ellipsoid touches water is skipped whole (that is what keeps the
  ocean out), so a walk that leaves the island's underground footprint just
  stops existing: the first starter-mine layout lost HALF its steps this way
  and would have looked fine in chat ("1 cave carved"). The previewer
  emulates the guard and counts the steps that will not carve; sweep cave
  seeds there until it reports zero.

- **Do not dither.** A global +-0.7-block dither was added to break contour
  terraces; it made every meadow read as random dirt speckle and Michael
  flagged it twice (on two different islands' generations) before it died. Real
  terrain is smoothed by erosion. Clean terraces look better than noise.
- **Round smooth terrain and noise separately.**
  `Round(smooth) + Round(noise)` means noise below half a block NEVER makes a
  step. Rounding the sum instead lets +-0.3 of noise flip columns wherever the
  smooth part sits near .5, which speckles entire flat regions.
- **Anything with ONE water level needs ONE source of truth.** The pond's rim
  and water level were derived per column (including noise) and the surface
  tore into steps with air gaps. Fix: derive the level once from the region's
  raw height, and flatten a one-cell collar of neighboring land to match.
- **Carved shapes look carved.** The pond's flat-bottomed bowl read as
  artificial; tapering depth from the edges (Smooth over ~4 blocks) fixed it.
  Default to gradients over constants for any natural feature.
- The shape grid is sampled with +-0.7 cell jitter, so "adjacent to region X"
  checks must scan all 8 neighboring cells, and thin one-cell features can be
  skipped over entirely: make map features at least 2 cells wide.
- **The plant pass originally skipped rock-topped columns entirely**, so
  nothing declared for a rock-surface region (boulders on the slate headland,
  copper bits over the mine) ever spawned, silently. When a whole feature is
  absent, check the pass's surface gate before tuning densities.
- **A noise threshold is not a density.** "noise > 0.82" gives an UNKNOWN ore
  fraction because the noise's value distribution is not analytic. To hit a
  real target like "2 ore blocks per 100 stone", calibrate: sample the noise
  field a few thousand times at parse time, sort, and take the quantile
  cutoff. Cheap, deterministic per seed, and the density becomes a promise.
- **Natural features come in CLUSTERS; per-column rolls make lonely singles.**
  Vanilla surface copper is its own deposit config
  (worldgen/deposits/metalore/nativecopper.json, code "surfacecopper"): a
  shallow disc radius ~4.25 just under the surface with surfaceBlockChance
  0.33 putting loose bits over it. Reproduce the mechanism, not the average
  density. When copying vanilla, find its actual generator config first.
- **A streaming pass overwrites its own multi-column features.** Columns are
  processed in order; a cluster stamped around column N writes onto columns
  N+1... whose own decor pass then replaces the blocks. Collect feature
  centres during the pass and stamp them AFTER it finishes (own accessor,
  own commit), checking what already exists (do not replace wood/leaves).
- **Surface hints must anchor to the real thing.** Loose copper scattered by
  raw chance reads as decoration; sampling the actual ore-vein noise under
  each column ("bit only where digging finds copper") makes it a honest
  prospecting signal. Same principle for loose stones: pick slate vs
  peridotite by the SAME blend noise the subsurface used, not a fixed rock.
- **Leaf litter belongs under canopies, not scattered uniformly.** Vanilla's
  ForestFloorSystem grades `forestfloor-0..7` outward from trunks, and a
  uniform region-wide sprinkle reads wrong immediately. Runtime GrowTree does
  NOT invoke that system for you (skipForestFloor=false is not enough); stamp
  the disc yourself around each planted tree (see StampLitter).

## Process lessons

- **Michael's feedback loop is screenshots.** You cannot run the VS client.
  Ship, ask for specific angles, iterate. Predicting what he will flag works
  poorly: of four predicted issues only one materialized, and the two real
  issues (waterline lip, noise source) were unpredicted. Test claims against
  assets/source instead of intuition.
- **Windows PowerShell 5.1 cannot reflect the net10 game DLLs** (member types
  fail to load). Write a tiny C# file and `dotnet run file.cs` instead; that
  is how ClimateMap/BroadcastMapRegion were verified.
- **Reflection shows what exists; decompiling shows what it DOES.** The
  climate-cache bug was invisible to reflection (the API surface all looked
  right). `dotnet tool install -g ilspycmd`, then
  `ilspycmd -p -o <outdir> "%APPDATA%\Vintagestory\VintagestoryLib.dll"` and
  grep the output tree. Works on VSSurvivalMod.dll too, which is how
  BlockEntityPumpkinVine's Die() condition was read. Escalate to this the
  moment behavior contradicts a verified data flow.
- **side=Universal does not have to force the mod on players.** Adding a
  client half normally makes joining clients install the mod; set
  `"requiredOnClient": false` in modinfo.json and vanilla clients can still
  join, they just miss the client-side nicety (here: live tint refresh).
- **Verify block codes against the assets, not memory.** Every "obvious" code
  guessed from memory (raspberry? leaf litter? high fertility soil?) was
  checked in `assets/survival/blocktypes/` first, and several would have been
  wrong. The blockpatches configs are the ground truth for what vanilla
  scatters and the exact codes it uses.
- Shape-only changes need no rebuild and no restart; DLL changes need both. If
  a change "did nothing", check which kind you shipped.
- The game may be running while you build. The csproj deploys a new
  version-named zip and deletes the old one with ContinueOnError, so builds
  succeed, but the running world still has the OLD mod loaded.
- modinfo.json version bumps on every behavior change, or the Mods folder
  collects stale same-name zips.
- **Embedded browser panes can report a 0-size viewport at load.** The
  previewer's canvas came out 0x0 (toDataURL returned "data:,") because
  window.innerWidth was 0 when the script ran and no resize event followed.
  Self-heal the renderer size inside render() instead of trusting load time.
- **Keep viewer/app.js's cave walk bit-identical to the C# one.** Same
  xorshift32, same draw ORDER (7 doubles per step, the sharp-turn draw only
  when its roll hits, 4 doubles + 1 uint per branch), same constants. Any
  drift and the preview shows a cave the game will not carve. Both sides
  carry a comment saying to change them together.
- **PowerShell `>` redirection writes UTF-16, and the previewer reads UTF-8.**
  `python gen.py > shapes/x.txt` from PowerShell produced a UTF-16-LE file:
  the GAME still read it fine (File.ReadAllLines detects the BOM), but the
  previewer's fetch decoded it as UTF-8 into NUL-riddled garbage, showing
  "0 columns / no caves declared" for a perfectly good shape. Worse, that
  failed build left the camera radius NaN (Math.max propagates NaN), so even
  a good reload rendered nothing until the page was reopened (now guarded in
  refresh()). Regenerate shapes from bash, or pass -Encoding utf8 explicitly;
  if the viewer shows 0 columns, check the file's first bytes before
  debugging the parser.
- **Three "different" big-island bugs, one root cause: unloaded chunks.**
  A 500-wide island came out with missing slices (bulk writes into unloaded
  chunks are silently dropped), most climate strips never tinted
  (StampClimate skips unloaded map regions), and the finish chain died with
  an NRE in SyncHeightmapsAndDeposits (a map chunk existed with a null
  WorldGenTerrainHeightMap). server-main.log had the stack; the chat showed
  nothing because the exception killed the tick handler before the report.
  Fixed in 0.27.0: a preload gate force-loads every chunk column under the
  island (LoadChunkColumnPriority + wait loop that only gives up after 30s
  WITHOUT loader progress), heightmap null guards, and a try/catch around
  the finish chain that reports the error in chat. The lesson for any new
  pass: never assume a chunk, map chunk, map region, or heightmap exists,
  and never let a finish pass throw silently.
- **The browser pane can lose its WebGL context, and stats survive while
  renders lie.** After two `computer` screenshot calls timed out, the pane's
  GPU process died: every capture came back blank (same byte count each
  time is the tell), page reloads then failed at renderer CREATION
  ("Error creating WebGL context"), which kills app.js top-level mid-run so
  even parseShape-then-rebuild throws a confusing TDZ error ("Cannot access
  'group' before initialization"). The fix is a NEW tab (tabs_create), not
  more reloads of the dead one. Crucially, rebuild() stats computed on the
  dead tab are still trustworthy (the cave walk and wet guard are pure CPU);
  only the pixels were wrong. Check `renderer.getContext().isContextLost()`
  before trusting any capture.

- **LoadChunkColumnPriority over ~2000 columns kills the server.** The chunk
  request fifo (MagicNum.RequestChunkColumnsQueueSize, default 2000) throws
  "Indexed Fifo Queue overflow" on overflow and that exception takes the whole
  server down ("Exception during Process"). The devastation pregen (3025
  columns in one call) crashed exactly this way while five smaller sites
  worked. Batch big rects into bands (we use 256 columns) chained via
  ChunkLoadOptions.OnLoaded, and leave headroom for the players' own chunk
  loading sharing the same queue.

- **Mod worldconfig lang keys need a specially named file.** The main menu
  preloads world-config translations from `assets/game/lang/worldconfig-<locale>.json`
  inside the mod (TranslationService.PreLoadModWorldConfig enumerates exactly
  that filename), NOT from the normal `en.json`. Keys in `en.json` work
  in-world but the Customize World tab and checkbox show raw lang keys at the
  menu. Also: a mod worldconfig.json without `"playStyles": []` NREs the
  vanilla world list, and the customize row label key is `worldattribute-<code>`
  (with optional `-desc` hover), while the tab is `worldconfig-category-<category>`.

- **Requesting a chunk column while its previous request retires kills the
  server.** Vanilla addChunkColumnRequest does GetOrAdd into the request
  index while retirement does TryRemove(key) on other threads; lose the race
  and EnqueueWithoutAddingToIndex throws "In queue but missed from index!",
  which dies as "Exception during Process". Hit it when the world setup's
  clearspawn deleted the columns under the player (their client re-requests
  them every tick while regen retires them in bursts) plus back-to-back
  pregen band requests. Cannot be caught from mod code (throws on a server
  thread). Mitigation in 0.34.0: never delete the chunks under the player,
  and separate every chunk request burst with RegisterCallback cooldowns
  (750ms between bands, 2s between sites, 4s after clearspawn).

## Worldgen-init handlers run in a half-built server (0.38.0 bugs, fixed 0.38.1)
Symptoms: 0.38.0's worldgen island rendering silently did nothing; the
player spawned on a vanilla continent and the live pass built the starter
island over it at runtime, leaving floating grass.
Three separate traps, all at InitWorldGenerator time:
- GenMaps.requireLandAt is a PUBLIC field in 1.22.3. Reflecting it with
  BindingFlags.NonPublic returns null, silently. The 0.31+ runtime clear
  had been failing this way all along, masked by clearspawn wiping the
  vanilla spawn land afterward. Just access the field directly; the ocean
  map generator keeps a reference to the same list, so Clear() sticks.
- sapi.World.DefaultSpawnPosition THROWS (NRE) during InitWorldGenerator:
  vanilla computes mapMiddleSpawnPos only AFTER worldgen init, inside
  InitWorldgenAndSpawnChunks. Use MapSizeX/2, MapSizeZ/2 directly.
- Log the full exception object, not e.Message: the 0.38.0 catch logged
  "Object reference not set..." with no stack, which identified nothing.
The payoff once fixed: InitWorldgenAndSpawnChunks triggers worldgen init
and then BLOCKING-generates the spawn chunks during the launch screen, so
a ChunkColumnGeneration pass genuinely runs before the world opens. That
is the only reliable "content exists before the player ever lands" hook;
everything tick-based races the client's loading screen.

## Blocking decoration silently deferred (0.38.2, fixed 0.38.3)
The 0.38.2 pre-open decoration never ran: the log showed "1 island(s) to
build" and the live pass planting trees a minute after join. Two guards
tripped, neither logged why (fixed: deferrals now log their reason):
- Vanilla's startup blocking load covers MagicNum.SpawnChunksWidth = 7
  chunks (224 blocks) around map middle, but a 150-wide starter island
  plus the 24-block tree margin spans 8 chunk columns when map middle
  falls on a chunk boundary. One column short = DecorationChunksLoaded
  false = deferred. Fix: MagicNum.SpawnChunksWidth is a public static
  read AFTER InitWorldGenerator handlers run, so init widens it to cover
  the spawn island's decoration rect (capped at 21 chunks across).
- The AllOnlinePlayers==0 guard: the singleplayer client is already
  CONNECTED (in the client list) during the RunGame phase transition even
  though it is still on the loading screen. Dropped the guard; on the
  auto first-run the tick loop has not started, so blocking is safe by
  construction.

## Previewer screenshots when the Browser pane screenshot tool times out
The in-app browser's screenshot action can time out repeatedly on the
WebGL previewer even while the page itself is healthy (JS still responds,
console clean). Workaround that works every time: run a 10-line Node HTTP
sink on another localhost port that base64-decodes POST bodies into PNG
files, then from the previewer page run
`fetch('http://localhost:5199/name', {method:'POST', body: canvas.toDataURL('image/png')})`
via the JS tool and Read the saved file as an image. Give the page ~1-2s
after changing the shape dropdown before grabbing the canvas.

## Decoration rect: grid square vs land bounding box (0.39.0)
GetDecorationChunkRect originally used the whole shape GRID
(max(W,H) * worldPerCell / 2 + 24). A narrow chain drawn across a wide
150-cell grid (cattail_isles) inflated the rect so far past the actual
land that the SpawnChunksWidth cap could not cover it, and its pre-open
decoration would always defer. The rect now uses the LAND cells' bounding
box (block markers included, corners carried through rotate=), so only
chunks the island can actually touch are demanded.

## Zero cattails anywhere: the clump noise was double-scaled (0.27.0, fixed 0.39.2)
Michael's cattail chain generated with every other plant in place but not
one reed, land or water. The clump gate did
`SurfNoise.Noise(x * 0.045, ...) > 0.58`, but SurfNoise is built with its
own 1/22 base frequency (FromDefaultOctaves(3, 1/22.0, ...)), so the extra
0.045 made the "20-40 block reed beds" actually ~500-block noise cells:
whole islands sat inside one bare trough and rolled zero, seed-dependent,
while densities looked fine. Textbook silent-failure rule: it was the gate,
not the tuning. Fix (ReedChance): coordinates scaled 0.7 for real ~30 block
beds, PLUS a thin ungated base scatter (0.6x density) near water so no
island can ever roll zero again. When a noise helper already carries a
base frequency, extra coordinate scaling multiplies INTO it; check the
constructor before adding a scale factor.

## UH serpent spawner on the deep floor never triggers (0.39.2)
The spawner block arms on players in WATER within SpawnerTriggerRange (40)
blocks in 3D (verified in Underwater-Horrors source). On a sea floor 30+
deep and 25 blocks off the swim line, a surface swimmer is 45-55 blocks
away and it never fires. `block` markers take an optional lift
(`block X underwaterhorrors:serpentspawner 15`), capped at 3 below the
surface, so the trigger sphere reaches the surface. Note the spawner also
skips creative/spectator players when /uh observer is ON.

## "A few chunks didn't generate" that was really the cave (0.40.0, fixed 0.41.0)

Michael reported broken chunks at ironmine's east face: a huge void roofed
by a one-block lattice of hanging grass with pines on top, a sheer smooth
pale wall, sea water pooled below. The server log showed one clean island
run, zero unloaded columns. None of it was chunk generation:

- The hanging lattice was the SHALLOW carve mode (mouthKind 1) doing its
  documented job: it kept exactly one surface block. Fine over a slim
  tunnel; under a 26-wide bore that stays shallow for tens of blocks it
  yields a gallery under a floating skin, which reads as failed worldgen.
  Fixed: shallow mode now keeps the surface plus a 3-block lid
  (roof = ground - 4, was ground - 1).
- The "smooth sand wall" was the stamped HEADWALL seen from inside:
  rock-chert is pale cream and the stamp is a smooth dome, so up close it
  reads as an unnaturally flat sand plane.
- Lesson: before debugging "bad chunks", read server-main.log for the
  island-complete line and its warnings, and place the report's HUD
  coordinates against the island design (556,-402 was exactly the mouth
  cell of an island generated at 319,-368).

## Live fills never update the heightmap, so rebuilds leave hanging remnants (fixed 0.41.0)

FillColumn cleared above the new terrain up to max(naturalY, dome top),
with naturalY from GetTerrainMapheightAt. The live builder never writes
that heightmap back (only the deposits pass does, and only on region
columns), so any column a PREVIOUS live island had raised still reported
the original seabed: regenerating over or beside an earlier build kept
everything above the clear top as floating slabs with sheer cut faces,
and trees were never cleared at all. Fixed structurally: after the normal
clear, the fill keeps scanning upward and deletes solids and fluids until
it sees 4 consecutive air blocks, so old terrain, old ponds and old trees
all come down. The heightmap itself is still only corrected by the
deposits pass; if a shape drops `deposits natural`, remnant clearing is
what saves the next rebuild.

## depth= was a suggestion: high weave drilled branches 40+ blocks past the design floor (fixed 0.44.1)

The walk's level-out is an EASED steering target (12% of the gap per
step), and weave adds vertical momentum noise on top. On a weave=0.7
cave, a bad momentum run out-fights the easing for a hundred straight
steps: Lone Bastion's depth=52 dungeon reported "deepest 88 below sea",
with the dive in a level-2 branch. Nothing warns; the previewer's deepest
line is the only tell. Fixed structurally: after the mantle clamp, no
step may sink below floorY - 2 (the 2 leaves room events their dished
floors). Mantle-depth designs are untouched (their design floor sits at
or under the y=8 clamp), but any REGENERATED island whose old walk had
strayed below its design depth gets a slightly different lower cave
layout than the version it was first built with.

## A cave marker on the outermost coast cell can drown and be silently skipped

The coastline jitter can round the outermost land cell's world column into
water, and a cave marker standing there is dropped without a note (the
previewer says "no caves declared", the game just carves nothing).
lone_bastion_1 hit this: the east-cliff mouth scan picked the last body
cell on its row. Place cave and bastion markers 2+ cells inside the coast;
the mouth's open-air scan walks outward and opens the face anyway.

- A structure marker in OPEN WATER stands on the natural seabed, not on
  your deep water. The ocean carve only reaches OceanRing blocks off
  shore (~22% of diameter); beyond that ColumnSurface returns false and
  Ground() falls back to sea - water, which LIES: the real floor is
  whatever vanilla generated (often 10 deep). Caught in the previewer
  when the colossus site rendered as shallow plateaus. Fix: `ocean
  basin=R depth=D` (0.49.0) guarantees the bowl around the island
  center; the previewer mirrors it.

## Rebuilding open-water structure shapes leaves remnants + drained ocean

Rebuilding an island at the same spot only restores columns the coast
carve processes (within OceanRing of land). For open-water shapes (wrecks,
megastructures) most of the area is BEYOND the ring: old structure blocks
out there are never cleared, and water state can end up mixed (the exact
previewer showed a lighthouse rebuilt inside a drained cylinder wearing
the previous tower's remnants). The dump pipeline sidesteps it by
recreating its world every run (dumpgen default). In the REAL world, when
iterating a struct/wreck shape at the same coordinates, expect remnants;
a clean regen needs /wgen delr on the area first (or a proper
clear-struct-bounds pass, not built yet).

## `ocean basin=` used to RAISE the outer seabed (fixed in 0.51.0)

Symptom: every basin structure (chainfield, colossus, serpent lagoon)
stood inside a huge raised plateau ring with vertical cliff walls down
to the natural ocean floor, and tiny land blobs inside the bowl were
extruded as sheer rock towers. Cause: the basin fade curve started at
sea-2 and OVERWROTE naturally deeper columns upward, and the bowl won
over the coast carve right at the shoreline. Fixed: basin is carve-only
(skips columns already deeper) and fades in over the first 30 blocks off
shore. If an old island was generated before 0.51.0, rebuilding it in
place will NOT remove the old plateau ring beyond OceanRing of land
(see the open-water rebuild papercut); /wgen delr first.

## Ghostlight cubes emitted NO light (fixed in 0.52.0)

Symptom: /genisland lighthouse looked right, but at night the whole
tower was pitch black; the serpent, using Underwater Horrors chiseled
ghostlights, baked its light fine in the same world. Cause: the
0.51.0 full-cube ghostlight declared `sideopaque: { all: false }`
(copied from UH's json-shape block). A CUBE drawtype declared
non-opaque on all sides breaks the engine's light emission for that
block. Both vanilla full-cube emitters (creativelight, paperlantern)
set sideopaque all TRUE; matching them fixed it. Debug with the
"[landmassgen] glow probe" server log line: the immediate read races
the async relight thread and can print 0 even when the light is fine;
trust the "(late)" line 1.5s later (should be ~22).

## A dump census by SUBSTRING counted 54k blocks that were never there

Checking the granary islands, "devastated" came out at 54,985 blocks on a
150-wide island, which read as the devastation patches having eaten the
whole farm. They had not: the census matched palette codes containing
"drock", and `crackedrock-granite` contains it. The real devastation was
127 soil blocks and 16 growths, i.e. too SMALL, the opposite diagnosis.
Match full palette codes, or anchor the substring, before believing any
dump statistic. Sibling of the count-by-full-code lesson in tips.md.

## The struct builder's Ground() reported a sea floor that was not there (fixed in 0.54.0)

Symptom: everything the diving bell mine built offshore came out as a
crust floating over open water. Michael, in game: "the terrain beneath
the bells is oddly hollow."

Cause: BuildStruct's Ground() called
`ColumnSurface(job, x, z, sea, out ...)`, passing SEA LEVEL where that
method expects the column's real natural terrain height. ColumnSurface
uses the argument to decide whether `ocean basin=` is allowed to carve
(`if (basinY >= naturalY - 3) return false`, i.e. skip when the basin
floor is above the natural floor). With sea level standing in for the
natural floor that test could never fail, so every open-water column
answered with a phantom sea floor at `sea - 2 - basinDepth`, often 40 to
60 blocks above the real sea bed. Anything anchored to it (the first
bank, the first chamber shell) was built in mid water with nothing
underneath.

Fix: read `GetTerrainMapheightAt` FIRST and pass it in, which is exactly
what FillColumn has always done. The lesson generalises: when a helper
takes the natural height as an argument, never feed it a constant.

## Ore balls and floating lights: writing without asking what is there (fixed in 0.55.0)

Symptom, from Michael in game: ore appearing "in odd spherical chunks
of floating blocks", and ghostlights hanging in the water instead of
sitting on anything.

Cause, in both cases the same one: the builder wrote a shape without
testing what occupied the cells. `Seam()` stamped a full ellipsoid of
host rock through `Set()`, so wherever the lens reached past the chasm
face it filled open water with stone and left a ball hanging in the
rift. `Glow()` queued whatever coordinate the caller computed, and a
rim light computed from the massif height lands in mid water whenever
that column has since been carved away.

The trap behind the first one is that you cannot simply read the world
to find out: inside BuildStruct every edit is still staged in the bulk
accessor, so `GetBlock` describes the terrain as it was before the
command. The fix is a predicate the builder owns (`IsRock`, backed by a
`carved` set the carve calls fill in), consulted before every ore
write. The fix for the second is a pinning pass that runs AFTER the
commit, where the world finally is the truth, and moves or drops any
emitter that touches nothing.

Lesson: a structure pass that writes blindly will look correct in the
code and wrong in the world. Either compute the occupancy yourself or
do the work after the commit, and log the count either way.

## dumpgen can time out on a 600-block island from a COLD start

`waitForDumps` allows `5 + shapes*15` minutes, so 20 for one shape. A 600
block island is about 14 minutes of build on its own, which fits; add a cold
server boot and the world gen under it and it does not. First run on
`ideal_house_island` printed `ERROR: timed out` and the wrapper exited, and
the build had NOT failed: the server was still working and wrote the .lmd
five minutes later (509,560 columns, 338M cells). The second run, against the
already-warm server, finished in 14 minutes and reported `done` normally.

So a timeout from dumpgen is not a result. Read `export/server.log` before
concluding anything, or tail it through a monitor while the job runs. The line
that matters is `[landmassgenerator] Island complete:`, which carries the
region problem notes and the cave notes; that is the actual verdict, and
`[dump] wrote` confirms the file. On anything this big, run dumpgen once to
warm the server and read the log rather than trusting the wrapper's exit.

## A cave marker on a plateau reports a buried entrance

First cut of the relaid ideal house island put the copper mine at t=0.88 on
the east marble ridge, which is a flat plateau from t=0.80 out to the coast.
The generator reported:

    cave at map 184,88: no open air within 24 blocks seaward of the mouth,
    entrance may be buried

The check walks SEAWARD from the mouth looking for open air, and seaward of a
plateau column is more plateau: the cliff edge was 30 blocks further out. A
cave mouth needs ground that FALLS AWAY in front of it within about 24 blocks.
Moving the marker down onto the beach at the ridge's foot fixes it, and reads
better anyway. Watch for this whenever a mine goes into a tall region whose
height is uniform out to the coast; the old island got away with it because it
had a separate low apron band (region C) at the waterline for exactly this.

## Chunk-error reports: what to collect BEFORE restarting the world

Michael reported "a huge chunk error while generating the island" on the 600
block ideal house island. What the logs could still tell me afterwards:

- `client-chat.log` APPENDS across sessions, so the whole run survived:
  build started 00:51:54, `Loading 357 chunk column(s)`, island complete
  01:02:36. Eleven minutes.
- No `WARNING: N chunk column(s) never loaded`. That guard is real (the
  pre-load covers the FULL job bounds, `MinX .. MinX+W-1`, which includes the
  offshore ring, not just the island footprint), so every column the build
  wrote into was loaded. Bulk writes into unloaded chunks being silently lost
  was NOT the cause.
- No exceptions anywhere.

What the logs could NOT tell me: `server-debug.log` and `server-main.log` are
TRUNCATED on restart, and he restarted at 01:03, a minute after the build
finished. The session that contains the evidence is gone. **When something
looks wrong after a generate, copy `%APPDATA%\VintagestoryData\Logs\` before
relaunching.** Only the chat log survives on its own.

The one suspicious event, unproven: `dynamicvillages` founded a village
("Jesion") at 00:55:21, three minutes into the build. There is a plausible
mechanism worth checking if this recurs. Force-loading 357 chunk columns makes
those columns GENERATE, and structure placers (villages, betterruins) run
during chunk generation, on the pre-island seabed. Their deferred features can
then land on or beside terrain the island has already written. The bigger the
island, the more fresh columns the pre-load triggers at once, so a 600 wide
island is the worst case by a wide margin.

Practical mitigation until it is understood: fly the area first so its chunks
are already generated, THEN run `/genisland`. The pre-load has nothing left to
trigger, and the chat line tells you it worked ("Loading N chunk column(s)"
with a small N, or no line at all).

## A big pond was a flat box: the bed ramp was a fixed 4 blocks (fixed 0.57.0)

`pond=N` carves `depth = 1 + round((N-1) * Smooth(dEdge / 4.0))`, and
`PondEdgeDist` searched a 6 cell window. Both constants suit a farm pond and
neither scales: at three blocks per cell the ramp saturates 4 blocks in from
the reeds and the window cannot see further than 18 blocks, so a 70 block mere
came out as a flat-bottomed box with a lip, which is exactly what the code's
own comment says it is avoiding.

Fixed with a per-region `pondslope=` (default 4, so every existing pond is
untouched) that sets the ramp length in blocks, and a search window sized from
it. `pond=30 pondslope=16` now measures 29 deep in the middle and slopes the
whole way in.

The second half of the same bug: the bed was pinned at `SeaLevel - 2`. A lake
whose rim sits 9 above the water line could therefore never be more than 11
deep no matter what you asked for, and it failed SILENTLY: you get a shallow
pond and no note. The bed may now sink into the island's own rock, floored at
`SeaLevel - MaxDepth + 8` so it cannot punch through into the sea.

The lesson to carry: a constant that reads as a sensible default at one scale
is a silent cap at another. Both of these were written for ponds a few cells
wide and neither announced itself when asked for something ten times bigger.
