using MiniArch.Core;

namespace MiniArch.Tests.Core;

public sealed class ArchetypeTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

    [Fact]
    public void Creating_an_archetype_allocates_an_initial_chunk()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();

        var archetype = new Archetype(new Signature(position));

        Assert.Single(archetype.Chunks);
        Assert.Equal(0, archetype.Chunks[0].Count);
    }

    [Fact]
    public void Adding_entities_fills_the_current_chunk_before_allocating_a_new_one()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position), chunkCapacity: 2);

        archetype.AddEntity(new Entity(1, 0), Components(position, new Position(1, 1)), out _, out _);
        archetype.AddEntity(new Entity(2, 0), Components(position, new Position(2, 2)), out _, out _);
        archetype.AddEntity(new Entity(3, 0), Components(position, new Position(3, 3)), out _, out _);

        Assert.Equal(2, archetype.Chunks.Count);
        Assert.Equal(2, archetype.Chunks[0].Count);
        Assert.Equal(1, archetype.Chunks[1].Count);
    }

    [Fact]
    public void Removing_an_entity_preserves_dense_packing()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var archetype = new Archetype(new Signature(position));

        var first = new Entity(1, 0);
        var second = new Entity(2, 0);
        var third = new Entity(3, 0);

        archetype.AddEntity(first, Components(position, new Position(1, 1)), out var chunkIndex, out _);
        archetype.AddEntity(second, Components(position, new Position(2, 2)), out _, out _);
        archetype.AddEntity(third, Components(position, new Position(3, 3)), out _, out _);

        var moved = archetype.RemoveEntity(chunkIndex, 1, out var movedEntity);

        Assert.True(moved);
        Assert.Equal(third, movedEntity);
        Assert.Equal(third, archetype.GetChunk(0).GetEntity(1));
        Assert.Equal(new Position(3, 3), archetype.GetChunk(0).GetComponent<Position>(position, 1));
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

    private static Dictionary<ComponentType, object?> Components(ComponentType position, Position value)
    {
        return new Dictionary<ComponentType, object?>
        {
            [position] = value,
        };
    }
}
