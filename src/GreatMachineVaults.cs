using System;
using System.Collections.Generic;
using System.Linq;

namespace LandmassGenerator;

// ── THE GREAT MACHINE: THE TEN WORKS (0.65.0) ───────────────────────────
// The buried megastructures under the crater, each in its own frame (u
// along it, w away from the crater centre, v up from its floor), with the
// pipes that join them (GreatMachineWorks.cs):
//
//  1. the Boiler Cathedral    steam: eight boilers under a pointed vault
//  2. the Engine Gallery      the prime movers, a flywheel in a pit
//  3. the Archive             galleried stacks round an orrery
//  4. the Laboratories        a domed test chamber, twelve labs round it
//  5. the Foundry             a lava crucible, molds, a gantry crane
//  6. the Axle Well           the drive under the abyss: crown gears
//  7. the Cistern             a pillared reservoir, beam pumps
//  8. the Resonator           a crystal in copper coils in a sphere
//  9. the Workers' Quarters   three storeys of rooms round a stair tower
// 10. the Deep Bore           a derrick over a shaft to the mantle
//
// plus two wings of offices and stores high under the plateau. The mid
// works ring the abyss below y 52, where no chasm reaches; the axle well
// lies under the abyss itself; the upper works sit between the chasms
// under the plateau, as high as the safe ceiling lets them.
public partial class LandmassGeneratorModSystem
{
    // ── Placement ────────────────────────────────────────────────────────
    private (string Name, double Bearing, double R, int Floor, int U0, int U1, int W0, int W1, int Top, bool Upper, Action<GmCtx, GmFrame, GmWork> Build)[] GmWorkSpecs()
        => new (string, double, double, int, int, int, int, int, int, bool, Action<GmCtx, GmFrame, GmWork>)[]
        {
            ("Axle Well",              0,   0,   7, -50, 50, -50, 50, 26, false, GmAxleWell),
            ("Resonator",             25, 190,   8, -36, 36, -36, 36, 47, false, GmResonator),
            ("Cistern",               90, 198,  14, -45, 45, -33, 48, 37, false, GmCistern),
            ("Boiler Cathedral",     155, 205,  14, -58, 76, -30, 48, 41, false, GmBoilerCathedral),
            ("Foundry",              225, 192,  11, -40, 40, -40, 56, 44, false, GmFoundry),
            ("Deep Bore",            280, 175,  14, -32, 32, -32, 46, 36, false, GmDeepBore),
            ("Engine Gallery",       335, 198,  10, -54, 54, -46, 30, 42, false, GmEngineGallery),
            ("Archive",              268, 252,  98, -36, 36, -54, 36, 42, true, GmArchive),
            ("Laboratories",         335, 252, 104, -36, 36, -36, 36, 32, true, GmLaboratories),
            ("Workers' Quarters",    157, 260, 104, -34, 44, -16, 16, 28, true, GmQuarters),
            ("Drafting Offices",      45, 250, 110, -42, 42, -14, 14, 8, true, GmOffices),
            ("Quartermaster's Stores",222, 250, 110, -42, 42, -14, 14, 8, true, GmStores),
        };

    // A work's frame at a bearing and distance from the crater's centre,
    // facing out along whichever axis is nearer the bearing.
    private static GmFrame GmPlaceFrame(GmCtx g, double bearing, double r, int floor)
    {
        double th = bearing * Math.PI / 180;
        int ox = g.Cx + (int)Math.Round(Math.Cos(th) * r), oz = g.Cz + (int)Math.Round(Math.Sin(th) * r);
        int wx, wz;
        if (r == 0) { wx = 1; wz = 0; }
        else if (Math.Abs(Math.Cos(th)) >= Math.Abs(Math.Sin(th))) { wx = Math.Sign(Math.Cos(th)); wz = 0; }
        else { wx = 0; wz = Math.Sign(Math.Sin(th)); }
        return new GmFrame(ox, floor, oz, wx, wz);
    }

    private void GmPlaceWorks(GmCtx g)
    {
        // A plateau hall's stairwell drops from the hall floor to its pipe,
        // which keeps under the safe ceiling; over an upper work it would cut
        // down into the work's top. The halls stand beside the planned works
        // (GmSurface), and the upper works first look for a spot clear of
        // the stairwells, then for any spot.
        bool UnderStair((int X0, int Z0, int X1, int Z1) rc)
            => g.HallDoors.Any(hd => hd.X + hd.Dx * GmLead >= rc.X0 - 8 && hd.X + hd.Dx * GmLead <= rc.X1 + 8
                                  && hd.Z + hd.Dz * GmLead >= rc.Z0 - 8 && hd.Z + hd.Dz * GmLead <= rc.Z1 + 8);
        foreach (var s in GmWorkSpecs())
        {
            GmFrame best = default;
            bool found = false;
            int bestDrop = 0;
            double bestOff = 0;
            for (int pass = s.Upper ? 0 : 1; pass < 2 && !found; pass++)
            for (int drop = 0; drop <= (s.Upper ? 44 : 0) && !found; drop += 4)
                foreach (double off in new[] { 0.0, 4, -4, 8, -8, 12, -12, 16, -16 })
                    foreach (double dr in new[] { 0.0, -10, 10 })
                    {
                        if (found) break;
                        if (s.R == 0 && (off != 0 || dr != 0)) continue;
                        var f = GmPlaceFrame(g, s.Bearing + off, s.R + dr, s.Floor - drop);
                        var rc = f.Rect(s.U0, s.W0, s.U1, s.W1);
                        int top = f.Y(s.Top);
                        bool ok = pass == 1 || !UnderStair(rc);
                        if (s.Upper)
                            for (int x = rc.X0 - 4; x <= rc.X1 + 4 && ok; x += 2)
                                for (int z = rc.Z0 - 4; z <= rc.Z1 + 4 && ok; z += 2)
                                    if (g.SafeTop(x, z) < top + 1) ok = false;
                        foreach (var w in g.Works)
                            if (ok && w.Overlaps(rc.X0, rc.Z0, rc.X1, rc.Z1, f.Y(-4), top + 4, 6)) ok = false;
                        if (!ok) continue;
                        best = f; found = true; bestDrop = drop; bestOff = off;
                    }
            if (!found)
            {
                sapi.Logger.Warning("[landmassgenerator] great machine: no room for the {0}, left out", s.Name);
                continue;
            }
            var r2 = best.Rect(s.U0, s.W0, s.U1, s.W1);
            var work = new GmWork { Name = s.Name, X0 = r2.X0, Z0 = r2.Z0, X1 = r2.X1, Z1 = r2.Z1, Y0 = best.Y(-4), Y1 = best.Y(s.Top + 4), Floor = best.Oy };
            g.Works.Add(work);
            g.CurWork = s.Name;
            s.Build(g, best, work);
            g.CurWork = null;
            sapi.Logger.Notification("[landmassgenerator] great machine work {0}: floor y {1} at {2}, {3} (bearing {4:0}, {5} lower than planned)",
                s.Name, best.Oy, best.Ox - g.Cx, best.Oz - g.Cz, s.Bearing + bestOff, bestDrop);
        }
    }

    // ── The network ──────────────────────────────────────────────────────
    // A ring of pipes joins the six mid works end to end, a spoke from the
    // axle well reaches each of them, each upper work drops to the nearest
    // mid work through a stairwell, and three stairwells climb from the
    // upper works to the ruined halls on the plateau.
    private void GmNetwork(GmCtx g)
    {
        GmWork W(string n) => g.Works.FirstOrDefault(w => w.Name == n);
        // pa, pb: the doorways each end offers, "down" also tries "down2"...
        void Link(string a, string pa, string b, string pb)
        {
            var A = W(a); var B = W(b);
            if (A == null || B == null) return;
            var ps = A.Ports.Where(kv => kv.Key == pa || kv.Key.StartsWith(pa) && pa == "down").Select(kv => kv.Value).ToList();
            var qs = B.Ports.Where(kv => kv.Key == pb).Select(kv => kv.Value).ToList();
            if (ps.Count == 0 || qs.Count == 0) return;
            GmConnect(g, ps, qs, a + " to " + b);
        }
        string[] ring = { "Resonator", "Cistern", "Boiler Cathedral", "Foundry", "Deep Bore", "Engine Gallery" };
        for (int i = 0; i < ring.Length; i++) Link(ring[i], "u+", ring[(i + 1) % ring.Length], "u-");
        foreach (string n in ring) Link("Axle Well", n, n, "w-");
        Link("Archive", "down", "Deep Bore", "w+");
        Link("Laboratories", "down", "Engine Gallery", "w+");
        Link("Workers' Quarters", "down", "Boiler Cathedral", "w+");
        Link("Drafting Offices", "down", "Resonator", "w+");
        Link("Quartermaster's Stores", "down", "Foundry", "w+");

        // the ways in: from a hall door on the plateau, a short pipe into
        // the hall to a stairwell, down to the work's top doorway. With
        // entrances=0 there are none and earth seals the works entirely.
        if (g.K.OptI("entrances", 1) == 0) return;
        foreach (var hd in g.HallDoors)
        {
            var w = g.Works.Where(x => x.Ports.ContainsKey("surface"))
                .OrderBy(x => Math.Pow((x.X0 + x.X1) / 2 - hd.X, 2) + Math.Pow((x.Z0 + x.Z1) / 2 - hd.Z, 2)).FirstOrDefault();
            if (w == null) continue;
            var a = w.Ports["surface"];
            var b = new GmPort { X = hd.X, Y = hd.Y, Z = hd.Z, Dx = hd.Dx, Dz = hd.Dz };
            g.Opened(() => GmConnect(g, new[] { a }, new[] { b }, w.Name + " to the plateau", true));
        }
    }

    // ── Fittings in a frame ──────────────────────────────────────────────
    private static void GS(GmCtx g, GmFrame f, int u, int v, int w, int id) => g.Solid(f.X(u, w), f.Y(v), f.Z(u, w), id);
    private static void GC(GmCtx g, GmFrame f, int u, int v, int w, string t, double du, double dw) => g.Clut(f.X(u, w), f.Y(v), f.Z(u, w), t, f.H(du, dw));
    private static void GD(GmCtx g, GmFrame f, int u, int v, int w, string t, double du, double dw) => g.Dev(f.X(u, w), f.Y(v), f.Z(u, w), t, f.H(du, dw));
    private static void GL(GmCtx g, GmFrame f, int u, int v, int w, string t, double du, double dw) => g.Lamp(f.X(u, w), f.Y(v), f.Z(u, w), t, f.H(du, dw));
    private static void GG(GmCtx g, GmFrame f, int u, int v, int w, int id = 0) => g.Glow(f.X(u, w), f.Y(v), f.Z(u, w), id);
    private static void GT(GmCtx g, GmFrame f, double u0, double v0, double w0, double u1, double v1, double w1, double r, Func<int, int, int, double, int> mat)
        => GmSolidTube(g, f.Xd(u0, w0), f.Oy + v0, f.Zd(u0, w0), f.Xd(u1, w1), f.Oy + v1, f.Zd(u1, w1), r, mat);
    private static void GW(GmCtx g, GmFrame f, double u, double v, double w, char localAxis, double R, int thick, int spokes, double phase, Func<int> mat, bool teeth = true)
        => GmGear(g, f.Xd(u, w), f.Oy + v, f.Zd(u, w), localAxis == 'y' ? 'y' : f.Axis(localAxis), R, thick, spokes, phase, mat, teeth);
    private static void GBox(GmCtx g, GmFrame f, int u0, int v0, int w0, int u1, int v1, int w1, Func<int, int, int, int> mat)
    {
        for (int u = Math.Min(u0, u1); u <= Math.Max(u0, u1); u++)
            for (int w = Math.Min(w0, w1); w <= Math.Max(w0, w1); w++)
                for (int v = Math.Min(v0, v1); v <= Math.Max(v0, v1); v++) GS(g, f, u, v, w, mat(u, v, w));
    }
    // a railing cell: iron fence running along the given local axis
    private static void GRail(GmCtx g, GmFrame f, int u, int v, int w, char localAxis)
        => GS(g, f, u, v, w, f.Axis(localAxis) == 'x' ? g.FenceEW : g.FenceNS);
    // a straight stair of half steps along u (du = +1 or -1) from (u0, v0)
    // up to v1, w0..w1 wide, a solid wedge under it
    private static void GStair(GmCtx g, GmFrame f, int u0, int du, int w0, int w1, int v0, int v1, int mat)
    {
        for (int i = 0; ; i++)
        {
            double h = v0 + (i + 1) * 0.5;
            if (h > v1) break;
            int u = u0 + i * du, full = (int)Math.Floor(h);
            for (int w = Math.Min(w0, w1); w <= Math.Max(w0, w1); w++)
            {
                for (int v = v0 - 1; v < full; v++) GS(g, f, u, v, w, mat);
                if (h - full > 0.25) GS(g, f, u, full, w, g.SlabG);
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // 1. THE BOILER CATHEDRAL
    // ═════════════════════════════════════════════════════════════════════
    // Brainstorm: the machine ran on steam before it ran on anything
    // stranger, and the steam was raised here, in a hall built like a
    // church because its builders worshipped what it fed. A nave 101 long
    // and 45 wide under a pointed vault with iron ribs every 8; two rows of
    // four boilers on brick plinths down the nave, each with its firebox
    // door, a riveted steam dome and a flue climbing into the vault; a
    // central catwalk at the domes' height and two side catwalks, joined
    // by three gantries across; stairs up from the floor at both ends; at
    // the north end a round apse where every boiler's steam met a
    // manifold of organ-pipe risers round a giant pressure gauge, with a
    // balcony at catwalk height; and a wing of stokers' rooms outside the
    // outer wall: a stokers' mess, coal store, valve room, gauge room and
    // the chief stoker's office.
    //
    // Frame: nave u -50..50, w -22..22, v 0..38 (spring at 22); apse round
    // u 50, radius 18; wing hall at w 31; catwalks at v 16.
    private void GmBoilerCathedral(GmCtx g, GmFrame f, GmWork work)
    {
        const int HU = 50, HW = 22, H = 38, Spring = 22, Cat = 16;
        var naveShell = (Func<int, int, int, int>)((x, y, z) =>
        {
            double u = f.U(x, z);
            if (y < f.Y(0)) return ((int)u + (int)f.Wd(x, z)) % 6 == 0 ? g.PolB : g.PolOld;
            if (y > f.Y(Spring) && ((int)Math.Round(u) % 8 + 8) % 8 == 0) return g.SteelR;          // the vault's iron ribs
            return (y - f.Y(0)) % 5 == 4 ? g.BrickB : g.K.Hash01(x * 7 + y, z * 11) < 0.15 ? g.BrickC : g.BrickA;
        });
        GmVault(g, f, -HU, 0, -HW, HU, H, HW, 'u', Spring, true, 3, naveShell);
        // the apse: a half round at the north end, domed
        GmCylW(g, f.Xd(HU, 0), f.Zd(HU, 0), 18, f.Y(0), f.Y(26), 10, 3, naveShell);
        // the stokers' wing outside the outer wall
        GmWing(g, f, -40, 40, HW + 9, 0, 0, 8, new[] { "mess", "store", "valves", "valves", "workshop", "office", "store", "kitchen" }, g.BrickA, g.BrickC, g.PolOld);
        foreach (int pu in new[] { -30, 0, 30 })
            GmRoom(g, f, pu, 0, HW + 1, pu + 1, 2, HW + 7, 1, GmMasonry(g, f.Y(0), f.Y(2), g.PolOld, g.BrickA, g.BrickC, g.BrickB, g.BrickA));
        // doorways: catwalk level at the south end and through the apse,
        // the floor of the inner wall, the end of the wing hall
        var door = GmMasonry(g, f.Y(0), f.Y(40), g.IronR, g.SteelR, g.SteelR, g.SteelR, g.SteelR);
        GmRoom(g, f, -HU - 4, Cat, -2, -HU, Cat + 4, 2, 1, door);
        GmRoom(g, f, HU + 17, Cat, -2, HU + 22, Cat + 4, 2, 1, door);
        GmRoom(g, f, -2, 0, -HW - 4, 2, 4, -HW, 1, door);
        work.Ports["u-"] = f.Port(-HU - 5, Cat, 0, -1, 0);
        work.Ports["u+"] = f.Port(HU + 23, Cat, 0, 1, 0);
        work.Ports["w-"] = f.Port(0, 0, -HW - 5, 0, -1);
        work.Ports["w+"] = f.Port(43, 0, HW + 9, 1, 0);

        int[] rowsU = { -36, -12, 12, 36 };
        g.Details.Add(() =>
        {
            // ── the boilers ──
            foreach (int sw in new[] { -1, 1 })
                foreach (int uc in rowsU)
                {
                    int wc = sw * 11;
                    // the plinth
                    GBox(g, f, uc - 9, 0, wc - 4, uc + 9, 1, wc + 4, (u, v, w) => v == 1 && (u + w) % 3 == 0 ? g.BrickB : g.BurntBrick);
                    // the drum: riveted bands every 4, rust between, a steel
                    // front plate
                    GT(g, f, uc - 10, 8, wc, uc + 10, 8, wc, 6, (x, y, z, d) =>
                    {
                        double u = f.U(x, z);
                        if (u < uc - 9.2) return g.Steel;
                        if (((int)Math.Round(u - uc) % 4 + 4) % 4 == 0 && d > 5) return g.SteelR;
                        return d > 5 ? g.Rust() : g.IronR;
                    });
                    // the steam dome and its flue into the vault
                    GT(g, f, uc + 3, 13, wc, uc + 3, 17, wc, 2.4, (x, y, z, d) => y == f.Y(17) ? g.SteelR : g.Copper);
                    double top = Spring + (H - Spring) * (1 - Math.Pow(Math.Abs(wc) / (HW + 0.5), 1.7));
                    GT(g, f, uc + 3, 17, wc, uc + 3, top + 2, wc, 1.4, (x, y, z, d) => (y - f.Y(0)) % 4 == 0 ? g.SteelR : g.RustA);
                    for (double v = 20; v < top; v += 4)
                        GT(g, f, uc + 3, v, wc, uc + 3, v + 0.6, wc, 2.1, (x, y, z, d) => g.SteelR);
                    // the firebox door in the front plate, and the stoking floor
                    GD(g, f, uc - 11, 3, wc, "boilerdoor-aged", -1, 0);
                    GD(g, f, uc - 11, 6, wc, "boilerend", -1, 0);
                    GC(g, f, uc - 12, 0, wc - 2, "pile-trash-oldore", -1, 0);
                    GC(g, f, uc - 12, 0, wc + 2, g.Pick(new[] { "crate/crate-large-ore1", "crate/crate-large-ore2", "crate/crate-large-ore3" }), -1, 0);
                    GC(g, f, uc - 13, 0, wc, "tool-shovel", -1, 0);
                    // gauges and valves on the aisle side
                    int ga = wc - sw * 6, gv = wc - sw * 4;
                    GD(g, f, uc - 4, 7, ga, g.Pick(new[] { "gauge1", "gauge2", "gauge3", "gauge4" }), 0, -sw);
                    GD(g, f, uc + 4, 7, ga, g.Pick(new[] { "sightglass1-aged", "sightglass3-aged" }), 0, -sw);
                    GD(g, f, uc, 3, gv, g.Pick(new[] { "valve1-aged", "valve2-aged", "valve3-aged" }), 0, -sw);
                    // a lumi lamp at each plinth corner on the aisle side
                    GL(g, f, uc - 9, 2, wc - sw * 4, "lumi-floor8", 0, -sw);
                    GL(g, f, uc + 9, 2, wc - sw * 4, "lumi-floor8", 0, -sw);
                }

            // ── catwalks: the spine, the two sides, three gantries ──
            for (int u = -HU; u <= HU + 14; u++)
            {
                for (int w = -2; w <= 2; w++) GS(g, f, u, Cat - 1, w, (u + w) % 4 == 0 ? g.SteelR : g.IronR);
                if (u <= HU) { GRail(g, f, u, Cat, -3, 'u'); GRail(g, f, u, Cat, 3, 'u'); }
                if (u > HU) continue;
                foreach (int sw in new[] { -1, 1 })
                {
                    // the side catwalks stop short where a stair comes up under them
                    if (sw > 0 && u < -16 || sw < 0 && u > 16) continue;
                    for (int w = 19; w <= 22; w++) GS(g, f, u, Cat - 1, sw * w, g.IronR);
                    GRail(g, f, u, Cat, sw * 18, 'u');
                }
                if (u % 12 == 0)
                    foreach (int sw in new[] { -3, 3 })
                        for (int v = 0; v < Cat - 1; v++) GS(g, f, u, v, sw, v % 5 == 0 ? g.SteelR : g.Iron);
            }
            foreach (int gu in new[] { -24, 0, 24 })
                for (int w = -HW; w <= HW; w++)
                {
                    if (Math.Abs(w) <= 2) continue;
                    GS(g, f, gu, Cat - 1, w, g.IronR); GS(g, f, gu + 1, Cat - 1, w, g.IronR);
                    if (Math.Abs(w) < 18) { GRail(g, f, gu - 1, Cat, w, 'w'); GRail(g, f, gu + 2, Cat, w, 'w'); }
                }
            // stairs up to the side catwalks, one at each end
            GStair(g, f, -48, 1, 19, 21, 0, Cat, g.BrickA);
            GStair(g, f, 48, -1, -21, -19, 0, Cat, g.BrickA);

            // ── the apse: a balcony, the manifold and the great gauge ──
            for (int du = 0; du <= 18; du++)
                for (int w = -18; w <= 18; w++)
                {
                    double q = Math.Sqrt(du * du + w * w);
                    if (q < 14 || q > 18) continue;
                    GS(g, f, HU + du, Cat - 1, w, g.IronR);
                    if (q < 14.8) GRail(g, f, HU + du, Cat, w, 'w');
                }
            for (int i = 0; i < 15; i++)
            {
                double a = -Math.PI / 2 + i * Math.PI / 14;
                double pu = HU + Math.Cos(a) * 16.5, pw = Math.Sin(a) * 16.5;
                int topV = 18 + (int)(8 * Math.Sin(i * Math.PI / 14)) + (i % 2) * 3;
                int mat = i % 3 == 0 ? g.Brass : i % 3 == 1 ? g.Copper : g.CopperC;
                GT(g, f, pu, 0, pw, pu, topV, pw, 0.9, (x, y, z, d) => (y - f.Y(0)) % 6 == 5 ? g.SteelR : mat);
                if (i % 2 == 0) GD(g, f, (int)Math.Round(pu - Math.Cos(a) * 1.4), 4, (int)Math.Round(pw - Math.Sin(a) * 1.4), "valve" + (1 + i % 3) + "-aged", -Math.Cos(a), -Math.Sin(a));
            }
            // the gauge: a glass dial 9 across in a brass rim, a steel needle
            {
                int gu = HU + 13, gv = 24;
                for (int w = -5; w <= 5; w++)
                    for (int v = -5; v <= 5; v++)
                    {
                        double q = Math.Sqrt(w * w + v * v);
                        if (q > 5.3) continue;
                        GS(g, f, gu + 1, gv + v, w, g.SteelR);
                        GS(g, f, gu, gv + v, w, q > 4.3 ? g.Brass : g.Glass);
                    }
                for (double t = 0; t <= 3.6; t += 0.5) GS(g, f, gu - 1, gv + (int)Math.Round(t * 0.6), (int)Math.Round(t * 0.8), g.Steel);
                GG(g, f, gu - 1, gv, 0);
            }

            // ── light ──
            for (int u = -HU + 4; u <= HU; u += 8)
            {
                GL(g, f, u, H, 0, "gas-ceiling1", 0, 1);                      // hung from the vault's crown
                foreach (int sw in new[] { -1, 1 })
                {
                    GL(g, f, u, 4, sw * HW, "gas-wall1", 0, -sw);              // on the side walls
                    GL(g, f, u + 4, Cat + 1, sw * 2, "lumi-floor2", 0, -sw);   // on the spine catwalk
                    // pipes on the walls: rusted risers between the lamps
                    for (int v = 0; v <= 12; v++) GC(g, f, u + 2, v, sw * HW, v == 12 ? "pipe-veryrusted-v-topW" : "pipe-veryrusted-v-empty", 0, -sw);
                }
            }
            // chains hanging from the vault over the aisle
            for (int u = -HU + 10; u <= HU - 10; u += 20)
                for (int v = H - 1; v >= H - 6; v--) GC(g, f, u, v, 0, v == H - 6 ? "chains/chain-end-hook" : "chains/chain-section", 0, 1);
            // ── clutter down the aisle and the side aisles ──
            for (int i = 0; i < 70; i++)
            {
                int u = g.RI(-HU + 2, HU - 2), w = g.RI(-HW + 1, HW - 1);
                if (Math.Abs(w) >= 5 && Math.Abs(w) <= 17 && rowsU.Any(uc => Math.Abs(u - uc) <= 13)) continue;
                if (Math.Abs(w) <= 3 && u % 12 == 0) continue;
                string t = g.Pick(new[] { "rubble-cracked1", "rubble-cracked3", "barrel-metal1", "barrel-metal4", "pile-trash-scrap", "crate/crate-small-ruined1",
                    "!junkpipe2", "!junksheet3", "!brokengear/small02", "!pipelong2-aged", "toolrack-ruined1", "bucket3" });
                if (t[0] == '!') GD(g, f, u, 0, w, t.Substring(1), 0, 1); else GC(g, f, u, 0, w, t, 0, 1);
            }
            // the chief stoker's chest, under the apse balcony
            g.K.Job.StructChests.Add((new Vintagestory.API.MathTools.BlockPos(f.X(HU + 6, 13), f.Y(0), f.Z(HU + 6, 13), g.K.Job.Dim), "normal-aged",
                RollLoot(g.K.Rand, MachineLoot, 5 + g.RI(0, 2)), "boiler cathedral"));
        });
    }

    // ═════════════════════════════════════════════════════════════════════
    // 2. THE ENGINE GALLERY
    // ═════════════════════════════════════════════════════════════════════
    // Brainstorm: the boilers' steam turned the prime movers, and these
    // turned the machine. An engine house 93 long and 45 wide under an
    // iron-trussed roof 40 up: three vertical engines stand in a row over
    // a crankshaft that runs the length of the hall on brick pedestals,
    // each engine an A-frame of riveted columns carrying a cylinder 9
    // across with its valve chest, a piston rod down to a crosshead in
    // guides, a connecting rod to the crank; at the shaft's end a flywheel
    // 34 across turns in a transverse pit 19 deep. An overhead crane runs
    // on rails along both walls; a mezzanine along the outer wall holds
    // the drivers' consoles, with a stair up at its south end; and a wing
    // inward holds the oil store, the engineers' workshop, the gauge room
    // and the drawing office.
    //
    // Frame: hall u -46..46, w -22..22, v 0..40; shaft at v 3 along u;
    // engines at u -26, -10, 6; flywheel at u 28 (pit u 24..33);
    // mezzanine w 15..22 at v 12; wing hall at w -31.
    private void GmEngineGallery(GmCtx g, GmFrame f, GmWork work)
    {
        const int HU = 46, HW = 22, H = 40, Mez = 12;
        var hallShell = (Func<int, int, int, int>)((x, y, z) =>
        {
            double u = f.U(x, z);
            if (y < f.Y(0)) return ((int)Math.Round(u) % 7 + 7) % 7 == 0 ? g.SteelR : g.PolB;
            if (y > f.Y(H)) return ((int)Math.Round(u) % 6 + 6) % 6 == 0 || ((int)Math.Round(f.Wd(x, z)) % 6 + 6) % 6 == 0 ? g.SteelR : g.BrickB;
            if (((int)Math.Round(u) % 12 + 12) % 12 == 0) return g.IronR;                                   // iron pilasters
            return (y - f.Y(0)) % 6 == 5 ? g.BrickB : g.K.Hash01(x * 5 + y, z * 9) < 0.15 ? g.BrickC : g.BrickG;
        });
        GmRoom(g, f, -HU, 0, -HW, HU, H, HW, 3, hallShell);
        // the flywheel's pit, and the crank race along the shaft
        GmRoom(g, f, 24, -19, -19, 33, -1, 19, 2, GmMasonry(g, f.Y(-19), f.Y(-1), g.BrickB, g.BrickG, g.BrickC, g.BrickB, g.BrickG));
        GmRoom(g, f, -38, -3, -3, 24, -1, 3, 2, GmMasonry(g, f.Y(-3), f.Y(-1), g.BrickB, g.BrickG, g.BrickC, g.BrickB, g.BrickG));
        // the wing, inward
        GmWing(g, f, -36, 36, -HW - 9, 0, 8, 0, new[] { "store", "workshop", "valves", "drafting", "workshop", "machine", "office", "store" }, g.BrickG, g.BrickC, g.PolOld);
        foreach (int pu in new[] { -24, 12 })
            GmRoom(g, f, pu, 0, -HW - 7, pu + 1, 2, -HW - 1, 1, GmMasonry(g, f.Y(0), f.Y(2), g.PolOld, g.BrickG, g.BrickC, g.BrickB, g.BrickG));
        var door = GmMasonry(g, f.Y(0), f.Y(40), g.IronR, g.SteelR, g.SteelR, g.SteelR, g.SteelR);
        GmRoom(g, f, -HU - 4, Mez, 16, -HU, Mez + 4, 20, 1, door);
        GmRoom(g, f, HU, Mez, 16, HU + 4, Mez + 4, 20, 1, door);
        GmRoom(g, f, -2, Mez, HW, 2, Mez + 4, HW + 4, 1, door);
        work.Ports["u-"] = f.Port(-HU - 5, Mez, 18, -1, 0);
        work.Ports["u+"] = f.Port(HU + 5, Mez, 18, 1, 0);
        work.Ports["w+"] = f.Port(0, Mez, HW + 5, 0, 1);
        work.Ports["w-"] = f.Port(-39, 0, -HW - 9, -1, 0);

        g.Details.Add(() =>
        {
            // ── the crankshaft on its pedestals ──
            GT(g, f, -40, 3, 0, 34, 3, 0, 1.6, (x, y, z, d) => d < 0.8 ? g.Steel : g.SteelR);
            for (int pu = -38; pu <= 22; pu += 10)
                GBox(g, f, pu - 1, -3, -2, pu + 1, 1, 2, (u, v, w) => v == 1 ? g.Bronze : g.BrickB);
            // ── the flywheel: a rim and eight spokes, no teeth ──
            GW(g, f, 27, 3, 0, 'u', 16, 4, 8, 0.2, () => g.R() < 0.75 ? g.RustA : g.SteelR, false);
            GT(g, f, 26, 3, 0, 32, 3, 0, 3.2, (x, y, z, d) => g.SteelR);
            // ── three engines ──
            foreach (int eu in new[] { -26, -10, 6 })
            {
                // A-frame legs
                foreach (int sw in new[] { -1, 1 })
                {
                    GT(g, f, eu - 4, 0, sw * 8, eu - 1, 24, sw * 4, 0.9, (x, y, z, d) => g.SteelR);
                    GT(g, f, eu + 4, 0, sw * 8, eu + 1, 24, sw * 4, 0.9, (x, y, z, d) => g.SteelR);
                    for (int v = 6; v <= 18; v += 6) GT(g, f, eu - 3, v, sw * (8 - v / 6.0), eu + 3, v, sw * (8 - v / 6.0), 0.5, (x, y, z, d) => g.IronR);
                    // crosshead guides
                    GT(g, f, eu, 9, sw * 2, eu, 20, sw * 2, 0.5, (x, y, z, d) => g.Steel);
                }
                // the cylinder and its valve chest
                GT(g, f, eu, 24, 0, eu, 34, 0, 4.5, (x, y, z, d) => (y - f.Y(0)) % 3 == 0 && d > 3.6 ? g.SteelR : d > 3.6 ? g.Rust() : g.IronR);
                GT(g, f, eu, 34, 0, eu, 35, 0, 5.3, (x, y, z, d) => g.SteelR);
                GBox(g, f, eu + 5, 26, -2, eu + 7, 32, 2, (u, v, w) => (v + w) % 3 == 0 ? g.SteelR : g.Copper);
                // piston rod, crosshead, connecting rod to the crank
                GT(g, f, eu, 23, 0, eu, 14, 0, 0.6, (x, y, z, d) => g.Steel);
                GBox(g, f, eu - 1, 13, -1, eu + 1, 14, 1, (u, v, w) => g.SteelR);
                GT(g, f, eu, 13, 0, eu + 1.5, 5, 0, 0.8, (x, y, z, d) => g.Steel);
                // the crank web
                GW(g, f, eu + 1, 3, 0, 'u', 3.2, 2, 0, 0, () => g.SteelR, false);
                // gauges, oil cups and a lamp at the engine's foot
                GD(g, f, eu, 1, -9, "gauge3", 0, -1);
                GD(g, f, eu + 2, 0, 9, "lever2-aged", 0, 1);
                GL(g, f, eu - 5, 0, 8, "lumi-floor9", 0, 1);
                GL(g, f, eu + 5, 0, -8, "lumi-floor9", 0, -1);
                GC(g, f, eu + 3, 0, -9, "barrel-metal2", 0, -1);
            }
            // ── railing round the pit, plates over the crank race ──
            for (int u = 23; u <= 34; u++)
                foreach (int w in new[] { -20, 20 }) GRail(g, f, u, 0, w, 'u');
            for (int w = -20; w <= 20; w++)
                foreach (int u in new[] { 23, 34 }) if (Math.Abs(w) > 3) GRail(g, f, u, 0, w, 'w');
            for (int u = -38; u <= 22; u++)
                foreach (int w in new[] { -3, 3 }) GS(g, f, u, -1, w, g.IronR);
            // ── the overhead crane ──
            for (int u = -HU; u <= HU; u++)
                foreach (int sw in new[] { -1, 1 }) { GS(g, f, u, 30, sw * HW, g.SteelR); GS(g, f, u, 31, sw * HW, g.Steel); GS(g, f, u, 30, sw * (HW - 1), g.IronR); }
            for (int w = -HW + 1; w <= HW - 1; w++) { GS(g, f, -2, 32, w, g.SteelR); GS(g, f, -1, 32, w, g.SteelR); GS(g, f, -2, 33, w, g.RustA); GS(g, f, -1, 33, w, g.RustA); }
            GBox(g, f, -3, 30, 6, 0, 31, 9, (u, v, w) => g.RustA);
            for (int v = 29; v >= 18; v--) GC(g, f, -2, v, 7, v == 18 ? "chains/chain-end-hook" : "chains/chain-section", 0, 1);
            // ── the mezzanine and its consoles ──
            for (int u = -40; u <= 40; u++)
            {
                for (int w = 15; w <= HW; w++) GS(g, f, u, Mez - 1, w, (u + w) % 4 == 0 ? g.SteelR : g.PlanksA);
                if (u < -24 || u > -18) GRail(g, f, u, Mez, 15, 'u');
                if (u % 4 == 0)
                {
                    string t = g.Pick(new[] { "switch1-aged", "switch3-aged", "lever1-aged", "lever4-aged", "gauge1", "gauge4", "sightglass2-aged", "valve2-aged" });
                    GD(g, f, u, Mez, HW, t, 0, -1);
                }
                if (u % 10 == 0) GL(g, f, u, Mez + 2, HW, "gas-wall2", 0, -1);
                if (u % 8 == 0) for (int v = 0; v < Mez - 1; v++) GS(g, f, u, v, 15, v % 4 == 0 ? g.SteelR : g.Iron);
            }
            for (int u = -HU; u < -40; u++) for (int w = 15; w <= HW; w++) GS(g, f, u, Mez - 1, w, g.IronR);
            for (int u = 41; u <= HU; u++) for (int w = 15; w <= HW; w++) GS(g, f, u, Mez - 1, w, g.IronR);
            GStair(g, f, -44, 1, 11, 14, 0, Mez, g.BrickG);
            // ── light ──
            for (int u = -HU + 3; u <= HU; u += 9)
            {
                GL(g, f, u, H, -10, "gas-ceiling1", 0, 1);
                GL(g, f, u, H, 10, "gas-ceiling1", 0, 1);
                GL(g, f, u, 5, -HW, "gas-wall1", 0, 1);
            }
            // glowing seams down the cylinders' sides
            foreach (int eu in new[] { -26, -10, 6 })
                for (int v = 25; v <= 33; v++) { GG(g, f, eu + 4, v, 0); GG(g, f, eu - 4, v, 0); }
            // ── clutter ──
            for (int i = 0; i < 60; i++)
            {
                int u = g.RI(-HU + 1, HU - 1), w = g.RI(-HW + 1, 14);
                if (Math.Abs(w) <= 10 && u > -36 && u < 36) continue;
                string t = g.Pick(new[] { "barrel-metal1", "barrel-metal3", "pile-tools2", "mech-part1", "mech-part4", "mechanics-ruined3", "rubble-cracked2",
                    "!gear12", "!brokengear/small01", "!junkpipe1", "!pipelong3-aged", "crate/crate-medium-junk", "toolrack-ruined2" });
                if (t[0] == '!') GD(g, f, u, 0, w, t.Substring(1), 0, 1); else GC(g, f, u, 0, w, t, 0, 1);
            }
            g.K.Job.StructChests.Add((new Vintagestory.API.MathTools.BlockPos(f.X(-44, 21), f.Y(Mez), f.Z(-44, 21), g.K.Job.Dim), "normal-aged",
                RollLoot(g.K.Rand, MachineLoot, 5 + g.RI(0, 2)), "engine gallery"));
        });
    }
}
