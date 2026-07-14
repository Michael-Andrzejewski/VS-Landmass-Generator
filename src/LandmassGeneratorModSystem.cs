using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Common;
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
/// The shape is analytic: a radial dome whose coastline is perturbed by simplex
/// noise, with the height falloff biased by compass direction so one side eases
/// into a sandy beach while the opposite side drops as a stone cliff. The island
/// is rooted on the REAL sea floor (probed per column) and its flank slopes down
/// to meet it, then blends back into the natural seabed at the work boundary, so
/// nothing floats. Ore is seeded with 3D noise veins (worldgen's ore pass never
/// runs on blocks we place, so every ore here is deliberate), and a forest pass
/// scatters trees over the grass afterwards.
/// </summary>
public class LandmassGeneratorModSystem : ModSystem
{
    private ICoreServerAPI sapi;

    // Active generation job. Generation is spread across ticks, so only one
    // island builds at a time; a second /genisland is rejected until it ends.
    private IslandJob _islandJob;
    private long _islandListenerId;
    private bool _islandBusy;

    // Graded ore types the game ships (worldproperties/block/ore-graded).
    private static readonly string[] OreTypes =
    {
        "nativecopper", "limonite", "galena", "cassiterite", "chromite", "ilmenite",
        "sphalerite", "bismuthinite", "magnetite", "hematite", "malachite",
        "pentlandite", "uranium", "wolframite", "rhodochrosite"
    };

    // Friendly names so you can write copper/iron/tin instead of the mineral.
    private static readonly Dictionary<string, string> OreAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "copper", "nativecopper" }, { "iron", "limonite" }, { "tin", "cassiterite" },
        { "zinc", "sphalerite" }, { "lead", "galena" }, { "nickel", "pentlandite" },
        { "chromium", "chromite" }, { "chrome", "chromite" }, { "titanium", "ilmenite" },
        { "tungsten", "wolframite" }, { "bismuth", "bismuthinite" }, { "manganese", "rhodochrosite" }
    };

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        var p = api.ChatCommands.Parsers;

        RegisterCmd(api, "genisland", name =>
            api.ChatCommands.Create(name)
                .WithDescription("Generate a procedural ocean island around you, spread across ticks so it does not freeze the server. Options are key=value, e.g. /genisland diameter=200 height=45 beachdir=s cliffdir=n ores=copper:rich,iron:medium forest=0.02 trees=oak,pine. Keys: diameter, height, water, sealevel, maxdepth, beachdir, cliffdir, seed, ores, forest, trees, stone, soil, grass, sand.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalAll("options"))
                .HandleWith(OnGenIsland));

        api.Logger.Notification("[landmassgenerator] Ready. Use /genisland to build an island where you stand.");
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
    //  Job state
    // ─────────────────────────────────────────────────────────────────────

    // One ore the island should contain. Worldgen never seeds our blocks, so
    // each ore is placed by its own 3D noise field: above the threshold is a
    // vein, and the deeper into the vein, the richer the grade.
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

    private class IslandJob
    {
        public int Cx, Cz, Dim, SeaLevel;      // centre column and the water line
        public double R, Rmax, OceanRing;      // island radius, work radius, offshore falloff
        public int DomeHeight, Water, MaxDepth;
        public double Bvx, Bvz, Cvx, Cvz;       // beach and cliff unit direction vectors
        public double BumpAmp;
        public NormalizedSimplexNoise CoastNoise, SurfNoise;
        public int StoneId, SoilId, GrassId, SandId, WaterId;
        public List<OreSpec> Ores = new();
        public List<ITreeGenerator> ForestTrees = new();
        public double ForestDensity;
        public ITreeGenerator SummitTree;
        public int MinX, MinZ, W, H;
        public long I, Total, Placed, Trees;
        public int ColumnsPerTick;
        public long Seed;
        public int Phase;                       // 0 = terrain, 1 = forest
        public LCGRandom Rand;
        public IServerPlayer Player;
        public bool HasNext => I < Total;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Command
    // ─────────────────────────────────────────────────────────────────────

    private TextCommandResult OnGenIsland(TextCommandCallingArgs args)
    {
        if (_islandBusy)
            return TextCommandResult.Error("An island is still generating. Wait for it to finish before starting another.");

        if (args.Caller?.Entity == null)
            return TextCommandResult.Error("Run /genisland in game so it centres on where you stand.");

        // Parse key=value options; a lone leading number is taken as diameter.
        string all = args.Parsers[0].GetValue() as string ?? "";
        string[] toks = all.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < toks.Length; i++)
        {
            string tok = toks[i];
            int eq = tok.IndexOf('=');
            if (eq > 0) opt[tok.Substring(0, eq)] = tok.Substring(eq + 1);
            else if (i == 0 && int.TryParse(tok, out _)) opt["diameter"] = tok;
        }

        GetOrigin(args.Caller, out int ox, out int _, out int oz, out int dim);

        int diameter = OptInt(opt, "diameter", 120, 8, 1024);
        int height = OptInt(opt, "height", 40, 3, 220);
        int water = OptInt(opt, "water", 30, 0, 200);
        int maxdepth = OptInt(opt, "maxdepth", 80, 8, 250);
        int seaLevel = OptInt(opt, "sealevel", sapi.World.SeaLevel, 2, sapi.WorldManager.MapSizeY - 2);
        string beachDir = OptDir(opt, "beachdir", "s");
        string cliffDir = OptDir(opt, "cliffdir", "n");

        long seed;
        if (!opt.TryGetValue("seed", out string seedStr) || !long.TryParse(seedStr, out seed))
            seed = sapi.World.Rand.Next(1, int.MaxValue);

        // Block palette, all overridable.
        string stoneCode = OptStr(opt, "stone", "rock-granite");
        Block stone = ResolveBlock(stoneCode, out string se);
        Block soil = ResolveBlock(OptStr(opt, "soil", "soil-medium-none"), out string oe);
        Block grass = ResolveBlock(OptStr(opt, "grass", "soil-medium-normal"), out string ge);
        Block sand = ResolveBlock(OptStr(opt, "sand", "sand-granite"), out string ae);
        Block waterBlock = ResolveBlock(OptStr(opt, "water_block", "water-still-7"), out string we);
        if (stone == null) return TextCommandResult.Error("stone: " + se);
        if (soil == null) return TextCommandResult.Error("soil: " + oe);
        if (grass == null) return TextCommandResult.Error("grass: " + ge);
        if (sand == null) return TextCommandResult.Error("sand: " + ae);
        if (waterBlock == null) return TextCommandResult.Error("water: " + we);

        // Ore must match the host rock, e.g. ore-medium-nativecopper-granite.
        string rockType = stoneCode.StartsWith("rock-", StringComparison.OrdinalIgnoreCase)
            ? stoneCode.Substring(5) : null;

        var ores = new List<OreSpec>();
        var oreProblems = new List<string>();
        if (opt.TryGetValue("ores", out string oreStr) && !string.IsNullOrWhiteSpace(oreStr))
        {
            if (rockType == null)
                oreProblems.Add("ores need a rock-* stone block to sit in");
            else
                ParseOres(oreStr, rockType, seed, ores, oreProblems);
        }

        // Forest.
        double forest = OptDouble(opt, "forest", 0.0, 0.0, 0.35);
        var forestTrees = new List<ITreeGenerator>();
        var treeProblems = new List<string>();
        if (forest > 0)
        {
            string treeStr = OptStr(opt, "trees", "oak");
            foreach (string want in treeStr.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                ITreeGenerator g = FindTreeGenerator(want.Trim());
                if (g != null) forestTrees.Add(g);
                else treeProblems.Add(want.Trim());
            }
            if (forestTrees.Count == 0)
            {
                forest = 0;
                treeProblems.Add("no usable tree generators, forest skipped");
            }
        }

        double R = diameter / 2.0;
        double oceanRing = Math.Max(24.0, R * 0.6);
        double rmax = R * 1.12 + oceanRing;
        int reach = (int)Math.Ceiling(rmax) + 2;

        DirVec(beachDir, out double bvx, out double bvz);
        DirVec(cliffDir, out double cvx, out double cvz);

        var rand = new LCGRandom(seed);

        var job = new IslandJob
        {
            Cx = ox, Cz = oz, Dim = dim, SeaLevel = seaLevel,
            R = R, Rmax = rmax, OceanRing = oceanRing,
            DomeHeight = height, Water = water, MaxDepth = maxdepth,
            Bvx = bvx, Bvz = bvz, Cvx = cvx, Cvz = cvz,
            BumpAmp = Math.Min(4.0, height * 0.15),
            CoastNoise = NormalizedSimplexNoise.FromDefaultOctaves(4, 1 / 40.0, 0.5, seed),
            SurfNoise = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 22.0, 0.5, seed + 1),
            StoneId = stone.BlockId, SoilId = soil.BlockId, GrassId = grass.BlockId,
            SandId = sand.BlockId, WaterId = waterBlock.BlockId,
            Ores = ores,
            ForestTrees = forestTrees,
            ForestDensity = forest,
            SummitTree = FindTreeGenerator("oak"),
            MinX = ox - reach, MinZ = oz - reach,
            W = reach * 2 + 1, H = reach * 2 + 1,
            ColumnsPerTick = 400,
            Seed = seed,
            Phase = 0,
            Rand = rand,
            Player = args.Caller?.Player as IServerPlayer
        };
        job.Total = (long)job.W * job.H;

        // Force-load every chunk column the island touches, then start the job
        // in the load callback so writes never hit an unloaded chunk.
        int cs = GlobalConstants.ChunkSize;
        int cx1 = FloorDiv(job.MinX, cs), cx2 = FloorDiv(job.MinX + job.W - 1, cs);
        int cz1 = FloorDiv(job.MinZ, cs), cz2 = FloorDiv(job.MinZ + job.H - 1, cs);

        _islandBusy = true;
        sapi.WorldManager.LoadChunkColumnPriority(cx1, cz1, cx2, cz2,
            new ChunkLoadOptions { KeepLoaded = false, OnLoaded = () => StartIslandJob(job) });

        string msg = $"Generating a {diameter}-block island (beach {beachDir}, cliffs {cliffDir}, sea level {seaLevel}, seed {seed})";
        if (ores.Count > 0) msg += $", ores: {string.Join(", ", ores.ConvertAll(o => o.Name))}";
        if (forest > 0) msg += $", forest {forest:0.###}";
        msg += ". It builds over a few seconds without freezing the server.";
        if (oreProblems.Count > 0) msg += " Skipped ore: " + string.Join("; ", oreProblems) + ".";
        if (treeProblems.Count > 0) msg += " Tree issues: " + string.Join("; ", treeProblems) + ".";
        return TextCommandResult.Success(msg);
    }

    // ores=copper:rich,iron:medium,tin:sparse  (richness may also be a 0..1 number)
    private void ParseOres(string spec, string rockType, long seed, List<OreSpec> ores, List<string> problems)
    {
        int idx = 0;
        foreach (string entry in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split(':');
            string name = parts[0].Trim();
            string rich = parts.Length > 1 ? parts[1].Trim() : "medium";

            if (OreAliases.TryGetValue(name, out string mapped)) name = mapped;
            if (Array.IndexOf(OreTypes, name.ToLowerInvariant()) < 0)
            {
                problems.Add($"unknown ore '{parts[0].Trim()}' (try: copper, iron, tin, zinc, lead, or a mineral name like {string.Join(", ", OreTypes[0], OreTypes[1], OreTypes[3])})");
                continue;
            }

            var o = new OreSpec
            {
                Name = name,
                Threshold = RichnessToThreshold(rich),
                Noise = NormalizedSimplexNoise.FromDefaultOctaves(2, 1 / 11.0, 0.5, seed + 100 + idx),
                PoorId = OreId("poor", name, rockType),
                MediumId = OreId("medium", name, rockType),
                RichId = OreId("rich", name, rockType),
                BountifulId = OreId("bountiful", name, rockType)
            };
            idx++;

            if (o.PoorId == 0 && o.MediumId == 0 && o.RichId == 0 && o.BountifulId == 0)
                problems.Add($"{name} does not occur in {rockType}");
            else
                ores.Add(o);
        }
    }

    private int OreId(string grade, string type, string rock)
    {
        Block b = sapi.World.GetBlock(new AssetLocation("game", $"ore-{grade}-{type}-{rock}"));
        return b?.BlockId ?? 0;
    }

    private static double RichnessToThreshold(string rich)
    {
        if (double.TryParse(rich, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return Math.Clamp(0.86 - 0.30 * Math.Clamp(v, 0, 1), 0.55, 0.90);
        switch (rich.ToLowerInvariant())
        {
            case "sparse": return 0.82;
            case "rich": return 0.68;
            case "abundant": return 0.62;
            default: return 0.75; // medium
        }
    }

    private void StartIslandJob(IslandJob job)
    {
        _islandJob = job;
        _islandListenerId = sapi.Event.RegisterGameTickListener(OnIslandTick, 40);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Tick loop: phase 0 lays terrain, phase 1 scatters the forest
    // ─────────────────────────────────────────────────────────────────────
    private void OnIslandTick(float dt)
    {
        IslandJob job = _islandJob;
        if (job == null) return;

        var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        var pos = new BlockPos(0, 0, 0, job.Dim);

        int budget = job.ColumnsPerTick;
        while (budget-- > 0 && job.HasNext)
        {
            int x = job.MinX + (int)(job.I % job.W);
            int z = job.MinZ + (int)(job.I / job.W);
            job.I++;

            if (job.Phase == 0)
            {
                if (FillColumn(job, ba, pos, x, z)) job.Placed++;
            }
            else
            {
                if (TryPlantTree(job, ba, pos, x, z)) job.Trees++;
            }
        }
        ba.Commit();

        if (job.HasNext) return;

        // Terrain done: run a forest pass if one was asked for.
        if (job.Phase == 0 && job.ForestDensity > 0 && job.ForestTrees.Count > 0)
        {
            job.Phase = 1;
            job.I = 0;
            return;
        }

        sapi.Event.UnregisterGameTickListener(_islandListenerId);
        _islandListenerId = 0;
        _islandJob = null;
        string treeMsg = PlaceSummitTree(job);
        _islandBusy = false;

        string done = $"Island complete: {job.Placed} column(s)";
        if (job.Trees > 0) done += $", {job.Trees} tree(s)";
        ReportIsland(job, done + ". " + treeMsg);
    }

    // Lay one column: root it on the real sea floor, stack stone (veined with
    // ore) then soil then a top block, and turn everything above into water up
    // to the water line or air above it.
    private bool FillColumn(IslandJob job, IBulkBlockAccessor ba, BlockPos pos, int x, int z)
    {
        // The natural terrain height BEFORE we touch this column. Each column is
        // visited once, so this is always the original sea floor / ground.
        pos.Set(x, job.SeaLevel, z);
        int naturalY = sapi.World.BlockAccessor.GetTerrainMapheightAt(pos);

        if (!ColumnSurface(job, x, z, naturalY, out int topY, out bool underwater, out int topMat, out bool nearIsland))
            return false;

        // Root the column on whichever is lower: the existing sea floor or our
        // own surface. That fills the gap down to the seabed so nothing floats,
        // and when we are carving DOWN it collapses to just the surface block.
        int fillFrom = Math.Min(naturalY, topY);
        fillFrom = Math.Max(fillFrom, Math.Max(1, job.SeaLevel - job.MaxDepth));

        const int soilDepth = 3;
        for (int y = fillFrom; y <= topY; y++)
        {
            pos.Set(x, y, z);
            int id;
            if (y == topY)
                id = topMat == 0 ? job.GrassId : topMat == 1 ? job.SandId : job.StoneId;
            else if (y > topY - soilDepth)
                id = topMat == 1 ? job.SandId : topMat == 2 ? job.StoneId : job.SoilId;
            else
                id = PickStone(job, x, y, z);
            ba.SetBlock(id, pos);
            // Our ground replaces open ocean below the water line, so clear the
            // fluid layer too, else the stone reads as submerged.
            if (y < job.SeaLevel) ba.SetBlock(0, pos, BlockLayersAccess.Fluid);
        }

        // Everything above the new surface: water up to the water line (this is
        // what carves the deepening sea outside the coast), air above it, and
        // any pre-existing terrain stripped away.
        int clearTop = Math.Max(naturalY, nearIsland ? job.SeaLevel + job.DomeHeight + 6 : job.SeaLevel);
        for (int y = topY + 1; y <= clearTop; y++)
        {
            pos.Set(x, y, z);
            ba.SetBlock(0, pos);
            ba.SetBlock(y < job.SeaLevel ? job.WaterId : 0, pos, BlockLayersAccess.Fluid);
        }
        return true;
    }

    // Stone, unless an ore vein claims this block.
    private int PickStone(IslandJob job, int x, int y, int z)
    {
        for (int i = 0; i < job.Ores.Count; i++)
            if (job.Ores[i].TryPick(x, y, z, out int oreId)) return oreId;
        return job.StoneId;
    }

    // The heart of the shape. topY is the top solid block for this column.
    // topMat: 0 grass, 1 sand, 2 stone. False means beyond the work radius.
    private bool ColumnSurface(IslandJob job, int x, int z, int naturalY,
        out int topY, out bool underwater, out int topMat, out bool nearIsland)
    {
        topY = 0; underwater = false; topMat = 0; nearIsland = false;

        double dx = x - job.Cx, dz = z - job.Cz;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist > job.Rmax) return false;

        double dirx = dist > 0.001 ? dx / dist : 0;
        double dirz = dist > 0.001 ? dz / dist : 0;
        double beachAlign = dirx * job.Bvx + dirz * job.Bvz;
        double cliffAlign = dirx * job.Cvx + dirz * job.Cvz;

        // Noise-perturbed coastline: the effective radius wobbles by about +-10%.
        double rn = job.CoastNoise.Noise(x, z);
        double edgeR = job.R * (0.9 + 0.2 * rn);
        double t = edgeR > 0.001 ? dist / edgeR : 999;
        nearIsland = t < 1.1;

        // Falloff exponent: low toward the beach (gentle), high toward the cliff
        // (a plateau that drops sharply at the coast).
        double p = 2.2;
        if (beachAlign > 0) p = Lerp(2.2, 1.3, beachAlign);
        if (cliffAlign > 0) p = Lerp(p, 3.8, cliffAlign);

        if (t < 1.0)
        {
            // Above water: the dome. Purely analytic, so the forest pass can
            // recompute the same surface later without re-reading the world.
            double rise = job.DomeHeight * (1.0 - Math.Pow(t, p));
            if (rise < 0) rise = 0;
            double bump = (job.SurfNoise.Noise(x, z) - 0.5) * 2 * job.BumpAmp * (1 - t);
            topY = (int)Math.Round(job.SeaLevel + rise + bump);
        }
        else
        {
            // Offshore: the flank keeps sloping down, then merges back into the
            // natural sea floor by the time we reach the work boundary, so the
            // edit has no visible rim.
            double over = dist - edgeR;
            double deep = job.SeaLevel - 2 - job.Water * Smooth(over / job.OceanRing);
            double toEdge = Smooth((dist - edgeR) / Math.Max(1.0, job.Rmax - edgeR));
            topY = (int)Math.Round(Lerp(deep, naturalY, toEdge));
        }

        underwater = topY < job.SeaLevel;
        if (!underwater)
        {
            bool beachTop = beachAlign > 0.3 && (topY - job.SeaLevel) <= 3;
            bool cliffTop = cliffAlign > 0.3 && t > 0.72;
            topMat = cliffTop ? 2 : beachTop ? 1 : 0;
        }
        else
        {
            topMat = topY >= job.SeaLevel - 4 ? 1 : 2; // sand near the shore, stone in the deep
        }
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Forest
    // ─────────────────────────────────────────────────────────────────────

    // Phase 1: for each grassy land column, roll the forest density and grow a
    // tree. The land surface is analytic, so no world read is needed.
    private bool TryPlantTree(IslandJob job, IBulkBlockAccessor ba, BlockPos pos, int x, int z)
    {
        if (!ColumnSurface(job, x, z, job.SeaLevel, out int topY, out bool underwater, out int topMat, out _))
            return false;
        if (underwater || topMat != 0) return false; // grass only: no beach, no cliff face

        job.Rand.InitPositionSeed(x, z);
        if (job.Rand.NextDouble() >= job.ForestDensity) return false;

        // Keep the summit clear so the landmark oak has room.
        double dx = x - job.Cx, dz = z - job.Cz;
        if (dx * dx + dz * dz < 36) return false;

        ITreeGenerator gen = job.ForestTrees[job.Rand.NextInt(job.ForestTrees.Count)];
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
        pos.Set(x, topY + 1, z);
        gen.GrowTree(ba, pos, tp, job.Rand);
        return true;
    }

    // A single large oak at the summit as a landmark.
    private string PlaceSummitTree(IslandJob job)
    {
        if (job.SummitTree == null) return "No oak generator found for the summit.";
        if (!ColumnSurface(job, job.Cx, job.Cz, job.SeaLevel, out int topY, out bool underwater, out _, out _) || underwater)
            return "No dry summit for a tree.";

        var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        var tp = new TreeGenParams
        {
            skipForestFloor = true,
            size = 1.6f,
            vinesGrowthChance = 0,
            mossGrowthChance = 0,
            otherBlockChance = 0,
            hemisphere = EnumHemisphere.North,
            treesInChunkGenerated = 0
        };
        var rnd = new LCGRandom(job.Seed);
        rnd.InitPositionSeed(job.Cx, job.Cz);
        job.SummitTree.GrowTree(ba, new BlockPos(job.Cx, topY + 1, job.Cz, job.Dim), tp, rnd);
        ba.Commit();
        return "Planted a tall oak at the summit.";
    }

    // Match a friendly name (oak, pine, birch) against the loaded tree
    // generators, preferring an exact-ish hit.
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
    private static void GetOrigin(Caller caller, out int ox, out int oy, out int oz, out int dim)
    {
        var entity = caller.Entity;
        ox = (int)Math.Floor(entity.Pos.X);
        oy = (int)Math.Floor(entity.Pos.Y);
        oz = (int)Math.Floor(entity.Pos.Z);
        dim = entity.Pos.Dimension;
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

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Smooth(double t) { t = Math.Clamp(t, 0.0, 1.0); return t * t * (3 - 2 * t); }
    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    private static int OptInt(Dictionary<string, string> opt, string key, int def, int lo, int hi)
    {
        if (opt.TryGetValue(key, out string s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            return Math.Clamp(v, lo, hi);
        return def;
    }

    private static double OptDouble(Dictionary<string, string> opt, string key, double def, double lo, double hi)
    {
        if (opt.TryGetValue(key, out string s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return Math.Clamp(v, lo, hi);
        return def;
    }

    private static string OptStr(Dictionary<string, string> opt, string key, string def)
        => opt.TryGetValue(key, out string s) && !string.IsNullOrEmpty(s) ? s : def;

    private static string OptDir(Dictionary<string, string> opt, string key, string def)
    {
        string d = OptStr(opt, key, def).ToLowerInvariant();
        return d == "n" || d == "e" || d == "s" || d == "w" ? d : def;
    }

    // Compass to unit vector in world space: +X east, +Z south, north is -Z.
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
