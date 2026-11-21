using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// Rustfall's breadcrumbs and set dressing:
//  - island maps (landmassgenerator:islandmap-*): use one and the plan
//    island it names gets a red X on your world map, like a Better Ruins or
//    Great Machine locator map;
//  - loot tables for chests the structures place themselves;
//  - a rust trail painted along a cave floor down to a chest (trail=rust);
//  - rusted machinery set into a mine's walls (cave machinery=N) and giant
//    spawners in its widest halls (cave giants=<block>*N);
//  - story chests: items added to a chest inside a vanilla story structure
//    once the game has generated it (plan line `storychest`).
public partial class LandmassGeneratorModSystem
{
    public override void Start(ICoreAPI api)
    {
        api.RegisterItemClass("LandmassGenerator.ItemIslandMap", typeof(ItemIslandMap));
    }

    // Centre of the first plan island built from this shape, at sea level.
    public Vec3d FindPlanIsland(string shape)
    {
        if (_wgIslandJobs == null || string.IsNullOrEmpty(shape)) return null;
        var job = _wgIslandJobs.FirstOrDefault(j => string.Equals(j.ShapeName, shape, StringComparison.OrdinalIgnoreCase));
        return job == null ? null : new Vec3d(job.Cx + 0.5, job.SeaLevel, job.Cz + 0.5);
    }

    // Centre of a story structure (lazaret, tobiascave...), at sea level.
    public Vec3d FindStoryLocation(string code)
    {
        var loc = sapi.ModLoader.GetModSystem<GenStoryStructures>()?.Structures.Get(code);
        return loc?.CenterPos == null ? null : new Vec3d(loc.CenterPos.X + 0.5, sapi.World.SeaLevel, loc.CenterPos.Z + 0.5);
    }

    // Where an island map's attributes point: a story structure
    // (storyLocation) or a plan island (islandShape).
    public Vec3d FindMapTarget(Vintagestory.API.Datastructures.JsonObject attrs)
    {
        string story = attrs?["storyLocation"]?.AsString();
        return story != null ? FindStoryLocation(story) : FindPlanIsland(attrs?["islandShape"]?.AsString());
    }

    public class MapTarget
    {
        public string Key, Label;
        public Vec3d Pos;
    }

    // Every place a worn chart may show: each island map's place (except
    // the ones marked randomPool: false, the starter's own waters) and each
    // Better Ruins island of the plan's `ruins` line.
    public List<MapTarget> MapTargets()
    {
        var list = new List<MapTarget>();
        foreach (Item item in sapi.World.Items)
        {
            if (item?.Code == null || item.Code.Domain != "landmassgenerator" || !item.Code.Path.StartsWith("islandmap-")) continue;
            if (item.Attributes?["randomTarget"]?.AsBool() == true || item.Attributes?["randomPool"]?.AsBool(true) == false) continue;
            Vec3d pos = FindMapTarget(item.Attributes);
            if (pos != null) list.Add(new MapTarget { Key = item.Code.Path, Label = Lang.Get(item.Attributes["waypointText"].AsString(item.Code.Path)), Pos = pos });
        }
        foreach (var job in _wgIslandJobs ?? new List<IslandJob>())
            if (job.Opt != null && job.Opt.TryGetValue("ruin", out string ruin))
                list.Add(new MapTarget { Key = $"ruin:{job.Cx},{job.Cz}", Label = RuinName(ruin), Pos = new Vec3d(job.Cx + 0.5, job.SeaLevel, job.Cz + 0.5) });
        return list;
    }

    // A worn chart's pick: a place this player (if any) has not marked yet,
    // at least 600 blocks from where they stand.
    public MapTarget PickMapTarget(IServerPlayer plr, Vec3d from)
    {
        var layer = sapi.ModLoader.GetModSystem<WorldMapManager>()?.MapLayers.FirstOrDefault(ml => ml is WaypointMapLayer) as WaypointMapLayer;
        var marked = plr == null ? new List<Vec3d>()
            : layer?.Waypoints.Where(wp => wp.OwningPlayerUid == plr.PlayerUID).Select(wp => wp.Position).ToList() ?? new List<Vec3d>();
        var left = MapTargets().Where(t => t.Pos.HorizontalSquareDistanceTo(from) > 600 * 600
            && !marked.Any(m => Math.Abs(m.X - t.Pos.X) < 2 && Math.Abs(m.Z - t.Pos.Z) < 2)).ToList();
        return left.Count == 0 ? null : left[sapi.World.Rand.Next(left.Count)];
    }

    // megastructures-houseofusher-virrno: "The House of Usher"
    private static readonly Dictionary<string, string> RuinNames = new()
    {
        ["oasis"] = "The Oasis", ["auldburg"] = "Auldburg", ["romanbathhouse"] = "The Roman Bathhouse", ["wallsegment"] = "The Old Wall",
        ["houseofusher"] = "The House of Usher", ["normancastle"] = "The Norman Castle", ["castlewalls"] = "The Castle Walls",
        ["visingsborg"] = "Visingsborg", ["destroyedlargechurch"] = "The Ruined Church", ["palaceofnestor"] = "The Palace of Nestor",
        ["forlornhopetower"] = "The Forlorn Hope Tower", ["rockformation"] = "The Rock Formation", ["settlementofkeykeepers"] = "The Keykeepers' Settlement",
        ["jumblerockcastle"] = "Jumblerock Castle", ["wolfstonecastle"] = "Wolfstone Castle", ["aqueduct"] = "The Aqueduct",
        ["undergroundlibrary"] = "The Underground Library", ["giantcathedral"] = "The Cathedral",
    };

    private static string RuinName(string code)
    {
        string[] parts = code.Split('-');
        string family = parts.Length > 1 && parts[0] == "megastructures" ? parts[1] : parts[0];
        return RuinNames.TryGetValue(family, out string name) ? name
            : "The " + char.ToUpperInvariant(family[0]) + family.Substring(1) + " Ruins";
    }

    // Settings on a placed block's entity (a spawner's trigger radius...):
    // read its attributes, change the named ones, read them back. Numbers
    // go in as floats, true/false as booleans; mods read their own keys.
    private void SetBlockEntitySettings(BlockPos pos, Dictionary<string, string> attrs)
    {
        var be = sapi.World.BlockAccessor.GetBlockEntity(pos);
        if (be == null || attrs == null || attrs.Count == 0) return;
        var tree = new Vintagestory.API.Datastructures.TreeAttribute();
        be.ToTreeAttributes(tree);
        foreach (var kv in attrs)
        {
            if (bool.TryParse(kv.Value, out bool b)) tree.SetBool(kv.Key, b);
            else if (float.TryParse(kv.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f)) tree.SetFloat(kv.Key, f);
            else tree.SetString(kv.Key, kv.Value);
        }
        be.FromTreeAttributes(tree, sapi.World);
        be.MarkDirty(true);
    }

    // ── loot ───────────────────────────────────────────────────────────

    private static readonly (string Code, int Min, int Max, double Weight)[] MachineLoot =
    {
        ("game:gear-rusty", 2, 6, 3.0),
        ("game:metalnailsandstrips-iron", 3, 8, 2.0),
        ("game:metalnailsandstrips-copper", 3, 8, 1.2),
        ("game:metalplate-iron", 1, 2, 1.0),
        ("game:metalchain-iron", 1, 2, 1.0),
        ("game:jonasparts-connector01", 1, 1, 0.8),
        ("game:jonasparts-cylinder01", 1, 1, 0.8),
        ("game:jonasparts-cylinder02", 1, 1, 0.6),
        ("game:jonasparts-tank01", 1, 1, 0.6),
        ("game:jonasparts-valve01", 1, 1, 0.8),
        ("game:jonasparts-pumphead", 1, 1, 0.5),
        ("game:woodenaxle-ud", 2, 6, 1.0),
        ("game:angledgears-s", 1, 2, 0.8),
        ("game:metalbit-iron", 4, 12, 1.0),
    };

    private static readonly (string Code, int Min, int Max, double Weight)[] HutLoot =
    {
        ("game:gear-rusty", 1, 3, 1.5),
        ("game:nugget-cassiterite", 4, 14, 2.0),
        ("game:metalbit-tinbronze", 3, 9, 1.2),
        ("game:ingot-tin", 1, 2, 0.6),
        ("game:metalnailsandstrips-tinbronze", 2, 6, 1.0),
        ("game:flaxtwine", 2, 6, 1.4),
        ("game:rope", 1, 3, 1.0),
        ("game:candle", 1, 4, 1.2),
        ("game:metalchain-copper", 1, 1, 0.6),
        ("game:jonasparts-valve01", 1, 1, 0.5),
        ("game:jonasparts-connector01", 1, 1, 0.5),
        ("game:cattailtops", 4, 12, 0.8),
        ("game:firewood", 4, 12, 1.0),
        ("game:metal-scraps", 1, 3, 0.8),
    };

    // `picks` distinct entries drawn by weight, each with a random count,
    // as chest specs ("game:gear-rusty*4").
    private static List<string> RollLoot(CaveRand rand, (string Code, int Min, int Max, double Weight)[] pool, int picks)
    {
        var left = pool.ToList();
        var outList = new List<string>();
        for (int i = 0; i < picks && left.Count > 0; i++)
        {
            double total = left.Sum(e => e.Weight), r = rand.NextDouble() * total;
            int k = 0;
            for (; k < left.Count - 1; k++) { r -= left[k].Weight; if (r <= 0) break; }
            var e = left[k];
            left.RemoveAt(k);
            int n = e.Min + (int)(rand.NextDouble() * (e.Max - e.Min + 1));
            outList.Add(n > 1 ? $"{e.Code}*{n}" : e.Code);
        }
        return outList;
    }

    // Fill a chest block entity's empty slots with these specs; returns how
    // many stacks went in.
    private int FillChest(BlockEntityGenericTypedContainer be, IEnumerable<string> specs, List<string> notes)
    {
        int put = 0, slot = 0;
        foreach (string spec in specs)
        {
            ItemStack stack = MakeChestStack(spec, notes);
            if (stack == null) continue;
            while (slot < be.Inventory.Count && !be.Inventory[slot].Empty) slot++;
            if (slot >= be.Inventory.Count) { notes.Add("chest full"); break; }
            be.Inventory[slot].Itemstack = stack;
            be.Inventory[slot].MarkDirty();
            slot++;
            put++;
        }
        be.MarkDirty(true);
        return put;
    }

    // ── rust trail ─────────────────────────────────────────────────────

    // From the chest back up to the cave's mouth through the carved air
    // (shortest path), faint rust traces along the way and the odd streak
    // down a wall. Decor only: nothing is mined or changed.
    private int RustTrail(IslandJob job, CaveDef cave, BlockPos chest)
    {
        if (job.CaveSteps == null) return 0;
        var steps = job.CaveSteps.Where(s => s.Def == cave).ToList();
        if (steps.Count == 0) return 0;
        Block floorRust = sapi.World.GetBlock(new AssetLocation("game", "oxidation-rust-normal"));
        Block dripRust = sapi.World.GetBlock(new AssetLocation("game", "oxidation-rust-dripping")) ?? floorRust;
        if (floorRust == null) return 0;
        var world = sapi.World.BlockAccessor;
        var p = new BlockPos(0, 0, 0, job.Dim);

        int x1 = int.MaxValue, y1 = int.MaxValue, z1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue, z2 = int.MinValue;
        foreach (var s in steps)
        {
            int r = (int)Math.Ceiling(Math.Max(s.R, s.V)) + 4;
            x1 = Math.Min(x1, (int)s.X - r); x2 = Math.Max(x2, (int)s.X + r);
            y1 = Math.Min(y1, (int)s.Y - r); y2 = Math.Max(y2, (int)s.Y + r);
            z1 = Math.Min(z1, (int)s.Z - r); z2 = Math.Max(z2, (int)s.Z + r);
        }
        bool Open(int x, int y, int z)
        {
            if (x < x1 || x > x2 || y < y1 || y > y2 || z < z1 || z > z2) return false;
            p.Set(x, y, z);
            return (world.GetBlock(p, BlockLayersAccess.SolidBlocks)?.Id ?? 1) == 0
                && (world.GetBlock(p, BlockLayersAccess.Fluid)?.Id ?? 1) == 0;
        }
        bool SolidTop(int x, int y, int z)
        {
            p.Set(x, y, z);
            var b = world.GetBlock(p, BlockLayersAccess.SolidBlocks);
            return b != null && b.Id != 0 && b.SideSolid[BlockFacing.UP.Index];
        }
        long Key(int x, int y, int z) => ((long)(x - x1) << 40) | ((long)(y - y1) << 20) | (long)(z - z1);

        var mouth = steps[0];
        var prev = new Dictionary<long, long>();
        var queue = new Queue<(int X, int Y, int Z)>();
        long startKey = Key(chest.X, chest.Y, chest.Z);
        prev[startKey] = startKey;
        queue.Enqueue((chest.X, chest.Y, chest.Z));
        (int X, int Y, int Z)? goal = null;
        while (queue.Count > 0 && prev.Count < 600000)
        {
            var c = queue.Dequeue();
            double dx = c.X + 0.5 - mouth.X, dy = c.Y + 0.5 - mouth.Y, dz = c.Z + 0.5 - mouth.Z;
            if (dx * dx + dy * dy + dz * dz <= 9) { goal = c; break; }
            foreach (var f in BlockFacing.ALLFACES)
            {
                int nx = c.X + f.Normali.X, ny = c.Y + f.Normali.Y, nz = c.Z + f.Normali.Z;
                long k = Key(nx, ny, nz);
                if (prev.ContainsKey(k) || !Open(nx, ny, nz)) continue;
                prev[k] = Key(c.X, c.Y, c.Z);
                queue.Enqueue((nx, ny, nz));
            }
        }
        if (goal == null) return 0;

        // The path, mouth first.
        var path = new List<(int X, int Y, int Z)>();
        var cur = goal.Value;
        while (true)
        {
            path.Add(cur);
            long ck = Key(cur.X, cur.Y, cur.Z);
            if (ck == startKey) break;
            long pk = prev[ck];
            cur = ((int)(pk >> 40) + x1, (int)((pk >> 20) & 0xFFFFF) + y1, (int)(pk & 0xFFFFF) + z1);
        }

        // Traces, not a path: a stain only every few steps, drifting from
        // one side of the tunnel to the other along a slow wave, mostly the
        // thin streaked rust variants with now and then a fuller patch, and
        // here and there a three-block streak running down a wall. Thicker
        // toward the chest, as if whatever left it bled more the further in
        // it went.
        Block[] thin = new[] { "oxidation-rust-3tall-top", "oxidation-rust-3tall-mid", "oxidation-rust-3tall-bot" }
            .Select(c => sapi.World.GetBlock(new AssetLocation("game", c))).Where(b => b != null).ToArray();
        if (thin.Length == 0) thin = new[] { floorRust };
        Block wTop = sapi.World.GetBlock(new AssetLocation("game", "oxidation-rust-3tall-top")) ?? dripRust;
        Block wMid = sapi.World.GetBlock(new AssetLocation("game", "oxidation-rust-3tall-mid")) ?? dripRust;
        Block wBot = sapi.World.GetBlock(new AssetLocation("game", "oxidation-rust-3tall-bot")) ?? dripRust;
        var rnd = new Random(chest.X * 31 + chest.Z * 17);
        double phase = rnd.NextDouble() * Math.PI * 2;
        int count = 0, lastWall = -100;
        for (int i = 0; i < path.Count; i++)
        {
            var c = path[i];
            double f = i / (double)Math.Max(1, path.Count - 1);     // 0 at the mouth, 1 at the chest
            if (rnd.NextDouble() > 0.10 + 0.14 * f) continue;
            // sideways drift across the direction of travel
            var nx = path[Math.Min(path.Count - 1, i + 2)];
            int dx = Math.Sign(nx.X - c.X), dz = Math.Sign(nx.Z - c.Z);
            int sx = dz != 0 ? 1 : 0, sz = dx != 0 && dz == 0 ? 1 : 0;
            int drift = (int)Math.Round(1.6 * Math.Sin(i * 0.11 + phase) + (rnd.NextDouble() - 0.5));
            int px = c.X + sx * drift, pz = c.Z + sz * drift;
            int fy = c.Y;
            if (!Open(px, fy, pz)) { px = c.X; pz = c.Z; }
            while (fy > c.Y - 6 && Open(px, fy - 1, pz)) fy--;
            if (!SolidTop(px, fy - 1, pz)) continue;
            p.Set(px, fy - 1, pz);
            Block b = rnd.NextDouble() < 0.12 ? floorRust : thin[rnd.Next(thin.Length)];
            if (world.SetDecor(b, p, BlockFacing.UP)) count++;

            // a streak down the nearest wall, rarely
            if (i - lastWall < 25 || rnd.NextDouble() > 0.35) continue;
            foreach (var face in BlockFacing.HORIZONTALS)
            {
                int wx = px, wz = pz, reach = 0;
                while (reach < 5 && Open(wx + face.Normali.X, fy, wz + face.Normali.Z)) { wx += face.Normali.X; wz += face.Normali.Z; reach++; }
                int bx = wx + face.Normali.X, bz = wz + face.Normali.Z;
                bool wall = true;
                for (int dy = 0; dy < 3 && wall; dy++)
                {
                    p.Set(bx, fy + dy, bz);
                    var wb = world.GetBlock(p, BlockLayersAccess.SolidBlocks);
                    wall = wb != null && wb.Id != 0 && wb.SideSolid[face.Opposite.Index] && Open(wx, fy + dy, wz);
                }
                if (!wall) continue;
                p.Set(bx, fy, bz); if (world.SetDecor(wBot, p, face.Opposite)) count++;
                p.Set(bx, fy + 1, bz); if (world.SetDecor(wMid, p, face.Opposite)) count++;
                p.Set(bx, fy + 2, bz); if (world.SetDecor(wTop, p, face.Opposite)) count++;
                lastWall = i;
                break;
            }
        }
        return count;
    }

    // ── mine machinery and giants ──────────────────────────────────────

    // machinery=N: every N steps, a stretch of tunnel wall is rusted plate
    // with a rusted machine standing against it, a rusty pipe up the wall
    // beside it and scrap on the floor.
    // giants=<block>*K: K giant spawners in caverns on level stretches of the
    // deep part of the mine; deepgiant=<block>: one in a cavern at the very
    // bottom; mouthgiant=<block>: one buried under open ground near the mouth.
    //
    // The props come from tools/clutter-catalog (every clutter type with its
    // measured colour and hitbox). The first set mixed clean grey gears with
    // pale tin-bronze ceiling pipes ("-aged" is tarnished tin-bronze, not rust,
    // and those pipes run 3 or 4 blocks with a hitbox on the first only).
    // Now everything is dark rusty iron (brightness about 0.2) with a hitbox
    // that matches its model; the grey gearbox and gear wear the rusty
    // texture flip. Since 0.72.2 (Michael picked sets A and B of
    // export/pipe-compare): the wall pipe comes out of the wall at the
    // bottom and bends back in at the top, and a rusty pipe runs along the
    // roof corner over many machines, one hitbox per block.
    private static readonly (string Block, string Type, string Flip, bool Flat)[] MineStanding =
    {
        ("clutter-devastation", "gearbox1", "slightlyrustyiron", false),
        ("clutter-devastation", "gear24", "rustysheet", true),
        ("clutter-devastation", "junktanksmall1", null, false),
        ("clutter-devastation", "junktanksmall2", null, false),
        ("clutter", "barrel-metal1", null, false),
        ("clutter", "barrel-metal3", null, false),
        ("clutter", "machine4", null, false),
        ("clutter", "machine6", null, false),
        ("clutter", "machine7", null, false),
        ("clutter", "mech-part5", null, false),
    };
    private static readonly string[] MineWallHung = { "wallhook-mechsection", "wallhook-arm2" };
    private static readonly string[] MineFloorScrap = { "scrap-art10", "chains/chain-floor", "machine2", "pile-precisiontools" };

    private string PlaceMineMachinery(IslandJob job, CaveDef def, List<(double X, double Y, double Z, double R, double V, CaveDef Def)> steps,
        System.Func<int, int, int, bool> open, System.Func<int, int, int, bool> solid)
    {
        var world = sapi.World.BlockAccessor;
        var p = new BlockPos(0, 0, 0, job.Dim);
        int Bid(string code) => sapi.World.GetBlock(new AssetLocation(code.Contains(':') ? code : "game:" + code))?.BlockId ?? 0;
        int rustA = Bid("metalblock-corroded-riveted-rusty-iron"), rustB = Bid("metalblock-corroded-plain-rusty-iron");
        int[] floorJunk = { Bid("loosegears-1"), Bid("loosegears-3"), Bid("metalpartpile-small"), Bid("metalpartpile-tiny"), Bid("metal-scraps") };
        var rotField = typeof(BEBehaviorShapeFromAttributes).GetField("<rotateY>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        void Put(int x, int y, int z, int id) { if (id == 0) return; p.Set(x, y, z); world.SetBlock(id, p); }
        // A clutter prop: its shape, its turn, and optionally a texture flip
        // and a push toward a wall (in blocks; its hitbox moves with it).
        bool Clut(string block, int x, int y, int z, string type, float rot, string flip = null, float ox = 0, float oz = 0, float oy = 0)
        {
            int id = Bid(block);
            if (id == 0 || !open(x, y, z)) return false;
            p.Set(x, y, z);
            world.SetBlock(id, p);
            var beh = world.GetBlockEntity(p)?.GetBehavior<BEBehaviorShapeFromAttributes>();
            if (beh == null) return false;
            beh.Type = type;
            rotField?.SetValue(beh, rot);
            if (flip != null) beh.overrideTextureCode = flip;
            beh.offsetX = ox; beh.offsetY = oy; beh.offsetZ = oz;
            beh.Blockentity.MarkDirty(true);
            return true;
        }

        int machines = 0, giants = 0;
        var rnd = new Random((int)(def.Seed * 7919u + 17u));
        int FloorUnder(int x, int y, int z, int depth) { int f = y; while (f > y - depth && open(x, f - 1, z)) f--; return f; }

        // every step's floor, mouth first
        var spots = new List<(int Index, int X, int Floor, int Z)>();
        var floorAt = new int[steps.Count];
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            int cx = (int)Math.Floor(s.X), cz = (int)Math.Floor(s.Z), cy = (int)Math.Floor(s.Y);
            floorAt[i] = open(cx, cy, cz) ? FloorUnder(cx, cy, cz, 12) : int.MinValue;
            if (i == 0 || floorAt[i] == int.MinValue) continue;
            var a = steps[i - 1];
            double dx = s.X - a.X, dz = s.Z - a.Z;
            if (Math.Sqrt(dx * dx + dz * dz) is < 0.2 or > 4) continue;
            int floor = FloorUnder(cx, cy, cz, 10);
            if (solid(cx, floor - 1, cz)) spots.Add((i, cx, floor, cz));
        }

        // The mine as a tree of its steps: each step joins the one before it,
        // and a branch's first step (a jump of more than 4 blocks) joins the
        // nearest earlier step, where it forked off.
        var parent = new int[steps.Count];
        var kids = new List<int>[steps.Count];
        for (int i = 0; i < steps.Count; i++) kids[i] = new List<int>();
        if (steps.Count > 0) parent[0] = -1;
        for (int i = 1; i < steps.Count; i++)
        {
            double D2(int j) { double ex = steps[i].X - steps[j].X, ey = steps[i].Y - steps[j].Y, ez = steps[i].Z - steps[j].Z; return ex * ex + ey * ey + ez * ez; }
            parent[i] = i - 1;
            if (D2(i - 1) > 16)
            {
                double best = double.MaxValue;
                for (int j = 0; j < i - 1; j++) { double d = D2(j); if (d < best) { best = d; parent[i] = j; } }
            }
            kids[parent[i]].Add(i);
        }

        // ── giant spawners ──
        // A giant needs room to appear: Oversized Horrors spawns one only
        // where its hitbox plus 3 blocks on every side and above is clear (a
        // deep shiver: an 11 x 11 x 7 box). It also needs room to move, and
        // the game's pathfinder must agree. A deep shiver is 4.8 blocks wide
        // and 4 tall, and the mine's tunnels narrow below that. The first
        // lairs were flat-floored rooms on the tunnel, with a slab of stone
        // bridging any dip under them, and they held their giants for good.
        // On the mine's dive (43 to 50 degrees) a body that wide sweeps a
        // band 9 blocks tall and the pathfinder wants 12 of headroom, so no
        // reasonable cut lets one climb it. Each spawner now sleeps under a
        // cavern cut beside a LEVEL stretch of tunnel, a run 7 wide and 8 tall
        // is cut along the tunnel from it for as far as the floor stays level
        // (up to 70 steps each way), and its trigger range stays inside that
        // stretch and under the ground above: it wakes for a player who is
        // down there with it and can be reached, never for one on the dive
        // above it or walking the island.
        var notes = new List<string>();
        int stoneId = job.StoneId;
        var lairs = new List<(int X, int Z)>();
        var runSteps = new HashSet<int>();
        bool InCavern(int x, int z) => lairs.Any(l => (l.X - x) * (l.X - x) + (l.Z - z) * (l.Z - z) < 9 * 9);
        Cuboidf GiantBox(string spawnerCode)
        {
            int dash = spawnerCode.IndexOf("giantspawner-", StringComparison.Ordinal);
            if (dash < 0) return null;
            string domain = spawnerCode.Contains(':') ? spawnerCode.Substring(0, spawnerCode.IndexOf(':')) : "game";
            var et = sapi.World.GetEntityType(new AssetLocation(domain, "giant" + spawnerCode.Substring(dash + "giantspawner-".Length)));
            if (et == null) return null;
            float half = et.CollisionBoxSize.X / 2f + 3f;
            return new Cuboidf(-half, 0, -half, half, et.CollisionBoxSize.Y + 3f, half);
        }
        bool RoomFor(Cuboidf box, int x, int y, int z) =>
            box == null || !sapi.World.CollisionTester.IsColliding(world, box, new Vec3d(x + 0.5, y, z + 0.5), false);
        bool FluidAt(int x, int y, int z) { p.Set(x, y, z); return (world.GetBlock(p, BlockLayersAccess.Fluid)?.Id ?? 0) != 0; }
        bool FluidNear(int x, int y, int z, int r, int h)
        {
            for (int ddx = -r; ddx <= r; ddx++)
                for (int ddz = -r; ddz <= r; ddz++)
                    for (int ddy = -2; ddy <= h; ddy++)
                        if (FluidAt(x + ddx, y + ddy, z + ddz)) return true;
            return false;
        }
        // The highest real rock or earth in a column (through any water: the
        // sea floor counts, a diver can stand there). Measured, not taken
        // from the design: a headwall or a cut stands where the design says
        // nothing.
        var surfaceCache = new Dictionary<long, int>();
        int Surface(int x, int z)
        {
            long k = ((long)x << 32) ^ (uint)z;
            if (surfaceCache.TryGetValue(k, out int t)) return t;
            t = 0;
            for (int y = job.SeaLevel + job.DomeHeight + 40; y > 0; y--)
            {
                p.Set(x, y, z);
                var b = world.GetBlock(p, BlockLayersAccess.SolidBlocks);
                if (b == null || b.Id == 0 || b.BlockMaterial is EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves) continue;
                t = y; break;
            }
            return surfaceCache[k] = t;
        }
        // Only rock and earth are cut: never a chest, a prop or a placed block.
        void Cut(int x, int y, int z)
        {
            p.Set(x, y, z);
            Block b = world.GetBlock(p, BlockLayersAccess.SolidBlocks);
            if (b == null || b.Id == 0) return;
            if (b.BlockMaterial is not (EnumBlockMaterial.Stone or EnumBlockMaterial.Ore or EnumBlockMaterial.Soil
                or EnumBlockMaterial.Gravel or EnumBlockMaterial.Sand)) return;
            if ((world.GetBlock(p, BlockLayersAccess.Fluid)?.Id ?? 0) != 0) return;
            world.SetBlock(0, p);
        }

        // A run: a round cut 7 blocks across and 8 tall from the floor up,
        // swept from one point to the next, leaving 4 blocks of roof (2 near
        // the mouth). Its floor is levelled: a dip of one or two blocks (the
        // groove an oval tunnel leaves along its middle) is filled with the
        // local rock, because the game's pathfinder will not set a wide body
        // over any hole under its middle. A deeper drop stays open.
        void Bore(double x, double z, int floor, bool doorway)
        {
            const double Rad = 3.6;
            for (int bx = (int)Math.Floor(x - Rad); bx <= (int)Math.Floor(x + Rad); bx++)
                for (int bz = (int)Math.Floor(z - Rad); bz <= (int)Math.Floor(z + Rad); bz++)
                {
                    double ex = bx + 0.5 - x, ez = bz + 0.5 - z;
                    if (ex * ex + ez * ez > Rad * Rad) continue;
                    int cap = Surface(bx, bz) - (doorway ? 2 : 4);
                    for (int y = floor; y <= floor + 7 && y <= cap; y++) Cut(bx, y, bz);
                    if (InCavern(bx, bz)) continue;   // a cavern keeps its own floor and its spawn room
                    if (open(bx, floor - 1, bz) && !FluidAt(bx, floor - 1, bz)
                        && (solid(bx, floor - 2, bz) || (open(bx, floor - 2, bz) && solid(bx, floor - 3, bz))))
                        for (int y = floor - 2; y <= floor - 1; y++) if (open(bx, y, bz)) Put(bx, y, bz, stoneId);
                }
        }
        void BoreLine(double x0, double z0, int f0, double x1, double z1, int f1, bool doorway)
        {
            double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
            int n = Math.Max(1, (int)Math.Ceiling(len / 0.7));
            for (int j = 0; j <= n; j++)
            {
                double t = j / (double)n;
                Bore(x0 + (x1 - x0) * t, z0 + (z1 - z0) * t, (int)Math.Round(f0 + (f1 - f0) * t), doorway);
            }
        }

        // The level stretch round a step: out along the tree both ways while
        // the floor stays within 2 blocks over any 6 steps (a slope of about
        // a third, what a 4.8-wide body can climb with the pathfinder's
        // blessing), up to 70 steps. Returns the steps in it and the steps
        // where it turned steep.
        (List<(int A, int B)> Edges, List<int> Steep, int Count) LevelStretch(int start)
        {
            var edges = new List<(int A, int B)>();
            var steep = new List<int>();
            var depth = new Dictionary<int, int> { [start] = 0 };
            var back = new Dictionary<int, int> { [start] = -1 };
            var q = new Queue<int>(); q.Enqueue(start);
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                var next = new List<int>(kids[c]);
                if (parent[c] >= 0) next.Add(parent[c]);
                foreach (int nb in next)
                {
                    if (depth.ContainsKey(nb)) continue;
                    if (floorAt[nb] == int.MinValue || floorAt[c] == int.MinValue) { steep.Add(nb); continue; }
                    int anc = c; for (int k = 0; k < 5 && back[anc] >= 0; k++) anc = back[anc];
                    if (Math.Abs(floorAt[nb] - floorAt[c]) > 1 || Math.Abs(floorAt[nb] - floorAt[anc]) > 2) { steep.Add(nb); continue; }
                    if (depth[c] + 1 > 70) continue;
                    depth[nb] = depth[c] + 1; back[nb] = c;
                    edges.Add((c, nb));
                    q.Enqueue(nb);
                }
            }
            return (edges, steep, depth.Count);
        }

        // How far the spawner may reach for a player: no further than the
        // nearest open ground above, less a margin, and not past the places
        // where its stretch turns steep.
        float RangeFor(int x, int y, int z, List<int> steep)
        {
            double best = 200;
            for (int ddx = -70; ddx <= 70; ddx += 2)
                for (int ddz = -70; ddz <= 70; ddz += 2)
                {
                    double h2 = ddx * ddx + ddz * ddz;
                    if (h2 >= best * best) continue;
                    double dy = Surface(x + ddx, z + ddz) + 1 - y;
                    best = Math.Min(best, Math.Sqrt(h2 + dy * dy));
                }
            double r = best - 4;
            foreach (int i in steep)
            {
                double ex = steps[i].X - (x + 0.5), ey = steps[i].Y - y, ez = steps[i].Z - (z + 0.5);
                r = Math.Min(r, Math.Sqrt(ex * ex + ey * ey + ez * ez) - 3);
            }
            float want = def.GiantRange > 0 ? def.GiantRange : 20f;   // Oversized Horrors' own default
            return (float)Math.Min(want, r);
        }

        // A cavern beside the tunnel at this step: its floor level with the
        // tunnel floor, flat over the middle and curling up into walls that
        // wander, under a low uneven dome. Cut into the rock only, so where
        // it meets the tunnel and the tunnel runs lower, the floor simply
        // ends in a ledge; nothing is built under it.
        bool Lair((int Index, int X, int Floor, int Z) sp, string code, string what)
        {
            int gid = Bid(code);
            if (gid == 0) { notes.Add($"spawner '{code}' is not in this game (is its mod installed?)"); return false; }
            var a = steps[Math.Max(0, sp.Index - 2)]; var b = steps[Math.Min(steps.Count - 1, sp.Index + 2)];
            double tx = b.X - a.X, tz = b.Z - a.Z, tl = Math.Sqrt(tx * tx + tz * tz);
            if (tl < 0.5) return false;
            tx /= tl; tz /= tl;
            var stretch = LevelStretch(sp.Index);
            if (stretch.Count < 30) return false;           // too little level ground to hunt on
            var lr = new Random(sp.X * 73856093 ^ sp.Z * 19349663 ^ (int)def.Seed);
            int first = lr.NextDouble() < 0.5 ? -1 : 1;
            // The floor stays flat out to the spawn box's corners (5.4 blocks each
            // way) however the walls wander, and the dome over it stays 7 high.
            const double R = 10.0;     // half-width, before the walls wander
            const int H = 11;          // the dome over the middle of the floor
            int floor = sp.Floor;
            foreach (int side in new[] { first, -first })
            {
                double nx = -tz * side, nz = tx * side;      // across the tunnel, toward this side
                int reach = 0;
                while (reach < 12 && open(sp.X + (int)Math.Round(nx * (reach + 1)), floor, sp.Z + (int)Math.Round(nz * (reach + 1)))) reach++;
                int cx = sp.X + (int)Math.Round(nx * (reach + 5.5)), cz = sp.Z + (int)Math.Round(nz * (reach + 5.5));
                bool thick = true;
                for (int ddx = -12; ddx <= 12 && thick; ddx += 3)
                    for (int ddz = -12; ddz <= 12 && thick; ddz += 3)
                        if (floor + H + 6 > Surface(cx + ddx, cz + ddz)) thick = false;   // a thick roof under the island
                if (!thick || FluidNear(cx, floor, cz, 12, H + 2)) continue;
                int under = 0, cells = 0;
                for (int ddx = -5; ddx <= 5; ddx++)
                    for (int ddz = -5; ddz <= 5; ddz++) { cells++; if (solid(cx + ddx, floor - 1, cz + ddz)) under++; }
                if (under < cells * 0.8) continue;           // rock to stand on, not a drop
                float range = RangeFor(cx, floor - 2, cz, stretch.Steep);
                if (range < 14) continue;                    // it would wake only on top of itself

                double w1 = lr.NextDouble() * 6.3, w2 = lr.NextDouble() * 6.3, w3 = lr.NextDouble() * 6.3;
                double c1 = lr.NextDouble() * 6.3, c2 = lr.NextDouble() * 6.3;
                for (int ddx = -13; ddx <= 13; ddx++)
                    for (int ddz = -13; ddz <= 13; ddz++)
                    {
                        double ang = Math.Atan2(ddz, ddx);
                        double rr = R * (1 + 0.10 * Math.Sin(3 * ang + w1) + 0.06 * Math.Sin(5 * ang + w2) + 0.04 * Math.Sin(8 * ang + w3));
                        double n = Math.Pow(Math.Abs(ddx) / rr, 3) + Math.Pow(Math.Abs(ddz) / rr, 3);
                        if (n > 1) continue;
                        double bump = Math.Sin(ddx * 0.55 + c1) * Math.Sin(ddz * 0.5 + c2);
                        int fl = floor + (n < 0.6 ? 0 : (int)Math.Round((n - 0.6) * 10 + bump * 0.8));
                        int top = floor + (int)Math.Round(H + 1.4 * bump - (n < 0.45 ? 0 : (n - 0.45) * 14));
                        for (int y = fl; y <= top; y++) Cut(cx + ddx, y, cz + ddz);
                    }
                // the buried spawner, rock all round it
                for (int ddx = -1; ddx <= 1; ddx++)
                    for (int ddz = -1; ddz <= 1; ddz++)
                        for (int y = floor - 3; y <= floor - 1; y++)
                            if (!solid(cx + ddx, y, cz + ddz)) Put(cx + ddx, y, cz + ddz, stoneId);
                Put(cx, floor - 2, cz, gid);
                lairs.Add((cx, cz));
                // the way from the cavern onto the tunnel, then the level run
                BoreLine(cx + 0.5, cz + 0.5, floor, steps[sp.Index].X, steps[sp.Index].Z, floorAt[sp.Index], false);
                foreach (var (ea, eb) in stretch.Edges)
                {
                    BoreLine(steps[ea].X, steps[ea].Z, floorAt[ea], steps[eb].X, steps[eb].Z, floorAt[eb], ea < 30 && eb < 30);
                    runSteps.Add(ea); runSteps.Add(eb);
                }
                runSteps.Add(sp.Index);
                // the run's points (HUD x, floor, z), for tests to walk it
                sapi.Logger.Debug("[landmassgenerator] {0} run: {1}", what, string.Join(" ", stretch.Edges.Select(e => e.B).Where((_, k) => k % 4 == 0)
                    .Select(i => $"{(int)Math.Floor(steps[i].X) - sapi.WorldManager.MapSizeX / 2},{floorAt[i]},{(int)Math.Floor(steps[i].Z) - sapi.WorldManager.MapSizeZ / 2}")));
                SetBlockEntitySettings(new BlockPos(cx, floor - 2, cz, job.Dim), new Dictionary<string, string> { ["triggerRange"] = range.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                giants++;
                bool room = RoomFor(GiantBox(code), cx, floor, cz);
                sapi.Logger.Notification("[landmassgenerator] {0}: {1} buried at HUD {2}, {3}, {4}; spawn room {5}; level run of {6} steps; wakes within {7:0} blocks", what, code,
                    cx - sapi.WorldManager.MapSizeX / 2, floor - 2, cz - sapi.WorldManager.MapSizeZ / 2, room ? "clear" : "BLOCKED", stretch.Count, range);
                if (!room) notes.Add($"{what} cavern at {cx - sapi.WorldManager.MapSizeX / 2},{cz - sapi.WorldManager.MapSizeZ / 2} lacks room");
                return true;
            }
            return false;
        }
        bool FarFromLairs(int x, int z, int d) => !lairs.Any(l => (l.X - x) * (l.X - x) + (l.Z - z) * (l.Z - z) < d * d);

        // the bottom of the mine: deepgiant=
        if (!string.IsNullOrEmpty(def.DeepGiantCode) && spots.Count > 0)
            foreach (var sp in spots.OrderBy(sp => sp.Floor).Take(60))
                if (Lair(sp, def.DeepGiantCode, "deepest lair")) break;

        // giants=<code>*N: caverns spread along the deep part of the mine
        if (def.GiantCount > 0 && !string.IsNullOrEmpty(def.GiantCode))
        {
            var deep = spots.Where(sp => sp.Floor <= job.SeaLevel - 15).ToList();
            int want = def.GiantCount, made = 0;
            for (int m = 0; m < want && deep.Count > 0; m++)
            {
                // start each search at an evenly spaced point along the mine
                int start = (int)((m + 0.5) * deep.Count / (double)want);
                for (int t = 0; t < deep.Count; t++)
                {
                    var sp = deep[(start + t) % deep.Count];
                    if (!FarFromLairs(sp.X, sp.Z, 26)) continue;
                    if (Lair(sp, def.GiantCode, "mine lair")) { made++; break; }
                }
            }
            if (made < want) notes.Add($"{made} of {want} mine lairs fit");
        }

        // mouthgiant=: buried under the open ground near the mine's mouth,
        // so the giant climbs out onto the surface
        if (!string.IsNullOrEmpty(def.MouthGiantCode) && steps.Count > 0)
        {
            int gid = Bid(def.MouthGiantCode);
            var box = GiantBox(def.MouthGiantCode);
            int mx = (int)steps[0].X, mz = (int)steps[0].Z;
            bool done = false;
            for (int r = 8; r <= 34 && !done && gid != 0; r += 2)
                for (int ang = 0; ang < 360 && !done; ang += 15)
                {
                    int x = mx + (int)Math.Round(Math.Cos(ang * Math.PI / 180) * r), z = mz + (int)Math.Round(Math.Sin(ang * Math.PI / 180) * r);
                    int top = -1;
                    for (int y = job.SeaLevel + job.DomeHeight + 30; y >= job.SeaLevel; y--)
                    {
                        p.Set(x, y, z);
                        var b = world.GetBlock(p, BlockLayersAccess.SolidBlocks);
                        if (b == null || b.Id == 0 || b.BlockMaterial is EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves) continue;
                        if ((world.GetBlock(p, BlockLayersAccess.Fluid)?.Id ?? 0) != 0) break;
                        top = y; break;
                    }
                    if (top < job.SeaLevel + 1 || !RoomFor(box, x, top + 1, z)) continue;
                    // under the top block, with ground under it
                    for (int y = top - 3; y <= top - 1; y++) if (!solid(x, y, z)) Put(x, y, z, stoneId);
                    Put(x, top - 1, z, gid);
                    if (def.GiantRange > 0) SetBlockEntitySettings(new BlockPos(x, top - 1, z, job.Dim), new Dictionary<string, string> { ["triggerRange"] = def.GiantRange.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                    giants++;
                    done = true;
                    sapi.Logger.Notification("[landmassgenerator] mouth spawner: {0} buried at HUD {1}, {2}, {3}; spawn room clear", def.MouthGiantCode,
                        x - sapi.WorldManager.MapSizeX / 2, top - 1, z - sapi.WorldManager.MapSizeZ / 2);
                }
            if (!done) notes.Add("no open ground near the mine mouth for the surface giant");
        }

        // ── machinery ── after the giants, so it stands in the tunnel as
        // their caverns and runs left it, and never in a run (a prop at the
        // wall narrows a run below what a giant's pathfinder will take)
        var runCells = runSteps.Where(i => i >= 0 && i < steps.Count)
            .Select(i => ((int)Math.Floor(steps[i].X), (int)Math.Floor(steps[i].Z))).ToList();
        int colPieces = 0, runPieces = 0;
        if (def.Machinery > 0)
            for (int i = 1; i < steps.Count; i++)
            {
                if (i % def.Machinery != def.Machinery / 3) continue;
                if (runSteps.Contains(i) || runSteps.Contains(i - 2) || runSteps.Contains(i + 2)) continue;
                var a = steps[i - 1]; var s = steps[i];
                double dx = s.X - a.X, dz = s.Z - a.Z, len = Math.Sqrt(dx * dx + dz * dz);
                if (len < 0.2 || len > 4) continue;
                dx /= len; dz /= len;
                bool acrossX = Math.Abs(dz) >= Math.Abs(dx);
                int cx = (int)Math.Floor(s.X), cz = (int)Math.Floor(s.Z), cy = (int)Math.Floor(s.Y);
                if (!open(cx, cy, cz)) continue;
                int floor = FloorUnder(cx, cy, cz, 10);
                if (!solid(cx, floor - 1, cz)) continue;
                int ceil = cy; while (ceil < cy + 14 && open(cx, ceil + 1, cz)) ceil++;
                int height = ceil - floor + 1;
                if (height < 3) continue;
                int ux = acrossX ? 1 : 0, uz = acrossX ? 0 : 1;
                int left = 0; while (left < 10 && open(cx - ux * (left + 1), floor, cz - uz * (left + 1))) left++;
                int right = 0; while (right < 10 && open(cx + ux * (right + 1), floor, cz + uz * (right + 1))) right++;
                // the wall on one side: plate it from the floor to head height
                int sgn = rnd.NextDouble() < 0.5 ? -1 : 1;
                int reach = sgn < 0 ? left : right;
                int ex = cx + sgn * ux * reach, ez = cz + sgn * uz * reach;      // last open cell
                int wx = ex + sgn * ux, wz = ez + sgn * uz;                         // the wall
                int ax = 1 - ux, az = 1 - uz;                                      // along the tunnel
                bool any = false;
                for (int k = -1; k <= 1; k++)
                    for (int y = floor; y <= Math.Min(ceil, floor + 3); y++)
                    {
                        int px = wx + ax * k, pz = wz + az * k;
                        if (!solid(px, y, pz)) continue;
                        Put(px, y, pz, (k + y) % 3 == 0 && rustA != 0 ? rustA : rustB != 0 ? rustB : rustA);
                        any = true;
                    }
                if (!any) continue;
                // rot turns a prop's north side (its flat back) to the wall
                float rot = acrossX ? (sgn < 0 ? (float)(Math.PI / 2) : (float)(-Math.PI / 2)) : (sgn < 0 ? 0f : (float)Math.PI);
                float pushX = sgn * ux * 0.34f, pushZ = sgn * uz * 0.34f;             // a pipe against the rock
                var st = MineStanding[rnd.Next(MineStanding.Length)];
                if (open(ex, floor, ez)) Clut(st.Block, ex, floor, ez, st.Type, rot, st.Flip, st.Flat ? pushX * 0.6f : 0, st.Flat ? pushZ * 0.6f : 0);
                // a rusty pipe up the wall beside it, and a bracket over it.
                // The game turns a mesh by rotateY with x' = x cos + z sin,
                // z' = z cos - x sin, so rot - 90 degrees turns a pipe's west
                // stub to the wall. Where rock is behind it, the bottom piece
                // comes out of the wall (v-W) and the top piece bends back in
                // (v-topW); now and then a middle piece is cracked.
                float stubRot = rot - (float)(Math.PI / 2);
                int side = rnd.NextDouble() < 0.5 ? -1 : 1;
                // The column slides out to the rock (the tunnel is often wider
                // beside the machine). The mine's round tunnels lean away above
                // the floor, so where the rock falls back the column gets a
                // backing of the same rusted plate: both ends always go into
                // something.
                int qx = ex + ax * side, qz = ez + az * side;
                for (int t = 0; t < 3 && open(qx, floor, qz) && open(qx + sgn * ux, floor, qz + sgn * uz); t++) { qx += sgn * ux; qz += sgn * uz; }
                int colTop = floor - 1;
                while (colTop < Math.Min(ceil - 1, floor + 3) && open(qx, colTop + 1, qz)) colTop++;
                if (colTop > floor && solid(qx, floor - 1, qz) && solid(qx + sgn * ux, floor, qz + sgn * uz))
                    for (int y = floor; y <= colTop; y++)
                    {
                        if (!solid(qx + sgn * ux, y, qz + sgn * uz)) Put(qx + sgn * ux, y, qz + sgn * uz, rustB != 0 ? rustB : rustA);
                        string pt = y == floor ? "pipe-veryrusted-v-W"
                            : y == colTop ? "pipe-veryrusted-v-topW"
                            : rnd.NextDouble() < 0.3 ? "pipe-veryrusted-v-empty_broken" : "pipe-veryrusted-v-empty";
                        bool stub = y == floor || y == colTop;
                        if (Clut("clutter", qx, y, qz, pt, stub ? stubRot : 0, null, pushX, pushZ)) colPieces++;
                    }
                // a rusty pipe along the ceiling over the machine, in the
                // corner of wall and roof: one block per segment, so every
                // block has its own hitbox. offsetY 0.31 lifts it against the
                // roof. A segment needs rock above and behind it and stays
                // well clear of a giant's run. Where the run meets rock it
                // goes in flush; where it stops in the open its end is broken.
                {
                    float runRot = ax != 0 ? 0f : (float)(-Math.PI / 2);     // turns the pipe's +x end along +along
                    // the roof corner: along the roof row from the middle of
                    // the tunnel toward the plated wall, the last open cell
                    int fx = cx, fz = cz;
                    for (int t = 0; t < 10 && open(fx + sgn * ux, ceil, fz + sgn * uz); t++) { fx += sgn * ux; fz += sgn * uz; }
                    bool RunCell(int k)
                    {
                        int rx = fx + ax * k, rz = fz + az * k;
                        if (!open(rx, ceil, rz) || !solid(rx, ceil + 1, rz) || !solid(rx + sgn * ux, ceil, rz + sgn * uz)) return false;
                        foreach (var (gx, gz) in runCells)
                            if ((gx - rx) * (gx - rx) + (gz - rz) * (gz - rz) < 36) return false;
                        return true;
                    }
                    int k0 = 0, k1 = 0;
                    if (RunCell(0))
                    {
                        while (k0 > -3 && RunCell(k0 - 1)) k0--;
                        while (k1 < 3 && RunCell(k1 + 1)) k1++;
                    }
                    if (RunCell(0) && k1 - k0 >= 2)
                        for (int k = k0; k <= k1; k++)
                        {
                            int rx = fx + ax * k, rz = fz + az * k;
                            bool end = k == k0 || k == k1;
                            int dir = k == k0 ? -1 : 1;
                            bool intoRock = end && solid(rx + ax * dir, ceil, rz + az * dir);
                            string pt = end && !intoRock ? "pipe-veryrusted-h-broken1"
                                : rnd.NextDouble() < 0.25 ? "pipe-veryrusted-h-EW_broken" : "pipe-veryrusted-h-EW";
                            float r = pt == "pipe-veryrusted-h-broken1" && dir < 0 ? runRot + (float)Math.PI : runRot;
                            if (Clut("clutter", rx, ceil, rz, pt, r, null, pushX, pushZ, 0.31f)) runPieces++;
                        }
                }
                if (height >= 4 && open(ex, floor + 2, ez)) Clut("clutter", ex, floor + 2, ez, MineWallHung[rnd.Next(MineWallHung.Length)], rot);
                // scrap on the floor
                int jx = ex - sgn * ux + ax * (rnd.Next(3) - 1), jz = ez - sgn * uz + az * (rnd.Next(3) - 1);
                if (open(jx, floor, jz) && solid(jx, floor - 1, jz))
                {
                    if (rnd.NextDouble() < 0.5) Put(jx, floor, jz, floorJunk[rnd.Next(floorJunk.Length)]);
                    else Clut("clutter", jx, floor, jz, MineFloorScrap[rnd.Next(MineFloorScrap.Length)], (float)(rnd.Next(4) * Math.PI / 2));
                }
                machines++;
            }

        if (machines > 0)
            sapi.Logger.Notification("[landmassgenerator] mine pipes: {0} wall machines, {1} wall pipe pieces, {2} ceiling pipe pieces", machines, colPieces, runPieces);
        return (machines > 0 ? $" {machines} wall machine(s)." : "") + (giants > 0 ? $" {giants} giant spawner(s)." : "")
            + (notes.Count > 0 ? " Giants: " + string.Join("; ", notes) + "." : "");
    }

    // ── story chests ───────────────────────────────────────────────────

    // Plan line: storychest <storycode> <contents> [near=<block>] [level=<n>].
    // Once the game has built that story structure (it does so when a player
    // first comes near), the items go into its top entrance chest: the
    // highest chest within the structure's footprint that has a heavy door
    // within 16 blocks (the Resonance Archives' surface entrance), else its
    // highest chest. With near=, the chest nearest a block whose code starts
    // with that (near=anvil: the smithy in Nadiya's village). With level=,
    // the chest nearest that many blocks above the structure's floor
    // (level=24: the Devastation tower's ground floor). Only chests a player
    // may open count; when the game's land claim locks every one (Nadiya's
    // village, Tobias' cave), a chest of our own goes just outside the claim.
    // Done once per world.
    private List<(string Code, string Contents, string Near, int? Level)> _storyChests;
    private long _storyChestListener;
    private bool _storyChestForce;   // /genstorychests: skip the player-nearby test, load the area
    private readonly HashSet<string> _storyChestLoading = new();

    private void StartStoryChestWatcher()
    {
        if (_storyChestListener != 0) return;
        _storyChestListener = sapi.Event.RegisterGameTickListener(StoryChestTick, 5000);
    }

    private void StoryChestTick(float dt)
    {
        if (_storyChests == null)
        {
            var saved = sapi.WorldManager.SaveGame.GetData<List<string>>("lgStoryChests", null) ?? new List<string>();
            _storyChests = saved.Select(s =>
            {
                var f = s.Split('|');
                return (f[0], f.Length > 1 ? f[1] : "", f.Length > 2 && f[2].Length > 0 ? f[2] : null,
                    f.Length > 3 && int.TryParse(f[3], out int lv) ? lv : (int?)null);
            }).ToList();
        }
        if (_storyChests.Count == 0) return;
        var genStory = sapi.ModLoader.GetModSystem<GenStoryStructures>();
        if (genStory == null) return;
        foreach (var sc in _storyChests.ToList())
        {
            string doneKey = "lgStoryChestDone:" + sc.Code;
            if (sapi.WorldManager.SaveGame.GetData<bool>(doneKey, false)) { _storyChests.Remove(sc); continue; }
            var loc = genStory.Structures.Get(sc.Code);
            if (loc == null || !loc.DidGenerate || loc.Location == null) { if (_storyChestForce) sapi.Logger.Notification("[landmassgenerator] story chest {0}: structure not generated yet", sc.Code); continue; }
            var area = loc.Location;
            bool near = sapi.World.AllOnlinePlayers.Any(pl => pl.Entity != null
                && pl.Entity.Pos.X > area.X1 - 200 && pl.Entity.Pos.X < area.X2 + 200
                && pl.Entity.Pos.Z > area.Z1 - 200 && pl.Entity.Pos.Z < area.Z2 + 200);
            if (!near && !_storyChestForce) continue;

            int cs = GlobalConstants.ChunkSize, margin = 16;
            int cx1 = (area.X1 - margin) / cs, cx2 = (area.X2 + margin) / cs, cz1 = (area.Z1 - margin) / cs, cz2 = (area.Z2 + margin) / cs;
            int cyMax = sapi.WorldManager.MapSizeY / cs;
            var candidates = new List<BlockEntityGenericTypedContainer>();
            bool allLoaded = true;
            for (int cx = cx1; cx <= cx2 && allLoaded; cx++)
                for (int cz = cz1; cz <= cz2 && allLoaded; cz++)
                    for (int cy = 0; cy < cyMax; cy++)
                    {
                        var chunk = sapi.WorldManager.GetChunk(cx, cy, cz);
                        if (chunk == null) { allLoaded = false; break; }
                        foreach (var be in chunk.BlockEntities.Values)
                            if (be is BlockEntityGenericTypedContainer ct && ct.Block?.Code?.Path?.StartsWith("chest") == true) candidates.Add(ct);
                    }
            if (!allLoaded)
            {
                // /genstorychests on a headless server: load the area and look again
                if (_storyChestForce && _storyChestLoading.Add(sc.Code))
                {
                    sapi.Logger.Notification("[landmassgenerator] story chest {0}: loading its area", sc.Code);
                    string code = sc.Code;
                    LoadChunkAreaBanded(cx1, cz1, cx2, cz2, false, () =>
                    {
                        _storyChestForce = true;
                        try { StoryChestTick(0); }
                        finally { _storyChestForce = false; _storyChestLoading.Remove(code); }
                    });
                }
                else if (_storyChestForce) sapi.Logger.Notification("[landmassgenerator] story chest {0}: its area is not loaded yet", sc.Code);
                continue;
            }

            var world = sapi.World.BlockAccessor;
            var q = new BlockPos(0, 0, 0, 0);
            bool NearHeavyDoor(BlockPos at)
            {
                for (int dx = -16; dx <= 16; dx++)
                    for (int dy = -6; dy <= 6; dy++)
                        for (int dz = -16; dz <= 16; dz++)
                        {
                            q.Set(at.X + dx, at.Y + dy, at.Z + dz);
                            string path = world.GetBlock(q)?.Code?.Path;
                            if (path != null && path.StartsWith("door-2x4heavy")) return true;
                        }
                return false;
            }
            // near=: distance from the chest to the nearest such block within 12
            double NearBlock(BlockPos at)
            {
                double best = double.MaxValue;
                for (int dx = -12; dx <= 12; dx++)
                    for (int dy = -6; dy <= 6; dy++)
                        for (int dz = -12; dz <= 12; dz++)
                        {
                            q.Set(at.X + dx, at.Y + dy, at.Z + dz);
                            string path = world.GetBlock(q)?.Code?.Path;
                            if (path != null && path.StartsWith(sc.Near)) best = Math.Min(best, dx * dx + dy * dy + dz * dz);
                        }
                return best;
            }
            BlockEntityGenericTypedContainer target = null;
            // a chest a player may open: none in a claim that keeps everyone out
            bool Open(BlockPos at) => sapi.World.Claims.Get(at)?.All(cl => cl.AllowUseEveryone) ?? true;
            var notes = new List<string>();
            BlockPos from = null;
            if (sc.Near != null)
            {
                var byBlock = candidates.Select(c => (Chest: c, D: NearBlock(c.Pos))).Where(t => t.D < double.MaxValue).OrderBy(t => t.D).Select(t => t.Chest).ToList();
                target = byBlock.FirstOrDefault(c => Open(c.Pos));
                from = byBlock.Count > 0 ? byBlock[0].Pos : null;
                if (target == null && from == null)
                {
                    // No chest within 12 of one (Rustfall 0.70's village kept 8 of
                    // its 32): start from the block itself
                    var found = FindBlocksIn(area, sc.Near);
                    from = found.FirstOrDefault();
                    notes.Add($"{candidates.Count} chest(s) in the {area.SizeX}x{area.SizeY}x{area.SizeZ} area, none near a {sc.Near}; {found.Count} {sc.Near} block(s) in it"
                        + (from != null ? $", the first at HUD {from.X - sapi.WorldManager.MapSizeX / 2}, {from.Y}, {from.Z - sapi.WorldManager.MapSizeZ / 2}" : ""));
                    if (from != null) target = candidates.Where(c => Open(c.Pos)).OrderBy(c => c.Pos.DistanceSqTo(from.X, from.Y, from.Z)).FirstOrDefault(c => c.Pos.DistanceSqTo(from.X, from.Y, from.Z) <= 24 * 24);
                }
                // Every one is locked (the game claims Nadiya's village whole): a
                // chest of our own just outside the claim, on the side nearest it
                if (target == null && from != null) target = StoryChestOutsideClaim(from, Open);
                if (target == null && from != null)
                {
                    if (_storyChestForce) sapi.Logger.Notification("[landmassgenerator] story chest {0}: {1} chest(s) in the area, {2} near a {3}, and no open ground outside its claim{4}",
                        sc.Code, candidates.Count, byBlock.Count, sc.Near, notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : "");
                    continue;
                }
            }
            // No such block in the area at all: the usual pick
            if (target == null)
            {
                var order = (sc.Level is int lv ? candidates.OrderBy(c => Math.Abs(c.Pos.Y - area.Y1 - lv)) : candidates.OrderByDescending(c => c.Pos.Y)).ToList();
                target = sc.Level == null ? order.FirstOrDefault(c => Open(c.Pos) && NearHeavyDoor(c.Pos)) : null;
                target ??= order.FirstOrDefault(c => Open(c.Pos));
                if (target == null && order.Count > 0) target = StoryChestOutsideClaim(order[0].Pos, Open);
                if (target == null) { if (_storyChestForce) sapi.Logger.Notification("[landmassgenerator] story chest {0}: {1} chest(s) in the area, none open and no open ground outside its claim{2}", sc.Code, candidates.Count, notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : ""); continue; }
            }

            int put = FillChest(target, sc.Contents.Split(',', StringSplitOptions.RemoveEmptyEntries), notes);
            sapi.WorldManager.SaveGame.StoreData(doneKey, true);
            _storyChests.Remove(sc);
            sapi.Logger.Notification("[landmassgenerator] story chest {0}: {1} stack(s) added to the chest at HUD {2}, {3}, {4}{5}", sc.Code, put,
                target.Pos.X - sapi.WorldManager.MapSizeX / 2, target.Pos.Y, target.Pos.Z - sapi.WorldManager.MapSizeZ / 2,
                notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : "");
        }
    }

    // /genstorycheck <code>: how much of a story structure's schematic stands in
    // the world. Compares every non-air block of the schematic with the world
    // at the structure's location (turned 0 or 180 degrees, at the height that
    // fits best: a surface structure's stored area starts at y 1), by the first
    // word of the block code, so a chest facing east matches one facing west.
    // Meta blocks (placeholders) never stand, so they are left out. Logs the
    // total and a map of 16-block cells. With repair, every chunk column where
    // less than half of its share stands is deleted and generated again (the
    // game places the structure into a column as it generates), then checked.
    private void StoryCheck(string code, string schematic, bool repair = false)
    {
        var genStory = sapi.ModLoader.GetModSystem<GenStoryStructures>();
        var loc = genStory?.Structures.Get(code);
        if (loc == null || loc.Location == null) { sapi.Logger.Notification("[landmassgenerator] story check {0}: no such story location", code); return; }
        if (!loc.DidGenerate) sapi.Logger.Notification("[landmassgenerator] story check {0}: not generated yet; loading its area generates it", code);
        var asset = sapi.Assets.TryGet(new AssetLocation("game", "worldgen/schematics/" + schematic + ".json"));
        if (asset == null) { sapi.Logger.Notification("[landmassgenerator] story check {0}: no schematic {1}", code, schematic); return; }
        var schem = JsonUtil.FromString<BlockSchematic>(asset.ToText());
        var area = loc.Location;
        int cs = GlobalConstants.ChunkSize;
        // first, which of its chunk columns exist already ('#'), before the
        // check loads (and so generates) the rest
        int ex1 = area.X1 / cs, ez1 = area.Z1 / cs, ew = (area.X1 + schem.SizeX - 1) / cs - ex1 + 1, eh = (area.Z1 + schem.SizeZ - 1) / cs - ez1 + 1;
        var exists = new bool[ew, eh];
        int tested = 0;
        for (int i = 0; i < ew; i++)
            for (int k = 0; k < eh; k++)
            {
                int ii = i, kk = k;
                sapi.WorldManager.TestMapChunkExists(ex1 + i, ez1 + k, ok => sapi.Event.EnqueueMainThreadTask(() =>
                {
                    exists[ii, kk] = ok;
                    if (++tested < ew * eh) return;
                    for (int r = 0; r < eh; r++)
                        sapi.Logger.Notification("[landmassgenerator] story check {0} existing z {1}: {2}", code, ez1 + r,
                            new string(Enumerable.Range(0, ew).Select(c => exists[c, r] ? '#' : '.').ToArray()));
                    StoryCheckLoaded(code, schematic, schem, area, repair);
                }, "genstorycheck"));
            }
    }

    private void StoryCheckLoaded(string code, string schematic, BlockSchematic schem, Cuboidi area, bool repair)
    {
        int cs = GlobalConstants.ChunkSize;
        LoadChunkAreaBanded((area.X1 - 16) / cs, (area.Z1 - 16) / cs, (area.X2 + 16) / cs, (area.Z2 + 16) / cs, false, () =>
        {
            var world = sapi.World.BlockAccessor;
            static string Word(string path) { int i = path.IndexOf('-'); return i < 0 ? path : path.Substring(0, i); }
            var want = new List<(int X, int Y, int Z, string W)>();
            for (int i = 0; i < schem.Indices.Count; i++)
            {
                uint idx = schem.Indices[i];
                if (!schem.BlockCodes.TryGetValue(schem.BlockIds[i], out var bc) || bc.Path == "air" || bc.Path.StartsWith("meta")) continue;
                want.Add(((int)(idx & 0x3ff), (int)((idx >> 20) & 0x3ff), (int)((idx >> 10) & 0x3ff), Word(bc.Path)));
            }
            var p = new BlockPos(0, 0, 0, 0);
            bool Match((int X, int Y, int Z, string W) b, bool turn, int y0)
            {
                int x = turn ? schem.SizeX - 1 - b.X : b.X, z = turn ? schem.SizeZ - 1 - b.Z : b.Z;
                p.Set(area.X1 + x, y0 + b.Y, area.Z1 + z);
                string path = world.GetBlock(p)?.Code?.Path;
                return path != null && Word(path) == b.W;
            }
            (bool Turn, int Dy, int N) best = (false, 0, -1);
            int sea = sapi.World.SeaLevel;
            foreach (bool turn in new[] { false, true })
                for (int y0 = Math.Max(1, sea - 60); y0 <= Math.Min(sapi.WorldManager.MapSizeY - schem.SizeY, sea + 80); y0++)
                {
                    int n = 0;
                    for (int i = 0; i < want.Count; i += 13) if (Match(want[i], turn, y0)) n++;
                    if (n > best.N) best = (turn, y0, n);
                }
            int cw = (schem.SizeX + 15) / 16, ch = (schem.SizeZ + 15) / 16;
            var all = new int[cw, ch]; var hit = new int[cw, ch];
            int total = 0;
            var missing = new Dictionary<string, int>();
            foreach (var b in want)
            {
                int x = best.Turn ? schem.SizeX - 1 - b.X : b.X, z = best.Turn ? schem.SizeZ - 1 - b.Z : b.Z;
                all[x / 16, z / 16]++;
                if (Match(b, best.Turn, best.Dy)) { hit[x / 16, z / 16]++; total++; }
                else missing[b.W] = missing.GetValueOrDefault(b.W) + 1;
            }
            sapi.Logger.Notification("[landmassgenerator] story check {0}: {1} of {2} schematic blocks stand ({3:0}%), turned {4}, base at y {5}; area HUD {6}, {7}, {8} to {9}, {10}; most missing: {11}",
                code, total, want.Count, 100.0 * total / Math.Max(1, want.Count), best.Turn ? 180 : 0, best.Dy,
                area.X1 - sapi.WorldManager.MapSizeX / 2, area.Y1, area.Z1 - sapi.WorldManager.MapSizeZ / 2,
                area.X2 - sapi.WorldManager.MapSizeX / 2, area.Z2 - sapi.WorldManager.MapSizeZ / 2,
                string.Join(", ", missing.OrderByDescending(kv => kv.Value).Take(10).Select(kv => kv.Key + " " + kv.Value)));
            // one line per 16-block row, north to south: tenths standing, '.' empty cell
            for (int r = 0; r < ch; r++)
            {
                var row = new System.Text.StringBuilder();
                for (int c = 0; c < cw; c++) row.Append(all[c, r] < 20 ? '.' : (char)('0' + Math.Min(9, 10 * hit[c, r] / all[c, r])));
                sapi.Logger.Notification("[landmassgenerator] story check {0} row {1,2}: {2}", code, r, row.ToString());
            }
            // the same by chunk column (32 blocks), and what stands instead
            // where less than half of a chunk column's share stands
            int ccx1 = area.X1 / cs, ccz1 = area.Z1 / cs;
            int ccw = (area.X1 + schem.SizeX - 1) / cs - ccx1 + 1, cch = (area.Z1 + schem.SizeZ - 1) / cs - ccz1 + 1;
            var call = new int[ccw, cch]; var chit = new int[ccw, cch];
            foreach (var b in want)
            {
                int x = best.Turn ? schem.SizeX - 1 - b.X : b.X, z = best.Turn ? schem.SizeZ - 1 - b.Z : b.Z;
                int i = (area.X1 + x) / cs - ccx1, k = (area.Z1 + z) / cs - ccz1;
                call[i, k]++;
                if (Match(b, best.Turn, best.Dy)) chit[i, k]++;
            }
            var instead = new Dictionary<string, int>();
            foreach (var b in want)
            {
                int x = best.Turn ? schem.SizeX - 1 - b.X : b.X, z = best.Turn ? schem.SizeZ - 1 - b.Z : b.Z;
                int i = (area.X1 + x) / cs - ccx1, k = (area.Z1 + z) / cs - ccz1;
                if (call[i, k] == 0 || chit[i, k] * 2 >= call[i, k] || Match(b, best.Turn, best.Dy)) continue;
                string w = world.GetBlock(p)?.Code?.Path ?? "none";
                w = w.IndexOf('-') < 0 ? w : w.Substring(0, w.IndexOf('-'));
                instead[w] = instead.GetValueOrDefault(w) + 1;
            }
            sapi.Logger.Notification("[landmassgenerator] story check {0}: chunk columns x {1}..{2} (mod 16: {3}..{4}), z {5}..{6} (mod 16: {7}..{8}); in the ones under half, instead: {9}",
                code, ccx1, ccx1 + ccw - 1, ccx1 % 16, (ccx1 + ccw - 1) % 16, ccz1, ccz1 + cch - 1, ccz1 % 16, (ccz1 + cch - 1) % 16,
                string.Join(", ", instead.OrderByDescending(kv => kv.Value).Take(10).Select(kv => kv.Key + " " + kv.Value)));
            for (int k = 0; k < cch; k++)
            {
                var row = new System.Text.StringBuilder();
                for (int i = 0; i < ccw; i++) row.Append(call[i, k] < 20 ? '.' : (char)('0' + Math.Min(9, 10 * chit[i, k] / call[i, k])));
                sapi.Logger.Notification("[landmassgenerator] story check {0} chunk row z {1}: {2}", code, ccz1 + k, row.ToString());
            }
            if (!repair) return;
            var bad = new List<(int X, int Z)>();
            for (int i = 0; i < ccw; i++)
                for (int k = 0; k < cch; k++)
                    if (call[i, k] >= 20 && chit[i, k] * 2 < call[i, k]) bad.Add((ccx1 + i, ccz1 + k));
            if (bad.Count == 0) { sapi.Logger.Notification("[landmassgenerator] story check {0}: nothing to repair", code); return; }
            foreach (var (x, z) in bad) sapi.WorldManager.DeleteChunkColumn(x, z);
            sapi.Logger.Notification("[landmassgenerator] story check {0}: repairing {1} chunk column(s): {2}", code, bad.Count, string.Join(" ", bad.Select(b => b.X + "," + b.Z)));
            sapi.Event.RegisterCallback(_ => LoadChunkAreaBanded(bad.Min(b => b.X), bad.Min(b => b.Z), bad.Max(b => b.X), bad.Max(b => b.Z), false,
                () => sapi.Event.RegisterCallback(__ => StoryCheck(code, schematic), 2000)), 2000);
        });
    }

    // Every block in the area whose code path starts with `prefix`.
    private List<BlockPos> FindBlocksIn(Cuboidi area, string prefix)
    {
        var world = sapi.World.BlockAccessor;
        var found = new List<BlockPos>();
        var p = new BlockPos(0, 0, 0, 0);
        for (int x = area.X1; x <= area.X2; x++)
            for (int z = area.Z1; z <= area.Z2; z++)
                for (int y = area.Y1; y <= area.Y2; y++)
                {
                    p.Set(x, y, z);
                    if (world.GetBlock(p)?.Code?.Path?.StartsWith(prefix) == true) found.Add(p.Copy());
                }
        return found;
    }

    // From `from` straight out past the nearest edge of the locked claim over
    // it, to the first dry ground a player may use, and a new chest there.
    private BlockEntityGenericTypedContainer StoryChestOutsideClaim(BlockPos from, System.Func<BlockPos, bool> open)
    {
        var area = (sapi.World.Claims.Get(from) ?? new LandClaim[0]).Where(cl => !cl.AllowUseEveryone)
            .SelectMany(cl => cl.Areas).FirstOrDefault(a => a.Contains(from));
        if (area == null) return null;
        var world = sapi.World.BlockAccessor;
        Block chest = sapi.World.GetBlock(new AssetLocation("game", "chest-east"));
        if (chest == null) return null;
        var p = new BlockPos(0, 0, 0, from.dimension);
        var sides = new[] { (D: from.X - area.X1, Sx: -1, Sz: 0), (D: area.X2 - from.X, Sx: 1, Sz: 0), (D: from.Z - area.Z1, Sx: 0, Sz: -1), (D: area.Z2 - from.Z, Sx: 0, Sz: 1) };
        foreach (var (d, sx, sz) in sides.OrderBy(s => s.D))
            for (int k = d + 3; k <= d + 14; k++)
            {
                int x = from.X + sx * k, z = from.Z + sz * k;
                for (int y = Math.Min(sapi.WorldManager.MapSizeY - 3, area.Y2 + 20); y >= sapi.World.SeaLevel - 1; y--)
                {
                    p.Set(x, y, z);
                    Block b = world.GetBlock(p, BlockLayersAccess.SolidBlocks);
                    if (b == null || b.Id == 0 || b.BlockMaterial is EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves or EnumBlockMaterial.Wood) continue;
                    p.Set(x, y + 1, z);
                    if (!b.SideSolid[BlockFacing.UP.Index] || (world.GetBlock(p, BlockLayersAccess.Fluid)?.Id ?? 0) != 0) break;
                    var at = new BlockPos(x, y + 1, z, from.dimension);
                    if (!open(at) || HasChestNear(world, at)) break;
                    world.SetBlock(chest.BlockId, at);
                    if (world.GetBlockEntity(at) is not BlockEntityGenericTypedContainer be) return null;
                    var ts = new ItemStack(chest);
                    ts.Attributes.SetString("type", "normal-aged");
                    be.OnBlockPlaced(ts);
                    return be;
                }
            }
        return null;
    }
}

// A chart with one place circled in red. Use it (right click) and the place
// gets a red X on your world map, the same way a Better Ruins or Great
// Machine locator map marks its find. Which place: the item's islandShape
// attribute (matched against the world plan's shape names) or storyLocation
// (a story structure: lazaret, tobiascave...). The worn chart
// (islandmap-random, randomTarget) circles nothing until first used: then it
// picks a place its reader has not marked yet, at least 600 blocks away, and
// keeps it (stack attributes mapTarget, mapTargetName).
public class ItemIslandMap : Item
{
    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
        if (slot.Empty) return;
        handling = EnumHandHandling.PreventDefault;
        if (api.Side != EnumAppSide.Server) return;
        if ((byEntity as EntityPlayer)?.Player is not IServerPlayer plr) return;

        var mod = api.ModLoader.GetModSystem<LandmassGeneratorModSystem>();
        string label;
        Vec3d pos;
        if (Attributes?["randomTarget"]?.AsBool() == true)
        {
            string key = slot.Itemstack.Attributes.GetString("mapTarget");
            var target = key != null ? mod?.MapTargets().FirstOrDefault(t => t.Key == key) : mod?.PickMapTarget(plr, byEntity.Pos.XYZ);
            if (target == null)
            {
                plr.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get(key != null ? "No location found on this map" : "landmassgenerator:islandmap-nothingnew"), EnumChatType.Notification);
                return;
            }
            if (key == null)
            {
                slot.Itemstack.Attributes.SetString("mapTarget", target.Key);
                slot.Itemstack.Attributes.SetString("mapTargetName", target.Label);
                slot.MarkDirty();
            }
            label = target.Label;
            pos = target.Pos;
        }
        else
        {
            label = Lang.Get(Attributes?["waypointText"]?.AsString() ?? Attributes?["islandShape"]?.AsString() ?? "?");
            pos = mod?.FindMapTarget(Attributes);
        }
        if (pos == null)
        {
            plr.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("No location found on this map"), EnumChatType.Notification);
            return;
        }
        var layer = api.ModLoader.GetModSystem<WorldMapManager>()?.MapLayers.FirstOrDefault(ml => ml is WaypointMapLayer) as WaypointMapLayer;
        if (!byEntity.World.Config.GetBool("allowMap", true) || layer == null)
        {
            var d = pos.Clone().Sub(byEntity.Pos.XYZ);
            d.Y = 0;
            plr.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("{0} blocks distance", (int)d.Length()), EnumChatType.Notification);
            return;
        }
        string uid = plr.PlayerUID;
        if (layer.Waypoints.Any(wp => wp.OwningPlayerUid == uid && wp.Position.X == pos.X && wp.Position.Z == pos.Z))
        {
            plr.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("Location already marked on your map"), EnumChatType.Notification);
            return;
        }
        layer.AddWaypoint(new Waypoint
        {
            Color = ColorUtil.ColorFromRgba(220, 30, 30, 255),
            Icon = "x",
            Pinned = true,
            Position = pos,
            OwningPlayerUid = uid,
            Title = label
        }, plr);
        plr.SendMessage(GlobalConstants.GeneralChatGroup, Lang.Get("Location of {0} added to your world map", label), EnumChatType.Notification);
    }

    public override void GetHeldItemInfo(ItemSlot inSlot, System.Text.StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
        string name = inSlot.Itemstack?.Attributes.GetString("mapTargetName");
        if (name != null) dsc.AppendLine(Lang.Get("landmassgenerator:islandmap-shows", name));
    }
}
