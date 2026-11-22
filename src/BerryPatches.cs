using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// Dense berry patches on the starter island (0.74.0): several thickets of wild berry bushes, enough to bring
// the island's bushes to `Mult` times what its regions grew, and a share of every wild bush on the island
// (old and new) starts ripe, the rest at a random stage as the game rolls it for wild bushes.
// Done once per island (lgBerriesDone:<shape>@x,z), so /genchests adds them to an older world.
public partial class LandmassGeneratorModSystem
{
    private record BerryDef(double Mult, int Patches, int Radius, double Ripe, string[] Kinds, int MinAdd);
    private static readonly Dictionary<string, BerryDef> BerryIslands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["starter_island"] = new(3.0, 6, 6, 0.5, new[] { "raspberry", "blueberry", "blackcurrant", "redcurrant", "cranberry", "strawberry" }, 60),
    };

    private string PlaceBerryPatches(IslandJob job)
    {
        if (job.ShapeName == null || !BerryIslands.TryGetValue(job.ShapeName, out var d)) return "";
        string doneKey = $"lgBerriesDone:{job.ShapeName}@{job.Cx},{job.Cz}";
        if (sapi.WorldManager.SaveGame.GetData<bool>(doneKey, false)) return "";
        var ba = sapi.World.BlockAccessor;
        var rand = new CaveRand((uint)(job.Seed * 389 + 7));
        int radius = (int)Math.Ceiling(job.LandRadius) + 8;
        var p = new BlockPos(0, 0, 0, job.Dim);

        // The bushes the regions grew, and the open fertile ground a new one could take.
        var bushes = new List<BlockPos>();
        var ground = new List<BlockPos>();
        for (int x = job.Cx - radius; x <= job.Cx + radius; x++)
            for (int z = job.Cz - radius; z <= job.Cz + radius; z++)
            {
                int top = ba.GetRainMapHeightAt(x, z);
                if (top < job.SeaLevel) continue;
                bool found = false;
                for (int y = top + 1; y >= top - 1 && !found; y--)
                {
                    p.Set(x, y, z);
                    if (ba.GetBlock(p).Code?.Path.StartsWith("fruitingbush-wild-") == true) { bushes.Add(p.Copy()); found = true; }
                }
                if (found) continue;
                p.Set(x, top, z);
                Block g = ba.GetBlock(p, BlockLayersAccess.Solid);
                if (g.BlockMaterial != EnumBlockMaterial.Soil || g.Code?.Path.StartsWith("soil-") != true) continue;
                p.Set(x, top + 1, z);
                Block above = ba.GetBlock(p);
                if (above.Id != 0 && above.BlockMaterial != EnumBlockMaterial.Plant) continue;
                if (above.Id != 0 && above.Code?.Path.StartsWith("tallgrass") != true && above.Code?.Path.StartsWith("flower") != true) continue;
                if (ba.GetBlock(p, BlockLayersAccess.Fluid).Id != 0) continue;
                ground.Add(new BlockPos(x, top + 1, z, job.Dim));
            }
        int existing = bushes.Count;
        int want = Math.Max(d.MinAdd, (int)Math.Round(existing * (d.Mult - 1)));

        // Patch centres: open ground, well apart, not on the beach (soil only) and not next to the landmarks.
        var free = new HashSet<long>(ground.Select(g => ((long)g.X << 32) ^ (uint)g.Z));
        int OpenAround(BlockPos c)
        {
            int n = 0;
            for (int dx = -d.Radius; dx <= d.Radius; dx++)
                for (int dz = -d.Radius; dz <= d.Radius; dz++)
                    if (dx * dx + dz * dz <= d.Radius * d.Radius && free.Contains(((long)(c.X + dx) << 32) ^ (uint)(c.Z + dz))) n++;
            return n;
        }
        var centres = new List<BlockPos>();
        var pool = ground.OrderBy(_ => rand.NextDouble()).ToList();
        foreach (int gap in new[] { d.Radius * 5, d.Radius * 3, d.Radius * 2 })
            foreach (var c in pool)
            {
                if (centres.Count >= d.Patches) break;
                if (centres.Any(o => o.HorDistanceSqTo(c.X, c.Z) < gap * gap)) continue;
                if (OpenAround(c) < d.Radius * d.Radius * 2) continue;   // a patch wants room (2/3 of its disc open)
                centres.Add(c);
            }
        if (centres.Count == 0) return "no open ground for berry patches.";

        int perPatch = (int)Math.Ceiling(want / (double)centres.Count), added = 0;
        foreach (var c in centres)
        {
            string kind = d.Kinds[(int)(rand.NextDouble() * d.Kinds.Length)];
            Block bush = sapi.World.GetBlock(new AssetLocation("game", $"fruitingbush-wild-{kind}-free"));
            if (bush == null) continue;
            var cells = ground.Where(g => g.HorDistanceSqTo(c.X, c.Z) <= d.Radius * d.Radius).OrderBy(g => g.HorDistanceSqTo(c.X, c.Z) + rand.NextDouble() * d.Radius * 4).ToList();
            int here = 0;
            foreach (var g in cells)
            {
                if (here >= perPatch || added >= want) break;
                long k = ((long)g.X << 32) ^ (uint)g.Z;
                if (!free.Contains(k) || rand.NextDouble() > 0.75) continue;
                ba.SetBlock(bush.BlockId, g);
                free.Remove(k);
                bushes.Add(g.Copy());
                here++; added++;
            }
        }

        // Every wild bush gets a proper wild start (health, stage, traits), and a share of them starts ripe.
        int ripe = 0, live = 0;
        foreach (var b in bushes)
        {
            Block blk = ba.GetBlock(b);
            if (blk.Code?.Path.StartsWith("fruitingbush-wild-") != true) continue;
            if (ba.GetBlockEntity(b) == null && blk.EntityClass != null) ba.SpawnBlockEntity(blk.EntityClass, b);
            var beh = ba.GetBlockEntity(b)?.GetBehavior<BEBehaviorFruitingBush>();
            if (beh == null) continue;
            beh.OnBlockPlaced(null);
            if (rand.NextDouble() < d.Ripe)
            {
                beh.BState.Growthstate = EnumFruitingBushGrowthState.Ripe;
                beh.BState.TransitionHoursLeft = beh.GetHoursForNextStage();
                ripe++;
            }
            ba.GetBlockEntity(b).MarkDirty(true);
            live++;
        }
        sapi.WorldManager.SaveGame.StoreData(doneKey, true);
        string r = $"{centres.Count} berry patch(es): {existing} bushes grew, {added} added, {ripe} of {live} start ripe.";
        sapi.Logger.Notification("[berries] {0}: {1}", job.ShapeName, r);
        return r;
    }
}
