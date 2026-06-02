using MiniArch.Core;
using System.Reflection;
using Xunit.Abstractions;

namespace MiniArchTests.Core;

public sealed class ChunkColumnIndexTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    private readonly ITestOutputHelper _output;

    public ChunkColumnIndexTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TryGetColumnIndices_SingleComponent_ReturnsSuccess()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();

        var signature = new Signature(position);
        var typedChunk = CreateTypedChunk(signature, new[] { typeof(Position) }, capacity: 4);

        Span<int> indices = stackalloc int[1];
        bool success = typedChunk.TryGetColumnIndices(
            new ReadOnlySpan<ComponentType>(in position),
            indices);

        Assert.True(success);
        Assert.Equal(0, indices[0]);
    }

    [Fact]
    public void TryGetColumnIndices_TwoComponents_ReturnsSuccess()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();

        var signature = new Signature(position, velocity);
        var typedChunk = CreateTypedChunk(signature, new[] { typeof(Position), typeof(Velocity) }, capacity: 4);

        Span<int> indices = stackalloc int[2];
        bool success = typedChunk.TryGetColumnIndices(
            new ComponentType[] { position, velocity },
            indices);

        Assert.True(success);
        Assert.Equal(0, indices[0]);
        Assert.Equal(1, indices[1]);
    }

    [Fact]
    public void TryGetColumnIndices_ThreeComponents_ReturnsSuccess()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();
        var health = registry.GetOrCreate<Health>();

        var signature = new Signature(position, velocity, health);
        var typedChunk = CreateTypedChunk(signature, new[] { typeof(Position), typeof(Velocity), typeof(Health) }, capacity: 4);

        Span<int> indices = stackalloc int[3];
        bool success = typedChunk.TryGetColumnIndices(
            new ComponentType[] { position, velocity, health },
            indices);

        Assert.True(success);
        Assert.Equal(0, indices[0]);
        Assert.Equal(1, indices[1]);
        Assert.Equal(2, indices[2]);
    }

    [Fact]
    public void TryGetColumnIndices_MissingComponent_ReturnsFailure()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();

        var signature = new Signature(position, velocity);
        var typedChunk = CreateTypedChunk(signature, new[] { typeof(Position), typeof(Velocity) }, capacity: 4);

        var nonExistent = new ComponentType(typeof(NonExistentComponent).GetHashCode());
        Span<int> indices = stackalloc int[2];
        bool success = typedChunk.TryGetColumnIndices(
            new ComponentType[] { position, nonExistent },
            indices);

        Assert.False(success);
    }

    [Fact]
    public void TryGetColumnIndices_MismatchedLengths_ThrowsArgumentException()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();

        var signature = new Signature(position, velocity);
        var typedChunk = CreateTypedChunk(signature, new[] { typeof(Position), typeof(Velocity) }, capacity: 4);

        Assert.Throws<ArgumentException>(() =>
        {
            var nonExistent = new ComponentType(typeof(NonExistentComponent).GetHashCode());
            var componentTypes = new ComponentType[] { position, velocity, nonExistent };
            Span<int> indices = stackalloc int[2];
            typedChunk.TryGetColumnIndices(
                componentTypes,
                indices);
        });
    }

    [Fact]
    public void TryGetColumnIndices_PartialFailureDoesNotModifyOutput()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();

        var signature = new Signature(position, velocity);
        var typedChunk = CreateTypedChunk(signature, new[] { typeof(Position), typeof(Velocity) }, capacity: 4);

        var nonExistent = new ComponentType(typeof(NonExistentComponent).GetHashCode());
        Span<int> indices = stackalloc int[2];
        
        indices[0] = 99;
        indices[1] = 88;

        bool success = typedChunk.TryGetColumnIndices(
            new ComponentType[] { position, nonExistent },
            indices);

        Assert.False(success);
        Assert.Equal(0, indices[0]);
        Assert.Equal(88, indices[1]);
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
            new[] { typeof(Signature), typeof(Type[]), typeof(int[]), typeof(int), typeof(int) },
            modifiers: null);

        Assert.NotNull(constructor);
        return (Chunk)constructor!.Invoke(new object?[] { signature, componentTypes, componentIdToColumnIndex, capacity, capacity });
    }

    private readonly record struct NonExistentComponent(int Value);
}
