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
///   /genisland [diameter=..] [height=..] [water=..] [basedepth=..]
///              [beachdir=n|e|s|w] [cliffdir=n|e|s|w] [seed=..]
///              [stone=..] [soil=..] [grass=..] [sand=..]
///
/// A whole island is far more blocks than a normal fill can touch in one go,
/// so this never runs as a single burst. It force-loads the island's chunks,
/// then places the terrain column by column across many game ticks, committing
/// one bulk batch per tick, so even a 500-block island does not stall the
/// server thread. The shape is analytic: a radial dome whose coastline is
/// perturbed by simplex noise, with the height falloff biased by compass
/// direction so one side eases into a sandy beach while the opposite side drops
/// as a stone cliff, and the seafloor deepening outside the coast. A native oak
/// is grown at the summit once the ground has landed.
/// </summary>
public class LandmassGeneratorModSystem : ModSystem
{
    private ICoreServerAPI sapi;

    // Active generation job. Generation is spread across ticks, so only one
    // island builds at a time; a second /genisland is rejected until it ends.
    private IslandJob _islandJob;
    private long _islandListenerId;
    private bool _islandBusy;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        var p = api.ChatCommands.Parsers;

        // Register under the bare name, falling back to a cb-prefixed alias if
        // some other mod already claims it, so a collision cannot abort us.
        RegisterCmd(api, "genisland", name =>
            api.ChatCommands.Create(name)
                .WithDescription("Generate a procedural ocean island around you, spread across ticks so it does not freeze the server. Options are key=value, e.g. /genisland diameter=200 height=45 beachdir=s cliffdir=n. Keys: diameter, height, water, basedepth, beachdir, cliffdir, seed, stone, soil, grass, sand.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalAll("options"))
                .HandleWith(OnGenIsland));

        api.Logger.Notification("[landmassgenerator] Ready. Use /genisland to build an island where you stand.");
    }

    // Register a command under its bare name, or under "lg" + name if the bare
    // name is already taken, so one collision cannot stop the mod loading.
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
    //  /genisland
    // ─────────────────────────────────────────────────────────────────────

    // Everything one generation job needs, precomputed once so the per-column
    // math on each tick stays cheap.
    private class IslandJob
    {
        public int Cx, Cz, Dim, BaseY;         // centre column and sea level
        public double R, Rmax, OceanRing;      // island radius, work radius, offshore falloff
        public int DomeHeight, Water, BaseDepth;
        public double Bvx, Bvz, Cvx, Cvz;       // beach and cliff unit direction vectors
        public double BumpAmp;                  // surface roughness amplitude
        public NormalizedSimplexNoise CoastNoise, SurfNoise;
        public int StoneId, SoilId, GrassId, SandId, WaterId;
        public int MinX, MinZ, W, H;            // bounding box of columns to visit
        public long I, Total, Placed;           // iteration cursor and running count
        public int ColumnsPerTick;
        public long Seed;
        public IServerPlayer Player;            // who to report back to (may be null)
        public bool HasNext => I < Total;
    }

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

        GetOrigin(args.Caller, out int ox, out int oy, out int oz, out int dim);

        int diameter = OptInt(opt, "diameter", 120, 8, 1024);
        int height = OptInt(opt, "height", 40, 3, 220);
        int water = OptInt(opt, "water", 30, 0, 200);
        int basedepth = OptInt(opt, "basedepth", 20, 1, 200);
        string beachDir = OptDir(opt, "beachdir", "s");
        string cliffDir = OptDir(opt, "cliffdir", "n");

        long seed;
        if (!opt.TryGetValue("seed", out string seedStr) || !long.TryParse(seedStr, out seed))
            seed = sapi.World.Rand.Next(1, int.MaxValue);

        // Resolve the block palette, letting the caller override any of it.
        Block stone = ResolveBlock(OptStr(opt, "stone", "rock-granite"), out string se);
        Block soil = ResolveBlock(OptStr(opt, "soil", "soil-medium-none"), out string oe);
        Block grass = ResolveBlock(OptStr(opt, "grass", "soil-medium-normal"), out string ge);
        Block sand = ResolveBlock(OptStr(opt, "sand", "sand-granite"), out string ae);
        Block waterBlock = ResolveBlock(OptStr(opt, "water_block", "water-still-7"), out string we);
        if (stone == null) return TextCommandResult.Error("stone: " + se);
        if (soil == null) return TextCommandResult.Error("soil: " + oe);
        if (grass == null) return TextCommandResult.Error("grass: " + ge);
        if (sand == null) return TextCommandResult.Error("sand: " + ae);
        if (waterBlock == null) return TextCommandResult.Error("water: " + we);

        int baseY = sapi.World.SeaLevel;
        double R = diameter / 2.0;
        double oceanRing = Math.Max(24.0, R * 0.6);
        double rmax = R * 1.12 + oceanRing;
        int reach = (int)Math.Ceiling(rmax) + 2;

        DirVec(beachDir, out double bvx, out double bvz);
        DirVec(cliffDir, out double cvx, out double cvz);

        var job = new IslandJob
        {
            Cx = ox, Cz = oz, Dim = dim, BaseY = baseY,
            R = R, Rmax = rmax, OceanRing = oceanRing,
            DomeHeight = height, Water = water, BaseDepth = basedepth,
            Bvx = bvx, Bvz = bvz, Cvx = cvx, Cvz = cvz,
            BumpAmp = Math.Min(4.0, height * 0.15),
            CoastNoise = NormalizedSimplexNoise.FromDefaultOctaves(4, 1 / 40.0, 0.5, seed),
            SurfNoise = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 22.0, 0.5, seed + 1),
            StoneId = stone.BlockId, SoilId = soil.BlockId, GrassId = grass.BlockId,
            SandId = sand.BlockId, WaterId = waterBlock.BlockId,
            MinX = ox - reach, MinZ = oz - reach,
            W = reach * 2 + 1, H = reach * 2 + 1,
            ColumnsPerTick = 400,
            Seed = seed,
            Player = args.Caller?.Player as IServerPlayer
        };
        job.Total = (long)job.W * job.H;

        // Force-load every chunk column the island touches, then start the job
        // in the load callback so writes never hit an unloaded chunk.
        int cs = GlobalConstants.ChunkSize;
        int cx1 = FloorDiv(job.MinX, cs), cx2 = FloorDiv(job.MinX + job.W - 1, cs);
        int cz1 = FloorDiv(job.MinZ, cs), cz2 = FloorDiv(job.MinZ + job.H - 1, cs);

        _islandBusy = true;
        var opts = new ChunkLoadOptions
        {
            KeepLoaded = false,
            OnLoaded = () => StartIslandJob(job)
        };
        sapi.WorldManager.LoadChunkColumnPriority(cx1, cz1, cx2, cz2, opts);

        return TextCommandResult.Success(
            $"Loading {(cx2 - cx1 + 1) * (cz2 - cz1 + 1)} chunk column(s), then generating a {diameter}-block island (beach {beachDir}, cliffs {cliffDir}, seed {seed}). It builds over a few seconds without freezing the server.");
    }

    private void StartIslandJob(IslandJob job)
    {
        _islandJob = job;
        _islandListenerId = sapi.Event.RegisterGameTickListener(OnIslandTick, 40);
    }

    // One tick's worth of columns: a fresh bulk accessor, up to ColumnsPerTick
    // columns, then a single Commit that relights and syncs the batch at once.
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
            if (FillColumn(job, ba, pos, x, z)) job.Placed++;
        }
        ba.Commit();

        if (!job.HasNext)
        {
            sapi.Event.UnregisterGameTickListener(_islandListenerId);
            _islandListenerId = 0;
            _islandJob = null;
            string treeMsg = PlaceSummitTree(job);
            _islandBusy = false;
            ReportIsland(job, $"Island complete: touched {job.Placed} column(s). {treeMsg}");
        }
    }

    // Compute this column's surface, then lay stone, soil, a top block, water up
    // to sea level, and clear any pre-existing terrain above. Returns false for
    // columns outside the work radius (nothing placed).
    private bool FillColumn(IslandJob job, IBulkBlockAccessor ba, BlockPos pos, int x, int z)
    {
        if (!ColumnSurface(job, x, z, out int topY, out bool underwater, out int topMat, out bool nearIsland))
            return false;

        int floorY = Math.Max(1, job.BaseY - job.BaseDepth - job.Water - 2);
        const int soilDepth = 3;

        for (int y = floorY; y <= topY; y++)
        {
            pos.Set(x, y, z);
            int id;
            if (y == topY)
                id = topMat == 0 ? job.GrassId : topMat == 1 ? job.SandId : job.StoneId;
            else if (y > topY - soilDepth)
                id = topMat == 1 ? job.SandId : topMat == 2 ? job.StoneId : job.SoilId;
            else
                id = job.StoneId;
            ba.SetBlock(id, pos);
            // Below sea level our ground replaces open ocean, so clear any water
            // the fluid layer still holds there (else stone reads as submerged).
            if (y <= job.BaseY) ba.SetBlock(0, pos, BlockLayersAccess.Fluid);
        }

        if (underwater)
            for (int y = topY + 1; y <= job.BaseY; y++)
            {
                pos.Set(x, y, z);
                ba.SetBlock(job.WaterId, pos, BlockLayersAccess.Fluid);
            }

        // Strip anything that was already standing above our result (so running
        // this over existing land still yields a clean island, not a buried one).
        // Only near the island: far offshore columns are already open air/water.
        if (nearIsland)
        {
            int clearFrom = (underwater ? job.BaseY : topY) + 1;
            int clearTo = job.BaseY + job.DomeHeight + 6;
            for (int y = clearFrom; y <= clearTo; y++)
            {
                pos.Set(x, y, z);
                ba.SetBlock(0, pos);
                ba.SetBlock(0, pos, BlockLayersAccess.Fluid);
            }
        }
        return true;
    }

    // The heart of the shape. Returns the top solid Y for a column plus whether
    // it sits below sea level and which material caps it (0 grass, 1 sand,
    // 2 stone). False means the column is beyond the work radius.
    private bool ColumnSurface(IslandJob job, int x, int z, out int topY, out bool underwater, out int topMat, out bool nearIsland)
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
            double rise = job.DomeHeight * (1.0 - Math.Pow(t, p));
            if (rise < 0) rise = 0;
            double bump = (job.SurfNoise.Noise(x, z) - 0.5) * 2 * job.BumpAmp * (1 - t);
            topY = (int)Math.Round(job.BaseY + rise + bump);
        }
        else
        {
            double over = dist - edgeR;
            double depthFrac = Smooth(over / job.OceanRing);
            topY = (int)Math.Round(job.BaseY - 2 - job.Water * depthFrac);
        }

        underwater = topY < job.BaseY;
        if (!underwater)
        {
            bool beachTop = beachAlign > 0.3 && (topY - job.BaseY) <= 3;
            bool cliffTop = cliffAlign > 0.3 && t > 0.72;
            topMat = cliffTop ? 2 : beachTop ? 1 : 0;
        }
        else
        {
            topMat = topY >= job.BaseY - 4 ? 1 : 2; // sand near the shoreline, stone in the deep
        }
        return true;
    }

    // Grow a native oak at the summit once the ground exists. Returns a short
    // status suffix for the completion message.
    private string PlaceSummitTree(IslandJob job)
    {
        if (!ColumnSurface(job, job.Cx, job.Cz, out int topY, out bool underwater, out _, out _) || underwater)
            return "No dry summit for a tree.";

        ITreeGenerator oak = FindOakGenerator();
        if (oak == null) return "No oak tree generator found.";

        var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        var pos = new BlockPos(job.Cx, topY + 1, job.Cz, job.Dim);
        var tp = new TreeGenParams
        {
            skipForestFloor = true,
            size = 1f,
            vinesGrowthChance = 0,
            mossGrowthChance = 0,
            otherBlockChance = 0,
            hemisphere = EnumHemisphere.North,
            treesInChunkGenerated = 0
        };
        var rnd = new LCGRandom(job.Seed);
        rnd.InitPositionSeed(job.Cx, job.Cz);
        oak.GrowTree(ba, pos, tp, rnd);
        ba.Commit();
        return "Planted an oak at the summit.";
    }

    // Prefer the English oak; fall back to any generator whose code mentions oak.
    private ITreeGenerator FindOakGenerator()
    {
        ITreeGenerator fallback = null;
        foreach (var kv in sapi.World.TreeGenerators)
        {
            string path = kv.Key.Path.ToLowerInvariant();
            if (path.Contains("englishoak")) return kv.Value;
            if (fallback == null && path.Contains("oak")) fallback = kv.Value;
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

    // Caller's block position, used as the island centre and the dimension the
    // writes go to.
    private static void GetOrigin(Caller caller, out int ox, out int oy, out int oz, out int dim)
    {
        var entity = caller.Entity;
        ox = (int)Math.Floor(entity.Pos.X);
        oy = (int)Math.Floor(entity.Pos.Y);
        oz = (int)Math.Floor(entity.Pos.Z);
        dim = entity.Pos.Dimension;
    }

    // Resolve a block code (game: domain by default; "air" clears).
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
