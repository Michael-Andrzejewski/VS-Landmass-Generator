using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace LandmassGenerator;

// Where a settled ("~") clutter prop really goes, read from the committed
// world and the clutter catalog (ClutterSupport.cs: which faces of its block
// each model touches, and its bounds). A prop stays only where something
// holds it:
//
// 1. as placed, a face its model touches meets support: a floor under it, a
//    ceiling over it, a wall beside it, or another prop placed next to it;
// 2. else a wall prop turns, so the side its model touches meets a wall;
// 3. else a small instrument whose model floats inside its block (gauges,
//    sight glasses, switches, the thin valves: built to sit on machinery)
//    turns to the nearest wall and is pushed flush against it (offsetX/Z;
//    its hitbox moves with it);
// 4. else a floor prop drops through open air (up to 4) to a floor.
// Anything else is left out. The game turns a mesh by rotateY with
// x' = x cos + z sin, z' = z cos - x sin, so each +90 degrees takes the
// model's north side to the west, west to south, south to east, east to
// north.
public partial class LandmassGeneratorModSystem
{
    private const byte TW = 1, TE = 2, TD = 4, TU = 8, TN = 16, TS = 32;

    // one +90 degree turn of the horizontal face bits
    private static byte TurnMask(byte m, int quarters)
    {
        for (int i = 0; i < ((quarters % 4) + 4) % 4; i++)
        {
            byte r = (byte)(m & (TD | TU));
            if ((m & TN) != 0) r |= TW;
            if ((m & TW) != 0) r |= TS;
            if ((m & TS) != 0) r |= TE;
            if ((m & TE) != 0) r |= TN;
            m = r;
        }
        return m;
    }

    private static (int X, int Z) FaceDir(byte bit) => bit switch
    {
        TW => (-1, 0), TE => (1, 0), TN => (0, -1), TS => (0, 1), _ => (0, 0)
    };

    private enum FitResult { Fail, Kept, Turned, Pushed, Dropped }

    private static FitResult ClutterFit(IBlockAccessor wba, BlockPos at, string blockCode, string type, HashSet<long> spots,
        ref float rot, out int y, out float ox, out float oz, bool turnToProps = false)
    {
        y = at.Y; ox = 0; oz = 0;
        if (!ClutterSupport.Types.TryGetValue((blockCode + "|" + type).ToLowerInvariant(), out var t)) return FitResult.Fail;
        var p = new BlockPos(0, 0, 0, at.dimension);
        Block B(int dx, int dy, int dz) { p.Set(at.X + dx, at.Y + dy, at.Z + dz); return wba.GetBlock(p, BlockLayersAccess.SolidBlocks); }
        bool Spot(int dx, int dy, int dz) => spots.Contains(SpotKey(at.X + dx, at.Y + dy, at.Z + dz));
        bool Solid(int dx, int dy, int dz, BlockFacing towardUs)
        {
            var b = B(dx, dy, dz);
            return b != null && b.Id != 0 && b.SideSolid[towardUs.Index];
        }
        bool IsClutterBlock(int dx, int dy, int dz) => (B(dx, dy, dz)?.Code?.Path ?? "").StartsWith("clutter");
        bool WallAt(byte bit)
        {
            var (fx, fz) = FaceDir(bit);
            var face = fx < 0 ? BlockFacing.EAST : fx > 0 ? BlockFacing.WEST : fz < 0 ? BlockFacing.SOUTH : BlockFacing.NORTH;
            return Solid(fx, 0, fz, face);
        }
        // turnToProps: a piece may also turn toward a neighbouring prop (the wreckage tangles)
        bool PropAt(byte bit)
        {
            var (fx, fz) = FaceDir(bit);
            return Spot(fx, 0, fz) || IsClutterBlock(fx, 0, fz);
        }
        bool Held(byte m)
        {
            if ((m & TD) != 0 && (Solid(0, -1, 0, BlockFacing.UP) || Spot(0, -1, 0) || IsClutterBlock(0, -1, 0))) return true;
            if ((m & TU) != 0 && (Solid(0, 1, 0, BlockFacing.DOWN) || Spot(0, 1, 0) || IsClutterBlock(0, 1, 0))) return true;
            foreach (byte bit in new[] { TW, TE, TN, TS })
            {
                if ((m & bit) == 0) continue;
                var (fx, fz) = FaceDir(bit);
                if (WallAt(bit) || Spot(fx, 0, fz) || IsClutterBlock(fx, 0, fz)) return true;
            }
            return false;
        }
        var self = B(0, 0, 0);
        if (self != null && self.Id != 0) return FitResult.Fail;

        int q = (int)Math.Round(rot / (Math.PI / 2));
        byte touch = t.Touch;
        const byte H = TW | TE | TN | TS;

        // 1. as placed
        if (touch != 0 && Held(TurnMask(touch, q))) return FitResult.Kept;

        // 2. a wall prop turns to its wall
        if ((touch & H) != 0)
            for (int k = 1; k <= 3; k++)
            {
                byte m = (byte)(TurnMask(touch, q + k) & H);
                foreach (byte bit in new[] { TW, TE, TN, TS })
                    if ((m & bit) != 0 && (WallAt(bit) || turnToProps && PropAt(bit))) { rot = (float)((q + k) * Math.PI / 2); return FitResult.Turned; }
            }

        // 3. a small instrument floating in its block: to the nearest wall, flush
        float ex = t.X2 - t.X1, ez = t.Z2 - t.Z1, ey = t.Y2 - t.Y1;
        if ((touch & (H | TD)) == 0 && Math.Min(ex, ez) <= 0.5f && ey <= 1.05f)
        {
            // the thin axis is the one the instrument faces along; its back is the nearer end
            bool alongX = ex <= ez;
            float gapMin = alongX ? t.X1 : t.Z1, gapMax = alongX ? 1 - t.X2 : 1 - t.Z2;
            byte back = alongX ? (gapMin <= gapMax ? TW : TE) : (gapMin <= gapMax ? TN : TS);
            float gap = Math.Min(gapMin, gapMax);
            for (int k = 0; k < 4; k++)
            {
                byte bit = TurnMask(back, q + k);
                if (!WallAt(bit)) continue;
                rot = (float)((q + k) * Math.PI / 2);
                var (fx, fz) = FaceDir(bit);
                ox = fx * gap; oz = fz * gap;
                return FitResult.Pushed;
            }
        }

        // 4. a floor prop drops to a floor
        if ((touch & TD) != 0)
            for (int d = 1; d <= 4; d++)
            {
                var b = B(0, -d, 0);
                if (b != null && b.Id != 0) return FitResult.Fail;
                var under = B(0, -d - 1, 0);
                if (under != null && under.Id != 0 && under.SideSolid[BlockFacing.UP.Index]) { y = at.Y - d; return FitResult.Dropped; }
            }
        return FitResult.Fail;
    }

    // the neighbour cells (dx, dy, dz) a prop's turned model touches
    private static List<(int X, int Y, int Z)> TouchedCells(string blockCode, string type, float rot)
    {
        var res = new List<(int, int, int)>();
        if (!ClutterSupport.Types.TryGetValue((blockCode + "|" + type).ToLowerInvariant(), out var t)) return res;
        byte m = TurnMask(t.Touch, (int)Math.Round(rot / (Math.PI / 2)));
        if ((m & TD) != 0) res.Add((0, -1, 0));
        if ((m & TU) != 0) res.Add((0, 1, 0));
        foreach (byte bit in new[] { TW, TE, TN, TS })
            if ((m & bit) != 0) { var (fx, fz) = FaceDir(bit); res.Add((fx, 0, fz)); }
        return res;
    }

    private static long SpotKey(int x, int y, int z) => ((long)(x & 0x3FFFFFF) << 38) | ((long)(y & 0xFFF) << 26) | (uint)(z & 0x3FFFFFF);
}
