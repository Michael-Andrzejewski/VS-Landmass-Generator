using System;
using System.Collections.Generic;
using System.Linq;

namespace LandmassGenerator;

// ── THE GREAT MACHINE: THE WARRENS (0.66.0) ─────────────────────────────
// Jonas had metal and labour without end: eidolons and locusts built
// themselves in his laboratories and dug wherever the Great Work needed
// them. What they dug ties the works together and to the world above:
//
// - a maze of service tunnels on seven levels, from just over the mantle to
//   just under the town: masonry halls, riveted iron passages, timbered
//   mine drifts and small walkable pipes between chambers on a grid 24
//   apart. A random spanning tree with a third of the other links added
//   back, so it branches, dead-ends and loops. Ladders in the chamber
//   corners and spiral stair towers between the grid lines join the levels;
//   some chambers are furnished rooms, a few are locust fabrication bays;
// - tie-ins: tunnels driven straight into every dry work and many pipes;
// - ways down from almost every building on the surface: a ladder shaft or,
//   in a big building, a spiral stair through the floor to the nearest
//   level below;
// - huge pipes jutting out of the chasm walls (some at the floor, to walk
//   into), out of the abyss walls and out of the crater's slope, each run
//   back to the maze.
//
// The tunnels are spaces like the rest (GreatMachineWorks.cs): shells, then
// hollows, then fittings, all under the safe ceiling. Only the ways in are
// built with it lifted, and each of those is checked for cover first. A part
// of the maze none of those reach is left out. The Cistern and the Foundry
// hold water and lava, so the maze keeps well away from both.
public partial class LandmassGeneratorModSystem
{
    private sealed class GmNode
    {
        public int I, J, L, X, Y, Z, Hw = 2, H = 5, Kind, Style;    // Kind: 0 junction, 1 furnished room, 2 fabrication bay
        public bool Anchor;
        public readonly List<(int Dx, int Dz)> Doors = new();         // the ways tunnels enter it
    }

    private sealed class GmLink
    {
        public GmNode A, B;
        public char Kind;          // 'h' a tunnel along a grid line, 'l' a ladder
        public int Style;
    }

    private void GmWarrens(GmCtx g)
    {
        var k = g.K;
        int cx = g.Cx, cz = g.Cz;
        const int S = 24;
        int[] levels = { 12, 30, 50, 70, 90, 110, 130 };
        double abyssKeep = g.AbyssR * 1.45 + 6;
        var wet = g.Works.Where(w => w.Name == "Cistern" || w.Name == "Foundry").ToList();
        var dry = g.Works.Where(w => !wet.Contains(w)).ToList();
        double Hyp(double a, double b) => Math.Sqrt(a * a + b * b);
        IEnumerable<int> Steps(int a, int b, int s) { for (int v = a; ; v += s) { if (v >= b) { yield return b; yield break; } yield return v; } }

        // Can a box of hollow (with a shell round it) go here: under the safe
        // ceiling by 2, inside the walls, out of the abyss's column above
        // y 24, clear of every work (the wet ones by 12) and of every hollow
        // already planned (pipes, stairwells, rooms)?
        bool Clear(int x0, int y0, int z0, int x1, int y1, int z1, GmWork except = null)
        {
            if (y0 < 8) return false;
            foreach (int x in Steps(x0 - 2, x1 + 2, 3))
                foreach (int z in Steps(z0 - 2, z1 + 2, 3))
                {
                    if (y1 + 2 > g.SafeTop(x, z)) return false;
                    double r = Hyp(x - cx, z - cz);
                    if (r > g.WallR - 6) return false;
                    if (y1 > 24 && r < abyssKeep) return false;
                }
            foreach (var w in g.Works) if (w != except && w.Overlaps(x0, z0, x1, z1, y0 - 1, y1 + 1, 4)) return false;
            foreach (var w in wet) if (w.Overlaps(x0, z0, x1, z1, y0 - 1, y1 + 1, 12)) return false;
            foreach (int x in Steps(x0 - 1, x1 + 1, 2))
                foreach (int z in Steps(z0 - 1, z1 + 1, 2))
                    foreach (int y in Steps(y0 - 1, y1 + 1, 2))
                        if (g.HollowAt(x, y, z, h => h.Kind != 'w' && (except == null || h.Work != except.Name)) != null) return false;
            return true;
        }

        // ── the grid of chambers ─────────────────────────────────────────
        var nodes = new List<GmNode>();
        var at = new Dictionary<(int, int, int), GmNode>();
        int span = g.WallR / S;
        for (int li = 0; li < levels.Length; li++)
            for (int i = -span; i <= span; i++)
                for (int j = -span; j <= span; j++)
                {
                    int x = cx + i * S, z = cz + j * S;
                    if (Hyp(x - cx, z - cz) > g.WallR - 14) continue;
                    int y = levels[li] + g.RI(-2, 2);
                    double r0 = g.R();
                    int kind = r0 < 0.03 ? 2 : r0 < 0.17 ? 1 : 0;
                    while (true)
                    {
                        int hw = kind == 2 ? 7 : kind == 1 ? 4 : 2, H = kind == 2 ? 8 : 5;
                        if (Clear(x - hw, y, z - hw, x + hw, y + H - 1, z + hw))
                        {
                            var n = new GmNode { I = i, J = j, L = li, X = x, Y = y, Z = z, Hw = hw, H = H, Kind = kind };
                            n.Style = GmLevelStyle(g, li);
                            nodes.Add(n);
                            at[(i, j, li)] = n;
                            break;
                        }
                        if (kind == 0) break;
                        kind = 0;
                    }
                }

        // ── the links: tunnels along the grid lines, ladders between levels
        var links = new List<GmLink>();
        foreach (var n in nodes)
        {
            foreach (var (di, dj) in new[] { (1, 0), (0, 1) })
            {
                if (!at.TryGetValue((n.I + di, n.J + dj, n.L), out var m)) continue;
                bool alongX = di == 1;
                int a0 = (alongX ? n.X : n.Z) + n.Hw, a1 = (alongX ? m.X : m.Z) - m.Hw;
                int q = alongX ? n.Z : n.X, ylo = Math.Min(n.Y, m.Y), yhi = Math.Max(n.Y, m.Y) + 3;
                if (a1 - a0 < 2) continue;
                bool ok = alongX ? Clear(a0, ylo, q - 1, a1, yhi, q + 1) : Clear(q - 1, ylo, a0, q + 1, yhi, a1);
                if (ok) links.Add(new GmLink { A = n, B = m, Kind = 'h', Style = g.R() < 0.6 ? n.Style : GmLevelStyle(g, n.L) });
            }
            if (n.Kind == 0 && at.TryGetValue((n.I, n.J, n.L + 1), out var up) && up.Kind == 0
                && Clear(n.X - 2, n.Y + n.H, n.Z - 2, n.X - 2, up.Y - 2, n.Z - 2))
                links.Add(new GmLink { A = n, B = up, Kind = 'l' });
        }

        // a random spanning tree, then a third of the other tunnels and a
        // fifth of the other ladders back in, so it loops
        var parent = new Dictionary<GmNode, GmNode>();
        GmNode Find(GmNode n) { while (parent.TryGetValue(n, out var p) && p != n) { parent[n] = parent.TryGetValue(p, out var pp) ? pp : p; n = p; } return n; }
        void Union(GmNode a, GmNode b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra] = rb; }
        foreach (var n in nodes) parent[n] = n;
        var kept = new List<GmLink>();
        foreach (var l in links.Select(l => (l, w: g.R())).OrderBy(t => t.w).Select(t => t.l))
        {
            if (Find(l.A) != Find(l.B)) { Union(l.A, l.B); kept.Add(l); }
            else if (g.R() < (l.Kind == 'h' ? 0.33 : 0.2)) kept.Add(l);
        }
        var tunnelAt = new Dictionary<(int, int, int, bool), GmLink>();   // (i, j, level, along x) of its lower end
        foreach (var l in kept.Where(l => l.Kind == 'h'))
            tunnelAt[(l.A.I, l.A.J, l.A.L, l.B.I != l.A.I)] = l;

        // the floor of a tunnel at a point along it (the carve's floor)
        int TunnelFloor(GmLink l, int a)
        {
            bool alongX = l.B.I != l.A.I;
            int aA = alongX ? l.A.X : l.A.Z, aB = alongX ? l.B.X : l.B.Z;
            double t = Math.Clamp((a - aA) / (double)(aB - aA), 0, 1);
            return (int)Math.Floor(l.A.Y + (l.B.Y - l.A.Y) * t);
        }

        // ── spiral stair towers between the grid lines ───────────────────
        // A tower at a cell's middle climbs two to four levels; on each it
        // has one doorway, a short passage to a tunnel along one of the
        // cell's four sides, a different side from the level below.
        var towers = new List<(int X, int Z, List<(double A, int Y)> Entries, List<(int X0, int Y, int Z0, int X1, int Z1)> Stubs, int Style, GmNode Via)>();
        for (int i = -span; i < span; i++)
            for (int j = -span; j < span; j++)
            {
                if (g.R() > 0.22) continue;
                int tx = cx + i * S + S / 2, tz = cz + j * S + S / 2;
                if (Hyp(tx - cx, tz - cz) > g.WallR - 20) continue;
                // per level, the tunnels round this cell: (link, side dx, dz)
                var sides = new List<(GmLink L, int Dx, int Dz)>[levels.Length];
                for (int li = 0; li < levels.Length; li++)
                {
                    sides[li] = new();
                    if (tunnelAt.TryGetValue((i, j, li, true), out var e1)) sides[li].Add((e1, 0, -1));
                    if (tunnelAt.TryGetValue((i, j + 1, li, true), out var e2)) sides[li].Add((e2, 0, 1));
                    if (tunnelAt.TryGetValue((i, j, li, false), out var e3)) sides[li].Add((e3, -1, 0));
                    if (tunnelAt.TryGetValue((i + 1, j, li, false), out var e4)) sides[li].Add((e4, 1, 0));
                }
                int start = g.RI(0, levels.Length - 2);
                while (start < levels.Length - 1 && sides[start].Count == 0) start++;
                if (start >= levels.Length - 1) continue;
                int end = start;
                while (end + 1 < levels.Length && sides[end + 1].Count > 0 && end - start < 3) end++;
                if (end == start) continue;
                var entries = new List<(double A, int Y)>();
                var stubs = new List<(int X0, int Y, int Z0, int X1, int Z1)>();
                var used = new List<GmLink>();
                (int Dx, int Dz) last = (0, 0);
                for (int li = start; li <= end; li++)
                {
                    var choices = sides[li].Where(s => (s.Dx, s.Dz) != last).ToList();
                    if (choices.Count == 0) choices = sides[li];
                    var c = choices[g.RI(0, choices.Count - 1)];
                    last = (c.Dx, c.Dz);
                    int fy = TunnelFloor(c.L, c.Dx == 0 ? tx : tz);
                    entries.Add((Math.Atan2(c.Dz, c.Dx), fy));
                    // from the tower's wall to the tunnel's axis (a tunnel
                    // along x lies at its nodes' z, one along z at their x)
                    stubs.Add((tx + c.Dx * 3, fy, tz + c.Dz * 3, c.Dx != 0 ? c.L.A.X : tx, c.Dz != 0 ? c.L.A.Z : tz));
                    used.Add(c.L);
                }
                int ylo = entries.Min(e => e.Y), yhi = entries.Max(e => e.Y);
                if (!Clear(tx - 4, ylo, tz - 4, tx + 4, yhi + 6, tz + 4)) continue;
                towers.Add((tx, tz, entries, stubs, GmLevelStyle(g, start), used[0].A));
                for (int u = 1; u < used.Count; u++) Union(used[0].A, used[u].A);
            }

        // ── tie-ins: tunnels driven into the dry works and the pipes ─────
        var ties = new List<(GmNode N, int Dx, int Dz, int Len, string What)>();
        int tieWorks = 0, tiePipes = 0;
        bool TryTie(GmNode n, int dx, int dz, int maxLen, Func<GmHollow, bool> target, GmWork into, string what)
        {
            int y = n.Y;
            for (int s = n.Hw + 1; s <= maxLen; s++)
            {
                int px = n.X + dx * s, pz = n.Z + dz * s;
                bool hit = true;
                for (int yy = y; yy <= y + 2 && hit; yy++) if (g.HollowAt(px, yy, pz, target) == null) hit = false;
                if (hit)
                {
                    ties.Add((n, dx, dz, s + 1, what));
                    n.Anchor = true;
                    n.Doors.Add((dx, dz));
                    return true;
                }
                // short of it: nothing else in the way, and covered outside the work
                if (g.HollowAt(px, y + 1, pz, h => !target(h)) != null) return false;
                bool inBox = into != null && into.Overlaps(px, pz, px, pz, y, y + 3, 4);
                if (!inBox)
                {
                    if (y + 5 > g.SafeTop(px, pz) || Hyp(px - cx, pz - cz) > g.WallR - 6 || (y + 3 > 24 && Hyp(px - cx, pz - cz) < abyssKeep)) return false;
                    foreach (var w in g.Works) if (w != into && w.Overlaps(px - 1, pz - 1, px + 1, pz + 1, y - 1, y + 4, 2)) return false;
                    foreach (var w in wet) if (w.Overlaps(px - 1, pz - 1, px + 1, pz + 1, y - 1, y + 4, 10)) return false;
                }
            }
            return false;
        }
        foreach (var w in dry)
        {
            int want = w.Name == "Axle Well" ? 6 : 3, got = 0;
            var tried = new List<(int X, int Z)>();
            foreach (var n in nodes.OrderBy(_ => g.R()))
            {
                if (got >= want) break;
                if (n.Y < w.Floor - 2 || n.Y > w.Floor + 8) continue;
                int dx = 0, dz = 0;
                if (n.X >= w.X0 + 3 && n.X <= w.X1 - 3) dz = n.Z < w.Z0 ? 1 : n.Z > w.Z1 ? -1 : 0;
                else if (n.Z >= w.Z0 + 3 && n.Z <= w.Z1 - 3) dx = n.X < w.X0 ? 1 : n.X > w.X1 ? -1 : 0;
                if (dx == 0 && dz == 0) continue;
                int gap = dx != 0 ? Math.Min(Math.Abs(n.X - w.X0), Math.Abs(n.X - w.X1)) : Math.Min(Math.Abs(n.Z - w.Z0), Math.Abs(n.Z - w.Z1));
                if (gap > 40 || tried.Any(t => Hyp(t.X - n.X, t.Z - n.Z) < 20)) continue;
                if (TryTie(n, dx, dz, gap + 40, h => h.Work == w.Name, w, w.Name)) { got++; tieWorks++; tried.Add((n.X, n.Z)); }
            }
        }
        foreach (var run in g.PipeRuns.ToList())
            for (int s = 0; s + 1 < run.Count; s++)
            {
                var a = run[s]; var b = run[s + 1];
                bool legX = Math.Abs(a.Z - b.Z) < 0.5 && Math.Abs(a.X - b.X) > 16, legZ = Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Z - b.Z) > 16;
                if (!legX && !legZ || g.R() < 0.4) continue;
                var cand = nodes.Where(n => legX
                        ? n.X > Math.Min(a.X, b.X) + 4 && n.X < Math.Max(a.X, b.X) - 4 && Math.Abs(n.Z - a.Z) < 40
                        : n.Z > Math.Min(a.Z, b.Z) + 4 && n.Z < Math.Max(a.Z, b.Z) - 4 && Math.Abs(n.X - a.X) < 40)
                    .Where(n =>
                    {
                        double t = legX ? (n.X - a.X) / (b.X - a.X) : (n.Z - a.Z) / (b.Z - a.Z);
                        double fl = a.Y + (b.Y - a.Y) * t;
                        return Math.Abs(n.Y - fl) <= 1.5;
                    }).OrderBy(_ => g.R()).ToList();
                foreach (var n in cand)
                {
                    int dx = legZ ? Math.Sign(a.X - n.X) : 0, dz = legX ? Math.Sign(a.Z - n.Z) : 0;
                    if (dx == 0 && dz == 0) continue;
                    if (TryTie(n, dx, dz, 44, h => h.Kind == 'p' && h.Work == null, null, "pipe")) { tiePipes++; break; }
                }
            }

        // Only the parts of the maze a tie-in joins to the works take ways in
        // from above, so every way down and every pipe mouth leads to the works.
        var joined = new HashSet<GmNode>(ties.Select(t => Find(t.N)));
        bool Joined(GmNode n) => joined.Contains(Find(n));

        // ── ways down from the buildings, and standpipes in the crater ───
        // A standpipe: a huge broken pipe standing out of the crater floor,
        // a doorway at its foot, a spiral stair inside down to the maze.
        var downs = new List<(int SX, int SZ, int F, int Y, int Kind, List<(int X, int Z)> Path, GmNode N, int Dx0, int Dz0)>();   // Kind 0 ladder, 1 spiral, 2 standpipe
        int bTotal = 0, bMissed = 0, nStand = 0;
        var shafts = g.Buildings.Where(b => b.Kind != 2)
            .Select(b => (b.SX, b.SZ, b.F, Kind: b.Kind == 0 && b.X1 - b.X0 - 1 >= 9 && b.Z1 - b.Z0 - 1 >= 9 ? 1 : 0, Building: true)).ToList();
        // the town's empty lots first, then anywhere open in the crater
        var spots = g.EmptyLots.OrderBy(_ => g.R()).ToList();
        for (int t = 0; t < 400; t++)
        {
            double a = g.R() * Math.PI * 2, r = g.AbyssR * 1.5 + g.R() * (g.RimR - 14 - g.AbyssR * 1.5);
            spots.Add((cx + (int)Math.Round(Math.Cos(a) * r), cz + (int)Math.Round(Math.Sin(a) * r)));
        }
        int wantStand = 0;
        foreach (var (x, z) in spots)
        {
            if (wantStand >= 9) break;
            if (x >= cx - 14 && x <= cx + 13 && z >= cz - 4) continue;                                  // the walkway
            bool flat = true;
            int g0 = g.Floor(x, z);
            for (int ox = -5; ox <= 5 && flat; ox += 5) for (int oz = -5; oz <= 5 && flat; oz += 5)
                if (Math.Abs(g.Floor(x + ox, z + oz) - g0) > 3 || g.Floor(x + ox, z + oz) < g.K.Ground(x + ox, z + oz) - 1) flat = false;
            if (!flat || g.Keep.Any(c => Hyp(x - c.X, z - c.Z) < c.R + 6) || g.Buildings.Any(b => x >= b.X0 - 6 && x <= b.X1 + 6 && z >= b.Z0 - 6 && z <= b.Z1 + 6)) continue;
            if (shafts.Any(s => !s.Building && Hyp(s.SX - x, s.SZ - z) < 40)) continue;
            shafts.Add((x, z, g0 + 1, 2, false));
            wantStand++;
        }
        foreach (var b in shafts)
        {
            if (b.Building) bTotal++;
            bool spiral = b.Kind >= 1;
            var cand = nodes.Where(n => n.Y <= b.F - 14 && Hyp(n.X - b.SX, n.Z - b.SZ) <= 36 && Joined(n))
                .OrderByDescending(n => n.L).ThenBy(n => Hyp(n.X - b.SX, n.Z - b.SZ)).ToList();
            bool done = false;
            foreach (var n in cand)
            {
                // the shaft's column clear of the works and of other hollows
                int sr = b.Kind == 2 ? 6 : spiral ? 5 : 1;
                bool col = true;
                foreach (var w in g.Works) if (w.Overlaps(b.SX - sr, b.SZ - sr, b.SX + sr, b.SZ + sr, n.Y - 2, b.F, 2)) col = false;
                for (int y = n.Y; y < b.F - 1 && col; y += 2)
                    foreach (int x in Steps(b.SX - sr, b.SX + sr, 2))
                        foreach (int z in Steps(b.SZ - sr, b.SZ + sr, 2))
                            if (g.HollowAt(x, y, z, h => h.Kind != 'w') != null) col = false;
                if (!col) continue;
                // then a tunnel from the shaft's foot to the chamber, x first or z first
                foreach (bool xFirst in new[] { g.R() < 0.5, true, false }.Distinct())
                {
                    var corner = xFirst ? (n.X, b.SZ) : (b.SX, n.Z);
                    var path = new List<(int X, int Z)> { (b.SX, b.SZ), corner, (n.X, n.Z) };
                    bool ok = true;
                    for (int p = 0; p + 1 < path.Count && ok; p++)
                    {
                        var (ax, az) = path[p]; var (bx, bz) = path[p + 1];
                        if (ax == bx && az == bz) continue;
                        // the leg up to the chamber's edge, past the shaft's own space
                        int pad = p == 0 ? (b.Kind == 2 ? 6 : spiral ? 4 : 1) : 0;
                        int lx0 = Math.Min(ax, bx), lx1 = Math.Max(ax, bx), lz0 = Math.Min(az, bz), lz1 = Math.Max(az, bz);
                        if (ax != bx) { if (ax < bx) lx0 += pad; else lx1 -= pad; } else { if (az < bz) lz0 += pad; else lz1 -= pad; }
                        if (p + 1 == path.Count - 1)
                        {
                            if (ax != bx) { if (ax < bx) lx1 -= n.Hw + 1; else lx0 += n.Hw + 1; } else { if (az < bz) lz1 -= n.Hw + 1; else lz0 += n.Hw + 1; }
                        }
                        if (lx1 < lx0 || lz1 < lz0) continue;
                        if (ax != bx) ok = Clear(lx0, n.Y, az - 1, lx1, n.Y + 3, az + 1);
                        else ok = Clear(ax - 1, n.Y, lz0, ax + 1, n.Y + 3, lz1);
                    }
                    if (!ok) continue;
                    var first = path[1] == path[0] ? path[2] : path[1];
                    int dx0 = Math.Sign(first.X - b.SX), dz0 = Math.Sign(first.Z - b.SZ);
                    downs.Add((b.SX, b.SZ, b.F, n.Y, b.Kind, path, n, dx0, dz0));
                    if (b.Kind == 2) nStand++;
                    n.Anchor = true;
                    var last = path[1] == path[2] ? path[0] : path[1];
                    n.Doors.Add((Math.Sign(last.X - n.X), Math.Sign(last.Z - n.Z)));
                    done = true;
                    break;
                }
                if (done) break;
            }
            if (!done && b.Building) bMissed++;
        }

        // ── huge pipes out into the chasms, the abyss and the crater ─────
        // Each mouth is a point in the open air (or at the floor) beside a
        // wall, a direction into the rock, and a floor height; the pipe runs
        // from a chamber of the maze to a point 14 inside the wall, then out
        // through the wall to the mouth, which is open.
        var mouths = new List<(List<(double X, double Y, double Z)> Pts, string Where, GmNode N, double R)>();
        int nChasm = 0, nAbyss = 0, nCrater = 0;
        // cover over a run: the ground stays above the pipe's top (and the
        // works, the wet works and other hollows stay clear of it)
        bool Covered(double ax, double az, double bx, double bz, int floorY, double r)
        {
            double len = Hyp(bx - ax, bz - az);
            for (double s = 0; s <= len; s += 1.5)
            {
                double t = len < 0.1 ? 0 : s / len;
                int x = (int)Math.Round(ax + (bx - ax) * t), z = (int)Math.Round(az + (bz - az) * t);
                int top = floorY + 2 + (int)Math.Ceiling(r + 2);
                for (int ox = -1; ox <= 1; ox++) for (int oz = -1; oz <= 1; oz++) if (g.Floor(x + ox * 3, z + oz * 3) < top + 2) return false;
                foreach (var w in g.Works) if (w.Overlaps(x - 6, z - 6, x + 6, z + 6, floorY - 6, top + 2, 0)) return false;
                if (g.HollowAt(x, floorY + 2, z, h => h.Kind != 'w') != null) return false;
            }
            return true;
        }
        // a chamber to run back to: the pipe's way to it in legs along x and
        // z, rising no more than one in three
        bool RunBack((double X, double Z) inner, int floorY, double r, out List<(double X, double Y, double Z)> legs, out GmNode node)
        {
            legs = null; node = null;
            foreach (var n in nodes.Where(Joined).OrderBy(n => Hyp(n.X - inner.X, n.Z - inner.Z)).Take(14))
            {
                foreach (bool xFirst in new[] { true, false })
                {
                    var corner = xFirst ? (X: (double)n.X, Z: inner.Z) : (X: inner.X, Z: (double)n.Z);
                    double l1 = Hyp(corner.X - inner.X, corner.Z - inner.Z), l2 = Hyp(n.X - corner.X, n.Z - corner.Z), len = l1 + l2;
                    if (len < 8 || Math.Abs(n.Y - floorY) > len / 3) continue;
                    double yc = floorY + (n.Y - floorY) * (len < 0.1 ? 1 : l1 / len);
                    // the legs keep under the safe ceiling once away from the wall
                    bool ok = true;
                    foreach (var (ax, az, ay, bx, bz, by) in new[] { (inner.X, inner.Z, (double)floorY, corner.X, corner.Z, yc), (corner.X, corner.Z, yc, (double)n.X, (double)n.Z, (double)n.Y) })
                    {
                        double ll = Hyp(bx - ax, bz - az);
                        for (double s = 0; s <= ll && ok; s += 2)
                        {
                            double t = ll < 0.1 ? 0 : s / ll;
                            int x = (int)Math.Round(ax + (bx - ax) * t), z = (int)Math.Round(az + (bz - az) * t), fy = (int)Math.Round(ay + (by - ay) * t);
                            int top = fy + 2 + (int)Math.Ceiling(r + 2);
                            if (top > g.SafeTop(x, z) + 2) ok = false;
                            foreach (var w in g.Works) if (w.Overlaps(x - 6, z - 6, x + 6, z + 6, fy - 6, top + 2, 0)) ok = false;
                            foreach (var w in wet) if (w.Overlaps(x - 6, z - 6, x + 6, z + 6, fy - 6, top + 2, 10)) ok = false;
                            if (g.HollowAt(x, fy + 2, z, h => h.Kind != 'w') != null) ok = false;
                        }
                    }
                    if (!ok) continue;
                    legs = new List<(double X, double Y, double Z)> { (n.X, n.Y, n.Z), (corner.X, yc, corner.Z) };
                    node = n;
                    return true;
                }
            }
            return false;
        }
        var why = new Dictionary<string, int>();
        void No(string reason) => why[reason] = why.TryGetValue(reason, out int c) ? c + 1 : 1;
        bool Mouth(double mx, double mz, double ix, double iz, int floorY, double r, string where)
        {
            // cover over the part in the rock (the last stretch before the
            // wall is the wall's own slope, so it is left to the shell)
            if (!Covered(ix, iz, ix + (mx - ix) * 0.45, iz + (mz - iz) * 0.45, floorY, r)) { No(where + ": no cover"); return false; }
            if (!RunBack((ix, iz), floorY, r, out var legs, out var n)) { No(where + ": no way back"); return false; }
            legs.Add((ix, floorY, iz));
            legs.Add((mx, floorY, mz));
            mouths.Add((legs, where, n, r));
            n.Anchor = true;
            var c = legs[1];
            n.Doors.Add((Math.Sign(Math.Round(c.X) - n.X), Math.Sign(Math.Round(c.Z) - n.Z)));
            return true;
        }
        // the wall along a direction at a height: the first column whose
        // ground is above the pipe's top
        double WallAlong(double px, double pz, double nx, double nz, int floorY, double r, double t0, double t1)
        {
            for (double t = t0; t <= t1; t += 1)
                if (g.Floor((int)Math.Round(px + nx * t), (int)Math.Round(pz + nz * t)) >= floorY + 2 + r + 3) return t;
            return -1;
        }
        // the chasms: every 26 to 40 along each inside the walls, sides in
        // turn, a third of the mouths at the floor
        foreach (var line in g.Chasms)
        {
            int side = g.R() < 0.5 ? 1 : -1;
            for (double s = g.AbyssR * 1.7; s < g.WallR + 24; s += 26 + g.R() * 14)
            {
                int seg = -1;
                for (int i = 0; i + 1 < line.Count; i++) if (line[i].S <= s && line[i + 1].S >= s) { seg = i; break; }
                if (seg < 0) continue;
                var p0 = line[seg]; var p1 = line[seg + 1];
                double t = (s - p0.S) / Math.Max(1e-6, p1.S - p0.S), px = p0.X + (p1.X - p0.X) * t, pz = p0.Z + (p1.Z - p0.Z) * t;
                double dl = Hyp(p1.X - p0.X, p1.Z - p0.Z), ux = (p1.X - p0.X) / dl, uz = (p1.Z - p0.Z) / dl;
                side = -side;
                double nx = -uz * side, nz = ux * side;
                int floorC = g.Floor((int)Math.Round(px), (int)Math.Round(pz));
                bool walkIn = g.R() < 0.35;
                double r = walkIn ? 4.0 : 4.5;
                int fy = walkIn ? floorC + 1 : floorC + 10 + g.RI(0, Math.Max(0, g.G - 34 - floorC - 10));
                double tw = WallAlong(px, pz, nx, nz, fy, r, 0, 70);
                if (tw < 0) { No("chasm: no wall"); continue; }
                double jut = walkIn ? Math.Max(0, tw - 1) : Math.Min(g.RI(4, 9), tw - 1);
                double mx = px + nx * (tw - jut), mz = pz + nz * (tw - jut);
                double ix = px + nx * (tw + 14), iz = pz + nz * (tw + 14);
                if (Mouth(mx, mz, ix, iz, fy, r, walkIn ? "chasm floor" : "chasm wall")) nChasm++;
            }
        }
        // the abyss: eight round it, clear of the walkway and the chasms
        {
            var chasmBear = g.Chasms.Where(l => l.Count > 1).Select(l => Math.Atan2(l[1].Z - cz, l[1].X - cx)).ToList();
            int want = 8;
            var placedA = new List<double>();
            for (int i = 0; i < 80 && nAbyss < want; i++)
            {
                double a = g.R() * Math.PI * 2;
                double deg = (a * 180 / Math.PI + 360) % 360;
                if (Math.Abs(deg - 90) < 16 || chasmBear.Any(cb => Math.Abs(Math.IEEERemainder(a - cb, Math.PI * 2)) < 0.2)) continue;
                if (placedA.Any(p => Math.Abs(Math.IEEERemainder(a - p, Math.PI * 2)) < 0.5)) continue;
                double ca = Math.Cos(a), sa = Math.Sin(a);
                int fy = Math.Max(14, g.Sea - 70) + 20 + g.RI(0, 40);
                double rw = WallAlong(cx, cz, ca, sa, fy, 4.5, g.AbyssR * 0.5, g.AbyssR * 1.6);
                if (rw < 0) { No("abyss: no wall"); continue; }
                double jut = g.RI(3, 8);
                if (Mouth(cx + ca * (rw - jut), cz + sa * (rw - jut), cx + ca * (rw + 14), cz + sa * (rw + 14), fy, 4.5, "abyss")) { nAbyss++; placedA.Add(a); }
            }
        }
        // the crater: culverts out of its slope at the ground, from just
        // inside the collar (where the town thins out) inward
        {
            int want = 7;
            for (int i = 0; i < 36 && nCrater < want; i++)
            {
                double a = (i + g.R() * 0.7) * Math.PI * 2 / 36;
                double deg = (a * 180 / Math.PI + 360) % 360;
                if (Math.Abs(deg - 90) < 14) continue;
                double ca = Math.Cos(a), sa = Math.Sin(a);
                for (double rm = g.RimR - 4; rm >= g.RimR - 34; rm -= 2)
                {
                    int mxI = cx + (int)Math.Round(ca * rm), mzI = cz + (int)Math.Round(sa * rm);
                    int fy = g.Floor(mxI, mzI) + 1;
                    double rw = WallAlong(cx, cz, ca, sa, fy, 4.0, rm, rm + 30);
                    if (rw < 0) { No("crater: no slope"); continue; }
                    // nothing built on the mouth or on the slope it climbs
                    bool built = false;
                    for (double rr = rm - 3; rr <= rw && !built; rr += 2)
                    {
                        double px = cx + ca * rr, pz = cz + sa * rr;
                        built = g.Keep.Any(c => Hyp(px - c.X, pz - c.Z) < c.R + 2)
                            || g.Buildings.Any(b => px >= b.X0 - 2 && px <= b.X1 + 2 && pz >= b.Z0 - 2 && pz <= b.Z1 + 2);
                    }
                    if (built) { No("crater: built over"); continue; }
                    // the mouth where its floor meets the ground; from there
                    // it runs half buried up the slope and into the rim
                    if (Mouth(cx + ca * rm, cz + sa * rm, cx + ca * (rw + 14), cz + sa * (rw + 14), fy, 4.0, "crater")) nCrater++;
                    break;
                }
            }
        }

        // ── keep what can be reached ─────────────────────────────────────
        var anchoredRoots = new HashSet<GmNode>(nodes.Where(n => n.Anchor).Select(Find));
        bool Kept(GmNode n) => anchoredRoots.Contains(Find(n));
        int keptNodes = nodes.Count(Kept);

        // doors of the kept tunnels, for furnishing round them
        foreach (var l in kept.Where(l => l.Kind == 'h' && Kept(l.A)))
        {
            int dx = Math.Sign(l.B.X - l.A.X), dz = Math.Sign(l.B.Z - l.A.Z);
            l.A.Doors.Add((dx, dz));
            l.B.Doors.Add((-dx, -dz));
        }

        // ── build it ─────────────────────────────────────────────────────
        g.CurKind = 'w';
        int nRooms = 0, nBays = 0, nLadders = 0, nTunnels = 0, nDeadEnds = 0, probeI = 0;
        int hudX = sapi.WorldManager.MapSizeX / 2, hudZ = sapi.WorldManager.MapSizeZ / 2;
        foreach (var n in nodes.Where(Kept))
        {
            if (probeI++ % 4 == 0)
                sapi.Logger.Notification("[landmassgenerator] great machine chamber probe at HUD {0}, {1}, {2}", n.X - hudX, n.Y + 1, n.Z - hudZ);
            bool straight = n.Kind == 0 && n.Doors.Count == 2 && n.Doors[0].Dx == -n.Doors[1].Dx && n.Doors[0].Dz == -n.Doors[1].Dz
                && !kept.Any(l => l.Kind == 'l' && (l.A == n || l.B == n));
            if (straight && g.R() < 0.6)
            {
                // a straight run: just the tunnel through, a little wider
                var d = n.Doors[0];
                GmTunnel(g, n.X - d.Dx * 3, n.Y, n.Z - d.Dz * 3, n.X + d.Dx * 3, n.Y, n.Z + d.Dz * 3, 1, 4, 0, 0, n.Style);
                continue;
            }
            GmChamber(g, n);
            if (n.Kind == 1) nRooms++;
            if (n.Kind == 2) nBays++;
            if (n.Doors.Count == 1 && n.Kind == 0)
            {
                nDeadEnds++;
                if (g.R() < 0.5)
                {
                    var d = n.Doors[0];
                    g.Loot.Add((n.X - d.Dx * 2, n.Y, n.Z - d.Dz * 2, GmLootFor(g, "deadend"), Math.Atan2(d.Dz, d.Dx)));
                }
            }
        }
        foreach (var l in kept.Where(l => Kept(l.A)))
        {
            if (l.Kind == 'l')
            {
                GmLadder(g, l.A.X - 2, l.A.Z - 2, l.A.Y, l.B.Y - 1, "west", true);
                nLadders++;
                continue;
            }
            bool alongX = l.B.I != l.A.I;
            int a0 = (alongX ? l.A.X : l.A.Z) + l.A.Hw, a1 = (alongX ? l.B.X : l.B.Z) - l.B.Hw;
            int ya = TunnelFloor(l, a0), yb = TunnelFloor(l, a1);
            if (l.Style == 3)
            {
                // a small walkable pipe
                GmPipe(g, new List<(double X, double Y, double Z)> {
                    (alongX ? a0 - 1 : l.A.X, ya, alongX ? l.A.Z : a0 - 1), (alongX ? a1 + 1 : l.A.X, yb, alongX ? l.A.Z : a1 + 1) }, false, 2.5, 1.4);
            }
            else if (alongX) GmTunnel(g, a0, ya, l.A.Z, a1, yb, l.A.Z, 1, 4, 1, 1, l.Style);
            else GmTunnel(g, l.A.X, ya, a0, l.A.X, yb, a1, 1, 4, 1, 1, l.Style);
            nTunnels++;
        }
        int nTowers = 0;
        foreach (var t in towers)
        {
            if (!Kept(t.Via)) continue;
            GmShaft(g, t.X, t.Z, t.Entries, 3.5);
            foreach (var st in t.Stubs) GmTunnel(g, st.X0, st.Y, st.Z0, st.X1, st.Y, st.Z1, 1, 4, 0, 1, t.Style);
            nTowers++;
        }
        foreach (var tie in ties)
        {
            var n = tie.N;
            GmTunnel(g, n.X + tie.Dx * n.Hw, n.Y, n.Z + tie.Dz * n.Hw, n.X + tie.Dx * tie.Len, n.Y, n.Z + tie.Dz * tie.Len, 1, 4, 0, 0, n.Style);
        }
        foreach (var d in downs)
        {
            var n = d.N;
            int yb = n.Y, F = d.F;
            string kindName = d.Kind == 2 ? "standpipe" : d.Kind == 1 ? "spiral" : "ladder";
            int pr = d.Kind == 0 ? 0 : 2;
            sapi.Logger.Notification("[landmassgenerator] great machine way down ({0}) at HUD {1}, {2}, {3} probe {4}, {5}, {6} shaft {7}", kindName,
                d.SX - hudX, F, d.SZ - hudZ, d.SX + d.Dx0 * pr - hudX, yb + 2, d.SZ + d.Dz0 * pr - hudZ, yb);
            // the shaft through the floor, built with the safe ceiling lifted
            if (d.Kind == 1)
            {
                double aTop = g.R() * Math.PI * 2, aBot = Math.Atan2(d.Dz0, d.Dx0);
                g.Opened(() =>
                {
                    GmSpace(g, d.SX - 3, yb, d.SZ - 3, d.SX + 3, F - 1, d.SZ + 3, 2, (x, y, z, gr) =>
                        y >= yb - gr && y <= F - 1 && Hyp(x - d.SX, z - d.SZ) <= 2.5 + gr,
                        (x, y, z) => y < yb ? g.IronR : (y - yb) % 8 == 0 ? g.SteelR : g.Rust());
                    GmShaft(g, d.SX, d.SZ, new List<(double A, int Y)> { (aBot, yb), (aTop, F) }, 2.5, null, false, false);
                });
            }
            else if (d.Kind == 2)
            {
                // the standpipe: 3.5 inside, 2 of riveted plate, standing 8 to
                // 13 out of the ground with a torn top, a doorway at its foot
                int H = g.RI(8, 13), salt = g.RI(0, 9999);
                int sx = d.SX, sz = d.SZ;
                int Top(int x, int z) => F + H - (int)(4 * GmNoise2(Math.Atan2(z - sz, x - sx) * 3, salt * 0.01, g.Seed + 951));
                int ddx = -d.Dx0, ddz = -d.Dz0;
                if (ddx == 0 && ddz == 0) ddx = 1;
                double aBot = Math.Atan2(d.Dz0, d.Dx0), aDoor = Math.Atan2(ddz, ddx);
                if (Math.Abs(Math.IEEERemainder(aDoor - aBot, Math.PI * 2)) < 0.1) aDoor += Math.PI / 2;
                int fdx = (int)Math.Round(Math.Cos(aDoor)), fdz = (int)Math.Round(Math.Sin(aDoor));
                g.Opened(() =>
                {
                    GmSpace(g, sx - 4, yb, sz - 4, sx + 4, F + H, sz + 4, 2, (x, y, z, gr) =>
                        y >= yb - gr && y <= Top(x, z) && Hyp(x - sx, z - sz) <= 3.5 + gr,
                        (x, y, z) => y < yb ? g.IronR : (y - yb) % 8 == 0 || y == F - 1 ? g.SteelR : g.Rust());
                    // the doorway through its wall at the ground
                    int dx0 = Math.Min(sx + fdx * 3, sx + fdx * 7), dx1 = Math.Max(sx + fdx * 3, sx + fdx * 7);
                    int dz0 = Math.Min(sz + fdz * 3, sz + fdz * 7), dz1 = Math.Max(sz + fdz * 3, sz + fdz * 7);
                    if (fdx == 0) { dx0 = sx - 1; dx1 = sx + 1; } else { dz0 = sz - 1; dz1 = sz + 1; }
                    GmBoxW(g, dx0, F, dz0, dx1, F + 3, dz1, 0, (x, y, z) => 0);
                    GmShaft(g, sx, sz, new List<(double A, int Y)> { (aBot, yb), (Math.Atan2(fdz, fdx), F) }, 3.5, null, false, false);
                    g.Details.Add(() =>
                    {
                        for (int y = yb + 4; y <= F - 3; y += 8)
                            for (int i = 0; i < 8; i++)
                            {
                                double a2 = i * Math.PI / 4;
                                g.Glow(sx + (int)Math.Round(Math.Cos(a2) * 4.3), y, sz + (int)Math.Round(Math.Sin(a2) * 4.3));
                            }
                    });
                });
            }
            else
            {
                string side = d.Dx0 > 0 ? "west" : d.Dx0 < 0 ? "east" : d.Dz0 > 0 ? "north" : "south";
                g.Opened(() => GmLadder(g, d.SX, d.SZ, yb, F - 1, side, false));
            }
            // the tunnel from its foot to the chamber
            int sPad = d.Kind == 2 ? 4 : d.Kind == 1 ? 3 : 0;
            for (int p = 0; p + 1 < d.Path.Count; p++)
            {
                var (ax, az) = d.Path[p]; var (bx, bz) = d.Path[p + 1];
                if (ax == bx && az == bz) continue;
                if (p == 0 && sPad > 0) { ax += Math.Sign(bx - ax) * sPad; az += Math.Sign(bz - az) * sPad; }
                GmTunnel(g, ax, yb, az, bx, yb, bz, 1, 4, p == 0 ? 0 : 1, 1, n.Style);
            }
        }
        foreach (var m in mouths)
        {
            g.Opened(() => GmPipe(g, m.Pts, true, m.R, 2.0));
            var mo = m.Pts[^1]; var inner = m.Pts[^2];
            sapi.Logger.Notification("[landmassgenerator] great machine pipe mouth ({0}) at HUD {1}, {2}, {3} probe {4}, {5}, {6}", m.Where,
                (int)Math.Round(mo.X) - hudX, (int)mo.Y, (int)Math.Round(mo.Z) - hudZ, (int)Math.Round(inner.X) - hudX, (int)inner.Y + 2, (int)Math.Round(inner.Z) - hudZ);
        }
        g.CurKind = 'r';

        sapi.Logger.Notification("[landmassgenerator] great machine warrens: {0} chambers kept of {1} ({2} rooms, {3} fabrication bays, {4} dead ends), {5} tunnels, {6} ladders, {7} stair towers; " +
            "tie-ins {8} into works, {9} into pipes; ways down from {10} of {11} buildings, {15} standpipes in the crater; huge pipes out into the chasms {12}, the abyss {13}, the crater {14}",
            keptNodes, nodes.Count, nRooms, nBays, nDeadEnds, nTunnels, nLadders, nTowers, tieWorks, tiePipes, bTotal - bMissed, bTotal, nChasm, nAbyss, nCrater, nStand);
        if (why.Count > 0)
            sapi.Logger.Notification("[landmassgenerator] great machine pipe mouths not made: {0}", string.Join(", ", why.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value)));
    }

    // a level's usual tunnel style: 0 masonry, 1 riveted iron, 2 timbered
    // mine drift, 3 small walkable pipe (deep levels are iron, high ones mines)
    private static int GmLevelStyle(GmCtx g, int level)
    {
        double r = g.R();
        return level <= 1 ? (r < 0.5 ? 1 : r < 0.8 ? 0 : r < 0.92 ? 2 : 3)
             : level <= 4 ? (r < 0.38 ? 0 : r < 0.66 ? 2 : r < 0.86 ? 1 : 3)
             : (r < 0.5 ? 2 : r < 0.8 ? 0 : r < 0.9 ? 1 : 3);
    }

    // The shell of a style round a floor at f (interior f..f+h-1).
    private static int GmStyleCell(GmCtx g, int style, int x, int y, int z, int f, int h, int along)
    {
        if (style == 1)
            return y < f ? ((x + z) % 5 == 0 ? g.SteelR : g.IronR) : y > f + h - 1 ? g.SteelR : along % 6 == 0 ? g.SteelR : g.Rust();
        if (style == 2)
        {
            double n = g.K.Hash01(x * 3 + y * 7, z * 5 - y);
            return y < f ? (n < 0.5 ? g.Andes : g.PolOldC) : n < 0.4 ? g.Granite : n < 0.75 ? g.Andes : n < 0.95 ? g.Basalt : g.Drock;
        }
        return y < f ? g.PolG : y > f + h - 1 ? g.BrickG : (y - f) % 4 == 3 ? g.BrickC : g.K.Hash01(x * 7 + y, z * 13 - y) < 0.18 ? g.BrickB : g.BrickA;
    }

    // A straight tunnel along x or z between two floor points (the same z,
    // or the same x), hw either side of its axis, h tall over a floor that
    // rises evenly from one end to the other (half steps where it slopes),
    // carried extA past its first end and extB past its last.
    private static void GmTunnel(GmCtx g, int ax, int ay, int az, int bx, int by, int bz, int hw, int h, int extA, int extB, int style)
    {
        bool alongX = az == bz && ax != bx || ax == bx && az == bz;
        int aA = alongX ? ax : az, aB = alongX ? bx : bz, q = alongX ? az : ax;
        int sg = aB >= aA ? 1 : -1;
        int lo = Math.Min(aA - extA * sg, aB + extB * sg), hi = Math.Max(aA - extA * sg, aB + extB * sg);
        double Fl(int a) => aA == aB ? ay : ay + (by - ay) * Math.Clamp((a - aA) / (double)(aB - aA), 0, 1);
        int Fi(int a) => (int)Math.Floor(Fl(a));
        int ylo = Math.Min(ay, by), yhi = Math.Max(ay, by) + h;
        int x0 = alongX ? lo : q - hw, x1 = alongX ? hi : q + hw, z0 = alongX ? q - hw : lo, z1 = alongX ? q + hw : hi;
        GmSpace(g, x0, ylo, z0, x1, yhi, z1, 1, (x, y, z, gr) =>
        {
            int a = alongX ? x : z, d = Math.Abs((alongX ? z : x) - q);
            if (a < lo - gr || a > hi + gr || d > hw + gr) return false;
            int f = Fi(a);
            return y >= f - gr && y <= f + h - 1 + gr;
        }, (x, y, z) => GmStyleCell(g, style, x, y, z, Fi(alongX ? x : z), h, alongX ? x : z));
        var self = g.Hollows[^1];
        double heading = alongX ? 0 : Math.PI / 2;
        g.Details.Add(() =>
        {
            int len = hi - lo;
            int Xa(int a, int d) => alongX ? a : q + d;
            int Za(int a, int d) => alongX ? q + d : a;
            // a fitting never goes into another space's hollow (a ladder or
            // stair crossing it, a chamber it runs into)
            void Put(int x, int y, int z, int id) { if (g.HollowAt(x, y, z, o => o != self) == null) g.Solid(x, y, z, id); }
            for (int a = lo; a <= hi; a++)
            {
                // the floor's half steps where it slopes
                double fr = Math.Round(Fl(a) * 2) / 2.0;
                int full = (int)Math.Floor(fr), f = Fi(a);
                bool step = fr - full >= 0.5;
                for (int d = -hw; d <= hw; d++)
                {
                    for (int y = f; y < full; y++) Put(Xa(a, d), y, Za(a, d), GmStyleCell(g, style, Xa(a, d), y, Za(a, d), full, h, a));
                    if (step) Put(Xa(a, d), full, Za(a, d), g.SlabG);
                }
                int s = a - lo;
                int ceil = f + h - 1;
                bool inner = a >= lo + 2 && a <= hi - 2;             // away from the ends, where chambers widen it
                if (style == 0)
                {
                    Put(Xa(a, 0), f - 1, Za(a, 0), g.PolB);
                    if (len >= 6 && inner) g.Clut(Xa(a, hw), ceil, Za(a, hw), s % 7 == 3 ? "jonas-lamp|lightsource/on/lumi-pipe-h-lamp" : "pipe-veryrusted-h-EW", heading);
                    if (s % 8 == 4 && inner) { int side = (s / 8) % 2 == 0 ? -hw : hw; g.Lamp(Xa(a, side), f + 2, Za(a, side), "gas-wall1", alongX ? (side < 0 ? Math.PI / 2 : -Math.PI / 2) : (side < 0 ? 0 : Math.PI)); }
                }
                else if (style == 1)
                {
                    if (s % 3 != 2 && g.HollowAt(Xa(a, 0), ceil + 1, Za(a, 0), o => o != self) == null) g.Glow(Xa(a, 0), ceil + 1, Za(a, 0), g.GlowB);
                    if (s % 10 == 5) g.Lamp(Xa(a, 0), ceil, Za(a, 0), "gas-ceiling1", 0);
                }
                else
                {
                    // a timber set every 4: posts in the walls, a cap across the roof
                    if (s % 4 == 0)
                    {
                        for (int y = f; y <= ceil; y++) { Put(Xa(a, -hw - 1), y, Za(a, -hw - 1), g.LogV); Put(Xa(a, hw + 1), y, Za(a, hw + 1), g.LogV); }
                        for (int d = -hw - 1; d <= hw + 1; d++) Put(Xa(a, d), ceil + 1, Za(a, d), g.PlanksAW);
                    }
                    if (s % 8 == 4 && inner) { int side = (s / 8) % 2 == 0 ? -hw : hw; g.Lamp(Xa(a, side), f + 2, Za(a, side), "gas-wall2", alongX ? (side < 0 ? Math.PI / 2 : -Math.PI / 2) : (side < 0 ? 0 : Math.PI)); }
                    if (g.R() < 0.05 && !step) Put(Xa(a, g.RI(-hw, hw)), f, Za(a, g.RI(-hw, hw)), g.Stones);
                }
                // odds and ends along the walls, on level floor
                if (len >= 6 && inner && !step && g.R() < 0.045)
                {
                    int side = g.R() < 0.5 ? -hw : hw;
                    string t = style == 2 ? g.Pick(new[] { "crate/crate-large-ore2", "pile-trash-oldore", "barrel-metal1", "pile-tools1", "rubble-cracked2", "crate/crate-small-stacked" })
                        : g.Pick(new[] { "barrel-metal2", "crate/crate-medium-junk", "rubble-cracked1", "rubble-cracked3", "pile-trash-scrap", "mech-part2", "tank-metal1" });
                    g.Clut(Xa(a, side), full, Za(a, side), t, alongX ? (side < 0 ? Math.PI / 2 : -Math.PI / 2) : (side < 0 ? 0 : Math.PI));
                }
            }
        });
    }

    // A chamber of the maze: a junction (5 across), a furnished room (9) or
    // a fabrication bay (15, 8 tall), in its level's style.
    private void GmChamber(GmCtx g, GmNode n)
    {
        int x0 = n.X - n.Hw, x1 = n.X + n.Hw, z0 = n.Z - n.Hw, z1 = n.Z + n.Hw, y0 = n.Y, y1 = n.Y + n.H - 1;
        int style = n.Style == 3 ? 1 : n.Style;
        GmBoxW(g, x0, y0, z0, x1, y1, z1, 1, (x, y, z) => GmStyleCell(g, style, x, y, z, y0, n.H, x + z));
        var self = g.Hollows[^1];
        var doors = new List<(int U, int W)>();
        // the room's frame: w along x, u along z
        var f = new GmFrame(n.X, n.Y, n.Z, 1, 0);
        foreach (var d in n.Doors) doors.Add((d.Dz * n.Hw, d.Dx * n.Hw));
        if (n.Kind == 1)
        {
            string kind = g.Pick(new[] { "workshop", "lab", "store", "machine", "valves", "hazmat", "specimens", "drafting", "office", "dorm", "infirmary", "optics", "locust", "minecamp" });
            g.Details.Add(() => GmFurnish(g, f, -n.Hw, n.Hw, -n.Hw, n.Hw, 0, n.H - 1, kind, doors));
            return;
        }
        if (n.Kind == 2)
        {
            g.Details.Add(() => GmBay(g, n, f, doors));
            return;
        }
        g.Details.Add(() =>
        {
            g.Lamp(n.X, y1, n.Z, "gas-ceiling1", 0);
            // timber posts in a mine chamber's corners, but not the ladder's
            // (x0, z0): a ladder from the chamber below comes up there
            if (style == 2)
                foreach (var (cx2, cz2) in new[] { (x1, z0), (x0, z1), (x1, z1) })
                    for (int y = y0; y <= y1; y++)
                        if (g.HollowAt(cx2, y, cz2, o => o != self) == null) g.Solid(cx2, y, cz2, g.LogV);
            // a panel or a pile on one wall, away from the doors
            for (int t = 0; t < 3; t++)
            {
                int u = g.RI(-n.Hw, n.Hw), w = g.RI(-n.Hw, n.Hw);
                if (!(Math.Abs(u) == n.Hw || Math.Abs(w) == n.Hw) || doors.Any(d => Math.Abs(d.U - u) <= 1 && Math.Abs(d.W - w) <= 1)) continue;
                if (u == -n.Hw && w == -n.Hw) continue;                 // the ladder's corner
                string ty = style == 2 ? g.Pick(new[] { "crate/crate-large-ore2", "pile-trash-oldore", "barrel-metal1", "pile-tools2" })
                    : g.Pick(new[] { "!valve1-aged", "!gauge2", "!switch2-aged", "barrel-metal3", "crate/crate-medium-junk", "!tank1-aged" });
                double hd = f.H(Math.Abs(u) == n.Hw ? -Math.Sign(u) : 0, Math.Abs(w) == n.Hw ? -Math.Sign(w) : 0);
                if (ty[0] == '!') g.Dev(f.X(u, w), y0, f.Z(u, w), ty.Substring(1), hd);
                else g.Clut(f.X(u, w), y0, f.Z(u, w), ty, hd);
                break;
            }
        });
    }

    // A fabrication bay: where the locusts and eidolons built themselves.
    // Assembly lines down the middle (bodies, limbs, parts, half-made
    // locusts), cages and spikes along the walls, a seated eidolon frame at
    // the far end, lamps overhead; its finds are Jonas devices and parts.
    private static void GmBay(GmCtx g, GmNode n, GmFrame f, List<(int U, int W)> doors)
    {
        int hw = n.Hw;
        bool NearDoor(int u, int w) => doors.Any(d => Math.Abs(d.U - u) <= 2 && Math.Abs(d.W - w) <= 2);
        string[] line = { "locust-body", "locust-full", "locust-limbs1", "locust-limbs2", "locust-parts", "mech-part1", "mech-part3", "mech-part4", "mech-part6", "machine3", "machine5" };
        string[] wall = { "toolrack-locust", "locust-parts", "mech-part2", "mech-part5", "golem-hand1", "golem-foot1", "golem-head", "inactivelocustnest", "!gearbox1", "!geartrain3" };
        int cage = g.K.Id("locustnest-cage");
        int[] spikes = { g.K.Id("locustnest-metalspike-small"), g.K.Id("locustnest-metalspike-medium"), g.K.Id("locustnest-metalspike-large") };
        // two assembly lines along u, a walkway between
        foreach (int w in new[] { -3, 3 })
            for (int u = -hw + 2; u <= hw - 2; u++)
            {
                if (NearDoor(u, w)) continue;
                if ((u + hw) % 3 == 0) g.Solid(f.X(u, w), f.Y(-1), f.Z(u, w), g.SteelR);
                g.Clut(f.X(u, w), f.Y(0), f.Z(u, w), g.Pick(line), f.H(0, w < 0 ? 1 : -1));
            }
        for (int u = -hw; u <= hw; u++)
            foreach (int w in new[] { -hw, hw })
            {
                if (NearDoor(u, w) || g.R() > 0.55) continue;
                double r = g.R();
                if (r < 0.25 && cage != 0) g.Solid(f.X(u, w), f.Y(0), f.Z(u, w), cage);
                else if (r < 0.4) { int sp = spikes[g.RI(0, 2)]; if (sp != 0) g.Solid(f.X(u, w), f.Y(0), f.Z(u, w), sp); }
                else
                {
                    string t = g.Pick(wall);
                    if (t[0] == '!') g.Dev(f.X(u, w), f.Y(0), f.Z(u, w), t.Substring(1), f.H(0, w < 0 ? 1 : -1));
                    else g.Clut(f.X(u, w), f.Y(0), f.Z(u, w), t, f.H(0, w < 0 ? 1 : -1));
                }
            }
        if (!NearDoor(hw - 1, 0)) g.Clut(f.X(hw - 1, 0), f.Y(0), f.Z(hw - 1, 0), "eidolon-sitting", f.H(-1, 0));
        // lamps: a row overhead, and gas lamps on the long walls
        for (int u = -hw + 2; u <= hw - 2; u += 4) g.Lamp(f.X(u, 0), f.Y(n.H - 1), f.Z(u, 0), "gas-ceiling1", 0);
        for (int u = -hw + 3; u <= hw - 3; u += 6)
        {
            g.Lamp(f.X(u, -hw), f.Y(2), f.Z(u, -hw), "gas-wall1", f.H(0, 1));
            g.Lamp(f.X(u, hw), f.Y(2), f.Z(u, hw), "gas-wall2", f.H(0, -1));
        }
        // its finds: devices and parts, in the corners
        foreach (var (u, w) in new[] { (-hw + 1, -hw + 1), (hw - 1, hw - 1), (-hw + 1, hw - 1) })
            if (!NearDoor(u, w)) g.Loot.Add((f.X(u, w), f.Y(0), f.Z(u, w), g.R() < 0.4 ? "device" : g.R() < 0.7 ? "chest:jonas" : "ingots", f.H(-Math.Sign(u), -Math.Sign(w))));
        g.Rooms++;
    }

    // A ladder in a 1 by 1 shaft from y0 up to y1, its wood on the given
    // side's wall. inChambers: its foot and head are in chambers already,
    // so only the rock between them is cut (else the whole run is its own
    // space, with no shell above y1 so it stays flush with a floor).
    private static void GmLadder(GmCtx g, int x, int z, int y0, int y1, string side, bool inChambers)
    {
        if (y1 < y0) return;
        GmSpace(g, x, y0, z, x, y1, z, 1, (xx, yy, zz, gr) =>
            Math.Abs(xx - x) <= gr && Math.Abs(zz - z) <= gr && yy >= y0 - gr && yy <= y1,
            (xx, yy, zz) => yy % 6 == 0 ? g.SteelR : g.IronR);
        int id = g.K.IdF("ladder-wood-aged-" + side, "ladder-wood-veryaged-" + side, "ladder-stick-" + side);
        int wx = side == "west" ? -1 : side == "east" ? 1 : 0, wz = side == "north" ? -1 : side == "south" ? 1 : 0;
        g.Details.Add(() =>
        {
            for (int y = y0; y <= y1; y++)
            {
                // its wall where a shell was carved, but not across another space
                if (g.HollowAt(x + wx, y, z + wz) == null) g.Solid(x + wx, y, z + wz, g.IronR);
                if (id != 0) g.Solid(x, y, z, id);
            }
        });
    }
}
