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

    // Slice 7: scale mode cranks boss ring-pattern spawn rate to push a single
    // archetype past the chunked-storage threshold (~50K entities for our
    // 28-byte bullet layout). Used to verify chunked storage stays byte-
    // identical across hosts.
    public bool ScaleMode { get; init; } = false;

    // Reusable per-tick delta buffer (Slice 7 alloc reduction). Allocated once
    // at first tick; reused across ticks. Caller still receives a valid
    // reference for the current tick's deltas (overwritten on next tick).
    private FrameDelta[]? _deltaBuffer;

    // Slice 7: track peak concurrent entity count across all hosts during the
    // run. End-of-run snapshot misses peaks that occur mid-run (e.g. bullet
    // waves that get destroyed before the run ends).
    public int PeakEntityCount { get; private set; }

    public LockstepSimulator(int hostCount)
    {
        _hosts = new LockstepHost[hostCount];
        for (var i = 0; i < hostCount; i++)
            _hosts[i] = new LockstepHost(i);
    }

    public bool Tick(int frame) => Tick(frame, out _);

    // Slice 7: ScaleMode reduces checksum frequency (every 50 frames) because
    // CanonicalChecksum is O(N) with SHA256 — too slow to run per-frame at
    // 30K+ entities. Determinism is still verified at sample points; the
    // post-replay systems run every frame on every host regardless.
    private bool ShouldChecksum(int frame) => !ScaleMode || frame % 50 == 0;

    public bool Tick(int frame, out FrameDelta[] deltas)
    {
        // 1. Record. Frame 0 = player init (if enabled); else bullet fire.
        foreach (var h in _hosts)
        {
            if (SpawnPlayers && frame == 0)
                h.RecordInit(SpawnBoss, ScaleMode);
            else
                h.RecordFrame(frame, ScaleMode);
        }

        // 2. Snapshot each host's intent to a placeholder delta, then Clear.
        //    Slice 7: reuse the same buffer across ticks to avoid per-frame
        //    allocation. Snapshot returns a fresh FrameDelta object per host
        //    (its internals are pooled inside CommandStream), but the array
        //    holding them is reused.
        if (_deltaBuffer is null || _deltaBuffer.Length != _hosts.Length)
            _deltaBuffer = new FrameDelta[_hosts.Length];
        for (var i = 0; i < _hosts.Length; i++)
        {
            _deltaBuffer[i] = _hosts[i].Stream.Snapshot();
            _hosts[i].Stream.Clear();
        }
        deltas = _deltaBuffer;

        // 3. Every host replays all deltas in fixed HostId order.
        ReplayDeltasOnAllHosts(deltas);

        // 4. Deterministic post-replay systems. Order matters — same on every host.
        foreach (var h in _hosts)
        {
            RunSystems(h.World, frame);
            var count = h.World.GetStats().EntityCount;
            if (count > PeakEntityCount)
                PeakEntityCount = count;
        }

        // 5. Compare canonical checksums at sampled frames (every frame in
        //    normal mode; every 50 frames in ScaleMode to keep runtime sane).
        if (!ShouldChecksum(frame))
            return true;

        var refChecksum = _hosts[0].Checksum();
        for (var i = 1; i < _hosts.Length; i++)
        {
            var other = _hosts[i].Checksum();
            if (!EqualBytes(refChecksum, other))
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

    // Slice 9 helper: runs a tick and returns a SNAPSHOT copy of the deltas
    // array (not the reused buffer). Use when the caller needs to keep deltas
    // across multiple ticks (e.g. rollback replay buffer). The per-host
    // FrameDelta objects themselves are independent across ticks (the stream
    // allocates fresh internals on each Snapshot), only the holding array is
    // reused by the pooled fast path.
    public FrameDelta[] TickAndSnapshotDeltas(int frame)
    {
        Tick(frame, out var deltas);
        var copy = new FrameDelta[deltas.Length];
        Array.Copy(deltas, copy, deltas.Length);
        return copy;
    }

    public void ReplayDeltasOnAllHosts(FrameDelta[] deltas)
    {
        foreach (var h in _hosts)
        {
            foreach (var d in deltas)
                h.Stream.Replay(d);
        }
    }

    public void ReplayAndTickSystemsOnHost(int hostIndex, FrameDelta[] deltas, int frame)
    {
        var h = _hosts[hostIndex];
        foreach (var d in deltas)
            h.Stream.Replay(d);
        RunSystems(h.World, frame);
    }

    // Slice 9: replaces a host's world with a fresh independent World (e.g.
    // a World.Clone() snapshot). The host's CommandStream is also rebound to
    // the new world. Used to emulate rollback-recovery: discard rogue state,
    // restart from a clean snapshot, replay authoritative deltas.
    public void ReplaceHostWorld(int hostIndex, World newWorld)
    {
        var oldHost = _hosts[hostIndex];
        _hosts[hostIndex] = new LockstepHost(oldHost.HostId, newWorld);
    }

    private static bool EqualBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
