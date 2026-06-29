using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo.Systems;

// Updates each WeakPoint's Position from its parent Boss's Position +
// LocalOffset. Reads parent via World.TryGetParent — exercises the
// hierarchy runtime read path on every host.
//
// Iterate in PlayerTag.HostId-like deterministic order: there's no HostId on
// weakpoints, so we sort by WeakPointTag.Index instead (the logical key that
// is identical across hosts).
public static class WeakPointFollowSystem
{
    private static readonly QueryDescription Query = new QueryDescription()
        .With<WeakPointTag>()
        .With<LocalOffset>();

    private static readonly List<(Entity E, int Index)> _buffer = new(32);

    public static void Run(World world)
    {
        _buffer.Clear();
        foreach (var e in world.Query(in Query))
        {
            var tag = world.Get<WeakPointTag>(e);
            _buffer.Add((e, tag.Index));
        }
        _buffer.Sort((a, b) => a.Index.CompareTo(b.Index));

        foreach (var (e, _) in _buffer)
        {
            if (!world.TryGetParent(e, out var parent))
                continue;
            var parentPos = world.Get<Position>(parent);
            var offset = world.Get<LocalOffset>(e);
            world.Set(e, new Position(parentPos.X + offset.Dx, parentPos.Y + offset.Dy));
        }
    }
}
