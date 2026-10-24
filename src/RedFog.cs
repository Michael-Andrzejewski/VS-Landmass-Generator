using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace LandmassGenerator;

// The Great Machine's shroud: a red fog over its island, the way the
// vanilla Devastation area wears its brown one, with red embers and ash
// rising out of the ground.
//
// Zones live in the save ("lgRedFog", "dim|x|z|radius|fade" per zone). The
// greatmachine structure adds its own when it is built; /genfog adds one by
// hand (for a world whose machine was built before this existed). The
// server sends the list to every player on join and to everyone when it
// changes. The client copies vanilla's ModSystemDevastationEffects: an
// ambient modifier for the fog close up, a fog sphere so the island reads
// as a red dome from out at sea, and an async particle spawner. Vanilla's
// devastation fog sphere uses slot 0 when its area is near; ours takes the
// next slot after it, so both can show at once.
public partial class LandmassGeneratorModSystem
{
    private const string RedFogKey = "lgRedFog";
    private const string RedFogChannel = "lgredfog";

    // The three parts of the effect can be switched off per world
    // (/genfog dome|haze|embers off): Michael kept the haze and dropped the
    // dome, which read too red and showed through the water from outside.
    private const string RedFogHiddenKey = "lgRedFogHidden";
    private static readonly string[] RedFogParts = { "dome", "haze", "embers" };

    [ProtoContract]
    public class RedFogPacket
    {
        [ProtoMember(1)]
        public List<string> Zones = new();
        [ProtoMember(2)]
        public bool HideDome;
        [ProtoMember(3)]
        public bool HideHaze;
        [ProtoMember(4)]
        public bool HideEmbers;
    }

    private struct FogZone
    {
        public int Dim, X, Z, Radius, Fade;

        public static bool TryParse(string s, out FogZone z)
        {
            z = default;
            var p = s.Split('|');
            if (p.Length < 5) return false;
            var ci = CultureInfo.InvariantCulture;
            return int.TryParse(p[0], NumberStyles.Integer, ci, out z.Dim) && int.TryParse(p[1], NumberStyles.Integer, ci, out z.X)
                && int.TryParse(p[2], NumberStyles.Integer, ci, out z.Z) && int.TryParse(p[3], NumberStyles.Integer, ci, out z.Radius)
                && int.TryParse(p[4], NumberStyles.Integer, ci, out z.Fade);
        }

        public override string ToString() => string.Join("|", Dim, X, Z, Radius, Fade);
    }

    // ── server ──────────────────────────────────────────────────────────

    private List<string> RedFogList() => sapi.WorldManager.SaveGame.GetData<List<string>>(RedFogKey, null) ?? new List<string>();
    private List<string> RedFogHidden() => sapi.WorldManager.SaveGame.GetData<List<string>>(RedFogHiddenKey, null) ?? new List<string>();

    private void StartRedFogServer(ICoreServerAPI api)
    {
        api.Network.RegisterChannel(RedFogChannel).RegisterMessageType<RedFogPacket>();
        api.Event.PlayerJoin += plr => SendRedFog(plr);

        var p = api.ChatCommands.Parsers;
        RegisterCmd(api, "genfog", name =>
            api.ChatCommands.Create(name)
                .WithDescription("Red fog zones (the Great Machine's shroud). /genfog list; /genfog here [radius] [fade] adds one centred on you; /genfog clear removes them all. "
                    + "/genfog dome|haze|embers on|off switches one part for this world: the dome is the red sphere seen from outside, the haze is the fog inside, the embers are the rising particles. The Great Machine adds its own zone when it is built.")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(p.OptionalWord("action"), p.OptionalAll("rest"))
                .HandleWith(OnGenFog));
    }

    private TextCommandResult OnGenFog(TextCommandCallingArgs args)
    {
        string action = args.Parsers[0].IsMissing ? "list" : ((string)args[0]).ToLowerInvariant();
        string[] rest = args.Parsers[1].IsMissing ? Array.Empty<string>() : ((string)args[1]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int mx = sapi.WorldManager.MapSizeX / 2, mz = sapi.WorldManager.MapSizeZ / 2;
        // "sphere" and "particles" are what people call them; the parts are dome and embers
        string part = action switch { "sphere" => "dome", "particles" => "embers", "fog" => "haze", _ => action };
        if (Array.IndexOf(RedFogParts, part) >= 0)
        {
            var hidden = RedFogHidden();
            string want = rest.Length > 0 ? rest[0].ToLowerInvariant() : "";
            if (want != "on" && want != "off")
                return TextCommandResult.Success($"Red fog {part} is {(hidden.Contains(part) ? "off" : "on")} in this world. /genfog {part} on|off");
            hidden.Remove(part);
            if (want == "off") hidden.Add(part);
            sapi.WorldManager.SaveGame.StoreData(RedFogHiddenKey, hidden);
            SendRedFog(null);
            return TextCommandResult.Success($"Red fog {part} {want} for this world.");
        }
        switch (action)
        {
            case "here":
            {
                var pos = args.Caller.Pos;
                if (pos == null) return TextCommandResult.Error("Needs a position: run it as a player.");
                int radius = rest.Length > 0 && int.TryParse(rest[0], out int r) ? r : 640;
                int fade = rest.Length > 1 && int.TryParse(rest[1], out int f) ? f : 200;
                int dim = (int)(pos.Y / BlockPos.DimensionBoundary);
                RegisterRedFogZone(dim, (int)pos.X, (int)pos.Z, radius, fade);
                return TextCommandResult.Success($"Red fog zone added at {(int)pos.X - mx}, {(int)pos.Z - mz}, radius {radius}, fade {fade}.");
            }
            case "clear":
                sapi.WorldManager.SaveGame.StoreData(RedFogKey, new List<string>());
                SendRedFog(null);
                return TextCommandResult.Success("All red fog zones removed.");
            default:
            {
                var lines = new List<string>();
                foreach (string s in RedFogList())
                    if (FogZone.TryParse(s, out var z)) lines.Add($"  {z.X - mx}, {z.Z - mz} radius {z.Radius} fade {z.Fade}" + (z.Dim != 0 ? $" (dimension {z.Dim})" : ""));
                var hidden = RedFogHidden();
                string parts = string.Join(", ", RedFogParts.Select(pt => pt + " " + (hidden.Contains(pt) ? "off" : "on")));
                return TextCommandResult.Success((lines.Count == 0 ? "No red fog zones." : "Red fog zones:\n" + string.Join("\n", lines)) + "\nParts: " + parts);
            }
        }
    }

    // A zone replaces any other zone centred within 32 blocks of it, so
    // rebuilding an island does not stack fogs.
    private void RegisterRedFogZone(int dim, int x, int z, int radius, int fade)
    {
        var list = RedFogList();
        list.RemoveAll(s => FogZone.TryParse(s, out var o) && o.Dim == dim && Math.Abs(o.X - x) < 32 && Math.Abs(o.Z - z) < 32);
        list.Add(new FogZone { Dim = dim, X = x, Z = z, Radius = Math.Max(16, radius), Fade = Math.Max(8, fade) }.ToString());
        sapi.WorldManager.SaveGame.StoreData(RedFogKey, list);
        sapi.Logger.Notification("[landmassgenerator] red fog zone at {0}, {1}, radius {2}, fade {3}",
            x - sapi.WorldManager.MapSizeX / 2, z - sapi.WorldManager.MapSizeZ / 2, radius, fade);
        SendRedFog(null);
    }

    private void SendRedFog(IServerPlayer to)
    {
        var ch = sapi.Network.GetChannel(RedFogChannel);
        if (ch == null) return;
        var hidden = RedFogHidden();
        var pkt = new RedFogPacket
        {
            Zones = RedFogList(),
            HideDome = hidden.Contains("dome"),
            HideHaze = hidden.Contains("haze"),
            HideEmbers = hidden.Contains("embers")
        };
        if (to != null) ch.SendPacket(pkt, to);
        else ch.BroadcastPacket(pkt);
    }

    // ── client ──────────────────────────────────────────────────────────

    private List<FogZone> _fogZones = new();
    private AmbientModifier _fogAmbient;
    private volatile float _fogStrength;
    private SimpleParticleProperties _emberParticles, _ashParticles;
    private readonly Random _fogRand = new();
    private ModSystemDevastationEffects _devaEffects;
    private volatile bool _hideDome, _hideHaze, _hideEmbers;

    // How it looks. FogDensity is the ambient fog close up (vanilla's
    // devastation uses 0.05, a little thicker than this, so the blades
    // still read as shapes inside); the sphere density is per block of
    // view ray through the dome.
    private static readonly float[] RedFogColor = { 0.50f, 0.10f, 0.07f };
    private const float RedFogDensity = 0.034f, RedFogSphereDensity = 0.004f;

    private void StartRedFogClient(ICoreClientAPI api)
    {
        api.Network.RegisterChannel(RedFogChannel).RegisterMessageType<RedFogPacket>()
            .SetMessageHandler<RedFogPacket>(pkt =>
            {
                var zones = new List<FogZone>();
                foreach (string s in pkt.Zones ?? new List<string>())
                    if (FogZone.TryParse(s, out var z)) zones.Add(z);
                _fogZones = zones;
                _hideDome = pkt.HideDome;
                _hideHaze = pkt.HideHaze;
                _hideEmbers = pkt.HideEmbers;
            });

        _fogAmbient = new AmbientModifier
        {
            FogColor = new WeightedFloatArray((float[])RedFogColor.Clone(), 0),
            FogDensity = new WeightedFloat(RedFogDensity, 0),
            AmbientColor = new WeightedFloatArray(new[] { 1.06f, 0.86f, 0.82f }, 0),
        }.EnsurePopulated();
        api.Ambient.CurrentModifiers["lgRedFog"] = _fogAmbient;

        // Embers: small, glowing, fast. Ash: bigger dark red flakes, slow.
        _emberParticles = new SimpleParticleProperties
        {
            MinQuantity = 1, AddQuantity = 0,
            Color = ColorUtil.ToRgba(255, 255, 86, 34),
            MinPos = new Vec3d(), MinVelocity = new Vec3f(), AddVelocity = new Vec3f(),
            LifeLength = 3.5f, addLifeLength = 3f,
            MinSize = 0.12f, MaxSize = 0.32f,
            GravityEffect = -0.015f, SelfPropelled = true, WindAffected = true,
            WithTerrainCollision = false, VertexFlags = 200,
            ParticleModel = EnumParticleModel.Quad,
            OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.QUADRATIC, -16f)
        };
        _ashParticles = new SimpleParticleProperties
        {
            MinQuantity = 1, AddQuantity = 0,
            Color = ColorUtil.ToRgba(210, 96, 26, 20),
            MinPos = new Vec3d(), MinVelocity = new Vec3f(), AddVelocity = new Vec3f(),
            LifeLength = 6f, addLifeLength = 4f,
            MinSize = 0.25f, MaxSize = 0.7f,
            GravityEffect = -0.006f, SelfPropelled = true, WindAffected = true,
            WithTerrainCollision = false,
            ParticleModel = EnumParticleModel.Quad,
            OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.QUADRATIC, -12f)
        };
        api.Event.RegisterAsyncParticleSpawner(RedFogParticles);
        api.Event.RegisterRenderer(new RedFogRenderer(this), EnumRenderStage.Before, "lgredfog");
    }

    // 1 inside a zone, fading to 0 across its fade band; the strongest zone
    // wins. Also hands back the zone whose outer edge is nearest, and the
    // distance to its centre.
    private float RedFogStrengthAt(double x, double z, int dim, out FogZone nearest, out double nearestDist)
    {
        float best = 0;
        nearest = default;
        nearestDist = double.MaxValue;
        double bestGap = double.MaxValue;
        foreach (var zn in _fogZones)
        {
            if (zn.Dim != dim) continue;
            double d = Math.Sqrt((x - zn.X) * (x - zn.X) + (z - zn.Z) * (z - zn.Z));
            double gap = d - zn.Radius - zn.Fade;
            if (gap < bestGap) { bestGap = gap; nearest = zn; nearestDist = d; }
            float w = (float)(1 - Smooth((d - zn.Radius) / zn.Fade));
            if (w > best) best = w;
        }
        return best;
    }

    private void RedFogFrame()
    {
        var plr = capi.World?.Player?.Entity;
        if (plr == null || _fogAmbient == null) return;
        int dim = plr.Pos.Dimension;
        float w = RedFogStrengthAt(plr.Pos.X, plr.Pos.Z, dim, out var z0, out double d0);
        _fogStrength = w;
        float haze = _hideHaze ? 0 : w;
        _fogAmbient.FogColor.Weight = haze;
        _fogAmbient.FogDensity.Weight = haze;
        _fogAmbient.AmbientColor.Weight = haze * 0.5f;

        // The dome. Vanilla's devastation effects run just before this
        // (same stage, lower render order) and set the sphere count to 0 or
        // 1 whenever the devastation area exists; with no devastation area
        // nobody else writes it, so the base is 0.
        var uni = capi.Render.ShaderUniforms;
        _devaEffects ??= capi.ModLoader.GetModSystem<ModSystemDevastationEffects>();
        int baseSlot = _devaEffects?.DevaLocationPresent != null ? uni.FogSphereQuantity : 0;
        uni.FogSphereQuantity = baseSlot;
        if (_hideDome || d0 == double.MaxValue || baseSlot >= 3) return;
        double outer = z0.Radius + z0.Fade;
        if (d0 > outer * 4) return;
        // A cap of a huge sphere: base radius about the zone's outer edge at
        // sea level, top 120 blocks above the sea.
        double capH = 120, capA = z0.Radius + z0.Fade * 0.5;
        double rs = (capA * capA + capH * capH) / (2 * capH);
        double cy = capi.World.SeaLevel + capH - rs;
        float bright = capi.Ambient.BlendedFogBrightness * capi.Ambient.BlendedSceneBrightness;
        float near = (float)Math.Clamp(1.5 * (1 - d0 / (outer * 4)), 0, 1);
        int o = baseSlot * 8;
        uni.FogSpheres[o] = (float)(z0.X + 0.5 - plr.Pos.X);
        uni.FogSpheres[o + 1] = (float)(cy - plr.Pos.Y);
        uni.FogSpheres[o + 2] = (float)(z0.Z + 0.5 - plr.Pos.Z);
        uni.FogSpheres[o + 3] = (float)rs;
        uni.FogSpheres[o + 4] = RedFogSphereDensity * near;
        uni.FogSpheres[o + 5] = RedFogColor[0] * bright;
        uni.FogSpheres[o + 6] = RedFogColor[1] * bright;
        uni.FogSpheres[o + 7] = RedFogColor[2] * bright;
        uni.FogSphereQuantity = baseSlot + 1;
    }

    // Embers and ash rising from the ground round the player, as many as
    // the fog is strong.
    private bool RedFogParticles(float dt, IAsyncParticleManager manager)
    {
        float w = _fogStrength;
        if (_hideEmbers || w <= 0.01f || _emberParticles == null) return true;
        var plr = capi.World?.Player?.Entity;
        if (plr == null) return true;
        var r = _fogRand;
        double px = plr.Pos.X, py = plr.Pos.Y, pz = plr.Pos.Z;
        int n = (int)(dt * 150 * w + r.NextDouble());
        var ba = capi.World.BlockAccessor;
        for (int i = 0; i < n; i++)
        {
            double x = px + (r.NextDouble() - 0.5) * 96, z = pz + (r.NextDouble() - 0.5) * 96;
            int rain = ba.GetRainMapHeightAt((int)x, (int)z);
            double y = Math.Max(rain, py - 24) + r.NextDouble() * 20 - 1;
            bool ember = r.NextDouble() < 0.35;
            var prop = ember ? _emberParticles : _ashParticles;
            prop.MinPos.Set(x, y, z);
            prop.MinVelocity.Set((float)(r.NextDouble() - 0.5) * 0.3f, (ember ? 0.5f : 0.2f) + (float)r.NextDouble() * (ember ? 0.7f : 0.3f), (float)(r.NextDouble() - 0.5) * 0.3f);
            manager.Spawn(prop);
        }
        return true;
    }

    private sealed class RedFogRenderer : IRenderer
    {
        private readonly LandmassGeneratorModSystem mod;
        public RedFogRenderer(LandmassGeneratorModSystem mod) { this.mod = mod; }
        public double RenderOrder => 1.05;   // after vanilla's devastation fog (1.0)
        public int RenderRange => 1000;
        public void OnRenderFrame(float deltaTime, EnumRenderStage stage) => mod.RedFogFrame();
        public void Dispose() { }
    }
}
