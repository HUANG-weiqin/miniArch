using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo.Systems;

// Steers homing bullets toward their target player. Each bullet's Target
// component stores a PlayerTag.HostId (logical key, not entity ref). The
// steer system resolves the target's Position via world.Query each frame,
// so every host arrives at the same velocity update despite differing
// local entity ids.
//
// Query: With<Target>. This distinguishes homing bullets from basic ones
// within the broader BulletTag family — same query pattern would extend to
// WithAny<Target, OtherMarker> if more bullet variants emerge.
public static class HomingBulletSteerSystem
{
    private const int SpeedCap = 2000;

    private static readonly QueryDescription Query = new QueryDescription()
        .With<Target>()
        .With<Velocity>()
        .With<Position>();

    public static void Run(World world)
    {
        foreach (var entity in world.Query(in Query))
        {
            var target = world.Get<Target>(entity);
            var targetPos = FindPlayerPosition(world, target.HostId);
            if (targetPos is null)
                continue;

            var pos = world.Get<Position>(entity);
            var vel = world.Get<Velocity>(entity);
            var turn = world.Get<TurnRate>(entity);

            var dx = targetPos.Value.X - pos.X;
            var dy = targetPos.Value.Y - pos.Y;
            var dist2 = (long)dx * dx + (long)dy * dy;
            if (dist2 == 0)
                continue;

            // Normalize direction (approx, fixed-point), then nudge velocity
            // toward target by TurnRate, capped at SpeedCap magnitude.
            var dist = (int)Math.Sqrt(dist2);
            var ndx = (int)((long)dx * 1000 / dist);
            var ndy = (int)((long)dy * 1000 / dist);

            var newDx = StepToward(vel.Dx, ndx * SpeedCap / 1000, turn.MilliPixPerFrameSq);
            var newDy = StepToward(vel.Dy, ndy * SpeedCap / 1000, turn.MilliPixPerFrameSq);
            world.Set(entity, new Velocity(newDx, newDy));
        }
    }

    private static int StepToward(int cur, int target, int maxStep)
    {
        if (cur == target) return cur;
        var delta = target - cur;
        if (delta > maxStep) delta = maxStep;
        if (delta < -maxStep) delta = -maxStep;
        return cur + delta;
    }

    private static Position? FindPlayerPosition(World world, int hostId)
    {
        var desc = PlayerQuery.Description;
        foreach (var e in world.Query(in desc))
        {
            if (world.Get<PlayerTag>(e).HostId == hostId)
                return world.Get<Position>(e);
        }
        return null;
    }
}
