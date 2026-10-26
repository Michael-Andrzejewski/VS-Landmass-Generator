using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// The Better Ruins megastructures, given islands of their own (0.67.0).
//
// Better Ruins places its megastructures (the cathedral, the castles, the
// aqueduct, the House of Usher...) as ordinary surface structures: the game
// tries one on a chunk and keeps it only where the ground under its centre and
// four corners is exactly level and dry. In a Rustfall world the only land the
// game sees while it places structures is the odd hilly natural island (the
// plan islands are drawn in a later pass), so they practically never appear.
//
// A `ruins` plan line fixes that. It picks one site in open sea for each
// megastructure family, seeded by the world, at least `margin` blocks (500)
// from the edge of every lore island (the plan's islands and pinned story
// locations), and gives each a small flat island sized to its ruin: the
// ruin_isle shapes, whose `ruin` structure levels a footing and places the
// schematic the way the game would (its own y offset, filler blocks to air,
// block entities and loot after the blocks). The sites are worked out once per
// world and kept in its save, so every load and every pass builds the same
// ones. The game's own random megastructure rolls are switched off in such a
// world, so none can turn up next to a lore island either.
//
//   ruins [margin=500] [gap=1400] [reach=2200] [spawn=1500] [count=N] [skip=megastructure-10,...] [vanilla=keep]
//
// A drawn island can carry one too: `struct Q kind=ruin structure=<code>`
// (Cardinals is the Better Ruins cathedral this way).
public partial class LandmassGeneratorModSystem
{
    private const string RuinSitesKey = "lgRuinSites";

    private sealed class RuinEntry
    {
        public string Code, Domain, Family;
        public readonly List<string> Schematics = new();   // asset paths, worldgen/schematics/....json
        public int OffsetY;
        public int MinTemp = -30, MaxTemp = 40;
        public double MaxRain = 1;
    }

    private List<RuinEntry> _ruinCatalog;

    // Every structure in the "megastructures" group of any mod's
    // worldgen/structures.json (Better Ruins' are the only ones today).
    private List<RuinEntry> RuinCatalog()
    {
        if (_ruinCatalog != null) return _ruinCatalog;
        var list = new List<RuinEntry>();
        foreach (var asset in sapi.Assets.GetMany("worldgen/structures.json"))
        {
            JsonObject root;
            try { root = JsonObject.FromJson(asset.ToText()); }
            catch (Exception e) { sapi.Logger.Warning("[landmassgenerator] ruins: could not read {0}: {1}", asset.Location, e.Message); continue; }
            if (root?["structures"]?.Exists != true) continue;
            foreach (var s in root["structures"].AsArray() ?? Array.Empty<JsonObject>())
            {
                if (!string.Equals(s["group"].AsString(), "megastructures", StringComparison.OrdinalIgnoreCase)) continue;
                var e = new RuinEntry
                {
                    Code = s["code"].AsString(),
                    Domain = asset.Location.Domain,
                    OffsetY = s["offsety"].Exists ? s["offsety"].AsInt(0) : s["offsetY"].AsInt(0),
                    MinTemp = s["minTemp"].AsInt(-30),
                    MaxTemp = s["maxTemp"].AsInt(40),
                    MaxRain = s["maxRain"].AsDouble(1)
                };
                var sch = s["schematics"].AsArray();
                if (sch == null) continue;
                foreach (string p in sch.Select(t => t.AsString()).Where(t => t != null))
                {
                    if (p.EndsWith("*"))
                    {
                        string prefix = "worldgen/schematics/" + p.TrimEnd('*');
                        foreach (var loc in sapi.Assets.GetLocations(prefix, e.Domain).OrderBy(l => l.Path, StringComparer.Ordinal))
                            if (loc.Path.EndsWith(".json")) e.Schematics.Add(loc.Path);
                    }
                    else e.Schematics.Add("worldgen/schematics/" + p + ".json");
                }
                if (e.Code == null || e.Schematics.Count == 0) continue;
                // the family is the schematic folder: wolfstone castle's four
                // climate entries are one family, so it gets one island
                string first = e.Schematics[0];
                string dir = first.Substring(0, first.LastIndexOf('/'));
                if (dir.EndsWith("/common")) dir = dir.Substring(0, dir.Length - 7);
                e.Family = dir.Substring(dir.LastIndexOf('/') + 1);
                list.Add(e);
            }
        }
        _ruinCatalog = list;
        return list;
    }

    private static readonly Regex SizeRx = new(@"""Size([XYZ])""\s*:\s*(\d+)", RegexOptions.Compiled);

    // A schematic's size from the head of its file (the whole file can be
    // several megabytes of block data).
    private (int X, int Y, int Z) SchematicSize(string domain, string path)
    {
        var a = sapi.Assets.TryGet(new AssetLocation(domain, path));
        if (a == null) return (0, 0, 0);
        string head = a.ToText();
        if (head.Length > 4000) head = head.Substring(0, 4000);
        int x = 0, y = 0, z = 0;
        foreach (Match m in SizeRx.Matches(head))
        {
            int v = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (m.Groups[1].Value == "X") x = v; else if (m.Groups[1].Value == "Y") y = v; else z = v;
        }
        return (x, y, z);
    }

    // The farthest land cell of a drawn shape from its centre, in cells.
    private static double ShapeLandRadius(char[,] cells, int w, int h)
    {
        double best = 0;
        for (int x = 0; x < w; x++)
            for (int z = 0; z < h; z++)
                if (cells[x, z] != '.' && cells[x, z] != ' ' && cells[x, z] != '\0')
                    best = Math.Max(best, Math.Sqrt((x + 0.5 - w / 2.0) * (x + 0.5 - w / 2.0) + (z + 0.5 - h / 2.0) * (z + 0.5 - h / 2.0)));
        return best;
    }

    // A plan island's edge (how far its land reaches) from its options:
    // its shape's farthest land at its diameter, or half the diameter.
    private int PlanIslandEdge(string options)
    {
        var opt = ParseIslandOptions(options);
        int d = OptInt(opt, "diameter", 120, 8, 4096);
        string name = OptStr(opt, "shape", null);
        if (name == null) return (int)Math.Ceiling(d / 2.0 * 1.12);
        if (name.EndsWith(".txt")) name = name.Substring(0, name.Length - 4);
        string text = KeptShapeText(name);
        string path = System.IO.Path.Combine(shapeFolder, name + ".txt");
        if (text == null && System.IO.File.Exists(path)) text = System.IO.File.ReadAllText(path);
        if (text == null) return d / 2;
        var rows = new List<string>();
        bool map = false;
        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            if (map) { if (raw.Length > 0) rows.Add(raw); }
            else if (raw.Trim() == "map") map = true;
        }
        if (rows.Count == 0) return d / 2;
        int w = rows.Max(r => r.Length), h = rows.Count;
        var cells = new char[w, h];
        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++) cells[x, z] = x < rows[z].Length ? rows[z][x] : '.';
        return (int)Math.Ceiling(ShapeLandRadius(cells, w, h) * d / Math.Max(w, h));
    }

    private static string RuinClimate(RuinEntry e) =>
        e.MaxTemp <= 0 ? "cold" : e.MinTemp >= 20 ? "arid" : e.MaxRain <= 0.3 ? "dry" : "temperate";

    // The edge radius of a story location's island: its forced land is
    // about (landformRadius + 32) * 1.41 across (tips.md).
    private static int StoryEdge(int landformRadius) => (int)((landformRadius + 32) * 1.41);

    private Dictionary<string, int> StoryLandformRadii()
    {
        var radii = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var genStory = sapi.ModLoader.GetModSystem<GenStoryStructures>();
        var cfg = genStory?.scfg;
        if (cfg?.Structures != null)
            foreach (var s in cfg.Structures) if (s?.Code != null) radii[s.Code] = s.LandformRadius;
        return radii;
    }

    // The plan's `ruins` line, expanded into island lines: the kept list of
    // this world, or worked out now and kept.
    private List<(int MapX, int MapZ, string Options)> PlanRuinIslands(IEnumerable<string> planLines, List<(int MapX, int MapZ, string Options)> islands)
    {
        var result = new List<(int MapX, int MapZ, string Options)>();
        string spec = null;
        var storySites = new List<(string Code, int X, int Z)>();
        foreach (string raw in planLines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts[0].Equals("ruins", StringComparison.OrdinalIgnoreCase)) spec = line;
            else if (parts[0].Equals("storyloc", StringComparison.OrdinalIgnoreCase) && parts.Length >= 4
                && int.TryParse(parts[2], out int sx) && int.TryParse(parts[3], out int sz))
                storySites.Add((parts[1].ToLowerInvariant(), sx, sz));
        }
        if (spec == null) return result;

        var save = sapi.WorldManager.SaveGame;
        var kept = save.GetData<List<string>>(RuinSitesKey, null);
        if (kept != null && kept.Count > 0 && kept[0] == spec)
        {
            foreach (string s in kept.Skip(1))
            {
                string[] p = s.Split('|');
                if (p.Length == 3 && int.TryParse(p[0], out int x) && int.TryParse(p[1], out int z)) result.Add((x, z, p[2]));
            }
            return result;
        }

        var opt = ParseIslandOptions(spec.Substring(5));
        int margin = OptInt(opt, "margin", 500, 0, 5000);
        int gap = OptInt(opt, "gap", 1400, 200, 10000);
        int reach = OptInt(opt, "reach", 2200, 0, 20000);
        int spawnClear = OptInt(opt, "spawn", 1500, 0, 20000);
        var skip = new HashSet<string>(OptStr(opt, "skip", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);

        // What the ruins keep away from: every plan island and pinned story
        // location by `margin` beyond its edge, the game's other story
        // locations (Better Ruins' own, wherever the world rolled them) by 300.
        var avoid = new List<(int X, int Z, int Edge, int Margin)>();
        foreach (var isl in islands)
            avoid.Add((isl.MapX, isl.MapZ, PlanIslandEdge(isl.Options), margin));
        var radii = StoryLandformRadii();
        foreach (var st in storySites)
            avoid.Add((st.X, st.Z, StoryEdge(radii.TryGetValue(st.Code, out int lr) ? lr : 300), margin));
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        var genStory = sapi.ModLoader.GetModSystem<GenStoryStructures>();
        if (genStory?.Structures != null)
            foreach (var pair in genStory.Structures)
            {
                if (storySites.Any(s => s.Code == pair.Key)) continue;
                avoid.Add((pair.Value.CenterPos.X - mx, pair.Value.CenterPos.Z - mz, StoryEdge(pair.Value.LandformRadius), 300));
            }
        if (avoid.Count == 0) return result;

        int x1 = avoid.Where(a => a.Margin == margin).Min(a => a.X - a.Edge) - reach, x2 = avoid.Where(a => a.Margin == margin).Max(a => a.X + a.Edge) + reach;
        int z1 = avoid.Where(a => a.Margin == margin).Min(a => a.Z - a.Edge) - reach, z2 = avoid.Where(a => a.Margin == margin).Max(a => a.Z + a.Edge) + reach;

        var rand = new LCGRandom(sapi.World.Seed ^ 0x52756E73L);
        rand.InitPositionSeed(7, 11);
        var families = RuinCatalog().GroupBy(e => e.Family)
            .Where(g => !skip.Contains(g.Key) && !g.Any(e => skip.Contains(e.Code)))
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        for (int i = families.Count - 1; i > 0; i--) { int j = rand.NextInt(i + 1); (families[i], families[j]) = (families[j], families[i]); }
        int count = OptInt(opt, "count", families.Count, 0, 200);

        var placed = new List<(int X, int Z, int R)>();
        var lines = new List<string> { spec };
        var notes = new List<string>();
        foreach (var fam in families)
        {
            if (placed.Count >= count) break;
            var entries = fam.OrderBy(e => e.Code, StringComparer.Ordinal).ToList();
            var entry = entries[rand.NextInt(entries.Count)];
            int pick = rand.NextInt(entry.Schematics.Count);
            var size = SchematicSize(entry.Domain, entry.Schematics[pick]);
            int side = Math.Max(size.X, size.Z);
            if (side <= 0) { notes.Add(entry.Code + ": no schematic size"); continue; }
            int diameter = Math.Clamp((int)Math.Round(side * 2.3 + 80), 180, 460);
            int r = diameter / 2;
            int rot = rand.NextInt(4) * 90;
            bool ok = false;
            for (int t = 0; t < 1500 && !ok; t++)
            {
                int x = x1 + rand.NextInt(Math.Max(1, x2 - x1)), z = z1 + rand.NextInt(Math.Max(1, z2 - z1));
                if ((long)x * x + (long)z * z < (long)(spawnClear + r) * (spawnClear + r)) continue;
                bool clear = true;
                foreach (var a in avoid)
                {
                    long need = a.Edge + a.Margin + r, dx = x - a.X, dz = z - a.Z;
                    if (dx * dx + dz * dz < need * need) { clear = false; break; }
                }
                if (!clear) continue;
                foreach (var p in placed)
                {
                    long need = Math.Max(gap, p.R + r + 300), dx = x - p.X, dz = z - p.Z;
                    if (dx * dx + dz * dz < need * need) { clear = false; break; }
                }
                if (!clear) continue;
                string clim = RuinClimate(entry);
                string shape = clim == "cold" ? "ruin_isle_cold" : clim is "arid" or "dry" ? "ruin_isle_arid" : "ruin_isle";
                string rock = clim == "cold" ? "rock-andesite" : clim is "arid" or "dry" ? "rock-sandstone" : "rock-granite";
                string sand = clim is "arid" or "dry" ? "sand-sandstone" : "sand-granite";
                string o = $"shape={shape} diameter={diameter} height=12 water=34 climate={clim} stone={rock} sand={sand} ruin={entry.Code} ruinschem={pick} ruinrot={rot}";
                placed.Add((x, z, r));
                lines.Add($"{x}|{z}|{o}");
                result.Add((x, z, o));
                sapi.Logger.Notification("[landmassgenerator] ruin site {0} ({1}, {2}x{3}) at HUD {4}, {5}, island {6} wide", entry.Code, entry.Schematics[pick].Substring(entry.Schematics[pick].LastIndexOf('/') + 1), size.X, size.Z, x, z, diameter);
                ok = true;
            }
            if (!ok) notes.Add(entry.Code + ": no room");
        }
        save.StoreData(RuinSitesKey, lines);
        sapi.Logger.Notification("[landmassgenerator] ruins: {0} Better Ruins megastructure island(s) placed{1}", result.Count,
            notes.Count > 0 ? "; not placed: " + string.Join(", ", notes) : "");
        return result;
    }

    // A world with a `ruins` line places the megastructures itself; the
    // game's own rolls (which could only land on natural islands, anywhere)
    // are switched off so none turns up next to a lore island.
    private void SwitchOffVanillaMegastructures(IEnumerable<string> planLines)
    {
        string spec = planLines.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("ruins", StringComparison.OrdinalIgnoreCase)
            && (l.Length == 5 || char.IsWhiteSpace(l[5])));
        if (spec == null) return;
        if (OptStr(ParseIslandOptions(spec.Substring(5)), "vanilla", "off").Equals("keep", StringComparison.OrdinalIgnoreCase)) return;
        var gs = sapi.ModLoader.GetModSystem("Vintagestory.ServerMods.GenStructures");
        var cfg = gs?.GetType().GetField("scfg", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(gs)
            as Vintagestory.ServerMods.WorldGenStructuresConfig;
        if (cfg?.Structures == null) { sapi.Logger.Warning("[landmassgenerator] ruins: the game's structure config was not found; its own megastructure rolls stay on."); return; }
        int n = 0;
        foreach (var s in cfg.Structures)
            if (string.Equals(s.Group, "megastructures", StringComparison.OrdinalIgnoreCase) && s.Chance > 0) { s.Chance = 0; n++; }
        if (n > 0) sapi.Logger.Notification("[landmassgenerator] ruins: the game's own rolls for {0} megastructure(s) switched off; the plan's ruin islands carry them.", n);
    }

    // ── THE RUIN ──────────────────────────────────────────────────────────
    // One Better Ruins megastructure on a levelled footing. Which one:
    // structure=<code> (and schematic=<n>, rot=<deg>) on the struct line, or
    // the island's ruin= / ruinschem= / ruinrot= options (the ruins plan
    // line writes those). The footing is the median ground of the footprint,
    // blended back into the slope over `blend` blocks; the schematic goes in
    // at ground + 1 + its own y offset, as the game places it.
    private void MegaRuin(MegaKit k, int cx, int cz)
    {
        var job = k.Job;
        string JobOpt(string key) => job.Opt != null && job.Opt.TryGetValue(key, out var v) ? v : null;
        string code = k.Opt("structure", null) ?? JobOpt("ruin");
        if (code == null) { sapi.Logger.Warning("[landmassgenerator] ruin: no structure= on the struct line and no ruin= on the island"); return; }
        // A story structure's schematic, named outright (0.68.0, the Mansion):
        // path=<domain>:worldgen/schematics/....json, and storyoffset= its
        // schematicYOffsets entry, so it sits as the game's SurfaceRuin
        // placement would put it (bottom at ground - height + offset).
        string direct = k.Opt("path", null);
        int storyOffset = k.OptI("storyoffset", int.MinValue);
        RuinEntry entry;
        if (direct != null)
        {
            var dl = new AssetLocation(direct);
            entry = new RuinEntry { Code = code, Domain = dl.Domain, Family = code };
            entry.Schematics.Add(dl.Path);
        }
        else entry = RuinCatalog().FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase));
        if (entry == null) { sapi.Logger.Warning("[landmassgenerator] ruin: no megastructure called {0} (is Better Ruins installed?)", code); return; }
        int pick = k.OptI("schematic", int.TryParse(JobOpt("ruinschem"), out int js) ? js : (int)(k.R() * entry.Schematics.Count));
        int rot = k.OptI("rot", int.TryParse(JobOpt("ruinrot"), out int jr) ? jr : (int)(k.R() * 4) * 90);
        string path = entry.Schematics[Math.Clamp(pick, 0, entry.Schematics.Count - 1)];
        var asset = sapi.Assets.TryGet(new AssetLocation(entry.Domain, path));
        if (asset == null) { sapi.Logger.Warning("[landmassgenerator] ruin: schematic {0}:{1} not found", entry.Domain, path); return; }
        var schem = JsonUtil.FromString<BlockSchematic>(asset.ToText());
        // The block-layer marker takes the ground's own top block when the
        // game places it; here that is the island's grass.
        foreach (var key in schem.BlockCodes.Keys.ToList())
            if (schem.BlockCodes[key].Path == "meta-blocklayer") schem.BlockCodes[key] = new AssetLocation("game", "soil-medium-normal");
        schem.Init(sapi.World.BlockAccessor);
        rot = ((rot % 360) + 360) % 360 / 90 * 90;
        if (rot != 0) schem.TransformWhilePacked(sapi.World, EnumOrigin.BottomCenter, rot);
        int sx = schem.SizeX, sy = schem.SizeY, sz = schem.SizeZ;
        int x0 = cx - sx / 2, z0 = cz - sz / 2;

        var gs = new List<int>();
        for (int x = x0; x < x0 + sx; x += 2)
            for (int z = z0; z < z0 + sz; z += 2) gs.Add(k.Ground(x, z));
        gs.Sort();
        int g0 = Math.Max(k.Sea + 3, gs[gs.Count / 2]);
        int soil = k.IdF("soil-medium-normal", "soil-low-normal");
        int dirt = k.IdF("soil-medium-none", "soil-low-none");
        int stone = job.StoneId;
        int blend = k.OptI("blend", 8);

        // Fell the trees over the footing and round it, whole (TreeFelling.cs),
        // then cut and fill.
        FellTrees(k.Set, job.Dim, x0 - blend - 2, z0 - blend - 2, x0 + sx + blend + 1, z0 + sz + blend + 1, g0 - 4, g0 + 40);
        for (int x = x0 - blend; x < x0 + sx + blend; x++)
            for (int z = z0 - blend; z < z0 + sz + blend; z++)
            {
                int dOut = Math.Max(Math.Max(x0 - x, x - (x0 + sx - 1)), Math.Max(z0 - z, z - (z0 + sz - 1)));
                int gy = k.Ground(x, z);
                int target = dOut <= 0 ? g0 : (int)Math.Round(g0 + (gy - g0) * (dOut / (double)(blend + 1)));
                if (gy < k.Sea && dOut > 0) continue;   // leave the sea where the blend reaches the shore
                for (int y = Math.Min(gy, target) - 1; y < target; y++) k.Set(x, y, z, y < target - 3 ? stone : dirt);
                k.Set(x, target, z, soil);
                for (int y = target + 1; y <= Math.Max(gy, target) + 4; y++) k.Set(x, y, z, 0);
            }

        int fy = storyOffset != int.MinValue ? g0 - sy + storyOffset : g0 + 1 + entry.OffsetY;
        string name = path.Substring(path.LastIndexOf('/') + 1);
        k.AfterCommit.Add(() =>
        {
            var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
            var start = new BlockPos(x0, fy, z0, job.Dim);
            // Place() would also spawn the schematic's entities, before its
            // blocks are in; they wait for the second pass below
            var ents = schem.Entities;
            schem.Entities = new List<string>();
            int n = schem.Place(ba, sapi.World, start, schem.ReplaceMode, true);
            ba.Commit();
            schem.Entities = ents;
            // the bulk accessor has no block entities until it commits, so
            // the chests, spawners and signs get their data now
            schem.PlaceEntitiesAndBlockEntities(sapi.World.BlockAccessor, sapi.World, start, schem.BlockCodes, schem.ItemCodes);
            schem.PlaceDecors(sapi.World.BlockAccessor, start);
            sapi.Logger.Notification("[landmassgenerator] ruin {0} ({1}, {2}x{3}x{4}, turned {5}) at HUD {6}, {7}, {8}: {9} blocks",
                entry.Code, name, sx, sy, sz, rot, x0 - sapi.WorldManager.MapSizeX / 2, fy, z0 - sapi.WorldManager.MapSizeZ / 2, n);
        });
    }
}
