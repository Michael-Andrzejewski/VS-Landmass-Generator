using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using System.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// A broken static translocator on every Rustfall island (0.72.0).
//
// Each shape below names a target cell (map column, row, as in the shape
// file, before rotate=) at a prominent spot: the landing, the approach to the
// island's landmark, the top of a lone rock. After the island and its
// structures stand, the nearest open, level, natural ground to that target
// gets the translocator: grass, soil, sand, gravel or bare rock with open sky
// above (so never inside a building or on a roof), the 3x3 around it within a
// block of the same height, above the sea, two clear blocks on top. The
// search widens ring by ring up to twice the radius before it gives up with a
// note. The starter and tin islands carry theirs as `block` markers in their
// shapes; the pylon lines are open sea and get none.
//
// /genchests places them into a world built before 0.72.0, skipping any
// island that already has a static translocator (broken or repaired) near
// the target.
public partial class LandmassGeneratorModSystem
{
    private const string BrokenTranslocatorCode = "game:statictranslocator-broken-north";

    // Spots chosen 2026-11-21 (0.72.3) from the real block dumps of each island, after Michael moved the
    // Forest Island one out of a wooded valley to the open meadow by the hut: open ground you see as you
    // come ashore or walk up to the island's landmark, beside it but never inside it.
    private static readonly Dictionary<string, (int Gx, int Gz)> TranslocatorSpots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alchemist_lair"] = (94, 141),          // the lawn just inside the college gate, from the south landing
        ["blackfen_island"] = (85, 31),          // the bare rock knoll on the north-east coast, by the bog mine mouth
        ["cardinals_island"] = (70, 100),        // the open meadow south of the cathedral
        ["chainfield"] = (50, 55),               // the orb's south shoulder, seen on the approach
        ["colossus_deep"] = (40, 43),            // the south edge of the crown, above the blade
        ["dangerous_swamplands"] = (68, 57),     // the grass clearing by the giant cypress
        ["divingbell_mine"] = (52, 17),          // the grass bluff over the blue hole, between the derricks
        ["eerie_island"] = (86, 97),             // the grass outside the stone circle
        ["forester_island"] = (49, 59),          // the meadow above the bay by the hut (Michael's pick, HUD 1923, -290)
        ["forge_volcano"] = (42, 67),            // the foot of the cone, below the catwalk to the crucible
        ["giant_serpent_skeleton"] = (16, 43),   // the west islet's lagoon edge, facing the skull
        ["granary_lush"] = (68, 77),             // the summit's south edge, at a gap in the yard wall
        ["great_machine"] = (155, 229),          // where the south landing ramp meets the plateau, on the way to the gate
        ["hanging_gardens"] = (106, 141),        // the foot of the temple's grand stair
        ["ideal_house_island"] = (97, 136),      // the prairie between the south-west beach and the pond
        ["ironmine_island"] = (146, 177),        // the clearing round the landmark pine
        ["lighthouse"] = (35, 30),               // the soil spit at the tower's foot
        ["lone_bastion_1"] = (50, 69),           // the south brink of the plateau, facing the castle
        ["mansion_island"] = (82, 97),           // the front lawn at the head of the drive
        ["power_plant"] = (77, 82),              // the yard in front of the reactor hall's north door
        ["refinery_island"] = (78, 98),          // the open ground between the east penstock and a tank
        ["ruin_isle"] = (50, 84),                // the south shore below the ruin
        ["ruin_isle_cold"] = (50, 84),
        ["ruin_isle_arid"] = (50, 84),
        ["saltteeth_island"] = (54, 70),         // the salt saddle east of the halite spire
        ["the_spoils"] = (84, 148),              // the sand shelf at the foot of the heap
        ["volcano_ring"] = (118, 236),           // the rim crest above the south cove, over the crater lake
        ["wreckage_maelstrom"] = (35, 85),       // the highest spire top that is open ground
    };

    // The 0.72.0 spots. /genchests clears a broken translocator still standing near one of these
    // when the island's spot has moved, before it places the new one (a repaired one is never touched).
    private static readonly Dictionary<string, (int Gx, int Gz)> OldTranslocatorSpots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alchemist_lair"] = (150, 125), ["blackfen_island"] = (38, 72), ["cardinals_island"] = (68, 112),
        ["cattail_isles"] = (62, 82), ["chainfield"] = (50, 50), ["colossus_deep"] = (40, 40),
        ["dangerous_swamplands"] = (95, 75), ["divingbell_mine"] = (50, 22), ["eerie_island"] = (84, 104),
        ["forester_island"] = (50, 72), ["forge_volcano"] = (45, 72), ["giant_serpent_skeleton"] = (15, 43),
        ["granary_lush"] = (60, 82), ["great_machine"] = (160, 232), ["hanging_gardens"] = (62, 150),
        ["ideal_house_island"] = (85, 108), ["ironmine_island"] = (125, 200), ["lighthouse"] = (40, 30),
        ["lone_bastion_1"] = (50, 50), ["mansion_island"] = (78, 125), ["power_plant"] = (80, 128),
        ["refinery_island"] = (38, 100), ["saltteeth_island"] = (40, 75), ["the_spoils"] = (60, 150),
        ["volcano_ring"] = (100, 250), ["wreckage_maelstrom"] = (38, 63),
    };

    // Islands with no natural ground (an orb, a statue, a drowned lighthouse): the translocator may stand on
    // the open top of anything solid.
    private static readonly HashSet<string> TranslocatorOnAnySurface = new(StringComparer.OrdinalIgnoreCase) { "chainfield", "colossus_deep", "lighthouse" };

    private int TranslocatorRadius(IslandJob job) => Math.Max(16, (int)Math.Round(job.WorldPerCell * 8));

    // Where the island's translocator should go, in world blocks; false when its shape has none.
    private bool TranslocatorTarget(IslandJob job, out int x, out int z)
    {
        x = z = 0;
        if (job.Shape == null || job.ShapeName == null || !TranslocatorSpots.TryGetValue(job.ShapeName, out var spot)) return false;
        MarkerWorld(job, spot.Gx, spot.Gz, out x, out z);
        return true;
    }

    private string PlaceTranslocator(IslandJob job)
    {
        string cleared = ClearOldTranslocator(job);
        if (!TranslocatorTarget(job, out int tx, out int tz)) return cleared;
        Block block = sapi.World.GetBlock(new AssetLocation(BrokenTranslocatorCode));
        if (block == null) return " Translocator block not in this game.";
        var ba = sapi.World.BlockAccessor;
        int radius = TranslocatorRadius(job);

        if (TranslocatorNear(job, tx, tz, radius * 2 + 4, out BlockPos existing))
            return cleared + $" Translocator already at {existing.X},{existing.Y},{existing.Z}.";

        bool anySurface = TranslocatorOnAnySurface.Contains(job.ShapeName);
        var pos = new BlockPos(0, 0, 0, job.Dim);
        for (int r = 0; r <= radius * 2; r++)
        {
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    int x = tx + dx, z = tz + dz;
                    if (!TranslocatorGround(job.SeaLevel, job.Dim, x, z, anySurface, out int y)) continue;
                    pos.Set(x, y + 1, z);
                    ba.SetBlock(block.BlockId, pos);
                    pos.Y = y + 2;
                    if (ba.GetBlock(pos).Id != 0) ba.SetBlock(0, pos);   // a tall plant on top
                    string ground = ba.GetBlock(new BlockPos(x, y, z, job.Dim)).Code?.Path ?? "?";
                    sapi.Logger.Notification("[translocators] {0}: placed at {1},{2},{3} on {4}, {5} blocks from its target {6},{7}",
                        job.ShapeName, x, y + 1, z, ground, r, tx, tz);
                    return cleared + $" Translocator at {x},{y + 1},{z} ({r} from its target).";
                }
        }
        sapi.Logger.Warning("[translocators] {0}: no open level ground within {1} blocks of {2},{3}; none placed", job.ShapeName, radius * 2, tx, tz);
        return $" No open ground for the translocator within {radius * 2} blocks of {tx},{tz}.";
    }

    // Where the island's 0.72.0 translocator was aimed, when its spot has moved since (or it has none now).
    private bool OldTranslocatorTarget(IslandJob job, out int x, out int z)
    {
        x = z = 0;
        if (job.Shape == null || job.ShapeName == null || !OldTranslocatorSpots.TryGetValue(job.ShapeName, out var old)) return false;
        if (TranslocatorSpots.TryGetValue(job.ShapeName, out var now) && now == old) return false;
        MarkerWorld(job, old.Gx, old.Gz, out x, out z);
        return true;
    }

    // A broken translocator left by 0.72.0 at a spot that has moved: taken away (only a broken one at the
    // surface, so one a player repaired, or a Great Machine find underground, stays).
    private string ClearOldTranslocator(IslandJob job)
    {
        if (!OldTranslocatorTarget(job, out int ox, out int oz)) return "";
        var ba = sapi.World.BlockAccessor;
        int range = TranslocatorRadius(job) * 2 + 4;
        var p = new BlockPos(0, 0, 0, job.Dim);
        int removed = 0;
        for (int x = ox - range; x <= ox + range; x++)
            for (int z = oz - range; z <= oz + range; z++)
            {
                int top = ba.GetRainMapHeightAt(x, z);
                for (int y = top + 1; y >= Math.Max(1, top - 1); y--)
                {
                    p.Set(x, y, z);
                    if (ba.GetBlock(p).Code?.Path.StartsWith("statictranslocator-broken") != true) continue;
                    ba.SetBlock(0, p);
                    removed++;
                    sapi.Logger.Notification("[translocators] {0}: took away the old one at {1},{2},{3}", job.ShapeName, x, y, z);
                }
            }
        return removed > 0 ? $" Took away {removed} old translocator(s)." : "";
    }

    // Open, level, natural, dry ground at this column; y is its top block.
    private bool TranslocatorGround(int seaLevel, int dim, int x, int z, bool anySurface, out int y)
    {
        var ba = sapi.World.BlockAccessor;
        y = ba.GetRainMapHeightAt(x, z);
        if (y < seaLevel || y <= 0) return false;
        var p = new BlockPos(x, y, z, dim);
        if (!Standable(ba.GetBlock(p, BlockLayersAccess.Solid), anySurface)) return false;
        if (ba.GetBlock(p, BlockLayersAccess.Fluid).Id != 0) return false;
        for (int up = 1; up <= 2; up++)
        {
            p.Y = y + up;
            Block b = ba.GetBlock(p);
            if (b.Id != 0 && b.Replaceable < 6000) return false;
            if (ba.GetBlock(p, BlockLayersAccess.Fluid).Id != 0) return false;
        }
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                int ny = ba.GetRainMapHeightAt(x + dx, z + dz);
                if (Math.Abs(ny - y) > 1) return false;
                if (!Standable(ba.GetBlock(new BlockPos(x + dx, ny, z + dz, dim), BlockLayersAccess.Solid), anySurface)) return false;
            }
        return true;
    }

    private static bool Standable(Block b, bool anySurface)
        => anySurface ? b != null && b.Id != 0 && b.SideSolid[BlockFacing.UP.Index] && b.BlockMaterial != EnumBlockMaterial.Leaves && !b.IsLiquid()
                      : NaturalGround(b);

    private static bool NaturalGround(Block b)
    {
        if (b == null || b.Id == 0 || b.Code == null) return false;
        string path = b.Code.Path;
        switch (b.BlockMaterial)
        {
            case EnumBlockMaterial.Soil:
            case EnumBlockMaterial.Sand:
            case EnumBlockMaterial.Gravel:
                return b.SideSolid[BlockFacing.UP.Index];
            case EnumBlockMaterial.Stone:
                return path.StartsWith("rock-") || path.StartsWith("crackedrock-") || path.StartsWith("rocksand") || path.StartsWith("halite");
            default:
                return false;
        }
    }

    private bool TranslocatorNear(IslandJob job, int cx, int cz, int range, out BlockPos found)
    {
        found = null;
        var ba = sapi.World.BlockAccessor;
        var p = new BlockPos(0, 0, 0, job.Dim);
        for (int x = cx - range; x <= cx + range; x++)
            for (int z = cz - range; z <= cz + range; z++)
            {
                int top = ba.GetRainMapHeightAt(x, z);
                for (int y = top + 1; y >= Math.Max(1, top - 2); y--)
                {
                    p.Set(x, y, z);
                    if (ba.GetBlock(p).Code?.Path.StartsWith("statictranslocator") == true) { found = p.Copy(); return true; }
                }
            }
        return false;
    }

    // ── story structures ────────────────────────────────────────────────
    // Story locations the game builds itself (not plan islands) that get a
    // translocator once they stand. The game claims Nadiya's village whole,
    // and a claim stops players repairing, naming or linking what is inside,
    // so the spot is the open level natural ground nearest the structure's
    // centre that is outside any locked claim: the village's edge.
    private static readonly string[] StoryTranslocators = { "village" };
    private long _storyTranslocatorListener;
    private readonly HashSet<string> _storyTranslocatorLoading = new();

    private void StartStoryTranslocatorWatcher()
    {
        StartIslandLureWatcher();   // the Great Machine's landing invasion (IslandLures.cs)
        if (_storyTranslocatorListener != 0) return;
        _storyTranslocatorListener = sapi.Event.RegisterGameTickListener(_ => StoryTranslocatorTick(false), 5000);
    }

    private void StoryTranslocatorTick(bool force)
    {
        var genStory = sapi.ModLoader.GetModSystem<GenStoryStructures>();
        if (genStory == null) return;
        foreach (string code in StoryTranslocators)
        {
            string doneKey = "lgStoryTranslocatorDone:" + code;
            bool tlDone = sapi.WorldManager.SaveGame.GetData<bool>(doneKey, false);
            bool chestDone = !StoryTreeChests.ContainsKey(code) || sapi.WorldManager.SaveGame.GetData<bool>("lgStoryTreeChestDone:" + code, false);
            if (tlDone && chestDone) continue;
            var loc = genStory.Structures.Get(code);
            if (loc == null || !loc.DidGenerate || loc.Location == null) { if (force) sapi.Logger.Notification("[translocators] story {0}: not generated yet", code); continue; }
            var area = loc.Location;
            int reach = Math.Max(area.SizeX, area.SizeZ) / 2 + 64;
            int cx = (area.X1 + area.X2) / 2, cz = (area.Z1 + area.Z2) / 2;
            bool near = sapi.World.AllOnlinePlayers.Any(pl => pl.Entity != null
                && Math.Abs(pl.Entity.Pos.X - cx) < reach + 200 && Math.Abs(pl.Entity.Pos.Z - cz) < reach + 200);
            if (!near && !force) continue;

            int cs = GlobalConstants.ChunkSize;
            // the hidden chest looks 40 blocks past the translocator's reach
            int c1x = (cx - reach - 42) / cs, c2x = (cx + reach + 42) / cs, c1z = (cz - reach - 42) / cs, c2z = (cz + reach + 42) / cs;
            // Loaded means the block chunks at ground level, not just the map chunks (those outlive them).
            bool loaded = true;
            var probe = new BlockPos(0, 0, 0, 0);
            for (int x = c1x; x <= c2x && loaded; x++)
                for (int z = c1z; z <= c2z && loaded; z++)
                {
                    if (sapi.WorldManager.GetMapChunk(x, z) == null) { loaded = false; break; }
                    probe.Set(x * cs + cs / 2, 0, z * cs + cs / 2);
                    probe.Y = Math.Max(1, sapi.World.BlockAccessor.GetRainMapHeightAt(probe.X, probe.Z));
                    if (sapi.World.BlockAccessor.GetChunkAtBlockPos(probe) == null) loaded = false;
                }
            if (!loaded)
            {
                if (force && _storyTranslocatorLoading.Add(code))
                {
                    // Kept loaded while we work: a headless server drops unwatched chunks within moments.
                    LoadChunkAreaBanded(c1x, c1z, c2x, c2z, true, () =>
                    {
                        try { StoryTranslocatorTick(true); }
                        finally
                        {
                            _storyTranslocatorLoading.Remove(code);
                            ReleaseKeptChunks(new IslandJob { Dim = 0, PreCx1 = c1x, PreCz1 = c1z, PreCx2 = c2x, PreCz2 = c2z });
                        }
                    });
                }
                else if (force) sapi.Logger.Warning("[translocators] story {0}: its area did not stay loaded", code);
                continue;
            }

            if (StoryTreeChests.TryGetValue(code, out string treeLore)) PlaceStoryTreeChest(code, cx, cz, reach, treeLore);
            if (tlDone) continue;

            if (TranslocatorNearAt(cx, cz, reach, 0, out BlockPos existing))
            {
                sapi.WorldManager.SaveGame.StoreData(doneKey, true);
                sapi.Logger.Notification("[translocators] story {0}: one already at {1}", code, existing);
                continue;
            }
            Block block = sapi.World.GetBlock(new AssetLocation(BrokenTranslocatorCode));
            var ba = sapi.World.BlockAccessor;
            bool Open(BlockPos at) => sapi.World.Claims.Get(at)?.All(cl => cl.AllowUseEveryone) ?? true;
            // The village's own ground level: the median height of the natural ground inside its area.
            var heights = new List<int>();
            for (int x = area.X1; x <= area.X2; x += 4)
                for (int z = area.Z1; z <= area.Z2; z += 4)
                {
                    int h = ba.GetRainMapHeightAt(x, z);
                    if (h >= sapi.World.SeaLevel && NaturalGround(ba.GetBlock(new BlockPos(x, h, z, 0), BlockLayersAccess.Solid))) heights.Add(h);
                }
            heights.Sort();
            int groundY = heights.Count > 0 ? heights[heights.Count / 2] : sapi.World.SeaLevel;

            // Of every open spot outside the claim, the one nearest the village in distance and in height
            // (a block of climb counts as four of walking), so it stands by the way in, not on a hill above.
            BlockPos placed = null;
            int ring = 0;
            double best = double.MaxValue;
            int levelSpots = 0;
            for (int dx = -reach; dx <= reach; dx++)
                for (int dz = -reach; dz <= reach; dz++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (!TranslocatorGround(sapi.World.SeaLevel, 0, x, z, false, out int y)) continue;
                    levelSpots++;
                    double score = Math.Sqrt(dx * dx + dz * dz) + 4 * Math.Abs(y - groundY);
                    if (score >= best) continue;
                    var at = new BlockPos(x, y + 1, z, 0);
                    if (!Open(at)) continue;
                    best = score; placed = at; ring = (int)Math.Round(Math.Sqrt(dx * dx + dz * dz));
                }
            if (placed != null)
            {
                ba.SetBlock(block.BlockId, placed);
                var top = placed.UpCopy();
                if (ba.GetBlock(top).Id != 0) ba.SetBlock(0, top);
            }
            if (placed == null)
            {
                sapi.Logger.Warning("[translocators] story {0}: no open level ground outside its claim within {1} blocks of its centre ({2} level spots, all inside the claim; village ground at y {3})", code, reach, levelSpots, groundY);
                continue;
            }
            sapi.WorldManager.SaveGame.StoreData(doneKey, true);
            sapi.Logger.Notification("[translocators] story {0}: placed at {1},{2},{3} (HUD {4}, {5}), {6} blocks from its centre, village ground at y {7}",
                code, placed.X, placed.Y, placed.Z, placed.X - sapi.WorldManager.MapSizeX / 2, placed.Z - sapi.WorldManager.MapSizeZ / 2, ring, groundY);
        }
    }

    // A hidden chest at the foot of a tree outside the story site's locked claim (0.73.1).
    private static readonly Dictionary<string, string> StoryTreeChests = new() { ["village"] = "lore:hunter_wolves" };

    private void PlaceStoryTreeChest(string code, int cx, int cz, int reach, string lore)
    {
        string doneKey = "lgStoryTreeChestDone:" + code;
        if (sapi.WorldManager.SaveGame.GetData<bool>(doneKey, false)) return;
        var ba = sapi.World.BlockAccessor;
        bool Open(BlockPos at) => sapi.World.Claims.Get(at)?.All(cl => cl.AllowUseEveryone) ?? true;
        var p = new BlockPos(0, 0, 0, 0);
        BlockPos best = null;
        double bestD = double.MaxValue;
        int far = reach + 40;
        for (int x = cx - far; x <= cx + far; x++)
            for (int z = cz - far; z <= cz + far; z++)
            {
                double d = Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
                if (d >= bestD) continue;
                int top = ba.GetRainMapHeightAt(x, z);
                p.Set(x, top, z);
                if (top < sapi.World.SeaLevel || ba.GetBlock(p).BlockMaterial != EnumBlockMaterial.Leaves) continue;
                int y = top - 1;
                for (; y > sapi.World.SeaLevel; y--)
                {
                    p.Set(x, y, z);
                    var b = ba.GetBlock(p, BlockLayersAccess.Solid);
                    if (b.Id != 0 && b.BlockMaterial != EnumBlockMaterial.Leaves && b.BlockMaterial != EnumBlockMaterial.Plant) break;
                }
                p.Set(x, y, z);
                if (!NaturalGround(ba.GetBlock(p, BlockLayersAccess.Solid))) continue;
                var at = new BlockPos(x, y + 1, z, 0);
                var a1 = ba.GetBlock(at, BlockLayersAccess.Solid);
                var a2 = ba.GetBlock(at.UpCopy(), BlockLayersAccess.Solid);
                if ((a1.Id != 0 && a1.BlockMaterial != EnumBlockMaterial.Plant) || (a2.Id != 0 && a2.BlockMaterial != EnumBlockMaterial.Plant)) continue;
                if (!Open(at)) continue;
                bool trunk = BlockFacing.HORIZONTALS.Any(f => ba.GetBlock(at.AddCopy(f)).Code?.Path.StartsWith("log-grown") == true);
                if (!trunk) continue;
                best = at; bestD = d;
            }
        if (best == null)
        {
            sapi.Logger.Warning("[props] story {0}: no tree outside its claim within {1} blocks for the hidden chest", code, far);
            return;
        }
        var rand = new CaveRand((uint)(best.X * 31 + best.Z));
        var contents = lore.Split(',').Concat(RollTable("ruin", rand, 3)).ToList();
        if (PlaceChestAt(best, "collapsed3", contents)) sapi.WorldManager.SaveGame.StoreData(doneKey, true);
    }

    private bool TranslocatorNearAt(int cx, int cz, int range, int dim, out BlockPos found)
    {
        found = null;
        var ba = sapi.World.BlockAccessor;
        var p = new BlockPos(0, 0, 0, dim);
        for (int x = cx - range; x <= cx + range; x++)
            for (int z = cz - range; z <= cz + range; z++)
            {
                int top = ba.GetRainMapHeightAt(x, z);
                for (int y = top + 1; y >= Math.Max(1, top - 2); y--)
                {
                    p.Set(x, y, z);
                    if (ba.GetBlock(p).Code?.Path.StartsWith("statictranslocator") == true) { found = p.Copy(); return true; }
                }
            }
        return false;
    }
}
