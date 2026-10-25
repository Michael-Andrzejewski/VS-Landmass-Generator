using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// ── THE GREAT MACHINE: ITS FINDS (0.66.0) ───────────────────────────────
// The works and the warrens are meant to pay for the trip down: chests of
// Jonas parts, temporal gears, ingots and tools, armor stands still dressed,
// ingot piles on the floors, and the rare Jonas devices themselves (static
// translocators, rift wards, the base and corpse return teleporters,
// resonators). The builders leave floor spots with a kind (GmLootFor); once
// the structure is committed, each spot is checked in the real world (an
// open cell on a solid floor, head room, nothing placed beside it) and
// filled. Every find is logged.
public partial class LandmassGeneratorModSystem
{
    // What a room of a kind holds, rolled once per spot.
    private static string GmLootFor(GmCtx g, string room)
    {
        double r = g.R();
        switch (room)
        {
            case "lab": case "optics": case "specimens":
                return r < 0.35 ? "chest:jonas" : r < 0.5 ? "device" : r < 0.8 ? "chest:gmlab" : "ingots";
            case "workshop": case "machine": case "valves":
                return r < 0.4 ? "chest:machinist" : r < 0.75 ? "ingots" : r < 0.85 ? "device" : "chest:forge";
            case "store": case "minecamp":
                return r < 0.25 ? "chest:mine" : r < 0.5 ? "chest:forge" : r < 0.85 ? "ingots" : "chest:treasury";
            case "office": case "drafting": case "archive":
                return r < 0.3 ? "chest:jonas" : r < 0.6 ? "chest:treasury" : "chest:ruin";
            case "hazmat":
                return r < 0.5 ? "stand:knight" : r < 0.7 ? "stand:forlorn" : "chest:machinist";
            case "locust":
                return r < 0.3 ? "device" : r < 0.75 ? "chest:jonas" : "ingots";
            case "deadend":
                return r < 0.3 ? "chest:jonas" : r < 0.55 ? "chest:treasury" : r < 0.75 ? "ingots" : r < 0.9 ? "device" : "stand:knight";
            default:
                return r < 0.5 ? "chest:ruin" : r < 0.7 ? "stand:guard" : r < 0.8 ? "chest:treasury" : "ingots";
        }
    }

    private void GmQueueLoot(GmCtx g)
    {
        if (g.Loot.Count == 0) return;
        g.K.AfterCommit.Add(() => GmPlaceLoot(g));
    }

    private void GmPlaceLoot(GmCtx g)
    {
        var acc = sapi.World.BlockAccessor;
        int dim = g.K.Job.Dim;
        var rand = new CaveRand((uint)(g.Seed * 7919 + 4242));
        var p = new BlockPos(0, 0, 0, dim);
        var notes = new List<string>();
        var taken = new List<BlockPos>();
        int chests = 0, stands = 0, piles = 0, devices = 0, skipped = 0;
        var deviceCount = new Dictionary<string, int>();
        bool Air(int x, int y, int z)
        {
            p.Set(x, y, z);
            return (acc.GetBlock(p, BlockLayersAccess.SolidBlocks)?.Id ?? 1) == 0 && (acc.GetBlock(p, BlockLayersAccess.Fluid)?.Id ?? 1) == 0;
        }
        string Side(double h)
        {
            double fx = Math.Cos(h), fz = Math.Sin(h);
            return Math.Abs(fx) >= Math.Abs(fz) ? (fx >= 0 ? "east" : "west") : (fz >= 0 ? "south" : "north");
        }
        foreach (var s in g.Loot)
        {
            if (!Air(s.X, s.Y, s.Z) || !Air(s.X, s.Y + 1, s.Z)) { skipped++; continue; }
            p.Set(s.X, s.Y - 1, s.Z);
            var below = acc.GetBlock(p, BlockLayersAccess.SolidBlocks);
            if (below == null || below.Id == 0 || !below.SideSolid[BlockFacing.UP.Index]) { skipped++; continue; }
            if (taken.Any(t => Math.Abs(t.X - s.X) <= 1 && Math.Abs(t.Z - s.Z) <= 1 && Math.Abs(t.Y - s.Y) <= 1)) { skipped++; continue; }
            var at = new BlockPos(s.X, s.Y, s.Z, dim);
            string hud = $"{s.X - sapi.WorldManager.MapSizeX / 2}, {s.Y}, {s.Z - sapi.WorldManager.MapSizeZ / 2}";
            try
            {
                if (s.Kind.StartsWith("chest:"))
                {
                    string table = s.Kind.Substring(6);
                    var block = sapi.World.GetBlock(new AssetLocation("game", "chest-" + Side(s.H))) ?? sapi.World.GetBlock(new AssetLocation("game", "chest-east"));
                    if (block == null) { skipped++; continue; }
                    acc.SetBlock(block.BlockId, at);
                    if (acc.GetBlockEntity(at) is not BlockEntityGenericTypedContainer be) { skipped++; continue; }
                    var ts = new ItemStack(block);
                    ts.Attributes.SetString("type", "normal-aged");
                    be.OnBlockPlaced(ts);
                    var contents = RollTable(table, rand, 5 + (int)(rand.NextDouble() * 3));
                    FillChest(be, contents, notes);
                    chests++;
                    sapi.Logger.Notification("[landmassgenerator] great machine chest {0} at HUD {1}: {2}", table, hud, string.Join(", ", contents));
                }
                else if (s.Kind == "ingots")
                {
                    double r0 = rand.NextDouble();
                    string metal = r0 < 0.22 ? "iron" : r0 < 0.38 ? "steel" : r0 < 0.52 ? "copper" : r0 < 0.66 ? "tinbronze" : r0 < 0.74 ? "blackbronze"
                        : r0 < 0.82 ? "gold" : r0 < 0.9 ? "silver" : r0 < 0.95 ? "meteoriciron" : "brass";
                    int count = 16 + (int)(rand.NextDouble() * 49);
                    if (GmIngotPile(acc, at, metal, count)) { piles++; sapi.Logger.Notification("[landmassgenerator] great machine ingot pile at HUD {0}: {1} {2}", hud, count, metal); }
                    else skipped++;
                }
                else if (s.Kind.StartsWith("stand:"))
                {
                    if (PlaceArmorStand(at, s.Kind.Substring(6), rand)) stands++;
                    else skipped++;
                }
                else if (s.Kind == "device")
                {
                    double r0 = rand.NextDouble();
                    string side = Side(s.H);
                    string code = r0 < 0.24 ? "statictranslocator-broken-" + side : r0 < 0.4 ? "statictranslocator-normal-" + side : r0 < 0.58 ? "riftward"
                        : r0 < 0.68 ? "basereturnteleporter" : r0 < 0.76 ? "basereturnteleporter-fried" : r0 < 0.89 ? "corpsereturnteleporter-" + side : "resonator-" + side;
                    var block = sapi.World.GetBlock(new AssetLocation("game", code));
                    if (block == null) { notes.Add("no " + code); skipped++; continue; }
                    acc.SetBlock(block.BlockId, at);
                    acc.MarkBlockDirty(at);
                    devices++;
                    string kind = code.Split('-')[0] + (code.Contains("-broken") ? " (broken)" : code.Contains("-fried") ? " (fried)" : "");
                    deviceCount[kind] = deviceCount.TryGetValue(kind, out int c) ? c + 1 : 1;
                    sapi.Logger.Notification("[landmassgenerator] great machine device {0} at HUD {1}", code, hud);
                }
                taken.Add(at);
            }
            catch (Exception e)
            {
                skipped++;
                sapi.Logger.Error("[landmassgenerator] great machine loot at HUD {0} failed: {1}", hud, e.Message);
            }
        }
        sapi.Logger.Notification("[landmassgenerator] great machine finds: {0} chests, {1} armor stands, {2} ingot piles, {3} Jonas devices ({4}); {5} of {6} spots unusable{7}",
            chests, stands, piles, devices, string.Join(", ", deviceCount.Select(kv => kv.Value + " " + kv.Key)), skipped, g.Loot.Count,
            notes.Count > 0 ? "; notes: " + string.Join("; ", notes.Distinct()) : "");
    }

    // A pile of ingots on the floor, the way a player stacks them: ground
    // storage holding the ingots, which picks its own pile model from them.
    private bool GmIngotPile(IBlockAccessor acc, BlockPos pos, string metal, int count)
    {
        Item ingot = sapi.World.GetItem(new AssetLocation("game", "ingot-" + metal));
        Block storage = sapi.World.GetBlock(new AssetLocation("game", "groundstorage"));
        if (ingot == null || storage == null || ingot.GetBehavior<CollectibleBehaviorGroundStorable>() == null) return false;
        acc.SetBlock(storage.BlockId, pos);
        if (acc.GetBlockEntity(pos) is not BlockEntityGroundStorage be) return false;
        be.Inventory[0].Itemstack = new ItemStack(ingot, Math.Min(count, 64));
        be.Inventory[0].MarkDirty();
        be.DetermineStorageProperties(null);
        be.MarkDirty(true);
        acc.MarkBlockDirty(pos);
        return true;
    }
}
