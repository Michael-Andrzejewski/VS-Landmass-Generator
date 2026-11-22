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
    private bool _zoneHullFromStories;
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
        var gs = sapi.ModLoader.GetModSystem<Vintagestory.GameContent.GenStoryStructures>();
        bool haveStories = gs?.Structures != null && gs.Structures.Count() > 0;
        if (_zoneHull != null && (_zoneHullFromStories || !haveStories)) return _zoneHull;
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
        // Only the story locations the plan pins: the game places the rest wherever it likes, sometimes
        // kilometres out, and they must not stretch the zone.
        var pinned = haveStories ? PlanStoryCodes() : null;
        if (haveStories)
            foreach (var pair in gs.Structures)
                if (pinned.Contains(pair.Key)) Ring(pair.Value.CenterPos.X, pair.Value.CenterPos.Z, StoryEdge(pair.Value.LandformRadius));
        _zoneHull = ConvexHull(pts);
        _zoneHullFromStories = haveStories;
        _devaChunkDist.Clear();
        double minX = _zoneHull.Min(p => p.X), maxX = _zoneHull.Max(p => p.X), minZ = _zoneHull.Min(p => p.Y), maxZ = _zoneHull.Max(p => p.Y);
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        sapi.Logger.Notification("[zone] Rustfall zone hull: {0} corners, HUD x {1:0} to {2:0}, z {3:0} to {4:0}{5}",
            _zoneHull.Count, minX - mx, maxX - mx, minZ - mz, maxZ - mz, haveStories ? "" : " (story locations not known yet)");
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

    // ── no hostile creatures on the starter island ──────────────────────

    private static readonly string[] HostilePaths = { "drifter", "bowtorn", "shiver", "bell", "locust", "wolf", "bear", "hyena", "eidolon", "erel" };
    private static readonly string[] HostileDomains = { "oversizedhorrors", "underwaterhorrors" };
    private IslandJob _starterJob;

    private void StartSafeStarter()
    {
        if (!sapi.WorldManager.SaveGame.WorldConfiguration.GetBool("rustfallWorld", false)) return;
        sapi.Event.OnTrySpawnEntity += (IBlockAccessor ba, ref EntityProperties props, Vec3d at, long herdId) =>
            !(props != null && IsHostile(props.Code) && OnStarterIsland(at.X, at.Z));
        sapi.Event.OnEntitySpawn += e =>
        {
            if (e == null || e is EntityPlayer || !IsHostile(e.Code)) return;
            if (!OnStarterIsland(e.ServerPos.X, e.ServerPos.Z)) return;
            sapi.Event.EnqueueMainThreadTask(() => { if (e.Alive) e.Die(EnumDespawnReason.Removed); }, "lg-safestarter");
        };
    }

    internal static bool IsHostile(AssetLocation code)
    {
        if (code == null) return false;
        if (HostileDomains.Contains(code.Domain)) return true;
        string p = code.Path;
        foreach (string h in HostilePaths) if (p.StartsWith(h)) return true;
        return false;
    }

    private bool OnStarterIsland(double x, double z)
    {
        if (_starterJob == null && _wgIslandJobs != null)
            _starterJob = _wgIslandJobs.FirstOrDefault(j => j.ShapeName == "starter_island");
        if (_starterJob == null) return false;
        double r = Math.Max(_starterJob.Diameter / 2.0, _starterJob.LandRadius) + 24;
        double dx = x - _starterJob.Cx, dz = z - _starterJob.Cz;
        return dx * dx + dz * dz <= r * r;
    }
}
