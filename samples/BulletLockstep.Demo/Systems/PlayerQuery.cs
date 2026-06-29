using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo.Systems;

// Helper for deterministic player iteration. In placeholder mode each host's
// local entity id for "player N" differs from other hosts — so iterating by
// entity id gives different logical orders on different hosts. To keep
// post-replay systems byte-identical across hosts, we iterate by the logical
// key PlayerTag.HostId.
//
// Static buffer: simulator is single-threaded, so reuse across systems within
// a frame is safe.
public static class PlayerQuery
{
    private static readonly QueryDescription Query = new QueryDescription().With<PlayerTag>();

    private static readonly List<(Entity Entity, PlayerTag Tag)> _buffer = new(64);

    // Returns players sorted by PlayerTag.HostId. Caller must not hold the
    // returned list across structural changes (it's reused next call).
    public static List<(Entity Entity, PlayerTag Tag)> SortedByHostId(World world)
    {
        _buffer.Clear();
        foreach (var e in world.Query(in Query))
            _buffer.Add((e, world.Get<PlayerTag>(e)));
        _buffer.Sort((a, b) => a.Tag.HostId.CompareTo(b.Tag.HostId));
        return _buffer;
    }
}
