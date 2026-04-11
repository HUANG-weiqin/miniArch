using System.Collections;
using System.Reflection;
using MiniArch.Core;

namespace MiniArch.Tests.Core;

public sealed class QueryFilterTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct TagA;
    private readonly record struct TagB;

    [Fact]
    public void Chain_query_builds_expected_required_excluded_and_any_sets()
    {
        var world = new World();

        var query = world.Query()
            .With<Position>()
            .Without<Velocity>()
            .Any<TagA>()
            .Or<TagB>()
            .Build();

        var position = world.Components.GetOrCreate<Position>();
        var velocity = world.Components.GetOrCreate<Velocity>();
        var tagA = world.Components.GetOrCreate<TagA>();
        var tagB = world.Components.GetOrCreate<TagB>();

        Assert.Equal(1, query.RequiredSignature.Count);
        Assert.Contains(position, query.RequiredSignature);
        Assert.Equal(1, query.ExcludedSignature.Count);
        Assert.Contains(velocity, query.ExcludedSignature);
        Assert.Equal(2, query.AnySignature.Count);
        Assert.Contains(tagA, query.AnySignature);
        Assert.Contains(tagB, query.AnySignature);
    }

    [Fact]
    public void Any_and_or_share_same_any_set()
    {
        var world = new World();

        var first = world.Query().Any<TagA>().Or<TagB>().Build();
        var second = world.Query().Or<TagB>().Any<TagA>().Build();

        Assert.Same(first, second);
    }

    [Fact]
    public void Generic_query_entrypoint_remains_compatible_with_chain_query()
    {
        var world = new World();

        var generic = world.Query<Position>();
        var chained = world.Query().With<Position>().Build();

        Assert.Same(generic, chained);
    }

    [Fact]
    public void Query_builder_does_not_mutate_source_builder_state()
    {
        var world = new World();

        var root = world.Query();
        var withA = root.With<TagA>().Build();
        var withB = root.With<TagB>().Build();

        var tagA = world.Components.GetOrCreate<TagA>();
        var tagB = world.Components.GetOrCreate<TagB>();
        Assert.Contains(tagA, withA.RequiredSignature);
        Assert.DoesNotContain(tagB, withA.RequiredSignature);
        Assert.Contains(tagB, withB.RequiredSignature);
        Assert.DoesNotContain(tagA, withB.RequiredSignature);
    }

    [Fact]
    public void Chain_query_is_materialized_only_when_built_or_enumerated()
    {
        var world = new World();

        var builder = world.Query().With<Position>().Without<Velocity>().Any<TagA>().Or<TagB>();
        Assert.Equal(0, GetCachedQueryCount(world));

        _ = builder.Build();
        Assert.Equal(1, GetCachedQueryCount(world));
    }

    private static int GetCachedQueryCount(World world)
    {
        var field = typeof(World).GetField("_queries", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var value = field.GetValue(world);
        var dictionary = Assert.IsAssignableFrom<IDictionary>(value);
        return dictionary.Count;
    }
}
