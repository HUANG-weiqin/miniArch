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
        var archetype = CreateArchetype(signature, typeof(Position), typeof(Velocity), capacity: 4);

        var first = new Entity(1, 1);
        var second = new Entity(2, 1);

        var firstRow = archetype.AddEntity(first);
        archetype.SetComponentAtTyped(archetype.GetComponentIndex(position), firstRow, new Position(1, 2));
        archetype.SetComponentAtTyped(archetype.GetComponentIndex(velocity), firstRow, new Velocity(3, 4));

        var secondRow = archetype.AddEntity(second);
        archetype.SetComponentAtTyped(archetype.GetComponentIndex(position), secondRow, new Position(5, 6));
        archetype.SetComponentAtTyped(archetype.GetComponentIndex(velocity), secondRow, new Velocity(7, 8));

        Assert.Equal(0, firstRow);
        Assert.Equal(1, secondRow);
        Assert.Equal(first, archetype.GetEntity(0));
        Assert.Equal(second, archetype.GetEntity(1));
        Assert.Equal(new Position(5, 6), archetype.GetFlatComponentSpan<Position>(position)[1]);
    }

    [Fact]
    public void Removing_a_row_swaps_last_row_into_the_gap()
    {
        var position = MiniArch.Core.Component<Position>.ComponentType;
        var velocity = MiniArch.Core.Component<Velocity>.ComponentType;
        var signature = new Signature(position, velocity);
        var archetype = CreateArchetype(signature, typeof(Position), typeof(Velocity), capacity: 4);

        var first = new Entity(1, 1);
        var second = new Entity(2, 1);
        var third = new Entity(3, 1);

        AddEntity(archetype, first, new Position(1, 1), new Velocity(1, 1));
        AddEntity(archetype, second, new Position(2, 2), new Velocity(2, 2));
        AddEntity(archetype, third, new Position(3, 3), new Velocity(3, 3));

        var moved = archetype.RemoveAt(1, out var movedEntity);

        Assert.True(moved);
        Assert.Equal(third, movedEntity);
        Assert.Equal(third, archetype.GetEntity(1));
        Assert.Equal(new Position(3, 3), archetype.GetFlatComponentSpan<Position>(position)[1]);
        Assert.Equal(2, archetype.EntityCount);
    }

    [Fact]
    public void Removing_the_last_row_returns_default_entity_as_no_move_marker()
    {
        var archetype = CreateEmptyArchetype(capacity: 4);
        var only = new Entity(1, 1);

        archetype.AddEntity(only);

        var moved = archetype.RemoveAt(0, out var movedEntity);

        Assert.False(moved);
        Assert.Equal(default, movedEntity);
        Assert.False(movedEntity.IsValid);
        Assert.Equal(0, archetype.EntityCount);
    }

    [Fact]
    public void Setting_a_component_only_mutates_the_targeted_row()
    {
        var position = MiniArch.Core.Component<Position>.ComponentType;
        var velocity = MiniArch.Core.Component<Velocity>.ComponentType;
        var signature = new Signature(position, velocity);
        var archetype = CreateArchetype(signature, typeof(Position), typeof(Velocity), capacity: 4);

        var first = new Entity(1, 1);
        var second = new Entity(2, 1);

        AddEntity(archetype, first, new Position(1, 1), new Velocity(1, 1));
        AddEntity(archetype, second, new Position(2, 2), new Velocity(2, 2));

        archetype.SetComponentAtTyped(archetype.GetComponentIndex(position), 1, new Position(9, 9));

        Assert.Equal(new Position(1, 1), archetype.GetFlatComponentSpan<Position>(position)[0]);
        Assert.Equal(new Position(9, 9), archetype.GetFlatComponentSpan<Position>(position)[1]);
    }

    [Fact]
    public void Chunk_exposes_a_read_only_span_over_its_live_entities()
    {
        var archetype = CreateEmptyArchetype(capacity: 4);
        var first = new Entity(1, 1);
        var second = new Entity(2, 1);

        archetype.AddEntity(first);
        archetype.AddEntity(second);

        var entities = archetype.GetEntities();

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
        var archetype = CreateArchetype(signature, typeof(Position), typeof(Velocity), capacity: 4);

        AddEntity(archetype, new Entity(1, 1), new Position(1, 2), new Velocity(3, 4));
        AddEntity(archetype, new Entity(2, 1), new Position(5, 6), new Velocity(7, 8));

        var positions = archetype.GetFlatComponentSpan<Position>(position);

        Assert.Equal(2, positions.Length);
        Assert.Equal(new Position(1, 2), positions[0]);
        Assert.Equal(new Position(5, 6), positions[1]);
    }

    [Fact]
    public void Component_lookup_does_not_store_mutable_last_lookup_cache_state()
    {
        Assert.Null(typeof(Archetype).GetField("_lastComponentId", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(typeof(Archetype).GetField("_lastColumnIndex", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void Chunk_uses_flat_byte_storage_for_component_columns()
    {
        Assert.Null(typeof(Archetype).GetField("_columns", BindingFlags.Instance | BindingFlags.NonPublic));
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
        var archetype = CreateArchetype(signature, typeof(Position), typeof(Velocity), capacity: 4);

        var first = new Entity(1, 1);
        var second = new Entity(2, 1);
        var firstPosition = new Position(1, 2);
        var secondPosition = new Position(3, 4);
        var firstVelocity = new Velocity(5, 6);
        var secondVelocity = new Velocity(7, 8);

        AddEntity(archetype, first, firstPosition, firstVelocity);
        AddEntity(archetype, second, secondPosition, secondVelocity);

        var moved = archetype.RemoveAt(0, out var movedEntity);

        var positions = archetype.GetFlatComponentSpan<Position>(position);
        var velocities = archetype.GetFlatComponentSpan<Velocity>(velocity);

        Assert.True(moved);
        Assert.Equal(second, movedEntity);
        Assert.Equal(secondPosition, archetype.GetFlatComponentSpan<Position>(position)[0]);
        Assert.Equal(secondVelocity, archetype.GetFlatComponentSpan<Velocity>(velocity)[0]);
        Assert.Equal(secondPosition, positions[0]);
        Assert.Equal(secondVelocity, velocities[0]);
    }

    [Fact]
    public void Flat_storage_preserves_mixed_size_component_columns()
    {
        var small = MiniArch.Core.Component<Small>.ComponentType;
        var large = MiniArch.Core.Component<Large>.ComponentType;
        var signature = new Signature(small, large);
        var archetype = CreateArchetype(signature, typeof(Small), typeof(Large), capacity: 4);

        AddEntityMixed(archetype, new Entity(1, 1), new Small(1), new Large(2, 3, 4));
        AddEntityMixed(archetype, new Entity(2, 1), new Small(5), new Large(6, 7, 8));

        archetype.SetComponentAtTyped(archetype.GetComponentIndex(small), 0, new Small(9));
        archetype.RemoveAt(0, out _);

        Assert.Equal(new Small(5), archetype.GetFlatComponentSpan<Small>(small)[0]);
        Assert.Equal(new Large(6, 7, 8), archetype.GetFlatComponentSpan<Large>(large)[0]);
        Assert.Equal(new Small(5), archetype.GetFlatComponentSpan<Small>(small)[0]);
        Assert.Equal(new Large(6, 7, 8), archetype.GetFlatComponentSpan<Large>(large)[0]);
    }

    [Fact]
    public void Flat_storage_rejects_managed_reference_components()
    {
        var registry = new ComponentRegistry();
        var label = registry.GetOrCreate<Label>();
        var signature = new Signature(label);

        var exception = Assert.Throws<NotSupportedException>(() =>
            new Archetype(signature, [typeof(Label)], capacity: 4));

        Assert.Contains(nameof(Label), exception.Message);
    }

    private static void AddEntity(Archetype archetype, Entity entity, Position position, Velocity velocity)
    {
        var row = archetype.AddEntity(entity);
        archetype.SetComponentAtTyped(archetype.GetComponentIndex(MiniArch.Core.Component<Position>.ComponentType), row, position);
        archetype.SetComponentAtTyped(archetype.GetComponentIndex(MiniArch.Core.Component<Velocity>.ComponentType), row, velocity);
    }

    private static void AddEntityMixed(Archetype archetype, Entity entity, Small small, Large large)
    {
        var row = archetype.AddEntity(entity);
        archetype.SetComponentAtTyped(archetype.GetComponentIndex(MiniArch.Core.Component<Small>.ComponentType), row, small);
        archetype.SetComponentAtTyped(archetype.GetComponentIndex(MiniArch.Core.Component<Large>.ComponentType), row, large);
    }

    private static Archetype CreateEmptyArchetype(int capacity)
    {
        return new Archetype(Signature.Empty, Type.EmptyTypes, capacity: capacity);
    }

    private static Archetype CreateArchetype(Signature signature, Type t1, Type t2, int capacity)
    {
        return new Archetype(signature, [t1, t2], capacity: capacity);
    }
}
