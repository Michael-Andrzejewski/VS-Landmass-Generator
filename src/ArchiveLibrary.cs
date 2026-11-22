using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// The Resonance Archives' library maps (0.74.4): once the game has generated the Archives and a player comes
// near (or /genstorychests runs), several chests go in beside the library's bookshelves, each with ONE island
// map and some library loot, spread apart; and display cases with a map each. More ways on from the Archives,
// so a player who misses one map still finds another. Once per world (lgArchiveLibraryDone).
public partial class LandmassGeneratorModSystem
{
    private const string ArchiveCode = "resonancearchive";
    // one map per chest: the Lazaret (the main route's next stop) and five more islands
    private static readonly string[] ArchiveChestMaps = { "lazaret", "forge", "swamplands", "craterlake", "blackfen", "ironmine" };
    // spare copies in display cases
    private static readonly string[] ArchiveCaseMaps = { "forge", "craterlake", "ironmine" };
    private bool _archiveLoading;

    private void ArchiveLibraryTick(bool force)
    {
        if (sapi.WorldManager.SaveGame.GetData<bool>("lgArchiveLibraryDone", false)) return;
        if (!sapi.WorldManager.SaveGame.WorldConfiguration.GetBool("rustfallWorld", false)) return;
        var genStory = sapi.ModLoader.GetModSystem<GenStoryStructures>();
        var loc = genStory?.Structures.Get(ArchiveCode);
        if (loc == null || !loc.DidGenerate || loc.Location == null) return;
        var area = loc.Location;
        bool near = sapi.World.AllOnlinePlayers.Any(pl => pl.Entity != null
            && pl.Entity.Pos.X > area.X1 - 200 && pl.Entity.Pos.X < area.X2 + 200
            && pl.Entity.Pos.Z > area.Z1 - 200 && pl.Entity.Pos.Z < area.Z2 + 200);
        if (!near && !force) return;

        int cs = GlobalConstants.ChunkSize, margin = 8;
        int cx1 = (area.X1 - margin) / cs, cx2 = (area.X2 + margin) / cs, cz1 = (area.Z1 - margin) / cs, cz2 = (area.Z2 + margin) / cs;
        int cyMax = sapi.WorldManager.MapSizeY / cs;
        for (int cx = cx1; cx <= cx2; cx++)
            for (int cz = cz1; cz <= cz2; cz++)
                for (int cy = 0; cy < cyMax; cy++)
                    if (sapi.WorldManager.GetChunk(cx, cy, cz) == null)
                    {
                        if (force && !_archiveLoading)
                        {
                            _archiveLoading = true;
                            sapi.Logger.Notification("[archives] loading the Archives' area for its library maps");
                            LoadChunkAreaBanded(cx1, cz1, cx2, cz2, false, () => { _archiveLoading = false; ArchiveLibraryTick(true); });
                        }
                        return;
                    }

        var ba = sapi.World.BlockAccessor;
        bool Open(BlockPos at) => sapi.World.Claims.Get(at)?.All(cl => cl.AllowUseEveryone) ?? true;
        bool Free(BlockPos p)
        {
            Block b = ba.GetBlock(p);
            return (b.Id == 0 || (b.Replaceable >= 6000 && !b.IsLiquid())) && ba.GetBlock(p, BlockLayersAccess.Fluid).Id == 0;
        }
        bool Floor(BlockPos p) { var d = p.DownCopy(); return ba.GetBlock(d).SideSolid[BlockFacing.UP.Index]; }

        // Floor cells beside a bookshelf: free, standing room above, a solid floor under, not in a locked claim.
        var spots = new List<BlockPos>();
        var seen = new HashSet<long>();
        var p = new BlockPos(0, 0, 0, 0);
        int shelves = 0;
        for (int x = area.X1; x <= area.X2; x++)
            for (int z = area.Z1; z <= area.Z2; z++)
                for (int y = Math.Max(1, area.Y1); y <= area.Y2; y++)
                {
                    p.Set(x, y, z);
                    string path = ba.GetBlock(p).Code?.Path;
                    if (path == null || !(path.Contains("bookshel") || path.StartsWith("bookshelves"))) continue;
                    shelves++;
                    foreach (var f in BlockFacing.HORIZONTALS)
                    {
                        var c = new BlockPos(x + f.Normali.X, y, z + f.Normali.Z, 0);
                        long key = ((long)c.X << 36) ^ ((long)c.Y << 24) ^ (uint)c.Z;
                        if (!seen.Add(key)) continue;
                        if (!Free(c) || !Free(c.UpCopy()) || !Floor(c) || !Open(c)) continue;
                        spots.Add(c);
                    }
                }
        if (spots.Count == 0)
        {
            sapi.Logger.Warning("[archives] {0} bookshelf block(s) in the Archives but no free floor beside one; library maps not placed yet", shelves);
            if (force) sapi.WorldManager.SaveGame.StoreData("lgArchiveLibraryDone", false);
            return;
        }

        // Spread them out: farthest-point picking from a seeded start.
        var rand = new CaveRand((uint)(sapi.World.Seed * 7 + 1337));
        var picked = new List<BlockPos> { spots[(int)(rand.NextDouble() * spots.Count)] };
        int want = ArchiveChestMaps.Length + ArchiveCaseMaps.Length;
        while (picked.Count < want && picked.Count < spots.Count)
        {
            BlockPos best = null; double bestD = -1;
            foreach (var s in spots)
            {
                double d = picked.Min(o => (double)o.DistanceSqTo(s.X, s.Y, s.Z));
                if (d > bestD) { bestD = d; best = s; }
            }
            if (best == null || bestD < 4) break;
            picked.Add(best);
        }

        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        var notes = new List<string>();
        int i = 0;
        foreach (string map in ArchiveChestMaps)
        {
            if (i >= picked.Count) break;
            var contents = new List<string> { "landmassgenerator:islandmap-" + map };
            contents.AddRange(RollTable("alchemist", rand, 3 + (int)(rand.NextDouble() * 3)));
            if (PlaceChestAt(picked[i], "normal-aged", contents)) notes.Add($"chest with the {map} map at HUD {picked[i].X - mx}, {picked[i].Y}, {picked[i].Z - mz}");
            i++;
        }
        Block caseBlock = sapi.World.GetBlock(new AssetLocation("game", "displaycase-aged1"));
        foreach (string map in ArchiveCaseMaps)
        {
            if (i >= picked.Count || caseBlock == null) break;
            var at = picked[i++];
            ba.SetBlock(caseBlock.BlockId, at);
            if (ba.GetBlockEntity(at) == null && caseBlock.EntityClass != null) ba.SpawnBlockEntity(caseBlock.EntityClass, at);
            Item mapItem = sapi.World.GetItem(new AssetLocation("landmassgenerator", "islandmap-" + map));
            if (ba.GetBlockEntity(at) is BlockEntityDisplayCase dc && mapItem != null)
            {
                dc.Inventory[0].Itemstack = new ItemStack(mapItem);
                dc.Inventory[0].MarkDirty();
                dc.MarkDirty(true);
                notes.Add($"display case with the {map} map at HUD {at.X - mx}, {at.Y}, {at.Z - mz}");
            }
        }
        sapi.WorldManager.SaveGame.StoreData("lgArchiveLibraryDone", true);
        sapi.Logger.Notification("[archives] library maps ({0} bookshelf blocks, {1} floor spots): {2}", shelves, spots.Count, string.Join("; ", notes));
    }
}
