using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace LandmassGenerator;

// /genkeepout check [rule]: is any natural land within `rule` blocks (100) of
// an island's edge? For every plan island (the ruin isles too) it loads the
// chunk columns under the ring from its edge (where its land really ends) out
// to edge + rule, generating any that do not exist yet, and reads the
// worldgen heightmap: a column at or above the sea in a ring that is not
// inside any island's edge or a story location's forced land is natural land
// too close. Results per island go to chat and the log.
public partial class LandmassGeneratorModSystem
{
    private bool _keepOutBusy;

    private TextCommandResult OnGenKeepOut(TextCommandCallingArgs args)
    {
        string action = args.Parsers[0].IsMissing ? "check" : ((string)args[0]).ToLowerInvariant();
        if (action != "check") return TextCommandResult.Error("Usage: /genkeepout check [rule blocks, default 100]");
        if (_keepOutBusy) return TextCommandResult.Error("A keep-out check is already running.");
        var jobs = _wgIslandJobs;
        if (jobs == null || jobs.Count == 0) return TextCommandResult.Error("This world has no plan islands.");
        int rule = args.Parsers[1].IsMissing ? 100 : Math.Clamp((int)args[1], 10, 2000);
        const int Slack = 6;   // coast jitter past the shape's last land cell

        var isl = jobs.Select(j => (Name: j.ShapeName ?? "island", X: j.Cx, Z: j.Cz, Edge: Math.Max(j.Diameter / 2.0, j.LandRadius) + Slack)).ToList();
        var story = new List<(string Name, int X, int Z, double Edge)>();
        var genStory = sapi.ModLoader.GetModSystem<Vintagestory.GameContent.GenStoryStructures>();
        if (genStory?.Structures != null)
            foreach (var pair in genStory.Structures)
                story.Add(("story " + pair.Key, pair.Value.CenterPos.X, pair.Value.CenterPos.Z, StoryEdge(pair.Value.LandformRadius) * 1.15));

        var cols = new HashSet<(int, int)>();
        foreach (var i in isl)
        {
            double outer = i.Edge + rule;
            for (int cx = (int)Math.Floor((i.X - outer) / 32); cx <= (int)Math.Floor((i.X + outer) / 32); cx++)
                for (int cz = (int)Math.Floor((i.Z - outer) / 32); cz <= (int)Math.Floor((i.Z + outer) / 32); cz++)
                {
                    // nearest and farthest point of the chunk from the centre
                    double nx = Math.Max(cx * 32 - i.X, Math.Max(0, i.X - (cx * 32 + 31))), nz = Math.Max(cz * 32 - i.Z, Math.Max(0, i.Z - (cz * 32 + 31)));
                    double fx = Math.Max(Math.Abs(cx * 32 - i.X), Math.Abs(cx * 32 + 31 - i.X)), fz = Math.Max(Math.Abs(cz * 32 - i.Z), Math.Abs(cz * 32 + 31 - i.Z));
                    if (Math.Sqrt(nx * nx + nz * nz) > outer || Math.Sqrt(fx * fx + fz * fz) < i.Edge) continue;
                    cols.Add((cx, cz));
                }
        }
        var queue = cols.ToList();
        var nearest = new double[isl.Count];
        var count = new int[isl.Count];
        for (int k = 0; k < nearest.Length; k++) nearest[k] = double.MaxValue;
        _keepOutBusy = true;
        sapi.Logger.Notification("[genkeepout] checking {0} chunk columns round {1} island(s), {2} blocks out from each edge", queue.Count, isl.Count, rule);
        KeepOutBatch(queue, 0, isl, story, rule, nearest, count);
        return TextCommandResult.Success($"Keep-out check started: {queue.Count} chunk columns round {isl.Count} island(s). Results follow in chat and the log.");
    }

    private void KeepOutBatch(List<(int Cx, int Cz)> queue, int idx, List<(string Name, int X, int Z, double Edge)> isl,
        List<(string Name, int X, int Z, double Edge)> story, int rule, double[] nearest, int[] count)
    {
        int midX = sapi.WorldManager.MapSizeX / 2, midZ = sapi.WorldManager.MapSizeZ / 2;
        if (idx >= queue.Count)
        {
            _keepOutBusy = false;
            int bad = 0;
            for (int k = 0; k < isl.Count; k++)
            {
                if (count[k] == 0) continue;
                bad++;
                sapi.Logger.Notification("[genkeepout] {0} at HUD {1}, {2}: {3} natural land column(s) within {4} of its edge, nearest {5:0} out",
                    isl[k].Name, isl[k].X - midX, isl[k].Z - midZ, count[k], rule, nearest[k]);
            }
            string msg = $"[genkeepout] Done: {isl.Count - bad} of {isl.Count} island(s) have no natural land within {rule} blocks of their edge.";
            sapi.Logger.Notification(msg);
            sapi.BroadcastMessageToAllGroups(msg, EnumChatType.Notification);
            return;
        }
        int end = Math.Min(queue.Count, idx + ScanBatchSize);
        int pending = end - idx;
        int sea = sapi.World.SeaLevel;
        for (int q = idx; q < end; q++)
        {
            var (cx, cz) = queue[q];
            void Done()
            {
                if (--pending == 0)
                {
                    if (end / ScanBatchSize % 20 == 0) sapi.Logger.Notification("[genkeepout] {0} of {1} chunk columns", end, queue.Count);
                    sapi.Event.RegisterCallback(_ => KeepOutBatch(queue, end, isl, story, rule, nearest, count), 400);
                }
            }
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
                                for (int c = 0; c < 32 * 32; c++)
                                {
                                    if (mc.WorldGenTerrainHeightMap[c] < sea) continue;
                                    int x = cx * 32 + c % 32, z = cz * 32 + c / 32;
                                    bool own = false;
                                    foreach (var i in isl) { double dx = x - i.X, dz = z - i.Z; if (dx * dx + dz * dz <= i.Edge * i.Edge) { own = true; break; } }
                                    if (!own) foreach (var s in story) { double dx = x - s.X, dz = z - s.Z; if (dx * dx + dz * dz <= s.Edge * s.Edge) { own = true; break; } }
                                    if (own) continue;
                                    for (int k = 0; k < isl.Count; k++)
                                    {
                                        double d = Math.Sqrt((double)(x - isl[k].X) * (x - isl[k].X) + (double)(z - isl[k].Z) * (z - isl[k].Z)) - isl[k].Edge;
                                        if (d > rule) continue;
                                        count[k]++;
                                        if (d < nearest[k]) nearest[k] = d;
                                    }
                                }
                        }
                        catch (Exception e) { sapi.Logger.Warning("[genkeepout] reading {0},{1} failed: {2}", cx, cz, e.Message); }
                        Done();
                    }
                });
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[genkeepout] request {0},{1} failed: {2}", cx, cz, e.Message);
                Done();
            }
        }
    }
}
