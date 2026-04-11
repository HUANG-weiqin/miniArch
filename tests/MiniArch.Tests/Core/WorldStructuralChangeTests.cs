using MiniArch.Core;

namespace MiniArch.Tests.Core;

public sealed class WorldStructuralChangeTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

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

        for (var i = 0; i < 1000; i++)
        {
            var entity = world.Create();
            world.Add(entity, new Position(i, i));
            entities.Add(entity);

            Assert.True(world.TryGetLocation(entities[0], out var firstLocation));
            Assert.Contains(world.Components.GetOrCreate<Position>(), firstLocation.Archetype.Signature);
        }

        foreach (var entity in entities)
        {
            Assert.True(world.TryGetLocation(entity, out var before));
            Assert.Contains(world.Components.GetOrCreate<Position>(), before.Archetype.Signature);
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
}
