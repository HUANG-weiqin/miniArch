using System.Collections;
using System.Reflection;
using MiniArch.Core;
using MiniQueryCache = MiniArch.Core.QueryCache;

namespace MiniArchTests.Core;

public sealed class QueryFilterTests
{
    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct TagA;
    private readonly record struct TagB;

    [Fact]
    public void Description_query_materialization_builds_expected_required_excluded_and_any_sets()
    {
        var world = new World();
        var description = new QueryDescription()
            .With<Position>()
            .Without<Velocity>()
            .WithAny<TagA>()
            .WithAny<TagB>();

        var query = MiniQueryCache.Create(world, in description);

        var position = ComponentRegistry.Shared.GetOrCreate<Position>();
        var velocity = ComponentRegistry.Shared.GetOrCreate<Velocity>();
        var tagA = ComponentRegistry.Shared.GetOrCreate<TagA>();
        var tagB = ComponentRegistry.Shared.GetOrCreate<TagB>();

        Assert.Equal(1, query.Filter.Required.Count);
        Assert.True(query.Filter.Required.AsSpan().Contains(position));
        Assert.Equal(1, query.Filter.Excluded.Count);
        Assert.True(query.Filter.Excluded.AsSpan().Contains(velocity));
        Assert.Equal(2, query.Filter.Any.Count);
        Assert.True(query.Filter.Any.AsSpan().Contains(tagA));
        Assert.True(query.Filter.Any.AsSpan().Contains(tagB));
    }

    [Fact]
    public void Any_and_or_share_same_any_set()
    {
        var world = new World();
        var firstDescription = new QueryDescription().WithAny<TagA>().WithAny<TagB>();
        var secondDescription = new QueryDescription().WithAny<TagB>().WithAny<TagA>();

        var first = MiniQueryCache.Create(world, in firstDescription);
        var second = MiniQueryCache.Create(world, in secondDescription);

        Assert.Same(first, second);
    }

    [Fact]
    public void Query_description_builds_expected_required_excluded_and_any_sets()
    {
        var description = new QueryDescription()
            .With<Position>()
            .Without<Velocity>()
            .WithAny<TagA>()
            .WithAny<TagB>();

        Assert.Equal(new[] { typeof(Position) }, description.RequiredTypes);
        Assert.Equal(new[] { typeof(Velocity) }, description.ExcludedTypes);
        Assert.Equal(new[] { typeof(TagA), typeof(TagB) }, description.AnyTypes);
    }

    [Fact]
    public void Semantically_equivalent_query_descriptions_compare_equal()
    {
        var first = new QueryDescription()
            .With<Position>()
            .Without<Velocity>()
            .WithAny<TagA>()
            .WithAny<TagB>();

        var second = new QueryDescription()
            .WithAny<TagB>()
            .WithAny<TagA>()
            .Without<Velocity>()
            .With<Position>();

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Query_description_public_type_views_returns_correct_types()
    {
        var description = new QueryDescription()
            .With<Position>()
            .Without<Velocity>()
            .WithAny<TagA>();

        Assert.Equal(new[] { typeof(Position) }, description.RequiredTypes);
        Assert.Equal(new[] { typeof(Velocity) }, description.ExcludedTypes);
        Assert.Equal(new[] { typeof(TagA) }, description.AnyTypes);
    }

    [Fact]
    public void Equivalent_descriptions_reuse_same_query()
    {
        var world = new World();
        var firstDescription = new QueryDescription().With<Position>();
        var secondDescription = new QueryDescription().With<Position>();

        var first = MiniQueryCache.Create(world, in firstDescription);
        var second = MiniQueryCache.Create(world, in secondDescription);

        Assert.Same(first, second);
    }

    [Fact]
    public void Query_description_does_not_mutate_source_description_state()
    {
        var root = new QueryDescription();
        var withA = root.With<TagA>();
        var withB = root.With<TagB>();

        Assert.Empty(root.RequiredTypes);
        Assert.Equal(new[] { typeof(TagA) }, withA.RequiredTypes);
        Assert.Equal(new[] { typeof(TagB) }, withB.RequiredTypes);
    }

    [Fact]
    public void Description_query_is_materialized_only_when_requested()
    {
        var world = new World();
        var description = new QueryDescription()
            .With<Position>()
            .Without<Velocity>()
            .WithAny<TagA>()
            .WithAny<TagB>();

        Assert.Equal(0, GetCachedQueryCount(world));

        _ = MiniQueryCache.Create(world, in description);
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

