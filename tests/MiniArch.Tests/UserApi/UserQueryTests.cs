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
    public void Query_can_order_entities_with_component_comparison()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>();
        var middle = world.Create(new Position(2, 0));
        var first = world.Create(new Position(1, 0));
        var last = world.Create(new Position(3, 0));

        var seen = new List<Entity>();
        foreach (var entity in world.Query(in description).OrderByComponent<Position>(
            (a, b) => a.X.CompareTo(b.X)))
        {
            seen.Add(entity);
        }

        Assert.Equal(new[] { first, middle, last }, seen);
    }

    [Fact]
    public void Query_can_order_entities_with_component_comparer()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>();
        var last = world.Create(new Position(3, 0));
        var first = world.Create(new Position(1, 0));
        var middle = world.Create(new Position(2, 0));

        var ordered = world.Query(in description).OrderByComponent<Position>(
            Comparer<Position>.Create((a, b) => a.X.CompareTo(b.X)));

        Assert.Equal(new[] { first, middle, last }, Capture(ordered));
    }

    [Fact]
    public async Task Ordered_component_query_can_be_enumerated_concurrently()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>();
        var expected = new List<Entity>();
        var expectedPairs = new List<(Entity Entity, int X)>();
        for (var i = 63; i >= 0; i--)
        {
            var e = world.Create(new Position(i, 0));
            expectedPairs.Add((e, i));
        }
        expectedPairs.Sort((a, b) => a.X.CompareTo(b.X));
        expected.AddRange(expectedPairs.Select(p => p.Entity));

        var ordered = world.Query(in description).OrderByComponent<Position>(
            (a, b) => a.X.CompareTo(b.X));

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

    [Fact]
    public void Ordered_component_query_in_comparison_does_not_corrupt_outer_sort()
    {
        // Regression: the ThreadStatic mutable IComparer<T> pattern let an
        // inner sort (triggered from the outer sort's comparison callback)
        // overwrite the shared comparer's Comparison field / Descending flag,
        // producing wrong order in the outer sort.
        var world = new World();
        var desc = new QueryDescription().With<Position>();
        // Entities with X = 0, 1, 2, 3, 4, 5 (creation order is reversed
        // so the sort is non-trivial).
        for (var i = 5; i >= 0; i--)
            world.Create(new Position(i, 0));

        var innerVerified = false;
        var outerEntities = new List<Entity>();

        foreach (var entity in world.Query(in desc).OrderByComponent<Position>(
            (a, b) =>
            {
                if (!innerVerified)
                {
                    innerVerified = true;
                    // Inner sort by X descending — must produce correct order
                    // (5,4,3,2,1,0) even though the outer sort's comparer is
                    // mid-execution.
                    var inner = new List<Entity>();
                    foreach (var e in world.Query(in desc).OrderByComponent<Position>(
                        (x, y) => y.X.CompareTo(x.X)))
                    {
                        inner.Add(e);
                    }

                    Assert.Equal(6, inner.Count);
                    for (var j = 1; j < inner.Count; j++)
                        Assert.True(
                            world.Get<Position>(inner[j - 1]).X >= world.Get<Position>(inner[j]).X,
                            $"Inner sort out of order at index {j - 1}/{j}");
                }

                return a.Y.CompareTo(b.Y);
            }))
        {
            outerEntities.Add(entity);
        }

        // Outer sort must not have been corrupted: all 6 entities are present.
        Assert.Equal(6, outerEntities.Count);

        // Also verify: inner sort result independently.
        var standalone = new List<Entity>();
        foreach (var e in world.Query(in desc).OrderByComponent<Position>(
            (x, y) => y.Y.CompareTo(x.Y)))
        {
            standalone.Add(e);
        }

        Assert.Equal(6, standalone.Count);
        Assert.Equal(standalone, outerEntities);
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

    private static Entity[] Capture(OrderedComponentQuery<Position> query)
    {
        var entities = new List<Entity>();
        foreach (var entity in query)
        {
            entities.Add(entity);
        }

        return entities.ToArray();
    }
}
