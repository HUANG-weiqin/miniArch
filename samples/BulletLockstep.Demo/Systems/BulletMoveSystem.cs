using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo.Systems;

// Deterministic post-replay system: advances every transient entity's
// position by its velocity. Slice 7 upgrade: parallel chunk iteration for
// large entity counts.
//
// Parallel safety: ChunkView.GetSpan<T>() returns a writable span into the
// archetype's storage. Distinct chunks live in distinct memory; writing
// positions[i] from one chunk never races with another chunk's write. No
// structural changes inside the parallel body (those still go through the
// simulator's record phase via CommandStream).
//
// Determinism: each chunk's rows are updated independently from their old values
// (positions[i] = f(positions[i], velocities[i])), so the result does not depend
// on chunk partitioning or execution order. Different Parallel.For partitions
// across hosts still converge to identical final state.
public static class BulletMoveSystem
{
    private static readonly QueryDescription Query = new QueryDescription()
        .With<Position>()
        .With<Velocity>();

    public static void Run(World world)
    {
        var query = world.Query(in Query);
        query.ForEachChunkParallel(chunk =>
        {
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetSpan<Velocity>();
            for (var i = 0; i < positions.Length; i++)
            {
                positions[i] = new Position(
                    positions[i].X + velocities[i].Dx,
                    positions[i].Y + velocities[i].Dy);
            }
        });
    }
}
