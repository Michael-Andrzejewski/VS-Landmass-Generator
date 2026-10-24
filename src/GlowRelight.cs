using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace LandmassGenerator;

// Structure lights re-lit when a player first comes near, the way the
// Underwater Horrors ruins fix theirs. A light written while nobody is
// around (a headless world build, a far island) reads as lit at the
// emitter, but in game the ghostlights of the megastructures shone on
// nothing. Re-setting each emitter through the world accessor with a
// player there relights it the normal way, and the client gets the result.
//
// Every BuildStruct that placed lights saves one batch: its centre and the
// emitter positions. The tick looks for a player within RelightRange of a
// batch centre and re-sets every emitter whose chunk is loaded; the batch
// is done once all of them have been, and the saved list shrinks with it.
public partial class LandmassGeneratorModSystem
{
    private const string RelightKey = "lgRelight";
    private const int RelightRange = 160;
    private List<string> _relight;
    private long _relightListener;

    private List<string> RelightList()
        => _relight ??= sapi.WorldManager.SaveGame.GetData<List<string>>(RelightKey, null) ?? new List<string>();

    // Batch format: "dim|cx|cz|x,y,z;x,y,z;..."
    private void QueueRelight(int dim, IList<(int X, int Y, int Z, int Id)> spots)
    {
        if (spots == null || spots.Count == 0) return;
        long sx = 0, sz = 0;
        foreach (var s in spots) { sx += s.X; sz += s.Z; }
        int cx = (int)(sx / spots.Count), cz = (int)(sz / spots.Count);
        string pts = string.Join(";", spots.Select(s => s.X + "," + s.Y + "," + s.Z));
        var list = RelightList();
        list.Add(dim + "|" + cx + "|" + cz + "|" + pts);
        sapi.WorldManager.SaveGame.StoreData(RelightKey, list);
        StartRelightWatcher();
    }

    private void StartRelightWatcher()
    {
        if (_relightListener != 0) return;
        if (RelightList().Count == 0) return;
        _relightListener = sapi.Event.RegisterGameTickListener(RelightTick, 3000);
    }

    private void RelightTick(float dt)
    {
        var list = RelightList();
        if (list.Count == 0) return;
        var players = sapi.World.AllOnlinePlayers.Where(p => p.Entity != null).ToArray();
        if (players.Length == 0) return;
        var acc = sapi.World.BlockAccessor;
        bool changed = false;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var parts = list[i].Split('|');
            if (parts.Length < 4) { list.RemoveAt(i); changed = true; continue; }
            int dim = int.Parse(parts[0], CultureInfo.InvariantCulture);
            int cx = int.Parse(parts[1], CultureInfo.InvariantCulture), cz = int.Parse(parts[2], CultureInfo.InvariantCulture);
            bool near = players.Any(p =>
            {
                double dx = p.Entity.Pos.X - cx, dz = p.Entity.Pos.Z - cz;
                return p.Entity.Pos.Dimension == dim && dx * dx + dz * dz < (double)RelightRange * RelightRange;
            });
            if (!near) continue;

            var left = new List<string>();
            int lit = 0;
            var pos = new BlockPos(0, 0, 0, dim);
            foreach (string pt in parts[3].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var c = pt.Split(',');
                pos.Set(int.Parse(c[0], CultureInfo.InvariantCulture), int.Parse(c[1], CultureInfo.InvariantCulture), int.Parse(c[2], CultureInfo.InvariantCulture));
                if (acc.GetChunkAtBlockPos(pos) == null) { left.Add(pt); continue; }
                Block b = acc.GetBlock(pos, BlockLayersAccess.SolidBlocks);
                if (b == null || b.Id == 0) continue;   // mined out: nothing to light
                byte[] hsv = b.GetLightHsv(acc, pos);   // a Jonas lamp's light comes from its block entity's type
                if (hsv == null || hsv[2] == 0) continue;
                if (acc.GetBlockEntity(pos) != null)
                {
                    // the lamp's own relight: out with its light, exchange in
                    // place so the block entity (and its type) stays
                    acc.RemoveBlockLight(hsv, pos);
                    acc.ExchangeBlock(b.BlockId, pos);
                }
                else
                {
                    // Out and back in: a same-id SetBlock may change no light
                    // at all, so the emitter is removed and set again.
                    acc.SetBlock(0, pos);
                    acc.SetBlock(b.BlockId, pos);
                }
                acc.MarkBlockDirty(pos);
                lit++;
            }
            if (lit > 0)
                sapi.Logger.Notification("[landmassgen] relit {0} structure light(s) near {1}, {2} ({3} waiting on chunks)",
                    lit, cx - sapi.WorldManager.MapSizeX / 2, cz - sapi.WorldManager.MapSizeZ / 2, left.Count);
            string rest = left.Count == 0 ? null : parts[0] + "|" + parts[1] + "|" + parts[2] + "|" + string.Join(";", left);
            if (rest != list[i]) { if (rest == null) list.RemoveAt(i); else list[i] = rest; changed = true; }
        }
        if (changed) sapi.WorldManager.SaveGame.StoreData(RelightKey, list);
    }
}
