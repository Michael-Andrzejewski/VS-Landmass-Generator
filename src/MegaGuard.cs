using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.ServerMods;

namespace LandmassGenerator;

// The game's own megastructure rolls keep clear of the lore islands (0.67.2).
//
// A world with a `ruins` plan line places the Better Ruins megastructures
// itself (RuinIsles.cs) and switches the game's rolls off. Every other
// Rustfall world, including ones set up before 0.67.0, keeps the rolls, which
// can only land on natural islands; this guard refuses any megastructure the
// game tries to place within 500 blocks of a plan island's or pinned story
// location's edge (plus 120 for the structure's own size, since the game
// rolls its corner), so they can still turn up around the world but never
// next to a lore island. A prefix on WorldGenStructure.TryGenerate.
public partial class LandmassGeneratorModSystem
{
    private const int MegaMargin = 500, MegaSizeAllowance = 120;
    private static (int X, int Z, long RadSq)[] _megaKeepOut = Array.Empty<(int, int, long)>();
    private static int _megaTried, _megaRefused;
    private Harmony _harmony;

    private void StartMegaGuard()
    {
        try
        {
            var target = AccessTools.Method(typeof(WorldGenStructure), "TryGenerate",
                new[] { typeof(IBlockAccessor), typeof(IWorldAccessor), typeof(BlockPos), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(string) });
            if (target == null) { sapi.Logger.Warning("[landmassgenerator] megastructure guard: WorldGenStructure.TryGenerate not found (game update?); the game's own rolls are not kept off the lore islands."); return; }
            _harmony = new Harmony("landmassgenerator.megaguard");
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(LandmassGeneratorModSystem), nameof(MegaGuardPrefix)));
            // what it did, now and then
            int logged = 0;
            sapi.Event.RegisterGameTickListener(_ =>
            {
                int tried = _megaTried;
                if (tried == logged) return;
                logged = tried;
                sapi.Logger.Notification("[landmassgenerator] megastructure guard: {0} of {1} megastructure attempts refused near lore islands", _megaRefused, tried);
            }, 120000);
        }
        catch (Exception e) { sapi.Logger.Warning("[landmassgenerator] megastructure guard not installed: {0}", e.Message); }
    }

    private void StopMegaGuard() => _harmony?.UnpatchAll("landmassgenerator.megaguard");

    // The lore islands' keep-out circles, from the plan this world draws:
    // its islands (not the ruin isles) by where their land really ends, and
    // its pinned story locations by their forced land.
    private void SetMegaKeepOut(List<IslandJob> jobs, IEnumerable<string> planLines)
    {
        var list = new List<(int X, int Z, long RadSq)>();
        foreach (var job in jobs ?? new List<IslandJob>())
        {
            if (job.Opt != null && job.Opt.ContainsKey("ruin")) continue;
            long r = (long)Math.Ceiling(Math.Max(job.Diameter / 2.0, job.LandRadius)) + MegaMargin + MegaSizeAllowance;
            list.Add((job.Cx, job.Cz, r * r));
        }
        var radii = StoryLandformRadii();
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        foreach (string raw in planLines ?? Array.Empty<string>())
        {
            string[] p = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4 || !p[0].Equals("storyloc", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(p[2], out int sx) || !int.TryParse(p[3], out int sz)) continue;
            long r = StoryEdge(radii.TryGetValue(p[1], out int lr) ? lr : 300) + MegaMargin + MegaSizeAllowance;
            list.Add((mx + sx, mz + sz, r * r));
        }
        _megaKeepOut = list.ToArray();
        _megaTried = _megaRefused = 0;
        if (list.Count > 0)
            sapi.Logger.Notification("[landmassgenerator] megastructure guard: the game's own megastructures keep {0}+ blocks off {1} lore island(s).", MegaMargin, list.Count);
    }

    private static bool MegaGuardPrefix(WorldGenStructure __instance, BlockPos startPos, ref bool __result)
    {
        if (startPos == null || !string.Equals(__instance?.Group, "megastructures", StringComparison.OrdinalIgnoreCase)) return true;
        var ko = _megaKeepOut;
        if (ko.Length == 0) return true;
        Interlocked.Increment(ref _megaTried);
        for (int i = 0; i < ko.Length; i++)
        {
            long dx = startPos.X - ko[i].X, dz = startPos.Z - ko[i].Z;
            if (dx * dx + dz * dz < ko[i].RadSq)
            {
                Interlocked.Increment(ref _megaRefused);
                __result = false;
                return false;
            }
        }
        return true;
    }
}
