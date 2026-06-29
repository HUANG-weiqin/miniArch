using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo.Systems;

// Boss state machine + deterministic HP drain. Each frame:
//   - Advance PhaseFrame; every PhaseLength frames, bump Phase (cycles 0..2).
//   - Drain 1 HP per frame from the boss (placeholder for "players damage
//     boss" — real collision comes in Slice 6).
//   - When Health.Cur <= 0: world.Destroy(boss). The library cascades the
//     destroy through the hierarchy, removing all linked WeakPoints.
//
// Phase changes are recorded as a Set on AIPattern. Phase 0 -> 1 -> 2 -> 0
// cycle is fully deterministic.
public static class BossAISystem
{
    private const int PhaseLength = 200;
    private const int HpDrainPerFrame = 1;

    private static readonly QueryDescription Query = new QueryDescription().With<BossTag>();

    public static void Run(World world, int frame)
    {
        foreach (var boss in world.Query(in Query))
        {
            var pattern = world.Get<AIPattern>(boss);
            var hp = world.Get<Health>(boss);

            var newPhaseFrame = pattern.PhaseFrame + 1;
            var newPhase = pattern.Phase;
            if (newPhaseFrame >= PhaseLength)
            {
                newPhase = (pattern.Phase + 1) % 3;
                newPhaseFrame = 0;
            }
            world.Set(boss, new AIPattern(newPhase, newPhaseFrame));

            var newHp = Math.Max(0, hp.Cur - HpDrainPerFrame);
            world.Set(boss, hp with { Cur = newHp });

            if (newHp == 0)
            {
                // Cascade destroy — library destroys all linked children.
                world.Destroy(boss);
            }
            // Only one boss expected; break out.
            break;
        }
    }
}
