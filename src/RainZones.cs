using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace LandmassGenerator;

// Rain zones: set how often it rains around a spot, nothing else.
//
// Vintage Story decides rain at the player's own block: the worldgen
// rainfall in the climate map there feeds a noise formula
// (WeatherSystemBase.GetRainCloudness), so at rainfall ~1.0 it rains most of
// the time. Rustfall's spawn came out "very common" and Michael found it
// raining almost constantly. A zone rewrites only the rainfall byte of the
// climate map pixels (temperature untouched), full strength out to its
// radius and fading over its fade band. The character screen calls 0.3-0.45
// "uncommon"; simulating the game's rain formula, 0.4 rains ~10-13% of the
// time, against ~50% at 1.0.
//
// Two sources, one mechanism:
// - plan lines `rain <mapX> <mapZ> <rainfall> [radius] [fade]`, read with the
//   islands at worldgen init and applied to each map region as it is born,
//   so a new world has the drier spawn from its first chunk;
// - `/genrain <rainfall> [radius] [fade]`, a zone centred on the player, kept
//   in the save, applied now to the regions that already exist and later to
//   new ones. `/genrain plan` applies the plan's own lines to existing
//   regions (a world made before the line was added); `/genrain` alone reads
//   the rainfall where you stand.
// Each region remembers which zones it got (region moddata), so a zone never
// lands twice on the same region whichever path reaches it first.
public partial class LandmassGeneratorModSystem
{
    private const string RainZonesKey = "lgRainZones";

    private struct RainZone
    {
        public int X, Z, Radius, Fade;   // world coordinates
        public float Rain;

        public string Key => "lgRain|" + string.Join("|", X, Z, Radius, Fade, Rain.ToString("0.###", CultureInfo.InvariantCulture));

        public static bool TryParse(string s, out RainZone z)
        {
            z = default;
            var p = s.Split('|');
            var ci = CultureInfo.InvariantCulture;
            return p.Length >= 5 && int.TryParse(p[0], NumberStyles.Integer, ci, out z.X) && int.TryParse(p[1], NumberStyles.Integer, ci, out z.Z)
                && int.TryParse(p[2], NumberStyles.Integer, ci, out z.Radius) && int.TryParse(p[3], NumberStyles.Integer, ci, out z.Fade)
                && float.TryParse(p[4], NumberStyles.Float, ci, out z.Rain);
        }

        public override string ToString() => string.Join("|", X, Z, Radius, Fade, Rain.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private List<RainZone> _planRainZones = new();
    private List<RainZone> _savedRainZones;

    // `rain <mapX> <mapZ> <rainfall 0..1> [radius] [fade]`, map coordinates
    // as in the rest of the plan (HUD, starter at 0,0).
    private bool TryParsePlanRain(string[] parts, out RainZone z, out string err)
    {
        z = default;
        err = null;
        var ci = CultureInfo.InvariantCulture;
        if (parts.Length < 4 || !int.TryParse(parts[1], out int mx) || !int.TryParse(parts[2], out int mz)
            || !float.TryParse(parts[3], NumberStyles.Float, ci, out float rain))
        {
            err = "not 'rain x z rainfall [radius] [fade]'";
            return false;
        }
        int radius = parts.Length > 4 && int.TryParse(parts[4], out int r) ? r : 400;
        int fade = parts.Length > 5 && int.TryParse(parts[5], out int f) ? f : 150;
        z = new RainZone
        {
            X = sapi.WorldManager.MapSizeX / 2 + mx, Z = sapi.WorldManager.MapSizeZ / 2 + mz,
            Radius = Math.Max(0, radius), Fade = Math.Max(16, fade), Rain = Math.Clamp(rain, 0f, 1f)
        };
        return true;
    }

    private List<RainZone> SavedRainZones()
    {
        if (_savedRainZones != null) return _savedRainZones;
        var list = new List<RainZone>();
        foreach (string s in sapi.WorldManager.SaveGame.GetData<List<string>>(RainZonesKey, null) ?? new List<string>())
            if (RainZone.TryParse(s, out var z)) list.Add(z);
        return _savedRainZones = list;
    }

    private void StartRainZones(ICoreServerAPI api)
    {
        api.Event.MapRegionGeneration(OnMapRegionGenRain, "standard");

        var p = api.ChatCommands.Parsers;
        RegisterCmd(api, "genrain", name =>
            api.ChatCommands.Create(name)
                .WithDescription("How often it rains. /genrain reads the rainfall where you stand (the character screen's label). /genrain <0..1> [radius] [fade] sets the rainfall around you, e.g. /genrain 0.4 for 'uncommon' (rain ~10% of the time); temperature is untouched, but grass tint follows rainfall a little. /genrain plan applies the world plan's rain lines to land that already exists. /genrain cap [percent] shows or sets the most rainfall the weather sees anywhere (Rustfall default 40; 100 = no cap), tint untouched. /genrain survey logs the rainfall and the share of the time it rains at every island.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalWord("rainfall"), p.OptionalInt("radius", 400), p.OptionalInt("fade", 150))
                .HandleWith(OnGenRain));
    }

    private static string RainLabel(float r) =>
        r > 0.9f ? "all the time" : r > 0.7f ? "very common" : r > 0.45f ? "common" : r > 0.3f ? "uncommon" : r > 0.15f ? "rarely" : "very rare";

    private TextCommandResult OnGenRain(TextCommandCallingArgs args)
    {
        int midX = sapi.WorldManager.MapSizeX / 2, midZ = sapi.WorldManager.MapSizeZ / 2;
        var cpos = args.Caller.Pos;
        int px = cpos != null ? (int)cpos.X : midX, pz = cpos != null ? (int)cpos.Z : midZ;
        string arg = args.Parsers[0].IsMissing ? null : ((string)args[0]).ToLowerInvariant();

        if (arg == null)
        {
            // Read at the caller's height (the console: the ground at map
            // middle): the game adds up to +0.16 rainfall near sea level
            // (Climate.GetRainFall), so a boat gets wetter than a hilltop.
            var bp = new BlockPos(px, 0, pz, 0);
            bp.Y = cpos != null ? (int)cpos.Y : sapi.World.BlockAccessor.GetTerrainMapheightAt(bp) + 1;
            var c = sapi.World.BlockAccessor.GetClimateAt(bp, EnumGetClimateMode.WorldGenValues);
            if (c == null) return TextCommandResult.Error("No climate here yet (region not loaded).");
            return TextCommandResult.Success($"Rainfall at {px - midX}, {bp.Y}, {pz - midZ}: {c.WorldgenRainfall:0.00} ({RainLabel(c.WorldgenRainfall)}). Near sea level the game adds up to 0.16.");
        }

        if (arg == "survey") return RainSurvey();

        if (arg == "cap")
        {
            var wc = sapi.WorldManager.SaveGame.WorldConfiguration;
            if (!args.Parsers[1].IsMissing)
            {
                int pct = Math.Clamp((int)args[1], 0, 100);
                wc.SetString("lgRainCap", pct >= 100 ? "off" : (pct / 100f).ToString("0.##", CultureInfo.InvariantCulture));
                _rainCap = ReadRainCap(wc);
            }
            return TextCommandResult.Success(_rainCap < 0
                ? "The weather sees the climate's own rainfall (no cap)."
                : $"The weather sees at most rainfall {_rainCap:0.00} ({RainLabel(_rainCap)}): unchanged up to {Math.Max(0f, _rainCap - RainKneeGap):0.00}, squeezed above it. Grass and leaf tint keep the real rainfall. /genrain survey shows what it does at each island.");
        }

        if (arg == "plan")
        {
            if (_planRainZones.Count == 0) return TextCommandResult.Error("The world plan has no rain lines (rain x z rainfall [radius] [fade]).");
            foreach (var z in _planRainZones) StampRainZoneNow(z);
            return TextCommandResult.Success($"Applying {_planRainZones.Count} plan rain zone(s) to the land that already exists; the log says when each region is done.");
        }

        if (!float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out float rain) || rain < 0 || rain > 1)
            return TextCommandResult.Error("Give a rainfall from 0 to 1 (0.4 is 'uncommon'), 'plan', or nothing to read the rainfall here.");
        var zone = new RainZone { X = px, Z = pz, Radius = Math.Max(0, (int)args[1]), Fade = Math.Max(16, (int)args[2]), Rain = rain };
        // a fresh list: the worldgen thread may be walking the old one
        var saved = new List<RainZone>(SavedRainZones()) { zone };
        _savedRainZones = saved;
        sapi.WorldManager.SaveGame.StoreData(RainZonesKey, saved.Select(z => z.ToString()).ToList());
        StampRainZoneNow(zone);
        return TextCommandResult.Success($"Rainfall {rain:0.00} ({RainLabel(rain)}) within {zone.Radius} blocks of {px - midX}, {pz - midZ}, fading over {zone.Fade}. Land generated later gets it too.");
    }

    // ── The weather's rainfall, capped (0.68.0) ──
    // The climate map's rainfall does two jobs: it tints grass and leaves,
    // and it sets how often it rains (GetRainCloudness offsets its noise by
    // (rainfall - 0.6) * 2). Rustfall's lush islands want the green but not
    // the weather: on Rustfall 0.67 /genrain survey found it raining 66% of
    // the time on Eerie Island, 64% on Blackfen and 44% on the Ideal Home
    // Island. A prefix on GetRainCloudness hands the weather a softened
    // rainfall, unchanged up to a knee 0.15 below the cap and squeezed above
    // it so that 1.0 becomes the cap; a postfix puts the real value back for
    // the tint. With the default 0.40 the 0.67 survey's wettest islands come
    // down to about a fifth of the time and most of them to a tenth.
    // World config lgRainCap ("0.55", or "off"); a Rustfall world without
    // one gets DefaultRainCap. Both sides read it (in single player they
    // share the one patched method), so the rain the client draws and the
    // rain the server counts for farmland agree.
    private const float RainKneeGap = 0.15f, DefaultRainCap = 0.40f;
    private static float _rainCap = -1f;   // below 0: no cap
    private static bool _rainCapPatched;

    private static float CapRain(float r, float cap)
    {
        if (cap < 0 || cap >= 1) return r;
        float knee = Math.Max(0f, cap - RainKneeGap);
        return r <= knee ? r : knee + (r - knee) * (cap - knee) / (1 - knee);
    }

    private static float ReadRainCap(Vintagestory.API.Datastructures.ITreeAttribute wc)
    {
        if (wc == null) return -1f;
        string s = wc.GetAsString("lgRainCap", null);
        if (string.IsNullOrEmpty(s)) return wc.GetBool("rustfallWorld", false) ? DefaultRainCap : -1f;
        if (s.Equals("off", StringComparison.OrdinalIgnoreCase)) return -1f;
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? Math.Clamp(v, 0f, 1f) : -1f;
    }

    // Both sides call this once the world config is known.
    private static void InstallRainCap(ICoreAPI api)
    {
        _rainCap = ReadRainCap(api.World?.Config);
        if (_rainCap >= 0) api.Logger.Notification("[landmassgenerator] rain cap: the weather sees at most rainfall {0:0.00} here ({1} side)", _rainCap, api.Side);
        if (_rainCapPatched) return;
        try
        {
            var target = HarmonyLib.AccessTools.Method(typeof(Vintagestory.GameContent.WeatherSystemBase), "GetRainCloudness");
            if (target == null) { api.Logger.Warning("[landmassgenerator] rain cap: WeatherSystemBase.GetRainCloudness not found (game update?); rain follows the climate map as before."); return; }
            new HarmonyLib.Harmony("landmassgenerator.raincap").Patch(target,
                prefix: new HarmonyLib.HarmonyMethod(typeof(LandmassGeneratorModSystem), nameof(RainCapPrefix)),
                postfix: new HarmonyLib.HarmonyMethod(typeof(LandmassGeneratorModSystem), nameof(RainCapPostfix)));
            _rainCapPatched = true;
        }
        catch (Exception e) { api.Logger.Warning("[landmassgenerator] rain cap not installed: {0}", e.Message); }
    }

    private static void RainCapPrefix(ClimateCondition conds, out float __state)
    {
        __state = float.NaN;
        float cap = _rainCap;
        if (cap < 0 || conds == null) return;
        __state = conds.Rainfall;
        conds.Rainfall = CapRain(conds.Rainfall, cap);
    }

    private static void RainCapPostfix(ClimateCondition conds, float __state)
    {
        if (!float.IsNaN(__state) && conds != null) conds.Rainfall = __state;
    }

    // /genrain survey: the rainfall at the middle of every plan island and
    // story location, read at its ground, and how much of the time it
    // rains there. The share is counted with the game's own weather noise
    // (WeatherSystemBase.GetRainCloudness) every two hours over 120 days, so
    // it is what a player standing there would see. Loads each spot's chunk
    // column first; the table goes to the log when the last one is in.
    private TextCommandResult RainSurvey()
    {
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        var spots = new List<(string Name, int X, int Z)>();
        foreach (var j in _wgIslandJobs ?? new List<IslandJob>()) spots.Add((j.ShapeName ?? "island", j.Cx, j.Cz));
        var genStory = sapi.ModLoader.GetModSystem<Vintagestory.GameContent.GenStoryStructures>();
        if (genStory?.Structures != null)
            foreach (var pair in genStory.Structures) spots.Add(("story " + pair.Key, pair.Value.CenterPos.X, pair.Value.CenterPos.Z));
        if (spots.Count == 0) return TextCommandResult.Error("No plan islands or story locations in this world.");
        var weather = sapi.ModLoader.GetModSystem<Vintagestory.GameContent.WeatherSystemServer>();
        var rows = new string[spots.Count];
        int left = spots.Count;
        for (int i = 0; i < spots.Count; i++)
        {
            int idx = i;
            var s = spots[i];
            sapi.WorldManager.LoadChunkColumnPriority(s.X / GlobalConstants.ChunkSize, s.Z / GlobalConstants.ChunkSize, new ChunkLoadOptions
            {
                OnLoaded = () =>
                {
                    var bp = new BlockPos(s.X, 0, s.Z, 0);
                    bp.Y = Math.Max(sapi.World.SeaLevel, sapi.World.BlockAccessor.GetTerrainMapheightAt(bp)) + 1;
                    var c = sapi.World.BlockAccessor.GetClimateAt(bp, EnumGetClimateMode.WorldGenValues);
                    string row;
                    if (c == null) row = $"{s.Name,-28} {s.X - mx,7} {s.Z - mz,7}  no climate";
                    else
                    {
                        int wet = 0, n = 0;
                        if (weather != null)
                            for (double day = 0; day < 120; day += 1 / 12.0, n++)
                                if (weather.GetRainCloudness(c, s.X + 0.5, s.Z + 0.5, day) - 0.5f > 0.02f) wet++;
                        string share = n > 0 ? $"{wet * 100.0 / n,5:0.0}%" : "   ?";
                        row = $"{s.Name,-28} {s.X - mx,7} {s.Z - mz,7} y{bp.Y,4}  rainfall {c.Rainfall:0.00} ({RainLabel(c.Rainfall)}), weather sees {CapRain(c.Rainfall, _rainCap):0.00}, raining {share}";
                    }
                    rows[idx] = row;
                    if (--left == 0)
                        sapi.Logger.Notification("[landmassgenerator] rain survey (name, HUD x, z, ground, rainfall, share of the time it rains):\n" + string.Join("\n", rows));
                }
            });
        }
        return TextCommandResult.Success($"Surveying the rain at {spots.Count} spots; the table goes to the log.");
    }

    // New regions: every plan and saved zone that reaches them.
    private void OnMapRegionGenRain(IMapRegion region, int regionX, int regionZ, Vintagestory.API.Datastructures.ITreeAttribute chunkGenParams = null)
    {
        foreach (var z in _planRainZones) ApplyRainZone(region, regionX, regionZ, z);
        foreach (var z in SavedRainZones()) ApplyRainZone(region, regionX, regionZ, z);
    }

    // Rewrites the rainfall of every climate pixel the zone reaches, the
    // whole padded grid like StampClimate (padding mirrors the neighbours and
    // both copies are sampled). False if the region already had this zone or
    // the zone does not reach it.
    private bool ApplyRainZone(IMapRegion region, int rx, int rz, RainZone zone)
    {
        var map = region?.ClimateMap;
        if (map?.Data == null || map.InnerSize <= 0) return false;
        if (region.GetModdata(zone.Key) != null) return false;
        int regionSize = sapi.WorldManager.RegionSize;
        double reach = zone.Radius + zone.Fade;
        if (Math.Abs(rx * regionSize + regionSize / 2.0 - zone.X) > regionSize + reach
            || Math.Abs(rz * regionSize + regionSize / 2.0 - zone.Z) > regionSize + reach) return false;
        double span = regionSize / (double)map.InnerSize;
        int pad = map.TopLeftPadding, target = (int)Math.Round(zone.Rain * 255);
        bool touched = false;
        for (int py = 0; py < map.Size; py++)
            for (int px = 0; px < map.Size; px++)
            {
                double wx = (rx * map.InnerSize + (px - pad) + 0.5) * span;
                double wz = (rz * map.InnerSize + (py - pad) + 0.5) * span;
                double d = Math.Sqrt((wx - zone.X) * (wx - zone.X) + (wz - zone.Z) * (wz - zone.Z));
                double w = 1.0 - Smooth((d - zone.Radius) / zone.Fade);
                if (w <= 0) continue;
                int idx = py * map.Size + px, old = map.Data[idx];
                int rain = (old >> 8) & 0xff;
                rain = (int)Math.Round(rain + (target - rain) * w);
                map.Data[idx] = (old & ~0xff00) | (rain << 8);
                touched = true;
            }
        if (!touched) return false;
        region.SetModdata(zone.Key, new byte[] { 1 });
        region.DirtyForSaving = true;
        return true;
    }

    // Existing regions: load one chunk column in each region the zone
    // reaches (that loads, or generates, its map region), apply, push the
    // region to the players and resend the loaded map chunks so grass
    // retints. A region generated by this load already got the zone from
    // the generation hook; the moddata marker makes the second pass a no-op.
    private void StampRainZoneNow(RainZone zone, bool loadMissing = true)
    {
        int regionSize = sapi.WorldManager.RegionSize, cs = GlobalConstants.ChunkSize;
        int reach = zone.Radius + zone.Fade;
        int r0x = FloorDiv(zone.X - reach, regionSize), r1x = FloorDiv(zone.X + reach, regionSize);
        int r0z = FloorDiv(zone.Z - reach, regionSize), r1z = FloorDiv(zone.Z + reach, regionSize);
        for (int rz = r0z; rz <= r1z; rz++)
            for (int rx = r0x; rx <= r1x; rx++)
            {
                int frx = rx, frz = rz;
                void Apply()
                {
                    var region = sapi.WorldManager.GetMapRegion(frx, frz);
                    bool done = ApplyRainZone(region, frx, frz, zone);
                    if (region != null) sapi.WorldManager.BroadcastMapRegion(frx, frz, false);
                    int c0x = Math.Max(frx * regionSize, zone.X - reach) / cs, c1x = Math.Min(frx * regionSize + regionSize - 1, zone.X + reach) / cs;
                    int c0z = Math.Max(frz * regionSize, zone.Z - reach) / cs, c1z = Math.Min(frz * regionSize + regionSize - 1, zone.Z + reach) / cs;
                    for (int cz = c0z; cz <= c1z; cz++)
                        for (int cx = c0x; cx <= c1x; cx++)
                            if (sapi.WorldManager.GetMapChunk(cx, cz) != null) sapi.WorldManager.ResendMapChunk(cx, cz, true);
                    sapi.Logger.Notification("[landmassgenerator] rain zone {0}: region {1},{2} {3}", zone, frx, frz,
                        region == null ? "not loaded" : done ? "rainfall rewritten" : "already had it");
                }
                if (sapi.WorldManager.GetMapRegion(rx, rz) != null) { Apply(); continue; }
                if (!loadMissing) continue;
                // the column of this region nearest the zone centre
                int bx = Math.Clamp(zone.X, rx * regionSize, rx * regionSize + regionSize - 1);
                int bz = Math.Clamp(zone.Z, rz * regionSize, rz * regionSize + regionSize - 1);
                sapi.WorldManager.LoadChunkColumnPriority(bx / cs, bz / cs, new ChunkLoadOptions { OnLoaded = () => Apply() });
            }
    }
}
