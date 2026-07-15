using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        "nativecopper", "limonite", "galena", "cassiterite", "chromite", "ilmenite",
        "sphalerite", "bismuthinite", "magnetite", "hematite", "malachite",
        "pentlandite", "uranium", "wolframite", "rhodochrosite"
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
        { "silver", new[] { "galena" } },
        { "nickel", new[] { "pentlandite" } },
        { "chromium", new[] { "chromite" } },
        { "chrome", new[] { "chromite" } },
        { "titanium", new[] { "ilmenite" } },
        { "tungsten", new[] { "wolframite" } },
        { "bismuth", new[] { "bismuthinite" } },
        { "manganese", new[] { "rhodochrosite" } }
    };

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

    // Surface treatments a region can have.
    private const int SurfGrass = 0, SurfSand = 1, SurfRock = 2, SurfRockSand = 3;

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
        public List<ITreeGenerator> Trees = new();
        public List<OreSpec> Ores = new();
        public int StoneId, SandId, SoilId, GrassId;
    }

    private class TreeMarker
    {
        public int Gx, Gz;
        public ITreeGenerator Gen;
        public float Size;
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

        public NormalizedSimplexNoise CoastNoise, SurfNoise, Dither;
        public int StoneId, SoilId, GrassId, SandId, WaterId;

        public int MinX, MinZ, W, H;
        public long I, Total, Placed, Trees;
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
        if (stone == null) return TextCommandResult.Error("stone: " + se);
        if (soil == null) return TextCommandResult.Error("soil: " + oe);
        if (grass == null) return TextCommandResult.Error("grass: " + ge);
        if (sand == null) return TextCommandResult.Error("sand: " + ae);
        if (waterBlock == null) return TextCommandResult.Error("water: " + we);

        var problems = new List<string>();

        var job = new IslandJob
        {
            Cx = ox, Cz = oz, Dim = dim, SeaLevel = seaLevel,
            DomeHeight = height, Water = water, MaxDepth = maxdepth,
            Seed = seed,
            CoastNoise = NormalizedSimplexNoise.FromDefaultOctaves(4, 1 / 40.0, 0.5, seed),
            SurfNoise = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 22.0, 0.5, seed + 1),
            Dither = NormalizedSimplexNoise.FromDefaultOctaves(2, 0.4, 0.5, seed + 21),
            JitterX = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 26.0, 0.5, seed + 7),
            JitterZ = NormalizedSimplexNoise.FromDefaultOctaves(3, 1 / 26.0, 0.5, seed + 13),
            StoneId = stone.BlockId, SoilId = soil.BlockId, GrassId = grass.BlockId,
            SandId = sand.BlockId, WaterId = waterBlock.BlockId,
            ColumnsPerTick = 400,
            Phase = 0,
            Rand = new LCGRandom(seed),
            Player = args.Caller?.Player as IServerPlayer
        };

        int reach;
        string shapeName = OptStr(opt, "shape", null);

        if (shapeName != null)
        {
            ShapeDef shape = LoadShape(shapeName, job, seed, problems, out string err);
            if (shape == null) return TextCommandResult.Error(err);

            job.Shape = shape;
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
        shape.HeightField = SmoothField(rawH, isLand, shape.W, shape.H, 3);
        shape.ShoreField = SmoothField(rawS, isLand, shape.W, shape.H, 3);
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
        string oreStr = null;
        string treeStr = null;

        for (int i = 2; i < tok.Length; i++)
        {
            int eq = tok[i].IndexOf('=');
            if (eq <= 0) continue;
            string k = tok[i].Substring(0, eq).ToLowerInvariant();
            string v = tok[i].Substring(eq + 1);

            switch (k)
            {
                case "rock": r.RockType = v; break;
                case "ores": oreStr = v; break;
                case "trees": treeStr = v; break;
                case "surface":
                    r.Surface = v.ToLowerInvariant() switch
                    {
                        "sand" => SurfSand,
                        "rock" => SurfRock,
                        "rocksand" => SurfRockSand,
                        _ => SurfGrass
                    };
                    break;
                case "height": r.Height = ParseD(v, 1.0); break;
                case "shore": r.ShoreWidth = Math.Max(1.0, ParseD(v, 8)); break;
                case "rough": r.Rough = ParseD(v, 0.3); break;
                case "forest": r.Forest = Math.Clamp(ParseD(v, 0), 0, 0.35); break;
            }
        }

        Block stone = sapi.World.GetBlock(new AssetLocation("game", "rock-" + r.RockType));
        Block rsand = sapi.World.GetBlock(new AssetLocation("game", "sand-" + r.RockType));
        r.StoneId = stone?.BlockId ?? job.StoneId;
        r.SandId = rsand?.BlockId ?? job.SandId;
        r.SoilId = job.SoilId;
        r.GrassId = job.GrassId;
        if (stone == null) problems.Add($"region {r.Key}: no rock-{r.RockType}, using the default stone");

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
        return r;
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
                    Threshold = RichnessToThreshold(rich),
                    Noise = NormalizedSimplexNoise.FromDefaultOctaves(2, 1 / 11.0, 0.5, seed + 100 + idx),
                    PoorId = OreId("poor", mineral, rockType),
                    MediumId = OreId("medium", mineral, rockType),
                    RichId = OreId("rich", mineral, rockType),
                    BountifulId = OreId("bountiful", mineral, rockType)
                };
                if (o.PoorId != 0 || o.MediumId != 0 || o.RichId != 0 || o.BountifulId != 0) { found = o; break; }
            }
            idx++;

            if (found == null) problems.Add($"no '{want}' ore occurs in {rockType}");
            else into.Add(found);
        }
    }

    private int OreId(string grade, string mineral, string rock)
        => sapi.World.GetBlock(new AssetLocation("game", $"ore-{grade}-{mineral}-{rock}"))?.BlockId ?? 0;

    private static double RichnessToThreshold(string rich)
    {
        if (double.TryParse(rich, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return Math.Clamp(0.86 - 0.30 * Math.Clamp(v, 0, 1), 0.55, 0.90);
        return rich.ToLowerInvariant() switch
        {
            "rare" => 0.86,
            "sparse" => 0.82,
            "rich" => 0.68,
            "abundant" => 0.62,
            _ => 0.75
        };
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

        var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        var pos = new BlockPos(0, 0, 0, job.Dim);

        int budget = job.ColumnsPerTick;
        while (budget-- > 0 && job.HasNext)
        {
            int x = job.MinX + (int)(job.I % job.W);
            int z = job.MinZ + (int)(job.I / job.W);
            job.I++;

            if (job.Phase == 0) { if (FillColumn(job, ba, pos, x, z)) job.Placed++; }
            else { if (TryPlantTree(job, ba, pos, x, z)) job.Trees++; }
        }
        ba.Commit();

        if (job.HasNext) return;

        if (job.Phase == 0 && HasForest(job))
        {
            job.Phase = 1;
            job.I = 0;
            return;
        }

        sapi.Event.UnregisterGameTickListener(_islandListenerId);
        _islandListenerId = 0;
        _islandJob = null;
        string extra = PlaceLandmarkTrees(job);
        _islandBusy = false;

        string done = $"Island complete: {job.Placed} column(s)";
        if (job.Trees > 0) done += $", {job.Trees} tree(s)";
        ReportIsland(job, done + ". " + extra);
    }

    private static bool HasForest(IslandJob job)
    {
        if (job.Shape == null) return job.ForestDensity > 0 && job.ForestTrees.Count > 0;
        foreach (var r in job.Shape.Regions.Values)
            if (r.Forest > 0 && r.Trees.Count > 0) return true;
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

        if (!ColumnSurface(job, x, z, naturalY, out int topY, out bool underwater, out int topMat, out bool nearIsland, out Region reg))
            return false;

        int stoneId = reg?.StoneId ?? job.StoneId;
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
                id = topMat == SurfGrass ? grassId : topMat == SurfSand ? sandId : stoneId;
            else if (y > topY - skin)
                id = topMat == SurfGrass ? soilId : topMat == SurfSand ? sandId : stoneId;
            else
                id = PickStone(ores, stoneId, x, y, z);
            ba.SetBlock(id, pos);
            if (y < job.SeaLevel) ba.SetBlock(0, pos, BlockLayersAccess.Fluid);
        }

        int clearTop = Math.Max(naturalY, nearIsland ? job.SeaLevel + job.DomeHeight + 6 : job.SeaLevel);
        for (int y = topY + 1; y <= clearTop; y++)
        {
            pos.Set(x, y, z);
            ba.SetBlock(0, pos);
            ba.SetBlock(y < job.SeaLevel ? job.WaterId : 0, pos, BlockLayersAccess.Fluid);
        }
        return true;
    }

    private static int PickStone(List<OreSpec> ores, int stoneId, int x, int y, int z)
    {
        for (int i = 0; i < ores.Count; i++)
            if (ores[i].TryPick(x, y, z, out int oreId)) return oreId;
        return stoneId;
    }

    // Where the ground's top block sits, what caps it, and (in shape mode) which
    // region owns it. False means this column is outside the work area.
    private bool ColumnSurface(IslandJob job, int x, int z, int naturalY,
        out int topY, out bool underwater, out int topMat, out bool nearIsland, out Region reg)
    {
        return job.Shape != null
            ? ShapeSurface(job, x, z, naturalY, out topY, out underwater, out topMat, out nearIsland, out reg)
            : RadialSurface(job, x, z, naturalY, out topY, out underwater, out topMat, out nearIsland, out reg);
    }

    // Drawn island: sample the grid, and turn distance-from-the-coast into height.
    private bool ShapeSurface(IslandJob job, int x, int z, int naturalY,
        out int topY, out bool underwater, out int topMat, out bool nearIsland, out Region reg)
    {
        topY = 0; underwater = false; topMat = SurfGrass; nearIsland = false; reg = null;
        ShapeDef s = job.Shape;

        // Nudge the sample point with noise so the coast is organic instead of
        // showing the grid's stair-steps. Kept modest so it wiggles the border
        // without speckling regions across each other.
        double jx = (job.JitterX.Noise(x, z) - 0.5) * 2.0 * 0.7;
        double jz = (job.JitterZ.Noise(x, z) - 0.5) * 2.0 * 0.7;
        double gx = (x - job.Cx) / job.WorldPerCell + s.W / 2.0 + jx;
        double gz = (z - job.Cz) / job.WorldPerCell + s.H / 2.0 + jz;

        int cx = (int)Math.Floor(gx), cz = (int)Math.Floor(gz);
        bool inGrid = cx >= 0 && cz >= 0 && cx < s.W && cz < s.H;
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
            // Fine-grained dither on the rounding so a smooth slope breaks into
            // ragged ground instead of clean contour terraces.
            double dith = (job.Dither.Noise(x, z) - 0.5) * 1.35;
            topY = (int)Math.Round(job.SeaLevel + rise + rough * Math.Min(1.0, dCoast / 6.0) + dith);

            if (topY < job.SeaLevel) topY = job.SeaLevel; // land never dips under its own shore
            topMat = SurfaceMat(job, r, x, z);
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
        topMat = topY >= job.SeaLevel - 4 ? SurfSand : SurfRock;
        return true;
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
        if (r.Surface != SurfRockSand) return r.Surface;
        // Rocky outcrops speckled with sand.
        return job.CoastNoise.Noise(x * 0.9, z * 0.9) > 0.5 ? SurfRock : SurfSand;
    }

    // The original radial dome, kept for quick islands with no shape file.
    private bool RadialSurface(IslandJob job, int x, int z, int naturalY,
        out int topY, out bool underwater, out int topMat, out bool nearIsland, out Region reg)
    {
        topY = 0; underwater = false; topMat = SurfGrass; nearIsland = false; reg = null;

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
    //  Forest
    // ─────────────────────────────────────────────────────────────────────
    private bool TryPlantTree(IslandJob job, IBulkBlockAccessor ba, BlockPos pos, int x, int z)
    {
        if (!ColumnSurface(job, x, z, job.SeaLevel, out int topY, out bool underwater, out int topMat, out _, out Region reg))
            return false;
        if (underwater || topMat != SurfGrass) return false; // grass only

        double density = reg?.Forest ?? job.ForestDensity;
        List<ITreeGenerator> pool = reg?.Trees ?? job.ForestTrees;
        if (density <= 0 || pool.Count == 0) return false;

        job.Rand.InitPositionSeed(x, z);
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
        pos.Set(x, topY + 1, z);
        pool[job.Rand.NextInt(pool.Count)].GrowTree(ba, pos, tp, job.Rand);
        return true;
    }

    private static void MarkerWorld(IslandJob job, TreeMarker m, out int x, out int z)
    {
        x = job.Cx + (int)Math.Round((m.Gx + 0.5 - job.Shape.W / 2.0) * job.WorldPerCell);
        z = job.Cz + (int)Math.Round((m.Gz + 0.5 - job.Shape.H / 2.0) * job.WorldPerCell);
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
                if (!ColumnSurface(job, x, z, job.SeaLevel, out int topY, out bool uw, out _, out _, out _) || uw) continue;
                var rnd = new LCGRandom(job.Seed);
                rnd.InitPositionSeed(x, z);
                m.Gen.GrowTree(ba, new BlockPos(x, topY + 1, z, job.Dim), LandmarkParams(m.Size), rnd);
                planted++;
            }
        }
        else if (job.SummitTree != null)
        {
            if (ColumnSurface(job, job.Cx, job.Cz, job.SeaLevel, out int topY, out bool uw, out _, out _, out _) && !uw)
            {
                var rnd = new LCGRandom(job.Seed);
                rnd.InitPositionSeed(job.Cx, job.Cz);
                job.SummitTree.GrowTree(ba, new BlockPos(job.Cx, topY + 1, job.Cz, job.Dim), LandmarkParams(1.6f), rnd);
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
