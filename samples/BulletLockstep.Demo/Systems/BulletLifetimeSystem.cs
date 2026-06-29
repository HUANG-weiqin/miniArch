using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo.Systems;

// Deterministic post-replay system: destroys transient entities older than
// LifetimeFrames. Runs identically on every host (same SpawnFrame values ->
// same destroy set), so destroy order matters: iterate in stable entity-id
// order to keep CanonicalChecksum aligned.
//
// Two-pass: collect first (can't mutate during query iteration), destroy
// after. Destroy is a structural change, applied directly to the local world.
// No record, no delta — every host independently reaches the same end state.
public static class BulletLifetimeSystem
{
    public const int LifetimeFrames = 60;

    private static readonly QueryDescription Query = new QueryDescription()
        .With<SpawnFrame>();

    public static void Run(World world, int frame)
    {
        // Collect entities to destroy. Reuse a growable list (static to avoid
        // per-call alloc — single-threaded simulator).
        _toDestroy.Clear();
        foreach (var entity in world.Query(in Query))
        {
            var sf = world.Get<SpawnFrame>(entity);
            if (frame - sf.Frame >= LifetimeFrames)
                _toDestroy.Add(entity);
        }

        // Stable order: sort by Entity (Id, then Version) so every host
        // destroys in the same sequence. Entity is a record struct -> default
        // comparer gives (Id, Version) ordering.
        _toDestroy.Sort();
        foreach (var e in _toDestroy)
            world.Destroy(e);
    }

    private static readonly List<Entity> _toDestroy = new(256);
}
