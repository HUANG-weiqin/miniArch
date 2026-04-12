using System.Collections;
using System.Reflection;
using MiniArch.Core;
using MiniQuery = MiniArch.Core.Query;

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
            .Or<TagB>();

        var query = MiniQuery.Create(world, in description);

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
        var firstDescription = new QueryDescription().WithAny<TagA>().Or<TagB>();
        var secondDescription = new QueryDescription().Or<TagB>().WithAny<TagA>();

        var first = MiniQuery.Create(world, in firstDescription);
        var second = MiniQuery.Create(world, in secondDescription);

        Assert.Same(first, second);
    }

    [Fact]
    public void Query_description_builds_expected_required_excluded_and_any_sets()
    {
        var description = new QueryDescription()
            .With<Position>()
            .Without<Velocity>()
            .WithAny<TagA>()
            .Or<TagB>();

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
            .Or<TagB>();

        var second = new QueryDescription()
            .Or<TagB>()
            .WithAny<TagA>()
            .Without<Velocity>()
            .With<Position>();

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Query_description_public_type_views_do_not_expose_mutable_internal_storage()
    {
        var description = new QueryDescription()
            .With<Position>()
            .Without<Velocity>()
            .WithAny<TagA>();

        var required = Assert.IsType<Type[]>(description.RequiredTypes);
        var excluded = Assert.IsType<Type[]>(description.ExcludedTypes);
        var any = Assert.IsType<Type[]>(description.AnyTypes);

        required[0] = typeof(TagB);
        excluded[0] = typeof(TagA);
        any[0] = typeof(Velocity);

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

        var first = MiniQuery.Create(world, in firstDescription);
        var second = MiniQuery.Create(world, in secondDescription);

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
            .Or<TagB>();

        Assert.Equal(0, GetCachedQueryCount(world));

        _ = MiniQuery.Create(world, in description);
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
