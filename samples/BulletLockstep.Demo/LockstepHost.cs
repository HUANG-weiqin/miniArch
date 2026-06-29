using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo;

// One lockstep peer. Owns an independent World + independent id allocator.
// DeferredEntities=true: Create() returns a placeholder; Snapshot emits a
// placeholder delta that every replaying host maps to its own local id.
//
// Consequence (kb 决策 #3): placeholder refs are single-frame only.
// Long-lived entities (players) are NOT referenced by record ops; they are
// located each frame via deterministic post-replay queries.
public sealed class LockstepHost
{
    public int HostId { get; }
    public World World { get; }
    public CommandStream Stream { get; }

    public LockstepHost(int hostId)
    {
        HostId = hostId;
        World = new World();
        Stream = new CommandStream(World) { DeferredEntities = true };
    }

    // Slice 1 v2: world starts empty. The only state changes flow through
    // placeholder deltas, so every host's logical state is identical even
    // though local entity ids may differ. (Slice 2 will spawn the local
    // "self" player here for movement system input.)

    // Records this host's intent for the frame. Slice 1 v2: only structural
    // Create of a transient "ping" entity. Carried components encode all the
    // data we need (no cross-frame Set on existing entities).
    public void RecordFrame(int frame)
    {
        var (dx, dy) = InputProvider.Get(HostId, frame);
        // Placeholder Create — delta will carry placeholder, replay maps to
        // each host's local id.
        var ping = Stream.Create();
        Stream.Add(ping, new SpawnFrame(frame));
        Stream.Add(ping, new FiredBy(HostId));
        Stream.Add(ping, new Position(0, 0));
        Stream.Add(ping, new Velocity(dx * 1000, dy * 1000));
    }

    public byte[] Checksum() => World.CanonicalChecksum();
}
