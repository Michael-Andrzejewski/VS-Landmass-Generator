using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace LandmassGenerator;

/// <summary>
/// Adds /genisland, a procedural landmass generator for Vintage Story.
///
/// A whole island is far more blocks than a normal fill can touch in one go, so
/// this never runs as a single burst. It force-loads the island's chunks, then
/// places terrain column by column across many game ticks, committing one bulk
/// batch per tick, so even a 500-block island does not stall the server thread.
///
/// Two ways to shape it:
///
///   /genisland diameter=200 beachdir=s cliffdir=n
///       A radial dome: simplex-perturbed coastline, a gentle beach on one
///       compass side and a steep cliff on the other.
///
///   /genisland shape=ideal_island diameter=400
///       A drawn island. Reads a shape file (an ASCII map plus a legend of
///       regions) and builds exactly that outline, giving each region its own
///       rock, surface, ore, forest and shore steepness. Height comes from a
///       distance-to-coast field, so the interior rises and the shore tapers,
///       and the coastline is jittered by noise so the grid never shows.
///
/// Ore is placed deliberately: the game's ore pass only runs during natural
/// worldgen, so stone we place is otherwise completely barren.
/// </summary>
public class LandmassGeneratorModSystem : ModSystem
{
    private ICoreServerAPI sapi;
    private string shapeFolder;

    private IslandJob _islandJob;
    private long _islandListenerId;
    private bool _islandBusy;

    // Plan islands rendered during chunk generation (Rustfall / pure-ocean
    // plan worlds). Built once at worldgen init, read by the chunk-gen pass.
    private List<IslandJob> _wgIslandJobs;

    // Late enough that every registration lands AFTER the vanilla worldgen
    // systems': the map-region flatten needs GenMaps' maps to exist, and the
    // worldgen island pass must run after vanilla vegetation (0.5) but
    // before worldgen lighting (GenLightSurvival, 0.95) computes sunlight.
    public override double ExecuteOrder() => 0.93;

    // Graded ore minerals the game ships (worldproperties/block/ore-graded).
    private static readonly string[] OreMinerals =
    {
        // Graded minerals: blocks are ore-{grade}-{mineral}-{rock}.
        "nativecopper", "limonite", "galena", "cassiterite", "chromite", "ilmenite",
        "sphalerite", "bismuthinite", "magnetite", "hematite", "malachite",
        "pentlandite", "uranium", "wolframite", "rhodochrosite",
        "quartz_nativegold", "quartz_nativesilver", "galena_nativesilver",
        // Ungraded minerals: one block, ore-{mineral}-{rock} (no grade segment).
        "lignite", "bituminouscoal", "anthracite", "quartz", "olivine", "sulfur",
        "alum", "borax", "cinnabar", "fluorite", "graphite", "kernite",
        "phosphorite", "lapislazuli", "corundum", "sylvite"
    };

    // A friendly metal name maps to the minerals that carry it, in preference
    // order. Which one actually exists depends on the host rock (there is no
    // limonite in granite, for instance, but there is hematite), so we try each
    // candidate and keep the first that occurs in this island's stone.
    private static readonly Dictionary<string, string[]> OreAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "copper", new[] { "nativecopper", "malachite" } },
        { "iron", new[] { "limonite", "hematite", "magnetite" } },
        { "tin", new[] { "cassiterite" } },
        { "zinc", new[] { "sphalerite" } },
        { "lead", new[] { "galena" } },
        { "silver", new[] { "galena_nativesilver", "quartz_nativesilver", "galena" } },
        { "gold", new[] { "quartz_nativegold" } },
        { "coal", new[] { "bituminouscoal", "lignite", "anthracite" } },
        { "nickel", new[] { "pentlandite" } },
        { "chromium", new[] { "chromite" } },
        { "chrome", new[] { "chromite" } },
        { "titanium", new[] { "ilmenite" } },
        { "tungsten", new[] { "wolframite" } },
        { "bismuth", new[] { "bismuthinite" } },
        { "manganese", new[] { "rhodochrosite" } }
    };

    // ─────────────────────────────────────────────────────────────────────
    //  Client side: live climate-tint refresh
    //
    //  Grass and leaf tint comes from the map region's ClimateMap, but the
    //  engine caches a pre-lerped copy per region for chunk tesselation
    //  (ClientWorldMap.LerpedClimateMaps) and NEVER invalidates it, so a
    //  server-side climate= edit only became visible after a relog. Whenever a
    //  map region (re)arrives from the server, drop that cache; the chunk
    //  columns the server resends right after are then meshed with the fresh
    //  climate. Engine internals via reflection: if a game update moves them,
    //  we silently fall back to relog-to-see-it.
    // ─────────────────────────────────────────────────────────────────────

    private ICoreClientAPI capi;
    private object clientWorldMap;
    private FieldInfo lerpedMapsField, lerpedLockField;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        api.Event.MapRegionLoaded += OnClientMapRegionLoaded;
    }

    private void OnClientMapRegionLoaded(Vec2i coord, IMapRegion region)
    {
        try
        {
            if (clientWorldMap == null)
            {
                clientWorldMap = capi.World.GetType().GetField("WorldMap")?.GetValue(capi.World);
                if (clientWorldMap == null) return;
                lerpedMapsField = clientWorldMap.GetType().GetField("LerpedClimateMaps", BindingFlags.NonPublic | BindingFlags.Instance);
                lerpedLockField = clientWorldMap.GetType().GetField("LerpedClimateMapsLock", BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (lerpedMapsField == null || lerpedLockField == null) return;

            // Swap in a fresh (empty) cache of the same type. It only ever
            // holds ~10 small maps, rebuilt lazily off-thread, so clearing on
            // every region load is cheap.
            object lockObj = lerpedLockField.GetValue(clientWorldMap);
            lock (lockObj)
            {
                lerpedMapsField.SetValue(clientWorldMap, Activator.CreateInstance(lerpedMapsField.FieldType, 10));
            }
        }
        catch
        {
            // Engine internals moved; climate retints then need a relog.
            lerpedMapsField = null;
            lerpedLockField = null;
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        shapeFolder = Path.Combine(GamePaths.DataPath, "LandmassGenerator");
        try { Directory.CreateDirectory(shapeFolder); } catch { /* best effort */ }

        var p = api.ChatCommands.Parsers;

        RegisterCmd(api, "genisland", name =>
            api.ChatCommands.Create(name)
                .WithDescription("Generate a procedural island around you, spread across ticks so it does not freeze the server. Radial: /genisland diameter=200 beachdir=s cliffdir=n ores=copper:rich forest=0.02. Drawn: /genisland shape=ideal_island diameter=400. /genisland shapes lists shape files.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalAll("options"))
                .HandleWith(OnGenIsland));

        RegisterCmd(api, "genstoryloc", name =>
            api.ChatCommands.Create(name)
                .WithDescription("Pin a vanilla story location at your position and regenerate the area so it appears now, without a world restart. Codes: resonancearchive, lazaret, village, devastationarea, tobiascave, treasurehunter. Optional chunk range overrides how far chunks are deleted for regeneration.")
                .RequiresPrivilege(Privilege.controlserver)
                .RequiresPlayer()
                .WithArgs(p.Word("code"), p.OptionalInt("range"))
                .HandleWith(OnGenStoryLoc));

        RegisterCmd(api, "genworldsetup", name =>
            api.ChatCommands.Create(name)
                .WithDescription("Set up a whole story world from a plan file in one go: optional pure ocean, pin every story location at planned coordinates, and pregenerate their areas with chat progress. Runs the default plan file worldplan.txt from the LandmassGenerator folder, or pass another plan name. A missing plan file is created with the Rustfall defaults and applied.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalWord("plan"))
                .HandleWith(OnGenWorldSetup));

        // The 'Rustfall world' checkbox on the world creation screen (see
        // worldconfig.json at the mod root) lands in the world config; run
        // the world setup once, as early as possible. RunGame fires while
        // the singleplayer client is still connecting, so the setup is
        // already underway before the player is fully in the world.
        // PlayerJoin stays as a fallback for worlds loaded before this.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, TryAutoRustfallSetup);
        api.Event.PlayerJoin += _ => TryAutoRustfallSetup();

        // For checkbox worlds, force the ocean config BEFORE worldgen ever
        // reads it (SaveGameLoaded fires ahead of InitWorldGenerator). The
        // first spawn chunks then already generate as pure ocean, instead
        // of a continent that the later setup only partially wipes, which
        // left square seams between old-settings and new-settings chunks.
        api.Event.SaveGameLoaded += () =>
        {
            var wc = sapi.WorldManager.SaveGame.WorldConfiguration;
            if (!wc.GetBool("rustfallWorld", false)) return;
            wc.SetString("landcover", "0");
            wc.SetString("upheavelCommonness", "0");
            wc.SetBool("lgPureOcean", true);
            // New players must land exactly on the starter island at map
            // center. The vanilla default scatters first spawns up to
            // spawnRadius blocks around it, which here is open ocean.
            wc.SetString("spawnRadius", "0");
        };

        // Rustfall / pure-ocean plan worlds: also render the plan's islands
        // DURING chunk generation, so the starter island's terrain exists
        // the moment its chunks are born. Vanilla holds a joining player at
        // the loading screen until the spawn chunk column is generated, so
        // on a brand-new world the player materializes standing on the
        // island, never swimming while it builds. The live tick builder
        // then only adds decoration, with the same deterministic seed.
        api.Event.InitWorldGenerator(InitWorldgenIslandJobs, "standard");
        api.Event.ChunkColumnGeneration(OnChunkColumnGenIslands, EnumWorldGenPass.Vegetation, "standard");

        // Pure ocean, part 2. landcover 0 stops the continent roll and
        // upheavelCommonness 0 stops raised seafloors, but GenTerra only
        // SHIFTS the landform terrain down by oceanicity (~85 blocks at
        // map height 256), so tall landforms rolled under the ocean still
        // breach the surface as small random islands. In pure-ocean worlds
        // flatten every landform cell to veryflat (except around story
        // locations) and zero the upheaval map as each region generates.
        // Registered after vanilla GenMaps' own handler (mod load order),
        // so the maps exist and story landform forcing already ran.
        api.Event.MapRegionGeneration(OnMapRegionGenPureOcean, "standard");

        api.Logger.Notification($"[landmassgenerator] Ready. Shape files go in: {shapeFolder}");
    }

    private int _pureOceanLandformIndex = int.MinValue;

    private void OnMapRegionGenPureOcean(IMapRegion mapRegion, int regionX, int regionZ, ITreeAttribute chunkGenParams = null)
    {
        var worldConfig = sapi.WorldManager.SaveGame.WorldConfiguration;
        if (!worldConfig.GetBool("lgPureOcean", false)) return;
        // 'Natural world-gen islands' checkbox (default on): let underwater
        // landforms occasionally breach as small natural islands. When on,
        // only the spawn clear zone is flattened, so the starter island is
        // always surrounded by open ocean; when off, everything is.
        bool naturalIslands = worldConfig.GetBool("rustfallNaturalIslands", true);
        int spawnMidX = sapi.WorldManager.MapSizeX / 2;
        int spawnMidZ = sapi.WorldManager.MapSizeZ / 2;
        const long SpawnClearRadiusSq = 640L * 640L;
        if (naturalIslands)
        {
            // Skip regions entirely outside the spawn clear zone.
            int regionSizeBlocks = sapi.WorldManager.RegionSize;
            long rdx = Math.Max(0, Math.Abs((regionX * regionSizeBlocks) + regionSizeBlocks / 2 - spawnMidX) - regionSizeBlocks / 2);
            long rdz = Math.Max(0, Math.Abs((regionZ * regionSizeBlocks) + regionSizeBlocks / 2 - spawnMidZ) - regionSizeBlocks / 2);
            if (rdx * rdx + rdz * rdz > SpawnClearRadiusSq) return;
        }
        var lfMap = mapRegion.LandformMap;
        if (lfMap?.Data == null || lfMap.Data.Length == 0) return;

        if (_pureOceanLandformIndex == int.MinValue)
        {
            _pureOceanLandformIndex = -1;
            var byIndex = Vintagestory.ServerMods.NoiseLandforms.landforms?.LandFormsByIndex;
            if (byIndex != null)
            {
                for (int i = 0; i < byIndex.Length; i++)
                {
                    if (byIndex[i].Code.Path == "veryflat") { _pureOceanLandformIndex = i; break; }
                }
            }
            if (_pureOceanLandformIndex < 0)
            {
                sapi.Logger.Warning("[landmassgenerator] Pure ocean: landform 'veryflat' not found; underwater landforms stay vanilla and may breach the surface.");
            }
        }
        if (_pureOceanLandformIndex < 0) return;

        // Story locations keep their forced terrain. Their landform forcing
        // wobbles cell positions by up to ~80 blocks, hence the margin.
        var keeps = new List<(int X, int Z, long RadSq)>();
        var genStory = sapi.ModLoader.GetModSystem<Vintagestory.GameContent.GenStoryStructures>();
        if (genStory?.Structures != null)
        {
            foreach (var pair in genStory.Structures)
            {
                long r = pair.Value.LandformRadius + 128;
                keeps.Add((pair.Value.CenterPos.X, pair.Value.CenterPos.Z, r * r));
            }
        }

        int inner = lfMap.InnerSize;
        int pad = lfMap.TopLeftPadding;
        int size = lfMap.Size;
        int cellSize = sapi.WorldManager.RegionSize / Math.Max(1, inner);
        for (int iz = 0; iz < size; iz++)
        {
            for (int ix = 0; ix < size; ix++)
            {
                int wx = (regionX * inner + ix - pad) * cellSize;
                int wz = (regionZ * inner + iz - pad) * cellSize;
                if (naturalIslands)
                {
                    long sdx = wx - spawnMidX, sdz = wz - spawnMidZ;
                    if (sdx * sdx + sdz * sdz > SpawnClearRadiusSq) continue;
                }
                bool keep = false;
                for (int k = 0; k < keeps.Count; k++)
                {
                    long dx = wx - keeps[k].X, dz = wz - keeps[k].Z;
                    if (dx * dx + dz * dz < keeps[k].RadSq) { keep = true; break; }
                }
                if (!keep) lfMap.Data[iz * size + ix] = _pureOceanLandformIndex;
            }
        }

        // No raised seafloor anywhere either, regardless of what the world
        // creation slider said. Vanilla already zeroes it around story
        // locations; their forced land does not come from upheaval. With
        // natural islands on, upheavelCommonness 0 already keeps it flat.
        if (!naturalIslands)
        {
            var upheavelMap = mapRegion.UpheavelMap;
            if (upheavelMap?.Data != null && upheavelMap.Data.Length > 0)
            {
                Array.Clear(upheavelMap.Data, 0, upheavelMap.Data.Length);
            }
        }
    }

    private void TryAutoRustfallSetup()
    {
        if (!sapi.World.Config.GetBool("rustfallWorld", false)) return;
        if (sapi.WorldManager.SaveGame.GetData<bool>("lgRustfallSetupDone", false)) return;
        sapi.WorldManager.SaveGame.StoreData("lgRustfallSetupDone", true);

        // Run synchronously, right here. On a fresh world this fires during
        // the RunGame phase transition: the tick loop has not started and no
        // player can join until this returns, so the story pinning AND the
        // blocking island decoration all happen behind the loading screen.
        // Console-privileged caller: the wgen subcommands need controlserver.
        sapi.Logger.Notification("[genworldsetup] Rustfall world detected, running first-time setup...");
        var caller = new Caller
        {
            Type = EnumCallerType.Console,
            CallerPrivileges = new[] { "*" },
            FromChatGroupId = GlobalConstants.GeneralChatGroup
        };
        TextCommandResult result = RunWorldSetup(caller, "worldplan", auto: true);
        sapi.Logger.Notification("[genworldsetup] " + (result.StatusMessage ?? "done"));
        sapi.BroadcastMessageToAllGroups("[genworldsetup] " + (result.StatusMessage ?? "done"), EnumChatType.Notification);
    }

    private void RegisterCmd(ICoreServerAPI api, string name, Action<string> build)
    {
        try
        {
            build(name);
        }
        catch (Exception)
        {
            string alt = "lg" + name;
            try
            {
                build(alt);
                api.Logger.Warning($"[landmassgenerator] Command /{name} is already taken; registered it as /{alt} instead.");
            }
            catch (Exception e2)
            {
                api.Logger.Error($"[landmassgenerator] Could not register /{name} or /{alt}: {e2.Message}");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  /genstoryloc: place a vanilla story location mid-session
    //
    //  Vanilla already has all the pieces: /wgen story setpos registers the
    //  location and forces land + landform + climate into the map, and
    //  /wgen delr deletes chunks AND their map regions so the next
    //  generation pass rebuilds everything, structure included. The one
    //  missing piece is the devastation: GenDevastationLayer, Timeswitch
    //  and ModSystemDevastationEffects each snapshot the devastationarea
    //  location ONCE at world load, so a mid-session setpos leaves them
    //  pointing at the old spot (tower generates, devastated land does
    //  not). This command chains the vanilla commands and re-points those
    //  snapshots in between, so one command does the whole job.
    // ─────────────────────────────────────────────────────────────────────

    private TextCommandResult OnGenStoryLoc(TextCommandCallingArgs args)
    {
        string code = ((string)args[0] ?? "").ToLowerInvariant();

        string lore = sapi.World.Config.GetAsString("loreContent", "true") ?? "true";
        if (lore.Equals("false", StringComparison.OrdinalIgnoreCase) || lore == "0")
        {
            return TextCommandResult.Error("This world was created with lore content disabled, so story structures cannot generate at all.");
        }

        var genStory = sapi.ModLoader.GetModSystem<Vintagestory.GameContent.GenStoryStructures>();
        if (genStory == null)
        {
            return TextCommandResult.Error("GenStoryStructures mod system not found; is the survival mod loaded?");
        }

        var pos = args.Caller.Entity.Pos;
        int x = (int)pos.X, z = (int)pos.Z;

        // 1. Vanilla setpos: writes the location registry entry and forces
        //    land, landform and climate around it. Absolute coordinates so
        //    there is no map-middle ambiguity.
        TextCommandResult sub = null;
        sapi.ChatCommands.ExecuteUnparsed(
            $"/wgen story setpos {code} ={x} =1 ={z} true",
            new TextCommandCallingArgs { Caller = args.Caller },
            r => sub = r);
        if (sub == null || sub.Status != EnumCommandStatus.Success)
        {
            return TextCommandResult.Error($"setpos step failed: {sub?.StatusMessage ?? "no response from /wgen story setpos"}");
        }

        var loc = genStory.Structures.Get(code);
        if (loc == null)
        {
            return TextCommandResult.Error($"setpos reported success but no location was stored for '{code}'.");
        }

        string extra = "";
        if (code == "devastationarea")
        {
            string failed = RepointDevastationSystems(genStory, loc);
            if (failed != null)
            {
                return TextCommandResult.Error(
                    $"Location was pinned, but re-pointing the devastation systems failed ({failed}). " +
                    "A game update likely moved an internal field. Restarting the world instead will pick the location up correctly.");
            }
            extra = " Devastation layer, timeswitch and effects re-pointed.";
        }

        // 2. Vanilla delr: deletes chunk columns and their map regions so
        //    everything (terrain, structure, devastation) regenerates.
        int radius = Math.Max(loc.LandformRadius, loc.GenerationRadius) + 32;
        int range = args.Parsers[1].IsMissing ? Math.Min(50, radius / 32 + 3) : GameMath.Clamp((int)args[1], 1, 50);
        sub = null;
        sapi.ChatCommands.ExecuteUnparsed(
            $"/wgen delr {range}",
            new TextCommandCallingArgs { Caller = args.Caller },
            r => sub = r);
        if (sub == null || sub.Status != EnumCommandStatus.Success)
        {
            return TextCommandResult.Error($"Location pinned{extra} but the chunk regen step failed: {sub?.StatusMessage ?? "no response from /wgen delr"}");
        }

        return TextCommandResult.Success(
            $"{code} pinned at your position; {range * 32} blocks in every direction deleted for regeneration.{extra} " +
            "Move or fly around the area and it regenerates with the structure in place.");
    }

    // The devastation systems cache the story location at world load. After a
    // mid-session setpos, point them at the new location the same way their
    // own InitWorldGen does. Returns null on success, or a description of
    // what could not be updated.
    private string RepointDevastationSystems(Vintagestory.GameContent.GenStoryStructures genStory, Vintagestory.ServerMods.StoryStructureLocation loc)
    {
        var deva = sapi.ModLoader.GetModSystem<Vintagestory.ServerMods.GenDevastationLayer>();
        var timeswitch = sapi.ModLoader.GetModSystem<Vintagestory.GameContent.Timeswitch>();
        var effects = sapi.ModLoader.GetModSystem<Vintagestory.GameContent.ModSystemDevastationEffects>();
        if (deva == null || timeswitch == null || effects == null)
        {
            return "one of the devastation mod systems is missing";
        }

        var locField = deva.GetType().GetField("devastationLocation", BindingFlags.NonPublic | BindingFlags.Instance);
        var dim2Field = deva.GetType().GetField("dim2Size", BindingFlags.NonPublic | BindingFlags.Instance);
        if (locField == null || dim2Field == null)
        {
            return "GenDevastationLayer internals changed";
        }

        locField.SetValue(deva, loc);
        timeswitch.SetPos(loc.CenterPos);
        int dim2Size = timeswitch.SetupDim2TowerGeneration(loc, genStory);
        dim2Field.SetValue(deva, dim2Size);

        effects.DevaLocationPresent = loc.CenterPos.ToVec3d();
        effects.DevaLocationPast = loc.CenterPos.Copy().SetDimension(2).ToVec3d();
        effects.EffectRadius = loc.GenerationRadius;

        // Clients get the location once on join; push the new one so the
        // fog and rift effects move without a relog.
        sapi.Network.GetChannel("devastation").BroadcastPacket(new Vintagestory.GameContent.DevaLocation
        {
            Pos = loc.CenterPos,
            Radius = loc.GenerationRadius
        });
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  /genworldsetup: one command to set up a fresh story world
    //
    //  Reads a plan file (created with the Rustfall defaults if missing)
    //  and applies it in one pass on a brand-new world:
    //
    //    pureocean            landcover and upheavel to 0 in the world
    //                         config, then rebuild the worldgen maps so it
    //                         applies NOW, not after a restart. Land then
    //                         only exists where something forces it.
    //    clearspawn <chunks>  wipe the vanilla forced land patch at map
    //                         center (chunks + map regions) so 0,0 is open
    //                         ocean for a hand-built starter island.
    //    storyloc <code> <x> <z>  pin a story location at map coordinates
    //                         (same numbers the coordinate HUD shows).
    //
    //  After pinning, GenMaps.initWorldGen() is re-run. That is the
    //  restart-equivalent: it rebuilds the ocean generator from the new
    //  config and drops the forced-land entries of the auto-rolled story
    //  locations, and SetupForceLandform re-adds forcing for the saved
    //  (now ours) locations. Finally each story area is pregenerated one
    //  by one with progress reported to chat.
    // ─────────────────────────────────────────────────────────────────────

    private const string DefaultWorldPlan =
@"# World setup plan for /genworldsetup. Lines:
#   pureocean                 no natural land at all (landcover 0, upheavel 0)
#   clearspawn <chunkRange>   wipe the vanilla spawn land patch at map center
#   storyloc <code> <mapX> <mapZ>   pin a story location (HUD coordinates)
#   island <mapX> <mapZ> <genisland options>   build a drawn island (runs
#       before the story pregeneration; shape file must be installed)
# Codes: resonancearchive, lazaret, village, devastationarea, tobiascave, treasurehunter
pureocean
clearspawn 16
island 0 0 shape=starter_island diameter=150 height=8 stone=rock-peridotite sand=sand-peridotite
island 265 0 shape=cattail_isles diameter=210 height=4 water=34
storyloc treasurehunter 2400 -250
storyloc lazaret -1400 -2500
storyloc tobiascave 1500 -8450
storyloc resonancearchive 5550 -3200
storyloc village -6250 -5550
storyloc devastationarea -2550 -8750
";

    private TextCommandResult OnGenWorldSetup(TextCommandCallingArgs args)
    {
        return RunWorldSetup(args.Caller, args.Parsers[0].IsMissing ? "worldplan" : (string)args[0]);
    }

    private TextCommandResult RunWorldSetup(Caller caller, string planName, bool auto = false)
    {
        string planPath = Path.Combine(shapeFolder, planName + ".txt");
        bool freshPlan = false;
        if (!File.Exists(planPath))
        {
            File.WriteAllText(planPath, DefaultWorldPlan);
            freshPlan = true;
        }

        string lore = sapi.World.Config.GetAsString("loreContent", "true") ?? "true";
        if (lore.Equals("false", StringComparison.OrdinalIgnoreCase) || lore == "0")
        {
            return TextCommandResult.Error("This world was created with lore content disabled, so story structures cannot generate at all.");
        }
        var genStory = sapi.ModLoader.GetModSystem<Vintagestory.GameContent.GenStoryStructures>();
        var genMaps = sapi.ModLoader.GetModSystem<Vintagestory.ServerMods.GenMaps>();
        if (genStory == null || genMaps == null)
        {
            return TextCommandResult.Error("GenStoryStructures or GenMaps mod system not found; is the survival mod loaded?");
        }

        // Parse the plan.
        bool pureOcean = false;
        int clearSpawnRange = 0;
        var sites = new List<(string Code, int MapX, int MapZ)>();
        var islands = new List<(int MapX, int MapZ, string Options)>();
        int lineNo = 0;
        foreach (string raw in File.ReadAllLines(planPath))
        {
            lineNo++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToLowerInvariant())
            {
                case "pureocean":
                    pureOcean = true;
                    break;
                case "clearspawn":
                    clearSpawnRange = parts.Length > 1 && int.TryParse(parts[1], out int csr) ? GameMath.Clamp(csr, 1, 50) : 10;
                    break;
                case "storyloc":
                    if (parts.Length < 4 || !int.TryParse(parts[2], out int mx) || !int.TryParse(parts[3], out int mz))
                    {
                        return TextCommandResult.Error($"Plan line {lineNo} is not 'storyloc code x z': {line}");
                    }
                    sites.Add((parts[1].ToLowerInvariant(), mx, mz));
                    break;
                case "island":
                    if (parts.Length < 4 || !int.TryParse(parts[1], out int imx) || !int.TryParse(parts[2], out int imz))
                    {
                        return TextCommandResult.Error($"Plan line {lineNo} is not 'island x z options...': {line}");
                    }
                    islands.Add((imx, imz, string.Join(" ", parts, 3, parts.Length - 3)));
                    break;
                default:
                    return TextCommandResult.Error($"Plan line {lineNo} has unknown directive '{parts[0]}'. Known: pureocean, clearspawn, storyloc, island.");
            }
        }
        if (!pureOcean && clearSpawnRange == 0 && sites.Count == 0 && islands.Count == 0)
        {
            return TextCommandResult.Error($"Plan file {planPath} contains no directives.");
        }

        var notes = new List<string>();
        if (freshPlan) notes.Add($"no plan file existed, wrote and applied the default at {planPath}");

        // Remember which plan this world uses, so the worldgen island
        // renderer reads the same plan after every restart.
        sapi.WorldManager.SaveGame.StoreData("lgWorldPlanName", planName);

        // 1. Pure ocean: from here on, land only exists where forced.
        if (pureOcean)
        {
            var wc = sapi.WorldManager.SaveGame.WorldConfiguration;
            wc.SetString("landcover", "0");
            wc.SetString("upheavelCommonness", "0");
            // Read by OnMapRegionGenPureOcean: flattens underwater landforms
            // in every region generated from here on, so no seamount peaks
            // breach the surface as random islands.
            wc.SetBool("lgPureOcean", true);
            notes.Add("pure ocean on (landcover 0, upheavel 0, underwater landforms flattened)");
        }

        // 2. Pin every story location via vanilla setpos (absolute coords).
        int midX = (int)sapi.World.DefaultSpawnPosition.X;
        int midZ = (int)sapi.World.DefaultSpawnPosition.Z;
        foreach (var site in sites)
        {
            TextCommandResult sub = null;
            sapi.ChatCommands.ExecuteUnparsed(
                $"/wgen story setpos {site.Code} ={midX + site.MapX} =1 ={midZ + site.MapZ} true",
                new TextCommandCallingArgs { Caller = caller },
                r => sub = r);
            if (sub == null || sub.Status != EnumCommandStatus.Success)
            {
                return TextCommandResult.Error($"setpos for {site.Code} failed: {sub?.StatusMessage ?? "no response"}. Locations pinned so far are kept.");
            }
        }

        // 3. Restart-equivalent rebuild: new ocean config takes effect and
        //    the auto-rolled locations' forced-land entries are dropped.
        genMaps.initWorldGen();
        if (pureOcean)
        {
            // Public field; the rebuilt ocean generator holds a reference to
            // this same list, so clearing it drops the vanilla spawn land.
            genMaps.requireLandAt.Clear();
        }
        var setupForce = genStory.GetType().GetMethod("SetupForceLandform", BindingFlags.NonPublic | BindingFlags.Instance);
        if (setupForce == null)
        {
            return TextCommandResult.Error("GenStoryStructures.SetupForceLandform not found (game update changed internals). Locations are pinned; restart the world instead, then fly to each site.");
        }
        setupForce.Invoke(genStory, null);

        // 4. Devastation systems snapshot their location at load; re-point.
        if (sites.Exists(s => s.Code == "devastationarea"))
        {
            var loc = genStory.Structures.Get("devastationarea");
            string failed = loc == null ? "location missing after setpos" : RepointDevastationSystems(genStory, loc);
            if (failed != null) notes.Add($"WARNING: devastation systems not re-pointed ({failed}); restart the world before generating that area");
        }

        // 5. Wipe the vanilla spawn land patch so 0,0 regenerates as ocean.
        if (clearSpawnRange > 0)
        {
            int ccx = sapi.WorldManager.MapSizeX / 2 / 32;
            int ccz = sapi.WorldManager.MapSizeZ / 2 / 32;
            for (int cx = ccx - clearSpawnRange; cx <= ccx + clearSpawnRange; cx++)
            {
                for (int cz = ccz - clearSpawnRange; cz <= ccz + clearSpawnRange; cz++)
                {
                    // Keep a small core under the player. Deleting the ground
                    // they stand on makes their client re-request those
                    // columns every tick while worldgen retires them in
                    // bursts, which is the perfect storm for a (vanilla)
                    // request/retire race that kills the server. 3x3 chunks
                    // (96 blocks) so a 150-wide starter island built at 0,0
                    // fully covers it.
                    if (Math.Abs(cx - ccx) <= 1 && Math.Abs(cz - ccz) <= 1) continue;
                    // On the automatic first-run setup every chunk was born
                    // AFTER the ocean config applied, and island chunks were
                    // born WITH their island terrain (worldgen renderer);
                    // deleting those would only make the island vanish and
                    // rebuild in front of the player.
                    if (auto && ChunkTouchesWorldgenIsland(cx, cz)) continue;
                    sapi.WorldManager.DeleteChunkColumn(cx, cz);
                }
            }
            int regionChunks = sapi.WorldManager.RegionSize / 32;
            for (int rx = (ccx - clearSpawnRange) / regionChunks; rx <= (ccx + clearSpawnRange) / regionChunks; rx++)
            {
                for (int rz = (ccz - clearSpawnRange) / regionChunks; rz <= (ccz + clearSpawnRange) / regionChunks; rz++)
                {
                    sapi.WorldManager.DeleteMapRegion(rx, rz);
                }
            }
            notes.Add($"spawn area wiped {clearSpawnRange * 32} blocks around map center; it regenerates as open ocean while you stand there");
        }

        // 6. Pregenerate each story area, one at a time, smallest first is
        //    however the plan orders them. Progress goes to chat.
        var queue = new List<(string Code, int Cx1, int Cz1, int Cx2, int Cz2)>();
        foreach (var site in sites)
        {
            var loc = genStory.Structures.Get(site.Code);
            if (loc == null) continue;
            int radius = Math.Max(loc.LandformRadius, loc.GenerationRadius) + 64;
            queue.Add((site.Code,
                (loc.CenterPos.X - radius) / 32, (loc.CenterPos.Z - radius) / 32,
                (loc.CenterPos.X + radius) / 32, (loc.CenterPos.Z + radius) / 32));
        }
        // On the automatic first-run setup we are still inside the RunGame
        // phase transition: no tick loop, no connected players, and the
        // spawn chunks are blocking-loaded. Decorate the worldgen-rendered
        // islands RIGHT NOW, synchronously, so the world opens with trees,
        // flora, ores and caves already in place. Islands whose chunks are
        // not loaded (or any run with players online) fall back to the
        // paced live pass below.
        if (auto && _wgIslandJobs != null)
        {
            var deferred = new List<(int MapX, int MapZ, string Options)>();
            foreach (var isl in islands)
            {
                // No online-players guard here: on the auto first-run the
                // tick loop has not started, so even a technically
                // connected singleplayer client is still on the loading
                // screen. Blocking is exactly what we want.
                var opt = ParseIslandOptions(isl.Options);
                if (!opt.ContainsKey("seed")) opt["seed"] = PlanIslandSeed(isl.MapX, isl.MapZ).ToString();
                int iox = sapi.WorldManager.MapSizeX / 2 + isl.MapX;
                int ioz = sapi.WorldManager.MapSizeZ / 2 + isl.MapZ;
                IslandJob job = BuildIslandJob(opt, iox, ioz, 0, new List<string>(), out string jerr);
                if (job == null)
                {
                    sapi.Logger.Warning("[genworldsetup] Island at {0}, {1}: {2}; decoration deferred to the live pass.", isl.MapX, isl.MapZ, jerr);
                    deferred.Add(isl);
                }
                else if (!DecorationChunksLoaded(job))
                {
                    sapi.Logger.Notification("[genworldsetup] Island at {0}, {1}: chunks not pre-loaded, decoration deferred to the live pass.", isl.MapX, isl.MapZ);
                    deferred.Add(isl);
                }
                else
                {
                    RunIslandDecorationBlocking(job, isl.MapX, isl.MapZ);
                }
            }
            if (deferred.Count < islands.Count)
            {
                notes.Add($"{islands.Count - deferred.Count} island(s) decorated before world open");
            }
            islands = deferred;
        }

        // Let the spawn-area churn settle, then: regenerate the wiped spawn
        // ocean (pushing fresh chunks to clients), build the plan's islands
        // (starter island first, so the player has ground fast), then
        // pregenerate the story areas. Everything spaced out; simultaneous
        // request and retire bursts on the chunk queue can trip a fatal
        // race inside the vanilla request index.
        if (clearSpawnRange > 0)
        {
            int rccx = sapi.WorldManager.MapSizeX / 2 / 32;
            int rccz = sapi.WorldManager.MapSizeZ / 2 / 32;
            if (auto)
            {
                // Auto setup: the islands' terrain is already worldgen-made,
                // so decorate them FIRST (fast, and usually finished while
                // the client is still on the loading screen), then sweep the
                // wiped ocean, then pregenerate the story areas.
                sapi.Event.RegisterCallback(_ => RunNextPlanIsland(islands, 0, queue, caller, auto,
                    then: () => RegenSpawnBand(rccx, rccz, clearSpawnRange, rccz - clearSpawnRange,
                        new List<(int MapX, int MapZ, string Options)>(), queue, caller, auto)), 1500);
            }
            else
            {
                // Manual retrofit: islands do a full terrain build, so the
                // wiped seabed regenerates before they root on it.
                sapi.Event.RegisterCallback(_ => RegenSpawnBand(rccx, rccz, clearSpawnRange, rccz - clearSpawnRange, islands, queue, caller, auto), 4000);
            }
        }
        else if (islands.Count > 0 || queue.Count > 0)
        {
            sapi.Event.RegisterCallback(_ => RunNextPlanIsland(islands, 0, queue, caller, auto), 4000);
        }

        string summary = $"World setup started: {sites.Count} story location(s) pinned"
            + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "")
            + (islands.Count > 0 ? $"; {islands.Count} island(s) to build" : "")
            + (queue.Count > 0 ? ". Progress follows in chat; the devastation takes the longest." : ".");
        return TextCommandResult.Success(summary);
    }

    // After clearspawn's deletions, actively regenerate the wiped square and
    // push the fresh chunks to connected clients. Vanilla only regenerates
    // deleted columns lazily, and clients keep rendering the stale terrain
    // they downloaded before the wipe, which reads as chunks refusing to
    // generate until a relog.
    private void RegenSpawnBand(int ccx, int ccz, int range, int bandCz1, List<(int MapX, int MapZ, string Options)> islands, List<(string Code, int Cx1, int Cz1, int Cx2, int Cz2)> pregenQueue, Caller caller, bool auto = false)
    {
        if (bandCz1 > ccz + range)
        {
            sapi.BroadcastMessageToAllGroups("[genworldsetup] Spawn ocean regenerated.", EnumChatType.Notification);
            sapi.Event.RegisterCallback(_ => RunNextPlanIsland(islands, 0, pregenQueue, caller, auto), 2000);
            return;
        }
        int width = 2 * range + 1;
        int rowsPerBand = Math.Max(1, PregenBatchColumns / width);
        int bandCz2 = Math.Min(ccz + range, bandCz1 + rowsPerBand - 1);
        int chunksY = sapi.WorldManager.MapSizeY / 32;
        try
        {
            sapi.WorldManager.LoadChunkColumnPriority(ccx - range, bandCz1, ccx + range, bandCz2, new ChunkLoadOptions
            {
                KeepLoaded = false,
                OnLoaded = () =>
                {
                    for (int cx = ccx - range; cx <= ccx + range; cx++)
                    {
                        for (int cz = bandCz1; cz <= bandCz2; cz++)
                        {
                            for (int cy = 0; cy < chunksY; cy++) sapi.WorldManager.BroadcastChunk(cx, cy, cz, onlyIfInRange: true);
                            sapi.WorldManager.ResendMapChunk(cx, cz, onlyIfInRange: true);
                        }
                    }
                    sapi.Event.RegisterCallback(_ => RegenSpawnBand(ccx, ccz, range, bandCz2 + 1, islands, pregenQueue, caller, auto), 750);
                }
            });
        }
        catch (Exception e)
        {
            sapi.Logger.Error("[genworldsetup] Spawn regen band {0}-{1} failed: {2}", bandCz1, bandCz2, e);
            sapi.Event.RegisterCallback(_ => RegenSpawnBand(ccx, ccz, range, bandCz1, islands, pregenQueue, caller, auto), 5000);
        }
    }

    private void RunNextPlanIsland(List<(int MapX, int MapZ, string Options)> islands, int idx, List<(string Code, int Cx1, int Cz1, int Cx2, int Cz2)> pregenQueue, Caller caller, bool auto = false, Action then = null)
    {
        if (idx >= islands.Count)
        {
            if (then != null) { then(); return; }
            if (pregenQueue.Count > 0) sapi.Event.RegisterCallback(_ => PregenNextStoryArea(pregenQueue, 0), 2000);
            else sapi.BroadcastMessageToAllGroups("[genworldsetup] World setup finished.", EnumChatType.Notification);
            return;
        }
        var isl = islands[idx];
        string opts = isl.Options;
        // Deterministic seed: the exact one the worldgen renderer derived,
        // so the live pass lands on identical terrain instead of reshaping
        // the island under the player.
        if (opts.IndexOf("seed=", StringComparison.OrdinalIgnoreCase) < 0)
            opts += " seed=" + PlanIslandSeed(isl.MapX, isl.MapZ);
        // Worldgen already rendered the terrain on the automatic first-run
        // setup; the live pass only decorates (trees, flora, ores, caves).
        if (auto && _wgIslandJobs != null && opts.IndexOf("skipterrain", StringComparison.OrdinalIgnoreCase) < 0)
            opts += " skipterrain=1";
        sapi.BroadcastMessageToAllGroups($"[genworldsetup] Building island {idx + 1} of {islands.Count} at {isl.MapX}, {isl.MapZ}...", EnumChatType.Notification);
        TextCommandResult sub = null;
        sapi.ChatCommands.ExecuteUnparsed($"/genisland {opts} x={isl.MapX} z={isl.MapZ}",
            new TextCommandCallingArgs { Caller = caller }, r => sub = r);
        if (sub == null || sub.Status != EnumCommandStatus.Success)
        {
            sapi.BroadcastMessageToAllGroups($"[genworldsetup] Island at {isl.MapX}, {isl.MapZ} failed to start ({sub?.StatusMessage ?? "no response"}); skipping it.", EnumChatType.Notification);
            sapi.Event.RegisterCallback(_ => RunNextPlanIsland(islands, idx + 1, pregenQueue, caller, auto, then), 2000);
            return;
        }
        WaitForIslandThenContinue(islands, idx, pregenQueue, caller, auto, then);
    }

    private void WaitForIslandThenContinue(List<(int MapX, int MapZ, string Options)> islands, int idx, List<(string Code, int Cx1, int Cz1, int Cx2, int Cz2)> pregenQueue, Caller caller, bool auto, Action then = null)
    {
        sapi.Event.RegisterCallback(_ =>
        {
            if (_islandBusy)
            {
                WaitForIslandThenContinue(islands, idx, pregenQueue, caller, auto, then);
                return;
            }
            sapi.BroadcastMessageToAllGroups($"[genworldsetup] Island at {islands[idx].MapX}, {islands[idx].MapZ} finished.", EnumChatType.Notification);
            sapi.Event.RegisterCallback(__ => RunNextPlanIsland(islands, idx + 1, pregenQueue, caller, auto, then), 2000);
        }, 2000);
    }

    // The chunk rect decoration actually touches: the island's land plus a
    // margin for tree crowns. The offshore ring needs no decoration, so it
    // does not matter that it pokes past the blocking-loaded spawn area.
    private void GetDecorationChunkRect(IslandJob job, out int cx1, out int cz1, out int cx2, out int cz2)
    {
        int cs = GlobalConstants.ChunkSize;
        if (job.Shape == null)
        {
            double half = job.R * 1.12 + 24;
            cx1 = FloorDiv(job.Cx - (int)half, cs); cx2 = FloorDiv(job.Cx + (int)half, cs);
            cz1 = FloorDiv(job.Cz - (int)half, cs); cz2 = FloorDiv(job.Cz + (int)half, cs);
            return;
        }

        // The land's real bounding box, not the whole grid: a narrow chain
        // drawn across a wide grid would otherwise demand spawn chunk
        // pregeneration far beyond anything it decorates. The four corners
        // go through the same rotation the markers use.
        var s = job.Shape;
        double minX = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxZ = double.MinValue;
        foreach ((int gx, int gz) in new[] { (s.LandGx1, s.LandGz1), (s.LandGx2, s.LandGz1), (s.LandGx1, s.LandGz2), (s.LandGx2, s.LandGz2) })
        {
            double lx = (gx + 0.5 - s.W / 2.0) * job.WorldPerCell;
            double lz = (gz + 0.5 - s.H / 2.0) * job.WorldPerCell;
            double wx = job.Cx + lx * job.RotCos - lz * job.RotSin;
            double wz = job.Cz + lx * job.RotSin + lz * job.RotCos;
            minX = Math.Min(minX, wx); maxX = Math.Max(maxX, wx);
            minZ = Math.Min(minZ, wz); maxZ = Math.Max(maxZ, wz);
        }
        const int margin = 24; // tree crowns + shore taper
        cx1 = FloorDiv((int)(minX - margin), cs); cx2 = FloorDiv((int)(maxX + margin), cs);
        cz1 = FloorDiv((int)(minZ - margin), cs); cz2 = FloorDiv((int)(maxZ + margin), cs);
    }

    private bool DecorationChunksLoaded(IslandJob job)
    {
        GetDecorationChunkRect(job, out int cx1, out int cz1, out int cx2, out int cz2);
        for (int cx = cx1; cx <= cx2; cx++)
        {
            for (int cz = cz1; cz <= cz2; cz++)
            {
                if (sapi.WorldManager.GetChunk(cx, 0, cz) == null) return false;
            }
        }
        return true;
    }

    // The live decorator's plant loop and finish passes in one synchronous
    // burst, for terrain the worldgen renderer already made. This blocks
    // the server main thread for a few seconds, so it is only used during
    // the RunGame phase transition, with no players connected: the player
    // is still on the loading screen and the world opens fully dressed.
    private void RunIslandDecorationBlocking(IslandJob job, int mapX, int mapZ)
    {
        _islandBusy = true;
        try
        {
            if (HasForest(job) || HasFlora(job))
            {
                var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
                var pos = new BlockPos(0, 0, 0, job.Dim);
                for (long i = 0; i < job.Total; i++)
                {
                    int x = job.MinX + (int)(i % job.W);
                    int z = job.MinZ + (int)(i / job.W);
                    PlantColumn(job, ba, pos, x, z);
                }
                ba.Commit();
            }
            string extra = PlaceLandmarkTrees(job);
            extra += PlaceMarkerBlocks(job);
            StampOreBitClusters(job);
            StampDevastation(job);
            StampPumpkinPatches(job);
            string caveNote = CarveCaves(job);
            StampClimate(job);
            string depositNote = SyncHeightmapsAndDeposits(job);
            sapi.Logger.Notification("[genworldsetup] Island at {0}, {1} decorated before world open: {2} tree(s), {3} plant(s){4}{5}. {6}",
                mapX, mapZ, job.Trees, job.Plants, caveNote, depositNote, extra);
        }
        catch (Exception e)
        {
            sapi.Logger.Error("[genworldsetup] Blocking decoration for the island at {0}, {1} failed; its terrain stands, decoration may be partial:", mapX, mapZ);
            sapi.Logger.Error(e);
        }
        finally
        {
            _islandBusy = false;
        }
    }

    // The server's chunk request fifo holds 2000 entries
    // (MagicNum.RequestChunkColumnsQueueSize) and OVERFLOWING IT KILLS THE
    // SERVER, so a site is never requested in one go (the devastation alone
    // is ~3000 columns). Each site is sliced into bands of at most this many
    // columns, chained via OnLoaded, leaving plenty of queue headroom for
    // the players' own chunk loading.
    private const int PregenBatchColumns = 256;

    private void PregenNextStoryArea(List<(string Code, int Cx1, int Cz1, int Cx2, int Cz2)> queue, int idx)
    {
        if (idx >= queue.Count)
        {
            sapi.BroadcastMessageToAllGroups("[genworldsetup] All story areas are generated. Teleport with /wgen story tp code, e.g. /wgen story tp devastationarea.", EnumChatType.Notification);
            return;
        }
        var s = queue[idx];
        int width = s.Cx2 - s.Cx1 + 1;
        int cols = width * (s.Cz2 - s.Cz1 + 1);
        int rowsPerBand = Math.Max(1, PregenBatchColumns / width);
        sapi.BroadcastMessageToAllGroups($"[genworldsetup] Generating {s.Code} area ({idx + 1} of {queue.Count}, {cols} chunk columns)...", EnumChatType.Notification);
        PregenNextBand(queue, idx, s.Cz1, rowsPerBand);
    }

    private void PregenNextBand(List<(string Code, int Cx1, int Cz1, int Cx2, int Cz2)> queue, int idx, int bandCz1, int rowsPerBand)
    {
        var s = queue[idx];
        if (bandCz1 > s.Cz2)
        {
            sapi.BroadcastMessageToAllGroups($"[genworldsetup] {s.Code} area generated.", EnumChatType.Notification);
            sapi.Event.RegisterCallback(_ => PregenNextStoryArea(queue, idx + 1), 2000);
            return;
        }
        int bandCz2 = Math.Min(s.Cz2, bandCz1 + rowsPerBand - 1);
        try
        {
            sapi.WorldManager.LoadChunkColumnPriority(s.Cx1, bandCz1, s.Cx2, bandCz2, new ChunkLoadOptions
            {
                KeepLoaded = false,
                // The cooldown before the next request lets this band's
                // requests retire first. Requesting a column in the same
                // instant its previous request is retired can kill the
                // server (vanilla GetOrAdd/TryRemove race in the chunk
                // request index), so keep bursts temporally separated.
                OnLoaded = () => sapi.Event.RegisterCallback(_ => PregenNextBand(queue, idx, bandCz2 + 1, rowsPerBand), 750)
            });
        }
        catch (Exception e)
        {
            sapi.Logger.Error("[genworldsetup] Chunk request for {0} rows {1}-{2} failed: {3}", s.Code, bandCz1, bandCz2, e);
            sapi.BroadcastMessageToAllGroups($"[genworldsetup] Chunk request for {s.Code} failed ({e.Message}); retrying that band in 5 seconds.", EnumChatType.Notification);
            sapi.Event.RegisterCallback(_ => PregenNextBand(queue, idx, bandCz1, rowsPerBand), 5000);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Model
    // ─────────────────────────────────────────────────────────────────────

    // One ore in one rock. Worldgen never seeds our blocks, so each ore gets its
    // own 3D noise field: above the threshold is a vein, and the deeper into the
    // vein a block sits, the richer the grade.
    private class OreSpec
    {
        public string Name;
        public NormalizedSimplexNoise Noise;
        public double Threshold;
        public int PoorId, MediumId, RichId, BountifulId;

        public bool TryPick(int x, int y, int z, out int id)
        {
            id = 0;
            double n = Noise.Noise(x, y, z);
            if (n < Threshold) return false;

            double into = n - Threshold;
            if (into > 0.12 && BountifulId != 0) id = BountifulId;
            else if (into > 0.08 && RichId != 0) id = RichId;
            else if (into > 0.04 && MediumId != 0) id = MediumId;
            else id = PoorId != 0 ? PoorId : MediumId != 0 ? MediumId : RichId;
            return id != 0;
        }
    }

    // Surface treatments a region can have. SurfSoil is bare dirt (pond beds,
    // and surface=barren regions: exposed fertility soil with no grass cover).
    // SurfPeat is a bog floor: peat blocks a few deep, a real fuel deposit.
    private const int SurfGrass = 0, SurfSand = 1, SurfRock = 2, SurfRockSand = 3, SurfSoil = 4, SurfPeat = 5;

    // One labelled area of a drawn island (forest, plains, beach, rocky arm...).
    private class Region
    {
        public char Key;
        public string RockType = "granite";
        public int Surface = SurfGrass;
        public double Height = 1.0;       // fraction of the island's peak height
        public double ShoreWidth = 8;     // blocks from the coast to full height: small = cliff
        public double Rough = 0.3;        // surface noise amplitude
        public double Forest;
        public int Pond;                  // 0 = dry land; else a pond this many blocks deep
        public double Cattails;           // pond region: chance per rim column; land region: chance per waterline column
        public double Flax;               // chance of a wild flax plant per grass column
        public double Devastation;        // chance per column of a devastated-ground patch centre
        public double WildGrass = -1;     // chance of a tallgrass tuft per grass column (-1 = default 0.35)
        public double Stones = -1;        // chance of a loose granite stone per column (-1 = default 0.012)
        public double Sticks;             // chance of a fallen stick per grass column
        public double Litter;             // chance a grass block becomes leaf-littered forest floor
        public double Lilies;             // pond region: chance of a waterlily per water column
        public double Shells;             // chance of a seashell per sand column
        public double Boulders;           // chance of a loose boulder per column
        public double Clay;               // pond region: chance a rim column becomes a clay deposit
        public double Sandy;              // fraction-ish of grass columns turned to sand, in noise blobs
        public double Pumpkins;           // chance per column of a wild pumpkin patch centre
        public int Flood;                 // region sits this many blocks under the sea: shallow flats
        public double Kelp;               // flood region: chance per water column of a seaweed stalk
        public bool HasClimate;           // this region stamps its own plant-tint climate
        public int ClimTempRaw, ClimRainRaw;
        public List<BushSpec> Bushes = new();
        public List<BushSpec> Scatter = new();   // generic decor: flowers, mushrooms, ferns...
        public List<ITreeGenerator> Trees = new();
        public List<OreSpec> Ores = new();
        public int StoneId, StoneId2, SandId, SoilId, GrassId;
        public int CattailId;
        public int[] FlaxIds = Array.Empty<int>();
        public List<OreBitSpec> OreBits = new();                 // surface ore clusters (copper, tin...)
        public int[] DevSoilIds = Array.Empty<int>();
        public int[] DevGrowthIds = Array.Empty<int>();
        public int DrockId;
        public int[] GrassIds = Array.Empty<int>();
        public int[] LitterIds = Array.Empty<int>();
        public int[] ShellIds = Array.Empty<int>();
        public int LooseStoneId, LooseStoneId2, LooseStickId, LilyId, BoulderId, ClayId, ClaySparseId;
        public int PeatId, PeatSparseId;                         // surface=peat: bare + verysparse-grass peat
        public int WaterCattailId;                               // reeds growing IN 1-deep water (flood= flats)
        public int KelpTopId, KelpSectionId;                     // kelp=: seaweed stalks in salt shallows
        public int SparseGrassId, SparseGrassId2;                // surface=barren: verysparse + sparse grass soil
        public int[] MotherIds = Array.Empty<int>();             // pumpkins=: crop-pumpkin mother plants
        public int[] VineIds = Array.Empty<int>();
        public int[] FruitIds = Array.Empty<int>();
        public int[] DebrisIds = Array.Empty<int>();
    }

    // One bush kind a region scatters: a fruiting-bush block, or (for "birch")
    // a shrub grown by the game's dwarf birch tree generator.
    private class BushSpec
    {
        public int BlockId;
        public ITreeGenerator Shrub;
        public double Chance;
    }

    // One surface-ore cluster kind: the loose-bit and shallow-ore blocks for
    // each of the region's two rocks, plus the per-column cluster chance.
    private class OreBitSpec
    {
        public double Chance;
        public int Bit1, Bit2;
        public int Poor1, Med1, Poor2, Med2;
    }

    private class TreeMarker
    {
        public int Gx, Gz;
        public ITreeGenerator Gen;
        public float Size;
    }

    // A `block <char> <blockcode>` marker: one block placed resting on the
    // actual ground at that map cell, on the sea floor when the cell is
    // underwater. Meant for spawners and props from other mods, so the code
    // is resolved at placement time and a missing block is a note, not an
    // error (the mod supplying it may not be installed).
    private class BlockMarker
    {
        public int Gx, Gz;
        public string Code;
        public int Up;   // blocks above the ground; underwater it never rises past SeaLevel - 3
    }

    // One cave design: where it enters the island and how it descends.
    // Declared in a shape file as `cave <char> key=value...`; each map cell
    // holding that char becomes an entrance carved with these parameters.
    private class CaveDef
    {
        public double HeadingDeg = double.NaN;  // map degrees, 0=north 90=east; NaN = aim at the island centre
        public double DipDeg = 12;              // how steeply the tunnel descends while diving
        public double Length = 80;              // main tunnel length in blocks
        public double Radius = 2.6;             // horizontal carve radius
        public double Squash = 0.72;            // vertical radius = Radius * Squash
        public double Weave = 0.5;              // 0 dead straight .. 1 very windy
        public double Scale = 1.0;              // overall size multiplier: radius AND room events
        public int Branches = 2;                // side tunnels forking off the main run
        public int BranchDepth = 2;             // branches may branch again this many levels
        public double BranchLen = 0.5;          // branch length as a fraction of the parent (its midpoint; each branch varies +-30%)
        public double BranchRadius = 0.85;      // branch carve radius as a fraction of the parent's, per level: 0.45 turns a vast main bore into narrow side passages
        public double Depth = 60;               // level out this many blocks below the mouth
        public int Mouth = 2;                   // mouth floor this many blocks above sea level
        public int Entry = 10;                  // blocks of dead-level adit before the dive starts
        public uint Seed;                       // 0 = derived from the entrance cell, stable per design
        public string OreName;                  // wall-lining ore (ores=copper:0.05)
        public double OreChance;                // chance per exposed wall block
    }

    private class CaveMarker
    {
        public int Gx, Gz;
        public CaveDef Def;
    }

    // Deterministic PRNG for cave paths, shared bit-for-bit with the localhost
    // previewer (viewer/app.js ports it verbatim), so the preview shows the
    // SAME weave and branches the game will carve. Do not swap for LCGRandom.
    private class CaveRand
    {
        private uint s;
        public CaveRand(uint seed) { s = seed == 0 ? 2463534242u : seed; }
        public uint NextUInt() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
        public double NextDouble() => (NextUInt() >> 8) / 16777216.0;
    }

    // A drawn island: the character grid, its regions, and the two distance
    // fields that turn a flat mask into terrain with height and a sea floor.
    private class ShapeDef
    {
        public int W, H;
        public char[,] Cells;
        public float[,] DistToOcean;  // land cell -> distance to the nearest water cell
        public float[,] DistToLand;   // water cell -> distance to the nearest land cell
        public float[,] HeightField;  // per-cell peak fraction, smoothed across region borders
        public float[,] DistToDry;    // flood support: distance to the nearest dry land cell
        public float[,] ShoreField;   // per-cell shore width, smoothed across region borders
        public Dictionary<char, Region> Regions = new();
        public List<TreeMarker> Markers = new();
        public List<BlockMarker> BlockMarkers = new();
        public List<CaveMarker> Caves = new();
        public bool NaturalDeposits;  // `deposits natural`: run the game's own ore pass
        // Bounding box of the actual land cells (plus block markers), in
        // cells. A narrow chain drawn across a wide grid touches far fewer
        // chunks than the grid square suggests.
        public int LandGx1, LandGz1, LandGx2, LandGz2;
    }

    private class IslandJob
    {
        public int Cx, Cz, Dim, SeaLevel;
        public int DomeHeight, Water, MaxDepth;
        public double OceanRing;
        public long Seed;
        public int Diameter;
        // The worldgen renderer already placed this island's terrain during
        // chunk generation; the live pass only decorates.
        public bool SkipTerrain;

        // Radial mode
        public double R, Rmax;
        public double Bvx, Bvz, Cvx, Cvz;
        public double BumpAmp;
        public List<OreSpec> Ores = new();
        public List<ITreeGenerator> ForestTrees = new();
        public double ForestDensity;
        public ITreeGenerator SummitTree;

        // Shape mode
        public ShapeDef Shape;
        public double WorldPerCell;
        public NormalizedSimplexNoise JitterX, JitterZ;
        public double RotCos = 1.0, RotSin = 0.0;   // rotate=: spins the drawn map

        // climate=: overwrite the worldgen climate over the island (plant tint)
        public bool HasClimate;
        public int ClimTempRaw, ClimRainRaw;
        public double ClimRadius;

        // Chunk preload gate: all columns under the island must be loaded
        // before the first block write, or writes are silently dropped.
        public bool ChunksRequested, ChunksLoaded;
        public int WaitTicks;
        public int LastMissing = int.MaxValue;

        // deposits natural / deposits=natural: after terrain, run the game's own
        // GenDeposits over the island's chunk columns, so the stone carries the
        // same ore the world would have generated there.
        public bool NaturalDeposits;

        public NormalizedSimplexNoise CoastNoise, SurfNoise, RockBlend;
        public int StoneId, SoilId, GrassId, SandId, WaterId, SaltWaterId;

        public int MinX, MinZ, W, H;
        public long I, Total, Placed, Trees, Plants;
        public List<(int X, int Z, OreBitSpec Spec)> OreBitCenters = new();
        public List<(int X, int Z, Region Reg)> DevastationCenters = new();
        public List<(int X, int Z, Region Reg)> PumpkinCenters = new();
        public int ColumnsPerTick;
        public int Phase;                 // 0 terrain, 1 forest
        public LCGRandom Rand;
        public IServerPlayer Player;
        public bool HasNext => I < Total;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Command
    // ─────────────────────────────────────────────────────────────────────

    private TextCommandResult OnGenIsland(TextCommandCallingArgs args)
    {
        string all = args.Parsers[0].GetValue() as string ?? "";

        if (all.Trim().Equals("shapes", StringComparison.OrdinalIgnoreCase))
            return ListShapes();

        if (_islandBusy)
            return TextCommandResult.Error("An island is still generating. Wait for it to finish before starting another.");

        var opt = ParseIslandOptions(all);

        // x=/z= (map coordinates, the numbers the HUD shows) centre the
        // island at an explicit position instead of the caller, which also
        // lets the world-setup plan build islands with no player involved.
        bool hasXZ = opt.TryGetValue("x", out string xStr) & opt.TryGetValue("z", out string zStr);
        int ox, oz, dim;
        if (hasXZ)
        {
            if (!int.TryParse(xStr, out int mapX) || !int.TryParse(zStr, out int mapZ))
                return TextCommandResult.Error("x= and z= must be whole map coordinates.");
            ox = (int)sapi.World.DefaultSpawnPosition.X + mapX;
            oz = (int)sapi.World.DefaultSpawnPosition.Z + mapZ;
            dim = 0;
        }
        else
        {
            if (args.Caller?.Entity == null)
                return TextCommandResult.Error("Run /genisland in game so it centres on where you stand, or pass x= and z= map coordinates.");
            GetOrigin(args.Caller, out ox, out int _, out oz, out dim);
        }

        var problems = new List<string>();
        IslandJob job = BuildIslandJob(opt, ox, oz, dim, problems, out string err);
        if (job == null) return TextCommandResult.Error(err);
        job.Player = args.Caller?.Player as IServerPlayer;

        int cs = GlobalConstants.ChunkSize;
        int cx1 = FloorDiv(job.MinX, cs), cx2 = FloorDiv(job.MinX + job.W - 1, cs);
        int cz1 = FloorDiv(job.MinZ, cs), cz2 = FloorDiv(job.MinZ + job.H - 1, cs);

        _islandBusy = true;
        sapi.WorldManager.LoadChunkColumnPriority(cx1, cz1, cx2, cz2,
            new ChunkLoadOptions { KeepLoaded = false, OnLoaded = () => StartIslandJob(job) });

        string shapeName = OptStr(opt, "shape", null);
        string msg = shapeName != null
            ? $"Building '{shapeName}' at {job.Diameter} blocks across (sea level {job.SeaLevel}, seed {job.Seed}), {job.Shape.Regions.Count} region(s)."
            : $"Generating a {job.Diameter}-block island (sea level {job.SeaLevel}, seed {job.Seed}).";
        msg += " It builds over a few seconds without freezing the server.";
        if (problems.Count > 0) msg += " Notes: " + string.Join("; ", problems) + ".";
        return TextCommandResult.Success(msg);
    }

    // The option tokens of /genisland (and of a plan file's island line):
    // key=value pairs, with a bare leading number as diameter shorthand.
    private static Dictionary<string, string> ParseIslandOptions(string all)
    {
        string[] toks = all.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < toks.Length; i++)
        {
            int eq = toks[i].IndexOf('=');
            if (eq > 0) opt[toks[i].Substring(0, eq)] = toks[i].Substring(eq + 1);
            else if (i == 0 && int.TryParse(toks[i], out _)) opt["diameter"] = toks[i];
        }
        return opt;
    }

    // Everything between the option map and a ready-to-run job: palette,
    // noises, shape or radial fields, climate, bounds. Shared by the chat
    // command and the worldgen renderer, so an island is defined ONCE and
    // both paths produce the identical result from the same seed.
    private IslandJob BuildIslandJob(Dictionary<string, string> opt, int ox, int oz, int dim, List<string> problems, out string err)
    {
        err = null;

        int diameter = OptInt(opt, "diameter", 120, 8, 1024);
        int height = OptInt(opt, "height", 40, 3, 220);
        int water = OptInt(opt, "water", 30, 0, 200);
        int maxdepth = OptInt(opt, "maxdepth", 80, 8, 250);
        int seaLevel = OptInt(opt, "sealevel", sapi.World.SeaLevel, 2, sapi.WorldManager.MapSizeY - 2);

        long seed;
        if (!opt.TryGetValue("seed", out string seedStr) || !long.TryParse(seedStr, out seed))
            seed = sapi.World.Rand.Next(1, int.MaxValue);

        // Global palette (regions may override rock and sand per area).
        string stoneCode = OptStr(opt, "stone", "rock-granite");
        Block stone = ResolveBlock(stoneCode, out string se);
        Block soil = ResolveBlock(OptStr(opt, "soil", "soil-medium-none"), out string oe);
        Block grass = ResolveBlock(OptStr(opt, "grass", "soil-medium-normal"), out string ge);
        Block sand = ResolveBlock(OptStr(opt, "sand", "sand-granite"), out string ae);
        Block waterBlock = ResolveBlock(OptStr(opt, "water_block", "water-still-7"), out string we);
        // The open ocean is SALT water; freshwater has a slightly different tint,
        // so the ring we fill around the island must be salt to match the biome.
        // Ponds stay freshwater. `oceanwater=water-still-7` forces fresh (lake set).
        Block oceanBlock = ResolveBlock(OptStr(opt, "oceanwater", "saltwater-still-7"), out string oce);
        if (stone == null) { err = "stone: " + se; return null; }
        if (soil == null) { err = "soil: " + oe; return null; }
        if (grass == null) { err = "grass: " + ge; return null; }
        if (sand == null) { err = "sand: " + ae; return null; }
        if (waterBlock == null) { err = "water: " + we; return null; }
        if (oceanBlock == null) { err = "oceanwater: " + oce; return null; }

        var job = new IslandJob
        {
            Cx = ox, Cz = oz, Dim = dim, SeaLevel = seaLevel,
            DomeHeight = height, Water = water, MaxDepth = maxdepth,
            Seed = seed,
            CoastNoise = NormalizedSimplexNoise.FromDefaultOctaves(4, 1 / 40.0, 0.5, seed),
            SurfNoise = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 22.0, 0.5, seed + 1),
            RockBlend = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 16.0, 0.5, seed + 9),
            JitterX = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 26.0, 0.5, seed + 7),
            JitterZ = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 26.0, 0.5, seed + 13),
            StoneId = stone.BlockId, SoilId = soil.BlockId, GrassId = grass.BlockId,
            SandId = sand.BlockId, WaterId = waterBlock.BlockId, SaltWaterId = oceanBlock.BlockId,
            ColumnsPerTick = 400,
            Phase = 0,
            Rand = new LCGRandom(seed)
        };
        job.Diameter = diameter;
        // skipterrain=1 (used by the auto world setup): the worldgen pass
        // already rendered this island's terrain during chunk generation,
        // so the live pass skips straight to decoration.
        job.SkipTerrain = opt.ContainsKey("skipterrain");

        // rotate=deg spins a drawn island clockwise on the map, so a shape's
        // harbour (or beach, or cliff) can be aimed at a neighbouring island.
        // rotate=90 means what pointed north now points east.
        double rotDeg = OptDouble(opt, "rotate", 0, -36000, 36000) * Math.PI / 180.0;
        job.RotCos = Math.Cos(rotDeg);
        job.RotSin = Math.Sin(rotDeg);

        // climate=arid (or dry/temperate/lush/cold, or <tempC>:<rain 0..1>)
        // rewrites the worldgen climate over the island, which is what tints
        // grass and leaves: a hot dry climate fades them rusty desert-yellow.
        if (opt.TryGetValue("climate", out string climStr) && !string.IsNullOrWhiteSpace(climStr))
        {
            if (!ParseClimate(climStr, out float climTempC, out float climRain, out string cerr))
            {
                err = "climate: " + cerr;
                return null;
            }
            job.HasClimate = true;
            job.ClimTempRaw = Math.Clamp(Climate.DescaleTemperature(climTempC), 0, 255);
            job.ClimRainRaw = (int)Math.Clamp(climRain * 255.0, 0, 255);
        }
        // Set even without a command climate=: shape regions may carry their
        // own climate=, and the stamp needs the island's radius either way.
        job.ClimRadius = diameter / 2.0;

        int reach;
        string shapeName = OptStr(opt, "shape", null);

        if (shapeName != null)
        {
            ShapeDef shape = LoadShape(shapeName, job, seed, problems, out err);
            if (shape == null) return null;

            job.Shape = shape;
            job.NaturalDeposits = shape.NaturalDeposits;
            job.WorldPerCell = diameter / (double)Math.Max(shape.W, shape.H);
            job.OceanRing = Math.Max(24.0, diameter * 0.22);
            // Half the grid's diagonal, plus the offshore ring we reshape.
            double half = 0.5 * Math.Sqrt(Math.Pow(shape.W * job.WorldPerCell, 2) + Math.Pow(shape.H * job.WorldPerCell, 2));
            reach = (int)Math.Ceiling(half + job.OceanRing) + 4;
        }
        else
        {
            double R = diameter / 2.0;
            job.R = R;
            job.OceanRing = Math.Max(24.0, R * 0.6);
            job.Rmax = R * 1.12 + job.OceanRing;
            job.BumpAmp = Math.Min(4.0, height * 0.15);
            reach = (int)Math.Ceiling(job.Rmax) + 2;

            DirVec(OptDir(opt, "beachdir", "s"), out double bvx, out double bvz);
            DirVec(OptDir(opt, "cliffdir", "n"), out double cvx, out double cvz);
            job.Bvx = bvx; job.Bvz = bvz; job.Cvx = cvx; job.Cvz = cvz;

            string rockType = stoneCode.StartsWith("rock-", StringComparison.OrdinalIgnoreCase) ? stoneCode.Substring(5) : null;
            if (opt.TryGetValue("ores", out string oreStr) && !string.IsNullOrWhiteSpace(oreStr))
            {
                if (rockType == null) problems.Add("ores need a rock-* stone block to sit in");
                else ParseOres(oreStr, rockType, seed, job.Ores, problems);
            }

            job.ForestDensity = OptDouble(opt, "forest", 0.0, 0.0, 0.35);
            if (job.ForestDensity > 0)
            {
                foreach (string want in OptStr(opt, "trees", "oak").Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    ITreeGenerator g = FindTreeGenerator(want.Trim());
                    if (g != null) job.ForestTrees.Add(g); else problems.Add($"no tree generator for '{want.Trim()}'");
                }
                if (job.ForestTrees.Count == 0) { job.ForestDensity = 0; problems.Add("forest skipped"); }
            }
            job.SummitTree = FindTreeGenerator("oak");
        }

        // deposits=natural forces the vanilla ore pass on; deposits=off forces it
        // off, overriding the shape file's `deposits natural` line either way.
        string depOpt = OptStr(opt, "deposits", null);
        if (depOpt != null)
            job.NaturalDeposits = depOpt.Equals("natural", StringComparison.OrdinalIgnoreCase);

        job.MinX = ox - reach; job.MinZ = oz - reach;
        job.W = reach * 2 + 1; job.H = reach * 2 + 1;
        job.Total = (long)job.W * job.H;
        return job;
    }

    private TextCommandResult ListShapes()
    {
        try
        {
            if (!Directory.Exists(shapeFolder)) return TextCommandResult.Success($"No shapes folder yet: {shapeFolder}");
            string[] files = Directory.GetFiles(shapeFolder, "*.txt");
            if (files.Length == 0) return TextCommandResult.Success($"No shape files in {shapeFolder}");
            var names = new List<string>();
            foreach (string f in files) names.Add(Path.GetFileNameWithoutExtension(f));
            return TextCommandResult.Success($"Shapes in {shapeFolder}: {string.Join(", ", names)}");
        }
        catch (Exception e) { return TextCommandResult.Error(e.Message); }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Shape files
    // ─────────────────────────────────────────────────────────────────────

    // Format:
    //   region <char> rock=.. surface=grass|sand|rock|rocksand ores=.. forest=..
    //                 trees=.. height=.. shore=.. rough=..
    //   tree   <char> <treetype> <size>
    //   map
    //   <grid lines, '.' is ocean>
    private ShapeDef LoadShape(string name, IslandJob job, long seed, List<string> problems, out string err)
    {
        err = null;
        if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) { err = "Shape name must be a plain file name."; return null; }

        string file = name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? name : name + ".txt";
        string path = Path.Combine(shapeFolder, file);
        if (!File.Exists(path)) { err = $"No shape '{file}' in {shapeFolder}. Use /genisland shapes to list them."; return null; }

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception e) { err = $"Could not read {file}: {e.Message}"; return null; }

        var shape = new ShapeDef();
        var markerDefs = new Dictionary<char, (ITreeGenerator gen, float size)>();
        var caveDefs = new Dictionary<char, CaveDef>();
        var blockDefs = new Dictionary<char, (string code, int up)>();
        var rows = new List<string>();
        bool inMap = false;
        int oreIdx = 0;

        foreach (string raw in lines)
        {
            string line = (raw ?? "").TrimEnd();
            if (inMap)
            {
                if (line.Trim().Length == 0) continue;
                rows.Add(line);
                continue;
            }

            string t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;

            if (t.Equals("map", StringComparison.OrdinalIgnoreCase)) { inMap = true; continue; }

            string[] tok = t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (tok[0].Equals("region", StringComparison.OrdinalIgnoreCase) && tok.Length >= 2)
            {
                var r = ParseRegion(tok, job, seed, ref oreIdx, problems);
                if (r != null) shape.Regions[r.Key] = r;
            }
            else if (tok[0].Equals("tree", StringComparison.OrdinalIgnoreCase) && tok.Length >= 3)
            {
                char key = tok[1][0];
                ITreeGenerator gen = FindTreeGenerator(tok[2]);
                float size = tok.Length > 3 && float.TryParse(tok[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float s) ? s : 1.5f;
                if (gen == null) problems.Add($"no tree generator for '{tok[2]}'");
                else markerDefs[key] = (gen, size);
            }
            else if (tok[0].Equals("cave", StringComparison.OrdinalIgnoreCase) && tok.Length >= 2)
            {
                caveDefs[tok[1][0]] = ParseCave(tok, problems);
            }
            else if (tok[0].Equals("block", StringComparison.OrdinalIgnoreCase) && tok.Length >= 3)
            {
                int up = tok.Length > 3 && int.TryParse(tok[3], out int u) ? Math.Clamp(u, 0, 60) : 0;
                blockDefs[tok[1][0]] = (tok[2], up);
            }
            else if (tok[0].Equals("deposits", StringComparison.OrdinalIgnoreCase) && tok.Length >= 2)
            {
                if (tok[1].Equals("natural", StringComparison.OrdinalIgnoreCase)) shape.NaturalDeposits = true;
                else problems.Add($"unknown deposits mode '{tok[1]}', only 'natural' exists");
            }
        }

        if (rows.Count == 0) { err = $"Shape '{file}' has no map."; return null; }

        int w = 0;
        foreach (string r in rows) w = Math.Max(w, r.Length);
        shape.W = w; shape.H = rows.Count;
        shape.Cells = new char[w, rows.Count];

        for (int z = 0; z < rows.Count; z++)
            for (int x = 0; x < w; x++)
            {
                char c = x < rows[z].Length ? rows[z][x] : '.';
                if (markerDefs.ContainsKey(c))
                {
                    shape.Markers.Add(new TreeMarker { Gx = x, Gz = z, Gen = markerDefs[c].gen, Size = markerDefs[c].size });
                    c = '?'; // resolved to a neighbouring region below
                }
                else if (caveDefs.ContainsKey(c))
                {
                    shape.Caves.Add(new CaveMarker { Gx = x, Gz = z, Def = caveDefs[c] });
                    c = '?';
                }
                else if (blockDefs.ContainsKey(c))
                {
                    shape.BlockMarkers.Add(new BlockMarker { Gx = x, Gz = z, Code = blockDefs[c].code, Up = blockDefs[c].up });
                    // Unlike tree/cave markers, a block marker may stand in
                    // open ocean (a spawner on the sea floor). Ocean around
                    // it means ocean; NeighbourRegion would instead grow a
                    // one-cell islet from land up to 4 cells away.
                    c = '!';
                }
                shape.Cells[x, z] = c;
            }

        // A marker cell still needs terrain, so adopt a neighbouring region.
        // '!' (block marker) cells go to ocean unless a direct neighbour is
        // land, so a sea-floor marker does not sprout a one-cell islet.
        for (int z = 0; z < shape.H; z++)
            for (int x = 0; x < shape.W; x++)
            {
                if (shape.Cells[x, z] == '?')
                    shape.Cells[x, z] = NeighbourRegion(shape, x, z);
                else if (shape.Cells[x, z] == '!')
                    shape.Cells[x, z] = AdjacentRegion(shape, x, z);
            }

        foreach (char c in shape.Cells)
            if (c != '.' && !shape.Regions.ContainsKey(c))
            {
                err = $"Shape '{file}' uses '{c}' in the map with no matching region line.";
                return null;
            }

        // The land's bounding box in cells, block markers included (a
        // sea-floor marker still needs its chunk).
        shape.LandGx1 = shape.W; shape.LandGz1 = shape.H; shape.LandGx2 = -1; shape.LandGz2 = -1;
        for (int z = 0; z < shape.H; z++)
            for (int x = 0; x < shape.W; x++)
                if (shape.Cells[x, z] != '.')
                {
                    if (x < shape.LandGx1) shape.LandGx1 = x;
                    if (x > shape.LandGx2) shape.LandGx2 = x;
                    if (z < shape.LandGz1) shape.LandGz1 = z;
                    if (z > shape.LandGz2) shape.LandGz2 = z;
                }
        foreach (var bm in shape.BlockMarkers)
        {
            shape.LandGx1 = Math.Min(shape.LandGx1, bm.Gx); shape.LandGx2 = Math.Max(shape.LandGx2, bm.Gx);
            shape.LandGz1 = Math.Min(shape.LandGz1, bm.Gz); shape.LandGz2 = Math.Max(shape.LandGz2, bm.Gz);
        }
        if (shape.LandGx2 < 0)
        {
            shape.LandGx1 = 0; shape.LandGz1 = 0;
            shape.LandGx2 = shape.W - 1; shape.LandGz2 = shape.H - 1;
        }

        // Distance fields: how far each land cell is from water, and each water
        // cell from land. These give the island its height and its sea floor.
        bool[,] isLand = new bool[shape.W, shape.H];
        bool[,] isOcean = new bool[shape.W, shape.H];
        for (int z = 0; z < shape.H; z++)
            for (int x = 0; x < shape.W; x++)
            {
                bool land = shape.Cells[x, z] != '.';
                isLand[x, z] = land;
                isOcean[x, z] = !land;
            }
        shape.DistToOcean = DistanceField(isOcean, shape.W, shape.H, true);
        shape.DistToLand = DistanceField(isLand, shape.W, shape.H, false);

        // Flooded regions need a third field: distance to the nearest DRY land
        // cell. Their water depth ramps from zero at that edge, so a marsh
        // meets the meadow as a smooth descent instead of a square step.
        bool anyFlood = false;
        foreach (Region fr in shape.Regions.Values) if (fr.Flood > 0) { anyFlood = true; break; }
        if (anyFlood)
        {
            var isDry = new bool[shape.W, shape.H];
            for (int z = 0; z < shape.H; z++)
                for (int x = 0; x < shape.W; x++)
                {
                    char c = shape.Cells[x, z];
                    isDry[x, z] = c != '.' && shape.Regions.TryGetValue(c, out Region rg2) && rg2.Flood == 0;
                }
            shape.DistToDry = DistanceField(isDry, shape.W, shape.H, false);
        }

        // Per-cell height and shore, then smoothed so inland region borders ramp
        // into each other instead of forming vertical seams. Materials stay
        // crisp; only the elevation blends. Flooded regions count as height 0
        // (water level): the neighbouring meadow then ramps DOWN to the water
        // through the smoothing, and the flood cap takes it under.
        var rawH = new float[shape.W, shape.H];
        var rawS = new float[shape.W, shape.H];
        for (int z = 0; z < shape.H; z++)
            for (int x = 0; x < shape.W; x++)
            {
                char c = shape.Cells[x, z];
                if (c != '.' && shape.Regions.TryGetValue(c, out Region rg))
                {
                    rawH[x, z] = rg.Flood > 0 ? 0f : (float)rg.Height;
                    rawS[x, z] = (float)rg.ShoreWidth;
                }
            }
        shape.HeightField = SmoothField(rawH, isLand, shape.W, shape.H, 5);
        shape.ShoreField = SmoothField(rawS, isLand, shape.W, shape.H, 5);
        return shape;
    }

    // Box-blur a per-cell field, but only averaging cells that already hold a
    // value (land, or an ocean cell filled by a previous pass). Each pass also
    // bleeds values one cell into the ocean, so land columns sampling across the
    // coast read a sensible height rather than zero.
    private static float[,] SmoothField(float[,] src, bool[,] known, int w, int h, int passes)
    {
        var cur = (float[,])src.Clone();
        var have = (bool[,])known.Clone();
        for (int pass = 0; pass < passes; pass++)
        {
            var next = (float[,])cur.Clone();
            var nextHave = (bool[,])have.Clone();
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    double sum = 0; int cnt = 0;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if (nx < 0 || nz < 0 || nx >= w || nz >= h || !have[nx, nz]) continue;
                            sum += cur[nx, nz]; cnt++;
                        }
                    if (cnt > 0) { next[x, z] = (float)(sum / cnt); nextHave[x, z] = true; }
                }
            cur = next; have = nextHave;
        }
        return cur;
    }

    // Block-marker cells: only a DIRECTLY adjacent region is adopted; open
    // water stays open water.
    private static char AdjacentRegion(ShapeDef shape, int x, int z)
    {
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= shape.W || nz >= shape.H) continue;
                char c = shape.Cells[nx, nz];
                if (c != '.' && c != '?' && c != '!') return c;
            }
        return '.';
    }

    private static char NeighbourRegion(ShapeDef shape, int x, int z)
    {
        for (int r = 1; r <= 4; r++)
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= shape.W || nz >= shape.H) continue;
                    char c = shape.Cells[nx, nz];
                    if (c != '.' && c != '?' && c != '!') return c;
                }
        return '.';
    }

    // Two-pass chamfer distance transform: distance in cells from every cell to
    // the nearest source cell. outsideIsSource treats beyond-the-grid as water,
    // so an island touching the border still tapers.
    private static float[,] DistanceField(bool[,] source, int w, int h, bool outsideIsSource)
    {
        const float INF = 1e9f;
        var d = new float[w, h];
        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
                d[x, z] = source[x, z] ? 0f : INF;

        float Edge(int x, int z)
        {
            if (!outsideIsSource) return INF;
            // Distance to the grid border, treated as water.
            return Math.Min(Math.Min(x + 1, w - x), Math.Min(z + 1, h - z));
        }

        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
            {
                float v = Math.Min(d[x, z], Edge(x, z));
                if (x > 0) v = Math.Min(v, d[x - 1, z] + 1f);
                if (z > 0) v = Math.Min(v, d[x, z - 1] + 1f);
                if (x > 0 && z > 0) v = Math.Min(v, d[x - 1, z - 1] + 1.414f);
                if (x < w - 1 && z > 0) v = Math.Min(v, d[x + 1, z - 1] + 1.414f);
                d[x, z] = v;
            }

        for (int z = h - 1; z >= 0; z--)
            for (int x = w - 1; x >= 0; x--)
            {
                float v = d[x, z];
                if (x < w - 1) v = Math.Min(v, d[x + 1, z] + 1f);
                if (z < h - 1) v = Math.Min(v, d[x, z + 1] + 1f);
                if (x < w - 1 && z < h - 1) v = Math.Min(v, d[x + 1, z + 1] + 1.414f);
                if (x > 0 && z < h - 1) v = Math.Min(v, d[x - 1, z + 1] + 1.414f);
                d[x, z] = v;
            }
        return d;
    }

    private Region ParseRegion(string[] tok, IslandJob job, long seed, ref int oreIdx, List<string> problems)
    {
        var r = new Region { Key = tok[1][0] };
        string oreStr = null, treeStr = null, rock2 = null, sandCode = null, fert = null, bushStr = null, scatterStr = null, oreBitsStr = null, climateStr = null;

        for (int i = 2; i < tok.Length; i++)
        {
            int eq = tok[i].IndexOf('=');
            if (eq <= 0) continue;
            string k = tok[i].Substring(0, eq).ToLowerInvariant();
            string v = tok[i].Substring(eq + 1);

            switch (k)
            {
                case "rock": r.RockType = v; break;
                case "rock2": rock2 = v; break;         // a second rock, blended underground
                case "sand": sandCode = v; break;        // explicit beach block (e.g. white sand)
                case "fertility": fert = v; break;       // verylow..high: sets the soil and grass
                case "ores": oreStr = v; break;
                case "trees": treeStr = v; break;
                case "surface":
                    r.Surface = v.ToLowerInvariant() switch
                    {
                        "sand" => SurfSand,
                        "rock" => SurfRock,
                        "rocksand" => SurfRockSand,
                        "barren" => SurfSoil,   // bare fertility soil, no grass cover
                        "peat" => SurfPeat,     // bog floor: minable peat, sparse grass
                        _ => SurfGrass
                    };
                    break;
                case "height": r.Height = ParseD(v, 1.0); break;
                case "shore": r.ShoreWidth = Math.Max(1.0, ParseD(v, 8)); break;
                case "rough": r.Rough = ParseD(v, 0.3); break;
                case "forest": r.Forest = Math.Clamp(ParseD(v, 0), 0, 0.35); break;
                case "pond": r.Pond = (int)Math.Clamp(ParseD(v, 3), 1, 40); break;
                case "cattails": r.Cattails = Math.Clamp(ParseD(v, 0), 0, 1); break;
                case "flax": r.Flax = Math.Clamp(ParseD(v, 0), 0, 1); break;
                case "orebits": oreBitsStr = oreBitsStr == null ? v : oreBitsStr + "," + v; break;
                case "copperbits": // legacy spelling of orebits=copper:x
                    oreBitsStr = (oreBitsStr == null ? "" : oreBitsStr + ",") + "copper:" + v; break;
                case "devastation": r.Devastation = Math.Clamp(ParseD(v, 0), 0, 1); break;
                case "wildgrass": r.WildGrass = Math.Clamp(ParseD(v, 0), 0, 1); break;
                case "stones": r.Stones = Math.Clamp(ParseD(v, 0), 0, 1); break;
                case "sticks": r.Sticks = Math.Clamp(ParseD(v, 0), 0, 1); break;
                case "litter": r.Litter = Math.Clamp(ParseD(v, 0), 0, 1); break;
                case "bushes": bushStr = v; break;
                case "scatter": scatterStr = v; break;
                case "lilies": r.Lilies = Math.Clamp(ParseD(v, 0), 0, 1); break;
                case "shells": r.Shells = Math.Clamp(ParseD(v, 0), 0, 1); break;
                case "boulders": r.Boulders = Math.Clamp(ParseD(v, 0), 0, 1); break;
                case "clay": r.Clay = Math.Clamp(ParseD(v, 0), 0, 1); break;
                case "sandy": r.Sandy = Math.Clamp(ParseD(v, 0), 0, 1); break;
                case "pumpkins": r.Pumpkins = Math.Clamp(ParseD(v, 0), 0, 1); break;
                case "flood": r.Flood = (int)Math.Clamp(ParseD(v, 1), 1, 3); break; // shallow sea over the region
                case "kelp": r.Kelp = Math.Clamp(ParseD(v, 0), 0, 1); break;        // seaweed in the flooded water
                case "climate": climateStr = v; break;   // this region's own plant tint
            }
        }

        // Per-region climate: same presets/syntax as the command's climate=,
        // stamped only over this region's footprint. Lets one island carry
        // several tints (or a test island carry every one of them).
        if (climateStr != null)
        {
            if (ParseClimate(climateStr, out float ct, out float cr, out string cerr))
            {
                r.HasClimate = true;
                r.ClimTempRaw = Math.Clamp(Climate.DescaleTemperature(ct), 0, 255);
                r.ClimRainRaw = (int)Math.Clamp(cr * 255.0, 0, 255);
            }
            else problems.Add($"region {r.Key}: climate: {cerr}");
        }

        Block stone = sapi.World.GetBlock(new AssetLocation("game", "rock-" + r.RockType));
        r.StoneId = stone?.BlockId ?? job.StoneId;
        if (stone == null) problems.Add($"region {r.Key}: no rock-{r.RockType}, using the default stone");

        if (rock2 != null)
        {
            Block s2 = sapi.World.GetBlock(new AssetLocation("game", "rock-" + rock2));
            if (s2 != null) r.StoneId2 = s2.BlockId;
            else problems.Add($"region {r.Key}: no rock-{rock2}, second rock ignored");
        }

        // Beach block: an explicit code (white sand, gravel...) or this rock's sand.
        Block rsand = sandCode != null ? ResolveBlock(sandCode, out _)
                                       : sapi.World.GetBlock(new AssetLocation("game", "sand-" + r.RockType));
        r.SandId = rsand?.BlockId ?? job.SandId;

        // Soil and grass follow the region's fertility, if given. The game's
        // block codes do not match their names: code "compost" displays as
        // "High fertility soil" and code "high" displays as "Terra preta", so
        // translate the friendly words people would actually write.
        if (fert != null)
        {
            fert = fert.ToLowerInvariant() switch
            {
                "high" => "compost",
                "terrapreta" => "high",
                _ => fert.ToLowerInvariant()
            };
            Block soilB = sapi.World.GetBlock(new AssetLocation("game", $"soil-{fert}-none"));
            Block grassB = sapi.World.GetBlock(new AssetLocation("game", $"soil-{fert}-normal"));
            r.SoilId = soilB?.BlockId ?? job.SoilId;
            r.GrassId = grassB?.BlockId ?? job.GrassId;
            if (soilB == null) problems.Add($"region {r.Key}: no soil fertility '{fert}', using default");
        }
        else
        {
            r.SoilId = job.SoilId;
            r.GrassId = job.GrassId;
        }

        // surface=barren tops: patchy verysparse/sparse grass cover, so worn
        // ground still reads alive rather than like a dug pit.
        string sparseFert = fert ?? "medium";
        r.SparseGrassId = sapi.World.GetBlock(new AssetLocation("game", $"soil-{sparseFert}-verysparse"))?.BlockId ?? r.SoilId;
        r.SparseGrassId2 = sapi.World.GetBlock(new AssetLocation("game", $"soil-{sparseFert}-sparse"))?.BlockId ?? r.SparseGrassId;

        if (r.Surface == SurfPeat)
        {
            r.PeatId = sapi.World.GetBlock(new AssetLocation("game", "peat-none"))?.BlockId ?? 0;
            r.PeatSparseId = sapi.World.GetBlock(new AssetLocation("game", "peat-verysparse"))?.BlockId ?? r.PeatId;
            if (r.PeatId == 0)
            {
                r.Surface = SurfSoil;
                problems.Add($"region {r.Key}: no peat block, using bare soil");
            }
        }

        if (!string.IsNullOrWhiteSpace(oreStr))
            ParseOres(oreStr, r.RockType, seed + 31 * (oreIdx++ + 1), r.Ores, problems);

        if (r.Forest > 0)
        {
            foreach (string want in (treeStr ?? "oak").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                ITreeGenerator g = FindTreeGenerator(want.Trim());
                if (g != null) r.Trees.Add(g);
            }
            if (r.Trees.Count == 0) { r.Forest = 0; problems.Add($"region {r.Key}: no usable trees, forest off"); }
        }

        if (r.Kelp > 0)
        {
            r.KelpTopId = sapi.World.GetBlock(new AssetLocation("game", "seaweed-top"))?.BlockId ?? 0;
            r.KelpSectionId = sapi.World.GetBlock(new AssetLocation("game", "seaweed-section"))?.BlockId ?? r.KelpTopId;
            if (r.KelpTopId == 0) { r.Kelp = 0; problems.Add($"region {r.Key}: no seaweed blocks, kelp off"); }
        }

        if (r.Cattails > 0)
        {
            r.CattailId = sapi.World.GetBlock(new AssetLocation("game", "tallplant-coopersreed-land-normal-free"))?.BlockId ?? 0;
            // The water variant grows IN 1-deep water (maxWaterDepth 1), for
            // flooded flats and the shallow rim just off a reedy shore.
            r.WaterCattailId = sapi.World.GetBlock(new AssetLocation("game", "tallplant-coopersreed-water-normal-free"))?.BlockId ?? 0;
            if (r.CattailId == 0) { r.Cattails = 0; problems.Add($"region {r.Key}: no cattail block, cattails off"); }
        }

        if (r.Flax > 0)
        {
            // Mixed maturity so a wild patch does not look machine-planted.
            var flaxIds = new List<int>();
            for (int stage = 6; stage <= 9; stage++)
            {
                Block b = sapi.World.GetBlock(new AssetLocation("game", "crop-flax-" + stage));
                if (b != null) flaxIds.Add(b.BlockId);
            }
            r.FlaxIds = flaxIds.ToArray();
            if (flaxIds.Count == 0) { r.Flax = 0; problems.Add($"region {r.Key}: no crop-flax blocks, flax off"); }
        }

        if (r.Pumpkins > 0)
        {
            // A wild pumpkin patch: mother plants, vines in mixed stages
            // (weighted toward healthy ones), fruits, and rusty debris.
            r.MotherIds = ResolveIds("crop-pumpkin-4", "crop-pumpkin-5", "crop-pumpkin-6", "crop-pumpkin-7");
            r.VineIds = ResolveIds("pumpkin-vine-2-normal", "pumpkin-vine-3-normal", "pumpkin-vine-3-normal",
                "pumpkin-vine-3-blooming", "pumpkin-vine-4-withered");
            r.FruitIds = ResolveIds("pumpkin-fruit-1", "pumpkin-fruit-2", "pumpkin-fruit-3", "pumpkin-fruit-4");
            r.DebrisIds = ResolveIds("loosegears-1", "loosegears-3", "metal-scraps");
            if (r.MotherIds.Length == 0 || r.VineIds.Length == 0)
            {
                r.Pumpkins = 0;
                problems.Add($"region {r.Key}: pumpkin plant blocks missing, pumpkins off");
            }
        }

        if (!string.IsNullOrWhiteSpace(oreBitsStr))
        {
            // orebits=tin:0.002,copper:0.001. Each entry is a surface-cluster
            // kind: friendly ore names map through the same aliases as ores=,
            // and each cluster needs the loose bit + shallow ore blocks
            // matched to the region's rocks (allowedVariants gates combos).
            foreach (string entry in oreBitsStr.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split(':');
                string want = parts[0].Trim().ToLowerInvariant();
                double chance = parts.Length > 1 ? Math.Clamp(ParseD(parts[1], 0.001), 0, 1) : 0.001;
                if (want.Length == 0 || chance <= 0) continue;

                string[] minerals = OreAliases.TryGetValue(want, out string[] al) ? al : new[] { want };
                var spec = new OreBitSpec { Chance = chance };
                ResolveOreBits(minerals, r.RockType, out spec.Bit1, out spec.Poor1, out spec.Med1);
                if (rock2 != null) ResolveOreBits(minerals, rock2, out spec.Bit2, out spec.Poor2, out spec.Med2);
                if (spec.Bit1 == 0 && spec.Bit2 == 0)
                    problems.Add($"region {r.Key}: no loose '{want}' blocks for its rocks, skipped");
                else r.OreBits.Add(spec);
            }
        }

        if (r.Devastation > 0)
        {
            var devSoil = new List<int>();
            for (int n = 0; n <= 10; n++)
            {
                Block b = sapi.World.GetBlock(new AssetLocation("game", "devastatedsoil-" + n));
                if (b != null) devSoil.Add(b.BlockId);
            }
            r.DevSoilIds = devSoil.ToArray();
            r.DrockId = sapi.World.GetBlock(new AssetLocation("game", "drock"))?.BlockId ?? 0;
            var growth = new List<int>();
            foreach (string t in new[] { "thorns", "bush", "shard" })
            {
                Block b = sapi.World.GetBlock(new AssetLocation("game", "devgrowth-" + t));
                if (b != null) growth.Add(b.BlockId);
            }
            r.DevGrowthIds = growth.ToArray();
            if (devSoil.Count == 0) { r.Devastation = 0; problems.Add($"region {r.Key}: no devastatedsoil blocks, devastation off"); }
        }

        // Ground cover blocks. Grass tufts and loose granite stones are on by
        // default everywhere; sticks and leaf litter only where asked.
        var grassIds = new List<int>();
        foreach (string g in new[] { "veryshort", "short", "mediumshort", "medium", "tall" })
        {
            Block b = sapi.World.GetBlock(new AssetLocation("game", $"tallgrass-{g}-free"));
            if (b != null) grassIds.Add(b.BlockId);
        }
        r.GrassIds = grassIds.ToArray();
        // Loose stones match the region's own geology: primary rock, and the
        // second rock where the underground blend favours it.
        r.LooseStoneId = sapi.World.GetBlock(new AssetLocation("game", $"loosestones-{r.RockType}-free"))?.BlockId
            ?? sapi.World.GetBlock(new AssetLocation("game", "loosestones-peridotite-free"))?.BlockId ?? 0;
        if (rock2 != null)
            r.LooseStoneId2 = sapi.World.GetBlock(new AssetLocation("game", $"loosestones-{rock2}-free"))?.BlockId ?? 0;
        r.LooseStickId = sapi.World.GetBlock(new AssetLocation("game", "loosestick-free"))?.BlockId ?? 0;

        if (r.Litter > 0)
        {
            // The full grass-coverage gradient: index 0 is bare leaf litter,
            // 7 is nearly grass. Litter stamps a disc under each tree, leafiest
            // at the trunk, so it needs the whole run.
            var litterIds = new List<int>();
            for (int n = 0; n <= 7; n++)
            {
                Block b = sapi.World.GetBlock(new AssetLocation("game", "forestfloor-" + n));
                if (b != null) litterIds.Add(b.BlockId);
            }
            r.LitterIds = litterIds.ToArray();
            if (litterIds.Count == 0) { r.Litter = 0; problems.Add($"region {r.Key}: no forestfloor blocks, litter off"); }
        }

        if (!string.IsNullOrWhiteSpace(bushStr))
        {
            // bushes=raspberry:0.01,birch:0.008  (fruiting bush types, or
            // "birch" for a dwarf birch shrub grown by the game's tree gen)
            foreach (string entry in bushStr.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split(':');
                string kind = parts[0].Trim().ToLowerInvariant();
                double chance = parts.Length > 1 ? Math.Clamp(ParseD(parts[1], 0.01), 0, 1) : 0.01;
                if (kind.Length == 0 || chance <= 0) continue;

                if (kind == "birch")
                {
                    ITreeGenerator shrub = FindTreeGenerator("dwarfbirch");
                    if (shrub != null) r.Bushes.Add(new BushSpec { Shrub = shrub, Chance = chance });
                    else problems.Add($"region {r.Key}: no dwarf birch generator, birch bushes off");
                    continue;
                }

                Block bush = sapi.World.GetBlock(new AssetLocation("game", $"fruitingbush-wild-{kind}-free"));
                if (bush != null) r.Bushes.Add(new BushSpec { BlockId = bush.BlockId, Chance = chance });
                else problems.Add($"region {r.Key}: no bush '{kind}', skipped");
            }
        }

        if (!string.IsNullOrWhiteSpace(scatterStr))
        {
            // scatter=cornflower:0.01,fieldmushroom:0.006,eaglefern:0.02
            // Friendly names resolve through the common decor prefixes; a full
            // block code also works.
            foreach (string entry in scatterStr.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split(':');
                string name = parts[0].Trim().ToLowerInvariant();
                double chance = parts.Length > 1 ? Math.Clamp(ParseD(parts[1], 0.01), 0, 1) : 0.01;
                if (name.Length == 0 || chance <= 0) continue;
                Block b = ResolveDecor(name);
                if (b != null) r.Scatter.Add(new BushSpec { BlockId = b.BlockId, Chance = chance });
                else problems.Add($"region {r.Key}: no decor block '{name}', skipped");
            }
        }

        if (r.Lilies > 0)
        {
            r.LilyId = sapi.World.GetBlock(new AssetLocation("game", "waterlily"))?.BlockId ?? 0;
            if (r.LilyId == 0) { r.Lilies = 0; problems.Add($"region {r.Key}: no waterlily block, lilies off"); }
        }

        if (r.Shells > 0)
        {
            var shellIds = new List<int>();
            foreach (string type in new[] { "scallop", "sundial", "turritella", "clam", "conch", "seastar", "volute" })
                foreach (string color in new[] { "latte", "plain", "seafoam", "darkpurple", "cinnamon", "turquoise" })
                {
                    Block b = sapi.World.GetBlock(new AssetLocation("game", $"seashell-{type}-{color}"));
                    if (b != null) shellIds.Add(b.BlockId);
                }
            r.ShellIds = shellIds.ToArray();
            if (shellIds.Count == 0) { r.Shells = 0; problems.Add($"region {r.Key}: no seashell blocks, shells off"); }
        }

        if (r.Boulders > 0)
        {
            r.BoulderId = sapi.World.GetBlock(new AssetLocation("game", $"looseboulders-{r.RockType}-free"))?.BlockId
                ?? sapi.World.GetBlock(new AssetLocation("game", "looseboulders-peridotite-free"))?.BlockId ?? 0;
            if (r.BoulderId == 0) { r.Boulders = 0; problems.Add($"region {r.Key}: no boulder block, boulders off"); }
        }

        if (r.Clay > 0)
        {
            r.ClayId = sapi.World.GetBlock(new AssetLocation("game", "rawclay-blue-none"))?.BlockId ?? 0;
            r.ClaySparseId = sapi.World.GetBlock(new AssetLocation("game", "rawclay-blue-verysparse"))?.BlockId ?? 0;
            if (r.ClayId == 0) { r.Clay = 0; problems.Add($"region {r.Key}: no rawclay block, clay off"); }
        }
        return r;
    }

    // cave <char> heading=auto|<deg> dip=12 length=80 radius=2.6 squash=0.72
    //             weave=0.5 branches=2 branchdepth=2 branchradius=0.85
    //             depth=60 mouth=2 ores=copper:0.05 seed=<n>
    // heading is in MAP degrees (0 = up on the map, 90 = right), so a shape
    // stays valid under rotate=; auto aims the tunnel at the island's centre.
    private static CaveDef ParseCave(string[] tok, List<string> problems)
    {
        var d = new CaveDef();
        for (int i = 2; i < tok.Length; i++)
        {
            int eq = tok[i].IndexOf('=');
            if (eq <= 0) continue;
            string k = tok[i].Substring(0, eq).ToLowerInvariant();
            string v = tok[i].Substring(eq + 1);
            switch (k)
            {
                case "heading":
                    if (!v.Equals("auto", StringComparison.OrdinalIgnoreCase)) d.HeadingDeg = ParseD(v, 0);
                    break;
                case "dip": d.DipDeg = Math.Clamp(ParseD(v, 12), 0, 60); break;
                case "length": d.Length = Math.Clamp(ParseD(v, 80), 8, 600); break;
                case "radius": d.Radius = Math.Clamp(ParseD(v, 2.6), 1.2, 8); break;
                case "squash": d.Squash = Math.Clamp(ParseD(v, 0.72), 0.4, 1.5); break;
                case "weave": d.Weave = Math.Clamp(ParseD(v, 0.5), 0, 1); break;
                case "scale": d.Scale = Math.Clamp(ParseD(v, 1), 0.5, 4); break;
                case "branches": d.Branches = (int)Math.Clamp(ParseD(v, 2), 0, 8); break;
                case "branchdepth": d.BranchDepth = (int)Math.Clamp(ParseD(v, 2), 0, 4); break;
                case "branchlen": d.BranchLen = Math.Clamp(ParseD(v, 0.5), 0.2, 1.2); break;
                case "branchradius": d.BranchRadius = Math.Clamp(ParseD(v, 0.85), 0.3, 1.2); break;
                case "depth": d.Depth = Math.Clamp(ParseD(v, 60), 4, 200); break;
                case "mouth": d.Mouth = (int)Math.Clamp(ParseD(v, 2), 0, 30); break;
                case "entry": d.Entry = (int)Math.Clamp(ParseD(v, 10), 0, 60); break;
                case "seed": d.Seed = (uint)Math.Abs((long)ParseD(v, 0)); break;
                case "ores":
                {
                    string[] parts = v.Split(':');
                    d.OreName = parts[0].Trim().ToLowerInvariant();
                    d.OreChance = parts.Length > 1 ? Math.Clamp(ParseD(parts[1], 0.04), 0, 1) : 0.04;
                    if (d.OreName.Length == 0 || d.OreChance <= 0) { d.OreName = null; d.OreChance = 0; }
                    break;
                }
            }
        }
        return d;
    }

    // The surface-cluster set for one rock: loose bit + shallow ore blocks,
    // taking the first candidate mineral that occurs in this rock.
    private void ResolveOreBits(string[] minerals, string rock, out int bit, out int poor, out int med)
    {
        bit = poor = med = 0;
        foreach (string mineral in minerals)
        {
            int b = sapi.World.GetBlock(new AssetLocation("game", $"looseores-{mineral}-{rock}-free"))?.BlockId ?? 0;
            if (b == 0) continue;
            bit = b;
            poor = sapi.World.GetBlock(new AssetLocation("game", $"ore-poor-{mineral}-{rock}"))?.BlockId ?? 0;
            med = sapi.World.GetBlock(new AssetLocation("game", $"ore-medium-{mineral}-{rock}"))?.BlockId ?? 0;
            return;
        }
    }

    // A decor name for scatter=: a full block code, or a friendly short name
    // tried against the common decoration block families.
    private Block ResolveDecor(string name)
    {
        foreach (string code in new[]
        {
            name,
            $"flower-{name}-free",
            $"mushroom-{name}-normal",
            $"fern-{name}",
            $"fern-{name}-free",
            $"herb-{name}-normal",
            $"tallgrass-{name}-free"
        })
        {
            Block b = sapi.World.GetBlock(new AssetLocation("game", code));
            if (b != null) return b;
        }
        return null;
    }

    // ores=copper:rich,iron:medium  (richness: sparse|medium|rich|abundant or 0..1)
    private void ParseOres(string spec, string rockType, long seed, List<OreSpec> into, List<string> problems)
    {
        int idx = 0;
        foreach (string entry in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split(':');
            string want = parts[0].Trim();
            if (want.Length == 0) continue;
            string rich = parts.Length > 1 ? parts[1].Trim() : "medium";

            string[] candidates = OreAliases.TryGetValue(want, out string[] c) ? c : new[] { want.ToLowerInvariant() };

            OreSpec found = null;
            foreach (string mineral in candidates)
            {
                if (Array.IndexOf(OreMinerals, mineral) < 0) continue;
                var o = new OreSpec
                {
                    Name = mineral,
                    Noise = NormalizedSimplexNoise.FromDefaultOctaves(2, 1 / 11.0, 0.5, seed + 100 + idx),
                    PoorId = OreId("poor", mineral, rockType),
                    MediumId = OreId("medium", mineral, rockType),
                    RichId = OreId("rich", mineral, rockType),
                    BountifulId = OreId("bountiful", mineral, rockType)
                };
                // Ungraded minerals (coal, quartz, sulfur, olivine...) have a
                // single block with no grade segment: use it for every grade.
                if (o.PoorId == 0 && o.MediumId == 0 && o.RichId == 0 && o.BountifulId == 0)
                {
                    int u = sapi.World.GetBlock(new AssetLocation("game", $"ore-{mineral}-{rockType}"))?.BlockId ?? 0;
                    if (u != 0) o.PoorId = o.MediumId = o.RichId = o.BountifulId = u;
                }
                o.Threshold = CalibrateThreshold(o.Noise, RichnessToDensity(rich), seed + idx);
                if (o.PoorId != 0 || o.MediumId != 0 || o.RichId != 0 || o.BountifulId != 0) { found = o; break; }
            }
            idx++;

            if (found == null) problems.Add($"no '{want}' ore occurs in {rockType}");
            else into.Add(found);
        }
    }

    private int OreId(string grade, string mineral, string rock)
        => sapi.World.GetBlock(new AssetLocation("game", $"ore-{grade}-{mineral}-{rock}"))?.BlockId ?? 0;

    // Richness is a target DENSITY: the fraction of the island's stone that is
    // ore. A number is the fraction directly (0.02 = 2 blocks per 100, about
    // the ceiling a prospecting pick reads in rich natural terrain).
    private static double RichnessToDensity(string rich)
    {
        if (double.TryParse(rich, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return Math.Clamp(v, 0.0005, 0.15);
        return rich.ToLowerInvariant() switch
        {
            "rare" => 0.002,
            "sparse" => 0.005,
            "rich" => 0.02,
            "abundant" => 0.035,
            _ => 0.01 // medium
        };
    }

    // The noise's value distribution is not analytic, so hitting a REAL target
    // density needs an empirical quantile: sample the field widely and take
    // the cutoff that passes exactly the requested fraction of blocks.
    private static double CalibrateThreshold(NormalizedSimplexNoise noise, double density, long seed)
    {
        const int n = 4096;
        var samples = new double[n];
        var rnd = new Random((int)(seed & 0x7fffffff));
        for (int i = 0; i < n; i++)
            samples[i] = noise.Noise(rnd.NextDouble() * 8192.0, rnd.NextDouble() * 512.0, rnd.NextDouble() * 8192.0);
        Array.Sort(samples);
        int idx = Math.Clamp((int)((1.0 - density) * n), 0, n - 1);
        return samples[idx];
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Tick loop
    // ─────────────────────────────────────────────────────────────────────
    private void StartIslandJob(IslandJob job)
    {
        // Terrain already rendered at worldgen: jump straight to the
        // decoration phase, or to the finish passes if nothing plants.
        if (job.SkipTerrain && job.Phase == 0)
        {
            job.Phase = 1;
            if (!HasForest(job) && !HasFlora(job)) job.I = job.Total;
        }
        _islandJob = job;
        _islandListenerId = sapi.Event.RegisterGameTickListener(OnIslandTick, 40);
    }

    private void OnIslandTick(float dt)
    {
        IslandJob job = _islandJob;
        if (job == null) return;

        // Before writing a single block, force-load every chunk column under
        // the island. Bulk writes into unloaded chunks are silently lost (the
        // missing-slice bug on big islands), and the finish passes then trip
        // over null heightmaps and unloaded map regions. 40ms ticks: cap the
        // wait at ~30s and proceed with a warning rather than hang forever.
        if (job.Dim == 0 && !job.ChunksLoaded)
        {
            int cs = GlobalConstants.ChunkSize;
            int px1 = FloorDiv(job.MinX, cs) - 1, px2 = FloorDiv(job.MinX + job.W - 1, cs) + 1;
            int pz1 = FloorDiv(job.MinZ, cs) - 1, pz2 = FloorDiv(job.MinZ + job.H - 1, cs) + 1;
            if (!job.ChunksRequested)
            {
                job.ChunksRequested = true;
                sapi.WorldManager.LoadChunkColumnPriority(px1, pz1, px2, pz2,
                    new ChunkLoadOptions { KeepLoaded = true });
            }
            int missing = 0;
            for (int cx = px1; cx <= px2; cx++)
                for (int cz = pz1; cz <= pz2; cz++)
                    if (sapi.WorldManager.GetChunk(cx, 0, cz) == null) missing++;
            if (missing == 0)
            {
                job.ChunksLoaded = true;
            }
            else
            {
                // Keep waiting as long as the loader is making progress; only
                // give up after 30s of NO new columns (big islands can take
                // longer than 30s in total, and that is fine).
                if (missing < job.LastMissing)
                {
                    if (job.LastMissing == int.MaxValue)
                        ReportIsland(job, $"Loading {missing} chunk column(s) under the island before building...");
                    job.LastMissing = missing;
                    job.WaitTicks = 0;
                }
                if (job.WaitTicks++ < 750) return;
                job.ChunksLoaded = true;
                ReportIsland(job, $"WARNING: {missing} chunk column(s) never loaded (30s without progress); parts of the island may be missing. Stand closer to the target area and regenerate.");
            }
        }

        var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        var pos = new BlockPos(0, 0, 0, job.Dim);

        int budget = job.ColumnsPerTick;
        while (budget-- > 0 && job.HasNext)
        {
            int x = job.MinX + (int)(job.I % job.W);
            int z = job.MinZ + (int)(job.I / job.W);
            job.I++;

            if (job.Phase == 0) { if (FillColumn(job, ba, pos, x, z)) job.Placed++; }
            else PlantColumn(job, ba, pos, x, z);
        }
        ba.Commit();

        if (job.HasNext) return;

        if (job.Phase == 0 && (HasForest(job) || HasFlora(job)))
        {
            job.Phase = 1;
            job.I = 0;
            return;
        }

        sapi.Event.UnregisterGameTickListener(_islandListenerId);
        _islandListenerId = 0;
        _islandJob = null;
        try
        {
            string extra = PlaceLandmarkTrees(job);
            extra += PlaceMarkerBlocks(job);
            int oreBits = StampOreBitClusters(job);
            int devPatches = StampDevastation(job);
            int pumpkinPatches = StampPumpkinPatches(job);
            string caveNote = CarveCaves(job);
            int climRegions = StampClimate(job);
            string depositNote = SyncHeightmapsAndDeposits(job);

            string done = $"Island complete: {job.Placed} column(s)";
            if (job.Trees > 0) done += $", {job.Trees} tree(s)";
            if (job.Plants > 0) done += $", {job.Plants} plant(s)";
            if (oreBits > 0) done += $", {oreBits} surface ore bit(s) in {job.OreBitCenters.Count} cluster(s)";
            if (devPatches > 0) done += $", {devPatches} devastated patch(es)";
            if (pumpkinPatches > 0) done += $", {pumpkinPatches} pumpkin patch(es)";
            done += caveNote;
            done += depositNote;
            if (climRegions > 0) done += $", climate retinted across {climRegions} map region(s)";
            else if (job.HasClimate) done += ". WARNING: climate= touched no loaded map regions";
            ReportIsland(job, done + ". " + extra);
        }
        catch (Exception e)
        {
            // A finish pass failing must never eat the whole island silently:
            // tell the player what broke instead of dying in the tick handler.
            sapi.Logger.Error(e);
            ReportIsland(job, "Island FAILED in a finish pass: " + e.Message + ". See server-main.log; the terrain itself is placed.");
        }
        finally
        {
            _islandBusy = false;
        }
    }

    private static bool HasForest(IslandJob job)
    {
        if (job.Shape == null) return job.ForestDensity > 0 && job.ForestTrees.Count > 0;
        foreach (var r in job.Shape.Regions.Values)
            if (r.Forest > 0 && r.Trees.Count > 0) return true;
        return false;
    }

    private static bool HasFlora(IslandJob job)
    {
        if (job.Shape == null) return false;
        foreach (var r in job.Shape.Regions.Values)
            if (r.Cattails > 0 || r.Flax > 0 || r.OreBits.Count > 0 || r.Devastation > 0 || r.Sticks > 0 || r.Litter > 0
                || r.Lilies > 0 || r.Shells > 0 || r.Boulders > 0 || r.Clay > 0 || r.Pumpkins > 0
                || r.Bushes.Count > 0 || r.Scatter.Count > 0 || r.WildGrass != 0 || r.Stones != 0) return true;
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Terrain
    // ─────────────────────────────────────────────────────────────────────

    private bool FillColumn(IslandJob job, IBulkBlockAccessor ba, BlockPos pos, int x, int z)
    {
        // Terrain height BEFORE we touch this column. Each column is visited
        // once, so this is always the original sea floor / ground.
        pos.Set(x, job.SeaLevel, z);
        int naturalY = sapi.World.BlockAccessor.GetTerrainMapheightAt(pos);

        if (!ColumnSurface(job, x, z, naturalY, out int topY, out bool underwater, out int topMat, out bool nearIsland, out int waterTopY, out Region reg))
            return false;

        ColumnPalette(job, reg, out int stoneId, out int stoneId2, out int sandId, out int soilId, out int grassId, out List<OreSpec> ores);

        // Root the column on whichever is lower: the existing sea floor or our
        // own surface. That fills the gap down to the seabed so nothing floats,
        // and when we are carving DOWN it collapses to just the surface block.
        int fillFrom = Math.Min(naturalY, topY);
        fillFrom = Math.Max(fillFrom, Math.Max(1, job.SeaLevel - job.MaxDepth));

        for (int y = fillFrom; y <= topY; y++)
        {
            pos.Set(x, y, z);
            ba.SetBlock(ColumnBlockAt(job, reg, ores, topMat, y, topY, stoneId, stoneId2, sandId, soilId, grassId, x, z), pos);
            if (y < job.SeaLevel) ba.SetBlock(0, pos, BlockLayersAccess.Fluid);
        }

        // Water: ocean up to sea level, or a pond up to its own local level.
        // Ocean fill uses SALT water to match the surrounding biome's tint; an
        // inland pond is freshwater.
        int waterId = (reg != null && reg.Pond > 0) ? job.WaterId : job.SaltWaterId;
        int clearTop = Math.Max(Math.Max(naturalY, waterTopY),
            nearIsland ? job.SeaLevel + job.DomeHeight + 6 : job.SeaLevel);
        for (int y = topY + 1; y <= clearTop; y++)
        {
            pos.Set(x, y, z);
            ba.SetBlock(0, pos);
            ba.SetBlock(y <= waterTopY ? waterId : 0, pos, BlockLayersAccess.Fluid);
        }
        return true;
    }

    // The per-area palette: region overrides, else the island's global one.
    private static void ColumnPalette(IslandJob job, Region reg, out int stoneId, out int stoneId2, out int sandId, out int soilId, out int grassId, out List<OreSpec> ores)
    {
        stoneId = reg?.StoneId ?? job.StoneId;
        stoneId2 = reg?.StoneId2 ?? 0;
        sandId = reg?.SandId ?? job.SandId;
        soilId = reg?.SoilId ?? job.SoilId;
        grassId = reg?.GrassId ?? job.GrassId;
        ores = reg?.Ores ?? job.Ores;
    }

    // The block for one depth of a column: surface cap, the soil/sand skin
    // beneath it, then ore-or-stone. Shared by the live builder and the
    // worldgen renderer so both produce the identical island.
    private int ColumnBlockAt(IslandJob job, Region reg, List<OreSpec> ores, int topMat, int y, int topY,
        int stoneId, int stoneId2, int sandId, int soilId, int grassId, int x, int z)
    {
        const int skin = 3;
        if (y == topY)
            return topMat == SurfGrass ? grassId
                // surface=barren land gets patchy sparse grass; pond beds
                // (the other SurfSoil source) stay bare mud.
                : topMat == SurfSoil ? (reg != null && reg.Pond == 0 && reg.SparseGrassId != 0
                    ? (job.SurfNoise.Noise(x * 0.31, z * 0.31) > 0.5 ? reg.SparseGrassId2 : reg.SparseGrassId)
                    : soilId)
                : topMat == SurfPeat ? (job.SurfNoise.Noise(x * 0.31, z * 0.31) > 0.45 ? reg.PeatSparseId : reg.PeatId)
                : topMat == SurfSand ? sandId : stoneId;
        if (y > topY - skin)
            return topMat == SurfGrass || topMat == SurfSoil ? soilId
                : topMat == SurfPeat ? reg.PeatId
                : topMat == SurfSand ? sandId : stoneId;
        return PickStone(job, ores, stoneId, stoneId2, x, y, z);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Worldgen island rendering
    //
    //  On a Rustfall (or pure-ocean plan) world the plan's islands are ALSO
    //  rendered during chunk generation, so their terrain exists the moment
    //  a chunk is born. Vanilla refuses to release a joining player until
    //  the spawn chunk column is generated, so a starter island at map
    //  center is under the player's feet on the very first frame of a
    //  brand-new world; there is no window where they swim over bare ocean
    //  while the island builds. The live tick builder then only decorates
    //  (trees, flora, ore bits, caves), sharing the same deterministic seed
    //  so both stages agree on every block.
    // ─────────────────────────────────────────────────────────────────────

    private void InitWorldgenIslandJobs()
    {
        _wgIslandJobs = null;
        var wc = sapi.WorldManager.SaveGame.WorldConfiguration;
        if (!wc.GetBool("lgPureOcean", false) && !wc.GetBool("rustfallWorld", false)) return;

        // Pure ocean from the very first chunk: drop the vanilla forced
        // spawn land NOW, before any map region generates. Without this the
        // spawn chunks are born as a continent the later setup must wipe.
        // requireLandAt is a public field (and the ocean map generator holds
        // a reference to the same list, so clearing it here sticks).
        var genMaps = sapi.ModLoader.GetModSystem<Vintagestory.ServerMods.GenMaps>();
        if (genMaps != null) genMaps.requireLandAt.Clear();
        else sapi.Logger.Warning("[landmassgenerator] GenMaps not found; the vanilla forced spawn land stays and a vanilla landmass will appear at map center.");

        string planName = sapi.WorldManager.SaveGame.GetData<string>("lgWorldPlanName", null) ?? "worldplan";
        string planPath = Path.Combine(shapeFolder, planName + ".txt");
        if (!File.Exists(planPath))
        {
            if (!wc.GetBool("rustfallWorld", false)) return;
            try { File.WriteAllText(planPath, DefaultWorldPlan); }
            catch (Exception e) { sapi.Logger.Error("[landmassgenerator] Could not write the default world plan: {0}", e.Message); return; }
        }

        var jobs = new List<IslandJob>();
        try
        {
            foreach (string raw in File.ReadAllLines(planPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4 || !parts[0].Equals("island", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(parts[1], out int mapX) || !int.TryParse(parts[2], out int mapZ)) continue;

                var opt = ParseIslandOptions(string.Join(" ", parts, 3, parts.Length - 3));
                if (!opt.ContainsKey("seed")) opt["seed"] = PlanIslandSeed(mapX, mapZ).ToString();
                // NOT DefaultSpawnPosition: vanilla computes the map-middle
                // spawn AFTER worldgen init (it needs the spawn chunks), so
                // reading it here throws. Map middle IS the default spawn.
                int ox = sapi.WorldManager.MapSizeX / 2 + mapX;
                int oz = sapi.WorldManager.MapSizeZ / 2 + mapZ;
                var problems = new List<string>();
                IslandJob job = BuildIslandJob(opt, ox, oz, 0, problems, out string err);
                if (job == null)
                {
                    sapi.Logger.Warning("[landmassgenerator] Plan island at {0},{1} cannot render during worldgen: {2}", mapX, mapZ, err);
                    continue;
                }
                jobs.Add(job);
            }
        }
        catch (Exception e)
        {
            sapi.Logger.Error("[landmassgenerator] Reading the world plan for worldgen island rendering failed:");
            sapi.Logger.Error(e);
        }
        if (jobs.Count > 0)
        {
            _wgIslandJobs = jobs;
            sapi.Logger.Notification("[landmassgenerator] {0} plan island(s) will render during chunk generation.", jobs.Count);

            // Right after this init returns, vanilla blocking-loads
            // SpawnChunksWidth wide chunk columns around map middle, still
            // behind the loading screen. Widen that so a spawn island's
            // land and tree margin sit fully inside the pre-loaded square:
            // that is what lets the setup decorate it before the world
            // opens. Capped so a huge island cannot stall startup; past
            // the cap decoration falls back to the paced live pass.
            int cs = GlobalConstants.ChunkSize;
            int midCx = sapi.WorldManager.MapSizeX / 2 / cs;
            int midCz = sapi.WorldManager.MapSizeZ / 2 / cs;
            int need = 0;
            foreach (var job in jobs)
            {
                GetDecorationChunkRect(job, out int cx1, out int cz1, out int cx2, out int cz2);
                if (cx2 < midCx - 10 || cx1 > midCx + 10 || cz2 < midCz - 10 || cz1 > midCz + 10) continue;
                need = Math.Max(need, Math.Max(Math.Max(midCx - cx1, cx2 - midCx), Math.Max(midCz - cz1, cz2 - midCz)));
            }
            need = Math.Min(need, 10);
            if (need > 0 && Vintagestory.Server.MagicNum.SpawnChunksWidth < need * 2)
            {
                Vintagestory.Server.MagicNum.SpawnChunksWidth = need * 2;
                sapi.Logger.Notification("[landmassgenerator] Spawn chunk pregeneration widened to cover the spawn island ({0} chunks across), so it can be decorated before the world opens.", need * 2 + 1);
            }
        }
    }

    // One island, one seed, forever: derived from the world seed and the
    // island's plan coordinates, so the worldgen renderer and the live
    // decorator build the exact same island, on any restart.
    private long PlanIslandSeed(int mapX, int mapZ)
    {
        long seed = unchecked(sapi.World.Seed * 6364136223846793005L + mapX * 9007199254740881L + mapZ * 2862933555777941757L);
        seed &= long.MaxValue;
        return seed == 0 ? 987654321L : seed;
    }

    private bool ChunkTouchesWorldgenIsland(int cx, int cz)
    {
        var jobs = _wgIslandJobs;
        if (jobs == null) return false;
        int bx = cx * GlobalConstants.ChunkSize, bz = cz * GlobalConstants.ChunkSize;
        for (int j = 0; j < jobs.Count; j++)
        {
            var job = jobs[j];
            if (bx + GlobalConstants.ChunkSize - 1 >= job.MinX && bx <= job.MinX + job.W - 1
                && bz + GlobalConstants.ChunkSize - 1 >= job.MinZ && bz <= job.MinZ + job.H - 1) return true;
        }
        return false;
    }

    private void OnChunkColumnGenIslands(IChunkColumnGenerateRequest request)
    {
        var jobs = _wgIslandJobs;
        if (jobs == null) return;
        int cs = GlobalConstants.ChunkSize;
        int bx = request.ChunkX * cs;
        int bz = request.ChunkZ * cs;
        IMapChunk mapChunk = request.Chunks[0].MapChunk;
        for (int j = 0; j < jobs.Count; j++)
        {
            IslandJob job = jobs[j];
            if (bx + cs - 1 < job.MinX || bx > job.MinX + job.W - 1 || bz + cs - 1 < job.MinZ || bz > job.MinZ + job.H - 1) continue;
            for (int lz = 0; lz < cs; lz++)
            {
                int z = bz + lz;
                if (z < job.MinZ || z >= job.MinZ + job.H) continue;
                for (int lx = 0; lx < cs; lx++)
                {
                    int x = bx + lx;
                    if (x < job.MinX || x >= job.MinX + job.W) continue;
                    FillColumnWorldgen(job, request.Chunks, mapChunk, lx, lz, x, z);
                }
            }
        }
    }

    // FillColumn's twin for a chunk column that is still being generated:
    // same surface math, same materials, but writes go straight into the
    // chunk data and the worldgen heightmaps get updated, so the spawn
    // resolver and the sunlight pass (which run later) see the island.
    private void FillColumnWorldgen(IslandJob job, IServerChunk[] chunks, IMapChunk mapChunk, int lx, int lz, int x, int z)
    {
        int cs = GlobalConstants.ChunkSize;
        int hidx = lz * cs + lx;
        int naturalY = mapChunk.WorldGenTerrainHeightMap[hidx];

        if (!ColumnSurface(job, x, z, naturalY, out int topY, out bool underwater, out int topMat, out bool nearIsland, out int waterTopY, out Region reg))
            return;

        ColumnPalette(job, reg, out int stoneId, out int stoneId2, out int sandId, out int soilId, out int grassId, out List<OreSpec> ores);

        int maxY = sapi.WorldManager.MapSizeY - 2;
        if (topY > maxY) topY = maxY;
        int fillFrom = Math.Min(naturalY, topY);
        fillFrom = Math.Max(fillFrom, Math.Max(1, job.SeaLevel - job.MaxDepth));

        for (int y = fillFrom; y <= topY; y++)
        {
            int idx = (y % cs * cs + lz) * cs + lx;
            IChunkBlocks data = chunks[y / cs].Data;
            data.SetBlockUnsafe(idx, ColumnBlockAt(job, reg, ores, topMat, y, topY, stoneId, stoneId2, sandId, soilId, grassId, x, z));
            if (y < job.SeaLevel) data.SetFluid(idx, 0);
        }

        int waterId = (reg != null && reg.Pond > 0) ? job.WaterId : job.SaltWaterId;
        int clearTop = Math.Min(maxY, Math.Max(Math.Max(naturalY, waterTopY),
            nearIsland ? job.SeaLevel + job.DomeHeight + 6 : job.SeaLevel));
        for (int y = topY + 1; y <= clearTop; y++)
        {
            int idx = (y % cs * cs + lz) * cs + lx;
            IChunkBlocks data = chunks[y / cs].Data;
            data.SetBlockUnsafe(idx, 0);
            data.SetFluid(idx, y <= waterTopY ? waterId : 0);
        }

        mapChunk.WorldGenTerrainHeightMap[hidx] = (ushort)topY;
        mapChunk.RainHeightMap[hidx] = (ushort)Math.Max(topY, waterTopY);
    }

    // Stone for a below-surface block: an ore vein if one claims it, else the
    // region's primary rock, or its second rock where the blend noise favours it.
    private int PickStone(IslandJob job, List<OreSpec> ores, int stoneId, int stoneId2, int x, int y, int z)
    {
        for (int i = 0; i < ores.Count; i++)
            if (ores[i].TryPick(x, y, z, out int oreId)) return oreId;
        if (stoneId2 != 0 && job.RockBlend.Noise(x, y, z) > 0.5) return stoneId2;
        return stoneId;
    }

    // Where the ground's top block sits, what caps it, and (in shape mode) which
    // region owns it. False means this column is outside the work area.
    private bool ColumnSurface(IslandJob job, int x, int z, int naturalY,
        out int topY, out bool underwater, out int topMat, out bool nearIsland, out int waterTopY, out Region reg)
    {
        return job.Shape != null
            ? ShapeSurface(job, x, z, naturalY, out topY, out underwater, out topMat, out nearIsland, out waterTopY, out reg)
            : RadialSurface(job, x, z, naturalY, out topY, out underwater, out topMat, out nearIsland, out waterTopY, out reg);
    }

    // Drawn island: sample the grid, and turn distance-from-the-coast into height.
    private bool ShapeSurface(IslandJob job, int x, int z, int naturalY,
        out int topY, out bool underwater, out int topMat, out bool nearIsland, out int waterTopY, out Region reg)
    {
        topY = 0; underwater = false; topMat = SurfGrass; nearIsland = false; waterTopY = -1; reg = null;
        ShapeDef s = job.Shape;

        // The sample point is nudged with noise (inside GridPos) so the coast is
        // organic instead of showing the grid's stair-steps.
        bool inGrid = GridPos(job, x, z, out double gx, out double gz, out int cx, out int cz);
        char cell = inGrid ? s.Cells[cx, cz] : '.';

        if (cell != '.' && s.Regions.TryGetValue(cell, out Region r))
        {
            reg = r;
            nearIsland = true;
            double dCoast = Bilinear(s.DistToOcean, s.W, s.H, gx, gz) * job.WorldPerCell;
            // Height and shore come from the SMOOTHED fields so inland region
            // borders ramp instead of stepping; the region itself still decides
            // rock, surface and ore.
            double hFrac = Bilinear(s.HeightField, s.W, s.H, gx, gz);
            double shore = Math.Max(1.0, Bilinear(s.ShoreField, s.W, s.H, gx, gz));
            double rise = job.DomeHeight * hFrac * Smooth(dCoast / shore);
            double rough = (job.SurfNoise.Noise(x, z) - 0.5) * 2.0 * r.Rough * 4.0;
            // No dither anywhere: erosion smooths real terrain, so only the
            // region's own low-frequency rough noise shapes the ground. The
            // smooth terrain and the noise round SEPARATELY: noise only makes a
            // step when it is worth a whole block by itself, so low-rough
            // regions come out perfectly clean. The whole island also sits one
            // block lower than the naive rounding, so the shore ends flush with
            // the water surface and a swimmer can climb out anywhere.
            double bumps = rough * Math.Min(1.0, dCoast / 6.0);
            int landY = (int)Math.Round(job.SeaLevel - 1 + rise) + (int)Math.Round(bumps);
            if (landY < job.SeaLevel - 1) landY = job.SeaLevel - 1;

            if (r.Pond > 0)
            {
                // A pond needs ONE flat water level; per-column noise would tear
                // the surface. The water sits one block below the meadow, flush
                // with its collar, so a swimmer can climb straight out. The bed
                // declines from the edges to full depth like a real pond, not a
                // carved-out box.
                int rimY = PondRim(job, r);
                double dEdge = PondEdgeDist(s, gx, gz) * job.WorldPerCell;
                int depth = 1 + (int)Math.Round((r.Pond - 1) * Smooth(dEdge / 4.0));
                topY = Math.Max(job.SeaLevel - 2, rimY - depth);
                waterTopY = rimY - 1;
                topMat = SurfSoil; // muddy pond bed
            }
            else
            {
                // Land beside a pond flattens to a level collar one block below
                // the meadow, exactly at the water surface: contained, no lip.
                Region pondN = NeighbourPond(s, cx, cz);
                topY = pondN != null ? PondRim(job, pondN) - 1 : landY;
                topMat = SurfaceMat(job, r, x, z);

                // flood=: the region's ground sinks below sea level and the
                // sea flows over it: marsh flats, mangrove shallows, lurking
                // reefs. The depth RAMPS from zero at the nearest dry land
                // (DistToDry) so the meadow descends into the water smoothly
                // instead of stepping off a square edge.
                if (r.Flood > 0 && s.DistToDry != null)
                {
                    double dDry = Bilinear(s.DistToDry, s.W, s.H, gx, gz) * job.WorldPerCell;
                    int cap = job.SeaLevel - 1 - (int)Math.Round(r.Flood * Smooth(dDry / 8.0));
                    if (topY > cap) topY = cap;
                }
                if (topY < job.SeaLevel - 1)
                {
                    underwater = true;
                    waterTopY = job.SeaLevel - 1;
                }
            }
            return true;
        }

        // Ocean: deepen sharply just off the coast, then blend back into the
        // natural sea floor so the edit leaves no rim.
        double dLand = DistToLandContinuous(s, gx, gz) * job.WorldPerCell;
        if (dLand > job.OceanRing) return false; // leave the open ocean alone

        nearIsland = dLand < job.WorldPerCell * 2;
        double deep = job.SeaLevel - 2 - job.Water * Smooth(dLand / (job.OceanRing * 0.45));
        double back = Smooth((dLand - job.OceanRing * 0.55) / (job.OceanRing * 0.45));
        topY = (int)Math.Round(Lerp(deep, naturalY, back));
        underwater = topY < job.SeaLevel;
        waterTopY = underwater ? job.SeaLevel - 1 : -1;
        topMat = topY >= job.SeaLevel - 4 ? SurfSand : SurfRock;
        return true;
    }

    // The meadow level around a pond: raw region height, no noise, so every
    // pond column agrees on it. Water surface and collar sit one below it.
    // Ponds belong in the island interior where the rise has saturated.
    private static int PondRim(IslandJob job, Region pond)
        => job.SeaLevel - 1 + (int)Math.Round(job.DomeHeight * pond.Height);

    // Distance (in cells) from a point inside a pond to the pond's edge, by
    // scanning outward for the nearest non-pond cell. Ponds are tiny, so the
    // small window is plenty and cheap.
    private static double PondEdgeDist(ShapeDef s, double gx, double gz)
    {
        int cx = (int)Math.Floor(gx), cz = (int)Math.Floor(gz);
        double best = 6.0;
        for (int dz = -6; dz <= 6; dz++)
            for (int dx = -6; dx <= 6; dx++)
            {
                int nx = cx + dx, nz = cz + dz;
                bool pond = nx >= 0 && nz >= 0 && nx < s.W && nz < s.H
                    && s.Cells[nx, nz] != '.'
                    && s.Regions.TryGetValue(s.Cells[nx, nz], out Region nr) && nr.Pond > 0;
                if (pond) continue;
                double d = Math.Sqrt((nx + 0.5 - gx) * (nx + 0.5 - gx) + (nz + 0.5 - gz) * (nz + 0.5 - gz));
                if (d < best) best = d;
            }
        return best;
    }

    // The pond region in any of the 8 cells around this one, or null.
    private static Region NeighbourPond(ShapeDef s, int cx, int cz)
    {
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                int nx = cx + dx, nz = cz + dz;
                if (nx < 0 || nz < 0 || nx >= s.W || nz >= s.H) continue;
                char c = s.Cells[nx, nz];
                if (c != '.' && s.Regions.TryGetValue(c, out Region nr) && nr.Pond > 0) return nr;
            }
        return null;
    }

    // The grid position this world column samples, jitter included. Terrain and
    // flora passes share this mapping so they always agree on the cell.
    private static bool GridPos(IslandJob job, int x, int z, out double gx, out double gz, out int cx, out int cz)
    {
        ShapeDef s = job.Shape;
        double jx = (job.JitterX.Noise(x, z) - 0.5) * 2.0 * 0.7;
        double jz = (job.JitterZ.Noise(x, z) - 0.5) * 2.0 * 0.7;
        // Inverse-rotate the world offset into map space, so the island itself
        // comes out rotated clockwise by the command's rotate= angle.
        double ox = x - job.Cx, oz = z - job.Cz;
        double rx = ox * job.RotCos + oz * job.RotSin;
        double rz = -ox * job.RotSin + oz * job.RotCos;
        gx = rx / job.WorldPerCell + s.W / 2.0 + jx;
        gz = rz / job.WorldPerCell + s.H / 2.0 + jz;
        cx = (int)Math.Floor(gx);
        cz = (int)Math.Floor(gz);
        return cx >= 0 && cz >= 0 && cx < s.W && cz < s.H;
    }

    // Distance (in cells) to the nearest land, continuous across the grid
    // border: clamp the sample into the grid, read the distance field there, and
    // add how far outside the sample fell. Without this, out-of-grid columns
    // measured distance to the grid RECTANGLE, which stamped a square ridge on
    // the sea floor around the island.
    private static double DistToLandContinuous(ShapeDef s, double gx, double gz)
    {
        double cx = Math.Clamp(gx, 0, s.W - 1);
        double cz = Math.Clamp(gz, 0, s.H - 1);
        double over = Math.Sqrt((gx - cx) * (gx - cx) + (gz - cz) * (gz - cz));
        return Bilinear(s.DistToLand, s.W, s.H, cx, cz) + over;
    }

    private int SurfaceMat(IslandJob job, Region r, int x, int z)
    {
        int surf = r.Surface;
        // Rocky outcrops speckled with sand.
        if (surf == SurfRockSand)
            surf = job.CoastNoise.Noise(x * 0.9, z * 0.9) > 0.5 ? SurfRock : SurfSand;
        // sandy=: contiguous noise blobs of the region's sand across grass or
        // barren ground, for wind-blown drifts inland.
        if (r.Sandy > 0 && (surf == SurfGrass || surf == SurfSoil)
            && job.SurfNoise.Noise(x * 0.23, z * 0.23) > 1.0 - r.Sandy * 0.62)
            surf = SurfSand;
        return surf;
    }

    // The original radial dome, kept for quick islands with no shape file.
    private bool RadialSurface(IslandJob job, int x, int z, int naturalY,
        out int topY, out bool underwater, out int topMat, out bool nearIsland, out int waterTopY, out Region reg)
    {
        topY = 0; underwater = false; topMat = SurfGrass; nearIsland = false; waterTopY = -1; reg = null;

        double dx = x - job.Cx, dz = z - job.Cz;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist > job.Rmax) return false;

        double dirx = dist > 0.001 ? dx / dist : 0;
        double dirz = dist > 0.001 ? dz / dist : 0;
        double beachAlign = dirx * job.Bvx + dirz * job.Bvz;
        double cliffAlign = dirx * job.Cvx + dirz * job.Cvz;

        double rn = job.CoastNoise.Noise(x, z);
        double edgeR = job.R * (0.9 + 0.2 * rn);
        double t = edgeR > 0.001 ? dist / edgeR : 999;
        nearIsland = t < 1.1;

        double p = 2.2;
        if (beachAlign > 0) p = Lerp(2.2, 1.3, beachAlign);
        if (cliffAlign > 0) p = Lerp(p, 3.8, cliffAlign);

        if (t < 1.0)
        {
            double rise = job.DomeHeight * (1.0 - Math.Pow(t, p));
            if (rise < 0) rise = 0;
            double bump = (job.SurfNoise.Noise(x, z) - 0.5) * 2 * job.BumpAmp * (1 - t);
            topY = (int)Math.Round(job.SeaLevel + rise + bump);
        }
        else
        {
            double over = dist - edgeR;
            double deep = job.SeaLevel - 2 - job.Water * Smooth(over / job.OceanRing);
            double toEdge = Smooth((dist - edgeR) / Math.Max(1.0, job.Rmax - edgeR));
            topY = (int)Math.Round(Lerp(deep, naturalY, toEdge));
        }

        underwater = topY < job.SeaLevel;
        waterTopY = underwater ? job.SeaLevel - 1 : -1;
        if (!underwater)
        {
            bool beachTop = beachAlign > 0.3 && (topY - job.SeaLevel) <= 3;
            bool cliffTop = cliffAlign > 0.3 && t > 0.72;
            topMat = cliffTop ? SurfRock : beachTop ? SurfSand : SurfGrass;
        }
        else topMat = topY >= job.SeaLevel - 4 ? SurfSand : SurfRock;
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Forest and flora
    // ─────────────────────────────────────────────────────────────────────
    private void PlantColumn(IslandJob job, IBulkBlockAccessor ba, BlockPos pos, int x, int z)
    {
        if (!ColumnSurface(job, x, z, job.SeaLevel, out int topY, out bool underwater, out int topMat, out _, out int waterTopY, out Region reg))
            return;

        // Pond water columns: swamp trees stand in the knee-deep rim (a pond
        // region with forest= grows cypress out of the mere), waterlilies
        // float on the surface.
        if (reg != null && reg.Pond > 0)
        {
            job.Rand.InitPositionSeed(x, z);
            if (reg.Forest > 0 && waterTopY - topY == 1
                && TryPlantTree(job, ba, pos, x, z, topY, reg))
            {
                job.Trees++;
                return;
            }
            if (reg.Lilies > 0 && reg.LilyId != 0 && waterTopY > topY && job.Rand.NextDouble() < reg.Lilies)
            {
                pos.Set(x, waterTopY + 1, z);
                ba.SetBlock(reg.LilyId, pos);
                job.Plants++;
            }
            return;
        }

        // Flooded flats (flood=): the region still owns this shallow water.
        // Swamp trees rise straight out of it, and reeds grow IN it where the
        // water is exactly one deep (the game's water coopersreed's limit),
        // in the same clumps the dry-shore reeds use.
        if (underwater && reg != null && reg.Flood > 0 && waterTopY >= topY)
        {
            job.Rand.InitPositionSeed(x, z);
            if (TryPlantTree(job, ba, pos, x, z, topY, reg)) { job.Trees++; return; }
            int depth = waterTopY - topY;
            // Kelp: seaweed stalks rooted on the flooded bed, up to the
            // surface. This is what belongs in SALT shallows.
            if (reg.Kelp > 0 && reg.KelpTopId != 0 && depth >= 1 && job.Rand.NextDouble() < reg.Kelp)
            {
                int stalk = 1 + job.Rand.NextInt(Math.Min(depth, 3));
                for (int i = 1; i <= stalk; i++)
                {
                    pos.Set(x, topY + i, z);
                    ba.SetBlock(i == stalk ? reg.KelpTopId : reg.KelpSectionId, pos);
                }
                job.Plants++;
                return;
            }
            if (reg.Cattails > 0 && reg.WaterCattailId != 0 && depth == 1
                && job.Rand.NextDouble() < ReedChance(job, x, z, reg.Cattails))
            {
                pos.Set(x, topY + 1, z);
                ba.SetBlock(reg.WaterCattailId, pos);
                job.Plants++;
            }
            return;
        }

        // The sea's own one-deep hem: the first ring of carved floor just
        // outside a coast sits exactly one block under the surface, and the
        // game's water reed can stand in it. Ocean columns own no region,
        // so borrow the nearest reed-bearing land region's cattails= within
        // a few cells, clumped by the same noise the shore beds use, so the
        // beds run from the meadow straight out into the water.
        if (underwater && job.Shape != null && waterTopY - topY == 1 && (reg == null || reg.Flood == 0))
        {
            Region reedN = (reg != null && reg.Cattails > 0 && reg.WaterCattailId != 0) ? reg : NeighbourCattails(job, x, z);
            if (reedN != null)
            {
                job.Rand.InitPositionSeed(x, z);
                if (job.Rand.NextDouble() < ReedChance(job, x, z, reedN.Cattails))
                {
                    pos.Set(x, topY + 1, z);
                    ba.SetBlock(reedN.WaterCattailId, pos);
                    job.Plants++;
                }
            }
            return;
        }

        if (underwater || (topMat != SurfGrass && topMat != SurfSand && topMat != SurfRock && topMat != SurfSoil && topMat != SurfPeat)) return;

        job.Rand.InitPositionSeed(x, z);
        if ((topMat == SurfGrass || topMat == SurfPeat) && TryPlantTree(job, ba, pos, x, z, topY, reg)) job.Trees++;
        else if (TryPlantFlora(job, ba, pos, x, z, topY, topMat, reg)) job.Plants++;
    }

    // The chance an eligible waterline or shallow-water column grows a reed.
    // Reeds gather into dense beds where a clump noise runs high, but a
    // thinner scatter grows near water EVERYWHERE, so no island can roll
    // zero reeds. SurfNoise already carries its own 1/22 base frequency;
    // the old gate scaled coordinates by another 0.045 on top of it, which
    // made the "20-40 block" clumps actually ~500 blocks wide, and a whole
    // island chain could sit inside one bare trough (zero cattails anywhere
    // while every other plant placed). Coordinates scaled by 0.7 put the
    // beds at the intended ~30 blocks.
    private static double ReedChance(IslandJob job, int x, int z, double cattails)
        => Math.Min(1.0, cattails * (job.SurfNoise.Noise(x * 0.7, z * 0.7) > 0.55 ? 2.2 : 0.6));

    // The nearest land region within 3 cells that grows reeds, for sea
    // columns that want the in-water hem. Pond regions keep their reeds on
    // the rim, so they do not spill into the sea.
    private static Region NeighbourCattails(IslandJob job, int x, int z)
    {
        if (!GridPos(job, x, z, out _, out _, out int ccx, out int ccz)) return null;
        var shape = job.Shape;
        for (int r = 1; r <= 3; r++)
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    int nx = ccx + dx, nz = ccz + dz;
                    if (nx < 0 || nz < 0 || nx >= shape.W || nz >= shape.H) continue;
                    char c = shape.Cells[nx, nz];
                    if (c == '.') continue;
                    if (shape.Regions.TryGetValue(c, out Region reg)
                        && reg.Cattails > 0 && reg.Pond == 0 && reg.WaterCattailId != 0) return reg;
                }
        return null;
    }

    private bool TryPlantTree(IslandJob job, IBulkBlockAccessor ba, BlockPos pos, int x, int z, int topY, Region reg)
    {
        double density = reg?.Forest ?? job.ForestDensity;
        List<ITreeGenerator> pool = reg?.Trees ?? job.ForestTrees;
        if (density <= 0 || pool.Count == 0) return false;

        if (job.Rand.NextDouble() >= density) return false;

        // Leave a clearing around any landmark tree so it stands alone.
        const int clearing = 28;
        foreach (var m in job.Shape?.Markers ?? new List<TreeMarker>())
        {
            MarkerWorld(job, m.Gx, m.Gz, out int mx, out int mz);
            double ddx = x - mx, ddz = z - mz;
            if (ddx * ddx + ddz * ddz < clearing * clearing) return false;
        }
        if (job.Shape == null)
        {
            double sdx = x - job.Cx, sdz = z - job.Cz;
            if (sdx * sdx + sdz * sdz < 36) return false;
        }

        var tp = new TreeGenParams
        {
            skipForestFloor = false,
            size = (float)(0.8 + job.Rand.NextDouble() * 0.5),
            vinesGrowthChance = 0,
            mossGrowthChance = 0,
            otherBlockChance = 0,
            hemisphere = EnumHemisphere.North,
            treesInChunkGenerated = 0
        };
        // GrowTree expects the GROUND block; it grows the trunk above it itself.
        pos.Set(x, topY, z);
        pool[job.Rand.NextInt(pool.Count)].GrowTree(ba, pos, tp, job.Rand);
        StampLitter(job, ba, pos, x, z, reg);
        return true;
    }

    // A clay column: sparse-grass clay on top so it hides in the meadow, pure
    // clay down through the soil until it touches the rock beneath.
    private static void PlaceClayColumn(IBulkBlockAccessor ba, BlockPos pos, Region clayReg, int x, int z, int topY)
    {
        pos.Set(x, topY, z);
        ba.SetBlock(clayReg.ClaySparseId != 0 ? clayReg.ClaySparseId : clayReg.ClayId, pos);
        for (int i = 1; i <= 3; i++)
        {
            pos.Set(x, topY - i, z);
            ba.SetBlock(clayReg.ClayId, pos);
        }
    }

    // Vanilla's "surfacecopper" deposit, reproduced: a small shallow ore disc
    // in the stone right under the soil (radius ~2.5-4.5, poor/medium grade),
    // with a loose copper bit on the surface over ~a third of its columns
    // (vanilla's surfaceBlockChance is 0.33). Digging under any bit finds the
    // ore. Runs after the plant pass with its own commit; skips columns whose
    // surface is already occupied by something solid like a trunk.
    private int StampOreBitClusters(IslandJob job)
    {
        if (job.OreBitCenters.Count == 0) return 0;
        var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        var pos = new BlockPos(0, 0, 0, job.Dim);
        int bits = 0;

        foreach ((int cxw, int czw, OreBitSpec spec) in job.OreBitCenters)
        {
            job.Rand.InitPositionSeed(cxw, czw);
            double radius = 2.5 + job.Rand.NextDouble() * 2.0;
            int rr = (int)Math.Ceiling(radius);
            for (int dz = -rr; dz <= rr; dz++)
                for (int dx = -rr; dx <= rr; dx++)
                {
                    if (Math.Sqrt(dx * dx + dz * dz) > radius) continue;
                    int x = cxw + dx, z = czw + dz;
                    if (!ColumnSurface(job, x, z, job.SeaLevel, out int topY, out bool uw, out int tm, out _, out _, out Region r2) || uw) continue;
                    if (r2 == null || r2.Pond > 0) continue;

                    bool second = job.RockBlend.Noise(x, topY - 4, z) > 0.5;
                    int poor = second && spec.Poor2 != 0 ? spec.Poor2 : spec.Poor1;
                    int med = second && spec.Med2 != 0 ? spec.Med2 : spec.Med1;
                    int oreId = med != 0 && job.Rand.NextDouble() < 0.35 ? med : poor;
                    if (oreId != 0)
                    {
                        pos.Set(x, topY - 3, z); // first stone block under the soil skin
                        ba.SetBlock(oreId, pos);
                        if (job.Rand.NextDouble() < 0.5) { pos.Set(x, topY - 4, z); ba.SetBlock(oreId, pos); }
                    }

                    int bit = second && spec.Bit2 != 0 ? spec.Bit2 : spec.Bit1;
                    if (bit != 0 && (tm == SurfGrass || tm == SurfRock || tm == SurfSoil) && job.Rand.NextDouble() < 0.33)
                    {
                        pos.Set(x, topY + 1, z);
                        Block existing = sapi.World.BlockAccessor.GetBlock(pos);
                        if (existing.BlockMaterial == EnumBlockMaterial.Wood || existing.BlockMaterial == EnumBlockMaterial.Leaves) continue;
                        ba.SetBlock(bit, pos);
                        bits++;
                    }
                }
        }
        ba.Commit();
        return bits;
    }

    // A devastated-ground patch: a ragged disc of devastatedsoil, heaviest
    // crust at the centre fading to light at the edge, drock pushed into the
    // ground near the middle, devastation growths sprouting from it, and any
    // grass tufts on it cleared. Stamped after the plant pass like ore
    // clusters, for the same overwrite reason.
    private int StampDevastation(IslandJob job)
    {
        if (job.DevastationCenters.Count == 0) return 0;
        var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        var pos = new BlockPos(0, 0, 0, job.Dim);
        int patches = 0;

        foreach ((int cxw, int czw, Region reg) in job.DevastationCenters)
        {
            job.Rand.InitPositionSeed(cxw, czw);
            double radius = 2.5 + job.Rand.NextDouble() * 3.0;
            int rr = (int)Math.Ceiling(radius);
            for (int dz = -rr; dz <= rr; dz++)
                for (int dx = -rr; dx <= rr; dx++)
                {
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d > radius) continue;
                    // ragged, organic edge
                    if (d > radius * 0.6 && job.Rand.NextDouble() < (d / radius - 0.6) * 2.0) continue;

                    int x = cxw + dx, z = czw + dz;
                    if (!ColumnSurface(job, x, z, job.SeaLevel, out int topY, out bool uw, out int tm, out _, out _, out Region r2) || uw) continue;
                    if (r2 == null || r2.Pond > 0) continue;
                    if (tm != SurfGrass && tm != SurfSand && tm != SurfRock && tm != SurfSoil && tm != SurfPeat) continue;

                    double fade = 1.0 - d / radius; // 1 at the centre
                    int idx = Math.Clamp((int)Math.Round(fade * 10.0), 0, reg.DevSoilIds.Length - 1);
                    pos.Set(x, topY, z);
                    ba.SetBlock(reg.DevSoilIds[idx], pos);
                    if (fade > 0.5 && reg.DrockId != 0 && job.Rand.NextDouble() < 0.35)
                    {
                        pos.Set(x, topY - 1, z);
                        ba.SetBlock(reg.DrockId, pos);
                    }

                    pos.Set(x, topY + 1, z);
                    Block existing = sapi.World.BlockAccessor.GetBlock(pos);
                    if (existing.BlockMaterial == EnumBlockMaterial.Wood || existing.BlockMaterial == EnumBlockMaterial.Leaves) continue;
                    int above = 0; // clears any grass tuft unless a growth takes its place
                    if (reg.DevGrowthIds.Length > 0 && job.Rand.NextDouble() < 0.08 + 0.14 * fade)
                        above = reg.DevGrowthIds[job.Rand.NextInt(reg.DevGrowthIds.Length)];
                    ba.SetBlock(above, pos);
                }
            patches++;
        }
        ba.Commit();
        return patches;
    }

    // pumpkins= support: wild pumpkin patches that look GROWN, not scattered.
    // The catch is that a pumpkin vine block carries a block entity that kills
    // itself within seconds unless its parentPlantPos points at a block whose
    // code starts with crop-pumpkin or pumpkin-vine (distance is never
    // checked). So each patch places one mother plant (crop-pumpkin stays put
    // forever on plain soil, since only farmland ticks crops), surrounds it
    // with vines in mixed stages that are all adopted by the mother, puts
    // fruits beside the vines, and mixes rusty debris between them. Uses the
    // plain block accessor, not a bulk one, so the vines' block entities exist
    // immediately for adoption.
    private int StampPumpkinPatches(IslandJob job)
    {
        if (job.PumpkinCenters.Count == 0) return 0;
        IBlockAccessor ba = sapi.World.BlockAccessor;
        var pos = new BlockPos(0, 0, 0, job.Dim);
        int patches = 0;

        foreach (var (cxw, czw, reg) in job.PumpkinCenters)
        {
            if (!PatchGround(job, ba, pos, cxw, czw, out int my)) continue;
            pos.Set(cxw, my + 1, czw);
            ba.SetBlock(reg.MotherIds[job.Rand.NextInt(reg.MotherIds.Length)], pos);
            BlockPos motherPos = pos.Copy();

            int vines = 5 + job.Rand.NextInt(5);
            for (int i = 0; i < vines; i++)
            {
                double ang = job.Rand.NextDouble() * Math.PI * 2.0;
                double dist = 1.2 + job.Rand.NextDouble() * 3.2;
                int vx = cxw + (int)Math.Round(Math.Cos(ang) * dist);
                int vz = czw + (int)Math.Round(Math.Sin(ang) * dist);
                if ((vx == cxw && vz == czw) || !PatchGround(job, ba, pos, vx, vz, out int vy)) continue;

                pos.Set(vx, vy + 1, vz);
                ba.SetBlock(reg.VineIds[job.Rand.NextInt(reg.VineIds.Length)], pos);
                AdoptVine(ba, pos, motherPos);

                // A fruit beside most vines, on its own ground.
                if (reg.FruitIds.Length > 0 && job.Rand.NextDouble() < 0.65)
                {
                    int fx = vx + job.Rand.NextInt(3) - 1, fz = vz + job.Rand.NextInt(3) - 1;
                    if ((fx != cxw || fz != czw) && PatchGround(job, ba, pos, fx, fz, out int fy))
                    {
                        pos.Set(fx, fy + 1, fz);
                        if (ba.GetBlock(pos).Id == 0 || ba.GetBlock(pos).BlockMaterial == EnumBlockMaterial.Plant)
                            ba.SetBlock(reg.FruitIds[job.Rand.NextInt(reg.FruitIds.Length)], pos);
                    }
                }
            }

            // Random debris between the plants.
            int debris = reg.DebrisIds.Length > 0 ? 1 + job.Rand.NextInt(3) : 0;
            for (int i = 0; i < debris; i++)
            {
                int dx = cxw + job.Rand.NextInt(9) - 4, dz = czw + job.Rand.NextInt(9) - 4;
                if (!PatchGround(job, ba, pos, dx, dz, out int dy)) continue;
                pos.Set(dx, dy + 1, dz);
                ba.SetBlock(reg.DebrisIds[job.Rand.NextInt(reg.DebrisIds.Length)], pos);
            }
            patches++;
        }
        return patches;
    }

    // Usable ground for a pumpkin patch block: our own terrain, dry, soil or
    // sand underfoot, and nothing solid (a tree, a boulder) already above.
    private bool PatchGround(IslandJob job, IBlockAccessor ba, BlockPos pos, int x, int z, out int topY)
    {
        topY = 0;
        if (!ColumnSurface(job, x, z, job.SeaLevel, out topY, out bool uw, out int tm, out _, out _, out Region r2) || uw) return false;
        if (r2 == null || r2.Pond > 0) return false;
        if (tm != SurfGrass && tm != SurfSoil && tm != SurfSand && tm != SurfPeat) return false;
        pos.Set(x, topY + 1, z);
        Block above = ba.GetBlock(pos);
        return above.Id == 0 || above.BlockMaterial == EnumBlockMaterial.Plant;
    }

    // Re-parent a just-placed vine's block entity onto the patch's mother
    // plant, through tree attributes so no survival-mod reference is needed.
    // Also pushes its next growth stage half a day out.
    private void AdoptVine(IBlockAccessor ba, BlockPos vinePos, BlockPos motherPos)
    {
        BlockEntity be = ba.GetBlockEntity(vinePos);
        if (be == null) return;
        var tree = new TreeAttribute();
        be.ToTreeAttributes(tree);
        tree.SetInt("parentPlantPosX", motherPos.X);
        tree.SetInt("parentPlantPosY", motherPos.Y);
        tree.SetInt("parentPlantPosZ", motherPos.Z);
        tree.SetDouble("totalHoursForNextStage", sapi.World.Calendar.TotalHours + 12.0);
        be.FromTreeAttributes(tree, sapi.World);
        be.MarkDirty(true);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Caves
    //
    //  cave= support: hand-placed cave systems, carved the way the game's own
    //  GenCaves does it: a tunnel is a 1-block-per-step random walk whose
    //  horizontal and vertical angles drift by momentum-smoothed noise, each
    //  step hollowing a tapered ellipsoid. Borrowed vanilla rules: the carve
    //  radius follows sin(progress*pi) so tunnels taper at both ends, a step
    //  that would touch ANY fluid is skipped whole (a cave next to the ocean
    //  must never breach it), and branches recurse with shorter length.
    //  Added for hand-design: everything is configurable per entrance, the
    //  walk keeps a 3-block roof below the terrain heightmap (no skylights)
    //  except at the mouth, and the path RNG is a fixed xorshift32 so the
    //  localhost previewer replays the exact same cave.
    // ─────────────────────────────────────────────────────────────────────

    // Shared state for one island's carve pass.
    private class CaveWork
    {
        public IslandJob Job;
        public IBulkBlockAccessor Ba;
        public Dictionary<long, int> HeightCache = new();
        public List<(double X, double Y, double Z, double R, double V, CaveDef Def)> Steps = new();
        public int TotalSteps;   // runaway guard across the whole system
        public int Blocks;
    }

    private string CarveCaves(IslandJob job)
    {
        var caves = job.Shape?.Caves;
        if (caves == null || caves.Count == 0) return "";

        var w = new CaveWork { Job = job, Ba = sapi.World.GetBlockAccessorBulkUpdate(true, true) };
        var notes = new List<string>();
        int tunnels = 0;

        foreach (CaveMarker cm in caves)
        {
            CaveDef def = cm.Def;

            // Entrance cell to world, forward-rotated like tree markers.
            double lx = (cm.Gx + 0.5 - job.Shape.W / 2.0) * job.WorldPerCell;
            double lz = (cm.Gz + 0.5 - job.Shape.H / 2.0) * job.WorldPerCell;
            int ex = job.Cx + (int)Math.Round(lx * job.RotCos - lz * job.RotSin);
            int ez = job.Cz + (int)Math.Round(lx * job.RotSin + lz * job.RotCos);

            if (!ColumnSurface(job, ex, ez, job.SeaLevel, out int topY, out bool uw, out _, out _, out _, out Region entReg) || uw)
            {
                notes.Add($"cave at map {cm.Gx},{cm.Gz} has no dry ground, skipped");
                continue;
            }

            // Mouth floor: exactly def.Mouth above sea level. Not clamped to
            // the entrance column's ground: the wall the adit enters is the
            // rising face AHEAD of it, not the ground underfoot, and the
            // level entry section bores horizontally until it is buried.
            int mouthY = Math.Max(job.SeaLevel - 1, job.SeaLevel - 1 + def.Mouth);

            // Heading: map degrees rotated into the world, or straight at the
            // island's centre so "into the island" needs no numbers.
            double hor;
            if (double.IsNaN(def.HeadingDeg))
            {
                hor = Math.Atan2(job.Cz - ez, job.Cx - ex);
            }
            else
            {
                double th = def.HeadingDeg * Math.PI / 180.0;
                double mx = Math.Sin(th), mz = -Math.Cos(th);
                hor = Math.Atan2(mx * job.RotSin + mz * job.RotCos, mx * job.RotCos - mz * job.RotSin);
            }

            // Stable per design: same shape file, same cave, no matter the
            // island seed, so a hand-tuned mine survives regeneration and the
            // previewer can show it. seed= on the cave line rerolls it.
            uint seed = def.Seed != 0 ? def.Seed
                : 0x9E3779B9u ^ (uint)(cm.Gx * 668265263) ^ (uint)(cm.Gz * 2246822519);

            // The bore must BEGIN standing in open air, however far seaward
            // that is: a sand shelf in front of the face used to stop the
            // mouth inside it. Scan along the heading, away from the island,
            // for the first column that is open at mouth height (or water),
            // and anchor the WHOLE mouth (headwall face and walk start)
            // there instead of at the marker cell.
            int openS = 0;
            bool foundOpen = false;
            for (int s = 0; s >= -24; s--)
            {
                int sxc = (int)Math.Floor(ex + 0.5 + Math.Cos(hor) * s);
                int szc = (int)Math.Floor(ez + 0.5 + Math.Sin(hor) * s);
                int g = DesignedGround(w, sxc, szc);
                if (g <= mouthY - 1 || g <= job.SeaLevel - 2) { openS = s; foundOpen = true; break; }
            }
            if (!foundOpen)
                notes.Add($"cave at map {cm.Gx},{cm.Gz}: no open air within 24 blocks seaward of the mouth, entrance may be buried");

            // Terrain that rises one block per step has no wall to bore a
            // horizontal hole into (clearing the thin cover instead cuts an
            // ugly ravine), so the mouth gets a stamped rock HEADWALL: a
            // small outcrop of the local stone, a couple of blocks taller
            // than the tunnel, that the adit visibly enters. Stone ceiling
            // from the first block in.
            StampHeadwall(w, def, ex, ez, mouthY, hor, entReg?.StoneId ?? job.StoneId, openS);

            // Start 4 blocks seaward of the face, in open air, so the
            // horizontal cut runs from daylight through the face.
            double sx = ex + 0.5 + Math.Cos(hor) * (openS - 4);
            double sz = ez + 0.5 + Math.Sin(hor) * (openS - 4);
            CarveTunnel(w, def, sx, mouthY + 1.6, sz, hor,
                def.DipDeg * Math.PI / 180.0, (int)def.Length + 4, def.Radius * def.Scale,
                mouthY + 1.6 - def.Depth, def.Branches, def.BranchDepth, new CaveRand(seed), 7);
            tunnels++;
        }

        w.Ba.Commit();
        int oreBlocks = LineCaveOres(w);

        string note = $", {tunnels} cave(s) carved ({w.Blocks} blocks";
        if (oreBlocks > 0) note += $", {oreBlocks} wall ore";
        note += ")";
        if (notes.Count > 0) note += ". Cave notes: " + string.Join("; ", notes);
        return tunnels > 0 || notes.Count > 0 ? note : "";
    }

    // A rounded outcrop of stone around the cave mouth, reaching 2 blocks
    // over the tunnel's ceiling, shouldering off to the sides and tapering
    // toward the sea. Crucially it is SOLID: it does not just build up low
    // ground, it also replaces the soil inside the strip with stone (ground
    // near a coast is often already at the right height but made of dirt),
    // so the doorway and the first stretch of ceiling are always rock. It
    // never builds out over water. The tunnel is carved through it
    // afterwards, which is what opens the doorway. viewer/app.js mirrors
    // this so the previewer shows the portal.
    private void StampHeadwall(CaveWork w, CaveDef def, int ex, int ez, int mouthY, double hor, int stoneId, int openS)
    {
        IslandJob job = w.Job;
        double r0 = Math.Max(1.5, def.Radius * def.Scale * 0.7);
        double v0 = Math.Max(1.45, r0 * def.Squash);
        double cy0 = mouthY + 1.6;
        double rw = r0 + 3.0;
        double dirx = Math.Cos(hor), dirz = Math.Sin(hor);
        var pos = new BlockPos(0, 0, 0, job.Dim);

        int reach = (int)Math.Ceiling(18 + Math.Abs(openS) + rw);
        for (int zz = ez - reach; zz <= ez + reach; zz++)
            for (int xx = ex - reach; xx <= ex + reach; xx++)
            {
                if (xx < job.MinX || xx >= job.MinX + job.W || zz < job.MinZ || zz >= job.MinZ + job.H) continue;
                double ox = xx - ex, oz = zz - ez;
                double s = ox * dirx + oz * dirz;   // along the heading, into the hill
                double q = -ox * dirz + oz * dirx;  // sideways
                // The strip is anchored at the FACE (openS), which may sit
                // well seaward of the marker cell when a sand shelf pushes
                // the open air out.
                if (s < openS - 1 || s > openS + 18 || Math.Abs(q) > rw) continue;

                int g = DesignedGround(w, xx, zz);
                if (g <= job.SeaLevel - 2) continue; // stay off the water
                double shoulder = (q / rw) * (q / rw);
                int top = (int)Math.Round(cy0 + v0 + 2 - 2.5 * shoulder - Math.Max(0, openS + 1 - s) * 1.2);
                // Solid from just under the mouth floor (or from the ground,
                // whichever is lower) up to the wall top: builds the bluff
                // where the ground is low AND converts dirt to stone where
                // the ground is already high enough.
                int yyStart = Math.Max(job.SeaLevel - 1, Math.Min(g + 1, mouthY - 2));
                for (int yy = yyStart; yy <= top; yy++)
                {
                    pos.Set(xx, yy, zz);
                    w.Ba.SetBlock(stoneId, pos);
                    w.Ba.SetBlock(0, pos, BlockLayersAccess.Fluid);
                    w.Blocks++;
                }
            }
    }

    // One tunnel: walk, carve, then fork branches off recorded points.
    // RNG draw order is FIXED and mirrored by viewer/app.js: 8 doubles per
    // step, plus 1 on a sharp turn, plus 1 on a room event, then 4 doubles +
    // 1 uint per branch. Carving itself never draws, so world state cannot
    // desync the path.
    private void CarveTunnel(CaveWork w, CaveDef def, double x, double y, double z,
        double hor, double dip, int length, double radius, double floorY,
        int branches, int branchDepth, CaveRand rand, int mouthSteps)
    {
        var path = new List<(double X, double Y, double Z, double Hor)>();
        // A main tunnel leaves its mouth DEAD LEVEL (a horizontal adit out of
        // the hill face, def.Entry blocks long) and only then starts diving,
        // so the entrance is a doorway in a wall, never a hole in the floor.
        // Branches (mouthSteps 0) start on the dive immediately.
        double mh = 0, mv = 0, pulse = 0, vert = mouthSteps > 0 ? 0 : -dip * 0.5;
        double hswell = 0, vswell = 0;
        double hor0 = hor;
        // Weave wanders AROUND the design bearing instead of forgetting it: a
        // pure drunk walk turns an adit back on itself within ~20 steps, so
        // every step also pulls the heading back toward hor0 (shortest arc).
        double homing = 0.03 + 0.05 * (1 - def.Weave);
        // The mouth stays in open-carve mode until the tunnel is genuinely
        // buried, so the entrance always reaches daylight no matter how the
        // hill face slopes. Branches start buried.
        bool buried = mouthSteps == 0;

        for (int i = 0; i < length; i++)
        {
            if (w.TotalSteps++ > 8000) break;
            double t = (double)i / length;

            double u1 = rand.NextDouble(), u2 = rand.NextDouble();
            double u3 = rand.NextDouble(), u4 = rand.NextDouble();
            double u5 = rand.NextDouble();
            double u7 = rand.NextDouble(), u8 = rand.NextDouble();
            double u9 = rand.NextDouble();

            mh = 0.9 * mh + (u1 * 2 - 1) * u2;
            hor += def.Weave * 0.25 * mh;
            if (u5 < 0.018) hor += (rand.NextDouble() - 0.5) * (Math.PI / 2);
            hor += Math.Atan2(Math.Sin(hor0 - hor), Math.Cos(hor0 - hor)) * homing;
            mv = 0.9 * mv + (u3 * 2 - 1) * u4;
            vert += def.Weave * 0.05 * mv;
            pulse = 0.9 * pulse + (u7 * 2 - 1) * u8;

            // Vanilla-style room events: an occasional widening impulse that
            // decays over the following steps, blowing the tunnel out into a
            // chamber. Impulses grow with depth (vanilla's big rooms live
            // well below the surface) and with the cave's scale.
            hswell *= 0.92;
            vswell *= 0.92;
            if (u9 < 0.011)
            {
                // u10 is ALWAYS drawn on a room roll so the RNG sequence
                // (and every saved cave seed) is independent of where the
                // roll lands; the swell is merely not APPLIED during the
                // mouth entry, where a chamber could balloon the tunnel up
                // through the headwall while the roof clamp is off.
                double u10 = rand.NextDouble();
                if (!(mouthSteps > 0 && i < mouthSteps + def.Entry + 10))
                {
                    double deepFrac = Math.Clamp(1.0 - (y - floorY) / Math.Max(8.0, def.Depth), 0, 1);
                    double boost = (0.8 + u10 * 2.2) * def.Scale * (0.6 + 1.4 * deepFrac);
                    hswell += boost;
                    vswell += boost * 0.45;
                }
            }

            // Level through the entry adit, then dive at the design dip
            // until the target depth, then level out.
            double target = mouthSteps > 0 && i < mouthSteps + def.Entry ? 0
                : y > floorY ? -dip : 0;
            vert += (target - vert) * 0.12;
            vert = Math.Clamp(vert, -0.85, 0.3);

            double cv = Math.Cos(vert);
            x += Math.Cos(hor) * cv;
            z += Math.Sin(hor) * cv;
            y += Math.Sin(vert);
            if (y < 8) y = 8;

            double r = Math.Min(13.0, Math.Max(1.5, radius * (0.7 + 0.6 * Math.Sin(t * Math.PI)) + pulse * 0.9 + hswell));
            double v = Math.Min(10.0, Math.Max(1.45, r * def.Squash + vswell * 0.5));
            if (!buried)
            {
                // Buried = the tube's top sits 5+ blocks under the designed
                // ground: past the 3-block soil skin with stone above the
                // ceiling, and safely below the normal roof clamp, so the
                // moment doorway mode ends nothing clips the tunnel.
                int g = DesignedGround(w, (int)Math.Floor(x), (int)Math.Floor(z));
                if (g - (y + v) >= 5) buried = true;
            }
            // Doorway mode cuts the hill face open and lasts until the
            // tunnel is genuinely buried (a fixed step count stopped short
            // of the face and left the mouth sealed a few blocks in). If a
            // tunnel is STILL shallow after the cap, it merely keeps the
            // surface intact instead of trenching it.
            int mouthKind = mouthSteps > 0 && !buried
                ? (i < 26 ? 2 : 1)
                : 0;
            CarveStep(w, def, x, y, z, r, v, mouthKind);
            path.Add((x, y, z, hor));
        }

        if (branchDepth <= 0 || path.Count < 20) return;
        for (int b = 0; b < branches; b++)
        {
            double f = 0.25 + rand.NextDouble() * 0.6;
            double side = rand.NextDouble() < 0.5 ? -1 : 1;
            // Fork 40 to 86 degrees off the parent: side galleries, never a
            // U-turn that would run back out under the sea floor.
            double angOff = side * (0.7 + rand.NextDouble() * 0.8);
            // At the default BranchLen 0.5 this is exactly the old
            // 0.35..0.65 range, so existing cave seeds keep their layouts.
            double lenFrac = def.BranchLen * (0.7 + rand.NextDouble() * 0.6);
            uint childSeed = rand.NextUInt();

            var p = path[Math.Clamp((int)(f * path.Count), 0, path.Count - 1)];
            CarveTunnel(w, def, p.X, p.Y, p.Z, p.Hor + angOff, dip * 0.75,
                (int)(length * lenFrac), radius * def.BranchRadius, floorY,
                Math.Max(1, branches - 1), branchDepth - 1, new CaveRand(childSeed), 0);
        }
    }

    // The ground height the ISLAND DESIGN puts at this column. Never use the
    // engine heightmap here: it can still hold the pre-island seabed after
    // our bulk fills, which once clamped the whole cave down to "3 blocks
    // under the old ocean floor" and reduced it to a few deep pockets. Our
    // own ColumnSurface is the truth the carve was designed against, and it
    // is exactly what the previewer replays.
    private int DesignedGround(CaveWork w, int x, int z)
    {
        IslandJob job = w.Job;
        if (x < job.MinX || x >= job.MinX + job.W || z < job.MinZ || z >= job.MinZ + job.H)
            return int.MinValue / 2;
        long key = ((long)(x - job.MinX) << 21) | (uint)(z - job.MinZ);
        if (w.HeightCache.TryGetValue(key, out int g)) return g;
        g = ColumnSurface(job, x, z, job.SeaLevel, out int topY, out _, out _, out _, out _, out _)
            ? topY : int.MinValue / 2;
        w.HeightCache[key] = g;
        return g;
    }

    // Hollow one step's ellipsoid. Two safety rules, both from vanilla: skip
    // the WHOLE step if any fluid sits within the padded radius (never breach
    // the ocean or a pond), and keep a 3-block roof below each column's
    // designed ground so tunnels never open skylights. mouthKind loosens
    // that: 2 = doorway (no roof, and thin ground above is cut away to open
    // the hill face), 1 = shallow entry (no roof clamp, but the surface is
    // never opened), 0 = normal.
    private void CarveStep(CaveWork w, CaveDef def, double cx, double cy, double cz, double hr, double vr, int mouthKind)
    {
        IslandJob job = w.Job;
        var world = sapi.World.BlockAccessor;
        var pos = new BlockPos(0, 0, 0, job.Dim);

        int x0 = (int)Math.Floor(cx - hr - 1), x1 = (int)Math.Ceiling(cx + hr + 1);
        int z0 = (int)Math.Floor(cz - hr - 1), z1 = (int)Math.Ceiling(cz + hr + 1);
        int y0 = Math.Max(5, (int)Math.Floor(cy - vr - 1));
        int y1 = Math.Min(sapi.WorldManager.MapSizeY - 3, (int)Math.Ceiling(cy + vr + 1));

        double pad = (hr + 1) * (hr + 1);
        for (int xx = x0; xx <= x1; xx++)
            for (int zz = z0; zz <= z1; zz++)
            {
                if (xx < job.MinX || xx >= job.MinX + job.W || zz < job.MinZ || zz >= job.MinZ + job.H) return;
                for (int yy = y0; yy <= y1; yy++)
                {
                    double dx = xx + 0.5 - cx, dy = yy + 0.5 - cy, dz = zz + 0.5 - cz;
                    if ((dx * dx + dz * dz) / pad + dy * dy / ((vr + 1) * (vr + 1)) > 1.0) continue;
                    pos.Set(xx, yy, zz);
                    if (world.GetBlock(pos, BlockLayersAccess.Fluid).BlockId != 0) return;
                }
            }

        double hr2 = hr * hr, vr2 = vr * vr;
        for (int xx = (int)Math.Floor(cx - hr); xx <= (int)Math.Ceiling(cx + hr); xx++)
            for (int zz = (int)Math.Floor(cz - hr); zz <= (int)Math.Ceiling(cz + hr); zz++)
            {
                if (xx < job.MinX || xx >= job.MinX + job.W || zz < job.MinZ || zz >= job.MinZ + job.H) continue;

                int ground = DesignedGround(w, xx, zz);
                // Doorway: carve anything. Shallow: keep at least the
                // surface block, so the ground never opens. Normal: stay 5
                // under the ground, which puts the ceiling below the 3-block
                // soil skin: cave ceilings are always ROCK, never dirt.
                int roof = mouthKind == 2 ? int.MaxValue : mouthKind == 1 ? ground - 1 : ground - 5;

                int yTop = Math.Min(sapi.WorldManager.MapSizeY - 3, (int)Math.Ceiling(cy + vr));
                for (int yy = Math.Max(5, (int)Math.Floor(cy - vr)); yy <= yTop; yy++)
                {
                    if (yy > roof) continue;
                    double dx = xx + 0.5 - cx, dy = yy + 0.5 - cy, dz = zz + 0.5 - cz;
                    if ((dx * dx + dz * dz) / hr2 + dy * dy / vr2 > 1.0) continue;
                    pos.Set(xx, yy, zz);
                    w.Ba.SetBlock(0, pos);
                    w.Ba.SetBlock(0, pos, BlockLayersAccess.Fluid);
                    w.Blocks++;
                }

                // NOTE: no "clear the thin ground above the tube" logic here.
                // It was tried twice for opening the mouth and on gently
                // rising ground it slices a RAVINE along the whole entry.
                // The mouth opens by boring into the stamped headwall
                // instead (StampHeadwall).
            }

        w.Steps.Add((cx, cy, cz, hr, vr, def));
    }

    // Line the carved tunnels' walls with ore so the mine reads as a real
    // deposit: every stone block in the shell just outside the carved air
    // rolls the cave's ore chance. The ore matches the rock the wall actually
    // is (the slate/peridotite blend picks per block), resolved lazily and
    // cached per rock. Runs AFTER the carve commit so it reads real walls.
    private int LineCaveOres(CaveWork w)
    {
        if (w.Steps.Count == 0) return 0;

        IslandJob job = w.Job;
        var world = sapi.World.BlockAccessor;
        var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        var pos = new BlockPos(0, 0, 0, job.Dim);
        var oreForRock = new Dictionary<(string, int), int[]>();
        var visited = new HashSet<long>();
        var rand = new CaveRand(0x51ED2701u);
        int placed = 0;

        foreach ((double cx, double cy, double cz, double hr, double vr, CaveDef def) in w.Steps)
        {
            if (def.OreName == null) continue;
            double or2 = (hr + 1.6) * (hr + 1.6), ov2 = (vr + 1.6) * (vr + 1.6);
            double ir2 = hr * hr, iv2 = vr * vr;
            for (int xx = (int)Math.Floor(cx - hr - 1.6); xx <= (int)Math.Ceiling(cx + hr + 1.6); xx++)
                for (int zz = (int)Math.Floor(cz - hr - 1.6); zz <= (int)Math.Ceiling(cz + hr + 1.6); zz++)
                {
                    if (xx < job.MinX || xx >= job.MinX + job.W || zz < job.MinZ || zz >= job.MinZ + job.H) continue;
                    for (int yy = Math.Max(5, (int)Math.Floor(cy - vr - 1.6)); yy <= (int)Math.Ceiling(cy + vr + 1.6); yy++)
                    {
                        double dx = xx + 0.5 - cx, dy = yy + 0.5 - cy, dz = zz + 0.5 - cz;
                        double horQ = dx * dx + dz * dz;
                        // Shell only: outside the carved air, inside the padded bound.
                        if (horQ / ir2 + dy * dy / iv2 <= 1.0) continue;
                        if (horQ / or2 + dy * dy / ov2 > 1.0) continue;

                        long key = ((long)(xx - job.MinX) << 42) | ((long)(zz - job.MinZ) << 21) | (uint)yy;
                        if (!visited.Add(key)) continue;
                        if (rand.NextDouble() >= def.OreChance) continue;

                        pos.Set(xx, yy, zz);
                        Block b = world.GetBlock(pos);
                        if (b.BlockMaterial != EnumBlockMaterial.Stone) continue;
                        string code = b.Code?.Path;
                        if (code == null || !code.StartsWith("rock-")) continue;

                        if (!oreForRock.TryGetValue((def.OreName, b.BlockId), out int[] ids))
                        {
                            ids = ResolveCaveOre(def.OreName, code.Substring(5));
                            oreForRock[(def.OreName, b.BlockId)] = ids;
                        }
                        if (ids == null) continue;

                        double g = rand.NextDouble();
                        int ore = g < 0.65 ? ids[0] : g < 0.92 ? ids[1] : ids[2];
                        if (ore == 0) ore = ids[0] != 0 ? ids[0] : ids[1];
                        if (ore == 0) continue;
                        ba.SetBlock(ore, pos);
                        placed++;
                    }
                }
        }
        ba.Commit();
        return placed;
    }

    // Poor/medium/rich ore ids for a friendly ore name in one rock, or null
    // if that ore cannot occur there (allowedVariants gates the combos).
    private int[] ResolveCaveOre(string want, string rock)
    {
        string[] minerals = OreAliases.TryGetValue(want, out string[] al) ? al : new[] { want };
        foreach (string mineral in minerals)
        {
            int poor = OreId("poor", mineral, rock);
            int med = OreId("medium", mineral, rock);
            int rich = OreId("rich", mineral, rock);
            if (poor != 0 || med != 0 || rich != 0) return new[] { poor, med, rich };
            // Ungraded minerals (coal, quartz, sulfur...) have a single block.
            int u = sapi.World.GetBlock(new AssetLocation("game", $"ore-{mineral}-{rock}"))?.BlockId ?? 0;
            if (u != 0) return new[] { u, u, u };
        }
        return null;
    }

    // climate= support. Grass and leaf COLOR is not in the block: the client
    // tints plants from the worldgen climate stored in each map region's
    // ClimateMap (temperature byte 16-23, rainfall byte 8-15; the low byte is
    // geologic activity and is left alone). So a rusty desert-faded island
    // means rewriting those pixels over the island's footprint. The blend
    // fades back to the natural climate over a band outside the island, every
    // touched region is marked dirty and broadcast, and the island's chunk
    // columns are resent so already-built meshes re-tint without a relog.
    private int StampClimate(IslandJob job)
    {
        // Region climates (a per-region climate= key) let one island carry
        // several tints. Each climate pixel then asks the shape which region
        // owns that ground and takes its target, falling back to the island
        // -wide climate= (if any) for regions without one.
        bool regionClimate = false;
        if (job.Shape != null)
            foreach (Region sr in job.Shape.Regions.Values)
                if (sr.HasClimate) { regionClimate = true; break; }

        if (!job.HasClimate && !regionClimate) return 0;

        int regionSize = sapi.WorldManager.RegionSize;
        double fade = Math.Max(48.0, job.ClimRadius * 0.35);
        double reach = job.ClimRadius + fade;

        int r0x = FloorDiv((int)(job.Cx - reach), regionSize) - 1;
        int r1x = FloorDiv((int)(job.Cx + reach), regionSize) + 1;
        int r0z = FloorDiv((int)(job.Cz - reach), regionSize) - 1;
        int r1z = FloorDiv((int)(job.Cz + reach), regionSize) + 1;

        int regionsTouched = 0;
        for (int rz = r0z; rz <= r1z; rz++)
            for (int rx = r0x; rx <= r1x; rx++)
            {
                IMapRegion region = sapi.WorldManager.GetMapRegion(rx, rz);
                IntDataMap2D map = region?.ClimateMap;
                if (map?.Data == null || map.InnerSize <= 0) continue;

                double span = regionSize / (double)map.InnerSize;
                int pad = map.TopLeftPadding;
                bool touched = false;

                // Walk the WHOLE padded grid: padding pixels mirror data owned
                // by neighbouring regions, and both copies are sampled during
                // interpolation, so both must be written from the same
                // world-position math or region borders show a tint seam.
                for (int pz = 0; pz < map.Size; pz++)
                    for (int px = 0; px < map.Size; px++)
                    {
                        double wx = (rx * map.InnerSize + (px - pad) + 0.5) * span;
                        double wz = (rz * map.InnerSize + (pz - pad) + 0.5) * span;
                        double d = Math.Sqrt((wx - job.Cx) * (wx - job.Cx) + (wz - job.Cz) * (wz - job.Cz));
                        double w = 1.0 - Smooth((d - job.ClimRadius) / fade);
                        if (w <= 0) continue;

                        // The target climate: this pixel's region's own, or the
                        // island-wide one. Neither -> leave the pixel natural.
                        int targetTemp = job.ClimTempRaw, targetRain = job.ClimRainRaw;
                        bool has = job.HasClimate;
                        if (regionClimate)
                        {
                            int xi = (int)wx, zi = (int)wz;
                            if (xi >= job.MinX && xi < job.MinX + job.W && zi >= job.MinZ && zi < job.MinZ + job.H
                                && ColumnSurface(job, xi, zi, job.SeaLevel, out _, out _, out _, out _, out _, out Region creg)
                                && creg != null && creg.HasClimate)
                            {
                                targetTemp = creg.ClimTempRaw;
                                targetRain = creg.ClimRainRaw;
                                has = true;
                            }
                        }
                        if (!has) continue;

                        int idx = pz * map.Size + px;
                        int old = map.Data[idx];
                        int temp = (old >> 16) & 0xff, rain = (old >> 8) & 0xff;
                        temp = (int)Math.Round(temp + (targetTemp - temp) * w);
                        rain = (int)Math.Round(rain + (targetRain - rain) * w);
                        map.Data[idx] = (old & ~0xffff00) | (temp << 16) | (rain << 8);
                        touched = true;
                    }

                if (!touched) continue;
                region.DirtyForSaving = true;
                sapi.WorldManager.BroadcastMapRegion(rx, rz, false);
                regionsTouched++;
            }

        if (regionsTouched > 0)
        {
            // Chunks already sent to the client were meshed with the OLD tint;
            // resend the island's columns so they rebuild with the new one.
            int chunkSize = GlobalConstants.ChunkSize;
            int c0x = FloorDiv(job.MinX, chunkSize), c1x = FloorDiv(job.MinX + job.W, chunkSize);
            int c0z = FloorDiv(job.MinZ, chunkSize), c1z = FloorDiv(job.MinZ + job.H, chunkSize);
            for (int cz = c0z; cz <= c1z; cz++)
                for (int cx = c0x; cx <= c1x; cx++)
                    sapi.WorldManager.ResendMapChunk(cx, cz, true);
        }
        return regionsTouched;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Natural deposits: the game's own ore, mirrored onto the island
    // ─────────────────────────────────────────────────────────────────────
    //
    // Vanilla ore deposits only exist where worldgen ran over land; stone this
    // mod places is barren, and the engine's per-column heightmaps still say
    // "sea floor" under the island. This pass first writes the DESIGNED surface
    // into WorldGenTerrainHeightMap / RainHeightMap (which also fixes propick
    // readings, rain and snow on every island), then re-runs the game's own
    // GenDeposits over the island's chunk columns.
    //
    // The deposit walk is fully deterministic per (world seed, chunk coords):
    // the same LCGRandom draws vanilla worldgen would have made, the same
    // regional ore maps the prospecting pick reads. So a "high native copper"
    // propick reading on the nearby sea floor produces the SAME high copper
    // inside the island, provided the island's rock can host that ore. Deposits
    // already generated below the old sea floor re-place identically (the RNG
    // is the same), so re-running is safe; only the surface-relative ores move
    // up into the new rock, which is the point.
    //
    // We drive a PRIVATE GenDeposits instance, the way the prospecting pick's
    // ProPickWorkSpace does (setApi + initAssets + initWorldGen), except with
    // blockCallbacks: true. The pro pick passes false because it only reads
    // statistics, but false strips withBlockCallback from every deposit, and
    // saltpeter NEEDS its callback: the deposit targets cave AIR and the
    // callback (BlockFullCoating.TryPlaceBlockForWorldGen) picks the coating
    // variant from which neighbour faces are solid. Without it, the raw-write
    // branch stamps floor-variant saltpeter-d into the whole disc of air,
    // attached to nothing, and every crust pops into an item on the first
    // neighbour update. Callbacks write through our instance's blockAccessor,
    // which setApi points at the plain world accessor (we never attach the
    // worldgen thread's), and the replay runs on the main thread: safe.
    // One reflection read (GenPartial.chunkRand) lets us position-seed the
    // walk per neighbour chunk like GenChunkColumn does.

    private Vintagestory.ServerMods.GenDeposits _depositGen;
    private FieldInfo _depositChunkRandField;
    private string _depositGenErr;

    private void EnsureDepositGen()
    {
        if (_depositGen != null || _depositGenErr != null) return;
        try
        {
            var gd = new Vintagestory.ServerMods.GenDeposits();
            gd.addHandbookAttributes = false;   // the real instance already wrote those
            gd.setApi(sapi);
            gd.initAssets(sapi, blockCallbacks: true);
            gd.initWorldGen();
            _depositChunkRandField = typeof(Vintagestory.ServerMods.GenPartial)
                .GetField("chunkRand", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_depositChunkRandField == null) { _depositGenErr = "GenPartial.chunkRand field not found"; return; }
            _depositGen = gd;
        }
        catch (Exception e)
        {
            _depositGenErr = e.Message;
        }
    }

    private string SyncHeightmapsAndDeposits(IslandJob job)
    {
        if (job.Dim != 0)
            return job.NaturalDeposits ? ". Natural deposits skipped: worldgen maps only exist in dimension 0" : "";

        int cs = GlobalConstants.ChunkSize;
        int cx1 = FloorDiv(job.MinX, cs), cx2 = FloorDiv(job.MinX + job.W - 1, cs);
        int cz1 = FloorDiv(job.MinZ, cs), cz2 = FloorDiv(job.MinZ + job.H - 1, cs);

        // 1. Engine heightmaps -> designed surface, land columns only. Ocean
        // columns keep their engine values (their reshape blended back into the
        // original sea floor, and rain height over water is the sea surface
        // either way).
        for (int cx = cx1; cx <= cx2; cx++)
            for (int cz = cz1; cz <= cz2; cz++)
            {
                IMapChunk mapChunk = sapi.WorldManager.GetMapChunk(cx, cz);
                // A map chunk can exist with null heightmaps if its terrain
                // never generated; writing would NRE and kill the whole pass.
                if (mapChunk?.WorldGenTerrainHeightMap == null || mapChunk.RainHeightMap == null) continue;
                bool touched = false;
                for (int lz = 0; lz < cs; lz++)
                    for (int lx = 0; lx < cs; lx++)
                    {
                        int x = cx * cs + lx, z = cz * cs + lz;
                        if (x < job.MinX || x >= job.MinX + job.W || z < job.MinZ || z >= job.MinZ + job.H) continue;
                        if (!ColumnSurface(job, x, z, job.SeaLevel, out int topY, out _, out _, out _, out int waterTopY, out Region reg)
                            || reg == null) continue;
                        int idx = lz * cs + lx;
                        mapChunk.WorldGenTerrainHeightMap[idx] = (ushort)topY;
                        mapChunk.RainHeightMap[idx] = (ushort)Math.Max(topY, waterTopY);
                        touched = true;
                    }
                if (touched) mapChunk.MarkDirty();
            }

        if (!job.NaturalDeposits) return "";

        EnsureDepositGen();
        if (_depositGen == null)
            return ". Natural deposits FAILED: " + _depositGenErr;

        var chunkRand = (LCGRandom)_depositChunkRandField.GetValue(_depositGen);
        int range = _depositGen.depositChunkRange;
        int chunksY = sapi.WorldManager.MapSizeY / cs;
        int done = 0, missing = 0;

        for (int cx = cx1; cx <= cx2; cx++)
            for (int cz = cz1; cz <= cz2; cz++)
            {
                var chunks = new IServerChunk[chunksY];
                bool loaded = true;
                for (int cy = 0; cy < chunksY; cy++)
                {
                    chunks[cy] = sapi.WorldManager.GetChunk(cx, cy, cz);
                    if (chunks[cy] == null) { loaded = false; break; }
                    // The island takes minutes to build and the server packs
                    // idle chunk data away in seconds. GenDeposits reads Data
                    // raw (GetBlockIdUnsafe skips the packed check by design),
                    // so a packed chunk NREs inside the vanilla generator.
                    chunks[cy].Unpack();
                }
                if (!loaded) { missing++; continue; }
                // The generator also reads these without null checks.
                IMapChunk depMc = chunks[0].MapChunk;
                if (depMc?.WorldGenTerrainHeightMap == null || depMc.RainHeightMap == null || depMc.MapRegion == null)
                { missing++; continue; }

                // Deposits centred up to `range` chunks away spill into this
                // column, exactly like vanilla GenChunkColumn's neighbour walk.
                for (int i = -range; i <= range; i++)
                    for (int j = -range; j <= range; j++)
                    {
                        chunkRand.InitPositionSeed(cx + i, cz + j);
                        _depositGen.GeneratePartial(chunks, cx, cz, i, j);
                    }

                // The generator writes straight into chunk data, bypassing the
                // usual accessors, so persist and resend by hand. Ore swaps rock
                // for rock, both opaque: no relight needed.
                for (int cy = 0; cy < chunksY; cy++)
                {
                    chunks[cy].MarkModified();
                    sapi.WorldManager.BroadcastChunk(cx, cy, cz, true);
                }
                done++;
            }

        string note = $". Natural ore deposits re-rolled across {done} chunk column(s)";
        if (missing > 0) note += $" ({missing} column(s) skipped: not loaded or missing worldgen maps)";
        return note;
    }

    private static bool ParseClimate(string s, out float tempC, out float rain, out string err)
    {
        err = null; rain = 0.5f; tempC = 12f;
        switch (s.Trim().ToLowerInvariant())
        {
            case "arid": tempC = 32f; rain = 0.08f; return true;      // rusty desert fade
            case "dry": tempC = 26f; rain = 0.28f; return true;       // faded savanna
            case "temperate": tempC = 12f; rain = 0.60f; return true;
            case "lush": tempC = 24f; rain = 0.90f; return true;      // deep tropical green
            case "cold": tempC = -2f; rain = 0.55f; return true;
        }
        string[] parts = s.Split(':');
        if (parts.Length == 2
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out tempC)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out rain))
        {
            tempC = Math.Clamp(tempC, -20f, 40f);
            rain = Math.Clamp(rain, 0f, 1f);
            return true;
        }
        err = "use arid, dry, temperate, lush, cold, or <tempC>:<rain 0..1> (e.g. climate=32:0.1)";
        return false;
    }

    // Vanilla concentrates forest floor under tree canopies, so litter stamps
    // a leafy disc around each planted tree: bare leaves at the trunk, grading
    // back to grass at the canopy's edge (the forestfloor 0..7 gradient).
    private void StampLitter(IslandJob job, IBulkBlockAccessor ba, BlockPos pos, int treeX, int treeZ, Region reg)
    {
        if (reg == null || reg.Litter <= 0 || reg.LitterIds.Length == 0) return;
        const int radius = 5;
        for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d > radius) continue;
                double fade = 1.0 - d / radius; // 1 at the trunk, 0 at the edge
                if (job.Rand.NextDouble() >= reg.Litter * (0.25 + 0.75 * fade)) continue;

                int x = treeX + dx, z = treeZ + dz;
                if (!ColumnSurface(job, x, z, job.SeaLevel, out int ty, out bool uw, out int tm, out _, out _, out Region r2) || uw) continue;
                if (tm != SurfGrass || r2 == null || r2.Pond > 0) continue;

                int idx = Math.Clamp((int)(d / radius * reg.LitterIds.Length), 0, reg.LitterIds.Length - 1);
                pos.Set(x, ty, z);
                ba.SetBlock(reg.LitterIds[idx], pos);
            }
    }

    // Cattails ring a pond's rim (or the sea's waterline, on a shore region
    // that asks for them); wild flax and loose surface copper dot the grass.
    // Everything sits in the block above the ground.
    private bool TryPlantFlora(IslandJob job, IBulkBlockAccessor ba, BlockPos pos, int x, int z, int topY, int topMat, Region reg)
    {
        if (job.Shape == null || reg == null) return false;
        if (!GridPos(job, x, z, out double gx, out double gz, out int cx, out int cz)) return false;

        // Peat counts as grass-like ground: vanilla peat grows tallgrass and
        // sits beside clay and reeds, so bog flora places the same way.
        bool grass = topMat == SurfGrass || topMat == SurfPeat, sand = topMat == SurfSand, rock = topMat == SurfRock;
        bool dirt = topMat == SurfSoil; // surface=barren: exposed soil

        // Clay ground: on a clay= region the whole soil column becomes a clay
        // deposit, hidden under a sparse-grass clay surface block.
        if (reg.Pond == 0 && reg.Clay > 0 && reg.ClayId != 0 && grass && job.Rand.NextDouble() < reg.Clay)
        {
            PlaceClayColumn(ba, pos, reg, x, z, topY);
            return true;
        }

        Region pondN = NeighbourPond(job.Shape, cx, cz);

        // Clay deposits on a pond's rim, like the game puts near water. Clay
        // changes the ground, so reeds may still grow on top of it.
        bool placed = false;
        if (pondN != null && pondN.Clay > 0 && pondN.ClayId != 0 && grass && job.Rand.NextDouble() < pondN.Clay)
        {
            PlaceClayColumn(ba, pos, pondN, x, z, topY);
            placed = true;
        }

        if (pondN != null && pondN.CattailId != 0 && job.Rand.NextDouble() < pondN.Cattails)
        {
            pos.Set(x, topY + 1, z);
            ba.SetBlock(pondN.CattailId, pos);
            return true;
        }

        if (reg.Pond == 0 && reg.Cattails > 0 && reg.CattailId != 0)
        {
            double dCoast = Bilinear(job.Shape.DistToOcean, job.Shape.W, job.Shape.H, gx, gz) * job.WorldPerCell;
            if (dCoast <= 3.0
                && job.Rand.NextDouble() < ReedChance(job, x, z, reg.Cattails))
            {
                pos.Set(x, topY + 1, z);
                ba.SetBlock(reg.CattailId, pos);
                return true;
            }
        }

        // Surface ore spawns in vanilla-style CLUSTERS, not singles: this
        // column only wins the right to host a cluster centre. The cluster
        // itself (shallow ore disc + bits over a third of it) is stamped after
        // the whole pass, so later columns' grass cannot overwrite the bits.
        // Devastated-ground patches work the same way.
        if (grass || rock || dirt)
        {
            foreach (OreBitSpec ob in reg.OreBits)
                if (job.Rand.NextDouble() < ob.Chance)
                {
                    job.OreBitCenters.Add((x, z, ob));
                    return true;
                }
            if (reg.Devastation > 0 && job.Rand.NextDouble() < reg.Devastation)
            {
                job.DevastationCenters.Add((x, z, reg));
                return true;
            }

            if (!rock && reg.Pumpkins > 0 && job.Rand.NextDouble() < reg.Pumpkins)
            {
                job.PumpkinCenters.Add((x, z, reg));
                return true;
            }
        }

        // Bushes grow on grass or beach sand.
        if (grass || sand)
        foreach (BushSpec bush in reg.Bushes)
        {
            if (job.Rand.NextDouble() >= bush.Chance) continue;
            if (bush.Shrub != null)
            {
                var tp = new TreeGenParams
                {
                    skipForestFloor = true,
                    size = (float)(0.7 + job.Rand.NextDouble() * 0.5),
                    vinesGrowthChance = 0,
                    mossGrowthChance = 0,
                    otherBlockChance = 0,
                    hemisphere = EnumHemisphere.North,
                    treesInChunkGenerated = 0
                };
                pos.Set(x, topY, z);
                bush.Shrub.GrowTree(ba, pos, tp, job.Rand);
            }
            else
            {
                pos.Set(x, topY + 1, z);
                ba.SetBlock(bush.BlockId, pos);
            }
            return true;
        }

        // Boulders belong on bare rock, not on lawns.
        if (rock && reg.Boulders > 0 && reg.BoulderId != 0 && job.Rand.NextDouble() < reg.Boulders)
        {
            pos.Set(x, topY + 1, z);
            ba.SetBlock(reg.BoulderId, pos);
            return true;
        }

        if (sand && reg.Shells > 0 && reg.ShellIds.Length > 0 && job.Rand.NextDouble() < reg.Shells)
        {
            pos.Set(x, topY + 1, z);
            ba.SetBlock(reg.ShellIds[job.Rand.NextInt(reg.ShellIds.Length)], pos);
            return true;
        }

        // Loose stones match the rock actually under this column: the same
        // blend noise the subsurface uses picks between the region's two rocks.
        double stones = reg.Stones >= 0 ? reg.Stones : 0.012;
        if (stones > 0 && reg.LooseStoneId != 0 && job.Rand.NextDouble() < stones)
        {
            int stoneId = reg.LooseStoneId2 != 0 && job.RockBlend.Noise(x, topY - 4, z) > 0.5
                ? reg.LooseStoneId2 : reg.LooseStoneId;
            pos.Set(x, topY + 1, z);
            ba.SetBlock(stoneId, pos);
            return true;
        }

        if (!grass && !dirt) return placed; // the rest wants soil underfoot

        if (grass && reg.Flax > 0 && reg.FlaxIds.Length > 0 && job.Rand.NextDouble() < reg.Flax)
        {
            pos.Set(x, topY + 1, z);
            ba.SetBlock(reg.FlaxIds[job.Rand.NextInt(reg.FlaxIds.Length)], pos);
            return true;
        }

        if (reg.Sticks > 0 && reg.LooseStickId != 0 && job.Rand.NextDouble() < reg.Sticks)
        {
            pos.Set(x, topY + 1, z);
            ba.SetBlock(reg.LooseStickId, pos);
            return true;
        }

        // (Leaf litter is not scattered here: it stamps discs under trees, see
        // StampLitter, which is how the game's own worldgen distributes it.)

        // Generic decor: flowers, mushrooms, ferns, whatever scatter= asked for.
        foreach (BushSpec decor in reg.Scatter)
        {
            if (job.Rand.NextDouble() >= decor.Chance) continue;
            pos.Set(x, topY + 1, z);
            ba.SetBlock(decor.BlockId, pos);
            return true;
        }

        double wildGrass = reg.WildGrass >= 0 ? reg.WildGrass : 0.35;
        if (grass && wildGrass > 0 && reg.GrassIds.Length > 0 && job.Rand.NextDouble() < wildGrass)
        {
            pos.Set(x, topY + 1, z);
            ba.SetBlock(reg.GrassIds[job.Rand.NextInt(reg.GrassIds.Length)], pos);
            return true;
        }
        return placed;
    }

    private static void MarkerWorld(IslandJob job, int gx, int gz, out int x, out int z)
    {
        // Forward rotation: map space to world, the inverse of GridPos.
        double lx = (gx + 0.5 - job.Shape.W / 2.0) * job.WorldPerCell;
        double lz = (gz + 0.5 - job.Shape.H / 2.0) * job.WorldPerCell;
        x = job.Cx + (int)Math.Round(lx * job.RotCos - lz * job.RotSin);
        z = job.Cz + (int)Math.Round(lx * job.RotSin + lz * job.RotCos);
    }

    // The drawn island's marked trees (or, for a radial island, a summit oak).
    private string PlaceLandmarkTrees(IslandJob job)
    {
        var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        int planted = 0;

        if (job.Shape != null)
        {
            foreach (var m in job.Shape.Markers)
            {
                MarkerWorld(job, m.Gx, m.Gz, out int x, out int z);
                if (!ColumnSurface(job, x, z, job.SeaLevel, out int topY, out bool uw, out _, out _, out _, out _) || uw) continue;
                var rnd = new LCGRandom(job.Seed);
                rnd.InitPositionSeed(x, z);
                m.Gen.GrowTree(ba, new BlockPos(x, topY, z, job.Dim), LandmarkParams(m.Size), rnd);
                planted++;
            }
        }
        else if (job.SummitTree != null)
        {
            if (ColumnSurface(job, job.Cx, job.Cz, job.SeaLevel, out int topY, out bool uw, out _, out _, out _, out _) && !uw)
            {
                var rnd = new LCGRandom(job.Seed);
                rnd.InitPositionSeed(job.Cx, job.Cz);
                job.SummitTree.GrowTree(ba, new BlockPos(job.Cx, topY, job.Cz, job.Dim), LandmarkParams(1.6f), rnd);
                planted++;
            }
        }

        ba.Commit();
        return planted > 0 ? $"Planted {planted} landmark tree(s)." : "No landmark tree placed.";
    }

    // Shape `block` markers: each block rests on the actual ground at its
    // cell, on the sea floor when the cell is underwater. Runs after terrain
    // is placed, so it probes real blocks instead of the design math; plant
    // blocks are stepped through so a marker on a meadow does not sit on a
    // grass tuft. A code the game does not know (its mod not installed) is
    // reported, never fatal.
    private string PlaceMarkerBlocks(IslandJob job)
    {
        if (job.Shape == null || job.Shape.BlockMarkers.Count == 0) return "";

        var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        int placed = 0;
        var missing = new List<string>();

        foreach (var m in job.Shape.BlockMarkers)
        {
            Block block = sapi.World.GetBlock(new AssetLocation(m.Code));
            if (block == null) { if (!missing.Contains(m.Code)) missing.Add(m.Code); continue; }

            MarkerWorld(job, m.Gx, m.Gz, out int x, out int z);
            var pos = new BlockPos(x, 0, z, job.Dim);
            int floor = Math.Max(1, job.SeaLevel - job.MaxDepth);
            for (int y = job.SeaLevel + job.DomeHeight + 8; y >= floor; y--)
            {
                pos.Y = y;
                Block b = ba.GetBlock(pos, BlockLayersAccess.SolidBlocks);
                if (b == null || b.Id == 0) continue;
                if (b.BlockMaterial == EnumBlockMaterial.Plant || b.BlockMaterial == EnumBlockMaterial.Leaves) continue;
                // The optional lift raises the block off the ground (a
                // spawner needs to sit within trigger range of swimmers at
                // the surface), but underwater it never breaches the sea.
                int py = y + 1 + m.Up;
                if (y < job.SeaLevel - 1) py = Math.Min(py, job.SeaLevel - 3);
                pos.Y = Math.Max(py, y + 1);
                ba.SetBlock(block.BlockId, pos);
                placed++;
                break;
            }
        }

        ba.Commit();
        string note = placed > 0 ? $" Placed {placed} marker block(s)." : "";
        if (missing.Count > 0) note += $" Marker block(s) not in this game: {string.Join(", ", missing)}. Is their mod installed?";
        return note;
    }

    private static TreeGenParams LandmarkParams(float size) => new()
    {
        skipForestFloor = true,
        size = size,
        vinesGrowthChance = 0,
        mossGrowthChance = 0,
        otherBlockChance = 0,
        hemisphere = EnumHemisphere.North,
        treesInChunkGenerated = 0
    };

    private ITreeGenerator FindTreeGenerator(string want)
    {
        want = want.ToLowerInvariant();
        string preferred = want switch
        {
            "oak" => "englishoak",
            "pine" => "scotspine",
            "birch" => "silverbirch",
            "maple" => "sugarmaple",
            "redwood" => "redwoodpine",
            _ => want
        };

        // Can run at worldgen init (plan parsing), where tree generators may
        // not be registered yet; a missing tree is a note, never a crash.
        if (sapi.World.TreeGenerators == null) return null;

        ITreeGenerator fallback = null;
        foreach (var kv in sapi.World.TreeGenerators)
        {
            string path = kv.Key.Path.ToLowerInvariant();
            if (path.Contains(preferred)) return kv.Value;
            if (fallback == null && path.Contains(want)) fallback = kv.Value;
        }
        return fallback;
    }

    private void ReportIsland(IslandJob job, string message)
    {
        if (job.Player != null)
            job.Player.SendMessage(GlobalConstants.GeneralChatGroup, "[genisland] " + message, EnumChatType.Notification);
        sapi.Logger.Notification("[landmassgenerator] " + message);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────
    private static float Bilinear(float[,] f, int w, int h, double gx, double gz)
    {
        double cx = Math.Clamp(gx, 0, w - 1.001);
        double cz = Math.Clamp(gz, 0, h - 1.001);
        int x0 = (int)cx, z0 = (int)cz;
        int x1 = Math.Min(x0 + 1, w - 1), z1 = Math.Min(z0 + 1, h - 1);
        double tx = cx - x0, tz = cz - z0;
        double a = f[x0, z0] * (1 - tx) + f[x1, z0] * tx;
        double b = f[x0, z1] * (1 - tx) + f[x1, z1] * tx;
        return (float)(a * (1 - tz) + b * tz);
    }

    private static void GetOrigin(Caller caller, out int ox, out int oy, out int oz, out int dim)
    {
        var e = caller.Entity;
        ox = (int)Math.Floor(e.Pos.X);
        oy = (int)Math.Floor(e.Pos.Y);
        oz = (int)Math.Floor(e.Pos.Z);
        dim = e.Pos.Dimension;
    }

    private Block ResolveBlock(string code, out string err)
    {
        err = null;
        if (string.IsNullOrEmpty(code)) { err = "No block code given."; return null; }
        if (code.Equals("air", StringComparison.OrdinalIgnoreCase)) return sapi.World.GetBlock(0);

        AssetLocation loc = code.Contains(':') ? new AssetLocation(code) : new AssetLocation("game", code);
        Block b = sapi.World.GetBlock(loc);
        if (b == null) err = $"Unknown block '{code}'.";
        return b;
    }

    private static double ParseD(string s, double def)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;

    // Resolve a list of block codes to ids, silently dropping missing ones.
    private int[] ResolveIds(params string[] codes)
    {
        var ids = new List<int>();
        foreach (string c in codes)
        {
            Block b = sapi.World.GetBlock(new AssetLocation("game", c));
            if (b != null) ids.Add(b.BlockId);
        }
        return ids.ToArray();
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Smooth(double t) { t = Math.Clamp(t, 0.0, 1.0); return t * t * (3 - 2 * t); }
    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    private static int OptInt(Dictionary<string, string> opt, string key, int def, int lo, int hi)
        => opt.TryGetValue(key, out string s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? Math.Clamp(v, lo, hi) : def;

    private static double OptDouble(Dictionary<string, string> opt, string key, double def, double lo, double hi)
        => opt.TryGetValue(key, out string s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? Math.Clamp(v, lo, hi) : def;

    private static string OptStr(Dictionary<string, string> opt, string key, string def)
        => opt.TryGetValue(key, out string s) && !string.IsNullOrEmpty(s) ? s : def;

    private static string OptDir(Dictionary<string, string> opt, string key, string def)
    {
        string d = OptStr(opt, key, def).ToLowerInvariant();
        return d == "n" || d == "e" || d == "s" || d == "w" ? d : def;
    }

    private static void DirVec(string dir, out double vx, out double vz)
    {
        switch (dir)
        {
            case "n": vx = 0; vz = -1; break;
            case "s": vx = 0; vz = 1; break;
            case "e": vx = 1; vz = 0; break;
            default: vx = -1; vz = 0; break; // "w"
        }
    }
}
