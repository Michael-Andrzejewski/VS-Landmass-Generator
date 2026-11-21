using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace LandmassGenerator;

// Temporal Growth Gears from the Timewood Tree mod on the Rustfall farm islands (0.73.3):
//
// - The starter island's landmark oak has a Gear grown into its trunk, on the side of Jonas' letter.
// - Each granary island has a small Timewood Tree (one Gear inside) on open ground near the granary.
//
// A Gear makes crops on farmland around it grow faster (Timewood's GrowthMultiplier, 5x by default).
// Without the Timewood mod nothing is placed. Done once per island (lgTimewoodDone:<shape>@x,z), apart
// from the other Rustfall props, so /genchests adds them to a world built before 0.73.3.
public partial class LandmassGeneratorModSystem
{
    private const string TimewoodCoreCode = "timewood:timewoodcore";

    // The landmark tree's marker, how many blocks above its ground the Gear goes, and which side of the
    // trunk it faces out of.
    private record TrunkGearDef(char Tree, int Up, string Side);
    private static readonly Dictionary<string, TrunkGearDef> TrunkGears = new(StringComparer.OrdinalIgnoreCase)
    {
        ["starter_island"] = new('O', 3, "east"),   // the side of Jonas' letter (chest J beside=O side=east)
    };

    // The structure marker the tree stands near, and how far from it (blocks) the trunk may be.
    private record TimewoodTreeDef(char Near, int MinDist, int MaxDist);
    private static readonly Dictionary<string, TimewoodTreeDef> TimewoodTrees = new(StringComparer.OrdinalIgnoreCase)
    {
        ["granary_lush"] = new('G', 14, 50),
        ["granary_arid"] = new('G', 14, 50),
        ["granary_cold"] = new('G', 14, 50),
        ["granary_temperate"] = new('G', 14, 50),
    };

    private string PlaceTimewood(IslandJob job)
    {
        if (job.ShapeName == null || job.Shape == null) return "";
        bool gear = TrunkGears.TryGetValue(job.ShapeName, out var gd);
        bool tree = TimewoodTrees.TryGetValue(job.ShapeName, out var td);
        if (!gear && !tree) return "";
        string doneKey = $"lgTimewoodDone:{job.ShapeName}@{job.Cx},{job.Cz}";
        if (sapi.WorldManager.SaveGame.GetData<bool>(doneKey, false)) return "";
        if (sapi.World.GetBlock(new AssetLocation(TimewoodCoreCode)) == null)
        {
            sapi.Logger.Notification("[timewood] {0}: the Timewood Tree mod is not installed, no Gear placed", job.ShapeName);
            return "";
        }
        var notes = new List<string>();
        bool ok = true;
        try
        {
            if (gear) { string n = PlaceTrunkGear(job, gd, out bool g); notes.Add(n); ok &= g; }
            if (tree) { string n = PlaceTimewoodTree(job, td, out bool t); notes.Add(n); ok &= t; }
        }
        catch (Exception e)
        {
            sapi.Logger.Error(e);
            return " Timewood FAILED: " + e.Message;
        }
        if (ok) sapi.WorldManager.SaveGame.StoreData(doneKey, true);
        string r = string.Concat(notes.Where(s => s.Length > 0).Select(s => " " + s));
        sapi.Logger.Notification("[timewood] {0}:{1}", job.ShapeName, r);
        return r;
    }

    // ── a Gear in the landmark tree's trunk ─────────────────────────────

    private string PlaceTrunkGear(IslandJob job, TrunkGearDef d, out bool ok)
    {
        ok = false;
        if (!job.Shape.FirstCells.TryGetValue(d.Tree, out var cell)) return $"no tree '{d.Tree}' for the Gear.";
        MarkerWorld(job, cell.X, cell.Z, out int x, out int z);
        var ba = sapi.World.BlockAccessor;
        var p = new BlockPos(x, 0, z, job.Dim);
        // the ground under the trunk
        int g0 = -1;
        for (int y = Math.Min(sapi.WorldManager.MapSizeY - 3, job.SeaLevel + job.DomeHeight + 40); y > job.SeaLevel - 3; y--)
        {
            p.Y = y;
            Block b = ba.GetBlock(p, BlockLayersAccess.SolidBlocks);
            if (b == null || b.Id == 0 || b.BlockMaterial is EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves or EnumBlockMaterial.Wood) continue;
            g0 = y; break;
        }
        if (g0 < 0) return "no ground under the landmark tree.";
        int gy = g0 + d.Up;
        p.Y = gy;
        if (ba.GetBlock(p, BlockLayersAccess.SolidBlocks)?.BlockMaterial != EnumBlockMaterial.Wood)
            return $"no trunk {d.Up} blocks up the landmark tree.";
        // walk out to the trunk's outer log on that side, so the Gear shows
        int sx = d.Side == "east" ? 1 : d.Side == "west" ? -1 : 0;
        int sz = d.Side == "south" ? 1 : d.Side == "north" ? -1 : 0;
        for (int k = 1; k <= 6; k++)
        {
            p.Set(x + sx, gy, z + sz);
            if (ba.GetBlock(p, BlockLayersAccess.SolidBlocks)?.BlockMaterial != EnumBlockMaterial.Wood) break;
            x += sx; z += sz;
        }
        var at = new BlockPos(x, gy, z, job.Dim);
        SetTimewoodCore(at);
        ok = true;
        return $"a Temporal Growth Gear in the landmark tree at HUD {x - sapi.WorldManager.MapSizeX / 2}, {gy}, {z - sapi.WorldManager.MapSizeZ / 2}.";
    }

    private void SetTimewoodCore(BlockPos at)
    {
        var ba = sapi.World.BlockAccessor;
        Block core = sapi.World.GetBlock(new AssetLocation(TimewoodCoreCode));
        ba.SetBlock(core.BlockId, at);
        if (ba.GetBlockEntity(at) == null && core.EntityClass != null) ba.SpawnBlockEntity(core.EntityClass, at);
        ba.MarkBlockDirty(at);
    }

    // ── a small Timewood Tree near the granary ──────────────────────────

    private string PlaceTimewoodTree(IslandJob job, TimewoodTreeDef d, out bool ok)
    {
        ok = false;
        ModSystem gen = sapi.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "TimewoodTree.TimewoodTreeGen");
        MethodInfo place = gen?.GetType().GetMethod("PlaceTreeAt", new[] { typeof(BlockPos), typeof(bool), typeof(string).MakeByRefType() });
        if (place == null) return "this Timewood version cannot place a tree.";

        int cx = job.Cx, cz = job.Cz;
        if (job.Shape.FirstCells.TryGetValue(d.Near, out var cell)) MarkerWorld(job, cell.X, cell.Z, out cx, out cz);
        var rand = new CaveRand((uint)(job.Seed * 613 + 29));
        _twReject.Clear();
        var spots = new List<(int X, int Y, int Z, double Key)>();
        // Strict first (the whole footprint level and bare); a terraced farm island may have no such
        // spot, so then a smaller patch round the trunk, then any open level ground.
        foreach (var (half, slope) in new[] { (7, 2), (4, 3), (0, 0) })
        {
            for (int dist = d.MinDist; dist <= d.MaxDist; dist += 3)
                for (int a = 0; a < 24; a++)
                {
                    double ang = a * Math.PI / 12 + rand.NextDouble() * 0.2;
                    int x = cx + (int)Math.Round(Math.Cos(ang) * dist), z = cz + (int)Math.Round(Math.Sin(ang) * dist);
                    if (!TimewoodFootprintClear(job, x, z, half, slope, out int y)) continue;
                    spots.Add((x, y, z, dist + rand.NextDouble() * 6));
                }
            if (spots.Count > 0) break;
        }
        if (spots.Count == 0)
            return "no open ground near the granary for a Timewood Tree (rejected: " + string.Join(", ", _twReject.Select(kv => kv.Key + " " + kv.Value)) + ").";
        var s = spots.OrderBy(t => t.Key).First();
        object[] args = { new BlockPos(s.X, s.Y + 1, s.Z, job.Dim), false, null };
        bool placed = (bool)place.Invoke(gen, args);
        if (!placed) return "Timewood Tree not placed: " + args[2];
        ok = true;
        return $"a Timewood Tree at HUD {s.X - sapi.WorldManager.MapSizeX / 2}, {s.Y + 1}, {s.Z - sapi.WorldManager.MapSizeZ / 2}.";
    }

    // The small tree is 12x14x13: the ground within `half` blocks of the trunk must be natural, within
    // `slope` blocks of level, with nothing built or grown on it (no walls, logs or other structures),
    // and not in water.
    private readonly Dictionary<string, int> _twReject = new();
    private bool TwReject(string why)
    {
        if (why.StartsWith("above:")) why = "above:" + why.Substring(6).Split('-')[0];
        _twReject[why] = _twReject.TryGetValue(why, out int n) ? n + 1 : 1;
        return false;
    }

    private bool TimewoodFootprintClear(IslandJob job, int x, int z, int half, int slope, out int y)
    {
        y = 0;
        if (!TranslocatorGround(job.SeaLevel, job.Dim, x, z, false, out y)) return TwReject("ground");
        var ba = sapi.World.BlockAccessor;
        var p = new BlockPos(0, 0, 0, job.Dim);
        for (int dx = -half; dx <= half; dx += 2)
            for (int dz = -half; dz <= half; dz += 2)
            {
                int h = ba.GetRainMapHeightAt(x + dx, z + dz);
                p.Set(x + dx, h, z + dz);
                Block top = ba.GetBlock(p, BlockLayersAccess.Solid);
                if (top.BlockMaterial == EnumBlockMaterial.Leaves || top.BlockMaterial == EnumBlockMaterial.Wood) return TwReject("tree");
                // walk down through plants to the ground
                while (h > job.SeaLevel && (top.Id == 0 || top.BlockMaterial == EnumBlockMaterial.Plant))
                {
                    h--; p.Y = h; top = ba.GetBlock(p, BlockLayersAccess.Solid);
                }
                if (Math.Abs(h - y) > slope) return TwReject("slope");
                if (!NaturalGround(top)) return TwReject("built");
                if (ba.GetBlock(p, BlockLayersAccess.Fluid).Id != 0) return TwReject("water");
                for (int up = 1; up <= 8; up++)
                {
                    p.Y = h + up;
                    Block b = ba.GetBlock(p, BlockLayersAccess.Solid);
                    if (b.Id != 0 && b.BlockMaterial != EnumBlockMaterial.Plant && b.Replaceable < 6000) return TwReject("above:" + b.Code?.Path);
                }
            }
        return true;
    }
}
