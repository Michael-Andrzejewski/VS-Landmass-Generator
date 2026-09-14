using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

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

        RegisterCmd(api, "timereset", name =>
            api.ChatCommands.Create(name)
                .WithDescription("Rewind the calendar to 8am on the 1st of May, year 0, the date and time a brand new world starts on. Use after building out a world, right before handing the save file to a new player, so their story begins on day one.")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(OnTimeReset));

        // The 'Rustfall world' checkbox on the world creation screen (see
        // worldconfig.json at the mod root) lands in the world config; run
        // the world setup once, as early as possible. RunGame fires while
        // the singleplayer client is still connecting, so the setup is
        // already underway before the player is fully in the world.
        // PlayerJoin stays as a fallback for worlds loaded before this.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, TryAutoRustfallSetup);
        api.Event.PlayerJoin += _ => TryAutoRustfallSetup();

        // Headless dump pipeline (tools/dumpgen.mjs): a dumpjobs.txt in the
        // LandmassGenerator folder is consumed at boot, each line runs as a
        // /genisland job in sequence, and the server stops itself when the
        // last dump is written. Nothing reads the server console, so this
        // file IS the console.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, TryRunDumpJobs);

        // Serve mode (dumpgen keeps the server alive between runs): in the
        // dump world, keep watching for a NEW dumpjobs.txt every 2 seconds,
        // so iterating on a structure costs one island build instead of a
        // full server boot every time.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
        {
            if (sapi.WorldManager.SaveGame?.WorldName != "LandmassGenerator dump world") return;
            sapi.Event.RegisterGameTickListener(dt =>
            {
                if (!_dumpJobsRunning && !_islandBusy) TryRunDumpJobs();
            }, 2000);
        });

        // For checkbox worlds, force the ocean config BEFORE worldgen ever
        // reads it (SaveGameLoaded fires ahead of InitWorldGenerator). The
        // first spawn chunks then already generate as pure ocean, instead
        // of a continent that the later setup only partially wipes, which
        // left square seams between old-settings and new-settings chunks.
        api.Event.SaveGameLoaded += () =>
        {
            var wc = sapi.WorldManager.SaveGame.WorldConfiguration;
            // A pending dumpjobs.txt marks the headless dump world: force the
            // same pure ocean a Rustfall world gets (serverconfig.json world
            // overrides are ignored at world creation), but skip the full
            // Rustfall story setup.
            if (File.Exists(Path.Combine(shapeFolder, "dumpjobs.txt"))
                || sapi.WorldManager.SaveGame?.WorldName == "LandmassGenerator dump world")
            {
                wc.SetString("landcover", "0");
                wc.SetString("upheavelCommonness", "0");
                wc.SetBool("lgPureOcean", true);
                sapi.Logger.Notification("[dump] dump world detected, forcing pure ocean config");
            }
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

    private static Caller ConsoleCaller() => new Caller
    {
        Type = EnumCallerType.Console,
        CallerPrivileges = new[] { "*" },
        FromChatGroupId = GlobalConstants.GeneralChatGroup
    };

    // dumpjobs.txt: one /genisland option line per row (with or without the
    // leading "/genisland"). The file is deleted before running so a crash
    // mid-job cannot boot-loop the server.
    private bool _dumpJobsRunning;

    private void TryRunDumpJobs()
    {
        string f = Path.Combine(shapeFolder, "dumpjobs.txt");
        if (!File.Exists(f)) return;
        var lines = new List<string>();
        foreach (string raw in File.ReadAllLines(f))
        {
            string l = raw.Trim();
            if (l.Length == 0 || l.StartsWith("#")) continue;
            lines.Add(l);
        }
        try { File.Delete(f); } catch { /* best effort */ }
        if (lines.Count == 0) return;

        _dumpJobsRunning = true;
        sapi.Logger.Notification("[dump] {0} dump job(s) queued", lines.Count);
        sapi.Event.RegisterCallback(_ => RunDumpJob(lines, 0), 4000);
    }

    private void RunDumpJob(List<string> lines, int idx)
    {
        if (idx >= lines.Count)
        {
            _dumpJobsRunning = false;
            // dumpwatch.flag (written by dumpgen): stay alive and keep
            // watching, so the next dumpgen run skips the server boot.
            if (File.Exists(Path.Combine(shapeFolder, "dumpwatch.flag")))
            {
                sapi.Logger.Notification("[dump] all dump jobs finished, server staying alive for more");
                return;
            }
            sapi.Logger.Notification("[dump] all dump jobs finished, stopping the server");
            sapi.Event.RegisterCallback(_ =>
                sapi.ChatCommands.ExecuteUnparsed("/stop",
                    new TextCommandCallingArgs { Caller = ConsoleCaller() }, r => { }), 2000);
            return;
        }

        string cmd = lines[idx].StartsWith("/") ? lines[idx] : "/genisland " + lines[idx];
        sapi.Logger.Notification("[dump] job {0} of {1}: {2}", idx + 1, lines.Count, cmd);
        TextCommandResult res = null;
        sapi.ChatCommands.ExecuteUnparsed(cmd, new TextCommandCallingArgs { Caller = ConsoleCaller() }, r => res = r);
        if (res != null && res.Status != EnumCommandStatus.Success)
        {
            sapi.Logger.Error("[dump] job could not start: " + (res.StatusMessage ?? "no response"));
            RunDumpJob(lines, idx + 1);
            return;
        }

        // /genisland flips _islandBusy synchronously; poll until the island
        // (and its dump, which runs inside the finish pass) is done.
        long[] lid = { 0 };
        lid[0] = sapi.Event.RegisterGameTickListener(dt =>
        {
            if (_islandBusy) return;
            sapi.Event.UnregisterGameTickListener(lid[0]);
            RunDumpJob(lines, idx + 1);
        }, 500);
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

    // Vanilla /time can set the hour and the month but never the year, so a
    // world that spent weeks being built cannot be handed over reading
    // "year 0" without this. TotalHours counts from the 1st of January of
    // year 0, so the target is an absolute date and Add() just bridges the
    // difference; every unit comes from the calendar itself so custom
    // days-per-month worlds land on the right date too.
    private TextCommandResult OnTimeReset(TextCommandCallingArgs args)
    {
        var cal = sapi.World.Calendar;
        double targetHours = (5 - 1) * (double)cal.DaysPerMonth * cal.HoursPerDay + 8.0;
        cal.Add((float)(targetHours - cal.TotalHours));
        return TextCommandResult.Success("Calendar rewound. It is now " + cal.PrettyDate());
    }

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
                ApplyWorldConfigIslandOverrides(opt, isl.MapX, isl.MapZ);
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
        // Same world-creation-screen override the worldgen renderer applied
        // (starter island diameter), so the live decoration pass sizes its
        // flora, caves and clear rect to the island that was actually built.
        if (isl.MapX == 0 && isl.MapZ == 0)
        {
            var o = ParseIslandOptions(opts);
            o.TryGetValue("diameter", out string plannedD);
            ApplyWorldConfigIslandOverrides(o, 0, 0);
            if (o.TryGetValue("diameter", out string dOverride) && dOverride != plannedD)
            {
                opts = System.Text.RegularExpressions.Regex.Replace(opts, @"(^|\s)diameter=\S+", "").Trim() + " diameter=" + dOverride;
            }
        }
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
            caveNote += BuildBastions(job);
            caveNote += BuildWrecks(job);
            caveNote += BuildStructs(job);
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
        public double Pinch;                    // 0..0.8: periodic squeezes along every tunnel, so wide passages neck down and open out again (a squeeze roughly every 85 blocks)
        public double Depth = 60;               // level out this many blocks below the mouth
        public int Mouth = 2;                   // mouth floor this many blocks above sea level
        public int Entry = 10;                  // blocks of dead-level adit before the dive starts
        public uint Seed;                       // 0 = derived from the entrance cell, stable per design
        public string OreName;                  // wall-lining ore (ores=copper:0.05)
        public double OreChance;                // chance per exposed wall block
        public bool Flooded;                    // flooded=1: mouth on the sea/lake FLOOR, tunnel filled with water (a diving cave)
    }

    private class CaveMarker
    {
        public int Gx, Gz;
        public CaveDef Def;
    }

    // A ruined fortress: four sheared corner towers on a crumbled curtain
    // wall, spiral stairs boring down to a flat dungeon level of rectangular
    // hallways and barred cells cut straight out of the island's rock.
    private class BastionDef
    {
        public int Size = 40;      // curtain wall outer width, blocks
        public int DungeonY = -8;  // dungeon FLOOR, blocks relative to sea level
        public int Seed = 1;
    }

    private class BastionMarker
    {
        public int Gx, Gz;
        public BastionDef Def;
    }

    // A drowned metallic wreckage field: one titanic capsized hull, shattered
    // ship segments, and a debris carpet of pipes, beams, spikes and rust
    // over low drowned banks. whirlpool=1 additionally sculpts a sealed
    // draining funnel at the center with flowing spiral streams, and sinks
    // the wrecks into it.
    private class WreckDef
    {
        public int Radius = 55;     // debris field radius, blocks
        public bool Whirlpool;      // 1 = maelstrom variant
        public int Seed = 1;
    }

    private class WreckMarker
    {
        public int Gx, Gz;
        public WreckDef Def;
    }

    // `struct <char> kind=... size=... seed=...`: one directive, many
    // megastructures. Kinds: lighthouse (drowned lighthouse), chains (colossal
    // anchor chains rising from the deep), colossus (a kneeling armored
    // giant, almost entirely submerged), serpent (a curled sea-serpent
    // skeleton with ghostlights), forge (a crater forge suspended over
    // real lava inside a volcano cone).
    private class StructDef
    {
        public string Kind = "";
        public int Size = 60;       // per-kind: lighthouse height, chains/serpent radius, forge crater radius
        public int Seed = 1;
    }

    private class StructMarker
    {
        public int Gx, Gz;
        public StructDef Def;
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
        public List<BastionMarker> Bastions = new();
        public List<WreckMarker> Wrecks = new();
        public List<StructMarker> Structs = new();
        public bool NaturalDeposits;  // `deposits natural`: run the game's own ore pass
        // `ocean plunge=N`: the reshaped sea floor starts N below sea right at
        // the coastline. The default 2 makes a wading shelf (and a visible
        // sand ring); 15+ makes coasts drop sheer into deep water.
        public int OceanPlunge = 2;
        // `ocean basin=R depth=D`: guarantee a bowl of D-deep water within R
        // blocks of the island center, no matter how far the nearest land is.
        public int BasinR;
        public int BasinDepth = 40;
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

        // dump=1 (or dump=name): after the island fully finishes, write every
        // block in the build volume to LandmassGenerator/dumps/<name>.lmd so
        // the localhost previewer can render the REAL result block for block.
        // Bare by default: columns the generator never touched are written as
        // air, so the viewer loads and meshes only the island and its
        // structures. dumpfull=1 keeps the natural terrain around it, for
        // checking how the piece meets the real seabed.
        public string DumpName;
        public bool DumpFull;
        // Columns any megastructure block landed in (world x<<32|z), so a
        // bare dump keeps chains, stumps and debris standing in open ocean
        // outside the terrain-pass footprint.
        public HashSet<long> TouchedCols = new();

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
    // Values picked on the world creation screen (Rustfall tab) that
    // override a plan island's options. The island at plan coordinates
    // 0,0 is the starter island; its diameter follows the "Starter island
    // diameter" slider when the world was created with one. Both the
    // worldgen renderer and the setup pass call this, so every stage of
    // the island agrees on the size.
    private void ApplyWorldConfigIslandOverrides(Dictionary<string, string> opt, int mapX, int mapZ)
    {
        if (mapX != 0 || mapZ != 0) return;
        var wc = sapi.WorldManager.SaveGame?.WorldConfiguration;
        if (wc == null) return;
        string raw = wc.GetAsString("rustfallStarterDiameter", null);
        if (string.IsNullOrEmpty(raw) || !int.TryParse(raw, out int d) || d <= 0) return;
        d = GameMath.Clamp(d, 8, 1024);
        opt.TryGetValue("diameter", out string planned);
        opt["diameter"] = d.ToString();
        if (planned != d.ToString())
        {
            sapi.Logger.Notification("[landmassgenerator] Starter island diameter {0} from the world creation screen (plan file said {1}).", d, planned ?? "default");
        }
    }

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

        // dump=1 names the file after the shape (or "island" for radial);
        // dump=<name> picks the file name outright.
        if (opt.TryGetValue("dump", out string dumpVal) && !string.IsNullOrWhiteSpace(dumpVal) && dumpVal != "0")
        {
            string dn = dumpVal == "1" ? (shapeName ?? "island") : dumpVal;
            var sb = new StringBuilder();
            foreach (char ch in dn.ToLowerInvariant())
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') sb.Append(ch);
            job.DumpName = sb.Length > 0 ? sb.ToString() : "island";
            string full = OptStr(opt, "dumpfull", "0");
            job.DumpFull = full == "1" || full.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

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
        var bastionDefs = new Dictionary<char, BastionDef>();
        var wreckDefs = new Dictionary<char, WreckDef>();
        var structDefs = new Dictionary<char, StructDef>();
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
            else if (tok[0].Equals("bastion", StringComparison.OrdinalIgnoreCase) && tok.Length >= 2)
            {
                bastionDefs[tok[1][0]] = ParseBastion(tok, problems);
            }
            else if (tok[0].Equals("wreck", StringComparison.OrdinalIgnoreCase) && tok.Length >= 2)
            {
                wreckDefs[tok[1][0]] = ParseWreck(tok, problems);
            }
            else if (tok[0].Equals("struct", StringComparison.OrdinalIgnoreCase) && tok.Length >= 2)
            {
                structDefs[tok[1][0]] = ParseStruct(tok, problems);
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
            else if (tok[0].Equals("ocean", StringComparison.OrdinalIgnoreCase) && tok.Length >= 2)
            {
                for (int ti = 1; ti < tok.Length; ti++)
                {
                    int eq = tok[ti].IndexOf('=');
                    if (eq <= 0) { problems.Add($"ocean: expected key=value, got '{tok[ti]}'"); continue; }
                    string k = tok[ti].Substring(0, eq).ToLowerInvariant();
                    string v = tok[ti].Substring(eq + 1);
                    if (k == "plunge") shape.OceanPlunge = (int)Math.Clamp(ParseD(v, 2), 2, 60);
                    else if (k == "basin") shape.BasinR = (int)Math.Clamp(ParseD(v, 0), 0, 200);
                    else if (k == "depth") shape.BasinDepth = (int)Math.Clamp(ParseD(v, 40), 4, 180);
                    else problems.Add($"ocean: unknown key '{k}'");
                }
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
                else if (bastionDefs.ContainsKey(c))
                {
                    shape.Bastions.Add(new BastionMarker { Gx = x, Gz = z, Def = bastionDefs[c] });
                    c = '?';
                }
                else if (wreckDefs.ContainsKey(c))
                {
                    shape.Wrecks.Add(new WreckMarker { Gx = x, Gz = z, Def = wreckDefs[c] });
                    c = '?';
                }
                else if (structDefs.ContainsKey(c))
                {
                    shape.Structs.Add(new StructMarker { Gx = x, Gz = z, Def = structDefs[c] });
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
                case "pinch": d.Pinch = Math.Clamp(ParseD(v, 0), 0, 0.8); break;
                case "depth": d.Depth = Math.Clamp(ParseD(v, 60), 4, 200); break;
                case "mouth": d.Mouth = (int)Math.Clamp(ParseD(v, 2), 0, 30); break;
                case "entry": d.Entry = (int)Math.Clamp(ParseD(v, 10), 0, 60); break;
                case "seed": d.Seed = (uint)Math.Abs((long)ParseD(v, 0)); break;
                case "flooded": d.Flooded = ParseD(v, 0) > 0; break;
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

    private static BastionDef ParseBastion(string[] tok, List<string> problems)
    {
        var d = new BastionDef();
        for (int i = 2; i < tok.Length; i++)
        {
            int eq = tok[i].IndexOf('=');
            if (eq <= 0) continue;
            string k = tok[i].Substring(0, eq).ToLowerInvariant();
            string v = tok[i].Substring(eq + 1);
            switch (k)
            {
                case "size": d.Size = (int)Math.Clamp(ParseD(v, 40), 16, 120); break;
                case "dungeony": d.DungeonY = (int)Math.Clamp(ParseD(v, -8), -30, 20); break;
                case "seed": d.Seed = (int)ParseD(v, 1); break;
                default: problems.Add($"bastion: unknown option '{k}'"); break;
            }
        }
        return d;
    }

    private static WreckDef ParseWreck(string[] tok, List<string> problems)
    {
        var d = new WreckDef();
        for (int i = 2; i < tok.Length; i++)
        {
            int eq = tok[i].IndexOf('=');
            if (eq <= 0) continue;
            string k = tok[i].Substring(0, eq).ToLowerInvariant();
            string v = tok[i].Substring(eq + 1);
            switch (k)
            {
                case "radius": d.Radius = (int)Math.Clamp(ParseD(v, 55), 20, 140); break;
                case "whirlpool": d.Whirlpool = ParseD(v, 0) > 0; break;
                case "seed": d.Seed = (int)ParseD(v, 1); break;
                default: problems.Add($"wreck: unknown option '{k}'"); break;
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
            caveNote += BuildBastions(job);
            caveNote += BuildWrecks(job);
            caveNote += BuildStructs(job);
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
            // The dump runs even after a failed finish pass: the terrain is
            // placed either way, and seeing the failure state in the viewer
            // is exactly what the dump is for.
            try { WriteDump(job); }
            catch (Exception e)
            {
                sapi.Logger.Error(e);
                ReportIsland(job, "Block dump FAILED: " + e.Message);
            }
            _islandBusy = false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  dump=: write the finished island, block for block, for the previewer
    // ─────────────────────────────────────────────────────────────────────
    //
    // Format (gzip around everything): "LMD1", int32 header length, UTF-8
    // JSON header {ox,oy,oz,sx,sy,sz,sea,palette}, then RLE runs of
    // uint32 count + uint16 palette index, x fastest, then z, then y.
    // Palette index 0 is always air. Clutter blocks carry their shape type
    // as "code|type" so the viewer can tell a pipe from a tank.
    private void WriteDump(IslandJob job)
    {
        if (job.DumpName == null) return;

        var ba = sapi.World.BlockAccessor;
        int margin = 16;
        int x0 = job.MinX - margin, z0 = job.MinZ - margin;
        int sx = job.W + margin * 2, sz = job.H + margin * 2;
        int y0 = 1, sy = Math.Min(sapi.WorldManager.MapSizeY - 1, job.SeaLevel + 150) - y0;

        var codeToIdx = new Dictionary<string, ushort> { ["air"] = 0 };
        var palette = new List<string> { "air" };
        var idToIdx = new Dictionary<int, ushort> { [0] = 0 };
        var pos = new BlockPos(0, 0, 0, 0);

        string dir = Path.Combine(shapeFolder, "dumps");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, job.DumpName + ".lmd");
        string tmp = path + ".tmp";

        // Bare dumps (the default): columns the generator never touched are
        // written as pure air, so the viewer only loads the landmass and the
        // blocks the mod actually placed. TouchedCols keeps structures that
        // stand in open ocean; a small halo keeps their block footprints.
        bool[] keep = null;
        if (!job.DumpFull)
        {
            keep = new bool[sx * sz];
            foreach (long k in job.TouchedCols)
            {
                int tx = (int)(k >> 32) - x0, tz = (int)k - z0;
                for (int hz = tz - 1; hz <= tz + 1; hz++)
                    for (int hx = tx - 1; hx <= tx + 1; hx++)
                        if (hx >= 0 && hz >= 0 && hx < sx && hz < sz) keep[hz * sx + hx] = true;
            }
            for (int dz = 0; dz < sz; dz++)
                for (int dx = 0; dx < sx; dx++)
                    if (!keep[dz * sx + dx]
                        && ColumnSurface(job, x0 + dx, z0 + dz, job.SeaLevel, out _, out _, out _, out _, out _, out _))
                        keep[dz * sx + dx] = true;
        }

        long cells = 0;
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var bw = new BinaryWriter(gz))
        {
            // Header last-minute problem: the palette grows while scanning, so
            // scan into memory runs first, then write header + runs.
            var runs = new List<(uint Count, ushort Idx)>(1 << 16);
            uint runLen = 0; ushort runIdx = 0; bool first = true;

            for (int y = y0; y < y0 + sy; y++)
            for (int dz = 0; dz < sz; dz++)
            for (int dx = 0; dx < sx; dx++)
            {
                if (keep != null && !keep[dz * sx + dx])
                {
                    cells++;
                    if (first) { runIdx = 0; runLen = 1; first = false; }
                    else if (runIdx == 0 && runLen < uint.MaxValue) runLen++;
                    else { runs.Add((runLen, runIdx)); runIdx = 0; runLen = 1; }
                    continue;
                }
                pos.Set(x0 + dx, y, z0 + dz);
                Block b = ba.GetBlock(pos, BlockLayersAccess.SolidBlocks);
                if (b == null || b.Id == 0) b = ba.GetBlock(pos, BlockLayersAccess.Fluid);

                ushort idx;
                if (b == null || b.Id == 0) idx = 0;
                else if (!idToIdx.TryGetValue(b.Id, out idx))
                {
                    string code = b.Code?.ToString() ?? "air";
                    if (!codeToIdx.TryGetValue(code, out idx))
                    {
                        idx = (ushort)palette.Count;
                        palette.Add(code);
                        codeToIdx[code] = idx;
                    }
                    idToIdx[b.Id] = idx;
                }

                // Clutter carries its real shape in the block entity, not the
                // block code: resolve it so the viewer can size/color by type.
                if (idx != 0 && palette[idx].Contains("clutter"))
                {
                    var beh = ba.GetBlockEntity(pos)?.GetBehavior<BEBehaviorShapeFromAttributes>();
                    if (beh?.Type != null)
                    {
                        string ckey = palette[idToIdx[b.Id]].Split('|')[0] + "|" + beh.Type;
                        if (!codeToIdx.TryGetValue(ckey, out idx))
                        {
                            idx = (ushort)palette.Count;
                            palette.Add(ckey);
                            codeToIdx[ckey] = idx;
                        }
                    }
                }

                cells++;
                if (first) { runIdx = idx; runLen = 1; first = false; }
                else if (idx == runIdx && runLen < uint.MaxValue) runLen++;
                else { runs.Add((runLen, runIdx)); runIdx = idx; runLen = 1; }
            }
            if (!first) runs.Add((runLen, runIdx));

            var hsb = new StringBuilder();
            hsb.Append("{\"ox\":").Append(x0).Append(",\"oy\":").Append(y0).Append(",\"oz\":").Append(z0)
               .Append(",\"sx\":").Append(sx).Append(",\"sy\":").Append(sy).Append(",\"sz\":").Append(sz)
               .Append(",\"sea\":").Append(job.SeaLevel).Append(",\"cx\":").Append(job.Cx).Append(",\"cz\":").Append(job.Cz)
               .Append(",\"palette\":[");
            for (int i = 0; i < palette.Count; i++)
            {
                if (i > 0) hsb.Append(',');
                hsb.Append('"').Append(palette[i].Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            }
            hsb.Append("]}");
            byte[] header = Encoding.UTF8.GetBytes(hsb.ToString());

            bw.Write(Encoding.ASCII.GetBytes("LMD1"));
            bw.Write(header.Length);
            bw.Write(header);
            foreach (var r in runs) { bw.Write(r.Count); bw.Write(r.Idx); }
        }

        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
        string note = $"[dump] wrote {path} ({cells} cells, {palette.Count} palette entries)";
        sapi.Logger.Notification(note);
        ReportIsland(job, note);
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

        // naturalY comes from the engine heightmap, which our own bulk fills
        // do not update (only the deposits pass fixes it, and only on region
        // columns). Regenerating over or beside an earlier built island can
        // therefore leave its terrain, and any island's TREES, hanging above
        // clearTop as floating remnants with sheer cut faces. Keep clearing
        // upward while anything solid remains; a few air probes per column
        // is cheap and a 4-block gap ends the scan under open sky while
        // still stepping through tree canopies.
        var world = sapi.World.BlockAccessor;
        int yTop = sapi.WorldManager.MapSizeY - 2;
        int gap = 0;
        for (int y = clearTop + 1; y <= yTop && gap < 4; y++)
        {
            pos.Set(x, y, z);
            bool occupied = world.GetBlock(pos, BlockLayersAccess.SolidBlocks).BlockId != 0
                || world.GetBlock(pos, BlockLayersAccess.Fluid).BlockId != 0;
            if (!occupied) { gap++; continue; }
            gap = 0;
            ba.SetBlock(0, pos);
            ba.SetBlock(0, pos, BlockLayersAccess.Fluid);
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
                ApplyWorldConfigIslandOverrides(opt, mapX, mapZ);
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

        // `ocean basin=R depth=D`: a bowl of guaranteed-deep water centered
        // on the island, independent of distance to land. The coast carve
        // only reaches OceanRing blocks off shore, so a megastructure in
        // open water (the colossus, the chainfield) would otherwise stand
        // on whatever shallow natural seabed happens to be there.
        double basinY = double.MaxValue;
        if (s.BasinR > 0)
        {
            double dCx = x - job.Cx, dCz = z - job.Cz;
            double dC = Math.Sqrt(dCx * dCx + dCz * dCz);
            // The fade scales with depth: an 18-block fade on a 95-deep
            // basin was a sheer cylindrical pit wall (exact-preview find).
            // Capped so the bowl never reaches past the build area, which
            // would leave a square seam at the job boundary.
            double bfade = Math.Max(18, Math.Min(s.BasinDepth * 1.2, job.W / 2.0 - s.BasinR - 6));
            if (dC < s.BasinR + bfade)
                basinY = job.SeaLevel - 2 - s.BasinDepth * Smooth(Math.Clamp((s.BasinR + bfade - dC) / bfade, 0, 1))
                    + (job.SurfNoise.Noise(x * 0.4, z * 0.4) - 0.5) * 4.0;
        }

        if (dLand > job.OceanRing)
        {
            // The basin only ever CARVES. Filling up to its fade curve raised
            // every naturally-deeper column to sea-2, building a plateau ring
            // with cliff walls around the whole site (exact-preview find).
            // The 3-block margin beats the fade's own +-2 noise: without it,
            // boundary columns flickered between carved and natural and left
            // single rock chips hovering in the water (exact-preview find).
            if (basinY == double.MaxValue || basinY >= naturalY - 3) return false;
            topY = (int)Math.Round(basinY);
            underwater = true;
            waterTopY = job.SeaLevel - 1;
            topMat = SurfRock;
            return true;
        }

        nearIsland = dLand < job.WorldPerCell * 2;
        double deep = job.SeaLevel - (job.Shape?.OceanPlunge ?? 2) - job.Water * Smooth(dLand / (job.OceanRing * 0.45));
        double back = Smooth((dLand - job.OceanRing * 0.55) / (job.OceanRing * 0.45));
        topY = (int)Math.Round(Lerp(deep, naturalY, back));
        // The basin fades in over the first 30 blocks off shore, so land
        // standing inside a deep basin descends into it instead of dropping
        // off a sheer tower flank (exact-preview find: the chainfield islets
        // and colossus stubs rendered as vertical rock columns).
        if (basinY != double.MaxValue && basinY < topY)
            topY = (int)Math.Round(Lerp(topY, basinY, Smooth(Math.Min(1.0, dLand / 30.0))));
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
            // Vanilla's default. The engine multiplies this into the treegen's
            // own otherLogChance, so pines get their 1-in-100 leaking resin
            // logs; species without an otherLog block are unaffected. 0 here
            // silently turned all wild resin off (saplings pass 0 on purpose:
            // farmed trees never leak).
            otherBlockChance = 1f,
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

            // The step budget is per cave LINE (a line's branches share it),
            // not per island: a vast mine used to starve every later cave
            // line of its whole allowance.
            w.TotalSteps = 0;

            // Entrance cell to world, forward-rotated like tree markers.
            double lx = (cm.Gx + 0.5 - job.Shape.W / 2.0) * job.WorldPerCell;
            double lz = (cm.Gz + 0.5 - job.Shape.H / 2.0) * job.WorldPerCell;
            int ex = job.Cx + (int)Math.Round(lx * job.RotCos - lz * job.RotSin);
            int ez = job.Cz + (int)Math.Round(lx * job.RotSin + lz * job.RotCos);

            if (!ColumnSurface(job, ex, ez, job.SeaLevel, out int topY, out bool uw, out _, out _, out _, out Region entReg) || (uw && !def.Flooded))
            {
                notes.Add($"cave at map {cm.Gx},{cm.Gz} has no dry ground, skipped");
                continue;
            }

            // Flooded caves dive from the sea (or crater lake) FLOOR: the
            // mouth is a hole in the actual ground at the marker, the walk
            // needs no headwall and no open-air scan (it starts in open
            // water), and CarveStep fills the tunnel with water.
            if (def.Flooded)
            {
                double fhor;
                if (double.IsNaN(def.HeadingDeg))
                {
                    fhor = Math.Atan2(job.Cz - ez, job.Cx - ex);
                }
                else
                {
                    double fth = def.HeadingDeg * Math.PI / 180.0;
                    double fmx = Math.Sin(fth), fmz = -Math.Cos(fth);
                    fhor = Math.Atan2(fmx * job.RotSin + fmz * job.RotCos, fmx * job.RotCos - fmz * job.RotSin);
                }
                uint fseed = def.Seed != 0 ? def.Seed
                    : 0x9E3779B9u ^ (uint)(cm.Gx * 668265263) ^ (uint)(cm.Gz * 2246822519);
                CarveTunnel(w, def, ex + 0.5, topY + 1.6, ez + 0.5, fhor,
                    def.DipDeg * Math.PI / 180.0, (int)def.Length + 4, def.Radius * def.Scale,
                    topY + 1.6 - def.Depth, def.Branches, def.BranchDepth, new CaveRand(fseed), 7);
                tunnels++;
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
        // pinch= necks every tunnel down and back open on a fixed rhythm:
        // wide hall, squeeze, wide hall on the far side. Deterministic and
        // RNG-free (phase comes from the tunnel's starting bearing), so it
        // never shifts a saved seed's path or branch layout.
        double pinchCycles = Math.Max(1.5, length / 85.0);
        double pinchPhase = hor0 * 3.7;
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
            // depth= is a promise, not a suggestion: high weave used to let
            // branch noise out-fight the level-out easing and drill 40+
            // blocks past the design floor. Rooms may still dish 2 below.
            if (y < floorY - 2) y = floorY - 2;

            // The squeeze scales the tunnel's own body but not the chamber
            // swells, so room events still blow out big halls anywhere.
            double pinchMul = 1.0 - def.Pinch * (0.5 + 0.5 * Math.Sin(t * pinchCycles * 2 * Math.PI + pinchPhase));
            double r = Math.Min(13.0, Math.Max(1.5, radius * (0.7 + 0.6 * Math.Sin(t * Math.PI)) * pinchMul + pulse * 0.9 + hswell));
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

        // Flooded caves are MEANT to meet water (they open on the sea or
        // lake floor and stay full of it), so only dry caves get the guard.
        double pad = (hr + 1) * (hr + 1);
        for (int xx = x0; xx <= x1; xx++)
            for (int zz = z0; zz <= z1; zz++)
            {
                if (xx < job.MinX || xx >= job.MinX + job.W || zz < job.MinZ || zz >= job.MinZ + job.H) return;
                if (def.Flooded) continue;
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
                // Doorway: carve anything. Shallow: keep the surface AND a
                // real lid under it. A single kept surface block was enough
                // for slim tunnels, but a vast bore can run shallow for tens
                // of blocks and a huge gallery under a one-block skin of
                // hanging grass reads as broken worldgen (Michael hit this
                // on ironmine's entry). Normal: stay 5 under the ground,
                // which puts the ceiling below the 3-block soil skin: cave
                // ceilings are always ROCK, never dirt.
                int roof = mouthKind == 2 ? int.MaxValue : mouthKind == 1 ? ground - 4 : ground - 5;

                int yTop = Math.Min(sapi.WorldManager.MapSizeY - 3, (int)Math.Ceiling(cy + vr));
                for (int yy = Math.Max(5, (int)Math.Floor(cy - vr)); yy <= yTop; yy++)
                {
                    if (yy > roof) continue;
                    double dx = xx + 0.5 - cx, dy = yy + 0.5 - cy, dz = zz + 0.5 - cz;
                    if ((dx * dx + dz * dz) / hr2 + dy * dy / vr2 > 1.0) continue;
                    pos.Set(xx, yy, zz);
                    w.Ba.SetBlock(0, pos);
                    // Flooded tunnels fill with the sea's own water up to the
                    // surface: a divable cave, stable against the lake above.
                    w.Ba.SetBlock(def.Flooded && yy <= job.SeaLevel - 1 ? job.SaltWaterId : 0, pos, BlockLayersAccess.Fluid);
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

    // ── bastion: a ruined fortress structure pass ────────────────────────
    // Four sheared corner towers on a crumbled curtain wall, spiral stairs
    // in the NW and SE towers boring down to a flat dungeon level of
    // rectangular hallways and barred cells cut out of the island's rock.
    // Runs AFTER CarveCaves so a side cave that dives to the dungeon level
    // meets carved halls and becomes a back entrance.

    private string BuildBastions(IslandJob job)
    {
        var list = job.Shape?.Bastions;
        if (list == null || list.Count == 0) return "";

        int built = 0, blocks = 0;
        foreach (var bm in list)
        {
            double lx = (bm.Gx + 0.5 - job.Shape.W / 2.0) * job.WorldPerCell;
            double lz = (bm.Gz + 0.5 - job.Shape.H / 2.0) * job.WorldPerCell;
            int cx = job.Cx + (int)Math.Round(lx * job.RotCos - lz * job.RotSin);
            int cz = job.Cz + (int)Math.Round(lx * job.RotSin + lz * job.RotCos);
            string rock = job.Shape.Regions.TryGetValue(job.Shape.Cells[bm.Gx, bm.Gz], out Region reg)
                ? reg.RockType : "granite";
            blocks += BuildBastion(job, bm.Def, cx, cz, rock);
            built++;
        }
        return built > 0 ? $", {built} bastion ruin(s) ({blocks} block edits)" : "";
    }

    private int BuildBastion(IslandJob job, BastionDef def, int cx, int cz, string rock)
    {
        var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        var pos = new BlockPos(0, 0, 0, job.Dim);
        var rand = new CaveRand((uint)(def.Seed * 2654435761L + 977));
        int placed = 0;

        int Id(string code) => sapi.World.GetBlock(new AssetLocation("game", code))?.BlockId ?? 0;
        int brick = Id($"stonebricks-{rock}");
        int cracked = Id($"crackedstonebricks-{rock}");
        int cobble = Id($"cobblestone-{rock}");
        if (brick == 0) brick = Id("stonebricks-granite");
        if (cracked == 0) cracked = brick;
        if (cobble == 0) cobble = brick;
        int barsBase = Id("ironfence-base-ew"), barsTop = Id("ironfence-top-ew");
        if (brick == 0) return 0; // no masonry blocks at all: nothing sane to build

        int Wall()
        {
            double r0 = rand.NextDouble();
            return r0 < 0.55 ? brick : r0 < 0.80 ? cracked : r0 < 0.93 ? cobble : brick;
        }

        void Set(int x, int y, int z, int id)
        {
            if (x < job.MinX || x >= job.MinX + job.W || z < job.MinZ || z >= job.MinZ + job.H) return;
            if (y < 5 || y > sapi.WorldManager.MapSizeY - 3) return;
            pos.Set(x, y, z);
            ba.SetBlock(id, pos);
            if (id == 0) ba.SetBlock(0, pos, BlockLayersAccess.Fluid);
            placed++;
        }

        // Designed ground, cached: the same design function the fill used,
        // so foundations sit on the terrain as built, trees excluded.
        var groundCache = new Dictionary<long, int>();
        int Ground(int x, int z)
        {
            long key = ((long)x << 24) ^ (uint)z;
            if (groundCache.TryGetValue(key, out int g)) return g;
            g = ColumnSurface(job, x, z, job.SeaLevel, out int ty, out _, out _, out _, out _, out _)
                ? ty : int.MinValue / 2;
            groundCache[key] = g;
            return g;
        }

        int gy = Ground(cx, cz);
        if (gy < job.SeaLevel + 3) return 0; // marker fell on water or a beach: refuse quietly

        int half = def.Size / 2;
        const int towerR = 4;   // towers are 9x9 shells on the curtain corners
        const int wallH = 6;
        int df = job.SeaLevel + def.DungeonY;              // dungeon FLOOR level
        if (df > gy - 8) df = gy - 8;                      // never scrape the courtyard

        // Column is deep enough inside the island for dungeon halls: solid
        // designed ground well above the sea here and 6 blocks to every side,
        // so halls never breach a cliff face or the sea floor.
        bool Deep(int x, int z)
        {
            if (Ground(x, z) < job.SeaLevel + 5) return false;
            return Ground(x + 6, z) >= job.SeaLevel + 5 && Ground(x - 6, z) >= job.SeaLevel + 5
                && Ground(x, z + 6) >= job.SeaLevel + 5 && Ground(x, z - 6) >= job.SeaLevel + 5;
        }

        uint Hash(int a, int b) => (uint)(a * 374761393 + b * 668265263 + def.Seed * 2246822519L);
        double Hash01(int a, int b) { uint h = Hash(a, b); h ^= h >> 13; h *= 1274126177u; return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777216.0; }

        // ── curtain walls: crumbled height from smooth waves, so ruin comes
        // in surviving runs and fallen breaches, not per-block static.
        for (int side = 0; side < 4; side++)
        {
            double phase = side * 2.1 + def.Seed * 0.7;
            for (int s = -half + towerR + 1; s <= half - towerR - 1; s++)
            {
                int x = side == 2 ? cx - half : side == 3 ? cx + half : cx + s;
                int z = side == 0 ? cz - half : side == 1 ? cz + half : cz + s;
                int g = Ground(x, z);
                if (g < job.SeaLevel + 3) continue;
                double hh = wallH * (0.30 + 0.85 * (0.5 + 0.5 * Math.Sin(s * 0.23 + phase))
                    + (rand.NextDouble() - 0.5) * 0.24);
                if (hh < 1.6)
                {
                    if (rand.NextDouble() < 0.4) Set(x, g + 1, z, cobble); // fallen stones in the breach
                    continue;
                }
                int top = gy + (int)Math.Round(hh);
                for (int y = Math.Min(g, gy); y <= top; y++)
                {
                    if (y > gy + 2 && rand.NextDouble() < 0.04) { Set(x, y, z, 0); continue; }
                    Set(x, y, z, Wall());
                }
                if (top >= gy + wallH - 1 && ((s & 1) == 0)) Set(x, top + 1, z, brick); // merlon
            }
        }

        // ── four corner towers, tops sheared diagonally toward the outside.
        for (int k = 0; k < 4; k++)
        {
            int sx = (k == 0 || k == 3) ? -1 : 1;
            int sz = k < 2 ? -1 : 1;
            int tx = cx + sx * half, tz = cz + sz * half;
            int hBase = 13 + (int)(Hash01(k, 17) * 4);      // 13-16 at the tall edge
            bool stairTower = k == 0 || k == 2;             // NW and SE carry the spirals

            for (int du = -towerR; du <= towerR; du++)
                for (int dv = -towerR; dv <= towerR; dv++)
                {
                    int x = tx + du, z = tz + dv;
                    int g = Ground(x, z);
                    if (g < job.SeaLevel + 3) continue;
                    // shear: tall at the inner corner, sheared low at the outer
                    double t = ((du * sx + dv * sz) / (2.0 * towerR)) + 0.5;
                    int top = gy + hBase - (int)Math.Round(7 * t) + (int)(Hash01(x, z) * 2) - 1;

                    bool shell = Math.Abs(du) == towerR || Math.Abs(dv) == towerR;
                    if (shell)
                    {
                        for (int y = Math.Min(g, gy); y <= top; y++)
                        {
                            // ragged upper edges and the odd window slit
                            if (y > gy + 10 && rand.NextDouble() < 0.22) continue;
                            if (y > gy + 2 && y < top - 1 && rand.NextDouble() < 0.05) { Set(x, y, z, 0); continue; }
                            Set(x, y, z, Wall());
                        }
                    }
                    else
                    {
                        // hollow interior with two ruined floors
                        for (int y = gy + 1; y <= gy + hBase + 1; y++) Set(x, y, z, 0);
                        Set(x, gy, z, rand.NextDouble() < 0.12 ? cobble : brick); // ground floor
                        foreach (int fy in new[] { gy + 5, gy + 10 })
                            if (fy < top - 1 && rand.NextDouble() > 0.18) Set(x, fy, z, brick);
                    }
                }

            // doorway into the courtyard: a 2-wide, 3-high opening in the
            // inner-facing shell wall next to the corner.
            for (int d = -1; d <= 0; d++)
                for (int y = gy + 1; y <= gy + 3; y++)
                {
                    Set(tx - sx * towerR, y, tz + d, 0);
                    Set(tx + d, y, tz - sz * towerR, 0);
                }

            // ── spiral stair: brick-lined 5x5 shaft with a solid center
            // pillar, treads descending one block per ring step, from the
            // courtyard down through the rock to the dungeon floor.
            if (stairTower)
            {
                for (int y = df; y <= gy; y++)
                {
                    for (int du = -2; du <= 2; du++)
                        for (int dv = -2; dv <= 2; dv++)
                        {
                            bool ring = Math.Abs(du) == 2 || Math.Abs(dv) == 2;
                            if (ring) { if (rand.NextDouble() < 0.85) Set(tx + du, y, tz + dv, Wall()); }
                            else if (du != 0 || dv != 0) Set(tx + du, y, tz + dv, 0);
                        }
                    Set(tx, y, tz, brick); // center pillar
                }
                var ring8 = new (int du, int dv)[] { (-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0) };
                int sy = gy, idx = 0;
                while (sy > df)
                {
                    var (du, dv) = ring8[idx % 8];
                    Set(tx + du, sy, tz + dv, brick);
                    idx++; sy--;
                }
                // landing under the tower, opening toward the island center
                for (int du = -1; du <= 1; du++)
                    for (int dv = -1; dv <= 1; dv++)
                        for (int y = df + 1; y <= df + 3; y++)
                            Set(tx + du, y, tz + dv, 0);
                // connecting hall from the landing toward the dungeon lattice
                for (int stp = 2; stp <= half + 14; stp++)
                {
                    int x = tx - sx * stp;
                    if (!Deep(x, tz)) break;
                    for (int dv = 0; dv <= 1; dv++)
                        for (int y = df + 1; y <= df + 3; y++)
                            Set(x, y, tz + dv, 0);
                    Set(x, df, tz, rand.NextDouble() < 0.6 ? brick : 0);
                    if (PosMod(x - cx, 12) < 2) break; // reached a north-south hallway
                }
            }
        }

        // ── two ruined outbuildings against the north and east curtain.
        foreach (var (bx1, bz1, bx2, bz2) in new[] {
            (cx - 12, cz - half + 2, cx - 4, cz - half + 7),
            (cx + half - 7, cz - 10, cx + half - 2, cz - 2) })
        {
            for (int x = bx1; x <= bx2; x++)
                for (int z = bz1; z <= bz2; z++)
                {
                    int g = Ground(x, z);
                    if (g < job.SeaLevel + 3) continue;
                    bool edge = x == bx1 || x == bx2 || z == bz1 || z == bz2;
                    if (!edge) { Set(x, gy, z, rand.NextDouble() < 0.2 ? cobble : brick); continue; }
                    double hh = 4 * (0.3 + 0.7 * Hash01(x * 3, z * 5)) ;
                    if (hh < 1.2) continue;
                    for (int y = Math.Min(g, gy); y <= gy + (int)hh; y++) Set(x, y, z, Wall());
                }
            // doorway on the courtyard-facing side
            for (int y = gy + 1; y <= gy + 2; y++) Set((bx1 + bx2) / 2, y, (bz1 > cz ? bz1 : bz2), 0);
        }

        // ── the dungeon: a flat lattice of rectangular hallways and barred
        // cells at df, bored straight out of the rock under the whole
        // fortress and beyond, clipped to columns deep inside the island.
        int R = (int)(half * 1.8) + 8;

        // carved(mu,mv) in tile space: 12 wide (x) by 10 deep (z); hallways
        // are the 2-cell bands, rooms the 8x6 interiors, walls 1 cell.
        bool TileCarved(int tileU, int tileV, int mu, int mv)
        {
            if (mu < 2 || mv < 2) return true;                    // hallways
            if (mu >= 3 && mu <= 10 && mv >= 3 && mv <= 8)        // cell interior
                return Hash01(tileU * 7 + 3, tileV * 11 + 5) > 0.15; // 15% collapsed cells
            return false;
        }

        for (int x = cx - R; x <= cx + R; x++)
            for (int z = cz - R; z <= cz + R; z++)
            {
                if (!Deep(x, z)) continue;
                int mu = PosMod(x - cx, 12), mv = PosMod(z - cz, 10);
                int tu = FloorDiv(x - cx, 12), tv = FloorDiv(z - cz, 10);

                if (TileCarved(tu, tv, mu, mv))
                {
                    for (int y = df + 1; y <= df + 3; y++) Set(x, y, z, 0);
                    double fr = rand.NextDouble();
                    Set(x, df, z, fr < 0.5 ? brick : fr < 0.68 ? cracked : job.StoneId);
                    if (rand.NextDouble() < 0.035) Set(x, df + 1, z, cobble); // rubble
                    continue;
                }

                // wall cells bordering carved space get part-dressed in brick;
                // cell doorways onto the east-west hallways get iron bars.
                bool borders = TileCarved(tu, tv, PosMod(mu + 1, 12), mv) || TileCarved(tu, tv, PosMod(mu - 1 + 12, 12), mv)
                    || TileCarved(tu, tv, mu, PosMod(mv + 1, 10)) || TileCarved(tu, tv, mu, PosMod(mv - 1 + 10, 10));
                if (!borders) continue;

                bool door = mv == 2 && (mu == 6 || mu == 7) && TileCarved(tu, tv, mu, 3);
                if (door)
                {
                    double dr = Hash01(tu * 13 + 1, tv * 17 + 2);
                    Set(x, df, z, brick);
                    Set(x, df + 3, z, Wall());                     // lintel
                    if (dr < 0.30) { Set(x, df + 1, z, 0); Set(x, df + 2, z, 0); }          // bars long gone
                    else { Set(x, df + 1, z, barsBase != 0 ? barsBase : 0); Set(x, df + 2, z, barsTop != 0 ? barsTop : 0); }
                    continue;
                }
                for (int y = df; y <= df + 3; y++)
                    if (rand.NextDouble() < 0.45) Set(x, y, z, Wall());
            }

        ba.Commit();
        return placed;
    }

    private static int PosMod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }

    // ── wreck: a drowned metallic wreckage field ─────────────────────────
    // One titanic hull rolled onto its side and half-sunk, shattered ship
    // segments scattered as if a whirlpool gathered them, a debris carpet
    // (rusted pipes, beams, spikes, gear piles, iron fences) and a drock
    // rust skin over every bank. whirlpool=1 sculpts a sealed draining
    // funnel with flowing spiral streams and sinks the wrecks into it.

    private string BuildWrecks(IslandJob job)
    {
        var list = job.Shape?.Wrecks;
        if (list == null || list.Count == 0) return "";

        int built = 0, blocks = 0;
        foreach (var wm in list)
        {
            double lx = (wm.Gx + 0.5 - job.Shape.W / 2.0) * job.WorldPerCell;
            double lz = (wm.Gz + 0.5 - job.Shape.H / 2.0) * job.WorldPerCell;
            int cx = job.Cx + (int)Math.Round(lx * job.RotCos - lz * job.RotSin);
            int cz = job.Cz + (int)Math.Round(lx * job.RotSin + lz * job.RotCos);
            blocks += BuildWreckField(job, wm.Def, cx, cz);
            built++;
        }
        return built > 0 ? $", {built} wreck field(s) ({blocks} block edits)" : "";
    }

    private int BuildWreckField(IslandJob job, WreckDef def, int cx, int cz)
    {
        var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        var pos = new BlockPos(0, 0, 0, job.Dim);
        var rand = new CaveRand((uint)(def.Seed * 2654435761L + 40787));
        int placed = 0;
        int sea = job.SeaLevel;

        int Id(string code) => sapi.World.GetBlock(new AssetLocation("game", code))?.BlockId ?? 0;
        int hullA = Id("metalblock-corroded-riveted-rusty-iron");
        int hullB = Id("metalblock-corroded-plain-rusty-iron");
        int hullC = Id("metalblock-corroded-riveted-iron");
        int drock = Id("drock");
        int dsoil = Id("devastatedsoil-7");
        int fenceNS = Id("ironfence-base-ns"), fenceEW = Id("ironfence-base-ew");
        int[] spikes = {
            Id("locustnest-metalspike-tiny"), Id("locustnest-metalspike-small"),
            Id("locustnest-metalspike-medium"), Id("locustnest-metalspike-large") };
        int[] piles = {
            Id("metalpartpile-tiny"), Id("metalpartpile-small"),
            Id("metal-parts"), Id("metal-scraps") };
        int[] thorns = {
            Id("devgrowth-thorns"), Id("devgrowth-bush"), Id("devgrowth-shrike") };
        int clutterId = Id("clutter-devastation");
        if (hullA == 0) return 0;
        if (hullB == 0) hullB = hullA;
        if (hullC == 0) hullC = hullA;

        // Rusted pipework and machinery junk: the devastation clutter block
        // carries the real shapes (pipe junctions, broken ends, long runs,
        // beams, hanging chains, tanks, giant gears). One block id, many
        // shapes: the shape name lives on the block entity, so these are
        // recorded during the build and stamped after the bulk commit.
        // Long pipes only ever run along an axis; junctions sit at bends.
        string[] longPipes = { "pipelong2", "pipelong3", "pipelong4", "pipelong4bent",
            "pipelong3-aged", "pipelong4-aged", "pipelong4bent-aged" };
        string[] pipeJoints = { "junkpipe1", "junkpipe2", "junkpipe3", "junkpipe4", "pipe1" };
        string[] beamTypes = { "junkbeamstraight1", "junkbeamstraight2", "junkbeamstraight3",
            "junkbeamstraight4", "junkbeamcross1", "junkbeamcross2" };
        string[] chainTypes = { "junkchain1", "junkchain2", "junkchain3",
            "junkchain4", "junkchain5", "junkchain6" };
        string[] junkTypes = { "junksheet2", "junksheet4", "junktanksmall1", "junktanksmallbase",
            "valve2-aged", "gauge2", "misc1-aged", "miscmedium1-aged" };
        var clutterSpots = new List<(int X, int Y, int Z, string Type, float Rot)>();

        int Hull()
        {
            double r0 = rand.NextDouble();
            return r0 < 0.55 ? hullA : r0 < 0.85 ? hullB : hullC;
        }

        bool InRect(int x, int z) => x >= job.MinX && x < job.MinX + job.W && z >= job.MinZ && z < job.MinZ + job.H;

        void Set(int x, int y, int z, int id)
        {
            if (!InRect(x, z) || y < 5 || y > sapi.WorldManager.MapSizeY - 3) return;
            pos.Set(x, y, z);
            ba.SetBlock(id, pos);
            if (id == 0) ba.SetBlock(0, pos, BlockLayersAccess.Fluid);
            job.TouchedCols.Add(((long)x << 32) | (uint)z);
            placed++;
        }

        void SetFluid(int x, int y, int z, int fluidId)
        {
            if (!InRect(x, z) || y < 5 || y > sapi.WorldManager.MapSizeY - 3) return;
            pos.Set(x, y, z);
            ba.SetBlock(0, pos);
            ba.SetBlock(fluidId, pos, BlockLayersAccess.Fluid);
            job.TouchedCols.Add(((long)x << 32) | (uint)z);
            placed++;
        }

        void Clutter(int x, int y, int z, string type, double heading)
        {
            if (!InRect(x, z) || y < 5 || y > sapi.WorldManager.MapSizeY - 3) return;
            float rot = (float)(Math.Round(-heading / (Math.PI / 2)) * (Math.PI / 2));
            clutterSpots.Add((x, y, z, type, rot));
            job.TouchedCols.Add(((long)x << 32) | (uint)z);
        }

        var groundCache = new Dictionary<long, int>();
        int Ground(int x, int z)
        {
            long key = ((long)x << 24) ^ (uint)z;
            if (groundCache.TryGetValue(key, out int g)) return g;
            g = ColumnSurface(job, x, z, sea, out int ty, out _, out _, out _, out _, out _)
                ? ty : sea - job.Water;
            groundCache[key] = g;
            return g;
        }

        uint Hash(int a, int b) => (uint)(a * 374761393 + b * 668265263 + def.Seed * 2246822519L);
        double Hash01(int a, int b) { uint h = Hash(a, b); h ^= h >> 13; h *= 1274126177u; return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777216.0; }

        int R = def.Radius;
        // The maelstrom is VAST: most of the field spirals down into it.
        int funnelR = def.Whirlpool ? Math.Max(40, (int)(R * 0.85)) : 0;

        // The whirlpool is a divot pressed into the open sea itself: no rim,
        // no drained pit. Each column inside it keeps the full ocean below and
        // loses only the water above the local cone surface; the surface block
        // is real directional flowing water spiraling inward, so the whole
        // bowl visibly runs downhill into a 2x2 down-flow throat at the eye.
        // The steepest slope stays under one block per block, so the flowing
        // staircase covers the surface with no exposed walls. Rock tall enough
        // to break the local surface is left standing and the swirl wraps
        // around it. Liquids only recompute on block updates, so the sculpted
        // sea holds its shape.
        double DivotDepth(double d) => !def.Whirlpool || d >= funnelR ? 0
            : 26.0 * Math.Pow(1 - d / funnelR, 1.6);
        double DistC(double x, double z) => Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
        int SurfY(double d) => sea - 1 - (int)Math.Round(DivotDepth(d));
        int WaterAt(double x, double z) => def.Whirlpool ? SurfY(DistC(x, z)) : sea - 1;

        if (def.Whirlpool)
        {
            for (int x = cx - funnelR; x <= cx + funnelR; x++)
                for (int z = cz - funnelR; z <= cz + funnelR; z++)
                {
                    double d = DistC(x, z);
                    if (d >= funnelR) continue;
                    int sy = SurfY(d);
                    if (sy >= sea - 1) continue;                  // fringe: the sea surface itself
                    int g = Ground(x, z);
                    for (int y = Math.Max(sy + 1, g + 1); y <= sea - 1; y++) Set(x, y, z, 0);
                    if (g >= sy)
                    {
                        // rock stands proud of the swirl; rust its crown
                        if (g < sea && drock != 0 && Hash01(x * 7, z * 13) < 0.4) Set(x, g, z, drock);
                        continue;
                    }
                    double dx = x - cx, dz = z - cz;
                    double il = Math.Max(1.0, d);
                    double tx = -dz / il, tz = dx / il;           // counter-clockwise swirl
                    double fx = tx * 0.85 - dx / il * 0.55, fz = tz * 0.85 - dz / il * 0.55;
                    string code = (fz < -0.38 ? "n" : fz > 0.38 ? "s" : "") + (fx > 0.38 ? "e" : fx < -0.38 ? "w" : "");
                    if (code.Length == 0) code = "d";
                    int lvl = Math.Max(3, 7 - (int)(DivotDepth(d) / 6));
                    int flow = Id($"saltwater-{code}-{lvl}");
                    if (flow != 0) SetFluid(x, sy, z, flow);
                }
            // the eye: a 2x2 down-flow throat churning below the divot floor
            int dn = Id("saltwater-d-6");
            if (dn != 0)
                for (int ex = 0; ex <= 1; ex++)
                    for (int ez = 0; ez <= 1; ez++)
                        for (int y = SurfY(0); y >= SurfY(0) - 20; y--)
                            SetFluid(cx + ex, y, cz + ez, dn);
        }

        // ── one hull: shared by the titan and every segment. Rolled frame:
        // the cross-section ellipse is rotated by rollDeg, so "deck sideways"
        // is just roll ~80-100 and "capsized keel-up" is roll ~180. Plating
        // survives where the tear-noise allows; rib frames survive everywhere,
        // so holes read as a torn rib cage. Devastated soil silts into some
        // holes above the waterline, and thorny devastation growth climbs out.
        void Hull3(double hx, double hz, double hy, double yaw, double rollDeg,
            int len, double beamHalf, double depthHalf, double bowSharp, double decay, int waterTopY)
        {
            double ux = Math.Cos(yaw), uz = Math.Sin(yaw);
            double px = -uz, pz = ux;
            double roll = rollDeg * Math.PI / 180;
            double cr = Math.Cos(roll), sr = Math.Sin(roll);
            double reach = Math.Max(beamHalf, depthHalf) + 2;
            int x0 = (int)(hx - len / 2.0 - reach), x1 = (int)(hx + len / 2.0 + reach);
            int z0 = (int)(hz - len / 2.0 - reach), z1 = (int)(hz + len / 2.0 + reach);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    double t = ((x - hx) * ux + (z - hz) * uz) / (len / 2.0);
                    if (t < -1.05 || t > 1.05) continue;
                    double v = (x - hx) * px + (z - hz) * pz;
                    // taper: blunt stern, sharp bow (positive t)
                    double s = t >= 0 ? Math.Pow(Math.Max(0, 1 - Math.Pow(t, bowSharp)), 0.6)
                        : Math.Pow(Math.Max(0, 1 - Math.Pow(-t, 3.5)), 0.45);
                    if (s < 0.12) continue;
                    for (int y = (int)(hy - reach); y <= (int)(hy + reach); y++)
                    {
                        double w = y - hy;
                        double p = v * cr + w * sr, q = -v * sr + w * cr;
                        double ea = Math.Pow(Math.Abs(p / (beamHalf * s)), 2.2)
                            + Math.Pow(Math.Abs(q / (depthHalf * s)), 2.2);
                        double e = Math.Pow(ea, 1.0 / 2.2);
                        if (e > 1.0) continue;
                        bool rib = PosMod((int)Math.Round((t + 1) * len / 2.0), 5) == 0;
                        if (e >= 0.78)
                        {
                            double tear = Hash01(x * 5 + y * 3, z * 5 - y * 2);
                            if (!rib && tear < decay)
                            {
                                // a torn hole: some silt up and sprout growth
                                if (y > waterTopY && tear > decay - 0.05 && dsoil != 0)
                                {
                                    Set(x, y, z, dsoil);
                                    int th = thorns[(int)(Hash01(x + y, z - y) * thorns.Length) % thorns.Length];
                                    if (th != 0 && Hash01(x - y, z + y) < 0.65) Set(x, y + 1, z, th);
                                }
                                continue;
                            }
                            Set(x, y, z, Hull());
                            // spiky metal along torn edges and top surfaces
                            if (y > waterTopY + 1 && rand.NextDouble() < 0.05 && spikes[3] != 0)
                            {
                                int sp = spikes[1 + (int)(rand.NextDouble() * 3) % 3];
                                if (sp != 0) Set(x, y + 1, z, sp);
                            }
                        }
                        else
                        {
                            // hollow interior: flooded below the LOCAL waterline
                            // (inside the whirlpool divot that line sits lower
                            // than the sea, so streams pour into the wreck)
                            if (y <= waterTopY) SetFluid(x, y, z, job.SaltWaterId);
                            else Set(x, y, z, 0);
                        }
                    }
                }
        }

        // ── recognizable ship parts ──────────────────────────────────────
        // A mast: a tilted pole with a yard crossbeam and chains off its tips.
        void Mast(double mx, double mz, double my, double dirYaw, double upTilt, int mlen)
        {
            double ux = Math.Cos(dirYaw) * Math.Cos(upTilt), uz = Math.Sin(dirYaw) * Math.Cos(upTilt);
            double uy = Math.Sin(upTilt);
            for (int k = 0; k < mlen; k++)
            {
                int x = (int)Math.Round(mx + ux * k), y = (int)Math.Round(my + uy * k), z = (int)Math.Round(mz + uz * k);
                Set(x, y, z, Hull());
                if (k == (int)(mlen * 0.7))
                {
                    double px = -Math.Sin(dirYaw), pz = Math.Cos(dirYaw);
                    for (int q = -4; q <= 4; q++)
                    {
                        int yx = (int)Math.Round(x + px * q), yz = (int)Math.Round(z + pz * q);
                        Set(yx, y, yz, Hull());
                        if (Math.Abs(q) == 4)
                            Clutter(yx, y - 1, yz, chainTypes[(int)(rand.NextDouble() * chainTypes.Length)], dirYaw);
                    }
                }
            }
        }

        // A bow section rising steeply out of the water: the sinking prow.
        void Prow(double bx, double bz, double byaw)
        {
            int plen = 10 + (int)(rand.NextDouble() * 6);
            double ux = Math.Cos(byaw), uz = Math.Sin(byaw);
            double px = -uz, pz = ux;
            for (int k = 0; k < plen; k++)
            {
                double frac = k / (double)plen;
                double bw = 3.2 * (1 - frac) + 0.4;               // narrows to the stem
                int y0 = (int)Math.Round(sea - 4 + k * 0.75);     // climbs out of the sea
                int xx = (int)Math.Round(bx + ux * k), zz = (int)Math.Round(bz + uz * k);
                for (double q = -bw; q <= bw; q += 1.0)
                    for (int h = 0; h < 3; h++)
                    {
                        if (Math.Abs(q) < bw - 1 && h > 0) continue;  // open deck, hull sides
                        int x = (int)Math.Round(xx + px * q), z = (int)Math.Round(zz + pz * q);
                        if (Hash01(x * 5 + h, z * 5 + k) < 0.20) continue; // torn
                        Set(x, y0 + h, z, Hull());
                    }
                if (k == plen - 1)
                    for (int h = 0; h < 2; h++) Set(xx, y0 + 3 + h, zz, Hull()); // stem post
            }
        }

        // Tangle nodes: every floating ruin registers here, and the truss pass
        // then lashes each one to its neighbours so the whole field reads as
        // wreckage holding wreckage above the deep.
        var nodes = new List<double[]>();

        // ── the titan: rolled onto its side, half-submerged, afloat over the
        // deep. In the maelstrom it lies deep inside the divot, decks in the
        // flow. Two masts skim sideways just above the water (the ship is on
        // its side), and a giant corroded gear at the stern is its propeller.
        double tYaw = rand.NextDouble() * Math.PI * 2;
        int tLen = Math.Min(64, R + 10);
        double tX = cx + Math.Cos(tYaw + 2.2) * R * 0.18, tZ = cz + Math.Sin(tYaw + 2.2) * R * 0.18;
        int tWater = WaterAt(tX, tZ);
        Hull3(tX, tZ, tWater - 1, tYaw, 80 + rand.NextDouble() * 25, tLen, 8, 7, 1.7, 0.30, tWater);
        for (int mi = -1; mi <= 1; mi += 2)
        {
            double mmx = tX + Math.Cos(tYaw) * tLen * 0.18 * mi, mmz = tZ + Math.Sin(tYaw) * tLen * 0.18 * mi;
            Mast(mmx, mmz, tWater + 2, tYaw + Math.PI / 2, 0.10 + rand.NextDouble() * 0.12, 12 + (int)(rand.NextDouble() * 5));
        }
        double stX = tX - Math.Cos(tYaw) * tLen * 0.52, stZ = tZ - Math.Sin(tYaw) * tLen * 0.52;
        Clutter((int)stX, tWater + 2, (int)stZ, "gearhugemetal15", tYaw + Math.PI / 2);
        Clutter((int)(stX - Math.Sin(tYaw) * 2), tWater + 1, (int)(stZ + Math.Cos(tYaw) * 2), "gearhugemetal9", tYaw + Math.PI / 2);
        nodes.Add(new[] { tX, tZ, (double)(tWater + 2) });
        nodes.Add(new[] { tX + Math.Cos(tYaw) * tLen * 0.35, tZ + Math.Sin(tYaw) * tLen * 0.35, (double)(tWater + 2) });
        nodes.Add(new[] { tX - Math.Cos(tYaw) * tLen * 0.35, tZ - Math.Sin(tYaw) * tLen * 0.35, (double)(tWater + 2) });

        // ── shattered segments, doubled up: bow cones and blunt hull rings.
        // Two thirds float in the tangle at the local waterline; every third
        // sank and rests on whatever is below, the deep floor or the top of a
        // submerged spire, so divers find whole wrecks under the field.
        int segs = 16 + (int)(rand.NextDouble() * 6);
        for (int i = 0; i < segs; i++)
        {
            double ang = rand.NextDouble() * Math.PI * 2;
            double rr = def.Whirlpool && i < 6
                ? 6 + rand.NextDouble() * (funnelR - 10)          // dragged into the divot
                : R * (0.25 + rand.NextDouble() * 0.65);
            double sx = cx + rr * Math.Cos(ang), sz = cz + rr * Math.Sin(ang);
            int len = 8 + (int)(rand.NextDouble() * 10);
            double beam = 3.5 + rand.NextDouble() * 2.5;
            int wTop = WaterAt(sx, sz);
            bool sunk = i % 3 == 2;
            double sy = sunk
                ? Ground((int)sx, (int)sz) + beam * 0.4
                : wTop - 0.5 - rand.NextDouble() * 2.0;
            double bow = rand.NextDouble() < 0.4 ? 1.7 : 8.0;     // 8 = blunt ring section
            Hull3(sx, sz, sy, rand.NextDouble() * Math.PI * 2, rand.NextDouble() * 180,
                len, beam, beam * 0.85, bow, 0.45 + rand.NextDouble() * 0.25, wTop);
            if (!sunk)
            {
                nodes.Add(new[] { sx, sz, (double)(wTop + 1) });
                // some floating segments raise a leaning mast
                if (rand.NextDouble() < 0.30)
                    Mast(sx, sz, wTop + 1, rand.NextDouble() * Math.PI * 2,
                        0.5 + rand.NextDouble() * 0.6, 8 + (int)(rand.NextDouble() * 5));
            }
        }

        // ── standalone set pieces: two sinking prows climbing out of the sea
        // and two capsized keels, hull-up domes with an air pocket inside.
        for (int i = 0; i < 2; i++)
        {
            double ang = rand.NextDouble() * Math.PI * 2, rr = R * (0.3 + rand.NextDouble() * 0.5);
            double sx2 = cx + rr * Math.Cos(ang), sz2 = cz + rr * Math.Sin(ang);
            Prow(sx2, sz2, rand.NextDouble() * Math.PI * 2);
            nodes.Add(new[] { sx2, sz2, (double)(WaterAt(sx2, sz2) + 2) });
        }
        for (int i = 0; i < 2; i++)
        {
            double ang = rand.NextDouble() * Math.PI * 2, rr = R * (0.3 + rand.NextDouble() * 0.55);
            double sx2 = cx + rr * Math.Cos(ang), sz2 = cz + rr * Math.Sin(ang);
            int wTop = WaterAt(sx2, sz2);
            Hull3(sx2, sz2, wTop - 1.5, rand.NextDouble() * Math.PI * 2, 175 + rand.NextDouble() * 10,
                14 + (int)(rand.NextDouble() * 7), 4.5, 5.5, 1.7, 0.22, wTop);
            Clutter((int)(sx2 - 8), wTop + 2, (int)sz2, "gearhugemetal9", rand.NextDouble() * Math.PI * 2);
            nodes.Add(new[] { sx2, sz2, (double)(wTop + 2) });
        }

        // ── the shape's own rock: find summits near or above the waterline so
        // the tangle can lash onto them.
        var spireSeen = new List<double[]>();
        for (int x = cx - R; x <= cx + R; x += 3)
            for (int z = cz - R; z <= cz + R; z += 3)
            {
                if (DistC(x, z) > R || Ground(x, z) < sea - 1) continue;
                bool near = false;
                foreach (var sp in spireSeen)
                    if ((sp[0] - x) * (sp[0] - x) + (sp[1] - z) * (sp[1] - z) < 100) { near = true; break; }
                if (near) continue;
                spireSeen.Add(new double[] { x, z });
                nodes.Add(new[] { (double)x, (double)z, (double)(sea + 1) });
            }

        // ── the tangle: trusses between neighbouring nodes, sagging toward
        // the middle. The path is an axis-aligned staircase, so pipe runs
        // stay straight with junction pieces at every bend: rectilinear
        // wreck plumbing, never diagonal strings of pipe. Chains hang below,
        // spikes ride on top, and deliberate gaps keep it torn.
        var linked = new HashSet<long>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var order = new List<(double D, int J)>();
            for (int j = 0; j < nodes.Count; j++)
                if (j != i)
                {
                    double dd = Math.Sqrt((nodes[i][0] - nodes[j][0]) * (nodes[i][0] - nodes[j][0])
                        + (nodes[i][1] - nodes[j][1]) * (nodes[i][1] - nodes[j][1]));
                    order.Add((dd, j));
                }
            order.Sort((a, b) => a.D.CompareTo(b.D));
            int links = i == 0 ? 4 : 3;
            for (int L = 0; L < Math.Min(links, order.Count); L++)
            {
                (double dd, int j) = order[L];
                if (dd < 6 || dd > 42) continue;
                long key = Math.Min(i, j) * 100000L + Math.Max(i, j);
                if (!linked.Add(key)) continue;

                double ax = nodes[i][0], az = nodes[i][1], ay = nodes[i][2];
                double by = nodes[j][2];
                double phase = Hash01(i * 31, j * 17) * 6.28;
                int x = (int)Math.Round(ax), z = (int)Math.Round(az);
                int gx2 = (int)Math.Round(nodes[j][0]), gz2 = (int)Math.Round(nodes[j][1]);
                int total = Math.Abs(gx2 - x) + Math.Abs(gz2 - z);
                bool lastAxisX = Math.Abs(gx2 - x) >= Math.Abs(gz2 - z);
                int k = 0;
                while ((x != gx2 || z != gz2) && k < 96)
                {
                    double f = total == 0 ? 1 : k / (double)total;
                    int y = (int)Math.Round(ay + (by - ay) * f
                        - Math.Sin(f * Math.PI) * 1.5 + Math.Sin(k * 0.55 + phase) * 0.8);
                    bool stepX = Math.Abs(gx2 - x) * (0.7 + Hash01(x, z) * 0.6) >= Math.Abs(gz2 - z);
                    bool corner = k > 0 && stepX != lastAxisX;
                    double axisRot = stepX ? 0.0 : Math.PI / 2;
                    double roll = Hash01(x * 3 + k, z * 5 - k);
                    if (corner && roll < 0.66)
                        Clutter(x, y, z, pipeJoints[(int)(rand.NextDouble() * pipeJoints.Length)], axisRot);
                    else if (roll < 0.38) Set(x, y, z, Hull());
                    else if (roll < 0.62)
                        Clutter(x, y, z, longPipes[(int)(rand.NextDouble() * longPipes.Length)], axisRot);
                    else if (roll < 0.72)
                        Clutter(x, y, z, beamTypes[(int)(rand.NextDouble() * beamTypes.Length)], axisRot);
                    else if (roll < 0.80) Set(x, y, z, stepX ? fenceEW : fenceNS);
                    if (roll < 0.80)
                    {
                        if (Hash01(x, z + 77) < 0.20)
                            Clutter(x, y - 1, z, chainTypes[(int)(rand.NextDouble() * chainTypes.Length)], axisRot);
                        if (y > sea && Hash01(x + 5, z) < 0.06)
                        {
                            int sp = spikes[(int)(rand.NextDouble() * 3)];
                            if (sp != 0) Set(x, y + 1, z, sp);
                        }
                    }
                    lastAxisX = stepX;
                    if (stepX) x += Math.Sign(gx2 - x); else z += Math.Sign(gz2 - z);
                    k++;
                }
            }
        }

        // ── junk knots around every node: where ruins meet, debris jams up.
        foreach (var nd in nodes)
        {
            int n = 5 + (int)(rand.NextDouble() * 6);
            for (int q = 0; q < n; q++)
            {
                int x = (int)(nd[0] + (rand.NextDouble() - 0.5) * 8);
                int z = (int)(nd[1] + (rand.NextDouble() - 0.5) * 8);
                int y = (int)(nd[2] + (rand.NextDouble() - 0.5) * 3);
                double r0 = rand.NextDouble();
                if (r0 < 0.40) Set(x, y, z, Hull());
                else if (r0 < 0.68) Clutter(x, y, z, pipeJoints[(int)(rand.NextDouble() * pipeJoints.Length)], rand.NextDouble() * Math.PI * 2);
                else Clutter(x, y, z, junkTypes[(int)(rand.NextDouble() * junkTypes.Length)], rand.NextDouble() * Math.PI * 2);
            }
        }

        // ── rock flanks: rust crust and litter wherever rock comes near the
        // waterline, submerged spire tops included. The truly deep floor
        // stays empty dark water apart from the sunken hulls.
        for (int x = cx - R; x <= cx + R; x++)
            for (int z = cz - R; z <= cz + R; z++)
            {
                double d = DistC(x, z);
                if (d > R) continue;
                int g = Ground(x, z);
                if (g < sea - 12) continue;                        // near-waterline rock only
                if (def.Whirlpool && g < SurfY(d)) continue;       // under the swirl surface: leave it
                if (drock != 0 && Hash01(x * 7, z * 13) < 0.35) Set(x, g, z, drock);

                double roll = rand.NextDouble();
                double p0 = 0.16 * Math.Pow(1 - d / (R + 1.0), 0.5) + 0.03;
                if (roll > p0) continue;

                double kind = rand.NextDouble();
                if (kind < 0.30)
                    Clutter(x, g + 1, z, pipeJoints[(int)(rand.NextDouble() * pipeJoints.Length)], rand.NextDouble() * Math.PI * 2);
                else if (kind < 0.45)
                {
                    // a jutting beam: a rising diagonal of hull metal
                    double bAng = rand.NextDouble() * Math.PI * 2;
                    int n = 4 + (int)(rand.NextDouble() * 5);
                    for (int k = 0; k < n; k++)
                        Set(x + (int)Math.Round(Math.Cos(bAng) * k), g + 1 + k / 2, z + (int)Math.Round(Math.Sin(bAng) * k), Hull());
                }
                else if (kind < 0.60 && g >= sea - 1)
                {
                    int pile = piles[(int)(rand.NextDouble() * piles.Length) % piles.Length];
                    if (pile != 0) Set(x, g + 1, z, pile);
                }
                else if (kind < 0.72 && g >= sea)
                {
                    int sp = spikes[(int)(rand.NextDouble() * 2)];
                    if (sp != 0) Set(x, g + 1, z, sp);
                }
                else if (kind < 0.80)
                {
                    int f = rand.NextDouble() < 0.5 ? fenceNS : fenceEW;
                    if (f != 0) Set(x, g + 1, z, f);
                }
                else if (kind < 0.90)
                {
                    int n = 1 + (int)(rand.NextDouble() * 3);
                    for (int k = 0; k < n; k++) Set(x, g + 1 + k, z, Hull());
                }
                else if (drock != 0)
                {
                    int br = 2 + (int)(rand.NextDouble() * 2);
                    for (int bx = -br; bx <= br; bx++)
                        for (int bz = -br; bz <= br; bz++)
                            if (bx * bx + bz * bz <= br * br && Hash01(x + bx, z + bz + 555) < 0.7)
                                Set(x + bx, Ground(x + bx, z + bz), z + bz, drock);
                }
            }

        ba.Commit();

        // Clutter shapes live on the block entity, so they go through the live
        // accessor after the bulk commit, into cells the build left empty.
        if (clutterId != 0)
        {
            var wba = sapi.World.BlockAccessor;
            var cpos = new BlockPos(0, 0, 0, job.Dim);
            // rotateY's setter is not public; write its auto-property backing
            // field directly (verified against the 1.22 VSSurvivalMod build).
            FieldInfo rotField = typeof(BEBehaviorShapeFromAttributes)
                .GetField("<rotateY>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var c in clutterSpots)
            {
                cpos.Set(c.X, c.Y, c.Z);
                if (wba.GetBlock(cpos, BlockLayersAccess.SolidBlocks).Id != 0) continue; // never eat terrain or hull
                wba.SetBlock(clutterId, cpos);
                var beh = wba.GetBlockEntity(cpos)?.GetBehavior<BEBehaviorShapeFromAttributes>();
                if (beh != null)
                {
                    beh.Type = c.Type;
                    rotField?.SetValue(beh, c.Rot);
                    beh.Blockentity.MarkDirty(true);
                }
                placed++;
            }
        }
        return placed;
    }

    private static StructDef ParseStruct(string[] tok, List<string> problems)
    {
        var d = new StructDef();
        for (int i = 2; i < tok.Length; i++)
        {
            int eq = tok[i].IndexOf('=');
            if (eq <= 0) continue;
            string k = tok[i].Substring(0, eq).ToLowerInvariant();
            string v = tok[i].Substring(eq + 1);
            switch (k)
            {
                case "kind": d.Kind = v.ToLowerInvariant(); break;
                case "size": d.Size = (int)Math.Clamp(ParseD(v, 60), 8, 160); break;
                case "seed": d.Seed = (int)ParseD(v, 1); break;
                default: problems.Add($"struct: unknown key '{k}'"); break;
            }
        }
        if (d.Kind == "beacon") d.Kind = "lighthouse";
        if (d.Kind != "lighthouse" && d.Kind != "chains" && d.Kind != "colossus" && d.Kind != "serpent"
            && d.Kind != "forge" && d.Kind != "divingbell" && d.Kind != "granary")
            problems.Add($"struct: unknown kind '{d.Kind}' (lighthouse, chains, colossus, serpent, forge, divingbell, granary)");
        return d;
    }

    private string BuildStructs(IslandJob job)
    {
        var list = job.Shape?.Structs;
        if (list == null || list.Count == 0) return "";

        int built = 0, blocks = 0;
        foreach (var sm in list)
        {
            double lx = (sm.Gx + 0.5 - job.Shape.W / 2.0) * job.WorldPerCell;
            double lz = (sm.Gz + 0.5 - job.Shape.H / 2.0) * job.WorldPerCell;
            int cx = job.Cx + (int)Math.Round(lx * job.RotCos - lz * job.RotSin);
            int cz = job.Cz + (int)Math.Round(lx * job.RotSin + lz * job.RotCos);
            blocks += BuildStruct(job, sm.Def, cx, cz);
            built++;
        }
        return built > 0 ? $", {built} megastructure(s) ({blocks} block edits)" : "";
    }

    // One builder, five megastructures. They share the sculpting helpers:
    // Blob (an oriented superellipsoid with per-part material logic),
    // ChainRun (colossal chain links along a line, walkable), HullTube (a
    // compact torn ship hull), and the clutter stamp for machinery shapes.
    private int BuildStruct(IslandJob job, StructDef def, int cx, int cz)
    {
        var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        var pos = new BlockPos(0, 0, 0, job.Dim);
        var rand = new CaveRand((uint)(def.Seed * 2654435761L + def.Kind.Length * 977 + 15731));
        int placed = 0;
        int sea = job.SeaLevel;

        int Id(string code)
        {
            var loc = code.IndexOf(':') >= 0 ? new AssetLocation(code) : new AssetLocation("game", code);
            return sapi.World.GetBlock(loc)?.BlockId ?? 0;
        }
        int IdFirst(params string[] codes)
        {
            foreach (string c in codes) { int i = Id(c); if (i != 0) return i; }
            return 0;
        }
        // Same as Id, but remembers what the game could not resolve. A
        // structure quietly missing a block is the mod's oldest failure mode
        // (see papercuts), so the names are logged after the build.
        var missingCodes = new List<string>();
        int Need(string code)
        {
            int i = Id(code);
            if (i == 0 && !missingCodes.Contains(code)) missingCodes.Add(code);
            return i;
        }

        bool InRect(int x, int z) => x >= job.MinX && x < job.MinX + job.W && z >= job.MinZ && z < job.MinZ + job.H;

        void Set(int x, int y, int z, int id)
        {
            if (!InRect(x, z) || y < 5 || y > sapi.WorldManager.MapSizeY - 3) return;
            pos.Set(x, y, z);
            ba.SetBlock(id, pos);
            if (id == 0) ba.SetBlock(0, pos, BlockLayersAccess.Fluid);
            job.TouchedCols.Add(((long)x << 32) | (uint)z);
            placed++;
        }

        void SetFluid(int x, int y, int z, int fluidId)
        {
            if (!InRect(x, z) || y < 5 || y > sapi.WorldManager.MapSizeY - 3) return;
            pos.Set(x, y, z);
            ba.SetBlock(0, pos);
            ba.SetBlock(fluidId, pos, BlockLayersAccess.Fluid);
            job.TouchedCols.Add(((long)x << 32) | (uint)z);
            placed++;
        }

        var groundCache = new Dictionary<long, int>();
        var groundPos = new BlockPos(0, 0, 0, job.Dim);
        int Ground(int x, int z)
        {
            long key = ((long)x << 24) ^ (uint)z;
            if (groundCache.TryGetValue(key, out int g)) return g;
            // The REAL terrain height has to come first, exactly as
            // FillColumn does it. ColumnSurface takes it as an argument and
            // uses it to decide whether `ocean basin=` may carve here, so
            // passing sea level instead (the old code did) made the basin
            // test always pass and handed back a phantom sea floor at
            // sea-basin-depth for every open-water column. Anything built
            // on that number came out as a crust floating over water, which
            // is exactly what the first diving bell site looked like.
            groundPos.Set(x, sea, z);
            int nat = sapi.World.BlockAccessor.GetTerrainMapheightAt(groundPos);
            if (nat <= 1) nat = sea - job.Water;
            g = ColumnSurface(job, x, z, nat, out int ty, out _, out _, out _, out _, out _) ? ty : nat;
            groundCache[key] = g;
            return g;
        }

        uint Hash(int a, int b) => (uint)(a * 374761393 + b * 668265263 + def.Seed * 2246822519L);
        double Hash01(int a, int b) { uint h = Hash(a, b); h ^= h >> 13; h *= 1274126177u; return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777216.0; }

        // Light emitters are held back and placed through the world accessor
        // AFTER the bulk commit, the same way the Underwater Horrors ruins
        // place theirs, so their light is baked into the chunks immediately.
        var glowSpots = new List<(int X, int Y, int Z, int Id)>();
        (int X, int Y, int Z)? lavaProbe = null;   // one melt-surface cell, light-checked after commit
        void Glow(int x, int y, int z, int id)
        {
            if (id == 0 || !InRect(x, z) || y < 5 || y > sapi.WorldManager.MapSizeY - 3) return;
            glowSpots.Add((x, y, z, id));
            job.TouchedCols.Add(((long)x << 32) | (uint)z);
        }

        var clutterSpots = new List<(int X, int Y, int Z, string Type, float Rot)>();
        void Clutter(int x, int y, int z, string type, double heading)
        {
            if (!InRect(x, z) || y < 5 || y > sapi.WorldManager.MapSizeY - 3) return;
            float rot = (float)(Math.Round(-heading / (Math.PI / 2)) * (Math.PI / 2));
            clutterSpots.Add((x, y, z, type, rot));
            job.TouchedCols.Add(((long)x << 32) | (uint)z);
        }

        // shared palettes
        int rustA = Id("metalblock-corroded-riveted-rusty-iron");
        int rustB = Id("metalblock-corroded-plain-rusty-iron");
        // Bright steel plate. metalblock-new-*-rusty-iron is skipVariant'd out
        // of the game entirely (exact-preview find: every "plate" was falling
        // back to corroded rust), but the steel and iron variants exist.
        int plateA = IdFirst("metalblock-new-plain-steel", "metalblock-new-plain-iron");
        int plateB = IdFirst("metalblock-new-riveted-steel", "metalblock-new-riveted-iron");
        if (plateA == 0) plateA = rustB;
        if (plateB == 0) plateB = rustA;
        int drock = Id("drock");
        int fenceNS = Id("ironfence-base-ns"), fenceEW = Id("ironfence-base-ew");
        int[] spikes = {
            Id("locustnest-metalspike-small"), Id("locustnest-metalspike-medium"),
            Id("locustnest-metalspike-large") };
        int Rust()
        {
            double r0 = rand.NextDouble();
            return r0 < 0.6 && rustA != 0 ? rustA : rustB != 0 ? rustB : rustA;
        }

        // Colossal chain: hollow oval links, alternating orientation, walkable.
        // From (x0,y0,z0) to (x1,y1,z1); sag > 0 dips the middle like a slack
        // catenary. Returns nothing but rust.
        void ChainRun(double x0, double y0, double z0, double x1, double y1, double z1, double sag, double linkR)
        {
            double dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 2) return;
            double ux = dx / len, uy = dy / len, uz = dz / len;
            // two perpendiculars: one horizontal, one completing the frame
            double hl = Math.Max(0.001, Math.Sqrt(ux * ux + uz * uz));
            double p1x = -uz / hl, p1y = 0, p1z = ux / hl;
            double p2x = uy * p1z - uz * p1y, p2y = uz * p1x - ux * p1z, p2z = ux * p1y - uy * p1x;
            int links = (int)(len / (linkR * 1.5));
            for (int li = 0; li <= links; li++)
            {
                double f = li / (double)Math.Max(1, links);
                double lx = x0 + dx * f, ly = y0 + dy * f - Math.Sin(f * Math.PI) * sag, lz = z0 + dz * f;
                bool flat = (li & 1) == 0;   // alternate link planes like a real chain
                for (double ph = 0; ph < Math.PI * 2; ph += 0.22)
                {
                    double a = Math.Cos(ph) * linkR * 1.35, b = Math.Sin(ph) * linkR * 0.8;
                    double wx = lx + ux * a + (flat ? p1x : p2x) * b;
                    double wy = ly + uy * a + (flat ? p1y : p2y) * b;
                    double wz = lz + uz * a + (flat ? p1z : p2z) * b;
                    Set((int)Math.Round(wx), (int)Math.Round(wy), (int)Math.Round(wz), Rust());
                }
            }
        }

        // Compact torn hull (a slimmed cousin of the wreck pass's Hull3).
        void HullTube(double hx, double hy, double hz, double yaw2, double rollDeg, int len, double beamHalf, double depthHalf, double decay)
        {
            double ux = Math.Cos(yaw2), uz = Math.Sin(yaw2);
            double px = -uz, pz = ux;
            double roll = rollDeg * Math.PI / 180;
            double cr = Math.Cos(roll), sr = Math.Sin(roll);
            double reach = Math.Max(beamHalf, depthHalf) + 2;
            for (int x = (int)(hx - len / 2.0 - reach); x <= (int)(hx + len / 2.0 + reach); x++)
                for (int z = (int)(hz - len / 2.0 - reach); z <= (int)(hz + len / 2.0 + reach); z++)
                {
                    double t = ((x - hx) * ux + (z - hz) * uz) / (len / 2.0);
                    if (t < -1.05 || t > 1.05) continue;
                    double v = (x - hx) * px + (z - hz) * pz;
                    double s = Math.Pow(Math.Max(0, 1 - Math.Pow(Math.Abs(t), 2.8)), 0.5);
                    if (s < 0.15) continue;
                    for (int y = (int)(hy - reach); y <= (int)(hy + reach); y++)
                    {
                        double w = y - hy;
                        double p = v * cr + w * sr, q = -v * sr + w * cr;
                        double e = Math.Pow(Math.Pow(Math.Abs(p / (beamHalf * s)), 2.2)
                            + Math.Pow(Math.Abs(q / (depthHalf * s)), 2.2), 1.0 / 2.2);
                        if (e > 1.0) continue;
                        if (e >= 0.75)
                        {
                            bool rib = PosMod((int)Math.Round((t + 1) * len / 2.0), 4) == 0;
                            if (!rib && Hash01(x * 5 + y * 3, z * 5 - y * 2) < decay) continue;
                            Set(x, y, z, Rust());
                        }
                        else if (y <= sea - 1) SetFluid(x, y, z, job.SaltWaterId);
                        else Set(x, y, z, 0);
                    }
                }
        }

        // A riveted box girder from A to B: four corner rails, a full frame
        // rib every few blocks, and (deck) a plated walkway with railings
        // along the top, so a derrick arm can be walked out on.
        void Girder(double x0, double y0, double z0, double x1, double y1, double z1, double hw, bool deck)
        {
            double dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 2) return;
            double ux = dx / len, uy = dy / len, uz = dz / len;
            double hl = Math.Max(0.001, Math.Sqrt(ux * ux + uz * uz));
            double p1x = -uz / hl, p1z = ux / hl;                    // across the beam, horizontal
            double p2x = -uy * ux / hl, p2y = hl, p2z = -uy * uz / hl; // up the beam's face
            int steps = (int)len;
            for (int s = 0; s <= steps; s++)
            {
                double lx = x0 + ux * s, ly = y0 + uy * s, lz = z0 + uz * s;
                bool rib = s % 4 == 0;
                for (double a = -hw; a <= hw + 0.01; a += 1.0)
                    for (double b = -hw; b <= hw + 0.01; b += 1.0)
                    {
                        bool onA = Math.Abs(Math.Abs(a) - hw) < 0.01, onB = Math.Abs(Math.Abs(b) - hw) < 0.01;
                        if (!(onA && onB) && !(rib && (onA || onB))) continue;
                        Set((int)Math.Round(lx + p1x * a + p2x * b),
                            (int)Math.Round(ly + p2y * b),
                            (int)Math.Round(lz + p1z * a + p2z * b), Rust());
                    }
                if (!deck) continue;
                // the walkway is corroded plate too: nothing on a drowned
                // derrick should read as bright steel
                int deckId = rustB != 0 ? rustB : rustA;
                for (double a = -hw; a <= hw + 0.01; a += 1.0)
                    Set((int)Math.Round(lx + p1x * a + p2x * (hw + 1)),
                        (int)Math.Round(ly + p2y * (hw + 1)),
                        (int)Math.Round(lz + p1z * a + p2z * (hw + 1)), deckId);
                if (s % 3 == 0 && fenceNS != 0)
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                        Set((int)Math.Round(lx + p1x * hw * sgn + p2x * (hw + 2)),
                            (int)Math.Round(ly + p2y * (hw + 2)),
                            (int)Math.Round(lz + p1z * hw * sgn + p2z * (hw + 2)),
                            Math.Abs(ux) > Math.Abs(uz) ? fenceEW : fenceNS);
            }
        }

        // A colossal cast bell: flared lip, long swelling waist, domed crown,
        // hollow inside, clapper on its bar. Written under the sea it keeps
        // its pocket of AIR (bulk-accessor writes never trigger a liquid
        // update), so a diver can surface inside one. `tilt` leans it over
        // for the wrecks lying on the floor; `decay` eats holes in the shell.
        // `forceDry` keeps the air pocket in a bell that is lying over or
        // driven into rock, and `openMouth` carves the throat as well as the
        // body so that pocket has a way in instead of being sealed by the
        // stone the bell was driven through.
        void Bell(double bx, double by, double bz, double br, double bh, double tilt, double taz, double decay, int glowId,
                  bool forceDry = false, bool openMouth = false)
        {
            double ux = Math.Sin(tilt) * Math.Cos(taz), uy = Math.Cos(tilt), uz = Math.Sin(tilt) * Math.Sin(taz);
            double reach = br * 1.3 + bh + 2;
            bool dry = forceDry || tilt < 0.5;
            double throat = openMouth ? -1.1 : 0.2;
            for (int x = (int)(bx - reach); x <= (int)(bx + reach); x++)
                for (int z = (int)(bz - reach); z <= (int)(bz + reach); z++)
                    for (int y = (int)(by - reach); y <= (int)(by + reach); y++)
                    {
                        double ox = x - bx, oy = y - by, oz = z - bz;
                        double h = ox * ux + oy * uy + oz * uz;
                        if (h < -1.2 || h > bh + 1.2) continue;
                        double rx = ox - h * ux, ry = oy - h * uy, rz = oz - h * uz;
                        double d = Math.Sqrt(rx * rx + ry * ry + rz * rz);
                        double f = Math.Clamp(h / bh, 0, 1);
                        double lip = f / 0.12;
                        double prof = br * (Math.Pow(1 - f, 0.40) + 0.12 * Math.Exp(-lip * lip));
                        if (d > prof + 1.1) continue;
                        bool crown = f > 0.88;
                        if (d >= prof - 1.1 || crown)
                        {
                            if (!crown && Hash01(x * 3 + y * 7, z * 5 - y) < decay) continue;
                            Set(x, y, z, Rust());
                        }
                        else if (h > throat)
                        {
                            if (dry || y > sea - 1) Set(x, y, z, 0);
                            else SetFluid(x, y, z, job.SaltWaterId);
                        }
                    }
            for (double h2 = bh * 0.22; h2 < bh * 0.90; h2 += 0.9)
            {
                int wx = (int)Math.Round(bx + ux * h2), wy = (int)Math.Round(by + uy * h2), wz = (int)Math.Round(bz + uz * h2);
                int bulb = h2 < bh * 0.34 ? 2 : 0;
                for (int ax = -bulb; ax <= bulb; ax++)
                    for (int ay = -bulb; ay <= bulb; ay++)
                        for (int az = -bulb; az <= bulb; az++)
                            if (ax * ax + ay * ay + az * az <= bulb * bulb + 1) Set(wx + ax, wy + ay, wz + az, Rust());
            }
            if (glowId != 0)
            {
                // four lamps hung against the INSIDE of the shell, in the
                // bell's own frame: offsetting along world x and z put them
                // in mid air the moment the bell was tilted over.
                double gh = bh * 0.70;
                double gx = bx + ux * gh, gy = by + uy * gh, gz = bz + uz * gh;
                double gprof = br * (Math.Pow(1 - 0.70, 0.40) + 0.12 * Math.Exp(-Math.Pow(0.70 / 0.12, 2)));
                double gr = Math.Max(1.0, gprof - 1.2);
                double ghl = Math.Sqrt(ux * ux + uz * uz);
                double q1x, q1y, q1z, q2x, q2y, q2z;
                if (ghl < 0.001) { q1x = 1; q1y = 0; q1z = 0; q2x = 0; q2y = 0; q2z = 1; }
                else
                {
                    q1x = -uz / ghl; q1y = 0; q1z = ux / ghl;
                    q2x = uy * q1z - uz * q1y; q2y = uz * q1x - ux * q1z; q2z = ux * q1y - uy * q1x;
                }
                for (int q = 0; q < 4; q++)
                {
                    double sgn = q < 2 ? 1 : -1;
                    double vx = (q % 2 == 0 ? q1x : q2x) * sgn, vy = (q % 2 == 0 ? q1y : q2y) * sgn, vz = (q % 2 == 0 ? q1z : q2z) * sgn;
                    Glow((int)Math.Round(gx + vx * gr), (int)Math.Round(gy + vy * gr), (int)Math.Round(gz + vz * gr), glowId);
                }
            }
        }

        int R = Math.Max(8, def.Size);

        switch (def.Kind)
        {
            // ── THE DROWNED LIGHTHOUSE ─────────────────────────────────────
            // A lighthouse whose keeper's islet sank beneath it: the tower
            // stands centered on the drowned shoal, an interior spiral stair
            // climbs through planked room floors (flooded below the sea,
            // furnished above it), and the top is a hollow glass lamp room
            // around a still-glowing ghostlight. A broken sister stump
            // stands nearby, its fallen lantern cage aglow on the shoal.
            case "lighthouse":
            {
                int brick = Id("stonebricks-granite"), cracked = Id("crackedstonebricks-granite"), cobble = Id("cobblestone-granite");
                int band = IdFirst("stonebricks-basalt", "stonebricks-andesite");
                int glass = IdFirst("glass-plain", "glass");
                int glow = IdFirst("landmassgenerator:ghostlight-green", "underwaterhorrors:ghostlight-green", "underwaterhorrors:ghostlight-blue");
                int planksA = IdFirst("planks-aged-we", "planks-aged-ns");
                int planksB = IdFirst("planks-veryaged-we", "planks-veryaged-ns");
                if (brick == 0) break;
                if (cracked == 0) cracked = brick;
                if (cobble == 0) cobble = brick;
                if (band == 0) band = cracked;
                if (planksA == 0) planksA = band;
                if (planksB == 0) planksB = planksA;
                int[] roomStuff = {
                    Id("lootvessel-food"), Id("lootvessel-tool"), Id("lootvessel-seed"),
                    Id("stationarybasket-north"), Id("stationarybasket-east"),
                    Id("loosegears-1"), Id("loosegears-4") };
                int Wall2()
                {
                    double r0 = rand.NextDouble();
                    return r0 < 0.55 ? brick : r0 < 0.82 ? cracked : cobble;
                }
                double Wrap(double a)
                {
                    while (a > Math.PI) a -= Math.PI * 2;
                    while (a < -Math.PI) a += Math.PI * 2;
                    return a;
                }

                int H = Math.Clamp(def.Size, 40, 110);
                int baseY = Math.Clamp(Ground(cx, cz), sea - 26, sea - 4);   // standing ON the drowned shoal
                int topY = baseY + H;
                int lampY = topY - 7;
                double WallR(int y) => 8.5 - 4.0 * (y - baseY) / (double)H;

                for (int y = baseY; y <= lampY - 2; y++)
                {
                    double rr = WallR(y);
                    bool waterBand = y >= sea - 3 && y <= sea + 4;
                    int room = (y - baseY) / 8;
                    // one plank floor every 8 blocks; the stair passes through a
                    // gap left in each floor
                    bool floorLevel = y > baseY && (y - baseY) % 8 == 0 && y < lampY - 6;
                    double slitA = Hash01(room * 31 + 5, def.Seed * 7) * Math.PI * 2;
                    double stairWant = (y - baseY) * 0.45;
                    for (int x = cx - 10; x <= cx + 10; x++)
                        for (int z = cz - 10; z <= cz + 10; z++)
                        {
                            double d = Math.Sqrt((x - cx) * (x - cx) + (double)(z - cz) * (z - cz));
                            if (d > rr + 0.4) continue;
                            double ang = Math.Atan2(z - cz, x - cx);
                            if (d > rr - 1.3)
                            {
                                // the wall ring; torn open around the waterline
                                if (waterBand && Hash01(x * 3 + y, z * 3 - y) < 0.34) { Set(x, y, z, 0); continue; }
                                bool doorway = y >= baseY + 1 && y <= baseY + 4 && Math.Abs(z - cz) <= 1 && x > cx;
                                bool breach = y >= sea + 2 && y <= sea + 5 && Math.Abs(x - cx) <= 1 && z > cz;
                                // two window slits per room, opposite each other
                                bool window = y > sea + 5 && ((y - baseY) % 8 == 4 || (y - baseY) % 8 == 5)
                                    && (Math.Abs(Wrap(ang - slitA)) < 0.16 || Math.Abs(Wrap(ang - slitA - Math.PI)) < 0.16);
                                if (doorway || breach || window) { Set(x, y, z, 0); continue; }
                                int m = (y - baseY) % 12 == 0 ? band : Wall2();
                                if (waterBand && drock != 0 && Hash01(x + y, z * 7) < 0.25) m = drock;
                                Set(x, y, z, m);
                            }
                            else
                            {
                                // the interior: a spiral stair hugging the wall,
                                // plank floors, flooded below the sea. Every
                                // tread carves 3 blocks of headroom above
                                // itself, THROUGH the plank floors, so the
                                // climb is never blocked.
                                // Each tread owns exactly the [0, 0.45) slice of
                                // the helix, the per-block advance, so wedges
                                // TILE instead of overlapping: the next tread is
                                // never directly on top of this one. The three
                                // slices behind are cleared for headroom.
                                bool inStairRing = d > rr - 3.6;
                                double diff = Wrap(ang - stairWant);
                                // treads stop 1.55 short of the wall face: the
                                // wall tapers inward with height, and the
                                // outermost tread column otherwise finds wall
                                // two blocks above its head
                                bool tread = inStairRing && d <= rr - 1.55 && diff >= 0 && diff < 0.45;
                                bool headroom = inStairRing && diff >= -1.35 && diff < 0;
                                if (tread) Set(x, y, z, brick);
                                else if (headroom)
                                {
                                    if (y <= sea - 1) SetFluid(x, y, z, job.SaltWaterId);
                                    else Set(x, y, z, 0);
                                }
                                else if (floorLevel)
                                {
                                    double pn = Hash01(x * 7 + y, z * 7 - y);
                                    if (pn < 0.10)                                       // rotted-away plank
                                    {
                                        if (y <= sea - 1) SetFluid(x, y, z, job.SaltWaterId);
                                        else Set(x, y, z, 0);
                                    }
                                    else Set(x, y, z, pn < 0.6 ? planksA : planksB);
                                }
                                else if (y <= sea - 1) SetFluid(x, y, z, job.SaltWaterId);
                                else Set(x, y, z, 0);
                            }
                        }
                }

                // sparse furnishings in the dry rooms
                for (int fy = baseY + 8; fy < lampY - 6; fy += 8)
                {
                    if (fy <= sea + 1) continue;                          // flooded rooms stay bare
                    int nItems = 1 + (int)(Hash01(fy, 91) * 2.99);
                    for (int it = 0; it < nItems; it++)
                    {
                        int idB = roomStuff[(int)(Hash01(fy * 3 + it * 13, 17) * roomStuff.Length) % roomStuff.Length];
                        if (idB == 0) continue;
                        double ia = Hash01(fy + it * 7, 33) * Math.PI * 2;
                        double ir2 = Math.Max(0.0, WallR(fy) - 4.6) * Hash01(fy, it + 3);
                        Set(cx + (int)(Math.Cos(ia) * ir2), fy + 1, cz + (int)(Math.Sin(ia) * ir2), idB);
                    }
                }

                // a still-burning ghostlight sconce in every room, flooded ones
                // included, so the whole shaft glows through the slits and
                // tears at night
                // set INTO the wall band, never the stair ring, so no tread
                // loses its headroom to a lamp; from outside they read as
                // lit windows
                for (int fy = baseY + 4; fy < lampY - 4; fy += 8)
                {
                    double sa = Hash01(fy * 5, 57) * Math.PI * 2;
                    double srr = WallR(fy) - 0.8;
                    Glow(cx + (int)Math.Round(Math.Cos(sa) * srr), fy, cz + (int)Math.Round(Math.Sin(sa) * srr), glow);
                }

                // the lamp room: a hollow glass-walled room with a gallery
                // walkway, corner pillars, the ghostlight on its pedestal, a
                // rail and a conical roof
                double lr = 4.6;
                double arriveA = (lampY - 1 - baseY) * 0.45;
                for (int x = cx - 7; x <= cx + 7; x++)
                    for (int z = cz - 7; z <= cz + 7; z++)
                    {
                        double d = Math.Sqrt((x - cx) * (x - cx) + (double)(z - cz) * (z - cz));
                        double ang = Math.Atan2(z - cz, x - cx);
                        if (d <= lr + 1.6)
                        {
                            // the hole trails the arrival angle so the last
                            // treads below keep their headroom through this floor
                            bool stairHole = d > lr - 2.4 && d <= lr - 0.4 && Math.Abs(Wrap(ang - (arriveA - 0.4))) < 1.1;
                            Set(x, lampY - 1, z, stairHole ? 0 : band);         // lamp floor + gallery deck
                        }
                        if (d > lr - 0.8 && d <= lr + 0.4)
                        {
                            bool pillar = (Math.Abs(x - cx) > 2.5 && Math.Abs(z - cz) > 2.5);
                            for (int y = lampY; y <= lampY + 3; y++)
                                Set(x, y, z, pillar ? brick : glass != 0 ? glass : 0);
                        }
                        else if (d <= lr - 0.8)
                            for (int y = lampY; y <= lampY + 3; y++)
                                Set(x, y, z, 0);                                // the room is a ROOM
                        if (d > lr + 0.6 && d <= lr + 1.6 && fenceNS != 0)
                            Set(x, lampY, z, Math.Abs(x - cx) > Math.Abs(z - cz) ? fenceNS : fenceEW); // gallery rail
                        double roofFrac = 1.0 - d / (lr + 1.5);
                        if (roofFrac > 0)
                            Set(x, lampY + 4 + (int)(roofFrac * 3), z, band);   // cone roof
                    }
                // the light itself: a pedestal and a baked ghostlight cluster
                for (int gx2 = 0; gx2 <= 1; gx2++)
                    for (int gz2 = 0; gz2 <= 1; gz2++)
                    {
                        Set(cx + gx2, lampY, cz + gz2, band);
                        Glow(cx + gx2, lampY + 1, cz + gz2, glow);
                        Glow(cx + gx2, lampY + 2, cz + gz2, glow);
                        Glow(cx + gx2, lampY + 3, cz + gz2, glow);
                    }

                // the broken sister stump and its fallen, still-glowing lantern.
                // Placed at the DEEPEST water on a 22-block ring: a random
                // angle can land on a neighbouring islet's mound, which
                // swallows the stump whole (found via the exact previewer).
                double sBaseAng = rand.NextDouble() * Math.PI * 2;
                double sAng = sBaseAng;
                int bestG = int.MaxValue;
                for (int k2 = 0; k2 < 16; k2++)
                {
                    double a2 = sBaseAng + k2 * Math.PI / 8;
                    int g2 = Ground(cx + (int)(Math.Cos(a2) * 22), cz + (int)(Math.Sin(a2) * 22));
                    if (g2 < bestG) { bestG = g2; sAng = a2; }
                }
                int sx = cx + (int)(Math.Cos(sAng) * 22), sz = cz + (int)(Math.Sin(sAng) * 22);
                int sBase = Math.Clamp(Ground(sx, sz), sea - 26, sea - 4);
                for (int y = sBase; y <= sea + 6; y++)
                    for (int x = sx - 8; x <= sx + 8; x++)
                        for (int z = sz - 8; z <= sz + 8; z++)
                        {
                            double d = Math.Sqrt((x - sx) * (x - sx) + (double)(z - sz) * (z - sz));
                            double rr = 6.8 - 2.5 * (y - sBase) / (double)H;
                            if (d > rr + 0.4 || d < rr - 1.3) continue;
                            int jag = (int)(Hash01(x * 5, z * 5) * 5);         // sheared top
                            if (y > sea + 1 + jag) continue;
                            if (Hash01(x * 3 + y, z * 3 - y) < 0.22) continue; // torn
                            Set(x, y, z, Wall2());
                        }
                int lx2 = sx + 7, lz2 = sz + 5, lb = Ground(lx2, lz2) + 1;
                for (int x = lx2 - 2; x <= lx2 + 2; x++)
                    for (int z = lz2 - 2; z <= lz2 + 2; z++)
                        for (int y = lb; y <= lb + 4; y++)
                        {
                            bool shell = x == lx2 - 2 || x == lx2 + 2 || z == lz2 - 2 || z == lz2 + 2 || y == lb || y == lb + 4;
                            if (!shell) continue;
                            bool frame = (Math.Abs(x - lx2) == 2) == (Math.Abs(z - lz2) == 2) || y == lb || y == lb + 4;
                            if (Hash01(x + y * 3, z - y) < 0.2) continue;
                            Set(x, y, z, frame ? band : glass != 0 ? glass : 0);
                        }
                Glow(lx2, lb + 2, lz2, glow);
                break;
            }

            // ── THE SEALED ORB ────────────────────────────────────────────
            // Something eldritch was shelled in devastated rock, chained to
            // the mantle, and rose to the surface anyway. The orb floats
            // half-out of the sea, thorned and overgrown, its shell cracked
            // and leaking green light; the chains run taut from its hide
            // straight down through the water and into the rock below.
            case "chains":
            {
                int drockId = drock != 0 ? drock : rustA;
                int dsoil = IdFirst("devastatedsoil-3", "devastatedsoil-1", "devastatedsoil-6");
                int[] growth = {
                    Id("devgrowth-thorns"), Id("devgrowth-thorns"), Id("devgrowth-shard"),
                    Id("devgrowth-shrike"), Id("devgrowth-bush") };
                int glowG2 = IdFirst("landmassgenerator:ghostlight-green", "underwaterhorrors:ghostlight-green");
                if (dsoil == 0) dsoil = drockId;

                double orbR = Math.Clamp(def.Size * 0.3, 12, 24);      // size=60 -> 18
                double oy = sea + orbR * 0.25;                          // stuck floating, most of it out

                // crack planes: two great-circle slashes across the upper shell
                double ca1 = rand.NextDouble() * Math.PI, ca2 = ca1 + 1.1 + rand.NextDouble() * 0.9;
                double n1x = Math.Cos(ca1), n1z = Math.Sin(ca1);
                double n2x = Math.Cos(ca2), n2z = Math.Sin(ca2);

                for (int x = (int)(cx - orbR - 1); x <= (int)(cx + orbR + 1); x++)
                    for (int z = (int)(cz - orbR - 1); z <= (int)(cz + orbR + 1); z++)
                        for (int y = (int)(oy - orbR - 1); y <= (int)(oy + orbR + 1); y++)
                        {
                            double dx = x - cx, dy = y - oy, dz = z - cz;
                            double d3 = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                            if (d3 > orbR + 0.4) continue;
                            // the hide is thickest around and under the
                            // waterline so the animated waves never clip
                            // through into the sealed hollow
                            double shellTh = y <= sea + 3 ? 7.0 : 4.0;
                            if (d3 > orbR - shellTh)
                            {
                                bool crack = y > sea + 1
                                    && (Math.Abs(dx * n1x + dz * n1z) < 1.1 || Math.Abs(dx * n2x + dz * n2z) < 1.1)
                                    && Hash01(x * 3 + y, z * 3 - y) < 0.8;
                                if (crack) { Set(x, y, z, 0); continue; }
                                double mn = Hash01(x * 5 + y * 3, z * 5 - y * 2);
                                Set(x, y, z, mn < 0.35 ? dsoil : drockId);
                            }
                            else
                            {
                                // sealed hollow: dry dark, a glowing heart
                                bool heart = Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1 && Math.Abs(dz) <= 1;
                                if (heart) Glow(x, y, z, glowG2);
                                Set(x, y, z, 0);
                            }
                        }

                // devastation growth crusting the dry hide. devgrowth is
                // dynamic and attaches to side faces too, so it sprouts
                // OUTWARD along the surface normal: upright on top, sideways
                // from the flanks, all around the sphere down to the waves.
                for (int i = 0; i < 120; i++)
                {
                    double ga = Hash01(i * 7, def.Seed) * Math.PI * 2;
                    double gp = -0.10 + Hash01(i * 13, def.Seed * 3) * 1.25;
                    double gx3 = Math.Cos(ga) * Math.Cos(gp), gy3 = Math.Sin(gp), gz3 = Math.Sin(ga) * Math.Cos(gp);
                    int px = (int)Math.Round(cx + gx3 * (orbR - 0.6));
                    int py = (int)Math.Round(oy + gy3 * (orbR - 0.6));
                    int pz = (int)Math.Round(cz + gz3 * (orbR - 0.6));
                    int qx = (int)Math.Round(cx + gx3 * (orbR + 0.7));
                    int qy = (int)Math.Round(oy + gy3 * (orbR + 0.7));
                    int qz = (int)Math.Round(cz + gz3 * (orbR + 0.7));
                    if (qy <= sea + 1) continue;
                    int g2 = growth[(int)(Hash01(i, 29) * growth.Length) % growth.Length];
                    if (g2 == 0) continue;
                    Set(px, py, pz, dsoil);                                 // growth roots in devastated soil
                    Set(qx, qy, qz, g2);
                }

                // the chains: taut from the hide straight down into the rock.
                // The block writer refuses anything below y=5, so "the mantle"
                // is y=6 here; the run still carves through the whole seabed.
                int n = 9;
                for (int i = 0; i < n; i++)
                {
                    double a0 = i * Math.PI * 2 / n + rand.NextDouble() * 0.35;
                    double tilt = 0.55 + rand.NextDouble() * 0.5;           // rad below horizontal
                    double ax = cx + Math.Cos(a0) * Math.Cos(tilt) * (orbR - 1);
                    double ay = oy - Math.Sin(tilt) * (orbR - 1);
                    double az = cz + Math.Sin(a0) * Math.Cos(tilt) * (orbR - 1);
                    double gr = R * (0.55 + rand.NextDouble() * 0.4);
                    double gxE = cx + Math.Cos(a0) * gr, gzE = cz + Math.Sin(a0) * gr;
                    ChainRun(ax, ay, az, gxE, 6, gzE, 0, 2.0);

                    // every third chain grew a deeper branch: hooked on a
                    // quarter of the way down the main run, running further
                    // out horizontally to its own anchor in the rock
                    if (i % 3 == 0)
                    {
                        double bf = 0.25;
                        double bx0 = ax + (gxE - ax) * bf;
                        double by0 = ay + (6 - ay) * bf;
                        double bz0 = az + (gzE - az) * bf;
                        double bAng = a0 + (rand.NextDouble() - 0.5) * 0.9;
                        double br2 = Math.Min(R * 1.5, gr * (1.7 + rand.NextDouble() * 0.4));
                        ChainRun(bx0, by0, bz0, cx + Math.Cos(bAng) * br2, 6, cz + Math.Sin(bAng) * br2, 0, 1.8);
                    }
                }
                break;
            }

            // ── THE KNEELING COLOSSUS ─────────────────────────────────────
            // A 110-block giant kneeling on the deep floor, carved from the
            // local rock with rusted-iron highlights: iron-edged part rims,
            // corroded plate courses, a rusted blade, ghostlight eyes behind
            // the visor and glowing crown studs. No ore, no gold, nothing
            // shiny. Only the crown and the blade break the surface.
            case "colossus":
            {
                int body = job.StoneId;
                int glowEye = IdFirst("landmassgenerator:ghostlight-green", "underwaterhorrors:ghostlight-green");
                int glowStud = IdFirst("landmassgenerator:ghostlight-blue", "underwaterhorrors:ghostlight-blue");
                int trim = rustB != 0 ? rustB : rustA;
                double yaw = rand.NextDouble() * Math.PI * 2;
                double cyw = Math.Cos(yaw), syw = Math.Sin(yaw);
                int y0 = Ground(cx, cz) - 2;

                int Armor(int x, int y, int z, double v, double e)
                {
                    // ghostlight highlights across the outer skin, densest on
                    // the deep lower body: seam studs along the part rims,
                    // loose flecks in the carved rock
                    bool skin = e > 0.90;
                    double depthBoost = y < y0 + 40 ? 2.5 : 1.0;
                    if (Math.Abs(v) > 0.86)
                    {
                        if (skin && Hash01(x * 11 + y * 5, z * 11 - y * 3) < 0.06 * depthBoost)
                            Glow(x, y, z, Hash01(x + z, y) < 0.7 ? glowEye : glowStud);
                        return trim;                                                // iron-edged part rims
                    }
                    double nz = Hash01(x * 3 + y * 7, z * 3 - y * 5);
                    if (skin && Hash01(x * 7 + y * 13, z * 7 + y * 5) < 0.012 * depthBoost)
                        Glow(x, y, z, Hash01(x - z, y * 3) < 0.7 ? glowEye : glowStud);
                    if (y < y0 + 34 && nz < 0.30) return Rust();                    // the deep rusts hardest
                    if (((y - y0) / 6) % 2 == 0 && nz < 0.35) return Rust();        // rusted plate courses
                    return body;                                                     // the carved rock
                }

                var colTop = new Dictionary<long, int>();                            // topmost statue block per column
                var sideSpots = new List<(int X, int Y, int Z)>();                   // flank cells for side-mounted thorns

                // A superellipsoid part in colossus-local space (+X forward),
                // rotated to world by the pose yaw. mode 0 carved rock with
                // iron, 1 iron trim, 2 mail (rust mix), 3 blade (rust mix).
                void Blob(double lxC, double lyC, double lzC, double rx, double ry, double rz, double expn, int mode)
                {
                    double wxC = cx + lxC * cyw - lzC * syw;
                    double wzC = cz + lxC * syw + lzC * cyw;
                    double reach = Math.Max(rx, rz) + 1;
                    for (int x = (int)(wxC - reach); x <= (int)(wxC + reach); x++)
                        for (int z = (int)(wzC - reach); z <= (int)(wzC + reach); z++)
                        {
                            // back to local space
                            double dx = x - cx, dz = z - cz;
                            double lx2 = dx * cyw + dz * syw, lz2 = -dx * syw + dz * cyw;
                            double u = (lx2 - lxC) / rx, w = (lz2 - lzC) / rz;
                            for (int y = (int)(lyC - ry); y <= (int)(lyC + ry); y++)
                            {
                                double v = (y - lyC) / ry;
                                double e = Math.Pow(Math.Pow(Math.Abs(u), expn) + Math.Pow(Math.Abs(v), expn) + Math.Pow(Math.Abs(w), expn), 1.0 / expn);
                                if (e > 1.0) continue;
                                int m = mode == 1 ? trim
                                    : mode == 2 || mode == 3 ? Rust()
                                    : Armor(x, y, z, v, e);
                                Set(x, y, z, m);
                                long ck = ((long)x << 32) | (uint)z;
                                if (!colTop.TryGetValue(ck, out int ct) || y > ct) colTop[ck] = y;
                                // devgrowth attaches to side faces, so thorns
                                // can creep out of the flanks: remember the
                                // cell just outward of sideways-facing skin
                                if (mode == 0 && e > 0.90 && y > lyC - ry + 4)
                                {
                                    double au = Math.Abs(u), aw = Math.Abs(w);
                                    if (Math.Max(au, aw) > Math.Abs(v)
                                        && Hash01(x * 23 + y * 11, z * 29 - y * 7) < 0.012)
                                    {
                                        int sxn, szn;
                                        if (au >= aw) { sxn = (int)Math.Round(Math.Sign(u) * cyw); szn = (int)Math.Round(Math.Sign(u) * syw); }
                                        else { sxn = (int)Math.Round(-Math.Sign(w) * syw); szn = (int)Math.Round(Math.Sign(w) * cyw); }
                                        sideSpots.Add((x + sxn, y, z + szn));
                                    }
                                }
                            }
                        }
                }

                // Everything is built from high-exponent superellipsoids: at
                // expn 9 they are rectangular boxes with barely-eased corners,
                // the blocky Minecraft-statue look, not ball joints.
                // legs: left planted forward, right kneeling, shin along the floor
                Blob(18, y0 + 3, -10, 7, 3.5, 5, 9, 0);       // left foot
                Blob(17, y0 + 15, -10, 5, 12, 5, 9, 0);       // left shin
                Blob(12, y0 + 30, -9, 5.5, 9, 5, 9, 0);       // left thigh lower
                Blob(5, y0 + 38, -7, 5.5, 8, 5, 9, 0);        // left thigh upper
                Blob(8, y0 + 10, 10, 5.5, 6.5, 5.5, 9, 0);    // right knee
                Blob(0, y0 + 7, 11, 5, 4.5, 4.5, 9, 0);       // right shin (lying)
                Blob(-8, y0 + 6, 12, 5, 4, 4.5, 9, 0);
                Blob(-16, y0 + 5, 13, 6, 3.5, 4, 9, 0);       // right foot, toes down
                Blob(4, y0 + 26, 9, 5.5, 11, 5, 9, 0);        // right thigh
                // hips, mail skirt, torso
                Blob(0, y0 + 35, 0, 9, 7, 12, 9, 2);          // mail skirt
                Blob(0, y0 + 44, 0, 10, 8, 13, 9, 0);         // pelvis
                Blob(1, y0 + 58, 0, 11, 11, 14, 9, 0);        // lower torso
                Blob(3, y0 + 74, 0, 12, 11, 16, 9, 0);        // chest
                Blob(4, y0 + 88, 0, 5.5, 5, 6.5, 9, 0);       // neck: joins chest to helm
                Blob(3, y0 + 84, -18, 7, 6, 7, 9, 0);         // left pauldron
                Blob(3, y0 + 84, 18, 7, 6, 7, 9, 0);          // right pauldron
                // left arm hugging the shield to the chest
                Blob(6, y0 + 74, -17, 4.5, 9, 4.5, 9, 0);
                Blob(12, y0 + 63, -11, 4, 8, 4, 9, 0);
                Blob(16, y0 + 57, -6, 3, 3, 3, 9, 0);         // hand
                Blob(20, y0 + 63, -6, 2, 15, 11, 9, 0);       // the shield, tucked in
                Blob(22, y0 + 63, -6, 1.5, 3, 3, 9, 1);       // gold boss
                // right arm raised with the greatsword
                Blob(5, y0 + 87, 24, 4.5, 4.5, 8, 9, 0);
                Blob(10, y0 + 92, 32, 4, 4, 6, 9, 0);
                Blob(13, y0 + 95, 38, 3, 3, 3, 9, 0);         // hand
                // the great helm; the crown breaks the surface
                Blob(5, y0 + 103, 0, 8.5, 11, 8.5, 9, 0);
                Blob(5, y0 + 113, 0, 7.5, 2, 7.5, 9, 1);      // iron crown band

                // eye slit: carved through the front of the helm
                for (int sz2 = -5; sz2 <= 5; sz2++)
                    for (int sx2 = 10; sx2 <= 14; sx2++)
                        for (int sy2 = 0; sy2 <= 1; sy2++)
                        {
                            int x = (int)Math.Round(cx + sx2 * cyw - sz2 * syw);
                            int z = (int)Math.Round(cz + sx2 * syw + sz2 * cyw);
                            Set(x, y0 + 103 + sy2, z, 0);
                        }
                // ghostlight eyes burning at the back of the slit, and
                // glowing studs around the crown band
                for (int side = -1; side <= 1; side += 2)
                {
                    int x = (int)Math.Round(cx + 10 * cyw - side * 3 * syw);
                    int z = (int)Math.Round(cz + 10 * syw + side * 3 * cyw);
                    Glow(x, y0 + 103, z, glowEye);
                }
                for (int k = 0; k < 8; k++)
                {
                    double ca2 = k * Math.PI / 4;
                    double lx3 = 5 + Math.Cos(ca2) * 7.0, lz3 = Math.Sin(ca2) * 7.0;
                    int x = (int)Math.Round(cx + lx3 * cyw - lz3 * syw);
                    int z = (int)Math.Round(cz + lx3 * syw + lz3 * cyw);
                    Glow(x, y0 + 113, z, glowStud);
                }

                // the greatsword: SOLID and straight, all rectangles. An iron
                // pommel and crossguard at the raised hand, then a rusted
                // blade rising point-up out of the sea, built from densely
                // overlapping boxes so there is not one floating block in it.
                Blob(12, y0 + 92.5, 38, 1.8, 1.8, 1.8, 9, 1);            // pommel
                Blob(13.6, y0 + 97, 38, 2.2, 1.6, 5.5, 9, 1);            // crossguard
                int blen = 32;
                for (int i = 0; i <= blen; i++)
                {
                    double f = i / (double)blen;
                    double lx2 = 14 + i * 0.52;                          // forward...
                    double ly2 = y0 + 98.5 + i * 0.85;                   // ...and up, dead straight
                    double wHalf = f > 0.86 ? 2.6 * (1.0 - f) / 0.14 + 0.6 : 2.6;   // tip taper only
                    Blob(lx2, ly2, 38, 1.6, 1.4, Math.Max(0.9, wHalf), 9, 3);
                }

                // devastation creeping over the statue: thorn growth rooted
                // in small devastated-soil patches on its upward faces
                int dsoilC = IdFirst("devastatedsoil-3", "devastatedsoil-1", "devastatedsoil-6");
                int[] growthC = {
                    Id("devgrowth-thorns"), Id("devgrowth-thorns"),
                    Id("devgrowth-shard"), Id("devgrowth-bush") };
                if (dsoilC != 0)
                    foreach (var kv in colTop)
                    {
                        int x = (int)(kv.Key >> 32), z = (int)kv.Key;
                        if (Hash01(x * 17 + kv.Value, z * 19) > 0.05) continue;
                        int g2 = growthC[(int)(Hash01(x, z) * growthC.Length) % growthC.Length];
                        if (g2 == 0) continue;
                        Set(x, kv.Value, z, dsoilC);
                        Set(x, kv.Value + 1, z, g2);
                    }
                // and out of the flanks: devgrowth attaches to side faces
                foreach (var s in sideSpots)
                {
                    int g2 = growthC[(int)(Hash01(s.X * 3, s.Z * 5) * growthC.Length) % growthC.Length];
                    if (g2 != 0) Set(s.X, s.Y, s.Z, g2);
                }
                break;
            }

            // ── THE VERTEBRAE SERPENT ─────────────────────────────────────
            // A colossal serpent skeleton curled on the lagoon floor around a
            // sunken ship, ribs arching 20+ blocks, entirely underwater.
            // Ghostlights stud its edges so it glows up through the water.
            case "serpent":
            {
                int bone = IdFirst("rock-chalk", "rock-limestone");
                int glowB = Id("underwaterhorrors:ghostlight-blue");
                int glowG = Id("underwaterhorrors:ghostlight-green");
                if (bone == 0) break;

                // The omega pose: a straight head/neck leg, a half-circle
                // body, a straight tapering tail leg. Not tangled, just one
                // clean Omega lying on the lagoon floor. size= is the
                // half-circle radius, so size=62 is a 330-block skeleton.
                double a0 = rand.NextDouble() * Math.PI * 2;
                double rotC = Math.Cos(a0), rotS = Math.Sin(a0);
                double Rc = Math.Max(30, R);
                double neckLen = Rc * 0.85, tailLen = Rc * 1.25;
                double arcLen = Math.PI * Rc;
                double totalLen = neckLen + arcLen + tailLen;

                var spine = new List<double[]>();     // wx, wz, y, normalAngle, arcPos, t
                void AddPt(double lx, double lz, double headingL, double arcPos)
                {
                    double wx = cx + lx * rotC - lz * rotS;
                    double wz = cz + lx * rotS + lz * rotC;
                    // clamped below the surface: a leg that crosses an islet
                    // slope must not breach the waves
                    double py = Math.Min(Ground((int)wx, (int)wz) + 3.0, sea - 6.0);
                    spine.Add(new[] { wx, wz, py, headingL + a0 + Math.PI / 2, arcPos, arcPos / totalLen });
                }
                double step2 = 1.5;
                for (double sN = 0; sN < neckLen; sN += step2)
                    AddPt(Rc, -neckLen + sN, Math.PI / 2, sN);           // the neck, dead straight
                for (double phA = 0; phA <= Math.PI + 1e-6; phA += step2 / Rc)
                    AddPt(Rc * Math.Cos(phA), Rc * Math.Sin(phA), phA + Math.PI / 2, neckLen + phA * Rc);
                for (double sT = 0; sT <= tailLen; sT += step2)
                    AddPt(-Rc, -sT, -Math.PI / 2, neckLen + arcLen + sT); // the tail, dead straight

                double nextVert = 0, nextRib = neckLen * 0.35;
                foreach (var sp in spine)
                {
                    double tailFrac = Math.Max(0, (sp[4] - neckLen - arcLen) / tailLen);
                    double coreR = 1.7 - 1.1 * tailFrac;                 // tail thins to its tip
                    bool vert = sp[4] >= nextVert;
                    if (vert) nextVert = sp[4] + 4;
                    double rr2 = vert ? coreR + 1.1 : coreR;             // distinct vertebra bulges
                    for (int x = (int)(sp[0] - rr2); x <= (int)(sp[0] + rr2); x++)
                        for (int z = (int)(sp[1] - rr2); z <= (int)(sp[1] + rr2); z++)
                            for (int y = (int)(sp[2] - rr2); y <= (int)(sp[2] + rr2); y++)
                            {
                                double d = Math.Sqrt((x - sp[0]) * (x - sp[0]) + (y - sp[2]) * (y - sp[2]) + (z - sp[1]) * (z - sp[1]));
                                if (d > rr2) continue;
                                Set(x, y, z, bone);
                            }
                    // dorsal ridge glow every few vertebrae
                    if (vert && Hash01((int)sp[0], (int)sp[1]) < 0.4)
                        Glow((int)sp[0], (int)(sp[2] + rr2 + 1), (int)sp[1], glowB);

                    // the ribcage: paired arcs all along the body, biggest
                    // amidships, none on the skull end or the thin tail tip
                    bool inBody = sp[4] > neckLen * 0.35 && tailFrac < 0.45;
                    if (sp[4] >= nextRib && inBody)
                    {
                        nextRib = sp[4] + 6;
                        double nx = Math.Cos(sp[3]), nz2 = Math.Sin(sp[3]);
                        double bodySpan = totalLen - neckLen * 0.35 - tailLen * 0.55;
                        double bodyF = Math.Sin(Math.PI * Math.Clamp((sp[4] - neckLen * 0.35) / bodySpan, 0, 1));
                        double ribR = 6.5 + 6.5 * bodyF + Hash01((int)sp[4], 7) * 1.5;
                        for (int side = -1; side <= 1; side += 2)
                        {
                            // the tall pair, arching up and over the spine
                            for (double ph = 0.15; ph < 2.35; ph += 0.06)
                            {
                                double outw = Math.Cos(ph) * ribR * side;
                                double up = Math.Sin(ph) * ribR * 1.6;
                                int x = (int)Math.Round(sp[0] + nx * outw);
                                int z = (int)Math.Round(sp[1] + nz2 * outw);
                                int y = (int)Math.Round(sp[2] + 1 + up);
                                if (y > sea - 3) continue;                // stay underwater
                                Set(x, y, z, bone);
                                if (ph > 2.28 && Hash01(x, z) < 0.35) Glow(x, y + 1, z, glowB);  // glowing rib tips
                            }
                            // the splayed pair, extending out the other way:
                            // low wide ribs lying toward the lagoon floor
                            for (double ph = 0.15; ph < 1.75; ph += 0.06)
                            {
                                double outw = Math.Sin(ph) * ribR * 1.35 * side;
                                double up = Math.Sin(Math.Min(Math.PI, ph * 1.8)) * ribR * 0.45;
                                int x = (int)Math.Round(sp[0] + nx * outw);
                                int z = (int)Math.Round(sp[1] + nz2 * outw);
                                int y = (int)Math.Round(sp[2] + up);
                                if (y > sea - 3) continue;
                                Set(x, y, z, bone);
                            }
                        }
                    }
                }

                // the skull, thrust forward off the straight neck
                var head = spine[0];
                double hAng = a0 - Math.PI / 2;                           // facing away down the neck line
                double hcx = Math.Cos(hAng), hcz = Math.Sin(hAng);
                double hx3 = head[0] + hcx * 5, hz3 = head[1] + hcz * 5;
                double hy3 = Ground((int)hx3, (int)hz3) + 4;
                void BoneBlob(double lx3, double ly3, double lz3, double rx, double ry, double rz, double expn)
                {
                    double wxC = hx3 + lx3 * hcx - lz3 * hcz;
                    double wzC = hz3 + lx3 * hcz + lz3 * hcx;
                    for (int x = (int)(wxC - Math.Max(rx, rz) - 1); x <= (int)(wxC + Math.Max(rx, rz) + 1); x++)
                        for (int z = (int)(wzC - Math.Max(rx, rz) - 1); z <= (int)(wzC + Math.Max(rx, rz) + 1); z++)
                            for (int y = (int)(ly3 - ry); y <= (int)(ly3 + ry); y++)
                            {
                                double dx = x - hx3, dz = z - hz3;
                                double lx4 = dx * hcx + dz * hcz, lz4 = -dx * hcz + dz * hcx;
                                double e = Math.Pow(Math.Pow(Math.Abs((lx4 - lx3) / rx), expn) + Math.Pow(Math.Abs((y - ly3) / ry), expn) + Math.Pow(Math.Abs((lz4 - lz3) / rz), expn), 1.0 / expn);
                                if (e <= 1.0) Set(x, y, z, bone);
                            }
                }
                BoneBlob(2, hy3 + 3, 0, 6, 4, 4.5, 2.6);                  // cranium
                BoneBlob(9, hy3 + 2, 0, 6, 2.2, 3, 2.4);                  // snout
                BoneBlob(8, hy3 - 1, 0, 6, 1.2, 2.6, 2.4);                // lower jaw, dropped open
                for (int i = 0; i < 5; i++)                               // teeth
                {
                    BoneBlob(5 + i * 2, hy3 + 0.5, 2.6, 0.5, 1.2, 0.5, 2);
                    BoneBlob(5 + i * 2, hy3 + 0.5, -2.6, 0.5, 1.2, 0.5, 2);
                }
                for (int side = -1; side <= 1; side += 2)                 // swept horns
                    for (int i = 0; i < 7; i++)
                        BoneBlob(-1 - i * 1.1, hy3 + 5 + i * 0.9, side * (3 + i * 0.5), 1.1, 1.1, 1.1, 2);
                // glowing eye sockets
                if (glowG != 0)
                    for (int side = -1; side <= 1; side += 2)
                    {
                        int x = (int)Math.Round(hx3 + 5 * hcx - side * 3.6 * hcz);
                        int z = (int)Math.Round(hz3 + 5 * hcz + side * 3.6 * hcx);
                        Set(x, (int)hy3 + 4, z, 0);
                        Glow(x, (int)hy3 + 3, z, glowG);
                    }

                // the kill, at the heart of the half-circle: a LARGE ship,
                // torn in two, the halves listing opposite ways with masts
                // and debris strewn across the tear
                double shipLz = Rc * 0.45;
                double shx = cx - shipLz * rotS, shz = cz + shipLz * rotC;
                double shipA = a0 + 0.7;
                double gapX = Math.Cos(shipA), gapZ = Math.Sin(shipA);
                int shipG = Ground((int)shx, (int)shz);
                HullTube(shx + gapX * 17, shipG + 5, shz + gapZ * 17, shipA, 18, 26, 6.5, 5.5, 0.35);        // bow half
                HullTube(shx - gapX * 15, shipG + 4, shz - gapZ * 15, shipA + 0.3, -38, 24, 6.5, 5.5, 0.45); // stern half
                int mast = IdFirst("planks-veryaged-we", "planks-aged-we");
                for (int mi = 0; mi < 2; mi++)
                {
                    double mA = shipA + 1.2 + mi * 0.9;
                    double mx0 = shx + (mi == 0 ? 4 : -6) * gapX, mz0 = shz + (mi == 0 ? 4 : -6) * gapZ;
                    for (int k = 0; k < 16 + mi * 6; k++)
                    {
                        int x = (int)Math.Round(mx0 + Math.Cos(mA) * k), z = (int)Math.Round(mz0 + Math.Sin(mA) * k);
                        if (mast != 0) Set(x, Ground(x, z) + 1 + k / 9, z, mast);   // fallen masts
                    }
                }
                for (int i = 0; i < 60; i++)
                {
                    double da = Hash01(i * 3, 41) * Math.PI * 2, dr2 = Hash01(i * 5, 43) * 14;
                    int x = (int)Math.Round(shx + Math.Cos(da) * dr2), z = (int)Math.Round(shz + Math.Sin(da) * dr2);
                    Set(x, Ground(x, z) + 1, z, Hash01(i, 47) < 0.7 ? Rust() : bone);
                }
                break;
            }

            // ── THE CRATER FORGE ──────────────────────────────────────────
            // Carves a real crater into the volcano cone, floods its throat
            // with lava, and hangs a crucible forge over the melt on four
            // colossal chains, reached by a catwalk from the rim.
            case "forge":
            {
                int lava = Id("lava-still-7");
                int craterR = Math.Clamp(def.Size, 12, 40);
                int gRim = 0;
                for (int i = 0; i < 4; i++)
                    gRim += Ground(cx + (int)(Math.Cos(i * 1.57) * craterR), cz + (int)(Math.Sin(i * 1.57) * craterR));
                gRim /= 4;
                int poolY = Math.Max(sea + 8, gRim - 32);

                for (int x = cx - craterR - 1; x <= cx + craterR + 1; x++)
                    for (int z = cz - craterR - 1; z <= cz + craterR + 1; z++)
                    {
                        double d = Math.Sqrt((x - cx) * (x - cx) + (double)(z - cz) * (z - cz));
                        if (d > craterR) continue;
                        int wall = Math.Max(poolY - 4, gRim - (int)((craterR - d) * 2.4));
                        int top2 = Ground(x, z) + 3;
                        for (int y = wall + 1; y <= top2; y++) Set(x, y, z, 0);   // the crater bowl
                        if (lava != 0 && wall >= poolY && wall < poolY + 5 && Hash01(x * 3, z * 5) < 0.25)
                            SetFluid(x, wall, z, lava);                           // molten seeps above the pool line
                        if (lava != 0 && wall < poolY)
                        {
                            for (int y = wall + 1; y <= poolY; y++) SetFluid(x, y, z, lava);  // the melt
                            if (lavaProbe == null) lavaProbe = (x, poolY, z);
                        }
                    }

                // the suspended crucible: a metal bowl full of lava
                int crY = poolY + 13;
                for (int x = cx - 8; x <= cx + 8; x++)
                    for (int z = cz - 8; z <= cz + 8; z++)
                    {
                        double d = Math.Sqrt((x - cx) * (x - cx) + (double)(z - cz) * (z - cz));
                        for (int y = crY; y <= crY + 7; y++)
                        {
                            double f = (y - crY) / 7.0;
                            double rr = 2.5 + f * 5.5;                            // the bowl widens upward
                            if (d > rr + 0.4) continue;
                            if (d > rr - 1.2) Set(x, y, z, y == crY + 7 ? plateB : Rust());
                            else if (y >= crY + 5 && lava != 0) SetFluid(x, y, z, lava);  // molten heart
                            else if (y < crY + 5) Set(x, y, z, Rust());
                        }
                        // the ring walkway around the crucible lip
                        if (d > 8.4 && d <= 10.6) Set(x, crY + 7, z, plateA);
                        if (d > 10.6 && d <= 11.8 && fenceNS != 0)
                            Set(x, crY + 8, z, Math.Abs(x - cx) > Math.Abs(z - cz) ? fenceNS : fenceEW);
                    }
                Clutter(cx + 9, crY + 8, cz + 2, "gearhugemetal9", 0);
                Clutter(cx - 9, crY + 8, cz - 3, "junktanksmall1", Math.PI / 2);
                Clutter(cx + 2, crY + 8, cz - 10, "valve2-aged", 0);

                // four chains up to the rim, and a catwalk in
                for (int i = 0; i < 4; i++)
                {
                    double a2 = i * Math.PI / 2 + 0.4;
                    double hx4 = cx + Math.Cos(a2) * 9.5, hz4 = cz + Math.Sin(a2) * 9.5;
                    double rx2 = cx + Math.Cos(a2) * (craterR - 1), rz2 = cz + Math.Sin(a2) * (craterR - 1);
                    // anchor into the CARVED bowl surface, two blocks deep:
                    // the natural Ground here is above the bowl cut, and
                    // anchoring to it left chain ends floating over the rim
                    int rimSurf = Math.Min(Ground((int)rx2, (int)rz2), Math.Max(poolY - 4, gRim - 2));
                    ChainRun(hx4, crY + 7, hz4, rx2, rimSurf - 2, rz2, 0, 1.7);
                }
                double ca = 2.0;
                double cx2 = cx + Math.Cos(ca) * (craterR - 1), cz3 = cz + Math.Sin(ca) * (craterR - 1);
                int rimY2 = Ground((int)cx2, (int)cz3);
                int steps2 = (int)(craterR * 1.3);
                for (int k = 0; k <= steps2; k++)
                {
                    double f = k / (double)steps2;
                    int x = (int)Math.Round(cx2 + (cx + Math.Cos(ca) * 10 - cx2) * f);
                    int z = (int)Math.Round(cz3 + (cz + Math.Sin(ca) * 10 - cz3) * f);
                    int y = (int)Math.Round(rimY2 + (crY + 7 - rimY2) * f);
                    Set(x, y, z, plateA);
                    Set(x + 1, y, z, plateA);
                    if (fenceNS != 0 && k % 2 == 0) Set(x, y + 1, z, fenceNS);
                }

                // three frozen lava runs spilling down the outer cone
                if (lava != 0)
                    for (int i = 0; i < 3; i++)
                    {
                        double a3 = rand.NextDouble() * Math.PI * 2;
                        double px3 = cx + Math.Cos(a3) * craterR, pz3 = cz + Math.Sin(a3) * craterR;
                        for (int k = 0; k < 70; k++)
                        {
                            px3 += Math.Cos(a3) * 1.3; pz3 += Math.Sin(a3) * 1.3;
                            int g2 = Ground((int)px3, (int)pz3);
                            if (g2 < sea + 5) break;
                            SetFluid((int)px3, g2, (int)pz3, lava);
                            if (Hash01((int)px3, (int)pz3) < 0.3)
                                SetFluid((int)px3 + (Hash01(k, i) < 0.5 ? 1 : -1), g2, (int)pz3, lava);
                        }
                    }
                break;
            }

            // ── THE DIVING BELL MINE ──────────────────────────────────────
            // A drowned mine over a rift. The sea floor here is a solid
            // ridge field of rock split by long chasms that fall away to the
            // mantle, with caves running off their walls. Ore is seeded
            // through the whole massif and thickest in the chasm faces,
            // ghostlights are pinned to the rock along the rims, and the
            // derrick on the shore lowers a house-sized diving bell into a
            // widened bay of the nearest rift. Older bells lie on the floor
            // or sit half swallowed by the walls, each holding a pocket of
            // air a diver can surface into.
            case "divingbell":
            {
                int host = job.StoneId != 0 ? job.StoneId : IdFirst("rock-limestone", "rock-granite");
                int host2 = IdFirst("rock-chert", "rock-shale", "rock-andesite");
                int glowG = IdFirst("landmassgenerator:ghostlight-green", "underwaterhorrors:ghostlight-green");
                int glowB = IdFirst("landmassgenerator:ghostlight-blue", "underwaterhorrors:ghostlight-blue");
                int brine = job.SaltWaterId;
                if (host2 == 0) host2 = host;

                double massifR = Math.Clamp(R * 0.74, 55, 104);
                double massifSkirt = massifR * 0.60;
                double peakY = sea - 16, troughY = sea - 60;

                // 1. THE SEA MOUNT. Rippling ridges of solid rock, filled
                //    from the real sea floor upward, so everything under the
                //    works is stone and nothing is hollow. Ridged noise
                //    (1 - |2n-1|) creases the crests instead of rounding
                //    them, which is what makes it read as mountains rather
                //    than dunes.
                var massifCache = new Dictionary<long, int>();
                int MassifY(int x, int z)
                {
                    long mkey = ((long)x << 24) ^ (uint)z;
                    if (massifCache.TryGetValue(mkey, out int mv)) return mv;
                    int nat = Ground(x, z);
                    double mdx = x - cx, mdz = z - cz;
                    double md = Math.Sqrt(mdx * mdx + mdz * mdz);
                    int outv;
                    if (md > massifR + massifSkirt || nat >= peakY) outv = nat;
                    else
                    {
                        double n1 = job.SurfNoise.Noise(x * 0.30, z * 0.30);
                        double n2 = job.RockBlend.Noise(x * 0.85, z * 0.85);
                        double ridge = 1.0 - Math.Abs(n1 * 2 - 1);
                        double topY = troughY + (peakY - troughY) * (Math.Pow(ridge, 0.72) * 0.80 + n2 * 0.20);
                        double f = md <= massifR ? 1.0 : Smooth((massifR + massifSkirt - md) / massifSkirt);
                        outv = Math.Max(nat, (int)Math.Round(nat + (topY - nat) * f));
                    }
                    massifCache[mkey] = outv;
                    return outv;
                }

                int mr = (int)(massifR + massifSkirt) + 2;
                for (int x = cx - mr; x <= cx + mr; x++)
                    for (int z = cz - mr; z <= cz + mr; z++)
                    {
                        double mdx = x - cx, mdz = z - cz;
                        if (Math.Sqrt(mdx * mdx + mdz * mdz) > massifR + massifSkirt) continue;
                        int nat = Ground(x, z), topY = MassifY(x, z);
                        for (int y = nat + 1; y <= topY; y++)
                            Set(x, y, z, job.RockBlend.Noise(x * 0.5 + y * 0.13, z * 0.5) > 0.54 ? host2 : host);
                    }

                // 1b. WHAT COUNTS AS ROCK. The ore and wreck passes must
                //     never write into open water, or a deposit reads as a
                //     ball of stone hanging in the rift, which is exactly
                //     what the first cut did. Solidity cannot be asked of
                //     the world here: the massif fill and every carve are
                //     still staged in the bulk accessor and invisible to a
                //     read. So it is answered from the same functions that
                //     built them, and only BELOW the natural sea bed does
                //     the world get the last word, which is also what keeps
                //     ore out of the natural cave network.
                var carved = new HashSet<long>();
                long VKey(int x, int y, int z)
                    => (((long)(x + 32768) & 0xFFFFL) << 40) | (((long)(z + 32768) & 0xFFFFL) << 24) | (uint)(y & 0xFFFF);
                var rockPos = new BlockPos(0, 0, 0, job.Dim);
                void Carve(int x, int y, int z)
                {
                    if (!InRect(x, z) || y < 6) return;
                    SetFluid(x, y, z, brine);
                    carved.Add(VKey(x, y, z));
                }
                bool IsRock(int x, int y, int z)
                {
                    if (!InRect(x, z) || y < 9 || y > MassifY(x, z)) return false;
                    if (carved.Contains(VKey(x, y, z))) return false;
                    if (y > Ground(x, z)) return true;                 // the massif fill, already staged
                    rockPos.Set(x, y, z);
                    return sapi.World.BlockAccessor.GetBlock(rockPos, BlockLayersAccess.SolidBlocks).Id != 0;
                }

                // 2. WHERE THE LAND IS. The rift is laid across the approach
                //    from the island, so every bell can be lowered from a
                //    beam that starts on the shore.
                double shoreAng = 0, shoreDist = 1e9;
                for (int i = 0; i < 96; i++)
                {
                    double a = i * Math.PI * 2 / 96;
                    for (double dd = 40; dd < R * 2.6; dd += 4)
                    {
                        int gx = (int)(cx + Math.Cos(a) * dd), gz = (int)(cz + Math.Sin(a) * dd);
                        if (Ground(gx, gz) > sea + 2)
                        {
                            if (dd < shoreDist) { shoreDist = dd; shoreAng = a; }
                            break;
                        }
                    }
                }
                bool haveShore = shoreDist < 1e9;
                if (!haveShore) { shoreAng = 0; shoreDist = massifR * 1.4; }
                double runX = -Math.Sin(shoreAng), runZ = Math.Cos(shoreAng);   // along the shore

                // 3. THE CHASMS. Each is a wobbling line in plan carved as a
                //    vertical slot: the half width breathes with depth, so
                //    the walls have ledges and overhangs like a cave instead
                //    of standing as two flat planes. They fall most of the
                //    way to the mantle, and the front rift carries widened
                //    bays where the bells go down.
                var bays = new List<(double X, double Z, int Rim, int Floor)>();
                var walls = new List<(double X, double Z, double Ux, double Uz, double Hw, int Rim, int Floor)>();
                int nCh = 3;
                for (int ci = 0; ci < nCh; ci++)
                {
                    double turn = (ci - 1) * 0.30 + (rand.NextDouble() - 0.5) * 0.16;
                    double ux = runX * Math.Cos(turn) - runZ * Math.Sin(turn);
                    double uz = runX * Math.Sin(turn) + runZ * Math.Cos(turn);
                    double off = ci == 0 ? 0 : (ci == 1 ? -massifR * 0.62 : -massifR * 1.05);
                    double ax = cx + Math.Cos(shoreAng) * off, az = cz + Math.Sin(shoreAng) * off;
                    double len = massifR * (ci == 0 ? 2.0 : 1.45);
                    double amp = 14 + rand.NextDouble() * 12;
                    double ph = rand.NextDouble() * 6.283;
                    int nBays = ci == 0 ? 5 : 1;
                    int steps = (int)len;

                    for (int s = 0; s <= steps; s++)
                    {
                        double t = s / (double)steps;
                        double along = (t - 0.5) * len;
                        double lat = amp * Math.Sin(t * 6.2831 * 1.3 + ph) + amp * 0.45 * Math.Sin(t * 6.2831 * 2.7 + ph * 1.7);
                        double px = ax + ux * along - uz * lat;
                        double pz = az + uz * along + ux * lat;

                        double taper = Math.Pow(Math.Sin(Math.PI * Math.Clamp(t, 0, 1)), 0.45);
                        double hw = (2.4 + 1.5 * Math.Sin(t * 21.0 + ci * 2.1)) * taper;
                        int nearBay = -1;
                        for (int b = 0; b < nBays; b++)
                        {
                            double tb = (b + 0.5) / nBays;
                            double dt = (t - tb) / 0.055;
                            hw += 8.5 * Math.Exp(-dt * dt) * taper;
                            if (Math.Abs(t - tb) < 0.004) nearBay = b;
                        }
                        if (hw < 1.2) continue;

                        int rim = MassifY((int)px, (int)pz);
                        // deep enough that the floor of the mid section is
                        // down among the mantle rock, not a shelf
                        int deep = (int)(78 + 30 * Math.Sin(t * 9.3 + ci));
                        int flr = Math.Max(14, rim - (int)(deep * taper) - 6);

                        for (int y = flr; y <= rim + 1; y++)
                        {
                            double hwy = hw * (0.86 + 0.30 * job.SurfNoise.Noise(s * 0.30 + ci * 40, y * 0.42));
                            for (double w = -hwy; w <= hwy; w += 0.5)
                            {
                                int wx = (int)Math.Round(px - uz * w), wz = (int)Math.Round(pz + ux * w);
                                if (y > MassifY(wx, wz)) continue;
                                Carve(wx, y, wz);
                            }
                            // a lamp left burning against one wall, deep down
                            if (s % 13 == 0 && y > flr + 4 && (y - flr) % 17 == (s / 13) % 17)
                            {
                                double side = (s / 13) % 2 == 0 ? 1 : -1;
                                Glow((int)Math.Round(px - uz * (hwy - 0.8) * side), y,
                                     (int)Math.Round(pz + ux * (hwy - 0.8) * side), (s % 26 == 0) ? glowG : glowB);
                            }
                        }

                        // ghostlights dotting both rims, the way a worked
                        // edge would be marked
                        if (s % 8 == 0)
                            for (int side = -1; side <= 1; side += 2)
                            {
                                double ex = px - uz * (hw * 1.12 + 1.2) * side, ez = pz + ux * (hw * 1.12 + 1.2) * side;
                                int er = MassifY((int)ex, (int)ez);
                                if (er > flr + 4) Glow((int)ex, er + 1, (int)ez, (s % 16 == 0) ? glowG : glowB);
                            }

                        if (s % 6 == 0) walls.Add((px, pz, ux, uz, hw, rim, flr));
                        if (nearBay >= 0 && ci == 0) bays.Add((px, pz, rim, flr));
                    }
                }

                // 4. SIDE CAVES off the chasm walls: short flooded tubes that
                //    swell into a pocket at the end, so the rift is worth
                //    swimming into and not just looking at. Carved before any
                //    ore goes in, so a deposit can never fill one.
                var pockets = new List<(double X, double Y, double Z, double Dx, double Dz)>();
                if (walls.Count > 0)
                    for (int i = 0; i < 9; i++)
                    {
                        var wsp = walls[(int)(Hash01(i * 61 + 3, def.Seed * 5) * walls.Count) % walls.Count];
                        double side = Hash01(i * 29, 11) < 0.5 ? 1 : -1;
                        double dirx = -wsp.Uz * side, dirz = wsp.Ux * side;
                        double yaw = (Hash01(i * 37, 19) - 0.5) * 1.1;
                        double dx3 = dirx * Math.Cos(yaw) - dirz * Math.Sin(yaw);
                        double dz3 = dirx * Math.Sin(yaw) + dirz * Math.Cos(yaw);
                        double frac = 0.25 + Hash01(i * 43, 23) * 0.55;
                        double px = wsp.X + dirx * (wsp.Hw - 1), pz = wsp.Z + dirz * (wsp.Hw - 1);
                        double py = wsp.Floor + (wsp.Rim - wsp.Floor) * frac;
                        int runLen = 22 + (int)(Hash01(i * 47, 29) * 24);
                        double dip = (Hash01(i * 59, 31) - 0.45) * 0.35;
                        double qx = px, qy = py, qz = pz;
                        for (int k = 0; k <= runLen; k++)
                        {
                            double cr2 = k > runLen - 8 ? 6.5 : 3.2 + 1.4 * Math.Sin(k * 0.5);
                            qx = px + dx3 * k; qz = pz + dz3 * k; qy = py + dip * k;
                            for (int ox = (int)-cr2; ox <= cr2; ox++)
                                for (int oz = (int)-cr2; oz <= cr2; oz++)
                                    for (int oy = (int)(-cr2 * 0.8); oy <= cr2 * 0.8; oy++)
                                    {
                                        if (ox * ox + oy * oy / 0.64 + oz * oz > cr2 * cr2) continue;
                                        int wx = (int)(qx + ox), wy = (int)(qy + oy), wz = (int)(qz + oz);
                                        if (wy < 10 || wy > MassifY(wx, wz) - 3) continue;
                                        Carve(wx, wy, wz);
                                    }
                        }
                        pockets.Add((qx, qy, qz, dx3, dz3));
                        Glow((int)px, (int)py + 2, (int)pz, glowG);       // a lamp at the cave mouth
                    }

                // 5. THE ORE. Every ore-and-rock pair below exists in the
                //    game's own allowedVariants list AND in the matching ore
                //    type property file, which is the part that catches the
                //    codes that only look real.
                (string Ore, string Rock)[] seams = {
                    ("ore-medium-ilmenite-basalt", "rock-basalt"),               // titanium
                    ("ore-rich-ilmenite-basalt", "rock-basalt"),
                    ("ore-medium-ilmenite-peridotite", "rock-peridotite"),
                    ("ore-medium-pentlandite-peridotite", "rock-peridotite"),    // nickel
                    ("ore-rich-pentlandite-peridotite", "rock-peridotite"),
                    ("ore-medium-chromite-peridotite", "rock-peridotite"),
                    ("ore-rich-chromite-kimberlite", "rock-kimberlite"),
                    ("ore-rich-uranium-slate", "rock-slate"),
                    ("ore-medium-uranium-granite", "rock-granite"),
                    ("ore-medium-bismuthinite-granite", "rock-granite"),
                    ("ore-fluorite-limestone", "rock-limestone"),
                    ("ore-fluorite-slate", "rock-slate"),
                    ("ore-fluorite-phyllite", "rock-phyllite"),
                    ("ore-lapislazuli-limestone", "rock-limestone"),
                    ("ore-lapislazuli-whitemarble", "rock-whitemarble"),
                    ("ore-cinnabar-basalt", "rock-basalt"),
                    ("ore-cinnabar-slate", "rock-slate"),
                    ("ore-medium-malachite-greenmarble", "rock-greenmarble"),
                    ("ore-medium-rhodochrosite-limestone", "rock-limestone"),
                    ("ore-corundum-whitemarble", "rock-whitemarble"),
                    ("ore-corundum-peridotite", "rock-peridotite"),
                    ("ore-graphite-phyllite", "rock-phyllite"),
                    ("ore-olivine-peridotite", "rock-peridotite"),
                    ("ore-high-olivine_peridot-peridotite", "rock-peridotite"),
                    ("ore-sylvite-halite", "rock-halite"),
                    ("ore-borax-chalk", "rock-chalk"),
                    ("ore-kernite-claystone", "rock-claystone"),
                    ("ore-medium-galena_nativesilver-limestone", "rock-limestone"),
                    ("ore-medium-sphalerite-limestone", "rock-limestone"),
                    ("ore-medium-diamond-kimberlite", "rock-kimberlite"),
                    ("ore-medium-emerald-limestone", "rock-limestone"),
                    ("ore-medium-cassiterite-granite", "rock-granite"),
                };
                var oreIds = new int[seams.Length];
                var oreRock = new int[seams.Length];
                for (int i = 0; i < seams.Length; i++)
                {
                    oreIds[i] = Need(seams[i].Ore);
                    oreRock[i] = Need(seams[i].Rock);
                }

                // A deposit. Never a sphere: the radius is chewed by noise
                // and the last fifth of it dissolves into the surrounding
                // stone, and the ore itself follows a noise field so it
                // comes in connected clumps the way a real seam does. When
                // the ore's host rock IS the massif rock the deposit only
                // sprinkles ore and leaves the stone alone; a foreign host
                // is written as an intrusion of that rock instead.
                void OreBody(double px, double py, double pz, double vr, int oreId, int rockId, double rate)
                {
                    if (oreId == 0 && rockId == 0) return;
                    bool intrusion = rockId != 0 && rockId != host;
                    int ir = (int)Math.Ceiling(vr) + 2;
                    for (int x = (int)px - ir; x <= (int)px + ir; x++)
                        for (int z = (int)pz - ir; z <= (int)pz + ir; z++)
                            for (int y = (int)(py - vr) - 1; y <= (int)(py + vr) + 1; y++)
                            {
                                double ax2 = x - px, ay2 = (y - py) / 0.72, az2 = z - pz;
                                double dist = Math.Sqrt(ax2 * ax2 + ay2 * ay2 + az2 * az2);
                                double lump = vr * (0.70 + 0.55 * job.RockBlend.Noise(x * 0.55 + y * 0.31, z * 0.55 - y * 0.17));
                                if (dist > lump) continue;
                                if (!IsRock(x, y, z)) continue;
                                double edge = dist / Math.Max(0.001, lump);
                                if (edge > 0.72 && Hash01(x * 13 + y * 7, z * 17 - y * 3) < (edge - 0.72) / 0.28) continue;
                                bool ore = oreId != 0
                                    && job.SurfNoise.Noise(x * 0.9 + y * 0.5, z * 0.9 - y * 0.35) > 1.0 - rate;
                                if (ore) Set(x, y, z, oreId);
                                else if (intrusion) Set(x, y, z, rockId);
                            }
                }

                // 5a. The worked faces: deposits set into both walls of every
                //     chasm at every depth, offset so each one is cut open by
                //     the slot and shows its ore to a diver.
                if (walls.Count > 0)
                    for (int i = 0; i < 120; i++)
                    {
                        var wsp = walls[(int)(Hash01(i * 19 + 7, def.Seed) * walls.Count) % walls.Count];
                        int si = (int)(Hash01(i * 31 + 5, def.Seed * 7) * seams.Length) % seams.Length;
                        if (oreRock[si] == 0) continue;
                        double vr = 4.5 + Hash01(i * 23, def.Seed * 3) * 5.5;
                        double side = Hash01(i * 41, 13) < 0.5 ? 1 : -1;
                        double frac = 0.05 + Hash01(i * 53, 17) * 0.90;
                        double sy = wsp.Floor + (wsp.Rim - wsp.Floor) * frac;
                        double sxp = wsp.X - wsp.Uz * (wsp.Hw + vr * 0.45) * side;
                        double szp = wsp.Z + wsp.Ux * (wsp.Hw + vr * 0.45) * side;
                        OreBody(sxp, sy, szp, vr, oreIds[si], oreRock[si], 0.34 + Hash01(i * 71, 9) * 0.20);
                        // the light that was hung to work it, against the face
                        if (i % 3 == 0)
                            Glow((int)Math.Round(wsp.X - wsp.Uz * (wsp.Hw - 0.8) * side), (int)sy + 1,
                                 (int)Math.Round(wsp.Z + wsp.Ux * (wsp.Hw - 0.8) * side), i % 6 == 0 ? glowG : glowB);
                    }

                // 5b. And through the whole body of rock under the works, so
                //     the mine is worth digging into anywhere and not just
                //     along the two faces that happen to be open.
                for (int i = 0; i < 320; i++)
                {
                    double a = Hash01(i * 7 + 1, def.Seed * 11) * Math.PI * 2;
                    double dd = Math.Sqrt(Hash01(i * 13 + 5, def.Seed * 3)) * massifR * 1.12;
                    int ox2 = (int)(cx + Math.Cos(a) * dd), oz2 = (int)(cz + Math.Sin(a) * dd);
                    int top = MassifY(ox2, oz2);
                    int oy2 = 16 + (int)(Hash01(i * 17 + 3, def.Seed * 5) * Math.Max(4, top - 22));
                    if (oy2 > top - 2) continue;
                    if (!IsRock(ox2, oy2, oz2)) continue;
                    int si = (int)(Hash01(i * 29 + 9, def.Seed * 13) * seams.Length) % seams.Length;
                    if (oreRock[si] == 0) continue;
                    OreBody(ox2, oy2, oz2, 3.0 + Hash01(i * 37, 7) * 4.0, oreIds[si], oreRock[si],
                            0.26 + Hash01(i * 43, 19) * 0.18);
                }

                // 5c. A seam at the end of every side cave, the reason the
                //     cave was driven in the first place.
                for (int i = 0; i < pockets.Count; i++)
                {
                    var pk = pockets[i];
                    int si = (int)(Hash01(i * 73, def.Seed * 3) * seams.Length) % seams.Length;
                    if (oreRock[si] == 0) continue;
                    OreBody(pk.X + pk.Dx * 6, pk.Y, pk.Z + pk.Dz * 6, 7.5, oreIds[si], oreRock[si], 0.42);
                    Glow((int)pk.X, (int)pk.Y + 3, (int)pk.Z, i % 2 == 0 ? glowB : glowG);
                }

                // 6. THE DERRICK. One arm per bay: a braced tower on the
                //    shore, a box girder out over the rift with a walkway
                //    and railings, and a chain dropping a bell into the bay.
                if (bays.Count > 1)
                {
                    var kept = new List<(double X, double Z, int Rim, int Floor)>();
                    foreach (var b in bays)
                    {
                        bool near = false;
                        foreach (var k in kept)
                            if (Math.Abs(b.X - k.X) + Math.Abs(b.Z - k.Z) < 26) { near = true; break; }
                        if (!near) kept.Add(b);
                    }
                    bays = kept;
                }
                for (int i = 0; i < bays.Count && i < 5; i++)
                {
                    var bay = bays[i];
                    int bx0 = 0, bz0 = 0, gy = 0;
                    bool found = false;
                    for (double dd = 12; dd < 240; dd += 3)
                    {
                        int gx = (int)(bay.X + Math.Cos(shoreAng) * dd), gz = (int)(bay.Z + Math.Sin(shoreAng) * dd);
                        if (Ground(gx, gz) > sea + 2)
                        {
                            bx0 = (int)(bay.X + Math.Cos(shoreAng) * (dd + 7));
                            bz0 = (int)(bay.Z + Math.Sin(shoreAng) * (dd + 7));
                            gy = Ground(bx0, bz0);
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        // no land in reach: stand the tower on a rock pier
                        // rising out of the ridge instead of shipping a mine
                        // with no headgear
                        bx0 = (int)(bay.X + Math.Cos(shoreAng) * 34);
                        bz0 = (int)(bay.Z + Math.Sin(shoreAng) * 34);
                        gy = MassifY(bx0, bz0);
                        for (int px2 = -3; px2 <= 3; px2++)
                            for (int pz2 = -3; pz2 <= 3; pz2++)
                                if (px2 * px2 + pz2 * pz2 <= 9)
                                    for (int y = gy; y <= sea + 1; y++) Set(bx0 + px2, y, bz0 + pz2, host);
                        gy = sea + 1;
                    }
                    int towerTop = Math.Max(sea + 15, gy + 13);

                    for (int leg = 0; leg < 4; leg++)
                    {
                        int lx = bx0 + ((leg & 1) == 0 ? -3 : 3), lz = bz0 + ((leg & 2) == 0 ? -3 : 3);
                        for (int y = gy - 2; y <= towerTop; y++) Set(lx, y, lz, Rust());
                    }
                    for (int y = gy + 2; y <= towerTop; y += 4)
                        for (int e = -3; e <= 3; e++)
                        {
                            Set(bx0 + e, y, bz0 - 3, Rust()); Set(bx0 + e, y, bz0 + 3, Rust());
                            Set(bx0 - 3, y, bz0 + e, Rust()); Set(bx0 + 3, y, bz0 + e, Rust());
                        }
                    for (int e = -3; e <= 3; e++)
                        for (int e2 = -3; e2 <= 3; e2++)
                            Set(bx0 + e, towerTop, bz0 + e2, rustB != 0 ? rustB : rustA);

                    double tipY = towerTop + 8 + rand.NextDouble() * 6;
                    Girder(bx0, towerTop + 1, bz0, bay.X, tipY, bay.Z, 2.0, true);
                    Girder(bx0 - Math.Cos(shoreAng) * 3, gy + 2, bz0 - Math.Sin(shoreAng) * 3,
                           bx0 + (bay.X - bx0) * 0.42, towerTop + 1 + (tipY - towerTop) * 0.42,
                           bz0 + (bay.Z - bz0) * 0.42, 1.0, false);
                    double anx = bx0 + Math.Cos(shoreAng) * 24, anz = bz0 + Math.Sin(shoreAng) * 24;
                    ChainRun(bay.X, tipY, bay.Z, anx, Ground((int)anx, (int)anz) + 1, anz, 3, 1.3);
                    Clutter(bx0, towerTop + 1, bz0 - 2, i % 2 == 0 ? "gearhugemetal9" : "gearhugemetal15", shoreAng);
                    Clutter(bx0 + 2, towerTop + 1, bz0 + 2, "junktanksmall1", shoreAng);

                    // the bell, lowered to a different level in every bay:
                    // just under the rim, halfway down, or resting on the
                    // floor of the rift
                    double br2 = 5.8 + rand.NextDouble() * 1.2;
                    double bh2 = 13 + rand.NextDouble() * 4;
                    double span = Math.Max(6, bay.Rim - bay.Floor - bh2 - 3);
                    double lvl = 0.30 + (i % 3) * 0.30;
                    double bellLip = bay.Rim - 10 - span * 0.55 * lvl;
                    bool broken = i == 2 && bays.Count >= 4;          // one chain hangs empty
                    if (broken) ChainRun(bay.X, tipY - 2, bay.Z, bay.X, bay.Rim - 6, bay.Z, 0, 1.6);
                    else
                    {
                        ChainRun(bay.X, tipY - 2, bay.Z, bay.X, bellLip + bh2 + 1, bay.Z, 0, 1.6);
                        Bell(bay.X, bellLip, bay.Z, br2, bh2, 0, 0, 0.04, i % 2 == 0 ? glowG : glowB);
                    }
                }

                // 7. THE OLDER WORKS. Bells that came off their chains lie on
                //    the rift floor and sit half swallowed by the walls, and
                //    the wrecked tackle is scattered around them. Every one
                //    of them is DRY inside with its mouth cut open, so each
                //    is an air pocket a diver can surface into, and none of
                //    them floats: the floor ones rest on the floor and the
                //    wall ones are driven into solid rock.
                string[] junk = { "junkchain2", "junkchain5", "junkbeamstraight2", "junksheet4", "junktanksmallbase", "junkpipe3" };

                // driven into the chasm walls, mouth opening on the rift
                if (walls.Count > 0)
                    for (int i = 0; i < 10; i++)
                    {
                        var wsp = walls[(int)(Hash01(i * 83 + 11, def.Seed * 9) * walls.Count) % walls.Count];
                        double side = Hash01(i * 67, 7) < 0.5 ? 1 : -1;
                        double dirx = -wsp.Uz * side, dirz = wsp.Ux * side;
                        double frac = 0.12 + Hash01(i * 89, 23) * 0.76;
                        double wy = wsp.Floor + (wsp.Rim - wsp.Floor) * frac;
                        double br4 = 4.5 + Hash01(i * 31, 41) * 2.0;
                        double bh4 = 10 + Hash01(i * 53, 29) * 4;
                        double fx2 = wsp.X + dirx * (wsp.Hw - 0.5), fz2 = wsp.Z + dirz * (wsp.Hw - 0.5);
                        double taz2 = Math.Atan2(dirz, dirx);
                        double tilt2 = 1.35 + (Hash01(i * 97, 13) - 0.5) * 0.5;
                        Bell(fx2, wy, fz2, br4, bh4, tilt2, taz2, 0.16,
                             i % 2 == 0 ? glowG : glowB, true, true);
                        for (int k = 0; k < 3; k++)
                        {
                            double jd = br4 + 1 + Hash01(k * 11, i * 5) * 4;
                            int jx = (int)(fx2 - dirx * jd), jz = (int)(fz2 - dirz * jd);
                            Clutter(jx, (int)wy - (int)br4, jz, junk[(int)(Hash01(k * 7, i * 3) * junk.Length) % junk.Length], taz2);
                        }
                    }

                // lying on the rift floor and out on the ridges
                for (int i = 0; i < 11; i++)
                {
                    double bx, bz;
                    int by;
                    if (i < 6 && walls.Count > 0)
                    {
                        var wsp = walls[(int)(Hash01(i * 97 + 3, def.Seed * 5) * walls.Count) % walls.Count];
                        bx = wsp.X; bz = wsp.Z; by = wsp.Floor + 1;      // resting ON the floor
                    }
                    else
                    {
                        double a = Hash01(i * 97 + 3, def.Seed * 5) * Math.PI * 2;
                        double dd = massifR * (0.35 + Hash01(i * 29, 13) * 0.55);
                        bx = cx + Math.Cos(a) * dd; bz = cz + Math.Sin(a) * dd;
                        by = MassifY((int)bx, (int)bz) - 2;              // sunk into the ridge
                    }
                    double br3 = 5.5 + Hash01(i * 37, 17) * 2.5;
                    Bell(bx, by, bz, br3, 11 + Hash01(i * 51, 23) * 4, 0.9 + Hash01(i * 43, 19) * 0.7,
                         Hash01(i * 59, 29) * Math.PI * 2, 0.34, i % 3 == 0 ? glowG : 0, true, true);
                    for (int k = 0; k < 5; k++)
                    {
                        double ja = Hash01(i * 61 + k, 31) * Math.PI * 2, jd = br3 + 2 + Hash01(k * 7, i) * 9;
                        int jx = (int)(bx + Math.Cos(ja) * jd), jz = (int)(bz + Math.Sin(ja) * jd);
                        int jy = by - (int)br3;
                        if (Hash01(k * 13, i * 3) < 0.5) Clutter(jx, jy, jz, junk[(int)(Hash01(k, i) * junk.Length) % junk.Length], ja);
                        else Set(jx, jy, jz, Rust());
                    }
                }

                // torn hull sections and fallen beams among the ridges: the
                // gear that came down with the bells
                for (int i = 0; i < 5; i++)
                {
                    double a = Hash01(i * 101 + 7, def.Seed * 17) * Math.PI * 2;
                    double dd = massifR * (0.25 + Hash01(i * 41, 37) * 0.70);
                    double hx2 = cx + Math.Cos(a) * dd, hz2 = cz + Math.Sin(a) * dd;
                    int hy2 = MassifY((int)hx2, (int)hz2) - 1;
                    HullTube(hx2, hy2, hz2, Hash01(i * 19, 23) * Math.PI * 2,
                             40 + Hash01(i * 29, 31) * 60, 16 + (int)(Hash01(i * 13, 11) * 14),
                             3.5, 2.8, 0.45);
                    for (int k = 0; k < 4; k++)
                    {
                        double ja = Hash01(i * 7 + k, 43) * Math.PI * 2, jd = 6 + Hash01(k * 5, i) * 12;
                        int jx = (int)(hx2 + Math.Cos(ja) * jd), jz = (int)(hz2 + Math.Sin(ja) * jd);
                        Clutter(jx, MassifY(jx, jz) + 1, jz, junk[(int)(Hash01(k * 3, i * 7) * junk.Length) % junk.Length], ja);
                    }
                }

                // chains left draped over the rims and down into the rift
                if (walls.Count > 0)
                    for (int i = 0; i < 6; i++)
                    {
                        var wsp = walls[(int)(Hash01(i * 113 + 5, def.Seed * 19) * walls.Count) % walls.Count];
                        double side = Hash01(i * 71, 17) < 0.5 ? 1 : -1;
                        double dirx = -wsp.Uz * side, dirz = wsp.Ux * side;
                        double ex2 = wsp.X + dirx * (wsp.Hw + 4), ez2 = wsp.Z + dirz * (wsp.Hw + 4);
                        double dy2 = wsp.Floor + (wsp.Rim - wsp.Floor) * (0.30 + Hash01(i * 79, 13) * 0.45);
                        ChainRun(ex2, MassifY((int)ex2, (int)ez2) + 1, ez2,
                                 wsp.X + dirx * (wsp.Hw - 1), dy2, wsp.Z + dirz * (wsp.Hw - 1), 0, 1.4);
                    }
                break;
            }

            // ── THE HILL GRANARY ──────────────────────────────────────────
            // What the terraces were farmed for, gone to ruin: a round silo
            // with its top courses fallen away, a barn whose footings and
            // roof timbers still stand, hay spilled through both, a cobbled
            // threshing floor, and the tumbled remains of the yard wall.
            case "granary":
            {
                string rockName = "granite";
                var stoneBlock = sapi.World.GetBlock(job.StoneId);
                if (stoneBlock?.Code?.Path != null && stoneBlock.Code.Path.StartsWith("rock-"))
                    rockName = stoneBlock.Code.Path.Substring(5);
                int brick = IdFirst($"stonebricks-{rockName}", "stonebricks-granite");
                int cracked = IdFirst($"crackedstonebricks-{rockName}", "crackedstonebricks-granite", $"stonebricks-{rockName}");
                int cobble = IdFirst($"cobblestone-{rockName}", "cobblestone-granite");
                int mossy = IdFirst($"mossycobble-{rockName}", $"cobblestone-{rockName}", "cobblestone-granite");
                int timber = IdFirst("log-placed-aged-ud", "planks-aged-ud");
                int timberWE = IdFirst("log-placed-aged-we", "planks-aged-we");
                int plank = IdFirst("planks-aged-we", "planks-veryaged-we");
                int hay = IdFirst("hay-normal-ud", "hay-aged-ud");
                int[] vessels = { Need("lootvessel-food"), Need("lootvessel-seed"), Need("lootvessel-tool") };
                int basket = IdFirst("stationarybasket-n", "stationarybasket-e");
                int quern = IdFirst($"quern-{rockName}", "quern-granite");
                int grass2 = IdFirst("tallgrass-medium-free", "tallgrass-short-free");
                if (brick == 0) break;
                if (cobble == 0) cobble = brick;
                if (mossy == 0) mossy = cobble;
                if (hay == 0) hay = plank;
                if (timber == 0) timber = plank;

                double yaw3 = rand.NextDouble() * Math.PI * 2;
                double cy3 = Math.Cos(yaw3), sy3 = Math.Sin(yaw3);
                int g0 = Ground(cx, cz);
                // Everything below is drawn in the compound's own frame:
                // u runs along the barn, w across it, dy above the yard.
                // The frame is rotated by yaw3, so every surface is rastered
                // at half-block steps: a whole-block step in a rotated frame
                // rounds into a lattice with holes in it.
                void Put(double u, double w, int dy, int id)
                {
                    if (id == 0) return;
                    Set((int)Math.Round(cx + u * cy3 - w * sy3), g0 + dy, (int)Math.Round(cz + u * sy3 + w * cy3), id);
                }
                int GroundAt(double u, double w)
                    => Ground((int)Math.Round(cx + u * cy3 - w * sy3), (int)Math.Round(cz + u * sy3 + w * cy3));

                // the silo, its rim fallen away toward one side
                double siloU = -R * 0.42, siloW = 0;
                double siloR = Math.Clamp(R * 0.22, 4.0, 6.5);
                int siloH = (int)Math.Clamp(R * 0.85, 12, 22);
                double fallAng = rand.NextDouble() * Math.PI * 2;
                for (int dy = -3; dy <= siloH; dy++)
                    for (double u = siloU - siloR - 1; u <= siloU + siloR + 1; u += 0.5)
                        for (double w = siloW - siloR - 1; w <= siloW + siloR + 1; w += 0.5)
                        {
                            double du = u - siloU, dw = w - siloW;
                            double d = Math.Sqrt(du * du + dw * dw);
                            double ang = Math.Atan2(dw, du);
                            int rimH = (int)(siloH - 3.5 * (Math.Cos(ang - fallAng) + 1)
                                - Hash01((int)(u * 7), (int)(w * 11)) * 3);
                            if (dy > rimH) continue;
                            if (d > siloR - 1.15 && d <= siloR + 0.35)
                            {
                                if (dy >= 0 && dy <= 2 && Math.Abs(ang) < 0.42) continue;     // the doorway
                                Put(u, w, dy, dy > rimH - 3 || Hash01((int)(u * 13) + dy, (int)(w * 17)) < 0.22 ? cracked : brick);
                            }
                            else if (d <= siloR - 1.15 && dy < 0) Put(u, w, dy, cobble);
                            else if (d <= siloR - 1.15 && dy <= 3 && Hash01((int)(u * 3) + dy * 5, (int)(w * 7)) < 0.8)
                                Put(u, w, dy, hay);                                          // the grain still in it
                        }
                for (int k = 0; k < 14; k++)
                    Put(siloU + siloR + 0.5 + Hash01(k * 5, 3) * 5, siloW + (Hash01(k * 9, 7) - 0.5) * 5, 0,
                        Hash01(k, 11) < 0.55 ? hay : cobble);

                // the barn: footing walls, standing posts, surviving beams
                double barnL = Math.Clamp(R * 0.45, 7, 12), barnW2 = Math.Clamp(R * 0.30, 4.5, 8);
                double barnU = R * 0.28;
                for (double u = -barnL; u <= barnL; u += 0.5)
                    for (double w = -barnW2; w <= barnW2; w += 0.5)
                    {
                        bool edge = Math.Abs(Math.Abs(u) - barnL) < 0.5 || Math.Abs(Math.Abs(w) - barnW2) < 0.5;
                        if (edge)
                        {
                            bool door = Math.Abs(w) < 1.6 && Math.Abs(u - barnL) < 0.5;
                            int h2 = door ? -1 : 1 + (int)(Hash01((int)(u * 11), (int)(w * 13)) * 2);
                            for (int dy = -2; dy <= h2; dy++)
                                Put(barnU + u, w, dy, dy < 0 ? cobble
                                    : (Hash01((int)(u * 7) + dy, (int)(w * 5)) < 0.35 ? mossy : cobble));
                        }
                        else Put(barnU + u, w, -1, Hash01((int)(u * 17), (int)(w * 19)) < 0.5 ? cobble : mossy);
                    }
                for (double u = -barnL; u <= barnL; u += 3)
                    for (int side = -1; side <= 1; side += 2)
                    {
                        if (Hash01((int)(u * 23), side) < 0.25) continue;      // some posts are gone
                        int postH = 4 + (int)(Hash01((int)(u * 29), side * 3) * 2);
                        for (int dy = 0; dy <= postH; dy++) Put(barnU + u, side * barnW2, dy, timber);
                        if (Hash01((int)(u * 31), side * 5) < 0.55)
                            for (double w = -barnW2; w <= barnW2; w += 0.5) Put(barnU + u, w, postH + 1, timberWE);
                    }
                for (double u = -barnL * 0.9; u <= -barnL * 0.15; u += 0.5)
                    for (double w = -barnW2 + 1; w <= barnW2 - 1; w += 0.5)
                        if (Hash01((int)(u * 37), (int)(w * 41)) < 0.7) Put(barnU + u, w, 7, plank);
                Put(barnU - barnL * 0.6, -barnW2 + 2, 0, hay);
                Put(barnU - barnL * 0.6, -barnW2 + 3, 0, hay);
                Put(barnU - barnL * 0.55, -barnW2 + 2, 1, hay);
                Put(barnU + barnL * 0.2, barnW2 - 2, 0, vessels[0]);
                Put(barnU + barnL * 0.45, barnW2 - 3, 0, vessels[1]);
                Put(barnU - barnL * 0.1, barnW2 - 2, 0, basket);
                Put(barnU + barnL * 0.6, -barnW2 + 2, 0, quern);

                // the threshing floor, and the yard wall tumbling around it all
                double thU = -R * 0.05, thW = -R * 0.50, thR = Math.Clamp(R * 0.20, 3.5, 7);
                for (double u = -thR; u <= thR; u += 0.5)
                    for (double w = -thR; w <= thR; w += 0.5)
                    {
                        if (u * u + w * w > thR * thR) continue;
                        int gh = GroundAt(thU + u, thW + w) - g0;
                        if (Math.Abs(gh) > 3) continue;
                        Put(thU + u, thW + w, gh, Hash01((int)(u * 43), (int)(w * 47)) < 0.25 ? mossy : cobble);
                    }
                for (int k = 0; k < 150; k++)
                {
                    double wa = k / 150.0 * Math.PI * 2;
                    double wr = R * 0.78 * (1 + 0.12 * Math.Sin(wa * 3 + 1.2));
                    double u = Math.Cos(wa) * wr, w = Math.Sin(wa) * wr;
                    int gh = GroundAt(u, w) - g0;
                    int wh = Hash01(k * 3, 5) < 0.35 ? 0 : 1 + (int)(Hash01(k * 7, 9) * 2);
                    for (int dy = 0; dy <= wh; dy++)
                        Put(u, w, gh + dy, Hash01(k + dy * 3, 13) < 0.4 ? mossy : cobble);
                    if (grass2 != 0 && Hash01(k * 11, 17) < 0.25) Put(u + 1.4, w, gh + wh + 1, grass2);
                }
                break;
            }
        }

        ba.Commit();

        if (missingCodes.Count > 0)
            sapi.Logger.Notification("[landmassgen] {0}: {1} block code(s) did not resolve: {2}",
                def.Kind, missingCodes.Count, string.Join(", ", missingCodes));

        if (glowSpots.Count > 0)
        {
            var gba = sapi.World.BlockAccessor;
            var gpos = new BlockPos(0, 0, 0, job.Dim);

            // Every emitter has to sit against something. A ghostlight left
            // in open water with nothing around it reads as a bead floating
            // in the dark, which is what the first rift looked like along
            // its rims. A light standing IN a solid block is fine (that is
            // how the colossus wears its seams); a light in air or water
            // must touch one. Anything else is walked out to the nearest
            // cell that does, and dropped if there is nothing within reach,
            // because no light at all beats one hanging in the water.
            // This runs after the commit, so it sees the finished world.
            var ring = new List<(int X, int Y, int Z)>();
            for (int ox = -4; ox <= 4; ox++)
                for (int oy = -4; oy <= 4; oy++)
                    for (int oz = -4; oz <= 4; oz++)
                        if (ox != 0 || oy != 0 || oz != 0) ring.Add((ox, oy, oz));
            ring.Sort((a, b) => (a.X * a.X + a.Y * a.Y + a.Z * a.Z).CompareTo(b.X * b.X + b.Y * b.Y + b.Z * b.Z));

            bool SolidCell(int x, int y, int z)
            {
                gpos.Set(x, y, z);
                var b = gba.GetBlock(gpos, BlockLayersAccess.SolidBlocks);
                return b != null && b.Id != 0;
            }
            bool Touches(int x, int y, int z)
            {
                foreach (var nb in BlockFacing.ALLFACES)
                    if (SolidCell(x + nb.Normali.X, y + nb.Normali.Y, z + nb.Normali.Z)) return true;
                return false;
            }

            // Emitters are judged as CLUSTERS, not one at a time. The
            // lighthouse stacks three lights on a pedestal, so the upper two
            // touch nothing but each other: what matters is whether the
            // group they belong to reaches something solid anywhere.
            long GKey(int x, int y, int z) => (((long)(x + 1048576) & 0x1FFFFF) << 42) | (((long)(z + 1048576) & 0x1FFFFF) << 21) | (uint)(y & 0x1FFFFF);
            var spotAt = new Dictionary<long, int>();
            for (int i = 0; i < glowSpots.Count; i++) spotAt[GKey(glowSpots[i].X, glowSpots[i].Y, glowSpots[i].Z)] = i;
            var group = new int[glowSpots.Count];
            for (int i = 0; i < group.Length; i++) group[i] = -1;
            var groupHeld = new List<bool>();
            var stack = new Stack<int>();
            for (int i = 0; i < glowSpots.Count; i++)
            {
                if (group[i] >= 0) continue;
                int gid = groupHeld.Count;
                bool held = false;
                groupHeld.Add(false);
                group[i] = gid;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    var s = glowSpots[stack.Pop()];
                    if (SolidCell(s.X, s.Y, s.Z) || Touches(s.X, s.Y, s.Z)) held = true;
                    foreach (var nb in BlockFacing.ALLFACES)
                    {
                        if (!spotAt.TryGetValue(GKey(s.X + nb.Normali.X, s.Y + nb.Normali.Y, s.Z + nb.Normali.Z), out int j)) continue;
                        if (group[j] >= 0) continue;
                        group[j] = gid;
                        stack.Push(j);
                    }
                }
                groupHeld[gid] = held;
            }

            var pinned = new List<(int X, int Y, int Z, int Id)>();
            int floated = 0, orphaned = 0;
            for (int i = 0; i < glowSpots.Count; i++)
            {
                var g = glowSpots[i];
                if (groupHeld[group[i]]) { pinned.Add(g); continue; }
                bool moved = false;
                foreach (var o in ring)
                {
                    int nx = g.X + o.X, ny = g.Y + o.Y, nz = g.Z + o.Z;
                    if (ny < 5 || SolidCell(nx, ny, nz) || !Touches(nx, ny, nz)) continue;
                    pinned.Add((nx, ny, nz, g.Id));
                    moved = true;
                    break;
                }
                if (moved) floated++; else orphaned++;
            }
            if (floated > 0 || orphaned > 0)
                sapi.Logger.Notification("[landmassgen] {0}: {1} emitter(s) pinned to the nearest surface, {2} dropped with nothing in reach",
                    def.Kind, floated, orphaned);
            glowSpots = pinned;
            foreach (var g in glowSpots)
            {
                gpos.Set(g.X, g.Y, g.Z);
                gba.SetBlock(g.Id, gpos);
                placed++;
            }
            // Ground truth that the light actually baked. Probe an emitter
            // that has somewhere to send its light: one walled into solid
            // rock stores no block light and reads 0, which looks exactly
            // like a bake failure and is not one.
            var p0 = glowSpots.Count > 0 ? glowSpots[0] : (X: 0, Y: 0, Z: 0, Id: 0);
            int buriedSkipped = 0;
            for (int gi = 0; gi < glowSpots.Count && gi < 80; gi++)
            {
                var cand = glowSpots[gi];
                bool open = false;
                foreach (var nb in BlockFacing.ALLFACES)
                {
                    gpos.Set(cand.X + nb.Normali.X, cand.Y + nb.Normali.Y, cand.Z + nb.Normali.Z);
                    var nblock = gba.GetBlock(gpos);
                    if (nblock == null || nblock.Id == 0 || !nblock.SideOpaque[0]) { open = true; break; }
                }
                if (open) { p0 = cand; break; }
                buriedSkipped++;
            }
            gpos.Set(p0.X, p0.Y, p0.Z);
            var pBlock = gba.GetBlock(gpos);
            sapi.Logger.Notification(
                "[landmassgen] glow probe: block light {0} at emitter {1}/{2}/{3} ({4} emitters, {7} walled in, block {5}, LightHsv {6})",
                gba.GetLightLevel(gpos, EnumLightLevelType.OnlyBlockLight),
                p0.X, p0.Y, p0.Z, glowSpots.Count, pBlock.Code,
                pBlock.LightHsv[0] + "," + pBlock.LightHsv[1] + "," + pBlock.LightHsv[2], buriedSkipped);
            // and once more after the relight queue has drained, since the
            // immediate read can race the lighting thread
            // A big structure commits millions of blocks and the engine
            // relights asynchronously, so one read 1.5s later can still be
            // 0 while the queue drains. Probe again as it settles.
            var probePos = new BlockPos(p0.X, p0.Y, p0.Z, job.Dim);
            foreach (int wait in new[] { 1500, 6000, 20000, 45000 })
            {
                int w = wait;
                sapi.Event.RegisterCallback(dt2 => sapi.Logger.Notification(
                    "[landmassgen] glow probe (+{4}ms): block light {0} at {1}/{2}/{3}",
                    sapi.World.BlockAccessor.GetLightLevel(probePos, EnumLightLevelType.OnlyBlockLight),
                    probePos.X, probePos.Y, probePos.Z, w), w);
            }
        }

        if (lavaProbe != null)
        {
            var lp = new BlockPos(lavaProbe.Value.X, lavaProbe.Value.Y + 1, lavaProbe.Value.Z, job.Dim);
            sapi.Event.RegisterCallback(dt2 => sapi.Logger.Notification(
                "[landmassgen] lava probe (late): block light {0} above the melt at {1}/{2}/{3}",
                sapi.World.BlockAccessor.GetLightLevel(lp, EnumLightLevelType.OnlyBlockLight),
                lp.X, lp.Y, lp.Z), 1500);
        }

        if (clutterSpots.Count > 0)
        {
            int clutterId = Id("clutter-devastation");
            if (clutterId != 0)
            {
                var wba = sapi.World.BlockAccessor;
                var cpos = new BlockPos(0, 0, 0, job.Dim);
                FieldInfo rotField = typeof(BEBehaviorShapeFromAttributes)
                    .GetField("<rotateY>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (var c in clutterSpots)
                {
                    cpos.Set(c.X, c.Y, c.Z);
                    if (wba.GetBlock(cpos, BlockLayersAccess.SolidBlocks).Id != 0) continue;
                    wba.SetBlock(clutterId, cpos);
                    var beh = wba.GetBlockEntity(cpos)?.GetBehavior<BEBehaviorShapeFromAttributes>();
                    if (beh != null)
                    {
                        beh.Type = c.Type;
                        rotField?.SetValue(beh, c.Rot);
                        beh.Blockentity.MarkDirty(true);
                    }
                    placed++;
                }
            }
        }
        return placed;
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
        otherBlockChance = 1f, // vanilla default: a landmark pine may leak resin too
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
