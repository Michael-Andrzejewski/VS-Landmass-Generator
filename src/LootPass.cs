using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// Loot that finds its own place. Two shape lines:
//
//   loot <table> <count> [near=<char>] [radius=N] [where=inside|surface|underwater|any] [cave=<char>] [type=<chest type>] [above=N] [below=N]
//   armorstand <set> <count> [near=<char>] [radius=N] [where=inside|surface|any]
//
// After every structure is built, the pass looks at the real blocks around
// the marker (near=, any map char: a struct, bastion, tree...; default the
// island centre and its whole radius) for floor cells with two blocks of
// room: "inside" has a roof within 10 blocks and walls on two sides (castle
// rooms, a lighthouse floor, a hull), "surface" is open sky, "underwater" a
// sea or lake floor. cave=<char> spreads the chests along that cave
// instead. Spots are spread out; when too few of the asked kind exist, the
// rest go to any floor in the area. Chests hold a roll of the named loot
// table (see LootTables), every item checked against the game first.
public partial class LandmassGeneratorModSystem
{
    private class LootDef
    {
        public string Table;
        public int Count = 1;
        public char Near;
        public int Radius;
        public string Where = "any";
        public char Cave;
        public string Type;
        public bool Stand;   // armorstand line: Table is the armor set
        public CaveDef CaveDef;   // cave=, resolved when the shape is read
        public int? Above, Below;  // above=N / below=N: only floors at least N over / under sea level
    }

    private static LootDef ParseLoot(string[] tok, bool stand, List<string> problems)
    {
        var d = new LootDef { Stand = stand, Table = tok.Length > 1 ? tok[1].ToLowerInvariant() : "" };
        if (tok.Length > 2 && int.TryParse(tok[2], out int n)) d.Count = Math.Clamp(n, 1, 40);
        for (int i = 3; i < tok.Length; i++)
        {
            int eq = tok[i].IndexOf('=');
            if (eq <= 0) { problems.Add($"{tok[0]}: expected key=value, got '{tok[i]}'"); continue; }
            string k = tok[i].Substring(0, eq).ToLowerInvariant(), v = tok[i].Substring(eq + 1);
            switch (k)
            {
                case "near": d.Near = v.Length > 0 ? v[0] : '\0'; break;
                case "radius": d.Radius = (int)Math.Clamp(ParseD(v, 0), 0, 400); break;
                case "where": d.Where = v.ToLowerInvariant(); break;
                case "cave": d.Cave = v.Length > 0 ? v[0] : '\0'; break;
                case "type": d.Type = v; break;
                case "above": d.Above = (int)ParseD(v, 0); break;
                case "below": d.Below = (int)ParseD(v, 0); break;
                default: problems.Add($"{tok[0]}: unknown key '{k}'"); break;
            }
        }
        if (!stand && !LootTables.ContainsKey(d.Table)) problems.Add($"loot: unknown table '{d.Table}' ({string.Join(", ", LootTables.Keys)})");
        if (stand && !ArmorSets.ContainsKey(d.Table)) problems.Add($"armorstand: unknown set '{d.Table}' ({string.Join(", ", ArmorSets.Keys)})");
        return d;
    }

    // ── tables ─────────────────────────────────────────────────────────
    // (code, min, max, weight); a chest gets 4 to 6 distinct entries.

    private static readonly Dictionary<string, (string Code, int Min, int Max, double W)[]> LootTables = new()
    {
        ["fortress"] = new (string, int, int, double)[]
        {
            ("game:arrow-iron", 6, 16, 2), ("game:arrow-copper", 8, 20, 2), ("game:arrow-tinbronze", 6, 16, 1.5),
            ("game:spear-generic-iron", 1, 1, 1), ("game:spear-generic-tinbronze", 1, 1, 1), ("game:spear-generic-copper", 1, 1, 1),
            ("game:blade-falx-iron", 1, 1, 0.6), ("game:blade-falx-tinbronze", 1, 1, 0.8), ("game:blade-blackguard-iron", 1, 1, 0.4),
            ("game:armor-head-chain-iron", 1, 1, 0.5), ("game:armor-body-chain-iron", 1, 1, 0.4), ("game:armor-legs-chain-iron", 1, 1, 0.5),
            ("game:armor-body-brigandine-iron", 1, 1, 0.3), ("game:armor-head-scale-tinbronze", 1, 1, 0.5),
            ("game:ingot-iron", 1, 3, 1.2), ("game:metalnailsandstrips-iron", 3, 8, 1.2), ("game:rope", 2, 5, 1.2),
            ("game:candle", 2, 6, 1.2), ("game:flaxtwine", 3, 8, 1), ("game:gear-rusty", 1, 2, 0.8), ("game:bow-long", 1, 1, 0.5),
        },
        ["treasury"] = new (string, int, int, double)[]
        {
            ("game:ingot-gold", 2, 5, 3), ("game:ingot-silver", 2, 6, 3), ("game:nugget-nativegold", 4, 12, 1.5),
            ("game:nugget-nativesilver", 4, 12, 1.5), ("game:gem-emerald-rough", 1, 2, 1), ("game:gem-diamond-rough", 1, 1, 0.6),
            ("game:gem-olivine_peridot-rough", 1, 3, 1), ("game:spear-generic-ornategold", 1, 1, 0.6), ("game:spear-generic-ornatesilver", 1, 1, 0.6),
            ("game:blade-falx-gold", 1, 1, 0.4), ("game:blade-falx-silver", 1, 1, 0.4), ("game:arrow-gold", 4, 10, 0.6),
        },
        ["lighthouse"] = new (string, int, int, double)[]
        {
            ("game:candle", 4, 10, 2), ("game:rope", 2, 6, 1.5), ("game:fat", 2, 6, 1.2), ("game:beeswax", 2, 6, 1.2),
            ("game:flaxtwine", 3, 8, 1), ("game:fishingpole-simple-wood", 1, 1, 0.8), ("game:oar-crude-aged", 1, 1, 0.8), ("game:ingot-copper", 1, 2, 0.8),
            ("game:knife-generic-copper", 1, 1, 0.6), ("game:gear-rusty", 1, 2, 0.6), ("game:glass-plain", 2, 6, 0.6), ("game:firewood", 6, 16, 1),
        },
        ["forge"] = new (string, int, int, double)[]
        {
            ("game:ingot-copper", 2, 6, 2), ("game:ingot-tinbronze", 2, 4, 1.6), ("game:ingot-iron", 1, 3, 1.4), ("game:ingot-steel", 1, 1, 0.5),
            ("game:ingot-gold", 1, 2, 0.6), ("game:metalplate-iron", 1, 2, 0.8), ("game:hammer-iron", 1, 1, 0.8), ("game:tongsmetal-standard-iron", 1, 1, 0.6),
            ("game:chisel-iron", 1, 1, 0.6), ("game:coke", 6, 16, 1.4), ("game:charcoal", 8, 24, 1.4), ("game:metalbit-iron", 6, 16, 1),
            ("game:metalbit-tinbronze", 6, 16, 1), ("game:metalnailsandstrips-iron", 3, 8, 1),
        },
        ["mine"] = new (string, int, int, double)[]
        {
            ("game:pickaxe-copper", 1, 1, 0.8), ("game:pickaxe-tinbronze", 1, 1, 0.6), ("game:prospectingpick-copper", 1, 1, 0.6),
            ("game:candle", 2, 6, 1.4), ("game:rope", 2, 5, 1.2), ("game:nugget-nativecopper", 6, 16, 1.5), ("game:nugget-malachite", 6, 14, 1),
            ("game:nugget-cassiterite", 4, 12, 0.8), ("game:charcoal", 6, 16, 1), ("game:metalnailsandstrips-copper", 3, 8, 1),
            ("game:shovel-copper", 1, 1, 0.5), ("game:gear-rusty", 1, 2, 0.6), ("game:firewood", 6, 16, 0.8),
        },
        ["ironmine"] = new (string, int, int, double)[]
        {
            ("game:pickaxe-iron", 1, 1, 0.6), ("game:ingot-iron", 1, 4, 1.4), ("game:nugget-limonite", 6, 16, 1.4), ("game:nugget-hematite", 6, 16, 1.2),
            ("game:nugget-magnetite", 6, 14, 1), ("game:ironbloom", 1, 2, 0.6), ("game:metalbit-iron", 6, 16, 1), ("game:coke", 6, 16, 1),
            ("game:charcoal", 8, 20, 1), ("game:prospectingpick-iron", 1, 1, 0.5), ("game:candle", 2, 6, 1), ("game:rope", 2, 5, 1),
        },
        ["swamp"] = new (string, int, int, double)[]
        {
            ("game:nugget-limonite", 6, 16, 1.6), ("game:cattailroot", 4, 10, 1), ("game:flaxtwine", 3, 8, 1), ("game:rope", 2, 4, 1),
            ("game:arrow-flint", 6, 12, 1), ("game:spear-generic-flint", 1, 1, 0.7), ("game:gear-rusty", 1, 2, 0.8), ("game:metal-scraps", 1, 3, 0.8),
            ("game:candle", 2, 4, 0.8), ("game:fat", 2, 5, 0.8), ("game:knife-generic-copper", 1, 1, 0.6),
        },
        ["wreck"] = new (string, int, int, double)[]
        {
            ("game:rope", 3, 8, 1.6), ("game:sail", 1, 3, 0.8), ("game:oar-crude-aged", 1, 1, 0.8), ("game:metalnailsandstrips-iron", 3, 8, 1.2),
            ("game:gear-rusty", 1, 3, 1.2), ("game:jonasparts-connector01", 1, 1, 0.6), ("game:jonasparts-valve01", 1, 1, 0.6),
            ("game:ingot-tinbronze", 1, 2, 0.8), ("game:ingot-gold", 1, 1, 0.4), ("game:candle", 2, 5, 1), ("game:metal-scraps", 1, 3, 1),
            ("game:arrow-iron", 4, 12, 0.8), ("game:metalchain-iron", 1, 2, 0.6),
        },
        ["sunken"] = new (string, int, int, double)[]
        {
            ("game:ingot-gold", 1, 3, 1.4), ("game:ingot-silver", 1, 3, 1.4), ("game:nugget-nativegold", 3, 10, 1), ("game:gem-emerald-rough", 1, 1, 0.5),
            ("game:gem-olivine_peridot-rough", 1, 2, 0.6), ("game:gear-rusty", 1, 3, 1), ("game:metal-scraps", 1, 3, 1), ("game:metalchain-iron", 1, 2, 0.8),
            ("game:jonasparts-tank01", 1, 1, 0.5), ("game:jonasparts-cylinder02", 1, 1, 0.5), ("game:arrow-blackbronze", 4, 10, 0.5),
        },
        ["granary"] = new (string, int, int, double)[]
        {
            ("game:grain-spelt", 8, 24, 1.4), ("game:grain-rye", 8, 24, 1.2), ("game:grain-flax", 6, 16, 1), ("game:flaxfibers", 4, 12, 1),
            ("game:drygrass", 8, 20, 1), ("game:scythe-copper", 1, 1, 0.6), ("game:hoe-copper", 1, 1, 0.6), ("game:candle", 2, 4, 0.8),
            ("game:rope", 2, 4, 0.8), ("game:flaxtwine", 3, 8, 0.8), ("game:seeds-turnip", 4, 10, 0.8), ("game:seeds-carrot", 4, 10, 0.8),
        },
        ["ruin"] = new (string, int, int, double)[]
        {
            ("game:arrow-copper", 6, 16, 1.2), ("game:spear-generic-copper", 1, 1, 0.8), ("game:ingot-copper", 1, 3, 1), ("game:ingot-silver", 1, 2, 0.5),
            ("game:candle", 2, 6, 1), ("game:rope", 2, 4, 1), ("game:gear-rusty", 1, 3, 1.2), ("game:metal-scraps", 1, 3, 1),
            ("game:nugget-nativecopper", 6, 14, 1), ("game:flaxtwine", 3, 8, 0.8), ("game:jonasparts-valve01", 1, 1, 0.5),
        },
    };

    // armor sets for stands: head, body, legs, then what it holds (first found of each)
    private static readonly Dictionary<string, string[][]> ArmorSets = new()
    {
        ["guard"] = new[]
        {
            new[] { "game:armor-head-chain-iron", "game:armor-head-chain-tinbronze" },
            new[] { "game:armor-body-chain-iron", "game:armor-body-brigandine-iron", "game:armor-body-chain-tinbronze" },
            new[] { "game:armor-legs-chain-iron", "game:armor-legs-chain-tinbronze" },
            new[] { "game:spear-generic-iron", "game:spear-generic-tinbronze" },
        },
        ["knight"] = new[]
        {
            new[] { "game:armor-head-plate-iron", "game:armor-head-scale-iron", "game:armor-head-chain-iron" },
            new[] { "game:armor-body-plate-iron", "game:armor-body-scale-iron", "game:armor-body-brigandine-iron" },
            new[] { "game:armor-legs-plate-iron", "game:armor-legs-scale-iron", "game:armor-legs-chain-iron" },
            new[] { "game:blade-blackguard-iron", "game:blade-falx-iron" },
        },
        ["bronze"] = new[]
        {
            new[] { "game:armor-head-scale-tinbronze", "game:armor-head-lamellar-tinbronze" },
            new[] { "game:armor-body-scale-tinbronze", "game:armor-body-lamellar-tinbronze" },
            new[] { "game:armor-legs-scale-tinbronze", "game:armor-legs-lamellar-tinbronze" },
            new[] { "game:blade-falx-tinbronze", "game:spear-generic-tinbronze" },
        },
        ["forlorn"] = new[]
        {
            new[] { "game:armor-head-antique-forlorn-damaged", "game:armor-head-antique-forlorn-broken" },
            new[] { "game:armor-body-antique-forlorn-damaged", "game:armor-body-antique-forlorn-broken" },
            new[] { "game:armor-legs-antique-forlorn-damaged", "game:armor-legs-antique-forlorn-broken" },
            new[] { "game:blade-forlorn-iron", "game:blade-forlorn-ruined" },
        },
    };

    private CollectibleObject Collectible(string code)
    {
        var loc = new AssetLocation(code);
        return (CollectibleObject)sapi.World.GetItem(loc) ?? sapi.World.GetBlock(loc);
    }

    private readonly HashSet<string> _lootMissing = new();

    // RollLoot over only the entries this game has; the missing are logged once.
    private List<string> RollTable(string table, CaveRand rand, int picks)
    {
        var pool = LootTables[table].Where(e =>
        {
            if (Collectible(e.Code) != null) return true;
            if (_lootMissing.Add(e.Code)) sapi.Logger.Notification("[landmassgenerator] loot table {0}: '{1}' is not in this game, skipped", table, e.Code);
            return false;
        }).ToArray();
        return pool.Length == 0 ? new List<string>() : RollLoot(rand, pool, picks);
    }

    // ── the pass ───────────────────────────────────────────────────────

    private string PlaceLoot(IslandJob job)
    {
        var defs = job.Shape?.Loot;
        if (defs == null || defs.Count == 0) return "";
        string doneKey = $"lgLootDone:{job.ShapeName}@{job.Cx},{job.Cz}";
        if (sapi.WorldManager.SaveGame.GetData<bool>(doneKey, false)) return "";
        sapi.WorldManager.SaveGame.StoreData(doneKey, true);

        int chests = 0, stands = 0;
        var notes = new List<string>();
        var taken = new List<BlockPos>();
        Block chestBlock = sapi.World.GetBlock(new AssetLocation("game", "chest-east"));
        for (int li = 0; li < defs.Count; li++)
        {
            var d = defs[li];
            var rand = new CaveRand((uint)(job.Seed * 31 + li * 7919 + 17));
            List<BlockPos> spots;
            if (d.Cave != '\0')
            {
                spots = d.CaveDef == null ? new List<BlockPos>() : CaveSpots(job, d.CaveDef, d.Count);
                if (d.CaveDef == null) notes.Add($"no cave '{d.Cave}'");
            }
            else
            {
                int cx = job.Cx, cz = job.Cz, radius = d.Radius > 0 ? d.Radius : Math.Max(30, job.Diameter / 2);
                if (d.Near != '\0')
                {
                    if (job.Shape.FirstCells.TryGetValue(d.Near, out var at)) { MarkerWorld(job, at.X, at.Z, out cx, out cz); if (d.Radius <= 0) radius = 40; }
                    else notes.Add($"no map cell '{d.Near}'");
                }
                spots = FloorSpots(job, cx, cz, radius, d.Where, d.Count, rand, taken, d.Above, d.Below);
            }
            if (spots.Count < d.Count) notes.Add($"{(d.Stand ? "armorstand " : "loot ")}{d.Table}: {spots.Count} of {d.Count} placed");
            foreach (var at in spots)
            {
                taken.Add(at);
                if (d.Stand)
                {
                    try { if (PlaceArmorStand(at, d.Table, rand)) stands++; }
                    catch (Exception e) { notes.Add("armor stand failed: " + e.Message); sapi.Logger.Error(e); }
                    continue;
                }
                if (chestBlock == null || HasChestNear(sapi.World.BlockAccessor, at)) continue;
                sapi.World.BlockAccessor.SetBlock(chestBlock.BlockId, at);
                if (sapi.World.BlockAccessor.GetBlockEntity(at) is not BlockEntityGenericTypedContainer be) continue;
                var ts = new ItemStack(chestBlock);
                string type = d.Type ?? (d.Where == "underwater" ? "collapsed" + (1 + (int)(rand.NextDouble() * 4)) : "normal-aged");
                ts.Attributes.SetString("type", type);
                be.OnBlockPlaced(ts);
                var contents = RollTable(d.Table, rand, 4 + (int)(rand.NextDouble() * 3));
                FillChest(be, contents, notes);
                chests++;
                sapi.Logger.Notification("[landmassgenerator] loot chest {0} ({1}) at HUD {2}, {3}, {4}: {5}", d.Table, type,
                    at.X - sapi.WorldManager.MapSizeX / 2, at.Y, at.Z - sapi.WorldManager.MapSizeZ / 2, string.Join(", ", contents));
            }
        }
        string r = chests + stands > 0 ? $" Loot: {chests} chest(s), {stands} armor stand(s)." : "";
        if (notes.Count > 0) r += " Loot notes: " + string.Join("; ", notes.Distinct()) + ".";
        return r;
    }

    // Floor cells with two blocks of room above, of the asked kind, spread
    // at least 6 blocks apart (3 when the area is tight), within radius.
    private List<BlockPos> FloorSpots(IslandJob job, int cx, int cz, int radius, string where, int need, CaveRand rand, List<BlockPos> taken, int? above = null, int? below = null)
    {
        var world = sapi.World.BlockAccessor;
        var p = new BlockPos(0, 0, 0, job.Dim);
        Block B(int x, int y, int z) { p.Set(x, y, z); return world.GetBlock(p, BlockLayersAccess.SolidBlocks); }
        bool Fluid(int x, int y, int z) { p.Set(x, y, z); return (world.GetBlock(p, BlockLayersAccess.Fluid)?.Id ?? 0) != 0; }
        bool Solid(int x, int y, int z) { var b = B(x, y, z); return b != null && b.Id != 0 && b.SideSolid[BlockFacing.UP.Index]; }
        bool Free(int x, int y, int z) { var b = B(x, y, z); return b == null || b.Id == 0 || b.BlockMaterial is EnumBlockMaterial.Plant; }

        int top = Math.Min(sapi.WorldManager.MapSizeY - 4, job.SeaLevel + job.DomeHeight + 60);
        int bottom = Math.Max(6, job.SeaLevel - job.MaxDepth - 8);
        int stride = radius > 80 ? 3 : radius > 45 ? 2 : 1;
        var kinds = new Dictionary<string, List<BlockPos>> { ["inside"] = new(), ["surface"] = new(), ["underwater"] = new(), ["any"] = new() };
        for (int x = cx - radius; x <= cx + radius; x += stride)
            for (int z = cz - radius; z <= cz + radius; z += stride)
            {
                if ((x - cx) * (x - cx) + (z - cz) * (z - cz) > radius * radius) continue;
                bool open = true;   // still under the open sky
                for (int y = top; y > bottom; y--)
                {
                    var b = B(x, y, z);
                    bool solidHere = b != null && b.Id != 0 && b.BlockMaterial is not EnumBlockMaterial.Plant;
                    if (!solidHere) continue;
                    bool floor = b.SideSolid[BlockFacing.UP.Index] && b.BlockMaterial is not EnumBlockMaterial.Leaves
                        && !(b.Code?.Path?.StartsWith("log-grown") ?? false) && Free(x, y + 1, z) && Free(x, y + 2, z);
                    if (floor && above.HasValue && y + 1 < job.SeaLevel + above.Value) floor = false;
                    if (floor && below.HasValue && y + 1 > job.SeaLevel - below.Value) floor = false;
                    if (floor)
                    {
                        bool wet = Fluid(x, y + 1, z);
                        string kind;
                        if (wet) kind = "underwater";
                        else if (open) kind = "surface";
                        else
                        {
                            int walls = 0;
                            foreach (var f in BlockFacing.HORIZONTALS)
                                for (int k = 1; k <= 4; k++)
                                    if (Solid(x + f.Normali.X * k, y + 1, z + f.Normali.Z * k)) { walls++; break; }
                            kind = walls >= 2 ? "inside" : "any";
                        }
                        var at = new BlockPos(x, y + 1, z, job.Dim);
                        kinds[kind].Add(at);
                        if (kind != "underwater") kinds["any"].Add(at);
                    }
                    open = false;
                }
            }
        // Wet floors only count when asked for or as a last resort.
        var order = where switch
        {
            "inside" => new[] { "inside", "any" },
            "surface" => new[] { "surface", "any" },
            "underwater" => new[] { "underwater" },
            _ => new[] { "any", "underwater" },
        };
        var picked = new List<BlockPos>();
        foreach (int minD in new[] { 6, 3 })
            foreach (string k in order)
            {
                var list = kinds[k];
                // deterministic shuffle
                for (int i = list.Count - 1; i > 0; i--) { int j = (int)(rand.NextDouble() * (i + 1)); (list[i], list[j]) = (list[j], list[i]); }
                foreach (var c in list)
                {
                    if (picked.Count >= need) return picked;
                    bool near = picked.Concat(taken).Any(o => Math.Abs(o.X - c.X) < minD && Math.Abs(o.Z - c.Z) < minD && Math.Abs(o.Y - c.Y) < 4);
                    if (!near) picked.Add(c);
                }
            }
        return picked;
    }

    // Floor spots spread evenly along a cave, from a third of the way in to the end.
    private List<BlockPos> CaveSpots(IslandJob job, CaveDef cave, int need)
    {
        var res = new List<BlockPos>();
        if (job.CaveSteps == null) return res;
        var steps = job.CaveSteps.Where(s => s.Def == cave).ToList();
        if (steps.Count == 0) return res;
        var world = sapi.World.BlockAccessor;
        var p = new BlockPos(0, 0, 0, job.Dim);
        for (int i = 0; i < need; i++)
        {
            int idx = (int)(steps.Count * (0.3 + 0.7 * (i + 0.5) / need));
            for (int t = 0; t < 40; t++)
            {
                var s = steps[Math.Clamp(idx + (t % 2 == 0 ? t / 2 : -(t / 2 + 1)), 0, steps.Count - 1)];
                int x = (int)Math.Floor(s.X), z = (int)Math.Floor(s.Z);
                BlockPos found = null;
                for (int y = (int)Math.Floor(s.Y); y >= (int)Math.Floor(s.Y - s.V - 3); y--)
                {
                    p.Set(x, y, z);
                    var b = world.GetBlock(p, BlockLayersAccess.SolidBlocks);
                    if (b == null || b.Id == 0) continue;
                    p.Set(x, y + 1, z);
                    bool air = (world.GetBlock(p, BlockLayersAccess.SolidBlocks)?.Id ?? 1) == 0 && (world.GetBlock(p, BlockLayersAccess.Fluid)?.Id ?? 1) == 0;
                    p.Set(x, y + 2, z);
                    bool head = (world.GetBlock(p, BlockLayersAccess.SolidBlocks)?.Id ?? 1) == 0;
                    if (air && head && b.SideSolid[BlockFacing.UP.Index]) found = new BlockPos(x, y + 1, z, job.Dim);
                    break;
                }
                if (found == null || res.Any(o => Math.Abs(o.X - found.X) < 6 && Math.Abs(o.Z - found.Z) < 6 && Math.Abs(o.Y - found.Y) < 4)) continue;
                res.Add(found);
                break;
            }
        }
        return res;
    }

    // An aged armor stand at the spot, wearing the set's first available
    // head, body and legs pieces and holding its weapon.
    private bool PlaceArmorStand(BlockPos at, string set, CaveRand rand)
    {
        var type = sapi.World.GetEntityType(new AssetLocation("game", "armorstand-aged")) ?? sapi.World.GetEntityType(new AssetLocation("game", "armorstand"));
        if (type == null) return false;
        Entity stand = sapi.World.ClassRegistry.CreateEntity(type);
        stand.Pos.SetPos(at.X + 0.5, at.Y, at.Z + 0.5);
        stand.Pos.Yaw = (float)(Math.Round(rand.NextDouble() * 4) * Math.PI / 2);
        stand.Pos.Dimension = at.dimension;
        sapi.World.SpawnEntity(stand);
        // Straight into the stand's wearable slots (head, body, legs, right
        // hand, in the entity's own slot order): the "give item" route picks
        // a slot through selection boxes, which only the client has.
        var worn = new List<string>();
        var beh = stand.GetBehavior<EntityBehaviorAttachable>();
        if (beh?.Inventory == null) return false;
        var sets = ArmorSets[set];
        for (int i = 0; i < sets.Length && i < beh.Inventory.Count; i++)
        {
            var obj = sets[i].Select(Collectible).FirstOrDefault(c => c != null);
            if (obj == null) continue;
            beh.Inventory[i].Itemstack = new ItemStack(obj);
            beh.Inventory[i].MarkDirty();
            worn.Add(obj.Code.ToShortString());
        }
        beh.storeInv();
        sapi.Logger.Notification("[landmassgenerator] armor stand ({0}) at HUD {1}, {2}, {3}: {4}", set,
            at.X - sapi.WorldManager.MapSizeX / 2, at.Y, at.Z - sapi.WorldManager.MapSizeZ / 2, worn.Count > 0 ? string.Join(", ", worn) : "nothing fitted");
        return true;
    }
}
