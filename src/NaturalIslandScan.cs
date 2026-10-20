using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace LandmassGenerator;

// /genscan: find the natural islands of a Rustfall world. With the "natural
// world-gen islands" option on, tall landforms under the ocean breach the
// surface wherever the seed puts them, so the only way to know where they are
// is to generate terrain and look. The scan generates one chunk column every
// `step` blocks over the plan's whole area (plus a margin), keeps every
// sample with land above sea level that no plan island or story location
// explains, then generates the full neighbourhood of each find to measure it.
// Results go to chat, the log, and natural_islands.txt in the LandmassGenerator
// folder. Generated chunks are ordinary world chunks and stay in the save.
public partial class LandmassGeneratorModSystem
{
    private bool _scanBusy;

    private class ScanSample { public int Cx, Cz, Land, MaxH; }

    private TextCommandResult OnGenScan(TextCommandCallingArgs args)
    {
        if (_scanBusy) return TextCommandResult.Error("A scan is already running.");
        int step = args.Parsers[0].IsMissing ? 128 : Math.Clamp((int)args[0], 32, 1024);
        int margin = args.Parsers[1].IsMissing ? 1500 : Math.Clamp((int)args[1], 0, 20000);
        step = Math.Max(32, step / 32 * 32);

        // What the plan and the story system put there on purpose.
        var known = new List<(string Name, int X, int Z, int R)>();
        if (_wgIslandJobs != null)
        {
            foreach (var job in _wgIslandJobs)
            {
                known.Add((job.Shape != null ? "plan island " + job.ShapeName : "plan island", job.Cx, job.Cz, (int)(job.Diameter * 0.62) + 48));
            }
        }
        var genStory = sapi.ModLoader.GetModSystem<Vintagestory.GameContent.GenStoryStructures>();
        if (genStory?.Structures != null)
        {
            foreach (var pair in genStory.Structures)
            {
                var loc = pair.Value;
                known.Add(("story " + pair.Key, loc.CenterPos.X, loc.CenterPos.Z, (int)((loc.LandformRadius + 32) * 1.6) + 64));
            }
        }
        if (known.Count == 0) return TextCommandResult.Error("No plan islands or story locations in this world, so there is no area to scan.");

        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        foreach (var k in known) sapi.Logger.Notification("[genscan] known: {0} at HUD {1}, {2}, radius {3}", k.Name, k.X - mx, k.Z - mz, k.R);
        // The scan area is the PLAN's area: story structures the plan did not
        // pin (the game places those wherever it likes, sometimes kilometres
        // out) still count as explained land, but do not stretch the area.
        var bounds = known.Where(k => k.Name.StartsWith("plan") || PlanStoryCodes().Contains(k.Name.Substring(6))).ToList();
        if (bounds.Count == 0) bounds = known;
        int x1 = bounds.Min(k => k.X - k.R) - margin, x2 = bounds.Max(k => k.X + k.R) + margin;
        int z1 = bounds.Min(k => k.Z - k.R) - margin, z2 = bounds.Max(k => k.Z + k.R) + margin;
        var queue = new List<(int Cx, int Cz)>();
        for (int x = x1; x <= x2; x += step)
            for (int z = z1; z <= z2; z += step)
                queue.Add((FloorDiv(x, 32), FloorDiv(z, 32)));

        _scanBusy = true;
        var found = new List<ScanSample>();
        int midX = sapi.WorldManager.MapSizeX / 2, midZ = sapi.WorldManager.MapSizeZ / 2;
        string area = $"HUD {x1 - midX}..{x2 - midX} x {z1 - midZ}..{z2 - midZ}";
        sapi.Logger.Notification("[genscan] sampling {0} chunk columns every {1} blocks over {2}", queue.Count, step, area);
        ScanBatch(queue, 0, step, known, found);
        return TextCommandResult.Success($"Natural island scan started: {queue.Count} sample columns every {step} blocks over {area}. Progress and results follow in chat and the server log.");
    }

    // Story codes the world plan pins (storyloc lines).
    private HashSet<string> PlanStoryCodes()
    {
        var codes = new HashSet<string>();
        string planName = sapi.WorldManager.SaveGame.GetData<string>("lgWorldPlanName", null) ?? "worldplan";
        string planPath = Path.Combine(shapeFolder, planName + ".txt");
        if (!File.Exists(planPath)) return codes;
        foreach (string raw in File.ReadAllLines(planPath))
        {
            string[] parts = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("storyloc", StringComparison.OrdinalIgnoreCase)) codes.Add(parts[1].ToLowerInvariant());
        }
        return codes;
    }

    private const int ScanBatchSize = 48;

    private void ScanBatch(List<(int Cx, int Cz)> queue, int idx, int step, List<(string Name, int X, int Z, int R)> known, List<ScanSample> found)
    {
        if (idx >= queue.Count)
        {
            sapi.Logger.Notification("[genscan] sampling done: {0} sample(s) with unexplained land", found.Count);
            sapi.Event.RegisterCallback(_ => RefineScan(ClusterSamples(found, step), 0, step, known, new List<string>()), 1000);
            return;
        }
        int end = Math.Min(queue.Count, idx + ScanBatchSize);
        int pending = end - idx;
        int sea = sapi.World.SeaLevel;
        for (int i = idx; i < end; i++)
        {
            var (cx, cz) = queue[i];
            try
            {
                sapi.WorldManager.LoadChunkColumnPriority(cx, cz, cx, cz, new ChunkLoadOptions
                {
                    KeepLoaded = false,
                    OnLoaded = () =>
                    {
                        try
                        {
                            var mc = sapi.WorldManager.GetMapChunk(cx, cz);
                            if (mc != null)
                            {
                                int land = 0, maxH = 0;
                                foreach (ushort h in mc.WorldGenTerrainHeightMap)
                                {
                                    if (h >= sea) land++;
                                    if (h > maxH) maxH = h;
                                }
                                if (land > 0 && !Explained(cx * 32 + 16, cz * 32 + 16, known))
                                {
                                    found.Add(new ScanSample { Cx = cx, Cz = cz, Land = land, MaxH = maxH });
                                }
                            }
                        }
                        catch (Exception e) { sapi.Logger.Warning("[genscan] reading {0},{1} failed: {2}", cx, cz, e.Message); }
                        if (--pending == 0)
                        {
                            if (end / ScanBatchSize % 20 == 0) sapi.Logger.Notification("[genscan] {0} of {1} samples, {2} with land so far", end, queue.Count, found.Count);
                            // Spaced out: a request in the same instant its previous
                            // one retires can trip the vanilla request-index race.
                            sapi.Event.RegisterCallback(_ => ScanBatch(queue, end, step, known, found), 400);
                        }
                    }
                });
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[genscan] request {0},{1} failed: {2}", cx, cz, e.Message);
                if (--pending == 0) sapi.Event.RegisterCallback(_ => ScanBatch(queue, end, step, known, found), 2000);
            }
        }
    }

    private static bool Explained(int x, int z, List<(string Name, int X, int Z, int R)> known)
    {
        foreach (var k in known)
        {
            long dx = x - k.X, dz = z - k.Z;
            if (dx * dx + dz * dz <= (long)k.R * k.R) return true;
        }
        return false;
    }

    // Samples closer than 1.5 steps belong to the same island.
    private static List<List<ScanSample>> ClusterSamples(List<ScanSample> found, int step)
    {
        var clusters = new List<List<ScanSample>>();
        var left = new List<ScanSample>(found);
        double link = step * 1.5 / 32.0;
        while (left.Count > 0)
        {
            var cluster = new List<ScanSample> { left[0] };
            left.RemoveAt(0);
            for (int i = 0; i < cluster.Count; i++)
            {
                for (int j = left.Count - 1; j >= 0; j--)
                {
                    if (Math.Abs(left[j].Cx - cluster[i].Cx) <= link && Math.Abs(left[j].Cz - cluster[i].Cz) <= link)
                    {
                        cluster.Add(left[j]);
                        left.RemoveAt(j);
                    }
                }
            }
            clusters.Add(cluster);
        }
        return clusters;
    }

    // Generate everything around one find and measure the real island.
    private void RefineScan(List<List<ScanSample>> clusters, int idx, int step, List<(string Name, int X, int Z, int R)> known, List<string> report)
    {
        int midX = sapi.WorldManager.MapSizeX / 2, midZ = sapi.WorldManager.MapSizeZ / 2;
        if (idx >= clusters.Count)
        {
            _scanBusy = false;
            var sb = new StringBuilder();
            sb.AppendLine($"# Natural islands, world seed {sapi.World.Seed}, scanned {DateTime.Now:yyyy-MM-dd HH:mm}. HUD coordinates.");
            foreach (string line in report) sb.AppendLine(line);
            if (report.Count == 0) sb.AppendLine("# none found");
            string file = Path.Combine(shapeFolder, "natural_islands.txt");
            try { File.WriteAllText(file, sb.ToString()); } catch (Exception e) { sapi.Logger.Error("[genscan] could not write {0}: {1}", file, e.Message); }
            sapi.Logger.Notification("[genscan] done: {0} natural island(s). Written to {1}", report.Count, file);
            sapi.BroadcastMessageToAllGroups($"[genscan] Done: {report.Count} natural island(s). See natural_islands.txt in the LandmassGenerator folder.", EnumChatType.Notification);
            return;
        }

        var c = clusters[idx];
        int pad = Math.Max(1, step / 32);
        int cx1 = c.Min(s => s.Cx) - pad, cx2 = c.Max(s => s.Cx) + pad;
        int cz1 = c.Min(s => s.Cz) - pad, cz2 = c.Max(s => s.Cz) + pad;
        // A huge cluster would mean a continent; cap the measured area.
        cx2 = Math.Min(cx2, cx1 + 47);
        cz2 = Math.Min(cz2, cz1 + 47);
        LoadChunkAreaBanded(cx1, cz1, cx2, cz2, false, () =>
        {
                    try
                    {
                        int sea = sapi.World.SeaLevel;
                        long sumX = 0, sumZ = 0, n = 0;
                        int bx1 = int.MaxValue, bz1 = int.MaxValue, bx2 = int.MinValue, bz2 = int.MinValue, maxH = 0;
                        for (int cx = cx1; cx <= cx2; cx++)
                        {
                            for (int cz = cz1; cz <= cz2; cz++)
                            {
                                var mc = sapi.WorldManager.GetMapChunk(cx, cz);
                                if (mc == null) continue;
                                for (int i = 0; i < 32 * 32; i++)
                                {
                                    int h = mc.WorldGenTerrainHeightMap[i];
                                    if (h < sea) continue;
                                    int x = cx * 32 + i % 32, z = cz * 32 + i / 32;
                                    if (Explained(x, z, known)) continue;
                                    sumX += x; sumZ += z; n++;
                                    bx1 = Math.Min(bx1, x); bx2 = Math.Max(bx2, x);
                                    bz1 = Math.Min(bz1, z); bz2 = Math.Max(bz2, z);
                                    maxH = Math.Max(maxH, h);
                                }
                            }
                        }
                        if (n > 0)
                        {
                            int hx = (int)(sumX / n) - midX, hz = (int)(sumZ / n) - midZ;
                            var near = known.OrderBy(k => (long)(k.X - midX - hx) * (k.X - midX - hx) + (long)(k.Z - midZ - hz) * (k.Z - midZ - hz)).First();
                            double nd = Math.Sqrt(Math.Pow(near.X - midX - hx, 2) + Math.Pow(near.Z - midZ - hz, 2));
                            string line = $"natural {report.Count + 1}: HUD {hx}, {hz}  size {bx2 - bx1 + 1} x {bz2 - bz1 + 1} blocks  land {n} columns  peak {maxH - sea + 1} above sea  nearest {near.Name} {nd:0} away";
                            report.Add(line);
                            sapi.Logger.Notification("[genscan] " + line);
                        }
                    }
                    catch (Exception e) { sapi.Logger.Warning("[genscan] measuring cluster {0} failed: {1}", idx, e.Message); }
                    sapi.Event.RegisterCallback(_ => RefineScan(clusters, idx + 1, step, known, report), 750);
                });
    }
}
