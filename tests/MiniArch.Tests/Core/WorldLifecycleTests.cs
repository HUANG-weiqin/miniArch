using MiniArch.Core;

namespace MiniArch.Tests.Core;

public sealed class WorldLifecycleTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

    [Fact]
    public void Create_returns_a_valid_entity()
    {
        var world = new World();

        var entity = world.Create();

        Assert.True(entity.IsValid);
        Assert.True(world.TryGetLocation(entity, out var info));
        Assert.Equal(entity.Version, info.Version);
    }

    [Fact]
    public void Destroy_recycles_ids_safely()
    {
        var world = new World();
        var first = world.Create();

        world.Destroy(first);
        var second = world.Create();

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Version, second.Version);
    }

    [Fact]
    public void Version_mismatch_makes_stale_entities_invalid()
    {
        var world = new World();
        var first = world.Create();

        world.Destroy(first);
        var second = world.Create();

        Assert.False(world.TryGetLocation(first, out _));
        Assert.True(world.TryGetLocation(second, out _));
    }

    [Fact]
    public void Entity_metadata_points_to_the_current_archetype_and_chunk_position()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new Position(1, 2));
        world.Add(entity, new Velocity(3, 4));

        Assert.True(world.TryGetLocation(entity, out var info));
        Assert.Contains(new ComponentType(0), info.Archetype.Signature);
        Assert.Contains(new ComponentType(1), info.Archetype.Signature);
        Assert.Equal(0, info.ChunkIndex);
        Assert.Equal(0, info.RowIndex);
    }
}
