using BulletLockstep.Demo.Systems;
using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo;

// Slice 9 utilities: FrameDelta.Concat verification and World.Clone-based
// replay-buffer rollback. These exercise the netcode-focused APIs that
// complement the lockstep core (Slice 1-7) and authority topology (Slice 8).
//
// All methods are deterministic and verify CanonicalChecksum byte-equality
// across the compared paths.
public static class NetcodeVerification
{
    // Verifies that World.Clone() produces an independent world whose logical
    // state is identical to the original. Then runs both forward identically
    // to confirm they stay in lockstep (clone is fully decoupled, not sharing
    // any internal storage).
    public static (bool Ok, string Detail) VerifyWorldClone(LockstepSimulator sim, int frame)
    {
        var orig = sim.Hosts[0].World;
        var clone = orig.Clone();
        var a = orig.CanonicalChecksum();
        var b = clone.CanonicalChecksum();
        if (!BytesEqual(a, b))
            return (false, $"clone checksum mismatch:\n  orig:  {Convert.ToHexString(a)}\n  clone: {Convert.ToHexString(b)}");

        // Run 5 more frames on both worlds identically; they must stay equal.
        // We capture the deltas from one more tick on the simulator and replay
        // them onto the clone as well. Then we run the same post-replay
        // systems on the clone in the same order the simulator uses.
        for (var i = 0; i < 5; i++)
        {
            sim.Tick(frame + i, out var deltas);
            foreach (var d in deltas)
                clone.Replay(d);
            // System order must match LockstepSimulator.RunSystems exactly.
            if (sim.SpawnBoss)
                HomingBulletSteerSystem.Run(clone);
            BulletMoveSystem.Run(clone);
            if (sim.SpawnPlayers)
                CollisionSystem.Run(clone);
            BulletLifetimeSystem.Run(clone, frame + i);
            if (sim.SpawnBoss)
            {
                BossAISystem.Run(clone, frame + i);
                WeakPointFollowSystem.Run(clone);
            }
            if (sim.SpawnPlayers)
            {
                BurningTriggerSystem.Run(clone, frame + i, hostCount: sim.HostCount);
                ShieldGrantSystem.Run(clone, frame + i, hostCount: sim.HostCount);
                TickDamageSystem.Run(clone);
                StatusTimerSystem.Run(clone);
            }
        }
        var c = orig.CanonicalChecksum();
        var d2 = clone.CanonicalChecksum();
        if (!BytesEqual(c, d2))
            return (false, $"clone diverged after 5 forward frames:\n  orig:  {Convert.ToHexString(c)}\n  clone: {Convert.ToHexString(d2)}");

        return (true, $"clone byte-identical initially and after 5 forward frames");
    }

    // Verifies FrameDelta.Concat semantics: replaying `count` sequential deltas
    // produces the same world state as replaying their Concat() output once.
    public static (bool Ok, string Detail) VerifyFrameDeltaConcat(LockstepSimulator sim, int startFrame, int count)
    {
        // Path A: collect `count` frames of deltas, replay each sequentially
        // on a fresh world.
        var worldA = new World();
        // Path B: collect same deltas, concat them, replay concatenated on a fresh world.
        var worldB = new World();

        // To collect deltas, we drive the simulator. But the simulator also
        // applies them to its own hosts. We capture the deltas via the out
        // parameter and replay onto our fresh worlds.
        FrameDelta merged = null!;
        for (var i = 0; i < count; i++)
        {
            sim.Tick(startFrame + i, out var deltas);
            // Each frame's "delta" is an array of per-host deltas. Concatenate
            // them via Concat on path B. Replay each on path A.
            foreach (var d in deltas)
            {
                worldA.Replay(d);
                merged = merged is null ? d : FrameDelta.Concat(merged, d);
            }
        }
        worldB.Replay(merged);

        // Run the same deterministic systems on both (post-replay state).
        // Must match LockstepSimulator.RunSystems order exactly.
        var finalFrame = startFrame + count - 1;
        foreach (var w in new[] { worldA, worldB })
        {
            if (sim.SpawnBoss)
                HomingBulletSteerSystem.Run(w);
            BulletMoveSystem.Run(w);
            if (sim.SpawnPlayers)
                CollisionSystem.Run(w);
            BulletLifetimeSystem.Run(w, finalFrame);
            if (sim.SpawnBoss)
            {
                BossAISystem.Run(w, finalFrame);
                WeakPointFollowSystem.Run(w);
            }
            if (sim.SpawnPlayers)
            {
                BurningTriggerSystem.Run(w, finalFrame, hostCount: sim.HostCount);
                ShieldGrantSystem.Run(w, finalFrame, hostCount: sim.HostCount);
                TickDamageSystem.Run(w);
                StatusTimerSystem.Run(w);
            }
        }

        var a = worldA.CanonicalChecksum();
        var b = worldB.CanonicalChecksum();
        if (!BytesEqual(a, b))
            return (false, $"Concat replay diverged from sequential replay:\n  seq:    {Convert.ToHexString(a)}\n  concat: {Convert.ToHexString(b)}");

        return (true, $"Concat({count} deltas) replay == sequential replay");
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
