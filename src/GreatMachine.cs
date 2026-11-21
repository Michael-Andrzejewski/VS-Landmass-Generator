using System;
using System.Collections.Generic;

using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace LandmassGenerator;

// ── THE GREAT MACHINE (0.63.0) ──────────────────────────────────────────
// The heart of the Rot, rebuilt after Michael's sketch (a Halo energy sword
// with the machine at its hilt). The island (great_machine shape) carries
// a vast impact crater; this pass finishes it:
//
// - the crater surface scorched to drock and devastated soil;
// - an abyss cut into the crater floor, dry far below the sea, with scree
//   at its walls and lava pools on its floor;
// - chasms from the abyss out through the island to the sea, each closed
//   by a rubble sill near the coast so the sea fills only its last stretch,
//   with branch cracks and thin fractures across the plateau;
// - a 30-block steel sphere hanging in the crater, its top a little below
//   the plateau, on massive chains from five floating islands, propped by
//   three stalagmites rising out of the abyss. Inside: a double iron door,
//   a deck to a central control area round the machine core, machinery
//   below the deck, and a rupture torn outward at the top right as seen
//   from the walkway;
// - a double helix from the sphere's top to a point, and two blades of
//   pale crystal either side of it, shaped like the blades of a Halo energy
//   sword, about twice the helix's height, the sphere where the hilt is;
// - a walkway with rusted railings from the crater wall to the doors, on
//   stalagmite piers, and a stone stair from its landing up to the rim;
// - ruined walls round the crater, broken into separate runs with whole
//   sides gone, blast holes through them, and the chasms cutting them;
// - a red fog zone over the island (RedFog.cs).
//
// Horizontal radii scale with the island (the shape was drawn at 4 blocks
// a cell); heights come from the built ground. Keys: rim, abyss, wall,
// sphere, turns, fog (zone radius, 0 = none), fogfade.
public partial class LandmassGeneratorModSystem
{
    private void MegaGreatMachine(MegaKit k, int cx, int cz)
    {
        double sc = Math.Max(0.5, k.Job.WorldPerCell / 4.0);
        int Sc(double v) => (int)Math.Round(v * sc);
        int sea = k.Sea, mapY = sapi.WorldManager.MapSizeY, seed = k.Def.Seed;
        int rimR = Sc(k.OptD("rim", 248)), abyssR = Sc(k.OptD("abyss", 84)), wallR = Sc(k.OptD("wall", 300));
        int sphR = Math.Clamp(k.OptI("sphere", 15), 8, 24);

        int basalt = k.IdF("rock-basalt"), andes = k.IdF("rock-andesite", "rock-basalt"), obsid = k.IdF("rock-obsidian", "rock-basalt");
        int drock = k.IdF("drock", "rock-basalt");
        int devSoil = k.IdF("devastatedsoil-3", "devastatedsoil-0");
        int conc = k.IdF("rockpolished-andesite", "rock-andesite"), concR = k.IdF("rock-andesite", "stonebricks-andesite");
        int crack = k.IdF("crackedstonebricks-andesite", "rock-andesite");
        int dark = k.IdF("rockpolished-basalt", "rock-basalt");
        int obsP = k.IdF("rockpolished-obsidian", "rock-obsidian");
        int steel = k.IdF("metalblock-new-plain-steel", "metalblock-new-plain-iron");
        int steelR = k.IdF("metalblock-new-riveted-steel", "metalblock-new-riveted-iron");
        int rustA = k.RustA != 0 ? k.RustA : steelR, rustB = k.RustB != 0 ? k.RustB : rustA;
        if (steel == 0) steel = rustB;
        if (steelR == 0) steelR = rustA;
        int ice = k.Need("packedglacierice"), iceGlow = k.Need("glacierice"), vein = k.Need("glass-blue");
        if (ice == 0) ice = steel;
        if (iceGlow == 0) iceGlow = ice;
        if (vein == 0) vein = ice;
        int glowB = k.Id("landmassgenerator:ghostlight-blue");
        int core = k.Id("spreadingdevastation:machinecore");
        int beacon = k.Id("spreadingdevastation:beaconlight");
        int lava = k.Id("lava-still-7");
        int fenceNS = k.Need("ironfence-base-ns");
        int slab = k.IdF("stonebrickslab-andesite-down-free", "cobblestoneslab-andesite-down-free");
        int doorLD = k.Need("irondoor-south-down-closed-left"), doorLU = k.Need("irondoor-south-up-closed-left");
        int doorRD = k.Need("irondoor-south-down-closed-right"), doorRU = k.Need("irondoor-south-up-closed-right");
        string[] growths = { "devgrowth-thorns", "devgrowth-shard", "devgrowth-bush", "devgrowth-shrike" };
        int[] growthIds = Array.ConvertAll(growths, c => k.Id(c));

        System.Func<int> concF = () => k.R() < 0.9 ? conc : crack;
        System.Func<int> rust = () => k.R() < 0.6 ? rustA : rustB;
        System.Func<int> steelF = () => k.R() < 0.5 ? steelR : steel;
        double Rad(double deg) => deg * Math.PI / 180.0;
        double Hyp(double a, double b) => Math.Sqrt(a * a + b * b);
        long CK(int x, int z) => ((long)x << 32) ^ (uint)z;

        // What the carving left: the new solid top of a carved column, and
        // the top of anything built on a column (the walls), so a later cut
        // goes all the way through.
        var floorAt = new Dictionary<long, int>();
        var topAt = new Dictionary<long, int>();
        int Floor(int x, int z) => floorAt.TryGetValue(CK(x, z), out int f) ? f : k.Ground(x, z);
        void Carved(int x, int z, int newTop)
        {
            long key = CK(x, z);
            if (!floorAt.TryGetValue(key, out int f) || newTop < f) floorAt[key] = newTop;
        }

        // The natural sea floor under a deep-ocean island keeps pockets of
        // sea water (in limestone up to ~sea-35, papercuts). A dry cut below
        // the sea must never open one: a cell next to water (sideways or
        // above) stays rock. The world read sees the committed terrain,
        // which is exactly the natural water; our own staged writes are
        // rock and air.
        var wba = sapi.World.BlockAccessor;
        var wpos = new BlockPos(0, 0, 0, k.Job.Dim);
        int keptRock = 0;
        bool WaterAt(int x, int y, int z)
        {
            wpos.Set(x, y, z);
            var b = wba.GetBlock(wpos, BlockLayersAccess.Fluid);
            return b != null && b.Id != 0;
        }
        bool KeepDry(int x, int y, int z)
        {
            if (y >= sea - 1) return true;
            if (WaterAt(x, y, z) || WaterAt(x + 1, y, z) || WaterAt(x - 1, y, z) || WaterAt(x, y, z + 1) || WaterAt(x, y, z - 1) || WaterAt(x, y + 1, z))
            {
                keptRock++;
                return false;
            }
            return true;
        }

        // The plateau's level, measured on a ring outside the walls.
        var ringG = new List<int>();
        for (int i = 0; i < 72; i++)
        {
            double a = i * Math.PI / 36;
            ringG.Add(k.Ground(cx + (int)Math.Round(Math.Cos(a) * (wallR + 40)), cz + (int)Math.Round(Math.Sin(a) * (wallR + 40))));
        }
        ringG.Sort();
        int G = Math.Max(sea + 10, ringG[ringG.Count / 2]);
        int bladeTop = mapY - 6;
        int Yc = Math.Min(G - 4 - sphR, bladeTop - 100);         // the sphere's top just under the plateau
        int floorA = Math.Max(14, sea - 70);                       // the abyss floor
        double vS = (bladeTop - Yc) / 110.0;                       // blade outline drawn 110 tall above the sphere centre
        int helixTop = Yc + sphR + (int)Math.Round((110 + 14) * vS / 2);   // the blades are twice the helix
        sapi.Logger.Notification("[landmassgenerator] great machine: plateau y {0}, sphere centre y {1}, abyss floor {2}, helix to {3}, blades to {4}, crater rim r {5}, walls r {6}",
            G, Yc, floorA, helixTop, bladeTop, rimR, wallR);

        // ── 1. Scorch the ground ──────────────────────────────────────
        // Drock and devastated soil in noise patches: heavy in the crater,
        // thinner across the rest of the island. Bare soil left alone grows
        // grass back in the chunks the game ticks (the first test build had
        // green squares on the crater rim), devastated ground does not. Only
        // natural ground is touched: the forts are already standing.
        int scorchR = Sc(620);
        for (int x = cx - scorchR; x <= cx + scorchR; x++)
            for (int z = cz - scorchR; z <= cz + scorchR; z++)
            {
                double d = Hyp(x - cx, z - cz);
                if (d > scorchR) continue;
                int g = k.Ground(x, z);
                if (g < sea - 1) continue;
                bool inCrater = d < rimR + 16;
                double n = GmFbm2(x * 0.045, z * 0.045, seed + 11);
                double tRock = inCrater ? 0.52 : 0.60, tSoil = inCrater ? 0.42 : 0.52;
                if (n <= tSoil) continue;
                wpos.Set(x, g, z);
                string cur = wba.GetBlock(wpos)?.Code?.Path ?? "";
                if (!(cur.StartsWith("soil") || cur.StartsWith("sand") || cur.StartsWith("gravel") || cur.StartsWith("rock-"))) continue;
                if (n > tRock)
                {
                    k.Set(x, g, z, drock);
                    if (n > tRock + 0.08) k.Set(x, g - 1, z, drock);
                }
                else if (devSoil != 0) k.Set(x, g, z, devSoil);
            }

        // ── 2. The ruined walls ───────────────────────────────────────
        // An 18-sided ring of brutalist wall round the crater, one side's
        // middle on the south axis (the gate the walkway's stair leads to).
        // Five sides are gone, five more stand only in part, the rest are
        // crumbled along the top; blast holes go through most of them.
        int sides = 18;
        double dA = Math.PI * 2 / sides, a0 = Math.PI / 2 - dA / 2;
        double apo = wallR * Math.Cos(dA / 2), halfLen = wallR * Math.Sin(dA / 2);
        int wallH = 34;
        var sideKind = new int[sides];
        var sideFrom = new double[sides];
        var sideTo = new double[sides];
        var holes = new List<(double U, double V, double R)>[sides];
        {
            var order = new List<int>();
            for (int j = 1; j < sides; j++) order.Add(j);
            for (int j = order.Count - 1; j > 0; j--) { int s = (int)(k.R() * (j + 1)); (order[j], order[s]) = (order[s], order[j]); }
            for (int j = 0; j < sides; j++) { sideFrom[j] = 0; sideTo[j] = 1; holes[j] = new List<(double, double, double)>(); }
            for (int i = 0; i < 5; i++) sideKind[order[i]] = 1;
            for (int i = 5; i < 10; i++)
            {
                sideKind[order[i]] = 2;
                if (k.R() < 0.5) sideFrom[order[i]] = 0.25 + k.R() * 0.4; else sideTo[order[i]] = 0.35 + k.R() * 0.4;
            }
            for (int j = 0; j < sides; j++)
            {
                if (sideKind[j] == 1) continue;
                int n = k.RI(0, 2);
                for (int i = 0; i < n; i++)
                    holes[j].Add(((k.R() - 0.5) * halfLen * 1.6, 8 + k.R() * 16, 5 + k.R() * 7));
            }
        }
        var rubbleAt = new List<(int X, int Z)>();
        for (int x = cx - wallR - 10; x <= cx + wallR + 10; x++)
            for (int z = cz - wallR - 10; z <= cz + wallR + 10; z++)
            {
                double dx = x - cx, dz = z - cz, d = Hyp(dx, dz);
                if (d < apo - 6 || d > wallR + 10) continue;
                double th = Math.Atan2(dz, dx);
                int j = (int)Math.Floor(((th - a0) % (Math.PI * 2) + Math.PI * 2) % (Math.PI * 2) / dA);
                if (j >= sides) j = sides - 1;
                double mid = a0 + (j + 0.5) * dA, nx = Math.Cos(mid), nz = Math.Sin(mid);
                double p = dx * nx + dz * nz, u = -dx * nz + dz * nx;
                double frac = (u + halfLen) / (2 * halfLen);
                bool body = p >= apo - 3 && p < apo + 3;
                bool fin = p >= apo + 3 && p < apo + 6 && ((int)Math.Floor(u) % 9 + 9) % 9 == 0;
                if (!body && !fin) continue;
                if (sideKind[j] == 1 || frac < sideFrom[j] || frac > sideTo[j])
                {
                    if (body && k.Hash01(x * 3, z * 7) < 0.05) rubbleAt.Add((x, z));
                    continue;
                }
                int g = k.Ground(x, z);
                if (g < sea - 1) continue;
                // the crown crumbles in notches, and worst at a broken end
                double n = GmNoise2(u * 0.07, j * 7.3, seed + 12);
                double lower = n > 0.58 ? (n - 0.58) * 70 : 0;
                double endDist = Math.Min(frac - sideFrom[j], sideTo[j] - frac) * 2 * halfLen;
                if ((sideFrom[j] > 0 && frac - sideFrom[j] < 0.12) || (sideTo[j] < 1 && sideTo[j] - frac < 0.12))
                    lower += Math.Max(0, 22 - endDist * 0.9);
                int top = g + wallH + (fin ? 2 : 0) - (int)lower - (k.Hash01(x * 5, z * 3) < 0.05 ? 2 : 0);
                bool gate = j == 0 && Math.Abs(u) < 8;
                for (int y = g - 2; y <= top; y++)
                {
                    int ly = y - g;
                    if (gate && ly >= 0 && ly < 22) continue;
                    bool holed = false;
                    foreach (var h in holes[j])
                        if ((u - h.U) * (u - h.U) + (ly - h.V) * (ly - h.V) < h.R * h.R) { holed = true; break; }
                    if (holed) continue;
                    k.Set(x, y, z, fin ? concR : (ly % 12 == 0 ? dark : concF()));
                }
                topAt[CK(x, z)] = top;
            }
        foreach (var (rx, rz) in rubbleAt)
        {
            int g = k.Ground(rx, rz), h = 1 + k.RI(0, 3);
            for (int i = 0; i < 3; i++)
            {
                int x = rx + k.RI(-3, 3), z = rz + k.RI(-3, 3), gg = k.Ground(x, z);
                if (gg < sea - 1) continue;
                for (int y = gg + 1; y <= gg + h - i; y++) k.Set(x, y, z, k.R() < 0.6 ? concF() : concR);
            }
        }

        // ── 3. The abyss ──────────────────────────────────────────────
        // A funnel cut into the crater floor down to floorA, its radius
        // wavering with the bearing and its walls chewed by 3D noise, so it
        // has ledges and overhangs. Scree banks up against the walls; lava
        // pools sit in hollows of the floor, level with their rims.
        double ARad(double th) => abyssR * (1 + 0.10 * Math.Sin(5 * th + 1) + 0.06 * Math.Sin(11 * th + 2) + 0.04 * Math.Sin(17 * th + 0.5));
        int FTop(int x, int z)
        {
            double d = Hyp(x - cx, z - cz), fr = d / ARad(Math.Atan2(z - cz, x - cx));
            return floorA + (int)Math.Round(10 * Smooth((fr - 0.55) / 0.45) + 3 * GmNoise2(x * 0.08, z * 0.08, seed + 21));
        }
        var pools = new List<(int X, int Z, double R, int Level)>();
        for (int i = 0; i < 5; i++)
        {
            double a = k.R() * Math.PI * 2, r = abyssR * (0.12 + k.R() * 0.42);
            int px = cx + (int)Math.Round(Math.Cos(a) * r), pz = cz + (int)Math.Round(Math.Sin(a) * r);
            double pr = 3 + k.R() * 4;
            int lvl = int.MaxValue;
            for (int x = px - (int)pr - 2; x <= px + pr + 2; x++)
                for (int z = pz - (int)pr - 2; z <= pz + pr + 2; z++)
                    if (Hyp(x - px, z - pz) <= pr + 1.6) lvl = Math.Min(lvl, FTop(x, z));
            pools.Add((px, pz, pr, lvl));
        }
        int abyssCells = 0;
        int rA = (int)(abyssR * 1.25) + 2;
        for (int x = cx - rA; x <= cx + rA; x++)
            for (int z = cz - rA; z <= cz + rA; z++)
            {
                double d = Hyp(x - cx, z - cz);
                if (d > rA) continue;
                double RA = ARad(Math.Atan2(z - cz, x - cx));
                int g = k.Ground(x, z);
                int fTop = FTop(x, z);
                int poolLvl = -1;
                bool poolRim = false;
                foreach (var pl in pools)
                {
                    double pd = Hyp(x - pl.X, z - pl.Z);
                    if (pd <= pl.R) { poolLvl = pl.Level; break; }
                    if (pd <= pl.R + 1.6) poolRim = true;
                }
                if (poolLvl >= 0) fTop = poolLvl - 1;
                int lowest = int.MaxValue;
                for (int y = fTop + 1; y <= g + 3; y++)
                {
                    double hf = Math.Clamp((y - floorA) / (double)Math.Max(1, g - floorA), 0, 1);
                    double wr = RA * (0.80 + 0.20 * hf) * (0.90 + 0.22 * GmNoise3(x * 0.07, y * 0.09, z * 0.07, seed + 22));
                    if (d > wr || !KeepDry(x, y, z)) continue;
                    k.Set(x, y, z, 0);
                    if (y < lowest) lowest = y;
                }
                if (lowest == int.MaxValue) continue;
                abyssCells++;
                int top = lowest - 1;
                if (poolLvl >= 0 && lava != 0 && lowest <= poolLvl)
                {
                    k.Set(x, poolLvl - 1, z, obsid);
                    k.SetFluid(x, poolLvl, z, lava);
                    Carved(x, z, poolLvl);
                    continue;
                }
                double n = GmNoise2(x * 0.11, z * 0.11, seed + 23);
                k.Set(x, top, z, poolRim ? obsid : n > 0.62 ? drock : n < 0.25 ? obsid : (k.Hash01(x, z * 3) < 0.5 ? basalt : andes));
                Carved(x, z, top);
            }

        // ── 4. Chasms ─────────────────────────────────────────────────
        // A fissure is a polyline with a half width and a floor along it.
        // Each column finds its nearest point on the line, then every cell
        // between the floor and the top is cut where it lies within the
        // half width at that height: a V that narrows to a quarter at the
        // bottom, each wall chewed by its own noise. Cells below the sea
        // in the coastal stretch fill with sea water.
        int chasmCells = 0;
        void Fissure(List<(double X, double Z, double S)> pts, System.Func<double, double> hwAt, System.Func<double, int, int> floorOf,
            double waterFromS, int salt)
        {
            if (pts.Count < 2) return;
            double hwMax = 0;
            foreach (var p in pts) hwMax = Math.Max(hwMax, hwAt(p.S));
            double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var p in pts) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z); }
            int pad = (int)Math.Ceiling(hwMax * 1.3) + 3;
            for (int x = (int)minX - pad; x <= (int)maxX + pad; x++)
                for (int z = (int)minZ - pad; z <= (int)maxZ + pad; z++)
                {
                    double best = double.MaxValue, bs = 0, side = 1;
                    for (int i = 0; i + 1 < pts.Count; i++)
                    {
                        double ax = pts[i].X, az = pts[i].Z, bx = pts[i + 1].X, bz = pts[i + 1].Z;
                        double vx = bx - ax, vz = bz - az, l2 = vx * vx + vz * vz;
                        double t = l2 < 1e-9 ? 0 : Math.Clamp(((x - ax) * vx + (z - az) * vz) / l2, 0, 1);
                        double qx = ax + vx * t - x, qz = az + vz * t - z, dd = qx * qx + qz * qz;
                        if (dd < best) { best = dd; bs = pts[i].S + (pts[i + 1].S - pts[i].S) * t; side = vx * (z - az) - vz * (x - ax) >= 0 ? 1 : -1; }
                    }
                    double dist = Math.Sqrt(best), hw = hwAt(bs);
                    if (hw <= 0.3 || dist > hw * 1.25 + 1) continue;
                    int g = k.Ground(x, z);
                    if (g < sea - 1) continue;                       // open sea already
                    int fy = floorOf(bs, g);
                    int top = Math.Max(g, topAt.TryGetValue(CK(x, z), out int wt) ? wt : g) + 3;
                    bool wet = bs >= waterFromS;
                    int lowest = int.MaxValue;
                    for (int y = fy + 1; y <= top; y++)
                    {
                        double hf = Math.Clamp((y - fy) / (double)Math.Max(4, G - fy), 0, 1);
                        double hwy = hw * (0.25 + 0.75 * Math.Pow(hf, 0.75));
                        double nn = GmNoise3(bs * 0.05, y * 0.08, side * 17 + salt, seed + 33);
                        if (dist > hwy * (0.80 + 0.40 * nn)) continue;
                        if (wet && y <= sea - 1) k.SetFluid(x, y, z, k.Job.SaltWaterId);
                        else if (KeepDry(x, y, z)) k.Set(x, y, z, 0);
                        else continue;
                        if (y < lowest) lowest = y;
                        chasmCells++;
                    }
                    if (lowest == int.MaxValue) continue;
                    // a column the abyss (or another fissure) already cut
                    // deeper keeps its floor: no slab left hanging in the air
                    bool deeper = floorAt.TryGetValue(CK(x, z), out int prev) && prev < lowest - 1;
                    if (!deeper && (!wet || lowest > sea - 1))
                    {
                        double n = GmNoise2(x * 0.13, z * 0.13, seed + 34);
                        k.Set(x, lowest - 1, z, n > 0.6 ? drock : n < 0.2 ? obsid : basalt);
                    }
                    Carved(x, z, lowest - 1);
                }
        }

        // the bearing where the coast lies along a polyline, in radius
        double CoastAlong(List<(double X, double Z, double S)> pts)
        {
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double len = Hyp(pts[i + 1].X - pts[i].X, pts[i + 1].Z - pts[i].Z);
                for (double s = 0; s < len; s += 2)
                {
                    double t = s / len;
                    int x = (int)Math.Round(pts[i].X + (pts[i + 1].X - pts[i].X) * t), z = (int)Math.Round(pts[i].Z + (pts[i + 1].Z - pts[i].Z) * t);
                    if (k.Ground(x, z) < sea - 1) return pts[i].S + (pts[i + 1].S - pts[i].S) * t;
                }
            }
            return pts[pts.Count - 1].S;
        }

        double[] chasmAngles = { 18, 72, 116, 198, 246, 292 };
        var chasmLines = new List<List<(double X, double Z, double S)>>();
        for (int ci = 0; ci < chasmAngles.Length; ci++)
        {
            double a = Rad(chasmAngles[ci] + (k.R() - 0.5) * 6), th = a, r = abyssR * 0.8;
            var pts = new List<(double X, double Z, double S)> { (cx + Math.Cos(th) * r, cz + Math.Sin(th) * r, r) };
            int wetRun = 0;
            // long straight runs with sharp kinks: a crack, not a river
            while (r < 1600)
            {
                r += 30 + k.R() * 22;
                th += Rad((k.R() - 0.5) * 18);
                th += (a - th) * 0.45;
                double px = cx + Math.Cos(th) * r, pz = cz + Math.Sin(th) * r;
                pts.Add((px, pz, r));
                if (k.Ground((int)Math.Round(px), (int)Math.Round(pz)) < sea - 1) { if (++wetRun >= 2) break; }
                else wetRun = 0;
            }
            chasmLines.Add(pts);
            double rc = CoastAlong(pts), r0 = pts[0].S;
            double hw0 = Sc(17 + k.R() * 7);
            int salt = 100 + ci * 13;
            double HwAt(double s) => hw0 * (1 - 0.55 * Math.Clamp((s - r0) / Math.Max(1, rc - r0), 0, 1)) * (0.85 + 0.3 * GmNoise2(s * 0.03, salt, seed + 31));
            int deep = Math.Max(floorA + 12, sea - 52);
            int FloorOf(double s, int g)
            {
                double inland = deep + 10 * GmNoise2(s * 0.02, salt + 1, seed + 32);
                double y = s < rc - 95 ? inland
                    : s < rc - 40 ? inland + (sea - 1 - inland) * Smooth((s - (rc - 95)) / 55.0)
                    : s < rc - 24 ? sea - 1
                    : sea - 1 - 13 * Smooth((s - (rc - 24)) / 24.0);
                return Math.Min(g - 2, (int)Math.Round(y));
            }
            Fissure(pts, HwAt, FloorOf, rc - 24, salt);

            // one branch crack off each chasm, dry, shallower, ending inland
            {
                int bi = Math.Clamp((int)(pts.Count * (0.3 + k.R() * 0.25)), 1, pts.Count - 2);
                double bth = Math.Atan2(pts[bi].Z - cz, pts[bi].X - cx) + (k.R() < 0.5 ? -1 : 1) * Rad(28 + k.R() * 22);
                double bx = pts[bi].X, bz = pts[bi].Z, blen = Sc(90 + k.R() * 80), s = 0;
                var bpts = new List<(double X, double Z, double S)> { (bx, bz, 0) };
                double bhead = bth;
                while (s < blen)
                {
                    s += 16 + k.R() * 8;
                    bhead += Rad((k.R() - 0.5) * 24);
                    bx += Math.Cos(bhead) * 20; bz += Math.Sin(bhead) * 20;
                    if (k.Ground((int)bx, (int)bz) < sea + 3) break;
                    bpts.Add((bx, bz, s));
                }
                double bw = Sc(6 + k.R() * 3), bend = Math.Max(1, bpts[bpts.Count - 1].S);
                Fissure(bpts, s2 => bw * (1 - Smooth(s2 / bend)) + 0.4, (s2, g) => Math.Min(g - 3, sea + 4 + (int)(6 * GmNoise2(s2 * 0.05, salt + 2, seed + 35))), double.MaxValue, salt + 7);
            }
        }

        // thin fractures across the plateau, radiating from the rim
        for (int i = 0; i < 22; i++)
        {
            double a = k.R() * Math.PI * 2, deg = (a * 180 / Math.PI) % 360;
            // keep clear of the walkway's stair, the chasms, the forts on the
            // diagonals and the pylon lines on the axes
            bool clear = Math.Abs(((deg - 90) + 540) % 360 - 180) > 12;
            foreach (double ca in chasmAngles) if (Math.Abs(((deg - ca) + 540) % 360 - 180) < 7) clear = false;
            foreach (double ba in new[] { 0.0, 45, 135, 180, 225, 270, 315 }) if (Math.Abs(((deg - ba) + 540) % 360 - 180) < 8) clear = false;
            if (!clear) continue;
            double r = rimR + 10 + k.R() * 50, len = Sc(50 + k.R() * 140), s = 0, head = a;
            double fx = cx + Math.Cos(a) * r, fz = cz + Math.Sin(a) * r;
            var fpts = new List<(double X, double Z, double S)> { (fx, fz, 0) };
            while (s < len)
            {
                s += 10;
                head += Rad((k.R() - 0.5) * 30) + (a - head) * 0.2;
                fx += Math.Cos(head) * 10; fz += Math.Sin(head) * 10;
                fpts.Add((fx, fz, s));
            }
            double fw = 1.2 + k.R() * 1.6;
            int depth = 6 + k.RI(0, 10);
            Fissure(fpts, s2 => fw * (1 - 0.8 * Smooth(s2 / len)) + 0.3, (s2, g) => g - depth, double.MaxValue, 300 + i);
        }

        // ── 5. Spikes: stalagmites and stalactites ────────────────────
        // A fluted cone from base to tip, any direction. Rock in bands, a
        // devastated skin near its foot.
        void Spike(double bx, double by, double bz, double tx, double ty, double tz, double rb, double rt, int salt)
        {
            double ax = tx - bx, ay = ty - by, az = tz - bz, len = Math.Sqrt(ax * ax + ay * ay + az * az);
            if (len < 1) return;
            double ux = ax / len, uy = ay / len, uz = az / len;
            double rr = Math.Max(rb, rt) * 1.3 + 1;
            int x0 = (int)Math.Floor(Math.Min(bx, tx) - rr), x1 = (int)Math.Ceiling(Math.Max(bx, tx) + rr);
            int y0 = (int)Math.Floor(Math.Min(by, ty) - rr), y1 = (int)Math.Ceiling(Math.Max(by, ty) + rr);
            int z0 = (int)Math.Floor(Math.Min(bz, tz) - rr), z1 = (int)Math.Ceiling(Math.Max(bz, tz) + rr);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                    {
                        double px = x - bx, py = y - by, pz = z - bz;
                        double t = (px * ux + py * uy + pz * uz) / len;
                        if (t < 0 || t > 1) continue;
                        double qx = px - ux * t * len, qy = py - uy * t * len, qz = pz - uz * t * len;
                        double q = Math.Sqrt(qx * qx + qy * qy + qz * qz);
                        double r = rb + (rt - rb) * Math.Pow(t, 0.85);
                        double ang = Math.Atan2(qz, qx);
                        r *= (1 + 0.13 * Math.Cos(6 * ang + t * 4 + salt)) * (0.92 + 0.16 * GmNoise3(x * 0.15, y * 0.15, z * 0.15, seed + salt));
                        if (q > r) continue;
                        int band = ((y + salt) % 7 + 7) % 7;
                        k.Set(x, y, z, t < 0.08 ? drock : band == 0 ? andes : (k.Hash01(x * 7 + y, z * 5 - y) < 0.06 ? obsid : basalt));
                    }
        }

        // ── 6. Floating islands ───────────────────────────────────────
        // Five chunks of the island torn loose and hanging over the crater:
        // a domed top of devastated ground, an underside tapering to a cone
        // with stalactites under it, a stalagmite rising toward each from
        // below (two of them meet it). Anchors for the chains on top.
        var isles = new List<(int X, int Z, int Top, double Rho, double Az)>();
        var isleFeet = new List<(int X, int Z)>();       // where each island's stalagmite rises
        var isleDefs = new (double Az, double R, int Up, double Rho)[]
        {
            (40, 112, 44, 17), (140, 104, 50, 19), (222, 120, 40, 15), (272, 98, 56, 21), (322, 116, 47, 16)
        };
        int chestIsle = k.RI(0, isleDefs.Length - 1);
        for (int ii = 0; ii < isleDefs.Length; ii++)
        {
            var def = isleDefs[ii];
            double az = Rad(def.Az + (k.R() - 0.5) * 8);
            int ix = cx + (int)Math.Round(Math.Cos(az) * Sc(def.R)), iz = cz + (int)Math.Round(Math.Sin(az) * Sc(def.R));
            int top = Math.Min(bladeTop - 30, Yc + def.Up);
            double rho = def.Rho;
            int salt = 400 + ii * 19;
            int lowestUnder = int.MaxValue, ux = ix, uz = iz;
            int ri = (int)Math.Ceiling(rho * 1.3);
            for (int x = ix - ri; x <= ix + ri; x++)
                for (int z = iz - ri; z <= iz + ri; z++)
                {
                    double dx = x - ix, dz = z - iz, q = Hyp(dx, dz), ang = Math.Atan2(dz, dx);
                    double edge = rho * (0.80 + 0.32 * GmNoise2(Math.Cos(ang) * 1.6 + ii * 5, Math.Sin(ang) * 1.6, seed + 41));
                    if (q > edge) continue;
                    double f = q / edge;
                    int yTop = top + (int)Math.Round(2.5 * (1 - f * f) + 1.4 * (GmNoise2(x * 0.15, z * 0.15, seed + salt) - 0.5));
                    double depth = rho * 1.25 * Math.Pow(1 - f, 0.75) + 2 + 3 * GmNoise2(x * 0.11, z * 0.11, seed + salt + 3);
                    int yBot = (int)Math.Round(top - depth);
                    for (int y = yBot; y <= yTop; y++)
                    {
                        int m = y == yTop ? (GmNoise2(x * 0.2, z * 0.2, seed + salt + 5) > 0.45 ? devSoil : drock)
                            : y >= yTop - 3 ? (k.Hash01(x + y, z) < 0.5 ? andes : basalt)
                            : (GmNoise3(x * 0.12, y * 0.12, z * 0.12, seed + salt + 6) > 0.7 ? drock : basalt);
                        k.Set(x, y, z, m == 0 ? drock : m);
                    }
                    if (yBot < lowestUnder) { lowestUnder = yBot; ux = x; uz = z; }
                    // dead growth on the devastated soil
                    if (k.Hash01(x * 11, z * 13) < 0.05)
                    {
                        int gid = growthIds[k.RI(0, growthIds.Length - 1)];
                        if (gid != 0) k.Set(x, yTop + 1, z, gid);
                    }
                }
            // stalactites, the longest under the cone's point
            int tipLow = lowestUnder;
            int nTites = 4 + k.RI(0, 4);
            for (int t = 0; t < nTites; t++)
            {
                double ta = k.R() * Math.PI * 2, tq = t == 0 ? 0 : rho * (0.2 + k.R() * 0.5);
                int tx = (t == 0 ? ux : ix + (int)Math.Round(Math.Cos(ta) * tq)), tz = (t == 0 ? uz : iz + (int)Math.Round(Math.Sin(ta) * tq));
                double under = top - (rho * 1.25 * Math.Pow(1 - Math.Min(1, tq / rho), 0.75) + 2);
                double L = t == 0 ? 14 + k.R() * 10 : 5 + k.R() * 12;
                Spike(tx, under + 3, tz, tx + (k.R() - 0.5) * 3, under - L, tz + (k.R() - 0.5) * 3, t == 0 ? 5.5 : 3 + k.R() * 2, 0.5, salt + t);
                tipLow = Math.Min(tipLow, (int)Math.Round(under - L));
            }
            // the stalagmite reaching up for it
            {
                int fl = Floor(ux, uz);
                int gap = ii % 2 == 0 ? 0 : 3 + k.RI(0, 4);
                if (tipLow - gap - fl > 6)
                    Spike(ux + (k.R() - 0.5) * 6, fl - 5, uz + (k.R() - 0.5) * 6, ux, tipLow - gap + (gap == 0 ? 2 : 0), uz, 6.5 + k.R() * 3, gap == 0 ? 2.2 : 1.2, salt + 50);
            }
            // a broken pillar on two of them, a chest on one
            if (ii % 2 == 1)
            {
                int px = ix + k.RI(-4, 4), pz = iz + k.RI(-4, 4), ph = 6 + k.RI(0, 8);
                k.Box(px - 1, top, pz - 1, px + 1, top + ph, pz + 1, conc);
                for (int y = top + ph - 2; y <= top + ph; y++) if (k.R() < 0.5) k.Set(px + k.RI(-1, 1), y, pz + k.RI(-1, 1), 0);
            }
            if (ii == chestIsle)
                k.Job.StructChests.Add((new BlockPos(ix + 2, top + 4, iz - 1, k.Job.Dim), "normal-aged", RollLoot(k.Rand, MachineLoot, 5 + k.RI(0, 2)), "floating island"));
            isles.Add((ix, iz, top, rho, az));
            isleFeet.Add((ux, uz));
        }

        // ── 7. The sphere ─────────────────────────────────────────────
        // Steel shell two blocks thick: riveted bands at the equator, the
        // tropics and on eight meridians, rust eating the panels between.
        // The rupture is torn outward at the top right as seen from the
        // walkway (south, looking north: right is east).
        double rupX = 0.507, rupY = 0.616, rupZ = 0.604;
        double rupCos = Math.Cos(Rad(30));
        int R = sphR;
        for (int x = cx - R - 1; x <= cx + R + 1; x++)
            for (int y = Yc - R - 1; y <= Yc + R + 1; y++)
                for (int z = cz - R - 1; z <= cz + R + 1; z++)
                {
                    double dx = x - cx, dy = y - Yc, dz = z - cz, d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d > R + 0.45) continue;
                    if (d <= R - 2.0) { k.Set(x, y, z, 0); continue; }
                    double ca = (dx * rupX + dy * rupY + dz * rupZ) / d;
                    double jag = 0.11 * (GmNoise3(dx * 0.45, dy * 0.45, dz * 0.45, seed + 51) - 0.5);
                    if (ca > rupCos + jag) { k.Set(x, y, z, 0); continue; }
                    double lon = Math.Atan2(dz, dx), rxz = Hyp(dx, dz);
                    bool rib = rxz > 2 && Math.Abs(Math.IEEERemainder(lon, Math.PI / 4)) * rxz < 0.9;
                    bool band = Math.Abs(dy) <= 1 || Math.Abs(Math.Abs(dy) - 8) < 0.6;
                    int m;
                    if (ca > Math.Cos(Rad(46))) m = k.R() < 0.4 ? drock : rustB;                   // scorched round the tear
                    else if (rib || band) m = steelR;
                    else
                    {
                        double n = GmNoise3(x * 0.18, y * 0.18, z * 0.18, seed + 52);
                        m = n > 0.56 ? rustA : n > 0.44 ? rustB : steel;
                    }
                    k.Set(x, y, z, m);
                }
        // petals of shell peeled outward round the tear
        {
            double ex = -rupZ, ey = 0, ez = rupX, el = Hyp(ex, ez); ex /= el; ez /= el;      // e1: horizontal, across the tear
            double fx = rupY * ez - rupZ * ey, fy = rupZ * ex - rupX * ez, fz = rupX * ey - rupY * ex;   // e2
            for (int p = 0; p < 9; p++)
            {
                double phi = p * Math.PI * 2 / 9 + k.R() * 0.4;
                double ox = Math.Cos(phi) * ex + Math.Sin(phi) * fx, oy = Math.Cos(phi) * ey + Math.Sin(phi) * fy, oz = Math.Cos(phi) * ez + Math.Sin(phi) * fz;
                double s26 = Math.Sin(Rad(31)), c26 = Math.Cos(Rad(31));
                double bxd = c26 * rupX + s26 * ox, byd = c26 * rupY + s26 * oy, bzd = c26 * rupZ + s26 * oz;
                double L = 3 + k.R() * 4;
                double sx = cx + bxd * (R - 0.5), sy = Yc + byd * (R - 0.5), sz = cz + bzd * (R - 0.5);
                double tx = sx + bxd * L * 0.8 + ox * L * 0.6, ty = sy + byd * L * 0.8 + oy * L * 0.6, tz = sz + bzd * L * 0.8 + oz * L * 0.6;
                double wx = -oy * bzd + oz * byd, wy = -oz * bxd + ox * bzd, wz = -ox * byd + oy * bxd;   // across the petal
                for (double w = -1.5; w <= 1.5; w += 0.75)
                    k.Line(sx + wx * w, sy + wy * w, sz + wz * w, tx + wx * w * 0.4, ty + wy * w * 0.4, tz + wz * w * 0.4, () => k.R() < 0.5 ? rustB : steel);
            }
        }

        // the cradle: three stalagmites out of the abyss floor, leaning in to
        // meet the sphere's underside and hold it up
        foreach (double ca in new[] { 40.0, 140, 270 })
        {
            double a = Rad(ca + (k.R() - 0.5) * 10), el = Rad(-58);
            double bx = cx + Math.Cos(a) * Sc(44), bz = cz + Math.Sin(a) * Sc(44);
            int fl = Floor((int)Math.Round(bx), (int)Math.Round(bz));
            double tr = R + 0.3;
            Spike(bx, fl - 5, bz, cx + Math.Cos(a) * Math.Cos(el) * tr, Yc + Math.Sin(el) * tr, cz + Math.Sin(a) * Math.Cos(el) * tr, 9.5, 2.8, 600 + (int)ca);
        }

        // inside: a deck across the middle, machinery below it, the control
        // area round the core on it, columns and the conduit up to the helix
        int ri2 = R - 2;
        for (int x = cx - ri2; x <= cx + ri2; x++)
            for (int z = cz - ri2; z <= cz + ri2; z++)
            {
                double rxz = Hyp(x - cx, z - cz);
                if (rxz * rxz > ri2 * ri2 - 1) continue;
                bool path = Math.Abs(x - cx + 0.5) < 3;
                if (!path && rxz > ri2 - 3 && k.Hash01(x * 3, z * 5) < 0.25) continue;      // holes at the rim, the gears show
                k.Set(x, Yc - 1, z, (x + z) % 4 == 0 ? steelR : rustA);
            }
        k.Gear(cx, Yc - 7, cz, 'y', ri2 - 3, 2, 6, 0.3, rust);
        k.Tube(cx, Yc - R + 1, cz, cx, Yc - 2, cz, 2.0, 2, steelF, false, 4);
        for (int i = 0; i < 4; i++)
        {
            double a = i * Math.PI / 2 + Math.PI / 4;
            k.Tube(cx, Yc - 10, cz, cx + Math.Cos(a) * (ri2 - 4.5), Yc - 10, cz + Math.Sin(a) * (ri2 - 4.5), 1.2, 1, rust, false);
        }
        for (int x = cx - 3; x <= cx + 3; x++)
            for (int z = cz - 3; z <= cz + 3; z++)
            {
                double rxz = Hyp(x - cx, z - cz);
                if (rxz > 3.4) continue;
                k.Set(x, Yc, z, rxz > 2.4 ? dark : obsP);
            }
        // The heart is the Lens's place now (0.73.0): the obsidian plinth
        // holds the Lens Housing of the Rustfall Great Machine mod (placed
        // ruined here when that mod is loaded; the mod puts it there itself
        // in older worlds and takes the old Spreading Devastation core away).
        // The conduit starts four blocks higher, so the Lens has room.
        k.Set(cx, Yc + 1, cz, obsP);
        int housing = k.Id("rustfallgreatmachine:lensholder-broken");
        if (housing != 0) k.Set(cx, Yc + 2, cz, housing);
        if (glowB != 0) for (int i = 0; i < 4; i++) k.Glow(cx + (i < 2 ? -3 : 3), Yc, cz + (i % 2 == 0 ? -1 : 1), glowB);
        // The control ring: eight two-high steel panels round the Lens, each
        // with an instrument fixed to the face toward it (and a lever bank
        // on top of every other one), and eight pillars behind them with a
        // gauge or valve on their inner face. "~" lets the clutter commit
        // fit each to the face it is on, so none hangs in the air.
        string[] panelFace = { "gauge1", "switch2-aged", "sightglass2-aged", "valve1-aged", "gauge3", "switch3-aged", "sightglass1-aged", "gauge4" };
        string[] panelTop = { "lever3-aged", "lever1-aged", "lever4-aged", "lever2-aged" };
        double[] consoleAz = { 130, 170, 210, 250, 290, 330, 10, 50 };
        (int X, int Z) Inward(int x, int z)
        {
            int dx = cx - x, dz = cz - z;
            return Math.Abs(dx) >= Math.Abs(dz) ? (x + Math.Sign(dx), z) : (x, z + Math.Sign(dz));
        }
        for (int i = 0; i < consoleAz.Length; i++)
        {
            double a = Rad(consoleAz[i]);
            int px = cx + (int)Math.Round(Math.Cos(a) * 6), pz = cz + (int)Math.Round(Math.Sin(a) * 6);
            k.Box(px, Yc, pz, px, Yc + 1, pz, steelR);
            var (fx, fz) = Inward(px, pz);
            k.Clutter(fx, Yc + 1, fz, "~" + panelFace[i], Math.Atan2(cz - fz, cx - fx));
            if (i % 2 == 0) k.Clutter(px, Yc + 2, pz, "~" + panelTop[i / 2], Math.Atan2(cz - pz, cx - px));
            int bx2 = cx + (int)Math.Round(Math.Cos(a) * 8), bz2 = cz + (int)Math.Round(Math.Sin(a) * 8);
            k.Box(bx2, Yc, bz2, bx2, Yc + 2, bz2, i % 2 == 0 ? rustA : steel);
            var (gx, gz) = Inward(bx2, bz2);
            k.Clutter(gx, Yc + 2, gz, "~" + (i % 2 == 0 ? "gauge2" : "valve3-aged"), Math.Atan2(cz - gz, cx - gx));
        }
        int colTop = Yc + (int)Math.Floor(Math.Sqrt(Math.Max(0, ri2 * ri2 - 9 * 9 - 2)));
        foreach (double ca2 in new[] { 45.0, 135, 225, 315 })
        {
            double a = Rad(ca2);
            int px = cx + (int)Math.Round(Math.Cos(a) * 9), pz = cz + (int)Math.Round(Math.Sin(a) * 9);
            k.Box(px, Yc, pz, px + 1, colTop, pz + 1, steelR);
            if (glowB != 0) k.Glow(px, colTop - 2, pz, glowB);
        }
        k.Tube(cx, Yc + 7, cz, cx, Yc + R - 1, cz, 1.5, 1, rust, false, 3);
        // the conduit's mouth over the Lens: a riveted collar
        for (int x = cx - 2; x <= cx + 2; x++)
            for (int z = cz - 2; z <= cz + 2; z++)
            {
                double rr = Hyp(x - cx, z - cz);
                if (rr > 1.2 && rr < 2.6) k.Set(x, Yc + 6, z, steelR);
            }
        k.Job.StructChests.Add((new BlockPos(cx + 3, Yc, cz - 5, k.Job.Dim), "golden-aged", RollLoot(k.Rand, MachineLoot, 4), "machine heart"));   // a golden chest has 4 slots

        // the door: a riveted frame proud of the shell on the south axis,
        // a passage through it, and a double iron door at its mouth
        int zf = cz + R;
        k.Box(cx - 3, Yc - 1, zf - 1, cx + 2, Yc + 3, zf + 1, steelR);
        k.Box(cx - 1, Yc, zf - 4, cx, Yc + 1, zf + 1, 0);
        k.Box(cx - 1, Yc - 1, zf - 4, cx, Yc - 1, zf + 1, rustA);
        if (doorLD != 0 && doorLU != 0 && doorRD != 0 && doorRU != 0)
        {
            k.Set(cx - 1, Yc, zf + 1, doorLD); k.Set(cx - 1, Yc + 1, zf + 1, doorLU);
            k.Set(cx, Yc, zf + 1, doorRD); k.Set(cx, Yc + 1, zf + 1, doorRU);
        }

        // ── 8. The blades ─────────────────────────────────────────────
        // The right blade's outline, traced from the energy sword picture
        // (u out from the centre line, v up from the sphere's centre, the
        // picture's handle where the sphere is), widened so the helix fits
        // between the blades. The left blade is its mirror. Each is a lens
        // in section: sharp glowing ice at the edges, packed ice in the
        // body, blue glass veins on its faces.
        var blade = new (double U, double V)[]
        {
            (7.4, 110), (9.3, 95), (11.6, 75.6), (13.6, 60), (17.1, 46.5), (22.9, 35.9), (28.1, 25.2), (31.4, 13.6), (31.8, 0),
            (17.1, -14), (19.0, -7.8), (20.5, 0), (20.3, 7.8), (17.8, 15.5), (13.6, 23.3), (8.7, 28.1), (5.8, 32.9),
            (4.8, 40.7), (5.0, 56.2), (5.6, 75.6), (6.2, 95)
        };
        for (int i = 0; i < blade.Length; i++) blade[i] = (1.1 * blade[i].U + 5, blade[i].V * vS);
        bool InBlade(double u, double v)
        {
            bool inside = false;
            for (int i = 0, j = blade.Length - 1; i < blade.Length; j = i++)
                if ((blade[i].V > v) != (blade[j].V > v) && u < (blade[j].U - blade[i].U) * (v - blade[i].V) / (blade[j].V - blade[i].V) + blade[i].U)
                    inside = !inside;
            return inside;
        }
        double EdgeDist(double u, double v)
        {
            double best = double.MaxValue;
            for (int i = 0, j = blade.Length - 1; i < blade.Length; j = i++)
            {
                double ax = blade[j].U, ay = blade[j].V, vx = blade[i].U - ax, vy = blade[i].V - ay, l2 = vx * vx + vy * vy;
                double t = Math.Clamp(((u - ax) * vx + (v - ay) * vy) / l2, 0, 1);
                best = Math.Min(best, Hyp(ax + vx * t - u, ay + vy * t - v));
            }
            return best;
        }
        double InnerU(double v)
        {
            for (double u = 0; u < 60; u += 0.5) if (InBlade(u, v)) return u;
            return 0;
        }
        int bladeCells = 0;
        foreach (int sgn in new[] { 1, -1 })
            for (int ui = 0; ui <= 46; ui++)
                for (int vi = (int)Math.Floor(-15 * vS); vi <= (int)Math.Ceiling(111 * vS); vi++)
                {
                    if (!InBlade(ui, vi)) continue;
                    double de = EdgeDist(ui, vi);
                    double halfT = Math.Min(4.6, 0.55 + 0.62 * de);
                    int x = cx + sgn * ui, y = Yc + vi;
                    double nv = GmFbm2(ui * 0.10, vi * 0.045 + sgn * 13, seed + 61);
                    bool veinHere = Math.Abs(nv - 0.5) < 0.045 || GmNoise2(ui * 0.07, vi * 0.03 + sgn * 7, seed + 62) > 0.70;
                    for (int dzc = -(int)Math.Ceiling(halfT); dzc <= (int)Math.Ceiling(halfT); dzc++)
                    {
                        if (Math.Abs(dzc) > halfT) continue;
                        bool face = Math.Abs(dzc) > halfT - 1.0;
                        int m = de < 1.2 ? iceGlow : face && veinHere ? vein : ice;
                        k.Set(x, y, cz + dzc, m);
                        bladeCells++;
                        if (face && veinHere && glowB != 0 && de > 3 && k.Hash01(x * 17 + dzc, y * 31) < 0.012) k.Glow(x, y, cz + dzc, glowB);
                    }
                }

        // struts from the sphere out to each blade's arch, the hilt
        foreach (int sgn in new[] { 1, -1 })
            foreach (double v in new[] { -9.0, 0, 12 })
            {
                double su = Math.Sqrt(Math.Max(0, R * R - v * v)) - 1, eu = InnerU(v) + 1.5;
                if (eu <= su + 1) continue;
                k.Tube(cx + sgn * su, Yc + v, cz, cx + sgn * eu, Yc + v, cz, v == 0 ? 2.4 : 1.6, 1, () => steelR, false, 4);
            }

        // ── 9. The double helix ───────────────────────────────────────
        {
            int y0 = Yc + R - 1, y1 = helixTop;
            double turns = k.OptD("turns", 3.0), rBase = 8.5;
            double ph = k.R() * Math.PI * 2;
            (double X, double Y, double Z) P(double t, int strand)
            {
                double r = rBase * Math.Pow(1 - t, 0.75) + 0.4;
                double a = ph + strand * Math.PI + t * turns * Math.PI * 2;
                return (cx + Math.Cos(a) * r, y0 + (y1 - y0) * t, cz + Math.Sin(a) * r);
            }
            double len = (y1 - y0) * 1.6 + turns * Math.PI * rBase * 2;
            int n = (int)(len * 1.6);
            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)n, tr = 1.8 * (1 - t) + 0.75;    // thin strands: a helix, not a cone
                for (int s = 0; s < 2; s++)
                {
                    var p = P(t, s);
                    int band = (int)p.Y % 6;
                    k.Ball(p.X, p.Y, p.Z, tr, () => band == 0 ? steelR : k.Rust());
                }
            }
            int rung = 0;
            for (double y = y0 + 4; y < y1 - 6; y += 6, rung++)
            {
                double t = (y - y0) / (y1 - y0);
                var a = P(t, 0); var b = P(t, 1);
                k.Line(a.X, a.Y, a.Z, b.X, b.Y, b.Z, steelF, 0.7 * (1 - t) + 0.45);
                if (glowB != 0 && rung % 3 == 0) k.Glow((int)Math.Round((a.X + b.X) / 2), (int)Math.Round(a.Y) + 1, (int)Math.Round((a.Z + b.Z) / 2), glowB);
            }
            k.Line(cx, y1 - 4, cz, cx, y1 + 3, cz, steelF);
            if (beacon != 0) k.Set(cx, y1 + 4, cz, beacon);
            else if (glowB != 0) k.Glow(cx, y1 + 4, cz, glowB);

            // the wavy brace between the blades just above the point, like
            // the little crystal piece between the sword's blades
            int yb = y1 + 9;
            double reach = InnerU(yb - Yc) + 1;
            if (reach > 2)
                for (double u = -reach; u <= reach; u += 0.5)
                    k.Ball(cx + u, yb + 2.2 * Math.Sin(u * 0.55), cz, 1.3, () => ice);
        }

        // ── 10. Chains ────────────────────────────────────────────────
        // Colossal links, alternating planes, walkable: from a lug on the
        // sphere's shoulder up to each floating island, and from each
        // island out to a bollard on the crater rim.
        void Chain(double x0, double y0, double z0, double x1, double y1, double z1, double sag, double linkR)
        {
            double dx = x1 - x0, dy = y1 - y0, dz = z1 - z0, len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 2) return;
            double ux = dx / len, uy = dy / len, uz = dz / len;
            double hl = Math.Max(0.001, Hyp(ux, uz));
            double p1x = -uz / hl, p1y = 0, p1z = ux / hl;
            double p2x = uy * p1z - uz * p1y, p2y = uz * p1x - ux * p1z, p2z = ux * p1y - uy * p1x;
            int links = (int)(len / (linkR * 1.5));
            for (int li = 0; li <= links; li++)
            {
                double f = li / (double)Math.Max(1, links);
                double lx = x0 + dx * f, ly = y0 + dy * f - Math.Sin(f * Math.PI) * sag, lz = z0 + dz * f;
                bool flat = (li & 1) == 0;
                for (double a = 0; a < Math.PI * 2; a += 0.2)
                {
                    double ca = Math.Cos(a) * linkR * 1.35, sb = Math.Sin(a) * linkR * 0.8;
                    k.Set((int)Math.Round(lx + ux * ca + (flat ? p1x : p2x) * sb), (int)Math.Round(ly + uy * ca + (flat ? p1y : p2y) * sb),
                        (int)Math.Round(lz + uz * ca + (flat ? p1z : p2z) * sb), k.Rust());
                }
            }
        }
        var chainSegs = new List<(double X0, double Y0, double Z0, double X1, double Y1, double Z1)>();
        var bollards = new List<(int X, int Z)>();
        foreach (var isl in isles)
        {
            double hx = Math.Cos(isl.Az), hz = Math.Sin(isl.Az);
            double lugX = cx + hx * (R + 1) * Math.Cos(Rad(35)), lugY = Yc + (R + 1) * Math.Sin(Rad(35)), lugZ = cz + hz * (R + 1) * Math.Cos(Rad(35));
            k.Ball(lugX, lugY, lugZ, 1.7, () => steelR);
            double ax = isl.X - hx * isl.Rho * 0.6, az2 = isl.Z - hz * isl.Rho * 0.6;
            k.Box((int)ax - 1, isl.Top - 2, (int)az2 - 1, (int)ax + 1, isl.Top + 1, (int)az2 + 1, steelR);
            Chain(lugX, lugY, lugZ, ax, isl.Top - 1, az2, 3, 2.3);
            chainSegs.Add((lugX, lugY, lugZ, ax, isl.Top - 1, az2));
            // tether to the rim
            double bx = isl.X + hx * isl.Rho * 0.6, bz = isl.Z + hz * isl.Rho * 0.6;
            int gx = cx + (int)Math.Round(hx * (rimR + 22)), gz = cz + (int)Math.Round(hz * (rimR + 22));
            int gg = Floor(gx, gz);
            if (gg >= sea)
            {
                k.Box(gx - 2, gg - 2, gz - 2, gx + 2, gg + 4, gz + 2, conc);
                k.Ball(gx, gg + 6, gz, 1.9, () => steelR);
                k.Box((int)bx - 1, isl.Top - 2, (int)bz - 1, (int)bx + 1, isl.Top + 1, (int)bz + 1, steelR);
                Chain(bx, isl.Top - 1, bz, gx, gg + 6, gz, 8, 2.0);
                chainSegs.Add((bx, isl.Top - 1, bz, gx, gg + 6, gz));
                bollards.Add((gx, gz));
            }
        }

        // ── 11. The walkway, its piers, the landing and the stair ────
        // Level with the door from the sphere to where the crater wall
        // comes up to meet it; a riveted deck six wide with rusted iron
        // railings, a truss under it, two stalagmite piers. From the
        // landing a stone stair climbs the crater wall to the rim.
        int zStart = zf + 2, zLand = zStart;
        while (zLand - cz < rimR + 20 && Floor(cx, zLand) < Yc - 2) zLand++;
        for (int z = zStart; z < zLand; z++)
        {
            for (int x = cx - 4; x <= cx + 3; x++)
            {
                bool edge = x == cx - 4 || x == cx + 3;
                if (!edge && (x == cx - 3 || x == cx + 2) && k.Hash01(x * 13, z * 7) < 0.05) continue;   // a missing plate
                k.Set(x, Yc - 1, z, edge ? rustB : (k.Hash01(x, z * 3) < 0.7 ? rustA : steel));
                for (int y = Yc; y <= Yc + 3; y++) k.Set(x, y, z, 0);
            }
            bool post = (z - zStart) % 8 == 0;
            foreach (int ex in new[] { cx - 4, cx + 3 })
            {
                if (post) { k.Set(ex, Yc, z, rustA); k.Set(ex, Yc + 1, z, rustA); }
                else if (fenceNS != 0 && k.Hash01(ex * 7, z * 11) > 0.12) k.Set(ex, Yc, z, fenceNS);
                k.Set(ex, Yc - 2, z, rustA);
            }
            k.Set(cx - 1, Yc - 6, z, rustB); k.Set(cx, Yc - 6, z, rustB);
            if ((z - zStart) % 8 == 0)
            {
                bool flip = ((z - zStart) / 8) % 2 == 0;
                k.Line(cx - 4, Yc - 2, z, cx - 0.5, Yc - 6, z + (flip ? 4 : -4), () => rustB);
                k.Line(cx + 3, Yc - 2, z, cx - 0.5, Yc - 6, z + (flip ? 4 : -4), () => rustB);
            }
        }
        foreach (int pd in new[] { 46, 108, 160 })
        {
            int pz = cz + Sc(pd);
            if (pz >= zLand - 6) continue;
            int fl = Floor(cx, pz);
            Spike(cx - 0.5 + (k.R() - 0.5) * 6, fl - 5, pz + (k.R() - 0.5) * 6, cx - 0.5, Yc - 7, pz, Yc - fl > 50 ? 10 : 7, 3.4, 500 + pd);
            k.Box(cx - 2, Yc - 7, pz - 1, cx + 1, Yc - 7, pz + 1, rustA);
        }
        // landing
        for (int x = cx - 6; x <= cx + 5; x++)
            for (int z = zLand - 1; z <= zLand + 8; z++)
            {
                int g = Floor(x, z);
                for (int y = Math.Min(g, Yc - 2) - 3; y <= Yc - 1; y++) k.Set(x, y, z, y == Yc - 1 ? conc : concR);
                for (int y = Yc; y <= Math.Max(g, Yc) + 4; y++) k.Set(x, y, z, 0);
            }
        k.Box(cx - 6, Yc, zLand - 1, cx - 5, Yc + 4, zLand, rustA);
        k.Box(cx + 4, Yc, zLand - 1, cx + 5, Yc + 4, zLand, rustA);
        // the stair: half a block up per block out, slab half steps
        {
            int half2 = 2 * Yc;
            for (int z = zLand + 9; z - cz < wallR - 12; z++)
            {
                half2++;
                int yb = half2 / 2;
                bool halfStep = (half2 & 1) == 1;
                int gAxis = Floor(cx, z);
                if (yb > gAxis + 1 && Floor(cx, z + 2) <= yb && Floor(cx, z + 4) <= yb) break;   // out on the rim
                for (int x = cx - 4; x <= cx + 3; x++)
                {
                    int g = Floor(x, z);
                    bool side = x == cx - 4 || x == cx + 3;
                    for (int y = Math.Min(g, yb - 2); y <= yb - 1; y++) k.Set(x, y, z, side ? concR : conc);
                    if (side) { if (yb > g) k.Set(x, yb, z, conc); }
                    else if (halfStep && slab != 0) k.Set(x, yb, z, slab);
                    for (int y = yb + (side && yb > g ? 1 : halfStep && !side ? 1 : 0); y <= Math.Max(g, yb) + 4; y++) k.Set(x, y, z, 0);
                }
            }
        }

        // ── 12. Debris under the tear ─────────────────────────────────
        for (int i = 0; i < 90; i++)
        {
            double a = Rad(10 + k.R() * 80), r = Sc(30 + k.R() * 110);
            int x = cx + (int)Math.Round(Math.Cos(a) * r), z = cz + (int)Math.Round(Math.Sin(a) * r);
            int f = Floor(x, z);
            if (f < sea - 1 || Math.Abs(x - cx) < 6) continue;
            int w = k.RI(1, 2);
            for (int ox = 0; ox < w; ox++) k.Set(x + ox, f + 1, z, k.R() < 0.5 ? rustA : steel);
        }

        // ── 14. The ruins above and the works below ───────────────────
        // GreatMachineSurface.cs: the collar, the throat ring, fallen metal,
        // fried buildings, machinery and halls inside the walls.
        // GreatMachineWorks.cs and GreatMachineVaults*.cs: the ten buried
        // works, their wings and pipes, buried machinery to the mantle.
        if (k.OptI("works", 1) != 0)
        {
            var gm = new GmCtx
            {
                K = k, Cx = cx, Cz = cz, Sea = sea, G = G, MapY = mapY, RimR = rimR, WallR = wallR, AbyssR = abyssR, Yc = Yc, SphR = R,
                ZStart = zStart, ZLand = zLand, Seed = seed, Floor = Floor, Carved = Carved, Chasms = chasmLines,
            };
            GmPalette(gm, ice, iceGlow, vein, glowB, lava);
            gm.Chains.AddRange(chainSegs);
            foreach (var b in bollards) gm.Keep.Add((b.X, b.Z, 9));
            foreach (var ft in isleFeet) gm.Keep.Add((ft.X, ft.Z, 14));
            GmSurface(gm);
            GmDepths(gm);
        }

        // ── 13. The red fog ───────────────────────────────────────────
        int fogR = Sc(k.OptD("fog", 640));
        if (fogR > 0) RegisterRedFogZone(k.Job.Dim, cx, cz, fogR, Sc(k.OptD("fogfade", 200)));

        sapi.Logger.Notification("[landmassgenerator] great machine built at HUD {0}, {1}: abyss {2} columns, chasms {3} cells, blades {4} cells, {5} floating islands, walkway {6} long, {7} cell(s) left as rock beside natural water",
            cx - sapi.WorldManager.MapSizeX / 2, cz - sapi.WorldManager.MapSizeZ / 2, abyssCells, chasmCells, bladeCells, isles.Count, zLand - zStart, keptRock);
    }

    // The palette of the surface ruins and the works below. A code the
    // game lacks is logged and falls back to the nearest thing it has, so
    // a shell is never left with a hole.
    private void GmPalette(GmCtx g, int ice, int iceGlow, int vein, int glowB, int lava)
    {
        var k = g.K;
        int P(int fb, params string[] codes)
        {
            foreach (string c in codes) { int i = k.Id(c); if (i != 0) return i; }
            k.Need(codes[0]);
            return fb;
        }
        g.Basalt = P(0, "rock-basalt");
        g.Granite = P(g.Basalt, "rock-granite");
        g.Andes = P(g.Basalt, "rock-andesite");
        g.Obsid = P(g.Basalt, "rock-obsidian");
        g.Drock = P(g.Basalt, "drock");
        g.Steel = P(g.Basalt, "metalblock-new-plain-steel", "metalblock-new-plain-iron");
        g.SteelR = P(g.Steel, "metalblock-new-riveted-steel", "metalblock-new-riveted-iron");
        g.RustA = k.RustA != 0 ? k.RustA : g.SteelR;
        g.RustB = k.RustB != 0 ? k.RustB : g.RustA;
        g.Iron = P(g.Steel, "metalblock-new-plain-iron");
        g.IronR = P(g.SteelR, "metalblock-new-riveted-iron");
        g.Copper = P(g.RustA, "metalblock-new-plain-copper", "metalblock-new-riveted-copper");
        g.CopperC = P(g.Copper, "metalblock-corroded-plain-copper", "metalblock-corroded-riveted-copper");
        g.Brass = P(g.Copper, "metalblock-new-riveted-brass", "metalblock-corroded-riveted-brass");
        g.Bronze = P(g.Copper, "metalblock-new-plain-tinbronze", "metalblock-new-plain-bismuthbronze", "metalblock-new-riveted-tinbronze");
        g.Gold = P(g.Brass, "metalblock-new-plain-gold", "metalblock-new-riveted-gold");
        g.BrickG = P(g.Andes, "stonebricks-granite", "stonebricks-andesite");
        g.BrickA = P(g.BrickG, "agedstonebricks-granite");
        g.BrickC = P(g.BrickG, "crackedstonebricks-granite", "crackedstonebricks-andesite");
        g.BrickB = P(g.BrickG, "stonebricks-basalt", "stonebricks-andesite");
        g.PolG = P(g.BrickG, "rockpolished-granite", "rockpolished-andesite");
        g.PolOld = P(g.PolG, "polishedrockold-full-granite");
        g.PolOldC = P(g.PolOld, "polishedrockold-cobbled-granite");
        g.PolB = P(g.PolG, "rockpolished-basalt");
        g.SlabG = P(g.BrickG, "stonebrickslab-granite-down-free", "stonebrickslab-andesite-down-free", "cobblestoneslab-andesite-down-free");
        g.PlanksA = P(g.BrickG, "planks-aged-ns", "planks-oak-ns");
        g.PlanksAW = P(g.PlanksA, "planks-aged-we");
        g.PlanksE = P(g.PlanksAW, "planks-agedebony-we");
        g.LogV = P(g.PlanksA, "debarkedlog-veryaged-ud", "log-placed-aged-ud");
        g.ColBot = P(g.LogV, "carvedlog-acacia-aged-bottom-fancy-ud");
        g.ColMid = P(g.LogV, "carvedlog-acacia-aged-middle-fancy-ud");
        g.ColTop = P(g.LogV, "carvedlog-acacia-aged-top-fancy-ud");
        g.Glass = P(g.SteelR, "glass-quartz", "glass-plain");
        g.GlassBlue = vein != 0 ? vein : g.Glass;
        g.Ice = ice != 0 ? ice : g.Glass;
        g.IceGlow = iceGlow != 0 ? iceGlow : g.Ice;
        g.RefrG = P(g.BrickG, "refractorybricks-good-tier3", "refractorybricks-good-tier2");
        g.RefrD = P(g.RefrG, "refractorybricks-damaged-tier3");
        // fired clay brick; the damaged kind (and damaged refractory brick)
        // is not a full block, so it is kept out of every underground shell
        g.BurntBrick = P(g.BrickC, "claybricks-good-fire");
        g.BurntBrickD = P(g.BurntBrick, "claybricks-damaged-fire");
        g.Charred = P(g.Drock, "burnedplanks-normal-charred-ud", "burnedplanks-normal-blackened-ud");
        g.CharredW = P(g.Charred, "burnedplanks-narrow-charred-we", "burnedplanks-normal-charred-we");
        g.Blackened = P(g.Charred, "burnedplanks-normal-blackened-ud");
        g.Scorched = P(g.Charred, "burnedplanks-narrow-scorched-ud");
        g.GlowB = glowB;
        g.GlowG = P(glowB, "landmassgenerator:ghostlight-green");
        g.Lava = lava;
        g.Water = P(0, "water-still-7");
        g.FenceNS = P(g.SteelR, "ironfence-base-ns");
        g.FenceEW = P(g.FenceNS, "ironfence-base-ew");
        g.Spider = P(0, "spiderweb");
        g.Gears1 = P(g.RustA, "loosegears-1");
        g.Gears3 = P(g.Gears1, "loosegears-3");
        g.PartsS = P(g.Gears1, "metalpartpile-small");
        g.PartsM = P(g.PartsS, "metalpartpile-medium");
        g.Stones = P(g.PartsS, "loosestones-basalt-free");
        g.Boulders = P(g.Stones, "looseboulders-basalt-free");
    }

    // Value noise for the Great Machine (0..1, smooth, deterministic per salt).
    private static double GmHash(int x, int y, int z, int salt)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177 + salt * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216.0;
        }
    }

    private static double GmNoise3(double x, double y, double z, int salt)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y), z0 = (int)Math.Floor(z);
        double fx = x - x0, fy = y - y0, fz = z - z0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
        double L(double a, double b, double t) => a + (b - a) * t;
        double c00 = L(GmHash(x0, y0, z0, salt), GmHash(x0 + 1, y0, z0, salt), fx);
        double c10 = L(GmHash(x0, y0 + 1, z0, salt), GmHash(x0 + 1, y0 + 1, z0, salt), fx);
        double c01 = L(GmHash(x0, y0, z0 + 1, salt), GmHash(x0 + 1, y0, z0 + 1, salt), fx);
        double c11 = L(GmHash(x0, y0 + 1, z0 + 1, salt), GmHash(x0 + 1, y0 + 1, z0 + 1, salt), fx);
        return L(L(c00, c10, fy), L(c01, c11, fy), fz);
    }

    private static double GmNoise2(double x, double z, int salt) => GmNoise3(x, 0.5, z, salt);

    private static double GmFbm2(double x, double z, int salt)
        => (GmNoise2(x, z, salt) + 0.5 * GmNoise2(x * 2.03, z * 2.03, salt + 1) + 0.25 * GmNoise2(x * 4.1, z * 4.1, salt + 2)) / 1.75;
}
