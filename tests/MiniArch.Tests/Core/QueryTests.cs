using System.Threading;
using System.Threading.Tasks;
using MiniArch.Core;

namespace MiniArch.Tests.Core;

public sealed class QueryTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

    [Fact]
    public void Query_returns_only_matching_archetypes()
    {
        var world = new World();

        var first = world.Create();
        world.Add(first, new Position(1, 1));

        var second = world.Create();
        world.Add(second, new Velocity(2, 2));

        var third = world.Create();
        world.Add(third, new Position(3, 3));
        world.Add(third, new Velocity(4, 4));

        var query = world.Query<Position>();
        var matched = query.MatchedArchetypes;

        Assert.Equal(2, matched.Count);
        Assert.All(matched, archetype => Assert.Contains(new ComponentType(0), archetype.Signature));
    }

    [Fact]
    public void Repeated_queries_reuse_cached_matches_when_world_has_not_changed()
    {
        var world = new World();
        var entity = world.Create();
        world.Add(entity, new Position(1, 1));

        var first = world.Query<Position>();
        var second = world.Query<Position>();

        Assert.Same(first, second);
        _ = first.MatchedArchetypes;
        Assert.Equal(1, first.RefreshCount);

        _ = second.MatchedArchetypes;

        Assert.Equal(1, first.RefreshCount);
    }

    [Fact]
    public void Chunk_iteration_visits_each_matching_chunk_exactly_once()
    {
        var world = new World(chunkCapacity: 1);
        var first = world.Create();
        var second = world.Create();
        var third = world.Create();

        world.Add(first, new Position(1, 1));
        world.Add(second, new Position(2, 2));
        world.Add(third, new Position(3, 3));

        var query = world.Query<Position>();
        var chunks = new List<Chunk>();

        foreach (var chunk in query.Chunks)
        {
            chunks.Add(chunk);
        }

        Assert.Equal(3, chunks.Count);
        Assert.Equal(3, chunks.Distinct().Count());
    }

    [Fact]
    public void Query_exposes_the_same_matching_chunks_as_chunk_enumeration()
    {
        var world = new World(chunkCapacity: 1);
        for (var i = 0; i < 3; i++)
        {
            var entity = world.Create();
            world.Add(entity, new Position(i, i));
        }

        var query = world.Query<Position>();

        var enumeratedChunks = new List<Chunk>();
        foreach (var chunk in query.Chunks)
        {
            enumeratedChunks.Add(chunk);
        }

        Assert.Equal(enumeratedChunks, query.MatchedChunks);
        Assert.Equal(enumeratedChunks, query.GetChunkSpan().ToArray());
        Assert.Equal(1, query.RefreshCount);
    }

    [Fact]
    public void Matching_chunks_refresh_when_world_changes()
    {
        var world = new World(chunkCapacity: 1);
        var first = world.Create();
        world.Add(first, new Position(1, 1));

        var query = world.Query<Position>();
        Assert.Single(query.MatchedChunks);
        Assert.Equal(1, query.RefreshCount);

        var second = world.Create();
        world.Add(second, new Position(2, 2));

        Assert.Equal(2, query.MatchedChunks.Count);
        Assert.Equal(2, query.GetChunkSpan().Length);
        Assert.Equal(2, query.RefreshCount);
    }

    [Fact]
    public async Task Same_query_can_be_enumerated_concurrently_by_multiple_tasks()
    {
        var world = CreateWorldWithMatchingEntities();
        var query = world.Query<Position>();
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
        var materializedQuery = world.Query<Position>();
        var enumeratedQuery = world.Query().With<Position>().Build();

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
        var expected = CaptureEnumeratedEntities(CreateWorldWithMatchingEntities().Query<Position>());
        var world = CreateWorldWithMatchingEntities();
        var start = new Barrier(9);
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                start.SignalAndWait();
                return CaptureEnumeratedEntities(world.Query<Position>());
            }))
            .ToArray();

        start.SignalAndWait();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, actual => Assert.Equal(expected, actual));
        Assert.Equal(1, GetCachedQueryCount(world));
    }

    [Fact]
    public async Task Equivalent_description_queries_can_be_materialized_concurrently_before_the_cache_is_warmed()
    {
        var description = new QueryDescription().With<Position>();
        var expected = CaptureEnumeratedEntities(CreateWorldWithMatchingEntities().Query(in description));
        var world = CreateWorldWithMatchingEntities();
        var start = new Barrier(9);
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                start.SignalAndWait();
                return CaptureEnumeratedEntities(world.Query(in description));
            }))
            .ToArray();

        start.SignalAndWait();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, actual => Assert.Equal(expected, actual));
        Assert.Equal(1, GetCachedQueryCount(world));
    }

    [Fact]
    public void Description_query_entrypoint_matches_generic_and_chain_queries()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>();

        Assert.Same(world.Query<Position>(), world.Query(in description));
        Assert.Same(world.Query().With<Position>().Build(), world.Query(in description));
    }

    [Fact]
    public void Repeated_queries_with_same_description_reuse_cached_query()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>();

        Assert.Same(world.Query(in description), world.Query(in description));
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

        var firstQuery = firstWorld.Query(in description);
        var secondQuery = secondWorld.Query(in description);

        Assert.Same(firstQuery, firstWorld.Query(in description));
        Assert.Same(secondQuery, secondWorld.Query(in description));
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

        Assert.Equal(6, CountEntities(world.Query(in description)));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var total = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            total += CountEntities(world.Query(in description));
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
            var entity = world.Create();
            world.Add(entity, new Position(i, i));
        }

        for (var i = 0; i < 6; i++)
        {
            var entity = world.Create();
            world.Add(entity, new Position(i + 6, i + 6));
            world.Add(entity, new Velocity(i, i));
        }

        return world;
    }

    private static async Task<Entity[][]> RunConcurrentReaders(
        int taskCount,
        Query query,
        Func<Query, Entity[]> capture)
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

    private static Entity[] CaptureEnumeratedEntities(Query query)
    {
        var entities = new List<Entity>();
        foreach (var chunk in query.Chunks)
        {
            for (var row = 0; row < chunk.Count; row++)
            {
                entities.Add(chunk.GetEntity(row));
            }
        }

        return entities.ToArray();
    }

    private static Entity[] CaptureMaterializedEntities(Query query)
    {
        var entities = new List<Entity>();
        foreach (var archetype in query.MatchedArchetypes)
        {
            foreach (var chunk in archetype.Chunks)
            {
                for (var row = 0; row < chunk.Count; row++)
                {
                    entities.Add(chunk.GetEntity(row));
                }
            }
        }

        return entities.ToArray();
    }

    private static int CountEntities(Query query)
    {
        var total = 0;
        foreach (ref readonly var chunk in query.GetChunkSpan())
        {
            total += chunk.GetEntities().Length;
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
}
