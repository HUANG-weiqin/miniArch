using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo.Systems;

// Deterministic post-replay system: advances every transient entity's
// position by its velocity. Runs identically on every host because the world
// state is identical after replay — no input needed, no record needed.
//
// Uses chunk-span iteration for in-place mutation (zero alloc, zero record).
public static class BulletMoveSystem
{
    private static readonly QueryDescription Query = new QueryDescription()
        .With<Position>()
        .With<Velocity>();

    public static void Run(World world)
    {
        foreach (var chunk in world.Query(in Query).GetChunks())
        {
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetSpan<Velocity>();
            for (var i = 0; i < positions.Length; i++)
            {
                positions[i] = new Position(
                    positions[i].X + velocities[i].Dx,
                    positions[i].Y + velocities[i].Dy);
            }
        }
    }
}
