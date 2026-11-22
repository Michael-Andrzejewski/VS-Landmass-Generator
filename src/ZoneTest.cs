using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace LandmassGenerator;

// /genzonetest: checks for the Rustfall zone rules (RustfallZone.cs), usable on a headless server.
//   at <hudx> <hudz>       distance outside the zone, and what Spreading Devastation itself now says there
//   seed <hudx> <hudz>     plant a devastated chunk there, as SD's own /dv chunk does (SD then spreads it if allowed)
//   count <hudx> <hudz> r  devastated blocks within r blocks (surface layers)
public partial class LandmassGeneratorModSystem
{
    private void RegisterZoneTest(ICoreServerAPI api)
    {
        var p = api.ChatCommands.Parsers;
        api.ChatCommands.Create("genzonetest")
            .WithDescription("Rustfall zone checks: at <x> <z> | seed <x> <z> | count <x> <z> <r> (HUD coordinates)")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(p.Word("action"), p.Int("x"), p.Int("z"), p.OptionalInt("r", 24))
            .HandleWith(args =>
            {
                string action = (string)args[0];
                int x = (int)args[1] + sapi.WorldManager.MapSizeX / 2, z = (int)args[2] + sapi.WorldManager.MapSizeZ / 2;
                int r = (int)args[3];
                ModSystem sd = sapi.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "SpreadingDevastation.SpreadingDevastationModSystem");
                const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var ba = sapi.World.BlockAccessor;
                int y = ba.GetRainMapHeightAt(x, z);
                var pos = new BlockPos(x, y, z, 0);
                switch (action)
                {
                    case "at":
                    {
                        string s = $"zone distance {ZoneDistance(x, z):0}, no devastation here: {NoDevastationAt(x, z)}";
                        if (sd != null)
                        {
                            var t = sd.GetType();
                            object bp = t.GetMethod("IsBlockProtectedByRiftWard", F, null, new[] { typeof(int), typeof(int), typeof(int) }, null)?.Invoke(sd, new object[] { x, y, z });
                            object cp = t.GetMethod("IsChunkProtectedByRiftWard", F, null, new[] { typeof(int), typeof(int) }, null)?.Invoke(sd, new object[] { x / 32, z / 32 });
                            Block grass = sapi.World.GetBlock(new AssetLocation("game", "soil-medium-normal"));
                            var fm = t.GetMethods(F).FirstOrDefault(m => m.Name == "TryGetDevastatedForm" && m.GetParameters().Length == 4 && m.GetParameters()[1].ParameterType == typeof(BlockPos));
                            object[] fa = { grass, pos, null, null };
                            object form = fm?.Invoke(sd, fa);
                            var rm = t.GetMethods(F).FirstOrDefault(m => m.Name == "TryResolveDevastation" && m.GetParameters().Length == 7);
                            object[] ra = { grass, x, y, z, null, null, null };
                            object res = rm?.Invoke(sd, ra);
                            object paused = t.GetField("isPaused", F)?.GetValue(sd);
                            object cfg = t.GetField("config", F)?.GetValue(sd);
                            object edge = cfg?.GetType().GetProperty("EdgeSpawningEnabled")?.GetValue(cfg);
                            s += $"; SD: block warded {bp}, chunk warded {cp}, soil would turn into {(Equals(form, true) ? fa[2] : "nothing")}, fast path {(Equals(res, true) ? ra[5] : "nothing")}; SD paused {paused}, edge spawning {edge}";
                        }
                        return TextCommandResult.Success(s);
                    }
                    case "load":
                    {
                        LoadChunkAreaBanded(x / 32 - r / 32 - 1, z / 32 - r / 32 - 1, x / 32 + r / 32 + 1, z / 32 + r / 32 + 1, true,
                            () => sapi.Logger.Notification("[zonetest] loaded around HUD {0}, {1}", (int)args[1], (int)args[2]));
                        return TextCommandResult.Success("loading (kept loaded)");
                    }
                    case "pad":
                    {
                        // A flat soil platform at sea level, r blocks round the point, for devastation tests at sea.
                        Block soil = sapi.World.GetBlock(new AssetLocation("game", "soil-medium-normal"));
                        Block air = sapi.World.GetBlock(0);
                        int sea = sapi.World.SeaLevel;
                        var q = new BlockPos(0, 0, 0, 0);
                        for (int dx = -r; dx <= r; dx++)
                            for (int dz = -r; dz <= r; dz++)
                            {
                                if (dx * dx + dz * dz > r * r) continue;
                                for (int yy = sea - 4; yy <= sea + 1; yy++) { q.Set(x + dx, yy, z + dz); ba.SetBlock(soil.BlockId, q); ba.SetBlock(0, q, BlockLayersAccess.Fluid); }
                                for (int yy = sea + 2; yy <= sea + 4; yy++) { q.Set(x + dx, yy, z + dz); ba.SetBlock(0, q); }
                            }
                        return TextCommandResult.Success($"soil pad r={r} at HUD {(int)args[1]}, {(int)args[2]}, top y {sea + 1}");
                    }
                    case "block":
                    {
                        // what is at an exact spot (y = r), and a container's first stacks
                        var bp = new BlockPos(x, r, z, 0);
                        var be = sapi.World.BlockAccessor.GetBlockEntity(bp);
                        string inv = "";
                        if (be is Vintagestory.API.Common.IBlockEntityContainer ct)
                            inv = string.Join(", ", ct.Inventory.Where(sl => !sl.Empty).Take(4).Select(sl => sl.Itemstack.Collectible.Code.ToShortString()));
                        return TextCommandResult.Success($"{sapi.World.BlockAccessor.GetBlock(bp).Code} {(be == null ? "" : be.GetType().Name)} [{inv}]");
                    }
                    case "climate":
                    {
                        var c = sapi.World.BlockAccessor.GetClimateAt(new BlockPos(x, y + 1, z, 0), EnumGetClimateMode.WorldGenValues);
                        return TextCommandResult.Success(c == null ? "no climate" : $"climate at HUD {(int)args[1]}, {y + 1}, {(int)args[2]}: temperature {c.Temperature:0.0} C, rainfall {c.WorldgenRainfall:0.00}, forest {c.ForestDensity:0.00}, shrubs {c.ShrubDensity:0.00} (wolves want -15..15 C, rain 0.25+, forest 0.5+)");
                    }
                    case "spawn":
                    case "wolf":
                    case "bear":
                    {
                        string code = action == "wolf" ? "wolf-eurasian-adult-male" : action == "bear" ? "bear-brown-adult-male" : "drifter-normal";
                        var type = sapi.World.GetEntityType(new AssetLocation("game", code));
                        if (type == null) return TextCommandResult.Error("no " + code);
                        Entity e = sapi.World.ClassRegistry.CreateEntity(type);
                        e.ServerPos.SetPos(x + 0.5, y + 1, z + 0.5); e.Pos.SetFrom(e.ServerPos);
                        sapi.World.SpawnEntity(e);
                        long id = e.EntityId;
                        int hx = (int)args[1], hz = (int)args[2];
                        sapi.Event.RegisterCallback(_ => sapi.Logger.Notification("[zonetest] {0} {1} at HUD {2}, {3} after 2 s: {4}", code, id, hx, hz,
                            sapi.World.GetEntityById(id)?.Alive == true ? "still alive" : "gone"), 2000);
                        return TextCommandResult.Success($"spawned {code} {id} at HUD {(int)args[1]}, {y + 1}, {(int)args[2]}; result in the log in 2 s");
                    }
                    case "seed":
                    {
                        if (sd == null) return TextCommandResult.Error("SD not installed");
                        var t = sd.GetType();
                        var dict = t.GetField("devastatedChunks", F)?.GetValue(sd) as IDictionary;
                        Type chunkT = t.Assembly.GetType("SpreadingDevastation.DevastatedChunk");
                        if (dict == null || chunkT == null) return TextCommandResult.Error("SD internals not found");
                        object c = Activator.CreateInstance(chunkT);
                        void Set(string n, object v) { var pi = chunkT.GetProperty(n); if (pi != null) pi.SetValue(c, v); else chunkT.GetField(n)?.SetValue(c, v); }
                        Set("ChunkX", x / 32); Set("ChunkZ", z / 32); Set("MarkedTime", sapi.World.Calendar.TotalHours);
                        Set("DevastationLevel", 0.0); Set("IsFullyDevastated", false); Set("FrontierInitialized", true);
                        Set("DevastationFrontier", new System.Collections.Generic.List<BlockPos> { pos });
                        long key = (long)chunkT.GetMethod("MakeChunkKey", BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[] { x / 32, z / 32 });
                        dict[key] = c;
                        return TextCommandResult.Success($"Seeded a devastated chunk at HUD {(int)args[1]}, {y}, {(int)args[2]} (zone distance {ZoneDistance(x, z):0}). Surface block there: {ba.GetBlock(pos).Code}");
                    }
                    case "count":
                    {
                        int n = 0, cols = 0;
                        var q = new BlockPos(0, 0, 0, 0);
                        for (int dx = -r; dx <= r; dx++)
                            for (int dz = -r; dz <= r; dz++)
                            {
                                int top = ba.GetRainMapHeightAt(x + dx, z + dz);
                                cols++;
                                for (int dy = -3; dy <= 1; dy++)
                                {
                                    q.Set(x + dx, top + dy, z + dz);
                                    string path = ba.GetBlock(q).Code?.Path ?? "";
                                    if ((path.Contains("devastat") || path.StartsWith("drock")) && !path.StartsWith("clutter")) n++;
                                }
                            }
                        return TextCommandResult.Success($"{n} devastated blocks within {r} of HUD {(int)args[1]}, {(int)args[2]} (zone distance {ZoneDistance(x, z):0}, {cols} columns)");
                    }
                }
                return TextCommandResult.Error("at | seed | count");
            });
    }
}
