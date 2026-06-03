using MiniArch.Core;
using MiniQuery = MiniArch.Core.Query;

namespace MiniArchTests.Core;

public sealed class WorldStructuralChangeTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    [Fact]
    public void Add_moves_entity_to_destination_archetype()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new Position(1, 2));

        Assert.True(world.TryGetLocation(entity, out var info));
        Assert.Single(info.Archetype.Signature);
        Assert.Contains(world.Components.GetOrCreate<Position>(), info.Archetype.Signature);
    }

    [Fact]
    public void Set_updates_existing_component_in_place()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new Position(1, 2));
        Assert.True(world.TryGetLocation(entity, out var before));

        world.Set(entity, new Position(9, 9));

        Assert.True(world.TryGetLocation(entity, out var after));
        Assert.Same(before.Archetype, after.Archetype);
        Assert.Equal(before.ChunkIndex, after.ChunkIndex);
        Assert.Equal(before.RowIndex, after.RowIndex);
        Assert.Equal(new Position(9, 9), after.Archetype.GetChunk(after.ChunkIndex).GetComponent<Position>(world.Components.GetOrCreate<Position>(), after.RowIndex));
    }

    [Fact]
    public void Set_only_mutates_the_target_component_when_multiple_components_are_present()
    {
        var world = new World();
        var entity = world.Create();

        var positionId = world.Components.GetOrCreate<Position>();
        var velocityId = world.Components.GetOrCreate<Velocity>();

        world.Add(entity, new Position(1, 2));
        world.Add(entity, new Velocity(3, 4));
        Assert.True(world.TryGetLocation(entity, out var before));

        world.Set(entity, new Position(9, 9));

        Assert.True(world.TryGetLocation(entity, out var after));
        Assert.Same(before.Archetype, after.Archetype);
        Assert.Equal(before.ChunkIndex, after.ChunkIndex);
        Assert.Equal(before.RowIndex, after.RowIndex);

        var chunk = after.Archetype.GetChunk(after.ChunkIndex);
        Assert.Equal(new Position(9, 9), chunk.GetComponent<Position>(positionId, after.RowIndex));
        Assert.Equal(new Velocity(3, 4), chunk.GetComponent<Velocity>(velocityId, after.RowIndex));
    }

    [Fact]
    public void Remove_moves_entity_back_to_smaller_archetype()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new Position(1, 2));
        world.Add(entity, new Velocity(3, 4));

        Assert.True(world.TryGetLocation(entity, out var before));

        world.Remove<Velocity>(entity);

        Assert.True(world.TryGetLocation(entity, out var after));
        Assert.NotSame(before.Archetype, after.Archetype);
        Assert.Single(after.Archetype.Signature);
        Assert.Contains(world.Components.GetOrCreate<Position>(), after.Archetype.Signature);
        Assert.DoesNotContain(world.Components.GetOrCreate<Velocity>(), after.Archetype.Signature);
    }

    [Fact]
    public void Bulk_set_over_many_entities_keeps_locations_valid()
    {
        var world = new World();
        var entities = new List<Entity>();
        var positionId = world.Components.GetOrCreate<Position>();

        for (var i = 0; i < 1000; i++)
        {
            var entity = world.Create();
            world.Add(entity, new Position(i, i));
            entities.Add(entity);

            Assert.True(world.TryGetLocation(entities[0], out var firstLocation));
            Assert.Contains(positionId, firstLocation.Archetype.Signature);
        }

        foreach (var entity in entities)
        {
            Assert.True(world.TryGetLocation(entity, out var before));
            Assert.Contains(positionId, before.Archetype.Signature);
            try
            {
                world.Set(entity, new Position(42, 42));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Set failed for entity {entity}.", ex);
            }
            Assert.True(world.TryGetLocation(entity, out var after));
            Assert.Same(before.Archetype, after.Archetype);
            Assert.Equal(before.ChunkIndex, after.ChunkIndex);
            Assert.Equal(before.RowIndex, after.RowIndex);
        }
    }

    [Fact]
    public void Set_missing_component_adds_component()
    {
        var world = new World();
        var entity = world.Create();

        world.Set(entity, new Position(9, 9));

        Assert.True(HasComponent<Position>(world, entity));
        Assert.Equal(new Position(9, 9), GetComponentValue(world, entity));
    }

    [Fact]
    public void Set_missing_component_preserves_existing_components()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new Position(1, 2));
        world.Set(entity, new Velocity(3, 4));

        Assert.True(HasComponent<Position>(world, entity));
        Assert.True(HasComponent<Velocity>(world, entity));
        Assert.Equal(new Position(1, 2), GetComponentValue(world, entity));
        Assert.Equal(new Velocity(3, 4), world.TryGetLocation(entity, out var info)
            ? info.Archetype.GetChunk(info.ChunkIndex).GetComponent<Velocity>(world.Components.GetOrCreate<Velocity>(), info.RowIndex)
            : default);
    }

    private static Position GetComponentValue(World world, Entity entity)
    {
        Assert.True(world.TryGetLocation(entity, out var info));
        return info.Archetype.GetChunk(info.ChunkIndex).GetComponent<Position>(world.Components.GetOrCreate<Position>(), info.RowIndex);
    }

    private static bool HasComponent<T>(World world, Entity entity)
    {
        return world.TryGetLocation(entity, out var info) && info.Archetype.Signature.Contains(world.Components.GetOrCreate<T>());
    }

}
