using MiniArch;
using MiniArch.Core;

namespace BulletLockstep.Demo.Systems;

// Updates each WeakPoint's Position from the encounter Boss's Position +
// LocalOffset. Iterate in deterministic WeakPointTag.Index order.
public static class WeakPointFollowSystem
{
    private static readonly QueryDescription Query = new QueryDescription()
        .With<WeakPointTag>()
        .With<LocalOffset>();
    private static readonly QueryDescription BossQuery = new QueryDescription().With<BossTag>();

    private static readonly List<(Entity E, int Index)> _buffer = new(32);

    public static void Run(World world)
    {
        var bossPosition = default(Position);
        var bossFound = false;
        foreach (var boss in world.Query(in BossQuery))
        {
            bossPosition = world.Get<Position>(boss);
            bossFound = true;
            break;
        }
        if (!bossFound) return;

        _buffer.Clear();
        foreach (var e in world.Query(in Query))
        {
            var tag = world.Get<WeakPointTag>(e);
            _buffer.Add((e, tag.Index));
        }
        _buffer.Sort((a, b) => a.Index.CompareTo(b.Index));

        foreach (var (e, _) in _buffer)
        {
            var offset = world.Get<LocalOffset>(e);
            world.Set(e, new Position(bossPosition.X + offset.Dx, bossPosition.Y + offset.Dy));
        }
    }
}
