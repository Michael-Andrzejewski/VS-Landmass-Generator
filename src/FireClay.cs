using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace LandmassGenerator;

// Fire clay (0.74.8). The game only puts fire clay in bauxite sand and gravel, which no Rustfall island has,
// so these patches are the only natural fire clay in the islands (Michael's list, 2026-11-23):
//
//   Forge Volcano        a clay pit on open ground near the forge
//   Volcano Ring         a clay bed on the crater lake's shore
//   Iron Mine            a seam in the walls of the main mine and of a side cave
//   Forest Island        a patch on open ground, and a seam in the cave beside a coal seam
//   Tin Island           a small patch on open ground
//
// New worlds get them as each island is decorated; /genfireclay adds them to an existing world.
// Once per island (lgFireClayDone:<shape>@<x>,<z>).
public partial class LandmassGeneratorModSystem
{
    // Kind: "surface" (open ground, Near = the map char to stay near, or none), "lakeshore" (beside water above
    // sea level), "cave" (Near = the cave's map char). Size: surface radius, or blocks of clay in a cave.
    private record FireClayPatch(string Kind, char Near, double Size, bool Coal = false);

    private static readonly Dictionary<string, FireClayPatch[]> FireClayPatches = new()
    {
        ["forge_volcano"] = new[] { new FireClayPatch("surface", 'F', 5) },
        ["volcano_ring"] = new[] { new FireClayPatch("lakeshore", '\0', 9) },
        ["ironmine_island"] = new[] { new FireClayPatch("cave", 'K', 36), new FireClayPatch("cave", 'c', 24) },
        ["forester_island"] = new[] { new FireClayPatch("surface", '\0', 4), new FireClayPatch("cave", 'V', 28, Coal: true) },
        ["tin_island"] = new[] { new FireClayPatch("surface", '\0', 2.5) },
    };

    private string PlaceFireClay(IslandJob job)
    {
        if (job.ShapeName == null || job.Shape == null || !FireClayPatches.TryGetValue(job.ShapeName, out var patches)) return "";
        string doneKey = $"lgFireClayDone:{job.ShapeName}@{job.Cx},{job.Cz}#";
        if (Enumerable.Range(1, patches.Length).All(n => sapi.WorldManager.SaveGame.GetData<bool>(doneKey + n, false))) return "";
        Block clay = sapi.World.GetBlock(new AssetLocation("game", "rawclay-fire-none"));
        Block clayGrass = sapi.World.GetBlock(new AssetLocation("game", "rawclay-fire-verysparse")) ?? clay;
        if (clay == null) return " no fire clay block in this game.";
        if (patches.Any(p => p.Kind == "cave") && job.CaveSteps == null)
        {
            var dry = new CaveWork { DryRun = true };
            CarveCaves(job, dry);
            job.CaveSteps = dry.Steps;
        }
        var notes = new List<string>();
        int i = 0;
        foreach (var p in patches)
        {
            var rand = new CaveRand((uint)(job.Seed * 977 + 131 * ++i));
            if (sapi.WorldManager.SaveGame.GetData<bool>(doneKey + i, false)) continue;
            try
            {
                bool ok;
                notes.Add(p.Kind == "cave" ? FireClayCave(job, p, clay, rand, out ok) : FireClaySurface(job, p, clay, clayGrass, rand, out ok));
                if (ok) sapi.WorldManager.SaveGame.StoreData(doneKey + i, true);
            }
            catch (Exception e) { sapi.Logger.Error(e); notes.Add("FAILED: " + e.Message); }
        }
        string msg = string.Join(" ", notes);
        sapi.Logger.Notification("[fireclay] {0}: {1}", job.ShapeName, msg);
        return " Fire clay: " + msg;
    }

    // The top natural block of a column (soil, sand, gravel or clay), skipping plants, leaves and logs;
    // -1 when the column is under water or has none.
    private int FireClayTop(IBlockAccessor ba, int x, int z, int dim, int seaLevel, bool rock = false)
    {
        var p = new BlockPos(x, ba.GetRainMapHeightAt(x, z), z, dim);
        for (; p.Y > seaLevel; p.Y--)
        {
            if (ba.GetBlock(p, BlockLayersAccess.Fluid).Id != 0) return -1;
            Block b = ba.GetBlock(p, BlockLayersAccess.Solid);
            if (b.Id == 0 || b.Replaceable >= 6000) continue;
            switch (b.BlockMaterial)
            {
                case EnumBlockMaterial.Leaves: case EnumBlockMaterial.Wood: case EnumBlockMaterial.Plant: continue;
                case EnumBlockMaterial.Soil: case EnumBlockMaterial.Sand: case EnumBlockMaterial.Gravel: return p.Y;
                case EnumBlockMaterial.Stone: return rock && b.Code?.Path.StartsWith("rock-") == true ? p.Y : -1;
                default: return -1;
            }
        }
        return -1;
    }

    private string FireClaySurface(IslandJob job, FireClayPatch d, Block clay, Block clayGrass, CaveRand rand, out bool ok)
    {
        ok = false;
        var ba = sapi.World.BlockAccessor;
        int ax = job.Cx, az = job.Cz;
        bool near = false;
        if (d.Near != '\0' && job.Shape.FirstCells.TryGetValue(d.Near, out var cell)) { near = true; MarkerWorld(job, cell.X, cell.Z, out ax, out az); }
        bool shore = d.Kind == "lakeshore";
        // the crater lake lies in the ring's middle, at sea level: look there for its shore
        double reach = near ? 40 : job.LandRadius * (shore ? 0.6 : 0.8);
        int r = (int)Math.Ceiling(d.Size);

        bool Lake(int x, int z)
        {
            double ddx = x - job.Cx, ddz = z - job.Cz;
            if (ddx * ddx + ddz * ddz > Math.Pow(job.LandRadius * 0.7, 2)) return false;   // the open sea outside is no lake
            var q = new BlockPos(x, ba.GetRainMapHeightAt(x, z), z, job.Dim);
            for (int k = 0; k < 4 && q.Y >= job.SeaLevel - 1; k++, q.Y--)
                if (ba.GetBlock(q, BlockLayersAccess.Fluid).IsLiquid()) return true;
            return false;
        }

        // Score candidate centres: dry natural ground across the disc (and, on a lake shore, water beside it).
        (int X, int Z, double S) best = (0, 0, -1);
        for (int t = 0; t < (shore ? 1500 : 400); t++)
        {
            double a = rand.NextDouble() * Math.PI * 2, dist = Math.Sqrt(rand.NextDouble()) * reach;
            int cx = ax + (int)Math.Round(Math.Cos(a) * dist), cz = az + (int)Math.Round(Math.Sin(a) * dist);
            if (near && dist < 10) continue;   // not on the structure itself
            double ground = 0, water = 0;
            for (int dx = -r; dx <= r; dx += 2)
                for (int dz = -r; dz <= r; dz += 2)
                {
                    if (dx * dx + dz * dz > r * r) continue;
                    { int gt = FireClayTop(ba, cx + dx, cz + dz, job.Dim, job.SeaLevel, true); if (gt > 0 && (!shore || gt <= job.SeaLevel + 7)) ground++; }
                }
            if (shore)
            {
                for (int k = 0; k < 16; k++)
                {
                    double b = k * Math.PI / 8;
                    if (Lake(cx + (int)Math.Round(Math.Cos(b) * (r + 2)), cz + (int)Math.Round(Math.Sin(b) * (r + 2)))) water++;
                }
                if (water < 2 || water > 12) continue;   // a shore, not open water or dry land
            }
            double s = ground + water * 0.5;
            if (s > best.S) best = (cx, cz, s);
        }
        if (best.S < 3) return $"no open ground for the {d.Kind} patch.";

        int placed = 0;
        var pos = new BlockPos(0, 0, 0, job.Dim);
        for (int dx = -r - 1; dx <= r + 1; dx++)
            for (int dz = -r - 1; dz <= r + 1; dz++)
            {
                double edge = d.Size * (0.8 + 0.35 * rand.NextDouble());
                if (dx * dx + dz * dz > edge * edge) continue;
                int x = best.X + dx, z = best.Z + dz;
                int top = FireClayTop(ba, x, z, job.Dim, job.SeaLevel, true);
                if (top < 0 || (shore && top > job.SeaLevel + 7)) continue;   // a shore bed stays at the water line
                int depth = 2 + (rand.NextDouble() < 0.5 ? 1 : 0);
                for (int k = 0; k < depth; k++)
                {
                    pos.Set(x, top - k, z);
                    Block b = ba.GetBlock(pos, BlockLayersAccess.Solid);
                    if (b.BlockMaterial is not (EnumBlockMaterial.Soil or EnumBlockMaterial.Sand or EnumBlockMaterial.Gravel)
                        && !(b.BlockMaterial == EnumBlockMaterial.Stone && b.Code?.Path.StartsWith("rock-") == true)) break;
                    bool grassy = k == 0 && b.BlockMaterial == EnumBlockMaterial.Soil && b.Code?.Path.EndsWith("-none") == false;
                    ba.SetBlock((grassy ? clayGrass : clay).BlockId, pos);
                    placed++;
                }
            }
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        int y = ba.GetRainMapHeightAt(best.X, best.Z);
        ok = placed > 0;
        return $"{(shore ? "lake shore bed" : "patch")} of {placed} blocks at HUD {best.X - mx}, {y}, {best.Z - mz}.";
    }

    private string FireClayCave(IslandJob job, FireClayPatch d, Block clay, CaveRand rand, out bool ok)
    {
        ok = false;
        if (!job.Shape.FirstCells.TryGetValue(d.Near, out var cell)) return $"no cave '{d.Near}' on the map.";
        CaveDef def = job.Shape.Caves.FirstOrDefault(c => c.Gx == cell.X && c.Gz == cell.Z)?.Def;
        var steps = def == null || job.CaveSteps == null ? null : job.CaveSteps.Where(s => s.Def == def).ToList();
        if (steps == null || steps.Count < 10) return $"cave '{d.Near}' not found.";
        var ba = sapi.World.BlockAccessor;
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        var pos = new BlockPos(0, 0, 0, job.Dim);
        bool Air(int x, int y, int z) { pos.Set(x, y, z); return ba.GetBlock(pos, BlockLayersAccess.Solid).Id == 0 && ba.GetBlock(pos, BlockLayersAccess.Fluid).Id == 0; }

        // A dry stretch from halfway in to three quarters, trying spots until one has rock walls.
        for (int t = 0; t < 30; t++)
        {
            var s = steps[(int)(steps.Count * (0.45 + 0.3 * rand.NextDouble()))];
            int sx = (int)Math.Floor(s.X), sy = (int)Math.Floor(s.Y), sz = (int)Math.Floor(s.Z);
            int reach = (int)Math.Ceiling(s.R) + 3;
            var wall = new List<(BlockPos P, double D, string Rock)>();
            for (int dx = -reach; dx <= reach; dx++)
                for (int dy = -reach; dy <= reach; dy++)
                    for (int dz = -reach; dz <= reach; dz++)
                    {
                        int x = sx + dx, y = sy + dy, z = sz + dz;
                        pos.Set(x, y, z);
                        Block b = ba.GetBlock(pos, BlockLayersAccess.Solid);
                        if (b.BlockMaterial != EnumBlockMaterial.Stone || b.Code?.Path.StartsWith("rock-") != true) continue;
                        if (!(Air(x + 1, y, z) || Air(x - 1, y, z) || Air(x, y + 1, z) || Air(x, y - 1, z) || Air(x, y, z + 1) || Air(x, y, z - 1))) continue;
                        wall.Add((new BlockPos(x, y, z, job.Dim), Math.Sqrt(dx * dx + dy * dy * 1.5 + dz * dz) + rand.NextDouble() * 1.5, b.Code.Path.Substring(5)));
                    }
            if (wall.Count < d.Size * 1.5) continue;
            // the clay hugs one side of the tunnel: the wall blocks nearest a point off its centre line
            var side = wall[(int)(rand.NextDouble() * wall.Count)].P;
            var ordered = wall.OrderBy(w => w.P.DistanceTo(side) + rand.NextDouble()).ToList();
            int n = Math.Min((int)d.Size, ordered.Count);
            foreach (var w in ordered.Take(n)) ba.SetBlock(clay.BlockId, w.P);
            int coal = 0;
            if (d.Coal)
                foreach (var w in ordered.Skip(n).Take((int)(d.Size * 0.6)))
                {
                    Block ore = new[] { w.Rock, "shale", "sandstone", "claystone", "limestone" }
                        .Select(r => sapi.World.GetBlock(new AssetLocation("game", "ore-bituminouscoal-" + r))).FirstOrDefault(b => b != null);
                    if (ore == null) continue;
                    ba.SetBlock(ore.BlockId, w.P);
                    coal++;
                }
            ok = n > 0;
            return $"cave seam of {n} blocks{(coal > 0 ? $" beside {coal} of coal" : "")} at HUD {side.X - mx}, {side.Y}, {side.Z - mz}.";
        }
        return $"no rock wall in cave '{d.Near}' for the clay.";
    }

    // ── /genfireclay: existing worlds ─────────────────────────────────────
    private void StartFireClay(ICoreServerAPI api)
    {
        RegisterCmd(api, "genfireclay", name =>
            api.ChatCommands.Create(name)
                .WithDescription("Add Rustfall's fire clay (Forge Volcano, Volcano Ring lake shore, Iron Mine caves, Forest Island and its cave, Tin Island) to this world. Once per island.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("island"))
                .HandleWith(args =>
                {
                    if (_wgIslandJobs == null) return TextCommandResult.Error("This world has no plan islands.");
                    // /genfireclay <shape>: place that island's patches again (adds more clay, the old stays)
                    string only = args.Parsers[0].IsMissing ? null : (string)args[0];
                    var todo = _wgIslandJobs.Where(j => j.ShapeName != null && FireClayPatches.ContainsKey(j.ShapeName) && (only == null || j.ShapeName == only)).ToList();
                    if (only != null)
                        foreach (var j in todo)
                            for (int n = 1; n <= FireClayPatches[j.ShapeName].Length; n++)
                                sapi.WorldManager.SaveGame.StoreData($"lgFireClayDone:{j.ShapeName}@{j.Cx},{j.Cz}#{n}", false);
                    if (todo.Count == 0) return TextCommandResult.Error("None of the fire clay islands is in this world's plan.");
                    GenFireClayNext(todo, 0, new List<string>());
                    return TextCommandResult.Success($"Adding fire clay to {todo.Count} island(s); results follow in chat.");
                }));
    }

    private void GenFireClayNext(List<IslandJob> todo, int idx, List<string> report)
    {
        if (idx >= todo.Count)
        {
            sapi.BroadcastMessageToAllGroups("[genfireclay] Done." + string.Concat(report), EnumChatType.Notification);
            return;
        }
        var job = todo[idx];
        if (job.Shape == null) { GenFireClayNext(todo, idx + 1, report); return; }
        var dry = new CaveWork { DryRun = true };
        CarveCaves(job, dry);
        job.CaveSteps = dry.Steps;
        int cs = GlobalConstants.ChunkSize, lr = (int)Math.Ceiling(job.LandRadius) + 16;
        int x1 = job.Cx - lr, x2 = job.Cx + lr, z1 = job.Cz - lr, z2 = job.Cz + lr;
        foreach (var s in job.CaveSteps) { x1 = Math.Min(x1, (int)s.X - 8); x2 = Math.Max(x2, (int)s.X + 8); z1 = Math.Min(z1, (int)s.Z - 8); z2 = Math.Max(z2, (int)s.Z + 8); }
        int lcx1 = FloorDiv(x1, cs), lcz1 = FloorDiv(z1, cs), lcx2 = FloorDiv(x2, cs), lcz2 = FloorDiv(z2, cs);
        LoadChunkAreaBanded(lcx1, lcz1, lcx2, lcz2, true, () =>
        {
            string r;
            try { r = PlaceFireClay(job); }
            catch (Exception e) { r = " FAILED: " + e.Message; sapi.Logger.Error(e); }
            job.PreCx1 = lcx1; job.PreCz1 = lcz1; job.PreCx2 = lcx2; job.PreCz2 = lcz2;
            ReleaseKeptChunks(job);
            report.Add($" {job.ShapeName}:{(r.Length > 0 ? r : " already done.")}");
            sapi.Event.RegisterCallback(_ => GenFireClayNext(todo, idx + 1, report), 500);
        });
    }
}
