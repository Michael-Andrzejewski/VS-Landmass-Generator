using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace LandmassGenerator;

// Wild beehives (0.74.9). The game hangs wild hives under trees while it fills new land with plants, a step that
// runs while a Rustfall world is still open ocean, so the hand-built islands had none (and without a wild hive
// there is no swarm to fill a skep). These islands get a few, hung by the hive block's own worldgen placement
// (under a log or branchy leaves, 2 to 7 blocks below the canopy top), spread out over the island.
// New worlds: as each island is decorated. Existing worlds: /genbeehives [island]. Once per island.
public partial class LandmassGeneratorModSystem
{
    // By size: Blackfen (260 across) has 4, the rest in proportion to their width (Michael, 2026-11-24).
    private static readonly Dictionary<string, int> WildHiveIslands = new()
    {
        ["blackfen_island"] = 4,          // 260
        ["granary_lush"] = 2,             // 150
        ["granary_temperate"] = 2,        // 150, the Meadow Granary
        ["dangerous_swamplands"] = 7,     // 480
        ["volcano_ring"] = 11,            // 700, the crater lake island
    };

    private string PlaceWildHives(IslandJob job)
    {
        if (job.ShapeName == null || !WildHiveIslands.TryGetValue(job.ShapeName, out int want)) return "";
        string doneKey = $"lgWildHivesDone:{job.ShapeName}@{job.Cx},{job.Cz}";
        if (sapi.WorldManager.SaveGame.GetData<bool>(doneKey, false)) return "";
        var hives = new[] { "wildbeehive-medium", "wildbeehive-large" }.Select(c => sapi.World.GetBlock(new AssetLocation("game", c))).Where(b => b != null).ToArray();
        if (hives.Length == 0) return " no wild beehive block in this game.";
        var ba = sapi.World.BlockAccessor;
        var rand = new LCGRandom(sapi.World.Seed + job.Seed * 31 + 7);
        var shuffle = new CaveRand((uint)(job.Seed * 389 + 17));
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        var placed = new List<BlockPos>();
        double minGap = Math.Max(14, job.LandRadius / 4);
        var pos = new BlockPos(0, 0, 0, job.Dim);
        for (int t = 0; t < 4000 && placed.Count < want; t++)
        {
            double a = shuffle.NextDouble() * Math.PI * 2, d = Math.Sqrt(shuffle.NextDouble()) * job.LandRadius * 0.85;
            int x = job.Cx + (int)Math.Round(Math.Cos(a) * d), z = job.Cz + (int)Math.Round(Math.Sin(a) * d);
            if (placed.Any(p => (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z) < minGap * minGap)) continue;
            // the canopy top: the first leaf or log from above (the rain map looks through leaves)
            int ground = ba.GetRainMapHeightAt(x, z);
            if (ground <= job.SeaLevel + 1) continue;
            int top = -1;
            for (int y = Math.Min(sapi.WorldManager.MapSizeY - 2, ground + 40); y > ground; y--)
            {
                pos.Set(x, y, z);
                var m = ba.GetBlock(pos).BlockMaterial;
                if (m == EnumBlockMaterial.Leaves || m == EnumBlockMaterial.Wood) { top = y; break; }
            }
            if (top < 0 || top - ground < 4) continue;   // no tree here, or one too small to hang a hive under
            pos.Set(x, top + 1, z);
            Block hive = hives[(int)(shuffle.NextDouble() * hives.Length)];
            if (!hive.TryPlaceBlockForWorldGen(ba, pos, BlockFacing.UP, rand)) continue;
            // find where it went (2 to 8 blocks under the spot)
            for (int dy = 1; dy <= 10; dy++)
            {
                var q = new BlockPos(x, top + 1 - dy, z, job.Dim);
                if (ba.GetBlock(q).Code?.Path.StartsWith("wildbeehive") == true) { placed.Add(q); break; }
            }
        }
        if (placed.Count > 0) sapi.WorldManager.SaveGame.StoreData(doneKey, true);
        string msg = placed.Count == 0 ? "no tree to hang a hive under." :
            $"{placed.Count} wild beehive(s) at HUD " + string.Join("; ", placed.Select(p => $"{p.X - mx}, {p.Y}, {p.Z - mz}")) + ".";
        sapi.Logger.Notification("[beehives] {0}: {1}", job.ShapeName, msg);
        return " Beehives: " + msg;
    }

    private void StartWildHives(ICoreServerAPI api)
    {
        RegisterCmd(api, "genbeehives", name =>
            api.ChatCommands.Create(name)
                .WithDescription("Hang Rustfall's wild beehives (Blackfen, the Lush and Meadow Granaries, the Dangerous Swamplands, the Volcano Ring) in this world. Once per island; naming an island places its hives again.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("island"))
                .HandleWith(args =>
                {
                    if (_wgIslandJobs == null) return TextCommandResult.Error("This world has no plan islands.");
                    string only = args.Parsers[0].IsMissing ? null : (string)args[0];
                    var todo = _wgIslandJobs.Where(j => j.ShapeName != null && WildHiveIslands.ContainsKey(j.ShapeName) && (only == null || j.ShapeName == only)).ToList();
                    if (todo.Count == 0) return TextCommandResult.Error("None of the beehive islands is in this world's plan.");
                    if (only != null) foreach (var j in todo) sapi.WorldManager.SaveGame.StoreData($"lgWildHivesDone:{j.ShapeName}@{j.Cx},{j.Cz}", false);
                    GenHivesNext(todo, 0, new List<string>());
                    return TextCommandResult.Success($"Hanging beehives on {todo.Count} island(s); results follow in chat.");
                }));
    }

    private void GenHivesNext(List<IslandJob> todo, int idx, List<string> report)
    {
        if (idx >= todo.Count)
        {
            sapi.BroadcastMessageToAllGroups("[genbeehives] Done." + string.Concat(report), EnumChatType.Notification);
            return;
        }
        var job = todo[idx];
        int cs = GlobalConstants.ChunkSize, lr = (int)Math.Ceiling(job.LandRadius) + 8;
        int lcx1 = FloorDiv(job.Cx - lr, cs), lcz1 = FloorDiv(job.Cz - lr, cs), lcx2 = FloorDiv(job.Cx + lr, cs), lcz2 = FloorDiv(job.Cz + lr, cs);
        LoadChunkAreaBanded(lcx1, lcz1, lcx2, lcz2, true, () =>
        {
            string r;
            try { r = PlaceWildHives(job); }
            catch (Exception e) { r = " FAILED: " + e.Message; sapi.Logger.Error(e); }
            job.PreCx1 = lcx1; job.PreCz1 = lcz1; job.PreCx2 = lcx2; job.PreCz2 = lcz2;
            ReleaseKeptChunks(job);
            report.Add($" {job.ShapeName}:{(r.Length > 0 ? r : " already done.")}");
            sapi.Event.RegisterCallback(_ => GenHivesNext(todo, idx + 1, report), 500);
        });
    }
}
