using System;
using System.Collections.Generic;
using System.Linq;

using Vintagestory.API.MathTools;
using Func0 = System.Func<int>;

namespace LandmassGenerator;

// The Rustfall megastructures: sunken power pylons, the rusted refinery, the
// ruined power plant and the Great Machine (the Eerie Island's graveyard is
// in EerieIsland.cs).
// They share one toolkit (MegaKit): voxel lines, tubes, gears, hyperboloid
// shells, swept curves, and an Air() that leaves water where the sea would
// be and air where the ground was. Every builder writes through the same
// bulk accessor as the other structures (BuildStruct hands its Set/Ground/...
// over), so the build, the dump and the real world are the same code.
public partial class LandmassGeneratorModSystem
{
    internal static readonly HashSet<string> MegaKinds = new() { "pylons", "refinery", "graveyard", "powerplant", "greatmachine", "ruin", "spoils", "gardens", "alchemist" };

    private sealed class MegaKit
    {
        public IslandJob Job;
        public StructDef Def;
        public CaveRand Rand;
        public int Sea;
        public Action<int, int, int, int> Set;
        public Action<int, int, int, int> SetFluid;
        public Func<int, int, int> Ground;
        public Func<int, int, double> Hash01;
        public Func<string, int> Id;
        public Action<int, int, int, string, double> Clutter;
        public Action<int, int, int, int> Glow;
        public List<Action> AfterCommit;
        public Func<string, int> Need;   // Id that logs a code the game does not have
        public int RustA, RustB, PlateA, PlateB;
        public LandmassGeneratorModSystem Mod;

        public double R() => Rand.NextDouble();
        public int RI(int lo, int hi) => lo + (int)(Rand.NextDouble() * (hi - lo + 1));
        public int Rust() => R() < 0.6 && RustA != 0 ? RustA : (RustB != 0 ? RustB : RustA);

        public int IdF(params string[] codes)
        {
            foreach (var c in codes) { int i = Id(c); if (i != 0) return i; }
            return 0;
        }

        public string Opt(string k, string d) => Def.Opts.TryGetValue(k, out var v) ? v : d;
        public double OptD(string k, double d) => Def.Opts.TryGetValue(k, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double r) ? r : d;
        public int OptI(string k, int d) => (int)Math.Round(OptD(k, d));

        // Empty a cell: sea water where it lies under the sea level above the
        // ground (a drowned room fills), air where it was ground or sky.
        public void Air(int x, int y, int z)
        {
            if (y < Sea && y > Ground(x, z)) SetFluid(x, y, z, Job.SaltWaterId);
            else Set(x, y, z, 0);
        }

        public void Put(int x, int y, int z, int id)
        {
            if (id == 0) Air(x, y, z); else Set(x, y, z, id);
        }

        public void Box(int x0, int y0, int z0, int x1, int y1, int z1, int id)
        {
            for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
                for (int z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++)
                    for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++) Put(x, y, z, id);
        }

        // A 1-block (or r-thick) voxel line.
        public void Line(double x0, double y0, double z0, double x1, double y1, double z1, Func<int> id, double r = 0)
        {
            double dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            int n = Math.Max(1, (int)Math.Ceiling(len / 0.5));
            int lx = int.MinValue, ly = 0, lz = 0;
            for (int i = 0; i <= n; i++)
            {
                double t = i / (double)n;
                double px = x0 + dx * t, py = y0 + dy * t, pz = z0 + dz * t;
                if (r <= 0.01)
                {
                    int ix = (int)Math.Round(px), iy = (int)Math.Round(py), iz = (int)Math.Round(pz);
                    if (ix == lx && iy == ly && iz == lz) continue;
                    lx = ix; ly = iy; lz = iz;
                    Set(ix, iy, iz, id());
                }
                else Ball(px, py, pz, r, id);
            }
        }

        public void Ball(double cx, double cy, double cz, double r, Func<int> id)
        {
            int ri = (int)Math.Ceiling(r);
            for (int x = -ri; x <= ri; x++)
                for (int y = -ri; y <= ri; y++)
                    for (int z = -ri; z <= ri; z++)
                    {
                        double fx = Math.Round(cx) + x - cx, fy = Math.Round(cy) + y - cy, fz = Math.Round(cz) + z - cz;
                        if (fx * fx + fy * fy + fz * fz > r * r + 0.25) continue;
                        Set((int)Math.Round(cx) + x, (int)Math.Round(cy) + y, (int)Math.Round(cz) + z, id());
                    }
        }

        // A tube from A to B: shell `wall` thick of shellId(), interior
        // emptied (water below the sea) when hollow, ends open. Optional
        // flange rings every `ringEvery` blocks along it.
        public void Tube(double ax, double ay, double az, double bx, double by, double bz, double r, double wall,
            Func<int> shell, bool hollow = true, double ringEvery = 0, Func<int, int, int, bool> skip = null)
        {
            double dx = bx - ax, dy = by - ay, dz = bz - az;
            double len2 = dx * dx + dy * dy + dz * dz, len = Math.Sqrt(len2);
            if (len < 0.5) return;
            double rr = r + (ringEvery > 0 ? 1 : 0);
            int x0 = (int)Math.Floor(Math.Min(ax, bx) - rr - 1), x1 = (int)Math.Ceiling(Math.Max(ax, bx) + rr + 1);
            int y0 = (int)Math.Floor(Math.Min(ay, by) - rr - 1), y1 = (int)Math.Ceiling(Math.Max(ay, by) + rr + 1);
            int z0 = (int)Math.Floor(Math.Min(az, bz) - rr - 1), z1 = (int)Math.Ceiling(Math.Max(az, bz) + rr + 1);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                    {
                        double px = x - ax, py = y - ay, pz = z - az;
                        double t = (px * dx + py * dy + pz * dz) / len2;
                        if (t < 0 || t > 1) continue;
                        double qx = px - dx * t, qy = py - dy * t, qz = pz - dz * t;
                        double d = Math.Sqrt(qx * qx + qy * qy + qz * qz);
                        bool ring = ringEvery > 0 && ((t * len) % ringEvery) < 1.0;
                        double outer = ring ? r + 1 : r;
                        if (d > outer) continue;
                        if (skip != null && skip(x, y, z)) continue;
                        if (d > r - wall || ring && d > r - wall) Set(x, y, z, shell());
                        else if (hollow) Air(x, y, z);
                        else Set(x, y, z, shell());
                    }
        }

        // A gear lying in the plane perpendicular to `axis` ('y' = flat, like
        // a clock wheel; 'x' / 'z' = standing). Rim, teeth, spokes, hub.
        public void Gear(double cx, double cy, double cz, char axis, double R, int thick, int spokes, double phase, Func<int> id, Func<int, int, int, bool> clip = null)
        {
            int teeth = Math.Max(8, (int)Math.Round(R * 1.4));
            double step = R > 20 ? 0.5 : 0.45;
            var done = new HashSet<long>();
            for (double a = -R - 1.6; a <= R + 1.6; a += step)
                for (double b = -R - 1.6; b <= R + 1.6; b += step)
                {
                    double r = Math.Sqrt(a * a + b * b), th = Math.Atan2(b, a) + phase;
                    double hub = Math.Max(1.6, R * 0.14), rim = Math.Max(1.2, R * 0.09), tooth = Math.Max(1.2, R * 0.07);
                    bool solid;
                    if (r <= hub) solid = r > hub * 0.45 || R < 12;   // a hole for the axle in big ones
                    else if (r >= R - rim && r <= R) solid = true;
                    else if (r > R && r <= R + tooth) solid = Math.Cos(teeth * th) > 0.2;
                    else if (r < R - rim)
                    {
                        solid = false;
                        double sw = Math.Max(0.7, R * 0.05);
                        for (int sp = 0; sp < spokes && !solid; sp++)
                        {
                            double sa = phase + sp * Math.PI * 2 / spokes;
                            solid = Math.Cos(sa) * a + Math.Sin(sa) * b > 0 && Math.Abs(-Math.Sin(sa) * a + Math.Cos(sa) * b) < sw;
                        }
                    }
                    else solid = false;
                    if (!solid) continue;
                    for (int k = 0; k < thick; k++)
                    {
                        int x, y, z;
                        if (axis == 'y') { x = (int)Math.Round(cx + a); y = (int)Math.Round(cy) + k; z = (int)Math.Round(cz + b); }
                        else if (axis == 'x') { x = (int)Math.Round(cx) + k; y = (int)Math.Round(cy + b); z = (int)Math.Round(cz + a); }
                        else { x = (int)Math.Round(cx + a); y = (int)Math.Round(cy + b); z = (int)Math.Round(cz) + k; }
                        long key = ((long)(x & 0xFFFFF) << 40) | ((long)(y & 0xFFFFF) << 20) | (long)(z & 0xFFFFF);
                        if (!done.Add(key)) continue;
                        if (clip != null && clip(x, y, z)) continue;
                        Set(x, y, z, id());
                    }
                }
        }
    }

    private void BuildMega(MegaKit k, int cx, int cz)
    {
        switch (k.Def.Kind)
        {
            case "pylons": MegaPylons(k, cx, cz); break;
            case "graveyard": MegaGraveyard(k, cx, cz); break;
            case "refinery": MegaRefinery(k, cx, cz); break;
            case "powerplant": MegaPowerPlant(k, cx, cz); break;
            case "greatmachine": MegaGreatMachine(k, cx, cz); break;
            case "ruin": MegaRuin(k, cx, cz); break;
            case "spoils": MegaSpoils(k, cx, cz); break;
            case "gardens": MegaGardens(k, cx, cz); break;
            case "alchemist": MegaAlchemist(k, cx, cz); break;
        }
    }

    // ── SUNKEN POWER PYLONS ─────────────────────────────────────────────
    // A line of lattice transmission towers marching across the island or
    // the sea floor, count= of them spacing= apart along heading= (map
    // degrees). Each one stands sunk into the ground, leans a little or a
    // lot, and some have snapped: their tops lie broken beside them. Cables
    // sag from arm to arm where both towers still have arms, and hang loose
    // where one has fallen. height= sets the tallest; the others vary.
    private void MegaPylons(MegaKit k, int cx, int cz)
    {
        int count = Math.Max(1, k.OptI("count", 5));
        double spacing = k.OptD("spacing", 46);
        // heading= is in map degrees, so it turns with the island's rotate=
        double heading = k.OptD("heading", 0) * Math.PI / 180.0 + Math.Atan2(k.Job.RotSin, k.Job.RotCos);
        int hmax = k.OptI("height", 44);
        double ux = Math.Sin(heading), uz = -Math.Cos(heading);    // map degrees: 0 = north (-z)
        var arms = new List<(double X, double Y, double Z)[]>();
        for (int i = 0; i < count; i++)
        {
            double along = (i - (count - 1) / 2.0) * spacing;
            double px = cx + ux * along + (k.R() - 0.5) * 6, pz = cz + uz * along + (k.R() - 0.5) * 6;
            int H = (int)(hmax * (0.78 + k.R() * 0.22));
            // above=N: tall enough that the crossarms clear the sea by N
            // (on a deep sea floor the towers would otherwise drown whole)
            int above = k.OptI("above", -1);
            if (above >= 0)
            {
                int g = k.Ground((int)Math.Round(px), (int)Math.Round(pz));
                H = Math.Max(H, (int)((k.Sea - g + above + 6) / 0.86));
            }
            double lean = k.R() < 0.35 ? 18 + k.R() * 22 : k.R() * 9;           // degrees
            double leanDir = k.R() * Math.PI * 2;
            int sink = 2 + k.RI(0, 9);
            bool snapped = k.R() < 0.3;
            arms.Add(Pylon(k, px, pz, heading, H, lean, leanDir, sink, snapped));
        }
        // cables between neighbours: three per side, sagging
        for (int i = 0; i + 1 < arms.Count; i++)
        {
            var a = arms[i]; var b = arms[i + 1];
            if (a == null || b == null) continue;
            for (int c = 0; c < Math.Min(a.Length, b.Length); c++)
            {
                if (k.R() < 0.3) continue;   // this one has parted
                Cable(k, a[c], b[c], 0.08);
            }
        }
    }

    private void Cable(MegaKit k, (double X, double Y, double Z) a, (double X, double Y, double Z) b, double sagFrac)
    {
        double dx = b.X - a.X, dz = b.Z - a.Z, len = Math.Sqrt(dx * dx + dz * dz);
        double sag = len * sagFrac;
        int n = Math.Max(2, (int)(len * 3));
        int px = int.MinValue, py = 0, pz = 0;
        for (int i = 0; i <= n; i++)
        {
            double t = i / (double)n;
            double x = a.X + dx * t, z = a.Z + dz * t, y = a.Y + (b.Y - a.Y) * t - Math.Sin(t * Math.PI) * sag;
            int ix = (int)Math.Round(x), iy = (int)Math.Round(y), iz = (int)Math.Round(z);
            if (iy <= k.Ground(ix, iz)) continue;      // lies on the ground there: buried, skip
            if (ix == px && iy == py && iz == pz) continue;
            px = ix; py = iy; pz = iz;
            k.Set(ix, iy, iz, k.Rust());
        }
    }

    // One lattice tower. Returns its arm tips in world space (null if it
    // snapped below its arms). Built in local space (u across the line, v
    // along it, y up), then leaned about its foot and placed.
    private (double X, double Y, double Z)[] Pylon(MegaKit k, double fx, double fz, double heading, int H, double leanDeg, double leanDir, int sink, bool snapped)
    {
        int g = k.Ground((int)Math.Round(fx), (int)Math.Round(fz));
        double baseY = g - sink;
        double b0 = Math.Max(4.0, H * 0.15), b1 = Math.Max(1.5, H * 0.045);
        double armY1 = H * 0.70, armY2 = H * 0.84, armW = Math.Max(8, H * 0.30);
        double cs = Math.Cos(heading), sn = Math.Sin(heading);
        double lean = leanDeg * Math.PI / 180, lx = Math.Cos(leanDir), lz = Math.Sin(leanDir);
        double cut = snapped ? H * (0.35 + k.R() * 0.3) : 1e9;
        int steel = k.IdF("metalblock-new-plain-iron", "metalblock-new-plain-steel");
        System.Func<int> mat = () => k.R() < 0.75 ? k.Rust() : (steel != 0 ? steel : k.Rust());

        // local (u across, y up, v along) -> world, leaning the tower
        (double X, double Y, double Z) W(double u, double y, double v)
        {
            double wx = u * cs - v * sn, wz = u * sn + v * cs;           // heading: u across the line
            double off = Math.Sin(lean) * y, yy = Math.Cos(lean) * y;    // lean the tower about its foot
            return (fx + wx + lx * off, baseY + yy, fz + wz + lz * off);
        }
        void Seg(double u0, double y0, double v0, double u1, double y1, double v1)
        {
            if (y0 > cut && y1 > cut) return;
            if (y1 > cut) { double f = (cut - y0) / (y1 - y0); u1 = u0 + (u1 - u0) * f; v1 = v0 + (v1 - v0) * f; y1 = cut; }
            if (y0 > cut) { double f = (cut - y1) / (y0 - y1); u0 = u1 + (u0 - u1) * f; v0 = v1 + (v0 - v1) * f; y0 = cut; }
            var a = W(u0, y0, v0); var b = W(u1, y1, v1);
            k.Line(a.X, a.Y, a.Z, b.X, b.Y, b.Z, mat);
        }
        double Half(double y) => y < H * 0.74 ? b0 + (b1 - b0) * (y / (H * 0.74)) : b1;

        // legs
        foreach (var (su, sv) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
        {
            Seg(su * b0, 0, sv * b0, su * b1, H * 0.74, sv * b1);
            Seg(su * b1, H * 0.74, sv * b1, su * b1 * 0.4, H * 0.95, sv * b1 * 0.4);
        }
        Seg(0, H * 0.95, 0, 0, H, 0);
        // a ring at each panel and ONE diagonal per face per panel,
        // zigzagging up: an open lattice you can see through
        double stepY = Math.Max(5, H / 7.0);
        int panel = 0;
        for (double y = 0; y + stepY <= H * 0.74 + 0.1; y += stepY, panel++)
        {
            double h0 = Half(y), h1 = Half(y + stepY);
            foreach (var (su, sv, tu, tv) in new[] { (1, 1, 1, -1), (1, -1, -1, -1), (-1, -1, -1, 1), (-1, 1, 1, 1) })
            {
                if (panel % 2 == 0) Seg(su * h0, y, sv * h0, tu * h1, y + stepY, tv * h1);
                else Seg(tu * h0, y, tv * h0, su * h1, y + stepY, sv * h1);
                Seg(su * h1, y + stepY, sv * h1, tu * h1, y + stepY, tv * h1);
            }
        }
        // two crossarm levels, each a flat truss out to both sides
        var tips = new List<(double, double, double)>();
        foreach (double ay in new[] { armY1, armY2 })
        {
            double w = ay == armY1 ? armW : armW * 0.75;
            foreach (int s in new[] { -1, 1 })
            {
                Seg(s * b1, ay, b1, s * w, ay, 0); Seg(s * b1, ay, -b1, s * w, ay, 0);
                Seg(s * b1, ay + 2, 0, s * w, ay, 0);
                if (ay <= cut)
                {
                    // insulator string: two blocks of chain under the tip
                    var t = W(s * w, ay - 1, 0); var t2 = W(s * w, ay - 2, 0);
                    int chain = k.Id("supportchain-two");
                    k.Set((int)Math.Round(t.X), (int)Math.Round(t.Y), (int)Math.Round(t.Z), chain != 0 ? chain : k.Rust());
                    tips.Add(t2);
                }
            }
        }
        if (snapped)
        {
            // the broken top lies on the ground beside the stump, toppled
            // away along the lean
            double fallLen = H - cut;
            double ex = fx + lx * (cut * Math.Sin(lean) + fallLen), ez = fz + lz * (cut * Math.Sin(lean) + fallLen);
            var top = W(0, cut, 0);
            int gEnd = k.Ground((int)Math.Round(ex), (int)Math.Round(ez));
            double hw = b1 + 1;
            foreach (var (o1, o2) in new[] { (-hw, -hw), (-hw, hw), (hw, -hw), (hw, hw) })
                k.Line(top.X + o1 * 0.5, top.Y - 1, top.Z + o2 * 0.5, ex + o1 * 0.5, gEnd + 1, ez + o2 * 0.5, mat);
            for (double t = 0; t <= 1; t += 0.12)
                k.Line(top.X + (ex - top.X) * t - hw, top.Y + (gEnd + 1 - top.Y) * t, top.Z + (ez - top.Z) * t - hw,
                       top.X + (ex - top.X) * t + hw, top.Y + (gEnd + 1 - top.Y) * t, top.Z + (ez - top.Z) * t + hw, mat);
            return null;
        }
        return tips.ToArray();
    }
}

public partial class LandmassGeneratorModSystem
{
    // ── THE RUSTED REFINERY ─────────────────────────────────────────────
    // Industrial ruin on a low island: concrete pads, a pipe rack of four
    // lines on steel trestles running the length of the site (gaps where
    // sections fell, the broken ends drooping to the ground), two colossal
    // penstock pipes sloping down to the shore on concrete saddles, round
    // storage tanks (one torn open, roofs fallen in), distillation columns
    // with ring platforms (one toppled), open scaffolding towers, a gantry
    // crane with its hook still hanging, a flooded sump, and the concrete
    // control room with what the crew left. size= is the half-width.
    private void MegaRefinery(MegaKit k, int cx, int cz)
    {
        int R = Math.Max(40, k.Def.Size);
        int conc = k.IdF("rockpolished-andesite", "stonebricks-andesite", "rock-andesite");
        int concB = k.IdF("crackedstonebricks-andesite", "rock-andesite", "cobblestone-andesite");
        int steel = k.IdF("metalblock-new-plain-iron", "metalblock-new-plain-steel");
        int fenceNS = k.Id("ironfence-base-ns"), fenceEW = k.Id("ironfence-base-ew");
        int glassId = k.IdF("glass-plain");
        int chain = k.Id("supportchain-two");
        int water = k.Job.WaterId;
        System.Func<int> rust = () => k.Rust();
        System.Func<int> beam = () => k.R() < 0.7 ? k.Rust() : (steel != 0 ? steel : k.Rust());
        string[] junk = { "pipelong3-aged", "pipelong4-aged", "pipelong2", "gear24", "gearbox1", "geartrain3" };
        int g0 = Median(k, cx, cz, R / 2);

        // concrete pad levelled at height y over a rectangle: cracked, with
        // grass breaking through (the pad is skipped at random)
        void Pad(int x0, int z0, int x1, int z1, int y)
        {
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    int g = k.Ground(x, z);
                    if (g < k.Sea - 1) continue;
                    for (int yy = Math.Min(g, y - 1); yy < y; yy++) k.Set(x, yy, z, k.Job.StoneId);
                    if (k.Hash01(x * 3, z * 7) > 0.12) k.Set(x, y, z, k.Hash01(x, z * 5) < 0.25 ? concB : conc);
                    for (int yy = y + 1; yy <= Math.Max(g, y) + 2; yy++) k.Set(x, yy, z, 0);
                }
        }
        Pad(cx - R, cz - R / 3, cx + R, cz + R / 3, g0);

        // 1. The pipe rack along x: trestles every 12, four lines.
        int rz = cz - 6;
        double[] pr = { 1.6, 2.2, 2.6, 1.6 };
        int[] py = { g0 + 6, g0 + 6, g0 + 12, g0 + 12 };
        int[] pz = { rz - 4, rz + 4, rz - 3, rz + 3 };
        for (int x = cx - R + 4; x <= cx + R - 4; x += 12)
        {
            for (int s = -1; s <= 1; s += 2)
                k.Line(x, g0, rz + s * 7, x, g0 + 15, rz + s * 7, beam);
            foreach (int y in new[] { g0 + 3, g0 + 9, g0 + 15 })
                k.Line(x, y, rz - 7, x, y, rz + 7, beam);
            k.Line(x, g0, rz - 7, x, g0 + 9, rz + 7, beam);
        }
        for (int i = 0; i < 4; i++)
        {
            // each line in three to five sections; one section fell
            int secs = 3 + k.RI(0, 2), fallen = k.RI(0, secs - 1);
            double len = 2.0 * (R - 4) / secs;
            for (int s = 0; s < secs; s++)
            {
                double x0 = cx - R + 4 + s * len, x1 = x0 + len - 1;
                if (s == fallen && i % 2 == 0)
                {
                    // the section hangs from one end down to the ground
                    k.Tube(x0, py[i], pz[i], x1, k.Ground((int)x1, pz[i]) + pr[i], pz[i] + 2, pr[i], 1, rust, true, 8);
                    continue;
                }
                if (s == fallen) continue;
                k.Tube(x0, py[i], pz[i], x1, py[i], pz[i], pr[i], 1, rust, true, 8);
            }
        }

        // 2. Two colossal penstocks running south, sloping to the shore.
        for (int p = 0; p < 2; p++)
        {
            int x = cx - R / 3 + p * 14;
            int zA = cz + R / 3, zB = cz + R + 20;
            int yA = g0 + 14, yB = Math.Max(k.Sea - 2, k.Ground(x, zB) + 4);
            k.Tube(x, yA, zA, x, yB, zB, 4.5, 1.2, rust, true, 7);
            for (int z = zA; z <= zB; z += 10)
            {
                double t = (z - zA) / (double)(zB - zA);
                int y = (int)(yA + (yB - yA) * t);
                int g = k.Ground(x, z);
                for (int yy = g - 2; yy <= y - 5; yy++)
                    for (int dx = -4; dx <= 4; dx++)
                        for (int dz = 0; dz <= 1; dz++)
                            k.Set(x + dx, yy, z + dz, conc);
            }
            // the intake: the pipe ends in a concrete block at the pad edge
            k.Box(x - 6, g0, zA - 4, x + 6, yA + 6, zA - 1, conc);
            k.Tube(x, yA, zA - 5, x, yA, zA + 1, 4.5, 1.2, rust, true);
        }

        // 3. Storage tanks in the four corners.
        var tanks = new[] { (cx - R + 18, cz - R / 3 - 16, 10), (cx + R - 18, cz - R / 3 - 14, 11), (cx + R - 22, cz + R / 3 + 16, 9), (cx - R + 20, cz + R / 3 + 18, 8) };
        for (int t = 0; t < tanks.Length; t++)
        {
            var (tx, tz, tr) = tanks[t];
            int tg = Median(k, tx, tz, tr);
            Pad(tx - tr - 2, tz - tr - 2, tx + tr + 2, tz + tr + 2, tg);
            int th = 12 + k.RI(0, 6);
            bool torn = t == 1;
            double tearA = k.R() * Math.PI * 2;
            for (int x = tx - tr; x <= tx + tr; x++)
                for (int z = tz - tr; z <= tz + tr; z++)
                {
                    double d = Math.Sqrt((x - tx) * (x - tx) + (z - tz) * (z - tz));
                    if (d > tr + 0.5) continue;
                    double ang = Math.Atan2(z - tz, x - tx);
                    for (int y = tg + 1; y <= tg + th + 2; y++)
                    {
                        bool wall = d > tr - 1.25;
                        bool roof = y >= tg + th + (int)Math.Round(2 * (1 - d / tr));
                        if (!wall && !roof) { if (y > tg + th - 1) continue; k.Set(x, y, z, 0); continue; }
                        if (roof && !wall && y > tg + th + (int)Math.Round(2 * (1 - d / tr))) continue;
                        if (roof && k.Hash01(x * 5 + t, z * 3) < 0.45) continue;          // fallen in
                        if (torn && Math.Abs(Math.IEEERemainder(ang - tearA, Math.PI * 2)) < 0.6 && y > tg + 2 + (int)(Math.Abs(Math.IEEERemainder(ang - tearA, Math.PI * 2)) * 8)) continue;
                        k.Set(x, y, z, (y - tg) % 4 == 0 ? (steel != 0 ? steel : k.Rust()) : k.Rust());
                    }
                }
            // a ring walkway round the top, half of it gone
            for (double a = 0; a < Math.PI * 2; a += 0.04)
            {
                if (Math.Sin(a * 2 + t) < -0.3) continue;
                int x = tx + (int)Math.Round(Math.Cos(a) * (tr + 1)), z = tz + (int)Math.Round(Math.Sin(a) * (tr + 1));
                k.Set(x, tg + th - 1, z, beam());
                int f = Math.Abs(Math.Cos(a)) > 0.7 ? fenceNS : fenceEW;
                if (f != 0) k.Set(x, tg + th, z, f);
            }
        }

        // 4. Distillation columns at the centre, platforms every 8.
        var cols = new[] { (cx - 10, cz + 10, 32), (cx + 2, cz + 14, 40), (cx + 14, cz + 10, 27) };
        for (int c = 0; c < cols.Length; c++)
        {
            var (ox, oz, h) = cols[c];
            int og = Median(k, ox, oz, 4);
            if (c == 2)
            {
                // toppled: stump, and the column lying west across the pad
                k.Tube(ox, og, oz, ox, og + 6, oz, 3, 1, rust, true, 0);
                k.Tube(ox + 2, og + 3, oz - 2, ox + 2 + h - 8, og + 3, oz - 2 - (h - 8) / 3, 3, 1, rust, true, 8);
                continue;
            }
            k.Tube(ox, og, oz, ox, og + h, oz, 3, 1, rust, true, 0);
            for (int y = og + 8; y < og + h; y += 8)
            {
                for (int x = ox - 5; x <= ox + 5; x++)
                    for (int z = oz - 5; z <= oz + 5; z++)
                    {
                        double d = Math.Sqrt((x - ox) * (x - ox) + (z - oz) * (z - oz));
                        if (d < 3.5 || d > 5.5) continue;
                        if (k.Hash01(x * 7 + y, z * 3) < 0.2) continue;
                        k.Set(x, y, z, beam());
                        if (d > 4.6) { int f = Math.Abs(x - ox) > Math.Abs(z - oz) ? fenceNS : fenceEW; if (f != 0) k.Set(x, y + 1, z, f); }
                    }
            }
            // an overhead line from the top over to the rack
            k.Tube(ox, og + h - 2, oz, ox, og + h - 2, rz + 4, 1.4, 1, rust, true, 6);
            k.Tube(ox, og + h - 2, rz + 4, ox, py[2], rz + 4, 1.4, 1, rust, true, 6);
        }

        // 5. Scaffolding towers: open frames with X bracing and two floors.
        var scaf = new[] { (cx - 34, cz + 6, 22), (cx + 30, cz - 2, 28) };
        foreach (var (sx, sz, sh) in scaf)
        {
            int sg = Median(k, sx, sz, 4);
            const int w = 8;
            for (int y = 0; y <= sh; y += 6)
            {
                k.Line(sx, sg + y, sz, sx + w, sg + y, sz, beam); k.Line(sx, sg + y, sz + w, sx + w, sg + y, sz + w, beam);
                k.Line(sx, sg + y, sz, sx, sg + y, sz + w, beam); k.Line(sx + w, sg + y, sz, sx + w, sg + y, sz + w, beam);
                if (y + 6 <= sh)
                {
                    k.Line(sx, sg + y, sz, sx + w, sg + y + 6, sz, beam); k.Line(sx + w, sg + y, sz + w, sx, sg + y + 6, sz + w, beam);
                    k.Line(sx, sg + y, sz + w, sx, sg + y + 6, sz, beam); k.Line(sx + w, sg + y, sz, sx + w, sg + y + 6, sz + w, beam);
                }
                if (y == 12 || y == sh - sh % 6)
                    for (int x = sx + 1; x < sx + w; x++)
                        for (int z = sz + 1; z < sz + w; z++)
                            if (k.Hash01(x * 3 + y, z * 11) > 0.3) k.Set(x, sg + y, z, k.PlateA != 0 ? k.PlateA : beam());
            }
            foreach (var (ax, az) in new[] { (sx, sz), (sx + w, sz), (sx, sz + w), (sx + w, sz + w) })
                k.Line(ax, sg - 1, az, ax, sg + sh, az, beam);
        }

        // 6. The gantry crane over the sump, hook hanging.
        {
            int gz = cz - R / 3 - 4, gx0 = cx - 22, gx1 = cx + 22, gh = 24;
            int gg = Median(k, cx, gz, 10);
            foreach (int lx in new[] { gx0, gx1 })
            {
                k.Line(lx, gg, gz - 5, lx, gg + gh, gz, beam); k.Line(lx, gg, gz + 5, lx, gg + gh, gz, beam);
                for (int y = 6; y < gh; y += 6) k.Line(lx, gg + y, gz - 5 + y * 5 / gh, lx, gg + y, gz + 5 - y * 5 / gh, beam);
            }
            for (int dy = 0; dy <= 2; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    if (dy != 1 || dz == 0 || k.R() < 0.4) k.Line(gx0, gg + gh + dy, gz + dz, gx1, gg + gh + dy, gz + dz, beam);
            int hx = cx + 4;
            k.Box(hx - 1, gg + gh - 1, gz - 1, hx + 1, gg + gh - 1, gz + 1, beam());
            for (int y = gg + gh - 2; y >= gg + 8; y--) k.Set(hx, y, gz, chain != 0 ? chain : k.Rust());
            k.Line(hx - 2, gg + 7, gz, hx + 2, gg + 7, gz, beam); k.Set(hx - 2, gg + 6, gz, beam()); k.Set(hx + 2, gg + 6, gz, beam());
            // the flooded sump under it
            for (int x = cx - 12; x <= cx + 12; x++)
                for (int z = gz - 6; z <= gz + 6; z++)
                {
                    bool rim = Math.Abs(x - cx) == 12 || Math.Abs(z - gz) == 6;
                    for (int y = gg - 3; y <= gg; y++)
                    {
                        if (rim) k.Set(x, y, z, conc);
                        else if (y == gg - 3) k.Set(x, y, z, concB);
                        else k.SetFluid(x, y, z, water);
                    }
                }
        }

        // 7. The control room: a concrete box with window slots and the
        // crew's lockers (two chests).
        {
            int bx = cx + R / 2, bz = cz + R / 3 - 6;
            int bg = Median(k, bx, bz, 8);
            for (int x = bx - 7; x <= bx + 7; x++)
                for (int z = bz - 5; z <= bz + 5; z++)
                    for (int y = bg; y <= bg + 7; y++)
                    {
                        bool wall = Math.Abs(x - bx) == 7 || Math.Abs(z - bz) == 5;
                        bool floor = y == bg, roof = y == bg + 7;
                        if (floor || roof || wall)
                        {
                            bool window = wall && !roof && y >= bg + 3 && y <= bg + 4 && (Math.Abs(z - bz) == 5 ? (x - bx) % 3 != 0 : (z - bz) % 3 != 0);
                            bool door = Math.Abs(z - bz) == 5 && z < bz && Math.Abs(x - bx) <= 1 && y >= bg + 1 && y <= bg + 3;
                            if (door) { k.Set(x, y, z, 0); continue; }
                            if (window) { if (glassId != 0 && k.R() < 0.2) k.Set(x, y, z, glassId); else k.Set(x, y, z, 0); continue; }
                            if (roof && k.Hash01(x * 9, z * 5) < 0.18) { k.Set(x, y, z, 0); continue; }
                            k.Set(x, y, z, k.Hash01(x * 3 + y, z * 5) < 0.2 ? concB : conc);
                        }
                        else k.Set(x, y, z, 0);
                    }
            k.Job.StructChests.Add((new BlockPos(bx + 5, bg + 1, bz + 3, k.Job.Dim), "normal-aged", new List<string>(RollLoot(k.Rand, MachineLoot, 4 + k.RI(0, 2))) { "randomlore:rfpool-jonas" }, "refinery"));
            k.Job.StructChests.Add((new BlockPos(bx - 5, bg + 1, bz + 3, k.Job.Dim), "normal-aged", RollLoot(k.Rand, HutLoot, 3 + k.RI(0, 2)), "refinery"));
        }

        // 8. Scrap across the pads.
        for (int i = 0; i < R / 2; i++)
        {
            int x = cx + k.RI(-R, R), z = cz + k.RI(-R / 3, R / 3);
            int g = k.Ground(x, z);
            if (g < k.Sea || Math.Abs(z - rz) < 9) continue;   // not under the rack
            k.Clutter(x, g0 + 1, z, junk[k.RI(0, junk.Length - 1)], k.R() * Math.PI * 2);
        }
    }

    // The median designed ground over a square: a fair level to build on.
    private static int Median(MegaKit k, int cx, int cz, int r)
    {
        var gs = new List<int>();
        for (int x = cx - r; x <= cx + r; x += Math.Max(1, r / 4))
            for (int z = cz - r; z <= cz + r; z += Math.Max(1, r / 4)) gs.Add(k.Ground(x, z));
        gs.Sort();
        return Math.Max(k.Sea + 1, gs[gs.Count / 2]);
    }
    // ── THE RUINED POWER PLANT (the experimental warping site) ──────────
    // Three hyperboloid cooling towers of stained concrete standing on
    // their ring of legs, the third broken off halfway and spilled across
    // the ground; a brutalist reactor hall with a façade of deep vertical
    // fins and a breached dome inside; a transformer yard. Under the hall a
    // ramp drops 30 blocks into a vast pillared chamber where the warp rig
    // stands: a great upright ring of rusted steel lit along its inner rim,
    // the floor around it gone to drock. Round coolant tunnels run from the
    // chamber out under each tower. size= is the site's half-width.
    private void MegaPowerPlant(MegaKit k, int cx, int cz)
    {
        int R = Math.Max(50, k.Def.Size);
        int conc = k.IdF("rockpolished-andesite", "stonebricks-andesite", "rock-andesite");
        int concD = k.IdF("rock-andesite", "cobblestone-andesite");
        int stain = k.IdF("rockpolished-basalt", "rock-basalt");
        int rubble = k.IdF("cobblestone-andesite", "rock-andesite");
        int steel = k.IdF("metalblock-new-plain-iron", "metalblock-new-plain-steel");
        int glowB = k.Id("landmassgenerator:ghostlight-blue");
        int drock = k.Id("drock");
        int devSoil = k.IdF("devastatedsoil-1", "devastatedsoil-0");
        int fenceNS = k.Id("ironfence-base-ns"), fenceEW = k.Id("ironfence-base-ew");
        System.Func<int> rust = () => k.Rust();
        System.Func<int> concF = () => k.R() < 0.85 ? conc : concD;
        int g0 = Median(k, cx, cz, R / 2);

        // 1. Cooling towers.
        var towers = new[] { (cx - R / 2, cz - R / 3, 62), (cx + 4, cz - R * 2 / 3 + 6, 66), (cx + R / 2 + 4, cz - R / 4, 58) };
        for (int t = 0; t < towers.Length; t++)
        {
            var (tx, tz, H) = towers[t];
            int tg = Median(k, tx, tz, 16);
            CoolingTower(k, tx, tz, tg, H, 19, 12, conc, concD, stain, rubble, collapsed: t == 2);
        }

        // 2. The reactor hall: 56 x 30, 28 tall, finned façade.
        int hx = cx + 6, hz = cz + R / 3, hw = 28, hd = 15, hh = 28;
        int hg = Median(k, hx, hz, 20);
        for (int x = hx - hw - 2; x <= hx + hw + 2; x++)
            for (int z = hz - hd - 2; z <= hz + hd + 2; z++)
            {
                int g = k.Ground(x, z);
                for (int y = Math.Min(g, hg) - 2; y < hg; y++) k.Set(x, y, z, concD);
                for (int y = hg; y <= Math.Max(g, hg) + 2; y++) k.Set(x, y, z, 0);
            }
        for (int x = hx - hw; x <= hx + hw; x++)
            for (int z = hz - hd; z <= hz + hd; z++)
            {
                int ex = Math.Abs(x - hx), ez = Math.Abs(z - hd - hz + hd);
                bool wall = ex >= hw - 1 || Math.Abs(z - hz) >= hd - 1;
                for (int y = hg; y <= hg + hh; y++)
                {
                    bool floor = y == hg, roof = y >= hg + hh - 1;
                    bool door = z <= hz - hd + 1 && Math.Abs(x - hx) <= 4 && y <= hg + 12 && y > hg;
                    if (door) { k.Set(x, y, z, 0); continue; }
                    if (floor) { k.Set(x, y, z, conc); continue; }
                    if (roof)
                    {
                        // the roof is broken over the dome
                        double dd = Math.Sqrt((x - hx) * (x - hx) + (z - hz) * (z - hz) * 2.0);
                        if (dd < 10 + k.Hash01(x, z) * 6) continue;
                        k.Set(x, y, z, k.Hash01(x * 3, z * 7) < 0.15 ? concD : conc);
                        continue;
                    }
                    if (wall)
                    {
                        // slit windows high up between fins
                        bool slit = y > hg + hh - 9 && y < hg + hh - 3 && ((Math.Abs(z - hz) >= hd - 1 ? x : z) % 4 == 0);
                        k.Set(x, y, z, slit ? 0 : (k.Hash01(x + y * 13, z * 3) < 0.12 ? stain : conc));
                    }
                }
            }
        // the fins: one block proud of every façade, every third block, full height
        for (int x = hx - hw; x <= hx + hw; x += 3)
            for (int y = hg; y <= hg + hh + 1; y++)
            {
                if (Math.Abs(x - hx) > 5 || y > hg + 13) k.Set(x, y, hz - hd - 1, conc);
                k.Set(x, y, hz + hd + 1, conc);
            }
        for (int z = hz - hd; z <= hz + hd; z += 3)
            for (int y = hg; y <= hg + hh + 1; y++) { k.Set(hx - hw - 1, y, z, conc); k.Set(hx + hw + 1, y, z, conc); }
        // the containment dome inside, its crown burst
        for (int x = hx - 11; x <= hx + 11; x++)
            for (int z = hz - 11; z <= hz + 11; z++)
                for (int y = hg + 1; y <= hg + 12; y++)
                {
                    double d = Math.Sqrt((x - hx) * (x - hx) + (z - hz) * (z - hz) + (y - hg - 1) * (y - hg - 1) * 1.4);
                    if (d < 9.5 || d > 11) continue;
                    if (y > hg + 8 && k.Hash01(x * 5, z * 3 + y) < 0.6) continue;
                    k.Set(x, y, z, k.R() < 0.3 ? steel : conc);
                }

        // 3. Underground: the ramp, the warp chamber, coolant tunnels.
        int fy = hg - 30;                         // chamber floor
        int ux0 = hx - 36, ux1 = hx + 36, uz0 = hz - 26, uz1 = hz + 26, uh = 18;
        // shell first, then hollow it
        for (int x = ux0 - 1; x <= ux1 + 1; x++)
            for (int z = uz0 - 1; z <= uz1 + 1; z++)
                for (int y = fy - 1; y <= fy + uh + 1; y++)
                {
                    bool shell = x == ux0 - 1 || x == ux1 + 1 || z == uz0 - 1 || z == uz1 + 1 || y == fy - 1 || y == fy + uh + 1;
                    bool pillar = ((x - ux0) % 12 < 3) && ((z - uz0) % 12 < 3) && (x - hx - 14) * (x - hx - 14) + (z - hz) * (z - hz) > 17 * 17
                        && !(Math.Abs(z - (hz - hd + 5)) <= 4 && x < hx + 8);   // clear round the rig and the ramp
                    if (shell || pillar) k.Set(x, y, z, shell && y == fy - 1 ? (k.Hash01(x, z) < 0.1 ? concD : conc) : conc);
                    else k.Set(x, y, z, 0);
                }
        // the ramp: from the hall floor down the west side to the chamber
        for (int i = 0; i <= 30 * 3; i++)
        {
            int rx = hx - 16 + i / 3, rz = hz - hd + 5;          // inside the door, runs east, one down every 3
            int ry = hg - i / 3;
            for (int w = -3; w <= 3; w++)
            {
                k.Set(rx - 10, ry - 1, rz + w, conc);
                for (int y = ry; y <= ry + 5; y++) k.Set(rx - 10, y, rz + w, 0);
                if (Math.Abs(w) == 3 && ry < hg - 2) for (int y = ry; y <= ry + 5; y++) k.Set(rx - 10, y, rz + w + Math.Sign(w), conc);
            }
            if (ry <= fy) break;
        }
        // the warp rig: an upright ring on the x axis, lit along its rim
        {
            int wx = hx + 14, wz = hz, wy = fy + 9;
            for (double a = 0; a < Math.PI * 2; a += 0.012)
            {
                double cy0 = Math.Sin(a), cz0 = Math.Cos(a);
                for (double r = 7; r <= 9.2; r += 0.5)
                    for (int dx = -1; dx <= 1; dx++)
                        k.Set(wx + dx, (int)Math.Round(wy + cy0 * r), (int)Math.Round(wz + cz0 * r), r > 8.6 || k.R() < 0.6 ? k.Rust() : (steel != 0 ? steel : k.Rust()));
                if (glowB != 0 && ((int)(a * 100)) % 37 == 0)
                    k.Glow(wx, (int)Math.Round(wy + cy0 * 6.5), (int)Math.Round(wz + cz0 * 6.5), glowB);
            }
            // its feet and the scorched floor
            k.Box(wx - 2, fy, wz - 4, wx + 2, wy - 8, wz - 3, steel != 0 ? steel : k.Rust());
            k.Box(wx - 2, fy, wz + 3, wx + 2, wy - 8, wz + 4, steel != 0 ? steel : k.Rust());
            for (int x = wx - 14; x <= wx + 14; x++)
                for (int z = wz - 14; z <= wz + 14; z++)
                {
                    double d = Math.Sqrt((x - wx) * (x - wx) + (z - wz) * (z - wz));
                    if (d > 14 - k.Hash01(x, z) * 4) continue;
                    k.Set(x, fy - 1, z, d < 7 ? (drock != 0 ? drock : stain) : (devSoil != 0 ? devSoil : stain));
                }
            // cable runs from the rig across the floor to the walls
            for (int c = 0; c < 6; c++)
            {
                int ex = c < 3 ? ux0 : ux1, ez = wz - 12 + c % 3 * 12;
                k.Tube(wx, fy + 1, wz, ex, fy + 1, ez, 1.0, 1, rust, false);
            }
            k.Job.StructChests.Add((new BlockPos(wx - 6, fy, wz + 10, k.Job.Dim), "normal-aged", RollLoot(k.Rand, MachineLoot, 5 + k.RI(0, 2)), "warp chamber"));
        }
        // coolant tunnels out under each tower
        foreach (var (tx, tz, _) in towers)
        {
            double y = fy + 3;
            k.Tube(hx, y, hz, tx, y, tz, 3.5, 1, () => conc, true);
        }

        // 4. Transformer yard east of the hall.
        int yx = hx + hw + 14, yz = hz;
        int yg = Median(k, yx, yz, 10);
        for (int x = yx - 10; x <= yx + 10; x++)
            for (int z = yz - 12; z <= yz + 12; z++)
            {
                k.Set(x, yg, z, k.Hash01(x * 2, z * 9) < 0.2 ? concD : conc);
                for (int y = yg + 1; y <= yg + 3; y++) k.Set(x, y, z, 0);
                bool edge = Math.Abs(x - yx) == 10 || Math.Abs(z - yz) == 12;
                if (edge && k.Hash01(x * 7, z) > 0.25) { int f = Math.Abs(x - yx) == 10 ? fenceNS : fenceEW; if (f != 0) k.Set(x, yg + 1, z, f); }
            }
        for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                int bx = yx + i * 6, bz = yz + j * 7;
                k.Box(bx - 1, yg + 1, bz - 2, bx + 1, yg + 4, bz + 2, k.Rust());
                for (int f = -2; f <= 2; f += 2) k.Set(bx + 2, yg + 2, bz + f, steel != 0 ? steel : k.Rust());
                k.Line(bx, yg + 5, bz, bx, yg + 8, bz, () => steel != 0 ? steel : k.Rust());
            }
        k.Job.StructChests.Add((new BlockPos(hx - hw + 4, hg + 1, hz + hd - 3, k.Job.Dim), "normal-aged", new List<string>(RollLoot(k.Rand, MachineLoot, 4 + k.RI(0, 2))) { "randomlore:rfpool-jonas" }, "reactor hall"));
    }

    // A hyperboloid cooling tower: base radius rb at the ground, waist rw at
    // 72% of the height, a ring of open legs at the foot, concrete with
    // darker stain bands, cracked. collapsed: the upper shell is gone above
    // a jagged line and lies heaped round the base.
    private void CoolingTower(MegaKit k, int tx, int tz, int tg, int H, double rb, double rw, int conc, int concD, int stain, int rubble, bool collapsed)
    {
        double yw = H * 0.72;
        double c = yw / Math.Sqrt((rb / rw) * (rb / rw) - 1);
        double Rad(double y) => rw * Math.Sqrt(1 + ((y - yw) / c) * ((y - yw) / c));
        double phase = k.R() * Math.PI * 2;
        int rmax = (int)Math.Ceiling(Math.Max(rb, Rad(H))) + 2;
        for (int x = tx - rmax; x <= tx + rmax; x++)
            for (int z = tz - rmax; z <= tz + rmax; z++)
            {
                double d = Math.Sqrt((x - tx) * (x - tx) + (z - tz) * (z - tz));
                double ang = Math.Atan2(z - tz, x - tx);
                double breakY = collapsed ? H * (0.42 + 0.12 * Math.Sin(ang * 3 + phase) + 0.06 * Math.Sin(ang * 7)) : 1e9;
                int g = k.Ground(x, z);
                // a level apron of concrete under the tower, a cooling pond inside
                if (d <= rb + 2)
                {
                    for (int y = Math.Min(g, tg) - 1; y < tg; y++) k.Set(x, y, z, concD);
                    k.Set(x, tg, z, d < rb - 1.5 ? concD : conc);
                    for (int y = tg + 1; y <= Math.Max(g, tg) + 1; y++) k.Set(x, y, z, 0);
                    if (d < rb - 2) k.SetFluid(x, tg, z, k.Job.WaterId);
                }
                for (int y = 1; y <= H; y++)
                {
                    double r = Rad(y), th = y < 8 ? 2.4 : 1.7;
                    if (d < r - th || d > r + 0.5) continue;
                    if (y > breakY) continue;
                    if (y <= 4 && Math.Cos(ang * 18) > -0.3) continue;            // the open legs
                    if (k.Hash01(x * 7 + y * 13, z * 3 - y) < 0.012 + (collapsed ? 0.03 : 0)) continue;   // cracks
                    int band = (y + (int)(Math.Sin(ang * 5) * 2)) % 11;
                    k.Set(x, tg + y, z, band == 0 || (y < 6 && k.R() < 0.3) ? stain : (k.Hash01(x + y, z * 5) < 0.1 ? concD : conc));
                }
            }
        if (!collapsed) return;
        // the fallen shell: rubble heaped round the base, thickest downwind
        double dir = phase;
        for (int i = 0; i < 900; i++)
        {
            double a = dir + (k.R() - 0.5) * 2.4, r = rb * (0.4 + k.R() * 1.4);
            int x = tx + (int)Math.Round(Math.Cos(a) * r), z = tz + (int)Math.Round(Math.Sin(a) * r);
            int h = 1 + (int)(k.R() * 4 * (1 - Math.Abs(r - rb) / (rb * 1.2)));
            int g = Math.Max(k.Ground(x, z), tg);
            for (int y = g + 1; y <= g + h; y++) k.Set(x, y, z, k.R() < 0.6 ? rubble : conc);
        }
    }
}
