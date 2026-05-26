using System.Reflection;
using MiniArch.Core;

namespace MiniArchTests.Core;

public sealed class ChunkTests
{
    private readonly record struct Small(byte Value);
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Large(long A, int B, byte C);
    private sealed record class Label(string Value);

    [Fact]
    public void Chunk_stores_entities_densely()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();
        var signature = new Signature(position, velocity);
        var chunk = CreateChunk(signature, typeof(Position), typeof(Velocity), capacity: 4);

        var first = new Entity(1, 1);
        var second = new Entity(2, 1);

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
        var signature = new Signature(position, velocity);
        var chunk = CreateChunk(signature, typeof(Position), typeof(Velocity), capacity: 4);

        var first = new Entity(1, 1);
        var second = new Entity(2, 1);
        var third = new Entity(3, 1);

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
    public void Removing_the_last_row_returns_default_entity_as_no_move_marker()
    {
        var chunk = CreateEmptyChunk(capacity: 4);
        var only = new Entity(1, 1);

        chunk.Add(only);

        var moved = chunk.RemoveAt(0, out var movedEntity);

        Assert.False(moved);
        Assert.Equal(default, movedEntity);
        Assert.False(movedEntity.IsValid);
        Assert.Equal(0, chunk.Count);
    }

    [Fact]
    public void Setting_a_component_only_mutates_the_targeted_row()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();
        var signature = new Signature(position, velocity);
        var chunk = CreateChunk(signature, typeof(Position), typeof(Velocity), capacity: 4);

        var first = new Entity(1, 1);
        var second = new Entity(2, 1);

        chunk.Add(first, Components(position, velocity, new Position(1, 1), new Velocity(1, 1)));
        chunk.Add(second, Components(position, velocity, new Position(2, 2), new Velocity(2, 2)));

        chunk.SetComponent(position, 1, new Position(9, 9));

        Assert.Equal(new Position(1, 1), chunk.GetComponent<Position>(position, 0));
        Assert.Equal(new Position(9, 9), chunk.GetComponent<Position>(position, 1));
    }

    [Fact]
    public void Chunk_exposes_a_read_only_span_over_its_live_entities()
    {
        var chunk = CreateEmptyChunk(capacity: 4);
        var first = new Entity(1, 1);
        var second = new Entity(2, 1);

        chunk.Add(first);
        chunk.Add(second);

        var entities = chunk.GetEntities();

        Assert.Equal(2, entities.Length);
        Assert.Equal(first, entities[0]);
        Assert.Equal(second, entities[1]);
    }

    [Fact]
    public void Typed_chunk_exposes_a_read_only_span_over_a_component_column()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();
        var signature = new Signature(position, velocity);
        var chunk = CreateChunk(signature, typeof(Position), typeof(Velocity), capacity: 4);

        chunk.Add(new Entity(1, 1), Components(position, velocity, new Position(1, 2), new Velocity(3, 4)));
        chunk.Add(new Entity(2, 1), Components(position, velocity, new Position(5, 6), new Velocity(7, 8)));

        var positions = chunk.GetComponentSpan<Position>(position);

        Assert.Equal(2, positions.Length);
        Assert.Equal(new Position(1, 2), positions[0]);
        Assert.Equal(new Position(5, 6), positions[1]);
    }

    [Fact]
    public void Component_lookup_does_not_store_mutable_last_lookup_cache_state()
    {
        Assert.Null(typeof(Chunk).GetField("_lastComponentId", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(typeof(Chunk).GetField("_lastColumnIndex", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void Chunk_uses_flat_byte_storage_for_component_columns()
    {
        Assert.Null(typeof(Chunk).GetField("_columns", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(Chunk).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(Chunk).GetField("_columnByteOffsets", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(Chunk).GetField("_elementSizes", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void Removing_a_row_preserves_moved_values_through_typed_apis()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();
        var signature = new Signature(position, velocity);
        var chunk = CreateChunk(signature, typeof(Position), typeof(Velocity), capacity: 4);

        var first = new Entity(1, 1);
        var second = new Entity(2, 1);
        var firstPosition = new Position(1, 2);
        var secondPosition = new Position(3, 4);
        var firstVelocity = new Velocity(5, 6);
        var secondVelocity = new Velocity(7, 8);

        chunk.Add(first, Components(position, velocity, firstPosition, firstVelocity));
        chunk.Add(second, Components(position, velocity, secondPosition, secondVelocity));

        var moved = chunk.RemoveAt(0, out var movedEntity);

        var positions = chunk.GetComponentSpan<Position>(position);
        var velocities = chunk.GetComponentSpan<Velocity>(velocity);

        Assert.True(moved);
        Assert.Equal(second, movedEntity);
        Assert.Equal(secondPosition, chunk.GetComponent<Position>(position, 0));
        Assert.Equal(secondVelocity, chunk.GetComponent<Velocity>(velocity, 0));
        Assert.Equal(secondPosition, positions[0]);
        Assert.Equal(secondVelocity, velocities[0]);
    }

    [Fact]
    public void Flat_storage_preserves_mixed_size_component_columns()
    {
        var registry = new ComponentRegistry();
        var small = registry.GetOrCreate<Small>();
        var large = registry.GetOrCreate<Large>();
        var signature = new Signature(small, large);
        var chunk = CreateChunk(signature, typeof(Small), typeof(Large), capacity: 4);

        chunk.Add(new Entity(1, 1), Components(small, large, new Small(1), new Large(2, 3, 4)));
        chunk.Add(new Entity(2, 1), Components(small, large, new Small(5), new Large(6, 7, 8)));

        chunk.SetComponent(small, 0, new Small(9));
        chunk.RemoveAt(0, out _);

        Assert.Equal(new Small(5), chunk.GetComponent<Small>(small, 0));
        Assert.Equal(new Large(6, 7, 8), chunk.GetComponent<Large>(large, 0));
        Assert.Equal(new Small(5), chunk.GetComponentSpan<Small>(small)[0]);
        Assert.Equal(new Large(6, 7, 8), chunk.GetComponentSpan<Large>(large)[0]);
    }

    [Fact]
    public void Flat_storage_rejects_managed_reference_components()
    {
        var registry = new ComponentRegistry();
        var label = registry.GetOrCreate<Label>();
        var signature = new Signature(label);

        var exception = Assert.Throws<NotSupportedException>(() =>
            new Chunk(signature, [typeof(Label)], new[] { 0 }, capacity: 4));

        Assert.Contains(nameof(Label), exception.Message);
    }

    private static Chunk CreateEmptyChunk(int capacity)
    {
        return new Chunk(Signature.Empty, Type.EmptyTypes, Array.Empty<int>(), capacity);
    }

    private static Chunk CreateChunk(Signature signature, Type t1, Type t2, int capacity)
    {
        var componentIdToColumnIndex = new int[2];
        var components = signature.AsSpan();
        for (var i = 0; i < components.Length; i++)
        {
            componentIdToColumnIndex[components[i].Value] = i;
        }

        return new Chunk(signature, [t1, t2], componentIdToColumnIndex, capacity);
    }

    private static Dictionary<ComponentType, object?> Components(ComponentType position, ComponentType velocity, Position p, Velocity v)
    {
        return new Dictionary<ComponentType, object?>
        {
            [position] = p,
            [velocity] = v,
        };
    }

    private static Dictionary<ComponentType, object?> Components(ComponentType small, ComponentType large, Small s, Large l)
    {
        return new Dictionary<ComponentType, object?>
        {
            [small] = s,
            [large] = l,
        };
    }

}
