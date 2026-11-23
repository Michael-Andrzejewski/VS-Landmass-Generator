using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace LandmassGenerator;

// ── THE EERIE ISLAND: THE BURYING ISLE ─────────────────────────────────
// After the Devastation, Tobias had the dead of The Machine's works, its
// mines and bell fleets and the towns round it ferried here, away from the
// living, and founded an order of wardens to bury them. There were too many
// for graves: the wardens dug great pits, coffined the named, laid the rest
// in rows in cloth, buried their belongings in old chests with them, and
// raised a marker of black glass over every pit (the ground round The
// Machine, fused to obsidian by the last firing). A red ochre cross on a
// marker means the pit is sealed. The six books in lore/eerie_*.txt tell it.
//
// The `graveyard` megastructure builds all of it:
//   - the great monolith in its obsidian circle over the Great Pit, and the
//     lone monoliths far apart over the downs, each over its own mass grave:
//     an earth pit shored with timber, coffins in rows along its walls, the
//     dead in cloth and bones between, and the chests of their belongings;
//   - the catacombs under the island's heart, two levels of galleries lined
//     with burial niches joining a rotunda under the Great Pit, ossuaries,
//     chapels, sarcophagus halls, the wardens' rooms and sealed vaults; a
//     spiral stair joins the levels, an oculus in the rotunda's floor drops
//     to the bone heap of the pit floor below, and the Warden's Gate stair
//     climbs to the downs; bone chutes fall into them from the pits above;
//   - buried family vaults, charnel trenches, ossuary wells, a buried
//     chapel and the grave robbers' tunnels from pit to pit, with their camp;
//   - hundreds of single graves in rows round the markers, walled family
//     plots, weathered pillars and the mausoleums.
// size= is the radius it spreads over. Counts: monoliths=, pillars=,
// graves=, plots=, mausoleums=, vaults=, trenches=, wells=, robbers=;
// catacombs= is the node reach each way (0 = none), pits=0 leaves out the
// mass graves.
public partial class LandmassGeneratorModSystem
{
    private sealed class EeriePit
    {
        public int X, Z, Top, Bottom;
        public bool Great;
        public readonly List<(int X, int Z)> Floor = new();
    }

    private sealed class EerieNode
    {
        public int I, J, X, Z, Y;
        public string Kind = "junction";
        public int Hx = 3, Hz = 3;
        public readonly List<EerieNode> Links = new();
    }

    private sealed class EerieCtx
    {
        public MegaKit K;
        public int Cx, Cz, R;
        public readonly List<(int X, int Z, int R)> Used = new();   // the surface: markers, graves, buildings
        public readonly List<(int X, int Z, int R)> Deep = new();   // underground: pits, vaults, the catacombs
        public readonly List<(int X, int Z)> Markers = new();
        public readonly List<EeriePit> Pits = new();
        public readonly HashSet<long> Open = new();                // the catacombs' hollow
        public readonly Dictionary<long, int> OpenTop = new();     // per column: its highest catacomb cell
        public readonly Dictionary<long, int> ShellTop = new();    // per column: its highest catacomb masonry
        public int Obs, ObsP, Brick, Cracked, Mossy, Cobble, MossCobble, SkullCobble, Polished, Slab,
            HeadStone, HeadSlab, Dirt, Soil, Post, BeamNS, BeamWE, Ribcage, Remains1, Remains2, Glow;
        public int Chests, Books, Coffins, Dead, Niches, Lights, Rooms, Galleries;
        public System.Func<int, int, int, bool> Wet = (x, y, z) => false;

        public static long Key(int x, int y, int z) => ((long)(x & 0x3FFFFF) << 42) | ((long)(y & 0xFFFFF) << 22) | (long)(z & 0x3FFFFF);
        public static long Col(int x, int z) => ((long)x << 32) | (uint)z;

        private static bool Clear(List<(int X, int Z, int R)> list, int x, int z, int r)
        {
            foreach (var u in list)
            {
                long dx = u.X - x, dz = u.Z - z, rr = u.R + r;
                if (dx * dx + dz * dz < rr * rr) return false;
            }
            return true;
        }

        public bool Free(int x, int z, int r, bool deep = false) => Clear(Used, x, z, r) && (!deep || Clear(Deep, x, z, r));
        public bool Dry(int x, int z) => K.Ground(x, z) >= K.Sea + 1;

        public (int X, int Z)? Spot(int minR, int maxR, int clear, bool deep = false)
        {
            for (int t = 0; t < 40; t++)
            {
                double a = K.R() * Math.PI * 2, r = minR + Math.Sqrt(K.R()) * (maxR - minR);
                int x = Cx + (int)Math.Round(Math.Cos(a) * r), z = Cz + (int)Math.Round(Math.Sin(a) * r);
                if (!Dry(x, z) || !Free(x, z, clear, deep)) continue;
                return (x, z);
            }
            return null;
        }

        public (int X, int Z)? SpotNear(int x0, int z0, int minD, int maxD, int clear)
        {
            for (int t = 0; t < 20; t++)
            {
                double a = K.R() * Math.PI * 2, r = minD + K.R() * (maxD - minD);
                int x = x0 + (int)Math.Round(Math.Cos(a) * r), z = z0 + (int)Math.Round(Math.Sin(a) * r);
                if ((x - Cx) * (long)(x - Cx) + (z - Cz) * (long)(z - Cz) > (long)R * R) continue;
                if (!Dry(x, z) || !Free(x, z, clear)) continue;
                return (x, z);
            }
            return null;
        }

        // An underground cell may be hollowed: `roof` under the ground, clear
        // of the catacombs' masonry, and with no natural water at or beside it.
        public bool Diggable(int x, int y, int z, int roof) => y <= K.Ground(x, z) - roof && Safe(x, y, z);
        public bool Safe(int x, int y, int z)
        {
            if (y < 8) return false;
            if (ShellTop.TryGetValue(Col(x, z), out int st) && y <= st + 1) return false;
            return !Wet(x, y, z);
        }

        public string Pick(params string[] a) => a[K.RI(0, a.Length - 1)];

        // The chest placer skips a chest with another within 3 sideways and 2
        // up or down, so a chest that close to one already planned is not
        // planned at all (false); a lore chest is kept whatever.
        private readonly List<(int X, int Y, int Z)> chestAt = new();
        public bool Chest(int x, int y, int z, string type, List<string> contents, string label)
        {
            bool lore = contents.Any(c => c.StartsWith("lore:"));
            if (!lore && chestAt.Any(c => Math.Abs(c.X - x) <= 3 && Math.Abs(c.Z - z) <= 3 && Math.Abs(c.Y - y) <= 2)) return false;
            chestAt.Add((x, y, z));
            K.Job.StructChests.Add((new BlockPos(x, y, z, K.Job.Dim), type, contents, label));
            Chests++;
            Books += contents.Count(c => c.StartsWith("lore:"));
            return true;
        }

        // Plain clutter props (the bare type would land on clutter-devastation)
        public void Prop(int x, int y, int z, string type, double heading) => K.Clutter(x, y, z, "clutter|" + type, heading);

        // heading 0 lays a coffin or a body along z, PI/2 along x
        public void Coffin(int x, int y, int z, double heading)
        {
            double r = K.R();
            Prop(x, y, z, r < 0.45 ? "coffin-rot-closed" : r < 0.7 ? "coffin-rot-ajar" : r < 0.88 ? "coffin-rot-open" : "coffin-rot-no-lid", heading);
            Coffins++;
        }

        // The dead on a floor cell: in cloth, a skeleton, a heap of bones.
        public void DeadAt(int x, int y, int z, double heading)
        {
            double r = K.R();
            if (r < 0.24) Prop(x, y, z, "pile-cloth" + K.RI(1, 5), heading);
            else if (r < 0.44) Prop(x, y, z, "skeleton-long", heading);
            else if (r < 0.56) Prop(x, y, z, "skeleton-compact" + K.RI(2, 3), heading);
            else if (r < 0.74) Prop(x, y, z, "pile-bones" + K.RI(1, 2), heading);
            else if (r < 0.88 && Remains1 != 0) K.Set(x, y, z, K.R() < 0.5 || Remains2 == 0 ? Remains1 : Remains2);
            else if (Ribcage != 0) K.Set(x, y, z, Ribcage);
            else Prop(x, y, z, "pile-bones1", heading);
            Dead++;
        }

        public void Candles(int x, int y, int z) => K.Clutter(x, y, z, "~clutter|" + (K.R() < 0.3 ? "candlestub-single" : "candlestubs-bunch" + K.RI(1, 4)), K.R() * Math.PI * 2);

        // A ghostlight set into the full block at x, y, z
        public void Light(int x, int y, int z)
        {
            if (Glow == 0) return;
            K.Glow(x, y, z, Glow);
            Lights++;
        }

        public int Wall()
        {
            double r = K.R();
            return r < 0.6 ? Brick : r < 0.75 ? Cracked : r < 0.9 ? Mossy : Cobble;
        }
    }

    private void EeriePalette(EerieCtx e)
    {
        var k = e.K;
        int N(params string[] codes)
        {
            foreach (var c in codes) { int i = k.Id(c); if (i != 0) return i; }
            return codes.Length > 0 ? k.Need(codes[0]) : 0;   // logs the code the game lacks
        }
        e.Obs = N("rock-obsidian", "rock-basalt");
        e.ObsP = N("rockpolished-obsidian", "rock-obsidian", "rock-basalt");
        e.Brick = N("stonebricks-andesite", "cobblestone-andesite");
        e.Cracked = N("crackedstonebricks-andesite", "stonebricks-andesite");
        e.Mossy = N("mossystonebricks-andesite", "crackedstonebricks-andesite");
        e.Cobble = N("cobblestone-andesite", "rock-andesite");
        e.MossCobble = N("mossycobble-andesite", "cobblestone-andesite");
        e.SkullCobble = N("cobbleskull-andesite", "cobblestone-andesite");
        e.Polished = N("rockpolished-andesite", "stonebricks-andesite");
        e.Slab = N("stonebrickslab-andesite-down-free", "cobbleslab-andesite-down-free");
        e.HeadStone = N("cobblestone-granite", "cobblestone-andesite");
        e.HeadSlab = N("cobblestoneslab-granite-down-free", "cobblestoneslab-andesite-down-free");
        e.Dirt = N("soil-medium-none", "soil-low-none");
        e.Soil = N("soil-medium-normal", "soil-low-normal");
        e.Post = N("log-placed-aged-ud", "log-placed-oak-ud");
        e.BeamNS = N("log-placed-aged-ns", "log-placed-oak-ns");
        e.BeamWE = N("log-placed-aged-we", "log-placed-oak-we");
        e.Ribcage = N("bonyremains-ribcage");
        e.Remains1 = N("drycarcass-humanoid1");
        e.Remains2 = N("drycarcass-humanoid2");
        e.Glow = k.Id("landmassgenerator:ghostlight-green");
        if (e.Slab == 0) e.Slab = e.Brick;
        if (e.HeadSlab == 0) e.HeadSlab = e.HeadStone;
    }

    private void MegaGraveyard(MegaKit k, int cx, int cz)
    {
        int R = Math.Max(30, k.Def.Size);
        int nPillars = k.OptI("pillars", R * 3 / 2), nMono = k.OptI("monoliths", Math.Max(6, R / 3)), nGraves = k.OptI("graves", R * 4);
        var e = new EerieCtx { K = k, Cx = cx, Cz = cz, R = R };
        EeriePalette(e);

        // Natural water: the world read sees the committed terrain, which is
        // exactly the natural water; our own staged writes are rock and air.
        var wba = sapi.World.BlockAccessor;
        var wpos = new BlockPos(0, 0, 0, k.Job.Dim);
        bool WaterAt(int x, int y, int z)
        {
            wpos.Set(x, y, z);
            var b = wba.GetBlock(wpos, BlockLayersAccess.Fluid);
            return b != null && b.Id != 0;
        }
        e.Wet = (x, y, z) => y < k.Sea - 1 && (WaterAt(x, y, z) || WaterAt(x + 1, y, z) || WaterAt(x - 1, y, z)
            || WaterAt(x, y, z + 1) || WaterAt(x, y, z - 1) || WaterAt(x, y + 1, z));

        Block xArt = sapi.World.GetBlock(new AssetLocation("game", "caveart-redochre-1-5-4"));
        void MarkX(int x, int y, int z, BlockFacing face)
        {
            if (xArt == null) return;
            k.AfterCommit.Add(() =>
            {
                var p = new BlockPos(x, y, z, k.Job.Dim);
                sapi.World.BlockAccessor.SetDecor(xArt, p, face);
            });
        }

        void Monolith(int x, int z, int H, double yaw, bool broken, int half = 1)
        {
            int g = k.Ground(x, z);
            bool alongX = Math.Abs(Math.Cos(yaw)) > Math.Abs(Math.Sin(yaw));
            double lean = k.R() < 0.4 ? (k.R() - 0.5) * 0.25 : 0;
            int top = broken ? (int)(H * (0.4 + k.R() * 0.3)) : H;
            for (int y = -3; y <= top; y++)
            {
                int off = (int)Math.Round(lean * Math.Max(0, y));
                // a broad slab narrows toward its crown
                int hw = half > 1 && y > top - 4 ? Math.Max(1, half - (y - (top - 4)) / 2) : half;
                for (int w = -hw; w <= hw; w++)
                    for (int d = 0; d <= 1; d++)                               // two blocks thick
                    {
                        if (y == top && !broken && w != 0) continue;           // a blunt crown
                        if (broken && y == top && k.R() < 0.5) continue;        // a ragged break
                        int px = x + (alongX ? w + off : d), pz = z + (alongX ? d : w + off);
                        k.Set(px, g + y, pz, k.R() < 0.85 ? e.Obs : e.ObsP);
                    }
            }
            // the red ochre cross: this pit is sealed
            if (k.R() < 0.75)
            {
                int y = g + 2 + k.RI(1, Math.Max(2, top - 3));
                int off = (int)Math.Round(lean * (y - g));
                if (alongX) { if (k.R() < 0.5) MarkX(x + off, y, z, BlockFacing.NORTH); else MarkX(x + off, y, z + 1, BlockFacing.SOUTH); }
                else { if (k.R() < 0.5) MarkX(x, y, z + off, BlockFacing.WEST); else MarkX(x + 1, y, z + off, BlockFacing.EAST); }
            }
            e.Used.Add((x, z, 3));
        }

        // 1. The great monolith and its obsidian circle, over the Great Pit.
        {
            int g = k.Ground(cx, cz);
            int H = k.OptI("great", 30);
            for (int y = g - 3; y <= g + H; y++)
                for (int x = -2; x <= 2; x++)
                    for (int z = -1; z <= 1; z++)
                    {
                        double taper = y > g + H - 4 ? (g + H - y) / 4.0 : 1;
                        if (Math.Abs(x) > 2 * taper + 0.2) continue;
                        k.Set(cx + x, y, cz + z, e.ObsP);
                    }
            for (int y = g + 4; y <= g + H - 6; y += 7) { MarkX(cx, y, cz - 1, BlockFacing.NORTH); MarkX(cx, y + 3, cz + 1, BlockFacing.SOUTH); }
            e.Used.Add((cx, cz, 12));
            int ring = k.OptI("ring", 9);
            for (int i = 0; i < ring; i++)
            {
                double a = i * Math.PI * 2 / ring;
                int x = cx + (int)Math.Round(Math.Cos(a) * 11), z = cz + (int)Math.Round(Math.Sin(a) * 11);
                Monolith(x, z, 7 + k.RI(0, 5), a + Math.PI / 2, i % 3 == 0);
            }
            if (e.Glow != 0)
                for (int i = 0; i < 4; i++)
                {
                    int lx = cx + (i < 2 ? -3 : 3), lz = cz + (i % 2 == 0 ? -2 : 2);
                    k.Glow(lx, k.Ground(lx, lz), lz, e.Glow);   // flush with the turf
                }
        }

        // 2. The catacombs, deep under the island's heart.
        EerieCatacombs(e);

        // 3. Lone obsidian monoliths across the downs, far apart: one or two
        // on every hill, each over a mass grave.
        // A marker stands only where its pit fits under it.
        bool pits = k.OptI("pits", 1) != 0;
        int monoGap = k.OptI("monogap", 16);
        var lone = new List<(int X, int Z, double Yaw, int Size)>();
        for (int i = 0, tries = 0; i < nMono && tries < nMono * 3; tries++)
        {
            var s = e.Spot(18, R, monoGap);
            if (s == null) continue;
            bool slab = k.R() < 0.3;
            double yaw = k.R() * Math.PI;
            int size = slab ? 3 : k.RI(0, 2);
            if (pits && EeriePitLow(e, s.Value.X, s.Value.Z, yaw, size, false) < k.Sea + 4) continue;
            Monolith(s.Value.X, s.Value.Z, (slab ? 10 : 13) + k.RI(0, slab ? 8 : 15), yaw, k.R() < 0.2, slab ? 2 : 1);
            e.Markers.Add((s.Value.X, s.Value.Z));
            lone.Add((s.Value.X, s.Value.Z, yaw, size));
            i++;
        }

        // 4. The mass graves under the markers.
        if (pits)
        {
            EerieDigPit(e, cx, cz, 0, 0, true);
            for (int i = 0; i < lone.Count; i++)
                EerieDigPit(e, lone[i].X, lone[i].Z, lone[i].Yaw, lone[i].Size, false, null);
        }

        // 5. Stone pillars: square, weathered, some fallen.
        int[] stone = { k.Id("rock-andesite"), k.Id("rock-granite"), e.Cobble, e.MossCobble, e.Cracked, e.Brick };
        stone = stone.Where(i => i != 0).ToArray();
        for (int i = 0; i < nPillars; i++)
        {
            var s = e.Spot(14, R, 3);
            if (s == null) continue;
            int x = s.Value.X, z = s.Value.Z, g = k.Ground(x, z);
            int w = k.R() < 0.55 ? 2 : 1;
            int H = 5 + k.RI(0, 15);
            if (k.R() < 0.2)
            {
                // fallen: lying on the grass
                bool ax = k.R() < 0.5;
                for (int t = 0; t < H; t++)
                    for (int a = 0; a < w; a++)
                    {
                        int px = x + (ax ? t : a), pz = z + (ax ? a : t);
                        k.Set(px, k.Ground(px, pz) + 1, pz, stone[k.RI(0, stone.Length - 1)]);
                    }
                e.Used.Add((x, z, 3));
                continue;
            }
            for (int y = -2; y <= H; y++)
                for (int a = 0; a < w; a++)
                    for (int b = 0; b < w; b++)
                    {
                        if (y > H - 2 && k.R() < 0.35) continue;    // crumbled top
                        k.Set(x + a, g + y, z + b, y < 2 && k.R() < 0.5 ? e.MossCobble : stone[k.RI(0, stone.Length - 1)]);
                    }
            e.Used.Add((x, z, 2));
        }

        // 6. Mausoleums: a stone-brick tomb house with a slab roof and an
        // open door; a stair inside goes down to a crypt with coffins in
        // wall niches and one chest.
        int nMaus = k.OptI("mausoleums", 4), madeMaus = 0;
        for (int m = 0; m < nMaus; m++)
        {
            var s = e.Spot(20, R - 10, 8, true);
            if (s == null) continue;
            int mx = s.Value.X, mz = s.Value.Z, mg = Median(k, mx, mz, 4);
            const int hx = 3, hz = 4;
            for (int x = mx - hx; x <= mx + hx; x++)
                for (int z = mz - hz; z <= mz + hz; z++)
                {
                    bool wall = Math.Abs(x - mx) == hx || Math.Abs(z - mz) == hz;
                    int g = k.Ground(x, z);
                    for (int y = Math.Min(g, mg) - 1; y < mg; y++) k.Set(x, y, z, e.Brick);
                    for (int y = mg; y <= mg + 5; y++)
                    {
                        bool door = z == mz - hz && Math.Abs(x - mx) <= 0 && y <= mg + 2 && y > mg;
                        if (y == mg) k.Set(x, y, z, e.Brick);
                        else if (wall && !door) k.Set(x, y, z, k.Hash01(x * 3 + y, z * 5) < 0.3 ? e.Mossy : e.Brick);
                        else if (y <= mg + 4) k.Set(x, y, z, 0);
                    }
                    k.Set(x, mg + 6, z, Math.Abs(x - mx) <= hx - 1 ? e.Slab : e.Brick);
                }
            // the crypt under it, reached by a stair along the back wall
            int cy = mg - 6;
            for (int x = mx - hx; x <= mx + hx; x++)
                for (int z = mz - hz; z <= mz + hz; z++)
                {
                    bool cwall = Math.Abs(x - mx) == hx || Math.Abs(z - mz) == hz;
                    k.Set(x, cy - 1, z, e.Brick);
                    for (int y = cy; y <= cy + 3; y++)
                        k.Set(x, y, z, cwall ? (k.Hash01(x * 7 + y, z * 3 - y) < 0.3 ? e.Mossy : e.Brick) : 0);
                    k.Set(x, cy + 4, z, e.Brick);   // the ceiling; the stair cuts its own way through
                }
            for (int st = 0; st < 6; st++)
            {
                int sz2 = mz + hz - 1 - st;
                for (int x = mx - 1; x <= mx; x++)
                {
                    for (int y = mg - st; y <= mg - st + 3 && y <= mg + 4; y++) k.Set(x, y, sz2, 0);
                    k.Set(x, mg - st - 1, sz2, e.Brick);
                }
            }
            for (int i = 0; i < 4; i++)
            {
                int zx = mz - hz + 2 + i * 2;
                if (zx > mz + hz - 2) break;
                e.Coffin(mx - hx + 1, cy, zx, Math.PI / 2);
                if (i < 3) e.Coffin(mx + hx - 1, cy, zx, Math.PI / 2);
            }
            e.DeadAt(mx + 1, cy, mz, 0);
            e.Chest(mx + 1, cy, mz - hz + 2, "normal-aged", RollLoot(k.Rand, GraveLoot, 4 + k.RI(0, 2)), "mausoleum");
            // set into the crypt's brick: the front wall facing the stair,
            // and both side walls over the coffin rows
            e.Light(mx, cy + 2, mz - hz);
            e.Light(mx - hx, cy + 2, mz - 1);
            e.Light(mx + hx, cy + 2, mz - 1);
            e.Used.Add((mx, mz, 9));
            e.Deep.Add((mx, mz, 9));
            madeMaus++;
        }

        // 7. Buried structures: family vaults, charnel trenches, ossuary
        // wells, a buried chapel and the grave robbers' tunnels.
        int nVaults = 0, nTrenches = 0, nWells = 0, nChapels = 0;
        for (int i = 0, want = k.OptI("vaults", 14); i < want * 3 && nVaults < want; i++)
        {
            var s = e.Spot(24, R - 14, 7, true);
            if (s != null && EerieVault(e, s.Value.X, s.Value.Z)) nVaults++;
        }
        for (int i = 0, want = k.OptI("trenches", 6); i < want * 3 && nTrenches < want; i++)
        {
            var s = e.Spot(24, R - 16, 10, true);
            if (s != null && EerieTrench(e, s.Value.X, s.Value.Z)) nTrenches++;
        }
        for (int i = 0, want = k.OptI("wells", 4); i < want * 3 && nWells < want; i++)
        {
            var s = e.Spot(22, R - 12, 6, true);
            if (s != null && EerieWell(e, s.Value.X, s.Value.Z)) nWells++;
        }
        for (int i = 0, want = k.OptI("chapels", 1); i < 12 && nChapels < want; i++)
        {
            var s = e.Spot(30, R - 22, 12, true);
            if (s != null && EerieChapel(e, s.Value.X, s.Value.Z)) nChapels++;
        }
        int nRobbers = EerieRobbers(e, k.OptI("robbers", 3));

        // 8. The single graves and the walled family plots.
        int nPlots = 0;
        for (int i = 0, want = k.OptI("plots", 12); i < want * 3 && nPlots < want; i++)
        {
            var s = e.Spot(20, R - 8, 8);
            if (s != null && EeriePlot(e, s.Value.X, s.Value.Z)) nPlots++;
        }
        int made = EerieGraves(e, nGraves);

        sapi.Logger.Notification("[landmassgenerator] graveyard: {0} graves and {1} family plots, {2} monoliths over {3} mass graves, {4} pillars, {5} mausoleums",
            made, nPlots, lone.Count + 1, e.Pits.Count, nPillars, madeMaus);
        sapi.Logger.Notification("[landmassgenerator] graveyard underground: catacombs of {0} rooms and {1} galleries with {2} niches, {3} vaults, {4} trenches, {5} wells, {6} buried chapel(s), {7} robber tunnel(s); {8} coffins, {9} dead laid out, {10} chests ({11} lore books), {12} lights",
            e.Rooms, e.Galleries, e.Niches, nVaults, nTrenches, nWells, nChapels, nRobbers, e.Coffins, e.Dead, e.Chests, e.Books, e.Lights);
    }

    // ── A mass grave ──────────────────────────────────────────────────────
    // An earth pit under a marker: an irregular dome, flat-floored, its
    // roof 4 under the lowest ground over it; walls lined with dug earth,
    // timber posts along the sides. Coffins stand in rows along the long
    // walls, the dead in cloth and bones lie between, earth is heaped at
    // the ends where the backfill came in, and the belongings went into
    // chests. The Great Pit under the circle is round and twice the size,
    // with a ring of coffins and a chute down into the catacombs' rotunda;
    // a pit over the catacombs sometimes has a bone chute too.
    // The lowest ground over a pit's footprint: its roof hangs 4 under it.
    private static int EeriePitLow(EerieCtx e, int mx, int mz, double yaw, int size, bool great)
    {
        var k = e.K;
        bool alongX = Math.Abs(Math.Cos(yaw)) >= Math.Abs(Math.Sin(yaw));
        int a = great ? 12 : 4 + size, b = great ? 12 : 3 + (size + 1) / 2;
        int low = int.MaxValue;
        for (int u = -a - 1; u <= a + 1; u++)
            for (int v = -b - 1; v <= b + 1; v++)
            {
                if (u * u / (double)((a + 1) * (a + 1)) + v * v / (double)((b + 1) * (b + 1)) > 1.05) continue;
                int x = alongX ? mx + u : mx + v, z = alongX ? mz + v : mz + u;
                low = Math.Min(low, k.Ground(x, z));
            }
        return low;
    }

    private void EerieDigPit(EerieCtx e, int mx, int mz, double yaw, int size, bool great, string lore = null)
    {
        var k = e.K;
        bool alongX = Math.Abs(Math.Cos(yaw)) >= Math.Abs(Math.Sin(yaw));
        int a = great ? 12 : 4 + size, b = great ? 12 : 3 + (size + 1) / 2;
        (int X, int Z) At(int u, int v) => alongX ? (mx + u, mz + v) : (mx + v, mz + u);
        int low = EeriePitLow(e, mx, mz, yaw, size, great);
        if (low < k.Sea + 4) return;   // too near the shore
        int top = low - 4, bottom = top - (great ? 7 : 6);
        var pit = new EeriePit { X = mx, Z = mz, Top = top, Bottom = bottom, Great = great };

        var open = new HashSet<long>();
        var cells = new List<(int X, int Y, int Z)>();
        var floor = new List<(int U, int V, int X, int Z)>();
        var colTop = new Dictionary<long, int>();
        for (int u = -a; u <= a; u++)
            for (int v = -b; v <= b; v++)
            {
                var p = At(u, v);
                double q = u * u / (double)(a * a) + v * v / (double)(b * b) + (k.Hash01(p.X * 5 + 17, p.Z * 3 - 11) - 0.5) * 0.22;
                if (q > 1) continue;
                int h = 1 + (int)Math.Round((top - bottom - 1) * Math.Sqrt(Math.Max(0, 1 - q)));
                if (!e.Diggable(p.X, bottom, p.Z, 3)) continue;
                int ct = bottom;
                for (int y = bottom; y < bottom + h; y++)
                {
                    if (!e.Diggable(p.X, y, p.Z, 3)) break;
                    open.Add(EerieCtx.Key(p.X, y, p.Z));
                    cells.Add((p.X, y, p.Z));
                    ct = y;
                }
                colTop[EerieCtx.Col(p.X, p.Z)] = ct;
                floor.Add((u, v, p.X, p.Z));
            }
        if (floor.Count < 6) return;

        // dug earth round the hollow, then the hollow
        foreach (var c in cells)
            foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0), (0, -1, 0) })
            {
                int x = c.X + dx, y = c.Y + dy, z = c.Z + dz;
                if (open.Contains(EerieCtx.Key(x, y, z)) || !e.Safe(x, y, z)) continue;
                k.Set(x, y, z, e.Dirt);
            }
        foreach (var c in cells) k.Set(c.X, c.Y, c.Z, 0);
        foreach (var f in floor) { pit.Floor.Add((f.X, f.Z)); }

        double across = alongX ? 0 : Math.PI / 2;    // lying across the pit's long axis
        var taken = new HashSet<long>();
        bool Take(int x, int z) => taken.Add(EerieCtx.Col(x, z));

        // timber posts along the sides, floor to roof
        var posts = great
            ? Enumerable.Range(0, 8).Select(i => ((int)Math.Round(Math.Cos(i * Math.PI / 4) * 7), (int)Math.Round(Math.Sin(i * Math.PI / 4) * 7))).ToList()
            : new List<(int, int)> { (-a / 2, -(b - 1)), (-a / 2, b - 1), (a / 2, -(b - 1)), (a / 2, b - 1) };
        foreach (var (pu, pv) in posts)
        {
            var p = At(pu, pv);
            if (!colTop.TryGetValue(EerieCtx.Col(p.X, p.Z), out int ct) || ct - bottom < 2 || !Take(p.X, p.Z)) continue;
            for (int y = bottom; y <= ct; y++) k.Set(p.X, y, p.Z, e.Post);
        }

        // the chute: the Great Pit's down into the rotunda, now and then a
        // lone pit's into whatever gallery runs under it
        if (great || k.R() < 0.45)
        {
            foreach (var f in floor.OrderBy(q => great ? q.U * q.U + q.V * q.V : k.R()))
            {
                if (great && f.U * f.U + f.V * f.V > 2) break;
                if (!e.OpenTop.TryGetValue(EerieCtx.Col(f.X, f.Z), out int ot) || ot >= bottom - 2) continue;
                for (int y = ot + 1; y < bottom; y++) k.Set(f.X, y, f.Z, 0);
                Take(f.X, f.Z);
                // bones where they landed
                int fy = ot;
                while (e.Open.Contains(EerieCtx.Key(f.X, fy - 1, f.Z))) fy--;
                if (!great) { e.DeadAt(f.X, fy, f.Z, across); break; }
            }
        }

        // the dead: coffins along the walls (round the Great Pit), the rest between
        foreach (var f in floor.OrderBy(_ => k.R()))
        {
            if (taken.Contains(EerieCtx.Col(f.X, f.Z))) continue;
            double d = Math.Sqrt(f.U * f.U + f.V * f.V);
            if (great)
            {
                if (d >= 9.5 && (f.U + f.V) % 2 == 0) { e.Coffin(f.X, bottom, f.Z, Math.Abs(f.U) > Math.Abs(f.V) ? Math.PI / 2 : 0); Take(f.X, f.Z); }
                else if (d > 2 && k.R() < 0.7) { e.DeadAt(f.X, bottom, f.Z, k.R() < 0.5 ? 0 : Math.PI / 2); Take(f.X, f.Z); }
                continue;
            }
            if (Math.Abs(f.V) >= b - 1.5 && f.U % 2 == 0 && Math.Abs(f.U) < a - 1)
            {
                if (k.R() < 0.75) { e.Coffin(f.X, bottom, f.Z, across); Take(f.X, f.Z); }
            }
            else if (Math.Abs(f.U) >= a - 1)
            {
                // backfill heaped at the ends
                if (k.R() < 0.55) { k.Set(f.X, bottom, f.Z, e.Dirt); Take(f.X, f.Z); }
            }
            else if (k.R() < 0.6) { e.DeadAt(f.X, bottom, f.Z, across); Take(f.X, f.Z); }
            else if (k.R() < 0.04) { e.Candles(f.X, bottom, f.Z); Take(f.X, f.Z); }
        }

        // the chests of their belongings
        int nChest = great ? 4 : 1 + (size >= 2 ? 1 : 0) + (k.R() < 0.3 ? 1 : 0);
        foreach (var f in floor.OrderBy(_ => k.R()))
        {
            if (nChest <= 0) break;
            if (!Take(f.X, f.Z)) continue;
            var contents = RollLoot(k.Rand, PitLoot, 3 + k.RI(0, 2));
            if (lore != null) { contents.Insert(0, "lore:" + lore); contents.Add("game:gear-rusty"); }
            if (!e.Chest(f.X, bottom, f.Z, e.Pick("collapsed1", "collapsed2", "collapsed3", "normal-aged"), contents, great ? "great pit" : "mass grave")) continue;
            if (contents[0].StartsWith("lore:")) lore = null;
            nChest--;
        }
        e.Pits.Add(pit);
        e.Deep.Add((mx, mz, Math.Max(a, b) + 2));
    }

    // ── The catacombs ─────────────────────────────────────────────────────
    // Two levels on a grid of nodes 13 apart round the island's centre, the
    // upper 20 under the lowest ground over it, the lower 9 under that.
    // Every node is a room and a spanning tree of galleries, with a few
    // loops, joins them. Built in passes: plan the hollow, wall every cell
    // within 2 of it (so the galleries can take burial niches), hollow it,
    // then fit it out.
    private void EerieCatacombs(EerieCtx e)
    {
        var k = e.K;
        const int S = 13;
        int N = Math.Clamp(k.OptI("catacombs", 4), 0, 6);
        if (N < 2) return;
        int cx = e.Cx, cz = e.Cz;
        double rMax = Math.Min(e.R * 0.48, N * S + 1.0);
        int span = (int)rMax + 12, minG = int.MaxValue;
        for (int dx = -span; dx <= span; dx += 3)
            for (int dz = -span; dz <= span; dz += 3)
                if (dx * dx + dz * dz <= span * span) minG = Math.Min(minG, k.Ground(cx + dx, cz + dz));
        int F = minG - 20, F2 = F - 9;
        if (F2 < 24 || minG < k.Sea + 4) return;

        var up = new Dictionary<(int, int), EerieNode>();
        var low = new Dictionary<(int, int), EerieNode>();
        for (int i = -N; i <= N; i++)
            for (int j = -N; j <= N; j++)
            {
                if (Math.Sqrt(i * i + j * j) * S > rMax) continue;
                up[(i, j)] = new EerieNode { I = i, J = j, X = cx + i * S, Z = cz + j * S, Y = F };
                if (Math.Sqrt(i * i + j * j) <= 2.3)
                    low[(i, j)] = new EerieNode { I = i, J = j, X = cx + i * S, Z = cz + j * S, Y = F2 };
            }
        var dirs = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
        void Link(EerieNode a, EerieNode b) { a.Links.Add(b); b.Links.Add(a); }
        void Tree(Dictionary<(int, int), EerieNode> nodes, (int, int) root, (int, int) skip, double loops)
        {
            var seen = new HashSet<(int, int)> { root, skip };
            var stack = new List<(int, int)> { root };
            while (stack.Count > 0)
            {
                var cur = stack[stack.Count - 1];
                var opts = dirs.Select(d => (cur.Item1 + d.Item1, cur.Item2 + d.Item2)).Where(p => nodes.ContainsKey(p) && !seen.Contains(p)).ToList();
                if (opts.Count == 0) { stack.RemoveAt(stack.Count - 1); continue; }
                var nx = opts[k.RI(0, opts.Count - 1)];
                seen.Add(nx);
                Link(nodes[cur], nodes[nx]);
                stack.Add(nx);
            }
            foreach (var kv in nodes)
                foreach (var d in new[] { (1, 0), (0, 1) })
                {
                    var p = (kv.Key.Item1 + d.Item1, kv.Key.Item2 + d.Item2);
                    if (kv.Key == skip || p == skip || !nodes.TryGetValue(p, out var nb)) continue;
                    if (kv.Value.Links.Contains(nb) || k.R() > loops) continue;
                    Link(kv.Value, nb);
                }
        }

        // the stair between the levels: two nodes out, a leaf on both levels,
        // reached from the centre's side
        var (sdi, sdj) = dirs[k.RI(0, 3)];
        var stairKey = (sdi * 2, sdj * 2);
        var inKey = (sdi, sdj);
        Tree(up, (0, 0), stairKey, 0.2);
        Link(up[stairKey], up[inKey]);
        Tree(low, inKey, stairKey, 0.25);
        Link(low[stairKey], low[inKey]);
        foreach (var dict in new[] { up, low })
            foreach (var key in dict.Where(kv => kv.Value.Links.Count == 0).Select(kv => kv.Key).ToList()) dict.Remove(key);

        // what each room is
        up[(0, 0)].Kind = "rotunda";
        low[(0, 0)].Kind = "pitfloor";
        up[stairKey].Kind = low[stairKey].Kind = "stair";
        var gateCands = up.Values.Where(n => n.Kind == "junction" && n != up[inKey])
            .Select(n => (n, d: Math.Abs(n.I) >= Math.Abs(n.J) ? (Math.Sign(n.I), 0) : (0, Math.Sign(n.J))))
            .Where(t => t.d != (0, 0) && !up.ContainsKey((t.n.I + t.d.Item1, t.n.J + t.d.Item2)))
            .OrderByDescending(t => t.n.I * t.n.I + t.n.J * t.n.J).ToList();
        if (gateCands.Count == 0) return;
        var gt = gateCands[k.RI(0, Math.Min(3, gateCands.Count - 1))];
        var gate = gt.n;
        gate.Kind = "gate";
        var wardenRoom = gate.Links.FirstOrDefault(n => n.Kind == "junction") ?? up.Values.FirstOrDefault(n => n.Kind == "junction");
        if (wardenRoom != null) wardenRoom.Kind = "warden";
        foreach (var n in up.Values.Where(n => n.Kind == "junction"))
        {
            double r = k.R();
            if (n.Links.Count == 1) n.Kind = r < 0.5 ? "vault" : r < 0.75 ? "ossuary" : "chapel";
            else n.Kind = r < 0.36 ? "junction" : r < 0.52 ? "ossuary" : r < 0.64 ? "chapel" : r < 0.79 ? "sarcophagi" : r < 0.9 ? "collapsed" : "niches";
        }
        var lowLeaves = low.Values.Where(n => n.Kind == "junction" && n.Links.Count == 1).ToList();
        if (lowLeaves.Count > 0) lowLeaves[k.RI(0, lowLeaves.Count - 1)].Kind = "cell";
        foreach (var n in low.Values.Where(n => n.Kind == "junction"))
        {
            double r = k.R();
            if (n.Links.Count == 1) n.Kind = r < 0.6 ? "vault" : "ossuary";
            else n.Kind = r < 0.4 ? "ossuary" : r < 0.7 ? "sarcophagi" : r < 0.85 ? "niches" : "junction";
        }
        foreach (var n in up.Values.Concat(low.Values))
        {
            bool alongX = k.R() < 0.5;
            switch (n.Kind)
            {
                case "chapel": case "sarcophagi": n.Hx = alongX ? 5 : 3; n.Hz = alongX ? 3 : 5; break;
                case "vault": case "cell": n.Hx = n.Hz = 2; break;
                case "junction": n.Hx = n.Hz = k.R() < 0.5 ? 2 : 3; break;
            }
        }
        int Reach(EerieNode n) => n.Kind switch
        {
            "rotunda" => 9, "pitfloor" => 8, "stair" => 4,
            _ => Math.Max(n.Hx, n.Hz) + 1,
        };

        // ── plan ──
        var openList = new List<(int X, int Y, int Z, byte S)>();
        void Plan(int x, int y, int z, byte style)
        {
            if (!e.Diggable(x, y, z, 6)) return;
            long key = EerieCtx.Key(x, y, z);
            if (!e.Open.Add(key)) return;
            openList.Add((x, y, z, style));
            long col = EerieCtx.Col(x, z);
            if (!e.OpenTop.TryGetValue(col, out int t) || y > t) e.OpenTop[col] = y;
        }
        byte StyleOf(EerieNode n) => n.Kind switch
        {
            "ossuary" or "pitfloor" => 1,
            "rotunda" or "chapel" or "warden" or "gate" or "cell" => 2,
            "collapsed" => 3,
            "vault" => 4,
            _ => 0,
        };

        var seals = new List<(int X, int Y, int Z)>();
        var galleries = new List<(EerieNode A, EerieNode B)>();
        foreach (var dict in new[] { up, low })
            foreach (var a in dict.Values)
                foreach (var b in a.Links)
                {
                    if (a.I > b.I || a.I == b.I && a.J > b.J) continue;   // once per link
                    galleries.Add((a, b));
                    bool alongX = a.Z == b.Z;
                    int len = alongX ? b.X - a.X : b.Z - a.Z;
                    for (int t = 0; t <= len; t++)
                    {
                        int px = alongX ? a.X + t : a.X, pz = alongX ? a.Z : a.Z + t;
                        // a vault's doorway is bricked up
                        bool seal = b.Kind == "vault" && len - t == Reach(b) || a.Kind == "vault" && t == Reach(a);
                        for (int w = -1; w <= 1; w++)
                        {
                            int x = px + (alongX ? 0 : w), z = pz + (alongX ? w : 0);
                            for (int y = a.Y; y <= a.Y + (w == 0 ? 4 : 3); y++)
                            {
                                if (seal) { seals.Add((x, y, z)); continue; }
                                Plan(x, y, z, 0);
                            }
                        }
                    }
                }

        // the rooms
        var stairs = new List<(int X, int Z, List<double> Surf)>();
        double th0 = Math.Atan2(-sdj, -sdi);    // the stair's treads start and end facing the centre
        foreach (var n in up.Values.Concat(low.Values))
        {
            byte st = StyleOf(n);
            switch (n.Kind)
            {
                case "rotunda":
                    for (int dx = -8; dx <= 8; dx++)
                        for (int dz = -8; dz <= 8; dz++)
                        {
                            double d = Math.Sqrt(dx * dx + dz * dz);
                            if (d > 8.4) continue;
                            int h = 4 + (int)Math.Round(3 * Math.Sqrt(Math.Max(0, 1 - d * d / 72.25)));
                            for (int y = n.Y; y <= n.Y + h; y++) Plan(n.X + dx, y, n.Z + dz, st);
                            // the oculus down to the pit floor
                            if (d <= 2) for (int y = n.Y - 2; y < n.Y; y++) Plan(n.X + dx, y, n.Z + dz, st);
                        }
                    break;
                case "pitfloor":
                    for (int dx = -7; dx <= 7; dx++)
                        for (int dz = -7; dz <= 7; dz++)
                        {
                            double d = Math.Sqrt(dx * dx + dz * dz);
                            if (d > 7.4) continue;
                            int h = 4 + (int)Math.Round(2 * Math.Sqrt(Math.Max(0, 1 - d * d / 56.25)));
                            for (int y = n.Y; y <= n.Y + h; y++) Plan(n.X + dx, y, n.Z + dz, st);
                        }
                    break;
                case "stair":
                    if (n.Y != F) break;   // one spiral serves both levels
                    for (int dx = -4; dx <= 4; dx++)
                        for (int dz = -4; dz <= 4; dz++)
                        {
                            double r = Math.Sqrt(dx * dx + dz * dz);
                            if (r > 3.6 || r < 1.5) continue;
                            double th = Math.Atan2(dz, dx) - th0;
                            while (th < 0) th += Math.PI * 2;
                            while (th >= Math.PI * 2) th -= Math.PI * 2;
                            var surf = new List<double>();
                            for (int t = 0; t < 3; t++)
                            {
                                double s = Math.Round((F - 4.5 * (th / (Math.PI * 2) + t)) * 2) / 2;
                                if (s < F2) break;
                                surf.Add(s);
                            }
                            if (surf.Count == 0 || surf[surf.Count - 1] > F2 + 1) surf.Add(F2);
                            int ceilY = F + 4;
                            foreach (double s in surf)
                            {
                                bool whole = s == Math.Floor(s);
                                int lo = whole ? (int)s : (int)Math.Floor(s) + 1;
                                for (int y = lo; y <= ceilY; y++) Plan(n.X + dx, y, n.Z + dz, 0);
                                ceilY = (whole ? (int)s - 1 : (int)Math.Floor(s)) - 1;
                            }
                            stairs.Add((n.X + dx, n.Z + dz, surf));
                        }
                    break;
                default:
                    int H = n.Kind == "chapel" ? 5 : 4;
                    for (int dx = -n.Hx; dx <= n.Hx; dx++)
                        for (int dz = -n.Hz; dz <= n.Hz; dz++)
                            for (int y = n.Y; y <= n.Y + H; y++) Plan(n.X + dx, y, n.Z + dz, st);
                    break;
            }
            e.Rooms++;
        }
        e.Galleries = galleries.Count;

        // ── walls: every cell within 2 sideways and 1 up or down ──
        int ShellBlock(byte s, bool floor, int x, int y, int z)
        {
            double h = k.Hash01(x * 7 + y * 13, z * 11 - y);
            switch (s)
            {
                case 1: return floor ? (h < 0.4 ? e.SkullCobble : e.Cobble) : h < 0.6 ? e.SkullCobble : h < 0.8 ? e.Brick : e.Mossy;
                case 2: return floor ? (h < 0.7 ? e.Polished : e.Brick) : h < 0.75 ? e.Brick : h < 0.9 ? e.Cracked : e.Mossy;
                case 3: return h < 0.4 ? e.Cobble : h < 0.7 ? e.MossCobble : h < 0.85 ? e.Cracked : e.Brick;
                case 4: return h < 0.5 ? e.Mossy : h < 0.8 ? e.Cracked : e.Brick;
                default: return floor ? (h < 0.5 ? e.Brick : h < 0.8 ? e.Cobble : e.MossCobble) : h < 0.62 ? e.Brick : h < 0.78 ? e.Cracked : h < 0.92 ? e.Mossy : e.SkullCobble;
            }
        }
        var walled = new HashSet<long>();
        foreach (var c in openList)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        int x = c.X + dx, y = c.Y + dy, z = c.Z + dz;
                        long key = EerieCtx.Key(x, y, z);
                        if (e.Open.Contains(key) || !walled.Add(key)) continue;
                        k.Set(x, y, z, ShellBlock(c.S, e.Open.Contains(EerieCtx.Key(x, y + 1, z)), x, y, z));
                        long col = EerieCtx.Col(x, z);
                        if (!e.ShellTop.TryGetValue(col, out int t) || y > t) e.ShellTop[col] = y;
                    }

        // ── hollow ──
        foreach (var c in openList) k.Set(c.X, c.Y, c.Z, 0);
        foreach (var s in seals) k.Set(s.X, s.Y, s.Z, k.Hash01(s.X * 3 + s.Y, s.Z) < 0.5 ? e.Cracked : e.Mossy);

        // ── fit out ──
        bool IsOpen(int x, int y, int z) => e.Open.Contains(EerieCtx.Key(x, y, z));
        bool Solid(int x, int y, int z) => walled.Contains(EerieCtx.Key(x, y, z));

        // the spiral stair: treads, a newel of skulls with lights in it
        foreach (var (x, z, surf) in stairs)
            foreach (double s in surf)
            {
                if (s == Math.Floor(s)) k.Set(x, (int)s - 1, z, e.Brick);
                else k.Set(x, (int)Math.Floor(s), z, e.Slab);
            }
        if (up.TryGetValue(stairKey, out var sn))
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx * dx + dz * dz > 2) continue;
                    for (int y = F2 - 1; y <= F + 4; y++) k.Set(sn.X + dx, y, sn.Z + dz, e.SkullCobble);
                }
            foreach (int y in new[] { F2 + 2, F - 3, F + 2 }) e.Light(sn.X, y, sn.Z);
        }

        // burial niches in the gallery walls: two tiers, two cells long,
        // a pier between; a light in the wall between the tiers now and then
        foreach (var (a, b) in galleries)
        {
            bool alongX = a.Z == b.Z;
            int len = alongX ? b.X - a.X : b.Z - a.Z, Y = a.Y;
            double lying = alongX ? Math.PI / 2 : 0;
            for (int t = Reach(a) + 1; t + 1 <= len - Reach(b) - 1; t++)
            {
                int px = alongX ? a.X + t : a.X, pz = alongX ? a.Z : a.Z + t;
                foreach (int side in new[] { -1, 1 })
                {
                    (int X, int Z) Off(int w, int dt) => alongX ? (px + dt, pz + side * w) : (px + side * w, pz + dt);
                    if (t % 12 == (side > 0 ? 2 : 8))
                    {
                        var lp = Off(2, 0);
                        if (Solid(lp.X, Y + 2, lp.Z)) e.Light(lp.X, Y + 2, lp.Z);
                    }
                    if (t % 3 != 0) continue;
                    foreach (int ny in new[] { Y + 1, Y + 3 })
                    {
                        var n0 = Off(2, 0); var n1 = Off(2, 1); var b0 = Off(3, 0); var b1 = Off(3, 1);
                        var i0 = Off(1, 0); var i1 = Off(1, 1);
                        if (!Solid(n0.X, ny, n0.Z) || !Solid(n1.X, ny, n1.Z) || !Solid(b0.X, ny, b0.Z) || !Solid(b1.X, ny, b1.Z)) continue;
                        if (!IsOpen(i0.X, ny, i0.Z) || !IsOpen(i1.X, ny, i1.Z) || k.R() < 0.1) continue;
                        k.Set(n0.X, ny, n0.Z, 0); k.Set(n1.X, ny, n1.Z, 0);
                        double r = k.R();
                        if (r < 0.3) e.Coffin(n0.X, ny, n0.Z, lying);
                        else if (r < 0.62) { e.Prop(n0.X, ny, n0.Z, "skeleton-long", lying); if (k.R() < 0.5) e.Prop(n1.X, ny, n1.Z, "pile-bones" + k.RI(1, 2), lying); e.Dead++; }
                        else if (r < 0.78) { e.Prop(n0.X, ny, n0.Z, "pile-cloth" + k.RI(1, 5), lying); e.Dead++; }
                        else if (r < 0.9) { e.Prop(n0.X, ny, n0.Z, "pile-bones" + k.RI(1, 2), lying); e.Prop(n1.X, ny, n1.Z, "pile-bones" + k.RI(1, 2), lying); e.Dead++; }
                        else e.Candles(n0.X, ny, n0.Z);
                        e.Niches++;
                    }
                }
            }
            // a stretch fallen in now and then: rubble to climb over
            if (k.R() < 0.12 && len > 8)
            {
                int t0 = len / 2;
                for (int dt = -1; dt <= 1; dt++)
                    for (int w = -1; w <= 1; w++)
                    {
                        int x = alongX ? a.X + t0 + dt : a.X + w, z = alongX ? a.Z + w : a.Z + t0 + dt;
                        if (!IsOpen(x, Y, z)) continue;
                        k.Set(x, Y, z, k.R() < 0.5 ? e.Cobble : e.MossCobble);
                        if (IsOpen(x, Y + 1, z) && k.R() < 0.4) e.Prop(x, Y + 1, z, "rubble-cracked" + k.RI(1, 4), k.R() * 6);
                    }
            }
        }

        // the rooms
        foreach (var n in up.Values.Concat(low.Values))
        {
            int X = n.X, Z = n.Z, Y = n.Y;
            bool lx = n.Hx >= n.Hz;    // the long axis runs along x
            (int X, int Z) P(int u, int w) => lx ? (X + u, Z + w) : (X + w, Z + u);
            int Hl = lx ? n.Hx : n.Hz, Hw = lx ? n.Hz : n.Hx;
            double along = lx ? Math.PI / 2 : 0, acrossH = lx ? 0 : Math.PI / 2;
            void WallLights(int y)
            {
                foreach (var (u, w) in new[] { (0, Hw + 1), (0, -(Hw + 1)), (Hl + 1, 0), (-(Hl + 1), 0) })
                {
                    var p = P(u, w);
                    if (Solid(p.X, y, p.Z)) { e.Light(p.X, y, p.Z); }
                    else
                    {
                        // a gallery comes in there: beside it instead
                        var q = Math.Abs(w) > 0 ? P(u + 2, w) : P(u, w + 2);
                        if (Solid(q.X, y, q.Z)) e.Light(q.X, y, q.Z);
                    }
                }
            }
            void FloorChest(int u, int w, string type, List<string> contents, string label)
            {
                var p = P(u, w);
                if (IsOpen(p.X, Y, p.Z)) e.Chest(p.X, Y, p.Z, type, contents, label);
            }
            switch (n.Kind)
            {
                case "rotunda":
                {
                    // sarcophagi round the hall, statues at the four quarters,
                    // lights set in the floor, a parapet round the oculus
                    for (int i = 0; i < 8; i++)
                    {
                        double ang = (i + 0.5) * Math.PI / 4;
                        int ux = (int)Math.Round(Math.Cos(ang) * 5), uz = (int)Math.Round(Math.Sin(ang) * 5);
                        int vx = (int)Math.Round(Math.Cos(ang) * 6), vz = (int)Math.Round(Math.Sin(ang) * 6);
                        foreach (var (sx, sz) in new[] { (ux, uz), (vx, vz) })
                        {
                            k.Set(X + sx, Y, Z + sz, e.Polished);
                            k.Set(X + sx, Y + 1, Z + sz, e.Slab);
                        }
                        if (k.R() < 0.6) e.Candles(X + (int)Math.Round(Math.Cos(ang) * 4), Y, Z + (int)Math.Round(Math.Sin(ang) * 4));
                    }
                    // lights in the floor at each doorway and toward each statue
                    foreach (var (lx2, lz2) in new[] { (7, 0), (-7, 0), (0, 7), (0, -7), (4, 4), (-4, 4), (4, -4), (-4, -4) })
                        e.Light(X + lx2, Y - 1, Z + lz2);
                    foreach (var (sx, sz, hd) in new[] { (5, 5, -Math.PI * 3 / 4), (-5, 5, Math.PI * 3 / 4), (5, -5, -Math.PI / 4), (-5, -5, Math.PI / 4) })
                        if (IsOpen(X + sx, Y, Z + sz)) e.Prop(X + sx, Y, Z + sz, "statue01", hd);
                    for (int dx = -3; dx <= 3; dx++)
                        for (int dz = -3; dz <= 3; dz++)
                        {
                            double d = Math.Sqrt(dx * dx + dz * dz);
                            if (d > 2.4 && d <= 3.3) k.Set(X + dx, Y, Z + dz, e.Polished);
                        }
                    e.Chest(X + 4, Y, Z - 1, "normal-aged", new List<string> { "randomlore:rfpool-village" }.Concat(RollLoot(k.Rand, VaultLoot, 3)).ToList(), "rotunda");
                    for (int i = 0; i < 5; i++)
                    {
                        double ang = k.R() * Math.PI * 2, d = 3.6 + k.R() * 3;
                        int x = X + (int)Math.Round(Math.Cos(ang) * d), z = Z + (int)Math.Round(Math.Sin(ang) * d);
                        if (IsOpen(x, Y, z) && Solid(x, Y - 1, z)) e.DeadAt(x, Y, z, k.R() * 6);
                    }
                    break;
                }
                case "pitfloor":
                {
                    // where the Great Pit's bones came down: a heap of them
                    for (int dx = -7; dx <= 7; dx++)
                        for (int dz = -7; dz <= 7; dz++)
                        {
                            double d = Math.Sqrt(dx * dx + dz * dz);
                            int x = X + dx, z = Z + dz;
                            if (!IsOpen(x, Y, z)) continue;
                            if (d <= 1.5) { k.Set(x, Y, z, e.SkullCobble); e.DeadAt(x, Y + 1, z, k.R() * 6); }
                            else if (d <= 3.6 || d <= 6.2 && k.R() < 0.45) e.DeadAt(x, Y, z, k.R() * 6);
                        }
                    foreach (var (dx, dz) in new[] { (6, 6), (-6, 6), (6, -6), (-6, -6) })
                        for (int r = 0; r <= 3; r++)
                        {
                            int x = X + dx + Math.Sign(dx) * r, z = Z + dz + Math.Sign(dz) * r;
                            if (Solid(x, Y + 2, z)) { e.Light(x, Y + 2, z); break; }
                        }
                    e.Chest(X + 5, Y, Z + 2, "collapsed2", RollLoot(k.Rand, PitLoot, 4), "pit floor");
                    break;
                }
                case "stair":
                    break;
                case "gate":
                {
                    WallLights(Y + 2);
                    FloorChest(-Hl + 1, Hw - 1, "normal-aged", new List<string> { "randomlore:rfpool-village" }.Concat(RollLoot(k.Rand, WardenLoot, 2)).ToList(), "warden's gate");
                    var lp = P(Hl - 1, -(Hw - 1));
                    e.Prop(lp.X, Y, lp.Z, "lantern/ground1", 0);
                    var dp = P(-Hl + 1, -(Hw - 1));
                    e.DeadAt(dp.X, Y, dp.Z, along);
                    EerieGateStair(e, n, gt.d.Item1, gt.d.Item2);
                    break;
                }
                case "warden":
                {
                    WallLights(Y + 2);
                    var bp = P(-Hl + 1, -Hw + 1); e.Prop(bp.X, Y, bp.Z, "bed/bed-straw-ruined1", along);
                    var tp = P(1, -Hw + 1); e.Prop(tp.X, Y, tp.Z, "table-ruined1", 0);
                    var cp = P(1, -Hw + 2); e.Prop(cp.X, Y, cp.Z, "chair-ruined1", 0);
                    var lp = P(Hl - 1, 0); e.Prop(lp.X, Y, lp.Z, "bookshelves/lecturn-aged-book-open-tall", lx ? -Math.PI / 2 : 0);
                    var sp = P(-Hl + 1, Hw - 1); e.Prop(sp.X, Y, sp.Z, "bookshelves/scrollrack-ruined1", along);
                    var pp = P(-1, Hw - 1); e.Prop(pp.X, Y, pp.Z, "bookshelves/bookpile-aged" + k.RI(1, 5), 0);
                    var kp = P(2, Hw - 1); e.Candles(kp.X, Y, kp.Z);
                    FloorChest(Hl - 1, Hw - 1, "normal-aged",
                        new List<string> { "randomlore:rfpool-tobias", "landmassgenerator:islandmap-random" }.Concat(RollLoot(k.Rand, WardenLoot, 3)).ToList(), "wardens' room");
                    break;
                }
                case "cell":
                {
                    // the last warden's cell: he died in his bed
                    var bp = P(-Hl + 1, -Hw + 1); e.Prop(bp.X, Y, bp.Z, "skeleton-inbed", along);
                    var bb = P(-Hl + 1, -Hw + 2); e.Prop(bb.X, Y, bb.Z, "bed/bed-straw-single1", along);   // its frame reaches back under him
                    var tp = P(1, -Hw + 1); e.Prop(tp.X, Y, tp.Z, "table-ruined2", 0);
                    var kp = P(0, Hw - 1); e.Candles(kp.X, Y, kp.Z);
                    var gp = P(Hl, 0);
                    if (Solid(gp.X + (lx ? 1 : 0), Y + 2, gp.Z + (lx ? 0 : 1))) e.Light(gp.X + (lx ? 1 : 0), Y + 2, gp.Z + (lx ? 0 : 1));
                    FloorChest(Hl - 1, Hw - 1, "normal-aged", new List<string> { "randomlore:rfpool-sea" }.Concat(RollLoot(k.Rand, WardenLoot, 2)).ToList(), "last warden");
                    break;
                }
                case "ossuary":
                {
                    WallLights(Y + 2);
                    // skulls and bones in niches all round, heaps along the walls,
                    // a bone altar in the middle
                    for (int u = -Hl; u <= Hl; u++)
                        for (int w = -Hw; w <= Hw; w++)
                        {
                            var p = P(u, w);
                            bool edge = Math.Abs(u) == Hl || Math.Abs(w) == Hw;
                            if (edge && k.R() < 0.6) e.Prop(p.X, Y, p.Z, "pile-bones" + k.RI(1, 2), k.R() * 6);
                        }
                    for (int u = -1; u <= 1; u++)
                    {
                        var p = P(u, 0);
                        k.Set(p.X, Y, p.Z, e.SkullCobble);
                        e.DeadAt(p.X, Y + 1, p.Z, along);
                    }
                    for (int u = -Hl + 1; u <= Hl - 1; u += 2)
                        foreach (int side in new[] { -1, 1 })
                            foreach (int ny in new[] { Y + 1, Y + 3 })
                            {
                                var p = P(u, side * (Hw + 1));
                                var q = P(u, side * (Hw + 2));
                                if (!Solid(p.X, ny, p.Z) || !Solid(q.X, ny, q.Z)) continue;
                                k.Set(p.X, ny, p.Z, 0);
                                e.Prop(p.X, ny, p.Z, k.R() < 0.6 ? "pile-bones" + k.RI(1, 2) : "skeleton-compact" + k.RI(2, 3), k.R() * 6);
                                e.Niches++; e.Dead++;
                            }
                    if (k.R() < 0.4) FloorChest(Hl - 1, -Hw + 1, e.Pick("collapsed1", "normal-aged"), RollLoot(k.Rand, GraveLoot, 3), "ossuary");
                    break;
                }
                case "chapel":
                {
                    // an altar at the far end, candles, a statue behind it,
                    // benches facing it
                    for (int w = -1; w <= 1; w++)
                    {
                        var p = P(Hl - 1, w);
                        k.Set(p.X, Y, p.Z, e.Polished);
                        if (w != 0) e.Candles(p.X, Y + 1, p.Z);
                    }
                    var lec = P(Hl - 1, 0);
                    e.Prop(lec.X, Y + 1, lec.Z, "bookshelves/bookstand-aged-book-opened-large", lx ? -Math.PI / 2 : Math.PI);
                    var stp = P(Hl, 0); e.Prop(stp.X, Y, stp.Z, "statue01", lx ? -Math.PI / 2 : Math.PI);
                    for (int u = -Hl + 1; u <= Hl - 4; u += 2)
                        foreach (int w in new[] { -2, 2 })
                        {
                            if (Math.Abs(w) > Hw) continue;
                            var p = P(u, w);
                            if (k.R() < 0.85) e.Prop(p.X, Y, p.Z, "chair-long", lx ? Math.PI / 2 : 0);
                        }
                    foreach (int side in new[] { -1, 1 })
                    {
                        var p = P(Hl - 1, side * (Hw + 1));
                        if (Solid(p.X, Y + 3, p.Z)) e.Light(p.X, Y + 3, p.Z);
                    }
                    var dp = P(0, 0); if (k.R() < 0.5) e.DeadAt(dp.X, Y, dp.Z, along);
                    if (k.R() < 0.5) FloorChest(Hl, -Hw + 1, "normal-aged", new List<string> { "randomlore:rfpool-tobias" }.Concat(RollLoot(k.Rand, GraveLoot, 3)).ToList(), "chapel");
                    break;
                }
                case "sarcophagi":
                {
                    WallLights(Y + 2);
                    for (int u = -Hl + 1; u + 1 <= Hl - 1; u += 3)
                        foreach (int w in new[] { -2, 2 })
                        {
                            if (Math.Abs(w) > Hw) continue;
                            for (int du = 0; du <= 1; du++)
                            {
                                var p = P(u + du, w);
                                k.Set(p.X, Y, p.Z, e.Polished);
                                k.Set(p.X, Y + 1, p.Z, e.Slab);
                            }
                        }
                    for (int u = -Hl + 2; u <= Hl - 2; u += 3)
                    {
                        var p = P(u, 0);
                        if (k.R() < 0.5) e.Coffin(p.X, Y, p.Z, along);
                    }
                    if (k.R() < 0.3) FloorChest(Hl, Hw, "normal-aged", RollLoot(k.Rand, GraveLoot, 3), "sarcophagus hall");
                    break;
                }
                case "collapsed":
                {
                    // the roof came down: heaps of stone, rubble, one of them under it
                    for (int u = -Hl; u <= Hl; u++)
                        for (int w = -Hw; w <= Hw; w++)
                        {
                            var p = P(u, w);
                            double hsh = k.Hash01(p.X * 9, p.Z * 7);
                            bool edge = Math.Abs(u) >= Hl - 1 || Math.Abs(w) >= Hw - 1;
                            if (hsh < (edge ? 0.55 : 0.2))
                            {
                                int hgt = hsh < 0.12 ? 2 : 1;
                                for (int y = Y; y < Y + hgt; y++) k.Set(p.X, y, p.Z, k.R() < 0.5 ? e.Cobble : e.MossCobble);
                            }
                            else if (k.R() < 0.3) e.Prop(p.X, Y, p.Z, "rubble-cracked" + k.RI(1, 4), k.R() * 6);
                        }
                    var sk = P(0, 0); e.Prop(sk.X, Y, sk.Z, "skeleton-compact2", along); e.Dead++;
                    break;
                }
                case "vault":
                {
                    // sealed: whoever was laid here took their goods with them
                    for (int u = -Hl; u <= Hl; u += 2)
                    {
                        var p = P(u, -Hw);
                        if (k.R() < 0.7) e.Coffin(p.X, Y, p.Z, along);
                    }
                    var dp = P(0, Hw); e.DeadAt(dp.X, Y, dp.Z, along);
                    var cp = P(Hl, Hw); e.Candles(cp.X, Y, cp.Z);
                    bool golden = k.R() < 0.5;
                    FloorChest(0, 0, golden ? "golden-aged" : "normal-aged", RollLoot(k.Rand, VaultLoot, golden ? 4 : 5), "sealed vault");
                    break;
                }
                case "niches":
                {
                    // a burial hall: the walls all niches, floor to ceiling
                    WallLights(Y + 2);
                    for (int u = -Hl; u <= Hl; u++)
                        foreach (int side in new[] { -1, 1 })
                            foreach (int ny in new[] { Y, Y + 1, Y + 3 })
                            {
                                var p = P(u, side * (Hw + 1));
                                var q = P(u, side * (Hw + 2));
                                if (!Solid(p.X, ny, p.Z) || !Solid(q.X, ny, q.Z) || k.R() < 0.25) continue;
                                k.Set(p.X, ny, p.Z, 0);
                                if (k.R() < 0.5) { e.Prop(p.X, ny, p.Z, "skeleton-long", along); e.Dead++; }
                                else if (k.R() < 0.6) { e.Prop(p.X, ny, p.Z, "pile-cloth" + k.RI(1, 5), along); e.Dead++; }
                                else e.Prop(p.X, ny, p.Z, "pile-bones" + k.RI(1, 2), along);
                                e.Niches++;
                            }
                    if (k.R() < 0.25) FloorChest(0, Hw, "collapsed3", RollLoot(k.Rand, GraveLoot, 2), "burial hall");
                    break;
                }
                default:   // a plain crossing
                {
                    if (n.Hx >= 3)
                    {
                        for (int y = Y; y <= Y + 4; y++) k.Set(X, y, Z, e.Mossy);
                        e.Light(X, Y + 2, Z);
                    }
                    else WallLights(Y + 2);
                    if (k.R() < 0.5) { var p = P(1, 1); e.DeadAt(p.X, Y, p.Z, along); }
                    if (k.R() < 0.4) { var p = P(-1, Hw); e.Candles(p.X, Y, p.Z); }
                    if (k.R() < 0.15) FloorChest(Hl, -Hw, "collapsed1", RollLoot(k.Rand, GraveLoot, 2), "catacomb");
                    break;
                }
            }
        }

        // the underground stays out of the catacombs' reach
        e.Deep.Add((cx, cz, (int)rMax + 10));
    }

    // The Warden's Gate: from the gate room a stair climbs out along
    // (dx, dz), half a block a step, roofed while there is room for a roof
    // and turf over it, then an open cut walled above the turf to a gateway
    // of black glass at the top.
    private void EerieGateStair(EerieCtx e, EerieNode n, int dx, int dz)
    {
        var k = e.K;
        int ux = -dz, uz = dx;
        int reach = Math.Max(n.Hx, n.Hz) + 1;
        bool gateBuilt = false;
        for (int i = 0; i < 160; i++)
        {
            int px = n.X + dx * (reach + i), pz = n.Z + dz * (reach + i);
            double s = n.Y + 0.5 * i;
            bool whole = s == Math.Floor(s);
            int lo = whole ? (int)s : (int)Math.Floor(s) + 1;
            int g = int.MaxValue;
            for (int w = -2; w <= 2; w++) g = Math.Min(g, k.Ground(px + ux * w, pz + uz * w));
            bool roofed = lo + 6 <= g;
            for (int w = -2; w <= 2; w++)
            {
                int x = px + ux * w, z = pz + uz * w;
                int gg = k.Ground(x, z);
                if (Math.Abs(w) == 2)
                {
                    int topW = roofed ? lo + 4 : Math.Max(lo + 4, gg + 1);
                    for (int y = lo - 1; y <= topW; y++) k.Set(x, y, z, e.Wall());
                    if (roofed && i % 6 == 3 && w == (i / 6 % 2 == 0 ? 2 : -2)) e.Light(x, lo + 2, z);
                    continue;
                }
                if (whole) k.Set(x, (int)s - 1, z, e.Brick); else k.Set(x, (int)Math.Floor(s), z, e.Slab);
                int top = roofed ? lo + 3 : Math.Max(lo + 3, gg);
                for (int y = lo; y <= top; y++) k.Set(x, y, z, 0);
                if (roofed) k.Set(x, lo + 4, z, e.Brick);
            }
            if (!roofed && !gateBuilt)
            {
                // the gateway where the roof gives out: two pillars of black
                // glass and a lintel, lights set in the pillars
                for (int y = lo - 1; y <= lo + 5; y++)
                    foreach (int w in new[] { -2, 2 }) k.Set(px + ux * w, y, pz + uz * w, e.ObsP);
                for (int w = -2; w <= 2; w++) { k.Set(px + ux * w, lo + 5, pz + uz * w, e.ObsP); k.Set(px + ux * w, lo + 6, pz + uz * w, w == 0 ? e.ObsP : e.Slab); }
                e.Light(px + ux * 2, lo + 3, pz + uz * 2);
                e.Light(px - ux * 2, lo + 3, pz - uz * 2);
                gateBuilt = true;
            }
            if (i % 3 == 0) { e.Used.Add((px, pz, 4)); e.Deep.Add((px, pz, 4)); }
            if (s >= g) { e.Used.Add((px, pz, 7)); break; }
        }
    }

    // ── Buried structures ────────────────────────────────────────────────
    private (bool AlongX, int Low) EerieFootprint(EerieCtx e, int x, int z, int hl, int hw, bool? alongX = null)
    {
        var k = e.K;
        bool ax = alongX ?? k.R() < 0.5;
        int low = int.MaxValue;
        for (int u = -hl - 1; u <= hl + 1; u++)
            for (int w = -hw - 1; w <= hw + 1; w++)
                low = Math.Min(low, k.Ground(x + (ax ? u : w), z + (ax ? w : u)));
        return (ax, low);
    }

    // A family vault: a buried brick chamber under a small obelisk, stone
    // shelves along its long walls with coffins on them.
    private bool EerieVault(EerieCtx e, int x, int z)
    {
        var k = e.K;
        const int hl = 3, hw = 2;
        var (ax, low) = EerieFootprint(e, x, z, hl, hw);
        int top = low - 6, y0 = top - 3;
        (int X, int Z) P(int u, int w) => ax ? (x + u, z + w) : (x + w, z + u);
        for (int u = -hl; u <= hl; u++)
            for (int w = -hw; w <= hw; w++)
            {
                var p = P(u, w);
                if (!e.Diggable(p.X, top, p.Z, 5) || !e.Diggable(p.X, y0, p.Z, 5)) return false;
            }
        for (int u = -hl - 1; u <= hl + 1; u++)
            for (int w = -hw - 1; w <= hw + 1; w++)
                for (int y = y0 - 1; y <= top + 1; y++)
                {
                    var p = P(u, w);
                    bool inside = Math.Abs(u) <= hl && Math.Abs(w) <= hw && y >= y0 && y <= top;
                    if (inside) k.Set(p.X, y, p.Z, 0);
                    else if (e.Safe(p.X, y, p.Z)) k.Set(p.X, y, p.Z, k.Hash01(p.X * 5 + y, p.Z * 3) < 0.35 ? e.Mossy : y == y0 - 1 ? e.Polished : e.Brick);
                }
        double along = ax ? Math.PI / 2 : 0;
        for (int u = -hl; u <= hl; u++)
            foreach (int w in new[] { -hw, hw })
            {
                var p = P(u, w);
                k.Set(p.X, y0, p.Z, e.Polished);
                if ((u + hl) % 2 == 0 && u < hl && k.R() < 0.8) e.Coffin(p.X, y0 + 1, p.Z, along);
            }
        var dp = P(-1, 0); e.DeadAt(dp.X, y0, dp.Z, along);
        var cp = P(-hl, 0); e.Candles(cp.X, y0, cp.Z);
        bool golden = k.R() < 0.25;
        var ch = P(hl, 0);
        e.Chest(ch.X, y0, ch.Z, golden ? "golden-aged" : "normal-aged", RollLoot(k.Rand, VaultLoot, golden ? 4 : 3 + k.RI(0, 2)), "family vault");
        // the marker over it: a short obelisk, a headstone beside
        int g = k.Ground(x, z);
        k.Set(x, g + 1, z, e.Polished); k.Set(x, g + 2, z, e.Polished); k.Set(x, g + 3, z, e.Slab);
        if (k.R() < 0.6) { var hp = P(0, 1); e.Prop(hp.X, k.Ground(hp.X, hp.Z) + 1, hp.Z, "gravestone-" + k.RI(1, 5), along); }
        e.Used.Add((x, z, 5));
        e.Deep.Add((x, z, 7));
        return true;
    }

    // A charnel trench: a long cut packed with the dead and filled in,
    // nothing on the surface but a long low mound.
    private bool EerieTrench(EerieCtx e, int x, int z)
    {
        var k = e.K;
        const int hl = 7, hw = 1;
        var (ax, low) = EerieFootprint(e, x, z, hl, hw);
        int top = low - 4, y0 = top - 2;
        (int X, int Z) P(int u, int w) => ax ? (x + u, z + w) : (x + w, z + u);
        for (int u = -hl; u <= hl; u++)
            for (int w = -hw; w <= hw; w++)
            {
                var p = P(u, w);
                if (!e.Diggable(p.X, top, p.Z, 3) || !e.Diggable(p.X, y0, p.Z, 3)) return false;
            }
        double across = ax ? 0 : Math.PI / 2;
        for (int u = -hl - 1; u <= hl + 1; u++)
            for (int w = -hw - 1; w <= hw + 1; w++)
                for (int y = y0 - 1; y <= top + 1; y++)
                {
                    var p = P(u, w);
                    bool inside = Math.Abs(u) <= hl && Math.Abs(w) <= hw && y >= y0 && y <= top;
                    if (inside) k.Set(p.X, y, p.Z, 0);
                    else if (e.Safe(p.X, y, p.Z)) k.Set(p.X, y, p.Z, e.Dirt);
                }
        var spots = new List<(int X, int Z)>();
        for (int u = -hl; u <= hl; u++)
            for (int w = -hw; w <= hw; w++)
            {
                var p = P(u, w);
                if (k.R() < 0.85) e.DeadAt(p.X, y0, p.Z, across); else spots.Add(p);
            }
        if (spots.Count > 0)
        {
            var c = spots[k.RI(0, spots.Count - 1)];
            e.Chest(c.X, y0, c.Z, "collapsed" + k.RI(1, 3), RollLoot(k.Rand, GraveLoot, 2), "charnel trench");
        }
        for (int u = -hl; u <= hl; u++)
            for (int w = -hw; w <= hw; w++)
            {
                var p = P(u, w);
                if (Math.Abs(u) < hl || k.R() < 0.5) k.Set(p.X, k.Ground(p.X, p.Z) + 1, p.Z, e.Soil);
            }
        e.Used.Add((x, z, hl + 1));
        e.Deep.Add((x, z, hl + 3));
        return true;
    }

    // An ossuary well: a round shaft lined with skulls, open to the sky
    // under a well head and a winding beam, the bottom a heap of bones.
    private bool EerieWell(EerieCtx e, int x, int z)
    {
        var k = e.K;
        int g = k.Ground(x, z), bottom = g - 11;
        for (int dx = -3; dx <= 3; dx++)
            for (int dz = -3; dz <= 3; dz++)
                if (Math.Abs(k.Ground(x + dx, z + dz) - g) > 1) return false;
        for (int dx = -2; dx <= 2; dx++)
            for (int dz = -2; dz <= 2; dz++)
                for (int y = bottom - 1; y <= g; y++)
                    if (!e.Safe(x + dx, y, z + dz)) return false;
        var floor = new List<(int X, int Z)>();
        for (int dx = -3; dx <= 3; dx++)
            for (int dz = -3; dz <= 3; dz++)
            {
                double r = Math.Sqrt(dx * dx + dz * dz);
                if (r > 2.7) continue;
                for (int y = bottom - 1; y <= g + 1; y++)
                {
                    if (r <= 1.5 && y >= bottom) k.Set(x + dx, y, z + dz, 0);
                    else if (y <= g) k.Set(x + dx, y, z + dz, y == bottom - 1 || k.Hash01((x + dx) * 3 + y, (z + dz) * 7) < 0.55 ? e.SkullCobble : e.MossCobble);
                    else if (r > 1.6) k.Set(x + dx, y, z + dz, e.Cobble);   // the well head
                }
                if (r <= 1.5) floor.Add((x + dx, z + dz));
            }
        foreach (var w in new[] { -2, 2 })
            for (int y = g + 2; y <= g + 3; y++) k.Set(x + w, y, z, e.Post);
        for (int w = -2; w <= 2; w++) k.Set(x + w, g + 4, z, e.BeamWE);
        var c = floor[k.RI(0, floor.Count - 1)];
        foreach (var f in floor) if (f != c) e.DeadAt(f.X, bottom, f.Z, k.R() * 6);
        e.Chest(c.X, bottom, c.Z, "collapsed" + k.RI(1, 3), RollLoot(k.Rand, GraveLoot, 3), "ossuary well");
        e.Used.Add((x, z, 4));
        e.Deep.Add((x, z, 4));
        return true;
    }

    // A buried chapel: the downs crept up over it until only a fallen hole
    // in its roof is left; a rubble heap climbs from the nave to the turf.
    private bool EerieChapel(EerieCtx e, int x, int z)
    {
        var k = e.K;
        const int hl = 6, hw = 3, H = 6;
        var (ax, low) = EerieFootprint(e, x, z, hl, hw);
        int y0 = low - 9, top = y0 + H - 1;
        (int X, int Z) P(int u, int w) => ax ? (x + u, z + w) : (x + w, z + u);
        for (int u = -hl; u <= hl; u++)
            for (int w = -hw; w <= hw; w++)
            {
                var p = P(u, w);
                if (!e.Diggable(p.X, top, p.Z, 3) || !e.Diggable(p.X, y0, p.Z, 3)) return false;
            }
        for (int u = -hl - 1; u <= hl + 1; u++)
            for (int w = -hw - 1; w <= hw + 1; w++)
                for (int y = y0 - 1; y <= top + 1; y++)
                {
                    var p = P(u, w);
                    bool inside = Math.Abs(u) <= hl && Math.Abs(w) <= hw && y >= y0 && y <= top;
                    if (inside) k.Set(p.X, y, p.Z, 0);
                    else k.Set(p.X, y, p.Z, y == y0 - 1 ? e.Polished : k.Hash01(p.X * 3 + y, p.Z * 5) < 0.3 ? e.Mossy : e.Brick);
                }
        double toAltar = ax ? Math.PI / 2 : 0;
        // the altar, candles, the statue behind it, a book on a stand
        for (int w = -1; w <= 1; w++)
        {
            var p = P(hl - 1, w);
            k.Set(p.X, y0, p.Z, e.Polished);
            if (w != 0) e.Candles(p.X, y0 + 1, p.Z);
        }
        var bs = P(hl - 1, 0); e.Prop(bs.X, y0 + 1, bs.Z, "bookshelves/bookstand-aged-book-closed", toAltar);
        var st = P(hl, 0); e.Prop(st.X, y0, st.Z, "statue01", toAltar + Math.PI);
        for (int u = -hl + 4; u <= hl - 3; u += 2)
            foreach (int w in new[] { -2, 2 })
            {
                var p = P(u, w);
                e.Prop(p.X, y0, p.Z, "chair-long", toAltar);
            }
        for (int i = 0; i < 3; i++) { var p = P(k.RI(-hl + 3, hl - 2), k.R() < 0.5 ? -1 : 1); e.DeadAt(p.X, y0, p.Z, toAltar); }
        foreach (int side in new[] { -1, 1 }) { var p = P(hl - 1, side * (hw + 1)); e.Light(p.X, y0 + 3, p.Z); }
        var ch = P(hl, -2);
        e.Chest(ch.X, y0, ch.Z, "normal-aged", new List<string> { "randomlore:rfpool-tobias" }.Concat(RollLoot(k.Rand, VaultLoot, 3)).ToList(), "buried chapel");
        // the roof fell in at the west end: a slope of rubble climbs from the
        // middle of the nave, a step at a time, out through the hole and the
        // turf beyond the west wall
        for (int i = 1; i < 40; i++)
        {
            int u = -i, gMax = int.MinValue;
            for (int w = -1; w <= 0; w++)
            {
                var p = P(u, w);
                int g = k.Ground(p.X, p.Z);
                gMax = Math.Max(gMax, g);
                for (int y = y0; y <= y0 + i - 1; y++) k.Set(p.X, y, p.Z, k.R() < 0.5 ? e.Cobble : e.MossCobble);
                // past the second bench from the west end it is open to the sky
                if (u <= -hl + 1)
                    for (int y = y0 + i; y <= Math.Max(g, top + 1); y++) k.Set(p.X, y, p.Z, 0);
            }
            if (y0 + i >= gMax + 1)
            {
                var m = P(u, 0);
                e.Used.Add((m.X, m.Z, 4));
                break;
            }
        }
        e.Used.Add((x, z, hl + 3));
        e.Deep.Add((x, z, hl + 4));
        return true;
    }

    // The grave robbers: tunnels dug from a camp under the downs to two or
    // three of the pits, timber-framed, a hole up to the surface with the
    // spoil heaped round it. One camp keeps the note they left; one pit
    // keeps the one who went ahead with the lantern.
    private int EerieRobbers(EerieCtx e, int want)
    {
        var k = e.K;
        var pits = e.Pits.Where(p => !p.Great && p.Floor.Count >= 8).ToList();
        int made = 0;
        bool noteLeft = false;
        for (int attempt = 0; attempt < want * 4 && made < want && pits.Count >= 2; attempt++)
        {
            var a = pits[k.RI(0, pits.Count - 1)];
            var others = pits.Where(p => p != a)
                .Select(p => (p, d: Math.Sqrt((p.X - a.X) * (double)(p.X - a.X) + (p.Z - a.Z) * (double)(p.Z - a.Z))))
                .Where(t => t.d > 14 && t.d < 70).OrderBy(t => t.d).Take(2).Select(t => t.p).ToList();
            if (others.Count == 0) continue;
            var chain = new List<EeriePit> { a, others[0] };
            if (others.Count > 1 && k.R() < 0.5) chain.Add(others[1]);
            foreach (var p in chain) pits.Remove(p);

            (int X, int Y, int Z) camp = default;
            bool campSet = false;
            for (int c = 0; c + 1 < chain.Count; c++)
            {
                var p = chain[c]; var q = chain[c + 1];
                // an L route, wobbling, floor sloping evenly between the pits
                var path = new List<(int X, int Z)>();
                int x = p.X, z = p.Z;
                bool xFirst = k.R() < 0.5;
                int wob = 0;
                while (x != q.X || z != q.Z)
                {
                    if ((xFirst && x != q.X) || (!xFirst && z == q.Z)) x += Math.Sign(q.X - x); else z += Math.Sign(q.Z - z);
                    if (k.R() < 0.08) wob = Math.Clamp(wob + (k.R() < 0.5 ? -1 : 1), -2, 2);
                    path.Add((x + (xFirst ? 0 : wob), z + (xFirst ? wob : 0)));
                }
                for (int i = 0; i < path.Count; i++)
                {
                    var (tx, tz) = path[i];
                    int y = (int)Math.Round(p.Bottom + (q.Bottom - p.Bottom) * (i / (double)Math.Max(1, path.Count - 1)));
                    for (int dx = 0; dx <= 1; dx++)
                        for (int dz = 0; dz <= 1; dz++)
                            for (int yy = y; yy <= y + 2; yy++)
                                if (e.Diggable(tx + dx, yy, tz + dz, 4)) k.Set(tx + dx, yy, tz + dz, 0);
                    // timber frames every 6
                    if (i % 6 == 3)
                    {
                        bool runX = i + 1 < path.Count ? path[i + 1].X != tx : true;
                        foreach (int side in new[] { -1, 2 })
                        {
                            int fx = tx + (runX ? 0 : side), fz = tz + (runX ? side : 0);
                            for (int yy = y; yy <= y + 2; yy++) if (e.Diggable(fx, yy, fz, 4)) k.Set(fx, yy, fz, e.Post);
                        }
                        for (int s = -1; s <= 2; s++)
                        {
                            int bx = tx + (runX ? 0 : s), bz = tz + (runX ? s : 0);
                            if (e.Diggable(bx, y + 3, bz, 4)) k.Set(bx, y + 3, bz, runX ? e.BeamNS : e.BeamWE);
                        }
                    }
                    if (i % 9 == 5 && k.R() < 0.4) e.Prop(tx, y, tz, "lantern/ground" + k.RI(1, 8), 0);
                    if (i % 6 == 0) e.Deep.Add((tx, tz, 3));
                    if (!campSet && i == path.Count / 2) { camp = (tx, y, tz); campSet = true; }
                }
            }
            if (!campSet) continue;

            // the camp
            for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                    for (int y = camp.Y; y <= camp.Y + 2; y++)
                        if (e.Diggable(camp.X + dx, y, camp.Z + dz, 4)) k.Set(camp.X + dx, y, camp.Z + dz, 0);
            e.Prop(camp.X + 1, camp.Y, camp.Z + 1, "table-ruined" + k.RI(1, 3), 0);
            e.Prop(camp.X + 2, camp.Y, camp.Z + 1, "chair-ruined" + k.RI(1, 3), Math.PI / 2);
            e.Prop(camp.X - 2, camp.Y, camp.Z + 2, "bed/bed-straw-ruined" + k.RI(1, 2), 0);
            e.Prop(camp.X - 2, camp.Y, camp.Z - 2, "sacks" + k.RI(1, 3), 0);
            e.Prop(camp.X, camp.Y, camp.Z - 2, "lantern/ground2", 0);
            var loot = RollLoot(k.Rand, PitLoot, 3);
            loot.Add("game:metalbit-gold*" + (2 + k.RI(0, 3)));
            if (!noteLeft) { loot.Insert(0, "randomlore:rfpool-trade"); noteLeft = true; }
            e.Chest(camp.X + 2, camp.Y, camp.Z - 2, "normal-aged", loot, "robbers' camp");
            // the one who went ahead, with the lantern, in the last pit
            var last = chain[chain.Count - 1];
            if (last.Floor.Count > 0)
            {
                var near = last.Floor.OrderBy(f => Math.Abs(f.X - camp.X) + Math.Abs(f.Z - camp.Z)).First();
                e.Prop(near.X, last.Bottom, near.Z, "skeleton-long", 0); e.Dead++;
            }

            // the hole up to the downs, a step up for every step along, the
            // spoil heaped round its mouth
            int hdx = k.R() < 0.5 ? (k.R() < 0.5 ? 1 : -1) : 0, hdz = hdx == 0 ? (k.R() < 0.5 ? 1 : -1) : 0;
            for (int i = 1; i < 40; i++)
            {
                int hx = camp.X + hdx * (2 + i), hz = camp.Z + hdz * (2 + i), fy = camp.Y + i;
                int g = Math.Min(k.Ground(hx, hz), k.Ground(hx + hdz, hz + hdx));
                for (int w = 0; w <= 1; w++)
                {
                    int x = hx + hdz * w, z = hz + hdx * w;
                    if (!e.Safe(x, fy, z)) continue;
                    for (int y = fy; y <= Math.Min(fy + 2, Math.Max(fy, g)); y++) k.Set(x, y, z, 0);
                }
                if (fy >= g)
                {
                    foreach (var (sx, sz) in new[] { (hdz * -1, hdx * -1), (hdz * 2, hdx * 2), (hdx, hdz) })
                    {
                        int x = hx + sx, z = hz + sz;
                        k.Set(x, k.Ground(x, z) + 1, z, e.Dirt);
                    }
                    e.Used.Add((hx, hz, 4));
                    break;
                }
            }
            made++;
        }
        return made;
    }

    // ── The graves ───────────────────────────────────────────────────────
    // Rows of single graves, most of them near a marker: a mound, a
    // headstone, and below it a coffin, a chest of grave goods, a bare
    // skeleton, or an open grave the earth was never put back on.
    private bool EerieGrave(EerieCtx e, int x, int z, bool ax)
    {
        var k = e.K;
        int g = k.Ground(x, z);
        int dx2 = ax ? 0 : 1, dz2 = ax ? 1 : 0;
        if (g < k.Sea + 1 || !e.Free(x, z, 1) || Math.Abs(k.Ground(x + dx2, z + dz2) - g) > 1 || Math.Abs(k.Ground(x - dx2, z - dz2) - g) > 1) return false;
        double lie = ax ? 0 : Math.PI / 2;
        double what = k.R();
        if (what < 0.08)
        {
            // open: dug and left, the coffin open in the hole, the earth beside
            for (int m = 0; m <= 1; m++) { k.Set(x + dx2 * m, g, z + dz2 * m, 0); k.Set(x + dx2 * m, g - 1, z + dz2 * m, 0); k.Set(x + dx2 * m, g - 2, z + dz2 * m, e.Dirt); }
            k.Clutter(x, g - 1, z, "clutter|coffin-rot-open", lie); e.Coffins++;
            int sx = x + (ax ? 1 : 0), sz = z + (ax ? 0 : 1);
            k.Set(sx, k.Ground(sx, sz) + 1, sz, e.Dirt);
            return true;
        }
        // the mound
        for (int m = 0; m <= 1; m++) { k.Set(x + dx2 * m, g, z + dz2 * m, e.Dirt); k.Set(x + dx2 * m, g + 1, z + dz2 * m, e.Soil); }
        int hx = x - dx2, hz = z - dz2;
        // the headstone stands on its own ground, which may sit a step below the mound's
        int hy = k.Ground(hx, hz) + 1;
        k.Set(hx, hy, hz, 0);   // clear tallgrass, or the clutter commit skips the cell
        double hs = k.R();
        if (hs < 0.45) e.Prop(hx, hy, hz, "gravestone-" + k.RI(1, 5), lie);
        else if (hs < 0.85)
        {
            k.Set(hx, hy, hz, e.HeadStone);
            if (k.R() < 0.6) k.Set(hx, hy + 1, hz, e.HeadSlab);
        }
        if (k.R() < 0.06) e.Candles(x + dx2, g + 2, z + dz2);
        if (what < 0.2 && e.Chest(x, g - 1, z, "collapsed" + k.RI(1, 3), RollLoot(k.Rand, GraveLoot, 2 + k.RI(0, 2)), "grave"))
        {
            k.Set(x, g - 1, z, 0); k.Set(x, g - 2, z, e.Dirt);
        }
        else if (what < 0.32)
        {
            k.Set(x, g - 1, z, e.Dirt); k.Set(x, g - 2, z, 0); k.Set(x, g - 3, z, e.Dirt);
            e.Prop(x, g - 2, z, "skeleton-long", lie); e.Dead++;
        }
        else
        {
            k.Set(x, g - 1, z, e.Dirt); k.Set(x, g - 2, z, 0); k.Set(x, g - 3, z, e.Dirt);
            e.Coffin(x, g - 2, z, lie);
        }
        return true;
    }

    private int EerieGraves(EerieCtx e, int nGraves)
    {
        var k = e.K;
        int made = 0;
        for (int tries = 0; made < nGraves && tries < nGraves * 6; tries++)
        {
            (int X, int Z)? s = null;
            if (e.Markers.Count > 0 && k.R() < 0.6)
            {
                var m = e.Markers[k.RI(0, e.Markers.Count - 1)];
                s = e.SpotNear(m.X, m.Z, 7, 28, 4);
            }
            s ??= e.Spot(16, e.R, 4);
            if (s == null) continue;
            int bx = s.Value.X, bz = s.Value.Z;
            bool ax = k.R() < 0.5;
            int len = 3 + k.RI(0, 5);
            for (int j = 0; j < len && made < nGraves; j++)
                if (EerieGrave(e, bx + (ax ? j * 3 : 0), bz + (ax ? 0 : j * 3), ax)) made++;
            e.Used.Add((bx + (ax ? len * 3 / 2 : 0), bz + (ax ? 0 : len * 3 / 2), len * 3 / 2 + 1));
        }
        return made;
    }

    // A walled family plot: a low wall of cobble with a gap, four graves in
    // a row and a small obelisk at their feet.
    private bool EeriePlot(EerieCtx e, int x, int z)
    {
        var k = e.K;
        const int hl = 4, hw = 3;
        bool ax = k.R() < 0.5;
        (int X, int Z) P(int u, int w) => ax ? (x + u, z + w) : (x + w, z + u);
        int g0 = k.Ground(x, z);
        for (int u = -hl; u <= hl; u++)
            for (int w = -hw; w <= hw; w++)
            {
                var p = P(u, w);
                if (Math.Abs(k.Ground(p.X, p.Z) - g0) > 1 || !e.Dry(p.X, p.Z)) return false;
            }
        for (int u = -hl; u <= hl; u++)
            for (int w = -hw; w <= hw; w++)
            {
                if (Math.Abs(u) != hl && Math.Abs(w) != hw) continue;
                if (u == 0 && w == hw) continue;   // the gap
                var p = P(u, w);
                k.Set(p.X, k.Ground(p.X, p.Z) + 1, p.Z, k.R() < 0.4 ? e.MossCobble : e.Cobble);
            }
        // the graves run across the plot (along w), heads to the far wall
        int made = 0;
        foreach (int u in new[] { -3, -1, 1, 3 })
        {
            var p = P(u, -1);
            if (EerieGrave(e, p.X, p.Z, ax)) made++;
        }
        var o = P(0, 2);
        int og = k.Ground(o.X, o.Z);
        if (k.R() < 0.15) e.Prop(o.X, og + 1, o.Z, "statue01", 0);
        else { k.Set(o.X, og + 1, o.Z, e.Polished); k.Set(o.X, og + 2, o.Z, e.Polished); k.Set(o.X, og + 3, o.Z, e.Slab); }
        e.Used.Add((x, z, hl + 2));
        return made > 0;
    }

    // ── Loot ─────────────────────────────────────────────────────────────
    private static readonly (string Code, int Min, int Max, double Weight)[] GraveLoot =
    {
        ("game:bone", 2, 6, 2.0),
        ("game:candle", 1, 3, 1.5),
        ("game:metalbit-silver", 2, 6, 0.8),
        ("game:metalbit-gold", 1, 3, 0.5),
        ("game:gear-rusty", 1, 2, 1.0),
        ("game:flaxtwine", 1, 4, 1.0),
        ("game:gem-emerald-rough", 1, 1, 0.3),
        ("game:gem-diamond-rough", 1, 1, 0.2),
        ("game:clearquartz", 1, 3, 0.8),
        ("game:jonasparts-valve01", 1, 1, 0.4),
    };

    // what the dead of the works and towns had on them
    private static readonly (string Code, int Min, int Max, double Weight)[] PitLoot =
    {
        ("game:bone", 2, 6, 1.5),
        ("game:gear-rusty", 1, 2, 1.6),
        ("game:metalnailsandstrips-iron", 2, 6, 1.0),
        ("game:metalbit-copper", 2, 6, 1.0),
        ("game:metalbit-silver", 1, 3, 0.5),
        ("game:candle", 1, 2, 0.8),
        ("game:flaxtwine", 1, 3, 1.0),
        ("game:clearquartz", 1, 2, 0.5),
        ("game:jonasparts-valve01", 1, 1, 0.3),
        ("game:jonasparts-connector01", 1, 1, 0.3),
    };

    // the wardens' stores
    private static readonly (string Code, int Min, int Max, double Weight)[] WardenLoot =
    {
        ("game:candle", 2, 6, 2.0),
        ("game:flaxtwine", 2, 6, 1.2),
        ("game:bone", 2, 8, 1.0),
        ("game:metalbit-copper", 2, 5, 0.8),
        ("game:metalnailsandstrips-iron", 2, 6, 0.8),
        ("game:clearquartz", 1, 2, 0.4),
    };

    // sealed vaults: what the better-off took down with them
    private static readonly (string Code, int Min, int Max, double Weight)[] VaultLoot =
    {
        ("game:metalbit-gold", 2, 5, 1.0),
        ("game:metalbit-silver", 3, 8, 1.2),
        ("game:gem-emerald-rough", 1, 2, 0.4),
        ("game:gem-diamond-rough", 1, 1, 0.25),
        ("game:clearquartz", 2, 4, 0.8),
        ("game:gear-temporal", 1, 1, 0.15),
        ("game:jonasparts-tank01", 1, 1, 0.3),
        ("game:candle", 2, 4, 0.6),
    };
}
