using MiniArchBenchmarks;
using MiniArch.Core;
using System.Reflection;
using MiniQuery = MiniArch.Core.QueryCache;

namespace MiniArchTests.Core;

public sealed class ComplexQueryBenchmarkScenarioTests
{
    [Fact]
    public void Complex_query_benchmark_world_uses_multiple_dense_archetypes()
    {
        var state = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(100);
        var archetypeComponentCounts = new HashSet<int>();
        var signatures = new HashSet<string>();

        foreach (var entity in state.Entities)
        {
            Assert.True(state.World.TryGetLocation(entity, out var info));
            archetypeComponentCounts.Add(info.Archetype.Signature.Count);
            signatures.Add(string.Join(",", info.Archetype.Signature.AsSpan().ToArray().Select(component => component.Value)));
        }

        Assert.Equal(5, signatures.Count);
        Assert.All(archetypeComponentCounts, count => Assert.True(count >= 8));
    }

    [Fact]
    public void Complex_query_benchmark_queries_return_expected_populations()
    {
        var state = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(100);

        var withAllDescription = new QueryDescription()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>();
        var withAll = MiniQuery.Create(state.World, in withAllDescription);

        var withWithoutDescription = new QueryDescription()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .Without<ExcludedTag>();
        var withWithout = MiniQuery.Create(state.World, in withWithoutDescription);

        var withAnyDescription = new QueryDescription()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>()
            .WithAny<AnyTagA>()
            .WithAny<AnyTagB>();
        var withAny = MiniQuery.Create(state.World, in withAnyDescription);

        Assert.Equal(90, CountEntities(withAll));
        Assert.Equal(70, CountEntities(withWithout));
        Assert.Equal(55, CountEntities(withAny));
    }

    [Fact]
    public void Complex_query_benchmark_matching_entities_have_at_least_eight_components()
    {
        var state = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(100);

        var description = new QueryDescription()
            .With<Position>()
            .With<Velocity>()
            .With<Health>()
            .With<Team>();
        var query = MiniQuery.Create(state.World, in description);

        var inspectedChunkCount = 0;
        foreach (var archetype in query.Chunks)
        {
            if (archetype.EntityCount == 0)
            {
                continue;
            }

            inspectedChunkCount++;
            var entity = archetype.GetEntity(0);
            Assert.True(state.World.TryGetLocation(entity, out var info));
            Assert.True(info.Archetype.Signature.Count >= 8);
        }

        Assert.True(inspectedChunkCount > 0);
    }

    [Fact]
    public void Complex_query_benchmark_world_uses_only_final_archetypes()
    {
        var state = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(100);
        var archetypes = GetArchetypes(state.World);
        var emptyCount = archetypes.Count(archetype => archetype.EntityCount == 0);

        Assert.Equal(5, archetypes.Count);
        Assert.Equal(0, emptyCount);
    }

    [Fact]
    public void Complex_query_benchmark_state_warms_miniarch_queries_before_measurement()
    {
        var state = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(100);

        Assert.Equal(1, state.WithAllQuery.RefreshCount);
        Assert.Equal(1, state.WithAllWithoutQuery.RefreshCount);
        Assert.Equal(1, state.WithAllAnyQuery.RefreshCount);

        Assert.Equal(90, CountEntities(state.WithAllQuery));
        Assert.Equal(70, CountEntities(state.WithAllWithoutQuery));
        Assert.Equal(55, CountEntities(state.WithAllAnyQuery));
    }

    private static int CountEntities(MiniQuery query)
    {
        var total = 0;
        foreach (var archetype in query.Chunks)
        {
            total += archetype.EntityCount;
        }

        return total;
    }

    private static IReadOnlyList<Archetype> GetArchetypes(World world)
    {
        var field = typeof(World).GetField("_archetypes", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var dictionary = Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(world));

        var archetypes = new List<Archetype>(dictionary.Count);
        foreach (System.Collections.DictionaryEntry entry in dictionary)
        {
            archetypes.Add(Assert.IsType<Archetype>(entry.Value));
        }

        return archetypes;
    }
}
