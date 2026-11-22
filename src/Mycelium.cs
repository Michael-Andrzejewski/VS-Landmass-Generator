using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// Mushrooms that grow back (0.74.1). Vanilla worldgen places a mycelium block entity in the soil under each
// wild mushroom patch; it regrows a patch of 2 to 12 mushrooms round it (within 7 blocks) every few weeks of
// warm weather, which is how mushrooms come back after harvesting. The island decorator scatters mushrooms as
// plain blocks, so about a tenth of them (at least 4 per island) get a mycelium here, once the island's
// decoration is in.
public partial class LandmassGeneratorModSystem
{
    private const double MyceliumShare = 1.0;      // every scattered mushroom is a candidate...
    private const double MyceliumPick = 0.10;      // ...and about a tenth of them get a mycelium,
    private const int MyceliumMin = 4;             // but at least this many on an island with mushrooms

    private string PlaceMycelium(IslandJob job)
    {
        if (job.Mycelium == null || job.Mycelium.Count == 0) return "";
        var ba = sapi.World.BlockAccessor;
        var rand = new LCGRandom(job.Seed * 57 + 3);
        int placed = 0;
        int want = Math.Max(MyceliumMin, (int)Math.Round(job.Mycelium.Count * MyceliumPick));
        var order = new System.Collections.Generic.List<(BlockPos Pos, int BlockId)>(job.Mycelium);
        var shuffle = new Random((int)(job.Seed * 31 + 11));
        for (int i = order.Count - 1; i > 0; i--) { int j = shuffle.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
        foreach (var (pos, blockId) in order)
        {
            if (placed >= want) break;
            if (ba.GetBlock(pos.UpCopy()).Id != blockId) continue;                     // the mushroom is gone
            Block host = ba.GetBlock(pos);
            if (!host.HasBehavior<BlockBehaviorMyceliumHost>()) continue;               // not soil that hosts mycelium
            if (ba.GetBlockEntity(pos) != null) continue;
            rand.InitPositionSeed(pos.X, pos.Z);
            ba.SpawnBlockEntity("Mycelium", pos);
            if (ba.GetBlockEntity(pos) is BlockEntityMycelium m && sapi.World.Blocks[blockId] is BlockMushroom mb)
            {
                m.OnGenerated(ba, rand, mb);
                placed++;
            }
        }
        job.Mycelium.Clear();
        return placed > 0 ? $"{placed} mushroom mycelium(s)." : "";
    }
}
