using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace LandmassGenerator;

// Clearing ground for a build without leaving a straight-cut treeline (0.68.0).
//
// The structures used to empty every log, leaf and plant in a box before they
// built, so a tree standing half in the box lost the half inside and the forest
// edge came out as a sheer wall of leaves along a grid line (Michael's
// screenshot of the Hanging Gardens' furniture market on Rustfall 0.67).
//
// FellTrees takes whole trees instead. Every tree with a log in the box goes,
// trunk, branches and crown, wherever its crown reaches; every tree whose logs
// all stand outside the box keeps every leaf, even the ones hanging over the
// box (the build's own air writes trim only what it really needs). A leaf
// belongs to the tree whose logs are nearest through the canopy: a breadth-
// first wave from every log at once, felled and kept, labels each leaf with
// the first wave to reach it. Ground plants in the box still go, and vines left
// hanging on nothing go with the tree.
public partial class LandmassGeneratorModSystem
{
    private const int FellReach = 10, FellCrown = 9, FellMaxLogs = 60000;

    private static bool IsTreeLog(Block b)
    {
        if (b == null || b.Id == 0 || b.Code == null) return false;
        string p = b.Code.Path;
        if (b.Code.Domain == "moretrees") return p.StartsWith("log") || p.StartsWith("branch") || p.StartsWith("trunkroot");
        return p.StartsWith("log-grown") || p.StartsWith("log-resin") || p.StartsWith("logsection-grown") || p.StartsWith("bamboo-grown");
    }

    private static bool IsTreeLeaf(Block b)
    {
        if (b == null || b.Id == 0 || b.Code == null) return false;
        if (b.BlockMaterial == EnumBlockMaterial.Leaves) return true;
        string p = b.Code.Path;
        return p.StartsWith("leaves") || p.StartsWith("bambooleaves");
    }

    private static bool IsHanger(Block b)
    {
        if (b == null || b.Id == 0 || b.Code == null) return false;
        string p = b.Code.Path;
        return p.StartsWith("wildvine") || p.StartsWith("hanginglichen") || p.Contains("vine");
    }

    // Fells every tree with a log in [x0..x1] x [y0..y1] x [z0..z1] and clears
    // the plants in the box, through `set` (a structure's staged writer). Reads
    // the committed world. Returns how many blocks it took.
    private int FellTrees(Action<int, int, int, int> set, int dim, int x0, int z0, int x1, int z1, int y0, int y1)
    {
        var wa = sapi.World.BlockAccessor;
        var p = new BlockPos(0, 0, 0, dim);
        y0 = Math.Max(1, y0);
        y1 = Math.Min(sapi.WorldManager.MapSizeY - 2, y1);
        Block At(int x, int y, int z) { p.Set(x, y, z); return wa.GetBlock(p); }

        // 1. the logs in the box, then every log joined to them
        var felled = new HashSet<long>();
        var queue = new Queue<(int X, int Y, int Z)>();
        static long K(int x, int y, int z) => ((long)(x & 0xFFFFFF) << 32) | ((long)(z & 0xFFFFFF) << 8) | (uint)(y & 0xFF);
        int removed = 0;
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                {
                    var b = At(x, y, z);
                    if (b == null || b.Id == 0) continue;
                    if (IsTreeLog(b)) { if (felled.Add(K(x, y, z))) queue.Enqueue((x, y, z)); }
                    else if (b.BlockMaterial == EnumBlockMaterial.Plant && !IsTreeLeaf(b)) { set(x, y, z, 0); removed++; }
                }
        int lx0 = x0 - FellReach, lx1 = x1 + FellReach, lz0 = z0 - FellReach, lz1 = z1 + FellReach;
        int ly0 = Math.Max(1, y0 - FellReach), ly1 = Math.Min(sapi.WorldManager.MapSizeY - 2, y1 + FellReach + 20);
        while (queue.Count > 0 && felled.Count < FellMaxLogs)
        {
            var (x, y, z) = queue.Dequeue();
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int nx = x + dx, ny = y + dy, nz = z + dz;
                        if (nx < lx0 || nx > lx1 || nz < lz0 || nz > lz1 || ny < ly0 || ny > ly1) continue;
                        if (felled.Contains(K(nx, ny, nz)) || !IsTreeLog(At(nx, ny, nz))) continue;
                        felled.Add(K(nx, ny, nz));
                        queue.Enqueue((nx, ny, nz));
                    }
        }
        if (felled.Count == 0) return removed;

        // 2. which tree each leaf belongs to: a wave from every log in reach
        int rx0 = lx0 - FellCrown, rx1 = lx1 + FellCrown, rz0 = lz0 - FellCrown, rz1 = lz1 + FellCrown;
        int ry0 = Math.Max(1, ly0 - FellCrown), ry1 = Math.Min(sapi.WorldManager.MapSizeY - 2, ly1 + FellCrown);
        var owner = new Dictionary<long, bool>();   // true: the tree is felled
        var wave = new Queue<(int X, int Y, int Z, int D)>();
        for (int x = rx0; x <= rx1; x++)
            for (int z = rz0; z <= rz1; z++)
                for (int y = ry0; y <= ry1; y++)
                {
                    if (!IsTreeLog(At(x, y, z))) continue;
                    bool f = felled.Contains(K(x, y, z));
                    owner[K(x, y, z)] = f;
                    wave.Enqueue((x, y, z, 0));
                }
        var felledLeaves = new List<(int X, int Y, int Z)>();
        while (wave.Count > 0)
        {
            var (x, y, z, d) = wave.Dequeue();
            if (d >= FellCrown) continue;
            bool f = owner[K(x, y, z)];
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int nx = x + dx, ny = y + dy, nz = z + dz;
                        if (nx < rx0 || nx > rx1 || nz < rz0 || nz > rz1 || ny < ry0 || ny > ry1) continue;
                        long nk = K(nx, ny, nz);
                        if (owner.ContainsKey(nk) || !IsTreeLeaf(At(nx, ny, nz))) continue;
                        owner[nk] = f;
                        if (f) felledLeaves.Add((nx, ny, nz));
                        wave.Enqueue((nx, ny, nz, d + 1));
                    }
        }

        // 3. down they come, with whatever hung from them
        var gone = new List<(int X, int Y, int Z)>(felledLeaves);
        foreach (long k in felled) gone.Add(((int)(k >> 32) << 8 >> 8, (int)(k & 0xFF), (int)((k >> 8) & 0xFFFFFF) << 8 >> 8));
        foreach (var (x, y, z) in gone)
        {
            set(x, y, z, 0);
            removed++;
            foreach (var (dx, dz) in new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1) })
                for (int yy = y - 1; yy > y - 24 && yy > 1; yy--)
                {
                    if (!IsHanger(At(x + dx, yy, z + dz))) break;
                    set(x + dx, yy, z + dz, 0);
                    removed++;
                }
        }
        // orphan leaves in the box itself (no log within reach): the old rule
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    if (!owner.ContainsKey(K(x, y, z)) && IsTreeLeaf(At(x, y, z))) { set(x, y, z, 0); removed++; }
        return removed;
    }
}
