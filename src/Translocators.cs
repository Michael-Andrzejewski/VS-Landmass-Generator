using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

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

    private static readonly Dictionary<string, (int Gx, int Gz)> TranslocatorSpots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alchemist_lair"] = (150, 125),         // the campus edge above the gateway bowl
        ["blackfen_island"] = (38, 72),          // dry peat west of the meres
        ["cardinals_island"] = (68, 112),        // the south approach to the cathedral
        ["cattail_isles"] = (62, 82),            // the largest islet
        ["chainfield"] = (50, 50),               // the top of the orb
        ["colossus_deep"] = (40, 40),            // the colossus' crown
        ["dangerous_swamplands"] = (95, 75),     // the sandy rise by the lagoon
        ["divingbell_mine"] = (50, 22),          // the south shore over the blue hole
        ["eerie_island"] = (84, 104),            // south of the stone circle
        ["forester_island"] = (50, 72),          // the logging clearing at the bay head
        ["forge_volcano"] = (45, 72),            // the flats at the foot of the cone
        ["giant_serpent_skeleton"] = (15, 43),   // the west islet over the lagoon
        ["granary_lush"] = (60, 82),             // a terrace below the granary
        ["great_machine"] = (160, 232),          // outside the south gate
        ["hanging_gardens"] = (62, 150),         // by the lily pond on the approach
        ["ideal_house_island"] = (85, 108),      // the prairie by the landmark tree
        ["ironmine_island"] = (125, 200),        // the south pine lowland
        ["lighthouse"] = (40, 30),               // the keeper's shoal
        ["lone_bastion_1"] = (50, 50),           // the castle courtyard
        ["mansion_island"] = (78, 125),          // the top of the landing beach drive
        ["power_plant"] = (80, 128),             // the south edge of the site
        ["refinery_island"] = (38, 100),         // the marsh flats west of the plant
        ["ruin_isle"] = (50, 84),                // the south shore below the ruin
        ["ruin_isle_cold"] = (50, 84),
        ["ruin_isle_arid"] = (50, 84),
        ["saltteeth_island"] = (40, 75),         // the meadow under the ridge
        ["the_spoils"] = (60, 150),              // the shelf below the heap
        ["volcano_ring"] = (100, 250),           // inland from the south cove
        ["wreckage_maelstrom"] = (38, 63),       // the broadest spire
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
        if (!TranslocatorTarget(job, out int tx, out int tz)) return "";
        Block block = sapi.World.GetBlock(new AssetLocation(BrokenTranslocatorCode));
        if (block == null) return " Translocator block not in this game.";
        var ba = sapi.World.BlockAccessor;
        int radius = TranslocatorRadius(job);

        if (TranslocatorNear(job, tx, tz, radius * 2 + 4, out BlockPos existing))
            return $" Translocator already at {existing.X},{existing.Y},{existing.Z}.";

        bool anySurface = TranslocatorOnAnySurface.Contains(job.ShapeName);
        var pos = new BlockPos(0, 0, 0, job.Dim);
        for (int r = 0; r <= radius * 2; r++)
        {
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    int x = tx + dx, z = tz + dz;
                    if (!TranslocatorGround(job, x, z, anySurface, out int y)) continue;
                    pos.Set(x, y + 1, z);
                    ba.SetBlock(block.BlockId, pos);
                    pos.Y = y + 2;
                    if (ba.GetBlock(pos).Id != 0) ba.SetBlock(0, pos);   // a tall plant on top
                    string ground = ba.GetBlock(new BlockPos(x, y, z, job.Dim)).Code?.Path ?? "?";
                    sapi.Logger.Notification("[translocators] {0}: placed at {1},{2},{3} on {4}, {5} blocks from its target {6},{7}",
                        job.ShapeName, x, y + 1, z, ground, r, tx, tz);
                    return $" Translocator at {x},{y + 1},{z} ({r} from its target).";
                }
        }
        sapi.Logger.Warning("[translocators] {0}: no open level ground within {1} blocks of {2},{3}; none placed", job.ShapeName, radius * 2, tx, tz);
        return $" No open ground for the translocator within {radius * 2} blocks of {tx},{tz}.";
    }

    // Open, level, natural, dry ground at this column; y is its top block.
    private bool TranslocatorGround(IslandJob job, int x, int z, bool anySurface, out int y)
    {
        var ba = sapi.World.BlockAccessor;
        y = ba.GetRainMapHeightAt(x, z);
        if (y < job.SeaLevel || y <= 0) return false;
        var p = new BlockPos(x, y, z, job.Dim);
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
                if (!Standable(ba.GetBlock(new BlockPos(x + dx, ny, z + dz, job.Dim), BlockLayersAccess.Solid), anySurface)) return false;
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
}
