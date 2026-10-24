using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace LandmassGenerator;

// /genlightcheck x z [radius]: are the structure lamps around HUD x z really
// lit? Loads the area, finds every Jonas lamp (and ghostlight) block, and
// reads the game's own stored block light at each lamp and in the open
// cells beside it. A lamp whose type says it shines but whose cell holds no
// light was placed without being lit. For checking a built world headless.
public partial class LandmassGeneratorModSystem
{
    private void StartLightCheck(ICoreServerAPI api)
    {
        var p = api.ChatCommands.Parsers;
        RegisterCmd(api, "genlightcheck", name =>
            api.ChatCommands.Create(name)
                .WithDescription("Read the stored block light at every Jonas lamp and ghostlight around HUD x z (radius, default 96), loading the area first. Logs how many shine as their type says.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.Int("x"), p.Int("z"), p.OptionalInt("radius", 96))
                .HandleWith(OnGenLightCheck));
        RegisterCmd(api, "genrelight", name =>
            api.ChatCommands.Create(name)
                .WithDescription("Relight every Jonas lamp and ghostlight around HUD x z now (radius, default 96), loading and holding the area first, with the same relight a structure's lights get when a player first comes near. For checking a headless build.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.Int("x"), p.Int("z"), p.OptionalInt("radius", 96))
                .HandleWith(a =>
                {
                    int hx = (int)a[0], hz = (int)a[1], r = Math.Clamp((int)a[2], 8, 400);
                    int x = hx + sapi.WorldManager.MapSizeX / 2, z = hz + sapi.WorldManager.MapSizeZ / 2, cs = GlobalConstants.ChunkSize;
                    LoadChunkAreaBanded(FloorDiv(x - r, cs), FloorDiv(z - r, cs), FloorDiv(x + r, cs), FloorDiv(z + r, cs), true,
                        () =>
                        {
                            var acc = sapi.World.BlockAccessor;
                            int n = 0;
                            foreach (var at in FindEmitters(x, z, r)) if (RelightAt(acc, at)) n++;
                            sapi.Logger.Notification("[genrelight] relit {0} structure light(s)", n);
                        });
                    return TextCommandResult.Success("Relighting; the count goes to the log.");
                }));
    }

    // Every Jonas lamp (by block entity) and ghostlight (by scan) in the box.
    private List<BlockPos> FindEmitters(int x, int z, int r)
    {
        var ba = sapi.World.BlockAccessor;
        var found = new List<BlockPos>();
        int cs = GlobalConstants.ChunkSize, mapCY = sapi.WorldManager.MapSizeY / cs;
        for (int ccx = FloorDiv(x - r, cs); ccx <= FloorDiv(x + r, cs); ccx++)
            for (int ccz = FloorDiv(z - r, cs); ccz <= FloorDiv(z + r, cs); ccz++)
                for (int ccy = 0; ccy < mapCY; ccy++)
                {
                    var chunk = ba.GetChunk(ccx, ccy, ccz);
                    if (chunk == null) continue;
                    foreach (var be in chunk.BlockEntities.Values)
                        if ((be.Block?.Code?.Path ?? "").StartsWith("jonas-lamp") && Math.Abs(be.Pos.X - x) <= r && Math.Abs(be.Pos.Z - z) <= r)
                            found.Add(be.Pos.Copy());
                }
        var gids = new HashSet<int>(new[] { "landmassgenerator:ghostlight-blue", "landmassgenerator:ghostlight-green" }
            .Select(c => sapi.World.GetBlock(new AssetLocation(c))?.BlockId ?? 0).Where(i => i != 0));
        var pos = new BlockPos(0, 0, 0, 0);
        for (int px = x - r; px <= x + r; px++)
            for (int pz = z - r; pz <= z + r; pz++)
                for (int py = 5; py < sapi.WorldManager.MapSizeY - 2; py++)
                {
                    pos.Set(px, py, pz);
                    if (gids.Contains(ba.GetBlockId(pos))) found.Add(pos.Copy());
                }
        return found;
    }

    private TextCommandResult OnGenLightCheck(TextCommandCallingArgs args)
    {
        int hx = (int)args[0], hz = (int)args[1], r = Math.Clamp((int)args[2], 8, 400);
        int x = hx + sapi.WorldManager.MapSizeX / 2, z = hz + sapi.WorldManager.MapSizeZ / 2;
        int cs = GlobalConstants.ChunkSize;
        LoadChunkAreaBanded(FloorDiv(x - r, cs), FloorDiv(z - r, cs), FloorDiv(x + r, cs), FloorDiv(z + r, cs), true, () =>
        {
            var ba = sapi.World.BlockAccessor;
            var pos = new BlockPos(0, 0, 0, 0);
            var counts = new Dictionary<string, (int N, int Lit, int Dark, long LightSum, int NearSum)>();
            var dark = new List<string>();
            var unlit = new List<string>();
            int mapCY = sapi.WorldManager.MapSizeY / cs;
            for (int ccx = FloorDiv(x - r, cs); ccx <= FloorDiv(x + r, cs); ccx++)
                for (int ccz = FloorDiv(z - r, cs); ccz <= FloorDiv(z + r, cs); ccz++)
                    for (int ccy = 0; ccy < mapCY; ccy++)
                    {
                        var chunk = ba.GetChunk(ccx, ccy, ccz);
                        if (chunk == null) continue;
                        foreach (var be in chunk.BlockEntities.Values)
                        {
                            Block b = be.Block;
                            string code = b?.Code?.Path ?? "";
                            if (!code.StartsWith("jonas-lamp")) continue;
                            if (Math.Abs(be.Pos.X - x) > r || Math.Abs(be.Pos.Z - z) > r) continue;
                            Tally(be.Pos, b, "jonas-lamp");
                        }
                    }
            // the ghostlights have no block entity: scan for them in the box
            int ghost = 0;
            var gids = new HashSet<int>(new[] { "landmassgenerator:ghostlight-blue", "landmassgenerator:ghostlight-green" }
                .Select(c => sapi.World.GetBlock(new AssetLocation(c))?.BlockId ?? 0).Where(i => i != 0));
            for (int px = x - r; px <= x + r; px += 1)
                for (int pz = z - r; pz <= z + r; pz += 1)
                    for (int py = 5; py < sapi.WorldManager.MapSizeY - 2; py++)
                    {
                        pos.Set(px, py, pz);
                        int id = ba.GetBlockId(pos);
                        if (!gids.Contains(id)) continue;
                        ghost++;
                        if (ghost % 7 == 0) Tally(pos.Copy(), ba.GetBlock(id), "ghostlight (1 in 7)");
                    }

            void Tally(BlockPos at, Block b, string kind)
            {
                byte[] hsv = b.GetLightHsv(ba, at);
                int own = ba.GetLightLevel(at, EnumLightLevelType.OnlyBlockLight);
                int near = 0;
                foreach (var f in BlockFacing.ALLFACES)
                {
                    var q = at.AddCopy(f);
                    var nb = ba.GetBlock(q);
                    if (nb != null && nb.Id != 0 && nb.SideOpaque[f.Opposite.Index]) continue;
                    near = Math.Max(near, ba.GetLightLevel(q, EnumLightLevelType.OnlyBlockLight));
                }
                counts.TryGetValue(kind, out var c);
                bool shines = hsv != null && hsv[2] > 0;
                bool lit = shines && Math.Max(own, near) >= hsv[2] - 2;
                c.N++; if (lit) c.Lit++; else if (shines) c.Dark++;
                c.LightSum += Math.Max(own, near); c.NearSum += near;
                counts[kind] = c;
                if (!shines && unlit.Count < 20)
                    unlit.Add($"{kind} type '{be2(at) ?? "(none)"}' at HUD {at.X - sapi.WorldManager.MapSizeX / 2}, {at.Y}, {at.Z - sapi.WorldManager.MapSizeZ / 2}");
                if (shines && !lit && dark.Count < 12)
                    dark.Add($"{kind} ({(be2(at) ?? "")}) at HUD {at.X - sapi.WorldManager.MapSizeX / 2}, {at.Y}, {at.Z - sapi.WorldManager.MapSizeZ / 2}: light {own}/{near}, should be about {hsv[2]}");
            }
            string be2(BlockPos at) => (ba.GetBlockEntity(at)?.GetBehavior<Vintagestory.GameContent.BEBehaviorShapeFromAttributes>())?.Type;

            foreach (var kv in counts)
                sapi.Logger.Notification("[genlightcheck] {0}: {1} found, {2} shining as their type says, {3} dark; mean light at or beside them {4:0.0}",
                    kv.Key, kv.Value.N, kv.Value.Lit, kv.Value.Dark, kv.Value.N == 0 ? 0 : kv.Value.LightSum / (double)kv.Value.N);
            sapi.Logger.Notification("[genlightcheck] {0} ghostlight block(s) in the box", ghost);
            foreach (string d in dark) sapi.Logger.Notification("[genlightcheck] dark: {0}", d);
            foreach (string d in unlit) sapi.Logger.Notification("[genlightcheck] no lit type: {0}", d);
        });
        return TextCommandResult.Success($"Checking lamps within {r} of HUD {hx}, {hz}; results in the log.");
    }
}
