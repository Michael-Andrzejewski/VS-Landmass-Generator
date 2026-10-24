using System;
using System.Collections.Generic;
using System.Linq;

namespace LandmassGenerator;

// ── THE GREAT MACHINE: THE WORKS BELOW (0.65.0) ─────────────────────────
// Under the crater the island is machine all the way down to the mantle:
// ten buried megastructures (GreatMachineVaults.cs), wings of hallways,
// workrooms and laboratories round them, huge walkable pipes between them
// lit by Jonas lamps and glowing seams, and buried machinery packed into
// the rock everywhere else. Earth covers all of it. Nothing is emptied
// within six blocks of the surface, the abyss or a chasm (the safe
// ceiling, SafeTop), and the only ways in are stairwells from ruined halls
// on the plateau.
//
// Every space is built in passes, so spaces can overlap and still seal:
// buried machinery first, then every space's shell, then every space's
// inside is emptied, then the fittings go in (pipe floors, stairs,
// machines, furniture, lamps). An emptied cell is always inside some
// shell, so a cut never meets bare rock. Below y 80, where the natural sea
// floor keeps pockets of sea water, a shell cell is cleared of water
// before it is set.
public partial class LandmassGeneratorModSystem
{
    private sealed class GmCtx
    {
        public MegaKit K;
        public int Cx, Cz, Sea, G, MapY, RimR, WallR, AbyssR, Yc, SphR, ZStart, ZLand, Seed;
        public Func<int, int, int> Floor;                 // top of the solid ground after every cut
        public Action<int, int, int> Carved;              // record a lowered column
        public List<List<(double X, double Z, double S)>> Chasms;
        public List<(double X0, double Y0, double Z0, double X1, double Y1, double Z1)> Chains = new();
        public List<(double X, double Z, double R)> Keep = new();        // surface spots taken
        public List<(int X, int Y, int Z, int Dx, int Dz)> HallDoors = new();   // plateau halls the stairwells rise into

        // the safe ceiling
        public int SX0, SZ0, SW;
        public short[] Safe;
        public bool Open;                                  // while set, nothing is held under the safe ceiling
        public int SafeTop(int x, int z)
        {
            if (Open) return MapY;
            int i = x - SX0, j = z - SZ0;
            return i < 0 || j < 0 || i >= SW || j >= SW ? -1000 : Safe[j * SW + i];
        }

        public readonly List<Action> Fill = new(), Shells = new(), Carves = new(), Details = new();
        public readonly List<GmWork> Works = new();
        public long Clipped, Placed;
        public int Pipes, PipeLen, Shafts, Rooms, Lamps;
        public const int DryBelow = 80;

        // palette
        public int Granite, Basalt, Andes, Obsid, Drock, BrickA, BrickG, BrickC, BrickB, PolG, PolOld, PolOldC, PolB,
            SlabG, PlanksA, PlanksAW, PlanksE, LogV, ColBot, ColMid, ColTop,
            RustA, RustB, Steel, SteelR, Iron, IronR, Copper, CopperC, Brass, Bronze, Gold,
            Glass, GlassBlue, Ice, IceGlow, RefrG, RefrD, BurntBrick, BurntBrickD, Charred, CharredW, Blackened, Scorched,
            GlowB, GlowG, Lava, Water, FenceNS, FenceEW, Spider, Gears1, Gears3, PartsS, PartsM, Stones, Boulders;

        // Register spaces that are meant to break the surface (the ways in):
        // their passes run with the safe ceiling lifted.
        public void Opened(Action register)
        {
            int f0 = Fill.Count, s0 = Shells.Count, c0 = Carves.Count, d0 = Details.Count;
            register();
            void Wrap(List<Action> list, int from)
            {
                for (int i = from; i < list.Count; i++)
                {
                    var inner = list[i];
                    list[i] = () => { Open = true; try { inner(); } finally { Open = false; } };
                }
            }
            Wrap(Fill, f0); Wrap(Shells, s0); Wrap(Carves, c0); Wrap(Details, d0);
        }

        public double R() => K.R();
        public int RI(int a, int b) => K.RI(a, b);
        public T Pick<T>(T[] a) => a[K.RI(0, a.Length - 1)];
        public int Rust() => K.R() < 0.6 ? RustA : RustB;

        // a solid cell of a shell, a machine or a fitting
        public void Solid(int x, int y, int z, int id)
        {
            if (id == 0 || y < 5 || y > SafeTop(x, z) + 3) return;
            if (y < DryBelow) K.Set(x, y, z, 0);
            K.Set(x, y, z, id);
            Placed++;
        }

        // an emptied cell; never above the safe ceiling
        public void Air(int x, int y, int z)
        {
            if (y < 5) return;
            if (y > SafeTop(x, z)) { Clipped++; return; }
            K.Set(x, y, z, 0);
            Placed++;
        }

        public void Fluid(int x, int y, int z, int id)
        {
            if (id == 0 || y < 5 || y > SafeTop(x, z)) return;
            K.SetFluid(x, y, z, id);
        }

        // Things that are not blocks: plain clutter by default, devastation
        // clutter (machinery) with Dev, a lit Jonas lamp with Lamp.
        public void Clut(int x, int y, int z, string type, double heading)
        {
            if (y < 5 || y > SafeTop(x, z)) return;
            K.Clutter(x, y, z, type.IndexOf('|') > 0 ? type : "clutter|" + type, heading);
        }
        public void Dev(int x, int y, int z, string type, double heading)
        {
            if (y < 5 || y > SafeTop(x, z)) return;
            K.Clutter(x, y, z, type, heading);
        }
        public void Lamp(int x, int y, int z, string type, double heading)
        {
            Lamps++;
            Clut(x, y, z, "jonas-lamp|lightsource/on/" + type, heading);
        }
        public void Shelf(int x, int y, int z, string front, string back, double heading)
            => Clut(x, y, z, "clutteredbookshelf-agedacacia|bookshelves/" + front + "|half|bookshelves/" + back, heading);
        public void Glow(int x, int y, int z, int id = 0)
        {
            if (y < 5 || y > SafeTop(x, z) + 3) return;
            K.Glow(x, y, z, id == 0 ? GlowB : id);
        }
    }

    // A place in the works: its box (for keeping others clear), and the
    // doors the pipes join it at.
    private sealed class GmWork
    {
        public string Name;
        public int X0, Y0, Z0, X1, Y1, Z1;
        public readonly Dictionary<string, GmPort> Ports = new();
        public bool Overlaps(int x0, int z0, int x1, int z1, int y0, int y1, int pad)
            => x0 <= X1 + pad && x1 >= X0 - pad && z0 <= Z1 + pad && z1 >= Z0 - pad && y0 <= Y1 + pad && y1 >= Y0 - pad;
    }

    // The floor cell just outside a doorway, and the way out of it.
    private sealed class GmPort
    {
        public int X, Y, Z, Dx, Dz;
    }

    // An axis-aligned frame: w points away from the crater centre, u along
    // the work's length, v up from the frame's floor.
    private readonly struct GmFrame
    {
        public readonly int Ox, Oy, Oz, Wx, Wz;
        public GmFrame(int ox, int oy, int oz, int wx, int wz) { Ox = ox; Oy = oy; Oz = oz; Wx = wx; Wz = wz; }
        public int Ux => -Wz;
        public int Uz => Wx;
        public int X(int u, int w) => Ox + u * Ux + w * Wx;
        public int Z(int u, int w) => Oz + u * Uz + w * Wz;
        public double Xd(double u, double w) => Ox + u * Ux + w * Wx;
        public double Zd(double u, double w) => Oz + u * Uz + w * Wz;
        public int Y(int v) => Oy + v;
        public double U(double x, double z) => (x - Ox) * Ux + (z - Oz) * Uz;
        public double Wd(double x, double z) => (x - Ox) * Wx + (z - Oz) * Wz;
        public double H(double du, double dw) => Math.Atan2(du * Uz + dw * Wz, du * Ux + dw * Wx);
        public (int X0, int Z0, int X1, int Z1) Rect(int u0, int w0, int u1, int w1)
        {
            int xa = X(u0, w0), xb = X(u1, w1), za = Z(u0, w0), zb = Z(u1, w1);
            return (Math.Min(xa, xb), Math.Min(za, zb), Math.Max(xa, xb), Math.Max(za, zb));
        }
        // the world axis a local axis runs along
        public char Axis(char local) => local == 'u' ? (Ux != 0 ? 'x' : 'z') : (Wx != 0 ? 'x' : 'z');
        public GmPort Port(int u, int v, int w, int du, int dw)
            => new GmPort { X = X(u, w), Y = Y(v), Z = Z(u, w), Dx = du * Ux + dw * Wx, Dz = du * Uz + dw * Wz };
    }

    // ── Spaces ───────────────────────────────────────────────────────────
    // inside(x, y, z, grow): is the cell in the space grown by `grow`? The
    // interior's box is x0..z1; the shell is the grown space minus the space.
    private static void GmSpace(GmCtx g, int x0, int y0, int z0, int x1, int y1, int z1, int wall,
        Func<int, int, int, double, bool> inside, Func<int, int, int, int> shell)
    {
        g.Shells.Add(() =>
        {
            for (int x = x0 - wall; x <= x1 + wall; x++)
                for (int z = z0 - wall; z <= z1 + wall; z++)
                    for (int y = y0 - wall; y <= y1 + wall; y++)
                        if (inside(x, y, z, wall) && !inside(x, y, z, 0)) g.Solid(x, y, z, shell(x, y, z));
        });
        g.Carves.Add(() =>
        {
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                        if (inside(x, y, z, 0)) g.Air(x, y, z);
        });
    }

    private static void GmBoxW(GmCtx g, int x0, int y0, int z0, int x1, int y1, int z1, int wall, Func<int, int, int, int> shell)
    {
        if (x1 < x0) (x0, x1) = (x1, x0);
        if (z1 < z0) (z0, z1) = (z1, z0);
        GmSpace(g, x0, y0, z0, x1, y1, z1, wall,
            (x, y, z, gr) => x >= x0 - gr && x <= x1 + gr && y >= y0 - gr && y <= y1 + gr && z >= z0 - gr && z <= z1 + gr, shell);
    }

    // a box in a frame's local coordinates (v up from the frame floor)
    private static void GmRoom(GmCtx g, GmFrame f, int u0, int v0, int w0, int u1, int v1, int w1, int wall, Func<int, int, int, int> shell)
    {
        var r = f.Rect(u0, w0, u1, w1);
        GmBoxW(g, r.X0, f.Y(v0), r.Z0, r.X1, f.Y(v1), r.Z1, wall, shell);
    }

    // A vaulted hall along a world axis: walls up to `spring`, then a
    // round (or pointed) arch to y1 across the span.
    private static void GmVaultW(GmCtx g, int x0, int y0, int z0, int x1, int y1, int z1, char axis, int spring, bool pointed, int wall,
        Func<int, int, int, int> shell)
    {
        if (x1 < x0) (x0, x1) = (x1, x0);
        if (z1 < z0) (z0, z1) = (z1, z0);
        double qc = axis == 'x' ? (z0 + z1) / 2.0 : (x0 + x1) / 2.0;
        double hs = (axis == 'x' ? z1 - z0 : x1 - x0) / 2.0 + 0.5;
        int a0 = axis == 'x' ? x0 : z0, a1 = axis == 'x' ? x1 : z1;
        GmSpace(g, x0, y0, z0, x1, y1, z1, wall, (x, y, z, gr) =>
        {
            int a = axis == 'x' ? x : z;
            if (a < a0 - gr || a > a1 + gr || y < y0 - gr) return false;
            double dq = Math.Abs((axis == 'x' ? z : x) - qc), hsg = hs + gr;
            if (dq > hsg) return false;
            if (y <= spring) return true;
            double e = dq / hsg, H = y1 - spring + gr;
            double top = spring + H * (pointed ? 1 - Math.Pow(e, 1.7) : Math.Sqrt(Math.Max(0, 1 - e * e)));
            return y <= top + 0.3;
        }, shell);
    }

    private static void GmVault(GmCtx g, GmFrame f, int u0, int v0, int w0, int u1, int v1, int w1, char localAxis, int springV, bool pointed, int wall,
        Func<int, int, int, int> shell)
    {
        var r = f.Rect(u0, w0, u1, w1);
        GmVaultW(g, r.X0, f.Y(v0), r.Z0, r.X1, f.Y(v1), r.Z1, f.Axis(localAxis), f.Y(springV), pointed, wall, shell);
    }

    // An upright cylinder from y0 to y1, with a dome capH high on top.
    private static void GmCylW(GmCtx g, double cx, double cz, double r, int y0, int y1, int capH, int wall, Func<int, int, int, int> shell)
    {
        int ri = (int)Math.Ceiling(r);
        GmSpace(g, (int)Math.Floor(cx) - ri, y0, (int)Math.Floor(cz) - ri, (int)Math.Ceiling(cx) + ri, y1 + capH, (int)Math.Ceiling(cz) + ri, wall, (x, y, z, gr) =>
        {
            double dx = x - cx, dz = z - cz, q = dx * dx + dz * dz, rg = r + gr;
            if (q > rg * rg || y < y0 - gr) return false;
            if (y <= y1) return true;
            if (capH <= 0) return y <= y1 + gr;
            double hy = (y - y1) / (capH + gr);
            return q / (rg * rg) + hy * hy <= 1;
        }, shell);
    }

    private static void GmBallW(GmCtx g, double cx, double cy, double cz, double r, int wall, Func<int, int, int, int> shell)
    {
        int ri = (int)Math.Ceiling(r);
        GmSpace(g, (int)cx - ri, (int)cy - ri, (int)cz - ri, (int)cx + ri, (int)cy + ri, (int)cz + ri, wall, (x, y, z, gr) =>
        {
            double dx = x - cx, dy = y - cy, dz = z - cz;
            return dx * dx + dy * dy + dz * dz <= (r + gr) * (r + gr);
        }, shell);
    }

    // ── Pipes ────────────────────────────────────────────────────────────
    // A walkable pipe through floor points: a bore 3.5 in radius whose axis
    // runs 2 above the floor, an iron shell with riveted steel rings every
    // 8, a glowing seam along its crown, gas lamps on alternate walls, and
    // on the straight runs a rusted conduit along one wall with lumi lamps
    // in it. Sloped runs get a stepped floor in half blocks.
    private static void GmPipe(GmCtx g, List<(double X, double Y, double Z)> pts, bool openEnd = false, double r = 3.5, double wall = 1.6)
    {
        double s0 = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) + (b.Z - a.Z) * (b.Z - a.Z));
            if (len < 0.5) continue;
            GmPipeSeg(g, a.X, a.Y, a.Z, b.X, b.Y, b.Z, r, wall, s0, openEnd && i + 2 == pts.Count);
            s0 += len;
        }
        g.Pipes++;
        g.PipeLen += (int)s0;
    }

    // openB: the far end opens to the air (a way in at the surface): the
    // cap is cut back to a collar.
    private static void GmPipeSeg(GmCtx g, double ax, double ay, double az, double bx, double by, double bz, double r, double wall, double sOff, bool openB = false)
    {
        const double Lift = 2;
        double cay = ay + Lift, cby = by + Lift;
        double dx = bx - ax, dy = cby - cay, dz = bz - az, len2 = dx * dx + dy * dy + dz * dz, len = Math.Sqrt(len2);
        double R = r + wall;
        int x0 = (int)Math.Floor(Math.Min(ax, bx) - R - 1), x1 = (int)Math.Ceiling(Math.Max(ax, bx) + R + 1);
        int y0 = (int)Math.Floor(Math.Min(cay, cby) - R - 1), y1 = (int)Math.Ceiling(Math.Max(cay, cby) + R + 1);
        int z0 = (int)Math.Floor(Math.Min(az, bz) - R - 1), z1 = (int)Math.Ceiling(Math.Max(az, bz) + R + 1);
        (double D, double T) At(int x, int y, int z)
        {
            double px = x - ax, py = y - cay, pz = z - az;
            double t = Math.Clamp((px * dx + py * dy + pz * dz) / len2, 0, 1);
            double qx = px - dx * t, qy = py - dy * t, qz = pz - dz * t;
            return (Math.Sqrt(qx * qx + qy * qy + qz * qz), t);
        }
        g.Shells.Add(() =>
        {
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                    {
                        var (d, t) = At(x, y, z);
                        if (d <= r || d > R) continue;
                        bool ring = ((int)Math.Floor(sOff + t * len) % 8 + 8) % 8 == 0;
                        g.Solid(x, y, z, ring ? g.SteelR : g.K.Hash01(x * 3 + y, z * 5 - y) < 0.12 ? g.CopperC : g.Rust());
                    }
        });
        g.Carves.Add(() =>
        {
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                    {
                        if (At(x, y, z).D <= r) { g.Air(x, y, z); continue; }
                        if (!openB) continue;
                        double px = x - ax, py = y - cay, pz = z - az, t = (px * dx + py * dy + pz * dz) / len2;
                        if (t <= 1 || (t - 1) * len > R + 1) continue;
                        double qx = px - dx * t, qy = py - dy * t, qz = pz - dz * t;
                        if (qx * qx + qy * qy + qz * qz <= r * r) g.Air(x, y, z);
                    }
        });
        g.Details.Add(() =>
        {
            // the floor
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                    {
                        var (d, t) = At(x, y, z);
                        if (d > r) continue;
                        double fy = Math.Round((ay + (by - ay) * t) * 2) / 2.0;
                        int full = (int)Math.Floor(fy);
                        if (y < full) g.Solid(x, y, z, y == full - 1 ? ((x + z) % 5 == 0 ? g.SteelR : g.IronR) : g.Rust());
                        else if (y == full && fy - full > 0.25) g.Solid(x, y, z, g.SlabG);
                    }
            double hl = Math.Sqrt(dx * dx + dz * dz);
            if (hl < 0.5) return;
            double ux = dx / hl, uz = dz / hl, nx = -uz, nz = ux;
            bool straight = Math.Abs(ux) < 0.01 || Math.Abs(uz) < 0.01;
            bool level = Math.Abs(by - ay) < 0.01;
            double heading = Math.Atan2(uz, ux);
            int lampSide = ((int)(sOff / 12)) % 2 == 0 ? 1 : -1;
            for (double s = 1; s < hl - 1; s += 1)
            {
                double t = s / hl, sx = ax + dx * t, sz = az + dz * t, fl = ay + (by - ay) * t, cy = fl + Lift;
                double sa = sOff + s;
                int si = (int)Math.Floor(sa);
                // the glowing seam in the crown, two on, one off
                if (si % 3 != 2) g.Glow((int)Math.Round(sx), (int)Math.Round(cy + r + 0.6), (int)Math.Round(sz));
                // gas lamps on alternate walls every 12
                if (si % 12 == 6)
                {
                    lampSide = -lampSide;
                    int lx = (int)Math.Round(sx + nx * lampSide * (r - 0.6)), lz = (int)Math.Round(sz + nz * lampSide * (r - 0.6));
                    g.Lamp(lx, (int)Math.Round(cy), lz, si % 24 == 6 ? "gas-wall1" : "gas-wall2", Math.Atan2(-nz * lampSide, -nx * lampSide));
                }
                // the conduit along the left wall, a lumi lamp every 6
                if (straight && level)
                {
                    int cx2 = (int)Math.Round(sx + nx * 2), cz2 = (int)Math.Round(sz + nz * 2);
                    g.Clut(cx2, (int)Math.Round(cy) + 2, cz2, si % 6 == 3 ? "jonas-lamp|lightsource/on/lumi-pipe-h-lamp" : "pipe-veryrusted-h-EW", heading);
                }
            }
        });
    }

    // ── Stairwells ───────────────────────────────────────────────────────
    // An upright pipe 9 across round a riveted newel, a spiral stair of
    // half steps from each doorway to the next one up, lit by a glowing
    // ring every 8 and a lamp on the newel at each doorway.
    // rIn: the stair's radius; shell: the shaft's own shell (iron by
    // default); space = false builds only the stair, inside a hall that is
    // already there.
    private static void GmShaft(GmCtx g, int sx, int sz, List<(double A, int Y)> entries, double rIn = 4.5,
        Func<int, int, int, int> shell = null, bool glowRings = true, bool space = true)
    {
        if (entries.Count < 2) return;
        entries = entries.OrderBy(e => e.Y).ToList();
        int yLo = entries[0].Y, yHi = entries[entries.Count - 1].Y, yTop = yHi + 6;
        if (space)
        {
            GmCylW(g, sx, sz, rIn, yLo, yTop, 0, 2, shell ?? ((x, y, z) => y < yLo ? g.IronR : (y - yLo) % 8 == 0 ? g.SteelR : g.Rust()));
            g.Shafts++;
        }
        int ri = (int)Math.Ceiling(rIn);
        g.Details.Add(() =>
        {
            for (int x = sx - ri; x <= sx + ri; x++)
                for (int z = sz - ri; z <= sz + ri; z++)
                {
                    double dx = x - sx, dz = z - sz, q = Math.Sqrt(dx * dx + dz * dz);
                    if (q > rIn) continue;
                    if (q <= 1.2)
                    {
                        for (int y = yLo; y <= yTop; y++) g.Solid(x, y, z, (y - yLo) % 6 == 0 ? g.SteelR : g.IronR);
                        continue;
                    }
                    double th = Math.Atan2(dz, dx);
                    for (int e = 0; e + 1 < entries.Count; e++)
                    {
                        var e0 = entries[e]; var e1 = entries[e + 1];
                        double dyE = e1.Y - e0.Y;
                        if (dyE < 1) continue;
                        double bse = ((e1.A - e0.A) % (Math.PI * 2) + Math.PI * 2) % (Math.PI * 2) / (Math.PI * 2);
                        double T = bse + Math.Max(0, Math.Round(dyE / 7.0 - bse));
                        if (T < 0.15) T += 1;
                        double dTh = T * Math.PI * 2;
                        double phi0 = ((th - e0.A) % (Math.PI * 2) + Math.PI * 2) % (Math.PI * 2);
                        for (double phi = phi0; phi <= dTh + 1e-6; phi += Math.PI * 2)
                        {
                            double h = Math.Round((e0.Y + dyE * phi / dTh) * 2) / 2.0;
                            int full = (int)Math.Floor(h);
                            if (h - full > 0.25) g.Solid(x, full, z, g.SlabG);
                            else g.Solid(x, full - 1, z, (x + z) % 3 == 0 ? g.SteelR : g.IronR);
                        }
                    }
                }
            if (glowRings)
                for (int y = yLo + 4; y < yTop; y += 8)
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        g.Glow(sx + (int)Math.Round(Math.Cos(a) * (rIn + 0.8)), y, sz + (int)Math.Round(Math.Sin(a) * (rIn + 0.8)));
                    }
            foreach (var e in entries)
                g.Lamp(sx + (int)Math.Round(Math.Cos(e.A) * 2), e.Y + 2, sz + (int)Math.Round(Math.Sin(e.A) * 2), "gas-wall2", e.A);
        });
    }

    // ── The router ───────────────────────────────────────────────────────
    // A pipe from one doorway to another: straight out of each, then one
    // or two straight legs at right angles. The legs keep under the safe
    // ceiling and out of the ground under the abyss above y 26; a doorway
    // above that level drops to it through a stairwell, and so does the
    // higher end when the slope between them would pass one in three.
    // Each end may offer several doorways: every pairing and every route
    // is scored by its length, by any other work a leg passes through, and
    // above all by a stairwell that would drop through another work.
    private void GmConnect(GmCtx g, IList<GmPort> aCands, IList<GmPort> bCands, string label, bool openEnd = false)
    {
        const int Lead = 9;
        int cx = g.Cx, cz = g.Cz;
        GmWork Owner(GmPort p) => g.Works.FirstOrDefault(w => w.Ports.ContainsValue(p));
        bool ShaftBlocked(int x, int z, int y0, int y1, GmWork own)
            => g.Works.Any(w => w != own && w.Overlaps(x - 7, z - 7, x + 7, z + 7, Math.Min(y0, y1) - 3, Math.Max(y0, y1) + 8, 0));

        GmPort A = null, B = null;
        List<(int X, int Z)> best = null;
        double bestScore = double.MaxValue, bestMinR = 0, bestLen = 0;
        int bestHa = 0, bestHb = 0;
        foreach (var a in aCands)
            foreach (var b in bCands)
            {
                var a1 = (X: a.X + a.Dx * Lead, Z: a.Z + a.Dz * Lead);
                var b1 = (X: b.X + b.Dx * Lead, Z: b.Z + b.Dz * Lead);
                GmWork ownA = Owner(a), ownB = Owner(b);
                var cands = new List<List<(int X, int Z)>>
                {
                    new() { a1, (a1.X, b1.Z), b1 },
                    new() { a1, (b1.X, a1.Z), b1 },
                };
                foreach (int push in new[] { 24, 48 })
                {
                    var a2 = (a1.X + a.Dx * push, a1.Z + a.Dz * push);
                    cands.Add(new() { a1, a2, (a2.Item1, b1.Z), b1 });
                    cands.Add(new() { a1, a2, (b1.X, a2.Item2), b1 });
                }
                foreach (var raw in cands)
                {
                    var path = raw.Where((p, i) => i == 0 || p != raw[i - 1]).ToList();
                    if (path.Count < 2) path.Add(path[0]);
                    double minR = double.MaxValue, len = 0;
                    int minSafe = int.MaxValue, crossings = 0;
                    for (int i = 0; i + 1 < path.Count; i++)
                    {
                        var p = path[i]; var q = path[i + 1];
                        double l = Math.Abs(q.X - p.X) + Math.Abs(q.Z - p.Z);
                        len += l;
                        int n = Math.Max(1, (int)l / 2);
                        for (int s = 0; s <= n; s++)
                        {
                            double t = s / (double)n;
                            int x = (int)Math.Round(p.X + (q.X - p.X) * t), z = (int)Math.Round(p.Z + (q.Z - p.Z) * t);
                            minR = Math.Min(minR, Math.Sqrt((x - cx) * (double)(x - cx) + (z - cz) * (double)(z - cz)));
                            for (int ox = -5; ox <= 5; ox += 5)
                                for (int oz = -5; oz <= 5; oz += 5) minSafe = Math.Min(minSafe, g.SafeTop(x + ox, z + oz));
                        }
                        // turning back on itself: a leg opposite a doorway's way out
                        if (i == 0 && (Math.Sign(q.X - p.X) == -a.Dx && a.Dx != 0 || Math.Sign(q.Z - p.Z) == -a.Dz && a.Dz != 0)) crossings += 3;
                        if (i == path.Count - 2 && (Math.Sign(p.X - q.X) == -b.Dx && b.Dx != 0 || Math.Sign(p.Z - q.Z) == -b.Dz && b.Dz != 0)) crossings += 3;
                        int lx0 = Math.Min(p.X, q.X) - 5, lx1 = Math.Max(p.X, q.X) + 5, lz0 = Math.Min(p.Z, q.Z) - 5, lz1 = Math.Max(p.Z, q.Z) + 5;
                        foreach (var w in g.Works)
                            if (w != ownA && w != ownB && w.Overlaps(lx0, lz0, lx1, lz1, Math.Min(a.Y, b.Y) - 2, Math.Max(a.Y, b.Y) + 8, 2)) crossings++;
                    }
                    int yMax = minSafe - 7;
                    if (minR < g.AbyssR * 1.42) yMax = Math.Min(yMax, 26);
                    int ha = Math.Min(a.Y, yMax), hb = Math.Min(b.Y, yMax);
                    int slack = (int)(len / 3);
                    if (ha > hb + slack) ha = hb + slack;
                    if (hb > ha + slack) hb = ha + slack;
                    double score = len + crossings * 400;
                    if (a.Y != ha && ShaftBlocked(a1.X, a1.Z, ha, a.Y, ownA)) score += 3000;
                    if (b.Y != hb && ShaftBlocked(b1.X, b1.Z, hb, b.Y, ownB)) score += 3000;
                    if (score < bestScore) { bestScore = score; best = path; bestMinR = minR; bestLen = len; A = a; B = b; bestHa = ha; bestHb = hb; }
                }
            }
        if (best == null) return;
        {
            var a = A; var b = B;
            var a1 = (X: a.X + a.Dx * Lead, Z: a.Z + a.Dz * Lead);
            var b1 = (X: b.X + b.Dx * Lead, Z: b.Z + b.Dz * Lead);
            int ha = bestHa, hb = bestHb;
            double midLen = bestLen;
            foreach (var w in g.Works) w.Ports.Remove(w.Ports.FirstOrDefault(kv => kv.Value == a).Key ?? "\0");
            foreach (var w in g.Works) w.Ports.Remove(w.Ports.FirstOrDefault(kv => kv.Value == b).Key ?? "\0");

            // The pipe runs a → (stairwell) → the turns → (stairwell) → b; a
            // stairwell's two pipes stop at its wall, 4 from its axis.
            var pts = new List<(double X, double Y, double Z)>();
            (int X, int Z) Dir((int X, int Z) p, (int X, int Z) q) => (Math.Sign(q.X - p.X), Math.Sign(q.Z - p.Z));
            var dFirst = Dir(best[0], best[1]);
            var dLast = Dir(best[best.Count - 2], best[best.Count - 1]);
            if (dFirst == (0, 0)) dFirst = (-b.Dx, -b.Dz);
            if (dLast == (0, 0)) dLast = (-b.Dx, -b.Dz);
            if (a.Y != ha)
            {
                GmPipe(g, new() { (a.X, a.Y, a.Z), (a1.X - a.Dx * 4.0, a.Y, a1.Z - a.Dz * 4.0) });
                GmShaft(g, a1.X, a1.Z, new() { (Math.Atan2(-a.Dz, -a.Dx), a.Y), (Math.Atan2(dFirst.Z, dFirst.X), ha) });
                pts.Add((a1.X + dFirst.X * 4.0, ha, a1.Z + dFirst.Z * 4.0));
            }
            else { pts.Add((a.X, a.Y, a.Z)); pts.Add((a1.X, ha, a1.Z)); }
            // the turns, each at the height the even slope has reached there
            double run = 0;
            for (int i = 1; i < best.Count; i++)
            {
                run += Math.Abs(best[i].X - best[i - 1].X) + Math.Abs(best[i].Z - best[i - 1].Z);
                double h = Math.Round((ha + (hb - ha) * (midLen < 1 ? 1 : run / midLen)) * 2) / 2;
                if (i == best.Count - 1)
                {
                    if (b.Y != hb) pts.Add((best[i].X - dLast.X * 4.0, hb, best[i].Z - dLast.Z * 4.0));
                    else { pts.Add((best[i].X, hb, best[i].Z)); pts.Add((b.X, b.Y, b.Z)); }
                }
                else pts.Add((best[i].X, h, best[i].Z));
            }
            GmPipe(g, pts, openEnd && b.Y == hb);
            if (b.Y != hb)
            {
                GmShaft(g, b1.X, b1.Z, new() { (Math.Atan2(-dLast.Z, -dLast.X), hb), (Math.Atan2(-b.Dz, -b.Dx), b.Y) });
                GmPipe(g, new() { (b1.X - b.Dx * 4.0, b.Y, b1.Z - b.Dz * 4.0), (b.X, b.Y, b.Z) }, openEnd);
            }
            sapi.Logger.Notification("[landmassgenerator] great machine pipe {0}: {1} long, floor {2} to {3}{4}{5}{6}", label, (int)midLen + 2 * Lead, a.Y, b.Y,
                a.Y != ha ? $", stairwell at its start ({a.Y} to {ha})" : "", b.Y != hb ? $", stairwell at its end ({hb} to {b.Y})" : "",
                bestScore >= 3000 ? " (a stairwell crosses another work)" : bestScore >= 400 ? " (crosses another work)" : "");
        }
    }

    // ── Rooms and their furniture ────────────────────────────────────────
    // Masonry shells: a polished floor, walls of the given brick with a
    // darker course every 4, a ceiling of the same brick.
    private static Func<int, int, int, int> GmMasonry(GmCtx g, int y0, int y1, int floor, int wallA, int wallB, int course, int ceil)
        => (x, y, z) => y < y0 ? floor : y > y1 ? ceil : (y - y0) % 4 == 3 ? course : g.K.Hash01(x * 7 + y, z * 13 - y) < 0.18 ? wallB : wallA;

    private static Func<int, int, int, int> GmIronShell(GmCtx g, int y0, int y1, int floor)
        => (x, y, z) => y < y0 ? floor : (y - y0) % 6 == 0 || (x + z) % 9 == 0 ? g.SteelR : g.Rust();

    private static readonly Dictionary<string, (string[] Wall, string[] Mid, double Fill)> GmKinds = new()
    {
        ["workshop"] = (new[] { "toolrack-ruined1", "toolrack-ruined2", "toolrack-empty", "shelf-tools", "shelf-woodworkingtools", "pile-tools1", "pile-tools2",
            "pile-tools3", "pile-tools4", "pile-woodworkingtools", "barrel-metal1", "barrel-metal3", "anvil-broken1", "anvil-broken2", "grinding-stone",
            "crate/crate-medium-junk", "mechanics-ruined2", "mechanics-ruined4", "wallhook-mechsection" },
            new[] { "table-long-with-accessories", "table-ruined2", "anvil-broken3", "swageblock1", "mech-part3", "machine2" }, 0.55),
        ["lab"] = (new[] { "shelf-flasks1", "shelf-flasks2", "shelf-lab-equipment", "tank-glass1", "tank-glass3", "tank-glass5", "tank-glass7", "tank-glass9",
            "pile-flasks1", "pile-flasks2", "pile-lab-equipment", "mortar-stone", "@bookshelf-alchemy01", "@bookshelf-reagents02", "@bookshelf-reagents05",
            "tank-metal1", "tank-metal2", "barrel-metal2" },
            new[] { "table-long-with-accessories", "table-long", "tank-glass2", "tank-glass4", "tank-glass6", "tank-glass8", "armillary2", "astrolabe" }, 0.6),
        ["optics"] = (new[] { "shelf-lab-equipment", "shelf-drafting-instrument", "@bookshelf-standard3", "pile-precisiontools", "globe2", "globe4", "alidade",
            "diptych-dial", "equi-dial", "tank-glass2" },
            new[] { "armillary1", "armillary3", "astrolabe", "table-long-with-accessories", "globe5" }, 0.5),
        ["drafting"] = (new[] { "shelf-drafting-instrument", "pile-drafting-instrument", "bookshelves/scrollrack-full1", "bookshelves/scrollrack-full3",
            "bookshelves/scrollrack-full4", "@bookshelf-ruined-full4", "@bookshelf-stuff03", "bookshelves/bookpile-aged2", "abacus2", "abacus4", "globe1" },
            new[] { "table-long-with-accessories", "table-long-with-cloth-blue", "bookshelves/lecturn-aged-book-opened", "bookshelves/bookstand-aged-book-opened-large" }, 0.55),
        ["store"] = (new[] { "crate/crate-large-junk", "crate/crate-large-ruined1", "crate/crate-large-ore2", "crate/crate-medium-empty", "crate/crate-medium-junk",
            "crate/crate-small-stacked", "crate/crate-small-ruined2", "barrel-metal1", "barrel-metal2", "barrel-metal4", "shelf-empty-straight", "shelf-empty",
            "pile-trash-scrap", "pile-trash-oldore" },
            new[] { "crate/crate-large-cobweb", "crate/crate-large-empty", "crate/crate-medium-pottery", "crate/crate-small-junk" }, 0.75),
        ["office"] = (new[] { "@bookshelf-ruined-full8", "@bookshelf-standard1", "@bookshelf-ruined-full12", "bookshelves/bookpile-aged4", "bookshelves/scrollrack-full2",
            "coatrack-ruined1", "globe3", "candlestubs-bunch2", "wallhook-single-coat", "abacus1" },
            new[] { "table-aged", "table-noble-single", "bookshelves/lecturn-aged-book-closed", "chair-aged" }, 0.45),
        ["dorm"] = (new[] { "bed/bed-metal-ruined1", "bed/bed-metal-ruined2", "bed/bed-metal-ruined3", "bed/bed-straw-ruined1", "bed-short-old",
            "bed-short-stitched-ruined", "coatrack-ruined2", "coatrack-full1", "wallhook-single-coat", "pile-cloth2", "pile-cloth4", "crate/crate-small-rot" },
            new[] { "table-ruined1", "chair-ruined1", "pile-cloth1" }, 0.6),
        ["mess"] = (new[] { "barrel-metal3", "pile-pottery2", "pile-pottery5", "crate/crate-medium-pottery-alt", "shelf-empty", "pottery/container2", "pottery/utensils3" },
            new[] { "table-long", "table-tavern-double", "chair-long", "table-long-with-cloth-red", "table-ruined5" }, 0.5),
        ["kitchen"] = (new[] { "stove-ruined", "barrel-metal1", "pottery/utensils1", "pottery/utensils4", "pottery/utensils6", "pottery/container4",
            "pottery/pot-food", "bucket2", "pile-pottery3", "pile-pottery7", "shelf-empty-straight" },
            new[] { "table-ruined3", "table-long", "pile-pottery1" }, 0.6),
        ["infirmary"] = (new[] { "bed/bed-metal", "bed/bed-metal-ruined2", "shelf-medical", "pile-medical", "skeleton-inbed", "tank-glass2", "shelf-flasks1", "bucket1" },
            new[] { "table-long", "pile-medical", "table-ruined4" }, 0.55),
        ["hazmat"] = (new[] { "hazmat-rack-ruined1", "hazmat-rack-ruined2", "hazmat-rack-ruined3", "hazmat-rack-ruined4", "hazmat-rack-ruined5", "hazmat-rack",
            "wallhook-single-mask", "wallhook-single-suit", "hazmat-ruined1", "hazmat-ruined2" },
            new[] { "hazmat-ruined3", "hazmat-ruined4", "table-ruined2" }, 0.7),
        ["valves"] = (new[] { "!valve1-aged", "!valve2-aged", "!valve3-aged", "!gauge1", "!gauge2", "!gauge3", "!gauge4", "!sightglass1-aged", "!sightglass3-aged",
            "!switch2-aged", "!switch4-aged", "!lever1-aged", "!lever3-aged", "!tank1-aged", "!vtankend" },
            new[] { "!tank2-aged", "!boilerbody", "!gearbox1", "!geartrain3", "!misc1-aged" }, 0.65),
        ["archive"] = (new[] { "@bookshelf-ruined-full1", "@bookshelf-ruined-full5", "@bookshelf-ruined-full9", "@bookshelf-ruined-full13", "@bookshelf-ruined-full16",
            "@bookshelf-ruined-empty3", "bookshelves/scrollrack-full5", "bookshelves/scrollrack-ruined2", "bookshelves/bookpile-aged5", "bookshelves/large-book-pile2" },
            new[] { "bookshelves/lecturn-aged-book-opened", "table-long-with-cloth-purple", "bookshelves/bookstand-verylarge-book-open", "bookshelves/large-book-pile3" }, 0.85),
        ["chapel"] = (new[] { "banner-aged-red-segment", "candlestubs-bunch1", "candlestubs-bunch3", "candlestubs-bunch4", "candlestub-single", "bookshelves/bookstand-aged-empty" },
            new[] { "chair-long", "chair-long", "bookshelves/lectern-large-book-open" }, 0.45),
        ["specimens"] = (new[] { "tank-glass1", "tank-glass3", "tank-glass5", "tank-glass7", "carcass-ribcage1", "carcass-ribcage2", "skeleton-long", "pile-bones1",
            "pile-bones2", "shelf-flasks2", "golem-head", "golem-hand2" },
            new[] { "tank-glass9", "golem-skull", "table-long", "carcass-large" }, 0.65),
        ["machine"] = (new[] { "!boilerend", "!tank1bracket-aged", "!column3-aged", "!column6-broke", "!pipelong3-aged", "!pipelong4bent-aged", "!junktanksmallbase",
            "!junktanksmall1", "!gear24", "!gear36", "!brokengear/big02", "machine1", "machine4", "machine6", "mech-part2", "mech-part5", "mechanics-ruined1" },
            new[] { "!gearbox1", "!geartrain3", "!tower2", "machine7", "machine8", "!boilerbody" }, 0.6),
    };

    // Furnish a room: interior u0..u1 x w0..w1 in a frame, floor at v0,
    // ceiling at v1. Wall things face in, the kind's centrepieces stand
    // down the middle, a gas lamp on two walls and a ceiling lamp. "door"
    // cells (local u, w) and their neighbours stay clear. A list entry
    // starting with ! is devastation clutter, with @ a cluttered bookshelf.
    private static void GmFurnish(GmCtx g, GmFrame f, int u0, int u1, int w0, int w1, int v0, int v1, string kind, List<(int U, int W)> doors)
    {
        if (!GmKinds.TryGetValue(kind, out var set)) return;
        g.Rooms++;
        bool NearDoor(int u, int w) => doors != null && doors.Any(d => Math.Abs(d.U - u) <= 1 && Math.Abs(d.W - w) <= 1);
        void Place(int u, int w, string t, double du, double dw)
        {
            int x = f.X(u, w), z = f.Z(u, w), y = f.Y(v0);
            double h = f.H(du, dw);
            if (t[0] == '!') g.Dev(x, y, z, t.Substring(1), h);
            else if (t[0] == '@') g.Shelf(x, y, z, t.Substring(1), g.Pick(new[] { "bookshelf-ruined-full2", "bookshelf-ruined-full6", "bookshelf-ruined-full10", "bookshelf-ruined-empty2" }), h);
            else g.Clut(x, y, z, t, h);
        }
        for (int u = u0; u <= u1; u++)
            for (int w = w0; w <= w1; w++)
            {
                bool edge = u == u0 || u == u1 || w == w0 || w == w1;
                if (!edge || NearDoor(u, w) || g.R() > set.Fill) continue;
                double du = u == u0 ? 1 : u == u1 ? -1 : 0, dw = w == w0 ? 1 : w == w1 ? -1 : 0;
                if (du != 0 && dw != 0) { if (g.R() < 0.5) du = 0; else dw = 0; }
                Place(u, w, g.Pick(set.Wall), du, dw);
            }
        int mu = (u0 + u1) / 2, mw = (w0 + w1) / 2;
        if (u1 - u0 >= 4 && w1 - w0 >= 4)
        {
            bool alongU = u1 - u0 >= w1 - w0;
            int n = alongU ? (u1 - u0 - 2) / 3 : (w1 - w0 - 2) / 3;
            for (int i = 0; i < Math.Max(1, n); i++)
            {
                int u = alongU ? u0 + 2 + i * 3 : mu, w = alongU ? mw : w0 + 2 + i * 3;
                if (u > u1 - 2 || w > w1 - 2 || NearDoor(u, w)) continue;
                Place(u, w, g.Pick(set.Mid), alongU ? 0 : 1, alongU ? 1 : 0);
                if (kind == "mess" || kind == "drafting" || kind == "office")
                {
                    if (alongU) { Place(u, w - 1, "chair-aged", 0, 1); Place(u, w + 1, "chair-ruined2", 0, -1); }
                    else { Place(u - 1, w, "chair-aged", 1, 0); Place(u + 1, w, "chair-ruined3", -1, 0); }
                }
            }
        }
        else if (g.R() < 0.4) Place(mu, mw, "rubble-cracked" + g.RI(1, 4), 0, 1);
        // lamps: two on the walls facing each other, one under the ceiling
        int lv = Math.Min(v0 + 2, v1);
        if ((u1 - u0) >= (w1 - w0))
        {
            g.Lamp(f.X(mu, w0), f.Y(lv), f.Z(mu, w0), "gas-wall1", f.H(0, 1));
            g.Lamp(f.X(mu + 1, w1), f.Y(lv), f.Z(mu + 1, w1), "gas-wall2", f.H(0, -1));
        }
        else
        {
            g.Lamp(f.X(u0, mw), f.Y(lv), f.Z(u0, mw), "gas-wall1", f.H(1, 0));
            g.Lamp(f.X(u1, mw + 1), f.Y(lv), f.Z(u1, mw + 1), "gas-wall2", f.H(-1, 0));
        }
        if (v1 - v0 >= 4) g.Lamp(f.X(mu, mw), f.Y(v1), f.Z(mu, mw), "gas-ceiling1", 0);
        // a little ruin: cobweb and rubble
        if (g.R() < 0.5) g.Solid(f.X(u0, w1), f.Y(v1), f.Z(u0, w1), g.Spider);
        if (g.R() < 0.6) Place(g.RI(u0 + 1, Math.Max(u0 + 1, u1 - 1)), g.RI(w0 + 1, Math.Max(w0 + 1, w1 - 1)), "rubble-cracked" + g.RI(1, 4), 0, 1);
    }

    // ── Wings: hallways with rooms either side ───────────────────────────
    // A hallway along u from u0 to u1 at w = wc (3 wide, 4 tall), rooms
    // either side, each a kind from the list, a doorway into each. The
    // hall is lit by gas lamps on alternate walls and a rusted pipe with
    // lumi lamps along its ceiling. Rooms 8 or more deep are 5 tall.
    private static void GmWing(GmCtx g, GmFrame f, int u0, int u1, int wc, int v0, int depthL, int depthR, string[] kinds, int wallA, int wallB, int floor)
    {
        if (u1 < u0) (u0, u1) = (u1, u0);
        var hallShell = GmMasonry(g, f.Y(v0), f.Y(v0 + 3), floor, wallA, wallB, g.BrickC, wallA);
        GmRoom(g, f, u0, v0, wc - 1, u1, v0 + 3, wc + 1, 1, hallShell);
        g.Details.Add(() =>
        {
            for (int u = u0; u <= u1; u++)
            {
                // a runner of darker stone down the middle of the floor
                g.Solid(f.X(u, wc), f.Y(v0 - 1), f.Z(u, wc), g.PolB);
                g.Clut(f.X(u, wc + 1), f.Y(v0 + 3), f.Z(u, wc + 1), (u - u0) % 7 == 3 ? "jonas-lamp|lightsource/on/lumi-pipe-h-lamp" : "pipe-veryrusted-h-EW", f.H(1, 0));
                if ((u - u0) % 8 == 4)
                {
                    int side = ((u - u0) / 8) % 2 == 0 ? -1 : 1;
                    g.Lamp(f.X(u, wc + side), f.Y(v0 + 2), f.Z(u, wc + side), "gas-wall1", f.H(0, -side));
                }
                if (g.R() < 0.04) g.Clut(f.X(u, wc - 1), f.Y(v0), f.Z(u, wc - 1), "rubble-cracked" + g.RI(1, 4), f.H(0, 1));
            }
        });
        foreach (int side in new[] { -1, 1 })
        {
            int depth = side < 0 ? depthL : depthR;
            if (depth < 4) continue;
            int u = u0;
            while (u + 5 <= u1)
            {
                int wdt = Math.Min(u1 - u, g.RI(6, 10));
                if (wdt < 5) break;
                int ra = u, rb = u + wdt;
                int wn = side < 0 ? wc - 3 : wc + 3, wf = side < 0 ? wc - 2 - depth : wc + 2 + depth;
                int wlo = Math.Min(wn, wf), whi = Math.Max(wn, wf);
                int h = depth >= 8 ? 5 : 4;
                string kind = g.Pick(kinds);
                var shell = GmMasonry(g, f.Y(v0), f.Y(v0 + h - 1), floor, wallA, wallB, g.BrickC, wallA);
                GmRoom(g, f, ra, v0, wlo, rb, v0 + h - 1, whi, 1, shell);
                int du = (ra + rb) / 2;
                GmRoom(g, f, du, v0, side < 0 ? wc - 2 : wc + 2, du + 1, v0 + 2, side < 0 ? wc - 2 : wc + 2, 1, shell);
                int ra2 = ra, rb2 = rb, wlo2 = wlo, whi2 = whi, h2 = h;
                var doors = new List<(int U, int W)> { (du, wn), (du + 1, wn) };
                g.Details.Add(() => GmFurnish(g, f, ra2, rb2, wlo2, whi2, v0, v0 + h2 - 1, kind, doors));
                u = rb + 2;
            }
        }
    }

    // ── Buried machinery ─────────────────────────────────────────────────
    // Solid machines packed into the rock between the works, from the
    // mantle up to the safe ceiling: conduits, gears, tanks, girders and
    // engine blocks, so a shaft dug anywhere in the walls' circle hits
    // metal. It goes in first; the works' shells and hollows replace it
    // where they reach.
    private static void GmBuriedMachinery(GmCtx g, int count)
    {
        int cx = g.Cx, cz = g.Cz, R = g.WallR - 10;
        for (int i = 0; i < count; i++)
        {
            double a = g.R() * Math.PI * 2, r = Math.Sqrt(g.R()) * R;
            int x = cx + (int)(Math.Cos(a) * r), z = cz + (int)(Math.Sin(a) * r);
            int top = g.SafeTop(x, z) + 2;
            if (top < 12) continue;
            int y = g.RI(7, top);
            int kind = i % 10;
            if (kind < 4)
            {
                // a conduit: an iron pipe with a copper core, long and straight
                char ax = g.R() < 0.4 ? 'y' : g.R() < 0.5 ? 'x' : 'z';
                int len = g.RI(24, 110), rr = g.RI(1, 3);
                int x1 = x + (ax == 'x' ? len : 0), y1 = Math.Min(y + (ax == 'y' ? len : 0), top), z1 = z + (ax == 'z' ? len : 0);
                double ccx = x, ccy = y, ccz = z, ex = x1, ey = y1, ez = z1;
                g.Fill.Add(() => GmSolidTube(g, ccx, ccy, ccz, ex, ey, ez, rr + 0.4, (px, py, pz, d) => d < rr * 0.5 ? g.Copper : ((int)(px + py + pz) % 6 == 0 ? g.SteelR : g.Rust())));
            }
            else if (kind < 6)
            {
                // a gear on its side or standing, half sheared away
                double gr = g.RI(6, 16);
                char ax = g.Pick(new[] { 'y', 'x', 'z' });
                int mat = g.R() < 0.5 ? g.Bronze : g.RustA;
                double ph = g.R();
                int gx = x, gy = Math.Min(y, top - (int)gr), gz = z;
                g.Fill.Add(() => GmGear(g, gx, gy, gz, ax, gr, 2, 6, ph, () => g.R() < 0.7 ? mat : g.RustB));
            }
            else if (kind == 6)
            {
                // a tank: riveted iron round a pocket of brass
                double tr = g.RI(3, 7);
                int ty = Math.Min(y, top - (int)tr);
                int tx2 = x, tz2 = z;
                g.Fill.Add(() =>
                {
                    int ri = (int)Math.Ceiling(tr);
                    for (int dx = -ri; dx <= ri; dx++)
                        for (int dy = -ri; dy <= ri; dy++)
                            for (int dz = -ri; dz <= ri; dz++)
                            {
                                double d = Math.Sqrt(dx * dx + dy * dy * 1.4 + dz * dz);
                                if (d > tr) continue;
                                g.Solid(tx2 + dx, ty + dy, tz2 + dz, d > tr - 1.2 ? (dy == 0 ? g.SteelR : g.RustA) : g.Brass);
                            }
                });
            }
            else if (kind == 7)
            {
                // a girder: an I-beam, any way up
                int len = g.RI(20, 64);
                bool alongX = g.R() < 0.5;
                int yy = y, xx = x, zz = z;
                g.Fill.Add(() =>
                {
                    for (int s = 0; s < len; s++)
                        for (int a2 = -2; a2 <= 2; a2++)
                            for (int b2 = -3; b2 <= 3; b2++)
                            {
                                bool web = a2 == 0, flange = Math.Abs(b2) == 3;
                                if (!web && !flange) continue;
                                int px = alongX ? xx + s : xx + a2, pz = alongX ? zz + a2 : zz + s;
                                g.Solid(px, yy + b2, pz, flange ? g.RustB : g.RustA);
                            }
                });
            }
            else
            {
                // an engine block: a box of plate, ribbed, a drum on top
                int w = g.RI(5, 11), d2 = g.RI(5, 11), h = g.RI(4, 9);
                int yy = Math.Min(y, top - h - 3), xx = x, zz = z;
                g.Fill.Add(() =>
                {
                    for (int px = xx; px <= xx + w; px++)
                        for (int pz = zz; pz <= zz + d2; pz++)
                            for (int py = yy; py <= yy + h; py++)
                                g.Solid(px, py, pz, (px - xx) % 3 == 0 ? g.SteelR : py == yy + h ? g.IronR : g.Rust());
                    GmSolidTube(g, xx + w / 2.0, yy + h + 2, zz, xx + w / 2.0, yy + h + 2, zz + d2, 2.2, (px, py, pz, dd) => dd < 1 ? g.Copper : g.SteelR);
                });
            }
        }
    }

    // A gear in the plane across `axis` ('y' lies flat), the same wheel as
    // MegaKit.Gear (rim, teeth, spokes, a hub with an axle hole in big
    // ones) written through the works' own safe setter.
    private static void GmGear(GmCtx g, double cx, double cy, double cz, char axis, double R, int thick, int spokes, double phase, Func<int> id, bool teeth = true)
    {
        int nTeeth = Math.Max(8, (int)Math.Round(R * 1.4));
        double step = R > 20 ? 0.5 : 0.45;
        var done = new HashSet<long>();
        double hub = Math.Max(1.6, R * 0.14), rim = Math.Max(1.2, R * 0.09), tooth = Math.Max(1.2, R * 0.07), sw = Math.Max(0.7, R * 0.05);
        for (double a = -R - 1.6; a <= R + 1.6; a += step)
            for (double b = -R - 1.6; b <= R + 1.6; b += step)
            {
                double r = Math.Sqrt(a * a + b * b), th = Math.Atan2(b, a) + phase;
                bool solid;
                if (r <= hub) solid = r > hub * 0.45 || R < 12;
                else if (r >= R - rim && r <= R) solid = true;
                else if (r > R && r <= R + tooth) solid = teeth && Math.Cos(nTeeth * th) > 0.2;
                else if (r < R - rim)
                {
                    solid = false;
                    for (int sp = 0; sp < spokes && !solid; sp++)
                    {
                        double sa = phase + sp * Math.PI * 2 / spokes;
                        solid = Math.Cos(sa) * a + Math.Sin(sa) * b > 0 && Math.Abs(-Math.Sin(sa) * a + Math.Cos(sa) * b) < sw;
                    }
                }
                else solid = false;
                if (!solid) continue;
                for (int t = 0; t < thick; t++)
                {
                    int x, y, z;
                    if (axis == 'y') { x = (int)Math.Round(cx + a); y = (int)Math.Round(cy) + t; z = (int)Math.Round(cz + b); }
                    else if (axis == 'x') { x = (int)Math.Round(cx) + t; y = (int)Math.Round(cy + b); z = (int)Math.Round(cz + a); }
                    else { x = (int)Math.Round(cx + a); y = (int)Math.Round(cy + b); z = (int)Math.Round(cz) + t; }
                    long key = ((long)(x & 0xFFFFF) << 40) | ((long)(y & 0xFFFFF) << 20) | (long)(z & 0xFFFFF);
                    if (done.Add(key)) g.Solid(x, y, z, id());
                }
            }
    }

    // A solid tube (no bore): mat(x, y, z, distance from the axis).
    private static void GmSolidTube(GmCtx g, double ax, double ay, double az, double bx, double by, double bz, double r, Func<int, int, int, double, int> mat)
    {
        double dx = bx - ax, dy = by - ay, dz = bz - az, len2 = dx * dx + dy * dy + dz * dz;
        if (len2 < 0.25) return;
        int x0 = (int)Math.Floor(Math.Min(ax, bx) - r - 1), x1 = (int)Math.Ceiling(Math.Max(ax, bx) + r + 1);
        int y0 = (int)Math.Floor(Math.Min(ay, by) - r - 1), y1 = (int)Math.Ceiling(Math.Max(ay, by) + r + 1);
        int z0 = (int)Math.Floor(Math.Min(az, bz) - r - 1), z1 = (int)Math.Ceiling(Math.Max(az, bz) + r + 1);
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                {
                    double px = x - ax, py = y - ay, pz = z - az;
                    double t = (px * dx + py * dy + pz * dz) / len2;
                    if (t < -0.5 / Math.Sqrt(len2) || t > 1 + 0.5 / Math.Sqrt(len2)) continue;   // flat ends
                    double qx = px - dx * t, qy = py - dy * t, qz = pz - dz * t, d = Math.Sqrt(qx * qx + qy * qy + qz * qz);
                    if (d <= r) g.Solid(x, y, z, mat(x, y, z, d));
                }
    }

    // ── The whole of it ──────────────────────────────────────────────────
    private void GmDepths(GmCtx g)
    {
        var k = g.K;
        int cx = g.Cx, cz = g.Cz;

        // the safe ceiling: the lowest ground within 4 columns, less 6
        int half = g.WallR + 60;
        g.SX0 = cx - half; g.SZ0 = cz - half; g.SW = 2 * half + 1;
        int W = g.SW;
        var f0 = new short[W * W];
        for (int j = 0; j < W; j++)
            for (int i = 0; i < W; i++) f0[j * W + i] = (short)g.Floor(g.SX0 + i, g.SZ0 + j);
        var f1 = new short[W * W];
        for (int j = 0; j < W; j++)
            for (int i = 0; i < W; i++)
            {
                short m = short.MaxValue;
                for (int o = Math.Max(0, i - 4); o <= Math.Min(W - 1, i + 4); o++) m = Math.Min(m, f0[j * W + o]);
                f1[j * W + i] = m;
            }
        g.Safe = new short[W * W];
        for (int j = 0; j < W; j++)
            for (int i = 0; i < W; i++)
            {
                short m = short.MaxValue;
                for (int o = Math.Max(0, j - 4); o <= Math.Min(W - 1, j + 4); o++) m = Math.Min(m, f1[o * W + i]);
                g.Safe[j * W + i] = (short)(m - 6);
            }

        GmBuriedMachinery(g, k.OptI("buried", 900));
        GmPlaceWorks(g);
        GmNetwork(g);

        int nFill = g.Fill.Count, nShell = g.Shells.Count, nCarve = g.Carves.Count, nDet = g.Details.Count;
        foreach (var a in g.Fill) a();
        long afterFill = g.Placed;
        foreach (var a in g.Shells) a();
        long afterShell = g.Placed;
        foreach (var a in g.Carves) a();
        long afterCarve = g.Placed;
        foreach (var a in g.Details) a();
        sapi.Logger.Notification("[landmassgenerator] great machine works: {0} works ({1}), {2} pipes {3} long, {4} stairwells, {5} furnished rooms, {6} lamps; " +
            "blocks: {7} buried machinery, {8} shells, {9} emptied, {10} fittings; {11} cell(s) kept solid under the safe ceiling; passes {12}/{13}/{14}/{15}",
            g.Works.Count, string.Join(", ", g.Works.Select(w => w.Name)), g.Pipes, g.PipeLen, g.Shafts, g.Rooms, g.Lamps,
            afterFill, afterShell - afterFill, afterCarve - afterShell, g.Placed - afterCarve, g.Clipped, nFill, nShell, nCarve, nDet);
    }
}
