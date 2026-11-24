using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace LandmassGenerator;

// /genloottest <entity code> [rolls]: rolls a creature's harvest loot the way the game's harvestable behavior
// does (its drops, resolved, each rolled once per carcass), without a player. Says which drops fail to resolve
// (a missing item: the classic cause of a looting crash) and the average of each item per carcass.
public partial class LandmassGeneratorModSystem
{
    private void RegisterLootTest(ICoreServerAPI api)
    {
        var p = api.ChatCommands.Parsers;
        // /genfindblock <code part> <hudx> <hudz> [r]: every block whose code contains the text, within r blocks (loaded chunks only)
        api.ChatCommands.Create("genfindblock")
            .WithDescription("List blocks whose code contains the text within r blocks of a HUD x,z (all heights, loaded chunks only)")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(p.Word("code"), p.Int("x"), p.Int("z"), p.OptionalInt("r", 48))
            .HandleWith(args =>
            {
                string part = (string)args[0];
                int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
                int cx = (int)args[1] + mx, cz = (int)args[2] + mz, r = Math.Clamp((int)args[3], 1, 256);
                var ba = sapi.World.BlockAccessor; var pos = new BlockPos(0, 0, 0, 0);
                var hits = new List<string>();
                for (int x = cx - r; x <= cx + r; x++)
                    for (int z = cz - r; z <= cz + r; z++)
                        for (int y = 1; y < sapi.WorldManager.MapSizeY - 1; y++)
                        {
                            pos.Set(x, y, z);
                            var c = ba.GetBlock(pos)?.Code;
                            if (c != null && c.Path.Contains(part)) hits.Add($"{c.Path} at HUD {x - mx}, {y}, {z - mz}");
                        }
                string msg = hits.Count == 0 ? $"no '{part}' within {r}" : string.Join("; ", hits.Take(20));
                sapi.Logger.Notification("[findblock] {0}", msg);
                return TextCommandResult.Success(msg);
            });
        api.ChatCommands.Create("genloottest")
            .WithDescription("Roll a creature's harvest loot N times (default 1000) and report each item's average per carcass and any drop that does not resolve")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(p.Word("entity"), p.OptionalInt("rolls", 1000))
            .HandleWith(args =>
            {
                string code = (string)args[0];
                int rolls = Math.Clamp((int)args[1], 1, 100000);
                EntityProperties type = sapi.World.GetEntityType(new AssetLocation(code));
                if (type == null) return TextCommandResult.Error("no entity " + code);
                JsonObject beh = type.Server?.BehaviorsAsJsonObj?.FirstOrDefault(b => b["code"].AsString() == "harvestable");
                if (beh == null) return TextCommandResult.Error(code + " has no harvestable behavior");
                JsonObject[] raw = beh["drops"].AsArray();
                if (raw == null) return TextCommandResult.Error(code + ": no drops list (dropsByType did not resolve for this variant)");
                BlockDropItemStack[] drops = raw.Select(j =>
                {
                    if (j?["code"].AsString() == null) return null;
                    var q = j["quantity"];
                    var dist = Enum.TryParse(q["dist"].AsString("uniform"), true, out EnumDistribution ed) ? ed : EnumDistribution.UNIFORM;
                    return new BlockDropItemStack
                    {
                        Type = j["type"].AsString("item") == "block" ? EnumItemClass.Block : EnumItemClass.Item,
                        Code = AssetLocation.Create(j["code"].AsString(), type.Code.Domain),
                        Quantity = q.Exists ? NatFloat.create(dist, q["avg"].AsFloat(1), q["var"].AsFloat(0)) : NatFloat.One,
                        LastDrop = j["lastDrop"].AsBool(false),
                    };
                }).ToArray();
                var bad = new List<string>();
                var good = new List<BlockDropItemStack>();
                foreach (var d in drops)
                {
                    if (d == null) { bad.Add("(null entry)"); continue; }
                    bool ok = d.Resolve(sapi.World, "loottest " + code, type.Code);
                    if (ok && d.ResolvedItemstack != null) good.Add(d); else bad.Add(d.Code?.ToString() ?? "?");
                }
                var totals = new Dictionary<string, double>();
                int stacksMax = 0;
                for (int i = 0; i < rolls; i++)
                {
                    int stacks = 0;
                    foreach (var d in good)
                    {
                        ItemStack st = d.GetNextItemStack(1f);
                        if (st == null) continue;
                        stacks++;
                        string k = st.Collectible.Code.ToShortString();
                        totals[k] = totals.GetValueOrDefault(k) + st.StackSize;
                        if (d.LastDrop) break;
                    }
                    stacksMax = Math.Max(stacksMax, stacks);
                }
                string list = string.Join(", ", totals.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value / rolls:0.###}"));
                string msg = $"{code}: {drops.Length} drops ({bad.Count} unresolved{(bad.Count > 0 ? ": " + string.Join(", ", bad) : "")}); per carcass over {rolls}: {list}; most stacks in one carcass {stacksMax}";
                sapi.Logger.Notification("[loottest] {0}", msg);
                return TextCommandResult.Success(msg);
            });
    }
}
