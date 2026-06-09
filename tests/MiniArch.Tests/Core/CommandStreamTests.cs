using MiniArch.Core;

namespace MiniArchTests.Core;

public sealed class CommandStreamTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    [Fact]
    public void Submit_applies_created_entity_components_and_preserves_reserved_handle()
    {
        var world = new World();
        var stream = new CommandStream(world);

        var entity = stream.Create();
        stream.Add(entity, new Position(1, 2));
        stream.Add(entity, new Velocity(3, 4));

        Assert.False(world.IsAlive(entity));
        Assert.True(stream.Submit());

        Assert.True(world.IsAlive(entity));
        Assert.True(world.TryGet(entity, out Position position));
        Assert.True(world.TryGet(entity, out Velocity velocity));
        Assert.Equal(new Position(1, 2), position);
        Assert.Equal(new Velocity(3, 4), velocity);
    }

    [Fact]
    public void Submit_applies_existing_entity_commands_and_allows_reuse()
    {
        var world = new World();
        var entity = world.Create(new Position(0, 0), new Velocity(5, 6));
        var stream = new CommandStream(world);

        stream.Set(entity, new Position(7, 8));
        stream.Remove<Velocity>(entity);
        stream.Add(entity, new Health(9));
        Assert.True(stream.Submit());

        Assert.True(world.TryGet(entity, out Position position));
        Assert.Equal(new Position(7, 8), position);
        Assert.False(world.TryGet<Velocity>(entity, out _));
        Assert.True(world.TryGet(entity, out Health health));
        Assert.Equal(new Health(9), health);

        stream.Set(entity, new Health(10));
        Assert.True(stream.Submit());
        Assert.True(world.TryGet(entity, out health));
        Assert.Equal(new Health(10), health);
    }

    [Fact]
    public void Submit_destroy_releases_created_entity_or_destroys_existing_entity()
    {
        var world = new World();
        var existing = world.Create(new Position(1, 2));
        var stream = new CommandStream(world);

        var created = stream.Create();
        stream.Add(created, new Position(3, 4));
        stream.Destroy(created);
        stream.Destroy(existing);

        Assert.True(stream.Submit());

        Assert.False(world.IsAlive(created));
        Assert.False(world.IsAlive(existing));
    }

    [Fact]
    public void Snapshot_builds_frame_delta_without_mutating_world()
    {
        var world = new World();
        var existing = world.Create(new Position(0, 0));
        var stream = new CommandStream(world);

        var created = stream.Create();
        stream.Add(created, new Position(1, 2));
        stream.Set(existing, new Position(3, 4));

        var delta = stream.Snapshot();

        Assert.False(world.IsAlive(created));
        Assert.True(world.TryGet(existing, out Position position));
        Assert.Equal(new Position(0, 0), position);

        world.Replay(delta);
        Assert.True(world.IsAlive(created));
        Assert.True(world.TryGet(created, out position));
        Assert.Equal(new Position(1, 2), position);
        Assert.True(world.TryGet(existing, out position));
        Assert.Equal(new Position(3, 4), position);
    }

    [Fact]
    public void Submit_returns_false_when_stream_is_empty()
    {
        var world = new World();
        var stream = new CommandStream(world);

        Assert.False(stream.Submit());
    }
}
