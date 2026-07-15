using MiniArch.Core;

namespace MiniArchTests.Core;

public sealed class ChunkColumnIndexTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct Health(int Value);

    [Fact]
    public void UnsafeGetComponentSpanAt_reads_a_matching_pre_resolved_column()
    {
        using var world = new World();
        world.Create(new Position(1, 2), new Velocity(3, 4));
        var query = world.Query(new QueryDescription().With<Position>().With<Velocity>());
        var chunks = query.GetChunks();
        Assert.Equal(1, chunks.Length);
        var chunk = chunks[0];

        Assert.True(chunk.TryGetComponentIndex<Position>(out var positionColumn));
        var positions = chunk.UnsafeGetComponentSpanAt<Position>(positionColumn);

        Assert.Equal(new Position(1, 2), Assert.Single(positions.ToArray()));
    }

#if DEBUG
    [Fact]
    public void UnsafeGetComponentSpanAt_rejects_a_mismatched_type_in_debug()
    {
        using var world = new World();
        world.Create(new Position(1, 2), new Velocity(3, 4));
        var query = world.Query(new QueryDescription().With<Position>().With<Velocity>());
        var chunk = query.GetChunks()[0];

        Assert.True(chunk.TryGetComponentIndex<Position>(out var positionColumn));

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = chunk.UnsafeGetComponentSpanAt<Velocity>(positionColumn);
        });
    }
#endif

    [Fact]
    public void TryGetComponentIndex_SingleComponent_ReturnsSuccess()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();

        var signature = new Signature(position);
        var archetype = CreateArchetype(signature, new[] { typeof(Position) }, capacity: 4);

        Assert.True(archetype.TryGetComponentIndex(position, out var index));
        Assert.Equal(0, index);
    }

    [Fact]
    public void TryGetComponentIndex_TwoComponents_ReturnsSuccess()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();

        var signature = new Signature(position, velocity);
        var archetype = CreateArchetype(signature, new[] { typeof(Position), typeof(Velocity) }, capacity: 4);

        Assert.True(archetype.TryGetComponentIndex(position, out var idx0));
        Assert.True(archetype.TryGetComponentIndex(velocity, out var idx1));
        Assert.Equal(0, idx0);
        Assert.Equal(1, idx1);
    }

    [Fact]
    public void TryGetComponentIndex_ThreeComponents_ReturnsSuccess()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();
        var health = registry.GetOrCreate<Health>();

        var signature = new Signature(position, velocity, health);
        var archetype = CreateArchetype(signature, new[] { typeof(Position), typeof(Velocity), typeof(Health) }, capacity: 4);

        Assert.True(archetype.TryGetComponentIndex(position, out var idx0));
        Assert.True(archetype.TryGetComponentIndex(velocity, out var idx1));
        Assert.True(archetype.TryGetComponentIndex(health, out var idx2));
        Assert.Equal(0, idx0);
        Assert.Equal(1, idx1);
        Assert.Equal(2, idx2);
    }

    [Fact]
    public void TryGetComponentIndex_MissingComponent_ReturnsFailure()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();

        var signature = new Signature(position, velocity);
        var archetype = CreateArchetype(signature, new[] { typeof(Position), typeof(Velocity) }, capacity: 4);

        var nonExistent = new ComponentType(typeof(NonExistentComponent).GetHashCode());
        Assert.False(archetype.TryGetComponentIndex(nonExistent, out _));
    }

    [Fact]
    public void TryGetComponentIndex_PartialLookupDoesNotAffectOtherResults()
    {
        var registry = new ComponentRegistry();
        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();

        var signature = new Signature(position, velocity);
        var archetype = CreateArchetype(signature, new[] { typeof(Position), typeof(Velocity) }, capacity: 4);

        var nonExistent = new ComponentType(typeof(NonExistentComponent).GetHashCode());

        Assert.True(archetype.TryGetComponentIndex(position, out var idx0));
        Assert.False(archetype.TryGetComponentIndex(nonExistent, out _));
        Assert.Equal(0, idx0);
    }

    private static Archetype CreateArchetype(Signature signature, Type[] componentTypes, int capacity)
    {
        return new Archetype(signature, componentTypes, capacity: capacity);
    }

    private readonly record struct NonExistentComponent(int Value);
}
