using MiniArch.Core;

namespace MiniArch.Tests.Core;

public sealed class ComponentRegistryTests
{
    private sealed class Position
    {
    }

    private sealed class Velocity
    {
    }

    [Fact]
    public void Same_type_gets_same_id()
    {
        var registry = new ComponentRegistry();

        var first = registry.GetOrCreate<Position>();
        var second = registry.GetOrCreate(typeof(Position));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_types_get_different_ids()
    {
        var registry = new ComponentRegistry();

        var position = registry.GetOrCreate<Position>();
        var velocity = registry.GetOrCreate<Velocity>();

        Assert.NotEqual(position, velocity);
    }

    [Fact]
    public void Reverse_lookup_returns_original_type()
    {
        var registry = new ComponentRegistry();

        var id = registry.GetOrCreate<Position>();

        Assert.True(registry.TryGetType(id, out var resolved));
        Assert.Equal(typeof(Position), resolved);
    }
}
