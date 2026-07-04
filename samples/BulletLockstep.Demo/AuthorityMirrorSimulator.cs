using BulletLockstep.Demo.Systems;
using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo;

// Authority + Mirror topology (Scenario C from kb-deferred-create-design).
// Single authority host owns the simulation; M mirror hosts receive real-id
// deltas and replay them verbatim. All hosts must have synchronized id
// allocators — ensured by every mirror replaying the same delta sequence
// from frame 0.
//
// Slice 8: exercises SubmitAndSnapshotAsync (pipelined submit + delta build)
// on the authority side. Distinct from Slice 1-7 P2P topology where each
// host has an independent id allocator and exchanges placeholder deltas.
public sealed class AuthorityMirrorSimulator
{
    private readonly LockstepHost _authority;
    private readonly World[] _mirrors;
    public int MirrorCount => _mirrors.Length;
    public LockstepHost Authority => _authority;
    public IReadOnlyList<World> Mirrors => _mirrors;

    public AuthorityMirrorSimulator(int mirrorCount, bool spawnBoss)
    {
        // Authority uses DeferredEntities=false so Create() reserves real ids
        // immediately. Snapshot() then emits a real-id delta that mirrors
        // can replay verbatim (their allocators stay synced from frame 0).
        _authority = new LockstepHost(0)
        {
            Stream = { DeferredEntities = false }
        };
        _mirrors = new World[mirrorCount];
        for (var i = 0; i < mirrorCount; i++)
            _mirrors[i] = new World();
        _spawnBoss = spawnBoss;
    }

    private readonly bool _spawnBoss;

    // Runs one authority tick: record, SubmitAndSnapshotAsync (pipelined),
    // wait for delta, replay on every mirror, run deterministic systems on
    // every host (authority + mirrors).
    public bool Tick(int frame)
    {
        // 1. Authority records (real-id reservations).
        if (frame == 0)
            _authority.RecordInit(_spawnBoss);
        else
            _authority.RecordFrame(frame);

        // 2. Pipelined submit + delta build. The main thread applies the
        //    commands to the authority's world; a background task builds the
        //    real-id delta from a frozen snapshot of the recorded state.
        var deltaTask = _authority.Stream.SubmitAndSnapshotAsync();
        // Await the background builder. By the time we get here, Submit has
        // typically finished on the main thread; the delta is ready shortly.
        var delta = deltaTask.GetAwaiter().GetResult();

        // 3. Mirrors replay the authority's delta. Their allocators advance
        //    in lockstep with the authority since they all started empty.
        foreach (var m in _mirrors)
            new CommandStream(m).Replay(delta);

        // 4. Deterministic systems on every host.
        RunSystems(_authority.World, frame);
        foreach (var m in _mirrors)
            RunSystems(m, frame);

        // 5. Compare canonical checksums.
        var refChecksum = _authority.Checksum();
        foreach (var m in _mirrors)
        {
            if (!EqualBytes(refChecksum, m.CanonicalChecksum()))
                return false;
        }
        return true;
    }

    private static void RunSystems(World world, int frame)
    {
        BulletMoveSystem.Run(world);
        BulletLifetimeSystem.Run(world, frame);

        // Player status systems no-op when there are no players (frame 0 has
        // not happened yet on mirrors — they receive players via replay).
        var playerDesc = new QueryDescription().With<PlayerTag>();
        var hasPlayers = false;
        foreach (var _ in world.Query(in playerDesc))
        {
            hasPlayers = true;
            break;
        }
        if (!hasPlayers)
            return;

        BurningTriggerSystem.Run(world, frame, hostCount: 1);
        ShieldGrantSystem.Run(world, frame, hostCount: 1);
        TickDamageSystem.Run(world);
        StatusTimerSystem.Run(world);
    }

    private static bool EqualBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
