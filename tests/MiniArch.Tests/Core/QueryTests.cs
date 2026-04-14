using System.Threading;
using System.Threading.Tasks;
using MiniArch.Core;
using MiniQuery = MiniArch.Core.Query;

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

        var first = world.Create();
        world.Add(first, new Position(1, 1));

        var second = world.Create();
        world.Add(second, new Velocity(2, 2));

        var third = world.Create();
        world.Add(third, new Position(3, 3));
        world.Add(third, new Velocity(4, 4));

        var query = MiniQuery.Create(world, in description);
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
        var description = new QueryDescription().With<Position>();

        var first = MiniQuery.Create(world, in description);
        var second = MiniQuery.Create(world, in description);

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
        var description = new QueryDescription().With<Position>();
        var first = world.Create();
        var second = world.Create();
        var third = world.Create();

        world.Add(first, new Position(1, 1));
        world.Add(second, new Position(2, 2));
        world.Add(third, new Position(3, 3));

        var query = MiniQuery.Create(world, in description);
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
        var description = new QueryDescription().With<Position>();
        for (var i = 0; i < 3; i++)
        {
            var entity = world.Create();
            world.Add(entity, new Position(i, i));
        }

        var query = MiniQuery.Create(world, in description);

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
        var description = new QueryDescription().With<Position>();

        var query = MiniQuery.Create(world, in description);
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
                return CaptureEnumeratedEntities(MiniQuery.Create(world, in description));
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
        var expected = CaptureEnumeratedEntities(MiniQuery.Create(CreateWorldWithMatchingEntities(), in description));
        var world = CreateWorldWithMatchingEntities();
        var start = new Barrier(9);
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                start.SignalAndWait();
                return CaptureEnumeratedEntities(MiniQuery.Create(world, in description));
            }))
            .ToArray();

        start.SignalAndWait();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, actual => Assert.Equal(expected, actual));
        Assert.Equal(1, GetCachedQueryCount(world));
    }

    [Fact]
    public void Description_query_entrypoint_matches_advanced_query_factory()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>();

        Assert.Same(MiniQuery.Create(world, in description), MiniQuery.Create(world, in description));
    }

    [Fact]
    public void Repeated_queries_with_same_description_reuse_cached_query()
    {
        var world = new World();
        var description = new QueryDescription().With<Position>();

        Assert.Same(MiniQuery.Create(world, in description), MiniQuery.Create(world, in description));
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

        var firstQuery = MiniQuery.Create(firstWorld, in description);
        var secondQuery = MiniQuery.Create(secondWorld, in description);

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

        Assert.Equal(6, CountEntities(MiniQuery.Create(world, in description)));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var total = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            total += CountEntities(MiniQuery.Create(world, in description));
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(600, total);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Query_results_restore_after_multi_frame_rewind_with_cascade_destroy()
    {
        var world = new World();
        var root = world.Create();
        var child = world.Create();
        var grandChild = world.Create();
        var sibling = world.Create();
        world.Add(root, new Position(1, 1));
        world.Add(child, new Position(2, 2));
        world.Add(grandChild, new Position(3, 3));
        world.Add(sibling, new Position(4, 4));
        world.Link(root, child);
        world.Link(child, grandChild);

        var initialQuery = CaptureEnumeratedEntities(CreatePositionQuery(world));

        var frame1 = new CommandBuffer(world);
        var branch = frame1.Create();
        frame1.Add(branch, new Position(5, 5));
        frame1.Link(child, branch);
        var compiledFrame1 = frame1.Playback();
        var reverse1 = world.ReplayWithReverse(in compiledFrame1);

        var frame1Query = CaptureEnumeratedEntities(CreatePositionQuery(world));
        Assert.Equal(new[] { root, child, grandChild, sibling, branch }, frame1Query.OrderBy(static entity => entity.Id).ToArray());

        var frame2 = new CommandBuffer(world);
        var transient = frame2.Create();
        frame2.Add(transient, new Position(6, 6));
        frame2.Link(root, transient);
        frame2.Destroy(root);
        var compiledFrame2 = frame2.Playback();
        var reverse2 = world.ReplayWithReverse(in compiledFrame2);

        Assert.Equal(new[] { sibling }, CaptureEnumeratedEntities(CreatePositionQuery(world)));

        world.Rewind(in reverse2);

        Assert.Equal(frame1Query.OrderBy(static entity => entity.Id).ToArray(), CaptureEnumeratedEntities(CreatePositionQuery(world)).OrderBy(static entity => entity.Id).ToArray());

        world.Rewind(in reverse1);

        Assert.Equal(initialQuery.OrderBy(static entity => entity.Id).ToArray(), CaptureEnumeratedEntities(CreatePositionQuery(world)).OrderBy(static entity => entity.Id).ToArray());
        Assert.False(world.IsAlive(branch));
        Assert.False(world.IsAlive(transient));
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

    private static MiniQuery CreatePositionQuery(World world)
    {
        var description = new QueryDescription().With<Position>();
        return MiniQuery.Create(world, in description);
    }

    private static async Task<Entity[][]> RunConcurrentReaders(
        int taskCount,
        MiniQuery query,
        Func<MiniQuery, Entity[]> capture)
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

    private static Entity[] CaptureEnumeratedEntities(MiniQuery query)
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

    private static Entity[] CaptureMaterializedEntities(MiniQuery query)
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

    private static int CountEntities(MiniQuery query)
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

    [Fact]
    public void World_has_unified_query_generation_and_warmed_query_refreshes_only_when_it_advances()
    {
        var world = new World();
        var generationField = typeof(World).GetField("_queryGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(generationField);
        Assert.Null(typeof(World).GetField("_archetypeGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        Assert.Null(typeof(World).GetField("_queryLayoutGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));

        var description = new QueryDescription().With<Velocity>();
        var query = MiniQuery.Create(world, in description);

        Assert.Empty(query.MatchedArchetypes);
        Assert.Equal(1, query.RefreshCount);

        var warmedGeneration = (int)generationField.GetValue(world)!;

        _ = query.MatchedArchetypes;

        Assert.Equal(warmedGeneration, (int)generationField.GetValue(world)!);
        Assert.Equal(1, query.RefreshCount);

        _ = world.Create(new Velocity(2, 2));

        var advancedGeneration = (int)generationField.GetValue(world)!;
        Assert.True(advancedGeneration > warmedGeneration);

        var matched = query.MatchedArchetypes;

        Assert.Single(matched);
        Assert.Equal(2, query.RefreshCount);
    }
}
