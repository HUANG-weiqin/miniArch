using System.Runtime.CompilerServices;

using MiniArch;

namespace MiniArchTests.Core;

public sealed class MultiWorldCreateArchetypeCacheTests
{
    private readonly record struct Position(int Value);
    private readonly record struct Velocity(int Value);
    private readonly record struct Health(int Value);

    [Fact]
    public void BUG_alternating_worlds_reuse_warmed_Create_archetypes_without_allocations()
    {
        using var first = new World(entityCapacity: 8);
        using var second = new World(entityCapacity: 8);

        for (var iteration = 0; iteration < 16; iteration++)
        {
            CreateAndDestroy(first);
            CreateAndDestroy(second);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            CreateAndDestroy(first);
            CreateAndDestroy(second);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Create_cache_does_not_keep_world_alive()
    {
        var weak = CreateWorldWithoutDisposing();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weak.TryGetTarget(out _));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<World> CreateWorldWithoutDisposing()
    {
        var world = new World();
        _ = world.Create(new Position(1), new Velocity(2));
        return new WeakReference<World>(world);
    }

    private static void CreateAndDestroy(World world)
    {
        world.Destroy(world.Create(new Position(1)));
        world.Destroy(world.Create(new Position(2), new Velocity(3)));
        world.Destroy(world.Create(new Position(4), new Velocity(5), new Health(6)));
    }
}
