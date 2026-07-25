using MiniArch.Core;

namespace MiniArchTests.Core;

public sealed class EntityAccessorTests
{
    private readonly record struct Health(int Value);
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Flag(bool Active);

    [Fact]
    public void Access_reads_single_component()
    {
        var world = new World();
        var entity = world.Create(new Health(100));

        var acc = world.Access(entity);
        ref var health = ref acc.Get<Health>();

        Assert.Equal(100, health.Value);
    }

    [Fact]
    public void Access_reads_multiple_components_on_same_entity()
    {
        var world = new World();
        var entity = world.Create(new Health(100), new Position(3, 7), new Velocity(-1, 2));

        var acc = world.Access(entity);
        ref var health = ref acc.Get<Health>();
        ref var pos = ref acc.Get<Position>();
        ref var vel = ref acc.Get<Velocity>();

        Assert.Equal(100, health.Value);
        Assert.Equal(3, pos.X);
        Assert.Equal(7, pos.Y);
        Assert.Equal(-1, vel.X);
        Assert.Equal(2, vel.Y);
    }

    [Fact]
    public void Access_get_ref_allows_mutation()
    {
        var world = new World();
        var entity = world.Create(new Health(100));

        var acc = world.Access(entity);
        ref var health = ref acc.Get<Health>();
        health = new Health(42);

        Assert.Equal(42, world.Get<Health>(entity).Value);
    }

    [Fact]
    public void Access_set_writes_component()
    {
        var world = new World();
        var entity = world.Create(new Position(1, 2));

        var acc = world.Access(entity);
        acc.Set(new Position(9, 8));

        Assert.Equal(9, world.Get<Position>(entity).X);
        Assert.Equal(8, world.Get<Position>(entity).Y);
    }

    [Fact]
    public void Access_has_returns_true_for_existing_component()
    {
        var world = new World();
        var entity = world.Create(new Health(50));

        var acc = world.Access(entity);

        Assert.True(acc.Has<Health>());
        Assert.False(acc.Has<Position>());
    }

    [Fact]
    public void Access_throws_on_dead_entity()
    {
        var world = new World();
        var entity = world.Create(new Health(10));
        world.Destroy(entity);

        Assert.Throws<InvalidOperationException>(() => world.Access(entity));
    }

    [Fact]
    public void Access_throws_on_invalid_entity()
    {
        var world = new World();
        var entity = new Entity(9999, 1);

        Assert.Throws<InvalidOperationException>(() => world.Access(entity));
    }

    [Fact]
    public void Access_still_works_after_world_set()
    {
        var world = new World();
        var entity = world.Create(new Health(50));

        world.Set(entity, new Health(99));

        var acc = world.Access(entity);
        ref var health = ref acc.Get<Health>();

        Assert.Equal(99, health.Value);
    }

    [Fact]
    public void Access_mixed_get_and_set()
    {
        var world = new World();
        var entity = world.Create(new Health(100), new Position(1, 2));

        var acc = world.Access(entity);
        ref var h = ref acc.Get<Health>();
        h = new Health(200);
        acc.Set(new Position(5, 6));

        Assert.Equal(200, world.Get<Health>(entity).Value);
        Assert.Equal(5, world.Get<Position>(entity).X);
        Assert.Equal(6, world.Get<Position>(entity).Y);
    }
}
