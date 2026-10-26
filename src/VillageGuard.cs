using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace LandmassGenerator;

// Dynamic Villages keep off the lore islands (0.68.0).
//
// Michael's Dynamic Villages mod founds a village wherever a player's
// surroundings offer a 64-block square of level soil. In a Rustfall world that
// is mostly the lore islands themselves: on Rustfall 0.67 one went up in the
// Hanging Gardens' lowland, one on the Ideal Home Island beside the pond, one
// on the Forge Volcano and one on the vanilla village's own site. A postfix on
// its VillageSpawner.SiteIsGood refuses any site within VillageMargin of a plan
// island's edge (the ruin isles too) or of a story location's forced land, so
// villages only grow on natural islands.
//
// The plan can still put one on a lore island on purpose:
//
//   village <mapX> <mapZ> [type]
//
// founds a Nadiya village of that type (hamlet, farmvillage, stockade; default
// farmvillage) right there, once per world, when a player first comes within
// VillageFoundRange of the spot after the setup has decorated every island.
// Dynamic Villages itself is optional; without it both halves do nothing.
public partial class LandmassGeneratorModSystem
{
    private const int VillageMargin = 60, VillageFoundRange = 600;
    private static (int X, int Z, long RadSq)[] _villageKeepOut;
    private static LandmassGeneratorModSystem _villageGuardOwner;
    private static bool _villageGuardPatched;
    private readonly List<(int X, int Z, string Type)> _planVillages = new();
    private long _villageListener;

    private void StartVillageGuard()
    {
        var spawner = AccessTools.TypeByName("DynamicVillages.VillageSpawner");
        if (spawner == null) return;   // Dynamic Villages is not installed
        var site = AccessTools.Method(spawner, "SiteIsGood");
        if (site == null) { sapi.Logger.Warning("[landmassgenerator] village guard: Dynamic Villages has no VillageSpawner.SiteIsGood (changed?); its villages may found on lore islands."); return; }
        _villageGuardOwner = this;
        _villageKeepOut = null;
        if (_villageGuardPatched) return;   // patched by an earlier world in this process
        try
        {
            new Harmony("landmassgenerator.villageguard").Patch(site, postfix: new HarmonyMethod(typeof(LandmassGeneratorModSystem), nameof(VillageSitePostfix)));
            _villageGuardPatched = true;
        }
        catch (Exception e) { sapi.Logger.Warning("[landmassgenerator] village guard not installed: {0}", e.Message); }
    }

    // Built on first use, on the main thread: by then worldgen init has read
    // the plan's islands and the game has rolled its story locations.
    private (int X, int Z, long RadSq)[] VillageKeepOut()
    {
        var list = new List<(int X, int Z, long RadSq)>();
        bool planWorld = sapi.World.Config.GetBool("rustfallWorld", false) || (_wgIslandJobs?.Count ?? 0) > 0;
        if (planWorld)
        {
            foreach (var j in _wgIslandJobs ?? new List<IslandJob>())
            {
                long r = (long)Math.Ceiling(Math.Max(j.Diameter / 2.0, j.LandRadius)) + VillageMargin;
                list.Add((j.Cx, j.Cz, r * r));
            }
            var genStory = sapi.ModLoader.GetModSystem<Vintagestory.GameContent.GenStoryStructures>();
            if (genStory?.Structures != null)
                foreach (var pair in genStory.Structures)
                {
                    long r = StoryEdge(pair.Value.LandformRadius) + VillageMargin;
                    list.Add((pair.Value.CenterPos.X, pair.Value.CenterPos.Z, r * r));
                }
            sapi.Logger.Notification("[landmassgenerator] village guard: Dynamic Villages keep {0}+ blocks off {1} lore islands and story locations.", VillageMargin, list.Count);
        }
        return list.ToArray();
    }

    private static void VillageSitePostfix(BlockPos pos, ref string reason, ref bool __result)
    {
        if (!__result || pos == null || _villageGuardOwner == null) return;
        var ko = _villageKeepOut ??= _villageGuardOwner.VillageKeepOut();
        for (int i = 0; i < ko.Length; i++)
        {
            long dx = pos.X - ko[i].X, dz = pos.Z - ko[i].Z;
            if (dx * dx + dz * dz >= ko[i].RadSq) continue;
            __result = false;
            reason = "a lore island (Rustfall keeps villages off them)";
            return;
        }
    }

    // /genvillages [check x z]: the plan's villages, or Dynamic Villages' own
    // verdict on a site (HUD), guard included. Loads the spot first.
    private TextCommandResult OnGenVillages(TextCommandCallingArgs args)
    {
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        string action = args.Parsers[0].IsMissing ? null : (string)args[0];
        if (action != "check")
        {
            var save = sapi.WorldManager.SaveGame;
            if (_planVillages.Count == 0) return TextCommandResult.Success("This world's plan names no villages.");
            return TextCommandResult.Success(string.Join("\n", _planVillages.Select(v =>
                $"{v.Type} at HUD {v.X - mx}, {v.Z - mz}: {(save.GetData<bool>(PlanVillageKey(v), false) ? "founded" : "waiting for a player within " + VillageFoundRange)}")));
        }
        if (args.Parsers[1].IsMissing || args.Parsers[2].IsMissing) return TextCommandResult.Error("Usage: /genvillages check <x> <z> (HUD)");
        var spawner = AccessTools.TypeByName("DynamicVillages.VillageSpawner");
        var modSys = AccessTools.TypeByName("DynamicVillages.DvModSystem");
        var site = spawner == null ? null : AccessTools.Method(spawner, "SiteIsGood");
        object cfg = modSys == null ? null : AccessTools.Field(modSys, "Config")?.GetValue(null);
        if (site == null || cfg == null) return TextCommandResult.Error("Dynamic Villages is not installed.");
        int x = mx + (int)args[1], z = mz + (int)args[2];
        int cx = x / GlobalConstants.ChunkSize, cz = z / GlobalConstants.ChunkSize;
        sapi.WorldManager.LoadChunkColumnPriority(cx - 2, cz - 2, cx + 2, cz + 2, new ChunkLoadOptions
        {
            OnLoaded = () =>
            {
                var a = new object[] { sapi, cfg, new BlockPos(x, 0, z, 0), null };
                bool ok = (bool)site.Invoke(null, a);
                sapi.Logger.Notification("[landmassgenerator] village site HUD {0}, {1}: {2}", x - mx, z - mz, ok ? "good" : "refused: " + a[3]);
            }
        });
        return TextCommandResult.Success("Checking; the verdict goes to the log.");
    }

    // `village <mapX> <mapZ> [type]` lines of this world's plan.
    private void ReadPlanVillages(IEnumerable<string> planLines)
    {
        _planVillages.Clear();
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        foreach (string raw in planLines ?? Array.Empty<string>())
        {
            string[] p = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3 || !p[0].Equals("village", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(p[1], out int x) || !int.TryParse(p[2], out int z)) continue;
            _planVillages.Add((mx + x, mz + z, p.Length > 3 ? p[3].ToLowerInvariant() : "farmvillage"));
        }
    }

    private static string PlanVillageKey((int X, int Z, string Type) v) => $"lgPlanVillage:{v.X},{v.Z}";

    private void StartPlanVillageWatcher()
    {
        if (_villageListener != 0 || _planVillages.Count == 0) return;
        if (AccessTools.TypeByName("DynamicVillages.VillageSpawner") == null)
        {
            sapi.Logger.Warning("[landmassgenerator] the plan names {0} village(s), but Dynamic Villages is not installed.", _planVillages.Count);
            return;
        }
        _villageListener = sapi.Event.RegisterGameTickListener(_ => PlanVillageTick(), 5000);
    }

    private bool _villageFounding;

    private void PlanVillageTick()
    {
        if (_villageFounding) return;
        var save = sapi.WorldManager.SaveGame;
        var todo = _planVillages.Where(v => !save.GetData<bool>(PlanVillageKey(v), false)).ToList();
        if (todo.Count == 0) { sapi.Event.UnregisterGameTickListener(_villageListener); _villageListener = 0; return; }
        // after the setup has decorated every island, so no tree lands in a house
        if (!save.GetData<bool>("lgRustfallSetupDone", false) && sapi.World.Config.GetBool("rustfallWorld", false)) return;
        if (!string.IsNullOrEmpty(save.GetData<string>(SetupIslandsKey, null))) return;
        foreach (var v in todo)
        {
            bool near = sapi.World.AllOnlinePlayers.Any(pl => pl.Entity != null
                && Math.Abs(pl.Entity.Pos.X - v.X) < VillageFoundRange && Math.Abs(pl.Entity.Pos.Z - v.Z) < VillageFoundRange);
            if (!near) continue;
            _villageFounding = true;
            int cx = v.X / GlobalConstants.ChunkSize, cz = v.Z / GlobalConstants.ChunkSize;
            sapi.WorldManager.LoadChunkColumnPriority(cx - 4, cz - 4, cx + 4, cz + 4, new ChunkLoadOptions
            {
                OnLoaded = () =>
                {
                    try { FoundPlanVillage(v); }
                    catch (Exception e) { sapi.Logger.Error("[landmassgenerator] founding the planned village at {0}, {1} failed: {2}", v.X, v.Z, e); }
                    finally { _villageFounding = false; }
                }
            });
            return;
        }
    }

    // Through Dynamic Villages' own founding call, the one its /village spawn
    // uses: the town hall and the start slots go up at once, and the rest is
    // the village's own growth.
    private void FoundPlanVillage((int X, int Z, string Type) v)
    {
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        var save = sapi.WorldManager.SaveGame;
        var spawner = AccessTools.TypeByName("DynamicVillages.VillageSpawner");
        var modSys = AccessTools.TypeByName("DynamicVillages.DvModSystem");
        var manager = AccessTools.TypeByName("DynamicVillages.VillageManager");
        object cfg = modSys == null ? null : AccessTools.Field(modSys, "Config")?.GetValue(null);
        var create = spawner == null ? null : AccessTools.Method(spawner, "Create");
        if (cfg == null || create == null) { sapi.Logger.Warning("[landmassgenerator] Dynamic Villages has no VillageSpawner.Create; the planned village at HUD {0}, {1} is skipped.", v.X - mx, v.Z - mz); save.StoreData(PlanVillageKey(v), true); return; }

        // one there already (founded by hand, or by an older guard-free build)?
        object inst = manager == null ? null : AccessTools.Field(manager, "Instance")?.GetValue(null);
        if (inst != null && AccessTools.Property(manager, "Villages")?.GetValue(inst) is IEnumerable villages)
            foreach (object vil in villages)
            {
                object c = AccessTools.Field(vil.GetType(), "Center")?.GetValue(vil);
                if (c == null) continue;
                int vx = (int)AccessTools.Field(c.GetType(), "X").GetValue(c), vz = (int)AccessTools.Field(c.GetType(), "Z").GetValue(c);
                if (Math.Abs(vx - v.X) < 150 && Math.Abs(vz - v.Z) < 150)
                {
                    sapi.Logger.Notification("[landmassgenerator] a village already stands at HUD {0}, {1}; the planned one is not founded again.", vx - mx, vz - mz);
                    save.StoreData(PlanVillageKey(v), true);
                    return;
                }
            }

        var pos = new BlockPos(v.X, sapi.World.SeaLevel, v.Z, 0);
        object village = create.Invoke(null, new object[] { sapi, cfg, pos, "nadiya", v.Type, 6 });
        save.StoreData(PlanVillageKey(v), true);
        if (village == null) { sapi.Logger.Warning("[landmassgenerator] Dynamic Villages could not found the planned {0} at HUD {1}, {2}.", v.Type, v.X - mx, v.Z - mz); return; }
        string name = AccessTools.Field(village.GetType(), "Name")?.GetValue(village) as string ?? "a village";
        sapi.Logger.Notification("[landmassgenerator] founded {0}, the plan's {1}, at HUD {2}, {3}.", name, v.Type, v.X - mx, v.Z - mz);
        sapi.SendMessageToGroup(GlobalConstants.GeneralChatGroup, "A village has been founded nearby: " + name, EnumChatType.Notification);
    }

    public override void Dispose()
    {
        // the patches stay for the process; what they read goes with the world
        _villageGuardOwner = null;
        _villageKeepOut = null;
        _rainCap = -1f;
        base.Dispose();
    }
}
