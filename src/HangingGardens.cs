using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace LandmassGenerator;

// ── THE HANGING GARDENS TEMPLE ─────────────────────────────────────────
// A handful of monks were stranded on this island by the Devastation. With
// nowhere to go they made the place a temple and spent their lives making it
// as beautiful as they could: they cut stairs up inside the spires, hung
// gardens from every ledge, slung rope bridges between the peaks and raised a
// hall and a pagoda on the terrace in the middle. Boats found them, and now
// the island trades: furniture, the produce of their fields, and cloth.
//
// The `gardens` megastructure builds everything above the terrain:
//   - the spires: sheer karst pillars, each with a spiral stair cut up its
//     middle from a door at its foot to a pavilion on its crown, ledges round
//     its sides planted as gardens with vines hanging from them;
//   - rope bridges with bamboo rails between the spires and out from the
//     terrace, each entering a spire through a passage onto its stair;
//   - on the terrace: the main hall (vermilion posts, white plaster, a grey
//     tiled roof), a five-tier pagoda, a pavilion, the monks' quarters, a
//     raked gravel garden and a koi pond;
//   - the approach: a broad stone stair from the lily pond up to the terrace
//     with torii gates and stone lanterns, and a torii standing in the sea
//     off the landing beach with its pier;
//   - three small markets in the lowland, each with its trader: furniture,
//     agriculture, clothing;
//   - bamboo groves, gravel paths, and the green creeping over all of it.
// radius= (or size=, which stops at 160) is how far it spreads; spires= how many.
public partial class LandmassGeneratorModSystem
{
    private sealed class GardenSpire
    {
        public int X, Z, Base, Top, F, K0;
        public double Rb, Rt, Ph1, Ph2;
        public readonly List<(int Y, double A, double Span, double Out)> Ledges = new();
    }

    private sealed class GardensCtx
    {
        public MegaKit K;
        public int Cx, Cz, R, Sea, T0;
        public readonly Dictionary<long, int> Tops = new();
        public readonly List<GardenSpire> Spires = new();
        public readonly List<(int X0, int Z0, int X1, int Z1)> Taken = new();
        public readonly List<(int X, int Y, int Z, string Gen, float Size)> Trees = new();
        public readonly List<(int X, int Y, int Z, string Code)> Traders = new();
        public readonly List<(int X, int Z)> Doors = new();
        public int Grass, Dirt, Stone, Chalk, Brick, BrickSlab, Mossy, Cobble, MossCobble, Gravel, Polished, PolishedSlab, Plaster,
            Red, RoofBlk, RoofSlab, Ridge, RedRoof, RedRoofSlab, Post, PostNS, PostWE, DarkPlank, Floor, FloorSlab, Deck, DeckSlab,
            FenceNS, FenceEW, Lantern, Water, Lily, VineSec, VineEnd, Bamboo1, Bamboo2, Bamboo3, BambooLeaf, Copper, Glass;
        public string VineSecCode, VineEndCode;
        public int[] Flowers, Low;
        public int Buildings, Bridges, Lights, Gardens, Vines, Lanterns, Stalls, Groves;

        public static long Col(int x, int z) => ((long)x << 32) | (uint)z;
        public bool Free(int x0, int z0, int x1, int z1, int pad = 0)
        {
            foreach (var t in Taken)
                if (x1 + pad >= t.X0 && x0 - pad <= t.X1 && z1 + pad >= t.Z0 && z0 - pad <= t.Z1) return false;
            return true;
        }
    }

    private static int GTop(GardensCtx g, int x, int z) => g.Tops.TryGetValue(GardensCtx.Col(x, z), out int t) ? t : g.K.Ground(x, z);

    // Water over the ground here (the lily pond, the sea): the committed
    // world holds exactly the natural and terrain water.
    private bool GWet(GardensCtx g, int x, int z)
    {
        var p = new BlockPos(x, GTop(g, x, z) + 1, z, g.K.Job.Dim);
        var b = sapi.World.BlockAccessor.GetBlock(p, BlockLayersAccess.Fluid);
        return b != null && b.Id != 0;
    }

    private static double AngDist(double a, double b)
    {
        double d = Math.Abs((a - b) % (Math.PI * 2));
        return d > Math.PI ? Math.PI * 2 - d : d;
    }

    private static double SpireR(GardenSpire s, int y, double ang, bool ledges = true)
    {
        if (y > s.Top) return -1;
        double f = Math.Clamp((y - s.Base) / (double)Math.Max(1, s.Top - s.Base), 0, 1);
        double r = s.Rb + (s.Rt - s.Rb) * Math.Pow(f, 0.8);
        r *= 1 + 0.09 * Math.Sin(ang * 3 + s.Ph1 + y * 0.06) + 0.05 * Math.Sin(ang * 7 + s.Ph2 + y * 0.13);
        int dTop = s.Top - y;
        if (dTop < 3) r -= (3 - dTop) * 0.6;            // the crown rounds over
        if (!ledges) return r;
        foreach (var l in s.Ledges)
        {
            if (y > l.Y || y < l.Y - 2) continue;
            double da = AngDist(ang, l.A);
            if (da < l.Span) r += l.Out * (1 - Math.Pow(da / l.Span, 2)) * (y == l.Y - 2 ? 0.5 : 1);
        }
        return r;
    }

    private static bool InSpire(GardensCtx g, int x, int y, int z, GardenSpire except = null)
    {
        foreach (var s in g.Spires)
        {
            if (s == except) continue;
            double dx = x - s.X, dz = z - s.Z;
            if (Math.Abs(dx) > s.Rb + 6 || Math.Abs(dz) > s.Rb + 6) continue;
            if (y < s.Base || y > s.Top) continue;
            if (Math.Sqrt(dx * dx + dz * dz) <= SpireR(s, y, Math.Atan2(dz, dx))) return true;
        }
        return false;
    }

    private void GardensPalette(GardensCtx g)
    {
        var k = g.K;
        int N(params string[] codes)
        {
            foreach (var c in codes) { int i = k.Id(c); if (i != 0) return i; }
            return codes.Length > 0 ? k.Need(codes[0]) : 0;
        }
        g.Grass = N("soil-medium-normal", "soil-low-normal");
        g.Dirt = N("soil-medium-none", "soil-low-none");
        g.Stone = N("rock-limestone", "rock-granite");
        g.Chalk = N("rock-chalk", "rock-limestone");
        g.Brick = N("stonebricks-granite", "stonebricks-andesite");
        g.BrickSlab = N("stonebrickslab-granite-down-free", "cobblestoneslab-granite-down-free");
        g.Mossy = N("mossystonebricks-granite", "stonebricks-granite");
        g.Cobble = N("cobblestone-granite", "cobblestone-andesite");
        g.MossCobble = N("mossycobble-granite", "cobblestone-granite");
        g.Gravel = N("gravel-granite", "gravel-limestone");
        g.Polished = N("rockpolished-granite", "stonebricks-granite");
        g.PolishedSlab = N("polishedrockslab-granite-down-free", "stonebrickslab-granite-down-free");
        g.Plaster = N("plaster-plain", "plaster-square");
        g.Red = N("tile-2x2-plain-red", "claybricks-good-fire");
        g.RoofBlk = N("clayshingleblock-gray", "clayshingleblock-black");
        g.RoofSlab = N("clayshinglelabs-gray-down-free", "clayshinglelabs-black-down-free");
        g.Ridge = N("clayshingleblock-black", "clayshingleblock-gray");
        g.RedRoof = N("clayshingleblock-red", "clayshingleblock-fire");
        g.RedRoofSlab = N("clayshinglelabs-red-down-free", "clayshinglelabs-fire-down-free");
        g.Post = N("log-placed-ebony-ud", "log-placed-aged-ud");
        g.PostNS = N("log-placed-ebony-ns", "log-placed-aged-ns");
        g.PostWE = N("log-placed-ebony-we", "log-placed-aged-we");
        g.DarkPlank = N("planks-ebony-ud", "planks-aged-ud");
        g.Floor = N("planks-larch-ud", "planks-birch-ud");
        g.FloorSlab = N("plankslab-larch-down-free", "plankslab-birch-down-free");
        g.Deck = N("planks-walnut-ud", "planks-oak-ud");
        g.DeckSlab = N("plankslab-walnut-down-free", "plankslab-oak-down-free");
        g.FenceNS = N("fencebamboo-brown-ns-free", "woodenfence-redwood-ns-free");
        g.FenceEW = N("fencebamboo-brown-ew-free", "woodenfence-redwood-ew-free");
        g.Lantern = N("paperlantern-on", "lantern-small-up");
        g.Water = N("water-still-7");
        g.Lily = k.Id("waterlily");
        g.VineSecCode = "wildvine-section-north";
        g.VineEndCode = "wildvine-end-north";
        g.Bamboo1 = N("bamboo-grown-green-segment1");
        g.Bamboo2 = N("bamboo-grown-green-segment2", "bamboo-grown-green-segment1");
        g.Bamboo3 = N("bamboo-grown-green-segment3", "bamboo-grown-green-segment1");
        g.BambooLeaf = N("bambooleaves-green-grown");
        g.Copper = N("metalblock-new-plain-copper", "metalblock-new-plain-brass", "metalblock-new-plain-iron");
        g.Glass = N("glass-plain");
        g.Flowers = new[] { "flower-cornflower-free", "flower-wilddaisy-free", "flower-catmint-free", "flower-lupine-purple-free", "flower-heather-free",
            "flower-orangemallow-free", "flower-forgetmenot-free", "flower-goldenpoppy-free", "flower-lilyofthevalley-free", "flower-woad-free" }
            .Select(c => k.Id(c)).Where(i => i != 0).ToArray();
        g.Low = new[] { "tallgrass-medium-free", "tallgrass-short-free", "fern-free", "tallfern-free", "fruitingbush-wild-raspberry-free", "fruitingbush-wild-blueberry-free" }
            .Select(c => k.Id(c)).Where(i => i != 0).ToArray();
    }

    private void MegaGardens(MegaKit k, int cx, int cz)
    {
        var g = new GardensCtx { K = k, Cx = cx, Cz = cz, R = Math.Max(80, k.OptI("radius", k.Def.Size)), Sea = k.Sea };
        GardensPalette(g);

        // the terrace: its level is the median ground round the middle
        var hs = new List<int>();
        for (int x = cx - 40; x <= cx + 40; x += 3)
            for (int z = cz - 40; z <= cz + 40; z += 3)
                if ((x - cx) * (x - cx) + (z - cz) * (z - cz) <= 1600) hs.Add(k.Ground(x, z));
        hs.Sort();
        g.T0 = hs[hs.Count / 2];
        // how far the terrace reaches each way at that level
        int Edge(double a)
        {
            for (int d = 10; d < g.R; d++)
            {
                int x = cx + (int)Math.Round(Math.Cos(a) * d), z = cz + (int)Math.Round(Math.Sin(a) * d);
                if (k.Ground(x, z) < g.T0 - 3) return d - 1;
            }
            return g.R;
        }

        // the terrace, the approach and the markets claim their ground first;
        // the spires stand where they leave room
        GardensTerrace(g);
        GardensStair(g, Edge(Math.PI / 2));
        GardensMarkets(g);
        GardensSpires(g, k.OptI("spires", 12), Edge);
        GardensBridges(g, Edge);
        GardensHarbor(g);
        GardensPaths(g);
        GardensGroves(g);

        k.AfterCommit.Add(() => GardensAfter(g));
    }

    // ── shared pieces ─────────────────────────────────────────────────────

    // Fell the trees in a box (the forest went in before the structures).
    private void GardensClearTrees(GardensCtx g, int x0, int z0, int x1, int z1, int y0, int y1)
    {
        var wa = sapi.World.BlockAccessor;
        var p = new BlockPos(0, 0, 0, g.K.Job.Dim);
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                {
                    p.Set(x, y, z);
                    var b = wa.GetBlock(p);
                    if (b != null && b.Id != 0 && (b.BlockMaterial == EnumBlockMaterial.Wood || b.BlockMaterial == EnumBlockMaterial.Leaves || b.BlockMaterial == EnumBlockMaterial.Plant))
                        g.K.Set(x, y, z, 0);
                }
    }

    // Cut and fill a rectangle to level y (top block `top`), blended back into
    // the slope over `blend` blocks. Returns false over water.
    private void GardensLevel(GardensCtx g, int x0, int z0, int x1, int z1, int y, int blend, int top)
    {
        var k = g.K;
        GardensClearTrees(g, x0 - blend - 4, z0 - blend - 4, x1 + blend + 4, z1 + blend + 4, Math.Min(y, k.Ground((x0 + x1) / 2, (z0 + z1) / 2)) - 3, y + 36);
        for (int x = x0 - blend; x <= x1 + blend; x++)
            for (int z = z0 - blend; z <= z1 + blend; z++)
            {
                int dOut = Math.Max(Math.Max(x0 - x, x - x1), Math.Max(z0 - z, z - z1));
                int gy = GTop(g, x, z);
                if (dOut > 0 && gy < k.Sea) continue;
                int target = dOut <= 0 ? y : (int)Math.Round(y + (gy - y) * (dOut / (double)(blend + 1)));
                for (int yy = Math.Min(gy, target) - 1; yy < target; yy++) k.Set(x, yy, z, yy < target - 3 ? g.Stone : g.Dirt);
                k.Set(x, target, z, dOut <= 0 ? top : g.Grass);
                for (int yy = target + 1; yy <= Math.Max(gy, target) + 3; yy++) k.Set(x, yy, z, 0);
                g.Tops[GardensCtx.Col(x, z)] = target;
            }
    }

    // A hipped roof over the eave rectangle, its lowest course at y: shallow
    // half steps at the eaves, steeper above, corners turned up, a dark ridge.
    private static void GardensRoof(GardensCtx g, int x0, int z0, int x1, int z1, int y, bool red = false)
    {
        var k = g.K;
        int blk = red ? g.RedRoof : g.RoofBlk, slab = red ? g.RedRoofSlab : g.RoofSlab;
        int lastY = y;
        for (int i = 0; ; i++)
        {
            int ax0 = x0 + i, ax1 = x1 - i, az0 = z0 + i, az1 = z1 - i;
            if (ax0 > ax1 || az0 > az1) break;
            int hy; bool half;
            if (i == 0) { hy = y; half = true; }
            else if (i == 1) { hy = y; half = false; }
            else if (i == 2) { hy = y + 1; half = true; }
            else { hy = y + i - 2; half = false; }
            for (int x = ax0; x <= ax1; x++)
                for (int z = az0; z <= az1; z++)
                {
                    if (x != ax0 && x != ax1 && z != az0 && z != az1) continue;
                    bool corner = (x == ax0 || x == ax1) && (z == az0 || z == az1);
                    if (i == 0 && corner) { k.Set(x, hy, z, blk); k.Set(x, hy + 1, z, slab); continue; }
                    k.Set(x, hy, z, half ? slab : blk);
                }
            lastY = hy;
            if (ax1 - ax0 <= 1 || az1 - az0 <= 1)
            {
                for (int x = ax0; x <= ax1; x++)
                    for (int z = az0; z <= az1; z++) k.Set(x, hy + 1, z, g.Ridge);
                break;
            }
        }
    }

    private static void GardensLantern(GardensCtx g, int x, int y, int z)
    {
        if (g.Lantern == 0) return;
        g.K.Glow(x, y, z, g.Lantern);
        g.Lanterns++;
    }

    // A stone lantern (toro): a stone foot, a post, the lit paper light, a cap.
    private static void GardensToro(GardensCtx g, int x, int y, int z)
    {
        var k = g.K;
        k.Set(x, y, z, k.R() < 0.3 ? g.MossCobble : g.Cobble);
        k.Set(x, y + 1, z, g.Polished);
        GardensLantern(g, x, y + 2, z);
        k.Set(x, y + 3, z, g.BrickSlab);
    }

    // A torii over a path running along z (alongX false) or x: two vermilion
    // pillars, the tie beam, and the black top beam with its ends turned up.
    private static void GardensTorii(GardensCtx g, int x, int y, int z, bool alongX, int half = 3, int h = 7)
    {
        var k = g.K;
        void P(int u, int yy, int id) { if (alongX) k.Set(x, yy, z + u, id); else k.Set(x + u, yy, z, id); }
        for (int s = -1; s <= 1; s += 2)
        {
            P(s * half, y - 1, g.Polished);
            for (int yy = y; yy < y + h; yy++) P(s * half, yy, g.Red);
        }
        for (int u = -half - 1; u <= half + 1; u++) P(u, y + h - 2, g.Red);          // nuki
        for (int u = -half - 1; u <= half + 1; u++) P(u, y + h - 1, g.Red);          // shimaki
        for (int u = -half - 2; u <= half + 2; u++) P(u, y + h, g.Ridge);            // kasagi
        P(-half - 2, y + h + 1, g.RoofSlab); P(half + 2, y + h + 1, g.RoofSlab);
        P(0, y + h - 1, g.DarkPlank);                                                // the name plaque
    }

    // A hanging vine down from (x, y, z), `len` long, stopping at anything solid.
    private void GardensVine(GardensCtx g, int x, int y, int z, int len, string facing)
    {
        var k = g.K;
        int sec = k.Id("wildvine-section-" + facing), end = k.Id("wildvine-end-" + facing);
        if (sec == 0 || end == 0) { k.Need("wildvine-section-" + facing); return; }
        int n = 0;
        for (int i = 0; i < len; i++)
        {
            int yy = y - i;
            if (yy <= GTop(g, x, z) || InSpire(g, x, yy, z)) break;
            n++;
        }
        if (n < 2) return;
        for (int i = 0; i < n; i++) k.Set(x, y - i, z, i == n - 1 ? end : sec);
        g.Vines++;
    }

    // A clump of bamboo round (x, z).
    private static void GardensBamboo(GardensCtx g, int x, int z, int culms, int hMax)
    {
        var k = g.K;
        if (g.Bamboo1 == 0) return;
        for (int c = 0; c < culms; c++)
        {
            int bx = x + k.RI(-3, 3), bz = z + k.RI(-3, 3);
            int y0 = GTop(g, bx, bz);
            if (y0 < k.Sea + 1 || InSpire(g, bx, y0 + 1, bz)) continue;
            int h = k.RI(hMax / 2, hMax);
            for (int i = 0; i < h; i++) k.Set(bx, y0 + 1 + i, bz, i % 3 == 0 ? g.Bamboo1 : i % 3 == 1 ? g.Bamboo2 : g.Bamboo3);
            if (g.BambooLeaf != 0)
                for (int i = h - 3; i <= h; i++)
                {
                    k.Set(bx, y0 + 1 + i, bz, i == h ? g.BambooLeaf : k.R() < 0.5 ? g.BambooLeaf : g.Bamboo2);
                    if (i >= h - 1)
                        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                            if (k.R() < 0.6) k.Set(bx + dx, y0 + 1 + i, bz + dz, g.BambooLeaf);
                }
        }
        g.Groves++;
    }

    // A building on a stone podium: timber frame (vermilion posts for the
    // temple, dark ones for the rest), white plaster, a plank skirting, a
    // door in the middle of the front (south, +z) and windows, a roof.
    private void GardensHall(GardensCtx g, int x0, int z0, int x1, int z1, int floorY, int wallH, bool temple, string furnish)
    {
        var k = g.K;
        int post = temple ? g.Red : g.Post;
        // podium and floor
        for (int x = x0 - 1; x <= x1 + 1; x++)
            for (int z = z0 - 1; z <= z1 + 1; z++)
            {
                int gy = GTop(g, x, z);
                for (int y = Math.Min(gy, floorY - 1); y < floorY; y++) k.Set(x, y, z, k.R() < 0.12 ? g.Mossy : g.Brick);
                k.Set(x, floorY, z, (x < x0 || x > x1 || z < z0 || z > z1) ? g.Polished : g.Floor);
                for (int y = floorY + 1; y <= floorY + wallH + 12; y++) k.Set(x, y, z, 0);
            }
        // front steps down from the podium
        int mid = (x0 + x1) / 2;
        for (int x = mid - 2; x <= mid + 2; x++)
        {
            int gy = GTop(g, x, z1 + 2);
            for (int s = 0; floorY - s > gy; s++)
                for (int y = gy; y <= floorY - s; y++) k.Set(x, y, z1 + 2 + s, y == floorY - s ? g.BrickSlab : g.Brick);
        }
        // walls and frame
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                bool wallX = x == x0 || x == x1, wallZ = z == z0 || z == z1;
                if (!wallX && !wallZ) continue;
                bool isPost = (wallX && (z - z0) % 4 == 0) || (wallZ && (x - x0) % 4 == 0) || (wallX && wallZ) || (wallX && z == z1) || (wallZ && x == x1);
                for (int y = floorY + 1; y <= floorY + wallH; y++)
                {
                    int id;
                    if (isPost) id = post;
                    else if (y == floorY + wallH) id = wallX ? g.PostNS : g.PostWE;
                    else if (y == floorY + 1) id = g.DarkPlank;
                    else id = g.Plaster;
                    // the door: the middle of the front, three wide, three high
                    if (z == z1 && Math.Abs(x - mid) <= 1 && y <= floorY + 3 && !isPost) id = 0;
                    // windows: one high, between posts, on the sides and back
                    if (!isPost && y == floorY + 3 && wallH >= 4 && (wallX ? (z - z0) % 4 == 2 : (x - x0) % 4 == 2) && z != z1) id = 0;
                    k.Set(x, y, z, id);
                }
            }
        GardensRoof(g, x0 - 2, z0 - 2, x1 + 2, z1 + 2, floorY + wallH + 1);
        // lanterns hung under the eaves at the corners and over the door
        GardensLantern(g, x0 - 1, floorY + wallH, z0 - 1); GardensLantern(g, x1 + 1, floorY + wallH, z0 - 1);
        GardensLantern(g, x0 - 1, floorY + wallH, z1 + 1); GardensLantern(g, x1 + 1, floorY + wallH, z1 + 1);
        GardensLantern(g, mid, floorY + wallH, z1 + 1);
        // a vine or two climbing the plaster (the green has crept in)
        for (int i = 0; i < 2; i++)
        {
            int vx = k.R() < 0.5 ? x0 - 1 : x1 + 1, vz = k.RI(z0 + 1, z1 - 1);
            GardensVine(g, vx, floorY + wallH - 1, vz, k.RI(2, wallH - 1), vx < x0 ? "west" : "east");   // a vine faces away from its wall
        }
        g.Taken.Add((x0 - 3, z0 - 3, x1 + 3, z1 + 6));
        g.Buildings++;

        // inside
        int fy = floorY + 1;
        switch (furnish)
        {
            case "hall":
            {
                // the altar at the back, the image on it, candles, lanterns
                for (int x = mid - 3; x <= mid + 3; x++)
                    for (int z = z0 + 1; z <= z0 + 2; z++) k.Set(x, fy, z, g.Polished);
                k.Clutter(mid, fy + 1, z0 + 1, "clutter|statue01", 0);
                k.Clutter(mid - 2, fy + 1, z0 + 2, "clutter|candlestubs-bunch2", 0);
                k.Clutter(mid + 2, fy + 1, z0 + 2, "clutter|candlestubs-bunch3", 0);
                k.Clutter(mid - 3, fy + 1, z0 + 1, "clutter|pottery/art" + k.RI(1, 27), 0);
                k.Clutter(mid + 3, fy + 1, z0 + 1, "clutter|pottery/art" + k.RI(1, 27), 0);
                for (int x = x0 + 2; x <= x1 - 2; x += 4) { GardensLantern(g, x, floorY + wallH - 1, z0 + 2); GardensLantern(g, x, floorY + wallH - 1, z1 - 2); }
                k.Job.StructChests.Add((new BlockPos(mid + 4, fy, z0 + 1, k.Job.Dim), "normal-aged",
                    new List<string> { "lore:hanging_gardens_chronicle", "game:candle*6", "game:beeswax*4" }, "the temple's chronicle"));
                break;
            }
            case "quarters":
            {
                for (int x = x0 + 1; x < x1; x += 3)
                {
                    k.Clutter(x, fy, z0 + 1, "clutter|bed/bed-straw-single" + k.RI(1, 2), 0);
                    if (k.R() < 0.5) k.Clutter(x + 1, fy, z0 + 1, "clutter|bookshelves/scrollrack-full" + k.RI(1, 5), 0);
                }
                k.Clutter(x0 + 1, fy, z1 - 1, "clutter|table1x1-plain", 0);
                k.Clutter(x0 + 2, fy, z1 - 1, "clutter|pottery/utensils" + k.RI(1, 8), 0);
                k.Clutter(x1 - 1, fy, z1 - 1, "clutter|jars" + k.RI(1, 4), 0);
                GardensLantern(g, mid, floorY + wallH - 1, (z0 + z1) / 2);
                k.Job.StructChests.Add((new BlockPos(x1 - 1, fy, z0 + 1, k.Job.Dim), "normal-aged", RollTable("temple", k.Rand, k.RI(4, 6)), "the monks' stores"));
                break;
            }
        }
    }

    // A five-tier pagoda: each tier a little narrower, each with its skirt
    // of roof, a tiled crown and a copper spire with its rings.
    private void GardensPagoda(GardensCtx g, int x, int z, int baseY)
    {
        var k = g.K;
        int[] hw = { 3, 3, 2, 2, 1 };
        int y = baseY;
        for (int x0 = x - 5; x0 <= x + 5; x0++)
            for (int z0 = z - 5; z0 <= z + 5; z0++)
            {
                int gy = GTop(g, x0, z0);
                for (int yy = Math.Min(gy, baseY - 1); yy < baseY; yy++) k.Set(x0, yy, z0, g.Brick);
                k.Set(x0, baseY, z0, g.Polished);
            }
        y = baseY + 1;
        for (int t = 0; t < hw.Length; t++)
        {
            int h = hw[t];
            int bodyH = t == 0 ? 5 : 3;
            for (int dx = -h; dx <= h; dx++)
                for (int dz = -h; dz <= h; dz++)
                    for (int yy = y; yy < y + bodyH; yy++)
                    {
                        bool edge = Math.Abs(dx) == h || Math.Abs(dz) == h;
                        if (!edge) { k.Set(x + dx, yy, z + dz, 0); continue; }
                        bool corner = Math.Abs(dx) == h && Math.Abs(dz) == h;
                        int id = corner ? g.Red : yy == y + bodyH - 1 ? g.DarkPlank : g.Plaster;
                        if (t == 0 && dz == h && Math.Abs(dx) <= 1 && yy < y + 3) id = 0;   // the door
                        k.Set(x + dx, yy, z + dz, id);
                    }
            int ry = y + bodyH;
            // the skirt: three courses out from the body
            for (int i = 0; i < 3; i++)
            {
                int r = h + 3 - i;
                int cy = i == 0 ? ry : i == 1 ? ry : ry + 1;
                bool half = i != 1;
                for (int dx = -r; dx <= r; dx++)
                    for (int dz = -r; dz <= r; dz++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dz) != r) continue;
                        bool corner = Math.Abs(dx) == r && Math.Abs(dz) == r;
                        if (i == 0 && corner) { k.Set(x + dx, cy, z + dz, g.RoofBlk); k.Set(x + dx, cy + 1, z + dz, g.RoofSlab); continue; }
                        k.Set(x + dx, cy, z + dz, half ? g.RoofSlab : g.RoofBlk);
                    }
            }
            for (int dx = -h; dx <= h; dx++)
                for (int dz = -h; dz <= h; dz++) k.Set(x + dx, ry, z + dz, g.DarkPlank);
            GardensLantern(g, x + h + 2, ry - 1, z + h + 2); GardensLantern(g, x - h - 2, ry - 1, z - h - 2);
            y = ry + 2;
        }
        // the crown and spire
        GardensRoof(g, x - 2, z - 2, x + 2, z + 2, y - 1);
        for (int i = 0; i < 9; i++) k.Set(x, y + 2 + i, z, i % 3 == 1 ? g.Copper : g.Post);
        g.Taken.Add((x - 7, z - 7, x + 7, z + 7));
        g.Buildings++;
    }

    // ── the terrace ───────────────────────────────────────────────────────
    private void GardensTerrace(GardensCtx g)
    {
        var k = g.K;
        int cx = g.Cx, cz = g.Cz, T = g.T0;
        // the court: level, paved down the middle, grass round it
        GardensLevel(g, cx - 42, cz - 34, cx + 42, cz + 30, T, 6, g.Grass);
        // the main hall faces south down the approach
        GardensHall(g, cx - 12, cz - 26, cx + 12, cz - 10, T + 2, 6, true, "hall");
        GardensPagoda(g, cx + 28, cz - 14, T + 1);
        // the open pavilion opposite the pagoda: four posts and a roof
        {
            int px = cx - 28, pz = cz - 14;
            for (int x = px - 4; x <= px + 4; x++)
                for (int z = pz - 4; z <= pz + 4; z++) k.Set(x, T + 1, z, g.Polished);
            foreach (var (dx, dz) in new[] { (-3, -3), (3, -3), (-3, 3), (3, 3) })
                for (int y = T + 2; y <= T + 5; y++) k.Set(px + dx, y, pz + dz, g.Red);
            GardensRoof(g, px - 5, pz - 5, px + 5, pz + 5, T + 6, true);
            k.Clutter(px, T + 2, pz - 1, "clutter|chair-long", 0);
            k.Clutter(px, T + 2, pz + 1, "clutter|chair-long", Math.PI);
            GardensLantern(g, px, T + 5, pz);
            g.Taken.Add((px - 6, pz - 6, px + 6, pz + 6));
        }
        // the monks' quarters behind the hall
        GardensHall(g, cx - 38, cz - 33, cx - 24, cz - 25, T + 1, 4, false, "quarters");
        GardensHall(g, cx + 24, cz - 33, cx + 38, cz - 25, T + 1, 4, false, "quarters");

        // the paved way from the hall's steps to the head of the stair, toro either side
        for (int z = cz - 7; z <= cz + 32; z++)
            for (int x = cx - 2; x <= cx + 2; x++)
            {
                int gy = GTop(g, x, z);
                if (gy < T - 1) continue;
                k.Set(x, gy, z, Math.Abs(x - cx) == 2 ? g.Brick : (k.R() < 0.1 ? g.MossCobble : g.Polished));
            }
        for (int z = cz - 4; z <= cz + 28; z += 8)
        {
            GardensToro(g, cx - 5, GTop(g, cx - 5, z) + 1, z);
            GardensToro(g, cx + 5, GTop(g, cx + 5, z) + 1, z);
        }
        // the raked gravel garden, its stones mossed over
        {
            int x0 = cx - 34, z0 = cz + 6, x1 = cx - 16, z1 = cz + 18;
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    bool rim = x == x0 || x == x1 || z == z0 || z == z1;
                    k.Set(x, GTop(g, x, z), z, rim ? g.Polished : g.Gravel);
                    k.Set(x, GTop(g, x, z) + 1, z, 0);
                }
            int moss = k.Id("mosslayertinted-full");
            for (int i = 0; i < 5; i++)
            {
                int bx = k.RI(x0 + 3, x1 - 4), bz = k.RI(z0 + 3, z1 - 4), by = GTop(g, bx, bz);
                int bs = k.RI(1, 2);
                for (int dx = 0; dx <= bs; dx++)
                    for (int dz = 0; dz <= bs; dz++)
                        for (int dy = 1; dy <= bs; dy++)
                            if (dx + dz + dy < bs * 2 + 1) k.Set(bx + dx, by + dy, bz + dz, g.Stone);
                if (moss != 0) k.Set(bx, by + bs + 1, bz, moss);
            }
            g.Taken.Add((x0, z0, x1, z1));
        }
        // the koi pond with its red bridge
        {
            int px = cx + 24, pz = cz + 12, r = 7;
            for (int x = px - r - 1; x <= px + r + 1; x++)
                for (int z = pz - r - 1; z <= pz + r + 1; z++)
                {
                    double d = Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz) * 1.4);
                    int gy = GTop(g, x, z);
                    if (d <= r)
                    {
                        // a lined basin two deep, sealed all round
                        k.Set(x, gy - 3, z, g.Stone);
                        for (int y = gy - 2; y <= gy; y++)
                            if (g.Water != 0) k.SetFluid(x, y, z, g.Water);
                        if (g.Lily != 0 && k.R() < 0.12) k.Set(x, gy + 1, z, g.Lily);
                    }
                    else if (d <= r + 1.2)
                    {
                        for (int y = gy - 3; y <= gy; y++) k.Set(x, y, z, y == gy ? (k.R() < 0.3 ? g.MossCobble : g.Polished) : g.Stone);
                    }
                }
            for (int x = px - r - 1; x <= px + r + 1; x++)
            {
                int gy = GTop(g, x, pz);
                int arch = (int)Math.Round(2 * Math.Sin(Math.PI * (x - (px - r - 1)) / (2.0 * r + 2)));
                k.Set(x, gy + 1 + arch, pz, g.Deck);
                k.Set(x, gy + 1 + arch, pz - 1, g.Red); k.Set(x, gy + 2 + arch, pz - 1, g.FenceEW);
                k.Set(x, gy + 1 + arch, pz + 1, g.Red); k.Set(x, gy + 2 + arch, pz + 1, g.FenceEW);
            }
            g.Taken.Add((px - r - 2, pz - r - 2, px + r + 2, pz + r + 2));
        }
        // maples round the court
        foreach (var (dx, dz) in new[] { (-40, -6), (40, -6), (-18, 26), (18, 26), (-40, 24), (40, 24) })
        {
            int x = cx + dx, z = cz + dz;
            g.Trees.Add((x, GTop(g, x, z), z, "japanesemaple", 1.1f));
        }
        GardensBamboo(g, cx - 40, cz - 40, 14, 11);
        GardensBamboo(g, cx + 42, cz - 40, 12, 11);
    }

    // ── the spires ────────────────────────────────────────────────────────
    private void GardensSpires(GardensCtx g, int count, System.Func<double, int> edge)
    {
        var k = g.K;
        int cx = g.Cx, cz = g.Cz;
        int island = k.Job.Diameter / 2;
        for (int t = 0; t < count * 60 && g.Spires.Count < count; t++)
        {
            double a = k.R() * Math.PI * 2;
            // leave the approach (south) and the landing beach open
            if (AngDist(a, Math.PI / 2) < 0.28) continue;
            double d = edge(a) + 30 + k.R() * Math.Max(10, island * 0.82 - edge(a) - 46);
            int x = cx + (int)Math.Round(Math.Cos(a) * d), z = cz + (int)Math.Round(Math.Sin(a) * d);
            double rb = 10 + k.R() * 6;
            int gy = k.Ground(x, z);
            if (gy < k.Sea + 3) continue;
            bool clear = g.Spires.All(s => Math.Sqrt((s.X - x) * (s.X - x) + (s.Z - z) * (s.Z - z)) > s.Rb + rb + 22);
            if (!clear || !g.Free(x - (int)rb - 4, z - (int)rb - 4, x + (int)rb + 4, z + (int)rb + 4)) continue;
            int minG = int.MaxValue;
            bool wet = false;
            for (int dx = -(int)rb - 4; dx <= rb + 4; dx += 2)
                for (int dz = -(int)rb - 4; dz <= rb + 4; dz += 2)
                {
                    minG = Math.Min(minG, k.Ground(x + dx, z + dz));
                    if (GWet(g, x + dx, z + dz)) wet = true;
                }
            if (wet) continue;
            var s = new GardenSpire
            {
                // the crown must hold the stair's 5x5 hole and its rail
                X = x, Z = z, Rb = rb, Rt = Math.Max(7.6, rb * (0.6 + k.R() * 0.12)),
                Base = Math.Max(k.Sea - 4, minG - 3), Top = k.Sea + k.RI(52, 96),
                Ph1 = k.R() * 6.28, Ph2 = k.R() * 6.28
            };
            if (s.Top - g.T0 < 20) s.Top = g.T0 + 20 + k.RI(0, 20);
            // ledges every 9 to 13 blocks up its side, each on its own bearing
            for (int ly = gy + 12; ly < s.Top - 6; ly += k.RI(9, 13))
                s.Ledges.Add((ly, k.R() * Math.PI * 2, 0.6 + k.R() * 0.9, 2.5 + k.R() * 1.8));
            g.Spires.Add(s);
        }
        foreach (var s in g.Spires) GardensBuildSpire(g, s);
    }

    private void GardensBuildSpire(GardensCtx g, GardenSpire s)
    {
        var k = g.K;
        int reach = (int)Math.Ceiling(s.Rb + 6);
        GardensClearTrees(g, s.X - reach - 3, s.Z - reach - 3, s.X + reach + 3, s.Z + reach + 3, s.Base, s.Top + 20);
        bool Solid(int x, int y, int z)
        {
            double dx = x - s.X, dz = z - s.Z;
            return Math.Sqrt(dx * dx + dz * dz) <= SpireR(s, y, Math.Atan2(dz, dx));
        }
        // Soil and grass only where something can grow: the crown and the
        // ledges (grass on every little step of the face read as spots).
        bool Garden(int x, int y, int z)
        {
            if (!Solid(x, y, z) || Solid(x, y + 1, z) || !Solid(x, y - 1, z) || !Solid(x, y - 2, z)) return false;
            if (y >= s.Top - 2) return true;
            double dx = x - s.X, dz = z - s.Z, d = Math.Sqrt(dx * dx + dz * dz);
            foreach (var l in s.Ledges)
                if (y == l.Y && d > SpireR(s, y, Math.Atan2(dz, dx), false) - 0.5) return true;
            return false;
        }
        // the rock
        for (int x = s.X - reach; x <= s.X + reach; x++)
            for (int z = s.Z - reach; z <= s.Z + reach; z++)
            {
                int gy = GTop(g, x, z);
                for (int y = Math.Min(s.Base, gy); y <= s.Top; y++)
                {
                    if (!Solid(x, y, z)) continue;
                    if (y <= gy - 2) continue;   // under the island's own ground: leave it
                    int id;
                    if (Garden(x, y, z)) id = g.Grass;
                    else if (Garden(x, y + 1, z)) id = g.Dirt;
                    else id = SpHash01(x >> 1, y / 3, z >> 1, 71) < 0.18 ? g.Chalk : g.Stone;
                    k.Set(x, y, z, id);
                }
            }

        // the stair up its middle: a 5x5 shaft, a post, eight sectors of half steps
        double toCentre = Math.Atan2(g.Cz - s.Z, g.Cx - s.X);
        int doorSector = (int)Math.Round(toCentre / (Math.PI / 4)) & 7;
        int[] sx8 = { 1, 1, 0, -1, -1, -1, 0, 1 }, sz8 = { 0, 1, 1, 1, 0, -1, -1, -1 };
        // the door: where the ground meets the spire on the side toward the terrace
        int ddx = sx8[doorSector], ddz = sz8[doorSector];
        int faceD = 3;
        while (faceD < s.Rb + 6 && Solid(s.X + ddx * faceD, GTop(g, s.X + ddx * faceD, s.Z + ddz * faceD) + 1, s.Z + ddz * faceD)) faceD++;
        int outX = s.X + ddx * (faceD + 1), outZ = s.Z + ddz * (faceD + 1);
        s.F = Math.Max(k.Sea + 2, GTop(g, outX, outZ) + 1);
        s.K0 = (doorSector + 2) & 7;
        // the shaft
        for (int dx = -2; dx <= 2; dx++)
            for (int dz = -2; dz <= 2; dz++)
            {
                k.Set(s.X + dx, s.F - 1, s.Z + dz, g.Floor);
                for (int y = s.F; y <= s.Top; y++) k.Set(s.X + dx, y, s.Z + dz, dx == 0 && dz == 0 ? g.Post : 0);
            }
        // the steps: every ring cell belongs to one of eight sectors
        int Sector(int dx, int dz) => (int)Math.Round(Math.Atan2(dz, dx) / (Math.PI / 4)) & 7;
        for (int i = 0; ; i++)
        {
            double surf = s.F + 0.5 * (i + 1);
            if (surf > s.Top + 1) break;
            int sec = (s.K0 + i) & 7;
            bool half = surf % 1 != 0;
            int y = half ? (int)Math.Floor(surf) : (int)surf - 1;
            for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    if (Sector(dx, dz) != sec) continue;
                    k.Set(s.X + dx, y, s.Z + dz, half ? g.FloorSlab : g.Floor);
                }
            // a lantern let into the shaft wall every few turns
            if (i % 12 == 6) GardensLantern(g, s.X + sx8[sec] * 3, y + 2, s.Z + sz8[sec] * 3);
        }
        // the door passage out to the foot of the spire
        GardensPassage(g, s, doorSector, s.F);
        g.Doors.Add((outX, outZ));
        // round the hole at the top: a rail, a pavilion over it
        for (int dx = -3; dx <= 3; dx++)
            for (int dz = -3; dz <= 3; dz++)
            {
                if (Math.Abs(dx) != 3 && Math.Abs(dz) != 3) continue;
                if (Solid(s.X + dx, s.Top + 1, s.Z + dz)) continue;
                k.Set(s.X + dx, s.Top + 1, s.Z + dz, Math.Abs(dx) == 3 && Math.Abs(dz) == 3 ? g.Red : Math.Abs(dx) == 3 ? g.FenceNS : g.FenceEW);
            }
        int exitSec = (s.K0 + 2 * (s.Top + 1 - s.F) - 1) & 7;
        k.Set(s.X + sx8[exitSec] * 3, s.Top + 1, s.Z + sz8[exitSec] * 3, 0);
        foreach (var (dx, dz) in new[] { (-3, -3), (3, -3), (-3, 3), (3, 3) })
            for (int y = s.Top + 1; y <= s.Top + 4; y++) k.Set(s.X + dx, y, s.Z + dz, g.Red);
        GardensRoof(g, s.X - 5, s.Z - 5, s.X + 5, s.Z + 5, s.Top + 5, k.R() < 0.35);
        GardensLantern(g, s.X, s.Top + 4, s.Z);
        g.Taken.Add((s.X - (int)s.Rb - 2, s.Z - (int)s.Rb - 2, s.X + (int)s.Rb + 2, s.Z + (int)s.Rb + 2));

        // the crown's garden: a maple or two, flowers, a lantern, bamboo
        var crown = new List<(int X, int Z)>();
        for (int x = s.X - (int)s.Rt; x <= s.X + s.Rt; x++)
            for (int z = s.Z - (int)s.Rt; z <= s.Z + s.Rt; z++)
                if (Math.Abs(x - s.X) > 4 || Math.Abs(z - s.Z) > 4)
                    if (Solid(x, s.Top, z) && !Solid(x, s.Top + 1, z)) crown.Add((x, z));
        int trees = 0;
        foreach (var (x, z) in crown.OrderBy(_ => k.R()))
        {
            double r = k.R();
            if (trees < 2 && r < 0.05) { g.Trees.Add((x, s.Top, z, "japanesemaple", 0.75f)); trees++; }
            else if (r < 0.3 && g.Flowers.Length > 0) k.Set(x, s.Top + 1, z, g.Flowers[k.RI(0, g.Flowers.Length - 1)]);
            else if (r < 0.5 && g.Low.Length > 0) k.Set(x, s.Top + 1, z, g.Low[k.RI(0, g.Low.Length - 1)]);
        }
        if (crown.Count > 6) { var (lx, lz) = crown[k.RI(0, crown.Count - 1)]; GardensToro(g, lx, s.Top + 1, lz); }

        // the hanging gardens: every ledge planted, vines spilling over its lip
        foreach (var l in s.Ledges)
        {
            int planted = 0;
            for (double a = l.A - l.Span; a <= l.A + l.Span; a += 0.08)
            {
                double rr = SpireR(s, l.Y, a);
                for (double d = rr - 0.8; d >= rr - l.Out; d -= 0.9)
                {
                    int x = s.X + (int)Math.Round(Math.Cos(a) * d), z = s.Z + (int)Math.Round(Math.Sin(a) * d);
                    if (!Solid(x, l.Y, z) || Solid(x, l.Y + 1, z)) continue;
                    double r = k.R();
                    if (r < 0.32 && g.Flowers.Length > 0) k.Set(x, l.Y + 1, z, g.Flowers[k.RI(0, g.Flowers.Length - 1)]);
                    else if (r < 0.62 && g.Low.Length > 0) k.Set(x, l.Y + 1, z, g.Low[k.RI(0, g.Low.Length - 1)]);
                    else if (r < 0.64 && l.Out > 3.4) g.Trees.Add((x, l.Y, z, "japanesemaple", 0.45f));
                    planted++;
                }
                // vines down from the lip, and moss hanging under it
                if (k.R() < 0.5) GardensFaceVine(g, s, a, l.Y - 1, k.RI(5, 16));
                if (k.R() < 0.35) GardensUnderLichen(g, s, a, l.Y - 2);
            }
            if (planted > 0) g.Gardens++;
            // a window from the stair out onto the ledge
            int sec = (int)Math.Round(l.A / (Math.PI / 4)) & 7;
            for (int dd = 3; dd < s.Rb + 3; dd++)
            {
                int x = s.X + sx8[sec] * dd, z = s.Z + sz8[sec] * dd;
                if (!Solid(x, l.Y + 1, z) && !Solid(x, l.Y + 2, z)) break;
                k.Set(x, l.Y + 1, z, 0); k.Set(x, l.Y + 2, z, 0);
                k.Set(x, l.Y, z, g.Floor);
            }
        }
        // vines down the sheer faces, thickest from the crown
        for (int i = 0; i < (int)(s.Rb * 4); i++)
        {
            double a = k.R() * Math.PI * 2;
            int y = k.R() < 0.4 ? s.Top - k.RI(1, 3) : k.RI(s.F + 8, s.Top - 2);
            GardensFaceVine(g, s, a, y, k.RI(6, 22));
        }
    }

    // A vine on the spire's face toward bearing `a`: out along the nearest
    // compass direction to the last rock at height y, the vine in the air
    // beyond it facing away from the rock (a vine hangs on the block behind
    // it), running down until it meets anything.
    private void GardensFaceVine(GardensCtx g, GardenSpire s, double a, int y, int len)
    {
        var k = g.K;
        int dx = 0, dz = 0; string facing;
        if (Math.Abs(Math.Cos(a)) >= Math.Abs(Math.Sin(a))) { dx = Math.Cos(a) > 0 ? 1 : -1; facing = dx > 0 ? "east" : "west"; }
        else { dz = Math.Sin(a) > 0 ? 1 : -1; facing = dz > 0 ? "south" : "north"; }
        // along a line parallel to that direction, offset across the face
        double off = Math.Sin(a) * Math.Cos(a) >= 0 ? 1 : -1;
        double across = (Math.Abs(dx) == 1 ? Math.Sin(a) : Math.Cos(a)) * SpireR(s, y, a) * 0.9;
        int ox = s.X + (dz != 0 ? (int)Math.Round(across) : 0), oz = s.Z + (dx != 0 ? (int)Math.Round(across) : 0);
        bool Rock(int x, int z)
        {
            double ddx = x - s.X, ddz = z - s.Z;
            return Math.Sqrt(ddx * ddx + ddz * ddz) <= SpireR(s, y, Math.Atan2(ddz, ddx));
        }
        int step = 0;
        while (step < s.Rb + 8 && Rock(ox + dx * (step + 1), oz + dz * (step + 1))) step++;
        if (!Rock(ox + dx * step, oz + dz * step)) return;
        GardensVine(g, ox + dx * (step + 1), y, oz + dz * (step + 1), len, facing);
    }

    // Old man's beard hanging from the underside of a ledge.
    private void GardensUnderLichen(GardensCtx g, GardenSpire s, double a, int bulgeBottom)
    {
        var k = g.K;
        double rr = SpireR(s, bulgeBottom, a);
        int x = s.X + (int)Math.Round(Math.Cos(a) * (rr - 0.7)), z = s.Z + (int)Math.Round(Math.Sin(a) * (rr - 0.7));
        if (!InSpire(g, x, bulgeBottom, z) || InSpire(g, x, bulgeBottom - 1, z)) return;
        int top = k.Id("hanginglichen-oldmans-top"), seg = k.Id("hanginglichen-oldmans-segment"), bot = k.Id("hanginglichen-oldmans-bottom");
        if (top == 0 || bot == 0) return;
        int n = k.RI(2, 4);
        for (int i = 0; i < n; i++)
        {
            int y = bulgeBottom - 1 - i;
            if (InSpire(g, x, y - 1, z) || y - 1 <= GTop(g, x, z)) { n = i + 1; break; }
        }
        for (int i = 0; i < n; i++)
            k.Set(x, bulgeBottom - 1 - i, z, i == 0 ? top : i == n - 1 ? bot : (seg != 0 ? seg : top));
        g.Vines++;
    }

    // A passage from the shaft out through the spire's side along one of the
    // eight sector directions, its floor surface at `surf` (a whole block, or
    // a half when it leaves from a half step), three high, three wide.
    private void GardensPassage(GardensCtx g, GardenSpire s, int sector, double surf)
    {
        var k = g.K;
        int[] sx8 = { 1, 1, 0, -1, -1, -1, 0, 1 }, sz8 = { 0, 1, 1, 1, 0, -1, -1, -1 };
        bool half = surf % 1 != 0;
        int fy = half ? (int)Math.Floor(surf) : (int)surf - 1;   // the floor block
        int dx = sx8[sector], dz = sz8[sector];
        int px = -dz, pz = dx;   // across
        for (int d = 3; d < s.Rb + 8; d++)
        {
            int x = s.X + dx * d, z = s.Z + dz * d;
            bool any = false;
            for (int w = -1; w <= 1; w++)
                for (int y = fy + 1; y <= fy + 3; y++)
                {
                    int ax = x + px * w, az = z + pz * w;
                    double ddx = ax - s.X, ddz = az - s.Z;
                    if (Math.Sqrt(ddx * ddx + ddz * ddz) <= SpireR(s, y, Math.Atan2(ddz, ddx))) any = true;
                }
            if (!any) break;
            for (int w = -1; w <= 1; w++)
            {
                int ax = x + px * w, az = z + pz * w;
                k.Set(ax, fy, az, half ? g.FloorSlab : g.Floor);
                for (int y = fy + 1; y <= fy + 3; y++) k.Set(ax, y, az, 0);
                k.Set(ax, fy + 4, az, dx != 0 && dz == 0 ? g.PostWE : g.PostNS);
            }
        }
    }

    // ── the bridges ───────────────────────────────────────────────────────
    private void GardensBridges(GardensCtx g, System.Func<double, int> edge)
    {
        var k = g.K;
        int n = g.Spires.Count;
        if (n == 0) return;
        // node n = the terrace
        var edges = new List<(int A, int B, double D)>();
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double d = Math.Sqrt(Math.Pow(g.Spires[i].X - g.Spires[j].X, 2) + Math.Pow(g.Spires[i].Z - g.Spires[j].Z, 2));
                if (d < 130) edges.Add((i, j, d));
            }
            double a = Math.Atan2(g.Spires[i].Z - g.Cz, g.Spires[i].X - g.Cx);
            double dt = Math.Sqrt(Math.Pow(g.Spires[i].X - g.Cx, 2) + Math.Pow(g.Spires[i].Z - g.Cz, 2)) - edge(a);
            if (dt < 110) edges.Add((i, n, dt));
        }
        edges.Sort((p, q) => p.D.CompareTo(q.D));
        var parent = Enumerable.Range(0, n + 1).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var chosen = new List<(int A, int B)>();
        foreach (var e in edges)
        {
            int ra = Find(e.A), rb = Find(e.B);
            if (ra != rb) { parent[ra] = rb; chosen.Add((e.A, e.B)); }
            else if (k.R() < 0.18) chosen.Add((e.A, e.B));
        }
        foreach (var (a, b) in chosen)
        {
            if (b == n) GardensBridgeToTerrace(g, g.Spires[a], edge);
            else GardensBridgeBetween(g, g.Spires[a], g.Spires[b]);
        }
    }

    // The surface of spire s's stair step facing `sector` nearest to `want`
    // (step i stands at F + (i+1)/2, a half block per step), or -1.
    private static double GardensLevelAt(GardenSpire s, int sector, double want)
    {
        double best = -1, bestD = double.MaxValue;
        for (int i = 0; s.F + 0.5 * (i + 1) <= s.Top - 3; i++)
        {
            if (((s.K0 + i) & 7) != sector) continue;
            double y = s.F + 0.5 * (i + 1);
            double d = Math.Abs(y - want);
            if (d < bestD) { bestD = d; best = y; }
        }
        return best;
    }

    private static int SectorOf(double a) => (int)Math.Round(a / (Math.PI / 4)) & 7;

    private void GardensBridgeBetween(GardensCtx g, GardenSpire A, GardenSpire B)
    {
        var k = g.K;
        double ab = Math.Atan2(B.Z - A.Z, B.X - A.X);
        int secA = SectorOf(ab), secB = SectorOf(ab + Math.PI);
        int want = Math.Min(A.Top, B.Top) - 6 - k.RI(0, 10);
        double ya = GardensLevelAt(A, secA, want);
        if (ya < 0) return;
        double yb = GardensLevelAt(B, secB, ya);
        if (yb < 0 || Math.Abs(ya - yb) > 3) return;
        GardensDeck(g, A, secA, ya, B, secB, yb, 0, 0);
    }

    private void GardensBridgeToTerrace(GardensCtx g, GardenSpire A, System.Func<double, int> edge)
    {
        var k = g.K;
        double toC = Math.Atan2(g.Cz - A.Z, g.Cx - A.X);
        int secA = SectorOf(toC);
        double ya = GardensLevelAt(A, secA, g.T0 + 1);
        if (ya < 0 || Math.Abs(ya - (g.T0 + 1)) > 3) return;
        double back = toC + Math.PI;
        int ed = edge(back) - 2;
        int tx = g.Cx + (int)Math.Round(Math.Cos(back) * ed), tz = g.Cz + (int)Math.Round(Math.Sin(back) * ed);
        GardensDeck(g, A, secA, ya, null, 0, g.T0 + 1, tx, tz);
    }

    // The deck: from spire A's passage to spire B's (or to the terrace edge at
    // (tx, tz)), five wide, sagging a little, half steps where it rises, bamboo
    // rails, stringers under its edges, lanterns at its ends and middle.
    private void GardensDeck(GardensCtx g, GardenSpire A, int secA, double ya, GardenSpire B, int secB, double yb, int tx, int tz)
    {
        var k = g.K;
        int[] sx8 = { 1, 1, 0, -1, -1, -1, 0, 1 }, sz8 = { 0, 1, 1, 1, 0, -1, -1, -1 };
        // the faces: out along each sector until the rock ends
        (double X, double Z) Face(GardenSpire s, int sec, double surf)
        {
            int y = (int)Math.Floor(surf);
            for (int d = 3; d < s.Rb + 10; d++)
            {
                int x = s.X + sx8[sec] * d, z = s.Z + sz8[sec] * d;
                bool rock = false;
                for (int yy = y; yy <= y + 2; yy++)
                {
                    double dx = x - s.X, dz = z - s.Z;
                    if (Math.Sqrt(dx * dx + dz * dz) <= SpireR(s, yy, Math.Atan2(dz, dx))) rock = true;
                }
                if (!rock) return (x, z);
            }
            return (s.X + sx8[sec] * (s.Rb + 10), s.Z + sz8[sec] * (s.Rb + 10));
        }
        var fa = Face(A, secA, ya);
        var fb = B != null ? Face(B, secB, yb) : ((double)tx, (double)tz);
        double dxs = fb.Item1 - fa.X, dzs = fb.Item2 - fa.Z;
        double len = Math.Sqrt(dxs * dxs + dzs * dzs);
        if (len < 6 || len > 150 || Math.Abs(yb - ya) * 2 > len) return;
        double ux = dxs / len, uz = dzs / len, px = -uz, pz = ux;
        double sag = Math.Min(3, len * 0.035);
        int steps = (int)Math.Ceiling(len);
        // first, is the way clear?
        var cells = new List<(int X, int Z, double Q, int W)>();
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            double cxp = fa.X + dxs * t, czp = fa.Z + dzs * t;
            double surf = ya + (yb - ya) * t - sag * Math.Sin(Math.PI * t);
            double q = Math.Round(surf * 2) / 2;
            for (int w = -2; w <= 2; w++)
            {
                int x = (int)Math.Round(cxp + px * w), z = (int)Math.Round(czp + pz * w);
                cells.Add((x, z, q, w));
            }
        }
        foreach (var c in cells)
        {
            int yd = (int)Math.Floor(c.Q) - (c.Q % 1 == 0 ? 1 : 0);
            double da = Math.Sqrt((c.X - fa.X) * (c.X - fa.X) + (c.Z - fa.Z) * (c.Z - fa.Z));
            double db = Math.Sqrt((c.X - fb.Item1) * (c.X - fb.Item1) + (c.Z - fb.Item2) * (c.Z - fb.Item2));
            // the ground in the way (the terrace end rests on the terrace)
            if (GTop(g, c.X, c.Z) >= yd - 1 && !(B == null && db < 5)) return;
            for (int y = yd; y <= yd + 3; y++)
                // only the two ends may touch rock (that is where the passages go)
                if (InSpire(g, c.X, y, c.Z, null) && da > 3 && db > 3) return;
        }
        // the passages into the spires
        GardensPassage(g, A, secA, ya);
        if (B != null) GardensPassage(g, B, secB, yb);
        int minY = cells.Min(c => (int)c.Q) - 3, maxY = cells.Max(c => (int)c.Q) + 6;
        int bx0 = cells.Min(c => c.X), bx1 = cells.Max(c => c.X), bz0 = cells.Min(c => c.Z), bz1 = cells.Max(c => c.Z);
        GardensClearTrees(g, bx0 - 1, bz0 - 1, bx1 + 1, bz1 + 1, minY, maxY);
        bool alongX = Math.Abs(ux) >= Math.Abs(uz);
        int fence = alongX ? g.FenceEW : g.FenceNS, beam = alongX ? g.PostWE : g.PostNS;
        foreach (var c in cells)
        {
            bool half = c.Q % 1 != 0;
            int yd = half ? (int)Math.Floor(c.Q) : (int)c.Q - 1;
            if (Math.Abs(c.W) == 2)
            {
                int ys = (int)Math.Floor(c.Q - 0.01);
                k.Set(c.X, ys, c.Z, g.Deck);
                k.Set(c.X, ys - 1, c.Z, beam);
                if (!InSpire(g, c.X, ys + 1, c.Z)) k.Set(c.X, ys + 1, c.Z, fence);
                continue;
            }
            k.Set(c.X, yd, c.Z, half ? g.DeckSlab : g.Deck);
            for (int y = yd + 1; y <= yd + 3; y++) if (!InSpire(g, c.X, y, c.Z)) k.Set(c.X, y, c.Z, 0);
        }
        // lanterns on the rail at both ends and the middle
        foreach (double t in new[] { 0.06, 0.5, 0.94 })
        {
            double cxp = fa.X + dxs * t, czp = fa.Z + dzs * t;
            double q = Math.Round((ya + (yb - ya) * t - sag * Math.Sin(Math.PI * t)) * 2) / 2;
            int ys = (int)Math.Floor(q - 0.01);
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                int x = (int)Math.Round(cxp + px * 2 * sgn), z = (int)Math.Round(czp + pz * 2 * sgn);
                GardensLantern(g, x, ys + 2, z);
            }
        }
        g.Bridges++;
    }

    // ── the approach stair ────────────────────────────────────────────────
    private void GardensStair(GardensCtx g, int edgeSouth)
    {
        var k = g.K;
        int cx = g.Cx, z0 = g.Cz + edgeSouth - 1;
        int y0 = g.T0;
        // down south at half a block per block until it meets the ground
        var rows = new List<(int Z, double Surf)>();
        for (int i = 0; i < 160; i++)
        {
            double surf = y0 + 1 - 0.5 * i;
            int z = z0 + i;
            int gy = k.Ground(cx, z);
            rows.Add((z, surf));
            if (surf <= gy + 1.01) break;
        }
        int footZ = rows[rows.Count - 1].Z;
        GardensClearTrees(g, cx - 6, z0 - 2, cx + 6, footZ + 3, g.Sea, y0 + 20);
        foreach (var (z, surf) in rows)
        {
            bool half = surf % 1 != 0;
            int ys = half ? (int)Math.Floor(surf) : (int)surf - 1;
            for (int x = cx - 4; x <= cx + 4; x++)
            {
                int gy = GTop(g, x, z);
                bool rail = Math.Abs(x - cx) == 4;
                int top = rail ? (int)Math.Floor(surf) : ys;
                for (int y = Math.Min(gy, top) - 1; y < top; y++) k.Set(x, y, z, k.R() < 0.1 ? g.Mossy : g.Brick);
                k.Set(x, top, z, rail ? (k.R() < 0.2 ? g.Mossy : g.Brick) : half ? g.BrickSlab : g.Brick);
                for (int y = top + 1; y <= top + 4; y++) k.Set(x, y, z, 0);
                g.Tops[GardensCtx.Col(x, z)] = top;
            }
        }
        for (int i = 4; i < rows.Count; i += 10)
        {
            var (z, surf) = rows[i];
            GardensToro(g, cx - 6, Math.Max(GTop(g, cx - 6, z), (int)surf - 1) + 1, z);
            GardensToro(g, cx + 6, Math.Max(GTop(g, cx + 6, z), (int)surf - 1) + 1, z);
        }
        // torii at the head, the middle and the foot
        GardensTorii(g, cx, y0 + 1, z0 + 1, false, 5, 8);
        if (rows.Count > 20) { var (mz, ms) = rows[rows.Count / 2]; GardensTorii(g, cx, (int)Math.Floor(ms) + 1, mz, false, 5, 8); }
        GardensTorii(g, cx, GTop(g, cx, footZ + 2) + 1, footZ + 2, false, 5, 8);
        g.Taken.Add((cx - 7, z0 - 2, cx + 7, footZ + 3));
        g.Doors.Insert(0, (cx, footZ + 3));   // paths start from the foot of the stair
    }

    // ── the markets ───────────────────────────────────────────────────────
    private void GardensMarkets(GardensCtx g)
    {
        var k = g.K;
        var trades = new[] { ("furniture", 2.35), ("clothing", 1.15), ("agriculture", 0.2) };
        foreach (var (trade, bearing) in trades)
        {
            for (int t = 0; t < 80; t++)
            {
                double a = bearing + (k.R() - 0.5) * 0.7;
                double d = 105 + k.R() * 60;
                int x = g.Cx + (int)Math.Round(Math.Cos(a) * d), z = g.Cz + (int)Math.Round(Math.Sin(a) * d);
                int x0 = x - 10, z0 = z - 9, x1 = x + 10, z1 = z + 9;
                if (!g.Free(x0, z0, x1, z1, 4) || g.Spires.Any(s => Math.Abs(s.X - x) < s.Rb + 16 && Math.Abs(s.Z - z) < s.Rb + 16)) continue;
                var ys = new List<int>();
                bool wet = false;
                for (int xx = x0; xx <= x1; xx += 2)
                    for (int zz = z0 - 4; zz <= z1 + 4; zz += 2) { int gy = GTop(g, xx, zz); ys.Add(gy); if (gy < k.Sea + 2 || GWet(g, xx, zz)) wet = true; }
                if (wet) continue;
                ys.Sort();
                if (ys[ys.Count - 1] - ys[0] > 7) continue;
                GardensMarket(g, trade, x0, z0, x1, z1, ys[ys.Count / 2]);
                break;
            }
        }
    }

    private void GardensMarket(GardensCtx g, string trade, int x0, int z0, int x1, int z1, int y)
    {
        var k = g.K;
        GardensLevel(g, x0, z0, x1, z1, y, 5, g.Cobble);
        // paving: cobble with gravel joints and moss
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                double r = SpHash01(x, z, 3, 81);
                k.Set(x, y, z, r < 0.12 ? g.MossCobble : r < 0.22 ? g.Gravel : g.Cobble);
            }
        int mx = (x0 + x1) / 2, mz = (z0 + z1) / 2;
        GardensToro(g, mx, y + 1, mz);
        // three stalls along the back, open to the south
        string[] goods = trade switch
        {
            "furniture" => new[] { "clutter|wardrobe-small", "clutter|shelf2x1-plain", "clutter|table2x1-plain", "clutter|chair-back", "clutter|bed/bed-straw-single1", "clutter|shelf1x1-plain", "clutter|table1x1-cloth" },
            "clothing" => new[] { "clutter|coatrack-full1", "clutter|coatrack-full2", "clutter|coatrack-full3", "clutter|shelf-clothing1", "clutter|shelf-clothing2", "clutter|shelf-shoes1", "clutter|clothing1", "clutter|clothing2", "clutter|pile-cloth2", "clutter|pile-sewingkit" },
            _ => new[] { "clutter|sacks1", "clutter|sacks2", "clutter|sacks3", "clutter|jars1", "clutter|jars2", "clutter|dryingrack2", "clutter|dryingrack4", "clutter|crate/crate-large-pottery", "clutter|pottery/container3" },
        };
        string awning = trade switch { "furniture" => "clutter|overhang2x1-angled-woodplain", "clothing" => "clutter|overhang2x1-angled-clothfancy", _ => "clutter|overhang2x1-angled-clothplain" };
        for (int i = 0; i < 3; i++)
        {
            int sx0 = x0 + 1 + i * 7, sz0 = z0 + 1, sx1 = sx0 + 5, sz1 = sz0 + 4;
            foreach (var (px, pz) in new[] { (sx0, sz0), (sx1, sz0), (sx0, sz1), (sx1, sz1) })
                for (int yy = y + 1; yy <= y + 4; yy++) k.Set(px, yy, pz, g.Post);
            // back wall of planks, a counter along the front
            for (int x = sx0 + 1; x < sx1; x++)
            {
                for (int yy = y + 1; yy <= y + 3; yy++) k.Set(x, yy, sz0, g.DarkPlank);
                k.Set(x, y + 1, sz1 - 1, g.Floor);
                k.Clutter(x, y + 2, sz1 - 1, goods[k.RI(0, goods.Length - 1)], Math.PI);
            }
            for (int x = sx0 + 1; x < sx1; x++)
                for (int z = sz0 + 1; z < sz1 - 1; z++)
                    if (k.R() < 0.35) k.Clutter(x, y + 1, z, goods[k.RI(0, goods.Length - 1)], k.RI(0, 3) * Math.PI / 2);
            GardensRoof(g, sx0 - 1, sz0 - 1, sx1 + 1, sz1 + 1, y + 5, trade != "furniture");
            k.Clutter(sx0 + 1, y + 4, sz1 + 1, awning, Math.PI);
            k.Clutter(sx1 - 2, y + 4, sz1 + 1, awning, Math.PI);
            GardensLantern(g, (sx0 + sx1) / 2, y + 4, sz1);
            g.Stalls++;
        }
        // the trader keeps the middle stall
        string code = $"trader-{(k.R() < 0.5 ? "male" : "female")}-{trade}-temperate";
        g.Traders.Add((x0 + 1 + 7 + 3, y + 1, z0 + 3, code));
        // goods out on the square too
        for (int i = 0; i < 10; i++)
        {
            int x = k.RI(x0 + 1, x1 - 1), z = k.RI(z0 + 8, z1 - 1);
            if (Math.Abs(x - mx) < 2 && Math.Abs(z - mz) < 2) continue;
            if (trade == "agriculture" && k.R() < 0.4) { int hay = k.Id("hay-normal-ud"); if (hay != 0) { k.Set(x, y + 1, z, hay); continue; } }
            k.Clutter(x, y + 1, z, goods[k.RI(0, goods.Length - 1)], k.RI(0, 3) * Math.PI / 2);
        }
        k.Clutter(x0 + 2, y + 1, z1 - 1, "clutter|chair-long", 0);
        k.Clutter(x1 - 2, y + 1, z1 - 1, "clutter|chair-long", 0);
        // the agriculture trader's plot beside the square
        if (trade == "agriculture")
        {
            int farm = k.IdF("farmland-moist-compost", "farmland-dry-compost", "farmland-dry-medium");
            string[] crops = { "crop-cabbage-6", "crop-carrot-7", "crop-onion-6", "crop-rice-9", "crop-turnip-5", "crop-soybean-9", "crop-flax-8" };
            int fx0 = x1 + 3, fz0 = z0;
            if (farm != 0 && g.Free(fx0, fz0, fx0 + 10, fz0 + 12))
            {
                for (int x = fx0; x <= fx0 + 10; x++)
                    for (int z = fz0; z <= fz0 + 12; z++)
                    {
                        int gy = GTop(g, x, z);
                        if (gy < k.Sea + 1 || Math.Abs(gy - y) > 3) continue;
                        if ((x - fx0) % 4 == 3) { k.Set(x, gy, z, g.Gravel); continue; }
                        k.Set(x, gy, z, farm);
                        int crop = k.Id(crops[((x - fx0) / 4 + (z - fz0) / 4) % crops.Length]);
                        if (crop != 0) k.Set(x, gy + 1, z, crop); else k.Need(crops[0]);
                    }
                g.Taken.Add((fx0, fz0, fx0 + 10, fz0 + 12));
            }
        }
        g.Taken.Add((x0 - 2, z0 - 2, x1 + 2, z1 + 2));
        g.Doors.Add((x0 + 10, z1 + 2));
    }

    // ── the landing ───────────────────────────────────────────────────────
    private void GardensHarbor(GardensCtx g)
    {
        var k = g.K;
        // south from the foot of the stair to the water
        int x = g.Cx + 14;
        int z = g.Cz + 60;
        while (z < g.Cz + g.R + 120 && k.Ground(x, z) >= k.Sea) z++;
        if (z >= g.Cz + g.R + 120) return;
        int shore = z;
        GardensClearTrees(g, x - 3, shore - 8, x + 3, shore + 28, k.Sea - 2, k.Sea + 12);
        for (int zz = shore - 6; zz <= shore + 24; zz++)
            for (int xx = x - 2; xx <= x + 2; xx++)
            {
                k.Set(xx, k.Sea + 1, zz, Math.Abs(xx - x) == 2 ? g.Deck : g.Floor);
                if (Math.Abs(xx - x) == 2 && (zz - shore) % 4 == 0)
                {
                    for (int y = k.Ground(xx, zz) + 1; y <= k.Sea; y++) k.Set(xx, y, zz, g.Post);
                    k.Set(xx, k.Sea + 2, zz, g.Post);
                    if ((zz - shore) % 8 == 0) GardensLantern(g, xx, k.Sea + 3, zz);
                }
            }
        // the torii standing in the sea beyond the pier
        // six past the pier's end; the shelf drops fast, so its pillars may
        // stand in deep water, each down to its own bit of sea floor
        int tz = shore + 30;
        int bed = Math.Min(k.Ground(x - 4, tz), k.Ground(x + 4, tz));
        if (bed < k.Sea && bed > k.Sea - 40)
        {
            for (int s = -1; s <= 1; s += 2)
                for (int y = k.Ground(x + s * 4, tz) + 1; y <= k.Sea; y++) k.Set(x + s * 4, y, tz, g.Red);
            GardensTorii(g, x, k.Sea + 1, tz, false, 4, 9);
        }
        g.Taken.Add((x - 3, shore - 7, x + 3, shore + 25));
    }

    // ── paths and groves ──────────────────────────────────────────────────
    private void GardensPaths(GardensCtx g)
    {
        var k = g.K;
        if (g.Doors.Count < 2) return;
        var (sx, sz) = g.Doors[0];
        foreach (var (tx, tz) in g.Doors.Skip(1))
        {
            double len = Math.Sqrt((tx - sx) * (tx - sx) + (tz - sz) * (tz - sz));
            int n = (int)len;
            double wob = k.R() * 6;
            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)Math.Max(1, n);
                double side = Math.Sin(t * Math.PI) * Math.Sin(t * 5 + wob) * Math.Min(10, len * 0.06);
                double px = sx + (tx - sx) * t - (tz - sz) / Math.Max(1, len) * side, pz = sz + (tz - sz) * t + (tx - sx) / Math.Max(1, len) * side;
                for (int w = -1; w <= 1; w++)
                    for (int v = -1; v <= 1; v++)
                    {
                        if (Math.Abs(w) + Math.Abs(v) > 1) continue;
                        int x = (int)Math.Round(px) + w, z = (int)Math.Round(pz) + v;
                        if (!g.Free(x, z, x, z)) continue;
                        int gy = GTop(g, x, z);
                        if (gy < k.Sea + 1 || InSpire(g, x, gy + 1, z) || GWet(g, x, z)) continue;
                        k.Set(x, gy, z, SpHash01(x, z, 1, 91) < 0.15 ? g.MossCobble : g.Gravel);
                        k.Set(x, gy + 1, z, 0);
                    }
            }
        }
    }

    private void GardensGroves(GardensCtx g)
    {
        var k = g.K;
        for (int i = 0; i < 18; i++)
        {
            double a = k.R() * Math.PI * 2, d = 80 + k.R() * (g.R - 90);
            int x = g.Cx + (int)Math.Round(Math.Cos(a) * d), z = g.Cz + (int)Math.Round(Math.Sin(a) * d);
            if (!g.Free(x - 4, z - 4, x + 4, z + 4) || GTop(g, x, z) < k.Sea + 2 || GWet(g, x, z)) continue;
            GardensClearTrees(g, x - 4, z - 4, x + 4, z + 4, GTop(g, x, z) + 1, GTop(g, x, z) + 20);
            GardensBamboo(g, x, z, k.RI(8, 16), k.RI(8, 13));
        }
    }

    // ── after the commit: trees, traders ──────────────────────────────────
    private void GardensAfter(GardensCtx g)
    {
        var k = g.K;
        int trees = 0;
        var tba = sapi.World.GetBlockAccessorBulkUpdate(true, true);
        foreach (var t in g.Trees)
        {
            var gen = FindTreeGenerator(t.Gen);
            if (gen == null) continue;
            var tp = new TreeGenParams
            {
                skipForestFloor = true, size = t.Size, vinesGrowthChance = 0, mossGrowthChance = 0, otherBlockChance = 1f,
                hemisphere = EnumHemisphere.North, treesInChunkGenerated = 0
            };
            gen.GrowTree(tba, new BlockPos(t.X, t.Y, t.Z, k.Job.Dim), tp, k.Job.Rand);
            trees++;
        }
        tba.Commit();

        int traders = 0;
        foreach (var t in g.Traders)
        {
            string key = $"lgGardensTrader:{t.X},{t.Z}";
            if (sapi.WorldManager.SaveGame.GetData<bool>(key, false)) continue;
            var type = sapi.World.GetEntityType(new AssetLocation("game", t.Code));
            if (type == null) { sapi.Logger.Warning("[landmassgenerator] gardens: no entity {0}", t.Code); continue; }
            Entity e = sapi.World.ClassRegistry.CreateEntity(type);
            e.ServerPos.SetPos(t.X + 0.5, t.Y, t.Z + 0.5);
            e.ServerPos.Dimension = k.Job.Dim;
            e.Pos.SetFrom(e.ServerPos);
            sapi.World.SpawnEntity(e);
            sapi.WorldManager.SaveGame.StoreData(key, true);
            traders++;
            sapi.Logger.Notification("[landmassgenerator] gardens: {0} at HUD {1}, {2}, {3}", t.Code,
                t.X - sapi.WorldManager.MapSizeX / 2, t.Y, t.Z - sapi.WorldManager.MapSizeZ / 2);
        }
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        foreach (var s in g.Spires)
            sapi.Logger.Notification("[landmassgenerator] gardens spire at HUD {0}, {1}: foot {2}, top {3}, door {4}", s.X - mx, s.Z - mz, s.Base, s.Top, s.F);
        sapi.Logger.Notification("[landmassgenerator] hanging gardens temple at HUD {0}, {1} (terrace {2}): {3} spires, {4} hanging gardens, {5} bridges, {6} buildings, {7} market stalls, {8} traders, {9} trees, {10} bamboo groves, {11} vines, {12} lanterns",
            g.Cx - mx, g.Cz - mz, g.T0, g.Spires.Count, g.Gardens, g.Bridges, g.Buildings, g.Stalls, traders, trees, g.Groves, g.Vines, g.Lanterns);
    }
}
