using System;
using System.Collections.Generic;
using System.Linq;

namespace LandmassGenerator;

// The ten works, continued (GreatMachineVaults.cs): the Archive and the
// Laboratories, high under the plateau.
public partial class LandmassGeneratorModSystem
{
    // A straight hallway's dressing: a darker runner, a rusted pipe with
    // lumi lamps along the ceiling's corner, gas lamps on alternate walls.
    // Local: along u (alongU) or w from a to b at cross position c, floor
    // v0, the hall 3 wide (c-1..c+1) and 4 tall.
    private static void GmHallDecor(GmCtx g, GmFrame f, int a, int b, int c, int v0, bool alongU)
    {
        if (b < a) (a, b) = (b, a);
        for (int s = a; s <= b; s++)
        {
            int u = alongU ? s : c, w = alongU ? c : s;
            int u1 = alongU ? s : c + 1, w1 = alongU ? c + 1 : s;
            GS(g, f, u, v0 - 1, w, g.PolB);
            GC(g, f, u1, v0 + 3, w1, (s - a) % 7 == 3 ? "jonas-lamp|lightsource/on/lumi-pipe-h-lamp" : "pipe-veryrusted-h-EW", alongU ? 1 : 0, alongU ? 0 : 1);
            if ((s - a) % 8 == 4)
            {
                int side = ((s - a) / 8) % 2 == 0 ? -1 : 1;
                int lu = alongU ? s : c + side, lw = alongU ? c + side : s;
                GL(g, f, lu, v0 + 2, lw, "gas-wall1", alongU ? 0 : -side, alongU ? -side : 0);
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // 3. THE ARCHIVE
    // ═════════════════════════════════════════════════════════════════════
    // Brainstorm: the engineers wrote down everything: the drawings, the
    // readings, the letters home, the confessions. Their archive is a
    // square hall 61 across and 40 tall in the manner of the Resonance
    // Archives: aged granite brick, old polished floors, carved acacia
    // columns. Three galleries 8 deep run round all four walls at 10, 20
    // and 30, each lined with double-sided book stacks in rows, and a
    // spiral stair in each corner joins them. Over the open middle hangs a
    // great orrery: a glowing sphere inside three tilted rings of brass,
    // copper and gold with their planets, on an axle from the ceiling. The
    // floor beneath is a compass rose set with reading tables, lecterns,
    // globes and armillaries. A wing inward holds the scroll vaults, the
    // map room, the catalogue office and the restorers' workshop.
    //
    // Frame: hall u -30..30, w -30..30, v 0..40; galleries where
    // max(|u|, |w|) >= 22, decks at v 9, 19, 29; corner stairs at
    // (+-26, +-26); wing hall at w -38.
    private void GmArchive(GmCtx g, GmFrame f, GmWork work)
    {
        const int HS = 30, H = 40, Gal = 22;
        int[] tiers = { 0, 10, 20, 30 };
        var shell = (Func<int, int, int, int>)((x, y, z) =>
        {
            if (y < f.Y(0)) return g.PolOld;
            if (y > f.Y(H)) return ((int)Math.Round(f.U(x, z)) % 6 + 6) % 6 == 0 || ((int)Math.Round(f.Wd(x, z)) % 6 + 6) % 6 == 0 ? g.PlanksE : g.BrickA;
            return (y - f.Y(0)) % 10 == 9 ? g.BrickB : g.K.Hash01(x * 7 + y, z * 3 - y) < 0.2 ? g.BrickC : g.BrickA;
        });
        GmRoom(g, f, -HS, 0, -HS, HS, H, HS, 3, shell);
        GmWing(g, f, -28, 28, -38, 0, 9, 0, new[] { "archive", "archive", "drafting", "office", "optics", "workshop", "archive", "store" }, g.BrickA, g.BrickC, g.PolOld);
        foreach (int pu in new[] { -16, 0, 16 })
            GmRoom(g, f, pu, 0, -36, pu + 1, 2, -31, 1, GmMasonry(g, f.Y(0), f.Y(2), g.PolOld, g.BrickA, g.BrickC, g.BrickB, g.BrickA));
        var door = GmMasonry(g, f.Y(0), f.Y(H), g.IronR, g.BrickB, g.BrickB, g.BrickB, g.BrickB);
        GmRoom(g, f, -2, 30, HS, 2, 34, HS + 4, 1, door);
        work.Ports["surface"] = f.Port(0, 30, HS + 5, 0, 1);
        work.Ports["down"] = f.Port(31, 0, -38, 1, 0);
        work.Ports["down2"] = f.Port(-31, 0, -38, -1, 0);
        work.Ports["down3"] = f.Port(HS + 4, 0, 0, 1, 0);
        work.Ports["down4"] = f.Port(-HS - 4, 0, 0, -1, 0);
        var stairs = new[] { (U: 26, W: 26), (U: -26, W: 26), (U: 26, W: -26), (U: -26, W: -26) };
        foreach (var st in stairs)
        {
            var ent = tiers.Select((t, i) => (A: f.H(i % 2 == 0 ? -Math.Sign(st.U) : 0, i % 2 == 0 ? 0 : -Math.Sign(st.W)), Y: f.Y(t))).ToList();
            GmShaft(g, f.X(st.U, st.W), f.Z(st.U, st.W), ent, 3.5, null, false, false);
        }

        g.Details.Add(() =>
        {
            bool NearStair(int u, int w) => stairs.Any(st => (u - st.U) * (u - st.U) + (w - st.W) * (w - st.W) <= 13);
            // ── the galleries ──
            for (int ti = 1; ti < tiers.Length; ti++)
            {
                int tv = tiers[ti];
                for (int u = -HS; u <= HS; u++)
                    for (int w = -HS; w <= HS; w++)
                    {
                        int m = Math.Max(Math.Abs(u), Math.Abs(w));
                        if (m < Gal || NearStair(u, w)) continue;
                        GS(g, f, u, tv - 1, w, (u + w) % 5 == 0 ? g.PlanksE : g.PlanksA);
                        if (m == Gal)
                        {
                            GRail(g, f, u, tv, w, Math.Abs(u) == Gal ? 'w' : 'u');
                            GS(g, f, u, tv - 2, w, g.PlanksE);          // a beam under the deck's edge
                        }
                    }
            }
            // carved columns at the gallery edge
            for (int s = -Gal; s <= Gal; s += 8)
                foreach (var (u, w) in new[] { (s, Gal), (s, -Gal), (Gal, s), (-Gal, s) })
                    for (int v = 0; v < H; v++) GS(g, f, u, v, w, v == 0 ? g.ColBot : v == H - 1 ? g.ColTop : g.ColMid);
            // ── the stacks: double-sided rows off each wall ──
            string[] fronts = { "bookshelf-ruined-full1", "bookshelf-ruined-full3", "bookshelf-ruined-full5", "bookshelf-ruined-full7", "bookshelf-ruined-full9",
                "bookshelf-ruined-full11", "bookshelf-ruined-full13", "bookshelf-ruined-full15", "bookshelf-ruined-full17", "bookshelf-ruined-empty1",
                "bookshelf-ruined-empty4", "bookshelf-standard2", "bookshelf-standard5" };
            foreach (int tv in tiers)
                for (int s = -18; s <= 18; s += 4)
                    for (int d = 0; d < 5; d++)
                        for (int h = 0; h < 2; h++)
                        {
                            int dd = HS - d;
                            g.Shelf(f.X(s, dd), f.Y(tv + h), f.Z(s, dd), g.Pick(fronts), g.Pick(fronts), f.H(1, 0));
                            g.Shelf(f.X(s, -dd), f.Y(tv + h), f.Z(s, -dd), g.Pick(fronts), g.Pick(fronts), f.H(1, 0));
                            g.Shelf(f.X(dd, s), f.Y(tv + h), f.Z(dd, s), g.Pick(fronts), g.Pick(fronts), f.H(0, 1));
                            g.Shelf(f.X(-dd, s), f.Y(tv + h), f.Z(-dd, s), g.Pick(fronts), g.Pick(fronts), f.H(0, 1));
                        }
            // between the stacks: book piles, ladders, big books
            foreach (int tv in tiers)
                for (int s = -16; s <= 16; s += 4)
                    foreach (var (u, w, du, dw) in new[] { (s, HS, 0, -1), (s, -HS, 0, 1), (HS, s, -1, 0), (-HS, s, 1, 0) })
                    {
                        double r0 = g.R();
                        string t = r0 < 0.3 ? "bookshelves/bookpile-aged" + g.RI(1, 5) : r0 < 0.45 ? "ladder-aged" + g.RI(1, 5) + "-noclimb"
                            : r0 < 0.55 ? "bookshelves/large-book-pile" + g.RI(1, 3) : null;
                        if (t != null) GC(g, f, u, tv, w, t, du, dw);
                    }
            // ── the orrery ──
            {
                int ov = 20;
                for (int u = -4; u <= 4; u++)
                    for (int v = -4; v <= 4; v++)
                        for (int w = -4; w <= 4; w++)
                        {
                            double d = Math.Sqrt(u * u + v * v + w * w);
                            if (d <= 2.2) GG(g, f, u, ov + v, w);
                            else if (d <= 3.4) GS(g, f, u, ov + v, w, g.Glass);
                        }
                GT(g, f, 0, ov + 3.5, 0, 0, H, 0, 0.6, (x, y, z, d) => g.SteelR);
                GT(g, f, 0, ov - 3.5, 0, 0, 9, 0, 0.6, (x, y, z, d) => g.SteelR);
                var rings = new[] { (R: 8.0, Tilt: 0.35, Spin: 0.0, Mat: g.Brass), (R: 11.0, Tilt: -0.6, Spin: 1.1, Mat: g.Copper), (R: 14.5, Tilt: 0.18, Spin: 2.3, Mat: g.Gold) };
                foreach (var ring in rings)
                {
                    double ct = Math.Cos(ring.Tilt), st2 = Math.Sin(ring.Tilt), cs = Math.Cos(ring.Spin), ss = Math.Sin(ring.Spin);
                    (double U, double V, double W) P(double a)
                    {
                        double x0 = Math.Cos(a) * ring.R, z0 = Math.Sin(a) * ring.R;
                        double y1 = z0 * st2, z1 = z0 * ct;                       // tilt about u
                        return (x0 * cs - z1 * ss, y1, x0 * ss + z1 * cs);        // then turn about v
                    }
                    for (double a = 0; a < Math.PI * 2; a += 0.04)
                    {
                        var p = P(a);
                        GS(g, f, (int)Math.Round(p.U), ov + (int)Math.Round(p.V), (int)Math.Round(p.W), ring.Mat);
                    }
                    for (int pl = 0; pl < 3; pl++)
                    {
                        var p = P(pl * 2.1 + ring.Spin);
                        int pm = pl == 0 ? g.Steel : pl == 1 ? g.CopperC : g.Bronze;
                        for (int du = -1; du <= 1; du++)
                            for (int dv = -1; dv <= 1; dv++)
                                for (int dw = -1; dw <= 1; dw++)
                                    if (du * du + dv * dv + dw * dw <= 2) GS(g, f, (int)Math.Round(p.U) + du, ov + (int)Math.Round(p.V) + dv, (int)Math.Round(p.W) + dw, pm);
                    }
                    var q1 = P(Math.PI / 2); var q2 = P(-Math.PI / 2);
                    GT(g, f, 0, ov, 0, q1.U, ov + q1.V, q1.W, 0.4, (x, y, z, d) => g.Steel);
                    GT(g, f, 0, ov, 0, q2.U, ov + q2.V, q2.W, 0.4, (x, y, z, d) => g.Steel);
                }
            }
            // ── the reading floor: a compass rose, tables, lecterns ──
            for (int u = -Gal + 1; u < Gal; u++)
                for (int w = -Gal + 1; w < Gal; w++)
                {
                    double q = Math.Sqrt(u * u + w * w), a = Math.Atan2(w, u);
                    bool ringLine = Math.Abs(q - 6) < 0.5 || Math.Abs(q - 13) < 0.5 || Math.Abs(q - 19) < 0.5;
                    bool ray = q < 19 && Math.Abs(Math.IEEERemainder(a, Math.PI / 4)) * q < 0.6;
                    if (ringLine || ray) GS(g, f, u, -1, w, q < 6.5 ? g.Gold : g.PolB);
                }
            for (int u = -16; u <= 16; u += 4)
                foreach (int w in new[] { -16, -9, 9, 16 })
                {
                    GC(g, f, u, 0, w, g.Pick(new[] { "table-long-with-cloth-purple", "table-long-with-cloth-red", "table-long", "table-long-with-accessories" }), 1, 0);
                    GC(g, f, u, 0, w - 1, "chair-aged", 0, 1);
                    GC(g, f, u, 0, w + 1, "chair-aged", 0, -1);
                }
            foreach (var (u, w) in new[] { (-6, -3), (6, 3), (3, -6), (-3, 6) })
                GC(g, f, u, 0, w, g.Pick(new[] { "bookshelves/lecturn-aged-book-opened", "bookshelves/bookstand-verylarge-book-open", "globe2", "armillary1", "globe6", "armillary3" }), -u, -w);
            // ── light ──
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                GL(g, f, (int)Math.Round(Math.Cos(a) * 16), 0, (int)Math.Round(Math.Sin(a) * 16), "lumi-floor" + (i % 2 == 0 ? "8" : "10"), -Math.Cos(a), -Math.Sin(a));
            }
            foreach (int tv in tiers)
                for (int s = -20; s <= 20; s += 8)
                {
                    GL(g, f, s + 2, tv + 3, HS, "gas-wall2", 0, -1);
                    GL(g, f, s + 2, tv + 3, -HS, "gas-wall2", 0, 1);
                    GL(g, f, HS, tv + 3, s + 2, "gas-wall1", -1, 0);
                    GL(g, f, -HS, tv + 3, s + 2, "gas-wall1", 1, 0);
                    int cv = tv == 30 ? H : tv + 8;
                    GL(g, f, s, cv, 26, "gas-ceiling1", 0, 1);
                    GL(g, f, s, cv, -26, "gas-ceiling1", 0, 1);
                }
            for (int i = 0; i < 40; i++)
            {
                int u = g.RI(-Gal + 2, Gal - 2), w = g.RI(-Gal + 2, Gal - 2);
                if (u * u + w * w < 30) continue;
                GC(g, f, u, 0, w, g.Pick(new[] { "bookshelves/bookpile-aged1", "bookshelves/bookpile-aged3", "bookshelves/large-book-open", "candlestubs-bunch2",
                    "bookshelves/large-book-closed", "rubble-wood2", "bookshelves/scrollrack-ruined1" }), g.R() - 0.5, g.R() - 0.5);
            }
            g.K.Job.StructChests.Add((new Vintagestory.API.MathTools.BlockPos(f.X(-20, 24), f.Y(30), f.Z(-20, 24), g.K.Job.Dim), "normal-aged",
                RollLoot(g.K.Rand, MachineLoot, 5 + g.RI(0, 2)), "archive"));
        });
    }

    // ═════════════════════════════════════════════════════════════════════
    // 4. THE LABORATORIES
    // ═════════════════════════════════════════════════════════════════════
    // Brainstorm: here they learned to make the machine sing. A domed round
    // test chamber 27 across sits at the centre: a column of glass on a
    // steel plinth with a green light alive inside it, copper coils wound
    // round it, conduits from its cap up into the dome, benches and
    // consoles in a ring facing it, and windows in its wall onto a ring
    // corridor. Four halls run out from the ring to a square corridor round
    // the whole, and the quarters between them hold twelve laboratories:
    // chemistry with its flasks and reagent shelves, optics with its
    // armillaries, a specimen vault of glass tanks and bones, a hazmat
    // dressing room, a mechanical workshop, a drafting room, the stores,
    // an infirmary, a valve room and the director's office.
    //
    // Frame: chamber round (0, 0) radius 13, v 0..14, dome to 24; ring
    // corridor radius 17..19.5; axis halls out to the square corridor at
    // max(|u|, |w|) 28..30.
    private void GmLaboratories(GmCtx g, GmFrame f, GmWork work)
    {
        var tile = GmMasonry(g, f.Y(0), f.Y(4), g.PolG, g.BrickG, g.BrickC, g.BrickB, g.BrickG);
        // the chamber and its riveted shell
        GmCylW(g, f.Xd(0, 0), f.Zd(0, 0), 13, f.Y(0), f.Y(14), 10, 3,
            (x, y, z) => y < f.Y(0) ? g.PolB : (y - f.Y(0)) % 5 == 0 ? g.SteelR : g.K.Hash01(x, z + y * 7) < 0.3 ? g.CopperC : g.IronR);
        // the ring corridor
        double rc0 = 17, rc1 = 19.5;
        GmSpace(g, f.Ox - 20, f.Y(0), f.Oz - 20, f.Ox + 20, f.Y(4), f.Oz + 20, 1, (x, y, z, gr) =>
        {
            double q = Math.Sqrt((x - f.Ox) * (double)(x - f.Ox) + (z - f.Oz) * (double)(z - f.Oz));
            return q >= rc0 - gr && q <= rc1 + gr && y >= f.Y(0) - gr && y <= f.Y(4) + gr;
        }, tile);
        // the four halls out, the doors into the chamber, the square corridor
        foreach (var (du, dw) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            if (du != 0) { GmRoom(g, f, du * 19, 0, -1, du * 28, 3, 1, 1, tile); GmRoom(g, f, du * 12, 0, -1, du * 17, 3, 1, 1, tile); }
            else { GmRoom(g, f, -1, 0, dw * 19, 1, 3, dw * 28, 1, tile); GmRoom(g, f, -1, 0, dw * 12, 1, 3, dw * 17, 1, tile); }
        }
        GmRoom(g, f, -30, 0, 28, 30, 3, 30, 1, tile);
        GmRoom(g, f, -30, 0, -30, 30, 3, -28, 1, tile);
        GmRoom(g, f, 28, 0, -27, 30, 3, 27, 1, tile);
        GmRoom(g, f, -30, 0, -27, -28, 3, 27, 1, tile);
        // twelve labs, three in each quarter, each with two doors
        string[] kinds = { "lab", "lab", "specimens", "optics", "hazmat", "workshop", "drafting", "store", "lab", "infirmary", "valves", "office" };
        int ki = 0;
        var labs = new[]
        {
            (U0: 3, U1: 13, W0: 21, W1: 26, D1U: 8, D1W: 27, D2U: 2, D2W: 23),
            (U0: 21, U1: 26, W0: 3, W1: 13, D1U: 27, D1W: 8, D2U: 23, D2W: 2),
            (U0: 16, U1: 26, W0: 16, W1: 26, D1U: 21, D1W: 27, D2U: 27, D2W: 21),
        };
        foreach (int su in new[] { -1, 1 })
            foreach (int sw in new[] { -1, 1 })
                foreach (var lb in labs)
                {
                    int u0 = su * lb.U0, u1 = su * lb.U1, w0 = sw * lb.W0, w1 = sw * lb.W1;
                    GmRoom(g, f, u0, 0, w0, u1, 4, w1, 1, tile);
                    int d1u = su * lb.D1U, d1w = sw * lb.D1W, d2u = su * lb.D2U, d2w = sw * lb.D2W;
                    // a door is two wide along the wall it pierces
                    bool d1AlongU = Math.Abs(lb.D1W) == 27, d2AlongU = Math.Abs(lb.D2W) == 2 || Math.Abs(lb.D2W) == 27;
                    GmRoom(g, f, d1u, 0, d1w, d1u + (d1AlongU ? su : 0), 2, d1w + (d1AlongU ? 0 : sw), 1, tile);
                    GmRoom(g, f, d2u, 0, d2w, d2u + (d2AlongU ? su : 0), 2, d2w + (d2AlongU ? 0 : sw), 1, tile);
                    string kind = kinds[ki++ % kinds.Length];
                    int a0 = Math.Min(u0, u1), a1 = Math.Max(u0, u1), b0 = Math.Min(w0, w1), b1 = Math.Max(w0, w1);
                    var doors = new List<(int U, int W)> { (d1u, d1w), (d1u + su, d1w), (d1u, d1w - sw), (d2u, d2w), (d2u + su, d2w), (d2u - su, d2w), (d2u, d2w + sw) };
                    g.Details.Add(() => GmFurnish(g, f, a0, a1, b0, b1, 0, 4, kind, doors));
                }
        var door = GmMasonry(g, f.Y(0), f.Y(4), g.IronR, g.SteelR, g.SteelR, g.SteelR, g.SteelR);
        GmRoom(g, f, -1, 0, -34, 1, 3, -30, 1, door);
        GmRoom(g, f, -1, 0, 30, 1, 3, 34, 1, door);
        work.Ports["down"] = f.Port(0, 0, -35, 0, -1);
        work.Ports["down2"] = f.Port(32, 0, 0, 1, 0);
        work.Ports["down3"] = f.Port(-32, 0, 0, -1, 0);
        work.Ports["surface"] = f.Port(0, 0, 35, 0, 1);

        g.Details.Add(() =>
        {
            // ── the column: plinth, glass, a green light, coils, conduits ──
            for (int u = -5; u <= 5; u++)
                for (int w = -5; w <= 5; w++)
                {
                    double q = Math.Sqrt(u * u + w * w);
                    if (q <= 5.2) GS(g, f, u, 0, w, q > 4.2 ? g.SteelR : g.Steel);
                    if (q <= 4.2) GS(g, f, u, 15, w, q > 3.2 ? g.SteelR : g.Steel);
                    for (int v = 1; v <= 14; v++)
                    {
                        if (q <= 1.0) GG(g, f, u, v, w, g.GlowG);
                        else if (q <= 2.6) GS(g, f, u, v, w, g.Glass);
                        else if (q <= 3.6 && v % 3 == 0) GS(g, f, u, v, w, g.Copper);
                    }
                }
            for (int i = 0; i < 4; i++)
            {
                double a = i * Math.PI / 2 + Math.PI / 4;
                GT(g, f, Math.Cos(a) * 3, 16, Math.Sin(a) * 3, Math.Cos(a) * 1.5, 24, Math.Sin(a) * 1.5, 0.8, (x, y, z, d) => y % 3 == 0 ? g.SteelR : g.Copper);
            }
            // benches and consoles round it, lamps among them, windows out
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4, b = a + Math.PI / 8;
                GC(g, f, (int)Math.Round(Math.Cos(a) * 8), 0, (int)Math.Round(Math.Sin(a) * 8), "chair-long", -Math.Cos(a), -Math.Sin(a));
                GD(g, f, (int)Math.Round(Math.Cos(b) * 11), 0, (int)Math.Round(Math.Sin(b) * 11),
                    g.Pick(new[] { "switch1-aged", "lever2-aged", "gauge2", "sightglass4-aged", "valve3-aged", "switch4-aged" }), -Math.Cos(b), -Math.Sin(b));
                if (i % 2 == 0) GL(g, f, (int)Math.Round(Math.Cos(b) * 12), 0, (int)Math.Round(Math.Sin(b) * 12), "lumi-floor3", -Math.Cos(b), -Math.Sin(b));
                for (double q = 12.6; q <= 17.4; q += 0.5)
                    for (int v = 2; v <= 3; v++)
                        GS(g, f, (int)Math.Round(Math.Cos(b) * q), v, (int)Math.Round(Math.Sin(b) * q), g.Glass);
            }
            // glowing ribs up the dome
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                for (int v = 15; v <= 23; v++)
                {
                    double hy = (v - 14) / 10.0, rr = 13 * Math.Sqrt(Math.Max(0, 1 - hy * hy)) + 0.6;
                    GG(g, f, (int)Math.Round(Math.Cos(a) * rr), v, (int)Math.Round(Math.Sin(a) * rr));
                }
            }
            // ── the corridors ──
            for (int i = 0; i < 24; i++)
            {
                double a = i * Math.PI / 12;
                GS(g, f, (int)Math.Round(Math.Cos(a) * 18.2), -1, (int)Math.Round(Math.Sin(a) * 18.2), g.PolB);
                if (i % 3 == 1) GL(g, f, (int)Math.Round(Math.Cos(a) * 19.4), 2, (int)Math.Round(Math.Sin(a) * 19.4), "gas-wall2", -Math.Cos(a), -Math.Sin(a));
            }
            GmHallDecor(g, f, 19, 28, 0, 0, true); GmHallDecor(g, f, -28, -19, 0, 0, true);
            GmHallDecor(g, f, 19, 28, 0, 0, false); GmHallDecor(g, f, -28, -19, 0, 0, false);
            GmHallDecor(g, f, -30, 30, 29, 0, true); GmHallDecor(g, f, -30, 30, -29, 0, true);
            GmHallDecor(g, f, -27, 27, 29, 0, false); GmHallDecor(g, f, -27, 27, -29, 0, false);
            g.K.Job.StructChests.Add((new Vintagestory.API.MathTools.BlockPos(f.X(-10, 0), f.Y(0), f.Z(-10, 0), g.K.Job.Dim), "normal-aged",
                RollLoot(g.K.Rand, MachineLoot, 5 + g.RI(0, 2)), "laboratories"));
        });
    }
}
