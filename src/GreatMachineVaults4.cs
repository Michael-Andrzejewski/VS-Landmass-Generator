using System;
using System.Collections.Generic;
using System.Linq;

namespace LandmassGenerator;

// The ten works, continued (GreatMachineVaults.cs): the Resonator, the
// Workers' Quarters and the Deep Bore, and the wings of offices and stores.
public partial class LandmassGeneratorModSystem
{
    // ═════════════════════════════════════════════════════════════════════
    // 8. THE RESONATOR
    // ═════════════════════════════════════════════════════════════════════
    // Brainstorm: the machine sang, and this is where the note was struck.
    // A sphere 43 across plated in green copper, and in its middle a great
    // crystal 20 tall, packed ice veined with blue glass round a burning
    // blue core, held in three crossed copper coils on struts from the
    // walls. A deck rings the sphere below the crystal. From the crystal's
    // point a glass conduit with a light in it climbs through the roof
    // toward the machine. Four antechambers open off the deck: three of
    // capacitor towers in copper and glass, and the control room, whose
    // long window looks into the sphere over a bank of consoles.
    //
    // Frame: sphere round (0, v 22, 0), radius 21; deck at v 15 (walked
    // at 16); antechambers at u or w 25..31, v 16..24.
    private void GmResonator(GmCtx g, GmFrame f, GmWork work)
    {
        const double R = 21;
        const int Cv = 22, Deck = 16;
        GmBallW(g, f.Ox, f.Y(Cv), f.Oz, R, 3, (x, y, z) =>
        {
            double u = f.U(x, z), w = f.Wd(x, z);
            if ((y - f.Y(0)) % 6 == 0) return g.SteelR;
            if (Math.Abs(Math.IEEERemainder(Math.Atan2(w, u), Math.PI / 6)) * Math.Sqrt(u * u + w * w) < 0.8) return g.SteelR;
            return g.K.Hash01(x * 3 + y, z * 7) < 0.7 ? g.CopperC : g.Copper;
        });
        var ante = GmMasonry(g, f.Y(Deck), f.Y(Deck + 8), g.PolG, g.BrickG, g.BrickC, g.BrickB, g.BrickG);
        var door = GmMasonry(g, f.Y(Deck), f.Y(Deck + 8), g.IronR, g.SteelR, g.SteelR, g.SteelR, g.SteelR);
        foreach (var (du, dw, port) in new[] { (1, 0, "u+"), (-1, 0, "u-"), (0, -1, "w-"), (0, 1, "w+") })
        {
            if (du != 0)
            {
                GmRoom(g, f, du * 25, Deck, -6, du * 31, Deck + 8, 6, 2, ante);
                GmRoom(g, f, du * 18, Deck, -2, du * 26, Deck + 3, 2, 1, door);
                GmRoom(g, f, du * 31, Deck, -2, du * 35, Deck + 4, 2, 1, door);
                work.Ports[port] = f.Port(du * 36, Deck, 0, du, 0);
            }
            else
            {
                GmRoom(g, f, -8, Deck, dw * 25, 8, Deck + 8, dw * 31, 2, ante);
                GmRoom(g, f, dw > 0 ? 3 : -2, Deck, dw * 18, dw > 0 ? 4 : 2, Deck + 3, dw * 26, 1, door);
                GmRoom(g, f, -2, Deck, dw * 31, 2, Deck + 4, dw * 35, 1, door);
                work.Ports[port] = f.Port(0, Deck, dw * 36, 0, dw);
            }
        }

        g.Details.Add(() =>
        {
            // ── the deck ──
            for (int u = -21; u <= 21; u++)
                for (int w = -21; w <= 21; w++)
                {
                    double q = Math.Sqrt(u * u + w * w), dy = Deck - 1 - Cv;
                    if (q < 14 || q * q + dy * dy > R * R) continue;
                    GS(g, f, u, Deck - 1, w, (u + w) % 4 == 0 ? g.SteelR : g.IronR);
                    if (q < 14.9) GRail(g, f, u, Deck, w, Math.Abs(u) > Math.Abs(w) ? 'w' : 'u');
                }
            // ── the crystal ──
            int cy = 27;
            for (int u = -7; u <= 7; u++)
                for (int w = -7; w <= 7; w++)
                    for (int v = cy - 11; v <= cy + 11; v++)
                    {
                        double e = Math.Abs(u) / 6.0 + Math.Abs(v - cy) / 10.0 + Math.Abs(w) / 6.0;
                        if (e > 1) continue;
                        if (e < 0.22) { GG(g, f, u, v, w); continue; }
                        bool vein = GmNoise3(u * 0.5, v * 0.25, w * 0.5, g.Seed + 811) > 0.66;
                        GS(g, f, u, v, w, e > 0.86 ? g.IceGlow : vein ? g.GlassBlue : g.Ice);
                    }
            // ── three coils on struts ──
            double rc = 12.5;
            for (int ring = 0; ring < 3; ring++)
            {
                for (double a = 0; a < Math.PI * 2; a += 0.03)
                {
                    double c = Math.Cos(a) * rc, s = Math.Sin(a) * rc;
                    double pu = ring == 0 ? c : ring == 1 ? c : 0, pv = ring == 0 ? 0 : s, pw = ring == 0 ? s : ring == 1 ? 0 : c;
                    int band = (int)(a * 10) % 7;
                    GS(g, f, (int)Math.Round(pu), cy + (int)Math.Round(pv), (int)Math.Round(pw), band == 0 ? g.SteelR : g.Copper);
                    GS(g, f, (int)Math.Round(pu * 1.06), cy + (int)Math.Round(pv * 1.06), (int)Math.Round(pw * 1.06), g.Copper);
                }
                for (int n = 0; n < 8; n++)
                {
                    double a = n * Math.PI / 4, c = Math.Cos(a) * (rc + 0.9), s = Math.Sin(a) * (rc + 0.9);
                    GG(g, f, (int)Math.Round(ring == 2 ? 0 : c), cy + (int)Math.Round(ring == 0 ? 0 : s), (int)Math.Round(ring == 0 ? s : ring == 1 ? 0 : c));
                }
            }
            foreach (var (su, sv, sw) in new[] { (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1) })
                GT(g, f, su * rc, cy, sw * rc, su * 20.6, cy, sw * 20.6, 0.7, (x, y, z, d) => g.SteelR);
            GT(g, f, 0, cy + rc, 0, 0, Cv + 20.6, 0, 0.7, (x, y, z, d) => g.SteelR);
            GT(g, f, 0, cy - rc, 0, 0, Cv - 20.6, 0, 0.7, (x, y, z, d) => g.SteelR);
            // ── the conduit from the crystal's point through the roof ──
            for (int v = cy + 10; v <= cy + 40; v++)
                for (int u = -2; u <= 2; u++)
                    for (int w = -2; w <= 2; w++)
                    {
                        double q = Math.Sqrt(u * u + w * w);
                        if (q <= 0.6) GG(g, f, u, v, w);
                        else if (q <= 1.6) GS(g, f, u, v, w, g.Glass);
                        else if (q <= 2.4 && v % 4 == 0) GS(g, f, u, v, w, g.Brass);
                    }
            // ── the antechambers ──
            foreach (var (du, dw) in new[] { (1, 0), (-1, 0), (0, -1), (0, 1) })
            {
                bool control = dw == 1;
                int bu = du * 28, bw = dw * 28;
                if (!control)
                {
                    // four capacitor towers: copper drums banded with glass
                    foreach (var (ou, ow) in new[] { (-2, -4), (2, -4), (-2, 4), (2, 4) })
                    {
                        int tu = du != 0 ? bu + ou : ow, tw = du != 0 ? ow : bw + ou;
                        for (int v = Deck; v <= Deck + 6; v++) GS(g, f, tu, v, tw, v % 2 == 0 ? g.Copper : g.Glass);
                        GD(g, f, tu + (du != 0 ? 0 : 1), Deck, tw + (du != 0 ? 1 : 0), g.Pick(new[] { "tower1", "tower2", "tower3", "towerbottom", "tank2-aged" }), -du, -dw);
                        GG(g, f, tu, Deck + 3, tw);
                    }
                    GD(g, f, bu + du * 3, Deck, bw + dw * 3, g.Pick(new[] { "gauge1", "gauge4", "sightglass2-aged" }), -du, -dw);
                    GL(g, f, bu + du * 3, Deck + 3, bw + dw * 3, "gas-wall1", -du, -dw);
                }
                else
                {
                    // the control room: consoles under a window into the sphere
                    for (int u = -6; u <= -1; u++)
                        for (int v = Deck + 1; v <= Deck + 3; v++)
                            for (int w = 19; w <= 24; w++) GS(g, f, u, v, w, g.Glass);
                    for (int u = -7; u <= 7; u++)
                    {
                        if (u >= 2 && u <= 5) continue;
                        GD(g, f, u, Deck, 25, g.Pick(new[] { "switch1-aged", "switch2-aged", "lever1-aged", "lever3-aged", "gauge2", "gauge3", "sightglass1-aged", "valve1-aged" }), 0, -1);
                        if (u % 3 == 0) GC(g, f, u, Deck, 27, "chair-aged", 0, -1);
                    }
                    GC(g, f, -7, Deck, 30, "clutteredbookshelf-agedacacia|bookshelves/bookshelf-stuff04|half|bookshelves/bookshelf-ruined-full3", 1, 0);
                    GC(g, f, 7, Deck, 30, "table-long-with-accessories", -1, 0);
                    GL(g, f, -8, Deck + 3, 28, "gas-wall2", 1, 0);
                    GL(g, f, 8, Deck + 3, 28, "gas-wall2", -1, 0);
                    GL(g, f, 0, Deck + 8, 28, "gas-ceiling1", 0, 1);
                }
            }
            // ── light on the deck ──
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI / 6 + 0.26;
                GL(g, f, (int)Math.Round(Math.Cos(a) * 17), Deck, (int)Math.Round(Math.Sin(a) * 17), "lumi-floor" + (i % 3 == 0 ? "9" : "1"), -Math.Cos(a), -Math.Sin(a));
            }
            g.K.Job.StructChests.Add((new Vintagestory.API.MathTools.BlockPos(f.X(5, 29), f.Y(Deck), f.Z(5, 29), g.K.Job.Dim), "normal-aged",
                RollLoot(g.K.Rand, MachineLoot, 5 + g.RI(0, 2)), "resonator"));
        });
    }

    // ═════════════════════════════════════════════════════════════════════
    // 9. THE WORKERS' QUARTERS
    // ═════════════════════════════════════════════════════════════════════
    // Brainstorm: thousands worked the machine, and they lived beside it
    // and died there when it woke. Three storeys of rooms off long brick
    // corridors, joined by a round stair tower at the east end. The ground
    // floor feeds them: mess halls, kitchens, stores, an infirmary. The
    // middle floor sleeps them: bunk rooms of iron beds, lockers of hazmat
    // suits. The top floor holds the chapels, more dormitories, the
    // foreman's office and a workshop. Beds stand where they were left,
    // the tables still laid, coats still on their hooks.
    //
    // Frame: corridors along u from -28 to 30 at w 0 on v 0, 8, 16; rooms
    // to w +-12; the stair tower round (36, 0), radius 5, to v 30, its
    // top doorway (v 24) facing out toward the plateau hall above.
    private void GmQuarters(GmCtx g, GmFrame f, GmWork work)
    {
        string[][] floors =
        {
            new[] { "mess", "mess", "kitchen", "kitchen", "store", "infirmary", "store", "office" },
            new[] { "dorm", "dorm", "dorm", "hazmat", "dorm", "store", "dorm", "office" },
            new[] { "dorm", "chapel", "dorm", "infirmary", "office", "dorm", "chapel", "workshop" },
        };
        for (int i = 0; i < 3; i++)
            GmWing(g, f, -28, 30, 0, i * 8, 10, 10, floors[i], g.BrickA, g.BrickC, g.PlanksA);
        var towerShell = GmMasonry(g, f.Y(0), f.Y(30), g.PolOld, g.BrickA, g.BrickC, g.BrickB, g.BrickA);
        GmCylW(g, f.Xd(36, 0), f.Zd(36, 0), 5, f.Y(0), f.Y(30), 0, 2, towerShell);
        double toCorr = f.H(-1, 0), outward = f.H(0, 1);
        GmShaft(g, f.X(36, 0), f.Z(36, 0), new() { (toCorr, f.Y(0)), (toCorr, f.Y(8)), (toCorr, f.Y(16)), (outward, f.Y(24)) }, 4.5, null, false, false);
        var door = GmMasonry(g, f.Y(0), f.Y(30), g.IronR, g.BrickB, g.BrickB, g.BrickB, g.BrickB);
        GmRoom(g, f, -31, 0, -1, -28, 3, 1, 1, door);
        GmRoom(g, f, 35, 24, 5, 37, 27, 9, 1, door);
        work.Ports["down"] = f.Port(-32, 0, 0, -1, 0);
        work.Ports["down2"] = f.Port(44, 0, 0, 1, 0);
        work.Ports["surface"] = f.Port(36, 24, 10, 0, 1);
        g.Details.Add(() =>
        {
            // a lamp on the tower's wall at each floor
            foreach (int v in new[] { 2, 10, 18, 26 })
            {
                GL(g, f, 36, v, 4, "gas-wall1", 0, -1);
                GL(g, f, 36, v, -4, "gas-wall1", 0, 1);
            }
            g.K.Job.StructChests.Add((new Vintagestory.API.MathTools.BlockPos(f.X(-27, -1), f.Y(16), f.Z(-27, -1), g.K.Job.Dim), "normal-aged",
                RollLoot(g.K.Rand, MachineLoot, 5 + g.RI(0, 2)), "foreman"));
        });
    }

    // ═════════════════════════════════════════════════════════════════════
    // 10. THE DEEP BORE
    // ═════════════════════════════════════════════════════════════════════
    // Brainstorm: the machine drank the heat of the world. A round drill
    // hall 49 across and 34 tall, and in its floor a bore 11 across, lined
    // in obsidian, falling to the mantle, where magma still glows. Over it
    // stands a lattice derrick with a crown block and sheave, its drill
    // string hanging down the bore to a bit above the magma; a rotary table
    // at its foot; a winding drum with its cable to the crown. A copper
    // heat exchanger winds round the bore's mouth, a gallery rings the
    // wall with two stairs up to it, and core samples of every rock they
    // drilled stand along the wall. A wing outside holds the drillers'
    // office, the sample lab and the crew room.
    //
    // Frame: hall round (0, 0) radius 24, v 0..34; bore radius 5.5 from
    // y 7 to the floor; gallery at v 10 (walked at 11); wing hall at w 33.
    private void GmDeepBore(GmCtx g, GmFrame f, GmWork work)
    {
        const double R = 24, Rb = 5.5;
        const int H = 34, Gal = 10;
        int bottom = Math.Max(7, f.Y(0) - 40);
        GmCylW(g, f.Ox, f.Oz, R, f.Y(0), f.Y(H), 0, 3, (x, y, z) =>
            y < f.Y(0) ? (g.K.Hash01(x, z) < 0.3 ? g.SteelR : g.PolB) : (y - f.Y(0)) % 6 == 5 ? g.SteelR : g.K.Hash01(x * 5 + y, z) < 0.2 ? g.BrickC : g.BrickB);
        GmCylW(g, f.Ox, f.Oz, Rb, bottom, f.Y(-1), 0, 3, (x, y, z) => g.K.Hash01(x + y * 3, z) < 0.5 ? g.Obsid : g.Basalt);
        GmWing(g, f, -20, 20, 33, 0, 0, 8, new[] { "office", "lab", "store", "dorm", "specimens", "workshop" }, g.BrickB, g.BrickC, g.PolB);
        GmRoom(g, f, -1, 0, 22, 0, 2, 31, 1, GmMasonry(g, f.Y(0), f.Y(2), g.PolB, g.BrickB, g.BrickC, g.BrickB, g.BrickB));
        var door = GmMasonry(g, f.Y(0), f.Y(H), g.IronR, g.SteelR, g.SteelR, g.SteelR, g.SteelR);
        GmRoom(g, f, -29, 0, -2, -22, 4, 2, 1, door);
        GmRoom(g, f, 22, 0, -2, 29, 4, 2, 1, door);
        GmRoom(g, f, -2, 0, -29, 2, 4, -22, 1, door);
        work.Ports["u-"] = f.Port(-30, 0, 0, -1, 0);
        work.Ports["u+"] = f.Port(30, 0, 0, 1, 0);
        work.Ports["w-"] = f.Port(0, 0, -30, 0, -1);
        work.Ports["w+"] = f.Port(23, 0, 33, 1, 0);
        foreach (double sa in new[] { 90.0, 270.0 })
        {
            double th = sa * Math.PI / 180;
            int sx = (int)Math.Round(f.Xd(Math.Cos(th) * 18, Math.Sin(th) * 18)), sz = (int)Math.Round(f.Zd(Math.Cos(th) * 18, Math.Sin(th) * 18));
            double wth = Math.Atan2(sz - f.Oz, sx - f.Ox);
            GmShaft(g, sx, sz, new() { (wth + Math.PI, f.Y(0)), (wth, f.Y(Gal + 1)) }, 3, null, false, false);
        }

        g.Details.Add(() =>
        {
            // ── magma at the bottom, heat in the bore's walls ──
            int by = bottom;
            for (int u = -6; u <= 6; u++)
                for (int w = -6; w <= 6; w++)
                {
                    if (u * u + w * w > Rb * Rb) continue;
                    g.Fluid(f.X(u, w), by, f.Z(u, w), g.Lava);
                    g.Fluid(f.X(u, w), by + 1, f.Z(u, w), g.Lava);
                }
            for (int y = by + 3; y < f.Y(-1); y += 4)
                for (int i = 0; i < 6; i++)
                {
                    double a = i * Math.PI / 3 + y * 0.3;
                    g.Glow(f.X((int)Math.Round(Math.Cos(a) * (Rb + 0.8)), (int)Math.Round(Math.Sin(a) * (Rb + 0.8))), y,
                        f.Z((int)Math.Round(Math.Cos(a) * (Rb + 0.8)), (int)Math.Round(Math.Sin(a) * (Rb + 0.8))), g.GlowG);
                }
            // ── the derrick ──
            foreach (var (su, sw) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
                GT(g, f, su * 8, 0, sw * 8, su * 2.5, 30, sw * 2.5, 0.7, (x, y, z, d) => g.SteelR);
            for (int v = 6; v <= 27; v += 6)
            {
                double e = 8 - 5.5 * v / 30.0;
                foreach (var (a, b) in new[] { ((1, 1), (1, -1)), ((1, -1), (-1, -1)), ((-1, -1), (-1, 1)), ((-1, 1), (1, 1)) })
                {
                    GT(g, f, a.Item1 * e, v, a.Item2 * e, b.Item1 * e, v, b.Item2 * e, 0.45, (x, y, z, d) => g.IronR);
                    double e2 = 8 - 5.5 * (v + 6) / 30.0;
                    if (v + 6 <= 30) GT(g, f, a.Item1 * e, v, a.Item2 * e, b.Item1 * e2, v + 6, b.Item2 * e2, 0.4, (x, y, z, d) => g.Iron);
                }
            }
            GBox(g, f, -3, 30, -3, 3, 31, 3, (u, v, w) => v == 31 ? g.SteelR : g.RustA);
            GW(g, f, -1, 34, 0, 'u', 3, 2, 4, 0, () => g.Steel);
            // ── the drill string, its bit, the rotary table ──
            GT(g, f, 0, 30, 0, 0, by - f.Y(0) + 5, 0, 1.2, (x, y, z, d) => (y - f.Y(0)) % 5 == 0 ? g.SteelR : g.Steel);
            for (int i = 0; i < 4; i++)
            {
                double rr = 2.6 - i * 0.6;
                GT(g, f, 0, by - f.Y(0) + 5 - i, 0, 0, by - f.Y(0) + 4 - i, 0, rr, (x, y, z, d) => g.SteelR);
            }
            for (int u = -5; u <= 5; u++)
                for (int w = -5; w <= 5; w++)
                {
                    double q = Math.Sqrt(u * u + w * w);
                    if (q >= 3.2 && q <= 5.2 && (u == 0 || w == 0 || Math.Abs(u) == Math.Abs(w))) GS(g, f, u, 0, w, g.SteelR);   // the table's spokes over the bore
                    if (q > 6.2 && q <= 7) GRail(g, f, u, 0, w, Math.Abs(u) > Math.Abs(w) ? 'w' : 'u');
                }
            // ── the heat exchanger round the mouth ──
            for (double t = 0; t < 3 * Math.PI * 2; t += 0.05)
            {
                double a = t, v = 0.6 + t / (3 * Math.PI * 2) * 11;
                int u = (int)Math.Round(Math.Cos(a) * 8.5), w = (int)Math.Round(Math.Sin(a) * 8.5);
                GS(g, f, u, (int)Math.Round(v), w, (int)(t * 4) % 9 == 0 ? g.SteelR : g.Copper);
                if ((int)(t * 4) % 23 == 0) GG(g, f, (int)Math.Round(Math.Cos(a) * 9.5), (int)Math.Round(v), (int)Math.Round(Math.Sin(a) * 9.5));
            }
            // ── the winding drum and its cable ──
            GT(g, f, -15, 3, -4, -15, 3, 4, 2.5, (x, y, z, d) => d > 1.8 ? g.RustA : g.Steel);
            foreach (int sw in new[] { -5, 5 })
            {
                GT(g, f, -18, 0, sw, -15, 3, sw, 0.6, (x, y, z, d) => g.SteelR);
                GT(g, f, -12, 0, sw, -15, 3, sw, 0.6, (x, y, z, d) => g.SteelR);
            }
            GT(g, f, -15, 5.5, 0, -3, 31, 0, 0.3, (x, y, z, d) => g.RustB);
            // ── the gallery and its consoles ──
            for (int u = -24; u <= 24; u++)
                for (int w = -24; w <= 24; w++)
                {
                    double q = Math.Sqrt(u * u + w * w);
                    if (q > R || q < 20) continue;
                    GS(g, f, u, Gal, w, (u + w) % 3 == 0 ? g.SteelR : g.IronR);
                    if (q < 20.9) GRail(g, f, u, Gal + 1, w, Math.Abs(u) > Math.Abs(w) ? 'w' : 'u');
                }
            for (int i = 0; i < 16; i++)
            {
                double a = i * Math.PI / 8 + 0.2;
                int u = (int)Math.Round(Math.Cos(a) * 23.4), w = (int)Math.Round(Math.Sin(a) * 23.4);
                if (i % 2 == 0) GD(g, f, u, Gal + 1, w, g.Pick(new[] { "gauge1", "gauge2", "gauge4", "switch3-aged", "lever2-aged", "valve2-aged", "sightglass3-aged" }), -Math.Cos(a), -Math.Sin(a));
                else GL(g, f, u, Gal + 1, w, "lumi-floor10", -Math.Cos(a), -Math.Sin(a));
                GL(g, f, u, Gal + 4, w, "gas-wall2", -Math.Cos(a), -Math.Sin(a));
                if (i % 4 != 1) GL(g, f, u, 3, w, "gas-wall1", -Math.Cos(a), -Math.Sin(a));
            }
            // ── core samples along the wall ──
            int[] rocks = { g.Granite, g.Basalt, g.Andes, g.Obsid, g.Drock, g.BrickC };
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI / 6 + 0.45;
                int u = (int)Math.Round(Math.Cos(a) * 22.6), w = (int)Math.Round(Math.Sin(a) * 22.6);
                if (Math.Abs(u) <= 3 || Math.Abs(w) <= 3) continue;
                for (int v = 0; v < 3; v++) GS(g, f, u, v, w, rocks[(i + v) % rocks.Length]);
            }
            for (int i = 0; i < 30; i++)
            {
                double a = g.R() * Math.PI * 2, q = 10 + g.R() * 9;
                GC(g, f, (int)Math.Round(Math.Cos(a) * q), 0, (int)Math.Round(Math.Sin(a) * q),
                    g.Pick(new[] { "pile-trash-oldore", "crate/crate-large-ore1", "crate/crate-large-ore3", "barrel-metal4", "pile-tools2", "rubble-cracked2" }), Math.Cos(a), Math.Sin(a));
            }
            g.K.Job.StructChests.Add((new Vintagestory.API.MathTools.BlockPos(f.X(-19, 6), f.Y(0), f.Z(-19, 6), g.K.Job.Dim), "normal-aged",
                RollLoot(g.K.Rand, MachineLoot, 5 + g.RI(0, 2)), "deep bore"));
        });
    }

    // ── The wings high under the plateau ─────────────────────────────────
    // The drafting offices: where the machine was drawn. A long hall of
    // drawing rooms, map rooms and clerks' offices.
    private void GmOffices(GmCtx g, GmFrame f, GmWork work)
    {
        GmWing(g, f, -38, 38, 0, 0, 9, 9, new[] { "drafting", "drafting", "office", "office", "archive", "optics", "store", "drafting", "office" }, g.BrickA, g.BrickC, g.PolOld);
        GmRoom(g, f, -41, 0, -1, -38, 3, 1, 1, GmMasonry(g, f.Y(0), f.Y(3), g.IronR, g.BrickB, g.BrickB, g.BrickB, g.BrickB));
        work.Ports["down"] = f.Port(-42, 0, 0, -1, 0);
        work.Ports["down2"] = f.Port(41, 0, 0, 1, 0);
    }

    // The quartermaster's stores: what the crews were issued, and what they
    // were never given back.
    private void GmStores(GmCtx g, GmFrame f, GmWork work)
    {
        GmWing(g, f, -38, 38, 0, 0, 9, 9, new[] { "store", "store", "store", "workshop", "office", "hazmat", "store", "machine" }, g.BrickG, g.BrickC, g.PolG);
        GmRoom(g, f, -41, 0, -1, -38, 3, 1, 1, GmMasonry(g, f.Y(0), f.Y(3), g.IronR, g.BrickB, g.BrickB, g.BrickB, g.BrickB));
        work.Ports["down"] = f.Port(-42, 0, 0, -1, 0);
        work.Ports["down2"] = f.Port(41, 0, 0, 1, 0);
    }
}
