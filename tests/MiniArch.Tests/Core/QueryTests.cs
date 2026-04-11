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
}
