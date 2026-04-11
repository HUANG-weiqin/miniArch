using MiniArch.Core;

namespace MiniArch.Tests.Core;

public sealed class ChunkTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);

    [Fact]
    public void Chunk_stores_entities_densely()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();
        var chunk = new Chunk(new Signature(position, velocity), capacity: 4);

        var first = new Entity(1, 0);
        var second = new Entity(2, 0);

        var firstRow = chunk.Add(first, new Dictionary<ComponentType, object?>
        {
            [position] = new Position(1, 2),
            [velocity] = new Velocity(3, 4),
        });
        var secondRow = chunk.Add(second, new Dictionary<ComponentType, object?>
        {
            [position] = new Position(5, 6),
            [velocity] = new Velocity(7, 8),
        });

        Assert.Equal(0, firstRow);
        Assert.Equal(1, secondRow);
        Assert.Equal(first, chunk.GetEntity(0));
        Assert.Equal(second, chunk.GetEntity(1));
        Assert.Equal(new Position(5, 6), chunk.GetComponent<Position>(position, 1));
    }

    [Fact]
    public void Removing_a_row_swaps_last_row_into_the_gap()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();
        var chunk = new Chunk(new Signature(position, velocity), capacity: 4);

        var first = new Entity(1, 0);
        var second = new Entity(2, 0);
        var third = new Entity(3, 0);

        chunk.Add(first, Components(position, velocity, new Position(1, 1), new Velocity(1, 1)));
        chunk.Add(second, Components(position, velocity, new Position(2, 2), new Velocity(2, 2)));
        chunk.Add(third, Components(position, velocity, new Position(3, 3), new Velocity(3, 3)));

        var moved = chunk.RemoveAt(1, out var movedEntity);

        Assert.True(moved);
        Assert.Equal(third, movedEntity);
        Assert.Equal(third, chunk.GetEntity(1));
        Assert.Equal(new Position(3, 3), chunk.GetComponent<Position>(position, 1));
        Assert.Equal(2, chunk.Count);
    }

    [Fact]
    public void Setting_a_component_only_mutates_the_targeted_row()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();
        var chunk = new Chunk(new Signature(position, velocity), capacity: 4);

        var first = new Entity(1, 0);
        var second = new Entity(2, 0);

        chunk.Add(first, Components(position, velocity, new Position(1, 1), new Velocity(1, 1)));
        chunk.Add(second, Components(position, velocity, new Position(2, 2), new Velocity(2, 2)));

        chunk.SetComponent(position, 1, new Position(9, 9));

        Assert.Equal(new Position(1, 1), chunk.GetComponent<Position>(position, 0));
        Assert.Equal(new Position(9, 9), chunk.GetComponent<Position>(position, 1));
    }

    private static Dictionary<ComponentType, object?> Components(ComponentType position, ComponentType velocity, Position p, Velocity v)
    {
        return new Dictionary<ComponentType, object?>
        {
            [position] = p,
            [velocity] = v,
        };
    }
}
