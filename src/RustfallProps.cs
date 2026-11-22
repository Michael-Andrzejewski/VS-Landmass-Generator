using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// Three kinds of Rustfall find, placed after an island's structures and loot (0.73.1):
//
// - Hidden chests: collapsed chests at the foot of a forest tree, under its canopy, spread far apart
//   (the Forest Island's four; the first holds "The Clockmaker's Maps").
// - Old bookshelves: ruined clutter bookshelves standing against a wall in a ruin's rooms. Most carry a
//   lore book that, taken and read, gives a Rustfall record the player has not found yet (the
//   randomlore pool rfpool-shelf: every Rustfall record, then the falx journal, then vanilla lore).
// - A tapestry at the bottom of the tin mine: one random Rustfall picture, whole, on a wall of the
//   mine's deepest stretch, with a collapsed chest beside it holding "The Clockmaker's Maps".
//
// Done once per island (lgRustfallPropsDone:<shape>@x,z), so /genchests can add them to an older world.
public partial class LandmassGeneratorModSystem
{
    // Lore: what goes in the first chests, one entry per chest (chest contents syntax, comma-separated).
    private record HiddenChestDef(int Count, string Table, params string[] Lore);
    private record ShelfDef(int Count, char Near, int Radius);

    private static readonly Dictionary<string, HiddenChestDef> HiddenChests = new(StringComparer.OrdinalIgnoreCase)
    {
        ["forester_island"] = new(4, "ruin", "lore:cinder_maps", "lore:hunter_wolves"),
        // the nature islands: the hunter's account in one of each
        ["blackfen_island"] = new(2, "swamp", "lore:hunter_wolves"),
        ["dangerous_swamplands"] = new(2, "swamp", "lore:hunter_wolves"),
        ["volcano_ring"] = new(2, "ruin", "lore:hunter_wolves"),
        ["ironmine_island"] = new(2, "ruin", "lore:hunter_wolves"),
        ["saltteeth_island"] = new(1, "ruin", "lore:hunter_wolves"),
        ["ideal_house_island"] = new(2, "ruin", "lore:hunter_wolves"),
    };

    // Rooms with old bookshelves: the shape's structure marker and how far round it to look.
    private static readonly Dictionary<string, ShelfDef[]> Bookshelves = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tin_island"] = new[] { new ShelfDef(2, 'G', 30) },          // the withdrawn tin laboratory
        ["cardinals_island"] = new[] { new ShelfDef(3, 'Q', 70) },    // the cathedral
        ["lone_bastion_1"] = new[] { new ShelfDef(2, '\0', 50) },     // the castle rooms
        ["mansion_island"] = new[] { new ShelfDef(3, 'Q', 45) },      // the mansion
        ["alchemist_lair"] = new[] { new ShelfDef(3, 'Q', 90) },      // the alchemists' college
        ["hanging_gardens"] = new[] { new ShelfDef(2, 'Q', 80) },     // the temple and markets
        ["forge_volcano"] = new[] { new ShelfDef(1, 'F', 45) },       // the forge
        ["granary_lush"] = new[] { new ShelfDef(1, 'G', 25) },
        ["granary_arid"] = new[] { new ShelfDef(1, 'G', 25) },
        ["granary_cold"] = new[] { new ShelfDef(1, 'G', 25) },
        ["granary_temperate"] = new[] { new ShelfDef(1, 'G', 25) },
        ["divingbell_mine"] = new[] { new ShelfDef(1, 'D', 70) },     // the derrick sheds
    };
    private const double ShelfLoreChance = 0.7;

    // The tin mine's deepest stretch: one Rustfall tapestry (whole 1x2 pictures and the 1x1 notice).
    private static readonly Dictionary<string, char> MineTapestries = new(StringComparer.OrdinalIgnoreCase) { ["tin_island"] = 'V' };
    private static readonly string[] RustfallTapestries1x2 = { "rustfall-lens", "rustfall-sealed", "rustfall-bells", "rustfall-keeper" };
    private const string MineTapestryChestWith = "lore:cinder_maps";

    // One chest of set contents somewhere in a ruin: The Spoils holds the map on to the Great Machine, the
    // last arrow of the map chain (Tobias' cave gives the map to The Spoils).
    private record PlacedChestDef(char Near, int Radius, string Where, string Type, string Contents);
    private static readonly Dictionary<string, PlacedChestDef> PlacedChests = new(StringComparer.OrdinalIgnoreCase)
    {
        ["the_spoils"] = new('Q', 90, "any", "collapsed2", "landmassgenerator:islandmap-greatmachine,randomlore:rfpool-jonas"),
    };

    private string PlaceBerryPatchesSafe(IslandJob job)
    {
        try { string r = PlaceBerryPatches(job); return r.Length > 0 ? " " + r : ""; }
        catch (Exception e) { sapi.Logger.Error(e); return " Berry patches FAILED: " + e.Message; }
    }

    private bool HasRustfallProps(IslandJob job) => job.ShapeName != null
        && (HiddenChests.ContainsKey(job.ShapeName) || Bookshelves.ContainsKey(job.ShapeName) || MineTapestries.ContainsKey(job.ShapeName)
            || PlacedChests.ContainsKey(job.ShapeName));

    private string PlaceRustfallProps(IslandJob job)
    {
        string tw = PlaceTimewood(job);   // Temporal Growth Gears, with their own done key (TimewoodPlacements.cs)
        tw += PlaceBerryPatchesSafe(job);   // dense berry thickets, own done key (BerryPatches.cs)
        if (job.Shape == null || !HasRustfallProps(job)) { sapi.Logger.Notification("[props] {0}: none for this island", job.ShapeName); return tw; }
        string doneKey = $"lgRustfallPropsDone:{job.ShapeName}@{job.Cx},{job.Cz}";
        if (sapi.WorldManager.SaveGame.GetData<bool>(doneKey, false)) { sapi.Logger.Notification("[props] {0}: already done", job.ShapeName); return tw; }
        var notes = new List<string>();
        try
        {
            if (HiddenChests.TryGetValue(job.ShapeName, out var hc)) notes.Add(PlaceHiddenChests(job, hc));
            if (Bookshelves.TryGetValue(job.ShapeName, out var shelves)) notes.Add(PlaceBookshelves(job, shelves));
            if (MineTapestries.TryGetValue(job.ShapeName, out char cave)) notes.Add(PlaceMineTapestry(job, cave));
            if (PlacedChests.TryGetValue(job.ShapeName, out var pc)) notes.Add(PlaceSetChest(job, pc));
        }
        catch (Exception e)
        {
            sapi.Logger.Error(e);
            return " Rustfall props FAILED: " + e.Message;
        }
        sapi.WorldManager.SaveGame.StoreData(doneKey, true);
        string r = string.Concat(notes.Where(n => n.Length > 0).Select(n => " " + n));
        sapi.Logger.Notification("[props] {0}:{1}", job.ShapeName, r);
        return tw + r;
    }

    // ── hidden chests by trees ──────────────────────────────────────────

    private string PlaceHiddenChests(IslandJob job, HiddenChestDef def)
    {
        var ba = sapi.World.BlockAccessor;
        var rand = new CaveRand((uint)(job.Seed * 131 + 977));
        int radius = (int)Math.Ceiling(job.LandRadius);
        var p = new BlockPos(0, 0, 0, job.Dim);
        var spots = new List<BlockPos>();
        // A trunk standing on the ground, with a free floor cell beside it that has leaves overhead.
        for (int x = job.Cx - radius; x <= job.Cx + radius; x += 2)
            for (int z = job.Cz - radius; z <= job.Cz + radius; z += 2)
            {
                int top = ba.GetRainMapHeightAt(x, z);
                if (top < job.SeaLevel) continue;
                p.Set(x, top, z);
                if (ba.GetBlock(p).BlockMaterial != EnumBlockMaterial.Leaves) continue;   // under a canopy
                // walk down to the ground
                int y = top - 1;
                for (; y > job.SeaLevel; y--)
                {
                    p.Set(x, y, z);
                    var b = ba.GetBlock(p, BlockLayersAccess.Solid);
                    if (b.Id != 0 && b.BlockMaterial != EnumBlockMaterial.Leaves && b.BlockMaterial != EnumBlockMaterial.Plant) break;
                }
                p.Set(x, y, z);
                if (!NaturalGround(ba.GetBlock(p, BlockLayersAccess.Solid))) continue;
                if (!FreeCell(job, x, y + 1, z) || !FreeCell(job, x, y + 2, z)) continue;
                bool trunk = false;
                foreach (var f in BlockFacing.HORIZONTALS)
                {
                    p.Set(x + f.Normali.X, y + 1, z + f.Normali.Z);
                    if (ba.GetBlock(p).Code?.Path.StartsWith("log-grown") == true) { trunk = true; break; }
                }
                if (trunk) spots.Add(new BlockPos(x, y + 1, z, job.Dim));
            }
        for (int i = spots.Count - 1; i > 0; i--) { int j = (int)(rand.NextDouble() * (i + 1)); (spots[i], spots[j]) = (spots[j], spots[i]); }
        var picked = new List<BlockPos>();
        foreach (int minD in new[] { 60, 35, 15 })
            foreach (var s in spots)
            {
                if (picked.Count >= def.Count) break;
                if (picked.Any(o => Math.Abs(o.X - s.X) < minD && Math.Abs(o.Z - s.Z) < minD)) continue;
                if (HasChestNear(ba, s)) continue;
                picked.Add(s);
            }
        int n = 0;
        foreach (var at in picked)
        {
            var contents = RollTable(def.Table, rand, 3 + (int)(rand.NextDouble() * 3));
            if (n < def.Lore.Length) contents = def.Lore[n].Split(',').Concat(contents).ToList();
            if (PlaceChestAt(at, "collapsed" + (1 + (int)(rand.NextDouble() * 4)), contents)) n++;
        }
        return $"{n} hidden chest(s) by trees ({spots.Count} trunk spots).";
    }

    private bool FreeCell(IslandJob job, int x, int y, int z)
    {
        var p = new BlockPos(x, y, z, job.Dim);
        var b = sapi.World.BlockAccessor.GetBlock(p, BlockLayersAccess.Solid);
        if (b != null && b.Id != 0 && b.BlockMaterial != EnumBlockMaterial.Plant) return false;
        return (sapi.World.BlockAccessor.GetBlock(p, BlockLayersAccess.Fluid)?.Id ?? 0) == 0;
    }

    private bool PlaceChestAt(BlockPos at, string type, List<string> contents)
    {
        Block chest = sapi.World.GetBlock(new AssetLocation("game", "chest-east"));
        if (chest == null) return false;
        sapi.World.BlockAccessor.SetBlock(chest.BlockId, at);
        if (sapi.World.BlockAccessor.GetBlockEntity(at) is not BlockEntityGenericTypedContainer be) return false;
        var ts = new ItemStack(chest);
        ts.Attributes.SetString("type", type);
        be.OnBlockPlaced(ts);
        var notes = new List<string>();
        FillChest(be, contents.Take(be.Inventory.Count).ToList(), notes);
        sapi.Logger.Notification("[props] chest ({0}) at HUD {1}, {2}, {3}: {4}", type,
            at.X - sapi.WorldManager.MapSizeX / 2, at.Y, at.Z - sapi.WorldManager.MapSizeZ / 2, string.Join(", ", contents));
        return true;
    }

    private string PlaceSetChest(IslandJob job, PlacedChestDef d)
    {
        int cx = job.Cx, cz = job.Cz;
        if (job.Shape.FirstCells.TryGetValue(d.Near, out var cell)) MarkerWorld(job, cell.X, cell.Z, out cx, out cz);
        var rand = new CaveRand((uint)(job.Seed * 977 + 61));
        var spots = FloorSpots(job, cx, cz, d.Radius, d.Where, 1, rand, new List<BlockPos>());
        if (spots.Count == 0) return "no floor for its chest.";
        return PlaceChestAt(spots[0], d.Type, d.Contents.Split(',').ToList()) ? "a chest with " + d.Contents + "." : "its chest failed.";
    }

    // ── old bookshelves ─────────────────────────────────────────────────

    // meshAngle for a prop whose back must face the wall in that direction (as the game turns a
    // wall-hung tapestry: north 0, west 90, south 180, east 270 degrees).
    private static float WallAngle(BlockFacing wall) => wall.Code switch
    {
        "north" => 0f, "west" => GameMath.PIHALF, "south" => GameMath.PI, _ => 3 * GameMath.PIHALF,
    };

    private string PlaceBookshelves(IslandJob job, ShelfDef[] defs)
    {
        Block lore = sapi.World.GetBlock(new AssetLocation("game", "clutteredbookshelfwithlore"));
        Block plain = sapi.World.GetBlock(new AssetLocation("game", "clutteredbookshelf"));
        if (lore == null || plain == null) return "Bookshelf blocks not in this game.";
        var ba = sapi.World.BlockAccessor;
        var rand = new CaveRand((uint)(job.Seed * 53 + 4421));
        int placed = 0, withLore = 0;
        var taken = new List<BlockPos>();
        foreach (var d in defs)
        {
            int cx = job.Cx, cz = job.Cz;
            if (d.Near != '\0' && job.Shape.FirstCells.TryGetValue(d.Near, out var cell)) MarkerWorld(job, cell.X, cell.Z, out cx, out cz);
            // Plenty of candidates: only room floors with a wall right beside them can take a shelf.
            var floors = FloorSpots(job, cx, cz, d.Radius, "inside", d.Count * 25, rand, taken);
            int here = 0;
            foreach (var at in floors)
            {
                if (here >= d.Count) break;
                BlockFacing wall = null;
                foreach (var f in BlockFacing.HORIZONTALS)
                {
                    var w1 = ba.GetBlock(at.AddCopy(f), BlockLayersAccess.Solid);
                    var w2 = ba.GetBlock(at.AddCopy(f).Up(), BlockLayersAccess.Solid);
                    if (w1.SideSolid[f.Opposite.Index] && w2.SideSolid[f.Opposite.Index]) { wall = f; break; }
                }
                if (wall == null) continue;
                if (taken.Any(o => o.ManhattenDistance(at) < 5)) continue;
                bool hasLore = rand.NextDouble() < ShelfLoreChance;
                Block block = hasLore ? lore : plain;
                ba.SetBlock(block.BlockId, at);
                var be = ba.GetBlockEntity(at);
                if (be == null) continue;
                var ts = new ItemStack(block);
                ts.Attributes.SetString("variant", "full");
                if (hasLore) ts.Attributes.SetString("loreCode", "rfpool-shelf");
                be.OnBlockPlaced(ts);
                var tree = new TreeAttribute();
                be.ToTreeAttributes(tree);
                tree.SetFloat("meshAngle", WallAngle(wall));
                be.FromTreeAttributes(tree, sapi.World);
                be.MarkDirty(true);
                taken.Add(at);
                here++; placed++;
                if (hasLore) withLore++;
                sapi.Logger.Notification("[props] {0} bookshelf at HUD {1}, {2}, {3}, against the {4} wall", hasLore ? "lore" : "old",
                    at.X - sapi.WorldManager.MapSizeX / 2, at.Y, at.Z - sapi.WorldManager.MapSizeZ / 2, wall.Code);
            }
        }
        return $"{placed} old bookshelf(s), {withLore} with lore.";
    }

    // ── a tapestry at the bottom of a mine ──────────────────────────────

    private string PlaceMineTapestry(IslandJob job, char caveChar)
    {
        // The island's (first) carved cave: the tin island has the one mine.
        var cave = job.Shape.Caves.FirstOrDefault()?.Def;
        if (cave == null) return $"no cave '{caveChar}' for the tapestry.";
        if (job.CaveSteps == null)
        {
            var dry = new CaveWork { DryRun = true };
            CarveCaves(job, dry);
            job.CaveSteps = dry.Steps;
        }
        var steps = job.CaveSteps.Where(s => s.Def == cave).OrderBy(s => s.Y).Take(80).ToList();
        if (steps.Count == 0) return "no cave steps for the tapestry.";
        var ba = sapi.World.BlockAccessor;
        var rand = new CaveRand((uint)(job.Seed * 211 + 31));
        string pic = RustfallTapestries1x2[(int)(rand.NextDouble() * RustfallTapestries1x2.Length) % RustfallTapestries1x2.Length];
        var p = new BlockPos(0, 0, 0, job.Dim);
        foreach (var s in steps)
        {
            int sx = (int)Math.Floor(s.X), sz = (int)Math.Floor(s.Z);
            // Floor cells round the step, each with a flat stone wall two blocks high beside it.
            for (int dx = -5; dx <= 5; dx++)
                for (int dz = -5; dz <= 5; dz++)
                {
                    int x = sx + dx, z = sz + dz;
                    for (int y = (int)Math.Floor(s.Y + s.V); y >= (int)Math.Floor(s.Y - s.V - 3); y--)
                    {
                        p.Set(x, y, z);
                        var floor = ba.GetBlock(p, BlockLayersAccess.Solid);
                        if (floor.Id == 0 || !floor.SideSolid[BlockFacing.UP.Index]) continue;
                        if (!FreeCell(job, x, y + 1, z) || !FreeCell(job, x, y + 2, z) || !FreeCell(job, x, y + 3, z)) break;
                        foreach (var f in BlockFacing.HORIZONTALS)
                        {
                            var w1 = ba.GetBlock(new BlockPos(x, y + 1, z, job.Dim).AddCopy(f), BlockLayersAccess.Solid);
                            var w2 = ba.GetBlock(new BlockPos(x, y + 2, z, job.Dim).AddCopy(f), BlockLayersAccess.Solid);
                            if (!w1.SideSolid[f.Opposite.Index] || !w2.SideSolid[f.Opposite.Index]) continue;
                            if (!(w1.BlockMaterial == EnumBlockMaterial.Stone && w2.BlockMaterial == EnumBlockMaterial.Stone)) continue;
                            Block tap = sapi.World.GetBlock(new AssetLocation("game", "tapestry-" + f.Code));
                            if (tap == null) return "tapestry block not in this game.";
                            // 1x2 pieces count from the top: piece 1 above, piece 2 below.
                            SetTapestry(tap, new BlockPos(x, y + 2, z, job.Dim), pic + "1");
                            SetTapestry(tap, new BlockPos(x, y + 1, z, job.Dim), pic + "2");
                            sapi.Logger.Notification("[props] tapestry {0} at HUD {1}, {2}, {3}, on the {4} wall", pic,
                                x - sapi.WorldManager.MapSizeX / 2, y + 1, z - sapi.WorldManager.MapSizeZ / 2, f.Code);
                            // The chest beside it, on the floor a step along the wall.
                            string chestNote = "";
                            foreach (var side in BlockFacing.HORIZONTALS.Where(h => h != f && h != f.Opposite))
                            {
                                var c = new BlockPos(x, y + 1, z, job.Dim).AddCopy(side);
                                var below = ba.GetBlock(c.DownCopy(), BlockLayersAccess.Solid);
                                if (!FreeCell(job, c.X, c.Y, c.Z) || !below.SideSolid[BlockFacing.UP.Index]) continue;
                                var contents = MineTapestryChestWith.Split(',').Concat(RollTable("mine", rand, 3)).ToList();
                                if (PlaceChestAt(c, "collapsed2", contents)) chestNote = " and a chest beside it";
                                break;
                            }
                            return $"tapestry {pic} at the mine's bottom (y {y + 1}){chestNote}.";
                        }
                        break;
                    }
                }
        }
        return "no wall at the mine's bottom for the tapestry.";
    }

    private void SetTapestry(Block tap, BlockPos at, string type)
    {
        var ba = sapi.World.BlockAccessor;
        ba.SetBlock(tap.BlockId, at);
        var be = ba.GetBlockEntity(at);
        if (be == null) return;
        var ts = new ItemStack(tap);
        ts.Attributes.SetString("type", type);
        ts.Attributes.SetBool("preserve", true);
        be.OnBlockPlaced(ts);
        be.MarkDirty(true);
    }
}
