using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace LandmassGenerator;

// An invasion when a player first sets foot on the Great Machine's island (0.73.3).
//
// A Rift Lure (the Rift Lure mod) only checks for players while its own chunk is loaded, and the Great
// Machine's island is 1,200 blocks across, so a lure at its heart would sleep through a landing on the coast.
// Instead this watches the players: the first survival player standing on the island's land gets a dormant
// lure planted on open ground 20 to 40 blocks ahead of them, toward the island's centre. The lure wakes at once
// (the player is well inside its range): a temporal storm, devastation round it, rifts. Once per world.
// Without the Rift Lure mod nothing happens.
public partial class LandmassGeneratorModSystem
{
    private static readonly string[] LureIslands = { "great_machine" };
    private const string IslandLureCode = "riftlure:riftlure-dormant";
    private long _islandLureListener;

    private void StartIslandLureWatcher()
    {
        if (_islandLureListener != 0) return;
        _islandLureListener = sapi.Event.RegisterGameTickListener(_ => IslandLureTick(), 2000);
    }

    private void IslandLureTick()
    {
        if (_wgIslandJobs == null) return;
        foreach (var job in _wgIslandJobs)
        {
            if (job.ShapeName == null || !LureIslands.Contains(job.ShapeName, StringComparer.OrdinalIgnoreCase)) continue;
            string doneKey = $"lgIslandLureDone:{job.ShapeName}@{job.Cx},{job.Cz}";
            if (sapi.WorldManager.SaveGame.GetData<bool>(doneKey, false)) continue;

            foreach (IServerPlayer player in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>())
            {
                if (player.ConnectionState != EnumClientState.Playing) continue;
                var e = player.Entity;
                if (e == null || !e.Alive || e.Pos.Dimension != job.Dim) continue;
                var mode = player.WorldData.CurrentGameMode;
                if (mode == EnumGameMode.Creative || mode == EnumGameMode.Spectator) continue;
                double dx = e.Pos.X - job.Cx, dz = e.Pos.Z - job.Cz;
                if (dx * dx + dz * dz > job.LandRadius * job.LandRadius) continue;
                if (!e.OnGround || e.Swimming || e.FeetInLiquid) continue;   // set foot on land, not sailing past
                if (PlantIslandLure(job, player)) sapi.WorldManager.SaveGame.StoreData(doneKey, true);
                break;
            }
        }
    }

    private bool PlantIslandLure(IslandJob job, IServerPlayer player)
    {
        Block lure = sapi.World.GetBlock(new AssetLocation(IslandLureCode));
        if (lure == null) return false;   // Rift Lure not installed
        var ba = sapi.World.BlockAccessor;
        var e = player.Entity;
        double dx = job.Cx - e.Pos.X, dz = job.Cz - e.Pos.Z, len = Math.Sqrt(dx * dx + dz * dz);
        if (len < 1) { dx = 1; dz = 0; len = 1; }
        dx /= len; dz /= len;
        // Ahead of the player, toward the centre; then any side; then right beside them.
        for (int dist = 25; dist <= 40; dist += 3)
            for (int turn = 0; turn < 8; turn++)
            {
                double a = turn * Math.PI / 4;
                double rx = dx * Math.Cos(a) - dz * Math.Sin(a), rz = dx * Math.Sin(a) + dz * Math.Cos(a);
                int x = (int)Math.Floor(e.Pos.X + rx * dist), z = (int)Math.Floor(e.Pos.Z + rz * dist);
                if (ba.GetChunkAtBlockPos(new BlockPos(x, (int)e.Pos.Y, z, job.Dim)) == null) continue;
                if (!TranslocatorGround(job.SeaLevel, job.Dim, x, z, true, out int y)) continue;
                var at = new BlockPos(x, y + 1, z, job.Dim);
                ba.SetBlock(lure.BlockId, at);
                sapi.Logger.Notification("[islandlure] {0} set foot on {1}: rift lure planted at HUD {2}, {3}, {4}",
                    player.PlayerName, job.ShapeName, x - sapi.WorldManager.MapSizeX / 2, y + 1, z - sapi.WorldManager.MapSizeZ / 2);
                return true;
            }
        sapi.Logger.Warning("[islandlure] {0} is on {1}, but no open ground 25 to 40 blocks from them for the lure; trying again", player.PlayerName, job.ShapeName);
        return false;
    }
}
