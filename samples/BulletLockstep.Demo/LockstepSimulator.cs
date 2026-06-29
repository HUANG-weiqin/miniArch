using BulletLockstep.Demo.Systems;
using MiniArch.Core;

namespace BulletLockstep.Demo;

// Drives N hosts in P2P lockstep. Each frame:
//   1. Every host records its own intent (placeholder Create) into its stream.
//   2. Every host produces a placeholder FrameDelta via Snapshot (relay-only:
//      Clear, not Submit — the host's own world is updated only by replay).
//   3. Every host replays ALL N deltas in fixed HostId order. Each host maps
//      placeholder seqs to its own local ids, so the same logical entity may
//      have different local ids on different hosts — CanonicalChecksum still
//      matches because it ignores construction path.
//   4. Run deterministic post-replay systems (move + lifetime) on every host.
//   5. Compare CanonicalChecksum across all hosts.
//
// Slice 1 v2: no deterministic post-replay systems yet — pure Create pipeline
// test. Slice 2 adds direct-mutation systems (move / lifetime) on long-lived
// and transient entities.
public sealed class LockstepSimulator
{
    private readonly LockstepHost[] _hosts;
    public int HostCount => _hosts.Length;
    public IReadOnlyList<LockstepHost> Hosts => _hosts;

    public LockstepSimulator(int hostCount)
    {
        _hosts = new LockstepHost[hostCount];
        for (var i = 0; i < hostCount; i++)
            _hosts[i] = new LockstepHost(i);
    }

    public bool Tick(int frame)
    {
        // 1. Record
        foreach (var h in _hosts)
            h.RecordFrame(frame);

        // 2. Snapshot each host's intent to a placeholder delta, then Clear.
        FrameDelta[] deltas = new FrameDelta[_hosts.Length];
        for (var i = 0; i < _hosts.Length; i++)
        {
            deltas[i] = _hosts[i].Stream.Snapshot();
            _hosts[i].Stream.Clear();
        }

        // 3. Every host replays all deltas in fixed HostId order.
        foreach (var h in _hosts)
        {
            foreach (var d in deltas)
                h.World.Replay(d);
        }

        // 4. Deterministic post-replay systems. Run identically on every
        // host (no input, no record) — they mutate the local world directly.
        // Order: move first (in-place value bump), then lifetime (structural
        // destroy). Both must run in identical order on every host.
        foreach (var h in _hosts)
        {
            BulletMoveSystem.Run(h.World);
            BulletLifetimeSystem.Run(h.World, frame);
        }

        // 5. Compare canonical checksums across all hosts.
        var refChecksum = _hosts[0].Checksum();
        for (var i = 1; i < _hosts.Length; i++)
        {
            if (!EqualBytes(refChecksum, _hosts[i].Checksum()))
                return false;
        }
        return true;
    }

    private static bool EqualBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
