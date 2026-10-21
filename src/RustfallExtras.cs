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
    // (shortest path), rust painted on the floor under every step of the
    // way, a block or so wide, with rust weeping down the walls here and
    // there. Decor only: nothing is mined or changed.
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

        // walk back from the mouth to the chest, painting the floor
        var painted = new HashSet<long>();
        int count = 0, n = 0;
        var cur = goal.Value;
        var rnd = new Random(chest.X * 31 + chest.Z * 17);
        while (true)
        {
            int fy = cur.Y;
            while (fy > cur.Y - 6 && Open(cur.X, fy - 1, cur.Z)) fy--;
            foreach (var (ox, oz) in new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                if ((ox != 0 || oz != 0) && rnd.NextDouble() < 0.55) continue;
                int fx = cur.X + ox, fz = cur.Z + oz;
                if (!Open(fx, fy, fz) || !SolidTop(fx, fy - 1, fz)) continue;
                long k = Key(fx, fy - 1, fz);
                if (!painted.Add(k)) continue;
                p.Set(fx, fy - 1, fz);
                if (world.SetDecor(floorRust, p, BlockFacing.UP)) count++;
            }
            if (++n % 5 == 0)
            {
                // rust weeping down a wall beside the trail
                foreach (var f in BlockFacing.HORIZONTALS)
                {
                    int wx = cur.X + f.Normali.X, wz = cur.Z + f.Normali.Z;
                    if (Open(wx, fy + 1, wz)) continue;
                    p.Set(wx, fy + 1, wz);
                    var wb = world.GetBlock(p, BlockLayersAccess.SolidBlocks);
                    if (wb == null || wb.Id == 0 || !wb.SideSolid[f.Opposite.Index]) continue;
                    if (world.SetDecor(dripRust, p, f.Opposite)) count++;
                    break;
                }
            }
            long ck = Key(cur.X, cur.Y, cur.Z);
            if (ck == startKey) break;
            long pk = prev[ck];
            cur = ((int)(pk >> 40) + x1, (int)((pk >> 20) & 0xFFFFF) + y1, (int)(pk & 0xFFFFF) + z1);
        }
        return count;
    }

    // ── mine machinery and giants ──────────────────────────────────────

    // machinery=N: every N steps, a stretch of tunnel wall is rusted plate
    // with a gearbox or pipe run standing against it and parts on the floor.
    // giants=<block>*K: K spawner blocks in the widest, tallest halls of the
    // cave, at least 30 blocks apart, against the hall's side.
    private string PlaceMineMachinery(IslandJob job, CaveDef def, List<(double X, double Y, double Z, double R, double V, CaveDef Def)> steps,
        System.Func<int, int, int, bool> open, System.Func<int, int, int, bool> solid)
    {
        var world = sapi.World.BlockAccessor;
        var p = new BlockPos(0, 0, 0, job.Dim);
        int Bid(string code) => sapi.World.GetBlock(new AssetLocation(code.Contains(':') ? code : "game:" + code))?.BlockId ?? 0;
        int rustA = Bid("metalblock-corroded-riveted-rusty-iron"), rustB = Bid("metalblock-corroded-plain-rusty-iron");
        int[] floorJunk = { Bid("loosegears-1"), Bid("loosegears-3"), Bid("metalpartpile-small"), Bid("metalpartpile-tiny"), Bid("metal-scraps") };
        string[] standing = { "gearbox1", "geartrain3", "gear24", "gear36", "pipelong3-aged", "pipelong4-aged" };
        int clutterId = Bid("clutter");
        var rotField = typeof(BEBehaviorShapeFromAttributes).GetField("<rotateY>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        void Put(int x, int y, int z, int id) { if (id == 0) return; p.Set(x, y, z); world.SetBlock(id, p); }
        void Clut(int x, int y, int z, string type, float rot)
        {
            if (clutterId == 0 || !open(x, y, z)) return;
            p.Set(x, y, z);
            world.SetBlock(clutterId, p);
            var beh = world.GetBlockEntity(p)?.GetBehavior<BEBehaviorShapeFromAttributes>();
            if (beh == null) return;
            beh.Type = type;
            rotField?.SetValue(beh, rot);
            beh.Blockentity.MarkDirty(true);
        }

        int machines = 0, giants = 0;
        var halls = new List<(int X, int Y, int Z, int Size)>();
        var rnd = new Random((int)(def.Seed * 7919u + 17u));
        for (int i = 1; i < steps.Count; i++)
        {
            var a = steps[i - 1]; var s = steps[i];
            double dx = s.X - a.X, dz = s.Z - a.Z, len = Math.Sqrt(dx * dx + dz * dz);
            if (len < 0.2 || len > 4) continue;
            dx /= len; dz /= len;
            bool acrossX = Math.Abs(dz) >= Math.Abs(dx);
            int cx = (int)Math.Floor(s.X), cz = (int)Math.Floor(s.Z), cy = (int)Math.Floor(s.Y);
            if (!open(cx, cy, cz)) continue;
            int floor = cy; while (floor > cy - 10 && open(cx, floor - 1, cz)) floor--;
            if (!solid(cx, floor - 1, cz)) continue;
            int ceil = cy; while (ceil < cy + 14 && open(cx, ceil + 1, cz)) ceil++;
            int height = ceil - floor + 1;
            int ux = acrossX ? 1 : 0, uz = acrossX ? 0 : 1;
            int left = 0; while (left < 10 && open(cx - ux * (left + 1), floor, cz - uz * (left + 1))) left++;
            int right = 0; while (right < 10 && open(cx + ux * (right + 1), floor, cz + uz * (right + 1))) right++;
            int width = left + right + 1;

            if (def.GiantCount > 0 && width >= 6 && height >= 5)
                halls.Add((cx - ux * left, floor, cz - uz * left, width * height));

            if (def.Machinery <= 0 || i % def.Machinery != def.Machinery / 3 || height < 3) continue;
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
            float rot = acrossX ? (sgn < 0 ? (float)(Math.PI / 2) : (float)(-Math.PI / 2)) : (sgn < 0 ? 0f : (float)Math.PI);
            if (open(ex, floor, ez)) Clut(ex, floor, ez, standing[rnd.Next(standing.Length)], rot);
            if (height >= 4 && open(ex, floor + 2, ez)) Clut(ex, floor + 2, ez, rnd.NextDouble() < 0.5 ? "pipelong3-aged" : "pipelong4-aged", rot + (float)(Math.PI / 2));
            int jx = ex - sgn * ux + ax * (rnd.Next(3) - 1), jz = ez - sgn * uz + az * (rnd.Next(3) - 1);
            int jid = floorJunk[rnd.Next(floorJunk.Length)];
            if (open(jx, floor, jz) && solid(jx, floor - 1, jz)) Put(jx, floor, jz, jid);
            machines++;
        }

        if (def.GiantCount > 0 && !string.IsNullOrEmpty(def.GiantCode))
        {
            int gid = Bid(def.GiantCode);
            if (gid == 0) return $" Giant spawner '{def.GiantCode}' is not in this game (is its mod installed?).";
            var chosen = new List<(int X, int Y, int Z, int Size)>();
            foreach (var h in halls.OrderByDescending(h => h.Size))
            {
                if (chosen.Count >= def.GiantCount) break;
                if (chosen.Any(c => (c.X - h.X) * (c.X - h.X) + (c.Z - h.Z) * (c.Z - h.Z) < 30 * 30)) continue;
                if (!open(h.X, h.Y, h.Z)) continue;
                chosen.Add(h);
            }
            foreach (var h in chosen)
            {
                Put(h.X, h.Y, h.Z, gid);
                giants++;
                sapi.Logger.Notification("[landmassgenerator] giant spawner {0} at HUD {1}, {2}, {3} (hall size {4})", def.GiantCode,
                    h.X - sapi.WorldManager.MapSizeX / 2, h.Y, h.Z - sapi.WorldManager.MapSizeZ / 2, h.Size);
            }
        }
        return (machines > 0 ? $" {machines} wall machine(s)." : "") + (giants > 0 ? $" {giants} giant spawner(s)." : "");
    }

    // ── story chests ───────────────────────────────────────────────────

    // Plan line: storychest <storycode> <contents>. Once the game has built
    // that story structure (it does so when a player first comes near), the
    // items go into its top entrance chest: the highest chest within the
    // structure's footprint that has a heavy door within 16 blocks (the
    // Resonance Archives' surface entrance). Done once per world.
    private List<(string Code, string Contents)> _storyChests;
    private long _storyChestListener;
    private bool _storyChestForce;   // /genstorychests: skip the player-nearby test

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
            _storyChests = saved.Select(s => { int bar = s.IndexOf('|'); return (s.Substring(0, bar), s.Substring(bar + 1)); }).ToList();
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
            if (!allLoaded) { if (_storyChestForce) sapi.Logger.Notification("[landmassgenerator] story chest {0}: its area is not loaded yet", sc.Code); continue; }

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
            var target = candidates.OrderByDescending(c => c.Pos.Y).FirstOrDefault(c => NearHeavyDoor(c.Pos));
            if (target == null) { if (_storyChestForce) sapi.Logger.Notification("[landmassgenerator] story chest {0}: {1} chest(s) in the area, none by a heavy door", sc.Code, candidates.Count); continue; }

            var notes = new List<string>();
            int put = FillChest(target, sc.Contents.Split(',', StringSplitOptions.RemoveEmptyEntries), notes);
            sapi.WorldManager.SaveGame.StoreData(doneKey, true);
            _storyChests.Remove(sc);
            sapi.Logger.Notification("[landmassgenerator] story chest {0}: {1} stack(s) added to the chest at HUD {2}, {3}, {4}{5}", sc.Code, put,
                target.Pos.X - sapi.WorldManager.MapSizeX / 2, target.Pos.Y, target.Pos.Z - sapi.WorldManager.MapSizeZ / 2,
                notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : "");
        }
    }
}

// A chart with one plan island circled in red. Use it (right click) and the
// island gets a red X on your world map, the same way a Better Ruins or
// Great Machine locator map marks its find. Which island: the item's
// islandShape attribute, matched against the world plan's shape names.
public class ItemIslandMap : Item
{
    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
        if (slot.Empty) return;
        handling = EnumHandHandling.PreventDefault;
        if (api.Side != EnumAppSide.Server) return;
        if ((byEntity as EntityPlayer)?.Player is not IServerPlayer plr) return;

        string shape = Attributes?["islandShape"]?.AsString();
        string label = Lang.Get(Attributes?["waypointText"]?.AsString() ?? shape ?? "?");
        Vec3d pos = api.ModLoader.GetModSystem<LandmassGeneratorModSystem>()?.FindPlanIsland(shape);
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
}
