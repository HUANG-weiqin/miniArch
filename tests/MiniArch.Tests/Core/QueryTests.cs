using System.Threading;
using System.Threading.Tasks;
using MiniArch.Core;
using MiniQueryCache = MiniArch.Core.QueryCache;

namespace MiniArchTests.Core;

public sealed class QueryTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

    [Fact]
    public void Query_returns_only_matching_archetypes()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>();

        var first = world.CreateEmpty();
        world.Add(first, new Position(1, 1));

        var second = world.CreateEmpty();
        world.Add(second, new Velocity(2, 2));

        var third = world.CreateEmpty();
        world.Add(third, new Position(3, 3));
        world.Add(third, new Velocity(4, 4));

        var query = MiniQueryCache.Create(world, in description);
        var matched = query.MatchedArchetypes;

        Assert.Equal(2, matched.Count);
        var positionId = ComponentRegistry.Shared.GetOrCreate<Position>();
        Assert.All(matched, archetype => Assert.Contains(positionId, archetype.Signature));
    }

    [Fact]
    public void Repeated_queries_reuse_cached_matches_when_world_has_not_changed()
    {
        var world = new World();
        var entity = world.CreateEmpty();
        world.Add(entity, new Position(1, 1));
        var description = new QueryDescription().With<Position>();

        var first = MiniQueryCache.Create(world, in description);
        var second = MiniQueryCache.Create(world, in description);

        Assert.Same(first, second);
        _ = first.MatchedArchetypes;
        Assert.Equal(1, first.RefreshCount);

        _ = second.MatchedArchetypes;

        Assert.Equal(1, first.RefreshCount);
    }

    [Fact]
    public void Archetype_iteration_visits_each_matching_archetype_exactly_once()
    {
        var world = new World(chunkCapacity: 1);
        var description = new QueryDescription().With<Position>();
        var first = world.CreateEmpty();
        var second = world.CreateEmpty();
        var third = world.CreateEmpty();

        world.Add(first, new Position(1, 1));
        world.Add(second, new Position(2, 2));
        world.Add(third, new Position(3, 3));

        var query = MiniQueryCache.Create(world, in description);
        var archetypes = new List<Archetype>();

        foreach (var archetype in query.MatchedArchetypes)
        {
            archetypes.Add(archetype);
        }

        Assert.Single(archetypes);
        Assert.Single(archetypes.Distinct());
    }

    [Fact]
    public void Query_exposes_the_same_matching_archetypes_as_chunk_enumeration()
    {
        var world = new World(chunkCapacity: 1);
        var description = new QueryDescription().With<Position>();
        for (var i = 0; i < 3; i++)
        {
            var entity = world.CreateEmpty();
            world.Add(entity, new Position(i, i));
        }

        var query = MiniQueryCache.Create(world, in description);

        var enumeratedArchetypes = new List<Archetype>();
        foreach (var archetype in query.MatchedArchetypes)
        {
            enumeratedArchetypes.Add(archetype);
        }

        Assert.Equal(enumeratedArchetypes, query.MatchedArchetypes);
        Assert.Equal(enumeratedArchetypes, query.GetArchetypeSpan().ToArray());
        Assert.Equal(1, query.RefreshCount);
    }

    [Fact]
    public void Query_exposes_the_same_matching_archetypes_as_span()
    {
        var world = new World(chunkCapacity: 1);
        var description = new QueryDescription().With<Position>();

        var first = world.Create(new Position(1, 1));
        world.Add(first, new Velocity(1, 1));
        _ = world.Create(new Position(2, 2));
        _ = world.Create(new Velocity(3, 3));

        var query = MiniQueryCache.Create(world, in description);
        var matched = query.MatchedArchetypes.ToArray();

        Assert.Equal(matched, query.GetArchetypeSpan().ToArray());
        Assert.Equal(1, query.RefreshCount);
    }

    [Fact]
    public void Archetype_is_the_single_storage_block()
    {
        var world = new World(chunkCapacity: 1);
        _ = world.Create(new Position(1, 1));
        _ = world.Create(new Position(2, 2));
        var description = new QueryDescription().With<Position>();

        var query = MiniQueryCache.Create(world, in description);
        var archetype = Assert.Single(query.GetArchetypeSpan().ToArray());

        Assert.Equal(2, archetype.EntityCount);
    }

    [Fact]
    public void BUG_order_by_component_supports_chunked_archetypes()
    {
        var world = new World();
        var first = world.Create(new Position(2, 0));
        var second = world.Create(new Position(1, 0));
        var description = new QueryDescription().With<Position>();
        var query = MiniQueryCache.Create(world, in description);
        var arch = Assert.Single(query.MatchedArchetypes);
        arch.ForceChunkedForTesting();
        Assert.True(arch.IsChunked);

        var sorted = new List<Entity>();
        foreach (var entity in world.Query(in description)
                     .OrderByComponent<Position>((left, right) => left.X.CompareTo(right.X)))
        {
            sorted.Add(entity);
        }

        Assert.Equal(new[] { second, first }, sorted);
    }

    [Fact]
    public void BUG_query_chunks_refresh_when_archetype_promotes_to_single_chunk_segment()
    {
        var world = new World();
        _ = world.Create(new Position(1, 0));
        _ = world.Create(new Position(2, 0));
        var description = new QueryDescription().With<Position>();
        var publicQuery = world.Query(in description);
        var coreQuery = MiniQueryCache.Create(world, in description);

        var initialChunks = publicQuery.GetChunks();
        Assert.Equal(1, initialChunks.Length);

        var arch = Assert.Single(coreQuery.MatchedArchetypes);
        arch.ForceChunkedForTesting();
        Assert.True(arch.IsChunked);

        var refreshedChunks = publicQuery.GetChunks();
        Assert.Equal(1, refreshedChunks.Length);
        var positions = refreshedChunks[0].GetSpan<Position>();

        Assert.Equal(new[] { new Position(1, 0), new Position(2, 0) }, positions.ToArray());
    }

    [Fact]
    public void BUG_query_chunks_refresh_existing_view_shape_when_archetype_count_also_changes()
    {
        var world = new World();
        _ = world.Create(new Position(1, 0));
        _ = world.Create(new Position(2, 0));
        var description = new QueryDescription().With<Position>();
        var publicQuery = world.Query(in description);
        var coreQuery = MiniQueryCache.Create(world, in description);
        var archetype = Assert.Single(coreQuery.MatchedArchetypes);

        Assert.Equal(1, publicQuery.GetChunks().Length);

        _ = world.Create(new Velocity(1, 0));
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        var refreshedChunks = publicQuery.GetChunks();
        Assert.Equal(1, refreshedChunks.Length);
        var positions = refreshedChunks[0].GetSpan<Position>();

        Assert.Equal(new[] { new Position(1, 0), new Position(2, 0) }, positions.ToArray());
    }

    [Fact]
    public void BUG_query_chunks_refresh_segment_growth_when_archetype_count_also_changes()
    {
        var world = new World();
        _ = world.Create(new Position(1, 0));
        var description = new QueryDescription().With<Position>();
        var publicQuery = world.Query(in description);
        var coreQuery = MiniQueryCache.Create(world, in description);
        var archetype = Assert.Single(coreQuery.MatchedArchetypes);
        archetype.ForceChunkedForTesting();
        Assert.True(archetype.IsChunked);

        Assert.Equal(1, publicQuery.GetChunks().Length);

        _ = world.Create(new Velocity(1, 0));
        archetype.AddSegmentForTesting();

        var refreshedChunks = publicQuery.GetChunks();

        Assert.Equal(2, refreshedChunks.Length);
    }

    [Fact]
    public void Matching_archetypes_refresh_when_world_changes()
    {
        var world = new World(chunkCapacity: 1);
        var first = world.CreateEmpty();
        world.Add(first, new Position(1, 1));
        var description = new QueryDescription().With<Position>();

        var query = MiniQueryCache.Create(world, in description);
        Assert.Single(query.MatchedArchetypes);
        Assert.Equal(1, query.RefreshCount);

        var second = world.CreateEmpty();
        world.Add(second, new Position(2, 2));

        Assert.Single(query.MatchedArchetypes);
        Assert.Single(query.GetArchetypeSpan().ToArray());
        Assert.True(query.RefreshCount >= 1, "Chunk snapshot should be valid");
    }

    [Fact]
    public async Task Same_query_can_be_enumerated_concurrently_by_multiple_tasks()
    {
        var world = CreateWorldWithMatchingEntities();
        var query = CreatePositionQuery(world);
        var expected = CaptureEnumeratedEntities(query);

        var results = await RunConcurrentReaders(
            taskCount: 8,
            query,
            CaptureEnumeratedEntities);

        Assert.Equal(1, query.RefreshCount);
        Assert.All(results, actual => Assert.Equal(expected, actual));
    }

    [Fact]
    public async Task Materialized_and_enumerated_equivalent_queries_return_the_same_entities_concurrently()
    {
        var world = CreateWorldWithMatchingEntities();
        var materializedQuery = CreatePositionQuery(world);
        var enumeratedQuery = CreatePositionQuery(world);

        Assert.Same(materializedQuery, enumeratedQuery);

        var expected = CaptureMaterializedEntities(materializedQuery);
        var start = new Barrier(9);
        var tasks = Enumerable.Range(0, 8)
            .Select(index => Task.Run(() =>
            {
                start.SignalAndWait();
                return index % 2 == 0
                    ? CaptureMaterializedEntities(materializedQuery)
                    : CaptureEnumeratedEntities(enumeratedQuery);
            }))
            .ToArray();

        start.SignalAndWait();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, materializedQuery.RefreshCount);
        Assert.All(results, actual => Assert.Equal(expected, actual));
    }

    [Fact]
    public async Task Equivalent_queries_can_be_materialized_concurrently_before_the_cache_is_warmed()
    {
        var expected = CaptureEnumeratedEntities(CreatePositionQuery(CreateWorldWithMatchingEntities()));
        var world = CreateWorldWithMatchingEntities();
        var description = new QueryDescription().With<Position>();
        var start = new Barrier(9);
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                start.SignalAndWait();
                return CaptureEnumeratedEntities(MiniQueryCache.Create(world, in description));
            }))
            .ToArray();

        start.SignalAndWait();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, actual => Assert.Equal(expected, actual));
        Assert.Equal(1, GetCachedQueryCount(world));
    }

    [Fact]
    public void Repeated_queries_with_same_description_reuse_cached_query()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>();

        Assert.Same(MiniQueryCache.Create(world, in description), MiniQueryCache.Create(world, in description));
    }

    [Fact]
    public void Same_query_description_can_be_reused_across_worlds()
    {
        var description = new QueryDescription()
            .With<Position>()
            .Without<Velocity>();

        var firstWorld = new World();
        var firstMatched = firstWorld.Create(new Position(1, 1));
        _ = firstWorld.Create(new Position(2, 2), new Velocity(3, 3));

        var secondWorld = new World();
        var secondMatched = secondWorld.Create(new Position(4, 4));
        _ = secondWorld.Create(new Position(5, 5), new Velocity(6, 6));

        var firstQuery = MiniQueryCache.Create(firstWorld, in description);
        var secondQuery = MiniQueryCache.Create(secondWorld, in description);

        Assert.NotSame(firstQuery, secondQuery);
        Assert.Equal(new[] { firstMatched }, CaptureEnumeratedEntities(firstQuery));
        Assert.Equal(new[] { secondMatched }, CaptureEnumeratedEntities(secondQuery));
    }

    [Fact]
    public void Warmed_description_query_does_not_allocate_on_steady_state_path()
    {
        var world = CreateWorldWithMatchingEntities();
        var description = new QueryDescription()
            .With<Position>()
            .Without<Velocity>();

        var warmTotal = 0;
        for (var w = 0; w < 10; w++)
        {
            warmTotal += CountEntities(MiniQueryCache.Create(world, in description));
        }

        Assert.Equal(60, warmTotal);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var total = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            total += CountEntities(MiniQueryCache.Create(world, in description));
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(600, total);
        Assert.Equal(0, allocated);
    }

    private static World CreateWorldWithMatchingEntities()
    {
        var world = new World(chunkCapacity: 4);

        for (var i = 0; i < 6; i++)
        {
            var entity = world.CreateEmpty();
            world.Add(entity, new Position(i, i));
        }

        for (var i = 0; i < 6; i++)
        {
            var entity = world.CreateEmpty();
            world.Add(entity, new Position(i + 6, i + 6));
            world.Add(entity, new Velocity(i, i));
        }

        return world;
    }

    private static MiniQueryCache CreatePositionQuery(World world)
    {
        var description = new QueryDescription().With<Position>();
        return MiniQueryCache.Create(world, in description);
    }

    private static async Task<Entity[][]> RunConcurrentReaders(
        int taskCount,
        MiniQueryCache query,
        Func<MiniQueryCache, Entity[]> capture)
    {
        var start = new Barrier(taskCount + 1);
        var tasks = Enumerable.Range(0, taskCount)
            .Select(_ => Task.Run(() =>
            {
                start.SignalAndWait();
                return capture(query);
            }))
            .ToArray();

        start.SignalAndWait();
        return await Task.WhenAll(tasks);
    }

    private static Entity[] CaptureEnumeratedEntities(MiniQueryCache query)
    {
        var entities = new List<Entity>();
        foreach (var archetype in query.MatchedArchetypes)
        {
            for (var row = 0; row < archetype.EntityCount; row++)
            {
                entities.Add(archetype.GetEntity(row));
            }
        }

        return entities.ToArray();
    }

    private static Entity[] CaptureMaterializedEntities(MiniQueryCache query)
    {
        var entities = new List<Entity>();
        foreach (var archetype in query.MatchedArchetypes)
        {
            for (var row = 0; row < archetype.EntityCount; row++)
            {
                entities.Add(archetype.GetEntity(row));
            }
        }

        return entities.ToArray();
    }

    private static int CountEntities(MiniQueryCache query)
    {
        var total = 0;
        foreach (ref readonly var archetype in query.GetArchetypeSpan())
        {
            total += archetype.GetEntities().Length;
        }

        return total;
    }

    private static int GetCachedQueryCount(World world)
    {
        var field = typeof(World).GetField("_queries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var value = field.GetValue(world);
        var dictionary = Assert.IsAssignableFrom<System.Collections.IDictionary>(value);
        return dictionary.Count;
    }

    [Fact]
    public void Warmed_query_refreshes_only_when_matched_archetype_generation_changes()
    {
        var world = new World();

        var description = new QueryDescription().With<Velocity>();
        var query = MiniQueryCache.Create(world, in description);

        Assert.Empty(query.MatchedArchetypes);
        Assert.Equal(1, query.RefreshCount);

        _ = query.MatchedArchetypes;

        Assert.Equal(1, query.RefreshCount);

        _ = world.Create(new Velocity(2, 2));

        var matched = query.MatchedArchetypes;

        Assert.Single(matched);
        Assert.Equal(2, query.RefreshCount);
    }

    [Fact]
    public void Component_row_wise_and_span_checksums_are_equivalent()
    {
        var world = new World(chunkCapacity: 4);
        for (var i = 0; i < 6; i++)
        {
            var entity = world.CreateEmpty();
            world.Add(entity, new Position(i, i + 1));
            world.Add(entity, new Velocity(i + 2, i + 3));
        }

        var description = new QueryDescription().With<Position>().With<Velocity>();
        var query = MiniQueryCache.Create(world, in description);
        var positionType = ComponentRegistry.Shared.GetOrCreate<Position>();
        var velocityType = ComponentRegistry.Shared.GetOrCreate<Velocity>();

        var rowWiseChecksum = ComputeRowWiseChecksum(query, positionType, velocityType);
        var spanChecksum = ComputeSpanChecksum(query, positionType, velocityType);

        Assert.Equal(rowWiseChecksum, spanChecksum);
    }

    [Fact]
    public void Component_row_wise_checksum_is_stable_across_iterations()
    {
        var world = new World(chunkCapacity: 4);
        for (var i = 0; i < 6; i++)
        {
            var entity = world.CreateEmpty();
            world.Add(entity, new Position(i, i + 1));
            world.Add(entity, new Velocity(i + 2, i + 3));
        }

        var description = new QueryDescription().With<Position>().With<Velocity>();
        var query = MiniQueryCache.Create(world, in description);
        var positionType = ComponentRegistry.Shared.GetOrCreate<Position>();
        var velocityType = ComponentRegistry.Shared.GetOrCreate<Velocity>();

        var firstRun = ComputeRowWiseChecksum(query, positionType, velocityType);
        var secondRun = ComputeRowWiseChecksum(query, positionType, velocityType);

        Assert.Equal(firstRun, secondRun);
    }

    [Fact]
    public void Component_span_checksum_is_stable_across_iterations()
    {
        var world = new World(chunkCapacity: 4);
        for (var i = 0; i < 6; i++)
        {
            var entity = world.CreateEmpty();
            world.Add(entity, new Position(i, i + 1));
            world.Add(entity, new Velocity(i + 2, i + 3));
        }

        var description = new QueryDescription().With<Position>().With<Velocity>();
        var query = MiniQueryCache.Create(world, in description);
        var positionType = ComponentRegistry.Shared.GetOrCreate<Position>();
        var velocityType = ComponentRegistry.Shared.GetOrCreate<Velocity>();

        var firstRun = ComputeSpanChecksum(query, positionType, velocityType);
        var secondRun = ComputeSpanChecksum(query, positionType, velocityType);

        Assert.Equal(firstRun, secondRun);
    }

    private static int ComputeRowWiseChecksum(MiniQueryCache query, ComponentType positionType, ComponentType velocityType)
    {
        var checksum = 0;
        foreach (ref readonly var archetype in query.GetArchetypeSpan())
        {
            var posColIdx = archetype.GetComponentIndex(positionType);
            var velColIdx = archetype.GetComponentIndex(velocityType);
            for (var row = 0; row < archetype.EntityCount; row++)
            {
                var position = archetype.GetComponentAt<Position>(posColIdx, row);
                var velocity = archetype.GetComponentAt<Velocity>(velColIdx, row);
                checksum += position.X + velocity.Y;
            }
        }

        return checksum;
    }

    private static int ComputeSpanChecksum(MiniQueryCache query, ComponentType positionType, ComponentType velocityType)
    {
        var checksum = 0;
        foreach (ref readonly var archetype in query.GetArchetypeSpan())
        {
            var positions = archetype.GetFlatComponentSpan<Position>(positionType);
            var velocities = archetype.GetFlatComponentSpan<Velocity>(velocityType);
            for (var row = 0; row < positions.Length; row++)
            {
                checksum += positions[row].X + velocities[row].Y;
            }
        }

        return checksum;
    }

    [Fact]
    public void QueryDescription_RequiredTypes_is_defensive_snapshot()
    {
        var desc = new QueryDescription().With<Position>().Without<Velocity>().WithAny<Position>();

        // Required: mutate via cast
        var required = (Type[])desc.RequiredTypes;
        required[0] = typeof(string);

        // Excluded: mutate via cast
        var excluded = (Type[])desc.ExcludedTypes;
        excluded[0] = typeof(int);

        // Any: mutate via cast
        var any = (Type[])desc.AnyTypes;
        any[0] = typeof(double);

        // Re-read: must still contain original types
        Assert.Equal(typeof(Position), desc.RequiredTypes[0]);
        Assert.Equal(typeof(Velocity), desc.ExcludedTypes[0]);
        Assert.Equal(typeof(Position), desc.AnyTypes[0]);
    }
}
