using MiniArch.Core;

namespace MiniArchTests.Core;

public sealed class RestoreCreateArchetypeCacheTests
{
    private readonly record struct Position(int Value);
    private readonly record struct Velocity(int Value);
    private readonly record struct Health(int Value);

    private readonly record struct LatePosition(int Value);
    private readonly record struct LateVelocity(int Value);
    private readonly record struct LateHealth(int Value);

    private readonly record struct ResetComponent(int Value);

    [Fact]
    public void RestoreState_preserves_warmed_create_caches_without_allocations()
    {
        using var world = new World(entityCapacity: 8);
        CreateAndDestroyCachedCombinations(world);
        for (var iteration = 0; iteration < 10; iteration++)
            RunRollbackCycle(world);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 8; iteration++)
            RunRollbackCycle(world);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void RestoreState_keeps_archetype_created_after_snapshot_available_for_Create()
    {
        using var world = new World();
        var snapshot = world.CaptureState();

        var predicted = world.Create(new LatePosition(1), new LateVelocity(2), new LateHealth(3));
        Assert.True(world.TryGetLocation(predicted, out var predictedLocation));

        world.RestoreState(snapshot);

        var restored = world.Create(new LatePosition(10), new LateVelocity(20), new LateHealth(30));

        Assert.True(world.TryGetLocation(restored, out var restoredLocation));
        Assert.Same(predictedLocation.Archetype, restoredLocation.Archetype);
        Assert.Equal(new LatePosition(10), world.Get<LatePosition>(restored));
        Assert.Equal(new LateVelocity(20), world.Get<LateVelocity>(restored));
        Assert.Equal(new LateHealth(30), world.Get<LateHealth>(restored));
    }

    [Fact]
    public void Reset_invalidates_create_cache_for_cleared_archetypes()
    {
        using var world = new World();
        var previous = world.Create(new ResetComponent(1));
        Assert.True(world.TryGetLocation(previous, out var previousLocation));

        world.Reset(entitySlotCount: 0);

        var current = world.Create(new ResetComponent(2));

        Assert.True(world.TryGetLocation(current, out var currentLocation));
        Assert.NotSame(previousLocation.Archetype, currentLocation.Archetype);
        Assert.Equal(new ResetComponent(2), world.Get<ResetComponent>(current));
    }

    private static void RunRollbackCycle(World world)
    {
        var snapshot = world.CaptureState();
        CreateAndDestroyCachedCombinations(world);
        world.RestoreState(snapshot);
        CreateAndDestroyCachedCombinations(world);
    }

    private static void CreateAndDestroyCachedCombinations(World world)
    {
        var position = world.Create(new Position(1));
        world.Destroy(position);

        var moving = world.Create(new Position(2), new Velocity(3));
        world.Destroy(moving);

        var living = world.Create(new Position(4), new Velocity(5), new Health(6));
        world.Destroy(living);
    }
}
