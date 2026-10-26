using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// /genmapexport [x1 z1 x2 z2]: the whole world map, as if you had explored it.
//
// The game's world map (M) is drawn by the client from the chunks it has
// been sent, one 32x32 piece per chunk column, and cached in
// VintagestoryData/Maps/<world id>.db. This draws the same pieces on the
// server: it loads (generating where needed) every chunk column in the area
// and paints each the way ChunkMapLayer.GenerateChunkImage does (the parchment
// palette by the top block's map colour code, the rain heightmap's slope
// shading blurred and banded, water edges inked), then writes them into a map
// database in the server's data folder, named after this world's id. Copy that
// file into the player's VintagestoryData/Maps and the map shows the area
// opened up. Default area: every plan island and the story locations near
// them plus 640, and a square round each story location farther out.
public partial class LandmassGeneratorModSystem
{
    private bool _mapExportBusy;

    private TextCommandResult OnGenMapExport(TextCommandCallingArgs args)
    {
        if (_mapExportBusy) return TextCommandResult.Error("A map export is already running.");
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        // areas in HUD blocks: the first is the core, any after it stand alone
        var areas = new List<(string Name, int X1, int Z1, int X2, int Z2)>();
        string first = args.Parsers[0].IsMissing ? null : (string)args[0];
        bool list = first == "list", coreOnly = first == "core";
        if (first != null && !list && !coreOnly)
        {
            if (!int.TryParse(first, out int x1) || args.Parsers[3].IsMissing)
                return TextCommandResult.Error("Usage: /genmapexport [x1 z1 x2 z2 | core | list]");
            int z1 = (int)args[1], x2 = (int)args[2], z2 = (int)args[3];
            areas.Add(("area", Math.Min(x1, x2), Math.Min(z1, z2), Math.Max(x1, x2), Math.Max(z1, z2)));
        }
        else
        {
            // the core: the plan's islands (the ruin isles among them) and the
            // story locations within 2200 of one, plus 640. Each story location
            // the game rolled farther out gets a square of its own.
            var pts = new List<(string Name, int X, int Z, int R)>();
            foreach (var j in _wgIslandJobs ?? new List<IslandJob>())
                pts.Add((j.ShapeName ?? "island", j.Cx - mx, j.Cz - mz, (int)Math.Max(j.Diameter / 2.0, j.LandRadius)));
            int nIsl = pts.Count;
            var far = new List<(string Name, int X, int Z, int R)>();
            var genStory = sapi.ModLoader.GetModSystem<GenStoryStructures>();
            if (genStory?.Structures != null)
                foreach (var pair in genStory.Structures)
                {
                    var s = ("story " + pair.Key, pair.Value.CenterPos.X - mx, pair.Value.CenterPos.Z - mz, StoryEdge(pair.Value.LandformRadius));
                    bool near = nIsl == 0 || pts.Take(nIsl).Any(i => Math.Sqrt(Math.Pow(i.X - s.Item2, 2) + Math.Pow(i.Z - s.Item3, 2)) - i.R - s.Item4 < 2200);
                    (near ? pts : far).Add(s);
                }
            if (pts.Count == 0) return TextCommandResult.Error("No plan islands or story locations; give the area: /genmapexport x1 z1 x2 z2");
            areas.Add(("core", pts.Min(p => p.X - p.R) - 640, pts.Min(p => p.Z - p.R) - 640, pts.Max(p => p.X + p.R) + 640, pts.Max(p => p.Z + p.R) + 640));
            if (!coreOnly)
                foreach (var f in far) areas.Add((f.Name, f.X - f.R - 640, f.Z - f.R - 640, f.X + f.R + 640, f.Z + f.R + 640));
            if (list)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("The core holds:");
                foreach (var q in pts) sb.AppendLine($"  {q.Name} at {q.X}, {q.Z}, edge {q.R}");
                sb.AppendLine("Areas drawn (HUD; /genmapexport core leaves out all but the first):");
                foreach (var a in areas) sb.AppendLine($"  {a.Name}: {a.X1}, {a.Z1} to {a.X2}, {a.Z2}");
                sapi.Logger.Notification("[genmapexport] " + sb);
                return TextCommandResult.Success(sb.ToString());
            }
        }
        // the same areas in chunk columns
        var rects = areas.Select(a => (X1: FloorDiv(a.X1 + mx, 32), Z1: FloorDiv(a.Z1 + mz, 32), X2: FloorDiv(a.X2 + mx, 32), Z2: FloorDiv(a.Z2 + mz, 32))).ToList();
        bool Want(int cx, int cz) => rects.Any(r => cx >= r.X1 && cx <= r.X2 && cz >= r.Z1 && cz <= r.Z2);

        string dir = Path.Combine(GamePaths.DataPath, "Maps");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, sapi.World.SavegameIdentifier + ".db");
        var db = new MapDB(sapi.World.Logger);
        string err = null;
        db.OpenOrCreate(path, ref err, true, true, false);
        if (err != null) return TextCommandResult.Error("Cannot open " + path + ": " + err);

        // tiles of 16 x 16 columns on a grid shared by all the areas, so
        // overlapping areas share their tiles
        var painter = new MapPainter(sapi);
        const int T = 16;
        var tileSet = new HashSet<(int X, int Z)>();
        foreach (var r in rects)
            for (int tz = FloorDiv(r.Z1, T) * T; tz <= r.Z2; tz += T)
                for (int tx = FloorDiv(r.X1, T) * T; tx <= r.X2; tx += T) tileSet.Add((tx, tz));
        // nearest the plan's islands first, spiralling out, so a copy taken
        // part way through already covers the islands
        double ox, oz;
        var core = _wgIslandJobs ?? new List<IslandJob>();
        if (core.Count > 0) { ox = core.Average(j => (double)j.Cx) / 32; oz = core.Average(j => (double)j.Cz) / 32; }
        else { ox = (rects[0].X1 + rects[0].X2) / 2.0; oz = (rects[0].Z1 + rects[0].Z2) / 2.0; }
        var tiles = tileSet.OrderBy(t => Math.Pow(t.X + T / 2.0 - ox, 2) + Math.Pow(t.Z + T / 2.0 - oz, 2)).ToList();
        long total = 0;
        foreach (var (tx, tz) in tiles)
            for (int cx = tx; cx < tx + T; cx++)
                for (int cz = tz; cz < tz + T; cz++) if (Want(cx, cz)) total++;
        _mapExportBusy = true;
        foreach (var a in areas)
            sapi.Logger.Notification("[genmapexport] area {0}: HUD {1}, {2} to {3}, {4}", a.Name, a.X1, a.Z1, a.X2, a.Z2);
        sapi.Logger.Notification("[genmapexport] {0} chunk columns in {1} areas into {2}", total, areas.Count, path);
        var stats = new int[2];
        MapExportTile(db, painter, tiles, 0, T, Want, stats, DateTime.UtcNow);
        return TextCommandResult.Success($"Map export started: {total} chunk columns in {areas.Count} areas into {path}. Progress in the log.");
    }

    private void MapExportTile(MapDB db, MapPainter painter, List<(int X, int Z)> tiles, int idx, int T, System.Func<int, int, bool> want, int[] stats, DateTime started)
    {
        if (idx >= tiles.Count)
        {
            _mapExportBusy = false;
            db.Close();
            string msg = $"[genmapexport] Done: {stats[0]} map pieces written, {stats[1]} chunk columns skipped, in {(DateTime.UtcNow - started).TotalMinutes:0} min.";
            sapi.Logger.Notification(msg);
            sapi.BroadcastMessageToAllGroups(msg, EnumChatType.Notification);
            return;
        }
        var (tx, tz) = tiles[idx];
        void Next(int delay) => sapi.Event.RegisterCallback(_ => MapExportTile(db, painter, tiles, idx + 1, T, want, stats, started), delay);
        var cols = new List<FastVec2i>();
        for (int cx = tx; cx < tx + T; cx++)
            for (int cz = tz; cz < tz + T; cz++) if (want(cx, cz)) cols.Add(new FastVec2i(cx, cz));
        // a tile an earlier run finished is left as it is
        if (db.GetMapPieces(cols).Count(m => m?.Pixels != null) == cols.Count) { stats[0] += cols.Count; Next(1); return; }
        int lx = cols.Min(c => c.X), lz = cols.Min(c => c.Y), hx = cols.Max(c => c.X), hz = cols.Max(c => c.Y);
        try
        {
            // the columns and a border of one: the shading and the water edges
            // look at the neighbouring columns. Held loaded until they are
            // drawn (a headless server unloads unwatched chunks within
            // moments, and the first export lost a few columns that way), then
            // let go.
            sapi.WorldManager.LoadChunkColumnPriority(lx - 1, lz - 1, hx + 1, hz + 1, new ChunkLoadOptions
            {
                KeepLoaded = true,
                OnLoaded = () => sapi.Event.EnqueueMainThreadTask(() =>
                {
                    var pieces = new Dictionary<FastVec2i, MapPieceDB>();
                    foreach (var c in cols)
                    {
                        int[] px = null;
                        try { px = painter.Paint(c.X, c.Y); }
                        catch (Exception e) { sapi.Logger.Warning("[genmapexport] {0},{1}: {2}", c.X, c.Y, e.Message); }
                        if (px == null) { stats[1]++; continue; }
                        pieces[c] = new MapPieceDB { Pixels = px };
                    }
                    if (pieces.Count > 0) db.SetMapPieces(pieces);
                    stats[0] += pieces.Count;
                    for (int cx = lx - 1; cx <= hx + 1; cx++)
                        for (int cz = lz - 1; cz <= hz + 1; cz++) sapi.WorldManager.UnloadChunkColumn(cx, cz);
                    if ((idx + 1) % 25 == 0)
                    {
                        double mins = (DateTime.UtcNow - started).TotalMinutes;
                        sapi.Logger.Notification("[genmapexport] tile {0} of {1}: {2} pieces so far, {3:0} min, about {4:0} min left",
                            idx + 1, tiles.Count, stats[0], mins, mins / (idx + 1) * (tiles.Count - idx - 1));
                    }
                    Next(50);
                }, "genmapexport")
            });
        }
        catch (Exception e)
        {
            sapi.Logger.Warning("[genmapexport] tile {0},{1}: {2}; retrying", tx, tz, e.Message);
            sapi.Event.RegisterCallback(_ => MapExportTile(db, painter, tiles, idx, T, want, stats, started), 3000);
        }
    }

    // ChunkMapLayer.GenerateChunkImage (the parchment map, colorAccurate off),
    // reading the server's chunks instead of the client's.
    private sealed class MapPainter
    {
        private readonly ICoreServerAPI sapi;
        private readonly int[] colors;
        private readonly byte[] block2Color;
        private readonly int wateredge;
        private readonly IWorldChunk[] col;

        public MapPainter(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
            var codes = ChunkMapLayer.hexColorsByCode;
            var byCode = new Dictionary<string, int>();
            colors = new int[codes.Count];
            int i = 0;
            foreach (var kv in codes)
            {
                colors[i] = ColorUtil.ReverseColorBytes(ColorUtil.Hex2Int(kv.Value));
                byCode[kv.Key] = i++;
            }
            wateredge = colors[byCode["wateredge"]];
            var blocks = sapi.World.Blocks;
            block2Color = new byte[blocks.Count];
            for (int b = 0; b < blocks.Count; b++)
            {
                var block = blocks[b];
                string code = "land";
                if (block?.Attributes != null)
                {
                    code = block.Attributes["mapColorCode"].AsString(null);
                    if (code == null && !ChunkMapLayer.defaultMapColorCodes.TryGetValue(block.BlockMaterial, out code)) code = "land";
                }
                block2Color[b] = (byte)(byCode.TryGetValue(code, out int ci) ? ci : byCode["land"]);
            }
            col = new IWorldChunk[sapi.WorldManager.MapSizeY / 32];
        }

        // materials as the game numbers them: 8 liquid, 9 snow, 10 ice
    private static bool IsLake(Block b) => (int)b.BlockMaterial == 8 || ((int)b.BlockMaterial == 10 && b.Code.Path != "glacierice");

        private int Color(Block b) => colors[block2Color[b.Id]];

        public int[] Paint(int cx, int cz)
        {
            var wm = sapi.WorldManager;
            var mc = wm.GetMapChunk(cx, cz);
            if (mc == null) return null;
            for (int cy = 0; cy < col.Length; cy++)
            {
                col[cy] = wm.GetChunk(cx, cy, cz);
                if (col[cy] == null) return null;
            }
            var blocks = sapi.World.Blocks;
            var px = new int[1024];
            var mcNW = wm.GetMapChunk(cx - 1, cz - 1);
            var mcW = wm.GetMapChunk(cx - 1, cz);
            var mcN = wm.GetMapChunk(cx, cz - 1);
            var shadow = new byte[1024];
            for (int i = 0; i < shadow.Length; i++) shadow[i] = 128;
            for (int j = 0; j < 1024; j++)
            {
                int h = mc.RainHeightMap[j];
                int cy = h / 32;
                if (cy >= col.Length) continue;
                int x = j % 32, z = j / 32;
                float shade = 1f;
                IMapChunk a = mc, bW = mc, cN = mc;
                int xl = x - 1, xr = x, zu = z - 1, zd = z;
                if (xl < 0 && zu < 0) { a = mcNW; bW = mcW; cN = mcN; }
                else
                {
                    if (xl < 0) { a = mcW; bW = mcW; }
                    if (zu < 0) { a = mcN; cN = mcN; }
                }
                xl = GameMath.Mod(xl, 32); zu = GameMath.Mod(zu, 32);
                int d1 = a != null ? h - a.RainHeightMap[zu * 32 + xl] : 0;
                int d2 = bW != null ? h - bW.RainHeightMap[zd * 32 + xl] : 0;
                int d3 = cN != null ? h - cN.RainHeightMap[zu * 32 + xr] : 0;
                float sign = Math.Sign(d1) + Math.Sign(d2) + Math.Sign(d3);
                float steep = Math.Max(Math.Max(Math.Abs(d1), Math.Abs(d2)), Math.Abs(d3));
                Block block = blocks[col[cy].UnpackAndReadBlock(MapUtil.Index3d(x, h % 32, z, 32, 32), 3)];
                if (sign > 0) shade = 1.08f + Math.Min(0.5f, steep / 10f) / 1.25f;
                if (sign < 0) shade = 0.92f - Math.Min(0.5f, steep / 10f) / 1.25f;
                if ((int)block.BlockMaterial == 9)
                {
                    h--;
                    cy = h / 32;
                    block = blocks[col[cy].UnpackAndReadBlock(MapUtil.Index3d(x, h % 32, z, 32, 32), 3)];
                }
                if (IsLake(block))
                {
                    IWorldChunk cw = col[cy], ce = col[cy], cn = col[cy], cs = col[cy];
                    int xw = x - 1, xe = x + 1, zn = z - 1, zs = z + 1;
                    if (xw < 0) cw = wm.GetChunk(cx - 1, cy, cz);
                    if (xe >= 32) ce = wm.GetChunk(cx + 1, cy, cz);
                    if (zn < 0) cn = wm.GetChunk(cx, cy, cz - 1);
                    if (zs >= 32) cs = wm.GetChunk(cx, cy, cz + 1);
                    if (cw != null && ce != null && cn != null && cs != null)
                    {
                        xw = GameMath.Mod(xw, 32); xe = GameMath.Mod(xe, 32); zn = GameMath.Mod(zn, 32); zs = GameMath.Mod(zs, 32);
                        bool all = IsLake(blocks[cw.UnpackAndReadBlock(MapUtil.Index3d(xw, h % 32, z, 32, 32), 3)])
                            && IsLake(blocks[ce.UnpackAndReadBlock(MapUtil.Index3d(xe, h % 32, z, 32, 32), 3)])
                            && IsLake(blocks[cn.UnpackAndReadBlock(MapUtil.Index3d(x, h % 32, zn, 32, 32), 3)])
                            && IsLake(blocks[cs.UnpackAndReadBlock(MapUtil.Index3d(x, h % 32, zs, 32, 32), 3)]);
                        px[j] = all ? Color(block) : wateredge;
                    }
                    else px[j] = Color(block);
                }
                else
                {
                    shadow[j] = (byte)(shadow[j] * shade);
                    px[j] = Color(block);
                }
            }
            var sharp = (byte[])shadow.Clone();
            BlurTool.Blur(shadow, 32, 32, 2);
            for (int l = 0; l < 1024; l++)
            {
                float f = (int)((shadow[l] / 128f - 1f) * 5f) / 5f;
                f += (sharp[l] / 128f - 1f) * 5f % 1f / 5f;
                px[l] = ColorUtil.ColorMultiply3Clamped(px[l], f + 1f) | unchecked((int)0xFF000000);
            }
            for (int cy = 0; cy < col.Length; cy++) col[cy] = null;
            return px;
        }
    }
}
