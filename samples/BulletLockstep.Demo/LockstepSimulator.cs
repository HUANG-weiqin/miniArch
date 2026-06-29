using BulletLockstep.Demo.Systems;
using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo;

// Drives N hosts in P2P lockstep. Each frame:
//   1. Every host records its own intent (placeholder Create) into its stream.
//      Frame 0 records player creation; frame > 0 records bullet creation.
//   2. Every host produces a placeholder FrameDelta via Snapshot (relay-only:
//      Clear, not Submit).
//   3. Every host replays ALL N deltas in fixed HostId order.
//   4. Run deterministic post-replay systems on every host.
//   5. Compare CanonicalChecksum across all hosts.
//
// When SpawnPlayers is false the simulator runs the Slice 2 minimal flow
// (no players, no status systems). When true (Slice 4+) it exercises the
// full archetype migration pipeline.
public sealed class LockstepSimulator
{
    private readonly LockstepHost[] _hosts;
    public int HostCount => _hosts.Length;
    public IReadOnlyList<LockstepHost> Hosts => _hosts;
    public bool SpawnPlayers { get; init; } = true;

    // Slice 5: when true, host 0 also records a Boss + 5 linked WeakPoints in
    // frame 0 init, and homing-bullet + boss + weakpoint-follow systems run.
    public bool SpawnBoss { get; init; } = false;

    public LockstepSimulator(int hostCount)
    {
        _hosts = new LockstepHost[hostCount];
        for (var i = 0; i < hostCount; i++)
            _hosts[i] = new LockstepHost(i);
    }

    public bool Tick(int frame) => Tick(frame, out _);

    public bool Tick(int frame, out FrameDelta[] deltas)
    {
        // 1. Record. Frame 0 = player init (if enabled); else bullet fire.
        foreach (var h in _hosts)
        {
            if (SpawnPlayers && frame == 0)
                h.RecordInit(SpawnBoss);
            else
                h.RecordFrame(frame);
        }

        // 2. Snapshot each host's intent to a placeholder delta, then Clear.
        deltas = new FrameDelta[_hosts.Length];
        for (var i = 0; i < _hosts.Length; i++)
        {
            deltas[i] = _hosts[i].Stream.Snapshot();
            _hosts[i].Stream.Clear();
        }

        // 3. Every host replays all deltas in fixed HostId order.
        ReplayDeltasOnAllHosts(deltas);

        // 4. Deterministic post-replay systems. Order matters — same on every host.
        foreach (var h in _hosts)
            RunSystems(h.World, frame);

        // 5. Compare canonical checksums.
        var refChecksum = _hosts[0].Checksum();
        for (var i = 1; i < _hosts.Length; i++)
        {
            if (!EqualBytes(refChecksum, _hosts[i].Checksum()))
                return false;
        }
        return true;
    }

    private void RunSystems(World world, int frame)
    {
        // Slice 5: homing steer first — modifies bullet Velocity so the new
        // heading applies to this frame's move.
        if (SpawnBoss)
            HomingBulletSteerSystem.Run(world);

        // Slice 2 baseline: move + lifetime.
        BulletMoveSystem.Run(world);

        // Slice 6: bullet × player collision. Set<Health> + Destroy<bullet>.
        if (SpawnPlayers)
            CollisionSystem.Run(world);

        BulletLifetimeSystem.Run(world, frame);

        if (!SpawnPlayers)
            return;

        // Slice 5: boss AI + hierarchy follow. BossAISystem may Destroy the
        // boss -> cascade removes weakpoints.
        if (SpawnBoss)
        {
            BossAISystem.Run(world, frame);
            WeakPointFollowSystem.Run(world);
        }

        // Slice 4: status pipeline. Order: structural adds first, then damage
        // (uses EntityAccessor — no structural inside the read/write pass,
        // structural remove after), then timer (decrement + structural removes).
        BurningTriggerSystem.Run(world, frame, HostCount);
        ShieldGrantSystem.Run(world, frame, HostCount);
        TickDamageSystem.Run(world);
        StatusTimerSystem.Run(world);
    }

    public void ReplayDeltasOnAllHosts(FrameDelta[] deltas)
    {
        foreach (var h in _hosts)
        {
            foreach (var d in deltas)
                h.World.Replay(d);
        }
    }

    public void ReplayAndTickSystemsOnHost(int hostIndex, FrameDelta[] deltas, int frame)
    {
        var h = _hosts[hostIndex];
        foreach (var d in deltas)
            h.World.Replay(d);
        RunSystems(h.World, frame);
    }

    private static bool EqualBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
