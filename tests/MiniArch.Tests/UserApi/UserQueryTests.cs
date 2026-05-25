using MiniArch;
using System.Threading;
using System.Threading.Tasks;

namespace MiniArchTests.UserApi;

public sealed class UserQueryTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

    [Fact]
    public void Description_based_single_component_query_can_be_enumerated_directly_with_foreach()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>();
        var first = world.Create(new Position(1, 2));
        world.Create();
        var second = world.Create(new Position(3, 4));

        var seen = new List<(Entity Entity, Position Position)>();
        foreach (var entity in world.Query(in description))
        {
            Assert.True(world.TryGet(entity, out Position position));
            seen.Add((entity, position));
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
    public void Description_based_dual_component_query_can_be_enumerated_directly_with_foreach()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>().With<Velocity>();
        var first = world.Create(new Position(1, 2), new Velocity(3, 4));
        world.Create(new Position(7, 8));
        var second = world.Create(new Position(5, 6), new Velocity(7, 8));

        var seen = new List<(Entity Entity, Position Position, Velocity Velocity)>();
        foreach (var entity in world.Query(in description))
        {
            Assert.True(world.TryGet(entity, out Position position));
            Assert.True(world.TryGet(entity, out Velocity velocity));
            seen.Add((entity, position, velocity));
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
    public void Description_based_query_can_be_enumerated_directly_with_foreach()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>().Without<Velocity>();
        var first = world.Create(new Position(11, 12));
        world.Create(new Position(13, 14), new Velocity(15, 16));

        var seen = new List<Entity>();
        foreach (var entity in world.Query(in description))
        {
            seen.Add(entity);
        }

        Assert.Equal(new[] { first }, seen);
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
        var description = new QueryDescription().With<Position>().With<Velocity>();
        for (var i = 0; i < 128; i++)
        {
            world.Create(new Position(i, i + 1), new Velocity(i + 2, i + 3));
        }

        var query = world.Query(in description);
        var warmupChecksum = Sum(world, query);
        Assert.NotEqual(0, warmupChecksum);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var checksum = 0;
        for (var iteration = 0; iteration < 200; iteration++)
        {
            checksum += Sum(world, query);
        }

        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.NotEqual(0, checksum);
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Query_can_order_entities_with_comparison()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>();
        var middle = world.Create(new Position(2, 0));
        var first = world.Create(new Position(1, 0));
        var last = world.Create(new Position(3, 0));

        var seen = new List<Entity>();
        foreach (var entity in world.Query(in description).OrderBy((left, right) =>
        {
            Assert.True(world.TryGet(left, out Position leftPosition));
            Assert.True(world.TryGet(right, out Position rightPosition));
            return leftPosition.X.CompareTo(rightPosition.X);
        }))
        {
            seen.Add(entity);
        }

        Assert.Equal(new[] { first, middle, last }, seen);
    }

    [Fact]
    public void Query_can_order_entities_with_comparer()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>();
        var last = world.Create(new Position(3, 0));
        var first = world.Create(new Position(1, 0));
        var middle = world.Create(new Position(2, 0));

        var ordered = world.Query(in description).OrderBy(Comparer<Entity>.Create((left, right) =>
        {
            Assert.True(world.TryGet(left, out Position leftPosition));
            Assert.True(world.TryGet(right, out Position rightPosition));
            return leftPosition.X.CompareTo(rightPosition.X);
        }));

        Assert.Equal(new[] { first, middle, last }, Capture(ordered));
    }

    [Fact]
    public async Task Ordered_query_can_be_enumerated_concurrently()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>();
        var expected = new List<Entity>();
        for (var i = 63; i >= 0; i--)
        {
            expected.Add(world.Create(new Position(i, 0)));
        }

        expected.Sort((left, right) =>
        {
            Assert.True(world.TryGet(left, out Position leftPosition));
            Assert.True(world.TryGet(right, out Position rightPosition));
            return leftPosition.X.CompareTo(rightPosition.X);
        });

        var ordered = world.Query(in description).OrderBy((left, right) =>
        {
            Assert.True(world.TryGet(left, out Position leftPosition));
            Assert.True(world.TryGet(right, out Position rightPosition));
            return leftPosition.X.CompareTo(rightPosition.X);
        });

        var start = new Barrier(9);
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                start.SignalAndWait();
                return Capture(ordered);
            }))
            .ToArray();

        start.SignalAndWait();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, actual => Assert.Equal(expected, actual));
    }

    private static int Sum(World world, Query query)
    {
        var total = 0;
        foreach (var entity in query)
        {
            total += entity.Id;
            if (world.TryGet(entity, out Position position))
            {
                total += position.X;
            }

            if (world.TryGet(entity, out Velocity velocity))
            {
                total += velocity.Y;
            }
        }

        return total;
    }

    private static Entity[] Capture(OrderedQuery query)
    {
        var entities = new List<Entity>();
        foreach (var entity in query)
        {
            entities.Add(entity);
        }

        return entities.ToArray();
    }
}
