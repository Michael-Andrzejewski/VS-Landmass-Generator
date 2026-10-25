using System;
using System.Collections.Generic;
using System.Linq;

namespace LandmassGenerator;

// ── THE GREAT MACHINE: THE SURFACE (0.65.0) ─────────────────────────────
// What stands inside the broken walls, built straight into the world (no
// passes: all of it is above the ground):
//
// - the collar: a riveted iron band 3 thick round the crater's lip, its
//   top folded inward, buttressed outside, torn away in places and leaning
//   into the crater in others;
// - the throat ring round the abyss mouth, with broken girders hanging
//   from it into the dark;
// - huge fallen metal in the crater: collar sections, girders, gears,
//   plates and pipe lengths, lying where they fell, half buried;
// - the crater filled with fried buildings: burnt brick and cracked stone
//   shells of one to three storeys on terraces cut into the bowl, charred
//   floors, fallen roofs, rubble and ash inside;
// - between the crater and the walls, machinery and wrecked halls:
//   smokestacks, gear towers, beam engines, tank farms, pipe racks, and
//   three halls whose stairwells go down to the works below.
//
// Nothing goes where the walkway and its stair run, on a chasm, round a
// chain bollard or under a chain.
public partial class LandmassGeneratorModSystem
{
    private void GmSurface(GmCtx g)
    {
        var k = g.K;
        int cx = g.Cx, cz = g.Cz;
        double Hyp(double a, double b) => Math.Sqrt(a * a + b * b);
        bool Cut(int x, int z) => g.Floor(x, z) < k.Ground(x, z) - 1;
        bool OnWalk(int x, int z, int pad) => x >= cx - 9 - pad && x <= cx + 8 + pad && z >= cz - pad && z <= cz + g.WallR + pad;
        bool Kept(double x, double z, double pad) => g.Keep.Any(c => Hyp(x - c.X, z - c.Z) < c.R + pad);
        // how high a thing may reach at (x, z): under every chain by 4
        int Ceiling(double x, double z)
        {
            int best = g.MapY;
            foreach (var c in g.Chains)
            {
                double dx = c.X1 - c.X0, dz = c.Z1 - c.Z0, l2 = dx * dx + dz * dz;
                if (l2 < 1) continue;
                double t = Math.Clamp(((x - c.X0) * dx + (z - c.Z0) * dz) / l2, 0, 1);
                if (Hyp(c.X0 + dx * t - x, c.Z0 + dz * t - z) > 12) continue;
                double cy = c.Y0 + (c.Y1 - c.Y0) * t - Math.Sin(t * Math.PI) * 8;
                best = Math.Min(best, (int)cy - 4);
            }
            return best;
        }
        bool FootprintOk(int x0, int z0, int x1, int z1, int pad)
        {
            for (int x = x0 - pad; x <= x1 + pad; x += 2)
                for (int z = z0 - pad; z <= z1 + pad; z += 2)
                    if (Cut(x, z) || OnWalk(x, z, 0)) return false;
            return !Kept((x0 + x1) / 2.0, (z0 + z1) / 2.0, Math.Max(x1 - x0, z1 - z0) / 2.0 + pad);
        }
        int Rust() => g.Rust();
        int nCollar = 0, nThroat = 0, nFallen = 0, nFried = 0, nMachines = 0;

        // ── the collar round the crater's lip ─────────────────────────────
        {
            double rIn = g.RimR - 1;
            int sections = 64;
            var state = new int[sections];         // 0 standing, 1 gone, 2 broken down, 3 leaning in
            for (int i = 0; i < sections; i++) { double r0 = g.R(); state[i] = r0 < 0.14 ? 1 : r0 < 0.32 ? 2 : r0 < 0.42 ? 3 : 0; }
            int steps = (int)(Math.PI * 2 * (rIn + 6) * 2.2);
            for (int si = 0; si < steps; si++)
            {
                double th = si * Math.PI * 2 / steps;
                int sec = (int)(th / (Math.PI * 2) * sections) % sections;
                if (state[sec] == 1) continue;
                double ct = Math.Cos(th), st = Math.Sin(th);
                int bx = cx + (int)Math.Round(ct * (rIn + 1.5)), bz = cz + (int)Math.Round(st * (rIn + 1.5));
                if (Cut(bx, bz) || OnWalk(bx, bz, 3)) continue;
                int g0 = g.Floor(bx, bz);
                double secPos = (th / (Math.PI * 2) * sections) % 1.0;
                int H = Math.Min(10 - (state[sec] == 2 ? (int)(7 * GmNoise2(th * 40, 3, g.Seed + 901)) : 0), Ceiling(bx, bz) - g0);
                double lean = state[sec] == 3 ? 0.35 * Math.Sin(secPos * Math.PI) : 0;
                bool rib = ((int)Math.Floor(th * (rIn + 3)) % 7 + 7) % 7 == 0;
                for (int ly = -4; ly <= H; ly++)
                {
                    double ri = rIn - Math.Max(0, ly) * lean;
                    double ro = ri + 3 + (rib ? Math.Max(0, 3 * (1 - (ly + 4) / (double)(H + 4))) : 0);
                    if (ly >= H - 1) ri -= 3;                                   // the lip folds over the crater
                    for (double r = ri; r <= ro; r += 0.5)
                    {
                        int x = cx + (int)Math.Round(ct * r), z = cz + (int)Math.Round(st * r);
                        int m = ly >= H - 1 || (ly + 4) % 3 == 0 ? g.SteelR : r > ri + 3 ? g.SteelR : Rust();
                        k.Set(x, g0 + ly, z, m);
                        nCollar++;
                    }
                }
            }
        }

        // ── the throat ring round the abyss mouth ─────────────────────────
        {
            int steps = (int)(Math.PI * 2 * g.AbyssR * 1.3 * 2.2);
            for (int si = 0; si < steps; si++)
            {
                double th = si * Math.PI * 2 / steps, ct = Math.Cos(th), st = Math.Sin(th);
                if (GmNoise2(th * 9, 1, g.Seed + 903) < 0.22) continue;           // torn away
                double re = 0;
                for (double r = g.AbyssR * 0.7; r < g.AbyssR * 1.6; r += 1)
                {
                    int x = cx + (int)Math.Round(ct * r), z = cz + (int)Math.Round(st * r);
                    if (!Cut(x, z)) { re = r; break; }
                }
                if (re == 0) continue;
                int ex = cx + (int)Math.Round(ct * (re + 2)), ez = cz + (int)Math.Round(st * (re + 2));
                if (Cut(ex, ez) || OnWalk(ex, ez, 2)) continue;
                int g0 = g.Floor(ex, ez);
                for (int ly = -3; ly <= 4; ly++)
                {
                    double r0 = ly >= 3 ? re - 2.5 : re + 0.5, r1 = re + 3.5;
                    for (double r = r0; r <= r1; r += 0.5)
                    {
                        int x = cx + (int)Math.Round(ct * r), z = cz + (int)Math.Round(st * r);
                        k.Set(x, g0 + ly, z, ly >= 3 || ly % 3 == 0 ? g.SteelR : Rust());
                        nThroat++;
                    }
                }
                // a girder hanging off the lip into the abyss, now and then
                if (si % 37 == 0)
                {
                    double len = 10 + g.R() * 18, sway = (g.R() - 0.5) * 6;
                    double hx = cx + ct * (re - 2), hz = cz + st * (re - 2);
                    k.Line(hx, g0 + 4, hz, hx - ct * 3 + sway * -st, g0 + 4 - len, hz - st * 3 + sway * ct, () => g.R() < 0.6 ? g.RustA : g.SteelR, 0.8);
                }
            }
        }

        // ── fallen metal in the crater ────────────────────────────────────
        // Each piece is a shape in its own frame, turned (yaw, pitch, roll)
        // and set into the ground; every cell of its box is tested.
        void Piece(double px, double pz, double ext, double yaw, double pitch, double roll, double sink, Func<double, double, double, int> shape)
        {
            int g0 = k.Ground((int)px, (int)pz);
            double py = g0 - sink;
            double cy = Math.Cos(yaw), sy = Math.Sin(yaw), cp = Math.Cos(pitch), sp = Math.Sin(pitch), cr = Math.Cos(roll), sr = Math.Sin(roll);
            int e = (int)Math.Ceiling(ext);
            for (int x = (int)px - e; x <= (int)px + e; x++)
                for (int z = (int)pz - e; z <= (int)pz + e; z++)
                {
                    if (Cut(x, z) || OnWalk(x, z, 1)) continue;
                    int gl = k.Ground(x, z);
                    for (int y = (int)py - e; y <= (int)py + e; y++)
                    {
                        if (y < gl - 6) continue;
                        double dx = x - px, dy = y - py, dz = z - pz;
                        // undo the yaw (about y), the pitch (about x), the roll (about z)
                        double ax = cy * dx + sy * dz, az = -sy * dx + cy * dz, ay = dy;
                        double bY = cp * ay - sp * az, bZ = sp * ay + cp * az;
                        double lx = cr * ax + sr * bY, ly = -sr * ax + cr * bY, lz = bZ;
                        int m = shape(lx, ly, lz);
                        if (m != 0) { k.Set(x, y, z, m); nFallen++; }
                    }
                }
            g.Keep.Add((px, pz, ext * 0.45));
        }
        int GearCell(double r, double th, double R, int spokes)
        {
            double hub = Math.Max(1.6, R * 0.14), rim = Math.Max(1.2, R * 0.09), tooth = Math.Max(1.2, R * 0.07);
            if (r <= hub) return 1;
            if (r >= R - rim && r <= R) return 1;
            if (r > R && r <= R + tooth) return Math.Cos(Math.Round(R * 1.4) * th) > 0.2 ? 1 : 0;
            if (r < R - rim)
                for (int s = 0; s < spokes; s++)
                {
                    double sa = s * Math.PI * 2 / spokes;
                    if (Math.Cos(sa) * Math.Cos(th) + Math.Sin(sa) * Math.Sin(th) > 0 && Math.Abs(Math.Sin(th - sa)) * r < Math.Max(0.8, R * 0.05)) return 1;
                }
            return 0;
        }
        {
            int placed = 0, tries = 0;
            while (placed < 36 && tries++ < 1200)
            {
                double a = g.R() * Math.PI * 2, r = g.AbyssR * 1.35 + g.R() * (g.RimR - 12 - g.AbyssR * 1.35);
                double px = cx + Math.Cos(a) * r, pz = cz + Math.Sin(a) * r;
                bool huge = placed < 10;
                int kind = placed % 5;
                double ext = kind switch { 0 => huge ? 36 : 22, 1 => huge ? 42 : 30, 2 => huge ? 27 : 18, 3 => huge ? 22 : 16, _ => huge ? 30 : 20 };
                if (Kept(px, pz, ext * 0.5) || OnWalk((int)px, (int)pz, (int)ext) || Cut((int)px, (int)pz)) continue;
                double yaw = g.R() * Math.PI * 2;
                switch (kind)
                {
                    case 0:
                    {
                        // a section of the collar: a curved band on its side
                        double Rc = 34 + g.R() * 14, A = (huge ? 64 : 34) / Rc, Hh = huge ? 12 : 9, T = 2.6;
                        Piece(px, pz, ext, yaw, 1.1 + g.R() * 0.5, (g.R() - 0.5) * 0.4, 3, (lx, ly, lz) =>
                        {
                            double rho = Math.Sqrt(lx * lx + (lz + Rc) * (lz + Rc)), phi = Math.Atan2(lx, lz + Rc);
                            if (rho > Rc || rho < Rc - T || Math.Abs(phi) > A / 2 || Math.Abs(ly) > Hh / 2) return 0;
                            return ((int)Math.Round(ly + 10) % 3 == 0) ? g.SteelR : Rust();
                        });
                        break;
                    }
                    case 1:
                    {
                        // a girder, one end on the ground and one raised
                        double L = huge ? 80 : 40 + g.R() * 16, Hg = huge ? 11 : 7, Wg = huge ? 9 : 5;
                        Piece(px, pz, ext, yaw, 0, 0.12 + g.R() * 0.25, 2, (lx, ly, lz) =>
                        {
                            if (Math.Abs(lx) > L / 2) return 0;
                            bool web = Math.Abs(lz) <= 0.7 && Math.Abs(ly) <= Hg / 2;
                            bool flange = Math.Abs(ly) >= Hg / 2 - 1.1 && Math.Abs(ly) <= Hg / 2 && Math.Abs(lz) <= Wg / 2;
                            if (!web && !flange) return 0;
                            return flange ? g.SteelR : ((int)Math.Round(lx) % 6 == 0 ? g.SteelR : Rust());
                        });
                        break;
                    }
                    case 2:
                    {
                        // a gear standing tilted, half sunk
                        double R = huge ? 23 : 11 + g.R() * 6;
                        int sp = huge ? 8 : 6, mat = g.R() < 0.5 ? g.Bronze : g.RustA;
                        Piece(px, pz, R + 3, yaw, 0.15 + g.R() * 0.35, 0, R * 0.35, (lx, ly, lz) =>
                        {
                            if (Math.Abs(lz) > (huge ? 2.2 : 1.2)) return 0;
                            return GearCell(Math.Sqrt(lx * lx + ly * ly), Math.Atan2(ly, lx), R, sp) == 1 ? (g.R() < 0.75 ? mat : g.RustB) : 0;
                        });
                        break;
                    }
                    case 3:
                    {
                        // a plate shard stuck edge-first in the ground
                        var poly = new List<(double X, double Z)>();
                        for (int i = 0; i < 6; i++) { double pa = i * Math.PI / 3 + g.R() * 0.5; double pr = (huge ? 12 : 7) + g.R() * 8; poly.Add((Math.Cos(pa) * pr, Math.Sin(pa) * pr)); }
                        Piece(px, pz, ext, yaw, 0.5 + g.R() * 0.6, (g.R() - 0.5) * 0.6, 4, (lx, ly, lz) =>
                        {
                            if (Math.Abs(ly) > 1.0) return 0;
                            bool inside = false;
                            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                                if ((poly[i].Z > lz) != (poly[j].Z > lz) && lx < (poly[j].X - poly[i].X) * (lz - poly[i].Z) / (poly[j].Z - poly[i].Z) + poly[i].X) inside = !inside;
                            if (!inside) return 0;
                            return ((int)Math.Round(lx) % 5 == 0 || (int)Math.Round(lz) % 5 == 0) ? g.SteelR : Rust();
                        });
                        break;
                    }
                    default:
                    {
                        // a length of pipe, its far end torn
                        double R = huge ? 8 : 4 + g.R() * 2.5, L = huge ? 56 : 26 + g.R() * 12;
                        Piece(px, pz, ext, yaw, 0, 0.05 + g.R() * 0.15, R * 0.55, (lx, ly, lz) =>
                        {
                            double rho = Math.Sqrt(ly * ly + lz * lz);
                            if (rho > R || rho < R - 1.6) return 0;
                            double tear = L / 2 - 4 * GmNoise2(Math.Atan2(lz, ly) * 2, 5, g.Seed + 907);
                            if (lx < -L / 2 || lx > tear) return 0;
                            return ((int)Math.Round(lx + 100) % 7 == 0) ? g.SteelR : Rust();
                        });
                        break;
                    }
                }
                placed++;
            }
        }

        // ── fried buildings ───────────────────────────────────────────────
        // One building: walls of burnt brick or cracked stone round a box on a
        // terrace (filled under its downhill side, cut into the uphill),
        // one to three storeys of 5 with window holes, a doorway, charred
        // floors with holes between storeys, most of the roof fallen in, the
        // wall tops crumbled, scorch and melt in patches, rubble inside.
        int Building(int x0, int z0, int x1, int z1, int storeys, int style, bool hall = false, bool keepOut = true)
        {
            int F = 0;
            var gs = new List<int>();
            for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++) gs.Add(k.Ground(x, z));
            gs.Sort();
            F = gs[gs.Count / 2] + 1;
            int wallA = style switch { 0 => g.BurntBrick, 1 => g.BrickC, 2 => g.BurntBrickD, _ => g.RustA };
            int wallB = style switch { 0 => g.BurntBrickD, 1 => g.Drock, 2 => g.BurntBrick, _ => g.RustB };
            int floorM = style == 3 ? g.IronR : g.Charred;
            int doorSide = g.RI(0, 3);
            int H = storeys * 5 + (hall ? 4 : 0);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    int gl = k.Ground(x, z);
                    bool edge = x == x0 || x == x1 || z == z0 || z == z1;
                    // the terrace
                    for (int y = Math.Min(gl, F - 1); y <= F - 1; y++) k.Set(x, y, z, y == F - 1 ? (g.K.Hash01(x * 3, z * 7) < 0.15 ? 0 : floorM) : g.BrickC);
                    if (gl >= F) { for (int y = F; y <= gl; y++) k.Set(x, y, z, 0); g.Carved(x, z, F - 1); }
                    if (!edge)
                    {
                        // upper floors, holed; the roof, mostly gone
                        for (int s = 1; s <= storeys; s++)
                        {
                            int fy = F + s * 5 - 1;
                            bool roof = s == storeys;
                            double keep = roof ? 0.3 : 0.6;
                            if (GmNoise2(x * 0.3, z * 0.3 + s * 7, g.Seed + 911 + style) < keep && !hall) k.Set(x, fy, z, (x + z) % 4 == 0 ? g.CharredW : floorM);
                        }
                        continue;
                    }
                    // the walls: crumbled top, windows, a doorway
                    double n = GmNoise2(x * 0.18, z * 0.18, g.Seed + 913);
                    int top = F + H - (int)(Math.Pow(n, 2.2) * (storeys * 4 + 2)) - (g.K.Hash01(x * 5, z * 3) < 0.08 ? 2 : 0);
                    bool gone = GmNoise2(x * 0.09 + 3, z * 0.09, g.Seed + 915) > 0.78;
                    if (gone) top = F + g.RI(0, 2);
                    int along = (z == z0 || z == z1) ? x - x0 : z - z0;
                    int side = z == z0 ? 0 : x == x1 ? 1 : z == z1 ? 2 : 3;
                    int len = side % 2 == 0 ? x1 - x0 : z1 - z0;
                    for (int y = F; y <= top; y++)
                    {
                        int ly = y - F, sly = ly % 5;
                        bool window = !hall && ly < storeys * 5 && (sly == 2 || sly == 3) && along % 3 == 1 && along > 0 && along < len;
                        bool doorway = side == doorSide && ly < (hall ? 8 : 3) && Math.Abs(along - len / 2) <= (hall ? 3 : 0);
                        if (window || doorway) { k.Set(x, y, z, 0); continue; }
                        bool scorch = GmNoise3(x * 0.2, y * 0.2, z * 0.2, g.Seed + 917) > 0.68;
                        int m = scorch ? (g.K.Hash01(x + y, z) < 0.3 ? g.Obsid : g.Drock) : sly == 4 ? wallB : g.K.Hash01(x * 7 + y, z * 3) < 0.25 ? wallB : wallA;
                        if (style == 3 && along % 4 == 0) m = g.SteelR;         // a shed's iron frame
                        k.Set(x, y, z, m);
                    }
                    for (int y = top + 1; y <= F + H + 1; y++) k.Set(x, y, z, 0);
                }
            // the cell a way down starts from (GmWarrens): the middle of a big
            // building, for a spiral stair, else a corner, for a ladder
            bool spiral = !hall && x1 - x0 - 1 >= 9 && z1 - z0 - 1 >= 9;
            int sx = spiral ? (x0 + x1) / 2 : x0 + 1, sz = spiral ? (z0 + z1) / 2 : z0 + 1, sr = spiral ? 5 : 1;
            // inside: rubble, charred furniture, ash, each on a sound floor
            int nIn = (x1 - x0) * (z1 - z0) / 8;
            for (int i = 0; i < nIn; i++)
            {
                int x = g.RI(x0 + 1, x1 - 1), z = g.RI(z0 + 1, z1 - 1);
                if (Math.Abs(x - sx) <= sr && Math.Abs(z - sz) <= sr) continue;
                k.Set(x, F - 1, z, floorM);
                double r0 = g.R();
                if (r0 < 0.25) { int h = g.RI(1, 2); for (int y = F; y < F + h; y++) k.Set(x, y, z, g.R() < 0.5 ? g.BrickC : g.Drock); }
                else if (r0 < 0.45) k.Set(x, F, z, g.Pick(new[] { g.PartsS, g.PartsM, g.Stones, g.Gears1 }));
                else k.Clutter(x, F, z, "~clutter|" + g.Pick(new[] { "rubble-cracked1", "rubble-cracked2", "rubble-cracked3", "rubble-wood1", "rubble-wood3",
                    "table-ruined1", "table-ruined4", "chair-ruined2", "bed/bed-ruined3", "barrel-metal2", "pile-trash-scrap", "crate/crate-small-ruined3" }), g.R() * Math.PI * 2);
            }
            nFried++;
            if (keepOut) g.Keep.Add(((x0 + x1) / 2.0, (z0 + z1) / 2.0, Math.Max(x1 - x0, z1 - z0) / 2.0 + 1));
            g.Buildings.Add((x0, z0, x1, z1, F, hall ? 1 : 0, sx, sz));
            return F;
        }

        // ── machinery and halls between the crater and the walls ──────────
        int ringR = (g.RimR + 14 + (int)(g.WallR * Math.Cos(Math.PI / 18)) - 6) / 2;   // the middle of the plateau ring
        int ringHalf = Math.Max(6, ((int)(g.WallR * Math.Cos(Math.PI / 18)) - 6 - (g.RimR + 14)) / 2);
        void Smokestack(int x, int z, int Hs, double r)
        {
            int g0 = k.Ground(x, z);
            int top = Math.Min(Hs, Ceiling(x, z) - g0);
            bool broken = g.R() < 0.4;
            if (broken) top = (int)(top * (0.4 + g.R() * 0.4));
            for (int dx = -(int)r - 2; dx <= r + 2; dx++)
                for (int dz = -(int)r - 2; dz <= r + 2; dz++)
                {
                    double q = Hyp(dx, dz);
                    for (int ly = -2; ly <= top; ly++)
                    {
                        double rr = r * (1 - 0.25 * ly / (double)Hs) + (ly < 3 ? 1.2 : 0);
                        if (q > rr) continue;
                        if (broken && ly > top - 4 && GmNoise3(dx * 0.6, ly * 0.4, dz * 0.6, g.Seed + 921) > 0.55) continue;
                        bool hollow = q < rr - 1.3 && ly > 0;
                        k.Set(x + dx, g0 + ly, z + dz, hollow ? 0 : ly % 8 == 7 ? g.SteelR : ly > top - 3 ? g.Drock : g.BurntBrick);
                    }
                }
        }
        void Tank(int x, int z, double r, int Ht)
        {
            int g0 = k.Ground(x, z);
            bool burst = g.R() < 0.4;
            double ba = g.R() * Math.PI * 2;
            for (int dx = -(int)r - 1; dx <= r + 1; dx++)
                for (int dz = -(int)r - 1; dz <= r + 1; dz++)
                {
                    double q = Hyp(dx, dz);
                    if (q > r) continue;
                    for (int ly = -1; ly <= Ht + (int)(r * 0.5); ly++)
                    {
                        double dome = ly > Ht ? Math.Sqrt(Math.Max(0, r * r - Math.Pow((ly - Ht) * 2, 2))) : r;
                        if (q > dome) continue;
                        bool shell = q > dome - 1.2 || ly <= 0;
                        if (!shell) { k.Set(x + dx, g0 + ly, z + dz, 0); continue; }
                        if (burst && Math.Abs(Math.IEEERemainder(Math.Atan2(dz, dx) - ba, Math.PI * 2)) < 0.6 && ly > 2 && ly < Ht - 1) continue;
                        k.Set(x + dx, g0 + ly, z + dz, ly % 4 == 0 ? g.SteelR : Rust());
                    }
                }
        }
        void PipeRun(double x0, double z0, double x1, double z1, double y, double r)
        {
            k.Tube(x0, y, z0, x1, y, z1, r, 1, () => g.R() < 0.7 ? g.RustA : g.CopperC, false, 6);
            double len = Hyp(x1 - x0, z1 - z0);
            for (double s = 0; s <= len; s += 8)
            {
                double t = s / len, px = x0 + (x1 - x0) * t, pz = z0 + (z1 - z0) * t;
                int g0 = k.Ground((int)px, (int)pz);
                k.Line(px, g0 - 1, pz, px, y - r, pz, () => g.SteelR, 0.6);
            }
        }
        void GearTower(int x, int z, double yaw)
        {
            int g0 = k.Ground(x, z), Ht = Math.Min(16 + g.RI(0, 10), Ceiling(x, z) - g0 - 14);
            if (Ht < 10) return;
            double ux = Math.Cos(yaw), uz = Math.Sin(yaw);
            foreach (var (a, b) in new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) })
                k.Line(x + a * 4 * ux - b * 3 * uz, g0 - 1, z + a * 4 * uz + b * 3 * ux, x + a * 1.5 * ux - b * 1.5 * uz, g0 + Ht, z + a * 1.5 * uz + b * 1.5 * ux, () => g.SteelR, 0.6);
            char axis = Math.Abs(ux) > Math.Abs(uz) ? 'x' : 'z';
            double R = 8 + g.R() * 4;
            k.Gear(x, g0 + Ht + R * 0.2, z, axis, R, 2, 6, g.R(), () => g.R() < 0.7 ? g.Bronze : g.RustA);
            k.Gear(x + (axis == 'z' ? R + 4 : 0), g0 + Ht - R * 0.4, z + (axis == 'x' ? R + 4 : 0), axis, R * 0.5, 2, 4, g.R(), () => g.RustA);
        }
        void BeamEngine(int x, int z, double yaw)
        {
            int g0 = k.Ground(x, z);
            if (Ceiling(x, z) - g0 < 22) return;
            double ux = Math.Cos(yaw), uz = Math.Sin(yaw);
            k.Tube(x + ux * 6, g0, z + uz * 6, x + ux * 6, g0 + 10, z + uz * 6, 2.6, 1, () => Rust(), false, 3);
            foreach (int s2 in new[] { -3, 3 }) k.Line(x - s2 * uz, g0 - 1, z + s2 * ux, x, g0 + 15, z, () => g.SteelR, 0.7);
            k.Line(x - ux * 9, g0 + 15 + 2, z - uz * 9, x + ux * 9, g0 + 15 - 1, z + uz * 9, () => g.RustA, 0.9);
            k.Line(x + ux * 6, g0 + 14, z + uz * 6, x + ux * 6, g0 + 10, z + uz * 6, () => g.Steel);
            k.Line(x - ux * 9, g0 + 17, z - uz * 9, x - ux * 9, g0 - 1, z - uz * 9, () => g.Steel);
            k.Gear(x - ux * 13, g0 + 8, z - uz * 13, Math.Abs(ux) > Math.Abs(uz) ? 'z' : 'x', 7, 2, 6, 0.2, () => g.RustA);
        }

        // the three halls the stairwells come up in, beside the upper works
        // they serve: the stairwell drops from the hall floor to below the
        // safe ceiling, and over a work it would cut into the work's top
        // (the Laboratories' dome is barely under that ceiling), so a hall
        // keeps its stairwell off where every upper work is planned to go
        var planned = GmWorkSpecs().Where(s => s.Upper)
            .Select(s => GmPlaceFrame(g, s.Bearing, s.R, s.Floor).Rect(s.U0, s.W0, s.U1, s.W1)).ToList();
        bool OverWork(int x, int z) => planned.Any(r => x >= r.X0 - 12 && x <= r.X1 + 12 && z >= r.Z0 - 12 && z <= r.Z1 + 12);
        foreach (var (name, bearing) in new[] { ("Workers' Quarters", 157.0), ("Archive", 262.0), ("Laboratories", 335.0) })
        {
            bool done = false;
            foreach (double off in new[] { 0.0, 4, -4, 8, -8, 12, -12, 16, -16, 20, -20, 24, -24 })
            {
                if (done) break;
                double th = (bearing + off) * Math.PI / 180;
                int hx = cx + (int)Math.Round(Math.Cos(th) * ringR), hz = cz + (int)Math.Round(Math.Sin(th) * ringR);
                // the hall's long axis runs along the ring (24 long, 14 wide)
                bool alongX = Math.Abs(Math.Sin(th)) > Math.Abs(Math.Cos(th));
                int hl = 12, hw = 7;
                int x0 = hx - (alongX ? hl : hw), x1 = hx + (alongX ? hl : hw), z0 = hz - (alongX ? hw : hl), z1 = hz + (alongX ? hw : hl);
                // a doorway at the hall's west (or north) end, the stairwell GmLead inside it
                int dxd = alongX ? 1 : 0, dzd = alongX ? 0 : 1;
                int doorX = alongX ? x0 : hx, doorZ = alongX ? hz : z0;
                if (OverWork(doorX - dxd + dxd * GmLead, doorZ - dzd + dzd * GmLead)) continue;
                if (!FootprintOk(x0, z0, x1, z1, 3)) continue;
                int F = Building(x0, z0, x1, z1, 2, 1, true);
                var hb = g.Buildings[^1]; hb.Kind = 2; g.Buildings[^1] = hb;     // it has its stairwell already
                for (int s = -3; s <= 3; s++)
                    for (int y = F; y < F + 8; y++) k.Set(doorX + (alongX ? 0 : s), y, doorZ + (alongX ? s : 0), 0);
                g.HallDoors.Add((doorX - dxd, F, doorZ - dzd, dxd, dzd));
                sapi.Logger.Notification("[landmassgenerator] great machine plateau hall ({0}) at HUD {1}, {2}, {3}", name,
                    doorX - dxd + dxd * GmLead - sapi.WorldManager.MapSizeX / 2, F, doorZ - dzd + dzd * GmLead - sapi.WorldManager.MapSizeZ / 2);
                done = true;
                nMachines++;
            }
        }
        // the machinery round the ring
        {
            int count = (int)(Math.PI * 2 * ringR / 34);
            for (int i = 0; i < count; i++)
            {
                double th = (i + 0.3 * g.R()) * Math.PI * 2 / count;
                double r = ringR + (g.R() - 0.5) * ringHalf;
                int x = cx + (int)Math.Round(Math.Cos(th) * r), z = cz + (int)Math.Round(Math.Sin(th) * r);
                if (Kept(x, z, 10) || OnWalk(x, z, 10) || Cut(x, z)) continue;
                if (!FootprintOk(x - 6, z - 6, x + 6, z + 6, 1)) continue;
                double tang = th + Math.PI / 2;
                switch (i % 6)
                {
                    case 0:
                        Smokestack(x, z, 26 + g.RI(0, 18), 2.6 + g.R());
                        if (g.R() < 0.6) Smokestack(x + (int)(Math.Cos(tang) * 8), z + (int)(Math.Sin(tang) * 8), 20 + g.RI(0, 14), 2.2);
                        break;
                    case 1: GearTower(x, z, tang); break;
                    case 2:
                        Tank(x, z, 4 + g.R() * 2, 8 + g.RI(0, 5));
                        Tank(x + (int)(Math.Cos(tang) * 11), z + (int)(Math.Sin(tang) * 11), 3.5 + g.R() * 1.5, 7 + g.RI(0, 4));
                        PipeRun(x, z, x + Math.Cos(tang) * 11, z + Math.Sin(tang) * 11, k.Ground(x, z) + 6, 1.2);
                        break;
                    case 3: BeamEngine(x, z, tang); break;
                    case 4:
                    {
                        // a wrecked engine hall with a boiler lying in it
                        bool alongX = Math.Abs(Math.Sin(th)) > Math.Abs(Math.Cos(th));
                        int x0 = x - (alongX ? 10 : 6), x1 = x + (alongX ? 10 : 6), z0 = z - (alongX ? 6 : 10), z1 = z + (alongX ? 6 : 10);
                        if (!FootprintOk(x0, z0, x1, z1, 1)) break;
                        int F = Building(x0, z0, x1, z1, 1, g.R() < 0.5 ? 0 : 3, true);
                        k.Tube(x - (alongX ? 6 : 0), F + 3, z - (alongX ? 0 : 6), x + (alongX ? 6 : 0), F + 3, z + (alongX ? 0 : 6), 3, 1, () => Rust(), false, 3);
                        break;
                    }
                    default:
                    {
                        // a pipe rack along the ring
                        double y = k.Ground(x, z) + 5 + g.RI(0, 3);
                        if (Ceiling(x, z) < y + 6) break;
                        double x1 = x + Math.Cos(tang) * 28, z1 = z + Math.Sin(tang) * 28;
                        if (Cut((int)x1, (int)z1) || OnWalk((int)x1, (int)z1, 4)) break;
                        PipeRun(x, z, x1, z1, y, 2.2);
                        PipeRun(x - Math.Cos(th) * 3, z - Math.Sin(th) * 3, x1 - Math.Cos(th) * 3, z1 - Math.Sin(th) * 3, y + 1, 1.3);
                        break;
                    }
                }
                g.Keep.Add((x, z, 9));
                nMachines++;
            }
        }

        // the crater's fried buildings: a burnt town on a street grid, a lot
        // every 19 blocks each way with a street 3 wide between, a shell
        // filling most of each lot, taller toward the rim
        const int Lot = 19;
        int rejBounds = 0, rejFoot = 0, rejSlope = 0, rejCeil = 0;
        double townIn = g.AbyssR * 1.42, townOut = g.RimR - 9;
        for (int gx = cx - g.RimR; gx <= cx + g.RimR; gx += Lot)
            for (int gz = cz - g.RimR; gz <= cz + g.RimR; gz += Lot)
            {
                int w = g.RI(9, Lot - 4), d = g.RI(9, Lot - 4);
                int x0 = gx + g.RI(0, Lot - 3 - w), x1 = x0 + w, z0 = gz + g.RI(0, Lot - 3 - d), z1 = z0 + d;
                bool inside = true;
                foreach (var (px, pz) in new[] { (x0, z0), (x1, z0), (x0, z1), (x1, z1) })
                {
                    double pr = Hyp(px - cx, pz - cz);
                    if (pr < townIn || pr > townOut) inside = false;
                }
                if (!inside) { rejBounds++; continue; }
                if (g.R() < 0.06) continue;                      // now and then an empty lot, a burnt square
                if (!FootprintOk(x0, z0, x1, z1, 1))
                {
                    // a smaller ruin squeezed into what is left of the lot
                    bool fits = false;
                    for (int t = 0; t < 4 && !fits; t++)
                    {
                        int w2 = g.RI(5, 8), d2 = g.RI(5, 8);
                        int a0 = gx + g.RI(0, Lot - 3 - w2), b0 = gz + g.RI(0, Lot - 3 - d2);
                        if (!FootprintOk(a0, b0, a0 + w2, b0 + d2, 1)) continue;
                        x0 = a0; z0 = b0; x1 = a0 + w2; z1 = b0 + d2; fits = true;
                    }
                    if (!fits) { rejFoot++; continue; }
                }
                int lo = int.MaxValue, hi = int.MinValue;
                for (int xx = x0; xx <= x1; xx += 2) for (int zz = z0; zz <= z1; zz += 2) { int gl = k.Ground(xx, zz); lo = Math.Min(lo, gl); hi = Math.Max(hi, gl); }
                if (hi - lo > 22) { rejSlope++; continue; }
                double outer = (Hyp((x0 + x1) / 2.0 - cx, (z0 + z1) / 2.0 - cz) - townIn) / (townOut - townIn);
                int storeys = 1 + (g.R() < 0.4 + 0.3 * outer ? 1 : 0) + (g.R() < 0.12 + 0.2 * outer ? 1 : 0);
                while (storeys > 0 && hi + storeys * 5 + 2 > Ceiling((x0 + x1) / 2.0, (z0 + z1) / 2.0)) storeys--;
                if (storeys == 0) { rejCeil++; continue; }
                Building(x0, z0, x1, z1, storeys, g.RI(0, 3), false, false);
            }
        sapi.Logger.Notification("[landmassgenerator] great machine surface: collar {0} blocks, throat ring {1}, fallen metal {2}, {3} fried buildings, {4} machines and halls, {5} stairwell hall(s)",
            nCollar, nThroat, nFallen, nFried, nMachines, g.HallDoors.Count);
        sapi.Logger.Notification("[landmassgenerator] great machine town lots left empty: {0} on a chasm, the walkway or fallen metal, {1} too steep, {2} under a chain", rejFoot, rejSlope, rejCeil);
    }
}
