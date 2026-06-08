namespace MiniArchTests.Core;

public sealed class WorldStatsTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int Dx, int Dy);
    private readonly record struct Health(int Hp);

    [Fact]
    public void GetStats_empty_world()
    {
        using var world = new World();

        var stats = world.GetStats();

        Assert.Equal(0, stats.EntityCount);
        Assert.True(stats.EntityCapacity > 0);
        Assert.Equal(0, stats.RecycledEntityCount);
        // Empty world has no archetypes (empty signature entities go into an archetype on Create)
        Assert.Equal(0, stats.ArchetypeCount);
    }

    [Fact]
    public void GetStats_reflects_entities_and_archetypes()
    {
        using var world = new World();
        var e1 = world.Create(new Position(1, 2));
        var e2 = world.Create(new Position(1, 2), new Velocity(3, 4));
        var e3 = world.Create(new Health(100));

        var stats = world.GetStats();

        Assert.Equal(3, stats.EntityCount);
        Assert.Equal(3, stats.ArchetypeCount);
    }

    [Fact]
    public void GetStats_reflects_recycled_after_destroy()
    {
        using var world = new World();
        var e = world.Create(new Position(1, 2));
        world.Destroy(e);

        var stats = world.GetStats();

        Assert.Equal(0, stats.EntityCount);
        Assert.Equal(1, stats.RecycledEntityCount);
    }

    [Fact]
    public void GetArchetypeStats_returns_per_archetype_info()
    {
        using var world = new World();
        world.Create(new Position(1, 2));
        world.Create(new Position(1, 2));
        world.Create(new Position(1, 2), new Velocity(3, 4));

        var stats = world.GetArchetypeStats();

        Assert.Equal(2, stats.Length);

        var posOnly = stats.FirstOrDefault(s =>
            s.ComponentTypes.Count == 1 &&
            s.ComponentTypes[0] == typeof(Position));
        var posVel = stats.FirstOrDefault(s =>
            s.ComponentTypes.Count == 2);

        Assert.Equal(2, posOnly.EntityCount);
        Assert.True(posOnly.Capacity >= 2);
        Assert.Equal(1, posVel.EntityCount);
        Assert.True(posVel.Capacity >= 1);
        Assert.Contains(typeof(Velocity), posVel.ComponentTypes);
    }

    [Fact]
    public void GetArchetypeStats_capacity_grows_with_entities()
    {
        using var world = new World(chunkCapacity: 2);

        // Create 3 entities with same signature, chunk capacity is 2 so it must grow
        world.Create(new Position(1, 2));
        world.Create(new Position(1, 2));
        world.Create(new Position(1, 2));

        var stats = world.GetArchetypeStats();

        Assert.Single(stats);
        Assert.Equal(3, stats[0].EntityCount);
        Assert.True(stats[0].Capacity >= 3);
    }

    [Fact]
    public void GetStats_snapshot_is_consistent_with_archetype_stats()
    {
        using var world = new World();
        world.Create(new Position(1, 2));
        world.Create(new Position(1, 2), new Velocity(3, 4));
        world.Destroy(world.Create(new Health(100)));

        var globalStats = world.GetStats();
        var archetypeStats = world.GetArchetypeStats();

        var totalEntities = archetypeStats.Sum(s => s.EntityCount);
        Assert.Equal(globalStats.EntityCount, totalEntities);
    }
}
