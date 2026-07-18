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

        api.Logger.Notification($"[landmassgenerator] Ready. Shape files go in: {shapeFolder}");
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
        public float[,] ShoreField;   // per-cell shore width, smoothed across region borders
        public Dictionary<char, Region> Regions = new();
        public List<TreeMarker> Markers = new();
        public List<CaveMarker> Caves = new();
        public bool NaturalDeposits;  // `deposits natural`: run the game's own ore pass
    }

    private class IslandJob
    {
        public int Cx, Cz, Dim, SeaLevel;
        public int DomeHeight, Water, MaxDepth;
        public double OceanRing;
        public long Seed;

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
        string[] toks = all.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

        if (toks.Length == 1 && toks[0].Equals("shapes", StringComparison.OrdinalIgnoreCase))
            return ListShapes();

        if (_islandBusy)
            return TextCommandResult.Error("An island is still generating. Wait for it to finish before starting another.");
        if (args.Caller?.Entity == null)
            return TextCommandResult.Error("Run /genisland in game so it centres on where you stand.");

        var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < toks.Length; i++)
        {
            int eq = toks[i].IndexOf('=');
            if (eq > 0) opt[toks[i].Substring(0, eq)] = toks[i].Substring(eq + 1);
            else if (i == 0 && int.TryParse(toks[i], out _)) opt["diameter"] = toks[i];
        }

        GetOrigin(args.Caller, out int ox, out int _, out int oz, out int dim);

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
        if (stone == null) return TextCommandResult.Error("stone: " + se);
        if (soil == null) return TextCommandResult.Error("soil: " + oe);
        if (grass == null) return TextCommandResult.Error("grass: " + ge);
        if (sand == null) return TextCommandResult.Error("sand: " + ae);
        if (waterBlock == null) return TextCommandResult.Error("water: " + we);
        if (oceanBlock == null) return TextCommandResult.Error("oceanwater: " + oce);

        var problems = new List<string>();

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
            Rand = new LCGRandom(seed),
            Player = args.Caller?.Player as IServerPlayer
        };

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
                return TextCommandResult.Error("climate: " + cerr);
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
            ShapeDef shape = LoadShape(shapeName, job, seed, problems, out string err);
            if (shape == null) return TextCommandResult.Error(err);

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

        int cs = GlobalConstants.ChunkSize;
        int cx1 = FloorDiv(job.MinX, cs), cx2 = FloorDiv(job.MinX + job.W - 1, cs);
        int cz1 = FloorDiv(job.MinZ, cs), cz2 = FloorDiv(job.MinZ + job.H - 1, cs);

        _islandBusy = true;
        sapi.WorldManager.LoadChunkColumnPriority(cx1, cz1, cx2, cz2,
            new ChunkLoadOptions { KeepLoaded = false, OnLoaded = () => StartIslandJob(job) });

        string msg = shapeName != null
            ? $"Building '{shapeName}' at {diameter} blocks across (sea level {seaLevel}, seed {seed}), {job.Shape.Regions.Count} region(s)."
            : $"Generating a {diameter}-block island (sea level {seaLevel}, seed {seed}).";
        msg += " It builds over a few seconds without freezing the server.";
        if (problems.Count > 0) msg += " Notes: " + string.Join("; ", problems) + ".";
        return TextCommandResult.Success(msg);
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
                shape.Cells[x, z] = c;
            }

        // A marker cell still needs terrain, so adopt a neighbouring region.
        for (int z = 0; z < shape.H; z++)
            for (int x = 0; x < shape.W; x++)
                if (shape.Cells[x, z] == '?')
                    shape.Cells[x, z] = NeighbourRegion(shape, x, z);

        foreach (char c in shape.Cells)
            if (c != '.' && !shape.Regions.ContainsKey(c))
            {
                err = $"Shape '{file}' uses '{c}' in the map with no matching region line.";
                return null;
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

        // Per-cell height and shore, then smoothed so inland region borders ramp
        // into each other instead of forming vertical seams. Materials stay
        // crisp; only the elevation blends.
        var rawH = new float[shape.W, shape.H];
        var rawS = new float[shape.W, shape.H];
        for (int z = 0; z < shape.H; z++)
            for (int x = 0; x < shape.W; x++)
            {
                char c = shape.Cells[x, z];
                if (c != '.' && shape.Regions.TryGetValue(c, out Region rg))
                {
                    rawH[x, z] = (float)rg.Height;
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

    private static char NeighbourRegion(ShapeDef shape, int x, int z)
    {
        for (int r = 1; r <= 4; r++)
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= shape.W || nz >= shape.H) continue;
                    char c = shape.Cells[nx, nz];
                    if (c != '.' && c != '?') return c;
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
    //             weave=0.5 branches=2 branchdepth=2 depth=60 mouth=2
    //             ores=copper:0.05 seed=<n>
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
            else if (job.WaitTicks++ < 750)
            {
                if (job.WaitTicks == 1)
                    ReportIsland(job, $"Loading {missing} chunk column(s) under the island before building...");
                return;
            }
            else
            {
                job.ChunksLoaded = true;
                ReportIsland(job, $"WARNING: {missing} chunk column(s) still not loaded after 30s; parts of the island may be missing. Stand closer to the target area and regenerate.");
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

        int stoneId = reg?.StoneId ?? job.StoneId;
        int stoneId2 = reg?.StoneId2 ?? 0;
        int sandId = reg?.SandId ?? job.SandId;
        int soilId = reg?.SoilId ?? job.SoilId;
        int grassId = reg?.GrassId ?? job.GrassId;
        List<OreSpec> ores = reg?.Ores ?? job.Ores;

        // Root the column on whichever is lower: the existing sea floor or our
        // own surface. That fills the gap down to the seabed so nothing floats,
        // and when we are carving DOWN it collapses to just the surface block.
        int fillFrom = Math.Min(naturalY, topY);
        fillFrom = Math.Max(fillFrom, Math.Max(1, job.SeaLevel - job.MaxDepth));

        const int skin = 3;
        for (int y = fillFrom; y <= topY; y++)
        {
            pos.Set(x, y, z);
            int id;
            if (y == topY)
                id = topMat == SurfGrass ? grassId
                    // surface=barren land gets patchy sparse grass; pond beds
                    // (the other SurfSoil source) stay bare mud.
                    : topMat == SurfSoil ? (reg != null && reg.Pond == 0 && reg.SparseGrassId != 0
                        ? (job.SurfNoise.Noise(x * 0.31, z * 0.31) > 0.5 ? reg.SparseGrassId2 : reg.SparseGrassId)
                        : soilId)
                    : topMat == SurfPeat ? (job.SurfNoise.Noise(x * 0.31, z * 0.31) > 0.45 ? reg.PeatSparseId : reg.PeatId)
                    : topMat == SurfSand ? sandId : stoneId;
            else if (y > topY - skin)
                id = topMat == SurfGrass || topMat == SurfSoil ? soilId
                    : topMat == SurfPeat ? reg.PeatId
                    : topMat == SurfSand ? sandId : stoneId;
            else
                id = PickStone(job, ores, stoneId, stoneId2, x, y, z);
            ba.SetBlock(id, pos);
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

                // flood=: the region's ground is capped below sea level, and
                // the sea flows over it: marsh flats, mangrove shallows,
                // lurking reefs. Shallow enough to wade, too shallow to raft.
                if (r.Flood > 0 && topY > job.SeaLevel - 1 - r.Flood)
                    topY = job.SeaLevel - 1 - r.Flood;
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

        // Waterlilies float on the pond's surface, so they go on the water
        // columns themselves, before the dry-land gate below.
        if (reg != null && reg.Pond > 0)
        {
            if (reg.Lilies > 0 && reg.LilyId != 0 && waterTopY > topY)
            {
                job.Rand.InitPositionSeed(x, z);
                if (job.Rand.NextDouble() < reg.Lilies)
                {
                    pos.Set(x, waterTopY + 1, z);
                    ba.SetBlock(reg.LilyId, pos);
                    job.Plants++;
                }
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
            if (reg.Cattails > 0 && reg.WaterCattailId != 0 && waterTopY - topY == 1
                && job.SurfNoise.Noise(x * 0.045, z * 0.045) > 0.58
                && job.Rand.NextDouble() < Math.Min(1.0, reg.Cattails * 2.2))
            {
                pos.Set(x, topY + 1, z);
                ba.SetBlock(reg.WaterCattailId, pos);
                job.Plants++;
            }
            return;
        }

        if (underwater || (topMat != SurfGrass && topMat != SurfSand && topMat != SurfRock && topMat != SurfSoil && topMat != SurfPeat)) return;

        job.Rand.InitPositionSeed(x, z);
        if ((topMat == SurfGrass || topMat == SurfPeat) && TryPlantTree(job, ba, pos, x, z, topY, reg)) job.Trees++;
        else if (TryPlantFlora(job, ba, pos, x, z, topY, topMat, reg)) job.Plants++;
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
            MarkerWorld(job, m, out int mx, out int mz);
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
                (int)(length * lenFrac), radius * 0.85, floorY,
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
    // We drive a PRIVATE GenDeposits instance, exactly the way the prospecting
    // pick's ProPickWorkSpace does (setApi + initAssets(blockCallbacks: false)
    // + initWorldGen). blockCallbacks: false routes every write through plain
    // chunk-data sets, so nothing touches the real worldgen thread's block
    // accessor. One reflection read (GenPartial.chunkRand) lets us position-seed
    // the walk per neighbour chunk like GenChunkColumn does.

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
            gd.initAssets(sapi, blockCallbacks: false);
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
                }
                if (!loaded) { missing++; continue; }

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
        if (missing > 0) note += $" ({missing} column(s) skipped: not loaded)";
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
            // Reeds grow in CLUMPS, not as an even hem along the whole shore:
            // a low-frequency noise gates reed beds (20-40 block patches),
            // dense inside, bare between. Boosted inside the clump so the
            // total stays close to the asked-for chance.
            double dCoast = Bilinear(job.Shape.DistToOcean, job.Shape.W, job.Shape.H, gx, gz) * job.WorldPerCell;
            if (dCoast <= 3.0
                && job.SurfNoise.Noise(x * 0.045, z * 0.045) > 0.58
                && job.Rand.NextDouble() < Math.Min(1.0, reg.Cattails * 2.2))
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

    private static void MarkerWorld(IslandJob job, TreeMarker m, out int x, out int z)
    {
        // Forward rotation: map space to world, the inverse of GridPos.
        double lx = (m.Gx + 0.5 - job.Shape.W / 2.0) * job.WorldPerCell;
        double lz = (m.Gz + 0.5 - job.Shape.H / 2.0) * job.WorldPerCell;
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
                MarkerWorld(job, m, out int x, out int z);
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
