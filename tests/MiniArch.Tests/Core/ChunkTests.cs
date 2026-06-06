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
        var position = MiniArch.Core.Component<Position>.ComponentType;
        var velocity = MiniArch.Core.Component<Velocity>.ComponentType;
        var signature = new Signature(position, velocity);
        var chunk = CreateChunk(signature, typeof(Position), typeof(Velocity), capacity: 4);

        var first = new Entity(1, 1);
        var second = new Entity(2, 1);

        var firstRow = chunk.Add(first);
        chunk.SetComponentAtTyped(chunk.GetComponentIndex(position), firstRow, new Position(1, 2));
        chunk.SetComponentAtTyped(chunk.GetComponentIndex(velocity), firstRow, new Velocity(3, 4));

        var secondRow = chunk.Add(second);
        chunk.SetComponentAtTyped(chunk.GetComponentIndex(position), secondRow, new Position(5, 6));
        chunk.SetComponentAtTyped(chunk.GetComponentIndex(velocity), secondRow, new Velocity(7, 8));

        Assert.Equal(0, firstRow);
        Assert.Equal(1, secondRow);
        Assert.Equal(first, chunk.GetEntity(0));
        Assert.Equal(second, chunk.GetEntity(1));
        Assert.Equal(new Position(5, 6), chunk.GetComponentSpan<Position>(position)[1]);
    }

    [Fact]
    public void Removing_a_row_swaps_last_row_into_the_gap()
    {
        var position = MiniArch.Core.Component<Position>.ComponentType;
        var velocity = MiniArch.Core.Component<Velocity>.ComponentType;
        var signature = new Signature(position, velocity);
        var chunk = CreateChunk(signature, typeof(Position), typeof(Velocity), capacity: 4);

        var first = new Entity(1, 1);
        var second = new Entity(2, 1);
        var third = new Entity(3, 1);

        AddEntity(chunk, first, new Position(1, 1), new Velocity(1, 1));
        AddEntity(chunk, second, new Position(2, 2), new Velocity(2, 2));
        AddEntity(chunk, third, new Position(3, 3), new Velocity(3, 3));

        var moved = chunk.RemoveAt(1, out var movedEntity);

        Assert.True(moved);
        Assert.Equal(third, movedEntity);
        Assert.Equal(third, chunk.GetEntity(1));
        Assert.Equal(new Position(3, 3), chunk.GetComponentSpan<Position>(position)[1]);
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
        var position = MiniArch.Core.Component<Position>.ComponentType;
        var velocity = MiniArch.Core.Component<Velocity>.ComponentType;
        var signature = new Signature(position, velocity);
        var chunk = CreateChunk(signature, typeof(Position), typeof(Velocity), capacity: 4);

        var first = new Entity(1, 1);
        var second = new Entity(2, 1);

        AddEntity(chunk, first, new Position(1, 1), new Velocity(1, 1));
        AddEntity(chunk, second, new Position(2, 2), new Velocity(2, 2));

        chunk.SetComponentAtTyped(chunk.GetComponentIndex(position), 1, new Position(9, 9));

        Assert.Equal(new Position(1, 1), chunk.GetComponentSpan<Position>(position)[0]);
        Assert.Equal(new Position(9, 9), chunk.GetComponentSpan<Position>(position)[1]);
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
        var position = MiniArch.Core.Component<Position>.ComponentType;
        var velocity = MiniArch.Core.Component<Velocity>.ComponentType;
        var signature = new Signature(position, velocity);
        var chunk = CreateChunk(signature, typeof(Position), typeof(Velocity), capacity: 4);

        AddEntity(chunk, new Entity(1, 1), new Position(1, 2), new Velocity(3, 4));
        AddEntity(chunk, new Entity(2, 1), new Position(5, 6), new Velocity(7, 8));

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
        Assert.NotNull(typeof(Archetype).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(Archetype).GetField("_columnByteOffsets", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(Archetype).GetField("_elementSizes", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void Removing_a_row_preserves_moved_values_through_typed_apis()
    {
        var position = MiniArch.Core.Component<Position>.ComponentType;
        var velocity = MiniArch.Core.Component<Velocity>.ComponentType;
        var signature = new Signature(position, velocity);
        var chunk = CreateChunk(signature, typeof(Position), typeof(Velocity), capacity: 4);

        var first = new Entity(1, 1);
        var second = new Entity(2, 1);
        var firstPosition = new Position(1, 2);
        var secondPosition = new Position(3, 4);
        var firstVelocity = new Velocity(5, 6);
        var secondVelocity = new Velocity(7, 8);

        AddEntity(chunk, first, firstPosition, firstVelocity);
        AddEntity(chunk, second, secondPosition, secondVelocity);

        var moved = chunk.RemoveAt(0, out var movedEntity);

        var positions = chunk.GetComponentSpan<Position>(position);
        var velocities = chunk.GetComponentSpan<Velocity>(velocity);

        Assert.True(moved);
        Assert.Equal(second, movedEntity);
        Assert.Equal(secondPosition, chunk.GetComponentSpan<Position>(position)[0]);
        Assert.Equal(secondVelocity, chunk.GetComponentSpan<Velocity>(velocity)[0]);
        Assert.Equal(secondPosition, positions[0]);
        Assert.Equal(secondVelocity, velocities[0]);
    }

    [Fact]
    public void Flat_storage_preserves_mixed_size_component_columns()
    {
        var small = MiniArch.Core.Component<Small>.ComponentType;
        var large = MiniArch.Core.Component<Large>.ComponentType;
        var signature = new Signature(small, large);
        var chunk = CreateChunk(signature, typeof(Small), typeof(Large), capacity: 4);

        AddEntityMixed(chunk, new Entity(1, 1), new Small(1), new Large(2, 3, 4));
        AddEntityMixed(chunk, new Entity(2, 1), new Small(5), new Large(6, 7, 8));

        chunk.SetComponentAtTyped(chunk.GetComponentIndex(small), 0, new Small(9));
        chunk.RemoveAt(0, out _);

        Assert.Equal(new Small(5), chunk.GetComponentSpan<Small>(small)[0]);
        Assert.Equal(new Large(6, 7, 8), chunk.GetComponentSpan<Large>(large)[0]);
        Assert.Equal(new Small(5), chunk.GetComponentSpan<Small>(small)[0]);
        Assert.Equal(new Large(6, 7, 8), chunk.GetComponentSpan<Large>(large)[0]);
    }

    [Fact]
    public void Flat_storage_rejects_managed_reference_components()
    {
        var registry = ComponentRegistry.Shared;
        var label = registry.GetOrCreate<Label>();
        var signature = new Signature(label);

        var exception = Assert.Throws<NotSupportedException>(() =>
            new Archetype(signature, [typeof(Label)], capacity: 4));

        Assert.Contains(nameof(Label), exception.Message);
    }

    private static void AddEntity(Chunk chunk, Entity entity, Position position, Velocity velocity)
    {
        var row = chunk.Add(entity);
        chunk.SetComponentAtTyped(chunk.GetComponentIndex(MiniArch.Core.Component<Position>.ComponentType), row, position);
        chunk.SetComponentAtTyped(chunk.GetComponentIndex(MiniArch.Core.Component<Velocity>.ComponentType), row, velocity);
    }

    private static void AddEntityMixed(Chunk chunk, Entity entity, Small small, Large large)
    {
        var row = chunk.Add(entity);
        chunk.SetComponentAtTyped(chunk.GetComponentIndex(MiniArch.Core.Component<Small>.ComponentType), row, small);
        chunk.SetComponentAtTyped(chunk.GetComponentIndex(MiniArch.Core.Component<Large>.ComponentType), row, large);
    }

    private static Chunk CreateEmptyChunk(int capacity)
    {
        return new Archetype(Signature.Empty, Type.EmptyTypes, capacity: capacity).GetChunkSpan()[0];
    }

    private static Chunk CreateChunk(Signature signature, Type t1, Type t2, int capacity)
    {
        return new Archetype(signature, [t1, t2], capacity: capacity).GetChunkSpan()[0];
    }
}
