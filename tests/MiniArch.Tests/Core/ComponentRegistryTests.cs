using System.Collections.Concurrent;
using MiniArch.Core;

namespace MiniArchTests.Core;

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

    [Fact]
    public async Task Concurrent_get_or_create_for_same_type_returns_same_id()
    {
        var registry = new ComponentRegistry();
        var start = new Barrier(9);
        var ids = new ConcurrentBag<ComponentType>();
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                start.SignalAndWait();
                ids.Add(registry.GetOrCreate<Position>());
            }))
            .ToArray();

        start.SignalAndWait();
        await Task.WhenAll(tasks);

        Assert.Single(ids.Distinct());

        var id = ids.First();
        Assert.True(registry.TryGetType(id, out var resolved));
        Assert.Equal(typeof(Position), resolved);
    }
}
