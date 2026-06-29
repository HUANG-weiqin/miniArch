using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo.Systems;

// Task 4.3 — StatusTimerSystem: ticks down BurningTimer each frame, and
// Remove's the component when it hits 0. Each Remove is a structural change
// that migrates the player back to the base archetype.
//
// Iterates BurningTimer holders in PlayerTag.HostId order so multi-host
// application order is byte-identical. Collects "expired" entities first
// (cannot structural-change during query iteration), then Remove's in order.
public static class StatusTimerSystem
{
    private static readonly QueryDescription Query = new QueryDescription()
        .With<BurningTimer>()
        .With<PlayerTag>();

    private static readonly List<Entity> _expired = new(32);

    public static void Run(World world)
    {
        // Collect (entity, hostId) pairs for stable ordering.
        var pairs = new List<(Entity e, int hostId)>(32);
        foreach (var e in world.Query(in Query))
            pairs.Add((e, world.Get<PlayerTag>(e).HostId));
        pairs.Sort((a, b) => a.hostId.CompareTo(b.hostId));

        // Decrement phase — in-place value update, no structural change.
        foreach (var (e, _) in pairs)
        {
            var t = world.Get<BurningTimer>(e);
            var next = t.Remaining - 1;
            world.Set(e, new BurningTimer(next));
            if (next <= 0)
                _expired.Add(e);
        }

        // Structural phase — Remove expired timers in HostId order.
        foreach (var e in _expired)
            world.Remove<BurningTimer>(e);
        _expired.Clear();
    }
}
