using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo.Systems;

// Task 4.4 — Burning injection via deterministic Add.
// Every TriggerEvery frames, the system picks one player (rotating by
// PlayerTag.HostId) and grants them BurningTimer if they don't already have
// one. Add is a structural change that migrates the player to a new archetype.
//
// The target player is chosen by (frame / TriggerEvery) % hostCount — same
// logical choice on every host, regardless of local entity id.
public static class BurningTriggerSystem
{
    public const int TriggerEvery = 7;
    public const int BurnDuration = 12;

    public static void Run(World world, int frame, int hostCount)
    {
        if (frame % TriggerEvery != 0)
            return;

        var targetHostId = (frame / TriggerEvery) % hostCount;

        // Find target player by logical HostId (not local entity id).
        var players = PlayerQuery.SortedByHostId(world);
        if (targetHostId >= players.Count)
            return;

        var (entity, _) = players[targetHostId];

        // Only Add if not already burning. has-check first avoids pointless
        // structural migration (Add when component already present is a no-op
        // at the world API level but still costs an archetype lookup).
        if (world.Has<BurningTimer>(entity))
            return;

        world.Add(entity, new BurningTimer(BurnDuration));
    }
}
