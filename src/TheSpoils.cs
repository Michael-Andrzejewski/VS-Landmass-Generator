using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.MathTools;

namespace LandmassGenerator;

// ── THE SPOILS ─────────────────────────────────────────────────────────
// The Great Machine's dumping ground. Jonas cut pieces out of reality to
// feed the Great Work and threw what he could not use into the sea, and here
// it piled up: a heap a quarter of a kilometre across of pieces of everywhere,
// each one cut clean and dropped at whatever angle it fell. A slab of meadow
// stands on its edge with its grass face sideways, strata of three rocks lean
// against a wall of somebody's house, a block of glacier ice is wedged under
// a seam of copper ore, and the Devastation has grown through all of it.
// Caverns run through the heap where the pieces never met; things from other
// places lie in them (a room, a tree hanging from the roof, a frozen pond),
// with the salvage worth the climb. The Great Machine's red fog hangs over it.
//
// How: the heap is a power diagram. Space is cut into cells on a jittered
// grid (`cell` blocks apart, each with its own weight so sizes vary), and
// every cell is one piece with its own material, tilted inner layering and
// ore. A voxel takes the piece of the nearest weighted centre, so pieces meet
// along flat cut faces. The heap's outline is a few overlapping peaks; some
// pieces near the top are shards that jut out of it. Caverns and the tunnels
// between them (and out to the flanks) stay above the sea.
//
// radius= (or size=, which stops at 160) is the heap's radius; peaks=, top= (highest peak above the sea),
// cell=, caverns=, entrances=.
public partial class LandmassGeneratorModSystem
{
    private sealed class SpoilsCtx
    {
        public MegaKit K;
        public int Cx, Cz, R, Cell, Sea;
        public readonly List<(double X, double Z, double R, double H)> Peaks = new();
        public readonly HashSet<long> Void = new();
        public readonly List<(int X, int Y, int Z, int Rx, int Ry, int Rz)> Caverns = new();
        public int[] Rocks;
        public string[] RockNames;
        public int[][] Ores;            // per rock: the ore blocks it can host
        public int Grass, Soil, Drock, DevSoil, Gravel, Ice, Snow, Glass, Cobble, Planks, Log, Brick, Cracked, Mossy, ClayBrick,
            RustA, RustB, Plate, Panel, Coal, Shale, Peat, Quartz, Marble, Obsidian, Halite, ClayBlue, ClayRed, Sand, Glow;
        public int[] Growths;
        public int Chests, Pieces, Shards, Props, Vignettes;

        public static long Key(int x, int y, int z) => ((long)(x & 0x3FFFFF) << 42) | ((long)(y & 0xFFFFF) << 22) | (long)(z & 0x3FFFFF);
    }

    private static uint SpHash(int a, int b, int c, int salt)
    {
        unchecked
        {
            uint h = (uint)(a * 374761393 + b * 668265263 + c * 1274126177 + salt * 2246822519u);
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }

    private static double SpHash01(int a, int b, int c, int salt) => (SpHash(a, b, c, salt) & 0xFFFFFF) / 16777216.0;

    // The heap's top over (x, z): the tallest of its overlapping peaks, with
    // a ragged edge of small bumps. Below the island's ground it is no heap.
    private static double SpoilsTop(SpoilsCtx s, int x, int z)
    {
        double best = 0;
        foreach (var p in s.Peaks)
        {
            double dx = x - p.X, dz = z - p.Z;
            double t = Math.Sqrt(dx * dx + dz * dz) / p.R;
            if (t >= 1) continue;
            double h = p.H * Math.Pow(1 - t * t, 1.15);
            if (h > best) best = h;
        }
        if (best <= 0) return 0;
        double n = SpHash01(x >> 2, z >> 2, 0, 31) - 0.5 + (SpHash01(x >> 3, z >> 3, 1, 37) - 0.5) * 1.6;
        return s.Sea + best + n * 2.4;
    }

    private void MegaSpoils(MegaKit k, int cx, int cz)
    {
        var s = new SpoilsCtx { K = k, Cx = cx, Cz = cz, R = Math.Max(40, k.OptI("radius", k.Def.Size)), Cell = Math.Clamp(k.OptI("cell", 8), 4, 20), Sea = k.Sea };
        int N(params string[] codes)
        {
            foreach (var c in codes) { int i = k.Id(c); if (i != 0) return i; }
            return codes.Length > 0 ? k.Need(codes[0]) : 0;
        }

        // ── palette: rocks that exist, the ores each can host ──────────────
        string[] rockNames = { "granite", "andesite", "basalt", "chalk", "limestone", "sandstone", "slate", "peridotite", "claystone",
            "conglomerate", "chert", "phyllite", "shale", "bauxite", "kimberlite", "suevite", "whitemarble", "redmarble", "greenmarble", "scoria", "tuff" };
        string[] graded = { "nativecopper", "malachite", "cassiterite", "sphalerite", "galena", "bismuthinite", "hematite", "limonite", "magnetite",
            "nativegold", "nativesilver", "pentlandite", "chromite", "ilmenite", "rhodochrosite", "wolframite", "uranium" };
        string[] grades = { "poor", "medium", "rich", "bountiful" };
        string[] ungraded = { "quartz", "bituminouscoal", "lignite", "anthracite", "sulfur", "cinnabar", "olivine", "borax", "fluorite", "graphite",
            "kernite", "lapislazuli", "phosphorite", "alum", "sylvite" };
        var rocks = new List<int>(); var names = new List<string>(); var ores = new List<int[]>();
        foreach (string r in rockNames)
        {
            int id = k.Id("rock-" + r);
            if (id == 0) continue;
            var o = new List<int>();
            foreach (string m in graded)
                foreach (string g in grades) { int oi = k.Id($"ore-{g}-{m}-{r}"); if (oi != 0) o.Add(oi); }
            foreach (string m in ungraded) { int oi = k.Id($"ore-{m}-{r}"); if (oi != 0) o.Add(oi); }
            rocks.Add(id); names.Add(r); ores.Add(o.ToArray());
        }
        if (rocks.Count == 0) { k.Need("rock-granite"); return; }
        s.Rocks = rocks.ToArray(); s.RockNames = names.ToArray(); s.Ores = ores.ToArray();
        s.Grass = N("soil-medium-normal", "soil-low-normal");
        s.Soil = N("soil-medium-none", "soil-low-none");
        s.Drock = N("drock", "rock-basalt");
        s.DevSoil = N("devastatedsoil-3", "devastatedsoil-0");
        s.Gravel = N("gravel-granite", "gravel-andesite");
        s.Ice = N("packedglacierice", "glacierice", "lakeice");
        s.Snow = N("snowblock", "packedglacierice");
        s.Glass = N("glass-plain", "glass-quartz");
        s.Cobble = N("cobblestone-granite", "cobblestone-andesite");
        s.Planks = N("planks-oak-ud", "planks-aged-ud");
        s.Log = N("log-placed-oak-ud", "log-placed-aged-ud");
        s.Brick = N("stonebricks-granite", "stonebricks-andesite");
        s.Cracked = N("crackedstonebricks-granite", "stonebricks-granite");
        s.Mossy = N("mossystonebricks-granite", "stonebricks-granite");
        s.ClayBrick = N("claybricks-good-fire", "claybricks-damaged-fire");
        s.RustA = k.RustA; s.RustB = k.RustB; s.Plate = k.PlateA;
        s.Panel = N("dpanel-panel1-aged", "metalblock-corroded-plain-rusty-iron");
        s.Shale = N("rock-shale", "rock-claystone");
        s.Coal = N("ore-bituminouscoal-shale", "ore-lignite-shale", "rock-shale");
        s.Peat = N("peat-none", "peat");
        s.Quartz = N("ore-quartz-whitemarble", "ore-quartz-granite", "glass-quartz");
        s.Marble = N("rock-whitemarble", "rock-granite");
        s.Obsidian = N("rock-obsidian", "rock-basalt");
        s.Halite = N("rock-halite", "rock-chalk");
        s.ClayBlue = N("rawclay-blue-none");
        s.ClayRed = N("rawclay-red-none", "rawclay-blue-none");
        s.Sand = N("sand-granite", "sand-limestone");
        s.Growths = new[] { k.Id("devgrowth-thorns"), k.Id("devgrowth-shard"), k.Id("devgrowth-shrike"), k.Id("devgrowth-bush"), k.Id("devastationgrowth-normal") }
            .Where(i => i != 0).ToArray();
        s.Glow = k.Id("landmassgenerator:ghostlight-blue");
        if (s.Ice == 0) s.Ice = s.Marble;
        if (s.Snow == 0) s.Snow = s.Ice;
        if (s.Peat == 0) s.Peat = s.Soil;

        // ── the outline: overlapping peaks ─────────────────────────────────
        int top = k.OptI("top", 72);
        int nPeaks = Math.Max(1, k.OptI("peaks", 6));
        s.Peaks.Add((cx, cz, s.R * 0.78, top * 0.88));
        for (int i = 1; i < nPeaks; i++)
        {
            double a = k.R() * Math.PI * 2, d = s.R * (0.25 + k.R() * 0.42);
            s.Peaks.Add((cx + Math.Cos(a) * d, cz + Math.Sin(a) * d, s.R * (0.32 + k.R() * 0.3), top * (0.45 + k.R() * 0.55)));
        }

        // ── caverns and the tunnels between them ──────────────────────────
        int nCav = k.OptI("caverns", 9);
        for (int t = 0; t < nCav * 30 && s.Caverns.Count < nCav; t++)
        {
            double a = k.R() * Math.PI * 2, d = Math.Sqrt(k.R()) * s.R * 0.62;
            int x = cx + (int)(Math.Cos(a) * d), z = cz + (int)(Math.Sin(a) * d);
            int rx = k.RI(8, 15), rz = k.RI(8, 15), ry = k.RI(5, 8);
            double hTop = Math.Min(Math.Min(SpoilsTop(s, x - rx, z), SpoilsTop(s, x + rx, z)), Math.Min(SpoilsTop(s, x, z - rz), SpoilsTop(s, x, z + rz)));
            int floorMin = Math.Max(s.Sea + 4, k.Ground(x, z) + 2);
            int ceilMax = (int)hTop - 7;
            if (ceilMax - floorMin < 2 * ry + 1) continue;
            int y = k.RI(floorMin + ry, ceilMax - ry);
            if (s.Caverns.Any(c => Math.Abs(c.X - x) < c.Rx + rx + 6 && Math.Abs(c.Z - z) < c.Rz + rz + 6 && Math.Abs(c.Y - y) < c.Ry + ry + 4)) continue;
            s.Caverns.Add((x, y, z, rx, ry, rz));
        }
        foreach (var c in s.Caverns) SpoilsCarveCavern(s, c);
        // a chain through the nearest neighbours, then a few loops
        var linked = new List<int> { 0 };
        var pairs = new List<(int A, int B)>();
        while (linked.Count < s.Caverns.Count)
        {
            int bi = -1, bj = -1; long bd = long.MaxValue;
            foreach (int i in linked)
                for (int j = 0; j < s.Caverns.Count; j++)
                {
                    if (linked.Contains(j)) continue;
                    long dx = s.Caverns[i].X - s.Caverns[j].X, dz = s.Caverns[i].Z - s.Caverns[j].Z, dy = s.Caverns[i].Y - s.Caverns[j].Y;
                    long dd = dx * dx + dz * dz + dy * dy * 4;
                    if (dd < bd) { bd = dd; bi = i; bj = j; }
                }
            if (bj < 0) break;
            linked.Add(bj); pairs.Add((bi, bj));
        }
        for (int i = 0; i < s.Caverns.Count / 3; i++)
        {
            int a = k.RI(0, s.Caverns.Count - 1), b = k.RI(0, s.Caverns.Count - 1);
            if (a != b) pairs.Add((a, b));
        }
        foreach (var (a, b) in pairs)
        {
            var A = s.Caverns[a]; var B = s.Caverns[b];
            SpoilsTunnel(s, A.X, A.Y - A.Ry + 3, A.Z, B.X, B.Y - B.Ry + 3, B.Z, 2.3 + k.R() * 1.0);
        }
        // the ways in: from a cavern out through the flank of the heap
        int nEnt = k.OptI("entrances", 6), entrances = 0;
        for (int i = 0; i < nEnt && s.Caverns.Count > 0; i++)
        {
            double a = i * Math.PI * 2 / nEnt + k.R() * 0.6;
            // the cavern nearest that side
            var c = s.Caverns.OrderByDescending(cv => (cv.X - cx) * Math.Cos(a) + (cv.Z - cz) * Math.Sin(a)).First();
            int fy = c.Y - c.Ry + 3;
            double x = c.X, z = c.Z, y = fy;
            int steps = 0;
            // walk outward, rising gently, until the heap's skin is passed
            while (steps++ < 400)
            {
                x += Math.Cos(a); z += Math.Sin(a);
                double tTop = SpoilsTop(s, (int)x, (int)z);
                if (y > tTop + 1 || tTop <= 0) break;
                if (steps % 3 == 0 && y < fy + 10) y += 1;
            }
            if (steps >= 400 || y < s.Sea + 3) continue;
            SpoilsTunnel(s, c.X, fy, c.Z, (int)x, (int)y, (int)z, 2.6 + k.R() * 0.8);
            entrances++;
        }

        // ── the heap, voxel by voxel ──────────────────────────────────────
        int C = s.Cell;
        int R2 = (int)(s.R * 1.05);
        for (int x = cx - R2; x <= cx + R2; x++)
            for (int z = cz - R2; z <= cz + R2; z++)
            {
                double tTop = SpoilsTop(s, x, z);
                if (tTop <= 0) continue;
                int g = k.Ground(x, z);
                int h = (int)Math.Round(tTop);
                if (h <= g) continue;
                int surface = -1;
                for (int y = g + 1; y <= h; y++)
                {
                    if (s.Void.Contains(SpoilsCtx.Key(x, y, z))) continue;
                    int id = SpoilsPieceBlock(s, x, y, z, out _);
                    if (id == 0) continue;
                    k.Set(x, y, z, id);
                    surface = y;
                }
                if (surface < 0 || surface < h) continue;
                // the Devastation on the top: patches of drock and devastated
                // soil, and its growths standing out of them
                double inf = SpHash01(x >> 3, z >> 3, 2, 41) * 0.6 + SpHash01(x >> 5, z >> 5, 3, 43) * 0.4;
                double r0 = SpHash01(x, z, surface, 47);
                if (inf > 0.55 && r0 < 0.75) k.Set(x, surface, z, r0 < 0.4 ? s.DevSoil : s.Drock);
                double r1 = SpHash01(x, z, surface, 53);
                if (s.Growths.Length > 0 && r1 < (inf > 0.55 ? 0.09 : 0.02))
                    k.Set(x, surface + 1, z, s.Growths[(int)(SpHash01(x, z, 9, 59) * s.Growths.Length) % s.Growths.Length]);
                else if (r1 > 0.996) { k.Clutter(x, surface + 1, z, SpoilsJunk(k), k.R() * Math.PI * 2); s.Props++; }
            }

        // A tunnel that dips under the island's own ground (a rise between
        // two caverns, the foot of a flank) is cut through it too.
        foreach (long key in s.Void)
        {
            // world x and z are positive and under 2^22, so they unpack as they went in
            int vx = (int)((ulong)key >> 42), vy = (int)((key >> 22) & 0xFFFFF), vz = (int)(key & 0x3FFFFF);
            if (vy <= k.Ground(vx, vz)) k.Set(vx, vy, vz, 0);
        }

        // ── shards jutting out of the heap ─────────────────────────────────
        int nShards = k.OptI("shards", s.R / 3);
        for (int i = 0; i < nShards; i++)
        {
            double a = k.R() * Math.PI * 2, d = Math.Sqrt(k.R()) * s.R * 0.9;
            int x = cx + (int)(Math.Cos(a) * d), z = cz + (int)(Math.Sin(a) * d);
            double tTop = SpoilsTop(s, x, z);
            if (tTop <= s.Sea + 3) continue;
            SpoilsShard(s, x, (int)tTop - 2, z);
        }

        // ── rusted machinery the Machine threw out with the rest ──────────
        for (int i = 0; i < k.OptI("gears", 7); i++)
        {
            double a = k.R() * Math.PI * 2, d = Math.Sqrt(k.R()) * s.R * 0.85;
            int x = cx + (int)(Math.Cos(a) * d), z = cz + (int)(Math.Sin(a) * d);
            double tTop = SpoilsTop(s, x, z);
            if (tTop <= 0) continue;
            double gr = 5 + k.R() * 9;
            k.Gear(x, tTop - gr * 0.45, z, k.R() < 0.5 ? 'x' : 'z', gr, 2, k.RI(4, 6), k.R() * 6, () => k.Rust(),
                (gx, gy, gz) => s.Void.Contains(SpoilsCtx.Key(gx, gy, gz)));
        }

        // ── what lies in the caverns ──────────────────────────────────────
        foreach (var c in s.Caverns) SpoilsFurnish(s, c);
        // the scavenger's notes in the first chest left in the heap; the
        // scribe's record of the day Jonas came to look in the next one
        var heapChests = k.Job.StructChests.Select((ch, i) => (ch, i)).Where(t => t.ch.Label == "the spoils").Select(t => t.i).ToList();
        if (heapChests.Count > 0) k.Job.StructChests[heapChests[0]].Contents.Insert(0, "lore:spoils_scavenger");
        if (heapChests.Count > 0) k.Job.StructChests[heapChests[Math.Min(1, heapChests.Count - 1)]].Contents.Insert(0, "lore:spoils_jonas");
        if (heapChests.Count > 2) k.Job.StructChests[heapChests[2]].Contents.Insert(0, "randomlore:rfpool-jonas");
        // the end of the arrows: a worn chart to somewhere not yet found (0.70.0)
        if (heapChests.Count > 0) k.Job.StructChests[heapChests[^1]].Contents.Insert(0, "landmassgenerator:islandmap-random");

        k.AfterCommit.Add(() =>
        {
            int fogR = Math.Max(s.R + 40, k.Job.Diameter / 2);
            RegisterRedFogZone(k.Job.Dim, cx, cz, fogR, k.OptI("fade", 160));
            sapi.Logger.Notification("[landmassgenerator] the spoils at HUD {0}, {1}: heap radius {2}, top {3} above the sea, {4} caverns, {5} ways in, {6} shards, {7} vignettes, {8} chests, {9} props",
                cx - sapi.WorldManager.MapSizeX / 2, cz - sapi.WorldManager.MapSizeZ / 2, s.R, top, s.Caverns.Count, entrances, s.Shards, s.Vignettes, s.Chests, s.Props);
        });
    }

    private static string SpoilsJunk(MegaKit k)
    {
        double r = k.R();
        return "clutter|" + (r < 0.2 ? "pile-trash-scrap" : r < 0.35 ? "crate/crate-large-junk" : r < 0.48 ? "crate/crate-medium-junk"
            : r < 0.6 ? "crate/large-metaljunk1" : r < 0.72 ? "pile-trash-pottery" : r < 0.84 ? "wheel-spoked-rusty" : r < 0.92 ? "crate/large-generic-junk1" : "pile-trash-oldore");
    }

    // The piece a voxel belongs to, and that piece's block there.
    private static int SpoilsPieceBlock(SpoilsCtx s, int x, int y, int z, out uint piece)
    {
        int C = s.Cell;
        int gx = (int)Math.Floor(x / (double)C), gy = (int)Math.Floor(y / (double)C), gz = (int)Math.Floor(z / (double)C);
        double best = double.MaxValue;
        int bi = 0, bj = 0, bk = 0;
        double bcx = 0, bcy = 0, bcz = 0;
        for (int i = gx - 1; i <= gx + 1; i++)
            for (int j = gy - 1; j <= gy + 1; j++)
                for (int l = gz - 1; l <= gz + 1; l++)
                {
                    double px = (i + 0.15 + 0.7 * SpHash01(i, j, l, 1)) * C;
                    double py = (j + 0.15 + 0.7 * SpHash01(i, j, l, 2)) * C;
                    double pz = (l + 0.15 + 0.7 * SpHash01(i, j, l, 3)) * C;
                    double w = SpHash01(i, j, l, 4);
                    w = w * w * C * C * 1.1;   // most pieces small, a few big
                    double dx = x - px, dy = y - py, dz = z - pz;
                    double d = dx * dx + dy * dy + dz * dz - w;
                    if (d < best) { best = d; bi = i; bj = j; bk = l; bcx = px; bcy = py; bcz = pz; }
                }
        piece = SpHash(bi, bj, bk, 5);
        return SpoilsMaterial(s, piece, bi, bj, bk, x - bcx, y - bcy, z - bcz, x, y, z);
    }

    // One piece's block at offset (ox, oy, oz) from its centre.
    private static int SpoilsMaterial(SpoilsCtx s, uint piece, int i, int j, int l, double ox, double oy, double oz, int x, int y, int z)
    {
        double kind = (piece & 0xFFFF) / 65536.0;
        // the piece's own "up", the way it lay before it was cut
        double ux = SpHash01(i, j, l, 6) * 2 - 1, uy = SpHash01(i, j, l, 7) * 2 - 1, uz = SpHash01(i, j, l, 8) * 2 - 1;
        double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz) + 1e-6;
        double t = (ox * ux + oy * uy + oz * uz) / ul;
        int rock = (int)(SpHash01(i, j, l, 9) * s.Rocks.Length) % s.Rocks.Length;
        double v = SpHash01(x, y, z, 10);
        if (kind < 0.27)
        {
            // rock with its ore: most pieces a sprinkling, a few rich seams
            var ores = s.Ores[rock];
            double dens = SpHash01(i, j, l, 11);
            dens = dens < 0.75 ? 0.03 + dens * 0.1 : 0.25 + (dens - 0.75) * 1.2;
            if (ores.Length > 0 && v < dens)
            {
                int o1 = ores[(int)(SpHash01(i, j, l, 12) * ores.Length) % ores.Length];
                int o2 = ores[(int)(SpHash01(i, j, l, 13) * ores.Length) % ores.Length];
                return v < dens * 0.7 ? o1 : o2;
            }
            return s.Rocks[rock];
        }
        if (kind < 0.36)
        {
            // strata: three rocks in tilted bands
            int band = (int)Math.Floor(t / 2.5);
            int r2 = (int)(SpHash01(i, j, l + band, 14) * s.Rocks.Length) % s.Rocks.Length;
            return s.Rocks[((band % 3) + 3) % 3 == 0 ? rock : r2];
        }
        if (kind < 0.50) return v < 0.7 ? s.Drock : s.DevSoil;               // the Devastation's own
        if (kind < 0.58) return t > 2.0 ? s.Grass : t > -1.0 ? s.Soil : s.Rocks[rock];   // a slab of meadow
        if (kind < 0.62) return t > 1.0 ? s.Sand : t > -2 ? (v < 0.5 ? s.Sand : s.Gravel) : s.Rocks[rock];
        if (kind < 0.66) return v < 0.85 ? s.Gravel : s.Cobble;
        if (kind < 0.69) return t > 0 ? s.ClayBlue : s.ClayRed;
        if (kind < 0.73) return t > 1.5 ? s.Snow : s.Ice;                    // a block of tundra
        if (kind < 0.77) return Math.Abs(t) < 1.0 ? s.Planks : s.Log;        // stacked timber
        if (kind < 0.81)
        {
            // somebody's house: a stone footing, a plank wall, a window
            if (t < -1.5) return s.Cobble;
            if (t < 2.5) return (Math.Abs(ox) < 1.2 && Math.Abs(oz) < 1.2) ? s.Glass : s.Planks;
            return s.ClayBrick;
        }
        if (kind < 0.86) return v < 0.55 ? s.Brick : v < 0.8 ? s.Cracked : s.Mossy;   // a stretch of wall
        if (kind < 0.92) return v < 0.4 ? s.RustA : v < 0.7 ? s.RustB : v < 0.9 ? s.Plate : s.Panel;   // the Machine's own
        if (kind < 0.95) return t > 2 ? s.Peat : v < 0.18 ? s.Coal : s.Shale;
        if (kind < 0.97)
        {
            // a geode: marble round a heart of quartz
            double r = Math.Sqrt(ox * ox + oy * oy + oz * oz);
            return r < 2.5 ? s.Quartz : s.Marble;
        }
        if (kind < 0.99) return s.Obsidian;
        return s.Halite;
    }

    private static void SpoilsCarve(SpoilsCtx s, int x, int y, int z)
    {
        if (y < s.Sea + 2) return;
        s.Void.Add(SpoilsCtx.Key(x, y, z));
    }

    private static void SpoilsCarveCavern(SpoilsCtx s, (int X, int Y, int Z, int Rx, int Ry, int Rz) c)
    {
        int floor = c.Y - c.Ry + 2;   // a flat floor across the bottom
        for (int x = -c.Rx; x <= c.Rx; x++)
            for (int z = -c.Rz; z <= c.Rz; z++)
                for (int y = -c.Ry; y <= c.Ry; y++)
                {
                    double w = SpHash01(c.X + x >> 2, c.Y + y >> 2, c.Z + z >> 2, 61) * 0.25;
                    double e = (x * x) / (double)(c.Rx * c.Rx) + (y * y) / (double)(c.Ry * c.Ry) + (z * z) / (double)(c.Rz * c.Rz);
                    if (e > 1 - w) continue;
                    if (c.Y + y < floor) continue;
                    SpoilsCarve(s, c.X + x, c.Y + y, c.Z + z);
                }
    }

    // A round bore from A to B with a flat floor, wandering a little.
    private static void SpoilsTunnel(SpoilsCtx s, int ax, int ay, int az, int bx, int by, int bz, double r)
    {
        double dx = bx - ax, dy = by - ay, dz = bz - az;
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1) return;
        int n = (int)Math.Ceiling(len);
        double wob = SpHash01(ax, ay, az, 63) * 6.28;
        for (int i = 0; i <= n; i++)
        {
            double f = i / (double)n;
            double side = Math.Sin(f * Math.PI) * Math.Sin(f * 7 + wob) * Math.Min(6, len * 0.08);
            double px = ax + dx * f - dz / len * side, py = ay + dy * f, pz = az + dz * f + dx / len * side;
            int ri = (int)Math.Ceiling(r);
            for (int x = -ri; x <= ri; x++)
                for (int z = -ri; z <= ri; z++)
                    for (int y = 0; y <= ri * 2 - 1; y++)
                    {
                        double cy = y - r + 0.5;
                        if (x * x + z * z + cy * cy > r * r + 0.5) continue;
                        SpoilsCarve(s, (int)Math.Round(px) + x, (int)Math.Round(py) + y, (int)Math.Round(pz) + z);
                    }
        }
    }

    // A slab of something cut clean and driven into the heap at an angle,
    // sticking out of it.
    private void SpoilsShard(SpoilsCtx s, int bx, int by, int bz)
    {
        var k = s.K;
        double a = k.R() * Math.PI * 2, tilt = 0.2 + k.R() * 0.9;
        // long axis: up and leaning; the slab is thin across one side
        double lx = Math.Sin(tilt) * Math.Cos(a), ly = Math.Cos(tilt), lz = Math.Sin(tilt) * Math.Sin(a);
        double wx = -Math.Sin(a), wz = Math.Cos(a);                    // across, horizontal
        double tx = ly * wz, ty = lz * wx - lx * wz, tz = -ly * wx;   // thickness axis
        double L = 8 + k.R() * 16, W = 2 + k.R() * 5, T = 1 + k.R() * 1.8;
        uint piece = (uint)(k.R() * uint.MaxValue);
        int ci = bx, cj = by, cl = bz;
        int reach = (int)Math.Ceiling(L + W + T);
        for (int x = -reach; x <= reach; x++)
            for (int z = -reach; z <= reach; z++)
                for (int y = -4; y <= reach; y++)
                {
                    double pl = x * lx + y * ly + z * lz, pw = x * wx + z * wz, pt = x * tx + y * ty + z * tz;
                    if (pl < -3 || pl > L || Math.Abs(pw) > W || Math.Abs(pt) > T) continue;
                    int X = bx + x, Y = by + y, Z = bz + z;
                    if (Y < s.Sea + 1 || s.Void.Contains(SpoilsCtx.Key(X, Y, Z))) continue;
                    k.Set(X, Y, Z, SpoilsMaterial(s, piece, ci, cj, cl, x, y, z, X, Y, Z));
                }
        s.Shards++;
    }

    // What lies in a cavern: rubble and growths on the floor, the salvage,
    // a strange light, and one piece of somewhere else.
    private void SpoilsFurnish(SpoilsCtx s, (int X, int Y, int Z, int Rx, int Ry, int Rz) c)
    {
        var k = s.K;
        int floor = c.Y - c.Ry + 2;
        bool Open(int x, int y, int z) => s.Void.Contains(SpoilsCtx.Key(x, y, z));
        // floor cells: open, with no open cell under them
        var cells = new List<(int X, int Z)>();
        for (int x = c.X - c.Rx; x <= c.X + c.Rx; x++)
            for (int z = c.Z - c.Rz; z <= c.Z + c.Rz; z++)
                if (Open(x, floor, z) && !Open(x, floor - 1, z) && Open(x, floor + 1, z)) cells.Add((x, z));
        if (cells.Count < 12) return;
        // make the floor solid rubble so nothing stands on a hole
        foreach (var (x, z) in cells)
            k.Set(x, floor - 1, z, k.R() < 0.6 ? s.Gravel : s.Drock);
        var used = new HashSet<long>();
        (int X, int Z)? Free(int clear)
        {
            for (int t = 0; t < 30; t++)
            {
                var p = cells[k.RI(0, cells.Count - 1)];
                bool ok = true;
                for (int x = p.X - clear; x <= p.X + clear && ok; x++)
                    for (int z = p.Z - clear; z <= p.Z + clear && ok; z++)
                        if (used.Contains(((long)x << 32) | (uint)z)) ok = false;
                if (!ok) continue;
                used.Add(((long)p.X << 32) | (uint)p.Z);
                return p;
            }
            return null;
        }
        // the piece of somewhere else gets its ground first (built below)
        var at = Free(4);
        if (at != null)
            for (int x = at.Value.X - 3; x <= at.Value.X + 3; x++)
                for (int z = at.Value.Z - 3; z <= at.Value.Z + 3; z++) used.Add(((long)x << 32) | (uint)z);
        // the salvage
        int chests = k.RI(1, 3);
        for (int i = 0; i < chests; i++)
        {
            var p = Free(3);
            if (p == null) break;
            k.Job.StructChests.Add((new BlockPos(p.Value.X, floor, p.Value.Z, k.Job.Dim), "normal-aged", RollTable("spoils", k.Rand, k.RI(4, 7)), "the spoils"));
            s.Chests++;
        }
        // junk and growths
        for (int i = 0; i < cells.Count / 14; i++)
        {
            var p = Free(1);
            if (p == null) break;
            if (k.R() < 0.55) { k.Clutter(p.Value.X, floor, p.Value.Z, SpoilsJunk(k), k.R() * Math.PI * 2); s.Props++; }
            else if (s.Growths.Length > 0) k.Set(p.Value.X, floor, p.Value.Z, s.Growths[k.RI(0, s.Growths.Length - 1)]);
        }
        // a cold light set into the wall
        if (s.Glow != 0)
            for (int i = 0; i < 3; i++)
            {
                double a = k.R() * Math.PI * 2;
                int lx = c.X + (int)Math.Round(Math.Cos(a) * (c.Rx + 1)), lz = c.Z + (int)Math.Round(Math.Sin(a) * (c.Rz + 1));
                int ly = floor + k.RI(2, Math.Max(2, c.Ry));
                if (!Open(lx, ly, lz) && ly < SpoilsTop(s, lx, lz) - 1 && ly > k.Ground(lx, lz)) k.Glow(lx, ly, lz, s.Glow);
            }

        // one piece of somewhere else
        if (at == null) return;
        int vx = at.Value.X, vz = at.Value.Z;
        double kind = k.R();
        if (kind < 0.25)
        {
            // a tree hanging from the roof, crown down
            int roof = floor;
            while (Open(vx, roof + 1, vz) && roof < floor + 2 * c.Ry + 2) roof++;
            int leaves = k.IdF("leaves-grown-oak", "leavesbranchy-grown-oak", "leaves-placed-oak");
            for (int y = roof; y > roof - 5 && y > floor + 3; y--) k.Set(vx, y, vz, s.Log);
            int cy = roof - 6;
            if (leaves != 0)
                for (int x = -2; x <= 2; x++)
                    for (int z = -2; z <= 2; z++)
                        for (int y = -2; y <= 1; y++)
                            if (x * x + z * z + y * y <= 6 && cy + y > floor + 1 && Open(vx + x, cy + y, vz + z)) k.Set(vx + x, cy + y, vz + z, leaves);
        }
        else if (kind < 0.5)
        {
            // a corner of somebody's room: floor boards, a table, chairs, a lit lamp
            for (int x = -2; x <= 2; x++)
                for (int z = -2; z <= 2; z++)
                    if (Open(vx + x, floor, vz + z)) k.Set(vx + x, floor - 1, vz + z, s.Planks);
            k.Clutter(vx, floor, vz, "clutter|table-aged", 0);
            k.Clutter(vx + 1, floor, vz, "clutter|chair-aged", Math.PI / 2);
            k.Clutter(vx - 1, floor, vz + 1, "clutter|bookshelves/bookpile" + k.RI(1, 5), 0);
            k.Clutter(vx - 2, floor, vz - 2, "clutter|lantern/ground" + k.RI(1, 8), 0);
            s.Props += 4;
        }
        else if (kind < 0.7)
        {
            // a frozen pond, snow round its edge
            for (int x = -3; x <= 3; x++)
                for (int z = -3; z <= 3; z++)
                {
                    int d2 = x * x + z * z;
                    if (d2 > 10 || !Open(vx + x, floor, vz + z)) continue;
                    k.Set(vx + x, floor - 1, vz + z, d2 <= 5 ? s.Ice : s.Snow);
                }
        }
        else if (kind < 0.85)
        {
            // a patch of meadow, flowers and all
            int[] flowers = { k.Id("flower-cornflower-free"), k.Id("flower-catmint-free"), k.Id("flower-wilddaisy-free"), k.Id("tallgrass-medium-free") };
            for (int x = -3; x <= 3; x++)
                for (int z = -3; z <= 3; z++)
                {
                    if (x * x + z * z > 10 || !Open(vx + x, floor, vz + z)) continue;
                    k.Set(vx + x, floor - 1, vz + z, s.Grass);
                    int f = flowers[k.RI(0, flowers.Length - 1)];
                    if (f != 0 && k.R() < 0.5) k.Set(vx + x, floor, vz + z, f);
                }
        }
        else
        {
            // a seam of rich ore across the floor
            var ores = s.Ores[k.RI(0, s.Ores.Length - 1)];
            if (ores.Length > 0)
                for (int x = -3; x <= 3; x++)
                    for (int z = -1; z <= 1; z++)
                        if (Open(vx + x, floor, vz + z)) k.Set(vx + x, floor - 1, vz + z, ores[k.RI(0, ores.Length - 1)]);
        }
        s.Vignettes++;
    }
}
