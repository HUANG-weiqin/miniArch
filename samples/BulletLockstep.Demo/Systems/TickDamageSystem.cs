using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo.Systems;

// Demonstrates EntityAccessor (Task 4.2). Applies a deterministic per-tick
// damage to every player. If the player has a Shield, damage is absorbed by
// Shield first; otherwise it goes to Health. If BurningTimer is present, an
// extra burn damage is applied.
//
// Important: the EntityAccessor is a ref struct that caches the archetype row.
// We must discard it before any structural change (Add/Remove). So we collect
// "shields that hit 0" during the read/write pass and Remove<Shield> them
// afterwards (Task 4.5 — Add/Remove complete cycle).
public static class TickDamageSystem
{
    private const int BaseDamage = 5;
    private const int BurnBonus = 3;

    private static readonly List<Entity> _depletedShields = new(32);

    public static void Run(World world)
    {
        var players = PlayerQuery.SortedByHostId(world);
        _depletedShields.Clear();

        foreach (var (entity, _) in players)
        {
            var acc = world.Access(entity);
            ref var hp = ref acc.Get<Health>();
            var dmg = BaseDamage;
            if (acc.Has<BurningTimer>())
                dmg += BurnBonus;

            if (acc.Has<Shield>())
            {
                ref var shield = ref acc.Get<Shield>();
                var absorbed = Math.Min(shield.Cur, dmg);
                shield = shield with { Cur = shield.Cur - absorbed };
                dmg -= absorbed;
                if (shield.Cur == 0)
                    _depletedShields.Add(entity);
            }

            if (dmg > 0)
            {
                hp = hp with { Cur = Math.Max(0, hp.Cur - dmg) };
            }
            // accessor discarded here (scope end) — safe to structural-change now
        }

        // Remove depleted shields. Order is deterministic (host id order from
        // PlayerQuery + stable List order). Each Remove triggers an archetype
        // migration for that player.
        foreach (var e in _depletedShields)
            world.Remove<Shield>(e);
    }
}
