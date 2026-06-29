using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo.Systems;

// Task 4.5 — Shield grant via deterministic Add.
// Every TriggerEvery frames (offset from BurningTrigger so they don't collide),
// grants Shield to a rotating player if they don't already have one.
// Combined with TickDamageSystem's shield-depletion Remove, this exercises the
// full Add -> (damage) -> Remove archetype migration cycle.
public static class ShieldGrantSystem
{
    public const int TriggerEvery = 11;
    public const int ShieldAmount = 60;  // ~12 frames of base damage soak

    public static void Run(World world, int frame, int hostCount)
    {
        // Offset by TriggerEvery/2 to desync from BurningTriggerSystem.
        if (frame % TriggerEvery != TriggerEvery / 2)
            return;

        var targetHostId = ((frame / TriggerEvery) + 1) % hostCount;
        var players = PlayerQuery.SortedByHostId(world);
        if (targetHostId >= players.Count)
            return;

        var (entity, _) = players[targetHostId];
        if (world.Has<Shield>(entity))
            return;  // already shielded — wait for it to deplete

        world.Add(entity, new Shield(ShieldAmount, ShieldAmount));
    }
}
