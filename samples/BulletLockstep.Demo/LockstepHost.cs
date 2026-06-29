using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo;

// One lockstep peer. Owns an independent World + independent id allocator.
// DeferredEntities=true: Create() returns a placeholder; Snapshot emits a
// placeholder delta that every replaying host maps to its own local id.
//
// Consequence (kb 决策 #3): placeholder refs are single-frame only.
// Long-lived entities (players) are NOT referenced by record ops; they are
// located each frame via deterministic post-replay queries (by PlayerTag.HostId).
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

    // Frame 0: each host records Create(player) + Add player base components.
    // All hosts replay all deltas -> every host ends up with all N players.
    // Local entity ids may differ across hosts; logical state (by PlayerTag.HostId)
    // is identical -> CanonicalChecksum matches.
    public void RecordInit()
    {
        var player = Stream.Create();
        Stream.Add(player, new PlayerTag(HostId));
        Stream.Add(player, new Position(HostId * 10_000, 0));
        Stream.Add(player, new Velocity(0, 0));
        Stream.Add(player, new Health(1000, 1000));
        // No Shield / BurningTimer / PowerupState at spawn — those are added
        // and removed over time by deterministic systems (archetype migration).
    }

    // Frame > 0: each host records Create(bullet) for its own fired bullet.
    public void RecordFrame(int frame)
    {
        var (dx, dy) = InputProvider.Get(HostId, frame);
        var bullet = Stream.Create();
        Stream.Add(bullet, new BulletTag());
        Stream.Add(bullet, new SpawnFrame(frame));
        Stream.Add(bullet, new FiredBy(HostId));
        Stream.Add(bullet, new Position(HostId * 10_000, 0));
        Stream.Add(bullet, new Velocity(dx * 1000, dy * 1000));
        Stream.Add(bullet, new Damage(50));
    }

    public byte[] Checksum() => World.CanonicalChecksum();
}
