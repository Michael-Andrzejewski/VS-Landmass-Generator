using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace LandmassGenerator;

// Runaway herd guard (Rustfall worlds only). On the Starter Island the
// game's runtime spawner kept adding wild sows: 384 adult and elder female
// pigs in one session, and not one boar or piglet. A pig herd is spawned as
// a male leader plus female and piglet companions, and the spawn cap is
// checked against the leader's kind; groups whose leader never appeared
// left nothing that counted, so the cap never closed. Whatever drops the
// leader, this refuses a natural spawn when the area already holds
// HerdCapMax creatures of the same kind (first code part: pig, chicken,
// deer...) within HerdCapRadius blocks. Real herds stay far below it.
public partial class LandmassGeneratorModSystem
{
    private const int HerdCapMax = 12;
    private const float HerdCapRadius = 48;
    // Worldgen fires the same event from the chunk thread, where reading
    // loaded entities is not safe; only the main thread (the runtime
    // spawner) is checked. Set when the guard is registered.
    internal int herdCapMainThread = -1;

    private bool OnTrySpawnHerdCap(IBlockAccessor ba, ref EntityProperties properties, Vec3d pos, long herdId)
    {
        if (System.Environment.CurrentManagedThreadId != herdCapMainThread) return true;
        string kind = properties?.Code?.FirstCodePart();
        if (kind == null || kind == "player") return true;
        int n = 0;
        foreach (Entity e in sapi.World.GetEntitiesAround(pos, HerdCapRadius, HerdCapRadius / 2,
            e => e.Alive && e.Code?.FirstCodePart() == kind))
        {
            if (++n >= HerdCapMax) return false;
        }
        return true;
    }
}
