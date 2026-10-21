using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace LandmassGenerator;

// Mine props along a carved cave: timber frames (two posts and a cap beam
// spanning the tunnel) every `timbers=` steps, and empty torch holders on
// the wall every `holders=` steps, for players to light the mine with
// torches. Runs on the real blocks after the carve, so every prop stands on
// rock and touches it. A step where the tunnel is too wide or too tall for a
// timber (a hall) or where water is present simply gets nothing. Safe to run
// again: a spot that is not open air is never overwritten.
public partial class LandmassGeneratorModSystem
{
    // A flag in the save marks an island whose props are in, so /genchests
    // never furnishes the same mine twice (a second pass would find the
    // tunnels narrowed by the first pass's timbers and add more).
    private string PropsDoneKey(IslandJob job) => $"lgPropsDone:{job.ShapeName}@{job.Cx},{job.Cz}";

    private string PlaceCaveProps(IslandJob job, bool skipIfDone = false)
    {
        var caves = job.Shape?.Caves;
        if (caves == null || job.CaveSteps == null) return "";
        if (!caves.Any(c => c.Def.Timbers > 0 || c.Def.Holders > 0 || c.Def.Machinery > 0 || c.Def.GiantCount > 0 || c.Def.DeepGiantCode != null || c.Def.MouthGiantCode != null)) return "";
        if (skipIfDone && sapi.WorldManager.SaveGame.GetData<bool>(PropsDoneKey(job), false)) return "";
        sapi.WorldManager.SaveGame.StoreData(PropsDoneKey(job), true);

        var world = sapi.World.BlockAccessor;
        int postId = sapi.World.GetBlock(new AssetLocation("game", "log-placed-aged-ud"))?.BlockId ?? 0;
        int beamNS = sapi.World.GetBlock(new AssetLocation("game", "log-placed-aged-ns"))?.BlockId ?? 0;
        int beamWE = sapi.World.GetBlock(new AssetLocation("game", "log-placed-aged-we"))?.BlockId ?? 0;
        int frames = 0, holders = 0;
        var p = new BlockPos(0, 0, 0, job.Dim);

        bool Open(int x, int y, int z)
        {
            p.Set(x, y, z);
            return (world.GetBlock(p, BlockLayersAccess.SolidBlocks)?.Id ?? 1) == 0
                && (world.GetBlock(p, BlockLayersAccess.Fluid)?.Id ?? 1) == 0;
        }
        bool Solid(int x, int y, int z)
        {
            p.Set(x, y, z);
            Block b = world.GetBlock(p, BlockLayersAccess.SolidBlocks);
            return b != null && b.Id != 0 && b.SideSolid[BlockFacing.UP.Index];
        }
        void Put(int x, int y, int z, int id) { if (id == 0) return; p.Set(x, y, z); world.SetBlock(id, p); }

        string extra = "";
        foreach (var cm in caves)
        {
            var def = cm.Def;
            if (def.Flooded) continue;
            var steps = job.CaveSteps.Where(s => s.Def == def).ToList();
            // Machinery first: the timbers below then fit around it.
            if (def.Machinery > 0 || def.GiantCount > 0 || def.DeepGiantCode != null || def.MouthGiantCode != null)
                extra += PlaceMineMachinery(job, def, steps, Open, Solid);
            if (def.Timbers <= 0 && def.Holders <= 0) continue;
            for (int i = 1; i < steps.Count; i++)
            {
                bool doFrame = def.Timbers > 0 && i % def.Timbers == 0;
                bool doHolder = def.Holders > 0 && i % def.Holders == def.Holders / 2;
                if (!doFrame && !doHolder) continue;
                var a = steps[i - 1]; var s = steps[i];
                double dx = s.X - a.X, dz = s.Z - a.Z, len = Math.Sqrt(dx * dx + dz * dz);
                if (len < 0.2 || len > 4) continue;            // a branch start, not a stride
                dx /= len; dz /= len;
                // Snap the across-tunnel direction to an axis so posts and the
                // beam line up on the block grid.
                bool acrossX = Math.Abs(dz) >= Math.Abs(dx);   // tunnel runs mostly N-S: span along X
                int cx = (int)Math.Floor(s.X), cz = (int)Math.Floor(s.Z), cy = (int)Math.Floor(s.Y);
                if (!Open(cx, cy, cz)) continue;
                int floor = cy; while (floor > cy - 8 && Open(cx, floor - 1, cz)) floor--;
                if (!Solid(cx, floor - 1, cz)) continue;
                int ceil = cy; while (ceil < cy + 10 && Open(cx, ceil + 1, cz)) ceil++;
                int height = ceil - floor + 1;
                int ux = acrossX ? 1 : 0, uz = acrossX ? 0 : 1;
                int left = 0; while (left < 6 && Open(cx - ux * (left + 1), floor, cz - uz * (left + 1))) left++;
                int right = 0; while (right < 6 && Open(cx + ux * (right + 1), floor, cz + uz * (right + 1))) right++;
                int width = left + right + 1;

                if (doFrame && height >= 3 && height <= 6 && width >= 3 && width <= 7)
                {
                    int lx = cx - ux * left, lz = cz - uz * left, rx = cx + ux * right, rz = cz + uz * right;
                    bool ok = Solid(lx, floor - 1, lz) && Solid(rx, floor - 1, rz);
                    for (int y = floor; y < floor + height && ok; y++) ok = Open(lx, y, lz) && Open(rx, y, rz);
                    if (ok)
                    {
                        int beamY = floor + height - 1;
                        for (int y = floor; y < beamY; y++) { Put(lx, y, lz, postId); Put(rx, y, rz, postId); }
                        for (int k = 0; k < width; k++)
                        {
                            int bx = lx + ux * k, bz = lz + uz * k;
                            if (Open(bx, beamY, bz)) Put(bx, beamY, bz, acrossX ? beamWE : beamNS);
                        }
                        frames++;
                    }
                }
                if (doHolder && height >= 3)
                {
                    // On the wall at head height, on whichever side has rock.
                    int hy = floor + 2;
                    foreach (int sgn in new[] { -1, 1 })
                    {
                        int reach = sgn < 0 ? left : right;
                        int hx = cx + sgn * ux * reach, hz = cz + sgn * uz * reach;
                        int wx = hx + sgn * ux, wz = hz + sgn * uz;
                        if (!Open(hx, hy, hz) || !Solid(wx, hy, wz)) continue;
                        // "-north" hangs on a wall to its north (the model's plate
                        // sits on its north face), and so on.
                        string side = acrossX ? (sgn < 0 ? "west" : "east") : (sgn < 0 ? "north" : "south");
                        int hid = sapi.World.GetBlock(new AssetLocation("game", $"torchholder-aged-empty-{side}"))?.BlockId ?? 0;
                        if (hid == 0) break;
                        Put(hx, hy, hz, hid);
                        holders++;
                        break;
                    }
                }
            }
        }
        return (frames + holders > 0 ? $" Mine props: {frames} timber frame(s), {holders} torch holder(s)." : "") + extra;
    }
}
