using MiniArch.Core;

namespace MiniArchTests.Core;

public sealed class ArchetypeTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    [Fact]
    public void Creating_an_archetype_starts_with_zero_entities()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();

        var archetype = new Archetype(new Signature(position), [typeof(Position)]);

        Assert.Equal(0, archetype.EntityCount);
    }

    [Fact]
    public void Entities_are_stored_in_a_single_growing_storage_block()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), [typeof(Position)], capacity: 2);

        var row1 = archetype.AddEntity(new Entity(1, 1));
        archetype.SetComponentAtTyped(0, row1, new Position(1, 1));
        var row2 = archetype.AddEntity(new Entity(2, 1));
        archetype.SetComponentAtTyped(0, row2, new Position(2, 2));

        Assert.Equal(2, archetype.EntityCount);

        var row3 = archetype.AddEntity(new Entity(3, 1));
        archetype.SetComponentAtTyped(0, row3, new Position(3, 3));

        Assert.Equal(3, archetype.EntityCount);
        Assert.True(archetype.Capacity >= 3);
    }

    [Fact]
    public void Removing_an_entity_preserves_dense_packing()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), [typeof(Position)]);

        var first = new Entity(1, 1);
        var second = new Entity(2, 1);
        var third = new Entity(3, 1);

        var row = archetype.AddEntity(first);
        archetype.SetComponentAtTyped(0, row, new Position(1, 1));
        var row2 = archetype.AddEntity(second);
        archetype.SetComponentAtTyped(0, row2, new Position(2, 2));
        var row3 = archetype.AddEntity(third);
        archetype.SetComponentAtTyped(0, row3, new Position(3, 3));

        var moved = archetype.RemoveAt(1, out var movedEntity);

        Assert.True(moved);
        Assert.Equal(third, movedEntity);
        Assert.Equal(third, archetype.GetEntity(1));
        Assert.Equal(new Position(3, 3), archetype.GetComponentSpan<Position>(position)[1]);
    }

    [Fact]
    public void Entities_use_a_single_storage_block_that_grows_and_shrinks()
    {
        var archetype = new Archetype(Signature.Empty, Type.EmptyTypes, capacity: 2);

        archetype.AddEntity(new Entity(1, 1));
        archetype.AddEntity(new Entity(2, 1));
        archetype.AddEntity(new Entity(3, 1));
        archetype.AddEntity(new Entity(4, 1));

        Assert.Equal(4, archetype.EntityCount);

        archetype.RemoveAt(0, out _);
        archetype.RemoveAt(0, out _);
        archetype.RemoveAt(0, out _);
        archetype.RemoveAt(0, out _);

        Assert.Equal(0, archetype.EntityCount);

        archetype.AddEntity(new Entity(5, 1));
        archetype.AddEntity(new Entity(6, 1));
        archetype.AddEntity(new Entity(7, 1));
        archetype.AddEntity(new Entity(8, 1));

        Assert.Equal(4, archetype.EntityCount);
    }

    [Fact]
    public void Archetype_is_a_single_storage_block()
    {
        var archetype = new Archetype(Signature.Empty, Type.EmptyTypes);
        Assert.Equal(0, archetype.EntityCount);
    }

    [Fact]
    public void Add_and_remove_transition_edges_are_cached_and_reused()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new Position(1, 1));
        Assert.True(world.TryGetLocation(entity, out var firstLocation));

        world.Remove<Position>(entity);
        world.Add(entity, new Position(2, 2));
        Assert.True(world.TryGetLocation(entity, out var secondLocation));

        Assert.Same(firstLocation.Archetype, secondLocation.Archetype);
    }

    [Fact]
    public void Transition_edges_handle_late_registered_component_ids()
    {
        var world = new World();
        var entity = world.Create();

        world.Add(entity, new Position(1, 1));
        world.Remove<Position>(entity);
        world.Add(entity, new Health(7));
        Assert.True(world.TryGetLocation(entity, out var firstHealthLocation));

        world.Remove<Health>(entity);
        world.Add(entity, new Health(9));
        Assert.True(world.TryGetLocation(entity, out var secondHealthLocation));

        Assert.Same(firstHealthLocation.Archetype, secondHealthLocation.Archetype);
    }

}
