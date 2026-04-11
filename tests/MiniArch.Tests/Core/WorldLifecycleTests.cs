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

    [Fact]
    public void EnsureCapacity_grows_entity_storage_before_creation()
    {
        var world = new World();

        world.EnsureCapacity(256);

        Assert.True(world.EntityCapacity >= 256);
    }

    [Fact]
    public void Pre_sized_world_can_create_many_valid_entities()
    {
        var world = new World();
        world.EnsureCapacity(512);

        Entity last = default;
        for (var i = 0; i < 512; i++)
        {
            last = world.Create();
        }

        Assert.Equal(512, world.EntityCapacity);
        Assert.True(last.IsValid);
        Assert.True(world.TryGetLocation(last, out var info));
        Assert.Equal(last.Version, info.Version);
    }

    [Fact]
    public void CreateMany_fills_the_supplied_buffer_with_valid_entities()
    {
        var world = new World();
        var entities = new Entity[8];

        world.CreateMany(entities);

        for (var i = 0; i < entities.Length; i++)
        {
            Assert.True(entities[i].IsValid);
            Assert.Equal(i, entities[i].Id);
            Assert.True(world.TryGetLocation(entities[i], out var info));
            Assert.Equal(entities[i].Version, info.Version);
        }
    }

    [Fact]
    public void CreateMany_preserves_location_order_inside_the_empty_archetype()
    {
        var world = new World();
        var entities = new Entity[16];

        world.CreateMany(entities);

        for (var i = 0; i < entities.Length; i++)
        {
            Assert.True(world.TryGetLocation(entities[i], out var info));
            Assert.Equal(0, info.ChunkIndex);
            Assert.Equal(i, info.RowIndex);
        }
    }
}
