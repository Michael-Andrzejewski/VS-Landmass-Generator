# Papercuts

Gotchas that cost real debugging time while building this mod. Read before
touching the generator; append when you hit a new one. General know-how goes
in [tips.md](tips.md).

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
- **A resolver failing quietly downgrades a feature to "missing".** Every
  optional feature here reports resolution failures via the problems list;
  keep that pattern for anything new, and treat any problem line in chat as a
  bug to fix, not a warning to ignore.

## Terrain-shaping lessons

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
