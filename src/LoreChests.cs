using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// Chests with contents: lore books, maps-as-notes, Jonas parts, anything.
//
//   chest <char> <type> <contents> [cave=<char>] [near=<char>] [at=gx,gz] [rust=N] [trunk=1]
//
// <type> is a vanilla chest type: normal-aged, collapsed1..4, golden-aged,
// owl-aged, normal-generic. <contents> is a comma-separated list:
//   lore:<id>                a written, read-only book from lore/<id>.txt
//   randomlore:<category>    a vanilla lore book revealing a random unread page of that category
//   <domain:code>[*count]    any item or block, e.g. game:jonasparts-tank01*2
// Placement: the <char> drawn on the map, OR at=gx,gz (a grid cell), OR
// near=<char> (beside the first cell of that marker, e.g. a landmark tree),
// OR cave=<char> (on the floor of that cave's deepest reachable step).
// rust=N stains N blocks around it with devastated soil.
//
// Lore files live in <LandmassGenerator folder>/lore/<id>.txt:
//   title: Letter to Seraph
//   by: Jonas Falx
//   book: aged-orangebrown
//   ---
//   the text; the game splits it into pages itself
public partial class LandmassGeneratorModSystem
{
    private class ChestDef
    {
        public char Key;
        public string Type = "normal-aged";
        public bool Trunk;
        public List<string> Contents = new();
        public char Cave, Near;
        public int AtX = -1, AtZ = -1;
        public int Rust;
    }

    private class ChestPlacement
    {
        public ChestDef Def;
        public int Gx = -1, Gz = -1;   // grid cell, or -1 for a cave chest
        public CaveDef Cave;
    }

    private ChestDef ParseChest(string[] tok, List<string> problems)
    {
        var d = new ChestDef { Key = tok[1][0] };
        if (tok.Length > 2) d.Type = tok[2];
        for (int i = 3; i < tok.Length; i++)
        {
            string t = tok[i];
            int eq = t.IndexOf('=');
            if (eq < 0)
            {
                d.Contents.AddRange(t.Split(',', StringSplitOptions.RemoveEmptyEntries));
                continue;
            }
            string k = t.Substring(0, eq).ToLowerInvariant(), v = t.Substring(eq + 1);
            switch (k)
            {
                case "cave": d.Cave = v.Length > 0 ? v[0] : '\0'; break;
                case "near": d.Near = v.Length > 0 ? v[0] : '\0'; break;
                case "rust": d.Rust = int.TryParse(v, out int r) ? Math.Clamp(r, 0, 8) : 0; break;
                case "trunk": d.Trunk = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "at":
                    var p = v.Split(',');
                    if (p.Length == 2 && int.TryParse(p[0], out int ax) && int.TryParse(p[1], out int az)) { d.AtX = ax; d.AtZ = az; }
                    else problems.Add($"chest {d.Key}: at= wants gx,gz, got '{v}'");
                    break;
                default: problems.Add($"chest {d.Key}: unknown key '{k}'"); break;
            }
        }
        return d;
    }

    // After the map is read: chests placed by at=, near= or cave= rather
    // than by drawing their character on the map.
    private void ResolveChestPlacements(ShapeDef shape, Dictionary<char, ChestDef> chestDefs, Dictionary<char, CaveDef> caveDefs,
        Dictionary<char, (int X, int Z)> firstCell, List<string> problems)
    {
        foreach (var d in chestDefs.Values)
        {
            if (shape.Chests.Any(c => c.Def == d)) continue;   // drawn on the map
            if (d.Cave != '\0')
            {
                if (caveDefs.TryGetValue(d.Cave, out CaveDef cd)) shape.Chests.Add(new ChestPlacement { Def = d, Cave = cd });
                else problems.Add($"chest {d.Key}: no cave '{d.Cave}'");
            }
            else if (d.AtX >= 0)
            {
                shape.Chests.Add(new ChestPlacement { Def = d, Gx = d.AtX, Gz = d.AtZ });
            }
            else if (d.Near != '\0')
            {
                if (!firstCell.TryGetValue(d.Near, out var at)) { problems.Add($"chest {d.Key}: no map cell '{d.Near}'"); continue; }
                // Two cells toward the island centre, so it sits beside the
                // landmark, not under its trunk or out on the beach.
                double dx = shape.W / 2.0 - at.X, dz = shape.H / 2.0 - at.Z, len = Math.Max(1, Math.Sqrt(dx * dx + dz * dz));
                shape.Chests.Add(new ChestPlacement { Def = d, Gx = at.X + (int)Math.Round(2 * dx / len), Gz = at.Z + (int)Math.Round(2 * dz / len) });
            }
            else problems.Add($"chest {d.Key}: not on the map and no at=, near= or cave=");
        }
    }

    // Places every chest of a job. Needs the cave steps of the job's caves
    // (job.CaveSteps, from the real carve or a dry run) for cave chests.
    // Skips any spot that already has a chest within 3 blocks (re-runs).
    private string PlaceChests(IslandJob job)
    {
        var chests = job.Shape?.Chests;
        if (chests == null || chests.Count == 0) return "";
        var world = sapi.World.BlockAccessor;
        int placed = 0, books = 0, skipped = 0;
        var notes = new List<string>();

        foreach (var cp in chests)
        {
            BlockPos at = cp.Cave != null ? CaveChestPos(job, cp.Cave) : GroundChestPos(job, cp.Gx, cp.Gz);
            if (at == null) { notes.Add($"chest {cp.Def.Key}: no place to stand"); continue; }
            if (HasChestNear(world, at)) { skipped++; continue; }

            Block chestBlock = sapi.World.GetBlock(new AssetLocation("game", cp.Def.Trunk ? "trunk-east" : "chest-east"))
                ?? sapi.World.GetBlock(new AssetLocation("game", cp.Def.Trunk ? "trunk" : "chest"));
            if (chestBlock == null) { notes.Add("no chest block in this game"); break; }

            if (cp.Def.Rust > 0) StainRust(world, at, cp.Def.Rust);
            world.SetBlock(chestBlock.BlockId, at);
            if (world.GetBlockEntity(at) is not BlockEntityGenericTypedContainer be) { notes.Add($"chest {cp.Def.Key}: no container entity"); continue; }
            var typeStack = new ItemStack(chestBlock);
            typeStack.Attributes.SetString("type", cp.Def.Type);
            be.OnBlockPlaced(typeStack);

            int slot = 0;
            foreach (string spec in cp.Def.Contents)
            {
                ItemStack stack = MakeChestStack(spec, notes);
                if (stack == null) continue;
                if (slot >= be.Inventory.Count) { notes.Add($"chest {cp.Def.Key}: full"); break; }
                be.Inventory[slot].Itemstack = stack;
                be.Inventory[slot].MarkDirty();
                slot++;
                if (spec.StartsWith("lore:")) books++;
            }
            be.MarkDirty(true);
            placed++;
            // Read the chest back, so the log proves what it holds.
            int held = (world.GetBlockEntity(at) as BlockEntityGenericTypedContainer)?.Inventory.Count(sl => !sl.Empty) ?? -1;
            sapi.Logger.Notification("[landmassgenerator] chest {0} ({1}) at HUD {2}, {3}, {4} holds {5} stack(s): {6}", cp.Def.Key, cp.Def.Type,
                at.X - sapi.WorldManager.MapSizeX / 2, at.Y, at.Z - sapi.WorldManager.MapSizeZ / 2, held, string.Join(", ", cp.Def.Contents));
        }

        string note = placed > 0 ? $" Placed {placed} chest(s) holding {books} lore book(s)." : "";
        if (skipped > 0) note += $" {skipped} chest(s) were already there.";
        if (notes.Count > 0) note += " Chest notes: " + string.Join("; ", notes.Distinct()) + ".";
        return note;
    }

    private static bool HasChestNear(IBlockAccessor world, BlockPos at)
    {
        var p = new BlockPos(0, 0, 0, at.dimension);
        for (int dx = -3; dx <= 3; dx++)
            for (int dy = -2; dy <= 2; dy++)
                for (int dz = -3; dz <= 3; dz++)
                {
                    p.Set(at.X + dx, at.Y + dy, at.Z + dz);
                    string path = world.GetBlock(p)?.Code?.Path;
                    if (path != null && (path.StartsWith("chest") || path.StartsWith("trunk"))) return true;
                }
        return false;
    }

    // Top of the ground at a grid cell: skips plants, leaves and logs so a
    // chest beside a tree lands on the forest floor, not in the canopy.
    private BlockPos GroundChestPos(IslandJob job, int gx, int gz)
    {
        MarkerWorld(job, gx, gz, out int x, out int z);
        var world = sapi.World.BlockAccessor;
        var pos = new BlockPos(x, 0, z, job.Dim);
        int floor = Math.Max(1, job.SeaLevel - 2);
        for (int y = Math.Min(sapi.WorldManager.MapSizeY - 3, job.SeaLevel + job.DomeHeight + 30); y >= floor; y--)
        {
            pos.Y = y;
            Block b = world.GetBlock(pos, BlockLayersAccess.SolidBlocks);
            if (b == null || b.Id == 0) continue;
            if (b.BlockMaterial is EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves or EnumBlockMaterial.Wood) continue;
            if (y < job.SeaLevel - 1) return null;          // under water
            return new BlockPos(x, y + 1, z, job.Dim);
        }
        return null;
    }

    // On the floor of the deepest carved step of a cave line that has air
    // to stand in: "in the depths of the mine".
    private BlockPos CaveChestPos(IslandJob job, CaveDef cave)
    {
        if (job.CaveSteps == null) return null;
        var world = sapi.World.BlockAccessor;
        var p = new BlockPos(0, 0, 0, job.Dim);
        foreach (var s in job.CaveSteps.Where(s => s.Def == cave).OrderBy(s => s.Y))
        {
            int x = (int)Math.Floor(s.X), z = (int)Math.Floor(s.Z);
            for (int y = (int)Math.Floor(s.Y); y >= (int)Math.Floor(s.Y - s.V - 2); y--)
            {
                p.Set(x, y, z);
                Block b = world.GetBlock(p, BlockLayersAccess.SolidBlocks);
                if (b == null || b.Id == 0) continue;
                p.Set(x, y + 1, z);
                bool air = (world.GetBlock(p, BlockLayersAccess.SolidBlocks)?.Id ?? 1) == 0
                    && (world.GetBlock(p, BlockLayersAccess.Fluid)?.Id ?? 1) == 0;
                p.Set(x, y + 2, z);
                bool head = (world.GetBlock(p, BlockLayersAccess.SolidBlocks)?.Id ?? 1) == 0;
                if (air && head) return new BlockPos(x, y + 1, z, job.Dim);
                break;
            }
        }
        return null;
    }

    private void StainRust(IBlockAccessor world, BlockPos at, int r)
    {
        var soil = new List<Block>();
        for (int n = 0; n < 4; n++)
        {
            var b = sapi.World.GetBlock(new AssetLocation("game", "devastatedsoil-" + n));
            if (b != null) soil.Add(b);
        }
        if (soil.Count == 0) return;
        var p = new BlockPos(0, 0, 0, at.dimension);
        var rnd = new Random(at.X * 31 + at.Z * 17 + at.Y);
        for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (dx * dx + dz * dz > r * r || rnd.NextDouble() < 0.25) continue;
                for (int dy = 1; dy >= -2; dy--)
                {
                    p.Set(at.X + dx, at.Y - 1 + dy, at.Z + dz);
                    Block b = world.GetBlock(p, BlockLayersAccess.SolidBlocks);
                    if (b == null || b.Id == 0 || b.BlockMaterial is EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves or EnumBlockMaterial.Wood) continue;
                    world.SetBlock(soil[rnd.Next(soil.Count)].BlockId, p);
                    break;
                }
            }
    }

    private ItemStack MakeChestStack(string spec, List<string> notes)
    {
        if (spec.StartsWith("lore:", StringComparison.OrdinalIgnoreCase)) return MakeLoreBook(spec.Substring(5), notes);
        if (spec.StartsWith("randomlore:", StringComparison.OrdinalIgnoreCase))
        {
            // A vanilla lore item: reading it reveals the next unread page of
            // a journal in this category (vanilla's own, Better Ruins', or
            // the Rustfall Lore mod's). randomlore:<category>[:<item code>]
            string[] parts = spec.Split(':');
            string category = parts.Length > 1 ? parts[1] : "jonas";
            string loreCode = parts.Length > 2 ? string.Join(":", parts.Skip(2)) : "game:lore-book-aged-orangebrown";
            Item item = sapi.World.GetItem(new AssetLocation(loreCode));
            if (item == null) { notes.Add($"'{loreCode}' is not in this game"); return null; }
            var st = new ItemStack(item);
            st.Attributes.SetString("category", category);
            return st;
        }
        int count = 1;
        string code = spec;
        int star = spec.LastIndexOf('*');
        if (star > 0 && int.TryParse(spec.Substring(star + 1), out int c)) { count = Math.Max(1, c); code = spec.Substring(0, star); }
        var loc = new AssetLocation(code);
        CollectibleObject obj = (CollectibleObject)sapi.World.GetItem(loc) ?? sapi.World.GetBlock(loc);
        if (obj == null) { notes.Add($"'{code}' is not in this game"); return null; }
        return new ItemStack(obj, Math.Min(count, Math.Max(1, obj.MaxStackSize)));
    }

    private ItemStack MakeLoreBook(string id, List<string> notes)
    {
        string file = Path.Combine(shapeFolder, "lore", id + ".txt");
        if (!File.Exists(file)) { notes.Add($"no lore file lore/{id}.txt"); return null; }
        string title = id, by = null, color = "aged-orangebrown";
        var text = new List<string>();
        bool body = false;
        foreach (string raw in File.ReadAllLines(file))
        {
            string line = raw.TrimEnd('\r');
            if (!body)
            {
                if (line.Trim() == "---") { body = true; continue; }
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                string k = line.Substring(0, c).Trim().ToLowerInvariant(), v = line.Substring(c + 1).Trim();
                if (k == "title") title = v;
                else if (k == "by") by = v;
                else if (k == "book") color = v;
                continue;
            }
            text.Add(line);
        }
        Item book = sapi.World.GetItem(new AssetLocation("game", "book-" + color)) ?? sapi.World.GetItem(new AssetLocation("game", "book-aged-orangebrown"));
        if (book == null) { notes.Add("no book item in this game"); return null; }
        var stack = new ItemStack(book);
        stack.Attributes.SetString("title", title);
        stack.Attributes.SetString("text", string.Join("\n", text).Trim());
        if (!string.IsNullOrEmpty(by)) stack.Attributes.SetString("signedby", by);
        return stack;
    }

    // Marker blocks added to a shape after its world was generated (the
    // anchors): place each one unless the same block already stands within
    // a few blocks of its spot, so spawners are never doubled.
    private string PlaceMissingMarkerBlocks(IslandJob job)
    {
        var world = sapi.World.BlockAccessor;
        int placed = 0;
        foreach (var m in job.Shape.BlockMarkers)
        {
            Block block = sapi.World.GetBlock(new AssetLocation(m.Code));
            if (block == null) continue;
            MarkerWorld(job, m.Gx, m.Gz, out int x, out int z);
            var pos = new BlockPos(x, 0, z, job.Dim);
            bool present = false;
            var q = new BlockPos(0, 0, 0, job.Dim);
            for (int dx = -3; dx <= 3 && !present; dx++)
                for (int dz = -3; dz <= 3 && !present; dz++)
                    for (int y = Math.Max(1, job.SeaLevel - job.MaxDepth); y <= job.SeaLevel + job.DomeHeight + 12 && !present; y++)
                    {
                        q.Set(x + dx, y, z + dz);
                        if (world.GetBlock(q)?.Id == block.Id) present = true;
                    }
            if (present) continue;
            int floor = Math.Max(1, job.SeaLevel - job.MaxDepth);
            for (int y = job.SeaLevel + job.DomeHeight + 8; y >= floor; y--)
            {
                pos.Y = y;
                Block b = world.GetBlock(pos, BlockLayersAccess.SolidBlocks);
                if (b == null || b.Id == 0 || b.BlockMaterial is EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves) continue;
                int py = y + 1 + m.Up;
                if (y < job.SeaLevel - 1) py = Math.Min(py, job.SeaLevel - 3);
                pos.Y = Math.Max(py, y + 1);
                world.SetBlock(block.BlockId, pos);
                placed++;
                break;
            }
        }
        return placed > 0 ? $" Placed {placed} new marker block(s)." : "";
    }

    // Structure kinds added after the Rustfall world was first generated.
    // /genchests builds these (and only these) where they are missing, so an
    // existing world gets the lab and the hut without any megastructure
    // being rebuilt. Each has a signature block that says "already built".
    private static readonly HashSet<string> NewStructKinds = new() { "lab", "hut" };

    private string BuildNewStructs(IslandJob job)
    {
        int built = 0;
        var world = sapi.World.BlockAccessor;
        foreach (var sm in job.Shape.Structs.Where(s => NewStructKinds.Contains(s.Def.Kind)))
        {
            double lx = (sm.Gx + 0.5 - job.Shape.W / 2.0) * job.WorldPerCell;
            double lz = (sm.Gz + 0.5 - job.Shape.H / 2.0) * job.WorldPerCell;
            int cx = job.Cx + (int)Math.Round(lx * job.RotCos - lz * job.RotSin);
            int cz = job.Cz + (int)Math.Round(lx * job.RotSin + lz * job.RotCos);
            string sig = sm.Def.Kind == "lab" ? "glass-plain" : "table-aged";
            int sigId = sapi.World.GetBlock(new AssetLocation("game", sig))?.BlockId ?? -1;
            bool present = false;
            var p = new BlockPos(0, 0, 0, job.Dim);
            for (int dx = -12; dx <= 12 && !present; dx++)
                for (int dz = -12; dz <= 12 && !present; dz++)
                    for (int y = job.SeaLevel - 10; y <= job.SeaLevel + job.DomeHeight + 20 && !present; y++)
                    {
                        p.Set(cx + dx, y, cz + dz);
                        if (world.GetBlock(p)?.BlockId == sigId) present = true;
                    }
            if (present) continue;
            BuildStruct(job, sm.Def, cx, cz);
            built++;
        }
        return built > 0 ? $" Built {built} new structure(s)." : "";
    }

    // /genchests: place the plan islands' chests into a world that was
    // generated before they existed (or refill after edits). Re-running is
    // safe: a spot with a chest already near it is skipped.
    private TextCommandResult OnGenChests(TextCommandCallingArgs args)
    {
        if (_wgIslandJobs == null || _wgIslandJobs.Count == 0) return TextCommandResult.Error("This world has no plan islands.");
        var todo = _wgIslandJobs.Where(j => j.Shape?.Chests.Count > 0 || j.Shape?.BlockMarkers.Count > 0 || j.Shape?.Structs.Any(s => NewStructKinds.Contains(s.Def.Kind)) == true || j.Shape?.Caves.Any(c => c.Def.Timbers > 0 || c.Def.Holders > 0) == true).ToList();
        if (todo.Count == 0) return TextCommandResult.Error("No plan island has chests or marker blocks in its shape file.");
        GenChestsNext(todo, 0, new List<string>());
        return TextCommandResult.Success($"Placing chests on {todo.Count} island(s); results follow in chat.");
    }

    private void GenChestsNext(List<IslandJob> todo, int idx, List<string> report)
    {
        if (idx >= todo.Count)
        {
            sapi.BroadcastMessageToAllGroups("[genchests] Done." + string.Concat(report), EnumChatType.Notification);
            return;
        }
        var job = todo[idx];
        // Cave chests need the cave steps; the walk is world-independent.
        if (job.Shape.Chests.Any(c => c.Cave != null) || job.Shape.Caves.Any(c => c.Def.Timbers > 0 || c.Def.Holders > 0))
        {
            var dry = new CaveWork { DryRun = true };
            CarveCaves(job, dry);
            job.CaveSteps = dry.Steps;
        }
        // Load every chest's surroundings (and the caves), then place.
        int cs = GlobalConstants.ChunkSize;
        int x1 = int.MaxValue, z1 = int.MaxValue, x2 = int.MinValue, z2 = int.MinValue;
        if (job.CaveSteps != null && job.Shape.Caves.Any(c => c.Def.Timbers > 0 || c.Def.Holders > 0))
            foreach (var s in job.CaveSteps)
            { x1 = Math.Min(x1, (int)s.X); x2 = Math.Max(x2, (int)s.X); z1 = Math.Min(z1, (int)s.Z); z2 = Math.Max(z2, (int)s.Z); }
        foreach (var cp in job.Shape.Chests)
        {
            if (cp.Cave != null && job.CaveSteps != null)
            {
                foreach (var s in job.CaveSteps.Where(s => s.Def == cp.Cave))
                { x1 = Math.Min(x1, (int)s.X); x2 = Math.Max(x2, (int)s.X); z1 = Math.Min(z1, (int)s.Z); z2 = Math.Max(z2, (int)s.Z); }
            }
            else
            {
                MarkerWorld(job, cp.Gx, cp.Gz, out int x, out int z);
                x1 = Math.Min(x1, x); x2 = Math.Max(x2, x); z1 = Math.Min(z1, z); z2 = Math.Max(z2, z);
            }
        }
        foreach (var m in job.Shape.BlockMarkers)
        {
            MarkerWorld(job, m.Gx, m.Gz, out int x, out int z);
            x1 = Math.Min(x1, x); x2 = Math.Max(x2, x); z1 = Math.Min(z1, z); z2 = Math.Max(z2, z);
        }
        foreach (var sm in job.Shape.Structs.Where(s => NewStructKinds.Contains(s.Def.Kind)))
        {
            MarkerWorld(job, sm.Gx, sm.Gz, out int x, out int z);
            x1 = Math.Min(x1, x - 24); x2 = Math.Max(x2, x + 24); z1 = Math.Min(z1, z - 24); z2 = Math.Max(z2, z + 24);
        }
        if (x2 < x1) { GenChestsNext(todo, idx + 1, report); return; }
        // KeepLoaded: with a plain load the first bands of a big area can
        // unload again before the last one arrives, and unloaded chunks read
        // as air: deep cave floors vanish and existing spawners look missing.
        int lcx1 = FloorDiv(x1, cs) - 1, lcz1 = FloorDiv(z1, cs) - 1, lcx2 = FloorDiv(x2, cs) + 1, lcz2 = FloorDiv(z2, cs) + 1;
        LoadChunkAreaBanded(lcx1, lcz1, lcx2, lcz2, true, () =>
        {
            string r;
            try { r = BuildNewStructs(job) + PlaceChests(job) + PlaceMissingMarkerBlocks(job) + PlaceCaveProps(job, skipIfDone: true); }
            catch (Exception e) { r = " FAILED: " + e.Message; sapi.Logger.Error(e); }
            job.PreCx1 = lcx1; job.PreCz1 = lcz1; job.PreCx2 = lcx2; job.PreCz2 = lcz2;
            ReleaseKeptChunks(job);
            report.Add($" {job.ShapeName}:{(r.Length > 0 ? r : " nothing to place.")}");
            sapi.Event.RegisterCallback(_ => GenChestsNext(todo, idx + 1, report), 500);
        });
    }
}
