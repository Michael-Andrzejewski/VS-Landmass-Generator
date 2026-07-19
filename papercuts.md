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
