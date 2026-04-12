using MiniArch.Ecs;

namespace MiniArch.Tests.UserApi;

public sealed class UserQueryTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

    [Fact]
    public void Single_component_query_can_be_enumerated_directly_with_foreach()
    {
        var world = new World();
        var first = world.Create(new Position(1, 2));
        world.Create();
        var second = world.Create(new Position(3, 4));

        var seen = new List<(Entity Entity, Position Position)>();
        foreach (var item in world.Query<Position>())
        {
            seen.Add((item.Entity, item.Component));
        }

        Assert.Equal(
            new[]
            {
                (first, new Position(1, 2)),
                (second, new Position(3, 4)),
            },
            seen);
    }

    [Fact]
    public void Dual_component_query_can_be_enumerated_directly_with_foreach()
    {
        var world = new World();
        var first = world.Create(new Position(1, 2), new Velocity(3, 4));
        world.Create(new Position(7, 8));
        var second = world.Create(new Position(5, 6), new Velocity(7, 8));

        var seen = new List<(Entity Entity, Position Position, Velocity Velocity)>();
        foreach (var item in world.Query<Position, Velocity>())
        {
            seen.Add((item.Entity, item.First, item.Second));
        }

        Assert.Equal(
            new[]
            {
                (first, new Position(1, 2), new Velocity(3, 4)),
                (second, new Position(5, 6), new Velocity(7, 8)),
            },
            seen);
    }

    [Fact]
    public void TryGet_reads_existing_component_without_has_check()
    {
        var world = new World();
        var entity = world.Create(new Position(9, 10));

        var found = world.TryGet(entity, out Position position);

        Assert.True(found);
        Assert.Equal(new Position(9, 10), position);
    }

    [Fact]
    public void IsAlive_forwards_lifecycle_state_through_the_user_facing_world()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));

        Assert.True(world.IsAlive(entity));

        world.Destroy(entity);

        Assert.False(world.IsAlive(entity));

        var recycled = world.Create(new Position(3, 4));

        Assert.True(world.IsAlive(recycled));
        Assert.False(world.IsAlive(entity));
    }

    [Fact]
    public void Warming_query_then_repeating_foreach_does_not_allocate()
    {
        var world = new World();
        for (var i = 0; i < 128; i++)
        {
            world.Create(new Position(i, i + 1), new Velocity(i + 2, i + 3));
        }

        var query = world.Query<Position, Velocity>();
        var warmupChecksum = Sum(query);
        Assert.NotEqual(0, warmupChecksum);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var checksum = 0;
        for (var iteration = 0; iteration < 200; iteration++)
        {
            checksum += Sum(query);
        }

        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.NotEqual(0, checksum);
        Assert.Equal(0, after - before);
    }

    private static int Sum(Query<Position, Velocity> query)
    {
        var total = 0;
        foreach (var item in query)
        {
            total += item.Entity.Id;
            total += item.First.X;
            total += item.Second.Y;
        }

        return total;
    }
}
