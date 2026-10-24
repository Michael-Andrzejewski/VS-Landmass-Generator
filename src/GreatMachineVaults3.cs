using System;
using System.Collections.Generic;
using System.Linq;

namespace LandmassGenerator;

// The ten works, continued (GreatMachineVaults.cs): the Foundry, the Axle
// Well and the Cistern, deep in the ring round the abyss.
public partial class LandmassGeneratorModSystem
{
    // ═════════════════════════════════════════════════════════════════════
    // 5. THE FOUNDRY
    // ═════════════════════════════════════════════════════════════════════
    // Brainstorm: the machine's plates, gears and chains were cast here. A
    // square hall 65 across and 42 tall walled in refractory brick, its
    // middle sunk 4 into a casting floor reached by steps. On four squat
    // pillars over the casting floor stands the crucible, a bowl 19 across
    // still brimming with lava, under a hood that narrows into a flue to
    // the roof. Molds lie on the casting floor beside it, most holding
    // cooled copper and iron, two still molten. A gantry crane runs on
    // rails along two walls with a ladle of molten metal hanging from its
    // chains. Quench tanks of water stand along the outer wall, slag heaps
    // in the corners, anvils, swage blocks and racks of tools along the
    // inner one. Nothing that burns is left within reach of the lava. A
    // wing outside holds the pattern shop, the foreman's office, the
    // moulders' mess and the ingot stores.
    //
    // Frame: hall u -32..32, w -32..32, v 0..42; casting floor u, w
    // -18..18 at v -4; crucible round (0, 0) on v 16; wing hall at w 41.
    private void GmFoundry(GmCtx g, GmFrame f, GmWork work)
    {
        const int HS = 32, H = 42, Pit = 18;
        var shell = (Func<int, int, int, int>)((x, y, z) =>
        {
            if (y < f.Y(-4)) return g.RefrG;
            if (y < f.Y(0)) return g.K.Hash01(x, z) < 0.3 ? g.BrickB : g.RefrG;
            if (y > f.Y(H)) return g.BurntBrick;
            return (y - f.Y(0)) % 7 == 6 ? g.SteelR : g.K.Hash01(x * 3 + y, z * 5) < 0.25 ? g.BurntBrick : g.RefrG;
        });
        GmRoom(g, f, -HS, 0, -HS, HS, H, HS, 3, shell);
        GmRoom(g, f, -Pit, -4, -Pit, Pit, -1, Pit, 2, shell);
        GmWing(g, f, -28, 28, HS + 9, 0, 0, 8, new[] { "workshop", "office", "mess", "store", "store", "drafting", "workshop", "kitchen" }, g.BurntBrick, g.BrickC, g.PolB);
        foreach (int pu in new[] { -14, 14 })
            GmRoom(g, f, pu, 0, HS + 1, pu + 1, 2, HS + 7, 1, GmMasonry(g, f.Y(0), f.Y(2), g.PolB, g.BurntBrick, g.BrickC, g.BrickB, g.BurntBrick));
        var door = GmMasonry(g, f.Y(0), f.Y(H), g.IronR, g.SteelR, g.SteelR, g.SteelR, g.SteelR);
        GmRoom(g, f, -HS - 4, 0, -2, -HS, 4, 2, 1, door);
        GmRoom(g, f, HS, 0, -2, HS + 4, 4, 2, 1, door);
        GmRoom(g, f, -2, 0, -HS - 4, 2, 4, -HS, 1, door);
        work.Ports["u-"] = f.Port(-HS - 5, 0, 0, -1, 0);
        work.Ports["u+"] = f.Port(HS + 5, 0, 0, 1, 0);
        work.Ports["w-"] = f.Port(0, 0, -HS - 5, 0, -1);
        work.Ports["w+"] = f.Port(31, 0, HS + 9, 1, 0);

        g.Details.Add(() =>
        {
            // ── steps down to the casting floor on two sides ──
            GStair(g, f, 11, 1, -2, 2, -4, 0, g.RefrG);
            GStair(g, f, -11, -1, -2, 2, -4, 0, g.RefrG);
            for (int s = -Pit; s <= Pit; s++)
                foreach (var (u, w) in new[] { (s, Pit + 1), (s, -Pit - 1), (Pit + 1, s), (-Pit - 1, s) })
                    if (Math.Abs(s) > 3 || Math.Abs(w) > Pit) GRail(g, f, u, 0, w, Math.Abs(w) > Pit ? 'u' : 'w');
            // ── the crucible on its pillars, under its hood ──
            foreach (int su in new[] { -6, 5 })
                foreach (int sw in new[] { -6, 5 })
                    GBox(g, f, su, -4, sw, su + 1, 11, sw + 1, (u, v, w) => v % 4 == 0 ? g.SteelR : g.RefrD);
            for (int u = -10; u <= 10; u++)
                for (int w = -10; w <= 10; w++)
                    for (int v = 6; v <= 16; v++)
                    {
                        double d = Math.Sqrt(u * u + (v - 16) * (v - 16) + w * w);
                        if (d > 9.5) continue;
                        if (d > 7.5) GS(g, f, u, v, w, v == 16 ? g.SteelR : g.K.Hash01(u * 7 + v, w * 3) < 0.3 ? g.BrickB : g.RefrG);   // full blocks only: it holds lava
                        else if (v <= 15) g.Fluid(f.X(u, w), f.Y(v), f.Z(u, w), g.Lava);
                    }
            // the spout: a lip of plate on the side toward the molds
            GBox(g, f, -12, 15, -1, -9, 16, 1, (u, v, w) => v == 15 ? g.RefrG : (w == 0 ? 0 : g.SteelR));
            for (int v = 24; v <= 34; v++)
            {
                double rr = 10 - (v - 24) * 0.7;
                for (int u = -11; u <= 11; u++)
                    for (int w = -11; w <= 11; w++)
                    {
                        double q = Math.Sqrt(u * u + w * w);
                        if (q > rr || q < rr - 1.3) continue;
                        GS(g, f, u, v, w, v % 3 == 0 ? g.SteelR : g.RefrD);
                    }
            }
            GT(g, f, 0, 34, 0, 0, H + 1, 0, 3, (x, y, z, d) => (y - f.Y(0)) % 4 == 0 ? g.SteelR : g.BurntBrick);
            // ── the molds: cooled metal, two still molten ──
            for (int i = 0; i < 6; i++)
            {
                int mu = -16 + (i % 3) * 5, mw = i < 3 ? -15 : 12;
                bool molten = i == 1 || i == 4;
                for (int u = mu - 1; u <= mu + 3; u++)
                    for (int w = mw - 1; w <= mw + 2; w++)
                    {
                        bool rim = u == mu - 1 || u == mu + 3 || w == mw - 1 || w == mw + 2;
                        if (rim) GS(g, f, u, -4, w, g.RefrG);
                        else if (molten) g.Fluid(f.X(u, w), f.Y(-4), f.Z(u, w), g.Lava);
                        else GS(g, f, u, -5, w, i % 2 == 0 ? g.CopperC : g.IronR);
                    }
            }
            // the runner: a stone trough from the spout to the molds
            for (int u = -16; u <= -9; u++)
            {
                GS(g, f, u, -4, -2, g.RefrG); GS(g, f, u, -4, 2, g.RefrG);
                GS(g, f, u, -5, 0, g.Drock);
            }
            // ── the gantry crane and its ladle ──
            for (int u = -HS; u <= HS; u++)
                foreach (int sw in new[] { -1, 1 }) { GS(g, f, u, 32, sw * HS, g.SteelR); GS(g, f, u, 33, sw * HS, g.Steel); GS(g, f, u, 32, sw * (HS - 1), g.IronR); }
            for (int w = -HS + 1; w <= HS - 1; w++)
                foreach (int u in new[] { 17, 18 }) { GS(g, f, u, 34, w, g.SteelR); GS(g, f, u, 35, w, g.RustA); }
            GBox(g, f, 16, 32, 6, 19, 33, 10, (u, v, w) => g.RustA);
            for (int v = 31; v >= 22; v--) GC(g, f, 17, v, 8, v == 22 ? "chains/chain-end-hook" : "chains/chain-section", 0, 1);
            for (int u = 14; u <= 21; u++)
                for (int w = 5; w <= 12; w++)
                    for (int v = 15; v <= 21; v++)
                    {
                        double q = Math.Sqrt((u - 17.5) * (u - 17.5) + (w - 8.5) * (w - 8.5));
                        if (q > 3.6) continue;
                        if (v == 15 || q > 2.6) GS(g, f, u, v, w, v == 21 ? g.SteelR : g.RustA);
                        else if (v == 16) g.Fluid(f.X(u, w), f.Y(v), f.Z(u, w), g.Lava);
                    }
            // ── quench tanks along the outer wall ──
            foreach (int tu in new[] { -22, -6, 10 })
                for (int u = tu; u <= tu + 6; u++)
                    for (int w = 25; w <= 30; w++)
                        for (int v = 0; v <= 3; v++)
                        {
                            bool wallCell = u == tu || u == tu + 6 || w == 25 || w == 30 || v == 0;
                            if (wallCell) GS(g, f, u, v, w, v == 3 ? g.SteelR : g.IronR);
                            else if (v <= 2) g.Fluid(f.X(u, w), f.Y(v), f.Z(u, w), g.Water);
                        }
            // ── slag heaps in the inner corners ──
            foreach (int su in new[] { -1, 1 })
            {
                double hx = su * 26, hz = -26;
                for (int u = -6; u <= 6; u++)
                    for (int w = -6; w <= 6; w++)
                    {
                        double q = Math.Sqrt(u * u + w * w);
                        int hh = (int)Math.Round(5 * (1 - q / 6.5) + 1.5 * GmNoise2(u * 0.4 + su, w * 0.4, g.Seed + 801));
                        for (int v = 0; v < hh; v++)
                            GS(g, f, (int)hx + u, v, (int)hz + w, g.K.Hash01(u * 3 + v, w * 7 - v) < 0.4 ? g.Obsid : g.K.Hash01(u, w + v) < 0.5 ? g.Drock : g.Basalt);
                    }
            }
            // ── the smiths' side: anvils, swages, racks, ingots ──
            for (int u = -18; u <= 18; u += 3)
            {
                if (Math.Abs(u) <= 3) continue;   // the doorway
                string t = g.Pick(new[] { "anvil-broken1", "anvil-broken2", "anvil-broken3", "swageblock1", "stakeblowhorn", "wiredrawdie", "toolrack-ruined1",
                    "pile-tools3", "pile-tools4", "grinding-stone" });
                GC(g, f, u, 0, -HS, t, 0, 1);
                GS(g, f, u + 1, 0, -HS + 1, g.R() < 0.5 ? g.PartsS : g.PartsM);
            }
            for (int i = 0; i < 30; i++)
            {
                int u = g.RI(-HS + 1, HS - 1), w = g.RI(-HS + 1, HS - 1);
                int m = Math.Max(Math.Abs(u), Math.Abs(w));
                if (m <= Pit + 4 || w >= 24) continue;
                if (g.R() < 0.4) GS(g, f, u, 0, w, g.Pick(new[] { g.Gears1, g.Gears3, g.PartsS, g.PartsM }));
                else GD(g, f, u, 0, w, g.Pick(new[] { "brokengear/small03", "junksheet1", "junkchain2", "pipelong4-aged", "miscmedium1-aged" }), 0, 1);
            }
            // ── light: the lava, gas lamps on every wall ──
            for (int s = -26; s <= 26; s += 9)
            {
                if (Math.Abs(s) <= 3) continue;
                foreach (var (u, w, du, dw) in new[] { (s, HS, 0, -1), (s, -HS, 0, 1), (HS, s, -1, 0), (-HS, s, 1, 0) })
                {
                    GL(g, f, u, 4, w, "gas-wall1", du, dw);
                    GL(g, f, u, 20, w, "gas-wall2", du, dw);
                }
            }
            foreach (var (u, w) in new[] { (Pit + 2, Pit + 2), (-Pit - 2, Pit + 2), (Pit + 2, -Pit - 2), (-Pit - 2, -Pit - 2) })
                GL(g, f, u, 0, w, "lumi-floor9", -u, -w);
            g.K.Job.StructChests.Add((new Vintagestory.API.MathTools.BlockPos(f.X(28, -28), f.Y(0), f.Z(28, -28), g.K.Job.Dim), "normal-aged",
                RollLoot(g.K.Rand, MachineLoot, 5 + g.RI(0, 2)), "foundry"));
        });
    }

    // ═════════════════════════════════════════════════════════════════════
    // 6. THE AXLE WELL
    // ═════════════════════════════════════════════════════════════════════
    // Brainstorm: straight under the hanging machine, under the abyss and
    // its lava, lies what once turned it: a round well 89 across, a colossal
    // axle rising through its middle from a thrust bearing in the floor to
    // the roof, glowing seams up its sides. Three crown gears ride the axle
    // at three heights, largest at the bottom, with pinions on radial
    // shafts standing on the biggest and running into the walls. A gallery
    // rings the wall, reached by two spiral stairs, and four catwalks cross
    // from it to the axle between the gears. Great dials on the walls show
    // what the machine was doing when it stopped. Six spoke pipes leave at
    // the floor for the six works of the ring.
    //
    // Frame: round (0, 0) radius 44, v 0..24, at the crater centre.
    private void GmAxleWell(GmCtx g, GmFrame f, GmWork work)
    {
        const double R = 44;
        const int H = 24, Gal = 12;
        GmCylW(g, f.Ox, f.Oz, R, f.Y(0), f.Y(H), 0, 2, (x, y, z) =>
            y < f.Y(0) ? (g.K.Hash01(x, z) < 0.2 ? g.SteelR : g.IronR) : y > f.Y(H) ? g.RustA : (y - f.Y(0)) % 6 == 0 ? g.Brass : g.Rust());
        var door = GmMasonry(g, f.Y(0), f.Y(H), g.IronR, g.SteelR, g.SteelR, g.SteelR, g.SteelR);
        var spokeAngles = new List<double>();
        foreach (var (name, bearing) in new[] { ("Resonator", 25.0), ("Cistern", 90.0), ("Boiler Cathedral", 155.0), ("Foundry", 225.0), ("Deep Bore", 280.0), ("Engine Gallery", 335.0) })
        {
            double th = bearing * Math.PI / 180;
            int dx = Math.Abs(Math.Cos(th)) >= Math.Abs(Math.Sin(th)) ? Math.Sign(Math.Cos(th)) : 0, dz = dx == 0 ? Math.Sign(Math.Sin(th)) : 0;
            int px = f.Ox + (int)Math.Round(Math.Cos(th) * 49), pz = f.Oz + (int)Math.Round(Math.Sin(th) * 49);
            GmBoxW(g, px - dx * 12 - (dz != 0 ? 2 : 0), f.Y(0), pz - dz * 12 - (dx != 0 ? 2 : 0), px + (dz != 0 ? 2 : 0), f.Y(4), pz + (dx != 0 ? 2 : 0), 1, door);
            work.Ports[name] = new GmPort { X = px + dx, Y = f.Y(0), Z = pz + dz, Dx = dx, Dz = dz };
            spokeAngles.Add(th);
        }
        // two spiral stairs up to the gallery
        var stairAt = new List<(double U, double W)>();
        foreach (double sa in new[] { 55.0, 245.0 })
        {
            double th = sa * Math.PI / 180;
            double su = Math.Cos(th) * 37, sw = Math.Sin(th) * 37;
            stairAt.Add((su, sw));
            int sx = (int)Math.Round(f.Xd(su, sw)), sz = (int)Math.Round(f.Zd(su, sw));
            GmShaft(g, sx, sz, new() { (th + Math.PI, f.Y(0)), (th, f.Y(Gal + 1)) }, 3.5, null, false, false);
        }

        g.Details.Add(() =>
        {
            // ── the axle, its thrust bearing, its glowing seams ──
            for (int u = -10; u <= 10; u++)
                for (int w = -10; w <= 10; w++)
                {
                    double q = Math.Sqrt(u * u + w * w);
                    if (q <= 9.4) { GS(g, f, u, 0, w, q > 8 ? g.SteelR : g.Bronze); GS(g, f, u, 1, w, q > 7 ? g.SteelR : g.Bronze); }
                    if (q <= 5.2)
                        for (int v = 2; v <= H + 3; v++)
                        {
                            bool seam = q > 4.2 && (Math.Abs(u) <= 0 || Math.Abs(w) <= 0) && v % 3 != 0;
                            if (seam) GG(g, f, u, v, w);
                            else GS(g, f, u, v, w, (v - 2) % 5 == 0 ? g.SteelR : g.Steel);
                        }
                }
            // ── three crown gears, pinions on the largest ──
            GW(g, f, 0, 3, 0, 'y', 31, 2, 10, 0.1, () => g.R() < 0.6 ? g.Bronze : g.RustA);
            GW(g, f, 0, 8, 0, 'y', 22, 2, 8, 0.5, () => g.R() < 0.6 ? g.Brass : g.RustB);
            GW(g, f, 0, 17, 0, 'y', 13, 2, 6, 0.9, () => g.R() < 0.6 ? g.Bronze : g.SteelR);
            foreach (var (pu, pw, axis) in new[] { (30, 0, 'u'), (-30, 0, 'u'), (0, -30, 'w') })
            {
                double ou = Math.Sign(pu) * 44.5, ow = Math.Sign(pw) * 44.5;
                GT(g, f, pu, 10, pw, pu == 0 ? 0 : ou, 10, pw == 0 ? 0 : ow, 1.5, (x, y, z, d) => d < 0.7 ? g.Steel : g.SteelR);
                GW(g, f, pu, 10, pw, axis, 5, 2, 4, 0.3, () => g.RustA);
            }
            // ── the gallery round the wall, the catwalks to the axle ──
            for (int u = -44; u <= 44; u++)
                for (int w = -44; w <= 44; w++)
                {
                    double q = Math.Sqrt(u * u + w * w);
                    if (q > R || q < 39.5) continue;
                    if (stairAt.Any(s => (u - s.U) * (u - s.U) + (w - s.W) * (w - s.W) <= 13)) continue;
                    GS(g, f, u, Gal, w, (u + w) % 4 == 0 ? g.SteelR : g.IronR);
                    if (q < 40.5) GRail(g, f, u, Gal + 1, w, Math.Abs(u) > Math.Abs(w) ? 'w' : 'u');
                }
            foreach (double ca in new[] { 45.0, 135, 225, 315 })
            {
                double th = ca * Math.PI / 180, cu = Math.Cos(th), cw = Math.Sin(th);
                for (double t = 5.5; t <= 40.5; t += 0.4)
                    for (double off = -1.6; off <= 1.6; off += 0.4)
                    {
                        int u = (int)Math.Round(cu * t - cw * off), w = (int)Math.Round(cw * t + cu * off);
                        GS(g, f, u, Gal, w, g.IronR);
                        if (Math.Abs(off) > 1.4) GRail(g, f, u, Gal + 1, w, 'u');
                    }
            }
            // ── the dials ──
            foreach (var (du, dw) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int cu = du * 43, cw = dw * 43;
                for (int a = -3; a <= 3; a++)
                    for (int b = -3; b <= 3; b++)
                    {
                        double q = Math.Sqrt(a * a + b * b);
                        if (q > 3.4) continue;
                        int u = cu + (dw != 0 ? a : 0), w = cw + (du != 0 ? a : 0);
                        GS(g, f, u, 18 + b, w, q > 2.5 ? g.Brass : g.Glass);
                    }
                for (int t = 0; t <= 2; t++) GS(g, f, cu - du + (dw != 0 ? t : 0), 18 + t, cw - dw + (du != 0 ? t : 0), g.Steel);
                GG(g, f, cu + 2 * du, 18, cw + 2 * dw);
            }
            // ── light ──
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4 + 0.2;
                GL(g, f, (int)Math.Round(Math.Cos(a) * 11), 0, (int)Math.Round(Math.Sin(a) * 11), "lumi-floor" + (i % 2 == 0 ? "1" : "8"), -Math.Cos(a), -Math.Sin(a));
            }
            for (int i = 0; i < 24; i++)
            {
                double a = i * Math.PI / 12;
                double wa = Math.Atan2(f.Zd(Math.Cos(a), Math.Sin(a)) - f.Oz, f.Xd(Math.Cos(a), Math.Sin(a)) - f.Ox);
                if (spokeAngles.Any(sa => Math.Abs(Math.IEEERemainder(sa - wa, Math.PI * 2)) < 0.2)) continue;
                GL(g, f, (int)Math.Round(Math.Cos(a) * 43.4), 3, (int)Math.Round(Math.Sin(a) * 43.4), "gas-wall1", -Math.Cos(a), -Math.Sin(a));
                if (i % 2 == 0) GL(g, f, (int)Math.Round(Math.Cos(a) * 43.4), Gal + 3, (int)Math.Round(Math.Sin(a) * 43.4), "gas-wall2", -Math.Cos(a), -Math.Sin(a));
                if (i % 3 == 0) GL(g, f, (int)Math.Round(Math.Cos(a) * 42), H, (int)Math.Round(Math.Sin(a) * 42), "gas-ceiling1", 0, 1);
            }
            // ── clutter: broken gears, gearboxes, oil, loose gears ──
            for (int i = 0; i < 80; i++)
            {
                double a = g.R() * Math.PI * 2, q = 12 + g.R() * 30;
                int u = (int)Math.Round(Math.Cos(a) * q), w = (int)Math.Round(Math.Sin(a) * q);
                double r0 = g.R();
                if (r0 < 0.3) GS(g, f, u, 0, w, g.Pick(new[] { g.Gears1, g.Gears3, g.PartsS }));
                else if (r0 < 0.7) GD(g, f, u, 0, w, g.Pick(new[] { "brokengear/big01", "brokengear/big02", "brokengear/big03", "gearbox1", "geartrain3", "gear24b", "gear36", "gear1290" }), Math.Cos(a), Math.Sin(a));
                else GC(g, f, u, 0, w, g.Pick(new[] { "barrel-metal1", "barrel-metal2", "mech-part6", "mechanics-ruined5", "pile-tools1", "rubble-cracked4" }), Math.Cos(a), Math.Sin(a));
            }
        });
    }

    // ═════════════════════════════════════════════════════════════════════
    // 7. THE CISTERN
    // ═════════════════════════════════════════════════════════════════════
    // Brainstorm: the machine ran hot enough to melt itself, and the water
    // that cooled it was kept here: a pillared cistern 77 long and 53 wide
    // in the old way, a forest of brick columns rising out of a still pool
    // 4 deep to arches under the roof, lit from below by lights set in the
    // pool floor and on the columns. Stone causeways cross it, one down the
    // middle and three across, to the pump house at its north end: a
    // raised floor with three beam pumps, each a steam cylinder, an A-frame
    // pivot, a rocking beam and a pump rod down into its well in the pool.
    // Three intake pipes rise from the water at the far end to the roof;
    // iron screens filter the pool. A wing beside it holds the valve
    // gallery, the pump keepers' room and the stores.
    //
    // Frame: hall u -38..38, w -26..26, v 0..34; water v 0..3; causeways
    // and the pump house floor at v 4 (walked on at 5); wing hall at w 35.
    private void GmCistern(GmCtx g, GmFrame f, GmWork work)
    {
        const int HU = 38, HW = 26, H = 34, Walk = 5;
        var shell = GmMasonry(g, f.Y(0), f.Y(H), g.PolG, g.BrickG, g.BrickC, g.BrickB, g.BrickG);
        GmRoom(g, f, -HU, 0, -HW, HU, H, HW, 3, shell);
        GmWing(g, f, -30, 30, HW + 9, Walk, 0, 8, new[] { "valves", "valves", "store", "workshop", "office", "dorm", "store", "valves" }, g.BrickG, g.BrickC, g.PolG);
        foreach (int pu in new[] { -18, 17 })            // on the cross causeways
            GmRoom(g, f, pu, Walk, HW + 1, pu + 1, Walk + 2, HW + 7, 1, shell);
        var door = GmMasonry(g, f.Y(0), f.Y(H), g.IronR, g.SteelR, g.SteelR, g.SteelR, g.SteelR);
        GmRoom(g, f, -HU - 4, Walk, -2, -HU, Walk + 4, 2, 1, door);
        GmRoom(g, f, HU, Walk, -2, HU + 4, Walk + 4, 2, 1, door);
        GmRoom(g, f, -2, Walk, -HW - 4, 2, Walk + 4, -HW, 1, door);
        work.Ports["u-"] = f.Port(-HU - 5, Walk, 0, -1, 0);
        work.Ports["u+"] = f.Port(HU + 5, Walk, 0, 1, 0);
        work.Ports["w-"] = f.Port(0, Walk, -HW - 5, 0, -1);
        work.Ports["w+"] = f.Port(31, Walk, HW + 9, 1, 0);

        int[] pu4 = { -32, -24, -12, -4, 4, 12, 24 };
        int[] pw4 = { -20, -12, -4, 4, 12, 20 };
        int[] crossU = { -18, 0, 18 };
        const int PumpU = 28;
        bool Pillar(int u, int w) => pu4.Any(p => Math.Abs(u - p) <= 1) && pw4.Any(p => Math.Abs(w - p) <= 1) && u < PumpU;
        bool Causeway(int u, int w) => Math.Abs(w) <= 1 || crossU.Any(c => Math.Abs(u - c) <= 1) || u >= PumpU;

        g.Details.Add(() =>
        {
            // ── columns, with arches between them under the roof ──
            foreach (int pu in pu4)
                foreach (int pw in pw4)
                {
                    for (int u = pu - 1; u <= pu + 1; u++)
                        for (int w = pw - 1; w <= pw + 1; w++)
                            for (int v = -1; v <= H; v++)
                                GS(g, f, u, v, w, v <= 0 || v >= H - 1 ? g.PolB : v % 6 == 5 ? g.BrickB : g.BrickG);
                    // lamps on the faces toward the causeways, at the walk's height
                    if (Math.Abs(pw) == 4) GL(g, f, pu, Walk + 2, pw - Math.Sign(pw) * 2, "gas-wall2", 0, -Math.Sign(pw));
                }
            void Arch(int u0, int w0, int u1, int w1)
            {
                double len = Math.Sqrt((u1 - u0) * (u1 - u0) + (w1 - w0) * (w1 - w0));
                for (double t = 0; t <= 1; t += 0.5 / len)
                {
                    double v = 26 + 7 * Math.Sin(t * Math.PI);
                    int u = (int)Math.Round(u0 + (u1 - u0) * t), w = (int)Math.Round(w0 + (w1 - w0) * t);
                    for (int vv = (int)Math.Round(v); vv <= H; vv++) GS(g, f, u, vv, w, vv == (int)Math.Round(v) ? g.BrickB : g.BrickG);
                }
            }
            for (int i = 0; i + 1 < pu4.Length; i++) foreach (int pw in pw4) Arch(pu4[i], pw, pu4[i + 1], pw);
            foreach (int pu in pu4) for (int j = 0; j + 1 < pw4.Length; j++) Arch(pu, pw4[j], pu, pw4[j + 1]);
            // ── the causeways and the pump house floor ──
            for (int u = -HU; u <= HU; u++)
                for (int w = -HW; w <= HW; w++)
                {
                    if (Pillar(u, w)) continue;
                    if (Causeway(u, w))
                        for (int v = 0; v < Walk; v++) GS(g, f, u, v, w, v == Walk - 1 ? ((u + w) % 3 == 0 ? g.PolB : g.PolG) : g.BrickG);
                    else
                    {
                        for (int v = 0; v < 4; v++) g.Fluid(f.X(u, w), f.Y(v), f.Z(u, w), g.Water);
                        if ((u * 7 + w * 3) % 23 == 0) GG(g, f, u, -1, w);        // a light in the pool floor
                    }
                }
            // ── iron screens across the pool ──
            foreach (int su in new[] { -28, 8 })
                for (int w = -HW; w <= HW; w++)
                    if (!Causeway(su, w) && !Pillar(su, w))
                        for (int v = 0; v < 4; v++) GRail(g, f, su, v, w, 'w');
            // ── the beam pumps ──
            foreach (int bw in new[] { -14, 0, 14 })
            {
                GT(g, f, 34, Walk, bw, 34, Walk + 8, bw, 2.5, (x, y, z, d) => (y - f.Y(0)) % 3 == 0 ? g.SteelR : d > 1.6 ? g.Rust() : g.IronR);
                foreach (int s2 in new[] { -3, 3 }) GT(g, f, PumpU, Walk, bw + s2, PumpU, Walk + 15, bw + Math.Sign(s2), 0.8, (x, y, z, d) => g.SteelR);
                GT(g, f, 20, Walk + 15, bw, 37, Walk + 15, bw, 0.9, (x, y, z, d) => g.RustA);
                GT(g, f, 34, Walk + 14, bw, 34, Walk + 9, bw, 0.5, (x, y, z, d) => g.Steel);
                GT(g, f, 21, Walk + 14, bw, 21, 0, bw, 0.5, (x, y, z, d) => g.Steel);
                // the well curb round the pump rod, in the pool
                for (int u = 19; u <= 23; u++)
                    for (int w = bw - 2; w <= bw + 2; w++)
                    {
                        double q = Math.Sqrt((u - 21) * (u - 21) + (w - bw) * (w - bw));
                        if (q > 1.2 && q <= 2.4) for (int v = 0; v < Walk; v++) GS(g, f, u, v, w, g.BrickB);
                    }
                GD(g, f, 31, Walk, bw - 2, g.Pick(new[] { "valve1-aged", "valve2-aged", "gauge1", "sightglass1-aged" }), -1, 0);
                GD(g, f, 31, Walk, bw + 2, g.Pick(new[] { "lever3-aged", "switch2-aged", "gauge3", "valve3-aged" }), -1, 0);
            }
            // ── intake pipes rising from the water at the far end ──
            foreach (int iw in new[] { -16, 0, 16 })
            {
                int iu = -35;
                GT(g, f, iu, 0, iw, iu, H + 1, iw, 2.2, (x, y, z, d) => (y - f.Y(0)) % 6 == 0 ? g.SteelR : d > 1.4 ? g.Rust() : g.Copper);
                for (int v = 3; v < H; v += 6)
                    foreach (var (du, dw) in new[] { (3, 0), (-3, 0), (0, 3), (0, -3) }) GG(g, f, iu + du, v, iw + dw);
            }
            // ── on the causeways ──
            for (int u = -HU + 2; u <= HU; u += 8) GL(g, f, u, Walk, 1, "lumi-floor2", 0, -1);
            foreach (int cu in crossU) for (int w = -HW + 3; w <= HW; w += 8) GL(g, f, cu + 1, Walk, w, "lumi-floor2", -1, 0);
            for (int i = 0; i < 30; i++)
            {
                int u = g.RI(-HU + 1, PumpU - 2), w = g.R() < 0.5 ? g.RI(-1, 1) : g.RI(-HW + 1, HW - 1);
                if (!Causeway(u, w) || Pillar(u, w)) continue;
                GC(g, f, u, Walk, w, g.Pick(new[] { "bucket1", "bucket2", "barrel-metal3", "rubble-cracked1", "pile-trash-scrap", "crate/crate-small-rot", "washboard2" }), 0, 1);
            }
            for (int u = PumpU; u <= HU; u += 2)
                GC(g, f, u, Walk, -HW, g.Pick(new[] { "toolrack-ruined2", "barrel-metal1", "crate/crate-medium-junk", "shelf-tools" }), 0, 1);
            // a lamp hung in every other bay of the roof
            for (int i = 0; i + 1 < pu4.Length; i += 2)
                for (int j = 0; j + 1 < pw4.Length; j++)
                    GL(g, f, (pu4[i] + pu4[i + 1]) / 2, H, (pw4[j] + pw4[j + 1]) / 2, "gas-ceiling1", 0, 1);
            g.K.Job.StructChests.Add((new Vintagestory.API.MathTools.BlockPos(f.X(36, 22), f.Y(Walk), f.Z(36, 22), g.K.Job.Dim), "normal-aged",
                RollLoot(g.K.Rand, MachineLoot, 5 + g.RI(0, 2)), "cistern"));
        });
    }
}
