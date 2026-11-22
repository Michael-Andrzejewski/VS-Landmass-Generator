using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// Spare island maps (0.74.4, 0.74.5): more than one way on from a place, one map per chest, so a player who
// misses one map still finds another. Some maps sit in display cases or lie flat on the floor instead of in
// a chest.
//
// - On islands: shape-file loot lines (`loot <table> 1 ... with=<map> [show=case|ground]`), see LootPass.cs;
//   ShowItem below places the shown ones.
// - At story locations (the game builds those): StoryCaches below, placed once the location has generated
//   and a player comes near (or /genstorychests runs). Where: beside the bookshelves (the Archives' library),
//   on floors under a roof inside the structure, or on open ground in its outskirts, outside any claim that
//   locks players out (Nadiya's village). Once per location (lgStoryCacheDone:<code>).
public partial class LandmassGeneratorModSystem
{
    private record StoryCache(string Code, string Anchor, string Table, string ChestType, (string Map, string Style)[] Items);

    private static readonly StoryCache[] StoryCaches =
    {
        new("resonancearchive", "bookshelf", "alchemist", "normal-aged", new[]
        {
            ("lazaret", "chest"), ("forge", "chest"), ("swamplands", "chest"), ("craterlake", "chest"), ("blackfen", "chest"), ("ironmine", "chest"),
            ("forge", "case"), ("craterlake", "case"), ("ironmine", "case"), ("swamplands", "ground"), ("blackfen", "ground"),
        }),
        new("lazaret", "inside", "ruin", "normal-aged", new[]
        {
            ("blackfen", "chest"), ("saltteeth", "ground"), ("swamplands", "case"), ("granarytemperate", "chest"),
        }),
        new("village", "outskirts", "ruin", "collapsed", new[]
        {
            ("fortress", "chest"), ("lighthouse", "chest"), ("forge", "chest"), ("wreckage", "chest"), ("chainfield", "chest"),
            ("granary", "chest"), ("divingbell", "chest"), ("tobiascave", "chest"),
            ("granaryarid", "chest"), ("granarycold", "chest"), ("granarytemperate", "chest"),
        }),
    };
    private readonly HashSet<string> _cacheLoading = new();

    // ── a map on show: in a display case, or lying on the floor ─────────

    private bool ShowItem(BlockPos at, string show, string code, CaveRand rand)
    {
        var ba = sapi.World.BlockAccessor;
        var loc = new AssetLocation(code);
        CollectibleObject coll = (CollectibleObject)sapi.World.GetItem(loc) ?? sapi.World.GetBlock(loc);
        if (coll == null) return false;
        var stack = new ItemStack(coll);
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        if (show == "case")
        {
            Block caseBlock = sapi.World.GetBlock(new AssetLocation("game", "displaycase-aged" + (1 + (int)(rand.NextDouble() * 4))))
                ?? sapi.World.GetBlock(new AssetLocation("game", "displaycase-aged1"));
            if (caseBlock == null) return false;
            ba.SetBlock(caseBlock.BlockId, at);
            if (ba.GetBlockEntity(at) == null && caseBlock.EntityClass != null) ba.SpawnBlockEntity(caseBlock.EntityClass, at);
            if (ba.GetBlockEntity(at) is not BlockEntityDisplayCase dc) return false;
            dc.Inventory[0].Itemstack = stack;
            dc.Inventory[0].MarkDirty();
            dc.MarkDirty(true);
        }
        else
        {
            Block gs = sapi.World.GetBlock(new AssetLocation("game", "groundstorage"));
            var props = coll.GetBehavior<CollectibleBehaviorGroundStorable>()?.StorageProps;
            if (gs == null || props == null) return false;
            ba.SetBlock(gs.BlockId, at);
            if (ba.GetBlockEntity(at) == null && gs.EntityClass != null) ba.SpawnBlockEntity(gs.EntityClass, at);
            if (ba.GetBlockEntity(at) is not BlockEntityGroundStorage g) return false;
            g.ForceStorageProps(props);
            g.Inventory[0].Itemstack = stack;
            g.Inventory[0].MarkDirty();
            g.MarkDirty(true);
        }
        sapi.Logger.Notification("[landmassgenerator] map on show ({0}) at HUD {1}, {2}, {3}: {4}", show, at.X - mx, at.Y, at.Z - mz, code);
        return true;
    }

    // ── story locations ─────────────────────────────────────────────────

    private void StoryCachesTick(bool force)
    {
        if (!sapi.WorldManager.SaveGame.WorldConfiguration.GetBool("rustfallWorld", false)) return;
        var genStory = sapi.ModLoader.GetModSystem<GenStoryStructures>();
        if (genStory == null) return;
        foreach (var sc in StoryCaches)
        {
            try { StoryCacheOne(genStory, sc, force); }
            catch (Exception e) { sapi.Logger.Error(e); }
        }
    }

    private void StoryCacheOne(GenStoryStructures genStory, StoryCache sc, bool force)
    {
        string doneKey = "lgStoryCacheDone:" + sc.Code;
        if (sapi.WorldManager.SaveGame.GetData<bool>(doneKey, false)) return;
        var loc = genStory.Structures.Get(sc.Code);
        if (loc == null || !loc.DidGenerate || loc.Location == null) return;
        var area = loc.Location;
        int reach = sc.Anchor == "outskirts" ? 48 : 8;
        bool near = sapi.World.AllOnlinePlayers.Any(pl => pl.Entity != null
            && pl.Entity.Pos.X > area.X1 - 200 && pl.Entity.Pos.X < area.X2 + 200
            && pl.Entity.Pos.Z > area.Z1 - 200 && pl.Entity.Pos.Z < area.Z2 + 200);
        if (!near && !force) return;

        int cs = GlobalConstants.ChunkSize;
        int cx1 = (area.X1 - reach) / cs, cx2 = (area.X2 + reach) / cs, cz1 = (area.Z1 - reach) / cs, cz2 = (area.Z2 + reach) / cs;
        int cyMax = sapi.WorldManager.MapSizeY / cs;
        for (int cx = cx1; cx <= cx2; cx++)
            for (int cz = cz1; cz <= cz2; cz++)
                for (int cy = 0; cy < cyMax; cy++)
                    if (sapi.WorldManager.GetChunk(cx, cy, cz) == null)
                    {
                        if (force && _cacheLoading.Add(sc.Code))
                        {
                            sapi.Logger.Notification("[maps] loading the area of {0} for its spare maps", sc.Code);
                            LoadChunkAreaBanded(cx1, cz1, cx2, cz2, false, () => { _cacheLoading.Remove(sc.Code); StoryCacheOne(genStory, sc, true); });
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
        bool Floor(BlockPos p) => ba.GetBlock(p.DownCopy()).SideSolid[BlockFacing.UP.Index];
        bool Roofed(BlockPos p)
        {
            var q = p.Copy();
            for (int up = 2; up <= 10; up++) { q.Y = p.Y + up; if (ba.GetBlock(q).SideSolid[BlockFacing.DOWN.Index]) return true; }
            return false;
        }

        var spots = new List<BlockPos>();
        var seen = new HashSet<long>();
        void Try(BlockPos c)
        {
            long key = ((long)c.X << 36) ^ ((long)c.Y << 24) ^ (uint)c.Z;
            if (!seen.Add(key)) return;
            if (!Free(c) || !Free(c.UpCopy()) || !Floor(c) || !Open(c)) return;
            spots.Add(c);
        }
        var p = new BlockPos(0, 0, 0, 0);
        if (sc.Anchor == "bookshelf")
        {
            for (int x = area.X1; x <= area.X2; x++)
                for (int z = area.Z1; z <= area.Z2; z++)
                    for (int y = Math.Max(1, area.Y1); y <= area.Y2; y++)
                    {
                        p.Set(x, y, z);
                        string path = ba.GetBlock(p).Code?.Path;
                        if (path == null || !path.Contains("bookshel")) continue;
                        foreach (var f in BlockFacing.HORIZONTALS) Try(new BlockPos(x + f.Normali.X, y, z + f.Normali.Z, 0));
                    }
        }
        else if (sc.Anchor == "inside")
        {
            for (int x = area.X1 + 1; x < area.X2; x++)
                for (int z = area.Z1 + 1; z < area.Z2; z++)
                    for (int y = Math.Max(2, area.Y1); y <= area.Y2; y++)
                    {
                        var c = new BlockPos(x, y, z, 0);
                        if (Free(c) && Floor(c) && Roofed(c)) Try(c);
                    }
        }
        else   // outskirts: open natural ground 8 to 40 blocks outside the structure's box
        {
            for (int d = 8; d <= 40; d += 4)
            {
                int x1 = area.X1 - d, x2 = area.X2 + d, z1 = area.Z1 - d, z2 = area.Z2 + d;
                for (int x = x1; x <= x2; x += 3) { Ground(x, z1); Ground(x, z2); }
                for (int z = z1; z <= z2; z += 3) { Ground(x1, z); Ground(x2, z); }
            }
            void Ground(int x, int z)
            {
                if (!TranslocatorGround(sapi.World.SeaLevel, 0, x, z, false, out int y)) return;
                Try(new BlockPos(x, y + 1, z, 0));
            }
        }
        if (spots.Count == 0)
        {
            sapi.Logger.Warning("[maps] {0}: no free floor for its spare maps ({1})", sc.Code, sc.Anchor);
            return;
        }

        // Spread them out: farthest-point picking from a seeded start.
        var rand = new CaveRand((uint)(sapi.World.Seed * 7 + sc.Code.GetHashCode()));
        var picked = new List<BlockPos> { spots[(int)(rand.NextDouble() * spots.Count)] };
        while (picked.Count < sc.Items.Length && picked.Count < spots.Count)
        {
            BlockPos best = null; double bestD = -1;
            foreach (var s in spots)
            {
                double dd = picked.Min(o => (double)o.DistanceSqTo(s.X, s.Y, s.Z));
                if (dd > bestD) { bestD = dd; best = s; }
            }
            if (best == null || bestD < 4) break;
            picked.Add(best);
        }

        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        var notes = new List<string>();
        for (int i = 0; i < sc.Items.Length && i < picked.Count; i++)
        {
            var (map, style) = sc.Items[i];
            string code = "landmassgenerator:islandmap-" + map;
            var at = picked[i];
            if (style == "chest")
            {
                var contents = new List<string> { code };
                contents.AddRange(RollTable(sc.Table, rand, 3 + (int)(rand.NextDouble() * 3)));
                string type = sc.ChestType == "collapsed" ? "collapsed" + (1 + (int)(rand.NextDouble() * 4)) : sc.ChestType;
                if (PlaceChestAt(at, type, contents)) notes.Add($"chest with the {map} map at HUD {at.X - mx}, {at.Y}, {at.Z - mz}");
            }
            else if (ShowItem(at, style, code, rand)) notes.Add($"{(style == "case" ? "display case" : "on the floor")}: the {map} map at HUD {at.X - mx}, {at.Y}, {at.Z - mz}");
        }
        sapi.WorldManager.SaveGame.StoreData(doneKey, true);
        sapi.Logger.Notification("[maps] {0} ({1} spots): {2}", sc.Code, spots.Count, string.Join("; ", notes));
    }
}
