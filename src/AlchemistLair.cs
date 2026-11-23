using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace LandmassGenerator;

// ── THE ALCHEMIST LAIR ─────────────────────────────────────────────────
// The alchemists of the old world kept a college on this island, south-east
// of where the Great Machine now stands: quad after quad of tall stone halls
// full of fine machinery, an observatory on the hill, and on the east shore,
// in a bowl of its own, the gateway they built to step through the world.
// They opened it. What came through was the Devastation, and it has grown
// out of the gateway over the whole college since: the halls nearest the bowl
// are holed and eaten into drock, the lawns are devastated ground, growths
// crust the slate roofs, and the clockwork stands in it all, stopped.
//
// The `alchemist` megastructure builds everything above the terrain
// (shapes/alchemist_lair.txt is the ground under it):
//   - the campus: a three by three grid of quads on the plateau, each a
//     courtyard closed by six halls of two to four storeys (stone, slate
//     gables, buttresses, tall windows, corner towers): libraries, archives,
//     laboratories, optics rooms, drafting rooms, dormitories, refectories,
//     kitchens, workshops, engine halls, cabinets of specimens, a chapel;
//   - in the courtyards: the great clock tower, an orrery in a fountain, a
//     walk-in engine of standing gears, an armillary sphere, a sundial
//     garden, and where the Devastation is worst, a blighted crater;
//   - paved streets between the quads, lit by Jonas lamps on posts;
//   - the observatory on the hill to the north: a round tower of three
//     storeys under a copper dome split for the great telescope, the orrery
//     and the star charts inside, a stair up the hillside;
//   - the portal gateway in the bowl on the east shore: a ring of polished
//     obsidian and brass on a stepped dais, ghostlights round its rim, coil
//     towers either side, the alchemists' control desk, a road and a pipe run
//     from the campus, and the wound it tore: shards of drock round its rim;
//   - the college gate on the south edge, a stair down to the landing quay;
//   - the Devastation over all of it (after the commit): thickest at the
//     gateway, thinning to the west.
// radius= is how far the island reaches (the overgrowth covers that much).
public partial class LandmassGeneratorModSystem
{
    private sealed class LairCtx
    {
        public MegaKit K;
        public int Cx, Cz, R, Sea, T0;
        public int HillX, HillZ, HillR, BowlX, BowlZ, BowlR;
        public readonly Dictionary<long, int> Tops = new();
        public readonly List<(int X0, int Z0, int X1, int Z1)> Taken = new();
        public int Wall, Cracked, Mossy, Quoin, Band, Found, Cobble, MossCobble, Pave, PaveSlab, Floor, FloorSlab, Ridge, Brick,
            BeamNS, BeamWE, BeamUD, Glass, Copper, Brass, Gold, Iron, Obsidian, ObsBrick, Drock, Growth, Grass, Dirt, Water, Ghost;
        public int[] DevSoil = Array.Empty<int>(), DevPlants = Array.Empty<int>(), Flowers = Array.Empty<int>();
        public readonly Dictionary<string, int> Roofing = new();
        public int Buildings, Storeys, Quads, Lamps, Machines, Chests, Courts, Overgrown;
        public int MaxY;

        public static long Col(int x, int z) => ((long)x << 32) | (uint)z;
        public bool Free(int x0, int z0, int x1, int z1, int pad = 0)
        {
            foreach (var t in Taken)
                if (x1 + pad >= t.X0 && x0 - pad <= t.X1 && z1 + pad >= t.Z0 && z0 - pad <= t.Z1) return false;
            return true;
        }
    }

    private static int LTop(LairCtx L, int x, int z) => L.Tops.TryGetValue(LairCtx.Col(x, z), out int t) ? t : L.K.Ground(x, z);

    // Smooth value noise: the hash on a grid `cell` blocks apart, blended.
    private static double LNoise(int x, int z, int cell, int salt)
    {
        int gx = (int)Math.Floor(x / (double)cell), gz = (int)Math.Floor(z / (double)cell);
        double fx = x / (double)cell - gx, fz = z / (double)cell - gz;
        fx = fx * fx * (3 - 2 * fx); fz = fz * fz * (3 - 2 * fz);
        double a = SpHash01(gx, gz, 0, salt), b = SpHash01(gx + 1, gz, 0, salt), c = SpHash01(gx, gz + 1, 0, salt), d = SpHash01(gx + 1, gz + 1, 0, salt);
        return a + (b - a) * fx + (c - a) * fz + (a - b - c + d) * fx * fz;
    }

    // How deep the Devastation lies here, 0..1: full at the gateway, fading
    // westward over the island, in fingers.
    private static double LairDev(LairCtx L, int x, int z)
    {
        double dx = x - L.BowlX, dz = z - L.BowlZ;
        double d = Math.Sqrt(dx * dx + dz * dz);
        double n = LNoise(x, z, 40, 211) * 0.65 + LNoise(x, z, 13, 223) * 0.35;
        return Math.Clamp(1.3 - d / (L.R * 1.15) + (n - 0.5) * 0.6, 0, 1);
    }

    // The centre of a shape region on the map, in world blocks, and its
    // radius (as if it were a disc); false when the shape has none of it.
    private static bool ShapeRegionCentre(IslandJob job, char ch, out int x, out int z, out int r)
    {
        x = z = r = 0;
        var s = job.Shape;
        if (s == null) return false;
        double sx = 0, sz = 0; int n = 0;
        for (int gx = 0; gx < s.W; gx++)
            for (int gz = 0; gz < s.H; gz++)
                if (s.Cells[gx, gz] == ch) { sx += gx; sz += gz; n++; }
        if (n == 0) return false;
        double lx = (sx / n + 0.5 - s.W / 2.0) * job.WorldPerCell, lz = (sz / n + 0.5 - s.H / 2.0) * job.WorldPerCell;
        x = job.Cx + (int)Math.Round(lx * job.RotCos - lz * job.RotSin);
        z = job.Cz + (int)Math.Round(lx * job.RotSin + lz * job.RotCos);
        r = (int)Math.Round(Math.Sqrt(n / Math.PI) * job.WorldPerCell);
        return true;
    }

    private void LairPalette(LairCtx L)
    {
        var k = L.K;
        int N(params string[] codes)
        {
            foreach (var c in codes) { int i = k.Id(c); if (i != 0) return i; }
            return codes.Length > 0 ? k.Need(codes[0]) : 0;
        }
        L.Wall = N("stonebricks-limestone", "stonebricks-granite");
        L.Cracked = N("crackedstonebricks-limestone", "crackedstonebricks-granite", "stonebricks-limestone");
        L.Mossy = N("mossystonebricks-limestone", "mossystonebricks-granite", "stonebricks-limestone");
        L.Quoin = N("rockpolished-granite", "stonebricks-granite");
        L.Band = N("stonebricks-granite", "stonebricks-andesite");
        L.Found = N("stonebricks-andesite", "cobblestone-andesite");
        L.Cobble = N("cobblestone-andesite", "cobblestone-granite");
        L.MossCobble = N("mossycobble-andesite", "cobblestone-andesite");
        L.Pave = N("stonebricks-andesite", "cobblestone-andesite");
        L.PaveSlab = N("stonebrickslab-andesite-down-free", "cobblestoneslab-andesite-down-free");
        L.Floor = N("planks-walnut-ud", "planks-oak-ud");
        L.FloorSlab = N("plankslab-walnut-down-free", "plankslab-oak-down-free");
        L.Ridge = N("clayshingleblock-gray", "clayshingleblock-black");
        L.Brick = N("claybricks-good-fire", "claybricks-damaged-fire");
        L.BeamNS = N("log-placed-aged-ns", "log-placed-oak-ns");
        L.BeamWE = N("log-placed-aged-we", "log-placed-oak-we");
        L.BeamUD = N("log-placed-aged-ud", "log-placed-oak-ud");
        L.Glass = N("glass-plain");
        L.Copper = N("metalblock-new-plain-copper", "metalblock-new-plain-brass", "metalblock-new-plain-iron");
        L.Brass = N("metalblock-new-plain-brass", "metalblock-new-plain-copper");
        L.Gold = N("metalblock-new-plain-gold", "metalblock-new-plain-brass", "metalblock-new-plain-copper");
        L.Iron = k.RustA != 0 ? k.RustA : N("metalblock-new-plain-iron");
        L.Obsidian = N("rockpolished-obsidian", "rock-obsidian", "rock-basalt");
        L.ObsBrick = N("stonebricks-basalt", "stonebricks-andesite");
        L.Drock = N("drock", "rock-basalt");
        L.Growth = k.Id("devastationgrowth-normal");
        L.Grass = N("soil-medium-normal", "soil-low-normal");
        L.Dirt = N("soil-medium-none", "soil-low-none");
        L.Water = N("water-still-7");
        L.Ghost = k.IdF("landmassgenerator:ghostlight-blue", "underwaterhorrors:ghostlight-blue");
        L.DevSoil = Enumerable.Range(0, 11).Select(i => k.Id("devastatedsoil-" + i)).Where(i => i != 0).ToArray();
        L.DevPlants = new[] { "devgrowth-shard", "devgrowth-shrike", "devgrowth-thorns", "devgrowth-bush" }.Select(c => k.Id(c)).Where(i => i != 0).ToArray();
        L.Flowers = new[] { "flower-cornflower-free", "flower-wilddaisy-free", "flower-catmint-free", "flower-lupine-purple-free", "flower-heather-free", "flower-forgetmenot-free" }
            .Select(c => k.Id(c)).Where(i => i != 0).ToArray();
        foreach (string dir in new[] { "north", "east", "south", "west" })
            L.Roofing[dir] = N("slantedroofing-slate-" + dir + "-free", "slantedroofing-grayclay-" + dir + "-free");
    }

    private void MegaAlchemist(MegaKit k, int cx, int cz)
    {
        var L = new LairCtx { K = k, Cx = cx, Cz = cz, R = Math.Max(120, k.OptI("radius", 290)), Sea = k.Sea };
        LairPalette(L);
        if (!ShapeRegionCentre(k.Job, 'O', out L.HillX, out L.HillZ, out L.HillR)) { L.HillX = cx; L.HillZ = cz - 190; L.HillR = 50; }
        if (!ShapeRegionCentre(k.Job, 'G', out L.BowlX, out L.BowlZ, out L.BowlR)) { L.BowlX = cx + 195; L.BowlZ = cz + 18; L.BowlR = 45; }

        // the campus level: the median ground round the middle
        var hs = new List<int>();
        for (int x = cx - 60; x <= cx + 60; x += 4)
            for (int z = cz - 60; z <= cz + 60; z += 4) hs.Add(k.Ground(x, z));
        hs.Sort();
        L.T0 = hs[hs.Count / 2];

        LairQuads(L);
        LairStreets(L);
        LairGate(L);
        LairObservatory(L);
        LairGateway(L);

        k.AfterCommit.Add(() => LairAfter(L));
    }

    // ── shared pieces ─────────────────────────────────────────────────────

    // Cut and fill a rectangle to level y (its top `top`), blended back into
    // the slope over `blend` blocks; the trees standing on it are felled.
    private void LairLevel(LairCtx L, int x0, int z0, int x1, int z1, int y, int blend, int top)
    {
        var k = L.K;
        FellTrees(k.Set, k.Job.Dim, x0 - blend - 2, z0 - blend - 2, x1 + blend + 2, z1 + blend + 2, Math.Min(y, k.Ground((x0 + x1) / 2, (z0 + z1) / 2)) - 3, y + 40);
        for (int x = x0 - blend; x <= x1 + blend; x++)
            for (int z = z0 - blend; z <= z1 + blend; z++)
            {
                int dOut = Math.Max(Math.Max(x0 - x, x - x1), Math.Max(z0 - z, z - z1));
                int gy = LTop(L, x, z);
                if (dOut > 0 && gy < k.Sea) continue;
                int target = dOut <= 0 ? y : (int)Math.Round(y + (gy - y) * (dOut / (double)(blend + 1)));
                for (int yy = Math.Min(gy, target) - 1; yy < target; yy++) k.Set(x, yy, z, yy < target - 3 ? k.Job.StoneId : L.Dirt);
                k.Set(x, target, z, dOut <= 0 ? top : L.Grass);
                for (int yy = target + 1; yy <= Math.Max(gy, target) + 4; yy++) k.Set(x, yy, z, 0);
                L.Tops[LairCtx.Col(x, z)] = target;
            }
    }

    private static int LairRoof(LairCtx L, string uphill) => L.Roofing.TryGetValue(uphill, out int id) ? id : L.Ridge;

    // Clutter: "type" plain, "!type" devastation machinery, "@front" a
    // cluttered bookshelf; each settles onto what holds it (the ~).
    private static void LairClut(LairCtx L, int x, int y, int z, string t, double heading)
    {
        if (string.IsNullOrEmpty(t)) return;
        if (t[0] == '!') L.K.Clutter(x, y, z, "~" + t.Substring(1), heading);
        else if (t[0] == '@')
        {
            string[] backs = { "bookshelf-ruined-full2", "bookshelf-ruined-full6", "bookshelf-ruined-full10", "bookshelf-ruined-full13" };
            L.K.Clutter(x, y, z, "~clutteredbookshelf-agedacacia|bookshelves/" + t.Substring(1) + "|half|bookshelves/" + backs[L.K.RI(0, backs.Length - 1)], heading);
        }
        else L.K.Clutter(x, y, z, "~" + (t.IndexOf('|') > 0 ? t : "clutter|" + t), heading);
    }

    private static void LairLamp(LairCtx L, int x, int y, int z, string type, double heading = 0)
    {
        L.K.Clutter(x, y, z, "~jonas-lamp|lightsource/on/" + type, heading);
        L.Lamps++;
    }

    // A straight flight of half steps from floor surface s0 up `rise` blocks,
    // starting at (x, z) and running along (ux, uz), `width` wide toward
    // (px, pz). Returns the cells it stands on.
    private List<(int X, int Z, double S)> LairFlight(LairCtx L, int x, int z, int ux, int uz, int px, int pz, int s0, int rise, int width)
    {
        var k = L.K;
        var cells = new List<(int X, int Z, double S)>();
        for (int i = 0; i < rise * 2; i++)
        {
            double s = s0 + 0.5 * (i + 1);
            bool half = s % 1 != 0;
            int y = half ? (int)Math.Floor(s) : (int)s - 1;
            for (int w = 0; w < width; w++)
            {
                int cx = x + ux * i + px * w, cz = z + uz * i + pz * w;
                for (int yy = s0; yy < y; yy++) k.Set(cx, yy, cz, L.Found);   // the stair's own masonry under it
                k.Set(cx, y, cz, half ? L.FloorSlab : L.Floor);
                for (int yy = y + 1; yy <= y + 3; yy++) k.Set(cx, yy, cz, 0);
                cells.Add((cx, cz, s));
            }
        }
        return cells;
    }

    // A gable roof over x0..x1, z0..z1 (the wall line), its ridge along the
    // long axis, one block of overhang all round, the lowest course at the
    // height of the top wall course `eave`. The gable ends are walled up.
    // `holes` (0..1) leaves patches of it fallen in.
    private void LairGable(LairCtx L, int x0, int z0, int x1, int z1, int eave, double holes)
    {
        var k = L.K;
        bool alongX = x1 - x0 >= z1 - z0;
        int m0 = (alongX ? z0 : x0) - 1, m1 = (alongX ? z1 : x1) + 1;   // across, with the overhang
        int a0 = (alongX ? x0 : z0) - 1, a1 = (alongX ? x1 : z1) + 1;   // along
        string upLo = alongX ? "south" : "east", upHi = alongX ? "north" : "west";
        void S(int along, int y, int across, int id)
        {
            if (alongX) k.Set(along, y, across, id); else k.Set(across, y, along, id);
        }
        bool Hole(int along, int across) => holes > 0 && LNoise(along * 3 + across, across * 3 - along, 5, 307) < holes * 0.55;
        for (int i = 0; ; i++)
        {
            int a = m0 + i, b = m1 - i, y = eave + i;
            if (a > b) break;
            for (int along = a0; along <= a1; along++)
            {
                if (a == b) { if (!Hole(along, a)) S(along, y, a, L.Ridge); continue; }
                if (!Hole(along, a)) S(along, y, a, LairRoof(L, upLo));
                if (!Hole(along, b)) S(along, y, b, LairRoof(L, upHi));
                if (a + 1 == b && !Hole(along, a)) S(along, y + 1, a, L.PaveSlab);   // a cap where the slopes meet
            }
            // the gables: wall between the slopes at both ends
            if (i >= 1)
                for (int across = a + 1; across <= b - 1; across++)
                {
                    bool window = across == (m0 + m1) / 2 && i >= 2 && i <= 3;
                    S(a0 + 1, y, across, window ? L.Glass : L.Wall);
                    S(a1 - 1, y, across, window ? L.Glass : L.Wall);
                }
            L.MaxY = Math.Max(L.MaxY, y + 1);
        }
    }

    // A hipped, pyramid roof over a small square (a tower's), slate, a
    // copper finial at the point.
    private void LairHip(LairCtx L, int x0, int z0, int x1, int z1, int eave)
    {
        var k = L.K;
        int y = eave;
        for (int i = 0; ; i++)
        {
            int ax0 = x0 - 1 + i, ax1 = x1 + 1 - i, az0 = z0 - 1 + i, az1 = z1 + 1 - i;
            y = eave + i;
            if (ax0 > ax1 || az0 > az1) break;
            if (ax0 == ax1 || az0 == az1)
            {
                for (int x = ax0; x <= ax1; x++) for (int z = az0; z <= az1; z++) k.Set(x, y, z, L.Ridge);
                break;
            }
            for (int x = ax0; x <= ax1; x++)
                for (int z = az0; z <= az1; z++)
                {
                    bool w = x == ax0, e = x == ax1, n = z == az0, s = z == az1;
                    if (!w && !e && !n && !s) continue;
                    int id = (w || e) && (n || s) ? L.Ridge : n ? LairRoof(L, "south") : s ? LairRoof(L, "north") : w ? LairRoof(L, "east") : LairRoof(L, "west");
                    k.Set(x, y, z, id);
                }
        }
        int mx = (x0 + x1) / 2, mz = (z0 + z1) / 2;
        for (int j = 1; j <= 3; j++) k.Set(mx, y + j, mz, j == 2 ? L.Gold : L.Copper);
        L.MaxY = Math.Max(L.MaxY, y + 4);
    }

    // A ring of blocks of radius r round (cx, cy, cz), in the plane whose
    // normal is (nx, ny, nz).
    private static void LairRing(LairCtx L, double cx, double cy, double cz, double r, double nx, double ny, double nz, int id)
    {
        double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        nx /= nl; ny /= nl; nz /= nl;
        // two axes in the plane
        double ax = 0, ay = 1, az = 0;
        if (Math.Abs(ny) > 0.9) { ax = 1; ay = 0; az = 0; }
        double ux = ay * nz - az * ny, uy = az * nx - ax * nz, uz = ax * ny - ay * nx;
        double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz); ux /= ul; uy /= ul; uz /= ul;
        double vx = ny * uz - nz * uy, vy = nz * ux - nx * uz, vz = nx * uy - ny * ux;
        for (double t = 0; t < Math.PI * 2; t += 0.4 / Math.Max(1, r))
        {
            double c = Math.Cos(t) * r, s = Math.Sin(t) * r;
            L.K.Set((int)Math.Round(cx + ux * c + vx * s), (int)Math.Round(cy + uy * c + vy * s), (int)Math.Round(cz + uz * c + vz * s), id);
        }
    }

    // ── the halls ─────────────────────────────────────────────────────────

    // What each kind of hall holds, storey by storey (furnished from the
    // Great Machine's room kinds, GreatMachineWorks.cs, plus two of its own).
    private static readonly Dictionary<string, string[]> LairStoreys = new()
    {
        ["library"] = new[] { "library", "archive", "library", "office" },
        ["laboratory"] = new[] { "lab", "lab", "specimens", "store" },
        ["observatory"] = new[] { "optics", "drafting", "optics", "archive" },
        ["lecture"] = new[] { "lecture", "lecture", "drafting", "office" },
        ["dormitory"] = new[] { "mess", "dorm", "dorm", "dorm" },
        ["refectory"] = new[] { "mess", "kitchen", "dorm", "store" },
        ["workshop"] = new[] { "workshop", "machine", "workshop", "store" },
        ["engine"] = new[] { "engine", "valves", "machine", "store" },
        ["museum"] = new[] { "specimens", "specimens", "archive", "office" },
        ["chapel"] = new[] { "chapel", "chapel", "office", "archive" },
        ["infirmary"] = new[] { "infirmary", "infirmary", "lab", "dorm" },
    };

    private static readonly Dictionary<string, (string[] Wall, string[] Mid, double Fill)> LairKinds = new()
    {
        ["library"] = (new[] { "@bookshelf-fancy1", "@bookshelf-fancy2", "@bookshelf-fancy3", "@bookshelf-fancy4", "@bookshelf-fancy5", "@bookshelf-full",
            "@bookshelf-alchemy01front", "@bookshelf-alchemy02front", "@bookshelf-alchemy03front", "bookshelves/scrollrack-full1", "globe2", "globe5" },
            new[] { "table-long-with-cloth-blue", "table-noble-double", "bookshelves/lecturn-aged-book-opened", "table-long-with-accessories" }, 0.85),
        ["lecture"] = (new[] { "bookshelves/scrollrack-full2", "@bookshelf-ruined-full4", "globe1", "globe3", "abacus2", "drafting-table-stuff", "coatrack-full2" },
            new[] { "table-long", "table-long", "table-long-with-accessories", "bookshelves/lecturn-aged-book-closed" }, 0.4),
        ["engine"] = (new[] { "!boilerend", "!boilerdoor-aged", "!tank1-aged", "!tank2-aged", "!gauge1", "!gauge3", "!valve2-aged", "!lever2-aged", "!switch1-aged",
            "!sightglass2-aged", "!junkpipe3", "!column1-aged", "machine1", "machine2", "machine6" },
            new[] { "!boilerbody", "!gearbox1", "!geartrain3", "!tower1", "!gear36", "machine7", "machine8" }, 0.7),
    };

    private void LairHall(LairCtx L, int x0, int z0, int x1, int z1, int y, int floors, string kind, int face, double ruin, bool tower, string lore = null)
    {
        var k = L.K;
        const int FH = 5;
        int eave = y + floors * FH;
        bool alongX = x1 - x0 >= z1 - z0;
        // podium: masonry from the ground up to the floor; the volume emptied
        for (int x = x0 - 1; x <= x1 + 1; x++)
            for (int z = z0 - 1; z <= z1 + 1; z++)
            {
                int gy = LTop(L, x, z);
                for (int yy = Math.Min(gy, y - 1); yy < y; yy++) k.Set(x, yy, z, L.Found);
                bool inside = x > x0 && x < x1 && z > z0 && z < z1;
                k.Set(x, y, z, inside ? L.Floor : L.Found);
                for (int yy = y + 1; yy <= eave + Math.Min(x1 - x0, z1 - z0) / 2 + 4; yy++) k.Set(x, yy, z, 0);
            }
        // the walls
        int doorMid = face == 0 || face == 2 ? (x0 + x1) / 2 : (z0 + z1) / 2;
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                bool wx = x == x0 || x == x1, wz = z == z0 || z == z1;
                if (!wx && !wz) continue;
                bool corner = wx && wz;
                int along = wz ? x - x0 : z - z0;                 // along this wall
                bool pilaster = corner || along % 4 == 0;
                bool doorWall = (face == 0 && z == z1) || (face == 2 && z == z0) || (face == 1 && x == x0) || (face == 3 && x == x1);
                int alongDoor = wz ? x : z;
                for (int yy = y + 1; yy <= eave; yy++)
                {
                    int f = (yy - y - 1) / FH, local = (yy - y - 1) % FH;
                    double h = SpHash01(x, yy, z, 331);
                    int id = pilaster ? L.Quoin : local == FH - 1 ? L.Band : h < 0.1 ? L.Cracked : h < 0.16 ? L.Mossy : L.Wall;
                    if (!pilaster && along % 4 == 2 && local >= 1 && local <= (f == floors - 1 ? 3 : 2)) id = L.Glass;
                    if (doorWall && f == 0 && Math.Abs(alongDoor - doorMid) <= 1 && !corner)
                        id = local <= 2 ? 0 : L.Quoin;
                    // the Devastation's breaches
                    if (!pilaster && local >= 1 && local <= 3 && h > 1 - ruin * 0.07) id = 0;
                    else if (id == L.Wall && h > 1 - ruin * 0.3) id = L.Drock != 0 && h > 1 - ruin * 0.12 ? L.Drock : L.Cracked;
                    k.Set(x, yy, z, id);
                }
                // buttresses on the long walls between the windows
                bool longWall = alongX ? wz : wx;
                if (longWall && !corner && along % 4 == 0)
                {
                    int ox = wx ? (x == x0 ? -1 : 1) : 0, oz = wz ? (z == z0 ? -1 : 1) : 0;
                    int top = eave - 2;
                    for (int yy = LTop(L, x + ox, z + oz) - 1; yy <= top; yy++) k.Set(x + ox, yy, z + oz, yy == top ? L.PaveSlab : L.Quoin);
                }
            }
        // the front steps
        {
            int fx = face == 1 ? x0 - 1 : face == 3 ? x1 + 1 : doorMid, fz = face == 0 ? z1 + 1 : face == 2 ? z0 - 1 : doorMid;
            int px = face == 0 || face == 2 ? 1 : 0, pz = 1 - px;
            for (int w = -2; w <= 2; w++) k.Set(fx + px * w, y, fz + pz * w, L.Quoin);
        }
        // the floors, a flight up to each, and the attic floor under the roof
        var keep = new HashSet<long>();
        for (int f = 1; f <= floors; f++)
            for (int x = x0 + 1; x < x1; x++)
                for (int z = z0 + 1; z < z1; z++) k.Set(x, y + f * FH, z, L.Floor);   // the storeys' floors and the attic floor
        for (int f = 0; f < floors; f++)
        {
            int s0 = y + f * FH + 1;   // standing surface of this storey
            int fy = y + f * FH;
            // the flight to the next storey, along the back wall, alternating ends
            if (f < floors - 1)
            {
                bool west = f % 2 == 0;
                int ux = alongX ? (west ? 1 : -1) : 0, uz = alongX ? 0 : (west ? 1 : -1);
                int sx = alongX ? (west ? x0 + 2 : x1 - 2) : (face == 1 ? x1 - 1 : face == 3 ? x0 + 1 : x0 + 1);
                int sz = alongX ? (face == 2 ? z1 - 1 : z0 + 1) : (west ? z0 + 2 : z1 - 2);
                int px = alongX ? 0 : (sx == x0 + 1 ? 1 : -1), pz = alongX ? (sz == z0 + 1 ? 1 : -1) : 0;
                var cells = LairFlight(L, sx, sz, ux, uz, px, pz, s0, FH, 2);
                int upFloor = fy + FH;
                foreach (var c in cells)
                {
                    keep.Add(LairCtx.Col(c.X, c.Z) ^ ((long)f << 56));
                    // open the floor above over the upper steps
                    if (c.S > s0 + FH - 3 && c.S < s0 + FH) k.Set(c.X, upFloor, c.Z, 0);
                }
            }
        }
        LairGable(L, x0, z0, x1, z1, eave, ruin);
        // a chimney or two through the ridge
        {
            int len = alongX ? x1 - x0 : z1 - z0;
            foreach (int at in new[] { len / 4, len * 3 / 4 })
            {
                if (k.R() < 0.4) continue;
                int chx = alongX ? x0 + at : (x0 + x1) / 2, chz = alongX ? (z0 + z1) / 2 : z0 + at;
                int top = eave + (alongX ? z1 - z0 : x1 - x0) / 2 + 3;
                for (int yy = eave; yy <= top; yy++) { k.Set(chx, yy, chz, L.Brick); k.Set(chx + (alongX ? 1 : 0), yy, chz + (alongX ? 0 : 1), L.Brick); }
            }
        }
        // a tower on the outer corner: a storey taller, a pyramid roof
        if (tower)
        {
            int tcx = face == 0 || face == 2 ? (k.R() < 0.5 ? x0 : x1) : (face == 1 ? x1 : x0);
            int tcz = face == 0 ? z0 : face == 2 ? z1 : (k.R() < 0.5 ? z0 : z1);
            int top = eave + FH + 2;
            for (int x = tcx - 3; x <= tcx + 3; x++)
                for (int z = tcz - 3; z <= tcz + 3; z++)
                {
                    bool edge = Math.Abs(x - tcx) == 3 || Math.Abs(z - tcz) == 3;
                    bool cornerT = Math.Abs(x - tcx) == 3 && Math.Abs(z - tcz) == 3;
                    int gy = LTop(L, x, z);
                    for (int yy = Math.Min(gy, y) - 1; yy <= y; yy++) k.Set(x, yy, z, L.Found);
                    for (int yy = y + 1; yy <= top; yy++)
                    {
                        if (!edge) { k.Set(x, yy, z, (yy - y) % FH == 0 ? L.Floor : 0); continue; }
                        int local = (yy - y - 1) % FH;
                        bool inner = x > x0 && x < x1 && z > z0 && z < z1;
                        if (inner && (x == tcx || z == tcz) && local <= 2 && yy <= eave) { k.Set(x, yy, z, 0); continue; }   // a doorway from the hall
                        bool win = !inner && !cornerT && (x == tcx || z == tcz) && local >= 1 && local <= 2 && yy > y + 1;
                        k.Set(x, yy, z, cornerT ? L.Quoin : win ? L.Glass : L.Wall);
                    }
                }
            // a ladder up its inside corner, through every floor
            int lx = tcx + (tcx == x0 ? -2 : 2), lz = tcz + (tcz == z0 ? -2 : 2);
            string side = tcz == z0 ? "north" : "south";
            int lad = k.IdF("ladder-wood-aged-" + side, "ladder-wood-veryaged-" + side, "ladder-stick-" + side);
            for (int yy = y + 1; yy <= top; yy++) if (lad != 0) k.Set(lx, yy, lz, lad);
            LairHip(L, tcx - 3, tcz - 3, tcx + 3, tcz + 3, top + 1);
        }
        // the rooms
        var stories = LairStoreys.TryGetValue(kind, out var st) ? st : LairStoreys["lecture"];
        for (int f = 0; f < floors; f++)
        {
            int s0 = y + f * FH + 1;
            bool Clear(int x, int z) => keep.Contains(LairCtx.Col(x, z) ^ ((long)f << 56)) || keep.Contains(LairCtx.Col(x, z) ^ ((long)(f - 1) << 56));
            LairFurnish(L, x0 + 1, z0 + 1, x1 - 1, z1 - 1, s0, s0 + FH - 2, stories[Math.Min(f, stories.Length - 1)], face, doorMid, f == 0, Clear, ruin,
                f == floors - 1 ? lore : null);
        }
        L.Taken.Add((x0 - 2, z0 - 2, x1 + 2, z1 + 2));
        L.Buildings++;
        L.Storeys += floors;
        L.MaxY = Math.Max(L.MaxY, eave + Math.Min(x1 - x0, z1 - z0) / 2 + (tower ? FH + 12 : 4));
    }

    // One storey's furnishing: wall things round the edge facing in, the
    // kind's centrepieces down the middle, lamps, rubble where it is ruined,
    // a chest in most. A room kind is one of the Great Machine's (GmKinds)
    // or the lair's own (LairKinds); "engine" puts a standing gear in.
    private void LairFurnish(LairCtx L, int x0, int z0, int x1, int z1, int s0, int top, string kind, int face, int doorMid, bool ground, System.Func<int, int, bool> keepClear, double ruin, string lore = null)
    {
        var k = L.K;
        if (!LairKinds.TryGetValue(kind, out var set) && !GmKinds.TryGetValue(kind, out set)) return;
        bool NearDoor(int x, int z)
        {
            if (!ground) return false;
            return face switch
            {
                0 => z >= z1 - 1 && Math.Abs(x - doorMid) <= 2,
                2 => z <= z0 + 1 && Math.Abs(x - doorMid) <= 2,
                1 => x <= x0 + 1 && Math.Abs(z - doorMid) <= 2,
                _ => x >= x1 - 1 && Math.Abs(z - doorMid) <= 2,
            };
        }
        bool Busy(int x, int z) => NearDoor(x, z) || keepClear(x, z) || keepClear(x + 1, z) || keepClear(x - 1, z) || keepClear(x, z + 1) || keepClear(x, z - 1);
        string Pick(string[] a) => a[k.RI(0, a.Length - 1)];
        // round the walls (not the window walls' window cells: things stand anyway)
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                bool edge = x == x0 || x == x1 || z == z0 || z == z1;
                if (!edge || Busy(x, z) || k.R() > set.Fill) continue;
                int dx = x == x0 ? 1 : x == x1 ? -1 : 0, dz = z == z0 ? 1 : z == z1 ? -1 : 0;
                if (dx != 0 && dz != 0) { if (k.R() < 0.5) dx = 0; else dz = 0; }
                LairClut(L, x, s0, z, Pick(set.Wall), Math.Atan2(dz, dx));
            }
        // down the middle
        bool alongX = x1 - x0 >= z1 - z0;
        int mx = (x0 + x1) / 2, mz = (z0 + z1) / 2;
        if (kind == "engine")
        {
            // a standing gear the height of the room, turning nothing now
            double gr = Math.Min(top - s0, (alongX ? z1 - z0 : x1 - x0) / 2.0) - 0.5;
            if (gr >= 2) k.Gear(mx, s0 + gr + 0.5, mz, alongX ? 'x' : 'z', gr, 1, 6, k.R(), () => k.R() < 0.6 ? L.Iron : L.Brass,
                (x, yy, z) => x < x0 || x > x1 || z < z0 || z > z1 || yy < s0 || yy > top);
            L.Machines++;
        }
        else
        {
            int n = alongX ? (x1 - x0 - 2) / 3 : (z1 - z0 - 2) / 3;
            for (int i = 0; i < Math.Max(1, n); i++)
            {
                int x = alongX ? x0 + 2 + i * 3 : mx, z = alongX ? mz : z0 + 2 + i * 3;
                if (x > x1 - 1 || z > z1 - 1 || Busy(x, z)) continue;
                LairClut(L, x, s0, z, Pick(set.Mid), alongX ? Math.PI / 2 : 0);
                if (kind == "mess" || kind == "lecture" || kind == "library" || kind == "drafting" || kind == "office")
                {
                    if (alongX) { if (!Busy(x, z - 1)) LairClut(L, x, s0, z - 1, "chair-aged", Math.PI / 2); if (!Busy(x, z + 1)) LairClut(L, x, s0, z + 1, "chair-aged", -Math.PI / 2); }
                    else { if (!Busy(x - 1, z)) LairClut(L, x - 1, s0, z, "chair-aged", 0); if (!Busy(x + 1, z)) LairClut(L, x + 1, s0, z, "chair-aged", Math.PI); }
                }
            }
        }
        // lamps under the ceiling, every eight blocks down the room
        int len = alongX ? x1 - x0 : z1 - z0;
        for (int i = 3; i < len; i += 8)
        {
            int lx = alongX ? x0 + i : mx, lz = alongX ? mz : z0 + i;
            if (!keepClear(lx, lz)) LairLamp(L, lx, top, lz, "gas-ceiling1");
        }
        // ruin: rubble, a growth come in through the windows
        for (int i = 0; i < (int)(ruin * 6); i++)
        {
            int x = k.RI(x0, x1), z = k.RI(z0, z1);
            if (Busy(x, z)) continue;
            if (k.R() < 0.5 && L.DevPlants.Length > 0) k.Set(x, s0, z, L.DevPlants[k.RI(0, L.DevPlants.Length - 1)]);
            else LairClut(L, x, s0, z, "rubble-stone" + k.RI(1, 4), 0);
        }
        // a find against a wall in one storey in five (and a book where one is meant to be)
        if (lore != null || k.R() < 0.2)
            for (int t = 0; t < 30; t++)
            {
                int x = k.RI(x0, x1), z = k.RI(z0, z1);
                if (!(x == x0 || x == x1 || z == z0 || z == z1) || Busy(x, z)) continue;
                var items = RollTable("alchemist", k.Rand, k.RI(4, 6));
                if (lore != null) items.Add(lore);
                k.Job.StructChests.Add((new BlockPos(x, s0, z, k.Job.Dim), "normal-aged", items, lore != null ? "the library's register" : "the college's stores"));
                L.Chests++;
                break;
            }
    }

    // ── the quads ─────────────────────────────────────────────────────────
    private const int LairQuadStep = 72, LairQuadHalf = 28;

    private void LairQuads(LairCtx L)
    {
        var k = L.K;
        // the hall kinds, dealt out round the campus (the great quad gets the library)
        var deck = new List<string>();
        foreach (var (kind, n) in new[] { ("library", 4), ("laboratory", 9), ("lecture", 8), ("dormitory", 6), ("refectory", 4), ("workshop", 6),
            ("engine", 6), ("museum", 4), ("observatory", 2), ("chapel", 2), ("infirmary", 3) })
            for (int i = 0; i < n; i++) deck.Add(kind);
        deck = deck.OrderBy(_ => k.R()).ToList();
        int di = 0;
        string Deal() => deck[di++ % deck.Count];
        string[] courts = { "orrery", "engine", "armillary", "sundial", "orrery", "engine", "armillary", "sundial" };
        int ci = k.RI(0, courts.Length - 1);
        for (int j = -1; j <= 1; j++)
            for (int i = -1; i <= 1; i++)
            {
                int qx = L.Cx + i * LairQuadStep, qz = L.Cz + j * LairQuadStep, Q = LairQuadHalf;
                var ys = new List<int>();
                bool bad = false;
                for (int x = qx - Q - 2; x <= qx + Q + 2 && !bad; x += 3)
                    for (int z = qz - Q - 2; z <= qz + Q + 2 && !bad; z += 3)
                    {
                        int gy = k.Ground(x, z);
                        if (gy < k.Sea + 3) bad = true;
                        ys.Add(gy);
                    }
                if (bad) continue;
                ys.Sort();
                if (ys[ys.Count - 1] - ys[0] > 16) continue;
                int y = ys[ys.Count / 2];
                double dev = LairDev(L, qx, qz);
                double ruin = Math.Clamp((dev - 0.3) * 1.5, 0, 1);
                bool great = i == 0 && j == 0;
                LairLevel(L, qx - Q - 2, qz - Q - 2, qx + Q + 2, qz + Q + 2, y, 10, L.Pave);
                // paving, the courtyard lawn and its paths
                for (int x = qx - Q - 2; x <= qx + Q + 2; x++)
                    for (int z = qz - Q - 2; z <= qz + Q + 2; z++)
                    {
                        bool lawn = Math.Abs(x - qx) <= 14 && Math.Abs(z - qz) <= 14;
                        bool path = Math.Abs(x - qx) <= 1 || Math.Abs(z - qz) <= 1;
                        double h = SpHash01(x, z, 5, 341);
                        k.Set(x, y, z, lawn && !path ? L.Grass : h < 0.12 ? L.Cobble : h < 0.18 ? L.MossCobble : L.Pave);
                    }
                // the halls: two on the north side, two on the south (the gate
                // between them), one each side
                int fN = great ? 4 : k.RI(3, 4), fS = k.RI(2, 3), fW = k.RI(2, 3), fE = k.RI(2, 3);
                LairHall(L, qx - Q, qz - Q, qx - 4, qz - 16, y, fN, great ? "library" : Deal(), 0, ruin, true, great ? "randomlore:rfpool-lab" : null);
                LairHall(L, qx + 4, qz - Q, qx + Q, qz - 16, y, fN, great ? "library" : Deal(), 0, ruin, true);
                LairHall(L, qx - Q, qz + 16, qx - 4, qz + Q, y, fS, Deal(), 2, ruin, k.R() < 0.3);
                LairHall(L, qx + 4, qz + 16, qx + Q, qz + Q, y, fS, Deal(), 2, ruin, k.R() < 0.3);
                LairHall(L, qx - Q, qz - 12, qx - 16, qz + 12, y, fW, Deal(), 3, ruin, false);
                LairHall(L, qx + 16, qz - 12, qx + Q, qz + 12, y, fE, Deal(), 1, ruin, false);
                string court = great ? "clocktower" : dev > 0.82 ? "blight" : courts[ci++ % courts.Length];
                LairCourt(L, qx, qz, y, court, ruin);
                L.Quads++;
            }
    }

    // ── the courtyards ────────────────────────────────────────────────────
    private void LairCourt(LairCtx L, int qx, int qz, int y, string court, double ruin)
    {
        var k = L.K;
        L.Courts++;
        // flower beds in the corners of the lawn, lamps at the path ends
        foreach (var (sx, sz) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
        {
            for (int i = 0; i < 6 && L.Flowers.Length > 0; i++)
                k.Set(qx + sx * k.RI(9, 13), y + 1, qz + sz * k.RI(9, 13), L.Flowers[k.RI(0, L.Flowers.Length - 1)]);
            LairPost(L, qx + sx * 3, y, qz + sz * 14);
        }
        switch (court)
        {
            case "clocktower": LairClockTower(L, qx, qz, y); break;
            case "orrery": LairOrrery(L, qx, qz, y); break;
            case "engine": LairEngineCourt(L, qx, qz, y); break;
            case "armillary": LairArmillary(L, qx, qz, y); break;
            case "sundial":
            {
                // a gnomon on a round stone dial, benches round it
                for (int dx = -4; dx <= 4; dx++)
                    for (int dz = -4; dz <= 4; dz++)
                        if (dx * dx + dz * dz <= 18) k.Set(qx + dx, y, qz + dz, dx * dx + dz * dz >= 12 ? L.Quoin : L.Band);
                for (int i = 0; i < 4; i++) for (int j = 0; j <= 3 - i; j++) k.Set(qx, y + 1 + j, qz - 2 + i, L.Brass);
                foreach (var (bx, bz, h) in new[] { (-6, 0, 0.0), (6, 0, Math.PI), (0, -6, Math.PI / 2), (0, 6, -Math.PI / 2) })
                    LairClut(L, qx + bx, y + 1, qz + bz, "chair-long", h);
                LairClut(L, qx + 9, y + 1, qz - 9, "statue01", Math.PI * 0.75);
                break;
            }
            case "blight":
            {
                // the Devastation's own court: a crater of drock, shards, a
                // broken orrery lying in it
                for (int dx = -13; dx <= 13; dx++)
                    for (int dz = -13; dz <= 13; dz++)
                    {
                        double d = Math.Sqrt(dx * dx + dz * dz);
                        if (d > 13) continue;
                        int depth = (int)Math.Round((1 - d / 13) * 4);
                        for (int yy = y - depth; yy <= y; yy++) k.Set(qx + dx, yy, qz + dz, yy == y - depth ? L.Drock : 0);
                        if (L.DevPlants.Length > 0 && k.R() < 0.18) k.Set(qx + dx, y - depth + 1, qz + dz, L.DevPlants[k.RI(0, L.DevPlants.Length - 1)]);
                    }
                k.Gear(qx - 2, y - 3, qz + 1, 'y', 5, 1, 6, k.R(), () => L.Iron);
                k.Gear(qx + 5, y - 2, qz - 3, 'y', 3, 1, 5, k.R(), () => L.Brass);
                break;
            }
        }
    }

    // A lamp post: an aged log three high with a lumi lamp on top.
    private void LairPost(LairCtx L, int x, int y, int z)
    {
        var k = L.K;
        for (int yy = y + 1; yy <= y + 3; yy++) k.Set(x, yy, z, L.BeamUD);
        LairLamp(L, x, y + 4, z, "lumi-floor" + (k.R() < 0.5 ? "" : "2"));
    }

    // The great clock tower in the middle of the great quad: four faces of
    // brass wheels, an open belfry, a slate spire.
    private void LairClockTower(LairCtx L, int x, int z, int y)
    {
        var k = L.K;
        int top = y + 42;
        for (int dx = -4; dx <= 4; dx++)
            for (int dz = -4; dz <= 4; dz++)
            {
                bool edge = Math.Abs(dx) == 4 || Math.Abs(dz) == 4, corner = Math.Abs(dx) == 4 && Math.Abs(dz) == 4;
                for (int yy = y; yy <= top; yy++)
                {
                    if (!edge) { k.Set(x + dx, yy, z + dz, (yy - y) % 8 == 0 ? L.Floor : 0); continue; }
                    bool belfry = !corner && yy > top - 6 && yy < top - 1 && Math.Abs(Math.Abs(dz) == 4 ? dx : dz) <= 1;
                    bool door = dz == 4 && Math.Abs(dx) <= 1 && yy <= y + 3 && yy > y;
                    bool slit = !corner && (dx == 0 || dz == 0) && (yy - y) % 8 == 4;
                    k.Set(x + dx, yy, z + dz, door || belfry ? 0 : corner ? L.Quoin : slit ? L.Glass : (yy - y) % 8 == 0 ? L.Band : L.Wall);
                }
            }
        // the clock faces
        int fy = top - 12;
        foreach (var (ox, oz, axis) in new[] { (0, -5, 'z'), (0, 5, 'z'), (-5, 0, 'x'), (5, 0, 'x') })
        {
            k.Gear(x + ox, fy, z + oz, axis, 3.6, 1, 12, k.R(), () => L.Brass);
            k.Set(x + ox, fy, z + oz, L.Gold);
        }
        // the bell
        LairClut(L, x, top - 3, z, "bell-head", 0);
        int lad = k.IdF("ladder-wood-aged-north", "ladder-stick-north");
        for (int yy = y + 1; yy <= top - 1; yy++) if (lad != 0) k.Set(x, yy, z - 3, lad);
        LairHip(L, x - 4, z - 4, x + 4, z + 4, top + 1);
        LairLamp(L, x, y + 6, z, "gas-ceiling1");
        L.Machines++;
    }

    // An orrery in a fountain: a round basin, a golden sun on a column,
    // tilted brass rings with their worlds on them.
    private void LairOrrery(LairCtx L, int x, int z, int y)
    {
        var k = L.K;
        for (int dx = -7; dx <= 7; dx++)
            for (int dz = -7; dz <= 7; dz++)
            {
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d > 7.4) continue;
                if (d > 6.2) { k.Set(x + dx, y, z + dz, L.Quoin); k.Set(x + dx, y + 1, z + dz, L.PaveSlab); continue; }
                k.Set(x + dx, y - 1, z + dz, L.Band);
                k.SetFluid(x + dx, y, z + dz, L.Water);
            }
        for (int yy = y; yy <= y + 4; yy++) k.Set(x, yy, z, L.Quoin);
        double sy = y + 7;
        k.Ball(x, sy, z, 1.4, () => L.Gold);
        double[][] rings = { new[] { 3.5, 0.2, 1, 0.1 }, new[] { 5.0, -0.3, 1, 0.25 }, new[] { 6.4, 0.1, 1, -0.35 } };
        foreach (var r in rings)
        {
            LairRing(L, x, sy, z, r[0], r[1], r[2], r[3], L.Brass);
            double a = k.R() * Math.PI * 2;
            k.Ball(x + Math.Cos(a) * r[0], sy, z + Math.Sin(a) * r[0], 0.7, () => k.R() < 0.5 ? L.Copper : L.Glass);
        }
        L.Machines++;
    }

    // A walk-in engine: two standing gears in mesh on an iron frame, a
    // boiler behind them, its chimney, gauges and valves on stands.
    private void LairEngineCourt(LairCtx L, int x, int z, int y)
    {
        var k = L.K;
        for (int dx = -11; dx <= 11; dx++)
            for (int dz = -3; dz <= 3; dz++) k.Set(x + dx, y, z + dz, L.ObsBrick);
        k.Gear(x - 4, y + 7.5, z, 'z', 6.2, 2, 6, 0.1, () => k.R() < 0.7 ? L.Iron : L.Brass);
        k.Gear(x + 6, y + 6.5, z, 'z', 4.6, 2, 5, 0.4, () => L.Brass);
        // the frame: posts and a beam over both
        foreach (int dx in new[] { -11, -1, 1, 11 })
            for (int yy = y + 1; yy <= y + 15; yy++) { k.Set(x + dx, yy, z - 2, L.Iron); k.Set(x + dx, yy, z + 2, L.Iron); }
        for (int dx = -11; dx <= 11; dx++) { k.Set(x + dx, y + 15, z - 2, L.Iron); k.Set(x + dx, y + 15, z + 2, L.Iron); }
        // the boiler and its chimney
        k.Tube(x - 8, y + 3, z + 7, x + 8, y + 3, z + 7, 2.6, 1, () => L.Iron, false, 4);
        for (int yy = y + 1; yy <= y + 20; yy++)
            for (int dx = 0; dx <= 1; dx++) for (int dz = 0; dz <= 1; dz++) k.Set(x + 9 + dx, yy, z + 7 + dz, L.Brick);
        foreach (var (t, dx) in new[] { ("!gauge2", -6), ("!valve1-aged", -3), ("!lever3-aged", 0), ("!switch2-aged", 3), ("!gauge4", 6) })
        {
            k.Set(x + dx, y + 1, z - 5, L.Iron);
            LairClut(L, x + dx, y + 2, z - 5, t, -Math.PI / 2);
        }
        L.Machines++;
    }

    // An armillary sphere: three great rings of brass round a polar rod, on
    // a stepped plinth.
    private void LairArmillary(LairCtx L, int x, int z, int y)
    {
        var k = L.K;
        for (int s = 0; s < 3; s++)
            for (int dx = -4 + s; dx <= 4 - s; dx++)
                for (int dz = -4 + s; dz <= 4 - s; dz++) k.Set(x + dx, y + 1 + s, z + dz, s == 2 ? L.Quoin : L.Band);
        double cy = y + 11;
        LairRing(L, x, cy, z, 6.5, 0, 1, 0, L.Brass);                 // the horizon
        LairRing(L, x, cy, z, 6.5, 1, 0, 0, L.Copper);                // the meridian
        LairRing(L, x, cy, z, 6.0, 0.35, 0.94, 0.2, L.Gold);          // the ecliptic
        LairRing(L, x, cy, z, 5.2, 0, 0.4, 0.92, L.Brass);
        k.Line(x, cy - 7, z - 3, x, cy + 7, z + 3, () => L.Iron);
        k.Ball(x, cy, z, 1.2, () => L.Gold);
        for (int yy = y + 4; yy < cy - 6; yy++) k.Set(x, yy, z, L.Iron);
        L.Machines++;
    }

    // ── streets, the college gate and the landing ─────────────────────────
    private void LairStreets(LairCtx L)
    {
        var k = L.K;
        int span = LairQuadStep + LairQuadHalf + 6;
        // pave every column of the campus the quads left alone, along the ground
        for (int x = L.Cx - span; x <= L.Cx + span; x++)
            for (int z = L.Cz - span; z <= L.Cz + span; z++)
            {
                if (L.Tops.ContainsKey(LairCtx.Col(x, z))) continue;
                int gy = LTop(L, x, z);
                if (gy < k.Sea + 2 || Math.Abs(gy - L.T0) > 10) continue;
                double h = SpHash01(x, z, 7, 353);
                k.Set(x, gy, z, h < 0.15 ? L.Cobble : h < 0.22 ? L.MossCobble : L.Pave);
            }
        // lamps along the streets between the quads
        for (int s = -1; s <= 1; s += 2)
            for (int t = -span + 6; t <= span - 6; t += 14)
            {
                int ax = L.Cx + s * (LairQuadStep / 2), az = L.Cz + t;
                if (L.Free(ax, az, ax, az)) LairPost(L, ax, LTop(L, ax, az), az);
                int bx = L.Cx + t, bz = L.Cz + s * (LairQuadStep / 2);
                if (L.Free(bx, bz, bx, bz)) LairPost(L, bx, LTop(L, bx, bz), bz);
            }
    }

    // The college gate on the south edge of the campus and the stair from it
    // down to the landing quay.
    private void LairGate(LairCtx L)
    {
        var k = L.K;
        int x = L.Cx, z = L.Cz + LairQuadStep + LairQuadHalf + 4;
        while (z < L.Cz + L.R && k.Ground(x, z) >= L.T0 - 4) z++;
        int edgeZ = z - 1, gy = LTop(L, x, edgeZ - 3);
        // the gatehouse: two towers, an arch between them
        foreach (int s in new[] { -1, 1 })
        {
            int tx = x + s * 7;
            for (int dx = -3; dx <= 3; dx++)
                for (int dz = -3; dz <= 3; dz++)
                {
                    bool edge = Math.Abs(dx) == 3 || Math.Abs(dz) == 3, corner = Math.Abs(dx) == 3 && Math.Abs(dz) == 3;
                    int g0 = LTop(L, tx + dx, edgeZ - 4 + dz);
                    for (int yy = Math.Min(g0, gy) - 1; yy <= gy; yy++) k.Set(tx + dx, yy, edgeZ - 4 + dz, L.Found);
                    for (int yy = gy + 1; yy <= gy + 16; yy++)
                    {
                        int id = !edge ? ((yy - gy) % 5 == 0 ? L.Floor : 0) : corner ? L.Quoin : (yy - gy) % 5 == 3 && (dx == 0 || dz == 0) ? L.Glass : L.Wall;
                        k.Set(tx + dx, yy, edgeZ - 4 + dz, id);
                    }
                    if (edge && (dx + dz) % 2 == 0) k.Set(tx + dx, gy + 17, edgeZ - 4 + dz, L.Wall);   // crenels
                }
            LairLamp(L, tx, gy + 4, edgeZ, "gas-wall1", Math.PI / 2);
        }
        for (int dx = -4; dx <= 4; dx++)
            for (int dz = -2; dz <= 2; dz++)
            {
                for (int yy = gy + 1; yy <= gy + 7; yy++) k.Set(x + dx, yy, edgeZ - 4 + dz, Math.Abs(dx) <= 3 && yy <= gy + 6 - (Math.Abs(dx) == 3 ? 1 : 0) ? 0 : L.Wall);
                k.Set(x + dx, gy + 8, edgeZ - 4 + dz, L.Band);
                k.Set(x + dx, gy, edgeZ - 4 + dz, L.Pave);
            }
        // the stair down to the beach, a half block a step
        var rows = new List<(int Z, double S)>();
        for (int i = 0; i < 200; i++)
        {
            double s = gy + 1 - 0.5 * i;
            int zz = edgeZ + i;
            rows.Add((zz, s));
            if (s <= k.Ground(x, zz) + 1.01 || s <= k.Sea + 1.5) break;
        }
        FellTrees(k.Set, k.Job.Dim, x - 6, edgeZ - 1, x + 6, rows[rows.Count - 1].Z + 2, k.Sea, gy + 20);
        foreach (var (zz, s) in rows)
        {
            bool half = s % 1 != 0;
            int ys = half ? (int)Math.Floor(s) : (int)s - 1;
            for (int dx = -4; dx <= 4; dx++)
            {
                int g0 = LTop(L, x + dx, zz);
                bool rail = Math.Abs(dx) == 4;
                int top = rail ? (int)Math.Floor(s) : ys;
                for (int yy = Math.Min(g0, top) - 1; yy < top; yy++) k.Set(x + dx, yy, zz, L.Found);
                k.Set(x + dx, top, zz, rail ? L.Quoin : half ? L.PaveSlab : L.Pave);
                for (int yy = top + 1; yy <= top + 4; yy++) k.Set(x + dx, yy, zz, 0);
                L.Tops[LairCtx.Col(x + dx, zz)] = top;
            }
        }
        for (int i = 6; i < rows.Count; i += 12) LairPost(L, x + 5, (int)Math.Floor(rows[i].S) - 1, rows[i].Z);
        // the quay: a stone pier out into the sea, a boat at it
        int foot = rows[rows.Count - 1].Z;
        int shore = foot;
        while (shore < foot + 60 && k.Ground(x, shore) >= k.Sea) shore++;
        for (int zz = foot; zz <= shore + 24; zz++)
            for (int dx = -3; dx <= 3; dx++)
            {
                int g0 = k.Ground(x + dx, zz);
                for (int yy = Math.Min(g0, k.Sea) + 1; yy <= k.Sea + 1; yy++) k.Set(x + dx, yy, zz, Math.Abs(dx) == 3 ? L.Quoin : L.Found);
                k.Set(x + dx, k.Sea + 2, zz, Math.Abs(dx) == 3 && (zz - shore) % 6 == 0 ? L.BeamUD : 0);
            }
        LairClut(L, x + 5, k.Sea + 1, shore + 14, "boat-trader", 0);
        L.Taken.Add((x - 12, edgeZ - 9, x + 12, shore + 24));
    }

    // ── the observatory ───────────────────────────────────────────────────
    private void LairObservatory(LairCtx L)
    {
        var k = L.K;
        // the top of the hill: the highest median ground near its middle
        int hx = L.HillX, hz = L.HillZ, best = int.MinValue;
        for (int dx = -24; dx <= 24; dx += 6)
            for (int dz = -24; dz <= 24; dz += 6)
            {
                int g = k.Ground(L.HillX + dx, L.HillZ + dz);
                if (g > best) { best = g; hx = L.HillX + dx; hz = L.HillZ + dz; }
            }
        var hs = new List<int>();
        for (int dx = -9; dx <= 9; dx += 3) for (int dz = -9; dz <= 9; dz += 3) hs.Add(k.Ground(hx + dx, hz + dz));
        hs.Sort();
        int ht = hs[hs.Count / 2];
        LairLevel(L, hx - 13, hz - 13, hx + 13, hz + 13, ht, 6, L.Pave);
        const int Ro = 8, storeys = 3, FH = 5;
        int top = ht + storeys * FH;
        // the tower
        for (int dx = -Ro - 1; dx <= Ro + 1; dx++)
            for (int dz = -Ro - 1; dz <= Ro + 1; dz++)
            {
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d > Ro + 0.5) continue;
                int x = hx + dx, z = hz + dz;
                bool wall = d > Ro - 0.6;
                double a = Math.Atan2(dz, dx);
                for (int yy = ht; yy <= top; yy++)
                {
                    int local = (yy - ht) % FH;
                    if (!wall) { k.Set(x, yy, z, local == 0 ? L.Floor : 0); continue; }
                    bool win = local >= 2 && local <= 3 && Math.Abs(Math.Sin(a * 3)) < 0.18;
                    bool door = dz > 0 && Math.Abs(dx) <= 1 && yy > ht && yy <= ht + 3;
                    k.Set(x, yy, z, door ? 0 : yy == ht ? L.Found : local == 0 ? L.Band : win ? L.Glass : Math.Abs(Math.Sin(a * 4)) < 0.1 ? L.Quoin : L.Wall);
                }
            }
        // the dome: copper on brass ribs, split from rim to crown toward the south
        int domeY = top;
        for (int dx = -Ro - 1; dx <= Ro + 1; dx++)
            for (int dz = -Ro - 1; dz <= Ro + 1; dz++)
                for (int dy = 0; dy <= Ro + 1; dy++)
                {
                    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d > Ro + 0.5 || d < Ro - 0.6) { if (d < Ro - 0.6 && dy > 0) k.Set(hx + dx, domeY + dy, hz + dz, 0); continue; }
                    if (Math.Abs(dx) <= 2 && dz > 0) { k.Set(hx + dx, domeY + dy, hz + dz, 0); continue; }   // the slit
                    double a = Math.Atan2(dz, dx);
                    bool rib = Math.Abs(Math.Sin(a * 4)) < 0.12;
                    k.Set(hx + dx, domeY + dy, hz + dz, rib ? L.Brass : L.Copper);
                }
        // the great telescope on its pier, aimed out through the slit
        for (int yy = domeY + 1; yy <= domeY + 5; yy++) k.Set(hx, yy, hz, L.Quoin);
        k.Tube(hx, domeY + 3, hz - 5, hx, domeY + 11, hz + 7, 1.1, 0.7, () => L.Brass, true, 3);
        k.Tube(hx, domeY + 2, hz - 7, hx, domeY + 3.5, hz - 5, 0.6, 0.6, () => L.Iron, false);
        // a ladder up the north wall through every floor
        int lad = k.IdF("ladder-wood-aged-north", "ladder-stick-north");
        for (int yy = ht + 1; yy <= top; yy++) { if (lad != 0) k.Set(hx, yy, hz - Ro + 1, lad); }
        // inside: the orrery on the ground floor, the charts and books above
        k.Gear(hx, ht + 1, hz, 'y', 3.2, 1, 6, k.R(), () => L.Brass);
        k.Ball(hx, ht + 3, hz, 0.9, () => L.Gold);
        LairRing(L, hx, ht + 3, hz, 2.4, 0.15, 1, 0.1, L.Copper);
        string[] charts = { "armillary1", "armillary2", "armillary3", "astrolabe", "globe1", "globe3", "globe5", "alidade", "equi-dial", "diptych-dial",
            "drafting-table-stuff", "shelf-drafting-instrument", "bookshelves/scrollrack-full1", "@bookshelf-fancy2", "pile-drafting-instrument" };
        for (int s = 0; s < storeys; s++)
        {
            int sy = ht + s * FH + 1;
            for (int i = 0; i < 14; i++)
            {
                double a = i * Math.PI * 2 / 14;
                int x = hx + (int)Math.Round(Math.Cos(a) * (Ro - 2)), z = hz + (int)Math.Round(Math.Sin(a) * (Ro - 2));
                if (Math.Abs(x - hx) <= 1 && z > hz) continue;                     // the door
                if (Math.Abs(x - hx) <= 1 && z < hz - Ro + 3) continue;            // the ladder
                if (k.R() < 0.75) LairClut(L, x, sy, z, charts[k.RI(0, charts.Length - 1)], a + Math.PI);
            }
            LairLamp(L, hx + 3, sy + 3, hz + 3, "gas-ceiling1");
            LairLamp(L, hx - 3, sy + 3, hz - 3, "gas-ceiling1");
        }
        // Jonas' quarrel with the young alchemist, folded away up here, and
        // the Clockmakers' own try at a lens (The Flux)
        k.Job.StructChests.Add((new BlockPos(hx - 5, ht + FH + 1, hz + 2, k.Job.Dim), "normal-aged",
            new List<string> { "lore:alchemist_scroll", "journal:falx:3:game:lore-book-aged-purpleorange" }.Concat(RollTable("alchemist", k.Rand, 6)).ToList(), "the observatory's cabinet"));
        L.Chests++;
        L.Machines += 2;
        L.MaxY = Math.Max(L.MaxY, domeY + Ro + 2);
        // the stair down the hillside to the campus
        var rows = new List<(int Z, double S)>();
        for (int i = 0; i < 200; i++)
        {
            double s = ht + 1 - 0.5 * i;
            int zz = hz + Ro + 2 + i;
            rows.Add((zz, s));
            if (s <= k.Ground(hx, zz) + 1.01) break;
        }
        FellTrees(k.Set, k.Job.Dim, hx - 5, hz + Ro, hx + 5, rows[rows.Count - 1].Z + 2, ht - 60, ht + 20);
        foreach (var (zz, s) in rows)
        {
            bool half = s % 1 != 0;
            int ys = half ? (int)Math.Floor(s) : (int)s - 1;
            for (int dx = -3; dx <= 3; dx++)
            {
                int g0 = LTop(L, hx + dx, zz);
                bool rail = Math.Abs(dx) == 3;
                int t = rail ? (int)Math.Floor(s) : ys;
                for (int yy = Math.Min(g0, t) - 1; yy < t; yy++) k.Set(hx + dx, yy, zz, L.Found);
                k.Set(hx + dx, t, zz, rail ? L.Quoin : half ? L.PaveSlab : L.Pave);
                for (int yy = t + 1; yy <= t + 4; yy++) k.Set(hx + dx, yy, zz, 0);
                L.Tops[LairCtx.Col(hx + dx, zz)] = t;
            }
        }
        for (int i = 8; i < rows.Count; i += 14) LairPost(L, hx + 4, (int)Math.Floor(rows[i].S) - 1, rows[i].Z);
        L.Taken.Add((hx - 14, hz - 14, hx + 14, rows[rows.Count - 1].Z + 2));
    }

    // ── the gateway ───────────────────────────────────────────────────────
    private void LairGateway(LairCtx L)
    {
        var k = L.K;
        int gx = L.BowlX, gz = L.BowlZ;
        var hs = new List<int>();
        for (int dx = -12; dx <= 12; dx += 3) for (int dz = -12; dz <= 12; dz += 3) hs.Add(k.Ground(gx + dx, gz + dz));
        hs.Sort();
        int gy = Math.Max(k.Sea + 2, hs[hs.Count / 2]);
        FellTrees(k.Set, k.Job.Dim, gx - 30, gz - 30, gx + 30, gz + 30, gy - 4, gy + 50);
        // the dais: three steps of basalt brick, the top of polished obsidian
        for (int dx = -17; dx <= 17; dx++)
            for (int dz = -17; dz <= 17; dz++)
            {
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d > 16.5) continue;
                int step = d <= 8.5 ? 3 : d <= 12.5 ? 2 : 1;
                int g0 = k.Ground(gx + dx, gz + dz);
                for (int yy = Math.Min(g0, gy) - 1; yy <= gy + step; yy++)
                    k.Set(gx + dx, yy, gz + dz, yy == gy + step ? (step == 3 ? L.Obsidian : (SpHash01(dx, dz, 1, 401) < 0.2 ? L.Drock : L.ObsBrick)) : L.ObsBrick);
                for (int yy = gy + step + 1; yy <= gy + 34; yy++) k.Set(gx + dx, yy, gz + dz, 0);
            }
        // the ring, standing across the bowl and facing the campus (west)
        const double Ro = 14, Ri = 10;
        double yc = gy + 4 + Ro;
        int ghosts = 0;
        for (int dz = -15; dz <= 15; dz++)
            for (int yy = (int)(yc - 15); yy <= (int)(yc + 15); yy++)
            {
                double r = Math.Sqrt(dz * dz + (yy - yc) * (yy - yc));
                if (r < Ri || r > Ro || yy <= gy + 3) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int id = r > 12.4 ? L.Obsidian : L.Brass;
                    k.Set(gx + dx, yy, gz + dz, id);
                }
            }
        // ghostlights round the inner rim, set in the brass
        if (L.Ghost != 0)
            for (int i = 0; i < 20; i++)
            {
                double a = i * Math.PI * 2 / 20;
                int yy = (int)Math.Round(yc + Math.Sin(a) * (Ri + 0.6)), dz = (int)Math.Round(Math.Cos(a) * (Ri + 0.6));
                if (yy <= gy + 3) continue;
                k.Glow(gx - 1, yy, gz + dz, L.Ghost);
                k.Glow(gx + 1, yy, gz + dz, L.Ghost);
                ghosts += 2;
            }
        // the wound: drock shards growing in from the rim
        for (int i = 0; i < 26; i++)
        {
            double a = k.R() * Math.PI * 2, len = 2 + k.R() * 4;
            double y0 = yc + Math.Sin(a) * Ri, z0 = gz + Math.Cos(a) * Ri;
            if (y0 <= gy + 4) continue;
            k.Line(gx + (k.R() - 0.5) * 2, y0, z0, gx + (k.R() - 0.5) * 3, y0 - Math.Sin(a) * len, z0 - Math.Cos(a) * len, () => L.Drock, 0.6);
        }
        // the coil towers either side, their cables to the crown of the ring
        foreach (int s in new[] { -1, 1 })
        {
            int tz = gz + s * 23, tx = gx;
            int tg = k.Ground(tx, tz);
            int ttop = gy + 22;
            for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                    for (int yy = Math.Min(tg, gy) - 1; yy <= gy + 1; yy++) k.Set(tx + dx, yy, tz + dz, L.ObsBrick);
            for (int yy = gy + 2; yy <= ttop; yy++)
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        k.Set(tx + dx, yy, tz + dz, (yy - gy) % 3 == 0 ? L.Copper : L.Iron);
            for (int yy = gy + 4; yy <= ttop - 2; yy += 3) LairRing(L, tx, yy, tz, 2.2, 0, 1, 0, L.Copper);
            k.Ball(tx, ttop + 2.5, tz, 2.2, () => L.Glass);
            if (L.Ghost != 0) { k.Glow(tx, ttop + 2, tz, L.Ghost); ghosts++; }
            k.Line(tx, ttop + 1, tz, gx, yc + Ro - 1, gz + s * 4, () => L.Iron, 0.5);
            L.Machines++;
        }
        // the control desk west of the dais, facing the ring
        int cx = gx - 24;
        int cg = Math.Max(k.Ground(cx, gz), k.Sea + 1);
        for (int dz = -5; dz <= 5; dz++)
            for (int dx = -3; dx <= 3; dx++)
            {
                for (int yy = Math.Min(k.Ground(cx + dx, gz + dz), cg) - 1; yy <= cg; yy++) k.Set(cx + dx, yy, gz + dz, L.ObsBrick);
                for (int yy = cg + 1; yy <= cg + 4; yy++) k.Set(cx + dx, yy, gz + dz, 0);
            }
        // controls that stand on the desk top (the wall instruments need a wall to be pushed against)
        string[] controls = { "!lever1-aged", "!lever2-aged", "!lever3-aged", "!lever4-aged", "!switch4-aged", "!gauge4", "!valve2-aged" };
        for (int dz = -4; dz <= 4; dz++)
        {
            int dx = 2 - (int)Math.Round(Math.Abs(dz) * 0.4);
            k.Set(cx + dx, cg + 1, gz + dz, L.Brass);
            LairClut(L, cx + dx, cg + 2, gz + dz, controls[k.RI(0, controls.Length - 1)], 0);
        }
        LairClut(L, cx - 1, cg + 1, gz, "chair-aged", 0);
        LairClut(L, cx - 1, cg + 1, gz + 2, "pile-writing", 0);
        k.Job.StructChests.Add((new BlockPos(cx - 2, cg + 1, gz - 3, k.Job.Dim), "normal-aged",
            new List<string>(RollTable("alchemist", k.Rand, 4)) { "randomlore:rfpool-lab", "landmassgenerator:islandmap-random" }, "the gatekeeper's chest"));
        L.Chests++;
        // the road from the campus down to the bowl, and the pipe beside it
        int x0 = L.Cx + LairQuadStep + LairQuadHalf + 4;
        for (int x = x0; x <= cx - 4; x++)
            for (int dz = -3; dz <= 3; dz++)
            {
                int zz = L.BowlZ + dz;
                int g0 = LTop(L, x, zz);
                if (g0 < k.Sea + 1) continue;
                if (Math.Abs(dz) <= 2) k.Set(x, g0, zz, SpHash01(x, zz, 9, 409) < 0.3 ? L.Cobble : L.Pave);
                if (dz == 3 && (x - x0) % 6 == 0) { for (int yy = g0 + 1; yy <= g0 + 3; yy++) k.Set(x, yy, zz, L.Iron); }
                if (dz == 3) k.Set(x, g0 + 4, zz, L.Iron);
                if (dz == -3 && (x - x0) % 16 == 8) LairPost(L, x, g0, zz);
            }
        L.Taken.Add((gx - 30, gz - 30, gx + 30, gz + 30));
        L.MaxY = Math.Max(L.MaxY, (int)(yc + Ro) + 4);
        sapi.Logger.Notification("[landmassgenerator] alchemist lair: the gateway at HUD {0}, {1} (ring centre y {2}, {3} ghostlights)",
            gx - sapi.WorldManager.MapSizeX / 2, gz - sapi.WorldManager.MapSizeZ / 2, (int)yc, ghosts);
    }

    // ── after the commit: the Devastation over all of it ─────────────────
    private void LairAfter(LairCtx L)
    {
        var k = L.K;
        var wa = sapi.World.BlockAccessor;
        var ba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        var p = new BlockPos(0, 0, 0, k.Job.Dim);
        int top = Math.Min(sapi.WorldManager.MapSizeY - 3, L.MaxY + 2);
        int soilN = 0, growN = 0, drockN = 0, plantN = 0;
        Block At(int x, int y, int z) { p.Set(x, y, z); return wa.GetBlock(p); }
        void Put(int x, int y, int z, int id) { p.Set(x, y, z); ba.SetBlock(id, p); }
        for (int x = L.Cx - L.R; x <= L.Cx + L.R; x++)
            for (int z = L.Cz - L.R; z <= L.Cz + L.R; z++)
            {
                double dev = LairDev(L, x, z);
                if (dev < 0.08) continue;
                double r = SpHash01(x, z, 11, 419), r2 = SpHash01(x, z, 12, 421);
                // the top block of the column, buildings included
                int y = top;
                Block b = null;
                for (; y > L.Sea - 2; y--)
                {
                    b = At(x, y, z);
                    if (b != null && b.Id != 0 && !b.IsLiquid() && b.BlockMaterial != EnumBlockMaterial.Plant && b.BlockMaterial != EnumBlockMaterial.Leaves) break;
                }
                if (b == null || b.Id == 0 || b.IsLiquid() || y <= L.Sea - 2) continue;
                var above = At(x, y + 1, z);
                bool airAbove = above == null || above.Id == 0 || above.BlockMaterial == EnumBlockMaterial.Plant;
                var mat = b.BlockMaterial;
                if (mat == EnumBlockMaterial.Soil || mat == EnumBlockMaterial.Gravel || mat == EnumBlockMaterial.Sand)
                {
                    if (r < dev * 0.95 && L.DevSoil.Length > 0)
                    {
                        Put(x, y, z, L.DevSoil[Math.Min(L.DevSoil.Length - 1, (int)(dev * L.DevSoil.Length))]);
                        soilN++;
                    }
                    if (!airAbove) continue;
                    if (r2 < dev * 0.1 && L.DevPlants.Length > 0) { Put(x, y + 1, z, L.DevPlants[(int)(r2 * 1000) % L.DevPlants.Length]); plantN++; }
                    else if (r2 < dev * 0.3 && L.Growth != 0) { Put(x, y + 1, z, L.Growth); growN++; }
                    else if (above != null && above.Id != 0 && dev > 0.45) Put(x, y + 1, z, 0);   // the grass dies
                }
                else if (mat == EnumBlockMaterial.Stone || mat == EnumBlockMaterial.Ceramic || mat == EnumBlockMaterial.Brick)
                {
                    if (b.Id != L.Glass && r < dev * 0.12 && L.Drock != 0) { Put(x, y, z, L.Drock); drockN++; }
                    if (airAbove && r2 < dev * 0.4 && L.Growth != 0) { Put(x, y + 1, z, L.Growth); growN++; }
                    // and down into the masonry below the top, here and there
                    if (dev > 0.55 && r < (dev - 0.55) * 0.25 && L.Drock != 0)
                        for (int yy = y - 1; yy > y - 1 - (int)(r2 * 8); yy--)
                        {
                            var bb = At(x, yy, z);
                            if (bb == null || bb.Id == 0 || bb.BlockMaterial != EnumBlockMaterial.Stone || bb.Id == L.Glass) break;
                            Put(x, yy, z, L.Drock);
                            drockN++;
                        }
                }
            }
        ba.Commit();
        L.Overgrown = soilN + growN + drockN + plantN;
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        sapi.Logger.Notification("[landmassgenerator] alchemist lair at HUD {0}, {1} (campus {2}): {3} quads, {4} halls of {5} storeys, {6} courts, {7} machines, {8} lamps, {9} chests; the observatory hill at HUD {10}, {11}, the gateway bowl at HUD {12}, {13}; the Devastation: {14} devastated soil, {15} growths, {16} drock, {17} plants",
            L.Cx - mx, L.Cz - mz, L.T0, L.Quads, L.Buildings, L.Storeys, L.Courts, L.Machines, L.Lamps, L.Chests,
            L.HillX - mx, L.HillZ - mz, L.BowlX - mx, L.BowlZ - mz, soilN, growN, drockN, plantN);
    }
}
