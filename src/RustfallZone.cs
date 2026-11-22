using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using System.Collections.Concurrent;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace LandmassGenerator;

// The Rustfall zone (0.74.0): the convex hull round every purposefully placed island (the plan's islands,
// not the ruin isles, plus the pinned story locations), and three rules that hang off it.
//
// - Wild sea outside it (world config rustfallWildOcean, e.g. 0.45): beyond the hull the ocean presses the
//   landforms down less, so more vanilla landforms breach as natural islands and those that breach are
//   bigger. Blended in over WildBlend blocks past the hull's edge.
// - Spreading Devastation only far outside it (world config rustfallDevastationOutside, e.g. 1000): a
//   Harmony patch on SD treats every block and chunk closer than that to the hull as warded, so SD never
//   spawns, spreads, bleeds or converts there; its edge spawning is switched on (for this run only) so
//   devastation turns up near players who go out that far. If any patch can't be applied SD is paused
//   instead, so it can never run unrestricted.
// - No hostile creatures on the starter island: natural spawns there are refused, and anything hostile
//   that appears there another way (a storm, a lure) is removed at once.
public partial class LandmassGeneratorModSystem
{
    private List<Vec2d> _zoneHull;
    private double _devaOutside;
    private readonly ConcurrentDictionary<long, double> _devaChunkDist = new();
    private readonly object _zoneLock = new();
    internal static LandmassGeneratorModSystem ZoneInstance;
    private Harmony _zoneHarmony;
    private const double WildBlend = 400;

    // ── the hull ────────────────────────────────────────────────────────

    /// <summary>The Rustfall zone's hull in world block coordinates, or null without a plan.</summary>
    private List<Vec2d> ZoneHull()
    {
        lock (_zoneLock) return ZoneHullLocked();
    }

    private List<Vec2d> ZoneHullLocked()
    {
        if (_zoneHull != null) return _zoneHull;
        if (_wgIslandJobs == null || _wgIslandJobs.Count == 0) return null;
        var pts = new List<Vec2d>();
        void Ring(double cx, double cz, double r)
        {
            for (int a = 0; a < 24; a++)
            {
                double t = a * Math.PI / 12;
                pts.Add(new Vec2d(cx + Math.Cos(t) * r, cz + Math.Sin(t) * r));
            }
        }
        foreach (var job in _wgIslandJobs)
        {
            if (job.Opt != null && job.Opt.ContainsKey("ruin")) continue;
            Ring(job.Cx, job.Cz, Math.Max(job.Diameter / 2.0, job.LandRadius));
        }
        // The story locations the plan pins, at their PLANNED spots (read from the plan, so the zone is the
        // same before and after setup moves them there). Unpinned stories, which the game places wherever it
        // likes, never stretch the zone.
        string[] lines = StoredWorldPlanLines();
        if (lines == null)
        {
            string planName = sapi.WorldManager.SaveGame.GetData<string>("lgWorldPlanName", null) ?? "worldplan";
            string planPath = System.IO.Path.Combine(shapeFolder, planName + ".txt");
            if (System.IO.File.Exists(planPath)) lines = System.IO.File.ReadAllLines(planPath);
        }
        var radii = StoryLandformRadii();
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        foreach (string raw in lines ?? Array.Empty<string>())
        {
            string[] p = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4 || !p[0].Equals("storyloc", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(p[2], out int sx) || !int.TryParse(p[3], out int sz)) continue;
            Ring(mx + sx, mz + sz, StoryEdge(radii.TryGetValue(p[1], out int lr) ? lr : 300));
        }
        _zoneHull = ConvexHull(pts);
        _devaChunkDist.Clear();
        double minX = _zoneHull.Min(v => v.X), maxX = _zoneHull.Max(v => v.X), minZ = _zoneHull.Min(v => v.Y), maxZ = _zoneHull.Max(v => v.Y);
        sapi.Logger.Notification("[zone] Rustfall zone hull: {0} corners, HUD x {1:0} to {2:0}, z {3:0} to {4:0}",
            _zoneHull.Count, minX - mx, maxX - mx, minZ - mz, maxZ - mz);
        return _zoneHull;
    }

    private static List<Vec2d> ConvexHull(List<Vec2d> pts)
    {
        var p = pts.OrderBy(v => v.X).ThenBy(v => v.Y).ToList();
        if (p.Count < 3) return p;
        double Cross(Vec2d o, Vec2d a, Vec2d b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<Vec2d>();
        foreach (var v in p) { while (h.Count >= 2 && Cross(h[^2], h[^1], v) <= 0) h.RemoveAt(h.Count - 1); h.Add(v); }
        int lower = h.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--) { var v = p[i]; while (h.Count >= lower && Cross(h[^2], h[^1], v) <= 0) h.RemoveAt(h.Count - 1); h.Add(v); }
        h.RemoveAt(h.Count - 1);
        return h;   // counter-clockwise
    }

    /// <summary>How far (blocks) a world position lies outside the Rustfall zone: 0 inside it.</summary>
    internal double ZoneDistance(double x, double z)
    {
        var h = ZoneHull();
        if (h == null || h.Count < 3) return double.MaxValue;
        bool inside = true;
        double best = double.MaxValue;
        for (int i = 0; i < h.Count; i++)
        {
            Vec2d a = h[i], b = h[(i + 1) % h.Count];
            double ex = b.X - a.X, ez = b.Y - a.Y;
            if (ex * (z - a.Y) - ez * (x - a.X) < 0) inside = false;   // right of a ccw edge: outside
            double len2 = ex * ex + ez * ez;
            double t = len2 > 0 ? Math.Clamp(((x - a.X) * ex + (z - a.Y) * ez) / len2, 0, 1) : 0;
            double dx = x - (a.X + t * ex), dz = z - (a.Y + t * ez);
            best = Math.Min(best, dx * dx + dz * dz);
        }
        return inside ? 0 : Math.Sqrt(best);
    }

    // ── wild sea outside the zone ───────────────────────────────────────

    /// <summary>The ocean depth factor for a position: 1 inside the zone, falling to rustfallWildOcean past it.</summary>
    private double WildOceanFactor(double wildOcean, double x, double z)
    {
        double d = ZoneDistance(x, z);
        if (d <= 0) return 1;
        double t = Math.Min(1, d / WildBlend);
        t = t * t * (3 - 2 * t);
        return 1 + (wildOcean - 1) * t;
    }

    private void ApplyWildOcean(IMapRegion mapRegion, int regionX, int regionZ)
    {
        var wc = sapi.WorldManager.SaveGame.WorldConfiguration;
        double wild = ParseD(wc.GetString("rustfallWildOcean", "1"), 1);
        if (wild >= 0.999 || wild <= 0) return;
        if (ZoneHull() == null) return;
        var om = mapRegion.OceanMap;
        if (om?.Data == null || om.Data.Length == 0) return;
        int inner = om.InnerSize, pad = om.TopLeftPadding, size = om.Size;
        int cell = sapi.WorldManager.RegionSize / Math.Max(1, inner);
        int changed = 0;
        for (int iz = 0; iz < size; iz++)
            for (int ix = 0; ix < size; ix++)
            {
                int wx = (regionX * inner + ix - pad) * cell + cell / 2;
                int wz = (regionZ * inner + iz - pad) * cell + cell / 2;
                double f = WildOceanFactor(wild, wx, wz);
                if (f >= 0.999) continue;
                int i = iz * size + ix;
                om.Data[i] = (int)Math.Round(om.Data[i] * f);
                changed++;
            }
    }

    // ── Spreading Devastation only far outside the zone ─────────────────

    private void StartDevastationZone()
    {
        var wc = sapi.WorldManager.SaveGame.WorldConfiguration;
        if (!wc.GetBool("rustfallWorld", false)) return;
        _devaOutside = ParseD(wc.GetString("rustfallDevastationOutside", "0"), 0);
        if (_devaOutside <= 0) return;
        ModSystem sd = sapi.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "SpreadingDevastation.SpreadingDevastationModSystem");
        if (sd == null) { sapi.Logger.Notification("[zone] Spreading Devastation is not installed"); return; }
        ZoneInstance = this;
        var t = sd.GetType();
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        MethodInfo blockProt = t.GetMethod("IsBlockProtectedByRiftWard", F, null, new[] { typeof(int), typeof(int), typeof(int) }, null);
        MethodInfo blockProtPos = t.GetMethod("IsBlockProtectedByRiftWard", F, null, new[] { typeof(BlockPos) }, null);
        MethodInfo chunkProt = t.GetMethod("IsChunkProtectedByRiftWard", F, null, new[] { typeof(int), typeof(int) }, null);
        MethodInfo resolve = t.GetMethods(F).FirstOrDefault(m => m.Name == "TryResolveDevastation" && m.GetParameters().Length == 7);
        MethodInfo form = t.GetMethods(F).FirstOrDefault(m => m.Name == "TryGetDevastatedForm" && m.GetParameters().Length == 4 && m.GetParameters()[1].ParameterType == typeof(BlockPos));
        bool ok = blockProt != null && blockProtPos != null && chunkProt != null && resolve != null && form != null;
        if (ok)
        {
            try
            {
                _zoneHarmony = new Harmony("landmassgenerator.rustfallzone");
                var me = typeof(LandmassGeneratorModSystem);
                _zoneHarmony.Patch(blockProt, postfix: new HarmonyMethod(me.GetMethod(nameof(SdBlockProtPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                _zoneHarmony.Patch(blockProtPos, postfix: new HarmonyMethod(me.GetMethod(nameof(SdBlockPosProtPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                _zoneHarmony.Patch(chunkProt, postfix: new HarmonyMethod(me.GetMethod(nameof(SdChunkProtPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                _zoneHarmony.Patch(resolve, prefix: new HarmonyMethod(me.GetMethod(nameof(SdResolvePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                _zoneHarmony.Patch(form, prefix: new HarmonyMethod(me.GetMethod(nameof(SdFormPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { sapi.Logger.Error(e); ok = false; }
        }
        if (!ok)
        {
            sapi.Logger.Error("[zone] Could not limit Spreading Devastation to the outer sea (this SD version is not the one Rustfall knows). Pausing SD so it cannot reach the Rustfall islands.");
            sapi.ChatCommands.ExecuteUnparsed("/devastate stop", new TextCommandCallingArgs { Caller = new Caller { Type = EnumCallerType.Console, CallerRole = "admin", CallerPrivileges = new[] { "*" }, FromChatGroupId = GlobalConstants.ConsoleGroup } }, _ => { });
            return;
        }
        // Edge spawning for this run (not written to SD's config file): devastation turns up near players
        // who stay out past the line, since nothing else seeds it in a Rustfall world.
        try
        {
            object cfg = t.GetField("config", F)?.GetValue(sd);
            cfg?.GetType().GetProperty("EdgeSpawningEnabled")?.SetValue(cfg, true);
        }
        catch (Exception e) { sapi.Logger.Warning("[zone] Could not switch on SD edge spawning: {0}", e.Message); }
        // A Rustfall world used to pause SD at setup; with the zone limit it runs.
        if (!sapi.WorldManager.SaveGame.GetData<bool>("lgDevaZoneStarted", false))
        {
            sapi.ChatCommands.ExecuteUnparsed("/devastate start", new TextCommandCallingArgs { Caller = new Caller { Type = EnumCallerType.Console, CallerRole = "admin", CallerPrivileges = new[] { "*" }, FromChatGroupId = GlobalConstants.ConsoleGroup } }, _ => { });
            sapi.WorldManager.SaveGame.StoreData("lgDevaZoneStarted", true);
        }
        ZoneHull();
        sapi.Logger.Notification("[zone] Spreading Devastation limited to {0:0} blocks or more outside the Rustfall zone", _devaOutside);
    }

    /// <summary>True where devastation must never be: closer than the set distance to the zone (or no zone known yet).</summary>
    internal bool NoDevastationAt(double x, double z)
    {
        if (ZoneHull() == null) return true;
        long key = ((long)Math.Floor(x / 32) << 32) ^ (uint)(int)Math.Floor(z / 32);
        if (!_devaChunkDist.TryGetValue(key, out double cd))
        {
            cd = ZoneDistance(Math.Floor(x / 32) * 32 + 16, Math.Floor(z / 32) * 32 + 16);
            if (_devaChunkDist.Count > 200000) _devaChunkDist.Clear();
            _devaChunkDist[key] = cd;
        }
        if (cd < _devaOutside - 24) return true;      // the whole chunk is inside the line
        if (cd > _devaOutside + 24) return false;     // the whole chunk is past it
        return ZoneDistance(x, z) < _devaOutside;
    }

    private bool NoDevastationInChunk(int chunkX, int chunkZ)
    {
        // Conservative: any part of the chunk inside the line protects it.
        double cx = chunkX * 32 + 16, cz = chunkZ * 32 + 16;
        return ZoneHull() == null || ZoneDistance(cx, cz) < _devaOutside + 24;
    }

    private static void SdBlockProtPostfix(int x, int z, ref bool __result)
    {
        if (!__result && ZoneInstance != null && ZoneInstance.NoDevastationAt(x, z)) __result = true;
    }
    private static void SdBlockPosProtPostfix(BlockPos pos, ref bool __result)
    {
        if (!__result && pos != null && ZoneInstance != null && ZoneInstance.NoDevastationAt(pos.X, pos.Z)) __result = true;
    }
    private static void SdChunkProtPostfix(int chunkX, int chunkZ, ref bool __result)
    {
        if (!__result && ZoneInstance != null && ZoneInstance.NoDevastationInChunk(chunkX, chunkZ)) __result = true;
    }
    private static bool SdResolvePrefix(int x, int z, ref Block target, ref string devastatedForm, ref bool isWater, ref bool __result)
    {
        if (ZoneInstance == null || !ZoneInstance.NoDevastationAt(x, z)) return true;
        target = null; devastatedForm = null; isWater = false; __result = false;
        return false;
    }
    private static bool SdFormPrefix(BlockPos pos, ref string devastatedBlock, ref string regeneratesTo, ref bool __result)
    {
        if (pos == null || ZoneInstance == null || !ZoneInstance.NoDevastationAt(pos.X, pos.Z)) return true;
        devastatedBlock = null; regeneratesTo = null; __result = false;
        return false;
    }

    // ── safe islands ────────────────────────────────────────────────────
    // The starter island has no hostile creatures at all; the Ideal Home Island (the village) and the rock and
    // wreck islands have no wolves or bears (0.74.2, 0.74.3). Natural spawns there are refused, and any such creature that appears another way (a
    // storm, worldgen, a lure) is removed as it spawns or loads. Everywhere else spawns as vanilla.

    private static readonly string[] HostilePaths = { "drifter", "bowtorn", "shiver", "bell", "locust", "wolf", "bear", "hyena", "eidolon", "erel" };
    private static readonly string[] HostileDomains = { "oversizedhorrors", "underwaterhorrors" };
    private static readonly string[] PredatorPaths = { "wolf", "bear" };
    private record SafeIsland(string Shape, bool AllHostiles);
    // No wolves or bears either on the bare rock and wreck islands, where only the climate map's forest value
    // would let them spawn (0.74.3).
    private static readonly SafeIsland[] SafeIslands =
    {
        new("starter_island", true), new("ideal_house_island", false),
        new("chainfield", false), new("wreckage_maelstrom", false), new("colossus_deep", false), new("lighthouse", false),
    };
    private List<(IslandJob Job, bool All)> _safeJobs;

    private void StartSafeStarter()
    {
        if (!sapi.WorldManager.SaveGame.WorldConfiguration.GetBool("rustfallWorld", false)) return;
        sapi.Event.OnTrySpawnEntity += (IBlockAccessor ba, ref EntityProperties props, Vec3d at, long herdId) =>
            !(props != null && Forbidden(props.Code, at.X, at.Z));
        void Check(Entity e)
        {
            if (e == null || e is EntityPlayer || !Forbidden(e.Code, e.ServerPos.X, e.ServerPos.Z)) return;
            sapi.Event.EnqueueMainThreadTask(() => { if (e.Alive) e.Die(EnumDespawnReason.Removed); }, "lg-safeisland");
        }
        sapi.Event.OnEntitySpawn += Check;
        sapi.Event.OnEntityLoaded += Check;
    }

    internal static bool IsHostile(AssetLocation code)
    {
        if (code == null) return false;
        if (HostileDomains.Contains(code.Domain)) return true;
        string p = code.Path;
        foreach (string h in HostilePaths) if (p.StartsWith(h)) return true;
        return false;
    }

    private static bool IsPredator(AssetLocation code)
    {
        if (code == null || code.Domain != "game") return false;
        foreach (string h in PredatorPaths) if (code.Path.StartsWith(h + "-")) return true;
        return false;
    }

    /// <summary>True when a creature of this code may not be at this spot (a safe island's rule).</summary>
    private bool Forbidden(AssetLocation code, double x, double z)
    {
        if (code == null) return false;
        bool hostile = IsHostile(code), predator = IsPredator(code);
        if (!hostile && !predator) return false;
        if (_safeJobs == null && _wgIslandJobs != null)
            _safeJobs = _wgIslandJobs.Where(j => SafeIslands.Any(s => s.Shape == j.ShapeName))
                .Select(j => (j, SafeIslands.First(s => s.Shape == j.ShapeName).AllHostiles)).ToList();
        if (_safeJobs == null) return false;
        foreach (var (job, all) in _safeJobs)
        {
            if (!(all ? hostile : predator)) continue;
            double r = Math.Max(job.Diameter / 2.0, job.LandRadius) + 24;
            double dx = x - job.Cx, dz = z - job.Cz;
            if (dx * dx + dz * dz <= r * r) return true;
        }
        return false;
    }

    // ── wolf forests (0.74.2) ───────────────────────────────────────────
    // Wolves and bears only spawn where the climate's forest value is high enough (wolves 0.5+), and that map
    // knows nothing of the hand-built forests, so the Forest Island read anywhere from 0.07 to 0.89. These
    // islands get at least the given forest value across their land (fading out over 64 blocks past the
    // edge), on new map regions and on old ones as they load. Only ever raises it.
    // Climate decides which animal: Forest, Blackfen, the swamplands and Cardinals suit wolves and brown or
    // black bears; the Volcano Ring (24 C) suits sun bears; the warm granaries (about 20 C) black bears.
    private static readonly Dictionary<string, float> WolfForests = new()
    {
        ["forester_island"] = 0.7f, ["blackfen_island"] = 0.7f, ["dangerous_swamplands"] = 0.7f, ["volcano_ring"] = 0.7f,
        ["cardinals_island"] = 0.7f, ["granary_lush"] = 0.7f, ["granary_temperate"] = 0.7f, ["granary_cold"] = 0.7f, ["granary_arid"] = 0.7f,
    };

    private void ApplyWolfForests(IMapRegion region, int rx, int rz)
    {
        if (_wgIslandJobs == null || region?.ForestMap?.Data == null) return;
        var wc = sapi.WorldManager.SaveGame?.WorldConfiguration;
        if (wc == null || !wc.GetBool("rustfallWorld", false)) return;
        var map = region.ForestMap;
        if (map.InnerSize <= 0) return;
        int regionSize = sapi.WorldManager.RegionSize;
        double span = regionSize / (double)map.InnerSize;
        int pad = map.TopLeftPadding;
        foreach (var job in _wgIslandJobs)
        {
            if (job.ShapeName == null || !WolfForests.TryGetValue(job.ShapeName, out float want)) continue;
            string key = $"lgWolfForest|{job.ShapeName}|{job.Cx},{job.Cz}";
            if (region.GetModdata(key) != null) continue;
            double radius = Math.Max(job.Diameter / 2.0, job.LandRadius), fade = 64;
            if (Math.Abs(rx * regionSize + regionSize / 2.0 - job.Cx) > regionSize + radius + fade
                || Math.Abs(rz * regionSize + regionSize / 2.0 - job.Cz) > regionSize + radius + fade) continue;
            int target = (int)Math.Round(want * 255);
            for (int py = 0; py < map.Size; py++)
                for (int px = 0; px < map.Size; px++)
                {
                    double wx = (rx * map.InnerSize + (px - pad) + 0.5) * span;
                    double wz = (rz * map.InnerSize + (py - pad) + 0.5) * span;
                    double d = Math.Sqrt((wx - job.Cx) * (wx - job.Cx) + (wz - job.Cz) * (wz - job.Cz));
                    double w = 1.0 - Smooth((d - radius) / fade);
                    if (w <= 0) continue;
                    int idx = py * map.Size + px, v = map.Data[idx];
                    if (v >= target) continue;
                    map.Data[idx] = (int)Math.Round(v + (target - v) * w);
                }
            region.SetModdata(key, new byte[] { 1 });
            region.DirtyForSaving = true;
        }
    }

    /// <summary>On the starter island (the all-hostiles safe island).</summary>
    private bool OnStarterIsland(double x, double z) => Forbidden(new AssetLocation("game", "drifter-normal"), x, z);
}
