using System.Reflection;
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

    [Fact]
    public void Chunk_exposes_a_read_only_span_over_its_live_entities()
    {
        var chunk = new Chunk(Signature.Empty, capacity: 4);
        var first = new Entity(1, 0);
        var second = new Entity(2, 0);

        chunk.Add(first);
        chunk.Add(second);

        var entities = chunk.GetEntities();

        Assert.Equal(2, entities.Length);
        Assert.Equal(first, entities[0]);
        Assert.Equal(second, entities[1]);
    }

    [Fact]
    public void Removing_from_a_typed_chunk_only_clears_reference_columns()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var label = registry.GetOrCreate<Label>();
        var signature = new Signature(position, label);
        var chunk = CreateTypedChunk(signature, new[] { typeof(Position), typeof(Label) }, capacity: 4);

        var first = new Entity(1, 0);
        var second = new Entity(2, 0);
        var firstPosition = new Position(1, 2);
        var secondPosition = new Position(3, 4);
        var firstLabel = new Label("first");
        var secondLabel = new Label("second");

        chunk.Add(first, Components(position, label, firstPosition, firstLabel));
        chunk.Add(second, Components(position, label, secondPosition, secondLabel));

        var moved = chunk.RemoveAt(0, out var movedEntity);

        var columns = GetColumns(chunk);
        var positionColumn = (Position[])columns[0];
        var labelColumn = (Label?[])columns[1];

        Assert.True(moved);
        Assert.Equal(second, movedEntity);
        Assert.Equal(secondPosition, positionColumn[0]);
        Assert.Equal(secondLabel, labelColumn[0]);
        Assert.Equal(secondPosition, positionColumn[1]);
        Assert.Null(labelColumn[1]);
    }

    [Fact]
    public void Removing_from_a_typed_chunk_clears_struct_columns_that_contain_references()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var labelStruct = registry.GetOrCreate<LabelStruct>();
        var signature = new Signature(position, labelStruct);
        var chunk = CreateTypedChunk(signature, new[] { typeof(Position), typeof(LabelStruct) }, capacity: 4);

        var first = new Entity(1, 0);
        var second = new Entity(2, 0);
        var firstPosition = new Position(1, 2);
        var secondPosition = new Position(3, 4);
        var firstLabel = new LabelStruct("first");
        var secondLabel = new LabelStruct("second");

        chunk.Add(first, Components(position, labelStruct, firstPosition, firstLabel));
        chunk.Add(second, Components(position, labelStruct, secondPosition, secondLabel));

        _ = chunk.RemoveAt(0, out _);

        var columns = GetColumns(chunk);
        var positionColumn = (Position[])columns[0];
        var labelColumn = (LabelStruct[])columns[1];

        Assert.Equal(secondPosition, positionColumn[0]);
        Assert.Equal(secondLabel, labelColumn[0]);
        Assert.Equal(secondPosition, positionColumn[1]);
        Assert.Equal(default, labelColumn[1]);
    }

    private static Dictionary<ComponentType, object?> Components(ComponentType position, ComponentType velocity, Position p, Velocity v)
    {
        return new Dictionary<ComponentType, object?>
        {
            [position] = p,
            [velocity] = v,
        };
    }

    private static Dictionary<ComponentType, object?> Components(ComponentType position, ComponentType label, Position p, Label value)
    {
        return new Dictionary<ComponentType, object?>
        {
            [position] = p,
            [label] = value,
        };
    }

    private static Dictionary<ComponentType, object?> Components(ComponentType position, ComponentType label, Position p, LabelStruct value)
    {
        return new Dictionary<ComponentType, object?>
        {
            [position] = p,
            [label] = value,
        };
    }

    private static Chunk CreateTypedChunk(Signature signature, Type[] componentTypes, int capacity)
    {
        var componentIdToColumnIndex = new int[componentTypes.Length];
        for (var index = 0; index < componentIdToColumnIndex.Length; index++)
        {
            componentIdToColumnIndex[index] = index;
        }

        var constructor = typeof(Chunk).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(Signature), typeof(Type[]), typeof(int[]), typeof(int) },
            modifiers: null);

        Assert.NotNull(constructor);
        return (Chunk)constructor!.Invoke(new object?[] { signature, componentTypes, componentIdToColumnIndex, capacity });
    }

    private static Array[] GetColumns(Chunk chunk)
    {
        var field = typeof(Chunk).GetField("_columns", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (Array[])field!.GetValue(chunk)!;
    }

    private sealed record class Label(string Value);
    private readonly record struct LabelStruct(string Value);
}
